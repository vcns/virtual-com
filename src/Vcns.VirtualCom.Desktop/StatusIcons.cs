using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Vcns.VirtualCom.Desktop;

internal enum TrayStatus
{
    Idle,
    Starting,
    Connected,
    Error,
}

/// <summary>Draws the serial-connector glyph and status badges at whatever pixel size the display needs.</summary>
internal static class StatusIcons
{
    private static readonly Color Body = Color.FromArgb(0x1F, 0x3A, 0x5C);
    private static readonly Color Connector = Color.FromArgb(0xF4, 0xF6, 0xF8);

    public static Color ColorOf(TrayStatus status) => status switch
    {
        TrayStatus.Starting => Color.FromArgb(0xE8, 0xA3, 0x17),
        TrayStatus.Connected => Color.FromArgb(0x2E, 0xA0, 0x43),
        TrayStatus.Error => Color.FromArgb(0xD1, 0x3B, 0x3B),
        _ => Color.FromArgb(0x8C, 0x93, 0x9D),
    };

    public static Icon CreateIcon(TrayStatus? status, int size)
    {
        using var bitmap = Draw(status, size);
        return new Icon(new MemoryStream(ToIco([bitmap])), size, size);
    }

    /// <summary>A plain status dot for menu items.</summary>
    public static Bitmap CreateDot(TrayStatus status, int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var inset = size * 0.22f;
        using var brush = new SolidBrush(ColorOf(status));
        g.FillEllipse(brush, inset, inset, size - 2 * inset, size - 2 * inset);
        return bitmap;
    }

    public static Bitmap Draw(TrayStatus? status, int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        float s = size;

        using (var body = RoundedRect(new RectangleF(0, 0, s, s), s * 0.2f))
        using (var brush = new SolidBrush(Body))
            g.FillPath(brush, body);

        // D-sub connector shell: a trapezoid, wider at the top.
        using (var shell = RoundedTrapezoid(s * 0.14f, s * 0.86f, s * 0.26f, s * 0.74f, s * 0.31f, s * 0.67f, s * 0.07f))
        using (var brush = new SolidBrush(Connector))
            g.FillPath(brush, shell);

        using (var pin = new SolidBrush(Body))
        {
            var r = Math.Max(s * 0.055f, 0.9f);
            if (size >= 24)
            {
                foreach (var x in new[] { 0.33f, 0.5f, 0.67f })
                    g.FillEllipse(pin, s * x - r, s * 0.43f - r, 2 * r, 2 * r);
                foreach (var x in new[] { 0.415f, 0.585f })
                    g.FillEllipse(pin, s * x - r, s * 0.56f - r, 2 * r, 2 * r);
            }
            else
            {
                foreach (var x in new[] { 0.34f, 0.5f, 0.66f })
                    g.FillEllipse(pin, s * x - r, s * 0.49f - r, 2 * r, 2 * r);
            }
        }

        if (status is { } st)
        {
            var radius = s * 0.24f;
            var cx = s - radius - s * 0.02f;
            var cy = s - radius - s * 0.02f;
            var ring = Math.Max(s * 0.06f, 1f);
            using (var halo = new SolidBrush(Color.White))
                g.FillEllipse(halo, cx - radius, cy - radius, 2 * radius, 2 * radius);
            using var dot = new SolidBrush(ColorOf(st));
            g.FillEllipse(dot, cx - radius + ring, cy - radius + ring, 2 * (radius - ring), 2 * (radius - ring));
        }
        return bitmap;
    }

    /// <summary>Packs bitmaps into an .ico file using PNG-compressed frames (supported since Windows Vista).</summary>
    public static byte[] ToIco(IReadOnlyList<Bitmap> frames)
    {
        var images = frames.Select(f =>
        {
            using var png = new MemoryStream();
            f.Save(png, ImageFormat.Png);
            return png.ToArray();
        }).ToList();

        using var ico = new MemoryStream();
        using var writer = new BinaryWriter(ico);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)frames.Count);
        var offset = 6 + 16 * frames.Count;
        for (var i = 0; i < frames.Count; i++)
        {
            writer.Write((byte)(frames[i].Width >= 256 ? 0 : frames[i].Width));
            writer.Write((byte)(frames[i].Height >= 256 ? 0 : frames[i].Height));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(images[i].Length);
            writer.Write(offset);
            offset += images[i].Length;
        }
        foreach (var image in images)
            writer.Write(image);
        writer.Flush();
        return ico.ToArray();
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
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

    private static GraphicsPath RoundedTrapezoid(float topLeft, float topRight, float bottomLeft, float bottomRight, float top, float bottom, float radius)
    {
        var path = new GraphicsPath();
        var corners = new[]
        {
            new PointF(topLeft, top), new PointF(topRight, top),
            new PointF(bottomRight, bottom), new PointF(bottomLeft, bottom),
        };
        // Approximate rounded corners by cutting each corner and joining the cuts with a Bézier through the corner.
        for (var i = 0; i < corners.Length; i++)
        {
            var prev = corners[(i + corners.Length - 1) % corners.Length];
            var curr = corners[i];
            var next = corners[(i + 1) % corners.Length];
            var a = Towards(curr, prev, radius);
            var b = Towards(curr, next, radius);
            if (i == 0)
                path.StartFigure();
            path.AddBezier(a, curr, curr, b);
            var after = Towards(next, curr, radius);
            path.AddLine(b, after);
        }
        path.CloseFigure();
        return path;
    }

    private static PointF Towards(PointF from, PointF to, float distance)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        return new PointF(from.X + dx / length * distance, from.Y + dy / length * distance);
    }
}
