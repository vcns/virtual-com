// SPDX-License-Identifier: MS-PL
// Copyright (c) 2026 VCNS Tech Ltd
/*
    Data path between the COM end and the host end.

    Every read and write is first parked in its manual queue, then PumpAll moves data and completes requests in
    arrival order. Only one thread pumps at a time; a thread that finds the pump busy just flags that there is new
    work, so requests are never reordered. Requests are completed only after the device lock has been released.

    COM-end reads follow the Win32 serial time-out rules (SERIAL_TIMEOUTS):
      - ReadIntervalTimeout = MAXDWORD, both totals 0: return at once with whatever is buffered;
      - ReadIntervalTimeout and ReadTotalTimeoutMultiplier = MAXDWORD, 0 < ReadTotalTimeoutConstant < MAXDWORD:
        return as soon as any byte is available, or after the constant with nothing;
      - otherwise: total time-out = multiplier x length + constant (0 = none), and the interval time-out
        (0 = none) restarts each time data arrives. Expiry returns the bytes read so far with STATUS_TIMEOUT.
*/
#include "internal.h"

VOID
DeferCompletion(
    _Inout_ PCOMPLETION_LIST List,
    _In_ WDFREQUEST Request,
    _In_ NTSTATUS Status,
    _In_ ULONG_PTR Information
    )
{
    if (List->Count == VCOM_MAX_COMPLETIONS) {
        // Each lock hold completes only a handful of requests, so this shouldn't happen. Completing now is
        // still safe: completion never calls back into this driver.
        WdfRequestCompleteWithInformation(Request, Status, Information);
        return;
    }
    List->Items[List->Count].Request = Request;
    List->Items[List->Count].Status = Status;
    List->Items[List->Count].Information = Information;
    List->Count++;
}

VOID
CompleteDeferred(
    _Inout_ PCOMPLETION_LIST List
    )
{
    for (ULONG i = 0; i < List->Count; i++) {
        WdfRequestCompleteWithInformation(List->Items[i].Request, List->Items[i].Status, List->Items[i].Information);
    }
    List->Count = 0;
}

// Takes ownership of an in-progress request so the caller can complete it. Lock held. Returns FALSE if the
// request is being cancelled, in which case EvtRequestCancelCurrent clears the slot and completes it.
BOOLEAN
TakeCurrent(
    _Inout_ WDFREQUEST *Slot
    )
{
    WDFREQUEST request = *Slot;
    PREQUEST_CONTEXT requestContext;

    if (request == NULL) {
        return FALSE;
    }
    requestContext = GetRequestContext(request);
    if (requestContext->CancelPending) {
        return FALSE;
    }
    if (WdfRequestUnmarkCancelable(request) == STATUS_CANCELLED) {
        requestContext->CancelPending = TRUE;
        return FALSE;
    }
    *Slot = NULL;
    return TRUE;
}

// Makes Request the in-progress request in Slot. Lock held. If the request was already cancelled, queues its
// completion instead and returns FALSE.
BOOLEAN
MakeCurrent(
    _Inout_ WDFREQUEST *Slot,
    _In_ WDFREQUEST Request,
    _Inout_ PCOMPLETION_LIST Completions
    )
{
    NTSTATUS status = WdfRequestMarkCancelableEx(Request, EvtRequestCancelCurrent);
    if (!NT_SUCCESS(status)) {
        DeferCompletion(Completions, Request, status, 0);
        return FALSE;
    }
    *Slot = Request;
    return TRUE;
}

