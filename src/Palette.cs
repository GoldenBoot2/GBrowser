using System;
using System.Collections.Generic;
using System.Drawing;
using Microsoft.Win32;

namespace GBrowser
{
    /// <summary>Material 3 colour scheme from one seed colour (like Chrome's "Customize Chrome" colours).
    /// Tonal palettes are built in CIE LCh: tone = L* (the same tone scale Material uses), hue from the seed,
    /// chroma clamped into the sRGB gamut. Roles use Material 3's "tonal spot" tones for light and dark.</summary>
    internal static class Palette
    {
        public sealed class Scheme : Dictionary<string, Color> { }

        /// <summary>Preset seeds shown in Settings. "chrome" (the default) is Chrome's own scheme.</summary>
        public static readonly string[][] Presets =
        {
            new[] { "chrome", "Chrome (default)", "#0B57D0" },
            new[] { "#1A73E8", "Blue" }, new[] { "#00897B", "Teal" }, new[] { "#2E7D32", "Green" },
            new[] { "#6750A4", "Purple" }, new[] { "#C2185B", "Pink" }, new[] { "#C62828", "Red" },
            new[] { "#E65100", "Orange" }, new[] { "#5D6B7A", "Grey" },
        };

        // ---- Chrome's own scheme, the default: Chrome 158's colour tokens (chrome://theme/colors.css) and its Settings
        // page, read 19-11-2026 with tools/local/chrome_colors.mjs and chrome_dark_page.mjs (temporary profile)
        static Scheme Chrome(bool dark)
        {
            int[] v = dark
                // primary, on-primary, primary-container, on-primary-container, surface (Settings page), surface-container
                // (cards), -high, -highest, field, on-surface, on-surface-variant, outline, outline-variant, error
                ? new[] { 0xA8C7FA, 0x062E6F, 0x0842A0, 0xD3E3FD, 0x202124, 0x292A2D, 0x333438, 0x3C3C3C, 0x3C3C3C, 0xE3E3E3, 0xC4C7C5, 0x8E918F, 0x444746, 0xF2B8B5 }
                : new[] { 0x0B57D0, 0xFFFFFF, 0xD3E3FD, 0x041E49, 0xFFFFFF, 0xF3F6FC, 0xEDF2FA, 0xE3E3E3, 0xFFFFFF, 0x1F1F1F, 0x444746, 0x747775, 0xE1E3E1, 0xB3261E };
            var s = new Scheme();
            for (int i = 0; i < Roles.Length; i++) s[Roles[i]] = Color.FromArgb(255, Color.FromArgb(v[i]));
            // window: tab strip (header), toolbar / active tab / new tab page (base), address box (omnibox-container)
            s["frame"] = Color.FromArgb(dark ? 0x1F2020 : 0xD3E3FD); s["toolbar"] = Color.FromArgb(dark ? 0x3C3C3C : 0xFFFFFF);
            s["omnibox"] = Color.FromArgb(dark ? 0x282828 : 0xEDF2FA);
            foreach (var k in new[] { "frame", "toolbar", "omnibox" }) s[k] = Color.FromArgb(255, s[k]);
            return s;
        }

        /// <summary>Dark surfaces at Chrome's lightness (measured on Chrome 158 dark, 19-11-2026: Settings page L* 13,
        /// cards L* 17, dividers L* 27), tinted with the scheme's own hue.</summary>
        static void DarkSurfaces(Scheme s, Color tint)
        {
            Lch(tint, out double _, out double c, out double h);
            s["surface"] = Tone(13, c, h); s["surface-container"] = Tone(17, c, h);
            s["surface-container-high"] = Tone(22, c, h); s["surface-container-highest"] = Tone(27, c, h);
            s["field"] = Tone(27, c, h);
        }

        public static readonly string[] Roles =
        {
            "primary", "on-primary", "primary-container", "on-primary-container",
            "surface", "surface-container", "surface-container-high", "surface-container-highest", "field",
            "on-surface", "on-surface-variant", "outline", "outline-variant", "error",
        };

        /// <summary>Seed for a setting value: "chrome", "accent" (Windows accent colour) or "#RRGGBB".</summary>
        public static Color? SeedOf(string setting)
        {
            if (string.IsNullOrEmpty(setting) || setting == "chrome") return null;
            if (setting == "accent") return WindowsAccent();
            try { return ColorTranslator.FromHtml(setting); } catch { return null; }
        }

        public static Scheme For(string setting, bool dark)
        {
            var seed = SeedOf(setting);
            var s = seed != null ? FromSeed(seed.Value, dark) : Chrome(dark);
            if (s.ContainsKey("frame")) return s;
            // the window parts, like Chrome: light = darker tab strip, toolbar in the page colour; dark (measured on
            // Chrome 158, 19-11-2026): tab strip L* 12, toolbar / active tab / new tab page L* 25, address box L* 16
            if (!dark) { s["frame"] = s["surface-container-high"]; s["toolbar"] = s["surface"]; s["omnibox"] = s["surface-container-high"]; }
            else
            {
                Lch(s["surface-container"], out double _, out double c, out double h);
                s["frame"] = Tone(12, c, h); s["toolbar"] = Tone(25, c, h); s["omnibox"] = Tone(16, c, h);
            }
            return s;
        }

