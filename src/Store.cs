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
            Bm.Migrate();
            History = Read<List<HistoryItem>>("history.json") ?? new List<HistoryItem>();
            EncryptLeftovers();
        }

        public static string ToJson(object o) => Json.Serialize(o);
        public static T FromJson<T>(string s) => Json.Deserialize<T>(s);

        static readonly HashSet<string> plainFound = new HashSet<string>();

        /// <summary>Decrypted text of one of GBrowser's data files (see SecureFile); null when missing.
        /// A file that cannot be decrypted is kept as name.unreadable, never overwritten.</summary>
        public static string ReadText(string name)
        {
            var p = Path.Combine(DataDir, name);
            try
            {
                var t = SecureFile.Read(p, out bool wasPlain);
                if (wasPlain) plainFound.Add(name);
                return t;
            }
            catch (Exception)
            {
                try { File.Move(p, p + "." + DateTime.Now.ToString("dd-MM-yyyy_HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture) + ".unreadable"); } catch { }
                return null;
            }
        }

        public static bool WasPlain(string name) => plainFound.Contains(name);

        public static void WriteText(string name, string text)
        {
            try { SecureFile.Write(Path.Combine(DataDir, name), text); plainFound.Remove(name); } catch { }
        }

        static T Read<T>(string name) where T : class
        {
            try { var t = ReadText(name); return t == null ? null : Json.Deserialize<T>(t); }
            catch { return null; }
        }

        static void Write(string name, object o) => WriteText(name, Json.Serialize(o));
        /// <summary>Other encrypted data files (cookie snapshots ...).</summary>
        public static T ReadData<T>(string name) where T : class => Read<T>(name);
        public static void WriteData(string name, object o) => Write(name, o);

        /// <summary>Re-saves files that were still plain text (older versions), now encrypted.</summary>
        public static void EncryptLeftovers()
        {
            if (plainFound.Contains("settings.json")) SaveSettings();
            if (plainFound.Contains("bookmarks.json")) SaveBookmarks();
            if (plainFound.Contains("history.json")) SaveHistory();
        }

        public static void SaveSettings() => Write("settings.json", Settings);
        public static void SaveBookmarks() => Write("bookmarks.json", Bookmarks);
        public static void SaveHistory() => Write("history.json", History);

        public static bool IsBookmarked(string url) => Bm.IsBookmarked(url);

        static DateTime lastHistorySave = DateTime.MinValue;

        public static void AddHistory(string url, string title)
        {
            if (string.IsNullOrEmpty(url)) return;
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
        public bool SearchSuggestions { get; set; } = true;  // address bar: ask the search engine for suggestions
        public string DevToolsDock { get; set; } = "right";  // right | bottom (docked DevTools, DevToolsDock.cs)
        public double DevToolsWidth { get; set; } = 0.38;    // share of the page area
        public double DevToolsHeight { get; set; } = 0.4;
        public string ThemeColor { get; set; } = "gold";    // gold | accent (Windows accent colour) | #RRGGBB
        public bool RestoreTabs { get; set; } = true;
        public int SleepMinutes { get; set; } = 5;          // 0 = never put hidden tabs to sleep
        public bool LowMemoryHiddenTabs { get; set; } = true;
        public List<string> KeepActiveSites { get; set; } = new List<string>();   // never unloaded (site = registrable domain)
        public bool ShowBookmarksBar { get; set; } = false;
        public bool ShowHomeButton { get; set; } = false;   // like Chrome: off by default
        // ad blocking (same switches as the WebView Adblocker Android app)
        public bool AdblockEnabled { get; set; } = true;
        public bool BlockAnnoyances { get; set; } = true;      // cookie notices, popups, app banners
        public bool BlockTrackers { get; set; } = true;        // trackers + tracking parameters in links
        public bool HideAdLeftovers { get; set; } = true;      // element hiding (cosmetic rules)
        public bool Stealth { get; set; } = true;              // hidden ads still measure like visible ones (anti ad-block detectors)
        public bool BlockAdSitePages { get; set; } = true;     // pages of ad / malware sites show a block page
        public bool DoNotTrack { get; set; } = false;
        // security / privacy
        public bool HttpsFirst { get; set; } = true;              // always use secure connections
        public bool SmartScreen { get; set; } = true;             // Microsoft Defender SmartScreen (sites + downloads)
        public string TrackingPrevention { get; set; } = "balanced";   // basic | balanced | strict (Edge tracking prevention)
        public string SecureDns { get; set; } = "cloudflare";     // off | cloudflare | google | quad9 | adguard | custom:<https template>
        public string DownloadFolder { get; set; } = "";          // "" = the Windows Downloads folder
        public bool AskWhereToSave { get; set; } = false;         // Save As window for every download
        public int DownloadConnections { get; set; } = 8;         // multi-part downloads: most connections (1 = off)
        public int DownloadSpeedLimit { get; set; } = 0;          // KB/s for GBrowser's own downloads, 0 = no limit
        public string DownloadConflict { get; set; } = "ask";      // a file with that name exists: ask | keep (both) | replace
        public Dictionary<string, bool> ExternalAllowed { get; set; } = new Dictionary<string, bool>();   // "origin|scheme": open app links without asking
        public Dictionary<string, double> SiteZoom { get; set; } = new Dictionary<string, double>();   // host -> zoom, like Chrome
        public bool ClearOnExit { get; set; } = false;           // cookies, site data, cache, history when GBrowser closes
        public List<string> AllowedSites { get; set; } = new List<string>();
        public long TotalBlocked { get; set; }
        public List<string> Session { get; set; } = new List<string>();
        public int SessionIndex { get; set; }
        public int SessionPinned { get; set; }    // the first N session tabs are pinned
        public int WindowX { get; set; } = -1;
        public int WindowY { get; set; } = -1;
        public int WindowW { get; set; } = 1200;
        public int WindowH { get; set; } = 800;
        public bool WindowMax { get; set; }
        public long LastBookmarkFolder { get; set; } = 1;
        public List<string> DisabledLists { get; set; } = new List<string>();   // filter list files turned off one by one
        public string UserRules { get; set; } = "";                             // your own filter rules (one per line)
        public List<CustomList> CustomLists { get; set; } = new List<CustomList>();
        public List<CookieRule> CookieRules { get; set; } = new List<CookieRule>();           // per site: allow / block / clear
        public List<CookieBlock> CookieBlocks { get; set; } = new List<CookieBlock>();        // cookie names blocked (site or "*")
        public List<ProtectedCookie> CookieProtected { get; set; } = new List<ProtectedCookie>();
        public bool CookieAutoDelete { get; set; } = false;                                  // a site's cookies go after its last tab closes
        public List<string> CookieKeep { get; set; } = new List<string>();                   // sites auto-delete keeps
        public int CookieMaxDays { get; set; } = 0;                                         // cap cookie lifetime (0 = off)
        public bool BlockThirdPartyCookies { get; set; } = false;
        public string UaOverride { get; set; }          // genuine Edge client hints for pages (UaOverride.cs), measured once
        public string UaOverrideVersion { get; set; }   // engine version they were measured on
        public bool HostListUpdates { get; set; } = true;       // host lists downloaded again each day
        public long HostListsUpdated { get; set; }   // where the star puts new bookmarks (the last folder chosen)
        /// <summary>Every window of the last session (the last used first): tabs, selected tab, pinned count, bounds.</summary>
        public List<WindowSession> Windows { get; set; } = new List<WindowSession>();
    }

    public class WindowSession
    {
        public List<string> Tabs { get; set; } = new List<string>();
        public int Index { get; set; }
        public int Pinned { get; set; }
        public int X { get; set; } = -1;
        public int Y { get; set; } = -1;
        public int W { get; set; } = 1200;
        public int H { get; set; } = 800;
        public bool Max { get; set; }
    }

    public class Bookmark
    {
        public long Id { get; set; }
        public long Parent { get; set; }      // Bm.Bar (1), Bm.Other (2) or a folder's Id
        public bool Folder { get; set; }
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

        public static string EngineName() => Engines.TryGetValue(Store.Settings.SearchEngine ?? "google", out var e) ? e[0] : "Google";

        /// <summary>True for an address that is a search on one of the search engines.</summary>
        public static bool IsSearchUrl(string url) => url != null && Engines.Values.Any(e => url.StartsWith(e[1], StringComparison.OrdinalIgnoreCase));

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
