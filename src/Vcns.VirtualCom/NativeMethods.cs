// SPDX-License-Identifier: MS-PL
// Copyright (c) 2026 VCNS Tech Ltd
using System.Runtime.InteropServices;

namespace Vcns.VirtualCom;

internal static partial class NativeMethods
{
    public const uint DICD_GENERATE_ID = 0x1;
    public const uint DIGCF_PRESENT = 0x2;
    public const uint SPDRP_HARDWAREID = 0x1;
    public const uint SPDRP_FRIENDLYNAME = 0xC;
    public const uint DIF_REGISTERDEVICE = 0x19;
    public const uint DIF_REMOVE = 0x5;
    public const uint DICS_FLAG_GLOBAL = 0x1;
    public const uint DIREG_DEV = 0x1;
    public const uint KEY_READ = 0x20019;
    public const uint KEY_ALL_ACCESS = 0xF003F;
    public const uint CR_SUCCESS = 0;
    public const uint CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0;
    public const uint DN_STARTED = 0x8;
    public const int ERROR_NO_MORE_ITEMS = 259;

    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    public const int ERROR_IO_PENDING = 997;

    public static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;

        public static SP_DEVINFO_DATA Create() => new() { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct OVERLAPPED
    {
        public IntPtr Internal;
        public IntPtr InternalHigh;
        public uint Offset;
        public uint OffsetHigh;
        public IntPtr hEvent;
    }

    [LibraryImport("setupapi.dll", SetLastError = true)]
    public static partial IntPtr SetupDiCreateDeviceInfoList(ref Guid classGuid, IntPtr hwndParent);

    [LibraryImport("setupapi.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [LibraryImport("setupapi.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiCreateDeviceInfoW(IntPtr deviceInfoSet, string deviceName, ref Guid classGuid,
        string? deviceDescription, IntPtr hwndParent, uint creationFlags, ref SP_DEVINFO_DATA deviceInfoData);

    [LibraryImport("setupapi.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiOpenDeviceInfoW(IntPtr deviceInfoSet, string deviceInstanceId, IntPtr hwndParent,
        uint openFlags, ref SP_DEVINFO_DATA deviceInfoData);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiSetDeviceRegistryPropertyW(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
        uint property, byte[] propertyBuffer, uint propertyBufferSize);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiGetDeviceRegistryPropertyW(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
        uint property, out uint propertyRegDataType, byte[]? propertyBuffer, uint propertyBufferSize, out uint requiredSize);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool SetupDiGetDeviceInstanceIdW(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
        char* deviceInstanceId, uint deviceInstanceIdSize, out uint requiredSize);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiCallClassInstaller(uint installFunction, IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    public static partial IntPtr SetupDiCreateDevRegKeyW(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, uint scope,
        uint hwProfile, uint keyType, IntPtr infHandle, IntPtr infSectionName);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    public static partial IntPtr SetupDiOpenDevRegKey(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, uint scope,
        uint hwProfile, uint keyType, uint samDesired);

    [LibraryImport("newdev.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DiInstallDevice(IntPtr hwndParent, IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
        IntPtr driverInfoData, uint flags, [MarshalAs(UnmanagedType.Bool)] out bool needReboot);

    [LibraryImport("newdev.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DiUninstallDevice(IntPtr hwndParent, IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
        uint flags, [MarshalAs(UnmanagedType.Bool)] out bool needReboot);

    [LibraryImport("newdev.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DiInstallDriverW(IntPtr hwndParent, string infPath, uint flags,
        [MarshalAs(UnmanagedType.Bool)] out bool needReboot);

    [LibraryImport("cfgmgr32.dll")]
    public static partial uint CM_Get_DevNode_Status(out uint status, out uint problemNumber, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint CM_Get_Device_Interface_List_SizeW(out uint length, ref Guid interfaceClassGuid,
        string? deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static unsafe partial uint CM_Get_Device_Interface_ListW(ref Guid interfaceClassGuid, string? deviceId,
        char* buffer, uint bufferLength, uint flags);

    [LibraryImport("msports.dll")]
    public static partial int ComDBOpen(out IntPtr hComDB);

    [LibraryImport("msports.dll")]
    public static partial int ComDBClose(IntPtr hComDB);

    [LibraryImport("msports.dll")]
    public static partial int ComDBClaimPort(IntPtr hComDB, uint comNumber, [MarshalAs(UnmanagedType.Bool)] bool forceClaim,
        [MarshalAs(UnmanagedType.Bool)] out bool forced);

    [LibraryImport("msports.dll")]
    public static partial int ComDBReleasePort(IntPtr hComDB, uint comNumber);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string fileName, uint desiredAccess,
        uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle device, uint ioControlCode,
        byte* inBuffer, uint inBufferSize, byte* outBuffer, uint outBufferSize, out uint bytesReturned, OVERLAPPED* overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool GetOverlappedResult(Microsoft.Win32.SafeHandles.SafeFileHandle file,
        OVERLAPPED* overlapped, out uint bytesTransferred, [MarshalAs(UnmanagedType.Bool)] bool wait);
}
