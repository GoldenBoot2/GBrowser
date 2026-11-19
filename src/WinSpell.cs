using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace GBrowser
{
    /// <summary>Spelling suggestions for GBrowser's own right-click menu. The engine marks misspelled words with the
    /// Windows spell checker but gives custom menus no suggestions (WebView2 API still a proposal, 19-11-2026), so they
    /// come from the same Windows spell checker (ISpellChecker, Windows 8+). "Add to dictionary" goes into the user's
    /// Windows dictionary, shared with Edge and other apps.</summary>
    internal static class WinSpell
    {
        [ComImport, Guid("8E018A9D-2415-4677-BF08-794EA61F94BB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ISpellCheckerFactory
        {
            IEnumString get_SupportedLanguages();
            [return: MarshalAs(UnmanagedType.Bool)] bool IsSupported([MarshalAs(UnmanagedType.LPWStr)] string languageTag);
            ISpellChecker CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string languageTag);
        }

        // vtable order as in spellcheck.h (only the first methods are used)
        [ComImport, Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ISpellChecker
        {
            [return: MarshalAs(UnmanagedType.LPWStr)] string get_LanguageTag();
            [return: MarshalAs(UnmanagedType.IUnknown)] object Check([MarshalAs(UnmanagedType.LPWStr)] string text);
            IEnumString Suggest([MarshalAs(UnmanagedType.LPWStr)] string word);
            void Add([MarshalAs(UnmanagedType.LPWStr)] string word);
            void Ignore([MarshalAs(UnmanagedType.LPWStr)] string word);
        }

        [ComImport, Guid("7AB36653-1796-484B-BDFA-E74F1DB7C1DC")]
        class SpellCheckerFactory { }

        static ISpellChecker checker;
        static string checkerLang;

        static ISpellChecker For(string lang)
        {
            if (checker != null && checkerLang == lang) return checker;
            var f = (ISpellCheckerFactory)new SpellCheckerFactory();
            string tag = f.IsSupported(lang) ? lang : f.IsSupported("en-US") ? "en-US" : null;
            if (tag == null) return null;
            checker = f.CreateSpellChecker(tag);
            checkerLang = lang;
            return checker;
        }

        /// <summary>Up to max suggestions for a word ("" list when it is spelled right or nothing is found).</summary>
        public static List<string> Suggest(string word, string lang, int max = 5)
        {
            var list = new List<string>();
            try
            {
                var c = For(lang);
                if (c == null || string.IsNullOrWhiteSpace(word)) return list;
                var e = c.Suggest(word.Trim());
                var one = new string[1];
                while (list.Count < max && e.Next(1, one, IntPtr.Zero) == 0) if (!string.IsNullOrEmpty(one[0]) && one[0] != word) list.Add(one[0]);
            }
            catch { }
            return list;
        }

        public static void AddToDictionary(string word, string lang)
        {
            try { For(lang)?.Add(word.Trim()); } catch { }
        }
    }
}
