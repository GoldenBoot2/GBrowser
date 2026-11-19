using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace GBrowser
{
    /// <summary>Hide my identity (Settings > Privacy): GBrowser stays a genuine Microsoft Edge (UaOverride), but what sites
    /// use to recognise a returning browser changes with each identity and differs per site, so two sites cannot
    /// link visits: tracker IDs in cookies (_ga, _fbp, MUID ...) get real-looking values of this identity, WebRTC
    /// hands out no local addresses, and optionally canvas / WebGL / audio read-backs get a tiny noise that stays the
    /// same for a site within the identity. New identity: a new seed and the data sites keep about this browser
    /// cleared (local / session storage, IndexedDB, caches, service workers; tracker cookies, or all cookies with
    /// "Also sign out"). The IP address stays (no VPN / proxy without the user's approval).</summary>
    internal static class Identity
    {
        static Settings S => Store.Settings;

        /// <summary>The page script's privacy settings for a page ("" = off). Seed per site and identity.</summary>
        public static string PageConfig(string href)
        {
            if (!S.HideIdentity) return "";
            if (S.IdentitySeed == 0) NewSeed();
            string site = Blocking.SiteOf(href) ?? Blocking.HostOf(href) ?? "";
            uint seed;
            using (var sha = SHA256.Create())
                seed = BitConverter.ToUInt32(sha.ComputeHash(Encoding.UTF8.GetBytes(S.IdentitySeed + "|" + site + "|" +
                    (S.SiteSalts.TryGetValue(site, out var salt) ? salt : ""))), 0);
            var sb = new StringBuilder("{\"desktop\":true,\"seed\":");
            sb.Append(seed).Append(",\"start\":").Append(S.IdentityStart).Append(",\"cn\":").Append(S.IdentityNoise ? "true" : "false").Append(",\"site\":");
            FilterEngine.JsonString(sb, site);
            return sb.Append('}').ToString();
        }

        /// <summary>A short name for the identity in use (changes with every new identity).</summary>
        public static string Id
        {
            get
            {
                using (var sha = SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes("id|" + S.IdentitySeed)), 0, 4).Replace("-", "");
            }
        }

        public static void NewSeed()
        {
            var b = new byte[8];
            using (var r = RandomNumberGenerator.Create()) r.GetBytes(b);
            S.IdentitySeed = BitConverter.ToInt64(b, 0) & long.MaxValue;
            // a session that "started" a few minutes ago (an exact "now" in every rewritten time stamp is itself a sign)
            S.IdentityStart = Store.Now() - (60000 + (long)(b[0] * 1000L * 2));
            S.SiteSalts.Clear();
            Store.SaveSettings();
        }

        /// <summary>Fresh start for one site (one that blocked or keeps recognising you): its own new identity values; its
        /// cookies and stored data are cleared by the caller. Other sites keep theirs.</summary>
        public static void NewForSite(string site)
        {
            S.SiteSalts[site] = Guid.NewGuid().ToString("N").Substring(0, 12);
            Store.SaveSettings();
        }

        /// <summary>New identity: new seed, and what sites stored about this browser cleared.</summary>
        public static async Task New(CoreWebView2 core, bool signOut)
        {
            NewSeed();
            if (core == null) return;
            var kinds = CoreWebView2BrowsingDataKinds.AllDomStorage | CoreWebView2BrowsingDataKinds.DiskCache;
            if (signOut) kinds |= CoreWebView2BrowsingDataKinds.Cookies;
            await core.Profile.ClearBrowsingDataAsync(kinds);
            if (!signOut)
                foreach (var c in await Cookies.All(core))
                {
                    Cookies.Label(c, out bool tracker);
                    if (tracker) await Cookies.Delete(core, c);
                }
        }
    }
}
