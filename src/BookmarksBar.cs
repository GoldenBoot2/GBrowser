using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace GBrowser
{
    /// <summary>The bookmarks bar under the toolbar, like Chrome's: the "Bookmarks bar" folder's pages and folders (a
    /// folder opens as a menu), a menu for what does not fit, "Other bookmarks" at the right, right-click menus,
    /// drag to reorder (onto a folder = into it), links dropped from pages or the address bar become bookmarks, and
    /// Chrome's hint with "Import bookmarks now" while it is empty.</summary>
    internal sealed class BookmarksBar : Control
    {
        public event Action<string, bool> Open;          // url, new tab
        public event Action<string> OpenWindow;          // url in a new window
        public event Action<string> OpenPrivate;         // url in a private tab
        public event Action<Bookmark> Edit;              // edit dialog
        public event Action<long, int> AddPage;          // parent, index
        public event Action<long, int> AddFolder;        // parent, index
        public event Action Manager;                     // bookmarks page
        public event Action ImportNow;                   // bookmarks page, import part
        public event Action HideBar;

        sealed class Item { public Rectangle R; public Bookmark B; public bool Overflow, OtherRoot, Import; }
        readonly List<Item> items = new List<Item>();
        int hover = -1, pressed = -1;
        Point pressAt; bool dragging; int dropIndex = -1; long dropInto;
        readonly ToolTip tip = new ToolTip();
        string tipText;

        public BookmarksBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.ToolbarBack;
            AllowDrop = true;
        }

        float Sc => DeviceDpi / 96f;
        int S(float v) => (int)Math.Round(v * Sc);
        public int PreferredHeight => S(32);
        static readonly Font font = new Font("Segoe UI", 9f);

        static string Label(Bookmark b)
        {
            string t = string.IsNullOrEmpty(b.Title) ? b.Url : b.Title;
            return t.Length > 28 ? t.Substring(0, 27) + "..." : t;
        }

        int ItemWidth(Bookmark b) => TextRenderer.MeasureText(Label(b), font).Width + S(34);

        void Build()
        {
            items.Clear();
            var bar = Bm.Children(Bm.Bar);
            bool other = Bm.Children(Bm.Other).Count > 0;
            int right = Width - S(8);
            int otherW = other ? TextRenderer.MeasureText(Bm.RootName(Bm.Other), font).Width + S(34) : 0;
            int chevW = S(28);
            int x = S(8);
            if (bar.Count == 0)
            {
                // Chrome's hint and import link
                string hint = "For quick access, place your bookmarks here on the bookmarks bar.";
                int hw = TextRenderer.MeasureText(hint, font).Width;
                items.Add(new Item { R = new Rectangle(x, S(3), hw + S(8), Height - S(6)) });
                int lx = x + hw + S(12);
                int lw = TextRenderer.MeasureText("Import bookmarks now...", font).Width + S(16);
                items.Add(new Item { R = new Rectangle(lx, S(3), lw, Height - S(6)), Import = true });
            }
            else
            {
                int limit = right - (other ? otherW + S(8) : 0);
                for (int i = 0; i < bar.Count; i++)
                {
                    int w = ItemWidth(bar[i]);
                    bool last = i == bar.Count - 1;
                    if (x + w > (last ? limit : limit - chevW - S(4)))
                    {
                        items.Add(new Item { R = new Rectangle(x, S(3), chevW, Height - S(6)), Overflow = true });
                        break;
                    }
                    items.Add(new Item { R = new Rectangle(x, S(3), w, Height - S(6)), B = bar[i] });
                    x += w + S(4);
                }
            }
            if (other)
                items.Add(new Item { R = new Rectangle(right - otherW, S(3), otherW, Height - S(6)), OtherRoot = true });
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Build();
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i]; var r = it.R;
                if (it.B == null && !it.Overflow && !it.OtherRoot && !it.Import)
                {
                    TextRenderer.DrawText(g, "For quick access, place your bookmarks here on the bookmarks bar.", font, r, Theme.OnSurfaceVariant,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                    continue;
                }
                if ((i == hover && !dragging) || i == pressed && !dragging || (dragging && it.B != null && it.B.Folder && dropInto == it.B.Id))
                    using (var p = Theme.Round(r, r.Height / 2f)) using (var br = new SolidBrush(Theme.Hover)) g.FillPath(br, p);
                if (it.Import)
                {
                    TextRenderer.DrawText(g, "Import bookmarks now...", font, r, Theme.Primary,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                    continue;
                }
                if (it.Overflow) { Icons.Draw(g, IconData.DoubleRight, r.X + (r.Width - S(18)) / 2, r.Y + (r.Height - S(18)) / 2, S(18), Theme.OnSurfaceVariant); continue; }
                int iy = r.Y + (r.Height - S(16)) / 2;
                string text;
                if (it.OtherRoot) { Icons.Draw(g, IconData.FolderClosed, r.X + S(8), iy, S(16), Theme.OnSurfaceVariant); text = Bm.RootName(Bm.Other); }
                else if (it.B.Folder) { Icons.Draw(g, IconData.FolderClosed, r.X + S(8), iy, S(16), Theme.OnSurfaceVariant); text = Label(it.B); }
                else
                {
                    // the site's own icon once a visit has shown it (Favicons), else Chrome's default globe
                    var fav = Favicons.Image(it.B.Url);
                    if (fav != null) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(fav, r.X + S(8), iy, S(16), S(16)); }
                    else Icons.Draw(g, IconData.Public, r.X + S(8), iy, S(16), Theme.OnSurfaceVariant);
                    text = Label(it.B);
                }
                TextRenderer.DrawText(g, text, font, new Rectangle(r.X + S(28), r.Y, r.Width - S(30), r.Height), Theme.OnSurface,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            }
            // where a dragged bookmark or link lands
            if (dropIndex >= 0 && dropInto == 0)
            {
                var bars = items.Where(x => x.B != null).ToList();
                int lx = dropIndex < bars.Count ? bars[dropIndex].R.X - S(2) : bars.Count > 0 ? bars[bars.Count - 1].R.Right + S(2) : S(8);
                using (var pen = new Pen(Theme.Primary, S(2))) g.DrawLine(pen, lx, S(5), lx, Height - S(5));
            }
            using (var pen = new Pen(Theme.OutlineVariant, 1)) g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
        }

        int Hit(Point p) { for (int i = 0; i < items.Count; i++) if (items[i].R.Contains(p) && (items[i].B != null || items[i].Overflow || items[i].OtherRoot || items[i].Import)) return i; return -1; }

        /// <summary>Drop position among the bar's items for a point (and the folder it would go into, if over one's middle).</summary>
        void DropAt(Point p, long dragged)
        {
            var bars = items.Where(x => x.B != null).ToList();
            dropInto = 0; dropIndex = bars.Count;
            for (int i = 0; i < bars.Count; i++)
            {
                var r = bars[i].R;
                if (bars[i].B.Folder && bars[i].B.Id != dragged && p.X > r.X + r.Width / 4 && p.X < r.Right - r.Width / 4) { dropInto = bars[i].B.Id; dropIndex = i; return; }
                if (p.X < r.X + r.Width / 2) { dropIndex = i; return; }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (pressed >= 0 && e.Button == MouseButtons.Left && items[pressed].B != null)
            {
                if (!dragging && Math.Abs(e.X - pressAt.X) + Math.Abs(e.Y - pressAt.Y) > S(6)) dragging = true;
                if (dragging) { DropAt(e.Location, items[pressed].B.Id); Cursor = Cursors.SizeWE; Invalidate(); return; }
            }
            int h = Hit(e.Location);
            if (h != hover)
            {
                hover = h; Invalidate();
                string t = h < 0 ? null : items[h].Overflow ? "More bookmarks" : items[h].OtherRoot ? Bm.RootName(Bm.Other) : items[h].Import ? null :
                    items[h].B.Folder ? items[h].B.Title : items[h].B.Title + "\n" + items[h].B.Url;
                if (t != tipText) { tipText = t; tip.SetToolTip(this, t); }
            }
        }

        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { pressed = Hit(e.Location); pressAt = e.Location; dragging = false; }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (dragging)
            {
                var b = items[pressed].B;
                if (dropInto != 0) Bm.Move(b.Id, dropInto, -1);
                else
                {
                    // index among the bar's children (the dragged item itself is counted before it is taken out)
                    var sibs = Bm.Children(Bm.Bar);
                    int from = sibs.FindIndex(x => x.Id == b.Id), to = dropIndex;
                    if (from >= 0 && to > from) to--;
                    Bm.Move(b.Id, Bm.Bar, to);
                }
                dragging = false; pressed = -1; dropIndex = -1; dropInto = 0; Cursor = Cursors.Default; Invalidate();
                return;
            }
            int h = Hit(e.Location);
            pressed = -1;
            if (e.Button == MouseButtons.Right) { ShowContextMenu(h, e.Location); return; }
            if (h < 0) return;
            var it = items[h];
            if (it.Import) { ImportNow?.Invoke(); return; }
            if (it.Overflow) { ShowOverflow(it.R); return; }
            if (it.OtherRoot) { ShowFolder(Bm.Other, it.R); return; }
            if (it.B.Folder)
            {
                if (e.Button == MouseButtons.Middle) OpenAll(it.B.Id, true);
                else ShowFolder(it.B.Id, it.R);
                return;
            }
            if (e.Button == MouseButtons.Left) Open?.Invoke(it.B.Url, (ModifierKeys & Keys.Control) != 0);
            else if (e.Button == MouseButtons.Middle) Open?.Invoke(it.B.Url, true);
        }

        // ------------------------------------------------------------ menus

        void OpenAll(long folder, bool newTabs)
        {
            foreach (var b in Bm.Children(folder).Where(x => !x.Folder)) Open?.Invoke(b.Url, true);
        }

        void FillFolder(MaterialMenu m, IEnumerable<Bookmark> list, Rectangle anchor)
        {
            foreach (var b in list)
            {
                var bb = b;
                if (b.Folder) m.Add(Label(b), IconData.FolderClosed, null, () => BeginInvoke((Action)(() => ShowFolder(bb.Id, anchor))));
                else
                {
                    var en = m.Add(Label(b), null, null, () => Open?.Invoke(bb.Url, (ModifierKeys & Keys.Control) != 0));
                    var fav = Favicons.Image(b.Url);
                    if (fav != null) en.Image = fav; else en.Icon = IconData.Public;
                }
            }
        }

        void ShowFolder(long folder, Rectangle anchor)
        {
            var m = new MaterialMenu();
            var list = Bm.Children(folder);
            if (list.Count == 0) m.AddNote("(empty)");
            FillFolder(m, list, anchor);
            int pages = list.Count(x => !x.Folder);
            if (pages > 1)
            {
                m.AddSeparator();
                m.Add("Open all (" + pages + ")", IconData.OpenNew, null, () => OpenAll(folder, true));
            }
            m.ShowAt(this, anchor, false, "bmfolder" + folder);
        }

        void ShowOverflow(Rectangle anchor)
        {
            var shown = new HashSet<long>(items.Where(x => x.B != null).Select(x => x.B.Id));
            var m = new MaterialMenu();
            FillFolder(m, Bm.Children(Bm.Bar).Where(b => !shown.Contains(b.Id)), anchor);
            m.ShowAt(this, anchor, false, "bmoverflow");
        }

        void ShowContextMenu(int h, Point p)
        {
            var m = new MaterialMenu();
            var b = h >= 0 ? items[h].B : null;
            int index = b != null ? Bm.Children(Bm.Bar).FindIndex(x => x.Id == b.Id) + 1 : -1;
            if (b != null && !b.Folder)
            {
                m.Add("Open in new tab", IconData.Tab, null, () => Open?.Invoke(b.Url, true));
                m.Add("Open in new window", IconData.NewWindow, null, () => OpenWindow?.Invoke(b.Url));
                m.Add("Open in private tab", IconData.Private, null, () => OpenPrivate?.Invoke(b.Url));
                m.AddSeparator();
            }
            else if (b != null)
            {
                int pages = Bm.Children(b.Id).Count(x => !x.Folder);
                m.Add("Open all (" + pages + ")", IconData.OpenNew, null, () => OpenAll(b.Id, true)).Enabled = pages > 0;
                m.AddSeparator();
            }
            if (b != null)
            {
                m.Add("Edit...", IconData.Edit, null, () => Edit?.Invoke(b));
                m.Add("Delete", IconData.Delete, null, () => Bm.Remove(b.Id));
                m.AddSeparator();
            }
            m.Add("Add page...", IconData.BookmarkAdd, null, () => AddPage?.Invoke(Bm.Bar, index));
            m.Add("Add folder...", IconData.FolderNew, null, () => AddFolder?.Invoke(Bm.Bar, index));
            m.AddSeparator();
            m.Add("Bookmark manager", IconData.Bookmarks, "Ctrl+Shift+O", () => Manager?.Invoke());
            m.Add("Hide bookmarks bar", IconData.Star, "Ctrl+Shift+B", () => HideBar?.Invoke());
            m.ShowAt(this, new Rectangle(p.X, p.Y, 1, 1), false, null);
        }

        // ------------------------------------------------------------ links dropped from pages / the address bar

        static string DroppedUrl(IDataObject d, out string title)
        {
            title = null;
            try
            {
                if (d.GetDataPresent("text/x-moz-url"))
                {
                    var o = d.GetData("text/x-moz-url");
                    string s = o is System.IO.MemoryStream ms ? System.Text.Encoding.Unicode.GetString(ms.ToArray()).TrimEnd('\0') : o as string;
                    if (!string.IsNullOrEmpty(s))
                    {
                        var parts = s.Split('\n');
                        if (parts.Length > 1) title = parts[1].Trim();
                        if (Uri.TryCreate(parts[0].Trim(), UriKind.Absolute, out var u0) && (u0.Scheme == "http" || u0.Scheme == "https")) return u0.AbsoluteUri;
                    }
                }
                foreach (var f in new[] { "UniformResourceLocatorW", "UniformResourceLocator", DataFormats.UnicodeText, DataFormats.Text })
                {
                    if (!d.GetDataPresent(f)) continue;
                    var o = d.GetData(f);
                    string s = o is System.IO.MemoryStream ms ? (f.EndsWith("W") ? System.Text.Encoding.Unicode : System.Text.Encoding.Default).GetString(ms.ToArray()).TrimEnd('\0') : o as string;
                    s = s?.Trim();
                    if (!string.IsNullOrEmpty(s) && Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https")) return u.AbsoluteUri;
                }
            }
            catch { }
            return null;
        }

        protected override void OnDragOver(DragEventArgs e)
        {
            string url = DroppedUrl(e.Data, out _);
            e.Effect = url != null ? DragDropEffects.Copy | DragDropEffects.Link : DragDropEffects.None;
            if (url != null) { DropAt(PointToClient(new Point(e.X, e.Y)), 0); Invalidate(); }
        }

        protected override void OnDragLeave(EventArgs e) { dropIndex = -1; dropInto = 0; Invalidate(); }

        protected override void OnDragDrop(DragEventArgs e)
        {
            string url = DroppedUrl(e.Data, out var title);
            DropAt(PointToClient(new Point(e.X, e.Y)), 0);
            if (url != null)
            {
                if (dropInto != 0) Bm.Add(url, title, dropInto);
                else Bm.Add(url, title, Bm.Bar, dropIndex);
            }
            dropIndex = -1; dropInto = 0; Invalidate();
        }

#if AUTOMATION
        /// <summary>test: what the bar shows ("[folder] name | page | ... >> | Other")</summary>
        public string TestItems()
        {
            Build();
            return string.Join(" | ", items.Select(x => x.Import ? "<import link>" : x.Overflow ? ">>" : x.OtherRoot ? "(Other bookmarks)" :
                x.B == null ? "<hint>" : (x.B.Folder ? "[" + x.B.Title + "]" : x.B.Title)));
        }
        public void TestRightClick(int i) { Build(); var r = items[i].R; ShowContextMenu(i, new Point(r.X + 4, r.Y + 4)); }
        public void TestClick(int i) { Build(); var r = items[i].R; OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, r.X + r.Width / 2, r.Y + 4, 0)); }
        public void TestDrag(int from, int toIndex)
        {
            Build();
            var bars = items.Where(x => x.B != null).ToList();
            pressed = items.IndexOf(bars[from]); dragging = true; dropInto = 0; dropIndex = toIndex;
            OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0));
        }
        /// <summary>test: a link dropped at x (client) the way a page / the address bar drops it (text/x-moz-url + URL)</summary>
        public void TestDropUrl(string url, string title, int x)
        {
            var d = new DataObject();
            d.SetData("text/x-moz-url", new System.IO.MemoryStream(System.Text.Encoding.Unicode.GetBytes(url + "\n" + title)));
            d.SetData(DataFormats.UnicodeText, url);
            var p = PointToScreen(new Point(x, S(8)));
            OnDragDrop(new DragEventArgs(d, 0, p.X, p.Y, DragDropEffects.Copy | DragDropEffects.Link, DragDropEffects.Copy));
        }
#endif

        protected override void Dispose(bool disposing) { if (disposing) tip.Dispose(); base.Dispose(disposing); }
    }
}
