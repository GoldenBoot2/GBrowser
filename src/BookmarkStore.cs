using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace GBrowser
{
    /// <summary>Bookmarks as Chrome keeps them: a tree under two roots, "Bookmarks bar" (1) and "Other bookmarks" (2),
    /// with folders. Stored flat in bookmarks.json (each item names its parent); siblings keep their list order.
    /// Bookmarks from older GBrowser versions (no id) become items of the bookmarks bar, as they were shown there.</summary>
    internal static class Bm
    {
        public const long Bar = 1, Other = 2;
        public static string RootName(long id) => id == Bar ? "Bookmarks bar" : id == Other ? "Other bookmarks" : null;
        public static event Action Changed;

        static List<Bookmark> All => Store.Bookmarks;

        /// <summary>Gives older entries ids and the bookmarks bar as parent.</summary>
        public static void Migrate()
        {
            long next = Math.Max(10, All.Count == 0 ? 0 : All.Max(b => b.Id)) + 1;
            bool changed = false;
            foreach (var b in All)
            {
                if (b.Id == 0) { b.Id = next++; changed = true; }
                if (b.Parent == 0) { b.Parent = Bar; changed = true; }
            }
            if (changed) Store.SaveBookmarks();
        }

        static long NextId() => Math.Max(10, All.Count == 0 ? 0 : All.Max(b => b.Id)) + 1;

        public static Bookmark Find(long id) => All.FirstOrDefault(b => b.Id == id);
        public static List<Bookmark> Children(long parent) => All.Where(b => b.Parent == parent).ToList();
        public static bool IsRoot(long id) => id == Bar || id == Other;
        public static bool Exists(long parent) => IsRoot(parent) || All.Any(b => b.Id == parent && b.Folder);
        public static bool IsBookmarked(string url) => !string.IsNullOrEmpty(url) && All.Any(b => !b.Folder && b.Url == url);
        public static Bookmark ForUrl(string url) => All.FirstOrDefault(b => !b.Folder && b.Url == url);

        public static string FolderName(long id) => RootName(id) ?? Find(id)?.Title ?? "Bookmarks bar";

        /// <summary>Folders for choosers, depth first: (id, name with its path).</summary>
        public static List<Tuple<long, string>> Folders()
        {
            var r = new List<Tuple<long, string>>();
            foreach (var root in new[] { Bar, Other })
            {
                r.Add(Tuple.Create(root, RootName(root)));
                Walk(root, RootName(root), r);
            }
            return r;
        }
        static void Walk(long parent, string path, List<Tuple<long, string>> r)
        {
            foreach (var f in Children(parent).Where(b => b.Folder))
            {
                string p = path + " / " + f.Title;
                r.Add(Tuple.Create(f.Id, p));
                Walk(f.Id, p, r);
            }
        }

        static bool IsInside(long id, long ancestor)
        {
            for (var b = Find(id); b != null; b = Find(b.Parent)) if (b.Parent == ancestor) return true;
            return false;
        }

        /// <summary>Puts item into the list at position index among parent's children (end when out of range).</summary>
        static void Place(Bookmark item, long parent, int index)
        {
            item.Parent = parent;
            var sibs = Children(parent).Where(b => b != item).ToList();
            All.Remove(item);
            if (index < 0 || index >= sibs.Count)
            {
                int at = sibs.Count == 0 ? All.Count : All.IndexOf(sibs[sibs.Count - 1]) + 1;
                All.Insert(at, item);
            }
            else All.Insert(All.IndexOf(sibs[index]), item);
        }

        public static Bookmark Add(string url, string title, long parent, int index = -1, long added = 0)
        {
            if (!Exists(parent)) parent = Bar;
            var b = new Bookmark { Id = NextId(), Parent = parent, Url = url, Title = string.IsNullOrEmpty(title) ? url : title, Added = added > 0 ? added : Store.Now() };
            Place(b, parent, index);
            Save();
            return b;
        }

        public static Bookmark AddFolder(string title, long parent, int index = -1)
        {
            if (!Exists(parent)) parent = Bar;
            var b = new Bookmark { Id = NextId(), Parent = parent, Folder = true, Title = string.IsNullOrWhiteSpace(title) ? "New folder" : title.Trim(), Added = Store.Now() };
            Place(b, parent, index);
            Save();
            return b;
        }

        public static void Edit(long id, string title, string url)
        {
            var b = Find(id);
            if (b == null) return;
            if (title != null) b.Title = b.Folder && string.IsNullOrWhiteSpace(title) ? b.Title : title;
            if (!b.Folder && !string.IsNullOrWhiteSpace(url)) b.Url = url.Trim();
            Save();
        }

        /// <summary>Moves an item (folders with everything in them) to parent at index; never into itself.</summary>
        public static bool Move(long id, long parent, int index)
        {
            var b = Find(id);
            if (b == null || !Exists(parent) || id == parent || (b.Folder && IsInside(parent, id))) return false;
            Place(b, parent, index);
            Save();
            return true;
        }

        public static void Remove(long id)
        {
            var b = Find(id);
            if (b == null) return;
            var gone = new HashSet<long> { id };
            bool more = true;
            while (more) { more = false; foreach (var x in All) if (!gone.Contains(x.Id) && gone.Contains(x.Parent)) { gone.Add(x.Id); more = true; } }
            All.RemoveAll(x => gone.Contains(x.Id));
            Save();
        }

        // ---------- several at once (the manager's selection), like Chrome's bookmark manager ----------

        /// <summary>Ids plus everything inside the folders among them.</summary>
        static HashSet<long> WithContents(IEnumerable<long> ids)
        {
            var set = new HashSet<long>(ids.Where(i => Find(i) != null));
            bool more = true;
            while (more) { more = false; foreach (var x in All) if (!set.Contains(x.Id) && set.Contains(x.Parent)) { set.Add(x.Id); more = true; } }
            return set;
        }

        /// <summary>What the last delete removed (positions in the list kept), for Undo (Chrome: Ctrl+Z / the Undo button).</summary>
        static List<Tuple<int, Bookmark>> lastDeleted;

        public static int RemoveMany(IEnumerable<long> ids)
        {
            var gone = WithContents(ids);
            if (gone.Count == 0) return 0;
            lastDeleted = All.Select((b, i) => Tuple.Create(i, b)).Where(t => gone.Contains(t.Item2.Id)).ToList();
            All.RemoveAll(x => gone.Contains(x.Id));
            Save();
            return lastDeleted.Count(t => ids.Contains(t.Item2.Id));
        }

        /// <summary>Puts back what the last delete removed, at the same places (ids taken meanwhile get new ones).</summary>
        public static bool UndoRemove()
        {
            if (lastDeleted == null || lastDeleted.Count == 0) return false;
            var map = new Dictionary<long, long>();
            foreach (var t in lastDeleted)
                if (All.Any(b => b.Id == t.Item2.Id)) map[t.Item2.Id] = Math.Max(NextId(), map.Count == 0 ? 0 : map.Values.Max() + 1);
            foreach (var t in lastDeleted.OrderBy(t => t.Item1))
            {
                var b = t.Item2;
                if (map.TryGetValue(b.Id, out var nid)) b.Id = nid;
                if (map.TryGetValue(b.Parent, out var np)) b.Parent = np;
                if (!Exists(b.Parent)) b.Parent = Other;
                All.Insert(Math.Min(t.Item1, All.Count), b);
            }
            lastDeleted = null;
            Save();
            return true;
        }

        /// <summary>Moves several items (in the order given) to parent at index, keeping them together.</summary>
        public static int MoveMany(IList<long> ids, long parent, int index)
        {
            if (!Exists(parent)) return 0;
            // index counts the folder's current items; the moved ones go in front of the first item at or after it
            // that is not itself being moved (or to the end), in the order given
            var moving = new HashSet<long>(ids);
            var anchor = index < 0 ? null : Children(parent).Skip(index).FirstOrDefault(b => !moving.Contains(b.Id));
            int moved = 0;
            foreach (var id in ids)
            {
                var b = Find(id);
                if (b == null || id == parent || (b.Folder && IsInside(parent, id))) continue;
                int at = anchor == null ? -1 : Children(parent).Where(x => x != b).ToList().IndexOf(anchor);
                Place(b, parent, at);
                moved++;
            }
            if (moved > 0) Save();
            return moved;
        }

        /// <summary>Copies items (folders with all they hold) into parent at index (Chrome's Copy + Paste).</summary>
        public static int CopyMany(IList<long> ids, long parent, int index)
        {
            if (!Exists(parent)) return 0;
            int n = 0;
            long Copy(Bookmark src, long into, int at)
            {
                var c = new Bookmark { Id = NextId(), Parent = into, Folder = src.Folder, Title = src.Title, Url = src.Url, Added = Store.Now() };
                Place(c, into, at);
                n++;
                if (src.Folder) foreach (var k in Children(src.Id)) Copy(k, c.Id, -1);
                return c.Id;
            }
            int i = index;
            foreach (var id in ids)
            {
                var b = Find(id);
                if (b == null || (b.Folder && (parent == id || IsInside(parent, id)))) continue;
                Copy(b, parent, i);
                if (i >= 0) i++;
            }
            if (n > 0) Save();
            return n;
        }

        /// <summary>Chrome's "Sort by name": folders first, then pages, each by name (current language order).</summary>
        public static void Sort(long parent)
        {
            var kids = Children(parent);
            if (kids.Count < 2) return;
            var sorted = kids.OrderBy(b => b.Folder ? 0 : 1).ThenBy(b => b.Title ?? b.Url ?? "", StringComparer.CurrentCultureIgnoreCase).ToList();
            int first = All.IndexOf(kids[0]);
            foreach (var k in kids) All.Remove(k);
            All.InsertRange(Math.Min(first, All.Count), sorted);
            Save();
        }

        public static void Save()
        {
            Store.SaveBookmarks();
            Changed?.Invoke();
        }

        // ------------------------------------------------------------ import / export (Netscape HTML, as all browsers)

        public static string ExportHtml()
        {
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE NETSCAPE-Bookmark-file-1>\n<!-- This is an automatically generated file.\n     It will be read and overwritten.\n     DO NOT EDIT! -->\n");
            sb.Append("<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">\n<TITLE>Bookmarks</TITLE>\n<H1>Bookmarks</H1>\n<DL><p>\n");
            long now = Store.Now() / 1000;
            sb.Append("    <DT><H3 ADD_DATE=\"" + now + "\" LAST_MODIFIED=\"" + now + "\" PERSONAL_TOOLBAR_FOLDER=\"true\">Bookmarks bar</H3>\n    <DL><p>\n");
            ExportLevel(sb, Bar, 2);
            sb.Append("    </DL><p>\n");
            ExportLevel(sb, Other, 1);
            sb.Append("</DL><p>\n");
            return sb.ToString();
        }

        static void ExportLevel(StringBuilder sb, long parent, int depth)
        {
            string ind = new string(' ', depth * 4);
            foreach (var b in Children(parent))
            {
                string date = (b.Added / 1000).ToString();
                if (b.Folder)
                {
                    sb.Append(ind + "<DT><H3 ADD_DATE=\"" + date + "\">" + WebUtility.HtmlEncode(b.Title ?? "") + "</H3>\n" + ind + "<DL><p>\n");
                    ExportLevel(sb, b.Id, depth + 1);
                    sb.Append(ind + "</DL><p>\n");
                }
                else
                {
                    // the site icon goes along, like Chrome's export (other browsers import it)
                    var png = Favicons.Get(b.Url);
                    string icon = png != null && png.Length <= 16 * 1024 && Favicons.PngWidth(png) > 0 ? " ICON=\"data:image/png;base64," + Convert.ToBase64String(png) + "\"" : "";
                    sb.Append(ind + "<DT><A HREF=\"" + WebUtility.HtmlEncode(b.Url ?? "") + "\" ADD_DATE=\"" + date + "\"" + icon + ">" + WebUtility.HtmlEncode(b.Title ?? "") + "</A>\n");
                }
            }
        }

        /// <summary>Netscape bookmark HTML (Chrome, Edge, Firefox exports) into a folder: the file's bookmarks-bar
        /// folder into the bar (into a folder named after the source when the bar already has items), the rest under
        /// Other bookmarks. Pages already bookmarked are skipped. Returns how many were added.</summary>
        public static int ImportHtml(string html, string source)
        {
            int count = 0;
            var stack = new Stack<long>();
            long target = NewImportFolder(Other, "Imported" + (source != null ? " from " + source : ""));
            stack.Push(target);
            long barTarget = 0;
            string pendingFolder = null; bool pendingBar = false;
            var tok = new Regex(@"<DT>\s*<H3([^>]*)>(.*?)</H3>|<DT>\s*<A([^>]*)>(.*?)</A>|<DL>|</DL>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            bool first = true;
            foreach (Match m in tok.Matches(html))
            {
                if (m.Groups[2].Success && m.Value.IndexOf("<H3", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    pendingFolder = WebUtility.HtmlDecode(Regex.Replace(m.Groups[2].Value, "<[^>]+>", "")).Trim();
                    pendingBar = Regex.IsMatch(m.Groups[1].Value, "PERSONAL_TOOLBAR_FOLDER\\s*=\\s*\"true\"", RegexOptions.IgnoreCase);
                }
                else if (m.Value.StartsWith("<DL", StringComparison.OrdinalIgnoreCase))
                {
                    if (first) { first = false; pendingFolder = null; continue; }   // the file's outer list
                    if (pendingFolder == null) { stack.Push(stack.Peek()); continue; }
                    long id;
                    if (pendingBar && stack.Count == 1)
                    {
                        if (barTarget == 0) barTarget = Children(Bar).Count == 0 ? Bar : NewImportFolder(Bar, source ?? "Imported");
                        id = barTarget;
                    }
                    else id = AddFolderQuiet(pendingFolder, stack.Peek());
                    stack.Push(id);
                    pendingFolder = null; pendingBar = false;
                }
                else if (m.Value.StartsWith("</DL", StringComparison.OrdinalIgnoreCase))
                {
                    if (stack.Count > 1) stack.Pop();
                }
                else if (m.Groups[3].Success)
                {
                    var href = Regex.Match(m.Groups[3].Value, "HREF\\s*=\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
                    if (!href.Success) continue;
                    string url = WebUtility.HtmlDecode(href.Groups[1].Value);
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https" && u.Scheme != "ftp" && u.Scheme != "file")) continue;
                    // exports from Chrome / Firefox carry the site icon: ICON="data:image/png;base64,..."
                    var ico = Regex.Match(m.Groups[3].Value, "ICON\\s*=\\s*\"data:image/png;base64,([A-Za-z0-9+/=]+)\"", RegexOptions.IgnoreCase);
                    if (ico.Success && (u.Scheme == "http" || u.Scheme == "https"))
                        try
                        {
                            var png = Convert.FromBase64String(ico.Groups[1].Value);
                            var cur = Favicons.Get(url);
                            if (Favicons.PngWidth(png) > 0 && (cur == null || Favicons.PngWidth(cur) < Favicons.PngWidth(png))) Favicons.Save(url, png);
                        }
                        catch { }
                    if (IsBookmarked(url)) continue;
                    var add = Regex.Match(m.Groups[3].Value, "ADD_DATE\\s*=\\s*\"(\\d+)\"", RegexOptions.IgnoreCase);
                    long added = add.Success && long.TryParse(add.Groups[1].Value, out var s) && s > 0 ? s * 1000 : 0;
                    string title = WebUtility.HtmlDecode(Regex.Replace(m.Groups[4].Value, "<[^>]+>", "")).Trim();
                    AddQuiet(url, title, stack.Peek(), added);
                    count++;
                }
            }
            DropEmptyFolders(target);
            Save();
            return count;
        }

        /// <summary>Chrome / Edge "Bookmarks" file (JSON) of a profile: same rules as the HTML import.</summary>
        /// <summary>How many page bookmarks a Chrome / Edge "Bookmarks" file holds (bar, other, mobile), -1 when unreadable;
        /// shown in the Import dialog so an empty profile is clear before Import is pressed.</summary>
        public static int CountChromium(string profileDir)
        {
            var roots = ReadProfileRoots(profileDir);
            if (roots == null) return -1;
            int n = 0;
            void Walk(object o)
            {
                if (!(o is Dictionary<string, object> d)) return;
                if (d.TryGetValue("type", out var t) && (t as string) == "url") n++;
                if (d.TryGetValue("children", out var c) && c is System.Collections.IEnumerable list) foreach (var k in list) Walk(k);
            }
            foreach (var key in new[] { "bookmark_bar", "other", "synced" }) if (roots.TryGetValue(key, out var node)) Walk(node);
            return n;
        }

        /// <summary>A Chrome / Edge profile's bookmark files: "AccountBookmarks" (bookmarks of the signed-in Google Account,
        /// Chrome 2025+) and "Bookmarks" (bookmarks kept only on this PC).</summary>
        static List<string> BookmarkFiles(string profileDir)
        {
            var r = new List<string>();
            foreach (var n in new[] { "AccountBookmarks", "Bookmarks" })
            {
                string f = Path.Combine(profileDir, n);
                if (File.Exists(f)) r.Add(f);
            }
            return r;
        }

        /// <summary>The profile's bookmark roots with both files merged like Chrome shows them (account items first);
        /// files are read shared (the browser may be running). Null when nothing could be read.</summary>
        public static Dictionary<string, object> ReadProfileRoots(string profileDir)
        {
            Dictionary<string, object> merged = null;
            foreach (var f in BookmarkFiles(profileDir))
            {
                try
                {
                    string text;
                    using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var rd = new StreamReader(fs)) text = rd.ReadToEnd();
                    var root = Store.FromJson<Dictionary<string, object>>(text);
                    if (root == null || !(root.TryGetValue("roots", out var r) && r is Dictionary<string, object> roots)) continue;
                    if (merged == null) merged = new Dictionary<string, object>();
                    foreach (var key in new[] { "bookmark_bar", "other", "synced" })
                    {
                        if (!(roots.TryGetValue(key, out var node) && node is Dictionary<string, object> nd)) continue;
                        var kids = nd.TryGetValue("children", out var c) && c is System.Collections.IEnumerable e ? e.Cast<object>().ToList() : new List<object>();
                        if (!merged.TryGetValue(key, out var have)) merged[key] = new Dictionary<string, object> { { "children", kids } };
                        else ((List<object>)((Dictionary<string, object>)have)["children"]).AddRange(kids);
                    }
                }
                catch { }
            }
            return merged;
        }

        /// <summary>Imports a Chrome / Edge / Brave profile (both its bookmark files), then the site icons the browser
        /// keeps for those bookmarks.</summary>
        public static int ImportProfile(string profileDir, string source)
        {
            var roots = ReadProfileRoots(profileDir);
            int n = roots == null ? 0 : ImportRoots(roots, source);
            try { ImportProfileIcons(profileDir); } catch { }
            return n;
        }

        /// <summary>Site icons from a Chromium profile's "Favicons" database for every bookmark GBrowser has: one icon per
        /// site (GBrowser's icon store is per site), the 32 px image when there is one (sharp at 125-200 %), a page that
        /// is itself bookmarked first. Never replaces a sharper icon GBrowser already has. Returns icons stored.</summary>
        public static int ImportProfileIcons(string profileDir)
        {
            string db = Path.Combine(profileDir, "Favicons");
            if (!File.Exists(db)) return 0;
            var wanted = new Dictionary<string, string>();   // site key -> one bookmarked address of that site
            foreach (var b in All)
                if (!b.Folder && !string.IsNullOrEmpty(b.Url))
                {
                    string k = Favicons.KeyOf(b.Url);
                    if (k != null && !wanted.ContainsKey(k)) wanted[k] = b.Url;
                }
            if (wanted.Count == 0) return 0;
            var bookmarked = new HashSet<string>(All.Where(b => !b.Folder && b.Url != null).Select(b => b.Url));
            var iconOfSite = new Dictionary<string, long>();
            var exact = new HashSet<string>();
            using (var r = new SqliteReader(db))
            {
                // icon_mapping(id, page_url, icon_id, page_url_type)
                foreach (var row in r.Rows("icon_mapping"))
                {
                    if (row.Values.Length < 3 || !(row.Values[1] is string page) || !(row.Values[2] is long icon)) continue;
                    string k = Favicons.KeyOf(page);
                    if (k == null || !wanted.ContainsKey(k)) continue;
                    bool isExact = bookmarked.Contains(page);
                    if (!iconOfSite.ContainsKey(k) || (isExact && !exact.Contains(k))) { iconOfSite[k] = icon; if (isExact) exact.Add(k); }
                }
                if (iconOfSite.Count == 0) return 0;
                var need = new HashSet<long>(iconOfSite.Values);
                var best = new Dictionary<long, byte[]>();
                var bestW = new Dictionary<long, long>();
                // favicon_bitmaps(id, icon_id, last_updated, image_data, width, height, last_requested)
                foreach (var row in r.Rows("favicon_bitmaps"))
                {
                    if (row.Values.Length < 5 || !(row.Values[1] is long icon) || !need.Contains(icon) || !(row.Values[3] is byte[] png) || png.Length == 0) continue;
                    long w = row.Values[4] is long lw ? lw : 0;
                    // prefer 32 px; otherwise the biggest up to 64, else whatever there is
                    long Score(long x) => x == 32 ? 1000 : x <= 64 ? x : 10 - x / 64;
                    if (!bestW.TryGetValue(icon, out var have) || Score(w) > Score(have)) { best[icon] = png; bestW[icon] = w; }
                }
                int stored = 0;
                foreach (var kv in iconOfSite)
                {
                    if (!best.TryGetValue(kv.Value, out var png) || Favicons.PngWidth(png) == 0) continue;
                    var cur = Favicons.Get(wanted[kv.Key]);
                    if (cur != null && Favicons.PngWidth(cur) >= Favicons.PngWidth(png)) continue;
                    Favicons.Save(wanted[kv.Key], png);
                    stored++;
                }
                return stored;
            }
        }

        public static int ImportChromium(string json, string source)
        {
            var root = Store.FromJson<Dictionary<string, object>>(json);
            var roots = root != null && root.TryGetValue("roots", out var r) ? r as Dictionary<string, object> : null;
            return roots == null ? 0 : ImportRoots(roots, source);
        }

        static int ImportRoots(Dictionary<string, object> roots, string source)
        {
            int count = 0;
            if (roots.TryGetValue("bookmark_bar", out var bar) && bar is Dictionary<string, object> barNode && HasChildren(barNode))
            {
                long into = Children(Bar).Count == 0 ? Bar : NewImportFolder(Bar, "Imported from " + source);
                count += ImportNode(barNode, into);
                DropEmptyFolders(into);
            }
            long other = 0;
            foreach (var key in new[] { "other", "synced" })
                if (roots.TryGetValue(key, out var o) && o is Dictionary<string, object> node && HasChildren(node))
                {
                    if (other == 0) other = NewImportFolder(Other, "Imported from " + source);
                    count += ImportNode(node, other);
                }
            if (other != 0) DropEmptyFolders(other);
            Save();
            return count;
        }

        static bool HasChildren(Dictionary<string, object> n) =>
            n.TryGetValue("children", out var c) && c is System.Collections.IEnumerable e && e.Cast<object>().Any();

        static int ImportNode(Dictionary<string, object> node, long parent)
        {
            int count = 0;
            if (!node.TryGetValue("children", out var kids) || !(kids is System.Collections.IEnumerable list)) return 0;
            foreach (var k in list.Cast<object>())
            {
                if (!(k is Dictionary<string, object> c)) continue;
                string type = c.TryGetValue("type", out var t) ? t as string : null;
                string name = c.TryGetValue("name", out var n) ? n as string : "";
                if (type == "folder") count += ImportNode(c, AddFolderQuiet(name, parent));
                else if (type == "url" && c.TryGetValue("url", out var uo) && uo is string url)
                {
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https" && u.Scheme != "ftp" && u.Scheme != "file")) continue;
                    if (IsBookmarked(url)) continue;
                    long added = 0;
                    // Chrome time: microseconds since 1601-01-01 UTC
                    if (c.TryGetValue("date_added", out var da) && long.TryParse(da as string, out var us) && us > 11644473600000000L)
                        added = (us - 11644473600000000L) / 1000;
                    AddQuiet(url, name, parent, added);
                    count++;
                }
            }
            return count;
        }

        static long NewImportFolder(long parent, string name)
        {
            string n = name; int i = 2;
            while (Children(parent).Any(b => b.Folder && b.Title == n)) n = name + " (" + i++ + ")";
            return AddFolderQuiet(n, parent);
        }

        static long AddFolderQuiet(string title, long parent)
        {
            var b = new Bookmark { Id = NextId(), Parent = parent, Folder = true, Title = string.IsNullOrWhiteSpace(title) ? "Folder" : title.Trim(), Added = Store.Now() };
            Place(b, parent, -1);
            return b.Id;
        }

        static void AddQuiet(string url, string title, long parent, long added)
        {
            var b = new Bookmark { Id = NextId(), Parent = parent, Url = url, Title = string.IsNullOrEmpty(title) ? url : title, Added = added > 0 ? added : Store.Now() };
            Place(b, parent, -1);
        }

        /// <summary>Removes empty folders (from an import that found nothing new) inside and including top.</summary>
        static void DropEmptyFolders(long top)
        {
            bool again = true;
            while (again)
            {
                again = false;
                foreach (var f in All.Where(b => b.Folder && (b.Id == top || IsInside(b.Id, top))).ToList())
                    if (!All.Any(x => x.Parent == f.Id)) { All.Remove(f); again = true; }
            }
        }

        // ------------------------------------------------------------ other browsers on this PC

        /// <summary>Chrome / Edge profiles with a Bookmarks file: (browser name, profile name, path).</summary>
        public static List<Tuple<string, string, string>> OtherBrowsers()
        {
            var r = new List<Tuple<string, string, string>>();
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach (var b in new[] { Tuple.Create("Google Chrome", @"Google\Chrome\User Data"), Tuple.Create("Microsoft Edge", @"Microsoft\Edge\User Data"),
                                       Tuple.Create("Brave", @"BraveSoftware\Brave-Browser\User Data") })
            {
                string dir = Path.Combine(local, b.Item2);
                if (!Directory.Exists(dir)) continue;
                Dictionary<string, object> names = null;
                try
                {
                    var ls = Store.FromJson<Dictionary<string, object>>(File.ReadAllText(Path.Combine(dir, "Local State")));
                    names = ((ls["profile"] as Dictionary<string, object>)?["info_cache"]) as Dictionary<string, object>;
                }
                catch { }
                foreach (var p in Directory.GetDirectories(dir))
                {
                    string id = Path.GetFileName(p);
                    if (BookmarkFiles(p).Count == 0) continue;
                    string name = id;
                    try { if (names != null && names.TryGetValue(id, out var o)) name = ((o as Dictionary<string, object>)?["name"] as string) ?? id; } catch { }
                    r.Add(Tuple.Create(b.Item1, name, p));
                }
            }
            return r;
        }
    }
}
