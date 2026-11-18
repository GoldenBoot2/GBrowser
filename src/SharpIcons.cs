using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace GBrowser
{
    /// <summary>Sharp site icons. The engine's GetFaviconAsync gives only 16 x 16 pixels, which looks blurry on scaled
    /// screens (125 %: 20 px in lists, 30 px on the new tab page). Like Chrome, GBrowser picks the best icon the page
    /// itself declares (link rel=icon sizes, apple-touch-icon, the .ico's biggest frame), loads it through the
    /// engine's own network stack (DevTools Network.loadNetworkResource: same TLS / HTTP as the page, no cookies) and
    /// keeps it at up to 64 x 64. Once per site per run; never for private tabs or GBrowser's own pages.</summary>
    internal static class SharpIcons
    {
        const int Want = 64, MaxBytes = 512 * 1024;
        static readonly HashSet<string> tried = new HashSet<string>();

        const string Script = @"(() => { const out = [];
  for (const l of document.querySelectorAll('link[rel][href]')) {
    const r = (l.getAttribute('rel') || '').toLowerCase().split(/\s+/);
    const touch = r.includes('apple-touch-icon') || r.includes('apple-touch-icon-precomposed');
    if (!touch && !r.includes('icon')) continue;
    let s = 0; for (const m of ((l.sizes && l.sizes.value) || '').matchAll(/(\d+)x(\d+)/gi)) s = Math.max(s, +m[1]);
    if (!s && touch) s = 180;
    out.push([l.href, s, (l.type || '').toLowerCase()]); }
  return out; })()";

        /// <summary>A sharper icon for this page as a PNG (null when the page offers nothing better than 16 px).</summary>
        public static async Task<byte[]> Fetch(CoreWebView2 core, string pageUrl, string engineIconUrl)
        {
            string key = Favicons.KeyOf(pageUrl);
            if (key == null) return null;
            lock (tried) if (!tried.Add(key)) return null;
            var cands = new List<Tuple<string, int>>();
            try
            {
                var raw = Store.FromJson<object[]>(await core.ExecuteScriptAsync(Script)) ?? new object[0];
                foreach (var o in raw)
                {
                    var a = o as object[];
                    if (a == null || a.Length < 3) continue;
                    string href = a[0] as string, type = a[2] as string ?? "";
                    int size = Convert.ToInt32(a[1]);
                    if (string.IsNullOrEmpty(href) || !href.StartsWith("http")) continue;
                    if (type.Contains("svg") || href.Split('?')[0].EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) continue;   // no SVG decoder
                    cands.Add(Tuple.Create(href, size));
                }
            }
            catch { }
            if (!string.IsNullOrEmpty(engineIconUrl) && engineIconUrl.StartsWith("http") && !cands.Any(c => c.Item1 == engineIconUrl))
                cands.Add(Tuple.Create(engineIconUrl, 0));   // size unknown: an .ico often holds 32 / 48 px frames
            // best first: the smallest declared size that is at least 64, then bigger-is-better, unknown sizes last
            var order = cands.Where(c => c.Item2 >= Want).OrderBy(c => c.Item2)
                .Concat(cands.Where(c => c.Item2 > 16 && c.Item2 < Want).OrderByDescending(c => c.Item2))
                .Concat(cands.Where(c => c.Item2 == 0)).Take(3).ToList();
            if (order.Count == 0) return null;
            string frameId = null;
            try
            {
                var tree = Store.FromJson<Dictionary<string, object>>(await core.CallDevToolsProtocolMethodAsync("Page.getFrameTree", "{}"));
                frameId = (((tree["frameTree"] as Dictionary<string, object>)?["frame"]) as Dictionary<string, object>)?["id"] as string;
            }
            catch { }
            if (frameId == null) return null;
            foreach (var c in order)
            {
                var bytes = await Load(core, frameId, c.Item1);
                var png = bytes == null ? null : ToPng(bytes);
                if (png != null) return png;
            }
            return null;
        }

        static async Task<byte[]> Load(CoreWebView2 core, string frameId, string url)
        {
            string handle = null;
            try
            {
                var args = Store.ToJson(new Dictionary<string, object> {
                    { "frameId", frameId }, { "url", url },
                    { "options", new Dictionary<string, object> { { "disableCache", false }, { "includeCredentials", false } } } });
                var r = Store.FromJson<Dictionary<string, object>>(await core.CallDevToolsProtocolMethodAsync("Network.loadNetworkResource", args));
                var res = r["resource"] as Dictionary<string, object>;
                if (res == null || !(res["success"] is bool ok && ok)) return null;
                if (res.TryGetValue("httpStatusCode", out var st) && Convert.ToInt32(st) != 200) return null;
                handle = res["stream"] as string;
                if (handle == null) return null;
                var ms = new MemoryStream();
                for (int i = 0; i < 64; i++)
                {
                    var chunk = Store.FromJson<Dictionary<string, object>>(await core.CallDevToolsProtocolMethodAsync("IO.read",
                        Store.ToJson(new Dictionary<string, object> { { "handle", handle }, { "size", 65536 } })));
                    string data = chunk["data"] as string ?? "";
                    var b = chunk.TryGetValue("base64Encoded", out var enc) && enc is bool e && e ? Convert.FromBase64String(data) : Encoding.UTF8.GetBytes(data);
                    ms.Write(b, 0, b.Length);
                    if (ms.Length > MaxBytes) return null;
                    if (chunk.TryGetValue("eof", out var eof) && eof is bool done && done) break;
                }
                return ms.ToArray();
            }
            catch { return null; }
            finally
            {
                if (handle != null) try { await core.CallDevToolsProtocolMethodAsync("IO.close", Store.ToJson(new Dictionary<string, object> { { "handle", handle } })); } catch { }
            }
        }

        /// <summary>PNG / ICO / JPEG / GIF / BMP -> PNG of at most 64 x 64; null when not decodable or not bigger than 16 px.</summary>
        static byte[] ToPng(byte[] data)
        {
            try
            {
                Bitmap src;
                bool ico = data.Length > 6 && data[0] == 0 && data[1] == 0 && data[2] == 1 && data[3] == 0;
                using (var ms = new MemoryStream(data))
                {
                    if (ico) using (var icon = new Icon(ms, 256, 256)) src = icon.ToBitmap();   // the frame closest to 256: the biggest
                    else using (var img = Image.FromStream(ms)) src = new Bitmap(img);
                }
                using (src)
                {
                    if (src.Width <= 16 || src.Height <= 16) return null;
                    int w = Math.Min(Want, src.Width), h = Math.Min(Want, src.Height);
                    if (src.Width != src.Height) { int m = Math.Min(Want, Math.Max(src.Width, src.Height)); w = h = m; }
                    using (var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                    {
                        using (var g = Graphics.FromImage(dst))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.CompositingQuality = CompositingQuality.HighQuality;
                            // keep the aspect ratio, centred
                            float s = Math.Min((float)w / src.Width, (float)h / src.Height);
                            float dw = src.Width * s, dh = src.Height * s;
                            using (var ia = new ImageAttributes())
                            {
                                ia.SetWrapMode(WrapMode.TileFlipXY);
                                g.DrawImage(src, new Rectangle((int)((w - dw) / 2), (int)((h - dh) / 2), (int)dw, (int)dh), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
                            }
                        }
                        using (var o = new MemoryStream()) { dst.Save(o, ImageFormat.Png); return o.ToArray(); }
                    }
                }
            }
            catch { return null; }
        }
    }
}
