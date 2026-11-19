using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace GBrowser
{
    public class CustomList
    {
        public string Url { get; set; }
        public string Title { get; set; }
        public bool Trusted { get; set; }
        public bool Enabled { get; set; } = true;
        public long Updated { get; set; }        // ms since 1970 (last good download)
        public int Hours { get; set; } = 24;     // update interval (the list's "! Expires:", else a day)
        public int Rules { get; set; }
        public string Error { get; set; }
    }

    /// <summary>Your own rules and custom filter lists: stored, downloaded, kept up to date, and turned into the engine's
    /// run-time rule set (FilterEngine.BuildUser) in the background whenever something changes.</summary>
    internal static class CustomLists
    {
        static string Dir => Path.Combine(Store.DataDir, "lists");
        public static event Action Changed;
        public static FilterEngine.UserRules Last;   // the rule set in use (counts, problems)

        /// <summary>Hosts of well-known filter list makers (AdGuard, EasyList, uBlock Origin, HaGeZi, OISD...): their
        /// lists are trusted like AdGuard's own registry lists. Any other address starts untrusted: no trusted-*
        /// scriptlets and no CSS rules unless you trust it yourself.</summary>
        static readonly string[] KnownSources =
        {
            "filters.adtidy.org", "easylist.to", "easylist-downloads.adblockplus.org", "secure.fanboy.co.nz", "fanboy.co.nz",
            "ublockorigin.github.io", "ublockorigin.pages.dev", "big.oisd.nl", "small.oisd.nl", "nsfw.oisd.nl", "pgl.yoyo.org",
            "malware-filter.gitlab.io", "someonewhocares.org", "adaway.org", "hostfiles.frogeye.fr",
            "raw.githubusercontent.com/uBlockOrigin/", "raw.githubusercontent.com/hagezi/", "raw.githubusercontent.com/AdguardTeam/",
            "raw.githubusercontent.com/DandelionSprout/", "raw.githubusercontent.com/StevenBlack/", "raw.githubusercontent.com/easylist/",
            "cdn.jsdelivr.net/gh/hagezi/", "codeberg.org/hagezi/",
        };

        public static bool IsKnown(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https") return false;
            string hp = u.Host.ToLowerInvariant() + u.AbsolutePath;
            return KnownSources.Any(k => k.Contains("/") ? hp.StartsWith(k, StringComparison.OrdinalIgnoreCase) : u.Host.Equals(k, StringComparison.OrdinalIgnoreCase));
        }

        static string FileOf(string url)
        {
            using (var sha = SHA1.Create())
                return Path.Combine(Dir, BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(url))).Replace("-", "").ToLowerInvariant() + ".txt");
        }

        // ------------------------------------------------------------ rebuild

        static readonly object gate = new object();
        static bool rebuilding, again;

        /// <summary>Rebuilds the run-time rules from your rules and the turned-on custom lists (in the background).</summary>
        public static void Rebuild()
        {
            lock (gate) { if (rebuilding) { again = true; return; } rebuilding = true; }
            Task.Run(async () =>
            {
                var eng = await FilterEngine.Preload();
                while (true)
                {
                    lock (gate) again = false;
                    try
                    {
                        var src = new List<FilterEngine.FilterSource>
                        {
                            new FilterEngine.FilterSource { Name = "", Text = Store.Settings.UserRules ?? "", Trusted = true }
                        };
                        foreach (var l in Store.Settings.CustomLists.Where(x => x.Enabled).ToList())
                        {
                            string f = FileOf(l.Url);
                            if (File.Exists(f)) src.Add(new FilterEngine.FilterSource { Name = l.Url, Text = File.ReadAllText(f), Trusted = l.Trusted });
                        }
                        var u = eng?.BuildUser(src);
                        Last = u;
                        FilterEngine.SetUser(u != null && (u.NetworkCount + u.CosmeticCount + u.ScriptletCount) > 0 ? u : null);
                        if (u != null) foreach (var l in Store.Settings.CustomLists) if (u.Counts.TryGetValue(l.Url, out var n)) l.Rules = n;
                    }
                    catch { }
                    lock (gate) { if (!again) { rebuilding = false; break; } }
                }
                Changed?.Invoke();
            });
        }

        // ------------------------------------------------------------ lists

        static readonly Regex TitleRx = new Regex(@"^!\s*Title:\s*(.+)$", RegexOptions.Multiline);
        static readonly Regex ExpiresRx = new Regex(@"^!\s*Expires:\s*(\d+)\s*(day|days|hour|hours|d|h)?", RegexOptions.Multiline | RegexOptions.IgnoreCase);

        /// <summary>Downloads a list; null = OK, else what went wrong.</summary>
        public static async Task<string> Download(CustomList l)
        {
            try
            {
                if (!Uri.TryCreate(l.Url, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http")) return "Not a web address";
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var req = (HttpWebRequest)WebRequest.Create(u);
                req.UserAgent = "GBrowser/" + typeof(CustomLists).Assembly.GetName().Version.ToString(3) + " (filter list update)";
                req.Timeout = 60000; req.ReadWriteTimeout = 60000;
                req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                string text;
                using (var resp = (HttpWebResponse)await req.GetResponseAsync())
                using (var s = resp.GetResponseStream())
                using (var ms = new MemoryStream())
                {
                    var buf = new byte[65536]; int n; long total = 0;
                    while ((n = await s.ReadAsync(buf, 0, buf.Length)) > 0)
                    {
                        total += n;
                        if (total > 64L * 1024 * 1024) return "List too large (over 64 MB)";
                        ms.Write(buf, 0, n);
                    }
                    text = Encoding.UTF8.GetString(ms.ToArray());
                }
                if (text.StartsWith("<") || text.IndexOf("<html", StringComparison.OrdinalIgnoreCase) >= 0 && text.IndexOf("##", StringComparison.Ordinal) < 0)
                    return "Not a filter list (a web page)";
                // removed while it downloaded (an hourly update racing "Remove"): keep nothing
                if (l.Updated != 0 && !Store.Settings.CustomLists.Contains(l)) return "Removed";
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FileOf(l.Url), text, new UTF8Encoding(false));
                var t = TitleRx.Match(text);
                if (t.Success) l.Title = t.Groups[1].Value.Trim();
                if (string.IsNullOrEmpty(l.Title)) l.Title = u.Host + u.AbsolutePath;
                var e = ExpiresRx.Match(text);
                if (e.Success && int.TryParse(e.Groups[1].Value, out var v) && v > 0)
                    l.Hours = Math.Max(1, Math.Min(24 * 7, e.Groups[2].Value.StartsWith("h", StringComparison.OrdinalIgnoreCase) ? v : v * 24));
                l.Updated = Store.Now();
                l.Error = null;
                return null;
            }
            catch (WebException ex) when (ex.Response is HttpWebResponse r) { return "Server answered " + (int)r.StatusCode; }
            catch (Exception ex) { return ex.Message; }
        }

        public static async Task<string> Add(string url, bool trust)
        {
            url = (url ?? "").Trim();
            if (Store.Settings.CustomLists.Any(x => x.Url == url)) return "Already added";
            var l = new CustomList { Url = url, Trusted = IsKnown(url) || trust };
            string err = await Download(l);
            if (err != null) return err;
            Store.Settings.CustomLists.Add(l);
            Store.SaveSettings();
            Rebuild();
            return null;
        }

        public static void Remove(string url)
        {
            Store.Settings.CustomLists.RemoveAll(x => x.Url == url);
            try { File.Delete(FileOf(url)); } catch { }
            Store.SaveSettings();
            Rebuild();
        }

        /// <summary>Lists due for an update (or all when forced) are downloaded again; rules rebuilt when any changed.</summary>
        public static async Task UpdateDue(bool force)
        {
            bool any = false;
            foreach (var l in Store.Settings.CustomLists.ToList())
            {
                if (!force && Store.Now() - l.Updated < l.Hours * 3600000L) continue;
                if (!Store.Settings.CustomLists.Contains(l)) continue;
                string err = await Download(l);
                l.Error = err;
                if (err == null) any = true;
            }
            Store.SaveSettings();
            if (any || force) Rebuild(); else Changed?.Invoke();
        }

        static System.Threading.Timer timer;
        /// <summary>At start: rules from what is stored; then updates every hour for lists that are due.</summary>
        public static void Start()
        {
            // files of lists that are no longer in Settings
            try
            {
                if (Directory.Exists(Dir))
                {
                    var keep = new HashSet<string>(Store.Settings.CustomLists.Select(l => FileOf(l.Url)), StringComparer.OrdinalIgnoreCase);
                    foreach (var f in Directory.GetFiles(Dir, "*.txt")) if (!keep.Contains(f)) File.Delete(f);
                }
            }
            catch { }
            Rebuild();
            timer = new System.Threading.Timer(_ => { _ = UpdateDue(false); }, null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(1));
        }
    }
}
