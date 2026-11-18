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
        public readonly TextBox Address = new TextBox();
        public bool CanBack, CanForward, Loading, Bookmarked, Secure, StoreChip, ShieldOn = true;
        public int BlockedCount;
        public string StoreChipText = "Add to GBrowser";

        public event Action Back, Forward, Reload, Stop, Home, Star, ExtensionsClicked, MenuClicked, StoreChipClicked, ShieldClicked;
        public event Action<string> Go;

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
            Address.BorderStyle = BorderStyle.None;
            Address.Font = new Font("Segoe UI", 10.5f);
            Address.BackColor = Theme.SurfaceContainerHigh;
            Address.ForeColor = Theme.OnSurface;
            Address.KeyDown += Address_KeyDown;
            Address.GotFocus += (s, e) => { Address.BackColor = Color.White; Invalidate(); BeginInvoke((Action)(() => Address.SelectAll())); };
            Address.LostFocus += (s, e) => { Address.BackColor = Theme.SurfaceContainerHigh; Address.Text = ShownUrl; Invalidate(); };
            Address.TextChanged += (s, e) => Invalidate();
            Address.MouseUp += (s, e) => { if (Address.SelectionLength == 0 && firstClick) Address.SelectAll(); firstClick = false; };
            Address.Enter += (s, e) => firstClick = true;
            Controls.Add(Address);

            Add("back", "Back (Alt+Left)", () => IconData.Back, () => CanBack, () => Back?.Invoke());
            Add("forward", "Forward (Alt+Right)", () => IconData.Forward, () => CanForward, () => Forward?.Invoke());
            Add("reload", null, () => Loading ? IconData.Stop : IconData.Refresh, () => true, () => { if (Loading) Stop?.Invoke(); else Reload?.Invoke(); });
            Add("home", "Home", () => IconData.Home, () => true, () => Home?.Invoke());
            Add("star", null, () => Bookmarked ? IconData.StarFill : IconData.Star, () => true, () => Star?.Invoke(), () => Bookmarked ? Theme.Primary : Theme.OnSurfaceVariant);
            Add("shield", null, () => IconData.Shield, () => true, () => ShieldClicked?.Invoke(), () => ShieldOn ? Theme.Primary : Theme.Outline);
            Add("ext", "Extensions", () => IconData.Extension, () => true, () => ExtensionsClicked?.Invoke());
            Add("menu", "Menu", () => IconData.More, () => true, () => MenuClicked?.Invoke());
        }

        bool firstClick;

        void Add(string name, string tipText, Func<string> icon, Func<bool> enabled, Action click, Func<Color> color = null)
            => buttons.Add(new Btn { Name = name, Tip = tipText, Icon = icon, Enabled = enabled, Click = click, Color = color });

        public Rectangle ButtonRect(string name) => buttons.Find(b => b.Name == name)?.Rect ?? Rectangle.Empty;

        float Sc => DeviceDpi / 96f;
        int S(float v) => (int)Math.Round(v * Sc);
        public int PreferredHeight => S(46);

        Rectangle pill, clearRect, chipRect;

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (buttons.Count < 8) return;   // layout can run while the constructor is still adding controls
            int bs = S(34), y = (Height - bs) / 2, x = S(6);
            foreach (var n in new[] { "back", "forward", "reload", "home" })
            {
                buttons.Find(b => b.Name == n).Rect = new Rectangle(x, y, bs, bs);
                x += bs + S(2);
            }
            int right = Width - S(6);
            foreach (var n in new[] { "menu", "ext", "shield" })
            {
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
            Address.SetBounds(pill.X + S(40), pill.Y + (pill.Height - th) / 2, clearRect.X - S(6) - (pill.X + S(40)), th);
        }

        public void LayoutNow() { PerformLayout(); Invalidate(); }

        bool ClearVisible => Address.Focused && Address.TextLength > 0;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Theme.OutlineVariant, 1)) g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
            // address pill
            using (var p = Theme.Round(pill, pill.Height / 2f))
            {
                using (var b = new SolidBrush(Address.Focused ? Color.White : Theme.SurfaceContainerHigh)) g.FillPath(b, p);
                if (Address.Focused) using (var pen = new Pen(Theme.Primary, S(2))) g.DrawPath(pen, p);
            }
            string leftIcon = Address.Focused || string.IsNullOrEmpty(ShownUrl) ? IconData.Search : Secure ? IconData.Lock : IconData.Info;
            Icons.Draw(g, leftIcon, pill.X + S(12), pill.Y + (pill.Height - S(18)) / 2, S(18), Theme.OnSurfaceVariant);
            if (!Address.Focused && Address.TextLength == 0)
                TextRenderer.DrawText(g, "Search or type a URL", Address.Font, Address.Bounds, Theme.Outline,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
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
                bool en = b.Enabled();
                if (en && (b == hover || b == press))
                    using (var br = new SolidBrush(b == press ? Theme.Pressed : Theme.Hover)) g.FillEllipse(br, b.Rect);
                int isz = S(20);
                var col = !en ? Theme.Disabled : b.Color != null ? b.Color() : Theme.OnSurfaceVariant;
                Icons.Draw(g, b.Icon(), b.Rect.X + (b.Rect.Width - isz) / 2f, b.Rect.Y + (b.Rect.Height - isz) / 2f, isz, col);
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
            if (h != hover || hc != hoverClear || hch != hoverChip)
            {
                hover = h; hoverClear = hc; hoverChip = hch; Invalidate();
                string t = h == null ? (hc ? "Clear" : hch ? "Install this extension" : null)
                    : h.Name == "reload" ? (Loading ? "Stop (Esc)" : "Reload (F5)")
                    : h.Name == "star" ? (Bookmarked ? "Remove bookmark (Ctrl+D)" : "Bookmark this page (Ctrl+D)")
                    : h.Name == "shield" ? (ShieldOn ? BlockedCount + " blocked" : "Blocking off")
                    : h.Tip;
                if (t != tipText) { tipText = t; tip.SetToolTip(this, t); }
            }
            Cursor = hc || hch ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e) { hover = null; hoverClear = hoverChip = false; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (ClearVisible && clearRect.Contains(e.Location)) { Address.Clear(); Address.Focus(); return; }
            if (pill.Contains(e.Location) && Hit(e.Location) == null) { Address.Focus(); return; }
            press = Hit(e.Location);
            if (press != null && !press.Enabled()) press = null;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (StoreChip && chipRect.Contains(e.Location)) { StoreChipClicked?.Invoke(); return; }
            var p = press; press = null; Invalidate();
            if (p != null && p.Rect.Contains(e.Location)) p.Click();
        }

        void Address_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                Go?.Invoke(Address.Text);
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                Address.Text = ShownUrl;
                Address.SelectAll();
            }
        }

        public void SetUrl(string shown)
        {
            ShownUrl = shown ?? "";
            if (!Address.Focused) Address.Text = ShownUrl;
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) tip.Dispose();
            base.Dispose(disposing);
        }
    }
}
