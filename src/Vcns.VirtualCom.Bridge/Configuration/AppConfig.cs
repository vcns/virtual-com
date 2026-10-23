namespace NovaStarProxy.Configuration;

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

    public int TargetPort { get; set; } = 5200;

    /// <summary>The port NovaLCT opens, e.g. COM9.</summary>
    public string LocalComPort { get; set; } = "";

    /// <summary>
    /// The other end of the virtual null-modem pair, which the proxy opens. Leave null to detect the
    /// com0com partner automatically; set it only when using a different virtual serial driver.
    /// </summary>
    public string? BridgePort { get; set; }

    public string Endpoint => TargetServer.Contains(':') ? $"[{TargetServer}]:{TargetPort}" : $"{TargetServer}:{TargetPort}";

    public string DisplayName => $"{LocalComPort} → {Endpoint}";

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
    /// How long to watch a new connection before declaring it established. The controller closes the
    /// socket of a second concurrent client, so a close inside this window is reported as "in use".
    /// </summary>
    public int InUseDetectionMilliseconds { get; set; } = 1500;

    public bool ShowZabbixReminder { get; set; } = true;

    /// <summary>Writes every forwarded byte to the log as hex. Off by default: payloads can contain controller configuration.</summary>
    public bool LogPayload { get; set; }

    /// <summary>Optional explicit path to com0com's setupc.exe when it isn't in the default install location.</summary>
    public string? SetupcPath { get; set; }

    public ProxyOptions Clone() => (ProxyOptions)MemberwiseClone();
}