VOID
EvtRequestCancelCurrent(
    _In_ WDFREQUEST Request
    )
{
    PDEVICE_CONTEXT context = GetDeviceContext(WdfIoQueueGetDevice(WdfRequestGetIoQueue(Request)));
    size_t done;

    LockContext(context);
    if (context->CurrentRead == Request) {
        context->CurrentRead = NULL;
    } else if (context->CurrentWrite == Request) {
        context->CurrentWrite = NULL;
    } else if (context->CurrentWait == Request) {
        context->CurrentWait = NULL;
    } else if (context->CurrentHostWrite == Request) {
        context->CurrentHostWrite = NULL;
    }
    done = GetRequestContext(Request)->Done;
    UnlockContext(context);

    WdfRequestCompleteWithInformation(Request, STATUS_CANCELLED, done);
    PumpAll(context);
}

static VOID
CompleteWait(
    _In_ WDFREQUEST Request,
    _In_ ULONG Events,
    _Inout_ PCOMPLETION_LIST Completions
    )
{
    PULONG output;
    NTSTATUS status = WdfRequestRetrieveOutputBuffer(Request, sizeof(ULONG), (PVOID *)&output, NULL);

    if (NT_SUCCESS(status)) {
        *output = Events;
    }
    DeferCompletion(Completions, Request, status, NT_SUCCESS(status) ? sizeof(ULONG) : 0);
}

// Records events the application is waiting for and completes its WAIT_ON_MASK request. Lock held.
VOID
SignalEvents(
    _In_ PDEVICE_CONTEXT Context,
    _In_ ULONG Events,
    _Inout_ PCOMPLETION_LIST Completions
    )
{
    WDFREQUEST wait = Context->CurrentWait;

    Context->EventHistory |= Events & Context->WaitMask;
    if (Context->EventHistory == 0 || !TakeCurrent(&Context->CurrentWait)) {
        return;
    }
    CompleteWait(wait, Context->EventHistory, Completions);
    Context->EventHistory = 0;
}

// Sets the modem-status lines the COM end sees and raises the matching events. Lock held.
VOID
SetHostLines(
    _In_ PDEVICE_CONTEXT Context,
    _In_ ULONG Lines,
    _Inout_ PCOMPLETION_LIST Completions
    )
{
    ULONG changed = (Context->HostLines ^ Lines) & VCOM_HOST_LINES_MASK;
    ULONG events = 0;

    Context->HostLines = Lines & VCOM_HOST_LINES_MASK;
    if (changed & VCOM_LINE_CTS) {
        events |= SERIAL_EV_CTS;
    }
    if (changed & VCOM_LINE_DSR) {
        events |= SERIAL_EV_DSR;
    }
    if (changed & VCOM_LINE_DCD) {
        events |= SERIAL_EV_RLSD;
    }
    if (changed & VCOM_LINE_RI) {
        events |= SERIAL_EV_RING;
    }
    if (events != 0) {
        SignalEvents(Context, events, Completions);
    }
}

VOID
ResetComSettings(
    _In_ PDEVICE_CONTEXT Context
    )
{
    Context->BaudRate = 9600;
    Context->LineControl.WordLength = 8;
    Context->LineControl.Parity = NO_PARITY;
    Context->LineControl.StopBits = STOP_BIT_1;
    ZeroMemory(&Context->Chars, sizeof(Context->Chars));
    Context->Chars.XonChar = 0x11;
    Context->Chars.XoffChar = 0x13;
    ZeroMemory(&Context->HandFlow, sizeof(Context->HandFlow));
    Context->HandFlow.ControlHandShake = SERIAL_DTR_CONTROL;
    Context->HandFlow.FlowReplace = SERIAL_RTS_CONTROL;
    Context->HandFlow.XonLimit = VCOM_BUFFER_SIZE / 4;
    Context->HandFlow.XoffLimit = VCOM_BUFFER_SIZE / 4;
    ZeroMemory(&Context->Timeouts, sizeof(Context->Timeouts));
}

