using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using NovaStarProxy.VirtualPorts;

namespace NovaStarProxy.Configuration;

internal static partial class ConfigValidator
{
    public const int MaxComNumber = 256;

    [GeneratedRegex("^COM([1-9][0-9]{0,2})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ComPortPattern();

    [GeneratedRegex("^[A-Z][A-Z0-9_]{0,31}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeviceNamePattern();

    public static bool TryParseComNumber(string? name, out int number)
    {
        number = 0;
        if (name is null)
            return false;
        var match = ComPortPattern().Match(name);
        return match.Success && int.TryParse(match.Groups[1].Value, out number) && number <= MaxComNumber;
    }

    /// <summary>Key identifying a controller endpoint for duplicate checks and session locking.</summary>
    /// <remarks>Formatted like <see cref="IPEndPoint.ToString"/> so it matches resolved addresses, but tolerates invalid ports.</remarks>
    public static string EndpointKey(string host, int port) => IPAddress.TryParse(host, out var ip)
        ? ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{ip}]:{port}" : $"{ip}:{port}"
        : $"{host.ToLowerInvariant()}:{port}";

    /// <summary>Checks the configuration on its own, without looking at the PC's ports or active sessions.</summary>
    public static IReadOnlyList<string> Validate(AppConfig config)
    {
        var errors = new List<string>();
        var profiles = config.Profiles;
        for (var i = 0; i < profiles.Count; i++)
            errors.AddRange(ValidateProfile(profiles[i]).Select(e => $"Row {i + 1}: {e}"));

        AddDuplicates(errors, profiles, p => p.LocalComPort,
            (port, rows) => $"{port} is assigned to more than one profile (rows {rows}). Each local COM port can belong to only one profile.");
        AddDuplicates(errors, profiles, p => string.IsNullOrEmpty(p.TargetServer) ? "" : EndpointKey(p.TargetServer, p.TargetPort),
            (_, rows) => $"Rows {rows} target the same controller endpoint. Each target server and port gets its own COM port.");
        AddDuplicates(errors, profiles, p => p.BridgePort ?? "",
            (port, rows) => $"Proxy-side port {port} is used by more than one profile (rows {rows}).");

        var localPorts = profiles.Select(p => p.LocalComPort).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < profiles.Count; i++)
        {
            if (profiles[i].BridgePort is { } bridge && localPorts.Contains(bridge))
                errors.Add($"Row {i + 1}: proxy-side port {bridge} is also used as a Local COM Port.");
        }

        var options = config.Options;
        if (options.ConnectTimeoutSeconds is < 1 or > 120)
            errors.Add("Connect timeout must be between 1 and 120 seconds.");
        if (options.ReleaseIntervalSeconds is < 0 or > 60)
            errors.Add("Release interval must be between 0 and 60 seconds.");
        if (options.InUseDetectionMilliseconds is < 0 or > 10000)
            errors.Add("In-use detection window must be between 0 and 10000 milliseconds.");
        return errors;
    }

    public static IReadOnlyList<string> ValidateProfile(ConnectionProfile profile)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(profile.TargetServer))
            errors.Add("Target Server is required.");
        else if (!IsValidHost(profile.TargetServer))
            errors.Add($"'{profile.TargetServer}' is not a valid IP address or host name.");

        if (profile.TargetPort is < 1 or > 65535)
            errors.Add($"Target Port {profile.TargetPort} is out of range (1–65535).");

        if (string.IsNullOrWhiteSpace(profile.LocalComPort))
            errors.Add("Local COM Port is required.");
        else if (!TryParseComNumber(profile.LocalComPort, out _))
            errors.Add($"'{profile.LocalComPort}' is not a valid COM port name (COM1–COM{MaxComNumber}).");

        if (profile.BridgePort is { } bridge)
        {
            if (!DeviceNamePattern().IsMatch(bridge))
                errors.Add($"Proxy-side port '{bridge}' is not a valid port name.");
            else if (string.Equals(bridge, profile.LocalComPort, StringComparison.OrdinalIgnoreCase))
                errors.Add("Proxy-side port must be the other end of the virtual pair, not the Local COM Port itself.");
        }
        return errors;
    }

    /// <summary>Connected profiles can't be removed or have their connection fields changed.</summary>
    public static IReadOnlyList<string> ValidateActiveChanges(AppConfig config, IEnumerable<ConnectionProfile> activeProfiles)
    {
        var errors = new List<string>();
        foreach (var active in activeProfiles)
        {
            var updated = config.Profiles.FirstOrDefault(p => p.Id == active.Id);
            if (updated is null)
                errors.Add($"{active.LocalComPort} is connected. Disconnect it before removing its profile.");
            else if (!updated.SameConnectionAs(active))
                errors.Add($"{active.LocalComPort} is connected. Disconnect it before changing its target server, port or COM port.");
        }
        return errors;
    }

    /// <summary>Rejects local COM ports that belong to other devices, so the proxy never touches them.</summary>
    public static IReadOnlyList<string> ValidatePorts(AppConfig config, PortInventory inventory)
    {
        var errors = new List<string>();
        for (var i = 0; i < config.Profiles.Count; i++)
        {
            var profile = config.Profiles[i];
            if (profile.BridgePort is not null || inventory.IsVirtual(profile.LocalComPort))
                continue;
            if (inventory.ForeignUse(profile.LocalComPort) is { } use)
                errors.Add($"Row {i + 1}: {profile.LocalComPort} is {use}. Choose another port number.");
        }
        return errors;
    }

    public static bool IsValidHost(string host)
    {
        if (host.Length is 0 or > 253)
            return false;
        if (IPAddress.TryParse(host, out var ip))
        {
            // IPAddress.TryParse also accepts shorthand such as "10.1"; insist on a full dotted quad.
            return ip.AddressFamily == AddressFamily.InterNetworkV6 || host.Count(c => c == '.') == 3;
        }
        // All digits and dots but not a valid address, e.g. 192.168.10.500.
        if (host.All(c => char.IsAsciiDigit(c) || c == '.'))
            return false;
        return Uri.CheckHostName(host) == UriHostNameType.Dns;
    }

    private static void AddDuplicates(List<string> errors, List<ConnectionProfile> profiles,
        Func<ConnectionProfile, string> key, Func<string, string, string> message)
    {
        var groups = profiles
            .Select((p, i) => (Key: key(p), Row: i + 1))
            .Where(x => x.Key.Length > 0)
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);
        foreach (var group in groups)
            errors.Add(message(group.Key, string.Join(", ", group.Select(x => x.Row))));
    }
}
