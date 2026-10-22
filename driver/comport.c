// SPDX-License-Identifier: MS-PL
// Copyright (c) 2026 VCNS Tech Ltd
// Portions Copyright (c) Microsoft Corporation
/*
    Serial control requests from the COM-end application (what GetCommState, SetCommState, SetCommTimeouts,
    WaitCommEvent, PurgeComm, ClearCommError, GetCommProperties and friends send).

    Settings are stored and reported back faithfully but never alter the data: there is no physical line, and the
    host end receives exactly the bytes the application writes.
*/
#include "internal.h"

static NTSTATUS
ReadInput(
    _In_ WDFREQUEST Request,
    _In_ size_t Size,
    _Out_writes_bytes_(Size) PVOID Destination
    )
{
    PVOID buffer;
    NTSTATUS status = WdfRequestRetrieveInputBuffer(Request, Size, &buffer, NULL);

    if (NT_SUCCESS(status)) {
        CopyMemory(Destination, buffer, Size);
    }
    return status;
}

static NTSTATUS
WriteOutput(
    _In_ WDFREQUEST Request,
    _In_reads_bytes_(Size) const VOID *Source,
    _In_ size_t Size,
    _Out_ PULONG_PTR Information
    )
{
    PVOID buffer;
    NTSTATUS status = WdfRequestRetrieveOutputBuffer(Request, Size, &buffer, NULL);

    *Information = 0;
    if (NT_SUCCESS(status)) {
        CopyMemory(buffer, Source, Size);
        *Information = Size;
    }
    return status;
}

static VOID
FillProperties(
    _Out_ PSERIAL_COMMPROP Properties
    )
{
    ZeroMemory(Properties, sizeof(*Properties));
    Properties->PacketLength = sizeof(*Properties);
    Properties->PacketVersion = 2;
    Properties->ServiceMask = SERIAL_SP_SERIALCOMM;
    Properties->MaxBaud = SERIAL_BAUD_USER;
    Properties->ProvSubType = SERIAL_SP_RS232;
    Properties->ProvCapabilities = SERIAL_PCF_DTRDSR | SERIAL_PCF_RTSCTS | SERIAL_PCF_CD | SERIAL_PCF_PARITY_CHECK
                                   | SERIAL_PCF_XONXOFF | SERIAL_PCF_SETXCHAR | SERIAL_PCF_TOTALTIMEOUTS
                                   | SERIAL_PCF_INTTIMEOUTS;
    Properties->SettableParams = SERIAL_SP_PARITY | SERIAL_SP_BAUD | SERIAL_SP_DATABITS | SERIAL_SP_STOPBITS
                                 | SERIAL_SP_HANDSHAKING | SERIAL_SP_PARITY_CHECK | SERIAL_SP_CARRIER_DETECT;
    Properties->SettableBaud = SERIAL_BAUD_075 | SERIAL_BAUD_110 | SERIAL_BAUD_134_5 | SERIAL_BAUD_150
                               | SERIAL_BAUD_300 | SERIAL_BAUD_600 | SERIAL_BAUD_1200 | SERIAL_BAUD_1800
                               | SERIAL_BAUD_2400 | SERIAL_BAUD_4800 | SERIAL_BAUD_7200 | SERIAL_BAUD_9600
                               | SERIAL_BAUD_14400 | SERIAL_BAUD_19200 | SERIAL_BAUD_38400 | SERIAL_BAUD_56K
                               | SERIAL_BAUD_57600 | SERIAL_BAUD_115200 | SERIAL_BAUD_128K | SERIAL_BAUD_USER;
    Properties->SettableData = SERIAL_DATABITS_5 | SERIAL_DATABITS_6 | SERIAL_DATABITS_7 | SERIAL_DATABITS_8;
    Properties->SettableStopParity = SERIAL_STOPBITS_10 | SERIAL_STOPBITS_15 | SERIAL_STOPBITS_20 | SERIAL_PARITY_NONE
                                     | SERIAL_PARITY_ODD | SERIAL_PARITY_EVEN | SERIAL_PARITY_MARK | SERIAL_PARITY_SPACE;
    Properties->CurrentTxQueue = VCOM_BUFFER_SIZE;
    Properties->CurrentRxQueue = VCOM_BUFFER_SIZE;
}

// Fails the in-progress and queued requests of one kind (PurgeComm's abort flags).
static VOID
AbortRequests(
    _In_ PDEVICE_CONTEXT Context,
    _Inout_ WDFREQUEST *Slot,
    _In_ WDFQUEUE Queue
    )
{
    COMPLETION_LIST completions;
    WDFREQUEST request;

    completions.Count = 0;
    LockContext(Context);
    request = *Slot;
    if (TakeCurrent(Slot)) {
        DeferCompletion(&completions, request, STATUS_CANCELLED, GetRequestContext(request)->Done);
    }
    UnlockContext(Context);
    CompleteDeferred(&completions);

    while (NT_SUCCESS(WdfIoQueueRetrieveNextRequest(Queue, &request))) {
        WdfRequestComplete(request, STATUS_CANCELLED);
    }
}

