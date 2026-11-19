using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GBrowser
{
    /// <summary>Default browser, the way Chrome does it on Windows 10/11: GBrowser registers itself for the current
    /// user as a browser (StartMenuInternet + Capabilities + RegisteredApplications, ProgIds for web addresses and
    /// web files). Windows lets only the user choose the default (Settings > Default apps; the choice is protected
    /// by a hash apps cannot write), so "Make default" registers and opens GBrowser's page there.
    /// Shared with the installer (registers at install, removes at uninstall).</summary>
    internal static class DefaultBrowser
    {
        public const string AppKey = "GBrowser";
        public const string UrlProgId = "GBrowserURL", HtmlProgId = "GBrowserHTML";
        static readonly string[] Schemes = { "http", "https" };
        static readonly string[] FileTypes = { ".htm", ".html", ".shtml", ".xht", ".xhtml", ".svg", ".webp", ".pdf" };

        /// <summary>Registers exe as a browser under HKCU\&lt;root&gt; (root "Software" for real; tests use their own key).</summary>
        public static void Register(string exe, string root = "Software")
        {
            string cmd = "\"" + exe + "\" \"%1\"", icon = "\"" + exe + "\",0";
            string clients = root + @"\Clients\StartMenuInternet\" + AppKey;
            using (var k = Registry.CurrentUser.CreateSubKey(clients))
            {
                k.SetValue("", "GBrowser");
                using (var c = k.CreateSubKey("Capabilities"))
                {
                    c.SetValue("ApplicationName", "GBrowser");
                    c.SetValue("ApplicationDescription", "GBrowser by GoldenBoot: a light browser with ad blocking");
                    c.SetValue("ApplicationIcon", icon);
                    using (var f = c.CreateSubKey("FileAssociations")) foreach (var t in FileTypes) f.SetValue(t, HtmlProgId);
                    using (var u = c.CreateSubKey("URLAssociations")) foreach (var sc in Schemes) u.SetValue(sc, UrlProgId);
                    using (var sm = c.CreateSubKey("StartMenu")) sm.SetValue("StartMenuInternet", AppKey);
                }
                using (var d = k.CreateSubKey("DefaultIcon")) d.SetValue("", icon);
                using (var o = k.CreateSubKey(@"shell\open\command")) o.SetValue("", "\"" + exe + "\"");
            }
            using (var r = Registry.CurrentUser.CreateSubKey(root + @"\RegisteredApplications"))
                r.SetValue(AppKey, @"Software\Clients\StartMenuInternet\" + AppKey + @"\Capabilities");
            ProgId(root, UrlProgId, "GBrowser URL", cmd, icon, true);
            ProgId(root, HtmlProgId, "GBrowser HTML Document", cmd, icon, false);
            if (root == "Software") Notify();
        }

        static void ProgId(string root, string id, string name, string cmd, string icon, bool url)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(root + @"\Classes\" + id))
            {
                k.SetValue("", name);
                if (url) { k.SetValue("URL Protocol", ""); k.SetValue("FriendlyTypeName", name); }
                using (var d = k.CreateSubKey("DefaultIcon")) d.SetValue("", icon);
                using (var c = k.CreateSubKey(@"shell\open\command")) c.SetValue("", cmd);
            }
        }

        public static void Unregister(string root = "Software")
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(root + @"\Clients\StartMenuInternet\" + AppKey, false); } catch { }
            try { using (var r = Registry.CurrentUser.OpenSubKey(root + @"\RegisteredApplications", true)) r?.DeleteValue(AppKey, false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(root + @"\Classes\" + UrlProgId, false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(root + @"\Classes\" + HtmlProgId, false); } catch { }
            if (root == "Software") Notify();
        }

        /// <summary>The exe GBrowser is registered with (null = not registered).</summary>
        public static string RegisteredExe(string root = "Software")
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(root + @"\Classes\" + UrlProgId + @"\shell\open\command"))
                {
                    var v = k?.GetValue("") as string;
                    if (string.IsNullOrEmpty(v) || !v.StartsWith("\"")) return null;
                    return v.Substring(1, v.IndexOf('"', 1) - 1);
                }
            }
            catch { return null; }
        }

        /// <summary>The user's choice for https links (what Windows opens web links with).</summary>
        public static bool IsDefault()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice"))
                    return string.Equals(k?.GetValue("ProgId") as string, UrlProgId, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>Settings > Default apps, on GBrowser's own page (Windows 11; Windows 10 shows the list).</summary>
        public static void OpenWindowsSettings()
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:defaultapps?registeredAppUser=" + AppKey) { UseShellExecute = true }); }
            catch { try { Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true }); } catch { } }
        }

        [DllImport("shell32.dll")] static extern void SHChangeNotify(int e, uint f, IntPtr a, IntPtr b);
        static void Notify() { try { SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero); } catch { } }
    }
}
