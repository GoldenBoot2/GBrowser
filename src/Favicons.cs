using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace GBrowser
{
    /// <summary>Site icons remembered from visited pages (like Chrome's favicon database), for bookmarks, history and
    /// the new tab page. Icons come only from pages the engine loaded (CoreWebView2.GetFaviconAsync), never from a
    /// separate download, and never from private tabs. One file per site, named by a hash (no site names on disk),
    /// encrypted like history (SecureFile), because the set of icons shows which sites were visited.</summary>
    internal static class Favicons
    {
        static readonly string Dir = Path.Combine(Store.DataDir, "Favicons");
        static readonly Dictionary<string, byte[]> cache = new Dictionary<string, byte[]>();   // key -> png (null = none)
        static readonly Dictionary<string, Image> images = new Dictionary<string, Image>();
        static readonly object gate = new object();

        /// <summary>Site key: the host without "www." (www.bbc.com and bbc.com share an icon, as in Chrome's lists).</summary>
        public static string KeyOf(string url)
        {
            string h = Blocking.HostOf(url);
            if (h == null) return null;
            return h.StartsWith("www.") ? h.Substring(4) : h;
        }

        static string FileOf(string key)
        {
            using (var sha = SHA256.Create())
            {
                var d = sha.ComputeHash(Encoding.UTF8.GetBytes("gbfav:" + key));
                return Path.Combine(Dir, string.Concat(d.Take(16).Select(b => b.ToString("x2"))) + ".fav");
            }
        }

        public static void Save(string url, byte[] png)
        {
            string key = KeyOf(url);
            if (key == null || png == null || png.Length == 0 || png.Length > 256 * 1024) return;
            lock (gate)
            {
                if (cache.TryGetValue(key, out var old) && old != null && old.SequenceEqual(png)) return;
                cache[key] = png;
                if (images.TryGetValue(key, out var img)) { img?.Dispose(); images.Remove(key); }
            }
            try { SecureFile.Write(FileOf(key), Convert.ToBase64String(png)); } catch { }
        }

        public static byte[] Get(string url)
        {
            string key = KeyOf(url);
            if (key == null) return null;
            lock (gate)
            {
                if (cache.TryGetValue(key, out var png)) return png;
                try
                {
                    var b64 = SecureFile.Read(FileOf(key), out _);
                    png = b64 == null ? null : Convert.FromBase64String(b64);
                }
                catch { png = null; }
                cache[key] = png;
                return png;
            }
        }

        /// <summary>The icon as an image (cached), or null.</summary>
        public static Image Image(string url)
        {
            string key = KeyOf(url);
            if (key == null) return null;
            lock (gate) if (images.TryGetValue(key, out var hit)) return hit;
            var png = Get(url);
            Image img = null;
            if (png != null) try { using (var ms = new MemoryStream(png)) img = new Bitmap(System.Drawing.Image.FromStream(ms)); } catch { }
            lock (gate) images[key] = img;
            return img;
        }

        public static string DataUrl(string url)
        {
            var png = Get(url);
            return png == null ? null : "data:image/png;base64," + Convert.ToBase64String(png);
        }

        /// <summary>Clearing history: drop every icon except the bookmarked sites' (as Chrome does).</summary>
        public static void ClearExcept(IEnumerable<string> keepUrls)
        {
            var keep = new HashSet<string>(keepUrls.Select(KeyOf).Where(k => k != null).Select(FileOf), StringComparer.OrdinalIgnoreCase);
            lock (gate)
            {
                foreach (var k in cache.Keys.Where(k => !keep.Contains(FileOf(k))).ToList()) cache.Remove(k);
                foreach (var k in images.Keys.Where(k => !keep.Contains(FileOf(k))).ToList()) { images[k]?.Dispose(); images.Remove(k); }
            }
            try
            {
                if (Directory.Exists(Dir))
                    foreach (var f in Directory.GetFiles(Dir, "*.fav"))
                        if (!keep.Contains(f)) File.Delete(f);
            }
            catch { }
        }
    }
}