static NTSTATUS
Purge(
    _In_ PDEVICE_CONTEXT Context,
    _In_ ULONG Flags
    )
{
    if (Flags == 0 || (Flags & ~(SERIAL_PURGE_TXABORT | SERIAL_PURGE_RXABORT | SERIAL_PURGE_TXCLEAR
                                 | SERIAL_PURGE_RXCLEAR)) != 0) {
        return STATUS_INVALID_PARAMETER;
    }
    if (Flags & SERIAL_PURGE_RXABORT) {
        AbortRequests(Context, &Context->CurrentRead, Context->ComReadQueue);
    }
    if (Flags & SERIAL_PURGE_TXABORT) {
        AbortRequests(Context, &Context->CurrentWrite, Context->ComWriteQueue);
    }
    LockContext(Context);
    if (Flags & SERIAL_PURGE_RXCLEAR) {
        RingClear(&Context->ToCom);
    }
    if (Flags & SERIAL_PURGE_TXCLEAR) {
        RingClear(&Context->ToHost);
    }
    UnlockContext(Context);
    return STATUS_SUCCESS;
}

// Handles IOCTL_SERIAL_WAIT_ON_MASK. Returns TRUE if the request was kept pending (and must not be completed).
static BOOLEAN
WaitOnMask(
    _In_ PDEVICE_CONTEXT Context,
    _In_ WDFREQUEST Request,
    _Out_ PNTSTATUS Status,
    _Out_ PULONG_PTR Information
    )
{
    COMPLETION_LIST completions;
    PVOID buffer;
    BOOLEAN pending = FALSE;

    completions.Count = 0;
    *Information = 0;
    *Status = WdfRequestRetrieveOutputBuffer(Request, sizeof(ULONG), &buffer, NULL);
    if (!NT_SUCCESS(*Status)) {
        return FALSE;
    }

    LockContext(Context);
    if (Context->WaitMask == 0 || Context->CurrentWait != NULL) {
        // Same as the inbox serial driver: there must be a mask, and only one wait at a time.
        *Status = STATUS_INVALID_PARAMETER;
    } else if (Context->EventHistory != 0) {
        *(PULONG)buffer = Context->EventHistory;
        *Information = sizeof(ULONG);
        Context->EventHistory = 0;
    } else {
        GetRequestContext(Request)->Done = 0;
        GetRequestContext(Request)->CancelPending = FALSE;
        pending = MakeCurrent(&Context->CurrentWait, Request, &completions);
        if (!pending) {
            completions.Count = 0;  // MakeCurrent queued a cancellation; let the caller complete it instead.
            *Status = STATUS_CANCELLED;
        }
    }
    UnlockContext(Context);
    return pending;
}

