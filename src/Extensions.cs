using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace GBrowser
{
    /// <summary>Chrome extensions on WebView2: install from the Chrome Web Store (CRX3 download, unpacked with its
    /// store key so the extension keeps its real ID), load unpacked folders, enable/disable, remove.</summary>
    internal static class Extensions
    {
        const string IndexFile = "extensions.json";   // encrypted (Store.ReadText / WriteText)
        static Dictionary<string, string> index;   // extension id -> folder

        public static Dictionary<string, string> Index
        {
            get
            {
                if (index == null)
                {
                    try { var t = Store.ReadText(IndexFile); index = t == null ? null : Store.FromJson<Dictionary<string, string>>(t); } catch { }
                    if (index != null) SaveIndex();   // re-save: encrypts a plain file from an older version
                    if (index == null) index = new Dictionary<string, string>();
                }
                return index;
            }
        }

        static void SaveIndex()
        {
            Store.WriteText(IndexFile, Store.ToJson(Index));
        }

        static readonly Regex IdRe = new Regex(@"(?<![a-p])([a-p]{32})(?![a-p])");

        /// <summary>Extension ID from a Chrome Web Store URL or a bare ID; null if none.</summary>
        public static string ParseId(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var m = IdRe.Match(text.Trim().ToLowerInvariant());
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>Store detail page (chromewebstore.google.com/detail/name/id or the old chrome.google.com/webstore).</summary>
        public static string StorePageId(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            if (!url.StartsWith("https://chromewebstore.google.com/detail/") && !url.StartsWith("https://chrome.google.com/webstore/detail/")) return null;
            return ParseId(url.Split('?', '#')[0]);
        }

        /// <summary>Asked before an extension is added: its name, what it can access, its icon file (or null).</summary>
        public delegate bool Confirm(string name, List<string> access, string iconFile);

        public static async Task<CoreWebView2BrowserExtension> InstallFromStore(CoreWebView2Profile profile, string id, string userAgent,
            string browserVersion, Confirm confirm)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            string major = (browserVersion ?? "157").Split('.')[0];
            string url = "https://clients2.google.com/service/update2/crx?response=redirect&prodversion=" + major +
                         ".0.0.0&acceptformat=crx3&x=id%3D" + id + "%26installsource%3Dondemand%26uc";
            byte[] crx;
            using (var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate }))
            {
                http.Timeout = TimeSpan.FromSeconds(90);
                if (!string.IsNullOrEmpty(userAgent)) http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
                var resp = await http.GetAsync(url);
                if (!resp.IsSuccessStatusCode) throw new Exception("Download failed (" + (int)resp.StatusCode + ")");
                crx = await resp.Content.ReadAsByteArrayAsync();
            }
            var parsed = Crx.Parse(crx, id);          // signature and ID checked, or it throws
            string folder = Path.Combine(Store.ExtensionsDir, id), tmp = folder + ".new";
            await Task.Run(() => Unpack(parsed.Zip, tmp, parsed.PublicKey));
            Dictionary<string, object> m = null;
            try { m = Store.FromJson<Dictionary<string, object>>(File.ReadAllText(Path.Combine(tmp, "manifest.json"), Encoding.UTF8).TrimStart('\uFEFF')); } catch { }
            if (m == null) { TryDelete(tmp); throw new Exception("Bad manifest"); }
            if (confirm != null && !confirm(LocalName(tmp, m) ?? id, AccessWarnings(m), IconPath(tmp, m)))
            {
                TryDelete(tmp);
                return null;
            }
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Directory.Move(tmp, folder);
            var ext = await profile.AddBrowserExtensionAsync(folder);
            Index[ext.Id] = folder;
            SaveIndex();
            Added(ext.Id);
            return ext;
        }

        public static async Task<CoreWebView2BrowserExtension> LoadUnpacked(CoreWebView2Profile profile, string folder)
        {
            if (!File.Exists(Path.Combine(folder, "manifest.json"))) throw new Exception("No manifest.json");
            var ext = await profile.AddBrowserExtensionAsync(folder);
            Index[ext.Id] = folder;
            SaveIndex();
            Added(ext.Id);
            return ext;
        }

        public static async Task Remove(CoreWebView2Profile profile, string id)
        {
            var ext = (await profile.GetBrowserExtensionsAsync()).FirstOrDefault(e => e.Id == id);
            if (ext != null) await ext.RemoveAsync();
            if (Index.TryGetValue(id, out var folder))
            {
                // delete only folders GBrowser unpacked itself, never a user's own unpacked folder
                try
                {
                    if (folder.StartsWith(Store.ExtensionsDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(folder))
                        Directory.Delete(folder, true);
                }
                catch { }
                Index.Remove(id);
                SaveIndex();
            }
            if (IsPinned(id)) SetPinned(id, false);
            if (Store.Settings.ExtensionShortcuts.Remove(id)) Store.SaveSettings();
        }

        static void TryDelete(string dir) { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { } }

        const long MaxUnpacked = 512L * 1024 * 1024;   // no "zip bombs": at most 512 MB / 20,000 files unpacked
        const int MaxEntries = 20000;

        static void Unpack(byte[] zip, string tmp, byte[] publicKey)
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);
            string root = Path.GetFullPath(tmp) + Path.DirectorySeparatorChar;
            using (var ms = new MemoryStream(zip))
            using (var za = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                if (za.Entries.Count > MaxEntries || za.Entries.Sum(x => x.Length) > MaxUnpacked) throw new Exception("Package too large");
                foreach (var e in za.Entries)
                {
                    string dest = Path.GetFullPath(Path.Combine(tmp, e.FullName.Replace('/', Path.DirectorySeparatorChar)));
                    if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;   // no path escapes
                    if (e.FullName.EndsWith("/")) { Directory.CreateDirectory(dest); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    e.ExtractToFile(dest, true);
                }
            }
            // Chromium refuses unpacked extensions with names starting with "_" except _locales (store adds _metadata)
            string meta = Path.Combine(tmp, "_metadata");
            if (Directory.Exists(meta)) Directory.Delete(meta, true);
            string mf = Path.Combine(tmp, "manifest.json");
            if (!File.Exists(mf)) throw new Exception("No manifest.json in package");
            if (publicKey != null)
            {
                string text = File.ReadAllText(mf, Encoding.UTF8).TrimStart('﻿');
                if (!Regex.IsMatch(text, "\"key\"\\s*:"))
                {
                    int brace = text.IndexOf('{');
                    text = text.Substring(0, brace + 1) + "\n  \"key\": \"" + Convert.ToBase64String(publicKey) + "\"," + text.Substring(brace + 1);
                    File.WriteAllText(mf, text, new UTF8Encoding(false));
                }
            }
        }

        /// <summary>Name from the manifest, resolving __MSG_x__ with _locales/default_locale/messages.json.</summary>
        static string LocalName(string dir, Dictionary<string, object> m) =>
            Localize(dir, m, m.TryGetValue("name", out var n) ? n as string : null);

        /// <summary>Resolves a manifest text like "__MSG_appDesc__" from _locales (Windows UI language, then its base
        /// language, then the extension's default_locale), like Chrome; other texts are returned as they are.</summary>
        public static string Localize(string dir, Dictionary<string, object> m, string text)
        {
            if (text == null || !text.StartsWith("__MSG_") || !text.EndsWith("__") || text.Length < 9) return text;
            string key = text.Substring(6, text.Length - 8);
            var ui = System.Globalization.CultureInfo.CurrentUICulture;
            var locales = new List<string> { ui.Name.Replace('-', '_'), ui.TwoLetterISOLanguageName };
            if (m.TryGetValue("default_locale", out var dl) && dl is string dls) locales.Add(dls);
            locales.Add("en");
            foreach (var loc in locales.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    string f = Path.Combine(dir, "_locales", loc, "messages.json");
                    if (!File.Exists(f)) continue;
                    var msgs = Store.FromJson<Dictionary<string, object>>(File.ReadAllText(f, Encoding.UTF8).TrimStart('\uFEFF'));
                    foreach (var kv in msgs)
                        if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase) && kv.Value is Dictionary<string, object> d && d.TryGetValue("message", out var msg) && msg is string ms)
                            return ms;
                }
                catch { }
            }
            return text;
        }

        /// <summary>A manifest text of an installed extension, localized; null when missing or unresolved.</summary>
        static string Text(string id, Dictionary<string, object> m, string key)
        {
            if (m == null || !m.TryGetValue(key, out var v) || !(v is string s)) return null;
            if (!s.StartsWith("__MSG_")) return s;
            string r = Index.TryGetValue(id, out var folder) ? Localize(folder, m, s) : s;
            return r.StartsWith("__MSG_") ? null : r;
        }

        static string IconPath(string dir, Dictionary<string, object> m)
        {
            if (!(m.TryGetValue("icons", out var ic) && ic is Dictionary<string, object> d)) return null;
            var best = d.Where(kv => int.TryParse(kv.Key, out _) && kv.Value is string).OrderBy(kv => Math.Abs(int.Parse(kv.Key) - 48)).FirstOrDefault();
            if (best.Value == null) return null;
            var p = Path.Combine(dir, ((string)best.Value).TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(p) ? p : null;
        }

        static readonly string[] AllHosts = { "<all_urls>", "*://*/*", "http://*/*", "https://*/*", "*://*/" };

        /// <summary>What the extension can access, worded like Chrome's install prompt.</summary>
        public static List<string> AccessWarnings(Dictionary<string, object> m)
        {
            var perms = new List<string>();
            foreach (var k in new[] { "permissions", "host_permissions", "optional_host_permissions" })
                if (m.TryGetValue(k, out var v) && v is System.Collections.IEnumerable list && !(v is string))
                    foreach (var o in list) if (o is string s) perms.Add(s);
            if (m.TryGetValue("content_scripts", out var cs) && cs is System.Collections.IEnumerable csl)
                foreach (var o in csl)
                    if (o is Dictionary<string, object> c && c.TryGetValue("matches", out var mt) && mt is System.Collections.IEnumerable ml)
                        foreach (var x in ml) if (x is string s) perms.Add(s);
            var w = new List<string>();
            var hosts = perms.Where(p => p.Contains("://") || p == "<all_urls>").ToList();
            bool all = hosts.Any(h => AllHosts.Contains(h)) || perms.Contains("debugger") || perms.Contains("proxy") || perms.Contains("pageCapture");
            if (all) w.Add("Read and change all your data on all websites");
            else if (hosts.Count > 0)
            {
                var names = hosts.Select(h => { var x = h.Substring(h.IndexOf("://") + 3); int sl = x.IndexOf('/'); return (sl >= 0 ? x.Substring(0, sl) : x).TrimStart('*', '.'); })
                    .Where(x => x.Length > 0).Distinct().ToList();
                w.Add("Read and change your data on " + string.Join(", ", names.Take(3)) + (names.Count > 3 ? " and " + (names.Count - 3) + " more sites" : ""));
            }
            var map = new Dictionary<string, string>
            {
                { "history", "Read and change your browsing history" }, { "tabs", "Read your browsing history" },
                { "webNavigation", "Read your browsing history" }, { "topSites", "Read a list of your most visited sites" },
                { "bookmarks", "Read and change your bookmarks" }, { "downloads", "Manage your downloads" },
                { "clipboardRead", "Read data you copy and paste" }, { "clipboardWrite", "Change data you copy and paste" },
                { "nativeMessaging", "Communicate with programs on this computer" }, { "management", "Manage your apps, extensions and themes" },
                { "privacy", "Change your privacy settings" }, { "contentSettings", "Change which features sites may use (cookies, camera, location ...)" },
                { "geolocation", "Detect your physical location" }, { "notifications", "Show notifications" },
                { "declarativeNetRequest", "Block content on any page" }, { "desktopCapture", "Capture what is on your screen" },
                { "debugger", "Access the page debugger" }, { "cookies", "Read and change cookies of the sites above" },
            };
            foreach (var p in perms) if (map.TryGetValue(p, out var t) && !w.Contains(t)) w.Add(t);
            return w;
        }

        /// <summary>Data the extensions page needs: from WebView2 plus the extension's manifest.</summary>
        public static async Task<List<Dictionary<string, object>>> List(CoreWebView2Profile profile)
        {
            var list = new List<Dictionary<string, object>>();
            foreach (var e in await profile.GetBrowserExtensionsAsync())
            {
                // only what the user added (WebView2's own components, like its PDF viewer, stay hidden)
                if (!Index.ContainsKey(e.Id)) continue;
                var m = Manifest(e.Id);
                list.Add(new Dictionary<string, object>
                {
                    { "id", e.Id }, { "name", e.Name }, { "enabled", e.IsEnabled },
                    { "version", Get(m, "version") ?? "" },
                    { "description", Text(e.Id, m, "description") ?? "" },
                    { "popup", PopupPage(m) ?? "" }, { "options", OptionsPage(m) ?? "" },
                    { "icon", IconDataUrl(e.Id, e.Name) ?? "" },
                    { "store", Index.TryGetValue(e.Id, out var f) && f.StartsWith(Store.ExtensionsDir, StringComparison.OrdinalIgnoreCase) },
                    { "pinned", IsPinned(e.Id) },
                });
            }
            return list;
        }

        public static Dictionary<string, object> Manifest(string id)
        {
            try
            {
                if (Index.TryGetValue(id, out var folder))
                    return Store.FromJson<Dictionary<string, object>>(File.ReadAllText(Path.Combine(folder, "manifest.json"), Encoding.UTF8).TrimStart('﻿'));
            }
            catch { }
            return null;
        }

        static string Get(Dictionary<string, object> m, string key) =>
            m != null && m.TryGetValue(key, out var v) && v is string s && !s.StartsWith("__MSG_") ? s : null;

        public static string PopupPage(Dictionary<string, object> m)
        {
            if (m == null) return null;
            foreach (var k in new[] { "action", "browser_action", "page_action" })
                if (m.TryGetValue(k, out var a) && a is Dictionary<string, object> d && d.TryGetValue("default_popup", out var p) && p is string s && s.Length > 0)
                    return s.TrimStart('/');
            return null;
        }

        public static string OptionsPage(Dictionary<string, object> m)
        {
            if (m == null) return null;
            if (m.TryGetValue("options_ui", out var o) && o is Dictionary<string, object> d && d.TryGetValue("page", out var p) && p is string s && s.Length > 0) return s.TrimStart('/');
            if (m.TryGetValue("options_page", out var op) && op is string s2 && s2.Length > 0) return s2.TrimStart('/');
            return null;
        }

        static string IconFile(string id, Dictionary<string, object> m)
        {
            if (m == null || !Index.TryGetValue(id, out var folder)) return null;
            var cands = new List<KeyValuePair<int, string>>();
            void AddFrom(object o)
            {
                if (o is string s) cands.Add(new KeyValuePair<int, string>(32, s));
                else if (o is Dictionary<string, object> d)
                    foreach (var kv in d) if (int.TryParse(kv.Key, out int n) && kv.Value is string f) cands.Add(new KeyValuePair<int, string>(n, f));
            }
            if (m.TryGetValue("icons", out var ic)) AddFrom(ic);
            foreach (var k in new[] { "action", "browser_action" })
                if (m.TryGetValue(k, out var a) && a is Dictionary<string, object> ad && ad.TryGetValue("default_icon", out var di)) AddFrom(di);
            var best = cands.OrderBy(c => c.Key >= 32 ? c.Key - 32 : 1000 - c.Key).FirstOrDefault();
            if (best.Value == null) return null;
            string path = Path.Combine(folder, best.Value.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(path) ? path : null;
        }

        public static Image Icon(string id) => Icon(id, 16);

        /// <summary>The extension's icon, or Chrome's stand-in when it has none: its first letter on a grey rounded tile.</summary>
        public static Image IconOrLetter(string id, string name, int px)
        {
            var img = Icon(id, px);
            if (img != null) return img;
            var bmp = new Bitmap(px, px);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                using (var p = Theme.Round(new Rectangle(0, 0, px - 1, px - 1), px / 4f))
                using (var b = new SolidBrush(Theme.Outline)) g.FillPath(b, p);
                string letter = string.IsNullOrEmpty(name) ? "?" : name.Trim().Substring(0, 1).ToUpperInvariant();
                using (var f = new Font("Segoe UI Semibold", px * 0.58f, GraphicsUnit.Pixel))
                using (var tb = new SolidBrush(Theme.Surface))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(letter, f, tb, new RectangleF(0, 0, px, px + px * 0.04f), sf);
            }
            return bmp;
        }

        /// <summary>The extension's icon at px x px, sharp like Chrome's: the smallest icon file that is at least px big
        /// (by its real pixel size, not the size its manifest claims), scaled down in halving steps.</summary>
        public static Image Icon(string id, int px)
        {
            string best = null;
            int bestSize = 0;
            foreach (var path in IconFiles(id, Manifest(id)))
            {
                try
                {
                    using (var i = Image.FromFile(path))
                    {
                        int sz = Math.Min(i.Width, i.Height);
                        bool better = best == null
                            || (sz >= px && (bestSize < px || sz < bestSize))   // big enough and closer to px
                            || (bestSize < px && sz > bestSize);               // nothing big enough yet: the biggest
                        if (better) { best = path; bestSize = sz; }
                    }
                }
                catch { }
            }
            if (best == null) return null;
            try { using (var img = Image.FromFile(best)) return Scale(img, px); }
            catch { return null; }
        }

        /// <summary>Every icon file of the extension (toolbar icon first, then its icons), existing raster files only.</summary>
        static List<string> IconFiles(string id, Dictionary<string, object> m)
        {
            var res = new List<string>();
            if (m == null || !Index.TryGetValue(id, out var folder)) return res;
            void Add(object o)
            {
                if (o is string f) Add1(f);
                else if (o is Dictionary<string, object> d) foreach (var kv in d) if (kv.Value is string f2) Add1(f2);
            }
            void Add1(string f)
            {
                if (f.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) return;   // GDI+ cannot read SVG
                string path = Path.Combine(folder, f.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(path) && !res.Contains(path, StringComparer.OrdinalIgnoreCase)) res.Add(path);
            }
            foreach (var k in new[] { "action", "browser_action", "page_action" })
                if (m.TryGetValue(k, out var a) && a is Dictionary<string, object> ad && ad.TryGetValue("default_icon", out var di)) Add(di);
            if (m.TryGetValue("icons", out var ic)) Add(ic);
            return res;
        }

        /// <summary>High-quality downscale to px x px (aspect kept, centred): halving steps first, then one bicubic step,
        /// so big icons (e.g. 500 px) stay crisp instead of thin and jagged.</summary>
        public static Bitmap Scale(Image src, int px)
        {
            Image cur = src;
            bool owned = false;
            try
            {
                while (Math.Max(cur.Width, cur.Height) / 2 >= px)
                {
                    var half = new Bitmap(Math.Max(1, cur.Width / 2), Math.Max(1, cur.Height / 2));
                    Draw(cur, half, new Rectangle(0, 0, half.Width, half.Height));
                    if (owned) cur.Dispose();
                    cur = half; owned = true;
                }
                var bmp = new Bitmap(px, px);
                float k = Math.Min((float)px / cur.Width, (float)px / cur.Height);
                int w = Math.Max(1, (int)Math.Round(cur.Width * k)), h = Math.Max(1, (int)Math.Round(cur.Height * k));
                Draw(cur, bmp, new Rectangle((px - w) / 2, (px - h) / 2, w, h));
                return bmp;
            }
            finally { if (owned) cur.Dispose(); }
        }

        static void Draw(Image from, Bitmap to, Rectangle dest)
        {
            using (var g = Graphics.FromImage(to))
            using (var ia = new System.Drawing.Imaging.ImageAttributes())
            {
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                ia.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY);   // no grey halo at the edges
                g.DrawImage(from, dest, 0, 0, from.Width, from.Height, GraphicsUnit.Pixel, ia);
            }
        }

        /// <summary>Raised when extensions are added, removed, turned on/off, pinned or reordered (every window refreshes its toolbar).</summary>
        public static event Action Changed;
        public static void RaiseChanged() => Changed?.Invoke();

        /// <summary>Chrome's shortcut text for a key ("Ctrl+Shift+Y", "Alt+1", "Ctrl+F5"); null when the key cannot be an
        /// extension shortcut (Chrome needs Ctrl or Alt and a letter, digit or F-key).</summary>
        public static string KeyText(System.Windows.Forms.Keys k)
        {
            var code = k & System.Windows.Forms.Keys.KeyCode;
            bool ctrl = (k & System.Windows.Forms.Keys.Control) != 0, alt = (k & System.Windows.Forms.Keys.Alt) != 0, shift = (k & System.Windows.Forms.Keys.Shift) != 0;
            if (!ctrl && !alt) return null;
            string key;
            if (code >= System.Windows.Forms.Keys.A && code <= System.Windows.Forms.Keys.Z) key = code.ToString();
            else if (code >= System.Windows.Forms.Keys.D0 && code <= System.Windows.Forms.Keys.D9) key = ((int)(code - System.Windows.Forms.Keys.D0)).ToString();
            else if (code >= System.Windows.Forms.Keys.F1 && code <= System.Windows.Forms.Keys.F12) key = code.ToString();
            else return null;
            return (ctrl ? "Ctrl+" : "") + (alt ? "Alt+" : "") + (shift ? "Shift+" : "") + key;
        }

        // ---------- toolbar pins (Chrome: pinned icons beside the extensions button) ----------

        /// <summary>Keeps Settings.PinnedExtensions in step with what is installed: the first time, every installed extension
        /// is pinned (Chrome's "Pin new extensions to Toolbar" is on by default); removed extensions drop out.</summary>
        public static void SyncPins(IEnumerable<string> installedIds)
        {
            var st = Store.Settings;
            var ids = installedIds.Where(i => Index.ContainsKey(i)).ToList();
            bool changed = false;
            if (st.PinnedExtensions == null) { st.PinnedExtensions = new List<string>(ids); changed = true; }
            else changed = st.PinnedExtensions.RemoveAll(p => !ids.Contains(p)) > 0;
            if (changed) Store.SaveSettings();
        }

        public static bool IsPinned(string id) => Store.Settings.PinnedExtensions?.Contains(id) == true;

        public static void SetPinned(string id, bool pin)
        {
            var st = Store.Settings;
            if (st.PinnedExtensions == null) st.PinnedExtensions = new List<string>();
            st.PinnedExtensions.Remove(id);
            if (pin) st.PinnedExtensions.Add(id);
            Store.SaveSettings();
        }

        /// <summary>A newly added extension is pinned when "Pin new extensions to toolbar" is on.</summary>
        public static void Added(string id)
        {
            if (Store.Settings.PinNewExtensions && !IsPinned(id)) SetPinned(id, true);
            var key = SuggestedActionKey(Manifest(id));
            var st = Store.Settings;
            if (key != null && !st.ExtensionShortcuts.ContainsKey(id) && !st.ExtensionShortcuts.ContainsValue(key))
            {
                st.ExtensionShortcuts[id] = key;
                Store.SaveSettings();
            }
        }

        /// <summary>The key an extension suggests for opening it (commands._execute_action / _execute_browser_action),
        /// written like Chrome ("Ctrl+Shift+Y"); null when none or not a valid GBrowser shortcut.</summary>
        static string SuggestedActionKey(Dictionary<string, object> m)
        {
            if (m == null || !(m.TryGetValue("commands", out var co) && co is Dictionary<string, object> cmds)) return null;
            foreach (var name in new[] { "_execute_action", "_execute_browser_action", "_execute_page_action" })
            {
                if (!(cmds.TryGetValue(name, out var c) && c is Dictionary<string, object> cd && cd.TryGetValue("suggested_key", out var sk))) continue;
                string k = sk as string ?? (sk is Dictionary<string, object> skd && (skd.TryGetValue("windows", out var w) || skd.TryGetValue("default", out w)) ? w as string : null);
                if (k == null) continue;
                // normalise to Chrome's own order: Ctrl, Alt, Shift, key
                var parts = k.Split('+').Select(x => x.Trim()).ToList();
                string key = parts.Last();
                bool ctrl = parts.Contains("Ctrl") || parts.Contains("Command"), alt = parts.Contains("Alt"), shift = parts.Contains("Shift");
                bool ok = (ctrl || alt) && (key.Length == 1 && char.IsLetterOrDigit(key[0]) || System.Text.RegularExpressions.Regex.IsMatch(key, "^F([1-9]|1[0-2])$"));
                if (ok) return (ctrl ? "Ctrl+" : "") + (alt ? "Alt+" : "") + (shift ? "Shift+" : "") + key.ToUpperInvariant();
            }
            return null;
        }

        // ---------- site access (Chrome match patterns) ----------

        /// <summary>Host patterns the extension asks for: host_permissions (MV3) / host patterns in permissions (MV2) and
        /// content script matches.</summary>
        public static List<string> HostPatterns(Dictionary<string, object> m)
        {
            var res = new List<string>();
            if (m == null) return res;
            foreach (var k in new[] { "host_permissions", "permissions" })
                if (m.TryGetValue(k, out var v) && v is System.Collections.IEnumerable list && !(v is string))
                    foreach (var o in list) if (o is string s && (s == "<all_urls>" || s.Contains("://"))) res.Add(s);
            if (m.TryGetValue("content_scripts", out var cs) && cs is System.Collections.IEnumerable csl)
                foreach (var o in csl)
                    if (o is Dictionary<string, object> c && c.TryGetValue("matches", out var mt) && mt is System.Collections.IEnumerable ml)
                        foreach (var x in ml) if (x is string s) res.Add(s);
            return res.Distinct().OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Does a Chrome match pattern (scheme://host/path, * wildcards, &lt;all_urls&gt;) cover this URL?</summary>
        public static bool Matches(string pattern, string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
            bool web = u.Scheme == "http" || u.Scheme == "https";
            if (pattern == "<all_urls>") return web || u.Scheme == "file" || u.Scheme == "ftp";
            int se = pattern.IndexOf("://", StringComparison.Ordinal);
            if (se <= 0) return false;
            string scheme = pattern.Substring(0, se), rest = pattern.Substring(se + 3);
            if (scheme == "*") { if (!web) return false; }
            else if (!string.Equals(scheme, u.Scheme, StringComparison.OrdinalIgnoreCase)) return false;
            int sl = rest.IndexOf('/');
            string host = sl < 0 ? rest : rest.Substring(0, sl), path = sl < 0 ? "/" : rest.Substring(sl);
            int colon = host.LastIndexOf(':');
            if (colon > 0 && !host.EndsWith("]")) host = host.Substring(0, colon);   // a port in the pattern matches any port here
            string h = u.Host.ToLowerInvariant();
            host = host.ToLowerInvariant();
            if (host != "*" && u.Scheme != "file")
            {
                if (host.StartsWith("*.")) { string b = host.Substring(2); if (h != b && !h.EndsWith("." + b)) return false; }
                else if (h != host) return false;
            }
            string target = u.AbsolutePath + u.Query;
            var re = "^" + Regex.Escape(path).Replace("\\*", ".*") + "$";
            return Regex.IsMatch(target, re);
        }

        /// <summary>Whether the extension can read and change the page at url (its host patterns cover it).</summary>
        public static bool CanAccess(string id, string url) => HostPatterns(Manifest(id)).Any(p => Matches(p, url));

        // ---------- details page (Chrome's chrome://extensions/?id=) ----------

        public static Dictionary<string, object> Details(string id)
        {
            var m = Manifest(id);
            if (m == null || !Index.TryGetValue(id, out var folder)) return null;
            long size = 0;
            try { size = new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); } catch { }
            var commands = new List<Dictionary<string, object>>();
            if (m.TryGetValue("commands", out var co) && co is Dictionary<string, object> cmds)
                foreach (var kv in cmds)
                {
                    if (kv.Key == "_execute_action" || kv.Key == "_execute_browser_action" || kv.Key == "_execute_page_action") continue;
                    var d = kv.Value as Dictionary<string, object>;
                    string desc = d != null && d.TryGetValue("description", out var de) ? Localize(folder, m, de as string) : null;
                    string key = null;
                    if (d != null && d.TryGetValue("suggested_key", out var sk))
                        key = sk as string ?? (sk is Dictionary<string, object> skd && (skd.TryGetValue("windows", out var w) || skd.TryGetValue("default", out w)) ? w as string : null);
                    commands.Add(new Dictionary<string, object> { { "name", kv.Key }, { "description", desc ?? kv.Key }, { "key", key ?? "" } });
                }
            bool store = folder.StartsWith(Store.ExtensionsDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            return new Dictionary<string, object>
            {
                { "id", id }, { "version", Get(m, "version_name") ?? Get(m, "version") ?? "" },
                { "size", size }, { "access", AccessWarnings(m) }, { "hosts", HostPatterns(m) },
                { "store", store }, { "path", store ? "" : folder }, { "commands", commands },
                { "homepage", Get(m, "homepage_url") ?? (store ? "https://chromewebstore.google.com/detail/" + id : "") },
                { "pinned", IsPinned(id) }, { "shortcut", Store.Settings.ExtensionShortcuts.TryGetValue(id, out var sc) ? sc : "" },
            };
        }

        static string IconDataUrl(string id, string name)
        {
            try
            {
                using (var img = IconOrLetter(id, name, 48))
                {
                    if (img == null) return null;
                    using (var ms = new MemoryStream())
                    {
                        img.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                        return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
                    }
                }
            }
            catch { return null; }
        }
    }

    /// <summary>CRX3 reader: "Cr24", version 3, header length, CrxFileHeader (protobuf), then the zip.</summary>
    internal static class Crx
    {
        public sealed class Result { public byte[] Zip; public byte[] PublicKey; }

        public static Result Parse(byte[] b, string expectedId)
        {
            if (b.Length < 16 || Encoding.ASCII.GetString(b, 0, 4) != "Cr24") throw new Exception("Not a CRX package");
            int version = BitConverter.ToInt32(b, 4);
            // CRX2 has a weaker signature scheme (SHA-1) and the Chrome Web Store no longer serves it: refused
            if (version != 3) throw new Exception("Unsupported package format (CRX" + version + ")");
            int hlen = BitConverter.ToInt32(b, 8);
            if (hlen <= 0 || 12 + hlen > b.Length) throw new Exception("Damaged package");
            var header = Slice(b, 12, hlen);
            var zip = Slice(b, 12 + hlen, b.Length - 12 - hlen);
            byte[] signedHeader = null;
            var rsaProofs = new List<KeyValuePair<byte[], byte[]>>();
            foreach (var f in Proto(header))
            {
                if (f.Key == 10000) signedHeader = f.Value;                    // SignedData { crx_id }
                if (f.Key != 2) continue;                                       // sha256_with_rsa proofs
                byte[] key = null, sig = null;
                foreach (var g in Proto(f.Value)) { if (g.Key == 1) key = g.Value; else if (g.Key == 2) sig = g.Value; }
                if (key != null && sig != null) rsaProofs.Add(new KeyValuePair<byte[], byte[]>(key, sig));
            }
            if (signedHeader == null) throw new Exception("Package is not signed");
            // the ID the package claims must be the one we asked for
            byte[] crxId = null;
            foreach (var f in Proto(signedHeader)) if (f.Key == 1) crxId = f.Value;
            if (crxId == null || crxId.Length != 16 || IdFromHash(crxId) != expectedId) throw new Exception("Package ID does not match");
            // what is signed (CRX3 spec): "CRX3 SignedData\0" + LE32 length + signed header + the zip archive
            var prefix = Encoding.ASCII.GetBytes("CRX3 SignedData\0");
            var signedData = new byte[prefix.Length + 4 + signedHeader.Length + zip.Length];
            Buffer.BlockCopy(prefix, 0, signedData, 0, prefix.Length);
            Buffer.BlockCopy(BitConverter.GetBytes(signedHeader.Length), 0, signedData, prefix.Length, 4);
            Buffer.BlockCopy(signedHeader, 0, signedData, prefix.Length + 4, signedHeader.Length);
            Buffer.BlockCopy(zip, 0, signedData, prefix.Length + 4 + signedHeader.Length, zip.Length);
            // the developer key (its hash is the extension ID) must have a valid RSA-SHA256 signature
            byte[] devKey = null;
            foreach (var p in rsaProofs)
                if (IdOf(p.Key) == expectedId && VerifyRsa(p.Key, signedData, p.Value)) devKey = p.Key;
            if (devKey == null) throw new Exception("Signature check failed");
            return new Result { Zip = zip, PublicKey = devKey };
        }

        /// <summary>RSA PKCS#1 v1.5 SHA-256 signature check with a DER SubjectPublicKeyInfo key.</summary>
        static bool VerifyRsa(byte[] spki, byte[] data, byte[] signature)
        {
            try
            {
                var p = new RSAParameters();
                int i = 0;
                Der(spki, ref i, 0x30, out int _);                 // SubjectPublicKeyInfo
                Der(spki, ref i, 0x30, out int algLen); i += algLen; // AlgorithmIdentifier (skipped)
                Der(spki, ref i, 0x03, out int _); i++;            // BIT STRING, unused-bits byte
                Der(spki, ref i, 0x30, out int _);                 // RSAPublicKey
                Der(spki, ref i, 0x02, out int nLen); p.Modulus = Unsigned(spki, i, nLen); i += nLen;
                Der(spki, ref i, 0x02, out int eLen); p.Exponent = Unsigned(spki, i, eLen);
                if (p.Modulus.Length < 256) return false;          // less than 2048-bit keys refused
                using (var rsa = new RSACng())
                {
                    rsa.ImportParameters(p);
                    return rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                }
            }
            catch { return false; }
        }

        static void Der(byte[] b, ref int i, byte tag, out int len)
        {
            if (b[i++] != tag) throw new FormatException("DER");
            int l = b[i++];
            if ((l & 0x80) != 0) { int n = l & 0x7F; l = 0; for (int k = 0; k < n; k++) l = (l << 8) | b[i++]; }
            len = l;
        }

        static byte[] Unsigned(byte[] b, int start, int len)
        {
            while (len > 1 && b[start] == 0) { start++; len--; }
            return Slice(b, start, len);
        }

        static string IdFromHash(byte[] h16)
        {
            var sb = new StringBuilder(32);
            for (int i = 0; i < 16; i++) { sb.Append((char)('a' + (h16[i] >> 4))); sb.Append((char)('a' + (h16[i] & 15))); }
            return sb.ToString();
        }

        public static string IdOf(byte[] publicKey)
        {
            using (var sha = SHA256.Create())
            {
                var h = sha.ComputeHash(publicKey);
                var sb = new StringBuilder(32);
                for (int i = 0; i < 16; i++) { sb.Append((char)('a' + (h[i] >> 4))); sb.Append((char)('a' + (h[i] & 15))); }
                return sb.ToString();
            }
        }

        static byte[] Slice(byte[] b, int start, int len) { var r = new byte[len]; Array.Copy(b, start, r, 0, len); return r; }

        /// <summary>Length-delimited fields of a protobuf message (other wire types skipped).</summary>
        static IEnumerable<KeyValuePair<int, byte[]>> Proto(byte[] m)
        {
            int i = 0;
            var res = new List<KeyValuePair<int, byte[]>>();
            while (i < m.Length)
            {
                ulong tag = Varint(m, ref i);
                int field = (int)(tag >> 3), wire = (int)(tag & 7);
                if (wire == 2)
                {
                    int len = (int)Varint(m, ref i);
                    if (len < 0 || i + len > m.Length) break;
                    res.Add(new KeyValuePair<int, byte[]>(field, Slice(m, i, len)));
                    i += len;
                }
                else if (wire == 0) Varint(m, ref i);
                else if (wire == 1) i += 8;
                else if (wire == 5) i += 4;
                else break;
            }
            return res;
        }

        static ulong Varint(byte[] m, ref int i)
        {
            ulong v = 0; int shift = 0;
            while (i < m.Length)
            {
                byte c = m[i++];
                v |= (ulong)(c & 0x7F) << shift;
                if ((c & 0x80) == 0) break;
                shift += 7;
            }
            return v;
        }
    }
}
