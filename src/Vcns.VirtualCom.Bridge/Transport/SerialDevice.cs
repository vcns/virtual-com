using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Vcns.VirtualCom.Bridge.Configuration;

namespace Vcns.VirtualCom.Bridge.Transport;

/// <summary>
/// Opens serial ports as overlapped (asynchronous) streams: the proxy side of a virtual serial pair, or a real port
/// shared in server mode. System.IO.Ports is not used because it refuses port names that don't start with "COM",
/// such as com0com's CNCB0.
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

    // DCB bit fields. fBinary (bit 0) is always set; fDtrControl (bits 4-5) and fRtsControl (bits 12-13) are
    // ENABLE (1), or HANDSHAKE (2) for RTS/CTS flow control. Unless chosen: no parity checking, no flow control,
    // no NUL stripping, no abort on error.
    private const uint DcbBinary = 0x0001;
    private const uint DcbParityCheck = 0x0002;
    private const uint DcbOutxCtsFlow = 0x0004;
    private const uint DcbOutX = 0x0100;
    private const uint DcbInX = 0x0200;
    private const uint DcbDtrEnable = 1 << 4;
    private const uint DcbRtsEnable = 1 << 12;
    private const uint DcbRtsHandshake = 2 << 12;

    /// <summary>Opens <paramref name="portName"/> (e.g. CNCB0 or COM10), the proxy side of a virtual pair, for raw 8N1 byte transfer.</summary>
    /// <exception cref="IOException">With a message suitable for showing to the user.</exception>
    public static FileStream Open(string portName) =>
        // The baud rate is irrelevant for a virtual null-modem (com0com ignores it unless baud emulation is switched
        // on) and never affects the TCP side; 8N1 with DTR and RTS raised tells the serial application a device is there.
        OpenCore(portName, new SerialSettings(), error => error switch
        {
            ErrorFileNotFound or ErrorPathNotFound => new IOException(
                $"The virtual serial port {portName} does not exist or isn't running. In Device Manager, check the entries under "
                + "\"com0com - serial port emulators\": a warning icon (Code 52) means Windows blocked the driver's signature."),
            ErrorAccessDenied or ErrorSharingViolation => new IOException(
                $"The virtual serial port {portName} is already open in another program."),
            _ => new IOException($"Could not open {portName}: {new Win32Exception(error).Message}"),
        });

    /// <summary>Opens a real COM port, such as a USB-attached device's, with the given line settings.</summary>
    /// <exception cref="IOException">With a message suitable for the log and the status shown to the user.</exception>
    public static FileStream OpenDevice(string portName, SerialSettings settings) =>
        OpenCore(portName, settings, error => error switch
        {
            ErrorFileNotFound or ErrorPathNotFound => new IOException(
                $"{portName} isn't present: the device may be unplugged or switched off, or its driver isn't running."),
            ErrorAccessDenied or ErrorSharingViolation => new IOException(
                $"{portName} is already open in another program, or this account isn't allowed to open it."),
            _ => new IOException($"Could not open {portName}: {new Win32Exception(error).Message}"),
        });

    private static FileStream OpenCore(string portName, SerialSettings settings, Func<int, IOException> describeOpenError)
    {
        var handle = CreateFile(@"\\.\" + portName, GenericRead | GenericWrite, 0, IntPtr.Zero, OpenExisting, FileFlagOverlapped, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw describeOpenError(error);
        }

        try
        {
            SetupComm(handle, 65536, 65536);

            var dcb = new Dcb { DCBlength = (uint)Marshal.SizeOf<Dcb>() };
            ThrowIfFailed(GetCommState(handle, ref dcb), portName, "read the port settings");
            dcb.BaudRate = (uint)settings.BaudRate;
            dcb.ByteSize = (byte)settings.DataBits;
            dcb.Parity = (byte)settings.Parity;
            dcb.StopBits = (byte)settings.StopBits;
            dcb.Flags = DcbBinary | DcbDtrEnable | (settings.Parity == SerialParity.None ? 0 : DcbParityCheck);
            switch (settings.FlowControl)
            {
                case SerialFlowControl.RtsCts:
                    dcb.Flags |= DcbOutxCtsFlow | DcbRtsHandshake;
                    break;
                case SerialFlowControl.XonXoff:
                    dcb.Flags |= DcbRtsEnable | DcbOutX | DcbInX;
                    dcb.XonLim = 2048;
                    dcb.XoffLim = 512;
                    break;
                default:
                    dcb.Flags |= DcbRtsEnable;
                    break;
            }
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
