using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GBrowser
{
    /// <summary>Address bar suggestions like Chrome's omnibox: while typing, a list under the address bar with
    /// "what you typed" first (search or open), then history (ranked by visits and recency), bookmarks and open tabs
    /// ("Switch to tab"), then the search engine's suggestions. Inline completion of visited sites (typing "you"
    /// completes "youtube.com"). Up/Down choose, Enter opens, Esc closes, Shift+Delete removes a history entry.
    /// The list never takes focus from the address bar.</summary>
    internal sealed class Omnibox
    {
        public enum Kind { Search, Url, History, Bookmark, Tab, Suggestion }

        public sealed class Row
        {
            public Kind Kind;
            public string Title, Url, Text;   // Text = what Enter uses (an address, or search words)
            public BrowserTab Tab;
        }

        readonly MaterialTextBox box;
        readonly Control anchor;
        readonly Func<Rectangle> pillOnScreen;
        readonly Func<IEnumerable<BrowserTab>> tabs;
        readonly Func<BrowserTab> current;
        public event Action<string, bool> Go;          // text, new tab
        public event Action<BrowserTab> SwitchTab;
        readonly Popup popup;
        readonly List<Row> rows = new List<Row>();
        int selected = -1;
        string typed = "";        // what the user typed (without the inline completion)
        bool setting;            // GBrowser is changing the text itself
        CancellationTokenSource suggestCts;
        static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

        public Omnibox(MaterialTextBox box, Control anchor, Func<Rectangle> pillOnScreen, Func<IEnumerable<BrowserTab>> tabs, Func<BrowserTab> current)
        {
            this.box = box; this.anchor = anchor; this.pillOnScreen = pillOnScreen; this.tabs = tabs; this.current = current;
            popup = new Popup(this);
            box.UserEdited += OnUserEdited;
            box.KeyDown += OnKeyDown;
            box.LostFocus += (s, e) => Close();
        }

        public bool IsOpen => popup.Visible;
        public string Describe() => (popup.Visible ? "open" : "closed") + " sel=" + selected + " | " +
            string.Join(" | ", rows.Select(r => r.Kind + ":" + r.Title + (r.Url != null ? " <" + Short(r.Url) + ">" : "")));

        public void Close()
        {
            suggestCts?.Cancel();
            if (popup.Visible) popup.Hide();
            rows.Clear();
            selected = -1;
        }

        // ------------------------------------------------------------ typing

        void OnUserEdited(bool addedAtEnd)
        {
            if (setting) return;
            typed = box.Text;
            if (typed.Trim().Length == 0) { Close(); return; }
            string completion = addedAtEnd && box.SelectionLength == 0 ? InlineCompletion(typed) : null;
            if (completion != null)
            {
                setting = true;
                box.Text = typed + completion;
                box.Select(typed.Length, completion.Length);
                setting = false;
            }
            Build(completion != null ? typed + completion : null);
            FetchSuggestions(typed);
        }

        /// <summary>A visited site whose address (without https:// and www.) starts with what was typed.</summary>
        string InlineCompletion(string t)
        {
            string q = t.ToLowerInvariant();
            if (q.Contains(" ") || q.Length < 1) return null;
            foreach (var h in Visits().OrderByDescending(v => v.Score))
            {
                string disp = Short(h.Url);
                // complete to the site first (bbc.com), the full address only when that is what was typed towards
                string host = disp.Split('/')[0];
                if (host.StartsWith(q, StringComparison.Ordinal) && host.Length > q.Length) return host.Substring(q.Length);
                if (disp.StartsWith(q, StringComparison.Ordinal) && disp.Length > q.Length && q.Contains("/")) return disp.Substring(q.Length);
            }
            return null;
        }

        static string Short(string url)
        {
            string s = url;
            int i = s.IndexOf("://", StringComparison.Ordinal);
            if (i >= 0) s = s.Substring(i + 3);
            if (s.StartsWith("www.")) s = s.Substring(4);
            if (s.EndsWith("/")) s = s.Substring(0, s.Length - 1);
            return s.ToLowerInvariant();
        }

        sealed class Visit { public string Url, Title; public int Count; public long Last; public bool Bookmark; public double Score; }

        /// <summary>History grouped by address, with bookmarks; scored like a frecency (visits + how recent).</summary>
        List<Visit> Visits()
        {
            var map = new Dictionary<string, Visit>(StringComparer.Ordinal);
            long now = Store.Now();
            foreach (var h in Store.History)
            {
                if (string.IsNullOrEmpty(h.Url) || h.Url.StartsWith("about:")) continue;
                if (!map.TryGetValue(h.Url, out var v)) map[h.Url] = v = new Visit { Url = h.Url, Title = h.Title };
                v.Count++;
                if (h.Time > v.Last) { v.Last = h.Time; if (!string.IsNullOrEmpty(h.Title)) v.Title = h.Title; }
            }
            foreach (var b in Store.Bookmarks.Where(x => !x.Folder && !string.IsNullOrEmpty(x.Url)))
            {
                if (!map.TryGetValue(b.Url, out var v)) map[b.Url] = v = new Visit { Url = b.Url, Title = b.Title };
                v.Bookmark = true;
            }
            foreach (var v in map.Values)
            {
                double days = v.Last == 0 ? 365 : (now - v.Last) / 86400000.0;
                v.Score = Math.Min(v.Count, 30) * 10 + (days < 1 ? 60 : days < 7 ? 40 : days < 30 ? 20 : 0) + (v.Bookmark ? 50 : 0)
                    + (Short(v.Url).IndexOf('/') < 0 ? 30 : 0);   // a site's front page before its inner pages
            }
            return map.Values.ToList();
        }

        void Build(string completed)
        {
            rows.Clear();
            string t = typed.Trim();
            string q = t.ToLowerInvariant();
            // 1) what was typed (or its inline completion): open or search
            string first = completed ?? t;
            string asUrl = Search.ToUrl(first);
            bool isSearch = asUrl != null && Search.IsSearchUrl(asUrl);
            rows.Add(isSearch
                ? new Row { Kind = Kind.Search, Title = first, Text = first }
                : new Row { Kind = Kind.Url, Title = Short(asUrl ?? first), Url = asUrl, Text = first });
            // 2) open tabs, history and bookmarks that match every word
            var words = q.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            bool Hit(string url, string title)
            {
                string u = Short(url), ti = (title ?? "").ToLowerInvariant();
                return words.All(w => u.Contains(w) || ti.Contains(w));
            }
            double Rank(Visit v)
            {
                string u = Short(v.Url), host = u.Split('/')[0];
                double s = v.Score;
                if (host.StartsWith(q)) s += 1000; else if (u.StartsWith(q)) s += 800;
                else if ((v.Title ?? "").ToLowerInvariant().Split(' ', '-', '|', ':').Any(w => w.StartsWith(words[0]))) s += 300;
                return s;
            }
            var cur = current();
            foreach (var tab in tabs().Where(x => x != cur && !x.Private && x.Url != null && !Pages.IsInternal(x.Url)))
            {
                string u = tab.Created ? tab.View.CoreWebView2.Source : tab.Url;
                if (Hit(u, tab.Title) && rows.Count(r => r.Kind == Kind.Tab) < 2)
                    rows.Add(new Row { Kind = Kind.Tab, Title = tab.Title ?? Short(u), Url = u, Text = u, Tab = tab });
            }
            foreach (var v in Visits().Where(v => Hit(v.Url, v.Title)).OrderByDescending(Rank).Take(6))
            {
                if (rows.Any(r => r.Url == v.Url)) continue;
                rows.Add(new Row { Kind = v.Bookmark ? Kind.Bookmark : Kind.History, Title = string.IsNullOrEmpty(v.Title) ? Short(v.Url) : v.Title, Url = v.Url, Text = v.Url });
            }
            // the first row and a history / bookmark entry for the same address become one row (with the page title)
            if (rows[0].Kind == Kind.Url && rows[0].Url != null)
            {
                var same = rows.Skip(1).FirstOrDefault(r => (r.Kind == Kind.History || r.Kind == Kind.Bookmark) && Short(r.Url) == Short(rows[0].Url));
                if (same != null) { rows.Remove(same); same.Text = rows[0].Text; rows[0] = same; }
            }
            selected = 0;
            Show();
        }

        // ------------------------------------------------------------ search suggestions

        static readonly Dictionary<string, string> SuggestUrls = new Dictionary<string, string>
        {
            // OpenSearch suggestion services (checked 19-11-2026: all answer ["q", [suggestions...]])
            { "google", "https://suggestqueries.google.com/complete/search?client=firefox&q=" },
            { "bing", "https://api.bing.com/osjson.aspx?query=" },
            { "duckduckgo", "https://duckduckgo.com/ac/?type=list&q=" },
            { "brave", "https://search.brave.com/api/suggest?q=" },
        };

        void FetchSuggestions(string text)
        {
            suggestCts?.Cancel();
            if (!Store.Settings.SearchSuggestions || text.Trim().Length == 0) return;
            if (!SuggestUrls.TryGetValue(Store.Settings.SearchEngine ?? "google", out var baseUrl)) return;
            var t = text.Trim();
            if (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || t.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
            var cts = suggestCts = new CancellationTokenSource();
            var ui = SynchronizationContext.Current;
            Task.Run(async () =>
            {
                await Task.Delay(120, cts.Token);   // only after a short pause in typing
                using (var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + Uri.EscapeDataString(t)))
                {
                    req.Headers.TryAddWithoutValidation("User-Agent", "GBrowser/" + Updater.Current);
                    using (var res = await http.SendAsync(req, cts.Token))
                    {
                        if (!res.IsSuccessStatusCode) return null;
                        var arr = Store.FromJson<object[]>(await res.Content.ReadAsStringAsync());
                        return arr.Length > 1 && arr[1] is System.Collections.IEnumerable list ? list.Cast<object>().OfType<string>().ToList() : null;
                    }
                }
            }, cts.Token).ContinueWith(task =>
            {
                if (cts.IsCancellationRequested || task.Status != TaskStatus.RanToCompletion || task.Result == null) return;
                ui.Post(_ => AddSuggestions(t, task.Result), null);
            });
        }

        void AddSuggestions(string forText, List<string> list)
        {
            if (!popup.Visible || typed.Trim() != forText) return;
            rows.RemoveAll(r => r.Kind == Kind.Suggestion);
            foreach (var s in list.Where(s => !string.Equals(s, forText, StringComparison.OrdinalIgnoreCase)).Take(5))
                rows.Add(new Row { Kind = Kind.Suggestion, Title = s, Text = s });
            Show();
        }

        // ------------------------------------------------------------ keys

        void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (!popup.Visible || rows.Count == 0) return;
            if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up)
            {
                e.Handled = e.SuppressKeyPress = true;
                selected = (selected + (e.KeyCode == Keys.Down ? 1 : rows.Count - 1)) % rows.Count;
                Preview();
            }
            else if (e.KeyCode == Keys.Delete && e.Shift && selected >= 0 && rows[selected].Kind == Kind.History)
            {
                // remove this address from history (like Chrome's Shift+Delete)
                e.Handled = e.SuppressKeyPress = true;
                string url = rows[selected].Url;
                Store.History.RemoveAll(h => h.Url == url);
                Store.SaveHistory();
                rows.RemoveAt(selected);
                selected = Math.Min(selected, rows.Count - 1);
                popup.Invalidate();
                popup.Relayout();
            }
        }

        /// <summary>The chosen row shows in the address bar (like Chrome).</summary>
        void Preview()
        {
            var r = rows[selected];
            setting = true;
            box.Text = r.Kind == Kind.Search || r.Kind == Kind.Suggestion || selected == 0 ? r.Text : Short(r.Url);
            box.Select(box.TextLength, 0);
            setting = false;
            popup.Invalidate();
        }

        /// <summary>Enter in the address bar: true when the list handled it.</summary>
        public bool Enter(bool newTab)
        {
            if (!popup.Visible || selected < 0 || selected >= rows.Count) return false;
            Activate(selected, newTab);
            return true;
        }

        /// <summary>Esc with a suggestion picked by arrow keys: closes the list and puts back what was typed. Without one
        /// it only closes the list and returns false, so the address bar goes back to the page's address at once (like
        /// Chrome's OmniboxEditModel::OnEscapeKeyPressed).</summary>
        public bool Escape()
        {
            if (!popup.Visible) return false;
            if (box.Text == typed) { Close(); return false; }
            setting = true;
            box.Text = typed;
            box.Select(typed.Length, 0);
            setting = false;
            Close();
            return true;
        }

        void Activate(int i, bool newTab)
        {
            var r = rows[i];
            Close();
            if (r.Kind == Kind.Tab && r.Tab != null && !newTab) { SwitchTab?.Invoke(r.Tab); return; }
            Go?.Invoke(r.Kind == Kind.History || r.Kind == Kind.Bookmark || r.Kind == Kind.Tab ? r.Url : r.Text, newTab);
        }

        void Show()
        {
            if (!box.Focused || rows.Count == 0) { Close(); return; }
            popup.Relayout();
            if (!popup.Visible) popup.Show(anchor.FindForm());
            popup.Invalidate();
        }

        // ------------------------------------------------------------ the list window

        sealed class Popup : Form
        {
            readonly Omnibox o;
            int hover = -1;
            readonly Font font = new Font("Segoe UI", 10f), small = new Font("Segoe UI", 9f);

            public Popup(Omnibox o)
            {
                this.o = o;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            }

            int S(float v) => (int)Math.Round(v * DeviceDpi / 96f);
            int RowH => S(40);

            public void Relayout()
            {
                var pill = o.pillOnScreen();
                Bounds = new Rectangle(pill.X, pill.Bottom + S(4), pill.Width, S(8) * 2 + o.rows.Count * RowH);
            }

            protected override bool ShowWithoutActivation => true;

            protected override CreateParams CreateParams
            {
                get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x00000080; cp.ClassStyle |= 0x00020000; return cp; }
            }

            [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int s);

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                int round = 2; try { DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { }
                int border = ColorTranslator.ToWin32(Theme.OutlineVariant); try { DwmSetWindowAttribute(Handle, 34, ref border, 4); } catch { }
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; }   // WM_MOUSEACTIVATE -> MA_NOACTIVATE: the address bar keeps the focus
                base.WndProc(ref m);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(Theme.SurfaceContainerHigh);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                int y = S(8);
                for (int i = 0; i < o.rows.Count; i++, y += RowH)
                {
                    var r = o.rows[i];
                    var rect = new Rectangle(0, y, Width, RowH);
                    if (i == o.selected || i == hover)
                        using (var b = new SolidBrush(i == o.selected ? Theme.SurfaceContainerHighest : Theme.Hover)) g.FillRectangle(b, rect);
                    // icon: the site's own (history / bookmark / tab), else Material search / globe
                    int ix = S(20), iy = y + (RowH - S(20)) / 2;
                    Image fav = r.Url != null && (r.Kind == Kind.History || r.Kind == Kind.Bookmark || r.Kind == Kind.Tab) ? Favicons.Image(r.Url) : null;
                    if (fav != null) g.DrawImage(fav, ix + S(2), iy + S(2), S(16), S(16));
                    else Icons.Draw(g, r.Kind == Kind.Search || r.Kind == Kind.Suggestion ? IconData.Search : r.Kind == Kind.Bookmark ? IconData.Star : IconData.Public, ix, iy, S(20), Theme.OnSurfaceVariant);
                    int tx = S(56), right = Width - S(16);
                    if (r.Kind == Kind.Tab)
                    {
                        // "Switch to this tab" chip
                        string chip = "Switch to this tab";
                        int cw = TextRenderer.MeasureText(chip, small).Width + S(20);
                        var cr = new Rectangle(right - cw, y + (RowH - S(26)) / 2, cw, S(26));
                        using (var p = Theme.Round(cr, cr.Height / 2f)) using (var b = new SolidBrush(Theme.PrimaryContainer)) g.FillPath(b, p);
                        TextRenderer.DrawText(g, chip, small, cr, Theme.OnPrimaryContainer, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                        right = cr.X - S(12);
                    }
                    string main = r.Title ?? "", second = null;
                    if (r.Kind == Kind.Search) second = Search.EngineName() + " Search";
                    else if (r.Kind == Kind.History || r.Kind == Kind.Bookmark || r.Kind == Kind.Tab) second = Short(r.Url);
                    int mw = TextRenderer.MeasureText(main, font).Width;
                    int avail = right - tx;
                    int mainW = second == null ? avail : Math.Min(mw, avail * 3 / 5);
                    TextRenderer.DrawText(g, main, font, new Rectangle(tx, y, mainW, RowH), Theme.OnSurface,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                    if (second != null)
                        TextRenderer.DrawText(g, "  –  " + second, font, new Rectangle(tx + mainW, y, avail - mainW, RowH),
                            r.Kind == Kind.Search ? Theme.OnSurfaceVariant : Theme.Primary,
                            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                }
            }

            int RowAt(Point p) { int i = (p.Y - S(8)) / RowH; return p.Y >= S(8) && i >= 0 && i < o.rows.Count ? i : -1; }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                int h = RowAt(e.Location);
                if (h != hover) { hover = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
            }

            protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                int i = RowAt(e.Location);
                if (i < 0) return;
                if (e.Button == MouseButtons.Left) o.Activate(i, (ModifierKeys & Keys.Control) != 0);
                else if (e.Button == MouseButtons.Middle) o.Activate(i, true);
            }
        }
    }
}
