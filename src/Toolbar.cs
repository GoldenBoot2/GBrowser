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
        public int ZoomPercent = 100;       // the page's zoom; the zoom button shows when it is not the default
        public int DefaultZoomPercent = 100;
        public bool ZoomShown;
        public bool PopupsShown;            // pop-ups were blocked on this page: Chrome's indicator in the address bar
        public event Action PopupsClicked;
        public event Action ZoomClicked;
        public float DownloadProgress = -1; // 0..1 while downloads run (ring around the icon), -1 = none running
        public event Action DownloadsClicked;
        public Omnibox Omni;                // address bar suggestions (Enter / Esc go to it first while it is open)
        public Rectangle PillRect => pill;   // an update is downloaded: a dot on the menu button (like Chrome's update badge)
        public string StoreChipText = "Add to GBrowser";

        public event Action Back, Forward, Reload, Stop, Home, Star, ExtensionsClicked, MenuClicked, StoreChipClicked, ShieldClicked;
        public event Action<string> Go;
        public event Action SiteInfoClicked;            // the icon at the left of the address (Chrome's "View site information")
        public Rectangle SiteInfoRect => siteRect;
        /// <summary>The page to drag out (address, title); null when the shown page cannot be dragged.</summary>
        public Func<Tuple<string, string>> DragPage;
        public event Action<string> DroppedText;   // a link / text / file dropped on the address bar
        Point? siteDown;
        internal static bool DraggingOut;            // our own drag is running (a drop back on the address bar is ignored)
        Rectangle siteRect;
        bool hoverSite;

        readonly List<Btn> buttons = new List<Btn>();

        /// <summary>An extension pinned to the toolbar (Chrome: its icon left of the extensions button).</summary>
        public sealed class ExtAction
        {
            public string Id, Name; public Image Icon; public Rectangle Rect;
            public string Badge = "", Title;     // chrome.action badge text / title (the tooltip), read from the extension
            public Color? BadgeBack, BadgeFore;  // null = default colours
        }
        public readonly List<ExtAction> Actions = new List<ExtAction>();
        public event Action<string, Rectangle> ActionClicked;   // extension id, the icon's screen rectangle
        public event Action<string, Rectangle> ActionMenu;      // right-click: the extension's own menu
        public Rectangle ActionRect(string id) => Actions.Find(a => a.Id == id)?.Rect ?? Rectangle.Empty;
        ExtAction hoverAct, pressAct;
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
            BackColor = Theme.ToolbarBack;
            Address.Font = new Font("Segoe UI", 10.5f);
            Address.BackColor = Theme.Omnibox;
            Address.ForeColor = Theme.OnSurface;
            Address.KeyDown += Address_KeyDown;
            Address.AddressStyle = true;
            Address.GotFocus += (s, e) =>
            {
                Address.BackColor = Theme.Field;
                // Chrome: focus keeps the short address (selected); the full one appears when the user edits
                if (Address.Text == ShownUrl && FullUrl.Length > 0)
                {
                    Address.SetElided(FullUrl);
                    if (!Address.Elided) Address.Text = FullUrl;   // the short form is not part of the full one
                }
                LayoutNow();
                // keyboard focus (Ctrl+L, F6, new tab) selects the address; a click is handled by the field itself
                if (MouseButtons == MouseButtons.None) BeginInvoke((Action)(() => Address.SelectAll()));
            };
            Address.LostFocus += (s, e) => { Address.BackColor = Theme.Omnibox; Address.Text = ShownUrl; Address.ResetView(); LayoutNow(); };
            Address.TextChanged += (s, e) => Invalidate();
            Address.PasteAndGo += t => Go?.Invoke(t);
            // drag the address out (like Chrome: the whole address when not editing, else the selected text)
            Address.DragOut = (t, whole) =>
            {
                var pg = DragPage?.Invoke();
                if (whole || (pg != null && (t == FullUrl || t == ShownUrl))) { if (pg != null) StartDrag(UrlDrag.Make(pg.Item1, pg.Item2)); }
                else StartDrag(UrlDrag.MakeText(t));
            };
            // links / text / files dropped on the address bar open in this tab (Chrome)
            AllowDrop = Address.AllowDrop = true;
            DragEventHandler over = (s, e) => e.Effect = !DraggingOut && UrlDrag.ReadTarget(e.Data) != null
                ? ((e.AllowedEffect & DragDropEffects.Link) != 0 ? DragDropEffects.Link : DragDropEffects.Copy) : DragDropEffects.None;
            DragEventHandler drop = (s, e) => { string t = DraggingOut ? null : UrlDrag.ReadTarget(e.Data); if (t != null) DroppedText?.Invoke(t); };
            DragOver += over; DragDrop += drop; Address.DragOver += over; Address.DragDrop += drop;
            Controls.Add(Address);

            Add("back", "Back (Alt+Left)", () => IconData.Back, () => CanBack, () => Back?.Invoke());
            Add("forward", "Forward (Alt+Right)", () => IconData.Forward, () => CanForward, () => Forward?.Invoke());
            Add("reload", null, () => Loading ? IconData.Stop : IconData.Refresh, () => true, () => { if (Loading) Stop?.Invoke(); else Reload?.Invoke(); });
            Add("home", "Home", () => IconData.Home, () => true, () => Home?.Invoke());
            Add("star", null, () => Bookmarked ? IconData.StarFill : IconData.Star, () => true, () => Star?.Invoke(), () => Bookmarked ? Theme.Primary : Theme.OnSurfaceVariant);
            Add("zoom", null, () => ZoomPercent > DefaultZoomPercent ? IconData.ZoomIn : IconData.ZoomOut, () => true, () => ZoomClicked?.Invoke());
            Add("popups", "Pop-ups blocked", () => IconData.PopupOff, () => true, () => PopupsClicked?.Invoke());
            Add("shield", null, () => IconData.Shield, () => true, () => ShieldClicked?.Invoke(), () => ShieldOn ? Theme.Primary : Theme.Outline);
            Add("downloads", "Downloads (Ctrl+J)", () => IconData.Download, () => true, () => DownloadsClicked?.Invoke(),
                () => DownloadProgress >= 0 ? Theme.Primary : Theme.OnSurfaceVariant);
            Add("ext", "Extensions", () => IconData.Extension, () => true, () => ExtensionsClicked?.Invoke());
            Add("menu", "Menu", () => IconData.More, () => true, () => MenuClicked?.Invoke());
        }


        void Add(string name, string tipText, Func<string> icon, Func<bool> enabled, Action click, Func<Color> color = null)
            => buttons.Add(new Btn { Name = name, Tip = tipText, Icon = icon, Enabled = enabled, Click = click, Color = color });

        public Rectangle ButtonRect(string name) => buttons.Find(b => b.Name == name)?.Rect ?? Rectangle.Empty;
