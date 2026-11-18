using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace GBrowser
{
    /// <summary>Docks DevTools inside the window like Chrome. WebView2 has no docking API: DevTools always opens as
    /// the engine's own top-level window (OpenDevToolsWindow, F12, Ctrl+Shift+I, Inspect). A WinEvent hook on the
    /// engine's browser process sees that window appear; it is then made a child of a panel beside (or below) the page,
    /// with its own title bar moved out of view (height measured with WM_NCHITTEST, so it fits any DPI) and a slim
    /// Material header instead (dock side, separate window, close). Each tab keeps its own DevTools.
    /// Before GBrowser closes, every docked window is given back to the engine and closed normally.</summary>
    internal sealed class DevToolsDock
    {
        readonly Form form;
        readonly Panel host = new Panel { Visible = false };           // header + clip area
        readonly Panel clip = new Panel { Dock = DockStyle.Fill };     // DevTools window lives here (clips its title bar)
        readonly Header header;
        readonly Divider splitter;
        readonly Func<BrowserTab> current;
        readonly Func<IEnumerable<BrowserTab>> tabs;
        readonly Dictionary<BrowserTab, IntPtr> windows = new Dictionary<BrowserTab, IntPtr>();
        readonly HashSet<IntPtr> undocked = new HashSet<IntPtr>();     // windows the user chose to keep separate
        readonly Dictionary<IntPtr, int[]> frames = new Dictionary<IntPtr, int[]>();   // engine frame: top (title bar), left, right, bottom
        readonly Timer watch = new Timer { Interval = 500 };
        IntPtr hook;
        uint enginePid;
        WinEventProc proc;   // kept alive while hooked

        public DevToolsDock(Form form, Control pageArea, Func<BrowserTab> current, Func<IEnumerable<BrowserTab>> tabs)
        {
            this.form = form; this.current = current; this.tabs = tabs;
            header = new Header(this) { Dock = DockStyle.Top };
            splitter = new Divider(this) { Visible = false };
            host.Controls.Add(clip);
            host.Controls.Add(header);
            clip.Resize += (s, e) => Fit();
            var parent = pageArea.Parent;
            parent.Controls.Add(splitter);
            parent.Controls.Add(host);
            // docking order: host first (outermost), then the splitter, then the page area fills the rest
            host.SendToBack();
            splitter.SendToBack();
            ApplySide();
            watch.Tick += (s, e) => Prune();
            watch.Start();
        }

        static Settings St => Store.Settings;
        int S(float v) => (int)Math.Round(v * form.DeviceDpi / 96f);
        bool Bottom => St.DevToolsDock == "bottom";

        /// <summary>Starts watching the engine's browser process for DevTools windows.</summary>
        public void Attach(uint browserPid)
        {
            if (hook != IntPtr.Zero || browserPid == 0) return;
            enginePid = browserPid;
            proc = OnWinEvent;
            hook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_NAMECHANGE, IntPtr.Zero, proc, browserPid, 0, WINEVENT_OUTOFCONTEXT);
        }

        void OnWinEvent(IntPtr hHook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (idObject != 0 || hwnd == IntPtr.Zero || undocked.Contains(hwnd)) return;
            if (ev != EVENT_OBJECT_SHOW && ev != EVENT_OBJECT_NAMECHANGE) return;
            if (GetParent(hwnd) != IntPtr.Zero || !IsWindowVisible(hwnd)) return;   // already docked / not a top-level window
            string title = Title(hwnd);
            if (!title.StartsWith("DevTools", StringComparison.Ordinal) || Class(hwnd) != "Chrome_WidgetWin_1") return;
            form.BeginInvoke((Action)(() => Dock(hwnd, title)));
        }

        // ------------------------------------------------------------ docking

        void Dock(IntPtr hwnd, string title)
        {
            if (!IsWindow(hwnd) || GetParent(hwnd) != IntPtr.Zero) return;
            var tab = TabFor(title);
            if (tab == null) return;
            if (windows.TryGetValue(tab, out var old) && old != hwnd && IsWindow(old)) Close(old);
            windows[tab] = hwnd;
            // measured while it is still the engine's normal window: once inside GBrowser it lays itself out again
            // asynchronously and answers hit tests from its old layout. Retried briefly until the title bar is there.
            int[] fr = MeasureFrame(hwnd);
            for (int i = 0; i < 10 && fr[0] < S(12); i++) { System.Threading.Thread.Sleep(50); fr = MeasureFrame(hwnd); }
            frames[hwnd] = fr;
            int style = GetWindowLong(hwnd, GWL_STYLE);
            style = (style & ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU)) | WS_CHILD;
            SetWindowLong(hwnd, GWL_STYLE, style);
            SetParent(hwnd, clip.Handle);
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, Math.Max(S(200), clip.Width), Math.Max(S(200), clip.Height), SWP_NOZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
            Show(current());
        }

        /// <summary>The engine's own frame around DevTools: title bar height and border widths, read by asking the window
        /// what each pixel is (WM_NCHITTEST: caption / left / right / bottom edge), so it fits any DPI and Windows version.</summary>
        int[] MeasureFrame(IntPtr hwnd)
        {
            GetWindowRect(hwnd, out var r);
            int Hit(int x, int y) => (int)SendMessage(hwnd, WM_NCHITTEST, IntPtr.Zero, (IntPtr)((((long)y & 0xFFFF) << 16) | ((long)x & 0xFFFF)));
            int Run(Func<int, int> hitAt, int max, params int[] codes)
            {
                int n = 0;
                for (int i = 0; i < max; i++) { if (Array.IndexOf(codes, hitAt(i)) >= 0) n = i + 1; else if (n > 0 || i > 2) break; }
                return n;
            }
            int midX = r.L + Math.Min(S(120), (r.R - r.L) / 3), midY = (r.T + r.B) / 2;
            int top = Run(i => Hit(midX, r.T + i), S(80), HTCAPTION, HTTOP);
            int left = Run(i => Hit(r.L + i, midY), S(20), HTLEFT);
            int right = Run(i => Hit(r.R - 1 - i, midY), S(20), HTRIGHT);
            int bottom = Run(i => Hit(midX, r.B - 1 - i), S(20), HTBOTTOM);
            return new[] { top, left, right, bottom };
        }

        BrowserTab TabFor(string title)
        {
            // "DevTools - example.com/path": the tab showing that address, else the current tab
            string addr = title.Length > 11 ? title.Substring(11) : "";
            var cur = current();
            if (cur != null && Matches(cur, addr)) return cur;
            foreach (var t in tabs()) if (Matches(t, addr) && !windows.ContainsKey(t)) return t;
            return cur;
        }

        static bool Matches(BrowserTab t, string addr)
        {
            string src = t.Created ? t.View.CoreWebView2.Source : t.Url;
            if (string.IsNullOrEmpty(src) || addr.Length == 0) return false;
            int s = src.IndexOf("://", StringComparison.Ordinal);
            if (s >= 0) src = src.Substring(s + 3);
            return src.StartsWith(addr, StringComparison.OrdinalIgnoreCase) || addr.StartsWith(src, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Shows the DevTools of this tab (or hides the panel when it has none).</summary>
        public void Show(BrowserTab t)
        {
            IntPtr mine = IntPtr.Zero;
            if (t != null && windows.TryGetValue(t, out var h) && IsWindow(h)) mine = h;
            foreach (var kv in windows) if (kv.Value != mine && IsWindow(kv.Value)) ShowWindow(kv.Value, SW_HIDE);
            bool on = mine != IntPtr.Zero;
            if (on)
            {
                ShowWindow(mine, SW_SHOWNA);
                header.Text = Title(mine);
            }
            if (host.Visible != on) { host.Visible = on; splitter.Visible = on; }
            Fit();
        }

        void Fit()
        {
            var t = current();
            if (t == null || !windows.TryGetValue(t, out var h) || !IsWindow(h)) return;
            var f = frames.TryGetValue(h, out var fr) ? fr : new int[4];
            // the frame lies outside the clip panel, so only DevTools' content shows
            SetWindowPos(h, IntPtr.Zero, -f[1], -f[0], clip.ClientSize.Width + f[1] + f[2], clip.ClientSize.Height + f[0] + f[3], SWP_NOZORDER | SWP_NOACTIVATE);
        }

        void ApplySide()
        {
            host.Dock = Bottom ? DockStyle.Bottom : DockStyle.Right;
            splitter.Dock = host.Dock;
            splitter.Width = splitter.Height = S(8);
            splitter.Cursor = Bottom ? Cursors.HSplit : Cursors.VSplit;
            splitter.Invalidate();
            var area = host.Parent.ClientSize;
            double frac = Bottom ? St.DevToolsHeight : St.DevToolsWidth;
            if (frac <= 0.1 || frac >= 0.9) frac = Bottom ? 0.4 : 0.38;
            if (Bottom) host.Height = (int)(area.Height * frac); else host.Width = (int)(area.Width * frac);
            header.Invalidate();
            Fit();
        }

        void SaveSize()
        {
            var area = host.Parent.ClientSize;
            if (Bottom) St.DevToolsHeight = area.Height > 0 ? (double)host.Height / area.Height : 0.4;
            else St.DevToolsWidth = area.Width > 0 ? (double)host.Width / area.Width : 0.38;
            Store.SaveSettings();
        }

        public void ToggleSide()
        {
            SaveSize();
            St.DevToolsDock = Bottom ? "right" : "bottom";
            Store.SaveSettings();
            ApplySide();
        }

        /// <summary>"Separate window": gives this tab's DevTools back to the engine as a normal window.</summary>
        public void Undock()
        {
            var t = current();
            if (t == null || !windows.TryGetValue(t, out var h) || !IsWindow(h)) return;
            windows.Remove(t);
            undocked.Add(h);
            Release(h, true);
            Show(t);
        }

        public void CloseCurrent()
        {
            var t = current();
            if (t != null && windows.TryGetValue(t, out var h)) { windows.Remove(t); Close(h); }
            Show(t);
        }

        public void CloseFor(BrowserTab t)
        {
            if (windows.TryGetValue(t, out var h)) { windows.Remove(t); Close(h); }
        }

        /// <summary>Back to a normal top-level window of the engine (as it was created).</summary>
        void Release(IntPtr h, bool show)
        {
            if (!IsWindow(h)) return;
            ShowWindow(h, SW_HIDE);
            SetParent(h, IntPtr.Zero);
            int style = GetWindowLong(h, GWL_STYLE);
            style = (style & ~WS_CHILD) | WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU;
            SetWindowLong(h, GWL_STYLE, style);
            var b = form.Bounds;
            SetWindowPos(h, IntPtr.Zero, b.X + b.Width / 3, b.Y + S(80), b.Width * 2 / 3, b.Height - S(120), SWP_NOZORDER | SWP_FRAMECHANGED | (show ? SWP_SHOWWINDOW : 0));
        }

        void Close(IntPtr h)
        {
            if (!IsWindow(h)) return;
            Release(h, false);
            PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>DevTools closed from inside (its own shortcut) or its tab went away: drop it.</summary>
        void Prune()
        {
            bool changed = false;
            foreach (var kv in new List<KeyValuePair<BrowserTab, IntPtr>>(windows))
                if (!IsWindow(kv.Value) || !kv.Key.Created) { windows.Remove(kv.Key); changed = true; }
            undocked.RemoveWhere(h => !IsWindow(h));
            if (changed) Show(current());
            else if (host.Visible)
            {
                var cur = current();
                if (cur != null && windows.TryGetValue(cur, out var h2)) header.Text = Title(h2);   // follows the page title
            }
        }

        /// <summary>GBrowser is closing: hand every docked DevTools back to the engine and close it normally.</summary>
        public void Shutdown()
        {
            watch.Stop();
            if (hook != IntPtr.Zero) { UnhookWinEvent(hook); hook = IntPtr.Zero; }
            foreach (var h in windows.Values) Close(h);
            windows.Clear();
        }

        public bool Docked(BrowserTab t) => t != null && windows.TryGetValue(t, out var h) && IsWindow(h);
        public int DockedCount => windows.Count;
        public Rectangle SplitterOnScreen => splitter.Visible ? splitter.RectangleToScreen(splitter.ClientRectangle) : Rectangle.Empty;
        public string FrameInfo()
        {
            var t = current();
            return t != null && windows.TryGetValue(t, out var h) && frames.TryGetValue(h, out var f) ? string.Join(",", f) : "-";
        }

        /// <summary>Drag to resize, live (like Chrome). The WinForms Splitter only drew an XOR preview line, invisible over
        /// web content, and resized on release, so dragging looked like it did nothing.</summary>
        void ResizeTo(int size)
        {
            var area = host.Parent.ClientSize;
            int max = (Bottom ? area.Height : area.Width) - S(160);
            size = Math.Max(S(160), Math.Min(max, size));
            if (Bottom) host.Height = size; else host.Width = size;
        }

        sealed class Divider : Control
        {
            readonly DevToolsDock dock;
            bool drag, hover;
            Point start;
            int startSize;

            public Divider(DevToolsDock d)
            {
                dock = d;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(Theme.Surface);
                bool active = drag || hover;
                int t = active ? Math.Max(2, (int)Math.Round(3 * DeviceDpi / 96f)) : 1;
                using (var b = new SolidBrush(active ? Theme.Primary : Theme.OutlineVariant))
                    if (dock.Bottom) g.FillRectangle(b, 0, (Height - t) / 2, Width, t);
                    else g.FillRectangle(b, (Width - t) / 2, 0, t, Height);
            }

            protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); }
            protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                drag = true;
                Capture = true;   // keeps the mouse while it moves over the page or DevTools
                start = Cursor.Position;
                startSize = dock.Bottom ? dock.host.Height : dock.host.Width;
                Invalidate();
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                if (!drag) return;
                var p = Cursor.Position;
                // the panel is on the right / at the bottom: moving the divider left / up makes it bigger
                dock.ResizeTo(startSize - (dock.Bottom ? p.Y - start.Y : p.X - start.X));
                Parent?.Update();
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                if (!drag) return;
                drag = false;
                Capture = false;
                dock.SaveSize();
                Invalidate();
            }

            protected override void OnMouseCaptureChanged(EventArgs e)
            {
                if (drag) { drag = false; dock.SaveSize(); Invalidate(); }
                base.OnMouseCaptureChanged(e);
            }
        }

        // ------------------------------------------------------------ header (Material, replaces the engine's title bar)

        sealed class Header : Control
        {
            readonly DevToolsDock dock;
            Rectangle side, sep, close;
            string hot;

            public Header(DevToolsDock d)
            {
                dock = d;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                Font = new Font("Segoe UI", 9f);
                Height = (int)Math.Round(32 * DeviceDpi / 96f);
            }

            int S(float v) => (int)Math.Round(v * DeviceDpi / 96f);
            protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(Theme.SurfaceContainer);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int b = S(28), y = (Height - b) / 2;
                close = new Rectangle(Width - S(6) - b, y, b, b);
                sep = new Rectangle(close.X - b - S(2), y, b, b);
                side = new Rectangle(sep.X - b - S(2), y, b, b);
                Icons.Draw(g, IconData.Code, S(10), (Height - S(16)) / 2f, S(16), Theme.Primary);
                string t = Text.StartsWith("DevTools - ") ? "DevTools  ·  " + Text.Substring(11) : Text;
                TextRenderer.DrawText(g, t, Font, new Rectangle(S(32), 0, Math.Max(0, side.X - S(36)), Height), Theme.OnSurfaceVariant,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                Btn(g, side, dock.Bottom ? IconData.DockRight : IconData.DockBottom, "side");
                Btn(g, sep, IconData.OpenNew, "sep");
                Btn(g, close, IconData.Close, "close");
                using (var p = new Pen(Theme.OutlineVariant, 1)) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
            }

            void Btn(Graphics g, Rectangle r, string icon, string name)
            {
                if (hot == name) using (var b = new SolidBrush(Theme.Hover)) g.FillEllipse(b, r);
                Icons.Draw(g, icon, r.X + (r.Width - S(18)) / 2f, r.Y + (r.Height - S(18)) / 2f, S(18), Theme.OnSurfaceVariant);
            }

            string Hit(Point p) => close.Contains(p) ? "close" : sep.Contains(p) ? "sep" : side.Contains(p) ? "side" : null;

            readonly ToolTip tip = new ToolTip();
            protected override void OnMouseMove(MouseEventArgs e)
            {
                string h = Hit(e.Location);
                if (h == hot) return;
                hot = h; Invalidate();
                tip.SetToolTip(this, h == "close" ? "Close DevTools" : h == "sep" ? "Open in a separate window" : h == "side" ? (dock.Bottom ? "Dock to right" : "Dock to bottom") : null);
            }

            protected override void OnMouseLeave(EventArgs e) { hot = null; Invalidate(); }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                switch (Hit(e.Location))
                {
                    case "close": dock.CloseCurrent(); break;
                    case "sep": dock.Undock(); break;
                    case "side": dock.ToggleSide(); break;
                }
            }
        }

        // ------------------------------------------------------------ Win32

        static string Title(IntPtr h) { var s = new StringBuilder(512); GetWindowText(h, s, 512); return s.ToString(); }
        static string Class(IntPtr h) { var s = new StringBuilder(128); GetClassName(h, s, 128); return s.ToString(); }

        delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
        const uint EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_NAMECHANGE = 0x800C, WINEVENT_OUTOFCONTEXT = 0;
        const int GWL_STYLE = -16, WS_CHILD = 0x40000000, WS_POPUP = unchecked((int)0x80000000), WS_CAPTION = 0x00C00000,
            WS_THICKFRAME = 0x00040000, WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000, WS_SYSMENU = 0x00080000;
        const uint SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20, SWP_SHOWWINDOW = 0x40;
        const int SW_HIDE = 0, SW_SHOWNA = 8, WM_NCHITTEST = 0x84, WM_CLOSE = 0x10, HTCAPTION = 2, HTTOP = 12, HTLEFT = 10, HTRIGHT = 11, HTBOTTOM = 15;

        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc fn, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr SetParent(IntPtr c, IntPtr p);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint f);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int m, IntPtr w, IntPtr l);
    }
}
