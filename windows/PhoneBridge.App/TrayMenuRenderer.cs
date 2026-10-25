using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace PhoneBridge.App;

/// <summary>Menu w zasobniku w kolorach aplikacji (piasek, koral, atrament).</summary>
internal sealed class TrayMenuRenderer : Forms.ToolStripProfessionalRenderer
{
    private static readonly Drawing.Color Ink = Drawing.Color.FromArgb(0x1C, 0x1B, 0x29);
    private static readonly Drawing.Color Muted = Drawing.Color.FromArgb(0x6E, 0x6A, 0x7C);
    private static readonly Drawing.Color CoralSoft = Drawing.Color.FromArgb(0xFF, 0xE6, 0xDF);
    private static readonly Drawing.Color OnCoralSoft = Drawing.Color.FromArgb(0x8A, 0x2A, 0x16);

    public TrayMenuRenderer() : base(new Palette())
    {
        RoundedEdges = true;
    }

    public static void Apply(Forms.ContextMenuStrip menu)
    {
        menu.Renderer = new TrayMenuRenderer();
        menu.Font = new Drawing.Font("Segoe UI", 9.75f);
        menu.Padding = new Forms.Padding(2, 6, 2, 6);
        menu.ShowImageMargin = true;
    }

    protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = !e.Item.Enabled ? Muted : e.Item.Selected ? OnCoralSoft : Ink;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled)
            return;
        var bounds = new Drawing.Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
        using var brush = new Drawing.SolidBrush(CoralSoft);
        e.Graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var path = RoundedRectangle(bounds, 6);
        e.Graphics.FillPath(brush, path);
    }

    private static Drawing.Drawing2D.GraphicsPath RoundedRectangle(Drawing.Rectangle r, int radius)
    {
        var d = radius * 2;
        var path = new Drawing.Drawing2D.GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private sealed class Palette : Forms.ProfessionalColorTable
    {
        private static readonly Drawing.Color Background = Drawing.Color.FromArgb(0xFF, 0xFD, 0xFB);
        private static readonly Drawing.Color Line = Drawing.Color.FromArgb(0xE9, 0xE2, 0xDA);

        public override Drawing.Color ToolStripDropDownBackground => Background;
        public override Drawing.Color ImageMarginGradientBegin => Background;
        public override Drawing.Color ImageMarginGradientMiddle => Background;
        public override Drawing.Color ImageMarginGradientEnd => Background;
        public override Drawing.Color MenuBorder => Line;
        public override Drawing.Color MenuItemBorder => CoralSoft;
        public override Drawing.Color MenuItemSelected => CoralSoft;
        public override Drawing.Color SeparatorDark => Line;
        public override Drawing.Color SeparatorLight => Background;
        public override Drawing.Color CheckBackground => CoralSoft;
        public override Drawing.Color CheckSelectedBackground => CoralSoft;
        public override Drawing.Color CheckPressedBackground => CoralSoft;
    }
}