// Starts the timer for the nearest non-zero deadline; a spurious expiry just re-runs the pump.
static VOID
ArmTimer(
    _In_ WDFTIMER Timer,
    _In_ ULONGLONG Deadline1,
    _In_ ULONGLONG Deadline2,
    _In_ ULONGLONG Now
    )
{
    ULONGLONG due = Deadline1;
    LONGLONG milliseconds;

    if (due == 0 || (Deadline2 != 0 && Deadline2 < due)) {
        due = Deadline2;
    }
    if (due == 0) {
        return;
    }
    milliseconds = due > Now ? (LONGLONG)(due - Now) : 1;
    (VOID)WdfTimerStart(Timer, WDF_REL_TIMEOUT_IN_MS(milliseconds));
}

//
// COM end: reads
//

static VOID
BeginComRead(
    _In_ PDEVICE_CONTEXT Context,
    _In_ WDFREQUEST Request,
    _In_ ULONGLONG Now,
    _Inout_ PCOMPLETION_LIST Completions
    )
{
    PREQUEST_CONTEXT requestContext = GetRequestContext(Request);
    SERIAL_TIMEOUTS timeouts = Context->Timeouts;
    PVOID buffer;
    size_t length;
    NTSTATUS status;

    if (WdfRequestGetFileObject(Request) != Context->ComFile) {
        DeferCompletion(Completions, Request, STATUS_CANCELLED, 0);
        return;
    }
    status = WdfRequestRetrieveOutputBuffer(Request, 1, &buffer, &length);
    if (!NT_SUCCESS(status)) {
        DeferCompletion(Completions, Request, status, 0);
        return;
    }

    ZeroMemory(requestContext, sizeof(*requestContext));
    requestContext->Buffer = (PUCHAR)buffer;
    requestContext->Length = length;
    if (timeouts.ReadIntervalTimeout == MAXDWORD && timeouts.ReadTotalTimeoutMultiplier == 0
        && timeouts.ReadTotalTimeoutConstant == 0) {
        requestContext->Immediate = TRUE;
    } else if (timeouts.ReadIntervalTimeout == MAXDWORD && timeouts.ReadTotalTimeoutMultiplier == MAXDWORD
               && timeouts.ReadTotalTimeoutConstant != 0 && timeouts.ReadTotalTimeoutConstant != MAXDWORD) {
        requestContext->ReturnOnAnyByte = TRUE;
        requestContext->TotalDeadline = Now + timeouts.ReadTotalTimeoutConstant;
    } else {
        if (timeouts.ReadTotalTimeoutMultiplier != 0 || timeouts.ReadTotalTimeoutConstant != 0) {
            requestContext->TotalDeadline = Now + (ULONGLONG)timeouts.ReadTotalTimeoutMultiplier * length
                                            + timeouts.ReadTotalTimeoutConstant;
        }
        requestContext->IntervalMs = timeouts.ReadIntervalTimeout == MAXDWORD ? 0 : timeouts.ReadIntervalTimeout;
    }
    (VOID)MakeCurrent(&Context->CurrentRead, Request, Completions);
}

static BOOLEAN
ServiceComRead(
    _In_ PDEVICE_CONTEXT Context,
    _In_ ULONGLONG Now,
    _Inout_ PCOMPLETION_LIST Completions
    )
{
    WDFREQUEST request = Context->CurrentRead;
    PREQUEST_CONTEXT requestContext;
    NTSTATUS status = STATUS_PENDING;
    size_t copied;

    if (request == NULL || GetRequestContext(request)->CancelPending) {
        return FALSE;
    }
    requestContext = GetRequestContext(request);

    copied = RingRead(&Context->ToCom, requestContext->Buffer + requestContext->Done,
                      requestContext->Length - requestContext->Done);
    if (copied > 0) {
        requestContext->Done += copied;
        Context->Stats.ReceivedCount += (ULONG)copied;
        if (requestContext->IntervalMs != 0) {
            requestContext->IntervalDeadline = Now + requestContext->IntervalMs;
        }
    }

    if (requestContext->Done == requestContext->Length || requestContext->Immediate
        || (requestContext->ReturnOnAnyByte && requestContext->Done > 0)) {
        status = STATUS_SUCCESS;
    } else if ((requestContext->TotalDeadline != 0 && Now >= requestContext->TotalDeadline)
               || (requestContext->IntervalDeadline != 0 && Now >= requestContext->IntervalDeadline)) {
        status = STATUS_TIMEOUT;
    }

    if (status == STATUS_PENDING) {
        ArmTimer(Context->ReadTimer, requestContext->TotalDeadline, requestContext->IntervalDeadline, Now);
        return copied > 0;
    }
    if (TakeCurrent(&Context->CurrentRead)) {
        DeferCompletion(Completions, request, status, requestContext->Done);
    }
    return TRUE;
}

