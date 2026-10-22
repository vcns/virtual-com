// SPDX-License-Identifier: MS-PL
// Copyright (c) 2026 VCNS Tech Ltd
// Portions Copyright (c) Microsoft Corporation
/*
    Virtual COM port driver (UMDF 2): internal definitions.

    Structure adapted from Microsoft's VirtualSerial2 sample (Windows-driver-samples, MS-PL).
*/
#pragma once

#define WIN32_NO_STATUS
#include <windows.h>
#undef WIN32_NO_STATUS
#include <ntstatus.h>

#pragma warning(push)
#pragma warning(disable: 4324)  // WDF headers: structure padded due to alignment specifier
#include <wdf.h>
#pragma warning(pop)

#pragma warning(push)
#pragma warning(disable: 4005)  // ntddser.h repeats a few winioctl.h macros with the same values (UCHAR vs BYTE)
#include <ntddser.h>
#pragma warning(pop)

#include <string.h>
#include <strsafe.h>

#include "vcom.h"
#include "ringbuffer.h"

#define VCOM_BUFFER_SIZE        (64 * 1024)
#define VCOM_PORT_NAME_LENGTH   16
#define VCOM_MAX_COMPLETIONS    8

#define SERIAL_EV_ALL           0x1FFF
#define VCOM_HOST_LINES_DEFAULT (VCOM_LINE_CTS | VCOM_LINE_DSR | VCOM_LINE_DCD)
#define VCOM_HOST_LINES_MASK    (VCOM_LINE_CTS | VCOM_LINE_DSR | VCOM_LINE_RI | VCOM_LINE_DCD)

#if DBG
#define VcomTrace(fmt, ...) VcomTraceImpl(fmt, __VA_ARGS__)
VOID VcomTraceImpl(_In_z_ _Printf_format_string_ PCSTR Format, ...);
#else
#define VcomTrace(fmt, ...) ((void)0)
#endif

typedef enum _VCOM_ROLE {
    VcomRoleNone = 0,
    VcomRoleCom,
    VcomRoleHost,
} VCOM_ROLE;

typedef struct _FILE_CONTEXT {
    VCOM_ROLE Role;
} FILE_CONTEXT, *PFILE_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(FILE_CONTEXT, GetFileContext)

// Progress of a read or write that can't be satisfied at once.
typedef struct _REQUEST_CONTEXT {
    PUCHAR      Buffer;
    size_t      Length;
    size_t      Done;
    ULONGLONG   TotalDeadline;      // GetTickCount64() value, 0 = none
    ULONGLONG   IntervalDeadline;   // set once data has arrived, 0 = none
    ULONG       IntervalMs;         // 0 = no interval time-out
    BOOLEAN     Immediate;          // return whatever is buffered, even nothing
    BOOLEAN     ReturnOnAnyByte;    // return as soon as at least one byte has been read
    BOOLEAN     CancelPending;      // EvtRequestCancelCurrent will complete it
} REQUEST_CONTEXT, *PREQUEST_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(REQUEST_CONTEXT, GetRequestContext)

typedef struct _DEVICE_CONTEXT {
    WDFDEVICE       Device;

    // Protects everything below. Requests are never completed while it is held.
    SRWLOCK         Lock;

    BOOLEAN         Pumping;        // a thread is running PumpAll
    BOOLEAN         PumpAgain;      // new work arrived while it was

    WDFFILEOBJECT   ComFile;
    WDFFILEOBJECT   HostFile;

    // Requests wait here in arrival order until they become the current request of their kind.
    WDFQUEUE        ComReadQueue;
    WDFQUEUE        ComWriteQueue;
    WDFQUEUE        HostReadQueue;
    WDFQUEUE        HostWriteQueue;

    // In-progress requests. Each is marked cancelable with EvtRequestCancelCurrent.
    WDFREQUEST      CurrentRead;
    WDFREQUEST      CurrentWrite;
    WDFREQUEST      CurrentWait;
    WDFREQUEST      CurrentHostWrite;

    WDFTIMER        ReadTimer;
    WDFTIMER        WriteTimer;

    RING_BUFFER     ToCom;      // host -> application
    RING_BUFFER     ToHost;     // application -> host
    UCHAR           ToComStorage[VCOM_BUFFER_SIZE];
    UCHAR           ToHostStorage[VCOM_BUFFER_SIZE];

    // Serial settings. Stored and reported back; the data always passes through unchanged.
    ULONG               BaudRate;
    SERIAL_LINE_CONTROL LineControl;
    SERIAL_CHARS        Chars;
    SERIAL_HANDFLOW     HandFlow;
    SERIAL_TIMEOUTS     Timeouts;
    ULONG               DtrRts;         // SERIAL_DTR_STATE | SERIAL_RTS_STATE
    BOOLEAN             BreakOn;
    ULONG               ModemControl;
    ULONG               FifoControl;
    ULONG               WaitMask;
    ULONG               EventHistory;
    ULONG               HostLines;      // VCOM_LINE_* lines the COM end sees
    SERIALPERF_STATS    Stats;

    WCHAR           PortName[VCOM_PORT_NAME_LENGTH];
    PWCHAR          PdoName;
    BOOLEAN         RegisteredInSerialComm;
} DEVICE_CONTEXT, *PDEVICE_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(DEVICE_CONTEXT, GetDeviceContext)

