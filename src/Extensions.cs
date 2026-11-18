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
            return ext;
        }

        public static async Task<CoreWebView2BrowserExtension> LoadUnpacked(CoreWebView2Profile profile, string folder)
        {
            if (!File.Exists(Path.Combine(folder, "manifest.json"))) throw new Exception("No manifest.json");
            var ext = await profile.AddBrowserExtensionAsync(folder);
            Index[ext.Id] = folder;
            SaveIndex();
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
        static string LocalName(string dir, Dictionary<string, object> m)
        {
            string name = m.TryGetValue("name", out var n) ? n as string : null;
            if (name == null || !name.StartsWith("__MSG_")) return name;
            try
            {
                string key = name.Substring(6, name.Length - 8);
                string loc = m.TryGetValue("default_locale", out var dl) ? dl as string : "en";
                var msgs = Store.FromJson<Dictionary<string, object>>(File.ReadAllText(Path.Combine(dir, "_locales", loc, "messages.json"), Encoding.UTF8).TrimStart('\uFEFF'));
                foreach (var kv in msgs)
                    if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase) && kv.Value is Dictionary<string, object> d && d.TryGetValue("message", out var msg))
                        return msg as string;
            }
            catch { }
            return name;
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
                    { "description", Get(m, "description") ?? "" },
                    { "popup", PopupPage(m) ?? "" }, { "options", OptionsPage(m) ?? "" },
                    { "icon", IconDataUrl(e.Id, m) ?? "" },
                    { "store", Index.TryGetValue(e.Id, out var f) && f.StartsWith(Store.ExtensionsDir, StringComparison.OrdinalIgnoreCase) },
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

        public static Image Icon(string id)
        {
            try { var f = IconFile(id, Manifest(id)); if (f != null) using (var img = Image.FromFile(f)) return new Bitmap(img, 16, 16); } catch { }
            return null;
        }

        static string IconDataUrl(string id, Dictionary<string, object> m)
        {
            try
            {
                var f = IconFile(id, m);
                if (f == null) return null;
                var bytes = File.ReadAllBytes(f);
                if (bytes.Length > 200000) return null;
                string mime = f.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? "image/svg+xml" : "image/png";
                return "data:" + mime + ";base64," + Convert.ToBase64String(bytes);
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
