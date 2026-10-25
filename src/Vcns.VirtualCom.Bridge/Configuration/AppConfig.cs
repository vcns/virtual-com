using System.Text.Json.Serialization;

namespace Vcns.VirtualCom.Bridge.Configuration;

internal sealed class AppConfig
{
    public int SchemaVersion { get; set; } = 1;

    public List<ConnectionProfile> Profiles { get; set; } = [];

    public ProxyOptions Options { get; set; } = new();

    public AppConfig Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        Profiles = Profiles.Select(p => p.Clone()).ToList(),
        Options = Options.Clone(),
    };
}

/// <summary>One-to-one mapping between a local virtual COM port and a remote TCP endpoint.</summary>
internal sealed class ConnectionProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public bool Enabled { get; set; } = true;

    public string TargetServer { get; set; } = "";

    public int TargetPort { get; set; }

    /// <summary>The port the serial application opens, e.g. COM9.</summary>
    public string LocalComPort { get; set; } = "";

    /// <summary>
    /// The other end of the virtual null-modem pair, which the proxy opens. Leave null to detect the
    /// com0com partner automatically; set it only when using a different virtual serial driver.
    /// </summary>
    public string? BridgePort { get; set; }

    /// <summary>Who or where the device belongs to, e.g. "Acme Ltd, Leeds". The tray menu groups profiles by it.</summary>
    public string? Customer { get; set; }

    /// <summary>What this port is for, e.g. "Main hall LED wall".</summary>
    public string? Label { get; set; }

    /// <summary>The device's make and model, e.g. "NovaPro UHD Jr".</summary>
    public string? Device { get; set; }

    /// <summary>Anything else worth knowing, such as which monitoring host polls it or who to call.</summary>
    public string? Notes { get; set; }

    public string Endpoint => TargetServer.Contains(':') ? $"[{TargetServer}]:{TargetPort}" : $"{TargetServer}:{TargetPort}";

    public string DisplayName => $"{Title} → {Endpoint}";

    /// <summary>The COM port with its label, e.g. "COM9 · Main hall LED wall", for menus and notifications.</summary>
    public string Title => Label is null ? LocalComPort : $"{LocalComPort} · {Label}";

    /// <summary>The name to give the virtual port in Device Manager (before "(COM9)"), or null for the standard name.</summary>
    public string? PortDisplayName => (Customer, Label) switch
    {
        (null, null) => null,
        ({ } customer, null) => customer,
        (null, { } label) => label,
        ({ } customer, { } label) => $"{customer} – {label}",
    };

    public ConnectionProfile Clone() => (ConnectionProfile)MemberwiseClone();

    /// <summary>True when the fields that determine what the profile connects to are identical.</summary>
    public bool SameConnectionAs(ConnectionProfile other) =>
        string.Equals(TargetServer, other.TargetServer, StringComparison.OrdinalIgnoreCase)
        && TargetPort == other.TargetPort
        && string.Equals(LocalComPort, other.LocalComPort, StringComparison.OrdinalIgnoreCase)
        && string.Equals(BridgePort ?? "", other.BridgePort ?? "", StringComparison.OrdinalIgnoreCase);
}

internal sealed class ProxyOptions
{
    public int ConnectTimeoutSeconds { get; set; } = 5;

    /// <summary>Pause after a session to an endpoint closes before another connection to it is attempted.</summary>
    public int ReleaseIntervalSeconds { get; set; } = 2;

    /// <summary>
    /// How long to watch a new connection before declaring it established. A single-session device closes the
    /// socket of a second concurrent client, so a close inside this window is reported as "in use".
    /// </summary>
    public int InUseDetectionMilliseconds { get; set; } = 1500;

    /// <summary>Show the edition's <see cref="BridgeEdition.ConnectReminder"/>, if it has one, before each connection.</summary>
    public bool ShowConnectReminder { get; set; } = true;

    /// <summary>The name NovaStar Proxy 1.0 saved <see cref="ShowConnectReminder"/> under. Read once, then folded into it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ShowZabbixReminder { get; set; }

    /// <summary>Writes every forwarded byte to the log as hex. Off by default: payloads can contain device configuration.</summary>
    public bool LogPayload { get; set; }

    /// <summary>Optional explicit path to com0com's setupc.exe when it isn't in the default install location.</summary>
    public string? SetupcPath { get; set; }

    public ProxyOptions Clone() => (ProxyOptions)MemberwiseClone();
}