        /// <summary>Windows accent colour (Settings > Personalization > Colors): HKCU\...\DWM\AccentColor, ABGR.</summary>
        public static Color? WindowsAccent()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
                {
                    object v = k?.GetValue("AccentColor") ?? k?.GetValue("ColorizationColor");
                    if (v is int abgr)
                    {
                        uint u = unchecked((uint)abgr);
                        bool isAccent = k.GetValue("AccentColor") != null;
                        return isAccent
                            ? Color.FromArgb((int)(u & 0xFF), (int)((u >> 8) & 0xFF), (int)((u >> 16) & 0xFF))     // ABGR
                            : Color.FromArgb((int)((u >> 16) & 0xFF), (int)((u >> 8) & 0xFF), (int)(u & 0xFF));     // ARGB
                    }
                }
            }
            catch { }
            return null;
        }

        static Scheme FromSeed(Color seed, bool dark)
        {
            Lch(seed, out double _, out double c0, out double h);
            double cp = Math.Max(c0, 36);            // primary: at least a clear colour, like Material's tonal spot
            if (c0 < 6) cp = c0;                      // greys stay grey
            double cn = Math.Min(c0 / 6, 5);          // neutral surfaces: a hint of the hue
            double cnv = Math.Min(c0 / 3, 10);        // neutral variant: outlines, secondary text
            Func<double, Color> P = t => Tone(t, cp, h), N = t => Tone(t, cn, h), NV = t => Tone(t, cnv, h);
            var s = new Scheme();
            if (!dark)
            {
                s["primary"] = P(40); s["on-primary"] = P(100); s["primary-container"] = P(90); s["on-primary-container"] = P(10);
                s["surface"] = N(98); s["surface-container"] = N(94); s["surface-container-high"] = N(92); s["surface-container-highest"] = N(90);
                s["field"] = N(100); s["on-surface"] = N(10); s["on-surface-variant"] = NV(30); s["outline"] = NV(50); s["outline-variant"] = NV(80);
                s["error"] = Color.FromArgb(0xBA, 0x1A, 0x1A);
            }
            else
            {
                s["primary"] = P(80); s["on-primary"] = P(20); s["primary-container"] = P(30); s["on-primary-container"] = P(90);
                s["surface"] = N(13); s["surface-container"] = N(17); s["surface-container-high"] = N(22); s["surface-container-highest"] = N(27);
                s["field"] = N(27); s["on-surface"] = N(90); s["on-surface-variant"] = NV(80); s["outline"] = NV(60); s["outline-variant"] = NV(30);
                s["error"] = Color.FromArgb(0xFF, 0xB4, 0xAB);
            }
            return s;
        }

        public static string Hex(Color c) => "#" + c.R.ToString("x2") + c.G.ToString("x2") + c.B.ToString("x2");

        // ---------------------------------------------------------------- colour maths (sRGB <-> CIE Lab/LCh, D65)
        static double Lin(double v) { v /= 255; return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        static double Gam(double v) => v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
        static double F(double t) => t > 216.0 / 24389 ? Math.Pow(t, 1.0 / 3) : (24389.0 / 27 * t + 16) / 116;
        static double Finv(double t) => t * t * t > 216.0 / 24389 ? t * t * t : (116 * t - 16) / (24389.0 / 27);
        const double Xn = 0.95047, Yn = 1.0, Zn = 1.08883;

        static void Lch(Color c, out double L, out double C, out double H)
        {
            double r = Lin(c.R), g = Lin(c.G), b = Lin(c.B);
            double x = (0.4124 * r + 0.3576 * g + 0.1805 * b) / Xn, y = (0.2126 * r + 0.7152 * g + 0.0722 * b) / Yn, z = (0.0193 * r + 0.1192 * g + 0.9505 * b) / Zn;
            double fx = F(x), fy = F(y), fz = F(z);
            L = 116 * fy - 16;
            double A = 500 * (fx - fy), B = 200 * (fy - fz);
            C = Math.Sqrt(A * A + B * B);
            H = Math.Atan2(B, A);
        }

        static bool ToRgb(double L, double C, double H, out Color col)
        {
            double a = C * Math.Cos(H), b = C * Math.Sin(H);
            double fy = (L + 16) / 116, fx = fy + a / 500, fz = fy - b / 200;
            double x = Finv(fx) * Xn, y = (L > 8 ? fy * fy * fy : L / (24389.0 / 27)) * Yn, z = Finv(fz) * Zn;
            double r = 3.2406 * x - 1.5372 * y - 0.4986 * z, g = -0.9689 * x + 1.8758 * y + 0.0415 * z, bl = 0.0557 * x - 0.2040 * y + 1.0570 * z;
            bool ok = r >= -0.0005 && r <= 1.0005 && g >= -0.0005 && g <= 1.0005 && bl >= -0.0005 && bl <= 1.0005;
            int R = (int)Math.Round(Math.Max(0, Math.Min(1, Gam(Math.Max(0, r)))) * 255);
            int G = (int)Math.Round(Math.Max(0, Math.Min(1, Gam(Math.Max(0, g)))) * 255);
            int B2 = (int)Math.Round(Math.Max(0, Math.Min(1, Gam(Math.Max(0, bl)))) * 255);
            col = Color.FromArgb(R, G, B2);
            return ok;
        }

        /// <summary>Colour with lightness L* = tone, the given hue, and the highest chroma up to c that fits sRGB.</summary>
        static Color Tone(double tone, double c, double h)
        {
            if (tone >= 100) return Color.White;
            if (tone <= 0) return Color.Black;
            Color col;
            if (ToRgb(tone, c, h, out col)) return col;
            double lo = 0, hi = c;                       // binary search the largest chroma inside the gamut
            for (int i = 0; i < 18; i++)
            {
                double mid = (lo + hi) / 2;
                if (ToRgb(tone, mid, h, out _)) lo = mid; else hi = mid;
            }
            ToRgb(tone, lo, h, out col);
            return col;
        }
    }
}
