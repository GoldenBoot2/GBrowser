using System;
using System.IO;

namespace GBrowser
{
    /// <summary>Internal pages (new tab, bookmarks, history, extensions, settings, about).
    /// They live in the app's pages folder and are served by WebView2 as https://gbrowser.local/,
    /// shown to the user as gbrowser://name.</summary>
    internal static class Pages
    {
        public const string Host = "gbrowser.local";
        public const string Origin = "https://" + Host + "/";
        public static readonly string Folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pages");
        public static readonly string[] Names = { "newtab", "bookmarks", "history", "extensions", "settings", "about", "downloads", "cookies", "log", "tests", "identity", "qr", "insecure", "certerror", "crashed" };

        public static bool IsInternal(string url) =>
            url != null && url.StartsWith(Origin, StringComparison.OrdinalIgnoreCase);

        public static string Url(string name) => Origin + name + ".html";

        /// <summary>gbrowser://bookmarks -> https://gbrowser.local/bookmarks.html</summary>
        public static string FromDisplay(string text)
        {
            var name = text.Substring("gbrowser://".Length).Trim('/').ToLowerInvariant();
            int q = name.IndexOfAny(new[] { '?', '#' });
            string rest = q >= 0 ? name.Substring(q) : "";
            if (q >= 0) name = name.Substring(0, q);
            if (Array.IndexOf(Names, name) < 0) name = "newtab";
            return Url(name) + rest;
        }

        /// <summary>https://gbrowser.local/bookmarks.html -> gbrowser://bookmarks (the new tab page shows empty).</summary>
        public static string ToDisplay(string url)
        {
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
            if (!IsInternal(url)) return null;
            var path = url.Substring(Origin.Length);
            int q = path.IndexOfAny(new[] { '?', '#' });
            if (q >= 0) path = path.Substring(0, q);
            return path.EndsWith(".html") ? path.Substring(0, path.Length - 5) : path;
        }
    }
}
