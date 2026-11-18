using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace GBrowser
{
    /// <summary>Owner-drawn tab row: select, close (button or middle click), drag to reorder, new tab button.</summary>
    internal sealed class TabStrip : Control
    {
        public List<BrowserTab> Tabs = new List<BrowserTab>();
        public int Selected = -1;

        public event Action<int> TabSelected;
        public event Action<int> TabClosed;
        public event Action NewTabClicked;
        public event Action<int, int> TabMoved;

        int hover = -1; bool hoverClose, hoverPlus, pressPlus;
        int dragIndex = -1; Point dragStart; bool dragging;
        readonly Timer spin = new Timer { Interval = 50 };
        float spinAngle;
        readonly ToolTip tip = new ToolTip();
        string tipText;

        public TabStrip()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.SurfaceContainerHigh;
            spin.Tick += (s, e) => { spinAngle = (spinAngle + 30) % 360; Invalidate(); };
        }

        float Sc => DeviceDpi / 96f;
        int S(float v) => (int)Math.Round(v * Sc);

        public int PreferredHeight => S(40);

        Rectangle TabRect(int i)
        {
            int left = S(8), plus = S(44);
            int avail = Width - left - plus - S(8);
            int n = Math.Max(1, Tabs.Count);
            int w = Math.Min(S(240), Math.Max(S(56), avail / n));
            return new Rectangle(left + i * w, S(6), w, Height - S(6));
        }

        Rectangle CloseRect(Rectangle tr) => new Rectangle(tr.Right - S(30), tr.Y + (tr.Height - S(20)) / 2, S(20), S(20));

        Rectangle PlusRect()
        {
            var last = Tabs.Count > 0 ? TabRect(Tabs.Count - 1) : new Rectangle(S(8), 0, 0, 0);
            return new Rectangle(last.Right + S(6), S(6) + (Height - S(6) - S(30)) / 2, S(30), S(30));
        }

        public void RefreshTabs()
        {
            bool anyLoading = Tabs.Exists(t => t.Loading);
            spin.Enabled = anyLoading;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var font = new Font("Segoe UI", 9f))
            {
                for (int i = 0; i < Tabs.Count; i++)
                {
                    var t = Tabs[i];
                    var r = TabRect(i);
                    bool sel = i == Selected;
                    if (sel)
                    {
                        using (var p = Theme.TopRound(new RectangleF(r.X, r.Y, r.Width, r.Height + 1), S(8)))
                        using (var b = new SolidBrush(Theme.Surface)) g.FillPath(b, p);
                    }
                    else
                    {
                        if (i == hover)
                            using (var p = Theme.Round(new RectangleF(r.X + S(2), r.Y + S(2), r.Width - S(4), r.Height - S(8)), S(8)))
                            using (var b = new SolidBrush(Theme.SurfaceContainerHighest)) g.FillPath(b, p);
                        // separator between unselected tabs
                        if (i + 1 < Tabs.Count && i + 1 != Selected && i != hover && i + 1 != hover)
                            using (var pen = new Pen(Theme.OutlineVariant, Math.Max(1, S(1))))
                                g.DrawLine(pen, r.Right, r.Y + S(9), r.Right, r.Bottom - S(13));
                    }
                    int ix = r.X + S(12), iy = r.Y + (r.Height - S(16)) / 2 - (sel ? 0 : S(2));
                    bool narrow = r.Width < S(90);
                    bool showClose = sel || i == hover || !narrow;
                    if (t.Loading)
                    {
                        using (var pen = new Pen(Theme.Primary, S(2)))
                            g.DrawArc(pen, ix + S(1), iy + S(1), S(14), S(14), spinAngle, 270);
                    }
                    else if (t.Private) Icons.Draw(g, IconData.Private, ix, iy, S(16), Theme.Primary);
                    else if (t.Favicon != null) g.DrawImage(t.Favicon, ix, iy, S(16), S(16));
                    else Icons.Draw(g, IconData.Public, ix, iy, S(16), Theme.OnSurfaceVariant);
                    int textLeft = ix + S(24);
                    int textRight = showClose ? CloseRect(r).X - S(4) : r.Right - S(10);
                    if (t.Blocked > 0 && r.Width >= S(120))
                    {
                        // ads / trackers blocked on this tab's page
                        string n = t.Blocked > 999 ? "999+" : t.Blocked.ToString();
                        using (var bf = new Font("Segoe UI", 7.5f, FontStyle.Bold))
                        {
                            int bw = Math.Max(S(20), TextRenderer.MeasureText(n, bf, Size.Empty, TextFormatFlags.NoPadding).Width + S(10));
                            var br = new Rectangle(textRight - bw, iy, bw, S(16));
                            using (var p = Theme.Round(br, br.Height / 2f))
                            using (var b = new SolidBrush(Theme.PrimaryContainer)) g.FillPath(b, p);
                            TextRenderer.DrawText(g, n, bf, br, Theme.OnPrimaryContainer,
                                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                            textRight -= bw + S(4);
                        }
                    }
                    if (t.PlayingAudio)
                    {
                        Icons.Draw(g, IconData.Volume, textRight - S(16), iy, S(16), Theme.Primary);
                        textRight -= S(20);
                    }
                    if (textRight - textLeft > S(8))
                    {
                        var color = t.Sleeping ? Theme.Outline : Theme.OnSurface;
                        TextRenderer.DrawText(g, t.Title ?? "New tab", font,
                            new Rectangle(textLeft, r.Y, textRight - textLeft, r.Height - (sel ? 0 : S(4))), color,
                            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                    }
                    if (showClose)
                    {
                        var cr = CloseRect(r);
                        if (!sel) cr.Offset(0, -S(2));
                        if (i == hover && hoverClose)
                            using (var b = new SolidBrush(Theme.Pressed)) g.FillEllipse(b, cr);
                        Icons.Draw(g, IconData.Close, cr.X + S(2), cr.Y + S(2), S(16), Theme.OnSurfaceVariant);
                    }
                }
            }
            var pr = PlusRect();
            if (hoverPlus || pressPlus)
                using (var b = new SolidBrush(pressPlus ? Theme.Pressed : Theme.Hover)) g.FillEllipse(b, pr);
            Icons.Draw(g, IconData.Add, pr.X + S(5), pr.Y + S(5), S(20), Theme.OnSurfaceVariant);
        }

        int HitTab(Point p)
        {
            for (int i = 0; i < Tabs.Count; i++) if (TabRect(i).Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragIndex >= 0 && e.Button == MouseButtons.Left)
            {
                if (!dragging && Math.Abs(e.X - dragStart.X) > S(6)) dragging = true;
                if (dragging)
                {
                    int target = HitTab(new Point(e.X, TabRect(0).Y + S(4)));
                    if (target >= 0 && target != dragIndex)
                    {
                        TabMoved?.Invoke(dragIndex, target);
                        dragIndex = target;
                    }
                    return;
                }
            }
            int h = HitTab(e.Location);
            bool hc = h >= 0 && CloseRect(TabRect(h)).Contains(e.Location);
            bool hp = PlusRect().Contains(e.Location);
            if (h != hover || hc != hoverClose || hp != hoverPlus)
            {
                hover = h; hoverClose = hc; hoverPlus = hp;
                Invalidate();
                string text = hp ? "New tab (Ctrl+T)" : hc ? "Close tab (Ctrl+W)" : h >= 0 ? Tabs[h].TooltipText : null;
                if (text != tipText) { tipText = text; tip.SetToolTip(this, text); }
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hover = -1; hoverClose = hoverPlus = false; Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int h = HitTab(e.Location);
            if (e.Button == MouseButtons.Left)
            {
                if (PlusRect().Contains(e.Location)) { pressPlus = true; Invalidate(); return; }
                if (h >= 0 && !CloseRect(TabRect(h)).Contains(e.Location))
                {
                    TabSelected?.Invoke(h);
                    dragIndex = h; dragStart = e.Location; dragging = false;
                }
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            int h = HitTab(e.Location);
            if (e.Button == MouseButtons.Left)
            {
                if (pressPlus) { pressPlus = false; Invalidate(); if (PlusRect().Contains(e.Location)) NewTabClicked?.Invoke(); }
                else if (!dragging && h >= 0 && CloseRect(TabRect(h)).Contains(e.Location)) TabClosed?.Invoke(h);
            }
            else if (e.Button == MouseButtons.Middle && h >= 0) TabClosed?.Invoke(h);
            dragIndex = -1; dragging = false;
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && HitTab(e.Location) < 0 && !PlusRect().Contains(e.Location)) NewTabClicked?.Invoke();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { spin.Dispose(); tip.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
