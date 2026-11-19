using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GBrowser
{
    /// <summary>Chrome's tab hover card: title (two lines at most), the site, and short notes (sleeping, muted, camera in
    /// use, blocked count) in a small Material card under the tab. Never takes focus.</summary>
    internal sealed class HoverCard : Form
    {
        string title = "", site = "";
        readonly List<string> notes = new List<string>();
        readonly Font bold = new Font("Segoe UI Semibold", 9.75f), small = new Font("Segoe UI", 9f);

        public HoverCard()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }

        int S(float v) => (int)Math.Round(v * DeviceDpi / 96f);

        /// <summary>Shows the card for tab t under tabScreen (the tab's rectangle on screen).</summary>
        public void ShowFor(Form owner, BrowserTab t, Rectangle tabScreen)
        {
            string url = t.Created ? t.View.CoreWebView2.Source : t.Url;
            title = string.IsNullOrEmpty(t.Title) ? "New tab" : t.Title;
            site = Pages.IsInternal(url) ? "GBrowser" : (Blocking.HostOf(url) ?? url ?? "");
            if (site.StartsWith("www.")) site = site.Substring(4);
            notes.Clear();
            if (t.Private) notes.Add("Private");
            if (t.Sleeping) notes.Add("Sleeping to save memory");
            if (t.Blocking != null && (t.Blocking.Camera > 0 || t.Blocking.Microphone > 0))
                notes.Add(t.Blocking.Camera > 0 && t.Blocking.Microphone > 0 ? "Using your camera and microphone" : t.Blocking.Camera > 0 ? "Using your camera" : "Using your microphone");
            if (t.Muted) notes.Add("Muted");
            else if (t.PlayingAudio) notes.Add("Playing sound");
            if (t.Blocked > 0) notes.Add(t.Blocked + " blocked");
            int w = S(280), pad = S(12);
            int th;
            using (var g = CreateGraphics())
                th = Math.Min(TextRenderer.MeasureText(g, title, bold, new Size(w - 2 * pad, 1000), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height, bold.Height * 2 + S(2));
            int h = pad + th + S(4) + small.Height + notes.Count * (small.Height + S(2)) + pad;
            var wa = Screen.FromRectangle(tabScreen).WorkingArea;
            int x = Math.Max(wa.Left, Math.Min(tabScreen.Left, wa.Right - w));
            Bounds = new Rectangle(x, tabScreen.Bottom + S(4), w, h);
            if (!Visible) { Owner = owner; Show(owner); }
            Invalidate();
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000020;   // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT
                cp.ClassStyle |= 0x00020000;                          // CS_DROPSHADOW
                return cp;
            }
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int s);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = 2;
            try { DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { }
            int border = ColorTranslator.ToWin32(Theme.OutlineVariant);
            try { DwmSetWindowAttribute(Handle, 34, ref border, 4); } catch { }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; }   // WM_MOUSEACTIVATE -> MA_NOACTIVATE
            if (m.Msg == 0x84) { m.Result = (IntPtr)(-1); return; } // WM_NCHITTEST -> HTTRANSPARENT: the mouse goes to the tabs
            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.SurfaceContainer);
            int pad = S(12), w = Width - 2 * pad, y = pad;
            int th = Math.Min(TextRenderer.MeasureText(g, title, bold, new Size(w, 1000), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height, bold.Height * 2 + S(2));
            TextRenderer.DrawText(g, title, bold, new Rectangle(pad, y, w, th), Theme.OnSurface,
                TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix);
            y += th + S(4);
            TextRenderer.DrawText(g, site, small, new Rectangle(pad, y, w, small.Height), Theme.OnSurfaceVariant,
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            y += small.Height;
            foreach (var n in notes)
            {
                y += S(2);
                TextRenderer.DrawText(g, n, small, new Rectangle(pad, y, w, small.Height), Theme.OnSurfaceVariant,
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                y += small.Height;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { bold.Dispose(); small.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
