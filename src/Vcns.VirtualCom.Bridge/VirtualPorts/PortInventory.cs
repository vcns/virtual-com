using System.Security;
using Microsoft.Win32;
using Vcns.VirtualCom.Bridge.Configuration;

namespace Vcns.VirtualCom.Bridge.VirtualPorts;

/// <summary>A com0com null-modem pair. Whatever one side writes, the other side reads.</summary>
internal sealed record Com0comPair(int Index, string PortA, string PortB)
{
    public string? PartnerOf(string port) =>
        string.Equals(port, PortA, StringComparison.OrdinalIgnoreCase) ? PortB
        : string.Equals(port, PortB, StringComparison.OrdinalIgnoreCase) ? PortA
        : null;
}

/// <summary>Registry data the inventory is built from. Kept separate so the logic can be tested without a registry.</summary>
internal sealed class RawPortData
{
    /// <summary>HKLM\HARDWARE\DEVICEMAP\SERIALCOMM: kernel device path → port name, for ports that exist right now.</summary>
    public Dictionary<string, string> SerialComm { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The COM Name Arbiter bitmap: bit n set means COM(n+1) is reserved, including for unplugged devices.</summary>
    public byte[]? ComDb { get; set; }

    /// <summary>Device instance ID → PortName, from every device's "Device Parameters", present or not.</summary>
    public Dictionary<string, string> DevicePortNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>com0com's per-port settings: CNCA0/CNCB0/... → value name → value.</summary>
    public Dictionary<string, Dictionary<string, string>> Com0comParameters { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Com0comInstalled { get; set; }

    /// <summary>Ports of the VCNS virtual COM driver.</summary>
    public List<VcnsPortInfo> VcnsPorts { get; } = [];

    /// <summary>This build carries the VCNS driver package, so it can create VCNS ports.</summary>
    public bool VcnsDriverAvailable { get; set; }
}

internal sealed record VcnsPortInfo(string InstanceId, string PortName, bool Running, uint ProblemCode);

/// <summary>A COM port that belongs to real hardware, with its kernel device name (e.g. \Device\USBSER000).</summary>
internal sealed record DevicePort(string PortName, string Device);

/// <summary>
/// Snapshot of the COM port numbers in use on this PC, the VCNS virtual ports and the com0com port pairs.
/// </summary>
internal sealed class PortInventory
{
    private const string Com0comDevicePrefix = @"\Device\com0com";
    private const string Com0comEnumerator = @"COM0COM\";
    private const string VcnsEnumerator = @"ROOT\VCNSVCOM\";

    private readonly Dictionary<int, string> _foreign = [];
    private readonly HashSet<int> _virtualNumbers = [];
    private readonly HashSet<string> _running;
    private readonly Dictionary<string, VcnsPortInfo> _vcns;

    internal PortInventory(RawPortData raw)
    {
        Com0comInstalled = raw.Com0comInstalled;
        VcnsDriverAvailable = raw.VcnsDriverAvailable;
        Pairs = BuildPairs(raw);
        _running = raw.SerialComm.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _vcns = raw.VcnsPorts.ToDictionary(p => p.PortName, StringComparer.OrdinalIgnoreCase);

        foreach (var name in Pairs.SelectMany(p => new[] { p.PortA, p.PortB }).Concat(_vcns.Keys))
        {
            if (ConfigValidator.TryParseComNumber(name, out var n))
                _virtualNumbers.Add(n);
        }

        var devicePorts = new List<DevicePort>();
        foreach (var (device, name) in raw.SerialComm)
        {
            if (!device.StartsWith(Com0comDevicePrefix, StringComparison.OrdinalIgnoreCase) && !_vcns.ContainsKey(name))
            {
                AddForeign(name, $"in use by another device ({device})");
                devicePorts.Add(new DevicePort(name, device));
            }
        }
        DevicePorts = devicePorts
            .OrderBy(p => ConfigValidator.TryParseComNumber(p.PortName, out var n) ? n : int.MaxValue)
            .ThenBy(p => p.PortName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var (instance, name) in raw.DevicePortNames)
        {
            if (!instance.StartsWith(Com0comEnumerator, StringComparison.OrdinalIgnoreCase)
                && !instance.StartsWith(VcnsEnumerator, StringComparison.OrdinalIgnoreCase))
                AddForeign(name, $"assigned to another device ({instance}), which may be unplugged at the moment");
        }
        if (raw.ComDb is { } comDb)
        {
            for (var bit = 0; bit < comDb.Length * 8 && bit < ConfigValidator.MaxComNumber; bit++)
            {
                if ((comDb[bit / 8] & (1 << (bit % 8))) != 0)
                    AddForeign($"COM{bit + 1}", "reserved by Windows for another device");
            }
        }
        AddForeign("COM1", "reserved for the PC's built-in serial port");
    }

    public IReadOnlyList<Com0comPair> Pairs { get; }

    public bool Com0comInstalled { get; }

    public bool VcnsDriverAvailable { get; }

    public IReadOnlyCollection<VcnsPortInfo> VcnsPorts => _vcns.Values;

    /// <summary>Real COM ports present right now, such as USB-attached devices: the ports server mode can share.</summary>
    public IReadOnlyList<DevicePort> DevicePorts { get; }

    /// <summary>COM-named virtual ports: VCNS ports and either side of a com0com pair.</summary>
    public IEnumerable<string> VirtualComPorts =>
        _vcns.Keys.Concat(Pairs.SelectMany(p => new[] { p.PortA, p.PortB })).Where(n => ConfigValidator.TryParseComNumber(n, out _));

    public bool IsVcnsPort(string port) => _vcns.ContainsKey(port);

    /// <summary>A virtual port of either kind, so no other device owns it.</summary>
    public bool IsVirtual(string port) => IsVcnsPort(port) || PairOf(port) is not null;

    public static PortInventory Capture(bool includeDeviceScan = true) => new(ReadRegistry(includeDeviceScan));

    public Com0comPair? PairOf(string port) => Pairs.FirstOrDefault(p => p.PartnerOf(port) is not null);

    /// <summary>The port the proxy opens for <paramref name="port"/>, or null if it isn't a com0com port.</summary>
    public string? PartnerOf(string port) => PairOf(port)?.PartnerOf(port);

    /// <summary>
    /// True if Windows has started the port's device. A com0com pair whose driver is blocked (for example by driver
    /// signature enforcement) still exists in com0com's settings but never appears here.
    /// </summary>
    public bool IsRunning(string port) => _vcns.TryGetValue(port, out var vcns) ? vcns.Running : _running.Contains(port);

    /// <summary>Why <paramref name="port"/> belongs to something other than com0com, or null if it doesn't.</summary>
    public string? ForeignUse(string port) =>
        ConfigValidator.TryParseComNumber(port, out var n) && !_virtualNumbers.Contains(n) && _foreign.TryGetValue(n, out var use) ? use : null;

    /// <summary>
    /// Free COM names above every number currently used or reserved, so new virtual ports never sit
    /// between, or collide with, ports that belong to real devices.
    /// </summary>
    public IReadOnlyList<string> SuggestFreePorts(IEnumerable<string> alsoTaken, int count)
    {
        var highest = _foreign.Keys.Concat(_virtualNumbers).Append(1).Max();
        foreach (var name in alsoTaken)
        {
            if (ConfigValidator.TryParseComNumber(name, out var n))
                highest = Math.Max(highest, n);
        }
        return Enumerable.Range(highest + 1, Math.Max(0, Math.Min(count, ConfigValidator.MaxComNumber - highest)))
            .Select(n => $"COM{n}")
            .ToList();
    }

    public string Describe(string port)
    {
        if (_vcns.TryGetValue(port, out var vcns))
        {
            return vcns.Running
                ? "Virtual port ready (VCNS virtual COM driver)"
                : $"⚠ Created but not running (Device Manager problem code {vcns.ProblemCode})";
        }
        if (PairOf(port) is { } pair)
        {
            return IsRunning(port)
                ? $"Virtual port ready (com0com pair {pair.Index}, proxy side {pair.PartnerOf(port)})"
                : $"⚠ Created but not running: Windows hasn't started com0com pair {pair.Index} (check Device Manager)";
        }
        if (ForeignUse(port) is { } use)
            return $"⚠ Not available: {use}";
        return VcnsDriverAvailable || Com0comInstalled
            ? "Virtual port not created yet"
            : "Virtual port not created (no virtual port driver available)";
    }

    private void AddForeign(string name, string description)
    {
        if (ConfigValidator.TryParseComNumber(name, out var n))
            _foreign.TryAdd(n, description);
    }

    private static List<Com0comPair> BuildPairs(RawPortData raw)
    {
        var names = new Dictionary<(int Index, bool IsA), string>();

        // Sources in increasing order of trust; later ones overwrite earlier ones.
        foreach (var (instance, name) in raw.DevicePortNames)
        {
            if (instance.StartsWith(Com0comEnumerator, StringComparison.OrdinalIgnoreCase)
                && TryParsePortId(instance[(instance.LastIndexOf('\\') + 1)..], out var key))
                names[key] = name;
        }
        foreach (var (id, values) in raw.Com0comParameters)
        {
            if (TryParsePortId(id, out var key) && ResolveConfiguredName(id, values) is { } name)
                names[key] = name;
        }
        foreach (var (device, name) in raw.SerialComm)
        {
            // com0com registers pair n as \Device\com0com1<n> (side A) and \Device\com0com2<n> (side B).
            if (!device.StartsWith(Com0comDevicePrefix, StringComparison.OrdinalIgnoreCase))
                continue;
            var suffix = device[Com0comDevicePrefix.Length..];
            if (suffix.Length >= 2 && suffix[0] is '1' or '2' && int.TryParse(suffix[1..], out var index))
                names[(index, suffix[0] == '1')] = name;
        }

        foreach (var id in raw.Com0comParameters.Keys)
        {
            if (TryParsePortId(id, out var key))
                names.TryAdd(key, id.ToUpperInvariant());
        }

        return names.Keys.Select(k => k.Index).Distinct().Order()
            .Select(i => new Com0comPair(i,
                names.GetValueOrDefault((i, true)) ?? $"CNCA{i}",
                names.GetValueOrDefault((i, false)) ?? $"CNCB{i}"))
            .ToList();
    }

    private static bool TryParsePortId(string id, out (int Index, bool IsA) key)
    {
        key = default;
        if (id.Length < 5 || !id.StartsWith("CNC", StringComparison.OrdinalIgnoreCase))
            return false;
        var side = char.ToUpperInvariant(id[3]);
        if (side is not ('A' or 'B') || !int.TryParse(id[4..], out var index))
            return false;
        key = (index, side == 'A');
        return true;
    }

    private static string? ResolveConfiguredName(string id, Dictionary<string, string> values)
    {
        if (!values.TryGetValue("PortName", out var name) || string.IsNullOrWhiteSpace(name) || name.Trim() == "-")
            return id.ToUpperInvariant();
        name = name.Trim();
        // "COM#" asks Windows to pick the number; the chosen one is stored as RealPortName.
        if (name.Equals("COM#", StringComparison.OrdinalIgnoreCase))
            return values.TryGetValue("RealPortName", out var real) && !string.IsNullOrWhiteSpace(real) ? real.Trim() : null;
        return name;
    }

    private static RawPortData ReadRegistry(bool includeDeviceScan)
    {
        var raw = new RawPortData();
        var hklm = Registry.LocalMachine;

        using (var key = TryOpen(hklm, @"HARDWARE\DEVICEMAP\SERIALCOMM"))
        {
            foreach (var name in key?.GetValueNames() ?? [])
            {
                if (key!.GetValue(name) is string port)
                    raw.SerialComm[name] = port.Trim().ToUpperInvariant();
            }
        }

        using (var key = TryOpen(hklm, @"SYSTEM\CurrentControlSet\Control\COM Name Arbiter"))
            raw.ComDb = key?.GetValue("ComDB") as byte[];

        using (var service = TryOpen(hklm, @"SYSTEM\CurrentControlSet\Services\com0com"))
        {
            raw.Com0comInstalled = service is not null;
            using var parameters = service is null ? null : TryOpen(service, "Parameters");
            foreach (var id in SubKeyNames(parameters))
            {
                using var port = TryOpen(parameters!, id);
                if (port is null)
                    continue;
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var valueName in port.GetValueNames())
                    values[valueName] = port.GetValue(valueName)?.ToString() ?? "";
                raw.Com0comParameters[id] = values;
            }
        }

        try
        {
            foreach (var port in Vcns.VirtualCom.VirtualComDevices.List())
            {
                if (port.PortName is not null)
                    raw.VcnsPorts.Add(new VcnsPortInfo(port.InstanceId, port.PortName, port.IsRunning, port.ProblemCode));
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Listing devices needs no special rights; if it fails anyway, treat it as no VCNS ports.
        }
        raw.VcnsDriverAvailable = DriverPackage.IsBundled;

        if (includeDeviceScan)
            ScanDevices(raw);
        return raw;
    }

    /// <summary>Finds PortName assignments for every known device, including ones not plugged in.</summary>
    private static void ScanDevices(RawPortData raw)
    {
        using var root = TryOpen(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Enum");
        foreach (var enumerator in SubKeyNames(root))
        {
            using var enumKey = TryOpen(root!, enumerator);
            foreach (var device in SubKeyNames(enumKey))
            {
                using var deviceKey = TryOpen(enumKey!, device);
                foreach (var instance in SubKeyNames(deviceKey))
                {
                    using var parameters = TryOpen(deviceKey!, instance + @"\Device Parameters");
                    if (parameters?.GetValue("PortName") is string name && name.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                        raw.DevicePortNames[$@"{enumerator}\{device}\{instance}"] = name.Trim().ToUpperInvariant();
                }
            }
        }
    }

    private static RegistryKey? TryOpen(RegistryKey parent, string path)
    {
        try
        {
            return parent.OpenSubKey(path, writable: false);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static string[] SubKeyNames(RegistryKey? key)
    {
        try
        {
            return key?.GetSubKeyNames() ?? [];
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }
}
