using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace GBrowser
{
    /// <summary>Daily update of the built-in host lists (like the Android app's): the compiled filters change only with
    /// a GBrowser release, so the host-style lists are downloaded again each day and their hosts join the compiled
    /// ones as extra entries of the same list (same list bit: the list switches and groups apply; the lists'
    /// exceptions still win). Stored hashed: about 12 bytes a host.</summary>
    internal static class HostUpdates
    {
        const string HZ = "https://raw.githubusercontent.com/hagezi/dns-blocklists/main/adblock/";
        // file name (as in filters/info.txt) -> address, same as tools/build_filters.py
        static readonly Dictionary<string, string> Sources = new Dictionary<string, string>
        {
            { "adguard_15.txt", "https://filters.adtidy.org/extension/chromium/filters/15.txt" },
            { "hagezi_proplus.txt", HZ + "pro.plus.txt" },
            { "peterlowe.txt", "https://pgl.yoyo.org/adservers/serverlist.php?hostformat=adblockplus&showintro=0&mimetype=plaintext" },
            { "oisd_big.txt", "https://big.oisd.nl/" },
            { "stevenblack.txt", "https://raw.githubusercontent.com/StevenBlack/hosts/master/hosts" },
            { "adaway.txt", "https://adaway.org/hosts.txt" },
            { "danpollock.txt", "https://someonewhocares.org/hosts/zero/hosts" },
            { "urlhaus.txt", "https://malware-filter.gitlab.io/malware-filter/urlhaus-filter-agh.txt" },
            { "frogeye.txt", "https://hostfiles.frogeye.fr/firstparty-trackers.txt" },
        };
        static string Dir => Path.Combine(Store.DataDir, "hostupdates");
        public static string State = "";   // for Settings

        static readonly Regex HostLine = new Regex(@"^(?:\|\|([a-z0-9._-]+)\^\s*$|(?:0\.0\.0\.0|127\.0\.0\.1)\s+([a-z0-9._-]+)\s*$|([a-z0-9][a-z0-9._-]*\.[a-z]{2,})\s*$)", RegexOptions.Compiled);
        static readonly HashSet<string> NotHosts = new HashSet<string> { "localhost", "localhost.localdomain", "local", "broadcasthost", "0.0.0.0", "ip6-localhost" };

        /// <summary>At start: what was downloaded before (newer than the release's filters); then daily.</summary>
        public static void Start()
        {
            Task.Run(async () =>
            {
                try
                {
                    await FilterEngine.Preload();
                    Apply();
                    if (Store.Settings.HostListUpdates && Store.Now() - Store.Settings.HostListsUpdated > 23L * 3600000) await Update();
                }
                catch { }
            });
            timer = new System.Threading.Timer(_ =>
            {
                if (Store.Settings.HostListUpdates && Store.Now() - Store.Settings.HostListsUpdated > 23L * 3600000) _ = Update();
            }, null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        }
        static System.Threading.Timer timer;
        static bool busy;

        public static async Task<string> Update()
        {
            if (busy) return "Already updating";
            busy = true;
            int ok = 0, bad = 0;
            try
            {
                Directory.CreateDirectory(Dir);
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                foreach (var kv in Sources)
                {
                    State = "Updating " + kv.Key;
                    try
                    {
                        var req = (HttpWebRequest)WebRequest.Create(kv.Value);
                        req.UserAgent = "GBrowser/" + typeof(HostUpdates).Assembly.GetName().Version.ToString(3) + " (host list update)";
                        req.Timeout = 60000; req.ReadWriteTimeout = 60000;
                        req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                        using (var resp = (HttpWebResponse)await req.GetResponseAsync())
                        using (var s = resp.GetResponseStream())
                        using (var f = File.Create(Path.Combine(Dir, kv.Key + ".tmp"))) await s.CopyToAsync(f);
                        string tmp = Path.Combine(Dir, kv.Key + ".tmp"), dst = Path.Combine(Dir, kv.Key);
                        if (new FileInfo(tmp).Length < 1000) { File.Delete(tmp); bad++; continue; }
                        if (File.Exists(dst)) File.Delete(dst);
                        File.Move(tmp, dst);
                        ok++;
                    }
                    catch { bad++; }
                }
                Store.Settings.HostListsUpdated = Store.Now();
                Store.SaveSettings();
                int n = Apply();
                State = "Updated " + DateTime.Now.ToString("dd-MM-yyyy HH:mm") + ": " + n.ToString("N0") + " hosts" + (bad > 0 ? ", " + bad + " lists failed" : "");
                return null;
            }
            finally { busy = false; }
        }

        /// <summary>Hashes of the downloaded lists into the engine (only lists GBrowser knows; newer than its filters).</summary>
        public static int Apply()
        {
            var e = FilterEngine.Instance;
            if (e == null || !Directory.Exists(Dir)) return 0;
            var map = new Dictionary<long, int>();
            DateTime built = File.GetLastWriteTimeUtc(Path.Combine(FilterEngine.Folder, "hosts.bin"));
            for (int i = 0; i < e.ListFiles.Length && i < 32; i++)
            {
                string f = Path.Combine(Dir, e.ListFiles[i]);
                if (!Sources.ContainsKey(e.ListFiles[i]) || !File.Exists(f) || File.GetLastWriteTimeUtc(f) < built) continue;
                int bit = 1 << i;
                foreach (var raw in File.ReadLines(f))
                {
                    string l = raw.Trim().ToLowerInvariant();
                    if (l.Length == 0 || l[0] == '!' || l[0] == '#' || l[0] == '[') continue;
                    int hash = l.IndexOf('#');
                    if (hash > 0) l = l.Substring(0, hash).Trim();
                    var m = HostLine.Match(l);
                    if (!m.Success) continue;
                    string h = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
                    h = h.TrimEnd('.');
                    if (h.Length < 4 || NotHosts.Contains(h) || h.IndexOf('.') < 0) continue;
                    long k = FilterEngine.Fnv64(h);
                    map[k] = map.TryGetValue(k, out var old) ? old | bit : bit;
                }
            }
            FilterEngine.SetExtraHosts(map);
            return map.Count;
        }
    }
}
