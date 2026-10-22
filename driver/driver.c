// SPDX-License-Identifier: MS-PL
// Copyright (c) 2026 VCNS Tech Ltd
/*
    Driver entry and device-add callback.
*/
#include "internal.h"

NTSTATUS
DriverEntry(
    _In_ PDRIVER_OBJECT DriverObject,
    _In_ PUNICODE_STRING RegistryPath
    )
{
    WDF_DRIVER_CONFIG config;

    WDF_DRIVER_CONFIG_INIT(&config, EvtDeviceAdd);
    return WdfDriverCreate(DriverObject, RegistryPath, WDF_NO_OBJECT_ATTRIBUTES, &config, WDF_NO_HANDLE);
}

NTSTATUS
EvtDeviceAdd(
    _In_ WDFDRIVER Driver,
    _Inout_ PWDFDEVICE_INIT DeviceInit
    )
{
    UNREFERENCED_PARAMETER(Driver);
    return DeviceCreate(DeviceInit);
}

#if DBG
VOID
VcomTraceImpl(
    _In_z_ _Printf_format_string_ PCSTR Format,
    ...
    )
{
    CHAR message[512];
    va_list args;

    va_start(args, Format);
    if (SUCCEEDED(StringCchVPrintfA(message, ARRAYSIZE(message), Format, args))) {
        OutputDebugStringA("vcom: ");
        OutputDebugStringA(message);
        OutputDebugStringA("\n");
    }
    va_end(args);
}
#endif
