using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace GBrowser
{
    /// <summary>Multi-part downloads (like IDM): the file is fetched in parts over several connections with HTTP Range
    /// requests and written into one file. Adaptive, because more connections only help when a server limits each
    /// connection (measured 18-11-2026: a CDN gave the full line speed on 1 connection; two test servers answered
    /// 429 to extra connections): it starts with one connection and adds another (splitting the biggest remaining
    /// part) only while the total speed keeps rising, up to Settings.DownloadConnections; a refusal (429/503/403)
    /// stops adding. Parts that finish take over half of the biggest remaining part. Pause, resume (also after a
    /// restart: progress is saved) and cancel.
    /// Safety: only plain GET downloads whose server answers a range request with 206 and the same size the engine
    /// expected; Cloudflare-protected hosts are left to the engine (a non-browser client there can get the IP
    /// blocked); the client says who it is (GBrowser, not "Chrome") and sends each host only its own cookies.</summary>
    internal sealed class FastDownload
    {
        sealed class Part
        {
            public long Start, End, Pos;   // End inclusive; Pos = next byte to fetch
            public bool Running;
            public long Left => Math.Max(0, End - Pos + 1);
        }

        /// <summary>Settings > Downloads > Speed limit: one budget shared by all of GBrowser's downloads (token bucket
        /// holding at most one second of data), so browsing stays smooth during big downloads.</summary>
        static class Throttle
        {
            static readonly object g = new object();
            static double tokens;
            static DateTime last = DateTime.UtcNow;

            public static async Task Take(int n, CancellationToken ct)
            {
                while (true)
                {
                    int limit = Store.Settings.DownloadSpeedLimit;
                    if (limit <= 0) return;
                    double rate = limit * 1024.0;
                    TimeSpan wait;
                    lock (g)
                    {
                        var now = DateTime.UtcNow;
                        tokens = Math.Min(rate, tokens + (now - last).TotalSeconds * rate);
                        last = now;
                        if (tokens >= n || tokens >= rate) { tokens -= n; return; }   // a full bucket lets a big chunk through
                        wait = TimeSpan.FromSeconds((n - tokens) / rate);
                    }
                    await Task.Delay(wait < TimeSpan.FromMilliseconds(5) ? TimeSpan.FromMilliseconds(5) : wait, ct);
                }
            }
        }

        public static string Agent => "GBrowser/" + Updater.Current + " (Windows NT 10.0; Win64; x64) downloader";
        const long MinSplit = 1 << 20;   // never split below 1 MB left

        public readonly Downloads.Item Item;
        readonly CoreWebView2CookieManager cookies;
        readonly List<Part> parts = new List<Part>();
        readonly object gate = new object();
        CancellationTokenSource run;
        FileStream file;
        string finalUrl;
        int allowed;
        int active { get { lock (gate) return parts.Count(x => x.Running); } }
        bool refused;
        long lastBytes;
        double lastSpeed;
        public double Speed { get; private set; }     // bytes per second, smoothed
        public int Connections => active;
        readonly List<Task> workers = new List<Task>();

        static readonly HttpClient http = MakeClient();

        static HttpClient MakeClient()
        {
            ServicePointManager.DefaultConnectionLimit = Math.Max(ServicePointManager.DefaultConnectionLimit, 64);
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | (SecurityProtocolType)12288;
            var h = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None, MaxConnectionsPerServer = 64 };
            var c = new HttpClient(h) { Timeout = Timeout.InfiniteTimeSpan };
            return c;
        }

        FastDownload(Downloads.Item item, CoreWebView2CookieManager cookies)
        {
            Item = item;
            this.cookies = cookies;
        }

        // ------------------------------------------------------------ probe (decides whether to take the download)

        sealed class Probe { public string FinalUrl, ETag, LastModified; public long Total; }

        /// <summary>Follows redirects by hand (cookies only for each host's own requests) and asks for byte 0.
        /// Returns null when this download is better left to the engine.</summary>
        static async Task<Probe> Check(string url, string referer, CoreWebView2CookieManager cm, long expected, CancellationToken ct)
        {
            string u = url;
            for (int hop = 0; hop < 10; hop++)
            {
                using (var req = await Request(u, referer, cm, 0, 0))
                using (var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    int code = (int)res.StatusCode;
                    if (code >= 300 && code < 400 && res.Headers.Location != null)
                    {
                        u = new Uri(new Uri(u), res.Headers.Location).AbsoluteUri;
                        if (!u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return null;
                        continue;
                    }
                    bool cf = res.Headers.Contains("cf-ray") || res.Headers.Server.Any(p => p.Product != null && p.Product.Name.Equals("cloudflare", StringComparison.OrdinalIgnoreCase));
                    if (cf || code != 206) return null;
                    var range = res.Content.Headers.ContentRange;
                    if (range == null || !range.Length.HasValue) return null;
                    long total = range.Length.Value;
                    if (expected > 0 && total != expected) return null;   // not the same file the engine was getting
                    return new Probe
                    {
                        FinalUrl = u, Total = total, ETag = res.Headers.ETag?.ToString(),
                        LastModified = res.Content.Headers.LastModified?.ToString("R"),
                    };
                }
            }
            return null;
        }

        /// <summary>Programs, installers and scripts: Microsoft Defender SmartScreen checks them (download reputation)
        /// in the engine before they are saved, and warns, like Chrome. GBrowser's multi-part engine has no such
        /// check (its files are marked and scanned only after saving), so these always go to the engine.</summary>
        static readonly string[] RiskyTypes =
        {
            ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".appinstaller", ".msp", ".msu",
            ".bat", ".cmd", ".com", ".scr", ".pif", ".cpl", ".dll", ".sys", ".ocx", ".lnk", ".url", ".reg", ".inf",
            ".ps1", ".psm1", ".psd1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".jar", ".application",
            ".gadget", ".msc", ".scf", ".iso", ".img", ".vhd", ".vhdx", ".docm", ".xlsm", ".pptm",
        };

        public static bool Risky(string path) => RiskyTypes.Contains(Path.GetExtension(path ?? "").ToLowerInvariant());

        static async Task<HttpRequestMessage> Request(string url, string referer, CoreWebView2CookieManager cm, long from, long to)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", Agent);
            req.Headers.Range = new RangeHeaderValue(from, to);
            req.Headers.AcceptEncoding.Clear();
            req.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
            if (!string.IsNullOrEmpty(referer)) try { req.Headers.Referrer = new Uri(referer); } catch { }
            if (cm != null)
            {
                try
                {
                    var list = await cm.GetCookiesAsync(url);   // only this address's own cookies
                    if (list.Count > 0) req.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", list.Select(c => c.Name + "=" + c.Value)));
                }
                catch { }
            }
            return req;
        }

        /// <summary>DownloadStarting: takes over big plain downloads that the server can serve in parts.</summary>
        public static async Task<FastDownload> TryTakeOver(CoreWebView2DownloadOperation op, string path, string referer, CoreWebView2CookieManager cm)
        {
            var st = Store.Settings;
            if (st.DownloadConnections <= 1) return null;
            string url = op.Uri ?? "";
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return null;
            long expected = (long)(op.TotalBytesToReceive ?? 0);
            if (expected > 0 && expected < 2 * MinSplit) return null;   // small files: nothing to gain
            if (Risky(path)) return null;   // programs and scripts: the engine's SmartScreen check before the download
            Probe p;
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
                try { p = await Check(url, referer, cm, expected, cts.Token); } catch { return null; }
            if (p == null || p.Total < 2 * MinSplit) return null;
            var item = Downloads.NewItem(url, path, p.Total);
            item.Engine = "multi";
            item.Referer = referer;
            item.ETag = p.ETag; item.LastModified = p.LastModified;
            var d = new FastDownload(item, cm) { finalUrl = p.FinalUrl };
            item.Fast = d;
            d.parts.Add(new Part { Start = 0, End = p.Total - 1, Pos = 0 });
            d.Start();
            return d;
        }

        /// <summary>"Download anyway" after a SmartScreen block (the user confirmed): one plain request by GBrowser,
        /// whatever the file type or server; the file still gets the internet mark + antivirus scan, so Windows
        /// SmartScreen checks it again when it is opened.</summary>
        public static void Plain(Downloads.Item item, CoreWebView2CookieManager cm)
        {
            item.State = "in_progress"; item.Reason = null; item.Received = 0;
            Downloads.MarkRunning(item);
            Downloads.Notify(item);
            Task.Run(async () =>
            {
                string part = item.Path + ".gbdownload";
                try
                {
                    string u = item.Url;
                    HttpResponseMessage res = null;
                    for (int hop = 0; hop < 10; hop++)
                    {
                        var req = new HttpRequestMessage(HttpMethod.Get, u);
                        req.Headers.TryAddWithoutValidation("User-Agent", Agent);
                        if (!string.IsNullOrEmpty(item.Referer)) try { req.Headers.Referrer = new Uri(item.Referer); } catch { }
                        try { var list = await cm.GetCookiesAsync(u); if (list.Count > 0) req.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", list.Select(c => c.Name + "=" + c.Value))); } catch { }
                        res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                        int code = (int)res.StatusCode;
                        if (code >= 300 && code < 400 && res.Headers.Location != null) { u = new Uri(new Uri(u), res.Headers.Location).AbsoluteUri; res.Dispose(); res = null; continue; }
                        break;
                    }
                    if (res == null || !res.IsSuccessStatusCode) throw new Exception("Server error");
                    using (res)
                    {
                        item.Total = res.Content.Headers.ContentLength ?? item.Total;
                        using (var s = await res.Content.ReadAsStreamAsync())
                        using (var o = File.Create(part))
                        {
                            var buf = new byte[1 << 16];
                            int n;
                            while ((n = await s.ReadAsync(buf, 0, buf.Length)) > 0)
                            {
                                await o.WriteAsync(buf, 0, n);
                                item.Received += n;
                                Downloads.Notify(item);
                            }
                        }
                    }
                    if (File.Exists(item.Path)) File.Delete(item.Path);
                    File.Move(part, item.Path);
                    Attachment.Mark(item.Path, item.Url, item.Referer);
                    item.State = "completed";
                    Downloads.CompleteReplace(item);
                    Downloads.Finished(item);
                }
                catch (Exception ex) { try { File.Delete(part); } catch { } item.State = "interrupted"; item.Reason = ex.Message; }
                finally { Downloads.MarkStopped(item); Downloads.Save(); Downloads.Notify(item); }
            });
        }

        /// <summary>A multi-part download saved in an earlier session (paused or interrupted).</summary>
        public static FastDownload Restore(Downloads.Item item, CoreWebView2CookieManager cm)
        {
            var d = new FastDownload(item, cm);
            item.Fast = d;
            foreach (var s in (item.Parts ?? "").Split(';'))
            {
                var f = s.Split(',');
                if (f.Length == 3 && long.TryParse(f[0], out var a) && long.TryParse(f[1], out var b) && long.TryParse(f[2], out var p))
                    d.parts.Add(new Part { Start = a, End = b, Pos = p });
            }
            if (d.parts.Count == 0) d.parts.Add(new Part { Start = 0, End = item.Total - 1, Pos = 0 });
            return d;
        }

        string PartFile => Item.Path + ".gbdownload";

        // ------------------------------------------------------------ running

        public void Start()
        {
            lock (gate)
            {
                if (run != null) return;
                run = new CancellationTokenSource();
                refused = false;
                allowed = Math.Max(1, Math.Min(16, Store.Settings.DownloadConnections));
                Item.State = "in_progress";
                Item.Reason = null;
            }
            Downloads.MarkRunning(Item);
            Downloads.Notify(Item);
            Task.Run(() => Main(run.Token));
        }

        async Task Main(CancellationToken ct)
        {
            try
            {
                if (finalUrl == null)
                {
                    // resumed: same file on the server? (else start again from the beginning)
                    var p = await Check(Item.Url, Item.Referer, cookies, Item.Total, ct);
                    if (p == null) throw new Exception("Can't resume");
                    if ((Item.ETag != null && p.ETag != Item.ETag) || (Item.LastModified != null && p.LastModified != Item.LastModified))
                    {
                        lock (gate) { parts.Clear(); parts.Add(new Part { Start = 0, End = p.Total - 1, Pos = 0 }); }
                        Item.ETag = p.ETag; Item.LastModified = p.LastModified;
                    }
                    finalUrl = p.FinalUrl;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(Item.Path));
                file = new FileStream(PartFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 1 << 16, FileOptions.Asynchronous);
                if (file.Length != Item.Total) file.SetLength(Item.Total);

                FillGaps();
                lock (gate) workers.Clear();
                lastBytes = Received();
                DateTime lastAdd = DateTime.UtcNow, lastSave = DateTime.UtcNow;
                while (!ct.IsCancellationRequested)
                {
                    lock (gate)
                    {
                        // start connections: first the waiting parts, then (while it helps) split the biggest one
                        foreach (var part in parts.Where(x => !x.Running && x.Left > 0).ToList())
                            if (parts.Count(x => x.Running) < allowed) Launch(part, ct);
                    }
                    if (active == 0)
                    {
                        if (Received() >= Item.Total) break;
                        bool waiting;
                        lock (gate) waiting = parts.Any(x => x.Left > 0);
                        if (!waiting) FillGaps();
                    }
                    await Task.Delay(500, ct);
                    UpdateSpeed(0.5);
                    lock (gate)
                    {
                        // adaptive: one more connection every ~2 s while the speed still rises by more than 10%
                        int running = parts.Count(x => x.Running);
                        if (!refused && running < allowed && (DateTime.UtcNow - lastAdd).TotalSeconds >= 2)
                        {
                            if (running > 1 && Speed < lastSpeed * 1.1) allowed = running;   // no gain from the last one
                            else
                            {
                                lastSpeed = Speed;
                                var big = parts.Where(x => x.Running && x.Left > 2 * MinSplit).OrderByDescending(x => x.Left).FirstOrDefault();
                                if (big != null)
                                {
                                    var half = Split(big);
                                    Launch(half, ct);
                                    lastAdd = DateTime.UtcNow;
                                }
                            }
                        }
                    }
                    Item.Received = Received();
                    Item.Parts = PartsText();
                    Downloads.Notify(Item);
                    // progress on disk every 5 s: after a crash or a closed window it resumes from about here
                    if ((DateTime.UtcNow - lastSave).TotalSeconds >= 5)
                    {
                        lastSave = DateTime.UtcNow;
                        try { await file.FlushAsync(); Downloads.Save(); } catch { }
                    }
                }
                Task[] all;
                lock (gate) all = workers.ToArray();
                await Task.WhenAll(all).ContinueWith(_ => { });
                ct.ThrowIfCancellationRequested();
                if (Received() < Item.Total) throw new Exception("Incomplete");
                await file.FlushAsync();
                file.Dispose(); file = null;
                string dest = Item.Path;
                if (File.Exists(dest)) dest = Unique(dest);
                File.Move(PartFile, dest);
                Item.Path = dest;
                Item.Received = Item.Total;
                Attachment.Mark(dest, Item.Url, Item.Referer);   // "downloaded from the internet" + antivirus scan
                Item.State = "completed";
                Item.Parts = null;
                Downloads.CompleteReplace(Item);
                Downloads.Finished(Item);
            }
            catch (OperationCanceledException) { if (Item.State != "cancelled") Item.State = "paused"; }
            catch (Exception ex) { Item.State = "interrupted"; Item.Reason = ex.Message; }
            finally
            {
                // let every connection stop before the file is closed (pause / error)
                Task[] all;
                lock (gate) all = workers.ToArray();
                try { Task.WaitAll(all, 10000); } catch { }
                file?.Dispose(); file = null;
                lock (gate) { run = null; foreach (var p in parts) p.Running = false; }
                Downloads.MarkStopped(Item);
                Speed = 0;
                if (Item.State == "cancelled") { try { File.Delete(PartFile); } catch { } Item.State = "interrupted"; Item.Reason = "Cancelled"; Item.Parts = null; }
                else if (Item.State != "completed") Item.Parts = PartsText();
                Item.Received = Item.State == "completed" ? Item.Total : Received();
                Downloads.Save();
                Downloads.Notify(Item);
            }
        }

        static string Unique(string path)
        {
            string dir = Path.GetDirectoryName(path), name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
            for (int i = 1; ; i++) { string p = Path.Combine(dir, name + " (" + i + ")" + ext); if (!File.Exists(p)) return p; }
        }

        /// <summary>Second half of a part becomes a new part (the running connection stops at the new end).</summary>
        Part Split(Part p)
        {
            long mid = p.Pos + p.Left / 2;
            var n = new Part { Start = mid, End = p.End, Pos = mid };
            p.End = mid - 1;
            parts.Add(n);
            parts.Sort((a, b) => a.Start.CompareTo(b.Start));
            return n;
        }

        void Launch(Part p, CancellationToken ct)
        {
            p.Running = true;
            workers.Add(Task.Run(() => Worker(p, ct)));
        }

        async Task Worker(Part p, CancellationToken ct)
        {
            int failures = 0;
            Part mine = p;   // the part this connection works on now (changes when it takes over another range)
            var buf = new byte[1 << 16];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    long from, to;
                    lock (gate) { from = p.Pos; to = p.End; }
                    if (from > to)
                    {
                        // done: take over half of the biggest remaining part (IDM-style). The finished part stays in
                        // the list as it is (its bytes are done); the taken range is a new part of its own.
                        lock (gate)
                        {
                            var big = parts.Where(x => x != p && x.Running && x.Left > 2 * MinSplit).OrderByDescending(x => x.Left).FirstOrDefault();
                            if (big == null) return;
                            var n = Split(big);
                            p.Running = false;
                            n.Running = true;
                            p = n;
                            mine = n;
                        }
                        continue;
                    }
                    try
                    {
                        using (var req = await Request(finalUrl, Item.Referer, cookies, from, to))
                        using (var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct))
                        {
                            int code = (int)res.StatusCode;
                            if (code == 429 || code == 503 || code == 403)
                            {
                                lock (gate)
                                {
                                    // the server limits connections: this one stops, its range waits for another
                                    int running = parts.Count(x => x.Running);
                                    if (running > 1) { refused = true; allowed = Math.Max(1, running - 1); return; }
                                }
                                if (code == 403 && failures == 0) { finalUrl = (await Check(Item.Url, Item.Referer, cookies, Item.Total, ct))?.FinalUrl ?? finalUrl; }
                                throw new HttpRequestException(code + " " + res.ReasonPhrase);
                            }
                            if (code != 206) throw new HttpRequestException("No partial content (" + code + ")");
                            using (var s = await res.Content.ReadAsStreamAsync())
                            {
                                int n;
                                while ((n = await s.ReadAsync(buf, 0, buf.Length, ct)) > 0)
                                {
                                    await Throttle.Take(n, ct);
                                    long at, end;
                                    lock (gate) { at = p.Pos; end = p.End; }
                                    int take = (int)Math.Min(n, end - at + 1);
                                    if (take <= 0) break;   // the part was split: the rest belongs to another connection
                                    lock (file) { file.Position = at; file.Write(buf, 0, take); }
                                    lock (gate) p.Pos = at + take;
                                    failures = 0;
                                    if (take < n) break;
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) when (!ct.IsCancellationRequested)
                    {
                        if (++failures > 5) throw;
                        await Task.Delay(1000 * failures, ct);
                    }
                }
            }
            finally { lock (gate) mine.Running = false; }
        }

        long Received() { lock (gate) return parts.Sum(x => Math.Max(0, Math.Min(x.Pos, x.End + 1) - x.Start)); }

        /// <summary>Every byte of the file must belong to a part: a range no part covers (a damaged saved state)
        /// becomes a new part to fetch, so a download can never wait forever for bytes nobody is getting.</summary>
        void FillGaps()
        {
            lock (gate)
            {
                parts.RemoveAll(x => x.End < x.Start);
                parts.Sort((a, b) => a.Start.CompareTo(b.Start));
                long next = 0;
                var add = new List<Part>();
                foreach (var x in parts)
                {
                    if (x.Start > next) add.Add(new Part { Start = next, End = x.Start - 1, Pos = next });
                    next = Math.Max(next, x.End + 1);
                }
                if (next < Item.Total) add.Add(new Part { Start = next, End = Item.Total - 1, Pos = next });
                if (add.Count > 0) { parts.AddRange(add); parts.Sort((a, b) => a.Start.CompareTo(b.Start)); }
            }
        }

        void UpdateSpeed(double seconds)
        {
            long now = Received();
            double inst = Math.Max(0, now - lastBytes) / seconds;
            lastBytes = now;
            Speed = Speed <= 0 ? inst : Speed * 0.7 + inst * 0.3;
        }

        string PartsText() { lock (gate) return string.Join(";", parts.Select(x => x.Start + "," + x.End + "," + x.Pos)); }

        /// <summary>Parts for the progress bar: [start, end, pos] fractions of the file.</summary>
        public List<double[]> Segments()
        {
            var o = new List<double[]>();
            lock (gate)
            {
                double T = Item.Total;
                foreach (var x in parts.OrderBy(x => x.Start))
                {
                    double a = x.Start / T, b = (x.End + 1) / T, p = Math.Min(x.Pos, x.End + 1) / T;
                    var last = o.Count > 0 ? o[o.Count - 1] : null;
                    // a finished range continues the one before it when that one is finished too
                    if (last != null && last[2] >= last[1] && p >= b) { last[1] = b; last[2] = b; }
                    else o.Add(new[] { a, b, p });
                }
            }
            return o;
        }

        public bool Running { get { lock (gate) return run != null; } }

        public void Pause() { lock (gate) run?.Cancel(); }

        public void Cancel()
        {
            Item.State = "cancelled";
            bool wasRunning;
            lock (gate) { wasRunning = run != null; run?.Cancel(); }
            if (!wasRunning)
            {
                try { File.Delete(PartFile); } catch { }
                Item.State = "interrupted"; Item.Reason = "Cancelled"; Item.Parts = null;
                Downloads.Save(); Downloads.Notify(Item);
            }
        }
    }
}
