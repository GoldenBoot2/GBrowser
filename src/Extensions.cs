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
        static readonly string IndexFile = Path.Combine(Store.DataDir, "extensions.json");
        static Dictionary<string, string> index;   // extension id -> folder

        public static Dictionary<string, string> Index
        {
            get
            {
                if (index == null)
                {
                    try { index = File.Exists(IndexFile) ? Store.FromJson<Dictionary<string, string>>(File.ReadAllText(IndexFile)) : null; } catch { }
                    if (index == null) index = new Dictionary<string, string>();
                }
                return index;
            }
        }

        static void SaveIndex()
        {
            try { File.WriteAllText(IndexFile, Store.ToJson(Index), new UTF8Encoding(false)); } catch { }
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

        public static async Task<CoreWebView2BrowserExtension> InstallFromStore(CoreWebView2Profile profile, string id, string userAgent, string browserVersion)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            string major = (browserVersion ?? "157").Split('.')[0];
            string url = "https://clients2.google.com/service/update2/crx?response=redirect&prodversion=" + major +
                         ".0.0.0&acceptformat=crx2,crx3&x=id%3D" + id + "%26installsource%3Dondemand%26uc";
            byte[] crx;
            using (var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate }))
            {
                http.Timeout = TimeSpan.FromSeconds(90);
                if (!string.IsNullOrEmpty(userAgent)) http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
                var resp = await http.GetAsync(url);
                if (!resp.IsSuccessStatusCode) throw new Exception("Download failed (" + (int)resp.StatusCode + ")");
                crx = await resp.Content.ReadAsByteArrayAsync();
            }
            var parsed = Crx.Parse(crx, id);
            string folder = Path.Combine(Store.ExtensionsDir, id);
            await Task.Run(() => Unpack(parsed.Zip, folder, parsed.PublicKey));
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

        static void Unpack(byte[] zip, string folder, byte[] publicKey)
        {
            string tmp = folder + ".new";
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);
            string root = Path.GetFullPath(tmp) + Path.DirectorySeparatorChar;
            using (var ms = new MemoryStream(zip))
            using (var za = new ZipArchive(ms, ZipArchiveMode.Read))
            {
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
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Directory.Move(tmp, folder);
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
            if (version == 2)
            {
                int keyLen = BitConverter.ToInt32(b, 8), sigLen = BitConverter.ToInt32(b, 12);
                var key = new byte[keyLen]; Array.Copy(b, 16, key, 0, keyLen);
                int start = 16 + keyLen + sigLen;
                return new Result { PublicKey = key, Zip = Slice(b, start, b.Length - start) };
            }
            if (version != 3) throw new Exception("Unknown CRX version " + version);
            int hlen = BitConverter.ToInt32(b, 8);
            var header = Slice(b, 12, hlen);
            var zip = Slice(b, 12 + hlen, b.Length - 12 - hlen);
            byte[] chosen = null;
            foreach (var f in Proto(header))
            {
                if (f.Key != 2 && f.Key != 3) continue;   // sha256_with_rsa / sha256_with_ecdsa proofs
                foreach (var g in Proto(f.Value))
                    if (g.Key == 1 && IdOf(g.Value) == expectedId) chosen = g.Value;
            }
            return new Result { Zip = zip, PublicKey = chosen };
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
