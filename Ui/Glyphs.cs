using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Microsoft.Win32;

namespace PodBridge.Ui;

/// <summary>Icons drawn for PodBridge, plus helpers based on the Windows icon font so they match system icons.</summary>
public static class Glyphs
{
    public const string Speaker = "";

    private const string PersonalizeKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    // Windows 11 ships Fluent, Windows 10 ships MDL2; both use the same code points.
    public static readonly string FontName =
        new InstalledFontCollection().Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    public static bool AppsUseLightTheme => Registry.GetValue(PersonalizeKey, "AppsUseLightTheme", 1) is int v && v != 0;

    public static bool TaskbarUsesLightTheme => Registry.GetValue(PersonalizeKey, "SystemUsesLightTheme", 0) is int v && v != 0;

    public static void Draw(Graphics g, string glyph, Color color, RectangleF bounds)
    {
        using var font = new Font(FontName, bounds.Height * 0.8f, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.DrawString(glyph, font, brush, bounds, format);
    }

    /// <summary>Tray icon: single-color HomePod with a colored status dot in the bottom right.</summary>
    public static Icon TrayIcon(Color? badge)
    {
        int size = SystemInformation.SmallIconSize.Width;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var foreground = TaskbarUsesLightTheme ? Color.FromArgb(26, 26, 26) : Color.White;
            DrawHomePod(g, foreground, new RectangleF(0, 0, size, size));
            if (badge is { } color)
            {
                float dot = size * 0.36f;
                var rect = new RectangleF(size - dot, size - dot, dot, dot);
                using var ring = new SolidBrush(TaskbarUsesLightTheme ? Color.FromArgb(243, 243, 243) : Color.FromArgb(32, 32, 32));
                g.FillEllipse(ring, RectangleF.Inflate(rect, size * 0.06f, size * 0.06f));
                using var fill = new SolidBrush(color);
                g.FillEllipse(fill, rect);
            }
        }
        IntPtr handle = bitmap.GetHicon();
        using var temporary = Icon.FromHandle(handle);
        var icon = (Icon)temporary.Clone();
        DestroyIcon(handle);
        return icon;
    }

    // HomePod mini from the side: a sphere cut at the top and bottom, at this fraction of the radius from the center.
    private const float CutRatio = 0.84f;
    private static readonly float CutAngle = (float)(Math.Asin(CutRatio) * 180 / Math.PI);

    /// <summary>Body: right and left arcs of the circle with flat top and bottom edges between them.</summary>
    private static GraphicsPath BodyPath(RectangleF bounds, out RectangleF circle, out float top)
    {
        float diameter = Math.Min(bounds.Width, bounds.Height / CutRatio);
        circle = new RectangleF(bounds.X + (bounds.Width - diameter) / 2, bounds.Y + (bounds.Height - diameter) / 2, diameter, diameter);
        top = circle.Y + diameter / 2 * (1 - CutRatio);
        var path = new GraphicsPath();
        path.AddArc(circle, -CutAngle, 2 * CutAngle);
        path.AddArc(circle, 180 - CutAngle, 2 * CutAngle);
        path.CloseFigure();
        return path;
    }

    /// <summary>Single-color HomePod: solid body with the top touch surface cut out.</summary>
    public static void DrawHomePod(Graphics g, Color color, RectangleF bounds)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var body = BodyPath(RectangleF.Inflate(bounds, -bounds.Width * 0.04f, -bounds.Height * 0.04f), out var circle, out float top);
        float topWidth = circle.Width * (float)Math.Sqrt(1 - CutRatio * CutRatio);
        // Keep the surface off the edge; otherwise the body looks like a hollow cup at small sizes.
        var surface = new RectangleF(circle.X + (circle.Width - topWidth * 0.8f) / 2, top + circle.Height * 0.09f,
                                     topWidth * 0.8f, circle.Height * 0.11f);
        body.FillMode = FillMode.Alternate;
        body.AddEllipse(surface);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, body);
    }

    /// <summary>App icon: white HomePod mini with the colorful Siri glow on a dark tile.</summary>
    public static Bitmap AppIcon(int size)
    {
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float inset = size * 0.04f;
        var tile = new RectangleF(inset, inset, size - 2 * inset, size - 2 * inset);
        using (var tilePath = RoundRect(tile, tile.Width * 0.22f))
        using (var background = new LinearGradientBrush(tile, Color.FromArgb(52, 62, 96), Color.FromArgb(16, 18, 34), 90f))
            g.FillPath(background, tilePath);

        var area = RectangleF.Inflate(tile, -tile.Width * 0.13f, -tile.Height * 0.13f);
        area.Offset(0, tile.Height * 0.03f);
        using var body = BodyPath(area, out var circle, out float top);
        using (var shade = new PathGradientBrush(body)
        {
            CenterPoint = new PointF(circle.X + circle.Width * 0.38f, circle.Y + circle.Height * 0.38f),
            CenterColor = Color.White,
            SurroundColors = [Color.FromArgb(178, 182, 192)],
        })
            g.FillPath(shade, body);

        // Mesh fabric texture; only adds noise at small sizes.
        if (size >= 48)
        {
            var clip = g.Clip;
            g.SetClip(body);
            float step = circle.Width / 16f;
            using var dot = new SolidBrush(Color.FromArgb(28, 0, 0, 0));
            for (float y = top + step; y < circle.Bottom; y += step)
                for (float x = circle.X + ((int)((y - top) / step) % 2 == 0 ? 0 : step / 2); x < circle.Right; x += step)
                    g.FillEllipse(dot, x, y, step * 0.42f, step * 0.42f);
            g.Clip = clip;
        }

        float topWidth = circle.Width * (float)Math.Sqrt(1 - CutRatio * CutRatio);
        var surface = new RectangleF(circle.X + (circle.Width - topWidth * 0.92f) / 2, top - circle.Height * 0.035f,
                                     topWidth * 0.92f, circle.Height * 0.14f);
        using (var glass = new SolidBrush(Color.FromArgb(34, 34, 40)))
            g.FillEllipse(glass, surface);
        var glowRect = RectangleF.Inflate(surface, -surface.Width * 0.14f, -surface.Height * 0.16f);
        using (var glowPath = new GraphicsPath())
        {
            glowPath.AddEllipse(glowRect);
            using var glow = new PathGradientBrush(glowPath)
            {
                CenterColor = Color.FromArgb(255, 120, 200),
                SurroundColors = [Color.FromArgb(0, 70, 120, 255)],
                FocusScales = new PointF(0.25f, 0.2f),
            };
            g.FillPath(glow, glowPath);
        }
        return bitmap;
    }

    private static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Writes a multi-size .ico with embedded PNGs; the build uses it as the app icon.</summary>
    public static void WriteIco(string path, params int[] sizes)
    {
        var images = sizes.Select(s =>
        {
            using var bitmap = AppIcon(s);
            using var ms = new MemoryStream();
            bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return (Size: s, Data: ms.ToArray());
        }).ToList();

        using var file = File.Create(path);
        using var writer = new BinaryWriter(file);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)images.Count);
        int offset = 6 + 16 * images.Count;
        foreach (var (size, data) in images)
        {
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(data.Length);
            writer.Write(offset);
            offset += data.Length;
        }
        foreach (var (_, data) in images) writer.Write(data);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
