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
                var ser = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 };
                string text = File.Exists(prefsPath) ? File.ReadAllText(prefsPath, Encoding.UTF8) : null;
                if (text == null)
                {
                    if (webprefs.Count == 0) return;
                    Directory.CreateDirectory(Path.GetDirectoryName(prefsPath));
                    File.WriteAllText(prefsPath, ser.Serialize(new Dictionary<string, object> { { "webkit", new Dictionary<string, object> { { "webprefs", webprefs } } } }), new UTF8Encoding(false));
                    return;
                }
                var before = ser.DeserializeObject(text) as Dictionary<string, object>;
                if (before == null) return;
                // the existing "webkit" entry with GBrowser's keys replaced (others the engine may have kept stay)
                var webkit = before.TryGetValue("webkit", out var wk) ? wk as Dictionary<string, object> ?? new Dictionary<string, object>() : new Dictionary<string, object>();
                var wp = webkit.TryGetValue("webprefs", out var w0) ? w0 as Dictionary<string, object> ?? new Dictionary<string, object>() : new Dictionary<string, object>();
                foreach (var k in new[] { "default_font_size", "default_fixed_font_size", "fonts" }) wp.Remove(k);
                foreach (var kv in webprefs) wp[kv.Key] = kv.Value;
                if (wp.Count > 0) webkit["webprefs"] = wp; else webkit.Remove("webprefs");
                string newWebkit = webkit.Count > 0 ? "\"webkit\":" + ser.Serialize(webkit) : null;
                int start, end;
                string updated;
                if (FindTopKey(text, "webkit", out start, out end))
                {
                    if (newWebkit != null) updated = text.Substring(0, start) + newWebkit + text.Substring(end);
                    else
                    {
                        // remove the member and one comma next to it
                        int s2 = start, e2 = end;
                        int j = e2; while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
                        if (j < text.Length && text[j] == ',') e2 = j + 1;
                        else { int i = s2 - 1; while (i >= 0 && char.IsWhiteSpace(text[i])) i--; if (i >= 0 && text[i] == ',') s2 = i; }
                        updated = text.Substring(0, s2) + text.Substring(e2);
                    }
                }
                else
                {
                    if (newWebkit == null) return;
                    int brace = text.IndexOf('{');
                    if (brace < 0) return;
                    string rest = text.Substring(brace + 1).TrimStart();
                    updated = "{" + newWebkit + (rest.StartsWith("}") ? "" : ",") + text.Substring(brace + 1);
                }
                if (updated == text) return;
                var after = ser.DeserializeObject(updated) as Dictionary<string, object>;
                if (after == null || after.Keys.Count(k => k != "webkit") != before.Keys.Count(k => k != "webkit")) return;
                string tmp = prefsPath + ".gbtmp";
                File.WriteAllText(tmp, updated, new UTF8Encoding(false));
                File.Replace(tmp, prefsPath, null);
            }
            catch { }
        }

        /// <summary>The span of a top-level member "key": value (from the opening quote to the end of the value).</summary>
        static bool FindTopKey(string t, string key, out int start, out int end)
        {
            start = end = -1;
            int depth = 0;
            for (int i = 0; i < t.Length; i++)
            {
                char c = t[i];
                if (c == '"')
                {
                    int j = i + 1;
                    while (j < t.Length && t[j] != '"') { if (t[j] == '\\') j++; j++; }
                    if (depth == 1 && j + 1 <= t.Length && t.Substring(i + 1, j - i - 1) == key)
                    {
                        int k = j + 1; while (k < t.Length && char.IsWhiteSpace(t[k])) k++;
                        if (k < t.Length && t[k] == ':')
                        {
                            start = i;
                            int v = k + 1; while (v < t.Length && char.IsWhiteSpace(t[v])) v++;
                            end = ValueEnd(t, v);
                            return end > v;
                        }
                    }
                    i = j;
                }
                else if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') depth--;
            }
            return false;
        }

        static int ValueEnd(string t, int v)
        {
            if (v >= t.Length) return -1;
            if (t[v] == '"') { int j = v + 1; while (j < t.Length && t[j] != '"') { if (t[j] == '\\') j++; j++; } return j + 1; }
            if (t[v] == '{' || t[v] == '[')
            {
                int depth = 0;
                for (int i = v; i < t.Length; i++)
                {
                    char c = t[i];
                    if (c == '"') { int j = i + 1; while (j < t.Length && t[j] != '"') { if (t[j] == '\\') j++; j++; } i = j; }
                    else if (c == '{' || c == '[') depth++;
                    else if ((c == '}' || c == ']') && --depth == 0) return i + 1;
                }
                return -1;
            }
            int e = v; while (e < t.Length && t[e] != ',' && t[e] != '}' && t[e] != ']') e++;
            return e;
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
