using System;
using System.Linq;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace GBrowser
{
    /// <summary>Owner-drawn tab row in the window's title bar (like Chrome): tab search button, tabs (select, close,
    /// middle-click, drag to reorder), new tab button, and the window's minimise / maximise / close buttons. Empty
    /// space and the window buttons are handed to the window (HTTRANSPARENT) so Windows moves the window, shows
    /// Snap Layouts on the maximise button and handles double-click to maximise.</summary>
    internal sealed class TabStrip : Control
    {
        public List<BrowserTab> Tabs = new List<BrowserTab>();
        public int Selected = -1;

        public event Action<int> TabSelected;
        public event Action<int> TabClosed;
        public event Action NewTabClicked;
        public event Action<int, int> TabMoved;
        public event Action TabSearchClicked;
        public event Action<int, Point> TabMenu;       // right-click on a tab (screen point)
        public event Action<int> TabMuteClicked;       // the speaker icon on a tab
        public event Action<int, Point> TabDraggedOut; // a tab pulled out of the tab row (screen point)
        // tab groups (like Chrome's): a coloured chip with the group's name before its tabs; click = collapse / expand
        public Dictionary<string, TabGroup> Groups = new Dictionary<string, TabGroup>();
        public event Action<string> GroupToggled;
        public event Action<string, Point> GroupMenu;  // right-click on a group's chip (screen point)

        /// <summary>Window state for the caption buttons (set by the window).</summary>
        public bool WindowMaximized;
        /// <summary>Window button under the mouse / pressed: 0 minimise, 1 maximise, 2 close, -1 none (set by the window).</summary>
        public int CaptionHover = -1, CaptionPressed = -1;
        bool hoverSearch;

        int hover = -1; bool hoverAudio, hoverClose, hoverPlus, pressPlus;
        int dragIndex = -1; Point dragStart; bool dragging;
        readonly Timer spin = new Timer { Interval = 50 };
        float spinAngle;
        readonly ToolTip tip = new ToolTip();
        // Chrome's tab hover card (title, site, notes) instead of a tooltip for the tab itself
        HoverCard card;
        readonly Timer cardTimer = new Timer { Interval = 500 };
        int cardTab = -1;

        void ShowCard(int i)
        {
            if (i < 0 || i >= Tabs.Count || FindForm() == null) { HideCard(); return; }
            if (card == null || card.IsDisposed) card = new HoverCard();
            cardTab = i;
            card.ShowFor(FindForm(), Tabs[i], RectangleToScreen(TabRect(i)));
        }

#if AUTOMATION
        public void TestCard(int i) { if (i < 0) HideCard(); else ShowCard(i); }
        public IntPtr CardHandle => card != null && !card.IsDisposed && card.Visible ? card.Handle : IntPtr.Zero;
#endif
        void HideCard()
        {
            cardTimer.Stop();
            cardTab = -1;
            if (card != null && !card.IsDisposed && card.Visible) card.Hide();
        }
        string tipText;

        public TabStrip()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.SurfaceContainerHigh;
            cardTimer.Tick += (o, e) =>
            {
                cardTimer.Stop();
                // still resting on that tab, with the window active (no card over other apps)
                if (cardTab >= 0 && cardTab == hover && Form.ActiveForm == FindForm()) ShowCard(cardTab);
            };
            spin.Tick += (s, e) => { spinAngle = (spinAngle + 30) % 360; Invalidate(); };
        }

        float Sc => DeviceDpi / 96f;
        int S(float v) => (int)Math.Round(v * Sc);

        public int PreferredHeight => S(40);

        int TopPad => WindowMaximized ? S(2) : S(6);
        public Rectangle SearchRect => new Rectangle(S(8), TopPad + (Height - TopPad - S(30)) / 2, S(30), S(30));
        int CaptionWidth => S(46);
        public Rectangle CaptionRect(int i) => new Rectangle(Width - (3 - i) * CaptionWidth, 0, CaptionWidth, Height);

        int PinnedW => S(44);

        Rectangle TabRect(int i)
        {
            var l = Layout();
            return i >= 0 && i < l.Item1.Length ? l.Item1[i] : Rectangle.Empty;
        }

        /// <summary>Tab rectangles (empty = in a collapsed group) and the group chips, left to right. Pinned tabs are
        /// narrow at the left; the others share the rest of the row with the chips of their groups.</summary>
        Tuple<Rectangle[], List<Tuple<string, Rectangle>>> Layout()
        {
            var rects = new Rectangle[Tabs.Count];
            var chips = new List<Tuple<string, Rectangle>>();
            int left = SearchRect.Right + S(6), plus = S(44);
            int pinned = Tabs.Count(t => t.Pinned);
            for (int i = 0; i < pinned; i++) rects[i] = new Rectangle(left + i * PinnedW, TopPad, PinnedW, Height - TopPad);
            left += pinned * PinnedW;
            // chip widths and the visible tabs
            var chipW = new Dictionary<string, int>();
            int chipsTotal = 0, visible = 0;
            string prev = null;
            for (int i = pinned; i < Tabs.Count; i++)
            {
                string gid = Tabs[i].Group;
                TabGroup grp = null;
                if (gid != null && !Groups.TryGetValue(gid, out grp)) gid = null;
                if (gid != null && gid != prev && !chipW.ContainsKey(gid))
                {
                    int w = string.IsNullOrEmpty(grp.Name) ? S(24) : Math.Min(S(160), TextRenderer.MeasureText(grp.Name, ChipFont).Width + S(20));
                    chipW[gid] = w; chipsTotal += w + S(6);
                }
                if (gid == null || !grp.Collapsed) visible++;
                prev = gid;
            }
            int avail = Width - left - plus - 3 * CaptionWidth - S(8) - chipsTotal;
            int tw = Math.Min(S(240), Math.Max(S(56), avail / Math.Max(1, visible)));
            int x = left;
            prev = null;
            var placed = new HashSet<string>();
            for (int i = pinned; i < Tabs.Count; i++)
            {
                string gid = Tabs[i].Group;
                TabGroup grp = null;
                if (gid != null && !Groups.TryGetValue(gid, out grp)) gid = null;
                if (gid != null && gid != prev && placed.Add(gid))
                {
                    int w = chipW[gid];
                    chips.Add(Tuple.Create(gid, new Rectangle(x + S(3), TopPad + (Height - TopPad - S(24)) / 2 - S(2), w, S(24))));
                    x += w + S(6);
                }
                if (gid != null && grp.Collapsed) rects[i] = Rectangle.Empty;
                else { rects[i] = new Rectangle(x, TopPad, tw, Height - TopPad); x += tw; }
                prev = gid;
            }
            return Tuple.Create(rects, chips);
        }

        static readonly Font ChipFont = new Font("Segoe UI Semibold", 9f);

        /// <summary>The group whose chip is at p, or null.</summary>
        string ChipAt(Point p)
        {
            foreach (var c in Layout().Item2) if (c.Item2.Contains(p)) return c.Item1;
            return null;
        }

        /// <summary>Chrome's tab group colours (light / dark theme).</summary>
        public static readonly string[] GroupColorNames = { "Grey", "Blue", "Red", "Yellow", "Green", "Pink", "Purple", "Cyan", "Orange" };
        static readonly int[] GroupLight = { 0x5F6368, 0x1A73E8, 0xD93025, 0xF9AB00, 0x1E8E3E, 0xD01884, 0xA142F4, 0x007B83, 0xFA903E };
        static readonly int[] GroupDark = { 0xDADCE0, 0x8AB4F8, 0xF28B82, 0xFDD663, 0x81C995, 0xFF8BCB, 0xC58AF9, 0x78D9EC, 0xFCAD70 };
        public static Color GroupColor(int c)
        {
            int v = (Theme.Dark ? GroupDark : GroupLight)[Math.Max(0, Math.Min(8, c))];
            return Color.FromArgb((v >> 16) & 255, (v >> 8) & 255, v & 255);
        }

        /// <summary>The speaker icon of a tab that plays sound or is muted (click = mute / unmute).</summary>
        Rectangle AudioRect(int i)
        {
            var t = Tabs[i];
            if (!(t.PlayingAudio || t.Muted) || t.Pinned) return Rectangle.Empty;
            var r = TabRect(i);
            bool sel = i == Selected, narrow = r.Width < S(90);
            bool showClose = sel || i == hover || !narrow;
            int right = showClose ? CloseRect(r).X - S(4) : r.Right - S(10);
            int iy = r.Y + (r.Height - S(16)) / 2 - (sel ? 0 : S(2));
            return new Rectangle(right - S(18), iy - S(1), S(18), S(18));
        }

        /// <summary>0..2 = window button at p, -1 = none.</summary>
        public int CaptionAt(Point p)
        {
            for (int i = 0; i < 3; i++) if (CaptionRect(i).Contains(p)) return i;
            return -1;
        }

        /// <summary>Empty tab-row space (drags the window).</summary>
        public bool IsDragArea(Point p) =>
            CaptionAt(p) < 0 && HitTab(p) < 0 && ChipAt(p) == null && !PlusRect().Contains(p) && !SearchRect.Contains(p);

        protected override void WndProc(ref Message m)
        {
            // WM_NCHITTEST: window buttons and empty space belong to the window, not to this control
            if (m.Msg == 0x0084)
            {
                long lp = m.LParam.ToInt64();
                var p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                if (CaptionAt(p) >= 0 || IsDragArea(p)) { m.Result = (IntPtr)(-1); return; }   // HTTRANSPARENT
            }
            base.WndProc(ref m);
        }

        Rectangle CloseRect(Rectangle tr) => new Rectangle(tr.Right - S(30), tr.Y + (tr.Height - S(20)) / 2, S(20), S(20));

        Rectangle PlusRect()
        {
            var lay = Layout();
            int right = SearchRect.Right;
            foreach (var r in lay.Item1) if (!r.IsEmpty) right = Math.Max(right, r.Right);
            foreach (var c in lay.Item2) right = Math.Max(right, c.Item2.Right + S(3));
            return new Rectangle(right + S(6), S(6) + (Height - S(6) - S(30)) / 2, S(30), S(30));
        }

        public void RefreshTabs()
        {
            // a group without tabs is gone (closed, moved away, ungrouped)
            foreach (var k in Groups.Keys.Where(k => !Tabs.Any(t => t.Group == k)).ToList()) Groups.Remove(k);
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
                    if (r.IsEmpty) continue;   // in a collapsed group
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
                    int ix = t.Pinned ? r.X + (r.Width - S(16)) / 2 : r.X + S(12), iy = r.Y + (r.Height - S(16)) / 2 - (sel ? 0 : S(2));
                    bool narrow = r.Width < S(90);
                    bool showClose = !t.Pinned && (sel || i == hover || !narrow);
                    if (t.Loading)
                    {
                        using (var pen = new Pen(Theme.Primary, S(2)))
                            g.DrawArc(pen, ix + S(1), iy + S(1), S(14), S(14), spinAngle, 270);
                    }
                    else if (t.Private) Icons.Draw(g, IconData.Private, ix, iy, S(16), Theme.Primary);
                    else if (t.Favicon != null)
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(t.Favicon, ix, iy, S(16), S(16));
                    }
                    else if (!t.Created && t.Url != null && Favicons.Image(t.Url) is Image remembered)
                        { g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic; g.DrawImage(remembered, ix, iy, S(16), S(16)); }   // a tab not loaded yet: the site's remembered icon (like Chrome)
                    else Icons.Draw(g, IconData.Public, ix, iy, S(16), Theme.OnSurfaceVariant);
                    int textLeft = ix + S(24);
                    int textRight = showClose ? CloseRect(r).X - S(4) : r.Right - S(10);
                    // (the blocked count is shown on the toolbar's shield, not on tabs)
                    if (t.Pinned)
                    {
                        // a pinned tab shows only its icon; a small dot when it plays sound / is muted
                        if (t.PlayingAudio || t.Muted)
                            using (var b = new SolidBrush(t.Muted ? Theme.Outline : Theme.Primary)) g.FillEllipse(b, r.Right - S(12), r.Y + S(8), S(6), S(6));
                        continue;
                    }
                    // camera / microphone in use (like Chrome's recording indicator), left of the speaker
                    if (t.Blocking != null && (t.Blocking.Camera > 0 || t.Blocking.Microphone > 0))
                    {
                        var ar0 = AudioRect(i);
                        int cx = (ar0.IsEmpty ? textRight : ar0.X - S(2)) - S(18);
                        Icons.Draw(g, t.Blocking.Camera > 0 ? IconData.Videocam : IconData.Mic, cx + S(1), r.Y + (r.Height - S(16)) / 2 - (sel ? 0 : S(2)), S(16), Theme.Error);
                        if (ar0.IsEmpty) textRight = cx - S(4);
                    }
                    if (t.PlayingAudio || t.Muted)
                    {
                        var ar = AudioRect(i);
                        if (i == hover && hoverAudio) using (var b = new SolidBrush(Theme.Pressed)) g.FillEllipse(b, ar);
                        Icons.Draw(g, t.Muted ? IconData.VolumeOff : IconData.Volume, ar.X + S(1), ar.Y + S(1), S(16), t.Muted ? Theme.OnSurfaceVariant : Theme.Primary);
                        textRight = ar.X - S(4);
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
            // group chips and the coloured line under each group's tabs
            {
                var lay = Layout();
                foreach (var c in lay.Item2)
                {
                    var grp = Groups[c.Item1];
                    var col = GroupColor(grp.Color);
                    var cr = c.Item2;
                    int lineRight = cr.Right;
                    for (int i = 0; i < Tabs.Count; i++) if (Tabs[i].Group == c.Item1 && !lay.Item1[i].IsEmpty) lineRight = Math.Max(lineRight, lay.Item1[i].Right - S(4));
                    using (var b = new SolidBrush(col)) g.FillRectangle(b, cr.X, Height - S(3), lineRight - cr.X, S(3));
                    using (var path = Theme.Round(cr, S(8))) using (var b = new SolidBrush(col)) g.FillPath(b, path);
                    if (!string.IsNullOrEmpty(grp.Name))
                    {
                        var fg = Theme.Dark ? Color.FromArgb(0x20, 0x21, 0x24) : Color.White;
                        TextRenderer.DrawText(g, grp.Name, ChipFont, cr, fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                    }
                    else using (var b = new SolidBrush(Theme.Dark ? Color.FromArgb(0x20, 0x21, 0x24) : Color.White)) g.FillEllipse(b, cr.X + cr.Width / 2 - S(4), cr.Y + cr.Height / 2 - S(4), S(8), S(8));
                }
            }
            // tab search (left)
            var sr = SearchRect;
            using (var p = Theme.Round(sr, S(8)))
            using (var b = new SolidBrush(hoverSearch ? Theme.SurfaceContainerHighest : Theme.SurfaceContainer)) g.FillPath(b, p);
            Icons.Draw(g, IconData.ExpandMore, sr.X + S(5), sr.Y + S(5), S(20), Theme.OnSurfaceVariant);
            // window buttons (right): minimise, maximise / restore, close
            string[] cap = { IconData.WinMin, WindowMaximized ? IconData.WinRestore : IconData.WinMax, IconData.WinClose };
            for (int i = 0; i < 3; i++)
            {
                var cr = CaptionRect(i);
                bool hot = CaptionHover == i, down = CaptionPressed == i;
                Color fg = Theme.OnSurface;
                if (i == 2 && (hot || down))
                {
                    using (var b = new SolidBrush(down ? Color.FromArgb(0x94, 0x1F, 0x14) : Color.FromArgb(0xC4, 0x2B, 0x1C))) g.FillRectangle(b, cr);
                    fg = Color.White;
                }
                else if (hot || down)
                    using (var b = new SolidBrush(down ? Theme.Pressed : Theme.Hover)) g.FillRectangle(b, cr);
                // every glyph scaled to the same visible 10 px (like Chrome), whatever padding the icon file has
                Icons.DrawFit(g, cap[i], cr.X + cr.Width / 2f, cr.Y + cr.Height / 2f, S(10), fg);
            }
            var pr = PlusRect();
            if (hoverPlus || pressPlus)
                using (var b = new SolidBrush(pressPlus ? Theme.Pressed : Theme.Hover)) g.FillEllipse(b, pr);
            Icons.Draw(g, IconData.Add, pr.X + S(5), pr.Y + S(5), S(20), Theme.OnSurfaceVariant);
        }

        int HitTab(Point p)
        {
            var l = Layout().Item1;
            for (int i = 0; i < l.Length; i++) if (!l[i].IsEmpty && l[i].Contains(p)) return i;
            return -1;
        }

        public Rectangle TabRectPublic(int i) => Tabs.Count == 0 ? Rectangle.Empty : TabRect(Math.Max(0, Math.Min(i, Tabs.Count - 1)));

        /// <summary>Where a tab dropped at p goes (index between tabs).</summary>
        public int IndexAt(Point p)
        {
            for (int i = 0; i < Tabs.Count; i++) { var r = TabRect(i); if (!r.IsEmpty && p.X < r.X + r.Width / 2) return i; }
            return Tabs.Count;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragIndex >= 0 && e.Button == MouseButtons.Left)
            {
                if (!dragging && Math.Abs(e.X - dragStart.X) > S(6)) dragging = true;
                if (dragging)
                {
                    // pulled away from the tab row (like Chrome): the tab leaves for its own window
                    if (e.Y < -S(30) || e.Y > Height + S(30))
                    {
                        int i = dragIndex;
                        dragIndex = -1; dragging = false; Capture = false;
                        TabDraggedOut?.Invoke(i, PointToScreen(e.Location));
                        return;
                    }
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
            bool hc = h >= 0 && !Tabs[h].Pinned && CloseRect(TabRect(h)).Contains(e.Location);
            bool ha = h >= 0 && AudioRect(h).Contains(e.Location);
            if (ha != hoverAudio) { hoverAudio = ha; Invalidate(); }
            bool hp = PlusRect().Contains(e.Location);
            bool hs = SearchRect.Contains(e.Location);
            if (h != hover || hc != hoverClose || hp != hoverPlus || hs != hoverSearch)
            {
                hover = h; hoverClose = hc; hoverPlus = hp; hoverSearch = hs;
                Invalidate();
                string text = hs ? "Search tabs (Ctrl+Shift+A)" : hp ? "New tab (Ctrl+T)" : hc ? "Close tab (Ctrl+W)" :
                    ha ? (Tabs[h].Muted ? "Unmute tab" : "Mute tab") : null;
                if (text != tipText) { tipText = text; tip.SetToolTip(this, text); }
                // the hover card: after a short rest on a tab; once one shows, it follows the pointer from tab to tab
                bool onBody = h >= 0 && !hc && !ha;
                if (!onBody) HideCard();
                else if (card != null && !card.IsDisposed && card.Visible) { if (h != cardTab) ShowCard(h); }
                else { cardTab = h; cardTimer.Stop(); cardTimer.Start(); }
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            HideCard();
            hover = -1; hoverClose = hoverPlus = hoverSearch = false; Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            HideCard();
            int h = HitTab(e.Location);
            string chip = h < 0 ? ChipAt(e.Location) : null;
            if (chip != null)
            {
                if (e.Button == MouseButtons.Left) GroupToggled?.Invoke(chip);
                else if (e.Button == MouseButtons.Right) GroupMenu?.Invoke(chip, PointToScreen(e.Location));
                return;
            }
            if (e.Button == MouseButtons.Left)
            {
                if (PlusRect().Contains(e.Location)) { pressPlus = true; Invalidate(); return; }
                if (SearchRect.Contains(e.Location)) { TabSearchClicked?.Invoke(); return; }
                if (h >= 0 && AudioRect(h).Contains(e.Location)) { TabMuteClicked?.Invoke(h); return; }
                if (h >= 0 && (Tabs[h].Pinned || !CloseRect(TabRect(h)).Contains(e.Location)))
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
                else if (!dragging && h >= 0 && !Tabs[h].Pinned && CloseRect(TabRect(h)).Contains(e.Location)) TabClosed?.Invoke(h);
            }
            else if (e.Button == MouseButtons.Right && h >= 0) TabMenu?.Invoke(h, PointToScreen(e.Location));
            else if (e.Button == MouseButtons.Middle && h >= 0) TabClosed?.Invoke(h);
            dragIndex = -1; dragging = false;
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            // (double-click on empty space maximises the window: Windows handles it, the area is HTCAPTION)
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { spin.Dispose(); tip.Dispose(); cardTimer.Dispose(); card?.Dispose(); }
            base.Dispose(disposing);
        }
    }
}

namespace GBrowser
{
    /// <summary>A tab group of a window (name, Chrome colour index 0-8, collapsed).</summary>
    public class TabGroup
    {
        public string Name { get; set; } = "";
        public int Color { get; set; }
        public bool Collapsed { get; set; }
    }
}
