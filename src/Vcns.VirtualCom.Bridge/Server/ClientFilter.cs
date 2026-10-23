using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

namespace Vcns.VirtualCom.Bridge.Server;

/// <summary>The client addresses a shared port accepts: single addresses and CIDR ranges, IPv4 or IPv6.</summary>
internal sealed class ClientFilter
{
    private readonly List<(byte[] Network, int PrefixLength)> _ranges = [];

    public ClientFilter(IEnumerable<string> entries)
    {
        foreach (var entry in entries)
        {
            if (TryParse(entry, out var network, out var prefix))
                _ranges.Add((network.GetAddressBytes(), prefix));
        }
    }

    public bool Allows(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        return _ranges.Any(r => r.Network.Length == bytes.Length && Matches(bytes, r.Network, r.PrefixLength));
    }

    /// <summary>Parses "10.0.0.5", "10.20.0.0/16", "fd00::/8" and so on. The host bits of a range are ignored.</summary>
    public static bool TryParse(string? entry, [NotNullWhen(true)] out IPAddress? network, out int prefixLength)
    {
        network = null;
        prefixLength = 0;
        if (string.IsNullOrWhiteSpace(entry))
            return false;
        var text = entry.Trim();
        var slash = text.IndexOf('/');
        var addressText = slash < 0 ? text : text[..slash];
        if (!IPAddress.TryParse(addressText, out var address))
            return false;
        // IPAddress.TryParse accepts shorthand such as "10.1"; insist on a full dotted quad.
        if (address.AddressFamily == AddressFamily.InterNetwork && addressText.Count(c => c == '.') != 3)
            return false;
        var maxPrefix = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (slash < 0)
            prefixLength = maxPrefix;
        else if (!int.TryParse(text[(slash + 1)..], out prefixLength) || prefixLength < 0 || prefixLength > maxPrefix)
            return false;

        var bytes = address.GetAddressBytes();
        for (var bit = prefixLength; bit < bytes.Length * 8; bit++)
            bytes[bit / 8] &= (byte)~(0x80 >> (bit % 8));
        network = new IPAddress(bytes);
        return true;
    }

    private static bool Matches(byte[] address, byte[] network, int prefixLength)
    {
        for (var bit = 0; bit < prefixLength; bit++)
        {
            var mask = 0x80 >> (bit % 8);
            if ((address[bit / 8] & mask) != (network[bit / 8] & mask))
                return false;
        }
        return true;
    }
}