// Requests to complete once the lock has been released.
typedef struct _COMPLETION_LIST {
    ULONG Count;
    struct {
        WDFREQUEST Request;
        NTSTATUS   Status;
        ULONG_PTR  Information;
    } Items[VCOM_MAX_COMPLETIONS];
} COMPLETION_LIST, *PCOMPLETION_LIST;

// driver.c
DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD EvtDeviceAdd;

// device.c
NTSTATUS DeviceCreate(_In_ PWDFDEVICE_INIT DeviceInit);
EVT_WDF_DEVICE_FILE_CREATE EvtDeviceFileCreate;
EVT_WDF_FILE_CLEANUP EvtFileCleanup;
EVT_WDF_OBJECT_CONTEXT_CLEANUP EvtDeviceCleanup;

// io.c
EVT_WDF_IO_QUEUE_IO_READ EvtIoRead;
EVT_WDF_IO_QUEUE_IO_WRITE EvtIoWrite;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL EvtIoDeviceControl;
EVT_WDF_REQUEST_CANCEL EvtRequestCancelCurrent;
EVT_WDF_TIMER EvtReadTimer;
EVT_WDF_TIMER EvtWriteTimer;
VOID PumpAll(_In_ PDEVICE_CONTEXT Context);
VOID ResetComSettings(_In_ PDEVICE_CONTEXT Context);
VOID SignalEvents(_In_ PDEVICE_CONTEXT Context, _In_ ULONG Events, _Inout_ PCOMPLETION_LIST Completions);
VOID SetHostLines(_In_ PDEVICE_CONTEXT Context, _In_ ULONG Lines, _Inout_ PCOMPLETION_LIST Completions);
VOID CancelRoleRequests(_In_ PDEVICE_CONTEXT Context, _In_ VCOM_ROLE Role, _In_opt_ WDFFILEOBJECT FileObject);
VOID DeferCompletion(_Inout_ PCOMPLETION_LIST List, _In_ WDFREQUEST Request, _In_ NTSTATUS Status, _In_ ULONG_PTR Information);
VOID CompleteDeferred(_Inout_ PCOMPLETION_LIST List);
BOOLEAN TakeCurrent(_Inout_ WDFREQUEST *Slot);
BOOLEAN MakeCurrent(_Inout_ WDFREQUEST *Slot, _In_ WDFREQUEST Request, _Inout_ PCOMPLETION_LIST Completions);

// comport.c
VOID ComDeviceControl(_In_ PDEVICE_CONTEXT Context, _In_ WDFREQUEST Request, _In_ ULONG IoControlCode);

// host.c
VOID HostDeviceControl(_In_ PDEVICE_CONTEXT Context, _In_ WDFREQUEST Request, _In_ ULONG IoControlCode);

FORCEINLINE VOID LockContext(_In_ PDEVICE_CONTEXT Context) { AcquireSRWLockExclusive(&Context->Lock); }
FORCEINLINE VOID UnlockContext(_In_ PDEVICE_CONTEXT Context) { ReleaseSRWLockExclusive(&Context->Lock); }
