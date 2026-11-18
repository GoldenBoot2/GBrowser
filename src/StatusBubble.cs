using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace GBrowser
{
    /// <summary>The link address shown at the bottom-left of the page while the mouse is over a link (Chrome's status
    /// bubble): small text, one rounded corner, a third of the page wide (long addresses shortened in the middle),
    /// full width after 1.6 s, moved to the right side when the mouse is over it. WebView2's own bubble has a fixed,
    /// large font and cannot be styled, so it is turned off (IsStatusBarEnabled = false) and this one is used.</summary>
    internal sealed class StatusBubble : Control
    {
        string text = "";
        bool expanded, right;
        readonly Timer expand = new Timer { Interval = 1600 };

        public StatusBubble()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            Font = new Font("Segoe UI", 8.25f);
            Visible = false;
            expand.Tick += (s, e) => { expand.Stop(); expanded = true; Place(); };
        }

        int S(float v) => (int)Math.Round(v * DeviceDpi / 96f);

        public void SetText(string t)
        {
            t = t ?? "";
            if (t == text) return;
            bool wasEmpty = text.Length == 0;
            text = t;
            if (t.Length == 0) { expand.Stop(); expanded = false; right = false; Visible = false; return; }
            if (wasEmpty) { expanded = false; expand.Stop(); expand.Start(); }
            Place();
        }

        /// <summary>Size and position inside the parent (the page area); call when the parent resizes too.</summary>
        public void Place()
        {
            if (Parent == null || text.Length == 0) return;
            var area = Parent.ClientRectangle;
            int h = S(22);
            int full = TextRenderer.MeasureText(text, Font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width + S(16);
            int max = expanded ? area.Width - S(4) : Math.Max(S(160), area.Width / 3);
            int w = Math.Min(full, max);
            var at = new Rectangle(right ? area.Right - w : area.Left, area.Bottom - h, w, h);
            // the mouse is over where the bubble goes: use the other side (else it would hide the link it describes)
            var mouse = Parent.PointToClient(Cursor.Position);
            if (at.Contains(mouse)) { right = !right; at.X = right ? area.Right - w : area.Left; }
            Bounds = at;
            using (var p = Shape(new Rectangle(0, 0, w, h)))
                Region = new Region(p);
            Visible = true;
            BringToFront();
            Invalidate();
        }

        GraphicsPath Shape(Rectangle r)
        {
            // one rounded corner: top-right when on the left, top-left when on the right (the corners facing the page)
            int c = S(6) * 2;
            var p = new GraphicsPath();
            if (!right)
            {
                p.AddLine(r.Left, r.Top, r.Right - c / 2, r.Top);
                p.AddArc(r.Right - c, r.Top, c, c, 270, 90);
                p.AddLine(r.Right, r.Top + c / 2, r.Right, r.Bottom);
                p.AddLine(r.Right, r.Bottom, r.Left, r.Bottom);
            }
            else
            {
                p.AddArc(r.Left, r.Top, c, c, 180, 90);
                p.AddLine(r.Left + c / 2, r.Top, r.Right, r.Top);
                p.AddLine(r.Right, r.Top, r.Right, r.Bottom);
                p.AddLine(r.Right, r.Bottom, r.Left, r.Bottom);
            }
            p.CloseFigure();
            return p;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.SurfaceContainerHigh);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = Shape(new Rectangle(0, 0, Width - 1, Height)))
            using (var pen = new Pen(Theme.OutlineVariant, 1)) g.DrawPath(pen, p);
            var box = new Rectangle(S(8), 0, Width - S(14), Height);
            TextRenderer.DrawText(g, Elide(text, box.Width - S(8)), Font, box, Theme.OnSurfaceVariant,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }

        const TextFormatFlags Measure = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;

        /// <summary>Like Chrome's URL eliding: keep the scheme + site and the end of the address, drop the middle of
        /// the path ("https://site.com/...why/articleshow/1359.cms"). Other text is cut at the end.</summary>
        string Elide(string t, int width)
        {
            if (TextRenderer.MeasureText(t, Font, Size.Empty, Measure).Width <= width) return t;
            int s = t.IndexOf("://", StringComparison.Ordinal);
            int slash = s < 0 ? -1 : t.IndexOf('/', s + 3);
            if (slash < 0) return t;   // not an address with a path: EndEllipsis
            string head = t.Substring(0, slash + 1) + "…";
            int lo = slash + 1, hi = t.Length;   // drop t[slash+1 .. cut) : find the smallest cut that fits
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (TextRenderer.MeasureText(head + t.Substring(mid), Font, Size.Empty, Measure).Width <= width) hi = mid; else lo = mid + 1;
            }
            return lo >= t.Length ? t : head + t.Substring(lo);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x84) { m.Result = (IntPtr)(-1); return; }   // WM_NCHITTEST -> HTTRANSPARENT (mouse goes to the page)
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) expand.Dispose();
            base.Dispose(disposing);
        }
    }
}
