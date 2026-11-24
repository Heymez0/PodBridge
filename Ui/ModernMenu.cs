using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace PodBridge.Ui;

/// <summary>Context menus drawn like the Windows 11 ones: rounded corners, roomy rows, a rounded highlight,
/// thin separators and Fluent check marks and arrows, light or dark with the Windows app theme.</summary>
public static class ModernMenu
{
    private const string CheckGlyph = "";
    private const string ChevronGlyph = "";

    /// <summary>Styles a menu and every submenu in it. Call after the items are added.</summary>
    public static T Apply<T>(T menu) where T : ToolStripDropDownMenu
    {
        menu.Renderer = SharedRenderer;
        menu.Font = new Font("Segoe UI", 9f);
        menu.ShowImageMargin = false;
        menu.ShowCheckMargin = menu.Items.OfType<ToolStripMenuItem>().Any(i => i.CheckOnClick || i.Checked);
        menu.Padding = new Padding(0, 4, 0, 4);
        menu.DropShadowEnabled = true;
        menu.HandleCreated += (_, _) => Theme.RoundCorners(menu.Handle, small: false);
        if (menu.IsHandleCreated) Theme.RoundCorners(menu.Handle, small: false);
        foreach (ToolStripItem item in menu.Items)
        {
            if (item is not ToolStripMenuItem menuItem) continue;
            menuItem.Padding = new Padding(4, 6, 16, 6);
            if (menuItem.HasDropDownItems && menuItem.DropDown is ToolStripDropDownMenu sub) Apply(sub);
        }
        return menu;
    }

    private static readonly MenuRenderer SharedRenderer = new();

    private sealed class MenuRenderer : ToolStripRenderer
    {
        private static Palette P => Theme.Current;

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) => e.Graphics.Clear(P.Menu);

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            // Windows 11 draws its own thin border around rounded windows; Windows 10 gets this one.
            if (Environment.OSVersion.Version.Build >= 22000) return;
            using var pen = new Pen(P.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new RectangleF(4, 1, e.Item.Width - 8, e.Item.Height - 2);
            using var path = Theme.RoundRect(rect, 4);
            using var brush = new SolidBrush(P.MenuHover);
            g.FillPath(brush, path);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? P.Text : P.Disabled;
            e.TextFormat |= TextFormatFlags.VerticalCenter;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            var bounds = new RectangleF(r.X + (r.Width - 14) / 2f, e.Item.ContentRectangle.Top + (e.Item.ContentRectangle.Height - 14) / 2f, 14, 14);
            e.Graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            Glyphs.Draw(e.Graphics, CheckGlyph, e.Item.Enabled ? P.Text : P.Disabled, bounds);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            var r = e.ArrowRectangle;
            var bounds = new RectangleF(r.X + (r.Width - 10) / 2f - 4, r.Y + (r.Height - 10) / 2f, 10, 10);
            Glyphs.Draw(e.Graphics, ChevronGlyph, e.Item?.Enabled == false ? P.Disabled : P.Subtext, bounds);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using var pen = new Pen(P.Line);
            e.Graphics.DrawLine(pen, 1, y, e.Item.Width - 2, y);
        }
    }

    /// <summary>Testing: draws a menu into an image without showing it.</summary>
    public static void RenderTo(ContextMenuStrip menu, string path)
    {
        Apply(menu);
        var size = menu.GetPreferredSize(Size.Empty);
        menu.Size = size;
        menu.PerformLayout();
        using var bitmap = new Bitmap(size.Width, size.Height);
        menu.DrawToBitmap(bitmap, new Rectangle(Point.Empty, size));
        bitmap.Save(path);
    }
}
