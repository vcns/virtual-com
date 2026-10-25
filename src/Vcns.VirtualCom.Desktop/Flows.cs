using Vcns.VirtualCom.Bridge;

namespace Vcns.VirtualCom.Desktop;

/// <summary>The two ways bytes travel, in the edition's words, for the Settings tabs and the About tab.</summary>
internal static class Flows
{
    /// <summary>Client mode: a serial application on this PC reaches a device on the network.</summary>
    public static FlowDiagram Client(BridgeEdition edition)
    {
        var diagram = new FlowDiagram();
        diagram.SetFlow(
        [
            new(edition.SerialApplicationAtStart, "opens the COM port"),
            new(edition.ProductName, "on this PC", Highlight: true),
            new(AtStart(edition.DeviceNoun), edition.DefaultTargetPort is { } port ? $"on the network, TCP {port}" : "on the network, its TCP port"),
        ],
        [
            new("virtual COM9"),
            new("TCP over the network", Network: true),
        ]);
        return diagram;
    }

    /// <summary>Server mode: clients on the network reach a device on one of this PC's COM ports.</summary>
    public static FlowDiagram Server(BridgeEdition edition)
    {
        var diagram = new FlowDiagram();
        diagram.SetFlow(
        [
            new("Clients", "other PCs, monitoring"),
            new($"{edition.ProductName} service", "on this PC", Highlight: true),
            new(AtStart(edition.DeviceNoun), "on USB or RS-232"),
        ],
        [
            new($"TCP {edition.ServerPortBase + 3}, network", Network: true),
            new("COM3"),
        ]);
        return diagram;
    }

    private static string AtStart(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
