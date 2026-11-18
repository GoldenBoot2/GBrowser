using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace GBrowser
{
    /// <summary>Draws Material Symbols Rounded path data (viewBox 0 -960 960 960) with GDI+ at any size.</summary>
    internal static class Icons
    {
        static readonly Dictionary<string, GraphicsPath> Cache = new Dictionary<string, GraphicsPath>();

        public static GraphicsPath Path(string d)
        {
            if (Cache.TryGetValue(d, out var p)) return p;
            p = Parse(d);
            Cache[d] = p;
            return p;
        }

        /// <summary>Fills icon d into the square box (x, y, size).</summary>
        public static void Draw(Graphics g, string d, float x, float y, float size, Color color)
        {
            var state = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TranslateTransform(x, y);
            g.ScaleTransform(size / 960f, size / 960f);
            g.TranslateTransform(0, 960);
            using (var b = new SolidBrush(color)) g.FillPath(b, Path(d));
            g.Restore(state);
        }

        public static Bitmap Bitmap(string d, int size, Color color, Color? background = null)
        {
            var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                if (background.HasValue)
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var bb = new SolidBrush(background.Value)) g.FillEllipse(bb, 0, 0, size - 1, size - 1);
                    float inner = size * 0.6f;
                    Draw(g, d, (size - inner) / 2, (size - inner) / 2, inner, color);
                }
                else Draw(g, d, 0, 0, size, color);
            }
            return bmp;
        }

        static GraphicsPath Parse(string d)
        {
            var path = new GraphicsPath(FillMode.Winding);
            var t = new Tokens(d);
            float x = 0, y = 0, sx = 0, sy = 0;
            float lcx = 0, lcy = 0; char lastCmd = ' ';
            char cmd = ' ';
            bool open = false;
            while (!t.End)
            {
                if (t.IsCommand) cmd = t.Command();
                else if (cmd == ' ') break;
                bool rel = char.IsLower(cmd);
                char c = char.ToUpperInvariant(cmd);
                switch (c)
                {
                    case 'M':
                    {
                        float nx = t.Num(), ny = t.Num();
                        if (rel) { nx += x; ny += y; }
                        if (open) path.CloseFigure();
                        path.StartFigure();
                        x = sx = nx; y = sy = ny; open = true;
                        cmd = rel ? 'l' : 'L';   // further pairs are line-tos
                        lastCmd = 'M';
                        continue;
                    }
                    case 'L':
                    {
                        float nx = t.Num(), ny = t.Num();
                        if (rel) { nx += x; ny += y; }
                        path.AddLine(x, y, nx, ny); x = nx; y = ny; break;
                    }
                    case 'H':
                    {
                        float nx = t.Num(); if (rel) nx += x;
                        path.AddLine(x, y, nx, y); x = nx; break;
                    }
                    case 'V':
                    {
                        float ny = t.Num(); if (rel) ny += y;
                        path.AddLine(x, y, x, ny); y = ny; break;
                    }
                    case 'C':
                    {
                        float x1 = t.Num(), y1 = t.Num(), x2 = t.Num(), y2 = t.Num(), nx = t.Num(), ny = t.Num();
                        if (rel) { x1 += x; y1 += y; x2 += x; y2 += y; nx += x; ny += y; }
                        path.AddBezier(x, y, x1, y1, x2, y2, nx, ny);
                        lcx = x2; lcy = y2; x = nx; y = ny; lastCmd = 'C'; continue;
                    }
                    case 'S':
                    {
                        float x2 = t.Num(), y2 = t.Num(), nx = t.Num(), ny = t.Num();
                        if (rel) { x2 += x; y2 += y; nx += x; ny += y; }
                        float x1 = lastCmd == 'C' ? 2 * x - lcx : x, y1 = lastCmd == 'C' ? 2 * y - lcy : y;
                        path.AddBezier(x, y, x1, y1, x2, y2, nx, ny);
                        lcx = x2; lcy = y2; x = nx; y = ny; lastCmd = 'C'; continue;
                    }
                    case 'Q':
                    {
                        float qx = t.Num(), qy = t.Num(), nx = t.Num(), ny = t.Num();
                        if (rel) { qx += x; qy += y; nx += x; ny += y; }
                        Quad(path, x, y, qx, qy, nx, ny);
                        lcx = qx; lcy = qy; x = nx; y = ny; lastCmd = 'Q'; continue;
                    }
                    case 'T':
                    {
                        float nx = t.Num(), ny = t.Num();
                        if (rel) { nx += x; ny += y; }
                        float qx = lastCmd == 'Q' ? 2 * x - lcx : x, qy = lastCmd == 'Q' ? 2 * y - lcy : y;
                        Quad(path, x, y, qx, qy, nx, ny);
                        lcx = qx; lcy = qy; x = nx; y = ny; lastCmd = 'Q'; continue;
                    }
                    case 'A':
                    {
                        float rx = t.Num(), ry = t.Num(), rot = t.Num();
                        bool large = t.Flag(), sweep = t.Flag();
                        float nx = t.Num(), ny = t.Num();
                        if (rel) { nx += x; ny += y; }
                        Arc(path, x, y, rx, ry, rot, large, sweep, nx, ny);
                        x = nx; y = ny; break;
                    }
                    case 'Z':
                        path.CloseFigure();
                        x = sx; y = sy; open = false; cmd = ' ';
                        lastCmd = 'Z';
                        if (!t.End && !t.IsCommand) return path; // malformed
                        continue;
                    default:
                        return path;
                }
                lastCmd = c;
            }
            if (open) path.CloseFigure();
            return path;
        }

        static void Quad(GraphicsPath p, float x0, float y0, float qx, float qy, float x1, float y1)
        {
            p.AddBezier(x0, y0, x0 + 2f / 3 * (qx - x0), y0 + 2f / 3 * (qy - y0),
                x1 + 2f / 3 * (qx - x1), y1 + 2f / 3 * (qy - y1), x1, y1);
        }

        // SVG arc (endpoint form) -> cubic beziers (SVG spec F.6.5).
        static void Arc(GraphicsPath p, float x1, float y1, float rx, float ry, float angle, bool large, bool sweep, float x2, float y2)
        {
            if (rx == 0 || ry == 0) { p.AddLine(x1, y1, x2, y2); return; }
            double phi = angle * Math.PI / 180, cos = Math.Cos(phi), sin = Math.Sin(phi);
            double dx = (x1 - x2) / 2.0, dy = (y1 - y2) / 2.0;
            double x1p = cos * dx + sin * dy, y1p = -sin * dx + cos * dy;
            double arx = Math.Abs(rx), ary = Math.Abs(ry);
            double lambda = x1p * x1p / (arx * arx) + y1p * y1p / (ary * ary);
            if (lambda > 1) { arx *= Math.Sqrt(lambda); ary *= Math.Sqrt(lambda); }
            double num = arx * arx * ary * ary - arx * arx * y1p * y1p - ary * ary * x1p * x1p;
            double den = arx * arx * y1p * y1p + ary * ary * x1p * x1p;
            double coef = Math.Sqrt(Math.Max(0, num / den)) * (large == sweep ? -1 : 1);
            double cxp = coef * arx * y1p / ary, cyp = -coef * ary * x1p / arx;
            double cx = cos * cxp - sin * cyp + (x1 + x2) / 2.0, cy = sin * cxp + cos * cyp + (y1 + y2) / 2.0;
            double th1 = Ang(1, 0, (x1p - cxp) / arx, (y1p - cyp) / ary);
            double dth = Ang((x1p - cxp) / arx, (y1p - cyp) / ary, (-x1p - cxp) / arx, (-y1p - cyp) / ary);
            if (!sweep && dth > 0) dth -= 2 * Math.PI; else if (sweep && dth < 0) dth += 2 * Math.PI;
            int segs = (int)Math.Ceiling(Math.Abs(dth) / (Math.PI / 2));
            double delta = dth / segs, k = 4.0 / 3 * Math.Tan(delta / 4);
            double px = x1, py = y1;
            for (int i = 0; i < segs; i++)
            {
                double a1 = th1 + i * delta, a2 = a1 + delta;
                double e1x = Math.Cos(a1), e1y = Math.Sin(a1), e2x = Math.Cos(a2), e2y = Math.Sin(a2);
                double c1x = e1x - k * e1y, c1y = e1y + k * e1x, c2x = e2x + k * e2y, c2y = e2y - k * e2x;
                Func<double, double, PointF> map = (ux, uy) => new PointF(
                    (float)(cos * arx * ux - sin * ary * uy + cx), (float)(sin * arx * ux + cos * ary * uy + cy));
                var pc1 = map(c1x, c1y); var pc2 = map(c2x, c2y); var pe = map(e2x, e2y);
                p.AddBezier(new PointF((float)px, (float)py), pc1, pc2, pe);
                px = pe.X; py = pe.Y;
            }
        }

        static double Ang(double ux, double uy, double vx, double vy)
        {
            double a = Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);
            return a;
        }

        sealed class Tokens
        {
            readonly string s; int i;
            public Tokens(string s) { this.s = s; Skip(); }
            void Skip() { while (i < s.Length && (char.IsWhiteSpace(s[i]) || s[i] == ',')) i++; }
            public bool End => i >= s.Length;
            public bool IsCommand => !End && char.IsLetter(s[i]) && s[i] != 'e' && s[i] != 'E';
            public char Command() { char c = s[i++]; Skip(); return c; }
            public bool Flag() { char c = s[i++]; Skip(); return c == '1'; }
            public float Num()
            {
                int st = i;
                if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
                bool dot = false;
                while (i < s.Length)
                {
                    char c = s[i];
                    if (char.IsDigit(c)) i++;
                    else if (c == '.' && !dot) { dot = true; i++; }
                    else if ((c == 'e' || c == 'E') && i + 1 < s.Length) { i++; if (s[i] == '-' || s[i] == '+') i++; }
                    else break;
                }
                if (st == i) { i++; Skip(); return 0; }
                float v = float.Parse(s.Substring(st, i - st), NumberStyles.Float, CultureInfo.InvariantCulture);
                Skip();
                return v;
            }
        }
    }
}
