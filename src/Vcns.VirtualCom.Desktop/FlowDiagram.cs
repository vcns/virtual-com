using System.Drawing.Drawing2D;

namespace Vcns.VirtualCom.Desktop;

/// <summary>
/// A one-line picture of how bytes travel, e.g. NovaLCT → COM9 → this PC → LAN → controller. Boxes and arrows are
/// drawn in system colours, so the diagram follows the Windows theme and high-contrast settings, and it scales with DPI.
/// </summary>
internal sealed class FlowDiagram : Control
{
    private IReadOnlyList<Node> _nodes = [];
    private IReadOnlyList<Link> _links = [];

    /// <param name="Highlight">The box for this program, drawn in the selection colour.</param>
    public sealed record Node(string Title, string Detail, bool Highlight = false);

    /// <param name="Network">Drawn dashed: the part that crosses the network.</param>
    public sealed record Link(string Label, bool Network = false);

    public FlowDiagram()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        TabStop = false;
        Margin = new Padding(0, 4, 0, 8);
        Dock = DockStyle.Top;
    }

    /// <summary>Boxes left to right, with one link fewer between them.</summary>
    public void SetFlow(IReadOnlyList<Node> nodes, IReadOnlyList<Link> links)
    {
        _nodes = nodes;
        _links = links;
        AccessibleRole = AccessibleRole.Diagram;
        AccessibleName = string.Join(" → ", nodes.Select((n, i) => i < links.Count ? $"{n.Title}, {links[i].Label}" : n.Title));
        UpdateHeight();
        Invalidate();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        UpdateHeight();
    }

    // Two lines of text in each box, plus padding. Measured from the font, which already follows the DPI.
    private void UpdateHeight() => Height = Font.Height * 2 + Font.Height * 3 / 2;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_nodes.Count == 0)
            return;

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // Room between the boxes for the longest link label; the boxes share what's left.
        var widest = _links.Select(l => TextRenderer.MeasureText(g, l.Label, Font).Width).DefaultIfEmpty(0).Max();
        var gap = _links.Count == 0 ? 0 : Math.Max(LogicalToDeviceUnits(90), widest + LogicalToDeviceUnits(20));
        var boxWidth = Math.Max(LogicalToDeviceUnits(110), (Width - 1 - gap * _links.Count) / _nodes.Count);
        var top = LogicalToDeviceUnits(2);
        var height = Height - top * 2 - 1;
        using var bold = new Font(Font, FontStyle.Bold);
        using var border = new Pen(SystemColors.ControlDark, Math.Max(1, LogicalToDeviceUnits(1)));
        using var highlightBorder = new Pen(SystemColors.Highlight, Math.Max(1, LogicalToDeviceUnits(1)));
        using var arrow = new Pen(SystemColors.ControlText, Math.Max(1.5f, LogicalToDeviceUnits(2)))
        {
            StartCap = LineCap.ArrowAnchor,
            EndCap = LineCap.ArrowAnchor,
        };
        using var dashed = (Pen)arrow.Clone();
        dashed.DashStyle = DashStyle.Dash;

        for (var i = 0; i < _nodes.Count; i++)
        {
            var node = _nodes[i];
            var box = new Rectangle(i * (boxWidth + gap), top, boxWidth, height);
            using (var path = Rounded(box, LogicalToDeviceUnits(6)))
            {
                using var fill = new SolidBrush(node.Highlight ? SystemColors.Highlight : SystemColors.Window);
                g.FillPath(fill, path);
                g.DrawPath(node.Highlight ? highlightBorder : border, path);
            }
            var text = node.Highlight ? SystemColors.HighlightText : SystemColors.ControlText;
            var muted = node.Highlight ? SystemColors.HighlightText : SystemColors.GrayText;
            var inner = Rectangle.Inflate(box, -LogicalToDeviceUnits(6), -LogicalToDeviceUnits(4));
            var titleRect = inner with { Height = inner.Height / 2 };
            var detailRect = inner with { Y = inner.Y + inner.Height / 2, Height = inner.Height - inner.Height / 2 };
            const TextFormatFlags centred = TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
            TextRenderer.DrawText(g, node.Title, bold, titleRect, text, centred | TextFormatFlags.Bottom);
            TextRenderer.DrawText(g, node.Detail, Font, detailRect, muted, centred | TextFormatFlags.Top);

            if (i < _links.Count)
            {
                var link = _links[i];
                var x1 = box.Right + LogicalToDeviceUnits(4);
                var x2 = box.Right + gap - LogicalToDeviceUnits(4);
                var y = top + height / 2 + LogicalToDeviceUnits(6);
                g.DrawLine(link.Network ? dashed : arrow, x1, y, x2, y);
                var labelRect = new Rectangle(box.Right, top, gap, y - top - LogicalToDeviceUnits(2));
                TextRenderer.DrawText(g, link.Label, Font, labelRect, SystemColors.ControlText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            }
        }
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
