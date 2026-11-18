using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace GBrowser
{
    /// <summary>Keeps session cookies (cookies without an expiry date, which many sites use for logins) across a
    /// restart, so a site you logged in to stays logged in (Chrome does this with "Continue where you left off").
    /// WebView2 drops them when it closes (measured 19-11-2026: a cookie with max-age survived a restart, one without
    /// did not). Saved encrypted like history, only for the normal profile (never private tabs), not at all when
    /// "Clear data when GBrowser closes" is on. Put back before the first page loads.</summary>
    internal static class SessionCookies
    {
        const string FileName = "session-cookies.json";   // encrypted (Store.ReadText / WriteText)

        public sealed class C
        {
            public string Name { get; set; }
            public string Value { get; set; }
            public string Domain { get; set; }
            public string Path { get; set; }
            public bool HttpOnly { get; set; }
            public bool Secure { get; set; }
            public int SameSite { get; set; }
        }

        static bool restored;

        /// <summary>Call with the normal profile's cookie manager before the first navigation.</summary>
        public static void Restore(CoreWebView2CookieManager cm)
        {
            if (restored || cm == null) return;
            restored = true;
            var st = Store.Settings;
            if (st.ClearOnExit) { Forget(); return; }
            List<C> list = null;
            try { var t = Store.ReadText(FileName); if (t != null) list = Store.FromJson<List<C>>(t); } catch { }
            if (list == null) return;
            foreach (var c in list)
            {
                try
                {
                    var k = cm.CreateCookie(c.Name, c.Value, c.Domain, c.Path);   // no Expires = a session cookie again
                    k.IsHttpOnly = c.HttpOnly;
                    k.IsSecure = c.Secure;
                    k.SameSite = (CoreWebView2CookieSameSiteKind)c.SameSite;
                    cm.AddOrUpdateCookie(k);
                }
                catch { }
            }
        }

        /// <summary>Call when GBrowser closes (normal profile).</summary>
        public static async Task Save(CoreWebView2CookieManager cm)
        {
            var st = Store.Settings;
            if (cm == null || st.ClearOnExit) { Forget(); return; }
            try
            {
                var all = await cm.GetCookiesAsync(null);   // every cookie of this profile
                var list = all.Where(c => c.IsSession).Select(c => new C
                {
                    Name = c.Name, Value = c.Value, Domain = c.Domain, Path = c.Path,
                    HttpOnly = c.IsHttpOnly, Secure = c.IsSecure, SameSite = (int)c.SameSite,
                }).ToList();
                Store.WriteText(FileName, Store.ToJson(list));
            }
            catch { }
        }

        public static void Forget()
        {
            try { Store.WriteText(FileName, "[]"); } catch { }
        }
    }
}
