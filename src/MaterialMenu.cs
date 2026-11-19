using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GBrowser
{
    /// <summary>Material 3 menu (Chrome-style): rounded surface with shadow, 36 px rows, Material Symbols icons, check
    /// marks for toggles, shortcut hints, keyboard (Up/Down/Home/End/Enter/Esc).
    /// It never takes focus from the browser window (like Chrome's menus), so it cannot rely on deactivation to close.
    /// Web pages run in another process: their clicks never reach this app's message loop. So while a menu is open, a
    /// low-level mouse hook closes it on any click outside it (web pages, other apps, the desktop) and a low-level
    /// keyboard hook gives it the menu keys while the browser window is in front. Both hooks exist only while it is open.</summary>
    internal sealed class MaterialMenu : Form
    {
        public sealed class Entry
        {
            public string Text, Shortcut, Icon;   // Icon = Material Symbols path data
            public Image Image;                   // e.g. a site's favicon (used instead of Icon)
            public bool Checked, Toggle, Separator, Header, Note;
            public bool Enabled = true;
            public Action Click;
            public object Tag;                    // the caller's own data (e.g. the download a row shows)
            internal Rectangle Bounds;
            internal bool Selectable => !Separator && !Header && !Note && Enabled;
        }

        public static MaterialMenu Current { get; private set; }
        public readonly List<Entry> Items = new List<Entry>();
        public int MinWidth;   // logical pixels at 96 dpi (rows whose text changes while the menu is open)
        Form owner;
        Rectangle anchorScreen;
        string key;
        int hover = -1, scroll, contentHeight;
        bool keyboard;   // last change came from the keyboard (shows the focus ring)
        readonly Font font = new Font("Segoe UI", 9.75f), bold = new Font("Segoe UI Semibold", 9.75f), small = new Font("Segoe UI", 9f);

        // a click on the button that opened the menu closes it; the same click must not reopen it
        static string closedKey;
        static DateTime closedAt;

        public MaterialMenu()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.SurfaceContainer;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            AccessibleRole = AccessibleRole.MenuPopup;
        }

        // ------------------------------------------------------------ building

        public Entry Add(string text, string icon, string shortcut, Action click)
        {
            var e = new Entry { Text = text, Icon = icon, Shortcut = shortcut, Click = click };
            Items.Add(e);
            return e;
        }

        public Entry AddToggle(string text, bool on, bool enabled, Action click)
        {
            var e = new Entry { Text = text, Checked = on, Toggle = true, Enabled = enabled, Click = click };
            Items.Add(e);
            return e;
        }

        public void AddSeparator() => Items.Add(new Entry { Separator = true });
        public void AddHeader(string text, string icon) => Items.Add(new Entry { Text = text, Icon = icon, Header = true });
        public void AddNote(string text) => Items.Add(new Entry { Text = text, Note = true });

        // ------------------------------------------------------------ showing

        int S(float v) => (int)Math.Round(v * DeviceDpi / 96f);

        bool HasLeading => Items.Exists(i => !i.Separator && !i.Note && (i.Icon != null || i.Image != null));

        /// <summary>Opens the menu under anchor (a rectangle in anchorControl's client coordinates), right-aligned to
        /// it when alignRight. key names the opener: a click on that opener that closed this menu does not reopen it.
        /// Returns false when that just happened.</summary>
        public bool ShowAt(Control anchorControl, Rectangle anchor, bool alignRight, string key)
        {
            if (key != null && key == closedKey && (DateTime.UtcNow - closedAt).TotalMilliseconds < 400) { closedKey = null; return false; }
            Current?.Close();
            this.key = key;
            owner = anchorControl.FindForm();
            anchorScreen = anchorControl.RectangleToScreen(anchor);
            Owner = owner;
            Layout_(alignRight);
            Current = this;
            Hooks.Start(this);
            owner.Deactivate += OwnerChanged;
            owner.Move += OwnerChanged;
            owner.Resize += OwnerChanged;
            Show(owner);
            return true;
        }

        void Layout_(bool alignRight)
        {
            int w = Math.Max(S(200), S(MinWidth));
            int lead = HasLeading ? S(16 + 20 + 12) : S(16);
            using (var g = CreateGraphics())
                foreach (var it in Items)
                {
                    if (it.Separator) continue;
                    int tw = TextRenderer.MeasureText(g, it.Text ?? "", it.Header ? bold : it.Note ? small : font).Width;
                    int sw = string.IsNullOrEmpty(it.Shortcut) ? 0 : S(32) + TextRenderer.MeasureText(g, it.Shortcut, small).Width;
                    int trail = it.Toggle ? S(12 + 20) : 0;
                    w = Math.Max(w, lead + tw + sw + trail + S(16));
                }
            w = Math.Min(w, S(420));
            int y = S(8);
            foreach (var it in Items)
            {
                int h = it.Separator ? S(17) : it.Header ? S(44) : it.Note ? S(30) : S(36);
                it.Bounds = new Rectangle(0, y, w, h);
                y += h;
            }
            contentHeight = y + S(8);
            var wa = Screen.FromRectangle(anchorScreen).WorkingArea;
            int height = Math.Min(contentHeight, wa.Height - S(16));
            int x = alignRight ? anchorScreen.Right - w : anchorScreen.Left;
            int top = anchorScreen.Bottom + S(4);
            if (top + height > wa.Bottom) top = Math.Max(wa.Top, anchorScreen.Top - S(4) - height);
            x = Math.Max(wa.Left, Math.Min(x, wa.Right - w));
            Bounds = new Rectangle(x, top, w, height);
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 | 0x00000080;   // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
                cp.ClassStyle |= 0x00020000;             // CS_DROPSHADOW (Windows 10; Windows 11 adds its own)
                return cp;
            }
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int s);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = 2;   // DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND (Windows 11: rounded corners + shadow)
            try { DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { }
            int border = ColorTranslator.ToWin32(Theme.OutlineVariant);   // DWMWA_BORDER_COLOR
            try { DwmSetWindowAttribute(Handle, 34, ref border, 4); } catch { }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; }   // WM_MOUSEACTIVATE -> MA_NOACTIVATE
            base.WndProc(ref m);
        }

        void OwnerChanged(object s, EventArgs e) => Close();

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (Current == this) Current = null;
            Hooks.Stop(this);
            if (owner != null) { owner.Deactivate -= OwnerChanged; owner.Move -= OwnerChanged; owner.Resize -= OwnerChanged; }
            base.OnFormClosed(e);
            BeginInvoke((Action)Dispose);
        }

        // ------------------------------------------------------------ drawing

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.SurfaceContainer);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TranslateTransform(0, -scroll);
            int lead = HasLeading ? S(16 + 20 + 12) : S(16);
            for (int i = 0; i < Items.Count; i++)
            {
                var it = Items[i];
                var r = it.Bounds;
                if (it.Separator)
                {
                    using (var p = new Pen(Theme.OutlineVariant, 1)) g.DrawLine(p, 0, r.Y + r.Height / 2, r.Width, r.Y + r.Height / 2);
                    continue;
                }
                if (i == hover && it.Selectable)
                {
                    var hr = new Rectangle(r.X + S(4), r.Y + S(1), r.Width - S(8), r.Height - S(2));
                    using (var p = Theme.Round(hr, S(8))) using (var b = new SolidBrush(Theme.SurfaceContainerHighest)) g.FillPath(b, p);
                    if (keyboard) using (var p = Theme.Round(hr, S(8))) using (var pen = new Pen(Theme.Primary, S(2))) g.DrawPath(pen, p);
                }
                Color fg = it.Header ? Theme.OnSurface : it.Note ? Theme.OnSurfaceVariant : it.Enabled ? Theme.OnSurface : Theme.Outline;
                int iy = r.Y + (r.Height - S(20)) / 2;
                if (it.Image != null) g.DrawImage(it.Image, r.X + S(16) + S(2), iy + S(2), S(16), S(16));
                else if (it.Icon != null)
                    Icons.Draw(g, it.Icon, r.X + S(16), iy, S(20), it.Header ? Theme.Primary : it.Enabled ? Theme.OnSurfaceVariant : Theme.Outline);
                int tx = it.Note ? S(16) : lead;
                int right = r.Right - S(16) - (it.Toggle ? S(32) : 0);
                if (!string.IsNullOrEmpty(it.Shortcut))
                {
                    var sz = TextRenderer.MeasureText(g, it.Shortcut, small);
                    TextRenderer.DrawText(g, it.Shortcut, small, new Rectangle(right - sz.Width, r.Y, sz.Width, r.Height), Theme.OnSurfaceVariant,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPrefix);
                    right -= sz.Width + S(24);
                }
                TextRenderer.DrawText(g, it.Text ?? "", it.Header ? bold : it.Note ? small : font, new Rectangle(tx, r.Y, Math.Max(0, right - tx), r.Height), fg,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                if (it.Toggle && it.Checked)
                    Icons.Draw(g, IconData.Check, r.Right - S(16) - S(20), iy, S(20), it.Enabled ? Theme.Primary : Theme.Outline);
            }
        }

        // ------------------------------------------------------------ mouse

        int HitIndex(Point p)
        {
            p.Y += scroll;
            for (int i = 0; i < Items.Count; i++) if (Items[i].Bounds.Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = HitIndex(e.Location);
            if (h != hover || keyboard) { hover = h; keyboard = false; Invalidate(); }
            Cursor = h >= 0 && Items[h].Selectable ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e) { if (!keyboard) { hover = -1; Invalidate(); } }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            int h = HitIndex(e.Location);
            if (h >= 0) Activate_(h);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            int max = Math.Max(0, contentHeight - ClientSize.Height);
            scroll = Math.Max(0, Math.Min(max, scroll - Math.Sign(e.Delta) * S(36) * 3));
            Invalidate();
        }

        void Activate_(int i)
        {
            var it = Items[i];
            if (!it.Selectable) return;
            Close();
            it.Click?.Invoke();
        }

        // ------------------------------------------------------------ keyboard (from the hook)

        internal bool Key(Keys k)
        {
            switch (k)
            {
                case Keys.Escape: Close(); return true;
                case Keys.Down: MoveSel(1); return true;
                case Keys.Up: MoveSel(-1); return true;
                case Keys.Home: hover = -1; MoveSel(1); return true;
                case Keys.End: hover = Items.Count; MoveSel(-1); return true;
                case Keys.Enter: case Keys.Space: if (hover >= 0 && hover < Items.Count) Activate_(hover); return true;
                case Keys.Tab: Close(); return false;
            }
            return false;
        }

        void MoveSel(int d)
        {
            int i = hover;
            for (int n = 0; n < Items.Count; n++)
            {
                i += d;
                if (i < 0) i = Items.Count - 1;
                if (i >= Items.Count) i = 0;
                if (Items[i].Selectable) break;
            }
            if (i < 0 || i >= Items.Count || !Items[i].Selectable) return;
            hover = i; keyboard = true;
            var b = Items[i].Bounds;
            if (b.Top - scroll < 0) scroll = b.Top - S(8);
            if (b.Bottom - scroll > ClientSize.Height) scroll = b.Bottom + S(8) - ClientSize.Height;
            scroll = Math.Max(0, scroll);
            Invalidate();
        }

        // ------------------------------------------------------------ hooks (only while a menu is open)

        static class Hooks
        {
            delegate IntPtr Proc(int code, IntPtr w, IntPtr l);
            [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, Proc fn, IntPtr mod, uint thread);
            [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
            [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
            [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
            [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
            [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
            [StructLayout(LayoutKind.Sequential)] struct MouseLL { public int X, Y; public uint Data, Flags, Time; public IntPtr Extra; }
            [StructLayout(LayoutKind.Sequential)] struct KeyLL { public uint Vk, Scan, Flags, Time; public IntPtr Extra; }

            static readonly Proc mouseProc = MouseHook, keyProc = KeyHook;   // kept alive while hooked
            static IntPtr mouseHook, keyHook;
            static MaterialMenu menu;
            static readonly uint ownPid = (uint)Process.GetCurrentProcess().Id;

            public static void Start(MaterialMenu m)
            {
                menu = m;
                IntPtr mod = GetModuleHandle(null);
                if (mouseHook == IntPtr.Zero) mouseHook = SetWindowsHookEx(14, mouseProc, mod, 0);   // WH_MOUSE_LL
                if (keyHook == IntPtr.Zero) keyHook = SetWindowsHookEx(13, keyProc, mod, 0);         // WH_KEYBOARD_LL
            }

            public static void Stop(MaterialMenu m)
            {
                if (menu != m) return;
                menu = null;
                if (mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(mouseHook); mouseHook = IntPtr.Zero; }
                if (keyHook != IntPtr.Zero) { UnhookWindowsHookEx(keyHook); keyHook = IntPtr.Zero; }
            }

            static IntPtr MouseHook(int code, IntPtr w, IntPtr l)
            {
                int msg = (int)w;
                // any button press (client or not) outside the menu closes it; the press itself goes on as normal
                if (code >= 0 && menu != null && (msg == 0x201 || msg == 0x204 || msg == 0x207 || msg == 0x20B || msg == 0xA1 || msg == 0xA4))
                {
                    var p = Marshal.PtrToStructure<MouseLL>(l);
                    var m = menu;
                    var pt = new Point(p.X, p.Y);
                    if (!m.Bounds.Contains(pt))
                    {
                        Rectangle anchor = m.anchorScreen;
                        if (anchor.Contains(pt)) { closedKey = m.key; closedAt = DateTime.UtcNow; }
                        m.BeginInvoke((Action)m.Close);
                    }
                }
                return CallNextHookEx(IntPtr.Zero, code, w, l);
            }

            static IntPtr KeyHook(int code, IntPtr w, IntPtr l)
            {
                int msg = (int)w;
                if (code >= 0 && menu != null && (msg == 0x100 || msg == 0x104))   // WM_KEYDOWN / WM_SYSKEYDOWN
                {
                    // only while GBrowser is the window in front (never swallow keys typed into other apps)
                    GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
                    if (pid == ownPid)
                    {
                        var k = (Keys)Marshal.PtrToStructure<KeyLL>(l).Vk;
                        if (k == Keys.Escape || k == Keys.Up || k == Keys.Down || k == Keys.Home || k == Keys.End ||
                            k == Keys.Enter || k == Keys.Space || k == Keys.Tab)
                        {
                            var m = menu;
                            if (k == Keys.Tab) { m.BeginInvoke((Action)m.Close); return CallNextHookEx(IntPtr.Zero, code, w, l); }
                            m.BeginInvoke((Action)(() => m.Key(k)));
                            return (IntPtr)1;   // the menu handles it
                        }
                    }
                }
                return CallNextHookEx(IntPtr.Zero, code, w, l);
            }
        }
    }
}
