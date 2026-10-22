// SPDX-License-Identifier: MS-PL
// Copyright (c) 2026 VCNS Tech Ltd
/*
    Control requests from the host end (the application supplying the data).
*/
#include "internal.h"

VOID
HostDeviceControl(
    _In_ PDEVICE_CONTEXT Context,
    _In_ WDFREQUEST Request,
    _In_ ULONG IoControlCode
    )
{
    NTSTATUS status;
    ULONG_PTR information = 0;
    COMPLETION_LIST completions;

    completions.Count = 0;

    switch (IoControlCode) {
    case IOCTL_VCOM_GET_PORT_STATE: {
        VCOM_PORT_STATE *state;
        status = WdfRequestRetrieveOutputBuffer(Request, sizeof(*state), (PVOID *)&state, NULL);
        if (!NT_SUCCESS(status)) {
            break;
        }
        ZeroMemory(state, sizeof(*state));
        state->Size = sizeof(*state);
        LockContext(Context);
        state->Flags = (Context->ComFile != NULL ? VCOM_STATE_COM_OPEN : 0)
                       | ((Context->DtrRts & SERIAL_DTR_STATE) ? VCOM_STATE_DTR : 0)
                       | ((Context->DtrRts & SERIAL_RTS_STATE) ? VCOM_STATE_RTS : 0)
                       | (Context->BreakOn ? VCOM_STATE_BREAK : 0);
        state->BaudRate = Context->BaudRate;
        state->DataBits = Context->LineControl.WordLength;
        state->Parity = Context->LineControl.Parity;
        state->StopBits = Context->LineControl.StopBits;
        state->BytesWaitingForCom = (ULONG)RingCount(&Context->ToCom);
        state->BytesWaitingForHost = (ULONG)RingCount(&Context->ToHost);
        (VOID)StringCchCopyW(state->PortName, ARRAYSIZE(state->PortName), Context->PortName);
        UnlockContext(Context);
        information = sizeof(*state);
        break;
    }
    case IOCTL_VCOM_SET_MODEM_STATUS: {
        PULONG lines;
        status = WdfRequestRetrieveInputBuffer(Request, sizeof(ULONG), (PVOID *)&lines, NULL);
        if (NT_SUCCESS(status)) {
            LockContext(Context);
            SetHostLines(Context, *lines, &completions);
            UnlockContext(Context);
        }
        break;
    }
    default:
        // The host end is not a serial port: it has no line settings to configure.
        status = STATUS_INVALID_DEVICE_REQUEST;
        break;
    }

    CompleteDeferred(&completions);
    WdfRequestCompleteWithInformation(Request, status, information);
}