static BOOLEAN
ComReadPump(
    _In_ PDEVICE_CONTEXT Context
    )
{
    BOOLEAN progress = FALSE;
    COMPLETION_LIST completions;
    WDFREQUEST next;
    BOOLEAN idle;

    completions.Count = 0;
    for (;;) {
        LockContext(Context);
        progress |= ServiceComRead(Context, GetTickCount64(), &completions);
        idle = Context->CurrentRead == NULL;
        UnlockContext(Context);
        CompleteDeferred(&completions);

        if (!idle || !NT_SUCCESS(WdfIoQueueRetrieveNextRequest(Context->ComReadQueue, &next))) {
            return progress;
        }
        progress = TRUE;
        LockContext(Context);
        BeginComRead(Context, next, GetTickCount64(), &completions);
        UnlockContext(Context);
        CompleteDeferred(&completions);
    }
}

//
// COM end: writes
//

static VOID
BeginWrite(
    _In_ PDEVICE_CONTEXT Context,
    _In_ WDFREQUEST Request,
    _In_ WDFFILEOBJECT Owner,
    _In_ BOOLEAN UseWriteTimeouts,
    _Inout_ WDFREQUEST *Slot,
    _Inout_ PCOMPLETION_LIST Completions
    )
{
    PREQUEST_CONTEXT requestContext = GetRequestContext(Request);
    SERIAL_TIMEOUTS timeouts = Context->Timeouts;
    PVOID buffer;
    size_t length;
    NTSTATUS status;

    if (WdfRequestGetFileObject(Request) != Owner) {
        DeferCompletion(Completions, Request, STATUS_CANCELLED, 0);
        return;
    }
    status = WdfRequestRetrieveInputBuffer(Request, 1, &buffer, &length);
    if (!NT_SUCCESS(status)) {
        DeferCompletion(Completions, Request, status, 0);
        return;
    }

    ZeroMemory(requestContext, sizeof(*requestContext));
    requestContext->Buffer = (PUCHAR)buffer;
    requestContext->Length = length;
    if (UseWriteTimeouts && (timeouts.WriteTotalTimeoutMultiplier != 0 || timeouts.WriteTotalTimeoutConstant != 0)) {
        requestContext->TotalDeadline = GetTickCount64() + (ULONGLONG)timeouts.WriteTotalTimeoutMultiplier * length
                                        + timeouts.WriteTotalTimeoutConstant;
    }
    (VOID)MakeCurrent(Slot, Request, Completions);
}