#if AUTOMATION
        public void ClickButton(string name) => buttons.Find(b => b.Name == name)?.Click();
#endif

        float Sc => DeviceDpi / 96f;
        int S(float v) => (int)Math.Round(v * Sc);
        public int PreferredHeight => S(46);

        Rectangle pill, clearRect, chipRect, notSecureRect;

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (buttons.Count < 11) return;   // layout can run while the constructor is still adding controls
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
                if (n == "downloads")
                    // pinned extensions sit between the extensions button and the rest, in the user's order
                    for (int i = Actions.Count - 1; i >= 0; i--)
                    {
                        right -= bs;
                        Actions[i].Rect = new Rectangle(right, y, bs, bs);
                        right -= S(2);
                    }
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
            // zoom indicator (like Chrome): left of the star while the page is not at the default zoom
            int zw = ZoomShown ? S(32) : 0;
            buttons.Find(b => b.Name == "zoom").Rect = ZoomShown ? new Rectangle(pill.Right - S(34) - zw, pill.Y + S(2), S(32), S(32)) : Rectangle.Empty;
            // pop-up blocked indicator (Chrome): left of the zoom indicator / star while this page had pop-ups blocked
            int pw = PopupsShown ? S(32) : 0;
            buttons.Find(b => b.Name == "popups").Rect = PopupsShown ? new Rectangle(pill.Right - S(34) - zw - pw, pill.Y + S(2), S(32), S(32)) : Rectangle.Empty;
            zw += pw;
            clearRect = new Rectangle(pill.Right - S(34) - zw - S(30), pill.Y + S(5), S(26), S(26));
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
            BackColor = Theme.ToolbarBack;
            Address.BackColor = Address.Focused ? Theme.Field : Theme.Omnibox;
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
                using (var b = new SolidBrush(Address.Focused ? Theme.Field : Theme.Omnibox)) g.FillPath(b, p);
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
            foreach (var a in Actions)
            {
                if (a.Rect.IsEmpty) continue;
                if (a == hoverAct || a == pressAct)
                    using (var br = new SolidBrush(a == pressAct ? Theme.Pressed : Theme.Hover)) g.FillEllipse(br, a.Rect);
                int isz = S(16);   // Chrome draws extension icons at 16 px in its 20 px icon area
                if (a.Icon != null) g.DrawImage(a.Icon, a.Rect.X + (a.Rect.Width - isz) / 2, a.Rect.Y + (a.Rect.Height - isz) / 2, isz, isz);
                else Icons.Draw(g, IconData.Extension, a.Rect.X + (a.Rect.Width - S(20)) / 2f, a.Rect.Y + (a.Rect.Height - S(20)) / 2f, S(20), Theme.OnSurfaceVariant);
                if (!string.IsNullOrEmpty(a.Badge))
                {
                    // Chrome: a small label over the icon's bottom right; at most 4 characters show
                    string txt = a.Badge.Length > 4 ? a.Badge.Substring(0, 4) : a.Badge;
                    var back = a.BadgeBack ?? Theme.Primary;
                    var fore = a.BadgeFore ?? (back.R * 0.299 + back.G * 0.587 + back.B * 0.114 > 150 ? Color.Black : Color.White);
                    using (var bf = new Font("Segoe UI", 6.5f, FontStyle.Bold))
                    {
                        // anchored to the icon's bottom right corner, reaching a little past it (Chrome)
                        int iconRight = a.Rect.X + (a.Rect.Width + isz) / 2, iconBottom = a.Rect.Y + (a.Rect.Height + isz) / 2;
                        int bw = Math.Max(S(12), TextRenderer.MeasureText(txt, bf, Size.Empty, TextFormatFlags.NoPadding).Width + S(5));
                        var br = new Rectangle(Math.Min(iconRight + S(5), a.Rect.Right) - bw, iconBottom - S(7), bw, S(12));
                        using (var p = Theme.Round(br, S(3))) using (var bb = new SolidBrush(back)) g.FillPath(bb, p);
                        TextRenderer.DrawText(g, txt, bf, br, fore,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                    }
                }
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
        ExtAction HitAction(Point p) => Actions.Find(a => !a.Rect.IsEmpty && a.Rect.Contains(p));

        /// <summary>Drag a page out (to the bookmarks bar, the tab row, the desktop, other apps).</summary>
        void StartDrag(DataObject d)
        {
            DraggingOut = true;
            try { DoDragDrop(d, DragDropEffects.Copy | DragDropEffects.Link); }
            finally { DraggingOut = false; }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            // the site icon pulled away: drag the page (Chrome's location icon)
            if (siteDown.HasValue && (e.Button & MouseButtons.Left) != 0)
            {
                var ds = SystemInformation.DragSize;
                if (Math.Abs(e.X - siteDown.Value.X) > ds.Width || Math.Abs(e.Y - siteDown.Value.Y) > ds.Height)
                {
                    siteDown = null;
                    var pg = DragPage?.Invoke();
                    if (pg != null) StartDrag(UrlDrag.Make(pg.Item1, pg.Item2));
                    return;
                }
            }
            var ha = HitAction(e.Location);
            if (ha != hoverAct)
            {
                hoverAct = ha; Invalidate();
                if (ha != null) { tipText = ha.Title ?? ha.Name; tip.SetToolTip(this, tipText); }   // Chrome: the extension's title
            }
            if (ha != null) { if (hover != null) { hover = null; Invalidate(); } Cursor = Cursors.Default; return; }
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
                    : h.Name == "zoom" ? "Zoom: " + ZoomPercent + "% (click to reset)"
                    : h.Name == "shield" ? (ShieldOn ? BlockedCount + " blocked" : "Blocking off")
                    : h.Name == "menu" && UpdateDot ? "Update available"
                    : h.Tip;
                if (t != tipText) { tipText = t; tip.SetToolTip(this, t); }
            }
            Cursor = hc || hch || hs ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e) { hover = null; hoverAct = null; hoverClear = hoverChip = hoverSite = false; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            var act = HitAction(e.Location);
            if (act != null && (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right)) { pressAct = act; Invalidate(); return; }
            if (e.Button != MouseButtons.Left) return;
            if (ClearVisible && clearRect.Contains(e.Location)) { Address.Clear(); Address.Focus(); return; }
            if (!siteRect.IsEmpty && siteRect.Contains(e.Location)) { siteDown = e.Location; return; }   // opens on mouse up, or a drag
            if (pill.Contains(e.Location) && Hit(e.Location) == null) { Address.Focus(); return; }
            press = Hit(e.Location);
            if (press != null && !press.Enabled()) press = null;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (pressAct != null)
            {
                var a = pressAct; pressAct = null; Invalidate();
                if (a.Rect.Contains(e.Location))
                {
                    var sr = RectangleToScreen(a.Rect);
                    if (e.Button == MouseButtons.Left) ActionClicked?.Invoke(a.Id, sr);
                    else if (e.Button == MouseButtons.Right) ActionMenu?.Invoke(a.Id, sr);
                }
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            if (StoreChip && chipRect.Contains(e.Location)) { StoreChipClicked?.Invoke(); return; }
            bool wasSite = siteDown.HasValue; siteDown = null;
            if (wasSite && !siteRect.IsEmpty && siteRect.Contains(e.Location)) { SiteInfoClicked?.Invoke(); return; }
            var p = press; press = null; Invalidate();
            if (p != null && p.Rect.Contains(e.Location)) p.Click();
        }

        void Address_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                if (Omni != null && Omni.Enter(e.Alt)) return;
                Go?.Invoke(Address.FullText);
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                if (Omni != null && Omni.Escape()) return;
                // back to the page's address, short form selected (Chrome)
                Address.Text = ShownUrl;
                Address.SetElided(FullUrl);
                Address.SelectAll();
            }
        }

        /// <summary>full = the address, shown = Chrome-style short form (no https://, no www., no lone "/").</summary>
        public void SetUrl(string full, string shown)
        {
#if AUTOMATION
            if ((full ?? "") != FullUrl) Blocking.TestLog.Add("SETURL " + DateTime.Now.ToString("HH:mm:ss.fff") + " full=" + full + " shown=" + shown);
#endif
            string oldFull = FullUrl, oldShown = ShownUrl, text = Address.Text;
            FullUrl = full ?? "";
            ShownUrl = shown ?? "";
            if (!Address.Focused) Address.Text = ShownUrl;
            // focused but not typed in (still the old address or empty): show the new one, like Chrome; typed text stays
            else if (text.Length == 0 || text == oldFull || text == oldShown)
            {
                if (text != ShownUrl) { Address.Text = ShownUrl; Address.SetElided(FullUrl); Address.SelectAll(); }
            }
            LayoutNow();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) tip.Dispose();
            base.Dispose(disposing);
        }
    }
}
