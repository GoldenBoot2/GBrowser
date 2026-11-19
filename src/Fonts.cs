using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace GBrowser
{
    /// <summary>Settings > Appearance > Font size and Customize fonts, like Chrome's. Stored where Chrome keeps them, in
    /// the profile's Preferences (webkit.webprefs: default_font_size, default_fixed_font_size, fonts.*.Zyyy), written
    /// before the engine starts: that covers every page and frame. A new size also applies at once (DevTools
    /// Page.setFontSizes); new fonts need a restart (Page.setFontFamilies took effect only once per page, measured
    /// 19-11-2026). At Chrome's defaults nothing is written. Chrome's "Minimum font size" is not offered.</summary>
    internal static class Fonts
    {
        static Settings S => Store.Settings;
        public const int DefaultSize = 16, DefaultFixedSize = 13;
        public static string ActiveFamilies = "";   // what the engine started with (font changes need a restart)
        static int startedSize = DefaultSize;

        public static string Families => (S.FontStandard ?? "") + "|" + (S.FontSerif ?? "") + "|" + (S.FontSans ?? "") + "|" + (S.FontFixed ?? "");
        static int FixedFor(int size) => Math.Max(6, size - (DefaultSize - DefaultFixedSize));   // Chrome keeps fixed-width 3 px smaller

        /// <summary>Before the engine starts: the font preferences into the profile (only the "webkit" entry changes; the
        /// file must parse the same with every other key kept, else it is left as it is).</summary>
        public static void ApplyPrefs(string prefsPath)
        {
            ActiveFamilies = Families;
            startedSize = S.FontSize;
            try
            {
                var webprefs = new Dictionary<string, object>();
                if (S.FontSize != DefaultSize) { webprefs["default_font_size"] = S.FontSize; webprefs["default_fixed_font_size"] = FixedFor(S.FontSize); }
                var fonts = new Dictionary<string, object>();
                void F(string kind, string name) { if (!string.IsNullOrEmpty(name)) fonts[kind] = new Dictionary<string, object> { { "Zyyy", name } }; }
                F("standard", S.FontStandard); F("serif", S.FontSerif); F("sansserif", S.FontSans); F("fixed", S.FontFixed);
                if (fonts.Count > 0) webprefs["fonts"] = fonts;
                EnginePrefs.Edit(prefsPath, "webkit", webkit =>
                {
                    var wp = webkit.TryGetValue("webprefs", out var w0) ? w0 as Dictionary<string, object> ?? new Dictionary<string, object>() : new Dictionary<string, object>();
                    foreach (var k in new[] { "default_font_size", "default_fixed_font_size", "fonts" }) wp.Remove(k);
                    foreach (var kv in webprefs) wp[kv.Key] = kv.Value;
                    if (wp.Count > 0) webkit["webprefs"] = wp; else webkit.Remove("webprefs");
                    return webkit;
                });
            }
            catch { }
        }

        /// <summary>A size chosen in this session, live for a tab (the preferences cover it from the next start).</summary>
        public static async Task Apply(CoreWebView2 core, bool force = false)
        {
            if (core == null || (!force && S.FontSize == startedSize)) return;
            try
            {
                await core.CallDevToolsProtocolMethodAsync("Page.setFontSizes", Store.ToJson(new Dictionary<string, object> {
                    { "fontSizes", new Dictionary<string, object> { { "standard", S.FontSize }, { "fixed", FixedFor(S.FontSize) } } } }));
            }
            catch { }
        }

        public static List<string> Installed()
        {
            try
            {
                using (var c = new System.Drawing.Text.InstalledFontCollection())
                    return c.Families.Select(f => f.Name).Where(n => !n.StartsWith("@")).Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch { return new List<string>(); }
        }
    }
}
