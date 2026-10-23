namespace Vcns.VirtualCom.Bridge.Configuration;

/// <summary>
/// Server mode: this PC's COM ports, each shared on its own TCP port. Machine-wide; read by the bridge's Windows
/// service and changed only through the elevated helper.
/// </summary>
internal sealed class ServerConfig
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Master switch. Off: the service is removed and nothing listens.</summary>
    public bool Enabled { get; set; }

    public List<ServerPort> Ports { get; set; } = [];

    /// <summary>Writes every forwarded byte to the service log as hex. Off by default: payloads can contain device configuration.</summary>
    public bool LogPayload { get; set; }

    public ServerConfig Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        Enabled = Enabled,
        Ports = Ports.Select(p => p.Clone()).ToList(),
        LogPayload = LogPayload,
    };
}

/// <summary>One COM port shared on one TCP port.</summary>
internal sealed class ServerPort
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public bool Enabled { get; set; } = true;

    /// <summary>The real COM port to share, e.g. COM3.</summary>
    public string ComPort { get; set; } = "";

    public int TcpPort { get; set; }

    /// <summary>The local address to listen on: one interface's address, or 0.0.0.0 (or ::) for every interface.</summary>
    public string ListenAddress { get; set; } = "";

    /// <summary>Addresses or CIDR ranges (e.g. 10.20.0.0/16) that may connect. 0.0.0.0/0 allows any IPv4 client.</summary>
    public List<string> AllowedClients { get; set; } = [];

    /// <summary>Fixed line settings: raw TCP clients have no way to set them.</summary>
    public SerialSettings Serial { get; set; } = new();

    public string Endpoint => ListenAddress.Contains(':') ? $"[{ListenAddress}]:{TcpPort}" : $"{ListenAddress}:{TcpPort}";

    public ServerPort Clone()
    {
        var copy = (ServerPort)MemberwiseClone();
        copy.AllowedClients = [.. AllowedClients];
        copy.Serial = Serial.Clone();
        return copy;
    }

    /// <summary>True when a running listener for <paramref name="other"/> can be kept as it is.</summary>
    public bool SameAs(ServerPort other) =>
        Id == other.Id
        && Enabled == other.Enabled
        && string.Equals(ComPort, other.ComPort, StringComparison.OrdinalIgnoreCase)
        && TcpPort == other.TcpPort
        && string.Equals(ListenAddress, other.ListenAddress, StringComparison.OrdinalIgnoreCase)
        && AllowedClients.SequenceEqual(other.AllowedClients, StringComparer.OrdinalIgnoreCase)
        && Serial.SameAs(other.Serial);
}

internal enum SerialParity
{
    None,
    Odd,
    Even,
    Mark,
    Space,
}

internal enum SerialStopBits
{
    One,
    OnePointFive,
    Two,
}

internal enum SerialFlowControl
{
    None,
    RtsCts,
    XonXoff,
}

internal sealed class SerialSettings
{
    public int BaudRate { get; set; } = 115200;

    public int DataBits { get; set; } = 8;

    public SerialParity Parity { get; set; }

    public SerialStopBits StopBits { get; set; }

    public SerialFlowControl FlowControl { get; set; }

    /// <summary>The usual shorthand, e.g. 8N1 or 7E1.</summary>
    public string Format => $"{DataBits}{"NOEMS"[(int)Parity]}{StopBits switch { SerialStopBits.OnePointFive => "1.5", SerialStopBits.Two => "2", _ => "1" }}";

    public SerialSettings Clone() => (SerialSettings)MemberwiseClone();

    public bool SameAs(SerialSettings other) =>
        BaudRate == other.BaudRate && DataBits == other.DataBits && Parity == other.Parity
        && StopBits == other.StopBits && FlowControl == other.FlowControl;

    /// <summary>Parses shorthand such as 8N1, 7E1 or 8N2 into data bits, parity and stop bits.</summary>
    public static bool TryParseFormat(string text, out int dataBits, out SerialParity parity, out SerialStopBits stopBits)
    {
        dataBits = 0;
        parity = SerialParity.None;
        stopBits = SerialStopBits.One;
        text = text.Trim().ToUpperInvariant();
        if (text.Length < 3 || text[0] is < '5' or > '8')
            return false;
        var p = "NOEMS".IndexOf(text[1]);
        if (p < 0)
            return false;
        switch (text[2..])
        {
            case "1":
                stopBits = SerialStopBits.One;
                break;
            case "1.5":
                stopBits = SerialStopBits.OnePointFive;
                break;
            case "2":
                stopBits = SerialStopBits.Two;
                break;
            default:
                return false;
        }
        dataBits = text[0] - '0';
        parity = (SerialParity)p;
        return true;
    }
}
