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
