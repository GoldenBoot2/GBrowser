using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using GBrowser;

namespace GBrowserSetup
{
    /// <summary>The setup window: a brand panel (animated logo, drifting light) and a feature showcase that keeps
    /// playing during the install, then a success screen. Everything is drawn here (Material 3, GBrowser's gold
    /// palette, light or dark like Windows) and animated with a 60 fps timer.</summary>
    internal sealed class SetupForm : Form
    {
        enum Page { Ready, Busy, Done, Failed }

        readonly Installer inst;
        readonly bool uninstall;
        Page page = Page.Ready;
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly Timer tick = new Timer { Interval = 15 };
        readonly Image logo;
        readonly string fontName;
        float shownProgress;   // eased towards inst.Progress
        double pageAt;         // when the page last changed (s)
        bool isUpdate, running;

        // feature showcase
        static readonly string[][] Features =
        {
            new[] { IconData.Shield, "Blocks ads and trackers", "Over a million rules, stopped before they load." },
            new[] { IconData.Memory, "Light on memory", "Tabs in the background sleep and free memory." },
            new[] { IconData.Lock, "Private by design", "Secure DNS, HTTPS-First and encrypted browsing data." },
            new[] { IconData.Extension, "Chrome extensions", "Add them straight from the Chrome Web Store." },
            new[] { IconData.Palette, "Your colours", "Material You themes, light and dark like Windows." },
            new[] { IconData.Private, "Private tabs", "Nothing is kept once you close them." },
        };
        int card, prevCard = -1;
        double cardAt;
        const double CardSeconds = 3.6, SlideSeconds = 0.45;

        // interactive areas
        Rectangle btnPrimary, btnSecondary, btnClose, btnMin, chkBox;
        readonly List<Rectangle> dots = new List<Rectangle>();
        string hot = "";
        bool pressed;

        public bool AutoStart;   // tests: start after 3 s
        public bool UpdateMode;  // started by GBrowser's updater: install at once, then start GBrowser again

