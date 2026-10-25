using System.Diagnostics;
using System.Reflection;
using Vcns.VirtualCom.Bridge;
using Vcns.VirtualCom.Bridge.Diagnostics;

namespace Vcns.VirtualCom.Desktop;

/// <summary>The Settings window's About tab: what the program is for, its two modes, other uses, and links.</summary>
internal sealed class AboutPanel : UserControl
{
    private const int TextWidth = 980;

    private static readonly string[] OtherUses =
    [
        "UPS monitoring: share the UPS's serial port from the PC it's plugged into, and run the UPS software (PowerChute, NUT, "
            + "apcupsd and the like) on a server elsewhere through a virtual COM port.",
        "Serial device servers (Moxa, Lantronix and others) in TCP server mode: give Windows software a COM port that leads to them.",
        "PLCs, CNC machines and DNC software, and other industrial controllers with a serial programming port.",
        "Laboratory instruments, weighing scales, barcode scanners, and GPS or NMEA receivers.",
        "Building management, access control and alarm panels configured over a serial port.",
        "Modems, satellite terminals and network equipment managed through a serial console.",
    ];

    public AboutPanel(BridgeEdition edition)
    {
        Dock = DockStyle.Fill;
        AutoScroll = true;
        Padding = new Padding(12, 8, 12, 8);

        var flow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
        };

        var version = (Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly()).GetName().Version;
        flow.Controls.Add(new Label
        {
            Text = edition.ProductName,
            Font = new Font(Font.FontFamily, Font.Size * 1.6f, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 2),
        });
        flow.Controls.Add(Paragraph($"Version {version?.ToString(3)} · Copyright © 2026 VCNS Tech Ltd · Microsoft Public License (MS-PL), with no warranty", muted: true));

        if (edition.AboutText is { } about)
            flow.Controls.Add(Paragraph(about));

        flow.Controls.Add(Heading("What it does"));
        flow.Controls.Add(Paragraph(
            "Many devices still speak a serial protocol while sitting on a network, and much of the software that manages them still "
            + "expects a COM port. The bridge joins the two, passing bytes unchanged in both directions, one session at a time."));
        flow.Controls.Add(Paragraph("Client mode (the Client profiles tab) gives software on this PC a COM port that leads to a device on the network:", bold: true));
        flow.Controls.Add(Sized(Flows.Client(edition)));
        flow.Controls.Add(Paragraph("Server mode (the Server ports tab) shares a COM port on this PC with clients on the network:", bold: true));
        flow.Controls.Add(Sized(Flows.Server(edition)));
        flow.Controls.Add(Paragraph(
            "Both together let software on one PC use a serial device plugged into another. The connection isn't encrypted, so use a "
            + "trusted network or a VPN.", muted: true));

        flow.Controls.Add(Heading("Other uses"));
        flow.Controls.Add(Paragraph(string.Join("\n", OtherUses.Select(u => "•  " + u))));
        flow.Controls.Add(Paragraph(
            "It works with protocols that tolerate network delay. Ones that depend on precise timing between bytes, such as some "
            + "Modbus RTU set-ups, may not work.", muted: true));

        flow.Controls.Add(Heading("Links"));
        foreach (var link in edition.Links)
        {
            var label = new LinkLabel { Text = link.Text, AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
            var url = link.Url;
            label.LinkClicked += (_, _) => Open(url);
            flow.Controls.Add(label);
        }

        Controls.Add(flow);
    }

    private FlowDiagram Sized(FlowDiagram diagram)
    {
        diagram.Dock = DockStyle.None;
        diagram.Width = TextWidth;
        return diagram;
    }

    private Label Heading(string text) => new()
    {
        Text = text,
        Font = new Font(Font, FontStyle.Bold),
        AutoSize = true,
        Margin = new Padding(0, 14, 0, 4),
    };

    private Label Paragraph(string text, bool muted = false, bool bold = false) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(TextWidth, 0),
        Margin = new Padding(0, 2, 0, 4),
        ForeColor = muted ? SystemColors.GrayText : SystemColors.ControlText,
        Font = bold ? new Font(Font, FontStyle.Bold) : Font,
    };

    private static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Warn($"Could not open {url}: {ex.Message}");
        }
    }
}
