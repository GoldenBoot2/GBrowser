using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace GBrowser
{
    public class CookieRule { public string Site { get; set; } public string Mode { get; set; } }            // allow | block | clear
    public class CookieBlock { public string Name { get; set; } public string Site { get; set; } }           // Site "*" = everywhere
    public class ProtectedCookie { public string Key { get; set; } public string Json { get; set; } }       // name|domain|path|partition

    /// <summary>Cookie manager (gbrowser://cookies) and cookie rules, on the engine's DevTools commands (Storage.getCookies
    /// lists every cookie with its partition, priority and size; Network.setCookie / deleteCookies change them).
    /// Rules: blocked cookies (a name on one site or everywhere), per-site Allow / Block all / Clear when closed,
    /// protected cookies (restored when a site changes or removes them), auto-delete after a site's last tab closes,
    /// a lifetime cap. Script-set cookies of blocked names never reach the page (page script, see PageBridge.rr);
    /// server-set ones are removed right after the response by Guard.</summary>
    internal static class Cookies
    {
        static Settings S => Store.Settings;

        // ------------------------------------------------------------ engine access

        public static async Task<List<Dictionary<string, object>>> All(CoreWebView2 core)
        {
            var r = Store.FromJson<Dictionary<string, object>>(await core.CallDevToolsProtocolMethodAsync("Storage.getCookies", "{}"));
            var list = new List<Dictionary<string, object>>();
            if (r != null && r.TryGetValue("cookies", out var c) && c is System.Collections.IEnumerable e)
                foreach (var x in e) if (x is Dictionary<string, object> d) list.Add(d);
            return list;
        }

        static string S2(Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : "";
        static bool B(Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) && v is bool b && b;
        static double D(Dictionary<string, object> d, string k) =>
            d != null && d.TryGetValue(k, out var v) && v != null && double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;

        public static string PartitionSite(Dictionary<string, object> c) =>
            c.TryGetValue("partitionKey", out var p) ? (p is Dictionary<string, object> pk ? S2(pk, "topLevelSite") : Convert.ToString(p)) : "";

        public static string KeyOf(Dictionary<string, object> c) => S2(c, "name") + "|" + S2(c, "domain") + "|" + S2(c, "path") + "|" + PartitionSite(c);

        /// <summary>Sets a cookie from the page's / a stored form (name, value, domain, path, expires (s, -1 = session),
        /// httpOnly, secure, sameSite, priority, partitionKey). Host-only cookies (domain without a dot) are set by URL.</summary>
        public static async Task<string> Set(CoreWebView2 core, Dictionary<string, object> c)
        {
            string name = S2(c, "name"), domain = S2(c, "domain").Trim(), path = S2(c, "path");
            if (string.IsNullOrEmpty(path)) path = "/";
            bool secure = B(c, "secure");
            string same = S2(c, "sameSite");
            if (same == "None" && !secure) return "SameSite=None needs Secure";
            if (name.StartsWith("__Host-") && (!secure || path != "/" || domain.StartsWith("."))) return "__Host- cookies need Secure, path / and no domain";
            if (name.StartsWith("__Secure-") && !secure) return "__Secure- cookies need Secure";
            if (domain.Length == 0) return "Domain missing";
            var a = new Dictionary<string, object> { { "name", name }, { "value", S2(c, "value") }, { "path", path }, { "secure", secure }, { "httpOnly", B(c, "httpOnly") } };
            if (domain.StartsWith(".")) a["domain"] = domain;
            else a["url"] = (secure ? "https://" : "http://") + domain + path;
            if (same == "Strict" || same == "Lax" || same == "None") a["sameSite"] = same;
            double exp = D(c, "expires");
            if (exp > 0 && !B(c, "session")) a["expires"] = exp;
            string pri = S2(c, "priority");
            if (pri == "Low" || pri == "Medium" || pri == "High") a["priority"] = pri;
            string part = PartitionSite(c);
            if (part.Length > 0) a["partitionKey"] = new Dictionary<string, object> { { "topLevelSite", part }, { "hasCrossSiteAncestor", false } };
            var r = Store.FromJson<Dictionary<string, object>>(await core.CallDevToolsProtocolMethodAsync("Network.setCookie", Store.ToJson(a)));
            return r != null && r.TryGetValue("success", out var ok) && ok is bool okb && okb ? null : "The browser refused this cookie";
        }

        public static async Task Delete(CoreWebView2 core, Dictionary<string, object> c)
        {
            var a = new Dictionary<string, object> { { "name", S2(c, "name") }, { "domain", S2(c, "domain") }, { "path", S2(c, "path") } };
            string part = PartitionSite(c);
            if (part.Length > 0) a["partitionKey"] = new Dictionary<string, object> { { "topLevelSite", part }, { "hasCrossSiteAncestor", false } };
            await core.CallDevToolsProtocolMethodAsync("Network.deleteCookies", Store.ToJson(a));
        }

        /// <summary>The site (registrable domain) a cookie belongs to.</summary>
        public static string SiteOf(Dictionary<string, object> c)
        {
            string d = S2(c, "domain").TrimStart('.');
            return FilterEngine.Instance?.Registrable(d) ?? d;
        }

        // ------------------------------------------------------------ labels

        static readonly Dictionary<string, string> Known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "_ga", "Google Analytics: your visitor id (2 years)" }, { "_gid", "Google Analytics: visitor id for a day" },
            { "_gat", "Google Analytics: request rate limit" }, { "_gcl_au", "Google Ads: ad click tracking" },
            { "IDE", "DoubleClick (Google ads): ad tracking across sites" }, { "test_cookie", "DoubleClick: checks cookies work" },
            { "NID", "Google: preferences and ad personalisation" }, { "__utma", "Google Analytics (old): visitor id" },
            { "_fbp", "Meta (Facebook) Pixel: ad tracking" }, { "fr", "Meta (Facebook): ad delivery" },
            { "_ttp", "TikTok Pixel: ad tracking" }, { "_pin_unauth", "Pinterest: ad tracking" },
            { "_clck", "Microsoft Clarity: visitor id" }, { "_clsk", "Microsoft Clarity: session" }, { "MUID", "Microsoft: visitor id across Microsoft sites" },
            { "_hjSessionUser", "Hotjar: visitor id" }, { "_uetsid", "Microsoft Ads: session" }, { "_uetvid", "Microsoft Ads: visitor id" },
            { "__cf_bm", "Cloudflare: bot check (needed by the site)" }, { "cf_clearance", "Cloudflare: passed security check (needed by the site)" },
            { "PHPSESSID", "Session (sign-in, basket)" }, { "JSESSIONID", "Session (sign-in, basket)" }, { "ASP.NET_SessionId", "Session (sign-in, basket)" },
            { "csrftoken", "Security token for forms" }, { "OptanonConsent", "Cookie consent choices (OneTrust)" }, { "CookieConsent", "Cookie consent choices (Cookiebot)" },
            { "euconsent-v2", "Cookie consent choices (TCF)" },
        };

        /// <summary>Tracker = the cookie's domain is on GBrowser's tracker / ad lists, or a well-known tracking name.</summary>
        public static string Label(Dictionary<string, object> c, out bool tracker)
        {
            string name = S2(c, "name");
            Known.TryGetValue(name, out var what);
            if (what == null && name.StartsWith("_ga_")) what = "Google Analytics 4: session state";
            if (what == null && name.StartsWith("_hj")) what = "Hotjar: analytics";
            tracker = what != null && (what.Contains("tracking") || what.Contains("visitor id") || what.Contains("Analytics") || what.Contains("Ads"));
            var e = FilterEngine.Instance;
            string d = S2(c, "domain").TrimStart('.');
            if (!tracker && e != null && d.Length > 0)
                try { tracker = e.Match("https://" + d + "/", d, null, FilterEngine.T_SCRIPT | FilterEngine.T_XHR | FilterEngine.T_IMAGE, Blocking.TrackerLists() | Blocking.Groups()) != null; } catch { }
            return what ?? "";
        }

        // ------------------------------------------------------------ rules

        static bool SiteMatch(string ruleSite, string site) =>
            ruleSite == "*" || site == ruleSite || site.EndsWith("." + ruleSite, StringComparison.Ordinal);

        public static string ModeFor(string site) => S.CookieRules.FirstOrDefault(r => SiteMatch(r.Site, site))?.Mode ?? "";

        static bool Blocked(Dictionary<string, object> c, string site)
        {
            if (ModeFor(site) == "block") return true;
            if (ModeFor(site) == "allow") return false;
            string name = S2(c, "name");
            return S.CookieBlocks.Any(b => b.Name == name && SiteMatch(b.Site, site));
        }

        /// <summary>Cookie names a page of this site must not see or set (page script; "" = all).</summary>
        public static IEnumerable<string> BlockedNames(string host)
        {
            string site = FilterEngine.Instance?.Registrable(host) ?? host;
            if (ModeFor(site) == "block") { yield return ""; yield break; }
            if (ModeFor(site) == "allow") yield break;
            foreach (var b in S.CookieBlocks) if (SiteMatch(b.Site, site)) yield return b.Name;
        }

        public static bool AnyRules => S.CookieRules.Count > 0 || S.CookieBlocks.Count > 0 || S.CookieProtected.Count > 0 || S.CookieMaxDays > 0;

        static readonly Dictionary<CoreWebView2, DateTime> pending = new Dictionary<CoreWebView2, DateTime>();

        /// <summary>Applies the rules after a page or a response set cookies (debounced per tab, 300 ms).</summary>
        public static async void Guard(CoreWebView2 core)
        {
            if (core == null || !AnyRules) return;
            lock (pending) { if (pending.ContainsKey(core)) return; pending[core] = DateTime.UtcNow; }
            try
            {
                await Task.Delay(300);
                var all = await All(core);
                var keys = new HashSet<string>(all.Select(KeyOf));
                double now = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
                foreach (var c in all)
                {
                    string site = SiteOf(c);
                    if (Blocked(c, site)) { await Delete(core, c); continue; }
                    if (S.CookieMaxDays > 0 && !B(c, "session") && D(c, "expires") > now + S.CookieMaxDays * 86400.0 && ModeFor(site) != "allow"
                        && !S.CookieProtected.Any(p => p.Key == KeyOf(c)))
                    {
                        var capped = new Dictionary<string, object>(c) { ["expires"] = Math.Floor(now + S.CookieMaxDays * 86400.0) };
                        await Set(core, capped);
                    }
                }
                // protected cookies come back as they were saved
                foreach (var p in S.CookieProtected)
                {
                    var saved = Store.FromJson<Dictionary<string, object>>(p.Json);
                    var cur = all.FirstOrDefault(c => KeyOf(c) == p.Key);
                    if (cur == null || S2(cur, "value") != S2(saved, "value")) await Set(core, saved);
                }
            }
            catch { }
            finally { lock (pending) pending.Remove(core); }
        }

        /// <summary>Removes the cookies (and, for "clear", the stored data) of sites that should not keep them now that no
        /// tab shows them: "Clear when closed" sites always, any site when auto-delete is on (except kept / allowed /
        /// protected ones). Runs a few seconds after a tab closes or leaves a site, like Cookie AutoDelete.</summary>
        public static async Task CleanClosed(CoreWebView2 core, ICollection<string> openSites)
        {
            if (core == null) return;
            bool auto = S.CookieAutoDelete;
            if (!auto && !S.CookieRules.Any(r => r.Mode == "clear")) return;
            try
            {
                var all = await All(core);
                foreach (var c in all)
                {
                    string site = SiteOf(c);
                    if (openSites.Contains(site)) continue;
                    string mode = ModeFor(site);
                    bool go = mode == "clear" || (auto && mode != "allow" && !S.CookieKeep.Any(k => SiteMatch(k, site)));
                    if (!go || S.CookieProtected.Any(p => p.Key == KeyOf(c))) continue;
                    await Delete(core, c);
                }
            }
            catch { }
        }

        // ------------------------------------------------------------ export / import

        /// <summary>json (Cookie-Editor format), netscape (cookies.txt) or header (Cookie: a=b; c=d).</summary>
        public static string Export(List<Dictionary<string, object>> list, string format)
        {
            var sb = new StringBuilder();
            if (format == "header")
                return string.Join("; ", list.Select(c => S2(c, "name") + "=" + S2(c, "value")));
            if (format == "netscape")
            {
                sb.Append("# Netscape HTTP Cookie File\n");
                foreach (var c in list)
                {
                    string d = S2(c, "domain");
                    sb.Append(B(c, "httpOnly") ? "#HttpOnly_" : "").Append(d).Append('\t').Append(d.StartsWith(".") ? "TRUE" : "FALSE").Append('\t')
                      .Append(S2(c, "path")).Append('\t').Append(B(c, "secure") ? "TRUE" : "FALSE").Append('\t')
                      .Append(B(c, "session") ? "0" : ((long)D(c, "expires")).ToString(CultureInfo.InvariantCulture)).Append('\t')
                      .Append(S2(c, "name")).Append('\t').Append(S2(c, "value")).Append('\n');
                }
                return sb.ToString();
            }
            // Cookie-Editor's JSON
            var o = list.Select(c => new Dictionary<string, object>
            {
                { "domain", S2(c, "domain") }, { "expirationDate", B(c, "session") ? null : (object)D(c, "expires") }, { "hostOnly", !S2(c, "domain").StartsWith(".") },
                { "httpOnly", B(c, "httpOnly") }, { "name", S2(c, "name") }, { "path", S2(c, "path") },
                { "sameSite", S2(c, "sameSite") == "None" ? "no_restriction" : S2(c, "sameSite").ToLowerInvariant() is string ss && ss.Length > 0 ? ss : "unspecified" },
                { "secure", B(c, "secure") }, { "session", B(c, "session") }, { "storeId", null }, { "value", S2(c, "value") },
            }).ToList();
            return Store.ToJson(o);
        }

        /// <summary>Cookies read from an export (any of the three formats); host = default domain for a Cookie header.</summary>
        public static List<Dictionary<string, object>> Parse(string text, string host)
        {
            var list = new List<Dictionary<string, object>>();
            text = (text ?? "").Trim();
            if (text.StartsWith("["))
            {
                foreach (var o in Store.FromJson<List<Dictionary<string, object>>>(text))
                {
                    string ss = S2(o, "sameSite").ToLowerInvariant();
                    list.Add(new Dictionary<string, object>
                    {
                        { "name", S2(o, "name") }, { "value", S2(o, "value") }, { "domain", B(o, "hostOnly") ? S2(o, "domain").TrimStart('.') : S2(o, "domain") },
                        { "path", S2(o, "path") }, { "secure", B(o, "secure") }, { "httpOnly", B(o, "httpOnly") },
                        { "sameSite", ss == "no_restriction" ? "None" : ss == "lax" ? "Lax" : ss == "strict" ? "Strict" : "" },
                        { "session", B(o, "session") || !o.ContainsKey("expirationDate") || o["expirationDate"] == null }, { "expires", D(o, "expirationDate") },
                    });
                }
                return list;
            }
            if (text.Contains("\t"))
            {
                foreach (var raw in text.Split('\n'))
                {
                    string l = raw.TrimEnd('\r');
                    bool http = l.StartsWith("#HttpOnly_");
                    if (http) l = l.Substring(10);
                    if (l.Length == 0 || l.StartsWith("#")) continue;
                    var f = l.Split('\t');
                    if (f.Length < 7) continue;
                    long.TryParse(f[4], out var exp);
                    list.Add(new Dictionary<string, object>
                    {
                        { "domain", f[0] }, { "path", f[2] }, { "secure", f[3] == "TRUE" }, { "expires", (double)exp }, { "session", exp == 0 },
                        { "name", f[5] }, { "value", f[6] }, { "httpOnly", http }, { "sameSite", "" },
                    });
                }
                return list;
            }
            if (text.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)) text = text.Substring(7);
            foreach (var p in text.Split(';'))
            {
                int eq = p.IndexOf('=');
                if (eq <= 0) continue;
                list.Add(new Dictionary<string, object>
                {
                    { "name", p.Substring(0, eq).Trim() }, { "value", p.Substring(eq + 1).Trim() }, { "domain", host ?? "" }, { "path", "/" },
                    { "secure", true }, { "session", true }, { "sameSite", "Lax" },
                });
            }
            return list;
        }

        // ------------------------------------------------------------ third-party cookies (the engine's own setting)

        /// <summary>Chromium's "Block third-party cookies" (profile.cookie_controls_mode: 0 allow, 1 block), written into
        /// the profile's Preferences before the engine starts (the engine reads it then; GBrowser restarts to apply).
        /// Only that one value changes; the file is checked to parse the same before it is replaced.</summary>
        public static void ApplyThirdPartyPref(string prefsPath, bool block)
        {
            try
            {
                if (!File.Exists(prefsPath))
                {
                    if (!block) return;
                    Directory.CreateDirectory(Path.GetDirectoryName(prefsPath));
                    File.WriteAllText(prefsPath, "{\"profile\":{\"cookie_controls_mode\":1}}", new UTF8Encoding(false));
                    return;
                }
                var ser = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 };
                string text = File.ReadAllText(prefsPath, Encoding.UTF8);
                var before = ser.DeserializeObject(text) as Dictionary<string, object>;
                if (before == null) return;
                string v = block ? "1" : "0";
                var re = new System.Text.RegularExpressions.Regex("\"cookie_controls_mode\"\\s*:\\s*\\d+");
                string updated;
                if (re.IsMatch(text)) updated = re.Replace(text, "\"cookie_controls_mode\":" + v, 1);
                else
                {
                    var pr = new System.Text.RegularExpressions.Regex("\"profile\"\\s*:\\s*\\{");
                    if (!pr.IsMatch(text)) { if (!block) return; updated = "{\"profile\":{\"cookie_controls_mode\":1}," + text.Substring(text.IndexOf('{') + 1); }
                    else updated = pr.Replace(text, m => m.Value + "\"cookie_controls_mode\":" + v + ",", 1);
                }
                if (updated == text) return;
                var after = ser.DeserializeObject(updated) as Dictionary<string, object>;
                if (after == null || after.Count < before.Count) return;
                string tmp = prefsPath + ".gbtmp";
                File.WriteAllText(tmp, updated, new UTF8Encoding(false));
                File.Replace(tmp, prefsPath, null);
            }
            catch { }
        }

        public static bool ThirdPartyActive;   // what the engine started with (a change needs a restart)

        // ------------------------------------------------------------ snapshots (switch accounts)

        public sealed class Snapshot { public string Name { get; set; } public string Site { get; set; } public long Time { get; set; } public string Json { get; set; } }

        public static List<Snapshot> Snapshots() => Store.ReadData<List<Snapshot>>("cookie-snapshots.json") ?? new List<Snapshot>();
        public static void SaveSnapshots(List<Snapshot> l) => Store.WriteData("cookie-snapshots.json", l);
    }
}
