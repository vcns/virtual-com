using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Vcns.VirtualCom.Bridge.Configuration;
using Vcns.VirtualCom.Bridge.Diagnostics;
using Vcns.VirtualCom.Bridge.VirtualPorts;
using Vcns.VirtualCom;

namespace Vcns.VirtualCom.Bridge.Transport;

/// <summary>The proxy's side of a virtual COM port: the other end from the one the serial application opens.</summary>
internal interface IPortEndpoint : IDisposable
{
    Stream Stream { get; }

    /// <summary>Tells the application on the COM port whether the TCP link is up, where the driver allows.</summary>
    void SetLinkUp(bool up);

    /// <summary>What the application on the COM port is doing, for display; null if the driver can't tell.</summary>
    string? DescribePeer();
}

/// <summary>How to open the proxy's side of a profile's virtual port.</summary>
internal sealed record PortTarget(string Description, Func<IPortEndpoint> Open)
{
    /// <summary>The VCNS virtual COM driver's host end of <paramref name="portName"/>.</summary>
    public static PortTarget Vcns(string portName) =>
        new($"{portName} (VCNS virtual COM driver)", () => VcnsEndpoint.Open(portName));

    /// <summary>Another serial device, such as the hidden end of a com0com pair, opened directly.</summary>
    public static PortTarget SerialDevice(string devicePortName) =>
        new(devicePortName, () => new SerialEndpoint(Transport.SerialDevice.Open(devicePortName)));

    /// <summary>Works out how to reach a profile's virtual port, or explains why it can't be used.</summary>
    public static bool TryResolve(ConnectionProfile profile, PortInventory inventory,
        [NotNullWhen(true)] out PortTarget? target, [NotNullWhen(false)] out string? problem)
    {
        var port = profile.LocalComPort;
        target = null;
        problem = null;
        if (profile.BridgePort is { } bridge)
            target = SerialDevice(bridge);
        else if (inventory.IsVcnsPort(port))
        {
            if (inventory.IsRunning(port))
                target = Vcns(port);
            else
                problem = $"{port} exists but Windows hasn't started it. Check Device Manager for a warning on \"VCNS Virtual COM Port ({port})\".";
        }
        else if (inventory.PartnerOf(port) is { } partner)
            target = SerialDevice(partner);
        else
            problem = $"{port} isn't a virtual COM port yet. Open Settings → GUI and choose \"Create missing virtual ports\".";
        return target is not null;
    }
}

/// <summary>Host end of a VCNS virtual port. Holds the COM end's modem lines low until the TCP link is up.</summary>
internal sealed class VcnsEndpoint : IPortEndpoint
{
    private readonly VirtualComHost _host;

    private VcnsEndpoint(VirtualComHost host) => _host = host;

    public Stream Stream => _host.Stream;

    public static IPortEndpoint Open(string portName)
    {
        var host = VirtualComHost.Open(portName);
        try
        {
            host.SetModemLines(ModemLines.None);
        }
        catch (IOException)
        {
            host.Dispose();
            throw;
        }
        return new VcnsEndpoint(host);
    }

    public void SetLinkUp(bool up) =>
        _host.SetModemLines(up ? ModemLines.Cts | ModemLines.Dsr | ModemLines.CarrierDetect : ModemLines.None);

    public string? DescribePeer()
    {
        try
        {
            var state = _host.GetState();
            if (!state.ComOpen)
                return $"No application has {state.PortName} open";
            var parity = state.Parity switch { 1 => "O", 2 => "E", 3 => "M", 4 => "S", _ => "N" };
            var stop = state.StopBits switch { 1 => "1.5", 2 => "2", _ => "1" };
            return $"{state.PortName} open at {state.BaudRate} baud, {state.DataBits}{parity}{stop}";
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or Win32Exception)
        {
            return null;
        }
    }

    public void Dispose() => _host.Dispose();
}

/// <summary>A serial device opened directly (com0com's hidden end, or another driver's paired port).</summary>
internal sealed class SerialEndpoint(Stream stream) : IPortEndpoint
{
    public Stream Stream { get; } = stream;

    // com0com mirrors the proxy's DTR/RTS (always raised) onto the serial application's DSR/CTS, so there is nothing to change.
    public void SetLinkUp(bool up)
    {
    }

    public string? DescribePeer() => null;

    public void Dispose()
    {
        try
        {
            Stream.Dispose();
        }
        catch (IOException ex)
        {
            Log.Warn($"Closing the virtual port failed: {ex.Message}");
        }
    }
}