static BOOLEAN
ServiceComWrite(
    _In_ PDEVICE_CONTEXT Context,
    _In_ ULONGLONG Now,
    _Inout_ PCOMPLETION_LIST Completions
    )
{
    WDFREQUEST request = Context->CurrentWrite;
    PREQUEST_CONTEXT requestContext;
    NTSTATUS status = STATUS_PENDING;
    size_t remaining;
    size_t copied;

    if (request == NULL || GetRequestContext(request)->CancelPending) {
        return FALSE;
    }
    requestContext = GetRequestContext(request);
    remaining = requestContext->Length - requestContext->Done;

    // With no host attached the bytes go nowhere, as on a serial line with nothing plugged in.
    copied = Context->HostFile == NULL
        ? remaining
        : RingWrite(&Context->ToHost, requestContext->Buffer + requestContext->Done, remaining);
    requestContext->Done += copied;
    Context->Stats.TransmittedCount += (ULONG)copied;

    if (requestContext->Done == requestContext->Length) {
        status = STATUS_SUCCESS;
    } else if (requestContext->TotalDeadline != 0 && Now >= requestContext->TotalDeadline) {
        status = STATUS_TIMEOUT;
    }

    if (status == STATUS_PENDING) {
        ArmTimer(Context->WriteTimer, requestContext->TotalDeadline, 0, Now);
        return copied > 0;
    }
    if (TakeCurrent(&Context->CurrentWrite)) {
        DeferCompletion(Completions, request, status, requestContext->Done);
    }
    if (RingCount(&Context->ToHost) == 0) {
        SignalEvents(Context, SERIAL_EV_TXEMPTY, Completions);
    }
    return TRUE;
}

static BOOLEAN
ComWritePump(
    _In_ PDEVICE_CONTEXT Context
    )
{
    BOOLEAN progress = FALSE;
    COMPLETION_LIST completions;
    WDFREQUEST next;
    BOOLEAN idle;

    completions.Count = 0;
    for (;;) {
        LockContext(Context);
        progress |= ServiceComWrite(Context, GetTickCount64(), &completions);
        idle = Context->CurrentWrite == NULL;
        UnlockContext(Context);
        CompleteDeferred(&completions);

        if (!idle || !NT_SUCCESS(WdfIoQueueRetrieveNextRequest(Context->ComWriteQueue, &next))) {
            return progress;
        }
        progress = TRUE;
        LockContext(Context);
        BeginWrite(Context, next, Context->ComFile, TRUE, &Context->CurrentWrite, &completions);
        UnlockContext(Context);
        CompleteDeferred(&completions);
    }
}

//
// Host end: reads complete as soon as any data is available, like a pipe.
//

static BOOLEAN
HostReadPump(
    _In_ PDEVICE_CONTEXT Context
    )
{
    BOOLEAN progress = FALSE;
    COMPLETION_LIST completions;
    WDFREQUEST request;
    PVOID buffer;
    size_t length;
    size_t copied;
    size_t available;
    NTSTATUS status;

    completions.Count = 0;
    for (;;) {
        LockContext(Context);
        available = RingCount(&Context->ToHost);
        UnlockContext(Context);
        if (available == 0 || !NT_SUCCESS(WdfIoQueueRetrieveNextRequest(Context->HostReadQueue, &request))) {
            return progress;
        }

        copied = 0;
        LockContext(Context);
        if (WdfRequestGetFileObject(request) != Context->HostFile) {
            DeferCompletion(&completions, request, STATUS_CANCELLED, 0);
        } else if (!NT_SUCCESS(status = WdfRequestRetrieveOutputBuffer(request, 1, &buffer, &length))) {
            DeferCompletion(&completions, request, status, 0);
        } else if ((copied = RingRead(&Context->ToHost, (PUCHAR)buffer, length)) > 0) {
            DeferCompletion(&completions, request, STATUS_SUCCESS, copied);
            if (RingCount(&Context->ToHost) == 0) {
                SignalEvents(Context, SERIAL_EV_TXEMPTY, &completions);
            }
        }
        UnlockContext(Context);

        if (completions.Count == 0) {
            // The data went away meanwhile (a purge); keep the request first in line.
            (VOID)WdfRequestRequeue(request);
            return progress;
        }
        CompleteDeferred(&completions);
        progress = TRUE;
    }
}

//
// Host end: writes
//

