// SPDX-License-Identifier: MS-PL
// Copyright (c) 2026 VCNS Tech Ltd
// Portions Copyright (c) Microsoft Corporation
/*
    Device creation, COM port registration and the two ends' open/close handling.
*/
#include "internal.h"

#define SYMBOLIC_LINK_PREFIX    L"\\DosDevices\\Global\\"
#define SERIAL_DEVICE_MAP       L"SERIALCOMM"
#define REG_VALUENAME_PORTNAME  L"PortName"

static NTSTATUS CreateQueues(_In_ PDEVICE_CONTEXT Context);
static NTSTATUS CreateTimers(_In_ PDEVICE_CONTEXT Context);
static NTSTATUS RegisterComPort(_In_ PDEVICE_CONTEXT Context);
static VCOM_ROLE RoleFromFileName(_In_opt_ PCUNICODE_STRING FileName);

NTSTATUS
DeviceCreate(
    _In_ PWDFDEVICE_INIT DeviceInit
    )
{
    WDF_FILEOBJECT_CONFIG fileConfig;
    WDF_OBJECT_ATTRIBUTES attributes;
    WDFDEVICE device;
    PDEVICE_CONTEXT context;
    NTSTATUS status;
    DECLARE_CONST_UNICODE_STRING(hostReference, VCOM_HOST_REFERENCE_STRING);

    WDF_FILEOBJECT_CONFIG_INIT(&fileConfig, EvtDeviceFileCreate, WDF_NO_EVENT_CALLBACK, EvtFileCleanup);
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, FILE_CONTEXT);
    WdfDeviceInitSetFileObjectConfig(DeviceInit, &fileConfig, &attributes);

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, REQUEST_CONTEXT);
    WdfDeviceInitSetRequestAttributes(DeviceInit, &attributes);

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, DEVICE_CONTEXT);
    attributes.EvtCleanupCallback = EvtDeviceCleanup;
    status = WdfDeviceCreate(&DeviceInit, &attributes, &device);
    if (!NT_SUCCESS(status)) {
        VcomTrace("WdfDeviceCreate failed 0x%08X", status);
        return status;
    }

    context = GetDeviceContext(device);
    context->Device = device;
    InitializeSRWLock(&context->Lock);
    RingInitialize(&context->ToCom, context->ToComStorage, sizeof(context->ToComStorage));
    RingInitialize(&context->ToHost, context->ToHostStorage, sizeof(context->ToHostStorage));
    ResetComSettings(context);

    status = CreateQueues(context);
    if (!NT_SUCCESS(status)) {
        return status;
    }
    status = CreateTimers(context);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    // The COM end is found through the standard COM port interface and the \DosDevices\COMn link; the host end
    // through our own interface, whose reference string tells the two apart when a handle is opened.
    status = WdfDeviceCreateDeviceInterface(device, &GUID_DEVINTERFACE_COMPORT, NULL);
    if (!NT_SUCCESS(status)) {
        VcomTrace("Creating the COM port interface failed 0x%08X", status);
        return status;
    }
    status = WdfDeviceCreateDeviceInterface(device, &GUID_DEVINTERFACE_VCOM_HOST, &hostReference);
    if (!NT_SUCCESS(status)) {
        VcomTrace("Creating the host interface failed 0x%08X", status);
        return status;
    }

    return RegisterComPort(context);
}

static NTSTATUS
CreateQueues(
    _In_ PDEVICE_CONTEXT Context
    )
{
    WDF_IO_QUEUE_CONFIG config;
    NTSTATUS status;
    WDFQUEUE *manualQueues[] = {
        &Context->ComReadQueue, &Context->ComWriteQueue, &Context->HostReadQueue, &Context->HostWriteQueue,
    };

    // There is no hardware to power down, so no queue is power-managed.
    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&config, WdfIoQueueDispatchParallel);
    config.PowerManaged = WdfFalse;
    config.EvtIoRead = EvtIoRead;
    config.EvtIoWrite = EvtIoWrite;
    config.EvtIoDeviceControl = EvtIoDeviceControl;
    status = WdfIoQueueCreate(Context->Device, &config, WDF_NO_OBJECT_ATTRIBUTES, WDF_NO_HANDLE);
    if (!NT_SUCCESS(status)) {
        VcomTrace("Creating the default queue failed 0x%08X", status);
        return status;
    }

    // Manual queues hold requests in arrival order; the framework cancels them if their caller goes away.
    for (ULONG i = 0; i < ARRAYSIZE(manualQueues); i++) {
        WDF_IO_QUEUE_CONFIG_INIT(&config, WdfIoQueueDispatchManual);
        config.PowerManaged = WdfFalse;
        status = WdfIoQueueCreate(Context->Device, &config, WDF_NO_OBJECT_ATTRIBUTES, manualQueues[i]);
        if (!NT_SUCCESS(status)) {
            VcomTrace("Creating a manual queue failed 0x%08X", status);
            return status;
        }
    }
    return STATUS_SUCCESS;
}

