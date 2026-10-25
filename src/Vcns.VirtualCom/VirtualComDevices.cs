// SPDX-License-Identifier: MS-PL
// Copyright (c) 2026 VCNS Tech Ltd
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using static Vcns.VirtualCom.NativeMethods;

namespace Vcns.VirtualCom;

/// <summary>A virtual COM port device created by this library.</summary>
/// <param name="InstanceId">Device instance ID, e.g. ROOT\VCNSVCOM\0000.</param>
/// <param name="PortName">The COM name applications open, e.g. COM9.</param>
/// <param name="IsRunning">The driver is loaded and the port can be opened.</param>
/// <param name="ProblemCode">Device Manager problem code when not running (52 = signature rejected).</param>
public sealed record VirtualComPort(string InstanceId, string? PortName, bool IsRunning, uint ProblemCode);

/// <summary>
/// Creates, lists and removes virtual COM port devices. Creating and removing ports and installing the driver
/// require administrator rights; listing does not.
/// </summary>
public static class VirtualComDevices
{
    public const string HardwareId = @"Root\VcnsVirtualCom";

    private const string DeviceIdBase = "VCNSVCOM";
    private const string DeviceDescription = "VCNS Virtual COM Port";
    private static readonly Guid PortsClass = new("4D36E978-E325-11CE-BFC1-08002BE10318");

    /// <summary>Adds the driver package to the driver store (and updates any existing ports to it).</summary>
    public static bool InstallDriver(string infPath)
    {
        if (!DiInstallDriverW(IntPtr.Zero, Path.GetFullPath(infPath), 0, out var needReboot))
            throw LastError($"Installing the driver package {infPath} failed");
        return needReboot;
    }

