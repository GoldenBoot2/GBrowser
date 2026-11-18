using System;
using System.Collections.Generic;
using System.Net;

namespace GBrowser
{
    /// <summary>Security helpers: HTTPS-First (http addresses are upgraded; sites without HTTPS need a click and
    /// are remembered only until GBrowser closes), local-network detection.</summary>
    internal static class Security
    {
        /// <summary>Sites (registrable domains, so subdomains count too, as in Chrome) the user chose to open over
        /// plain HTTP this session; never saved.</summary>
        static readonly HashSet<string> httpAllowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static void AllowHttp(string host) { var s = SiteOf(host); if (s != null) httpAllowed.Add(s); }
        public static bool HttpAllowed(string host) { var s = SiteOf(host); return s != null && httpAllowed.Contains(s); }

        static string SiteOf(string host)
        {
            if (string.IsNullOrEmpty(host)) return null;
            var e = FilterEngine.Instance;
            return e != null ? e.Registrable(host) : host;
        }

        // ---------------------------------------------------------------- Secure DNS (DNS over HTTPS)

        /// <summary>DoH providers offered in Settings: id, name, template (RFC 8484).</summary>
        public static readonly string[][] DnsProviders =
        {
            new[] { "cloudflare", "Cloudflare (1.1.1.1)", "https://cloudflare-dns.com/dns-query" },
            new[] { "google", "Google Public DNS", "https://dns.google/dns-query{?dns}" },
            new[] { "quad9", "Quad9", "https://dns.quad9.net/dns-query" },
            new[] { "adguard", "AdGuard DNS (blocks ads)", "https://dns.adguard-dns.com/dns-query" },
        };

        /// <summary>The DoH template for Settings.SecureDns ("off", a provider id, or "custom:https://...");
        /// null = off. Custom templates must be https URLs.</summary>
        public static string DnsTemplate(string setting)
        {
            if (string.IsNullOrEmpty(setting) || setting == "off") return null;
            if (setting.StartsWith("custom:"))
            {
                string t = setting.Substring(7).Trim();
                return Uri.TryCreate(t.Replace("{?dns}", ""), UriKind.Absolute, out var u) && u.Scheme == "https" ? t : null;
            }
            foreach (var p in DnsProviders) if (p[0] == setting) return p[2];
            return null;
        }

        /// <summary>Sets the engine's own secure DNS preference (Local State: dns_over_https.mode / .templates, the same
        /// prefs Edge's and Chrome's "Use secure DNS" write) before the engine starts. MEASURED 18-11-2026 (WebView2 157,
        /// one.one.one.one/help): this works ("Using DNS over HTTPS: Yes"; with Google's template the resolver became
        /// Google instead of the PC's 1.1.1.1), while the DnsOverHttps feature/field-trial switches had no effect.
        /// A provider = "secure" mode (no fallback to plain DNS); "off" = "automatic" (Chromium's default: DoH when
        /// the PC's DNS provider supports it). Only this one entry of Local State changes; the result is checked
        /// (the os_crypt key that protects cookies and passwords must be identical) or nothing is written.</summary>
        public static void ApplySecureDnsPref(string localStatePath, string setting)
        {
            string t = DnsTemplate(setting);
            var ser = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 };
            string entry = "\"dns_over_https\":" + ser.Serialize(new Dictionary<string, object>
            {
                { "mode", t == null ? "automatic" : "secure" }, { "templates", t ?? "" },
            });
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(localStatePath));
                if (!System.IO.File.Exists(localStatePath))
                {
                    System.IO.File.WriteAllText(localStatePath, "{" + entry + "}", new System.Text.UTF8Encoding(false));
                    return;
                }
                string text = System.IO.File.ReadAllText(localStatePath, System.Text.Encoding.UTF8);
                var before = ser.DeserializeObject(text) as Dictionary<string, object>;
                if (before == null) return;
                // the object, allowing braces inside its strings (Google's template has "{?dns}")
                var re = new System.Text.RegularExpressions.Regex("\"dns_over_https\"\\s*:\\s*\\{(?:[^{}\"]|\"(?:[^\"\\\\]|\\\\.)*\")*\\}");
                string updated = re.IsMatch(text) ? re.Replace(text, entry.Replace("$", "$$"), 1)
                    : text.Substring(0, text.IndexOf('{') + 1) + entry + (before.Count > 0 ? "," : "") + text.Substring(text.IndexOf('{') + 1);
                if (updated == text) return;
                var after = ser.DeserializeObject(updated) as Dictionary<string, object>;
                if (after == null || after.Count < before.Count) return;
                if (before.TryGetValue("os_crypt", out var oc1) && (!after.TryGetValue("os_crypt", out var oc2) || ser.Serialize(oc1) != ser.Serialize(oc2))) return;
                string tmp = localStatePath + ".gbtmp";
                System.IO.File.WriteAllText(tmp, updated, new System.Text.UTF8Encoding(false));
                System.IO.File.Replace(tmp, localStatePath, null);
            }
            catch { }
        }

        /// <summary>The setting the running engine was started with (to tell the user a restart is needed).</summary>
        public static string ActiveSecureDns;

        /// <summary>localhost, single-label names, private / link-local / loopback IPs and .local / .lan / .home.arpa:
        /// these usually have no HTTPS and are not on the internet, so they are not upgraded (Chrome does the same).</summary>
        public static bool IsLocal(string host)
        {
            if (string.IsNullOrEmpty(host)) return true;
            host = host.Trim('[', ']').ToLowerInvariant();
            if (host == "localhost" || host.EndsWith(".localhost") || !host.Contains(".") && !host.Contains(":")) return true;
            if (host.EndsWith(".local") || host.EndsWith(".lan") || host.EndsWith(".home.arpa") || host.EndsWith(".internal")) return true;
            if (IPAddress.TryParse(host, out var ip))
            {
                if (IPAddress.IsLoopback(ip)) return true;
                var b = ip.GetAddressBytes();
                if (b.Length == 4)
                    return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) ||
                           (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);   // + CGNAT / Tailscale
                return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (b[0] & 0xFE) == 0xFC;              // + unique local fc00::/7
            }
            return false;
        }
    }
}