static NTSTATUS
CreateTimers(
    _In_ PDEVICE_CONTEXT Context
    )
{
    WDF_TIMER_CONFIG config;
    WDF_OBJECT_ATTRIBUTES attributes;
    NTSTATUS status;

    WDF_OBJECT_ATTRIBUTES_INIT(&attributes);
    attributes.ParentObject = Context->Device;

    WDF_TIMER_CONFIG_INIT(&config, EvtReadTimer);
    config.AutomaticSerialization = FALSE;
    status = WdfTimerCreate(&config, &attributes, &Context->ReadTimer);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    WDF_TIMER_CONFIG_INIT(&config, EvtWriteTimer);
    config.AutomaticSerialization = FALSE;
    return WdfTimerCreate(&config, &attributes, &Context->WriteTimer);
}

// Creates \DosDevices\COMn for the name the Ports class installer assigned, and lists it in SERIALCOMM so that
// applications enumerating serial ports find it.
static NTSTATUS
RegisterComPort(
    _In_ PDEVICE_CONTEXT Context
    )
{
    WDFKEY key;
    WDFMEMORY memory;
    WDF_OBJECT_ATTRIBUTES attributes;
    UNICODE_STRING pdoName;
    UNICODE_STRING portName;
    NTSTATUS status;
    WCHAR linkBuffer[64];
    UNICODE_STRING linkName;
    DECLARE_CONST_UNICODE_STRING(portNameValue, REG_VALUENAME_PORTNAME);
    DECLARE_CONST_UNICODE_STRING(serialComm, SERIAL_DEVICE_MAP);
    DECLARE_UNICODE_STRING_SIZE(comPort, VCOM_PORT_NAME_LENGTH);

    status = WdfDeviceOpenRegistryKey(Context->Device, PLUGPLAY_REGKEY_DEVICE, KEY_QUERY_VALUE,
                                      WDF_NO_OBJECT_ATTRIBUTES, &key);
    if (!NT_SUCCESS(status)) {
        VcomTrace("Opening the device key failed 0x%08X", status);
        return status;
    }
    status = WdfRegistryQueryUnicodeString(key, &portNameValue, NULL, &comPort);
    WdfRegistryClose(key);
    if (!NT_SUCCESS(status) || comPort.Length == 0) {
        VcomTrace("The device has no PortName value 0x%08X", status);
        return NT_SUCCESS(status) ? STATUS_INVALID_PARAMETER : status;
    }

    if (FAILED(StringCchCopyNW(Context->PortName, ARRAYSIZE(Context->PortName), comPort.Buffer,
                               comPort.Length / sizeof(WCHAR)))
        || FAILED(StringCchPrintfW(linkBuffer, ARRAYSIZE(linkBuffer), L"%s%s", SYMBOLIC_LINK_PREFIX, Context->PortName))) {
        return STATUS_NAME_TOO_LONG;
    }
    linkName.Buffer = linkBuffer;
    linkName.Length = (USHORT)(wcslen(linkBuffer) * sizeof(WCHAR));
    linkName.MaximumLength = sizeof(linkBuffer);

    status = WdfDeviceCreateSymbolicLink(Context->Device, &linkName);
    if (!NT_SUCCESS(status)) {
        VcomTrace("Creating %ls failed 0x%08X", linkBuffer, status);
        return status;
    }

    WDF_OBJECT_ATTRIBUTES_INIT(&attributes);
    attributes.ParentObject = Context->Device;
    status = WdfDeviceAllocAndQueryProperty(Context->Device, DevicePropertyPhysicalDeviceObjectName, NonPagedPoolNx,
                                            &attributes, &memory);
    if (!NT_SUCCESS(status)) {
        VcomTrace("Querying the PDO name failed 0x%08X", status);
        return STATUS_SUCCESS;  // The port works without a SERIALCOMM entry; enumeration just won't list it.
    }
    Context->PdoName = (PWCHAR)WdfMemoryGetBuffer(memory, NULL);

    status = WdfDeviceOpenDevicemapKey(Context->Device, &serialComm, KEY_SET_VALUE, WDF_NO_OBJECT_ATTRIBUTES, &key);
    if (NT_SUCCESS(status)) {
        RtlInitUnicodeString(&pdoName, Context->PdoName);
        RtlInitUnicodeString(&portName, Context->PortName);
        status = WdfRegistryAssignUnicodeString(key, &pdoName, &portName);
        Context->RegisteredInSerialComm = NT_SUCCESS(status);
        WdfRegistryClose(key);
    }
    if (!NT_SUCCESS(status)) {
        VcomTrace("Writing SERIALCOMM failed 0x%08X", status);
    }
    return STATUS_SUCCESS;
}

