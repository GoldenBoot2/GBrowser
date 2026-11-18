using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace GBrowser
{
    /// <summary>Optional row of bookmarks under the toolbar (Settings: "Show bookmarks bar").</summary>
    internal sealed class BookmarksBar : Control
    {
        public event Action<string, bool> Open;   // url, new tab
        readonly List<KeyValuePair<Rectangle, Bookmark>> items = new List<KeyValuePair<Rectangle, Bookmark>>();
        int hover = -1;
        readonly ToolTip tip = new ToolTip();

        public BookmarksBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface;
        }

        float Sc => DeviceDpi / 96f;
        int S(float v) => (int)Math.Round(v * Sc);
        public int PreferredHeight => S(32);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            items.Clear();
            int x = S(8);
            using (var font = new Font("Segoe UI", 9f))
            {
                foreach (var b in Store.Bookmarks)
                {
                    string t = string.IsNullOrEmpty(b.Title) ? b.Url : b.Title;
                    if (t.Length > 28) t = t.Substring(0, 27) + "...";
                    int w = TextRenderer.MeasureText(t, font).Width + S(34);
                    if (x + w > Width - S(8)) break;
                    var r = new Rectangle(x, S(3), w, Height - S(6));
                    if (items.Count == hover)
                        using (var p = Theme.Round(r, r.Height / 2f)) using (var br = new SolidBrush(Theme.Hover)) g.FillPath(br, p);
                    // the site's own icon once a visit has shown it (Favicons), else Chrome's default globe
                    var fav = Favicons.Image(b.Url);
                    if (fav != null)
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(fav, r.X + S(8), r.Y + (r.Height - S(16)) / 2, S(16), S(16));
                    }
                    else Icons.Draw(g, IconData.Public, r.X + S(8), r.Y + (r.Height - S(16)) / 2, S(16), Theme.OnSurfaceVariant);
                    TextRenderer.DrawText(g, t, font, new Rectangle(r.X + S(28), r.Y, r.Width - S(30), r.Height), Theme.OnSurface,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                    items.Add(new KeyValuePair<Rectangle, Bookmark>(r, b));
                    x += w + S(4);
                }
                if (items.Count == 0)
                    TextRenderer.DrawText(g, "No bookmarks", font, new Rectangle(S(12), 0, Width, Height), Theme.Outline,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            }
            using (var pen = new Pen(Theme.OutlineVariant, 1)) g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
        }

        int Hit(Point p) { for (int i = 0; i < items.Count; i++) if (items[i].Key.Contains(p)) return i; return -1; }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = Hit(e.Location);
            if (h != hover) { hover = h; Invalidate(); tip.SetToolTip(this, h >= 0 ? items[h].Value.Title + "\n" + items[h].Value.Url : null); }
        }

        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            int h = Hit(e.Location);
            if (h < 0) return;
            if (e.Button == MouseButtons.Left) Open?.Invoke(items[h].Value.Url, (ModifierKeys & Keys.Control) != 0);
            else if (e.Button == MouseButtons.Middle) Open?.Invoke(items[h].Value.Url, true);
        }

        protected override void Dispose(bool disposing) { if (disposing) tip.Dispose(); base.Dispose(disposing); }
    }
}
