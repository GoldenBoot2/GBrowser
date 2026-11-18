using System.Drawing;
using System.Drawing.Drawing2D;

namespace GBrowser
{
    /// <summary>Material 3 light colours from a gold seed (GoldenBoot brand).</summary>
    internal static class Theme
    {
        public static readonly Color Primary = Color.FromArgb(0x78, 0x5A, 0x00);
        public static readonly Color OnPrimary = Color.White;
        public static readonly Color PrimaryContainer = Color.FromArgb(0xFF, 0xDF, 0xA0);
        public static readonly Color OnPrimaryContainer = Color.FromArgb(0x26, 0x1A, 0x00);
        public static readonly Color Surface = Color.FromArgb(0xFF, 0xF8, 0xF2);
        public static readonly Color SurfaceContainer = Color.FromArgb(0xF4, 0xED, 0xE1);
        public static readonly Color SurfaceContainerHigh = Color.FromArgb(0xEE, 0xE7, 0xDB);
        public static readonly Color SurfaceContainerHighest = Color.FromArgb(0xE8, 0xE1, 0xD5);
        public static readonly Color OnSurface = Color.FromArgb(0x1F, 0x1B, 0x13);
        public static readonly Color OnSurfaceVariant = Color.FromArgb(0x4D, 0x46, 0x39);
        public static readonly Color Outline = Color.FromArgb(0x7F, 0x76, 0x67);
        public static readonly Color OutlineVariant = Color.FromArgb(0xD0, 0xC5, 0xB4);
        public static readonly Color Hover = Color.FromArgb(0x14, 0x1F, 0x1B, 0x13);
        public static readonly Color Pressed = Color.FromArgb(0x24, 0x1F, 0x1B, 0x13);
        public static readonly Color Disabled = Color.FromArgb(0x61, 0x1F, 0x1B, 0x13);

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = radius * 2;
            if (d > r.Height) d = r.Height;
            if (d > r.Width) d = r.Width;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>Rounded only at the top (active tab shape).</summary>
        public static GraphicsPath TopRound(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = radius * 2;
            p.AddLine(r.X - radius, r.Bottom, r.X - radius, r.Bottom);
            p.AddArc(r.X - d, r.Bottom - d, d, d, 90, -90);
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right, r.Bottom - d, d, d, 180, -90);
            p.CloseFigure();
            return p;
        }

        public static Font UiFont(float pt, FontStyle style = FontStyle.Regular) => new Font("Segoe UI", pt, style, GraphicsUnit.Point);
    }
}
