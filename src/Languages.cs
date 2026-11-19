using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace GBrowser
{
    /// <summary>Settings > Languages, like Chrome's: the preferred languages (their order is the Accept-Language header and
    /// navigator.languages). Stored where Chrome keeps them in the profile's Preferences
    /// (intl.accept_languages / selected_languages, spellcheck.dictionaries), written before
    /// the engine starts (a change needs a restart). With no list of the user's own nothing is written: the engine's
    /// default from Windows stays, the same as real Edge.</summary>
    internal static class Languages
    {
        static Settings S => Store.Settings;
        public static string Active = "";   // what the engine started with

        public static string Key => string.Join(",", S.Languages ?? new List<string>());

        public static void ApplyPrefs(string prefsPath)
        {
            Active = Key;
            var langs = (S.Languages ?? new List<string>()).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            bool custom = langs.Count > 0;
            EnginePrefs.Edit(prefsPath, "intl", d =>
            {
                if (custom) { d["accept_languages"] = string.Join(",", langs); d["selected_languages"] = string.Join(",", langs); }
                else if (S.LanguagesWritten) { d.Remove("accept_languages"); d.Remove("selected_languages"); }
                return d;
            });
            EnginePrefs.Edit(prefsPath, "spellcheck", d =>
            {
                // the engine checks with the Windows spell checker; its dictionaries follow the languages, like Chrome's.
                // There is no off switch: WebView2 kept underlining with browser.enable_spellchecking false and with no
                // dictionaries (measured 19-11-2026), so GBrowser offers none.
                if (custom) d["dictionaries"] = langs.Where(l => l.Contains("-")).DefaultIfEmpty(langs[0]).ToList();
                else if (S.LanguagesWritten || (d.TryGetValue("dictionaries", out var dv) && dv is System.Collections.ICollection dc && dc.Count == 0)) d.Remove("dictionaries");
                return d;
            });
            EnginePrefs.Edit(prefsPath, "browser", d => { d.Remove("enable_spellchecking"); return d; });   // earlier test builds wrote it
            if (S.LanguagesWritten != custom) { S.LanguagesWritten = custom; Store.SaveSettings(); }
        }

        /// <summary>The language the right-click spelling suggestions use.</summary>
        public static string SpellLanguage => (S.Languages != null && S.Languages.Count > 0) ? S.Languages[0] : CultureInfo.CurrentUICulture.Name;

        /// <summary>Languages to add: specific cultures (en-GB) and neutral ones (en), with their English names.</summary>
        public static List<Dictionary<string, object>> All()
        {
            return CultureInfo.GetCultures(CultureTypes.SpecificCultures | CultureTypes.NeutralCultures)
                .Where(c => c.Name.Length >= 2 && !c.Name.Contains("_") && c.Name.Split('-').Length <= 2)
                .GroupBy(c => c.Name).Select(g => g.First())
                .OrderBy(c => c.EnglishName, StringComparer.OrdinalIgnoreCase)
                .Select(c => new Dictionary<string, object> { { "code", c.Name }, { "name", c.EnglishName } }).ToList();
        }
    }
}
