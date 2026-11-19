using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace GBrowser
{
    /// <summary>Masked cookie consent (from the Android app, Consent.java): when GBrowser blocks a consent tool's script
    /// (OneTrust, Cookiebot), some sites fail without the consent cookie that script would have written (VFS Global
    /// answered with a 502 after login). GBrowser then writes that cookie itself, in the tool's own format, saying
    /// "only necessary". Nothing is sent to the consent tool. Values are format-valid (consentId a real UUID v4, the
    /// Cookiebot stamp real base64) and come from the identity seed: a new identity gives new IDs and replaces only
    /// cookies GBrowser wrote. A consent cookie the user set is never touched.</summary>
    internal static class Consent
    {
        static Settings S => Store.Settings;
        static readonly HashSet<string> busy = new HashSet<string>();

        // the OneTrust script version in use today (cnn.com, 19-11-2026); an old version would stand out
        const string OtVersion = "202610.1.0";

        // a seed of this install (never a shared constant: the same consent ID on every GBrowser would identify it)
        static long Seed { get { if (S.IdentitySeed == 0) Identity.NewSeed(); return S.IdentitySeed; } }

        static string ConsentId()
        {
            var b = new byte[16];
            new Random((int)(Seed ^ (Seed >> 32) ^ 0x5DEECE66)).NextBytes(b);
            b[6] = (byte)((b[6] & 0x0f) | 0x40);
            b[8] = (byte)((b[8] & 0x3f) | 0x80);
            var sb = new System.Text.StringBuilder(36);
            for (int i = 0; i < 16; i++) { if (i == 4 || i == 6 || i == 8 || i == 10) sb.Append('-'); sb.Append(b[i].ToString("x2")); }
            return sb.ToString();
        }

        public static void OnBlocked(string requestHost, string pageUrl, CoreWebView2 core)
        {
            if (requestHost == null || pageUrl == null || core == null || !pageUrl.StartsWith("http")) return;
            string h = requestHost.ToLowerInvariant();
            string kind = h.EndsWith("cookielaw.org") || h.EndsWith("onetrust.com") || h.EndsWith("onetrust.io") ? "ot"
                : h.EndsWith("cookiebot.com") || h.EndsWith("cookiebot.eu") ? "cb" : null;
            if (kind == null) return;
            string site = Blocking.SiteOf(pageUrl);
            if (site == null) return;
            lock (busy) { if (!busy.Add(kind + site)) return; }
            _ = Write(kind, site, pageUrl, core);
        }

        /// <summary>We may write: no cookie yet, or one we wrote for an older identity.</summary>
        static bool MayWrite(string existing, string current)
        {
            if (existing == null) return true;
            if (existing.Contains(current)) return false;
            return S.ConsentIds.Any(id => existing.Contains(id));
        }

        static async Task Write(string kind, string site, string url, CoreWebView2 core)
        {
            try
            {
                var cm = core.CookieManager;
                var have = await cm.GetCookiesAsync(url);
                string Val(string n) => have.FirstOrDefault(c => c.Name == n)?.Value;
                var now = DateTime.Now;
                if (kind == "ot")
                {
                    string id = ConsentId();
                    if (!MayWrite(Val("OptanonConsent"), id)) return;
                    var tz = TimeZoneInfo.Local;
                    // datestamp as JavaScript's Date.toString() prints it ("Wed Nov 19 2026 14:02:41 GMT+0530 (India Standard Time)")
                    var off = tz.GetUtcOffset(now);
                    string stamp = now.ToString("ddd MMM dd yyyy HH:mm:ss", CultureInfo.InvariantCulture) + " GMT" + (off < TimeSpan.Zero ? "-" : "+") +
                                   off.ToString("hhmm") + " (" + (tz.IsDaylightSavingTime(now) ? tz.DaylightName : tz.StandardName) + ")";
                    // encoded as OneTrust itself writes it (measured on cnn.com 19-11-2026): spaces as '+', brackets kept
                    string ds = Uri.EscapeDataString(stamp).Replace("%20", "+").Replace("%28", "(").Replace("%29", ")");
                    string v = "isGpcEnabled=0&datestamp=" + ds + "&version=" + OtVersion + "&browserGpcFlag=0&isDntEnabled=0&isIABGlobal=false&hosts=" +
                               "&consentId=" + id + "&interactionCount=1&isAnonUser=1&landingPath=NotLandingPage&groups=" +
                               Uri.EscapeDataString("C0001:1,C0002:0,C0003:0,C0004:0,C0005:0") + "&intType=2&AwaitingReconsent=false";
                    Add(cm, "OptanonAlertBoxClosed", DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture), site);
                    Add(cm, "OptanonConsent", v, site);
                    Remember(id);
                }
                else
                {
                    var raw = new byte[32];
                    new Random((int)(Seed ^ (Seed >> 32) ^ 0xC00C1E)).NextBytes(raw);
                    string stampEnc = Convert.ToBase64String(raw).Replace("+", "%2B").Replace("/", "%2F").Replace("=", "%3D");
                    if (!MayWrite(Val("CookieConsent"), stampEnc)) return;
                    long utc = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
                    // Cookiebot takes the region from the visitor's country: Windows' Home location (Settings > Time & language >
                    // Region), not the language setting (often en-US on Indian PCs)
                    string region = null;
                    try { region = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\International\Geo", "Name", null) as string; } catch { }
                    if (region == null || region.Length != 2) try { region = RegionInfo.CurrentRegion.TwoLetterISORegionName; } catch { region = "in"; }
                    region = region.ToLowerInvariant();
                    string v = "{stamp:%27" + stampEnc + "%27%2Cnecessary:true%2Cpreferences:false%2Cstatistics:false%2Cmarketing:false%2Cmethod:%27explicit%27%2Cver:1%2Cutc:" +
                               utc + "%2Cregion:%27" + region + "%27}";
                    Add(cm, "CookieConsent", v, site);
                    Remember(stampEnc);
                }
            }
            catch { }
            finally { lock (busy) busy.Remove(kind + site); }
        }

        static void Add(CoreWebView2CookieManager cm, string name, string value, string site)
        {
            var c = cm.CreateCookie(name, value, "." + site, "/");
            c.Expires = DateTime.Now.AddYears(1);
            c.SameSite = CoreWebView2CookieSameSiteKind.Lax;
            cm.AddOrUpdateCookie(c);
        }

        static void Remember(string id)
        {
            if (S.ConsentIds.Contains(id)) return;
            S.ConsentIds.Add(id);
            if (S.ConsentIds.Count > 50) S.ConsentIds.RemoveAt(0);
            Store.SaveSettings();
        }
    }
}
