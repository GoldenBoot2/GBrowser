using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace GBrowser
{
    /// <summary>One tab. Its WebView2 is created only when the tab is first shown (restored tabs cost no memory
    /// until opened) and is suspended after a while in the background (sleeping tabs).</summary>
    internal sealed class BrowserTab
    {
        public WebView2 View;
        public string Url;            // real URL (internal pages: https://gbrowser.local/...)
        public string Title = "New tab";
        public Image Favicon;
        public bool ApplyingZoom;   // GBrowser is setting the site's saved zoom (not the user zooming)
        public bool Loading, Sleeping, PlayingAudio;
        public string Group;          // tab group ID (TabStrip.Groups), null = not in a group
        public bool Pinned;           // narrow tab with only its icon, kept at the left (like Chrome)
        public bool Muted;            // the tab's sound is off (engine IsMuted)
        public bool FindOpen;         // this tab's find bar is open (each tab keeps its own, like Chrome)
        public string FindText;
        public bool OpenedByPage;     // created for NewWindowRequested: the page fills it, don't navigate
        public int Blocked;           // ads / trackers blocked on the current page
        public Blocking Blocking;
        /// <summary>Private tab (WebView2 InPrivate): own cookies/storage kept only in memory and dropped when the
        /// last private tab closes; no history, not restored, not unloaded to disk, no extensions.</summary>
        public bool Private;
        public DateTime HiddenSince = DateTime.UtcNow;
        public bool Created => View?.CoreWebView2 != null;
        static int nextId;
        public readonly int Id = System.Threading.Interlocked.Increment(ref nextId);   // for the request log page
        Task creating;

        public BrowserTab(string url, string title = null)
        {
            Url = url;
            if (!string.IsNullOrEmpty(title)) Title = title;
            else if (Pages.IsInternal(url)) Title = PageTitle(url);
            else if (!string.IsNullOrEmpty(url)) Title = url;
        }

        public string TooltipText => (Private ? "Private: " : "") + Title + (Blocked > 0 ? "\n" + Blocked + " blocked" : "") + (Sleeping ? "\nSleeping" : "");

        public static string PageTitle(string url)
        {
            switch (Pages.PageName(url))
            {
                case "bookmarks": return "Bookmarks";
                case "history": return "History";
                case "extensions": return "Extensions";
                case "settings": return "Settings";
                case "about": return "About GBrowser";
                case "downloads": return "Downloads";
                case "cookies": return "Cookies and site data";
                case "log": return "Request log";
                case "tests": return "Blocking tests";
                case "identity": return "Identity check";
                case "qr": return "QR code";
                case "tasks": return "Task manager";
                case "insecure": return "Not secure";
                case "certerror": return "Privacy error";
                default: return "New tab";
            }
        }

        /// <summary>Creates the WebView2 inside host (once). The returned task completes when CoreWebView2 is ready.</summary>
        public Task EnsureCreated(CoreWebView2Environment env, Control host, Action<BrowserTab> setup)
        {
            if (creating != null) return creating;
            View = new WebView2 { Dock = DockStyle.Fill, Visible = false, DefaultBackgroundColor = Theme.Surface };
            host.Controls.Add(View);
            creating = Create(env, setup);
            return creating;
        }

        async Task Create(CoreWebView2Environment env, Action<BrowserTab> setup)
        {
            if (Private)
            {
                var o = env.CreateCoreWebView2ControllerOptions();
                o.IsInPrivateModeEnabled = true;
                o.ProfileName = "private";
                await View.EnsureCoreWebView2Async(env, o);
            }
            else await View.EnsureCoreWebView2Async(env);
            // genuine Edge client hints before the first page (see UaOverride)
            await UaOverride.Apply(View.CoreWebView2);
            setup(this);
        }

        public void Navigate(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            Url = url;
            if (Created)
            {
                try { View.CoreWebView2.Navigate(url); }
                catch (ArgumentException) { View.CoreWebView2.Navigate(Search.ToUrl(url) ?? Pages.Url("newtab")); }
            }
        }

        public async void UpdateFavicon(Action done)
        {
            try
            {
                if (!Created || string.IsNullOrEmpty(View.CoreWebView2.FaviconUri)) { Favicon = null; done(); return; }
                using (var s = await View.CoreWebView2.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png))
                {
                    if (s == null || s.Length == 0) { Favicon = null; done(); return; }
                    var ms = new MemoryStream();
                    await s.CopyToAsync(ms);
                    // remembered for bookmarks / history / new tab (never from private tabs or GBrowser's own pages)
                    string src = View.CoreWebView2.Source;
                    bool keep = !Private && !Pages.IsInternal(src);
                    if (keep) Favicons.Save(src, ms.ToArray());
                    ms.Position = 0;
                    var old = Favicon;
                    // a sharper icon kept for the site (SharpIcons) is used on the tab too
                    var sharp = keep ? Favicons.Image(src) : null;
                    Favicon = sharp != null && sharp.Width > 16 ? new Bitmap(sharp) : new Bitmap(Image.FromStream(ms));
                    old?.Dispose();
                    if (keep) GetSharpIcon(src, View.CoreWebView2.FaviconUri, done);
                }
            }
            catch { Favicon = null; }
            done();
        }

        async void GetSharpIcon(string src, string engineIcon, Action done)
        {
            try
            {
                var png = await SharpIcons.Fetch(View.CoreWebView2, src, engineIcon);
                if (png == null || !Created || View.CoreWebView2.Source != src) return;
                Favicons.Save(src, png);
                var img = Favicons.Image(src);
                if (img == null || img.Width <= 16) return;
                var old = Favicon;
                Favicon = new Bitmap(img);
                old?.Dispose();
                done();
            }
            catch { }
        }

        /// <summary>Hidden tab: ask the engine to use less memory; after SleepMinutes it is suspended.</summary>
        public void OnHidden()
        {
            HiddenSince = DateTime.UtcNow;
            if (!Created) return;
            View.Visible = false;
            if (Store.Settings.LowMemoryHiddenTabs)
                try { View.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low; } catch { }
        }

        public void OnShown()
        {
            if (!Created) return;
            try { View.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal; } catch { }
            View.Visible = true;
            Sleeping = false;
        }

        /// <summary>Unloads a background tab (like Chrome's Memory Saver): its WebView2 and page processes are freed;
        /// the tab keeps its address, title and icon and loads again when it is opened. Measured 2026-11-18:
        /// WebView2's TrySuspendAsync only freezes a page (5 news tabs: 1,092 -> 1,076 MB), so it is not used.</summary>
        public void Discard()
        {
            if (!Created || View.Visible || PlayingAudio || Private) return;   // a private session lives only in memory
            try { Url = View.CoreWebView2.Source; } catch { }
            string site = Blocking.SiteOf(Url);
            if (site != null && Store.Settings.KeepActiveSites.Contains(site)) return;   // user: always keep active
            try { View.Parent?.Controls.Remove(View); View.Dispose(); } catch { }
            View = null;
            creating = null;
            Blocking = null;
            Blocked = 0;
            Loading = false;
            OpenedByPage = false;
            Sleeping = true;
        }

        public void Dispose()
        {
            try { View?.Parent?.Controls.Remove(View); View?.Dispose(); } catch { }
            Favicon?.Dispose();
            View = null;
        }
    }
}