        public SetupForm(Installer inst, bool uninstall)
        {
            this.inst = inst;
            this.uninstall = uninstall;
            Text = uninstall ? "Uninstall GBrowser" : "GBrowser Setup";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            try { Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location); } catch { }
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("logo.png"))
                if (s != null) logo = new Bitmap(Image.FromStream(s));
            fontName = FontExists("Segoe UI Variable Display") ? "Segoe UI Variable Display" : "Segoe UI";
            ClientSize = new Size(S(880), S(540));
            Pal.Load();
            if (!uninstall)
            {
                isUpdate = Installer.InstalledDir() != null;
                running = inst.Running().Count > 0;
            }
            tick.Tick += (s, e) => OnTick();
            tick.Start();
            KeyPreview = true;
        }

        static bool FontExists(string name)
        {
            using (var f = new InstalledFontCollection()) foreach (var ff in f.Families) if (ff.Name == name) return true;
            return false;
        }

        int S(float v) => (int)Math.Round(v * DeviceDpi / 96f);
        double Now => clock.Elapsed.TotalSeconds;
        Font F(float pt, FontStyle st = FontStyle.Regular, string name = null) => new Font(name ?? fontName, pt * 96f / DeviceDpi * DeviceDpi / 96f, st, GraphicsUnit.Point);

        // ------------------------------------------------------------ window chrome

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int s);

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ClassStyle |= 0x00020000; /* CS_DROPSHADOW */ cp.Style |= 0x00020000; /* WS_MINIMIZEBOX */ return cp; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = 2; try { DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { }       // rounded corners (Windows 11)
            int dark = Pal.Dark ? 1 : 0; try { DwmSetWindowAttribute(Handle, 20, ref dark, 4); } catch { }
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x84 && (int)m.Result == 1)   // WM_NCHITTEST in the client area: drag by any empty spot
            {
                var p = PointToClient(new Point((short)((long)m.LParam & 0xFFFF), (short)(((long)m.LParam >> 16) & 0xFFFF)));
                if (HitName(p) == "") m.Result = (IntPtr)2;   // HTCAPTION
            }
        }

        // ------------------------------------------------------------ flow

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            clock.Restart(); cardAt = 0; pageAt = 0;   // the intro animation plays once the window is on screen
            if (AutoStart) Task.Delay(3000).ContinueWith(_ => BeginInvoke((Action)Start));
            else if (UpdateMode) Task.Delay(900).ContinueWith(_ => BeginInvoke((Action)Start));
        }

        void SetPage(Page p) { page = p; pageAt = Now; Invalidate(); }

        void Start()
        {
            if (page == Page.Busy) return;
            inst.Error = null; inst.Progress = 0; shownProgress = 0;
            SetPage(Page.Busy);
            Task.Run(() =>
            {
                if (uninstall) inst.Uninstall();
                else
                {
                    if (inst.Running().Count > 0) { inst.Step = "Closing GBrowser"; if (!inst.CloseRunning()) { inst.Error = "Close GBrowser first"; return; } }
                    inst.Install();
                }
            });
        }

        void OnTick()
        {
            if (WindowState == FormWindowState.Minimized) return;
            if (page == Page.Busy)
            {
                if (inst.Error != null) SetPage(Page.Failed);
                else if (inst.Progress >= 1f && shownProgress > 0.995f)
                {
                    SetPage(Page.Done);
                    if (UpdateMode) Task.Delay(1600).ContinueWith(_ => BeginInvoke((Action)(() => { inst.Launch(); Close(); })));
                }
            }
            shownProgress += (inst.Progress - shownProgress) * 0.12f;
            if (inst.Progress >= 1f && 1f - shownProgress < 0.004f) shownProgress = 1f;
            if (Now - cardAt > CardSeconds && page != Page.Done && !uninstall) NextCard((card + 1) % Features.Length);
            Invalidate();
        }

        void NextCard(int i)
        {
            if (i == card) return;
            prevCard = card; card = i; cardAt = Now;
        }

        void Primary()
        {
            switch (page)
            {
                case Page.Ready: Start(); break;
                case Page.Failed: Start(); break;
                case Page.Done:
                    if (!uninstall) inst.Launch();
                    Close();
                    break;
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys k)
        {
            if (k == Keys.Enter) { if (page != Page.Busy) Primary(); return true; }
            if (k == Keys.Escape) { if (page != Page.Busy) Close(); return true; }
            if (k == Keys.Right && !uninstall) { NextCard((card + 1) % Features.Length); return true; }
            if (k == Keys.Left && !uninstall) { NextCard((card + Features.Length - 1) % Features.Length); return true; }
            return base.ProcessCmdKey(ref msg, k);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (page == Page.Busy) { e.Cancel = true; return; }   // never stop half-way
            base.OnFormClosing(e);
        }

        // ------------------------------------------------------------ mouse

        string HitName(Point p)
        {
            if (btnClose.Contains(p)) return "close";
            if (btnMin.Contains(p)) return "min";
            if (btnPrimary.Contains(p)) return "primary";
            if (btnSecondary.Contains(p)) return "secondary";
            if (chkBox.Contains(p)) return "check";
            for (int i = 0; i < dots.Count; i++) if (dots[i].Contains(p)) return "dot" + i;
            return "";
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            string h = HitName(e.Location);
            if (h != hot) { hot = h; Cursor = h == "" ? Cursors.Default : Cursors.Hand; }
        }

        protected override void OnMouseLeave(EventArgs e) { hot = ""; pressed = false; }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) pressed = true; }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            bool was = pressed; pressed = false;
            if (!was || e.Button != MouseButtons.Left) return;
            string h = HitName(e.Location);
            if (h == "close") { if (page != Page.Busy) Close(); }
            else if (h == "min") WindowState = FormWindowState.Minimized;
            else if (h == "primary") Primary();
            else if (h == "secondary") { if (page != Page.Busy) Close(); }
            else if (h == "check" && page == Page.Ready)
            {
                if (uninstall) inst.DeleteData = !inst.DeleteData; else inst.DesktopShortcut = !inst.DesktopShortcut;
            }
            else if (h.StartsWith("dot")) NextCard(int.Parse(h.Substring(3)));
        }

        // ------------------------------------------------------------ easing

        static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
        static double EaseOut(double t) => 1 - Math.Pow(1 - Clamp01(t), 3);
        static double EaseInOut(double t) { t = Clamp01(t); return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2; }
        static double EaseBack(double t) { t = Clamp01(t); const double c1 = 1.70158, c3 = c1 + 1; return 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2); }
        static Color A(Color c, double a) => Color.FromArgb((int)(Clamp01(a) * c.A), c);

        static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ------------------------------------------------------------ painting

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Pal.Surface);
            int split = S(360);
            DrawBrand(g, new Rectangle(0, 0, split, ClientSize.Height));
            DrawRight(g, new Rectangle(split, 0, ClientSize.Width - split, ClientSize.Height));
            DrawWindowButtons(g);
        }

        void DrawBrand(Graphics g, Rectangle r)
        {
            double t = Now;
            using (var bg = new LinearGradientBrush(r, Pal.BrandTop, Pal.BrandBottom, 70f)) g.FillRectangle(bg, r);
            // drifting light
            var state = g.Save();
            g.SetClip(r);
            for (int i = 0; i < 3; i++)
            {
                double ph = t * (0.11 + i * 0.04) + i * 2.1;
                float cx = r.X + r.Width * (0.5f + 0.38f * (float)Math.Sin(ph * 1.3 + i));
                float cy = r.Y + r.Height * (0.45f + 0.36f * (float)Math.Cos(ph + i * 0.7));
                float rad = S(170 + 40 * i);
                using (var path = new GraphicsPath())
                {
                    path.AddEllipse(cx - rad, cy - rad, rad * 2, rad * 2);
                    using (var pb = new PathGradientBrush(path) { CenterColor = A(i == 1 ? Pal.Glow2 : Pal.Glow, 0.55), SurroundColors = new[] { Color.FromArgb(0, Pal.Glow) } })
                        g.FillPath(pb, path);
                }
            }
            g.Restore(state);

            // logo: pops in, then floats
            double intro = EaseBack(t / 0.9);
            float size = S(112) * (float)(0.6 + 0.4 * intro);
            float lx = r.X + (r.Width - size) / 2f, ly = r.Y + S(150) - size / 2f + (float)Math.Sin(t * 1.6) * S(4) * (float)Clamp01(t - 0.9);
            if (logo != null)
            {
                var cm = new System.Drawing.Imaging.ColorMatrix { Matrix33 = (float)Clamp01(t / 0.5) };
                using (var ia = new System.Drawing.Imaging.ImageAttributes())
                {
                    ia.SetColorMatrix(cm);
                    g.DrawImage(logo, new Rectangle((int)lx, (int)ly, (int)size, (int)size), 0, 0, logo.Width, logo.Height, GraphicsUnit.Pixel, ia);
                }
            }
            double ta = EaseOut((t - 0.35) / 0.7);
            float rise = (float)(1 - ta) * S(14);
            using (var f = F(28, FontStyle.Bold))
            using (var b = new SolidBrush(A(Pal.BrandText, ta)))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center })
                g.DrawString("GBrowser", f, b, new RectangleF(r.X, r.Y + S(222) + rise, r.Width, S(50)), sf);
            double ta2 = EaseOut((t - 0.55) / 0.7);
            using (var f = F(12))
            using (var b = new SolidBrush(A(Pal.BrandSub, ta2)))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center })
                g.DrawString("Fast. Private. Light.", f, b, new RectangleF(r.X, r.Y + S(284) + (float)(1 - ta2) * S(10), r.Width, S(30)), sf);
            using (var f = F(9))
            using (var b = new SolidBrush(A(Pal.BrandSub, 0.8 * ta2)))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center })
                g.DrawString("Version " + Installer.Version + "  ·  GoldenBoot", f, b, new RectangleF(r.X, r.Bottom - S(44), r.Width, S(24)), sf);
        }

        void DrawWindowButtons(Graphics g)
        {
            int w = S(46), h = S(36);
            btnClose = new Rectangle(ClientSize.Width - w, 0, w, h);
            btnMin = new Rectangle(ClientSize.Width - 2 * w, 0, w, h);
            if (hot == "close") using (var b = new SolidBrush(Color.FromArgb(0xC4, 0x2B, 0x1C))) g.FillRectangle(b, btnClose);
            if (hot == "min") using (var b = new SolidBrush(Pal.Hover)) g.FillRectangle(b, btnMin);
            bool busy = page == Page.Busy;
            Icons.Draw(g, IconData.WinClose, btnClose.X + (w - S(16)) / 2f, (h - S(16)) / 2f, S(16), hot == "close" ? Color.White : A(Pal.OnSurfaceVariant, busy ? 0.35 : 1));
            Icons.Draw(g, IconData.WinMin, btnMin.X + (w - S(16)) / 2f, (h - S(16)) / 2f, S(16), Pal.OnSurfaceVariant);
        }

        void DrawRight(Graphics g, Rectangle r)
        {
            int pad = S(48);
            var inner = new Rectangle(r.X + pad, r.Y + S(56), r.Width - 2 * pad, r.Height - S(56) - S(40));
            double pt = Now - pageAt;
            dots.Clear();
            btnPrimary = btnSecondary = chkBox = Rectangle.Empty;

            if (page == Page.Done) { DrawDone(g, inner, pt); return; }

            // heading
            string head = uninstall ? (page == Page.Busy ? "Removing GBrowser" : page == Page.Failed ? "Something went wrong" : "Remove GBrowser?")
                : page == Page.Busy ? (isUpdate ? "Updating GBrowser" : "Installing GBrowser")
                : page == Page.Failed ? "Something went wrong"
                : isUpdate ? "Update GBrowser" : "Welcome to GBrowser";
            double ha = EaseOut((Now - 0.2) / 0.6);
            using (var f = F(20, FontStyle.Bold))
            using (var b = new SolidBrush(A(Pal.OnSurface, ha)))
                g.DrawString(head, f, b, inner.X, inner.Y + (float)(1 - ha) * S(8));

            var cardRect = new Rectangle(inner.X, inner.Y + S(56), inner.Width, S(220));
            if (uninstall) DrawUninstallCard(g, cardRect);
            else DrawShowcase(g, cardRect);

            // action area
            int ay = inner.Bottom - S(52);
            if (page == Page.Busy) DrawProgress(g, new Rectangle(inner.X, ay - S(16), inner.Width, S(68)));
            else
            {
                if (page == Page.Failed)
                    using (var f = F(10)) using (var b = new SolidBrush(Pal.Error))
                        g.DrawString(inst.Error ?? "", f, b, new RectangleF(inner.X, ay - S(44), inner.Width, S(40)));
                else
                {
                    // the one option
                    bool on = uninstall ? inst.DeleteData : inst.DesktopShortcut;
                    string label = uninstall ? "Also delete browsing data (history, bookmarks, passwords)" : "Desktop shortcut";
                    chkBox = new Rectangle(inner.X - S(6), ay - S(52), S(30) + TextWidth(g, label, 10.5f) + S(12), S(36));
                    DrawCheck(g, chkBox, label, on);
                }
                string ptext = page == Page.Failed ? "Try again" : uninstall ? "Remove" : running ? "Close GBrowser and install" : isUpdate ? "Update" : "Install";
                int bw = TextWidth(g, ptext, 11f, FontStyle.Bold) + S(56);
                btnPrimary = new Rectangle(inner.Right - bw, ay, bw, S(48));
                DrawButton(g, btnPrimary, ptext, true, "primary", uninstall && page != Page.Failed);
                string stext = "Cancel";
                int sw = TextWidth(g, stext, 11f, FontStyle.Bold) + S(40);
                btnSecondary = new Rectangle(btnPrimary.X - sw - S(8), ay, sw, S(48));
                DrawButton(g, btnSecondary, stext, false, "secondary");
                if (!uninstall && page == Page.Ready)
                    using (var f = F(9)) using (var b = new SolidBrush(Pal.OnSurfaceVariant))
                    using (var sf = new StringFormat { Alignment = StringAlignment.Center })
                        g.DrawString(isUpdate ? "Your tabs, bookmarks and settings stay as they are" : "Installs for your Windows account · no admin needed",
                            f, b, new RectangleF(inner.X, cardRect.Bottom + S(40), inner.Width, S(24)), sf);
            }
        }

        int TextWidth(Graphics g, string s, float pt, FontStyle st = FontStyle.Regular)
        {
            using (var f = F(pt, st)) return (int)Math.Ceiling(g.MeasureString(s, f).Width);
        }

        void DrawShowcase(Graphics g, Rectangle r)
        {
            using (var p = Round(r, S(24))) using (var b = new SolidBrush(Pal.Container)) g.FillPath(b, p);
            double st = (Now - cardAt) / SlideSeconds;
            var state = g.Save();
            using (var clip = Round(r, S(24))) g.SetClip(clip);
            if (prevCard >= 0 && st < 1)
            {
                double e = EaseInOut(st);
                DrawFeature(g, r, prevCard, (float)(-e * S(80)), 1 - e);
                DrawFeature(g, r, card, (float)((1 - e) * S(80)), e);
            }
            else DrawFeature(g, r, card, 0, prevCard < 0 ? EaseOut(Now / 0.8) : 1);
            // a thin bar showing time to the next card
            double life = Clamp01((Now - cardAt) / CardSeconds);
            using (var b = new SolidBrush(A(Pal.Primary, 0.35)))
                g.FillRectangle(b, r.X, r.Bottom - S(3), (float)(r.Width * life), S(3));
            g.Restore(state);

            // dots (the current one is a pill)
            int n = Features.Length, dw = S(8), gap = S(8), x = r.X + (r.Width - (n * dw + (n - 1) * gap + S(16))) / 2, y = r.Bottom + S(18);
            for (int i = 0; i < n; i++)
            {
                double on = i == card ? EaseOut(st) : i == prevCard ? 1 - EaseOut(st) : 0;
                int w = dw + (int)(S(16) * on);
                var d = new Rectangle(x, y, w, dw);
                using (var p = Round(d, dw / 2f)) using (var b = new SolidBrush(Blend(Pal.OutlineVariant, Pal.Primary, on))) g.FillPath(b, p);
                dots.Add(Rectangle.Inflate(d, S(4), S(6)));
                x += w + gap;
            }
        }

        static Color Blend(Color a, Color b, double t)
        {
            t = Clamp01(t);
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        void DrawFeature(Graphics g, Rectangle r, int i, float dx, double alpha)
        {
            var f = Features[i];
            int ic = S(72);
            float x = r.X + S(32) + dx, y = r.Y + S(36);
            // icon in a tonal circle, with a soft pulse
            double pulse = 0.5 + 0.5 * Math.Sin(Now * 2.2);
            using (var b = new SolidBrush(A(Pal.PrimaryContainer, alpha * (0.25 + 0.15 * pulse))))
                g.FillEllipse(b, x - S(6), y - S(6), ic + S(12), ic + S(12));
            using (var b = new SolidBrush(A(Pal.PrimaryContainer, alpha))) g.FillEllipse(b, x, y, ic, ic);
            Icons.Draw(g, f[0], x + (ic - S(36)) / 2f, y + (ic - S(36)) / 2f, S(36), A(Pal.OnPrimaryContainer, alpha));
            using (var tf = F(17, FontStyle.Bold)) using (var b = new SolidBrush(A(Pal.OnSurface, alpha)))
                g.DrawString(f[1], tf, b, x - S(2), y + ic + S(20));
            using (var sf = F(11)) using (var b = new SolidBrush(A(Pal.OnSurfaceVariant, alpha)))
                g.DrawString(f[2], sf, b, new RectangleF(x, y + ic + S(56), r.Width - S(64), S(48)));
        }

        void DrawUninstallCard(Graphics g, Rectangle r)
        {
            using (var p = Round(r, S(24))) using (var b = new SolidBrush(Pal.Container)) g.FillPath(b, p);
            int ic = S(72);
            float x = r.X + S(32), y = r.Y + S(36);
            using (var b = new SolidBrush(Pal.PrimaryContainer)) g.FillEllipse(b, x, y, ic, ic);
            Icons.Draw(g, IconData.Delete, x + (ic - S(36)) / 2f, y + (ic - S(36)) / 2f, S(36), Pal.OnPrimaryContainer);
            using (var tf = F(15, FontStyle.Bold)) using (var b = new SolidBrush(Pal.OnSurface))
                g.DrawString("GBrowser " + (Installer.InstalledVersion() ?? Installer.Version), tf, b, x - S(2), y + ic + S(20));
            using (var sf = F(11)) using (var b = new SolidBrush(Pal.OnSurfaceVariant))
                g.DrawString("The app and its shortcuts will be removed.", sf, b, new RectangleF(x, y + ic + S(52), r.Width - S(64), S(48)));
        }

        void DrawProgress(Graphics g, Rectangle r)
        {
            using (var f = F(10.5f)) using (var b = new SolidBrush(Pal.OnSurfaceVariant))
                g.DrawString(inst.Step ?? "", f, b, r.X, r.Y);
            string pct = (int)Math.Round(shownProgress * 100) + "%";
            using (var f = F(10.5f, FontStyle.Bold)) using (var b = new SolidBrush(Pal.OnSurface))
            using (var sf = new StringFormat { Alignment = StringAlignment.Far })
                g.DrawString(pct, f, b, new RectangleF(r.X, r.Y, r.Width, S(24)), sf);
            var track = new RectangleF(r.X, r.Y + S(36), r.Width, S(8));
            using (var p = Round(track, S(4))) using (var b = new SolidBrush(Pal.Container)) g.FillPath(b, p);
            var fill = new RectangleF(track.X, track.Y, Math.Max(S(8), track.Width * shownProgress), track.Height);
            using (var p = Round(fill, S(4)))
            {
                using (var b = new SolidBrush(Pal.Primary)) g.FillPath(b, p);
                // a light sweeping along the filled part
                float sx = fill.X + (float)((Now * 0.9) % 1.4 - 0.2) * track.Width;
                var state = g.Save();
                g.SetClip(p);
                var sweep = new RectangleF(sx - S(60), fill.Y, S(120), fill.Height);
                using (var lb = new LinearGradientBrush(new RectangleF(sweep.X - 1, sweep.Y, sweep.Width + 2, sweep.Height), Color.FromArgb(0, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 0f))
                {
                    var blend = new ColorBlend { Colors = new[] { Color.FromArgb(0, 255, 255, 255), Color.FromArgb(110, 255, 255, 255), Color.FromArgb(0, 255, 255, 255) }, Positions = new[] { 0f, 0.5f, 1f } };
                    lb.InterpolationColors = blend;
                    g.FillRectangle(lb, sweep);
                }
                g.Restore(state);
            }
        }

        void DrawDone(Graphics g, Rectangle r, double pt)
        {
            float cx = r.X + r.Width / 2f, cy = r.Y + S(120);
            double pop = EaseBack(pt / 0.6);
            float rad = S(56) * (float)pop;
            // expanding ring
            double ring = Clamp01(pt / 0.9);
            using (var pen = new Pen(A(Pal.Primary, 0.5 * (1 - ring)), S(3)))
                g.DrawEllipse(pen, cx - S(56) - (float)ring * S(36), cy - S(56) - (float)ring * S(36), (S(56) + (float)ring * S(36)) * 2, (S(56) + (float)ring * S(36)) * 2);
            using (var b = new SolidBrush(Pal.Primary)) g.FillEllipse(b, cx - rad, cy - rad, rad * 2, rad * 2);
            // the check draws itself from left to right
            double draw = EaseOut((pt - 0.35) / 0.5);
            if (draw > 0)
            {
                var state = g.Save();
                float cs = S(60);
                g.SetClip(new RectangleF(cx - cs / 2, cy - cs / 2, cs * (float)draw, cs));
                Icons.Draw(g, IconData.Check, cx - cs / 2, cy - cs / 2, cs, Pal.OnPrimary);
                g.Restore(state);
            }
            double ta = EaseOut((pt - 0.5) / 0.6);
            string title = uninstall ? "GBrowser has been removed" : "GBrowser is ready";
            string sub = uninstall ? (inst.DeleteData ? "Your browsing data was deleted too." : "Your browsing data was kept.") : "Find it in the Start menu" + (inst.DesktopShortcut ? " and on your desktop." : ".");
            using (var f = F(20, FontStyle.Bold)) using (var b = new SolidBrush(A(Pal.OnSurface, ta)))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center })
                g.DrawString(title, f, b, new RectangleF(r.X, cy + S(80) + (float)(1 - ta) * S(10), r.Width, S(40)), sf);
            using (var f = F(11)) using (var b = new SolidBrush(A(Pal.OnSurfaceVariant, ta)))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center })
                g.DrawString(sub, f, b, new RectangleF(r.X, cy + S(124), r.Width, S(30)), sf);
            if (ta > 0.5)
            {
                string ptext = uninstall ? "Close" : "Launch GBrowser";
                int bw = TextWidth(g, ptext, 11f, FontStyle.Bold) + S(56);
                int sw = uninstall ? 0 : TextWidth(g, "Close", 11f, FontStyle.Bold) + S(40);
                int total = bw + (uninstall ? 0 : sw + S(8));
                int x0 = (int)(cx - total / 2f), by = r.Bottom - S(60);
                if (!uninstall)
                {
                    btnSecondary = new Rectangle(x0, by, sw, S(48));
                    DrawButton(g, btnSecondary, "Close", false, "secondary");
                    x0 += sw + S(8);
                }
                btnPrimary = new Rectangle(x0, by, bw, S(48));
                DrawButton(g, btnPrimary, ptext, true, "primary");
            }
        }

        void DrawButton(Graphics g, Rectangle r, string text, bool filled, string name, bool danger = false)
        {
            bool h = hot == name, p = h && pressed;
            Color bg = filled ? (danger ? Pal.Error : Pal.Primary) : Color.Transparent;
            Color fg = filled ? (danger ? Pal.OnError : Pal.OnPrimary) : Pal.Primary;
            using (var path = Round(r, r.Height / 2f))
            {
                if (filled)
                {
                    if (h) using (var sh = new SolidBrush(A(Color.Black, Pal.Dark ? 0.35 : 0.18))) using (var sp = Round(new RectangleF(r.X, r.Y + S(2), r.Width, r.Height), r.Height / 2f)) g.FillPath(sh, sp);
                    using (var b = new SolidBrush(bg)) g.FillPath(b, path);
                }
                if (h || p) using (var b = new SolidBrush(A(filled ? fg : Pal.Primary, p ? 0.16 : 0.08))) g.FillPath(b, path);
            }
            using (var f = F(11f, FontStyle.Bold)) using (var b = new SolidBrush(fg))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(text, f, b, r, sf);
        }

        void DrawCheck(Graphics g, Rectangle r, string label, bool on)
        {
            if (hot == "check") using (var p = Round(r, S(8))) using (var b = new SolidBrush(Pal.Hover)) g.FillPath(b, p);
            var box = new RectangleF(r.X + S(9), r.Y + (r.Height - S(18)) / 2f, S(18), S(18));
            using (var p = Round(box, S(3)))
            {
                if (on) using (var b = new SolidBrush(Pal.Primary)) g.FillPath(b, p);
                else using (var pen = new Pen(Pal.OnSurfaceVariant, S(2))) g.DrawPath(pen, p);
            }
            if (on) Icons.Draw(g, IconData.Check, box.X + S(1), box.Y + S(1), S(16), Pal.OnPrimary);
            using (var f = F(10.5f)) using (var b = new SolidBrush(Pal.OnSurface))
            using (var sf = new StringFormat { LineAlignment = StringAlignment.Center })
                g.DrawString(label, f, b, new RectangleF(box.Right + S(12), r.Y, r.Width, r.Height), sf);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { tick.Dispose(); logo?.Dispose(); }
            base.Dispose(disposing);
        }
    }

    /// <summary>GBrowser's gold Material 3 scheme (same values as the browser's Palette.Gold), light or dark like Windows.</summary>
    internal static class Pal
    {
        public static bool Dark;
        public static Color Primary, OnPrimary, PrimaryContainer, OnPrimaryContainer, Surface, Container, OnSurface, OnSurfaceVariant,
            Outline, OutlineVariant, Error, OnError, Hover, BrandTop, BrandBottom, BrandText, BrandSub, Glow, Glow2;

        static Color C(int v) => Color.FromArgb(255, Color.FromArgb(v));

        public static void Load()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    Dark = k?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch { }
            if (Dark)
            {
                Primary = C(0xF0BF48); OnPrimary = C(0x402D00); PrimaryContainer = C(0x5B4300); OnPrimaryContainer = C(0xFFDFA0);
                Surface = C(0x17130B); Container = C(0x241F17); OnSurface = C(0xECE1D4); OnSurfaceVariant = C(0xD3C5B0);
                Outline = C(0x9C8F7C); OutlineVariant = C(0x4F4637); Error = C(0xFFB4AB); OnError = C(0x690005);
                BrandTop = C(0x2E2310); BrandBottom = C(0x120E07); BrandText = C(0xFFF3DC); BrandSub = C(0xE3CFA6);
                Glow = C(0xC8901A); Glow2 = C(0x8A5A00);
            }
            else
            {
                Primary = C(0x785A00); OnPrimary = C(0xFFFFFF); PrimaryContainer = C(0xFFDFA0); OnPrimaryContainer = C(0x261A00);
                Surface = C(0xFFF8F2); Container = C(0xF4EDE1); OnSurface = C(0x1F1B13); OnSurfaceVariant = C(0x4D4639);
                Outline = C(0x7F7667); OutlineVariant = C(0xD0C5B4); Error = C(0xBA1A1A); OnError = C(0xFFFFFF);
                BrandTop = C(0x3A2A06); BrandBottom = C(0x16100A); BrandText = C(0xFFF3DC); BrandSub = C(0xE8D5AE);
                Glow = C(0xE0A526); Glow2 = C(0x9A6A00);
            }
            Hover = Color.FromArgb(0x14, OnSurface);
        }
    }
}