static BOOLEAN
ServiceHostWrite(
    _In_ PDEVICE_CONTEXT Context,
    _Inout_ PCOMPLETION_LIST Completions
    )
{
    WDFREQUEST request = Context->CurrentHostWrite;
    PREQUEST_CONTEXT requestContext;
    PUCHAR start;
    size_t remaining;
    size_t copied;
    ULONG events;

    if (request == NULL || GetRequestContext(request)->CancelPending) {
        return FALSE;
    }
    requestContext = GetRequestContext(request);
    start = requestContext->Buffer + requestContext->Done;
    remaining = requestContext->Length - requestContext->Done;

    if (Context->ComFile == NULL) {
        copied = remaining;     // Nobody has the COM end open: discard.
    } else {
        copied = RingWrite(&Context->ToCom, start, remaining);
        if (copied > 0) {
            events = SERIAL_EV_RXCHAR;
            if ((Context->WaitMask & SERIAL_EV_RXFLAG) && memchr(start, Context->Chars.EventChar, copied) != NULL) {
                events |= SERIAL_EV_RXFLAG;
            }
            if (RingCount(&Context->ToCom) >= Context->ToCom.Capacity / 10 * 8) {
                events |= SERIAL_EV_RX80FULL;
            }
            SignalEvents(Context, events, Completions);
        }
    }
    requestContext->Done += copied;

    if (requestContext->Done < requestContext->Length) {
        return copied > 0;
    }
    if (TakeCurrent(&Context->CurrentHostWrite)) {
        DeferCompletion(Completions, request, STATUS_SUCCESS, requestContext->Done);
    }
    return TRUE;
}

static BOOLEAN
HostWritePump(
    _In_ PDEVICE_CONTEXT Context
    )
{
    BOOLEAN progress = FALSE;
    COMPLETION_LIST completions;
    WDFREQUEST next;
    BOOLEAN idle;

    completions.Count = 0;
    for (;;) {
        LockContext(Context);
        progress |= ServiceHostWrite(Context, &completions);
        idle = Context->CurrentHostWrite == NULL;
        UnlockContext(Context);
        CompleteDeferred(&completions);

        if (!idle || !NT_SUCCESS(WdfIoQueueRetrieveNextRequest(Context->HostWriteQueue, &next))) {
            return progress;
        }
        progress = TRUE;
        LockContext(Context);
        BeginWrite(Context, next, Context->HostFile, FALSE, &Context->CurrentHostWrite, &completions);
        UnlockContext(Context);
        CompleteDeferred(&completions);
    }
}

VOID
PumpAll(
    _In_ PDEVICE_CONTEXT Context
    )
{
    BOOLEAN progress;

    LockContext(Context);
    if (Context->Pumping) {
        Context->PumpAgain = TRUE;
        UnlockContext(Context);
        return;
    }
    Context->Pumping = TRUE;
    UnlockContext(Context);

    for (;;) {
        do {
            progress = HostWritePump(Context);
            progress |= ComReadPump(Context);
            progress |= ComWritePump(Context);
            progress |= HostReadPump(Context);
        } while (progress);

        LockContext(Context);
        if (!Context->PumpAgain) {
            Context->Pumping = FALSE;
            UnlockContext(Context);
            return;
        }
        Context->PumpAgain = FALSE;
        UnlockContext(Context);
    }
}

VOID
CancelRoleRequests(
    _In_ PDEVICE_CONTEXT Context,
    _In_ VCOM_ROLE Role,
    _In_opt_ WDFFILEOBJECT FileObject
    )
{
    COMPLETION_LIST completions;
    WDFREQUEST *slots[3];
    WDFQUEUE queues[2];
    WDFREQUEST request;
    ULONG slotCount;

    completions.Count = 0;
    if (Role == VcomRoleCom) {
        slots[0] = &Context->CurrentRead;
        slots[1] = &Context->CurrentWrite;
        slots[2] = &Context->CurrentWait;
        slotCount = 3;
        queues[0] = Context->ComReadQueue;
        queues[1] = Context->ComWriteQueue;
    } else {
        slots[0] = &Context->CurrentHostWrite;
        slotCount = 1;
        queues[0] = Context->HostReadQueue;
        queues[1] = Context->HostWriteQueue;
    }

    LockContext(Context);
    for (ULONG i = 0; i < slotCount; i++) {
        request = *slots[i];
        if (TakeCurrent(slots[i])) {
            DeferCompletion(&completions, request, STATUS_CANCELLED, GetRequestContext(request)->Done);
        }
    }
    UnlockContext(Context);
    CompleteDeferred(&completions);

    for (ULONG i = 0; i < ARRAYSIZE(queues); i++) {
        while (NT_SUCCESS(FileObject != NULL
                              ? WdfIoQueueRetrieveRequestByFileObject(queues[i], FileObject, &request)
                              : WdfIoQueueRetrieveNextRequest(queues[i], &request))) {
            WdfRequestComplete(request, STATUS_CANCELLED);
        }
    }
}

