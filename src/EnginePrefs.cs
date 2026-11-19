using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace GBrowser
{
    /// <summary>Careful edits of the engine profile's Preferences before the engine starts: one top-level entry is
    /// replaced (its other sub-keys kept); the file must parse with every other top-level key kept, else nothing is
    /// written. Used by fonts (webkit), languages (intl), spell check (spellcheck, browser).</summary>
    internal static class EnginePrefs
    {
        public static string ProfilePrefs => Path.Combine(Store.WebDataDir, "EBWebView", "Default", "Preferences");

        /// <summary>modify gets the entry's object (empty when missing) and returns it (empty / null = remove the entry).</summary>
        public static void Edit(string prefsPath, string topKey, Func<Dictionary<string, object>, Dictionary<string, object>> modify)
        {
            try
            {
                var ser = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 };
                string text = File.Exists(prefsPath) ? File.ReadAllText(prefsPath, Encoding.UTF8) : null;
                if (text == null)
                {
                    var fresh = modify(new Dictionary<string, object>());
                    if (fresh == null || fresh.Count == 0) return;
                    Directory.CreateDirectory(Path.GetDirectoryName(prefsPath));
                    File.WriteAllText(prefsPath, ser.Serialize(new Dictionary<string, object> { { topKey, fresh } }), new UTF8Encoding(false));
                    return;
                }
                var before = ser.DeserializeObject(text) as Dictionary<string, object>;
                if (before == null) return;
                var cur = before.TryGetValue(topKey, out var v) ? v as Dictionary<string, object> : null;
                if (before.ContainsKey(topKey) && cur == null) return;   // not an object: leave it
                var next = modify(cur != null ? new Dictionary<string, object>(cur) : new Dictionary<string, object>());
                string member = next != null && next.Count > 0 ? "\"" + topKey + "\":" + ser.Serialize(next) : null;
                string updated;
                if (FindTopKey(text, topKey, out int start, out int end))
                {
                    if (member != null) updated = text.Substring(0, start) + member + text.Substring(end);
                    else
                    {
                        int s2 = start, e2 = end;
                        int j = e2; while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
                        if (j < text.Length && text[j] == ',') e2 = j + 1;
                        else { int i = s2 - 1; while (i >= 0 && char.IsWhiteSpace(text[i])) i--; if (i >= 0 && text[i] == ',') s2 = i; }
                        updated = text.Substring(0, s2) + text.Substring(e2);
                    }
                }
                else
                {
                    if (member == null) return;
                    int brace = text.IndexOf('{');
                    if (brace < 0) return;
                    string rest = text.Substring(brace + 1).TrimStart();
                    updated = "{" + member + (rest.StartsWith("}") ? "" : ",") + text.Substring(brace + 1);
                }
                if (updated == text) return;
                var after = ser.DeserializeObject(updated) as Dictionary<string, object>;
                if (after == null || after.Keys.Count(k => k != topKey) != before.Keys.Count(k => k != topKey)) return;
                string tmp = prefsPath + ".gbtmp";
                File.WriteAllText(tmp, updated, new UTF8Encoding(false));
                File.Replace(tmp, prefsPath, null);
            }
            catch { }
        }

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
                    if (depth == 1 && t.Substring(i + 1, Math.Max(0, j - i - 1)) == key)
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
    }
}
