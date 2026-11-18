using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace GBrowser
{
    /// <summary>Settings, bookmarks, history and session, saved as small JSON files in the data folder.</summary>
    internal static class Store
    {
        public static readonly string DataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GoldenBoot",
#if AUTOMATION
            "GBrowser-Test");   // local test builds never touch the real profile
#else
            "GBrowser");
#endif
        public static readonly string WebDataDir = Path.Combine(DataDir, "WebData");
        public static readonly string ExtensionsDir = Path.Combine(DataDir, "Extensions");

        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 };

        public static Settings Settings = new Settings();
        public static List<Bookmark> Bookmarks = new List<Bookmark>();
        public static List<HistoryItem> History = new List<HistoryItem>();
        const int HistoryMax = 5000;

        public static void Load()
        {
            Directory.CreateDirectory(DataDir);
            Settings = Read<Settings>("settings.json") ?? new Settings();
            Bookmarks = Read<List<Bookmark>>("bookmarks.json") ?? new List<Bookmark>();
            History = Read<List<HistoryItem>>("history.json") ?? new List<HistoryItem>();
        }

        public static string ToJson(object o) => Json.Serialize(o);
        public static T FromJson<T>(string s) => Json.Deserialize<T>(s);

        static T Read<T>(string name) where T : class
        {
            try
            {
                var p = Path.Combine(DataDir, name);
                return File.Exists(p) ? Json.Deserialize<T>(File.ReadAllText(p, Encoding.UTF8)) : null;
            }
            catch { return null; }
        }

        static void Write(string name, object o)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                var p = Path.Combine(DataDir, name);
                var tmp = p + ".tmp";
                File.WriteAllText(tmp, Json.Serialize(o), new UTF8Encoding(false));
                if (File.Exists(p)) File.Replace(tmp, p, null); else File.Move(tmp, p);
            }
            catch { }
        }

        public static void SaveSettings() => Write("settings.json", Settings);
        public static void SaveBookmarks() => Write("bookmarks.json", Bookmarks);
        public static void SaveHistory() => Write("history.json", History);

        public static bool IsBookmarked(string url) => Bookmarks.Any(b => b.Url == url);

        public static void ToggleBookmark(string url, string title)
        {
            var b = Bookmarks.FirstOrDefault(x => x.Url == url);
            if (b != null) Bookmarks.Remove(b);
            else Bookmarks.Insert(0, new Bookmark { Url = url, Title = string.IsNullOrEmpty(title) ? url : title, Added = Now() });
            SaveBookmarks();
        }

        static DateTime lastHistorySave = DateTime.MinValue;

        public static void AddHistory(string url, string title)
        {
            if (string.IsNullOrEmpty(url) || Pages.IsInternal(url) || url.StartsWith("about:") || url.StartsWith("data:")) return;
            var last = History.FirstOrDefault();
            if (last != null && last.Url == url && Now() - last.Time < 30 * 60 * 1000L)
            {
                last.Time = Now();
                if (!string.IsNullOrEmpty(title)) last.Title = title;
            }
            else History.Insert(0, new HistoryItem { Url = url, Title = string.IsNullOrEmpty(title) ? url : title, Time = Now() });
            if (History.Count > HistoryMax) History.RemoveRange(HistoryMax, History.Count - HistoryMax);
            if ((DateTime.UtcNow - lastHistorySave).TotalSeconds > 20) { lastHistorySave = DateTime.UtcNow; SaveHistory(); }
        }

        public static void UpdateHistoryTitle(string url, string title)
        {
            var h = History.FirstOrDefault();
            if (h != null && h.Url == url && !string.IsNullOrEmpty(title)) h.Title = title;
        }

        public static long Now() => (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
    }

    public class Settings
    {
        public string HomePage { get; set; } = "gbrowser://newtab";
        public string SearchEngine { get; set; } = "google";
        public string ThemeMode { get; set; } = "system";   // system | light | dark
        public bool RestoreTabs { get; set; } = true;
        public int SleepMinutes { get; set; } = 5;          // 0 = never put hidden tabs to sleep
        public bool LowMemoryHiddenTabs { get; set; } = true;
        public List<string> KeepActiveSites { get; set; } = new List<string>();   // never unloaded (site = registrable domain)
        public bool ShowBookmarksBar { get; set; } = false;
        // ad blocking (same switches as the WebView Adblocker Android app)
        public bool AdblockEnabled { get; set; } = true;
        public bool BlockAnnoyances { get; set; } = true;      // cookie notices, popups, app banners
        public bool BlockTrackers { get; set; } = true;        // trackers + tracking parameters in links
        public bool HideAdLeftovers { get; set; } = true;      // element hiding (cosmetic rules)
        public bool Stealth { get; set; } = true;              // hidden ads still measure like visible ones (anti ad-block detectors)
        public bool BlockAdSitePages { get; set; } = true;     // pages of ad / malware sites show a block page
        public bool DoNotTrack { get; set; } = false;
        public List<string> AllowedSites { get; set; } = new List<string>();
        public long TotalBlocked { get; set; }
        public List<string> Session { get; set; } = new List<string>();
        public int SessionIndex { get; set; }
        public int WindowX { get; set; } = -1;
        public int WindowY { get; set; } = -1;
        public int WindowW { get; set; } = 1200;
        public int WindowH { get; set; } = 800;
        public bool WindowMax { get; set; }
    }

    public class Bookmark
    {
        public string Url { get; set; }
        public string Title { get; set; }
        public long Added { get; set; }
    }

    public class HistoryItem
    {
        public string Url { get; set; }
        public string Title { get; set; }
        public long Time { get; set; }
    }

    internal static class Search
    {
        public static readonly Dictionary<string, string[]> Engines = new Dictionary<string, string[]>
        {
            { "google", new[] { "Google", "https://www.google.com/search?q=" } },
            { "bing", new[] { "Bing", "https://www.bing.com/search?q=" } },
            { "duckduckgo", new[] { "DuckDuckGo", "https://duckduckgo.com/?q=" } },
            { "brave", new[] { "Brave Search", "https://search.brave.com/search?q=" } },
        };

        /// <summary>Turns what the user typed in the address box into a URL (address or search).</summary>
        public static string ToUrl(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return null;
            if (text.StartsWith("gbrowser://", StringComparison.OrdinalIgnoreCase)) return Pages.FromDisplay(text);
            if (Uri.TryCreate(text, UriKind.Absolute, out var u) &&
                (u.Scheme == "http" || u.Scheme == "https" || u.Scheme == "file" || u.Scheme == "chrome-extension" || u.Scheme == "edge"))
                return text;
            if (text.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return text;
            bool looksHost = !text.Contains(" ") && (text.Contains(".") || text.StartsWith("localhost", StringComparison.OrdinalIgnoreCase))
                && !text.EndsWith(".") && Uri.TryCreate("https://" + text, UriKind.Absolute, out var h)
                && (h.Host.Contains(".") || h.Host == "localhost");
            if (looksHost)
            {
                bool local = text.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) || text.StartsWith("127.") || text.StartsWith("192.168.") || text.StartsWith("10.");
                return (local ? "http://" : "https://") + text;
            }
            var e = Engines.TryGetValue(Store.Settings.SearchEngine ?? "", out var v) ? v : Engines["google"];
            return e[1] + Uri.EscapeDataString(text);
        }
    }
}