VOID
EvtDeviceCleanup(
    _In_ WDFOBJECT Object
    )
{
    PDEVICE_CONTEXT context = GetDeviceContext((WDFDEVICE)Object);
    WDFKEY key;
    UNICODE_STRING pdoName;
    DECLARE_CONST_UNICODE_STRING(serialComm, SERIAL_DEVICE_MAP);

    if (!context->RegisteredInSerialComm) {
        return;
    }

    // Remove only our own value. SERIALCOMM also lists every other serial port on the machine, so the key
    // itself must never be deleted.
    if (NT_SUCCESS(WdfDeviceOpenDevicemapKey(context->Device, &serialComm, KEY_SET_VALUE,
                                              WDF_NO_OBJECT_ATTRIBUTES, &key))) {
        RtlInitUnicodeString(&pdoName, context->PdoName);
        (VOID)WdfRegistryRemoveValue(key, &pdoName);
        WdfRegistryClose(key);
    }
}

static VCOM_ROLE
RoleFromFileName(
    _In_opt_ PCUNICODE_STRING FileName
    )
{
    PCWSTR name;
    int length;

    if (FileName == NULL || FileName->Length == 0) {
        return VcomRoleCom;
    }
    name = FileName->Buffer;
    length = FileName->Length / sizeof(WCHAR);
    if (length > 0 && name[0] == L'\\') {
        name++;
        length--;
    }
    if (length == 0) {
        return VcomRoleCom;
    }
    return CompareStringOrdinal(name, length, VCOM_HOST_REFERENCE_STRING, -1, TRUE) == CSTR_EQUAL
        ? VcomRoleHost
        : VcomRoleNone;
}

VOID
EvtDeviceFileCreate(
    _In_ WDFDEVICE Device,
    _In_ WDFREQUEST Request,
    _In_ WDFFILEOBJECT FileObject
    )
{
    PDEVICE_CONTEXT context = GetDeviceContext(Device);
    VCOM_ROLE role = RoleFromFileName(WdfFileObjectGetFileName(FileObject));
    COMPLETION_LIST completions;
    NTSTATUS status = STATUS_SUCCESS;

    completions.Count = 0;
    if (role == VcomRoleNone) {
        WdfRequestComplete(Request, STATUS_OBJECT_NAME_NOT_FOUND);
        return;
    }

    // Like a real serial port, each end can be open only once at a time.
    LockContext(context);
    if (role == VcomRoleCom) {
        if (context->ComFile != NULL) {
            status = STATUS_ACCESS_DENIED;
        } else {
            context->ComFile = FileObject;
            context->WaitMask = 0;
            context->EventHistory = 0;
            RingClear(&context->ToCom);
            ZeroMemory(&context->Stats, sizeof(context->Stats));
        }
    } else {
        if (context->HostFile != NULL) {
            status = STATUS_ACCESS_DENIED;
        } else {
            context->HostFile = FileObject;
            RingClear(&context->ToHost);
            SetHostLines(context, VCOM_HOST_LINES_DEFAULT, &completions);
        }
    }
    if (NT_SUCCESS(status)) {
        GetFileContext(FileObject)->Role = role;
    }
    UnlockContext(context);

    CompleteDeferred(&completions);
    VcomTrace("%ls: %s end open: 0x%08X", context->PortName, role == VcomRoleCom ? "COM" : "host", status);
    WdfRequestComplete(Request, status);
}

VOID
EvtFileCleanup(
    _In_ WDFFILEOBJECT FileObject
    )
{
    PDEVICE_CONTEXT context = GetDeviceContext(WdfFileObjectGetDevice(FileObject));
    VCOM_ROLE role = GetFileContext(FileObject)->Role;
    COMPLETION_LIST completions;

    completions.Count = 0;
    if (role == VcomRoleNone) {
        return;
    }

    // Detach the end first so the pumps can't start another of its requests, then fail what is outstanding.
    LockContext(context);
    if (role == VcomRoleCom && context->ComFile == FileObject) {
        context->ComFile = NULL;
        context->WaitMask = 0;
        context->EventHistory = 0;
        context->DtrRts = 0;
        context->BreakOn = FALSE;
        RingClear(&context->ToCom);
    } else if (role == VcomRoleHost && context->HostFile == FileObject) {
        context->HostFile = NULL;
        RingClear(&context->ToHost);
        SetHostLines(context, 0, &completions);
    }
    UnlockContext(context);

    CompleteDeferred(&completions);
    CancelRoleRequests(context, role, FileObject);
    VcomTrace("%ls: %s end closed", context->PortName, role == VcomRoleCom ? "COM" : "host");

    // Writes waiting for the other end may now be discarded, and the closed end's peer may have changes to see.
    PumpAll(context);
}
