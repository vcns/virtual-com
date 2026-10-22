// SPDX-License-Identifier: MS-PL
// Copyright (c) 2026 VCNS Tech Ltd
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using static Vcns.VirtualCom.NativeMethods;

namespace Vcns.VirtualCom;

/// <summary>Modem-status lines the application on the COM end sees (as GetCommModemStatus reports them).</summary>
[Flags]
public enum ModemLines : uint
{
    None = 0,
    Cts = 0x10,
    Dsr = 0x20,
    Ring = 0x40,
    CarrierDetect = 0x80,
}

/// <summary>Snapshot of the COM end, from the host's point of view.</summary>
public sealed record VirtualComPortState(
    string PortName,
    bool ComOpen,
    bool Dtr,
    bool Rts,
    bool Break,
    uint BaudRate,
    byte DataBits,
    byte Parity,
    byte StopBits,
    uint BytesWaitingForCom,
    uint BytesWaitingForHost);

/// <summary>
/// The host end of a virtual COM port: what an application such as a TCP bridge opens to exchange bytes with
/// whatever has the COM end open. Reads complete as soon as at least one byte is available.
/// </summary>
public sealed class VirtualComHost : IDisposable
{
    public static readonly Guid InterfaceGuid = new("13DBB15D-28F8-4AEB-81B8-C45B6F98B810");

    private const uint FileDeviceSerialPort = 0x1B;
    private const uint IoctlGetPortState = (FileDeviceSerialPort << 16) | (1 << 14) | (0x900 << 2);
    private const uint IoctlSetModemStatus = (FileDeviceSerialPort << 16) | (2 << 14) | (0x901 << 2);
    private const int PortStateSize = 56;

    private readonly SafeFileHandle _handle;

    private VirtualComHost(SafeFileHandle handle, string portName)
    {
        _handle = handle;
        PortName = portName;
        Stream = new FileStream(handle, FileAccess.ReadWrite, bufferSize: 0, isAsync: true);
    }

    public string PortName { get; }

    /// <summary>Bytes written here are read by the COM-end application, and vice versa.</summary>
    public Stream Stream { get; }

    /// <exception cref="IOException">The port doesn't exist, isn't running, or its host end is already open.</exception>
    public static VirtualComHost Open(string portName)
    {
        var path = VirtualComDevices.FindHostPath(portName)
            ?? throw new IOException($"{portName} is not a running virtual COM port.");
        var handle = CreateFile(path, GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException(error == 5
                ? $"The host end of {portName} is already open in another program."
                : $"Opening the host end of {portName} failed: {new Win32Exception(error).Message}");
        }
        return new VirtualComHost(handle, portName);
    }

    public VirtualComPortState GetState()
    {
        var buffer = Control(IoctlGetPortState, [], PortStateSize);
        var flags = BitConverter.ToUInt32(buffer, 4);
        var name = Encoding.Unicode.GetString(buffer, 24, 32).TrimEnd('\0');
        return new VirtualComPortState(
            name,
            ComOpen: (flags & 1) != 0,
            Dtr: (flags & 2) != 0,
            Rts: (flags & 4) != 0,
            Break: (flags & 8) != 0,
            BaudRate: BitConverter.ToUInt32(buffer, 8),
            DataBits: buffer[12],
            Parity: buffer[13],
            StopBits: buffer[14],
            BytesWaitingForCom: BitConverter.ToUInt32(buffer, 16),
            BytesWaitingForHost: BitConverter.ToUInt32(buffer, 20));
    }

    /// <summary>Sets the modem-status lines the COM end sees. Opening the host end asserts CTS, DSR and DCD.</summary>
    public void SetModemLines(ModemLines lines) => Control(IoctlSetModemStatus, BitConverter.GetBytes((uint)lines), 0);

    public void Dispose()
    {
        Stream.Dispose();
        _handle.Dispose();
    }

    // The handle is opened for overlapped I/O and bound to the thread pool by FileStream, so control requests use
    // their own event, with the low bit set so the completion isn't also posted to the thread pool's port.
    private unsafe byte[] Control(uint code, byte[] input, int outputSize)
    {
        var output = new byte[outputSize];
        using var completed = new ManualResetEvent(false);
        var overlapped = (OVERLAPPED*)NativeMemory.AllocZeroed((nuint)sizeof(OVERLAPPED));
        try
        {
            overlapped->hEvent = completed.SafeWaitHandle.DangerousGetHandle() | 1;
            fixed (byte* inPtr = input)
            fixed (byte* outPtr = output)
            {
                if (!DeviceIoControl(_handle, code, inPtr, (uint)input.Length, outPtr, (uint)output.Length, out _, overlapped))
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error != ERROR_IO_PENDING)
                        throw new IOException($"Control request 0x{code:X} failed: {new Win32Exception(error).Message}");
                    completed.WaitOne();
                }
                if (!GetOverlappedResult(_handle, overlapped, out var transferred, wait: false))
                    throw new IOException($"Control request 0x{code:X} failed: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
                return transferred == output.Length ? output : output[..(int)transferred];
            }
        }
        finally
        {
            NativeMemory.Free(overlapped);
        }
    }
}