VOID
ComDeviceControl(
    _In_ PDEVICE_CONTEXT Context,
    _In_ WDFREQUEST Request,
    _In_ ULONG IoControlCode
    )
{
    NTSTATUS status = STATUS_SUCCESS;
    ULONG_PTR information = 0;
    COMPLETION_LIST completions;
    BOOLEAN pump = FALSE;

    completions.Count = 0;

    switch (IoControlCode) {
    case IOCTL_SERIAL_SET_BAUD_RATE: {
        SERIAL_BAUD_RATE baud;
        status = ReadInput(Request, sizeof(baud), &baud);
        if (NT_SUCCESS(status) && baud.BaudRate == 0) {
            status = STATUS_INVALID_PARAMETER;
        }
        if (NT_SUCCESS(status)) {
            LockContext(Context);
            Context->BaudRate = baud.BaudRate;
            UnlockContext(Context);
        }
        break;
    }
    case IOCTL_SERIAL_GET_BAUD_RATE: {
        SERIAL_BAUD_RATE baud;
        LockContext(Context);
        baud.BaudRate = Context->BaudRate;
        UnlockContext(Context);
        status = WriteOutput(Request, &baud, sizeof(baud), &information);
        break;
    }
    case IOCTL_SERIAL_SET_LINE_CONTROL: {
        SERIAL_LINE_CONTROL line;
        status = ReadInput(Request, sizeof(line), &line);
        if (NT_SUCCESS(status) && (line.WordLength < 5 || line.WordLength > 8 || line.Parity > SPACE_PARITY
                                   || line.StopBits > STOP_BITS_2)) {
            status = STATUS_INVALID_PARAMETER;
        }
        if (NT_SUCCESS(status)) {
            LockContext(Context);
            Context->LineControl = line;
            UnlockContext(Context);
        }
        break;
    }
    case IOCTL_SERIAL_GET_LINE_CONTROL: {
        SERIAL_LINE_CONTROL line;
        LockContext(Context);
        line = Context->LineControl;
        UnlockContext(Context);
        status = WriteOutput(Request, &line, sizeof(line), &information);
        break;
    }
    case IOCTL_SERIAL_SET_CHARS: {
        SERIAL_CHARS chars;
        status = ReadInput(Request, sizeof(chars), &chars);
        if (NT_SUCCESS(status)) {
            LockContext(Context);
            Context->Chars = chars;
            UnlockContext(Context);
        }
        break;
    }
    case IOCTL_SERIAL_GET_CHARS: {
        SERIAL_CHARS chars;
        LockContext(Context);
        chars = Context->Chars;
        UnlockContext(Context);
        status = WriteOutput(Request, &chars, sizeof(chars), &information);
        break;
    }
    case IOCTL_SERIAL_SET_HANDFLOW: {
        SERIAL_HANDFLOW handFlow;
        status = ReadInput(Request, sizeof(handFlow), &handFlow);
        if (NT_SUCCESS(status)) {
            LockContext(Context);
            Context->HandFlow = handFlow;
            UnlockContext(Context);
        }
        break;
    }
    case IOCTL_SERIAL_GET_HANDFLOW: {
        SERIAL_HANDFLOW handFlow;
        LockContext(Context);
        handFlow = Context->HandFlow;
        UnlockContext(Context);
        status = WriteOutput(Request, &handFlow, sizeof(handFlow), &information);
        break;
    }
    case IOCTL_SERIAL_SET_TIMEOUTS: {
        SERIAL_TIMEOUTS timeouts;
        status = ReadInput(Request, sizeof(timeouts), &timeouts);
        if (NT_SUCCESS(status) && timeouts.ReadIntervalTimeout == MAXDWORD
            && timeouts.ReadTotalTimeoutMultiplier == MAXDWORD && timeouts.ReadTotalTimeoutConstant == MAXDWORD) {
            status = STATUS_INVALID_PARAMETER;
        }
        if (NT_SUCCESS(status)) {
            LockContext(Context);
            Context->Timeouts = timeouts;
            UnlockContext(Context);
        }
        break;
    }
    case IOCTL_SERIAL_GET_TIMEOUTS: {
        SERIAL_TIMEOUTS timeouts;
        LockContext(Context);
        timeouts = Context->Timeouts;
        UnlockContext(Context);
        status = WriteOutput(Request, &timeouts, sizeof(timeouts), &information);
        break;
    }
    case IOCTL_SERIAL_SET_DTR:
    case IOCTL_SERIAL_CLR_DTR:
    case IOCTL_SERIAL_SET_RTS:
    case IOCTL_SERIAL_CLR_RTS:
        LockContext(Context);
        switch (IoControlCode) {
        case IOCTL_SERIAL_SET_DTR: Context->DtrRts |= SERIAL_DTR_STATE; break;
        case IOCTL_SERIAL_CLR_DTR: Context->DtrRts &= ~SERIAL_DTR_STATE; break;
        case IOCTL_SERIAL_SET_RTS: Context->DtrRts |= SERIAL_RTS_STATE; break;
        default: Context->DtrRts &= ~SERIAL_RTS_STATE; break;
        }
        UnlockContext(Context);
        break;
    case IOCTL_SERIAL_GET_DTRRTS: {
        ULONG dtrRts;
        LockContext(Context);
        dtrRts = Context->DtrRts;
        UnlockContext(Context);
        status = WriteOutput(Request, &dtrRts, sizeof(dtrRts), &information);
        break;
    }
    case IOCTL_SERIAL_SET_BREAK_ON:
    case IOCTL_SERIAL_SET_BREAK_OFF:
        LockContext(Context);
        Context->BreakOn = IoControlCode == IOCTL_SERIAL_SET_BREAK_ON;
        UnlockContext(Context);
        break;
    case IOCTL_SERIAL_GET_MODEMSTATUS: {
        ULONG modemStatus;
        LockContext(Context);
        modemStatus = Context->HostLines;
        UnlockContext(Context);
        status = WriteOutput(Request, &modemStatus, sizeof(modemStatus), &information);
        break;
    }
    case IOCTL_SERIAL_GET_COMMSTATUS: {
        SERIAL_STATUS commStatus;
        ZeroMemory(&commStatus, sizeof(commStatus));
        LockContext(Context);
        commStatus.AmountInInQueue = (ULONG)RingCount(&Context->ToCom);
        commStatus.AmountInOutQueue = (ULONG)RingCount(&Context->ToHost);
        UnlockContext(Context);
        status = WriteOutput(Request, &commStatus, sizeof(commStatus), &information);
        break;
    }
    case IOCTL_SERIAL_GET_PROPERTIES: {
        SERIAL_COMMPROP properties;
        FillProperties(&properties);
        status = WriteOutput(Request, &properties, sizeof(properties), &information);
        break;
    }
    case IOCTL_SERIAL_GET_STATS: {
        SERIALPERF_STATS stats;
        LockContext(Context);
        stats = Context->Stats;
        UnlockContext(Context);
        status = WriteOutput(Request, &stats, sizeof(stats), &information);
        break;
    }
    case IOCTL_SERIAL_CLEAR_STATS:
        LockContext(Context);
        ZeroMemory(&Context->Stats, sizeof(Context->Stats));
        UnlockContext(Context);
        break;
    case IOCTL_SERIAL_CONFIG_SIZE: {
        ULONG size = 0;
        status = WriteOutput(Request, &size, sizeof(size), &information);
        break;
    }
    case IOCTL_SERIAL_SET_MODEM_CONTROL:
        status = ReadInput(Request, sizeof(ULONG), &Context->ModemControl);
        break;
    case IOCTL_SERIAL_GET_MODEM_CONTROL:
        status = WriteOutput(Request, &Context->ModemControl, sizeof(ULONG), &information);
        break;
    case IOCTL_SERIAL_SET_FIFO_CONTROL:
        status = ReadInput(Request, sizeof(ULONG), &Context->FifoControl);
        break;
    case IOCTL_SERIAL_SET_WAIT_MASK: {
        ULONG mask;
        WDFREQUEST wait;
        status = ReadInput(Request, sizeof(mask), &mask);
        if (NT_SUCCESS(status) && (mask & ~SERIAL_EV_ALL) != 0) {
            status = STATUS_INVALID_PARAMETER;
        }
        if (NT_SUCCESS(status)) {
            // A pending wait completes with no events when the mask changes.
            LockContext(Context);
            wait = Context->CurrentWait;
            if (TakeCurrent(&Context->CurrentWait)) {
                PULONG output;
                if (NT_SUCCESS(WdfRequestRetrieveOutputBuffer(wait, sizeof(ULONG), (PVOID *)&output, NULL))) {
                    *output = 0;
                }
                DeferCompletion(&completions, wait, STATUS_SUCCESS, sizeof(ULONG));
            }
            Context->WaitMask = mask;
            Context->EventHistory = 0;
            UnlockContext(Context);
        }
        break;
    }
    case IOCTL_SERIAL_GET_WAIT_MASK: {
        ULONG mask;
        LockContext(Context);
        mask = Context->WaitMask;
        UnlockContext(Context);
        status = WriteOutput(Request, &mask, sizeof(mask), &information);
        break;
    }
    case IOCTL_SERIAL_WAIT_ON_MASK:
        if (WaitOnMask(Context, Request, &status, &information)) {
            return;
        }
        break;
    case IOCTL_SERIAL_PURGE: {
        ULONG flags;
        status = ReadInput(Request, sizeof(flags), &flags);
        if (NT_SUCCESS(status)) {
            status = Purge(Context, flags);
            pump = TRUE;
        }
        break;
    }
    case IOCTL_SERIAL_IMMEDIATE_CHAR: {
        UCHAR character;
        status = ReadInput(Request, sizeof(character), &character);
        if (NT_SUCCESS(status)) {
            LockContext(Context);
            if (Context->HostFile != NULL && RingWrite(&Context->ToHost, &character, 1) == 0) {
                status = STATUS_INSUFFICIENT_RESOURCES;
            }
            UnlockContext(Context);
            pump = TRUE;
        }
        break;
    }
    case IOCTL_SERIAL_SET_QUEUE_SIZE: {
        SERIAL_QUEUE_SIZE queueSize;
        status = ReadInput(Request, sizeof(queueSize), &queueSize);  // Buffers are fixed; accept any request.
        break;
    }
    case IOCTL_SERIAL_SET_XON:
    case IOCTL_SERIAL_SET_XOFF:
    case IOCTL_SERIAL_XOFF_COUNTER:
    case IOCTL_SERIAL_RESET_DEVICE:
    case IOCTL_SERIAL_LSRMST_INSERT:
    case IOCTL_SERIAL_APPLY_DEFAULT_CONFIGURATION:
        break;
    default:
        status = STATUS_INVALID_PARAMETER;
        break;
    }

    CompleteDeferred(&completions);
    WdfRequestCompleteWithInformation(Request, status, information);
    if (pump) {
        PumpAll(Context);
    }
}
