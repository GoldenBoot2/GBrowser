using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GBrowser
{
    /// <summary>Material 3 colours from a gold seed (GoldenBoot brand), light and dark. Settings.ThemeMode:
    /// "system" follows Windows (Settings > Personalization > Colors > app mode), or "light" / "dark".</summary>
    internal static class Theme
    {
        public static bool Dark { get; private set; }

        static Color L(int rgb) => Color.FromArgb(255, Color.FromArgb(rgb));
        static Color Pick(int light, int dark) => Dark ? L(dark) : L(light);

        public static Color Primary => Pick(0x785A00, 0xF0BF48);
        public static Color OnPrimary => Pick(0xFFFFFF, 0x402D00);
        public static Color PrimaryContainer => Pick(0xFFDFA0, 0x5B4300);
        public static Color OnPrimaryContainer => Pick(0x261A00, 0xFFDFA0);
        public static Color Surface => Pick(0xFFF8F2, 0x17130B);
        public static Color SurfaceContainer => Pick(0xF4EDE1, 0x241F17);
        public static Color SurfaceContainerHigh => Pick(0xEEE7DB, 0x2F2921);
        public static Color SurfaceContainerHighest => Pick(0xE8E1D5, 0x3A342B);
        /// <summary>Focused address box (white in light mode).</summary>
        public static Color Field => Pick(0xFFFFFF, 0x3E382F);
        public static Color OnSurface => Pick(0x1F1B13, 0xECE1D4);
        public static Color OnSurfaceVariant => Pick(0x4D4639, 0xD3C5B0);
        public static Color Outline => Pick(0x7F7667, 0x9C8F7C);
        public static Color OutlineVariant => Pick(0xD0C5B4, 0x4F4637);
        public static Color Hover => Dark ? Color.FromArgb(0x14, 0xEC, 0xE1, 0xD4) : Color.FromArgb(0x14, 0x1F, 0x1B, 0x13);
        public static Color Pressed => Dark ? Color.FromArgb(0x24, 0xEC, 0xE1, 0xD4) : Color.FromArgb(0x24, 0x1F, 0x1B, 0x13);
        public static Color Disabled => Dark ? Color.FromArgb(0x61, 0xEC, 0xE1, 0xD4) : Color.FromArgb(0x61, 0x1F, 0x1B, 0x13);

        /// <summary>Windows app mode: HKCU ...\Themes\Personalize\AppsUseLightTheme (0 = dark).</summary>
        public static bool SystemDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch { return false; }
        }

        /// <summary>Recomputes Dark from the setting; true when it changed.</summary>
        public static bool Update()
        {
            string mode = Store.Settings.ThemeMode ?? "system";
            bool dark = mode == "dark" || (mode != "light" && SystemDark());
            bool changed = dark != Dark;
            Dark = dark;
            return changed;
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>Dark or light window title bar (DWMWA_USE_IMMERSIVE_DARK_MODE = 20, Windows 10 2004+ / 11).</summary>
        public static void TitleBar(IntPtr hwnd)
        {
            int v = Dark ? 1 : 0;
            try { DwmSetWindowAttribute(hwnd, 20, ref v, sizeof(int)); } catch { }
        }

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
