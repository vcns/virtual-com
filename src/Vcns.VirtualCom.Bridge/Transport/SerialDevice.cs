using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Vcns.VirtualCom.Bridge.Transport;

/// <summary>
/// Opens the proxy side of a virtual serial pair as an overlapped (asynchronous) stream. System.IO.Ports is not
/// used because it refuses port names that don't start with "COM", such as com0com's CNCB0.
/// </summary>
internal static partial class SerialDevice
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint MaxDword = 0xFFFFFFFF;

    private const uint PurgeTxAbort = 0x0001;
    private const uint PurgeRxAbort = 0x0002;
    private const uint PurgeTxClear = 0x0004;
    private const uint PurgeRxClear = 0x0008;

    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;

    // DCB bit fields: fBinary (bit 0), fDtrControl = DTR_CONTROL_ENABLE (bits 4-5), fRtsControl = RTS_CONTROL_ENABLE
    // (bits 12-13). Everything else off: no parity checking, no flow control, no NUL stripping, no abort on error.
    private const uint DcbFlags = 0x0001 | (1 << 4) | (1 << 12);

    /// <summary>Opens <paramref name="portName"/> (e.g. CNCB0 or COM10) for raw 8N1 byte transfer.</summary>
    /// <exception cref="IOException">With a message suitable for showing to the user.</exception>
    public static FileStream Open(string portName)
    {
        var handle = CreateFile(@"\\.\" + portName, GenericRead | GenericWrite, 0, IntPtr.Zero, OpenExisting, FileFlagOverlapped, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw error switch
            {
                ErrorFileNotFound or ErrorPathNotFound => new IOException(
                    $"The virtual serial port {portName} does not exist or isn't running. In Device Manager, check the entries under "
                    + "\"com0com - serial port emulators\": a warning icon (Code 52) means Windows blocked the driver's signature."),
                ErrorAccessDenied or ErrorSharingViolation => new IOException(
                    $"The virtual serial port {portName} is already open in another program."),
                _ => new IOException($"Could not open {portName}: {new Win32Exception(error).Message}"),
            };
        }

        try
        {
            // The baud rate is irrelevant for a virtual null-modem (com0com ignores it unless baud emulation is
            // switched on) and never affects the TCP side; 8N1 with DTR and RTS raised tells the serial application a device is there.
            SetupComm(handle, 65536, 65536);

            var dcb = new Dcb { DCBlength = (uint)Marshal.SizeOf<Dcb>() };
            ThrowIfFailed(GetCommState(handle, ref dcb), portName, "read the port settings");
            dcb.BaudRate = 115200;
            dcb.ByteSize = 8;
            dcb.Parity = 0;
            dcb.StopBits = 0;
            dcb.Flags = DcbFlags;
            if (dcb.XonChar == dcb.XoffChar)
            {
                dcb.XonChar = 0x11;
                dcb.XoffChar = 0x13;
            }
            ThrowIfFailed(SetCommState(handle, ref dcb), portName, "apply the port settings");

            // Reads complete as soon as at least one byte is available, and otherwise wait (practically) forever,
            // so the reader neither polls nor imposes message boundaries. Writes have no timeout.
            var timeouts = new CommTimeouts
            {
                ReadIntervalTimeout = MaxDword,
                ReadTotalTimeoutMultiplier = MaxDword,
                ReadTotalTimeoutConstant = MaxDword - 1,
            };
            ThrowIfFailed(SetCommTimeouts(handle, ref timeouts), portName, "apply the port time-outs");
            PurgeComm(handle, PurgeTxAbort | PurgeRxAbort | PurgeTxClear | PurgeRxClear);

            return new FileStream(handle, FileAccess.ReadWrite, bufferSize: 0, isAsync: true);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void ThrowIfFailed(bool ok, string portName, string action)
    {
        if (!ok)
            throw new IOException($"Could not {action} of {portName}: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Dcb
    {
        public uint DCBlength;
        public uint BaudRate;
        public uint Flags;
        public ushort Reserved;
        public ushort XonLim;
        public ushort XoffLim;
        public byte ByteSize;
        public byte Parity;
        public byte StopBits;
        public byte XonChar;
        public byte XoffChar;
        public byte ErrorChar;
        public byte EofChar;
        public byte EvtChar;
        public ushort Reserved1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CommTimeouts
    {
        public uint ReadIntervalTimeout;
        public uint ReadTotalTimeoutMultiplier;
        public uint ReadTotalTimeoutConstant;
        public uint WriteTotalTimeoutMultiplier;
        public uint WriteTotalTimeoutConstant;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCommState(SafeFileHandle file, ref Dcb dcb);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCommState(SafeFileHandle file, ref Dcb dcb);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCommTimeouts(SafeFileHandle file, ref CommTimeouts timeouts);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupComm(SafeFileHandle file, uint inQueue, uint outQueue);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PurgeComm(SafeFileHandle file, uint flags);
}
