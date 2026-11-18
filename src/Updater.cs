using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace GBrowser
{
    /// <summary>Updates from GitHub Releases (GoldenBoot2/GBrowser): checks 20 s after start and every 4 hours
    /// (and on demand from About), downloads the new GBrowserSetup-&lt;version&gt;.exe in the background, verifies it
    /// against the SHA-256 digest GitHub publishes for the release file, then "Relaunch to update" runs it with
    /// --update (it closes GBrowser, installs, and starts it again; tabs are restored by the session).
    /// The check is a plain API request with an honest User-Agent (GBrowser/&lt;version&gt;), not a web page load.</summary>
    internal static class Updater
    {
        public const string Repo = "GoldenBoot2/GBrowser";
        static readonly TimeSpan Every = TimeSpan.FromHours(4);

        public static string Current => Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
        public static string State = "idle";   // idle, checking, uptodate, downloading, ready, error
        public static string Latest, Error, SetupPath;
        public static int Percent;
        public static DateTime LastCheck;
        public static event Action Changed;

        static System.Windows.Forms.Timer timer;
        static bool busy;
        static string downloadUrl, digest;
        static long size;

        public static bool UpdateReady => State == "ready";
        public static bool UpdateFound => State == "downloading" || State == "ready";

        public static void Start()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | (SecurityProtocolType)12288;   // TLS 1.2 + 1.3
            timer = new System.Windows.Forms.Timer { Interval = 20000 };
            timer.Tick += async (s, e) => { timer.Interval = (int)Every.TotalMilliseconds; await Check(); };
            timer.Start();
        }

        static void Set(string state) { State = state; try { Changed?.Invoke(); } catch { } }

        static HttpClient Client()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("GBrowser/" + Current);
            return c;
        }

        static Version V(string s)
        {
            s = (s ?? "").Trim().TrimStart('v', 'V');
            return Version.TryParse(s.Contains('.') ? s : s + ".0", out var v) ? v : new Version(0, 0);
        }

        /// <summary>Looks for a newer release; downloads it when found.</summary>
        public static async Task Check()
        {
            if (busy || State == "ready") return;
            busy = true;
            try
            {
                Set("checking");
                Error = null;
                using (var c = Client())
                using (var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/" + Repo + "/releases/latest"))
                {
                    req.Headers.Accept.ParseAdd("application/vnd.github+json");
                    req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                    using (var res = await c.SendAsync(req))
                    {
                        LastCheck = DateTime.Now;
                        if (res.StatusCode == HttpStatusCode.NotFound) { Set("uptodate"); return; }   // no release yet
                        res.EnsureSuccessStatusCode();
                        var d = Store.FromJson<Dictionary<string, object>>(await res.Content.ReadAsStringAsync());
                        string tag = d.TryGetValue("tag_name", out var t) ? t as string : null;
                        if (V(tag) <= V(Current)) { Latest = tag?.TrimStart('v'); Set("uptodate"); return; }
                        var asset = (d.TryGetValue("assets", out var a) ? a as System.Collections.IEnumerable : null)?
                            .Cast<object>().OfType<Dictionary<string, object>>()
                            .FirstOrDefault(x => x.TryGetValue("name", out var n) && n is string ns &&
                                ns.StartsWith("GBrowserSetup-", StringComparison.OrdinalIgnoreCase) && ns.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                        if (asset == null) { Set("uptodate"); return; }
                        Latest = tag.TrimStart('v', 'V');
                        downloadUrl = asset["browser_download_url"] as string;
                        digest = asset.TryGetValue("digest", out var dg) ? dg as string : null;
                        size = Convert.ToInt64(asset.TryGetValue("size", out var sz) ? sz : 0);
                    }
                }
                await Download();
            }
            catch (Exception ex) { Error = ex.Message; Set("error"); }
            finally { busy = false; }
        }

        static async Task Download()
        {
            if (string.IsNullOrEmpty(digest) || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                throw new Exception("Update not verifiable");
            if (downloadUrl == null || !downloadUrl.StartsWith("https://github.com/" + Repo + "/releases/download/", StringComparison.OrdinalIgnoreCase))
                throw new Exception("Unexpected download address");
            string dir = Path.Combine(Path.GetTempPath(), "GBrowserUpdate");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "GBrowserSetup-" + Latest + ".exe");
            string want = digest.Substring(7).ToLowerInvariant();
            if (File.Exists(file) && Sha256(file) == want) { SetupPath = file; Percent = 100; Set("ready"); return; }
            Percent = 0;
            Set("downloading");
            string part = file + ".part";
            using (var c = Client())
            using (var res = await c.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                res.EnsureSuccessStatusCode();
                long total = res.Content.Headers.ContentLength ?? size;
                using (var s = await res.Content.ReadAsStreamAsync())
                using (var o = File.Create(part))
                {
                    var buf = new byte[1 << 16];
                    long done = 0; int n, last = -1;
                    while ((n = await s.ReadAsync(buf, 0, buf.Length)) > 0)
                    {
                        await o.WriteAsync(buf, 0, n);
                        done += n;
                        int p = total > 0 ? (int)(done * 100 / total) : 0;
                        if (p != last) { last = p; Percent = p; Set("downloading"); }
                    }
                }
            }
            if (Sha256(part) != want) { try { File.Delete(part); } catch { } throw new Exception("Download damaged"); }
            if (File.Exists(file)) File.Delete(file);
            File.Move(part, file);
            SetupPath = file;
            Percent = 100;
            Set("ready");
        }

        static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var f = File.OpenRead(path))
                return string.Concat(sha.ComputeHash(f).Select(b => b.ToString("x2")));
        }

        /// <summary>Starts the verified setup in update mode; the caller then closes GBrowser.</summary>
        public static bool RunSetup()
        {
            if (!UpdateReady || SetupPath == null || !File.Exists(SetupPath)) return false;
            string args = "--update";
#if AUTOMATION
            // test builds: GB_UPDATE_TEST_ARGS (e.g. --target <dir> --no-shortcuts --no-register) keeps tests out of the real install
            string extra = Environment.GetEnvironmentVariable("GB_UPDATE_TEST_ARGS");
            if (!string.IsNullOrEmpty(extra)) args += " " + extra;
#endif
            try { Process.Start(new ProcessStartInfo(SetupPath, args) { UseShellExecute = false }); return true; }
            catch (Exception ex) { Error = ex.Message; Set("error"); return false; }
        }
    }
}