//
// Framework callbacks
//

static VCOM_ROLE
RequestRole(
    _In_ WDFREQUEST Request
    )
{
    WDFFILEOBJECT fileObject = WdfRequestGetFileObject(Request);
    return fileObject != NULL ? GetFileContext(fileObject)->Role : VcomRoleNone;
}

static VOID
ForwardAndPump(
    _In_ PDEVICE_CONTEXT Context,
    _In_ WDFREQUEST Request,
    _In_ WDFQUEUE ComQueue,
    _In_ WDFQUEUE HostQueue
    )
{
    VCOM_ROLE role = RequestRole(Request);
    WDFQUEUE target = role == VcomRoleCom ? ComQueue : role == VcomRoleHost ? HostQueue : NULL;
    NTSTATUS status;

    if (target == NULL) {
        WdfRequestComplete(Request, STATUS_INVALID_DEVICE_REQUEST);
        return;
    }
    status = WdfRequestForwardToIoQueue(Request, target);
    if (!NT_SUCCESS(status)) {
        WdfRequestComplete(Request, status);
        return;
    }
    PumpAll(Context);
}

VOID
EvtIoRead(
    _In_ WDFQUEUE Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t Length
    )
{
    PDEVICE_CONTEXT context = GetDeviceContext(WdfIoQueueGetDevice(Queue));

    UNREFERENCED_PARAMETER(Length);
    ForwardAndPump(context, Request, context->ComReadQueue, context->HostReadQueue);
}

VOID
EvtIoWrite(
    _In_ WDFQUEUE Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t Length
    )
{
    PDEVICE_CONTEXT context = GetDeviceContext(WdfIoQueueGetDevice(Queue));

    UNREFERENCED_PARAMETER(Length);
    ForwardAndPump(context, Request, context->ComWriteQueue, context->HostWriteQueue);
}

VOID
EvtIoDeviceControl(
    _In_ WDFQUEUE Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t OutputBufferLength,
    _In_ size_t InputBufferLength,
    _In_ ULONG IoControlCode
    )
{
    PDEVICE_CONTEXT context = GetDeviceContext(WdfIoQueueGetDevice(Queue));

    UNREFERENCED_PARAMETER(OutputBufferLength);
    UNREFERENCED_PARAMETER(InputBufferLength);

    switch (RequestRole(Request)) {
    case VcomRoleCom:
        ComDeviceControl(context, Request, IoControlCode);
        break;
    case VcomRoleHost:
        HostDeviceControl(context, Request, IoControlCode);
        break;
    default:
        WdfRequestComplete(Request, STATUS_INVALID_DEVICE_REQUEST);
        break;
    }
}

VOID
EvtReadTimer(
    _In_ WDFTIMER Timer
    )
{
    PumpAll(GetDeviceContext((WDFDEVICE)WdfTimerGetParentObject(Timer)));
}

VOID
EvtWriteTimer(
    _In_ WDFTIMER Timer
    )
{
    PumpAll(GetDeviceContext((WDFDEVICE)WdfTimerGetParentObject(Timer)));
}
