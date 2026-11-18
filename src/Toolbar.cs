using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace GBrowser
{
    /// <summary>Toolbar: back, forward, reload/stop, home | address box (+ clear button) | add-extension chip, star,
    /// extensions, menu. Owner-drawn icon buttons around a borderless TextBox.</summary>
    internal sealed class Toolbar : Control
    {
        public readonly MaterialTextBox Address = new MaterialTextBox { Placeholder = "Search or type a URL", AccessibleName = "Address and search bar" };
        public bool CanBack, CanForward, Loading, Bookmarked, Secure, StoreChip, ShieldOn = true;
        /// <summary>Page kind for the site-info icon: "secure" (https), "internal" (GBrowser page), "insecure" (http), "" (other).</summary>
        public string PageKind = "";
        public bool ShowHome;
        /// <summary>Full address (shown while editing) and the Chrome-style short form (shown otherwise).</summary>
        public string FullUrl = "";
        public int BlockedCount;
        public bool UpdateDot;
        public bool ShowDownloads;          // a download happened in this session: the download button is shown
        public float DownloadProgress = -1; // 0..1 while downloads run (ring around the icon), -1 = none running
        public event Action DownloadsClicked;
        public Omnibox Omni;                // address bar suggestions (Enter / Esc go to it first while it is open)
        public Rectangle PillRect => pill;   // an update is downloaded: a dot on the menu button (like Chrome's update badge)
        public string StoreChipText = "Add to GBrowser";

        public event Action Back, Forward, Reload, Stop, Home, Star, ExtensionsClicked, MenuClicked, StoreChipClicked, ShieldClicked;
        public event Action<string> Go;
        public event Action SiteInfoClicked;            // the icon at the left of the address (Chrome's "View site information")
        public Rectangle SiteInfoRect => siteRect;
        Rectangle siteRect;
        bool hoverSite;

        readonly List<Btn> buttons = new List<Btn>();
        Btn hover, press;
        bool hoverClear, hoverChip;
        readonly ToolTip tip = new ToolTip();
        string tipText;
        public string ShownUrl = "";

        sealed class Btn
        {
            public string Name, Tip; public Rectangle Rect; public Func<string> Icon; public Func<bool> Enabled; public Func<Color> Color; public Action Click;
        }

        public Toolbar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface;
            Address.Font = new Font("Segoe UI", 10.5f);
            Address.BackColor = Theme.SurfaceContainerHigh;
            Address.ForeColor = Theme.OnSurface;
            Address.KeyDown += Address_KeyDown;
            Address.GotFocus += (s, e) =>
            {
                Address.BackColor = Theme.Field;
                if (Address.Text == ShownUrl && FullUrl.Length > 0) Address.Text = FullUrl;   // edit the full address
                LayoutNow();
                // keyboard focus (Ctrl+L, F6, new tab) selects the address; a click is handled by the field itself
                if (MouseButtons == MouseButtons.None) BeginInvoke((Action)(() => Address.SelectAll()));
            };
            Address.LostFocus += (s, e) => { Address.BackColor = Theme.SurfaceContainerHigh; Address.Text = ShownUrl; Address.ResetView(); LayoutNow(); };
            Address.TextChanged += (s, e) => Invalidate();
            Address.PasteAndGo += t => Go?.Invoke(t);
            Controls.Add(Address);

            Add("back", "Back (Alt+Left)", () => IconData.Back, () => CanBack, () => Back?.Invoke());
            Add("forward", "Forward (Alt+Right)", () => IconData.Forward, () => CanForward, () => Forward?.Invoke());
            Add("reload", null, () => Loading ? IconData.Stop : IconData.Refresh, () => true, () => { if (Loading) Stop?.Invoke(); else Reload?.Invoke(); });
            Add("home", "Home", () => IconData.Home, () => true, () => Home?.Invoke());
            Add("star", null, () => Bookmarked ? IconData.StarFill : IconData.Star, () => true, () => Star?.Invoke(), () => Bookmarked ? Theme.Primary : Theme.OnSurfaceVariant);
            Add("shield", null, () => IconData.Shield, () => true, () => ShieldClicked?.Invoke(), () => ShieldOn ? Theme.Primary : Theme.Outline);
            Add("downloads", "Downloads (Ctrl+J)", () => IconData.Download, () => true, () => DownloadsClicked?.Invoke(),
                () => DownloadProgress >= 0 ? Theme.Primary : Theme.OnSurfaceVariant);
            Add("ext", "Extensions", () => IconData.Extension, () => true, () => ExtensionsClicked?.Invoke());
            Add("menu", "Menu", () => IconData.More, () => true, () => MenuClicked?.Invoke());
        }


        void Add(string name, string tipText, Func<string> icon, Func<bool> enabled, Action click, Func<Color> color = null)
            => buttons.Add(new Btn { Name = name, Tip = tipText, Icon = icon, Enabled = enabled, Click = click, Color = color });

        public Rectangle ButtonRect(string name) => buttons.Find(b => b.Name == name)?.Rect ?? Rectangle.Empty;

        float Sc => DeviceDpi / 96f;
        int S(float v) => (int)Math.Round(v * Sc);
        public int PreferredHeight => S(46);

        Rectangle pill, clearRect, chipRect, notSecureRect;

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (buttons.Count < 9) return;   // layout can run while the constructor is still adding controls
            int bs = S(34), y = (Height - bs) / 2, x = S(6);
            foreach (var n in new[] { "back", "forward", "reload", "home" })
            {
                if (n == "home" && !ShowHome) { buttons.Find(b => b.Name == n).Rect = Rectangle.Empty; continue; }
                buttons.Find(b => b.Name == n).Rect = new Rectangle(x, y, bs, bs);
                x += bs + S(2);
            }
            int right = Width - S(6);
            foreach (var n in new[] { "menu", "ext", "downloads", "shield" })
            {
                if (n == "downloads" && !ShowDownloads) { buttons.Find(b => b.Name == n).Rect = Rectangle.Empty; continue; }
                right -= bs;
                buttons.Find(b => b.Name == n).Rect = new Rectangle(right, y, bs, bs);
                right -= S(2);
            }
            int chipW = 0;
            if (StoreChip)
            {
                using (var f = new Font("Segoe UI", 9f, FontStyle.Bold))
                    chipW = TextRenderer.MeasureText(StoreChipText, f).Width + S(40);
                right -= S(4);
                chipRect = new Rectangle(right - chipW, (Height - S(30)) / 2, chipW, S(30));
                right -= chipW + S(4);
            }
            else chipRect = Rectangle.Empty;
            pill = new Rectangle(x + S(6), (Height - S(36)) / 2, Math.Max(S(120), right - x - S(10)), S(36));
            // star sits inside the right end of the address pill
            buttons.Find(b => b.Name == "star").Rect = new Rectangle(pill.Right - S(34), pill.Y + S(2), S(32), S(32));
            clearRect = new Rectangle(pill.Right - S(34) - S(30), pill.Y + S(5), S(26), S(26));
            int th = Address.PreferredHeight;
            int ax = pill.X + S(40);
            notSecureRect = Rectangle.Empty;
            if (PageKind == "insecure" && !Address.Focused)
            {
                using (var f = new Font("Segoe UI", 9f, FontStyle.Bold))
                {
                    int w = TextRenderer.MeasureText("Not secure", f).Width + S(8);
                    notSecureRect = new Rectangle(pill.X + S(36), pill.Y + (pill.Height - S(24)) / 2, w, S(24));
                }
                ax = notSecureRect.Right + S(10);
            }
            Address.SetBounds(ax, pill.Y + (pill.Height - th) / 2, clearRect.X - S(6) - ax, th);
        }

        public void LayoutNow() { PerformLayout(); Invalidate(); }

        public void ApplyTheme()
        {
            BackColor = Theme.Surface;
            Address.BackColor = Address.Focused ? Theme.Field : Theme.SurfaceContainerHigh;
            Address.ForeColor = Theme.OnSurface;
            Invalidate(true);
        }

        bool ClearVisible => Address.Focused && Address.TextLength > 0;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Theme.OutlineVariant, 1)) g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
            // address pill
            using (var p = Theme.Round(pill, pill.Height / 2f))
            {
                using (var b = new SolidBrush(Address.Focused ? Theme.Field : Theme.SurfaceContainerHigh)) g.FillPath(b, p);
                if (Address.Focused) using (var pen = new Pen(Theme.Primary, S(2))) g.DrawPath(pen, p);
            }
            // site-info button: the icon (and "Not secure") at the left of the address, clickable like Chrome's
            siteRect = Address.Focused || string.IsNullOrEmpty(ShownUrl) ? Rectangle.Empty
                : new Rectangle(pill.X + S(3), pill.Y + S(3), (notSecureRect.IsEmpty ? S(30) : notSecureRect.Right - pill.X + S(6)), pill.Height - S(6));
            if (hoverSite && !siteRect.IsEmpty)
                using (var p = Theme.Round(siteRect, siteRect.Height / 2f)) using (var b = new SolidBrush(Theme.Hover)) g.FillPath(b, p);
            // site-info icon like Chrome: "tune" for secure sites, the GBrowser logo for its own pages, a warning for http
            string leftIcon = Address.Focused || string.IsNullOrEmpty(ShownUrl) ? IconData.Search
                : PageKind == "secure" ? IconData.Tune : PageKind == "internal" ? IconData.Language
                : PageKind == "insecure" ? IconData.Warning : IconData.Info;
            var iconColor = PageKind == "insecure" && !Address.Focused ? Color.FromArgb(Theme.Dark ? 0xFF : 0xBA, Theme.Dark ? 0xB4 : 0x1A, Theme.Dark ? 0xAB : 0x1A) : Theme.OnSurfaceVariant;
            Icons.Draw(g, leftIcon, pill.X + S(12), pill.Y + (pill.Height - S(18)) / 2, S(18), iconColor);
            if (!notSecureRect.IsEmpty)
                using (var f = new Font("Segoe UI", 9f, FontStyle.Bold))
                    TextRenderer.DrawText(g, "Not secure", f, notSecureRect, iconColor, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
            if (ClearVisible)
            {
                if (hoverClear) using (var b = new SolidBrush(Theme.Hover)) g.FillEllipse(b, clearRect);
                Icons.Draw(g, IconData.Close, clearRect.X + S(4), clearRect.Y + S(4), S(18), Theme.OnSurfaceVariant);
            }
            if (StoreChip)
            {
                using (var p = Theme.Round(chipRect, chipRect.Height / 2f))
                using (var b = new SolidBrush(hoverChip ? Theme.Primary : Theme.PrimaryContainer)) g.FillPath(b, p);
                var fg = hoverChip ? Theme.OnPrimary : Theme.OnPrimaryContainer;
                Icons.Draw(g, IconData.Add, chipRect.X + S(10), chipRect.Y + (chipRect.Height - S(18)) / 2, S(18), fg);
                using (var f = new Font("Segoe UI", 9f, FontStyle.Bold))
                    TextRenderer.DrawText(g, StoreChipText, f, new Rectangle(chipRect.X + S(30), chipRect.Y, chipRect.Width - S(36), chipRect.Height), fg,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
            }
            foreach (var b in buttons)
            {
                if (b.Rect.IsEmpty) continue;   // hidden (e.g. the home button when it is switched off)
                bool en = b.Enabled();
                if (en && (b == hover || b == press))
                    using (var br = new SolidBrush(b == press ? Theme.Pressed : Theme.Hover)) g.FillEllipse(br, b.Rect);
                int isz = S(20);
                var col = !en ? Theme.Disabled : b.Color != null ? b.Color() : Theme.OnSurfaceVariant;
                Icons.Draw(g, b.Icon(), b.Rect.X + (b.Rect.Width - isz) / 2f, b.Rect.Y + (b.Rect.Height - isz) / 2f, isz, col);
                if (b.Name == "downloads" && DownloadProgress >= 0)
                {
                    // progress ring like Chrome's download button
                    var ring = new RectangleF(b.Rect.X + S(4), b.Rect.Y + S(4), b.Rect.Width - S(8), b.Rect.Height - S(8));
                    using (var track = new Pen(Theme.OutlineVariant, S(2))) g.DrawEllipse(track, ring);
                    using (var arc = new Pen(Theme.Primary, S(2)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
                        g.DrawArc(arc, ring, -90, Math.Max(6f, 360f * Math.Min(1f, DownloadProgress)));
                }
                if (b.Name == "menu" && UpdateDot)
                    using (var bb = new SolidBrush(Theme.Primary))
                        g.FillEllipse(bb, b.Rect.Right - S(13), b.Rect.Y + S(6), S(8), S(8));
                if (b.Name == "shield" && ShieldOn && BlockedCount > 0)
                {
                    string n = BlockedCount > 99 ? "99+" : BlockedCount.ToString();
                    using (var bf = new Font("Segoe UI", 7f, FontStyle.Bold))
                    {
                        int bw = Math.Max(S(16), TextRenderer.MeasureText(n, bf, Size.Empty, TextFormatFlags.NoPadding).Width + S(6));
                        var br = new Rectangle(b.Rect.Right - bw + S(2), b.Rect.Bottom - S(15), bw, S(14));
                        using (var p = Theme.Round(br, br.Height / 2f)) using (var bb = new SolidBrush(Theme.Primary)) g.FillPath(bb, p);
                        TextRenderer.DrawText(g, n, bf, br, Theme.OnPrimary,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                    }
                }
            }
        }

        Btn Hit(Point p) => buttons.Find(b => b.Rect.Contains(p));

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var h = Hit(e.Location);
            bool hc = ClearVisible && clearRect.Contains(e.Location);
            bool hch = StoreChip && chipRect.Contains(e.Location);
            bool hs = !siteRect.IsEmpty && siteRect.Contains(e.Location);
            if (h != hover || hc != hoverClear || hch != hoverChip || hs != hoverSite)
            {
                hover = h; hoverClear = hc; hoverChip = hch; hoverSite = hs; Invalidate();
                string t = h == null ? (hc ? "Clear" : hch ? "Install this extension" : hs ? "View site information" : null)
                    : h.Name == "reload" ? (Loading ? "Stop (Esc)" : "Reload (F5)")
                    : h.Name == "star" ? (Bookmarked ? "Remove bookmark (Ctrl+D)" : "Bookmark this page (Ctrl+D)")
                    : h.Name == "shield" ? (ShieldOn ? BlockedCount + " blocked" : "Blocking off")
                    : h.Name == "menu" && UpdateDot ? "Update available"
                    : h.Tip;
                if (t != tipText) { tipText = t; tip.SetToolTip(this, t); }
            }
            Cursor = hc || hch || hs ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e) { hover = null; hoverClear = hoverChip = hoverSite = false; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (ClearVisible && clearRect.Contains(e.Location)) { Address.Clear(); Address.Focus(); return; }
            if (!siteRect.IsEmpty && siteRect.Contains(e.Location)) return;   // opens on mouse up
            if (pill.Contains(e.Location) && Hit(e.Location) == null) { Address.Focus(); return; }
            press = Hit(e.Location);
            if (press != null && !press.Enabled()) press = null;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (StoreChip && chipRect.Contains(e.Location)) { StoreChipClicked?.Invoke(); return; }
            if (!siteRect.IsEmpty && siteRect.Contains(e.Location)) { SiteInfoClicked?.Invoke(); return; }
            var p = press; press = null; Invalidate();
            if (p != null && p.Rect.Contains(e.Location)) p.Click();
        }

        void Address_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                if (Omni != null && Omni.Enter(e.Alt)) return;
                Go?.Invoke(Address.Text);
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                if (Omni != null && Omni.Escape()) return;
                Address.Text = FullUrl.Length > 0 ? FullUrl : ShownUrl;
                Address.SelectAll();
            }
        }

        /// <summary>full = the address, shown = Chrome-style short form (no https://, no www., no lone "/").</summary>
        public void SetUrl(string full, string shown)
        {
            FullUrl = full ?? "";
            ShownUrl = shown ?? "";
            if (!Address.Focused) Address.Text = ShownUrl;
            LayoutNow();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) tip.Dispose();
            base.Dispose(disposing);
        }
    }
}
