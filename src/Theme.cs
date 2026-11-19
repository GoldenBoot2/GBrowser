using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GBrowser
{
    /// <summary>Material 3 colours (default: Chrome's own scheme, see Palette.cs), light and dark. Settings.ThemeMode:
    /// "system" follows Windows (Settings > Personalization > Colors > app mode), or "light" / "dark".</summary>
    internal static class Theme
    {
        public static bool Dark { get; private set; }

        // current scheme (Settings.ThemeColor: "chrome", "accent" or "#RRGGBB"; see Palette.cs)
        static Palette.Scheme scheme = Palette.For("chrome", false);
        static string schemeKey = "";
        static Color R(string role) => scheme[role];

        public static Color Primary => R("primary");
        public static Color OnPrimary => R("on-primary");
        public static Color PrimaryContainer => R("primary-container");
        public static Color OnPrimaryContainer => R("on-primary-container");
        public static Color Surface => R("surface");
        public static Color SurfaceContainer => R("surface-container");
        public static Color SurfaceContainerHigh => R("surface-container-high");
        public static Color SurfaceContainerHighest => R("surface-container-highest");
        // window parts (Palette.For): tab strip, toolbar + active tab + bookmarks bar, address box when not typing
        public static Color Frame => R("frame");
        public static Color ToolbarBack => R("toolbar");
        public static Color Omnibox => R("omnibox");
        /// <summary>Focused address box (white in light mode).</summary>
        public static Color Field => R("field");
        public static Color OnSurface => R("on-surface");
        public static Color OnSurfaceVariant => R("on-surface-variant");
        public static Color Outline => R("outline");
        public static Color OutlineVariant => R("outline-variant");
        // Material 3 error role (recording indicator): the baseline red, not tinted by the theme colour
        public static Color Error => Dark ? Color.FromArgb(0xF2, 0xB8, 0xB5) : Color.FromArgb(0xB3, 0x26, 0x1E);
        public static Color Hover => Color.FromArgb(0x14, OnSurface);
        public static Color Pressed => Color.FromArgb(0x24, OnSurface);
        public static Color Disabled => Color.FromArgb(0x61, OnSurface);

        /// <summary>Both schemes as CSS custom properties for GBrowser's own pages.</summary>
        public static Dictionary<string, object> CssVars()
        {
            string c = Store.Settings.ThemeColor;
            Func<bool, Dictionary<string, string>> vars = d =>
            {
                var o = new Dictionary<string, string>();
                foreach (var kv in Palette.For(c, d)) o["--" + kv.Key] = Palette.Hex(kv.Value);
                return o;
            };
            return new Dictionary<string, object> { { "light", vars(false) }, { "dark", vars(true) } };
        }

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
            string color = Store.Settings.ThemeColor ?? "chrome";
            var seed = Palette.SeedOf(color);
            string key = dark + "|" + color + "|" + (seed.HasValue ? seed.Value.ToArgb().ToString() : "");
            bool changed = key != schemeKey;
            Dark = dark;
            if (changed) { scheme = Palette.For(color, dark); schemeKey = key; }
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