    /// <summary>Creates a port with the given COM name and installs the driver on it.</summary>
    public static VirtualComPort Create(string portName)
    {
        portName = portName.Trim().ToUpperInvariant();
        if (List().Any(p => string.Equals(p.PortName, portName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"{portName} already exists.");

        var classGuid = PortsClass;
        var set = SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
        if (set == InvalidHandle)
            throw LastError("Creating a device information set failed");
        try
        {
            var device = SP_DEVINFO_DATA.Create();
            if (!SetupDiCreateDeviceInfoW(set, DeviceIdBase, ref classGuid, DeviceDescription, IntPtr.Zero, DICD_GENERATE_ID, ref device))
                throw LastError("Creating the device node failed");

            var hardwareIds = Encoding.Unicode.GetBytes(HardwareId + "\0\0");
            if (!SetupDiSetDeviceRegistryPropertyW(set, ref device, SPDRP_HARDWAREID, hardwareIds, (uint)hardwareIds.Length))
                throw LastError("Setting the hardware ID failed");
            if (!SetupDiCallClassInstaller(DIF_REGISTERDEVICE, set, ref device))
                throw LastError("Registering the device failed");

            try
            {
                // Choose the COM number before the driver installs, so the Ports class installer keeps it.
                using (var key = OpenDeviceKey(set, ref device, create: true))
                    key.SetValue("PortName", portName, RegistryValueKind.String);
                ClaimComNumber(portName);

                if (!DiInstallDevice(IntPtr.Zero, set, ref device, IntPtr.Zero, 0, out _))
                    throw LastError("Installing the driver on the new device failed");
            }
            catch
            {
                SetupDiCallClassInstaller(DIF_REMOVE, set, ref device);
                ReleaseComNumber(portName);
                throw;
            }

            return Describe(set, ref device);
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    /// <summary>
    /// Sets the name Device Manager and port lists show for a port, e.g. "Main hall LED wall (COM9)". A null or blank
    /// label restores the standard "VCNS Virtual COM Port (COM9)". Requires administrator rights. Reinstalling or
    /// updating the driver on the port can restore the standard name.
    /// </summary>
    public static void SetDisplayName(string portName, string? label)
    {
        var port = List().FirstOrDefault(p => string.Equals(p.PortName, portName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No virtual COM port called {portName}.");

        var classGuid = PortsClass;
        var set = SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
        if (set == InvalidHandle)
            throw LastError("Creating a device information set failed");
        try
        {
            var device = SP_DEVINFO_DATA.Create();
            if (!SetupDiOpenDeviceInfoW(set, port.InstanceId, IntPtr.Zero, 0, ref device))
                throw LastError($"Opening {port.InstanceId} failed");
            var name = $"{(string.IsNullOrWhiteSpace(label) ? DeviceDescription : label.Trim())} ({port.PortName})";
            var bytes = Encoding.Unicode.GetBytes(name + "\0");
            if (!SetupDiSetDeviceRegistryPropertyW(set, ref device, SPDRP_FRIENDLYNAME, bytes, (uint)bytes.Length))
                throw LastError($"Naming {port.PortName} failed");
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    /// <summary>Removes the port with the given COM name or device instance ID.</summary>
    public static void Remove(string portNameOrInstanceId)
    {
        var port = List().FirstOrDefault(p =>
                string.Equals(p.PortName, portNameOrInstanceId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.InstanceId, portNameOrInstanceId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No virtual COM port called {portNameOrInstanceId}.");

        var classGuid = PortsClass;
        var set = SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
        if (set == InvalidHandle)
            throw LastError("Creating a device information set failed");
        try
        {
            var device = SP_DEVINFO_DATA.Create();
            if (!SetupDiOpenDeviceInfoW(set, port.InstanceId, IntPtr.Zero, 0, ref device))
                throw LastError($"Opening {port.InstanceId} failed");
            if (!DiUninstallDevice(IntPtr.Zero, set, ref device, 0, out _))
                throw LastError($"Removing {port.InstanceId} failed");
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        if (port.PortName is not null)
            ReleaseComNumber(port.PortName);
    }

    /// <summary>All virtual COM ports, including ones whose driver isn't running.</summary>
    public static IReadOnlyList<VirtualComPort> List()
    {
        var classGuid = PortsClass;
        var set = SetupDiGetClassDevsW(ref classGuid, null, IntPtr.Zero, 0);
        if (set == InvalidHandle)
            throw LastError("Listing ports failed");
        try
        {
            var ports = new List<VirtualComPort>();
            var device = SP_DEVINFO_DATA.Create();
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref device); i++)
            {
                if (GetMultiString(set, ref device, SPDRP_HARDWAREID).Any(id => id.Equals(HardwareId, StringComparison.OrdinalIgnoreCase)))
                    ports.Add(Describe(set, ref device));
            }
            return ports;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    /// <summary>The path an application opens to become the host end of a port, or null if it isn't running.</summary>
    public static string? FindHostPath(string portName)
    {
        var port = List().FirstOrDefault(p => string.Equals(p.PortName, portName, StringComparison.OrdinalIgnoreCase));
        if (port is null)
            return null;

        var hostInterface = VirtualComHost.InterfaceGuid;
        if (CM_Get_Device_Interface_List_SizeW(out var length, ref hostInterface, port.InstanceId, CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != CR_SUCCESS
            || length <= 1)
            return null;
        var buffer = new char[length];
        unsafe
        {
            fixed (char* p = buffer)
            {
                if (CM_Get_Device_Interface_ListW(ref hostInterface, port.InstanceId, p, length, CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != CR_SUCCESS)
                    return null;
            }
        }
        return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    }

    private static unsafe VirtualComPort Describe(IntPtr set, ref SP_DEVINFO_DATA device)
    {
        var idBuffer = new char[512];
        uint idLength;
        fixed (char* p = idBuffer)
        {
            if (!SetupDiGetDeviceInstanceIdW(set, ref device, p, (uint)idBuffer.Length, out idLength))
                throw LastError("Reading the device instance ID failed");
        }
        var instanceId = new string(idBuffer, 0, (int)Math.Max(0, idLength - 1));

        string? portName = null;
        try
        {
            using var key = OpenDeviceKey(set, ref device, create: false);
            portName = key.GetValue("PortName") as string;
        }
        catch (Win32Exception)
        {
        }

        var running = CM_Get_DevNode_Status(out var status, out var problem, device.DevInst, 0) == CR_SUCCESS
                      && (status & DN_STARTED) != 0;
        return new VirtualComPort(instanceId, portName, running, running ? 0 : problem);
    }

    private static RegistryKey OpenDeviceKey(IntPtr set, ref SP_DEVINFO_DATA device, bool create)
    {
        var handle = create
            ? SetupDiCreateDevRegKeyW(set, ref device, DICS_FLAG_GLOBAL, 0, DIREG_DEV, IntPtr.Zero, IntPtr.Zero)
            : SetupDiOpenDevRegKey(set, ref device, DICS_FLAG_GLOBAL, 0, DIREG_DEV, KEY_READ);
        if (handle == InvalidHandle)
            throw LastError("Opening the device's registry key failed");
        return RegistryKey.FromHandle(new SafeRegistryHandle(handle, ownsHandle: true));
    }

    private static string[] GetMultiString(IntPtr set, ref SP_DEVINFO_DATA device, uint property)
    {
        SetupDiGetDeviceRegistryPropertyW(set, ref device, property, out _, null, 0, out var size);
        if (size == 0)
            return [];
        var buffer = new byte[size];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref device, property, out _, buffer, size, out _))
            return [];
        return Encoding.Unicode.GetString(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    // The COM Name Arbiter database records which numbers are taken, so other devices won't be given ours.
    private static void ClaimComNumber(string portName)
    {
        if (!TryParseComNumber(portName, out var number) || ComDBOpen(out var db) != 0)
            return;
        try
        {
            ComDBClaimPort(db, number, forceClaim: true, out _);
        }
        finally
        {
            ComDBClose(db);
        }
    }

    private static void ReleaseComNumber(string portName)
    {
        if (!TryParseComNumber(portName, out var number) || ComDBOpen(out var db) != 0)
            return;
        try
        {
            ComDBReleasePort(db, number);
        }
        finally
        {
            ComDBClose(db);
        }
    }

    private static bool TryParseComNumber(string portName, out uint number)
    {
        number = 0;
        return portName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) && uint.TryParse(portName.AsSpan(3), out number) && number is > 0 and <= 256;
    }

    private static Win32Exception LastError(string message)
    {
        var error = Marshal.GetLastPInvokeError();
        return new Win32Exception(error, $"{message}: {new Win32Exception(error).Message} (0x{error:X8})");
    }
}
