using System;
using System.IO;

namespace GBrowser
{
    /// <summary>Internal pages (new tab, bookmarks, history, extensions, settings, about).
    /// They live in the app's pages folder and are served by WebView2 as https://gbrowser.invalid/,
    /// shown to the user as gbrowser://name.</summary>
    internal static class Pages
    {
        // ".invalid" is reserved (RFC 2606 / 6761) and never resolves: lookups fail at once. The old name ended in
        // ".local", which Windows looks up by multicast DNS on the local network for ~2-3 s; SmartScreen looks up
        // every address it checks, so each GBrowser page waited ~2.1 s before it could start (measured 19-11-2026).
        public const string Host = "gbrowser.invalid";
        public const string Origin = "https://" + Host + "/";
        const string OldOrigin = "https://gbrowser.local/";

        /// <summary>Addresses saved before 0.1.7 (tabs, bookmarks, links) under the old name, moved to the new one.</summary>
        public static string Upgrade(string url) =>
            url != null && url.StartsWith(OldOrigin, StringComparison.OrdinalIgnoreCase) ? Origin + url.Substring(OldOrigin.Length) : url;
        public static bool IsOld(string url) => url != null && url.StartsWith(OldOrigin, StringComparison.OrdinalIgnoreCase);
        public static readonly string Folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pages");
        public static readonly string[] Names = { "newtab", "bookmarks", "history", "extensions", "settings", "about", "downloads", "cookies", "log", "tests", "identity", "qr", "tasks", "insecure", "certerror", "crashed" };

        public static bool IsInternal(string url) =>
            url != null && url.StartsWith(Origin, StringComparison.OrdinalIgnoreCase);

        public static string Url(string name) => Origin + name + ".html";

        /// <summary>gbrowser://bookmarks -> https://gbrowser.invalid/bookmarks.html</summary>
        public static string FromDisplay(string text)
        {
            var name = text.Substring("gbrowser://".Length).Trim('/').ToLowerInvariant();
            int q = name.IndexOfAny(new[] { '?', '#' });
            string rest = q >= 0 ? name.Substring(q) : "";
            if (q >= 0) name = name.Substring(0, q);
            if (Array.IndexOf(Names, name) < 0) name = "newtab";
            return Url(name) + rest;
        }

        /// <summary>https://gbrowser.invalid/bookmarks.html -> gbrowser://bookmarks (the new tab page shows empty).</summary>
        public static string ToDisplay(string url)
        {
            url = Upgrade(url);
            if (!IsInternal(url)) return url ?? "";
            var path = url.Substring(Origin.Length);
            int q = path.IndexOfAny(new[] { '?', '#' });
            string rest = q >= 0 ? path.Substring(q) : "";
            if (q >= 0) path = path.Substring(0, q);
            if (path.EndsWith(".html")) path = path.Substring(0, path.Length - 5);
            if (path == "newtab") return "";
            return "gbrowser://" + path + rest;
        }

        public static string PageName(string url)
        {
            url = Upgrade(url);
            if (!IsInternal(url)) return null;
            var path = url.Substring(Origin.Length);
            int q = path.IndexOfAny(new[] { '?', '#' });
            if (q >= 0) path = path.Substring(0, q);
            return path.EndsWith(".html") ? path.Substring(0, path.Length - 5) : path;
        }
    }
}
