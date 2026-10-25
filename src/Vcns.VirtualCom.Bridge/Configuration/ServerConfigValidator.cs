using System.Net;
using System.Net.Sockets;
using Vcns.VirtualCom.Bridge.Server;
using Vcns.VirtualCom.Bridge.VirtualPorts;

namespace Vcns.VirtualCom.Bridge.Configuration;

internal static class ServerConfigValidator
{
    public const int MaxBaudRate = 4_000_000;

    /// <summary>Checks the server configuration on its own, without looking at the PC's ports.</summary>
    public static IReadOnlyList<string> Validate(ServerConfig config)
    {
        var errors = new List<string>();
        var ports = config.Ports;
        for (var i = 0; i < ports.Count; i++)
            errors.AddRange(ValidatePort(ports[i]).Select(e => $"Server row {i + 1}: {e}"));

        foreach (var group in ports.Select((p, i) => (p.ComPort, Row: i + 1))
                     .Where(x => x.ComPort.Length > 0)
                     .GroupBy(x => x.ComPort, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            errors.Add($"{group.Key} is shared by more than one server row (rows {string.Join(", ", group.Select(x => x.Row))}). Each COM port can be shared once.");
        }

        for (var i = 0; i < ports.Count; i++)
        {
            for (var j = i + 1; j < ports.Count; j++)
            {
                if (ports[i].TcpPort == ports[j].TcpPort && ListenersOverlap(ports[i].ListenAddress, ports[j].ListenAddress))
                    errors.Add($"Server rows {i + 1} and {j + 1} both listen on TCP port {ports[i].TcpPort}. Each row needs its own TCP port.");
            }
        }
        return errors;
    }

    public static IReadOnlyList<string> ValidatePort(ServerPort port)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(port.ComPort))
            errors.Add("COM Port is required.");
        else if (!ConfigValidator.TryParseComNumber(port.ComPort, out _))
            errors.Add($"'{port.ComPort}' is not a valid COM port name (COM1–COM{ConfigValidator.MaxComNumber}).");

        if (port.TcpPort is < 1 or > 65535)
            errors.Add($"TCP Port {port.TcpPort} is out of range (1–65535).");

        ConfigValidator.AddTextErrors(errors, "Label", port.Label, ConfigValidator.MaxNameLength);

        if (!IPAddress.TryParse(port.ListenAddress, out var listen))
            errors.Add($"Listen address '{port.ListenAddress}' is not an IP address. Choose one of this PC's addresses, or all interfaces.");
        else if (listen.AddressFamily == AddressFamily.InterNetwork && port.ListenAddress.Count(c => c == '.') != 3)
            errors.Add($"Listen address '{port.ListenAddress}' is not a full IPv4 address.");

        if (port.Enabled && port.AllowedClients.Count == 0)
            errors.Add("Allowed Clients is empty, so nothing could connect. List the addresses or ranges that may connect, or 0.0.0.0/0 for any.");
        foreach (var entry in port.AllowedClients)
        {
            if (!ClientFilter.TryParse(entry, out _, out _))
                errors.Add($"Allowed client '{entry}' is not an IP address or a range such as 10.20.0.0/16.");
        }

        var serial = port.Serial;
        if (serial.BaudRate is < 50 or > MaxBaudRate)
            errors.Add($"Baud rate {serial.BaudRate} is out of range (50–{MaxBaudRate:N0}).");
        if (serial.DataBits is < 5 or > 8)
            errors.Add($"Data bits must be 5 to 8, not {serial.DataBits}.");
        if (!Enum.IsDefined(serial.Parity) || !Enum.IsDefined(serial.StopBits) || !Enum.IsDefined(serial.FlowControl))
            errors.Add("The parity, stop bits or flow control setting isn't recognised.");
        return errors;
    }

    /// <summary>Rejects ports this PC can't share: virtual ports, which belong to the bridge's client side.</summary>
    public static IReadOnlyList<string> ValidatePorts(ServerConfig config, PortInventory inventory)
    {
        var errors = new List<string>();
        for (var i = 0; i < config.Ports.Count; i++)
        {
            var port = config.Ports[i];
            if (inventory.IsVirtual(port.ComPort))
                errors.Add($"Server row {i + 1}: {port.ComPort} is a virtual port. Server mode shares real COM ports, such as USB-attached devices.");
        }
        return errors;
    }

    /// <summary>Two listeners on the same TCP port clash unless they use different specific addresses.</summary>
    private static bool ListenersOverlap(string a, string b)
    {
        if (!IPAddress.TryParse(a, out var x) || !IPAddress.TryParse(b, out var y))
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        return x.Equals(IPAddress.Any) || x.Equals(IPAddress.IPv6Any) || y.Equals(IPAddress.Any) || y.Equals(IPAddress.IPv6Any) || x.Equals(y);
    }
}
