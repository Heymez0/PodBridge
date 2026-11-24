using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace PodBridge.Ui;

/// <summary>Windows 11 colors for menus and dialogs, light or dark following the Windows app theme
/// (Settings &gt; Personalization &gt; Colors). Read when something is drawn, so a theme switch shows up at once.</summary>
public sealed record Palette(
    bool Light,
    Color Background,
    Color Surface,
    Color SurfaceHover,
    Color SurfacePressed,
    Color Menu,
    Color MenuHover,
    Color Text,
    Color Subtext,
    Color Disabled,
    Color Border,
    Color Line,
    Color Accent,
    Color AccentHover,
    Color AccentPressed,
    Color AccentText,
    Color Warn);

public static class Theme
{
    /// <summary>Forces light (true) or dark (false), for rendering previews.</summary>
    public static bool? Override { get; set; }

    public static Palette Current => Override ?? Glyphs.AppsUseLightTheme ? Light : Dark;

    private static readonly Palette Light = new(
        Light: true,
        Background: Color.FromArgb(243, 243, 243),
        Surface: Color.FromArgb(251, 251, 251),
        SurfaceHover: Color.FromArgb(246, 246, 246),
        SurfacePressed: Color.FromArgb(240, 240, 240),
        Menu: Color.FromArgb(249, 249, 249),
        MenuHover: Color.FromArgb(234, 234, 234),
        Text: Color.FromArgb(26, 26, 26),
        Subtext: Color.FromArgb(96, 96, 96),
        Disabled: Color.FromArgb(160, 160, 160),
        Border: Color.FromArgb(220, 220, 220),
        Line: Color.FromArgb(229, 229, 229),
        Accent: Color.FromArgb(0, 95, 184),
        AccentHover: Color.FromArgb(25, 117, 197),
        AccentPressed: Color.FromArgb(49, 131, 202),
        AccentText: Color.White,
        Warn: Color.FromArgb(157, 93, 0));

    private static readonly Palette Dark = new(
        Light: false,
        Background: Color.FromArgb(32, 32, 32),
        Surface: Color.FromArgb(45, 45, 45),
        SurfaceHover: Color.FromArgb(50, 50, 50),
        SurfacePressed: Color.FromArgb(39, 39, 39),
        Menu: Color.FromArgb(44, 44, 44),
        MenuHover: Color.FromArgb(61, 61, 61),
        Text: Color.White,
        Subtext: Color.FromArgb(200, 200, 200),
        Disabled: Color.FromArgb(120, 120, 120),
        Border: Color.FromArgb(64, 64, 64),
        Line: Color.FromArgb(58, 58, 58),
        Accent: Color.FromArgb(96, 205, 255),
        AccentHover: Color.FromArgb(90, 186, 230),
        AccentPressed: Color.FromArgb(84, 167, 205),
        AccentText: Color.FromArgb(0, 30, 50),
        Warn: Color.FromArgb(255, 200, 80));

    public static GraphicsPath RoundRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        if (d <= 0)
        {
            path.AddRectangle(rect);
            return path;
        }
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Rounded corners on Windows 11 (no effect on Windows 10). Small for menus, normal for windows.</summary>
    public static void RoundCorners(IntPtr handle, bool small)
    {
        int preference = small ? 3 : 2;   // DWMWCP_ROUNDSMALL / DWMWCP_ROUND
        DwmSetWindowAttribute(handle, 33, ref preference, sizeof(int));
    }

    /// <summary>Title bar in the window's own colors instead of the accent or default gray.</summary>
    public static void StyleTitleBar(IntPtr handle, Palette palette)
    {
        int dark = palette.Light ? 0 : 1;
        DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));   // DWMWA_USE_IMMERSIVE_DARK_MODE
        int caption = ColorRef(palette.Background);
        DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));   // DWMWA_CAPTION_COLOR
        int text = ColorRef(palette.Text);
        DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));   // DWMWA_TEXT_COLOR
    }

    private static int ColorRef(Color c) => c.R | c.G << 8 | c.B << 16;
}
