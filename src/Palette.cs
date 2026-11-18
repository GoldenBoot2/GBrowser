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

        /// <summary>Preset seeds shown in Settings. "gold" keeps GBrowser's original hand-tuned scheme.</summary>
        public static readonly string[][] Presets =
        {
            new[] { "gold", "Gold", "#785A00" },
            new[] { "#1A73E8", "Blue" }, new[] { "#00897B", "Teal" }, new[] { "#2E7D32", "Green" },
            new[] { "#6750A4", "Purple" }, new[] { "#C2185B", "Pink" }, new[] { "#C62828", "Red" },
            new[] { "#E65100", "Orange" }, new[] { "#5D6B7A", "Grey" },
        };

        // ---- the original gold scheme (exact values, unchanged)
        static Scheme Gold(bool dark)
        {
            int[] v = dark
                ? new[] { 0xF0BF48, 0x402D00, 0x5B4300, 0xFFDFA0, 0x17130B, 0x241F17, 0x2F2921, 0x3A342B, 0x3E382F, 0xECE1D4, 0xD3C5B0, 0x9C8F7C, 0x4F4637, 0xFFB4AB }
                : new[] { 0x785A00, 0xFFFFFF, 0xFFDFA0, 0x261A00, 0xFFF8F2, 0xF4EDE1, 0xEEE7DB, 0xE8E1D5, 0xFFFFFF, 0x1F1B13, 0x4D4639, 0x7F7667, 0xD0C5B4, 0xBA1A1A };
            var s = new Scheme();
            for (int i = 0; i < Roles.Length; i++) s[Roles[i]] = Color.FromArgb(255, Color.FromArgb(v[i]));
            return s;
        }

        public static readonly string[] Roles =
        {
            "primary", "on-primary", "primary-container", "on-primary-container",
            "surface", "surface-container", "surface-container-high", "surface-container-highest", "field",
            "on-surface", "on-surface-variant", "outline", "outline-variant", "error",
        };

        /// <summary>Seed for a setting value: "gold", "accent" (Windows accent colour) or "#RRGGBB".</summary>
        public static Color? SeedOf(string setting)
        {
            if (string.IsNullOrEmpty(setting) || setting == "gold") return null;
            if (setting == "accent") return WindowsAccent();
            try { return ColorTranslator.FromHtml(setting); } catch { return null; }
        }

        public static Scheme For(string setting, bool dark)
        {
            var seed = SeedOf(setting);
            return seed == null ? Gold(dark) : FromSeed(seed.Value, dark);
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
                s["surface"] = N(6); s["surface-container"] = N(12); s["surface-container-high"] = N(17); s["surface-container-highest"] = N(22);
                s["field"] = N(24); s["on-surface"] = N(90); s["on-surface-variant"] = NV(80); s["outline"] = NV(60); s["outline-variant"] = NV(30);
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
