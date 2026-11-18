using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace GBrowser
{
    /// <summary>Material-style menus: surface colour, rounded hover highlight, no gradients.</summary>
    internal sealed class MenuRenderer : ToolStripProfessionalRenderer
    {
        public MenuRenderer() : base(new Colors()) { RoundedEdges = false; }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var r = new Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = Theme.Round(r, 6)) using (var b = new SolidBrush(Theme.SurfaceContainerHighest)) e.Graphics.FillPath(b, p);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.OnSurface : Theme.Outline;
            base.OnRenderItemText(e);
        }

        sealed class Colors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Theme.SurfaceContainer;
            public override Color ImageMarginGradientBegin => Theme.SurfaceContainer;
            public override Color ImageMarginGradientMiddle => Theme.SurfaceContainer;
            public override Color ImageMarginGradientEnd => Theme.SurfaceContainer;
            public override Color MenuBorder => Theme.OutlineVariant;
            public override Color SeparatorDark => Theme.OutlineVariant;
            public override Color SeparatorLight => Theme.SurfaceContainer;
            public override Color MenuItemBorder => Color.Transparent;
            public override Color MenuItemSelected => Theme.SurfaceContainerHighest;
        }
    }
}
