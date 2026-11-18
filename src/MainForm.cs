using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace GBrowser
{
    internal sealed class MainForm : Form
    {
        readonly TabStrip strip = new TabStrip { Dock = DockStyle.Top };
        readonly Toolbar toolbar = new Toolbar { Dock = DockStyle.Top };
        readonly BookmarksBar bookmarksBar = new BookmarksBar { Dock = DockStyle.Top };
        readonly Panel content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface };
        readonly Timer sleepTimer = new Timer { Interval = 30000 };
        readonly Stack<string> closedTabs = new Stack<string>();
        CoreWebView2Environment env;
        bool fullscreen; FormWindowState preFullState; FormBorderStyle preFullBorder;

        List<BrowserTab> Tabs => strip.Tabs;
        BrowserTab Current => strip.Selected >= 0 && strip.Selected < Tabs.Count ? Tabs[strip.Selected] : null;
        static readonly string AppVersion = typeof(MainForm).Assembly.GetName().Version.ToString(3);

        readonly Timer uiTimer = new Timer { Interval = 300 };
        bool uiDirty;
        long savedTotal;

        public MainForm()
        {
            FilterEngine.Preload();
            Text = "GBrowser";
            BackColor = Theme.Surface;
            AutoScaleMode = AutoScaleMode.Dpi;
            KeyPreview = true;
            MinimumSize = new Size(480, 360);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            var st = Store.Settings;
            StartPosition = FormStartPosition.Manual;
            var wa = Screen.PrimaryScreen.WorkingArea;
            var bounds = new Rectangle(st.WindowX, st.WindowY, Math.Max(600, st.WindowW), Math.Max(400, st.WindowH));
            if (st.WindowX < 0 || !Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(bounds)))
                bounds = new Rectangle(wa.X + (wa.Width - Math.Min(1200, wa.Width)) / 2, wa.Y + (wa.Height - Math.Min(800, wa.Height)) / 2,
                    Math.Min(1200, wa.Width), Math.Min(800, wa.Height));
            Bounds = bounds;
            if (st.WindowMax) WindowState = FormWindowState.Maximized;

            Controls.Add(content);
            Controls.Add(bookmarksBar);
            Controls.Add(toolbar);
            Controls.Add(strip);
            bookmarksBar.Visible = st.ShowBookmarksBar;
            ApplyHeights();

            strip.TabSelected += i => Select(i);
            strip.TabClosed += i => CloseTab(i);
            strip.NewTabClicked += () => NewTab(null);
            strip.TabMoved += (from, to) =>
            {
                var t = Tabs[from]; Tabs.RemoveAt(from); Tabs.Insert(to, t);
                strip.Selected = Tabs.IndexOf(Current == null ? t : Current);
                if (strip.Selected < 0) strip.Selected = to;
                strip.Invalidate();
            };
            toolbar.Back += () => { if (Current?.Created == true && Current.View.CoreWebView2.CanGoBack) Current.View.CoreWebView2.GoBack(); };
            toolbar.Forward += () => { if (Current?.Created == true && Current.View.CoreWebView2.CanGoForward) Current.View.CoreWebView2.GoForward(); };
            toolbar.Reload += () => { if (Current?.Created == true) Current.View.CoreWebView2.Reload(); };
            toolbar.Stop += () => { if (Current?.Created == true) Current.View.CoreWebView2.Stop(); };
            toolbar.Home += () => Current?.Navigate(Search.ToUrl(Store.Settings.HomePage) ?? Pages.Url("newtab"));
            toolbar.Go += text =>
            {
                var url = Search.ToUrl(text);
                if (url == null || Current == null) return;
                Current.Navigate(url);
                Current.View?.Focus();
            };
            toolbar.Star += ToggleBookmark;
            toolbar.MenuClicked += ShowMenu;
            toolbar.ExtensionsClicked += ShowExtensionsMenu;
            toolbar.StoreChipClicked += InstallFromStorePage;
            toolbar.ShieldClicked += ShowShieldMenu;
            uiTimer.Tick += (s, e) =>
            {
                if (!uiDirty) return;
                uiDirty = false;
                strip.Invalidate();
                UpdateUi();
            };
            uiTimer.Start();
            bookmarksBar.Open += (url, newTab) => { if (newTab) NewTab(url, false); else Current?.Navigate(url); };

            sleepTimer.Tick += async (s, e) =>
            {
                await SleepHiddenTabs();
                if (Store.Settings.TotalBlocked != savedTotal) { savedTotal = Store.Settings.TotalBlocked; Store.SaveSettings(); }
            };
            sleepTimer.Start();
            Load += async (s, e) => await Start();
#if AUTOMATION
            Automation.Start(this);
#endif
        }

        void ApplyHeights()
        {
            strip.Height = strip.PreferredHeight;
            toolbar.Height = toolbar.PreferredHeight;
            bookmarksBar.Height = bookmarksBar.PreferredHeight;
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            ApplyHeights();
        }

        async Task Start()
        {
            try
            {
                var opts = new CoreWebView2EnvironmentOptions { AreBrowserExtensionsEnabled = true };
                env = await CoreWebView2Environment.CreateAsync(null, Store.WebDataDir, opts);
            }
            catch (WebView2RuntimeNotFoundException)
            {
                MessageBox.Show(this, "Microsoft Edge WebView2 Runtime is missing.\nInstall it from microsoft.com and start GBrowser again.",
                    "GBrowser", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
                return;
            }
            // filters load in the background since the window was created (about a second); pages wait for them
            await FilterEngine.Preload();
            var st = Store.Settings;
            var args = Environment.GetCommandLineArgs().Skip(1).Where(a => !a.StartsWith("--")).ToList();
            if (st.RestoreTabs && st.Session != null && st.Session.Count > 0)
            {
                foreach (var u in st.Session) Tabs.Add(new BrowserTab(u, TitleFor(u)));
                int sel = Math.Max(0, Math.Min(st.SessionIndex, Tabs.Count - 1));
                if (args.Count == 0) Select(sel);
            }
            foreach (var a in args) NewTab(File.Exists(a) ? new Uri(Path.GetFullPath(a)).AbsoluteUri : Search.ToUrl(a));
            if (Tabs.Count == 0) NewTab(null);
            strip.RefreshTabs();
            // load extensions once at start so their background scripts run (profile is shared by all tabs)
            await LoadExtensionsOnce();
        }

        static string TitleFor(string url)
        {
            if (Pages.IsInternal(url)) return BrowserTab.PageTitle(url);
            return Store.History.FirstOrDefault(h => h.Url == url)?.Title ?? url;
        }

        // ---------- tabs ----------

        public BrowserTab NewTab(string url, bool select = true, int index = -1)
        {
            var t = new BrowserTab(url ?? Pages.Url("newtab"));
            if (index < 0 || index > Tabs.Count) index = Current == null ? Tabs.Count : strip.Selected + 1;
            Tabs.Insert(index, t);
            if (strip.Selected >= index) strip.Selected++;
            if (select) Select(index);
            else strip.RefreshTabs();
            if (url == null && select) BeginInvoke((Action)(() => { toolbar.Address.Focus(); }));
            return t;
        }

        void Select(int i)
        {
            if (i < 0 || i >= Tabs.Count) return;
            var old = Current;
            var t = Tabs[i];
            strip.Selected = i;
            if (old != null && old != t) old.OnHidden();
            if (!t.Created)
            {
                bool fresh = t.View == null;
                var task = t.EnsureCreated(env, content, SetupCore);
                if (fresh) task.ContinueWith(_ => BeginInvoke((Action)(() => { if (Current == t) t.OnShown(); })), TaskScheduler.FromCurrentSynchronizationContext());
            }
            else t.OnShown();
            UpdateUi();
            strip.RefreshTabs();
        }

        void CloseTab(int i)
        {
            if (i < 0 || i >= Tabs.Count) return;
            var t = Tabs[i];
            if (!string.IsNullOrEmpty(t.Url) && Pages.PageName(t.Url) != "newtab") closedTabs.Push(t.Url);
            bool wasSel = i == strip.Selected;
            Tabs.RemoveAt(i);
            t.Dispose();
            if (Tabs.Count == 0) { Close(); return; }
            if (wasSel) { strip.Selected = -1; Select(Math.Min(i, Tabs.Count - 1)); }
            else { if (strip.Selected > i) strip.Selected--; strip.RefreshTabs(); }
        }

        void SetupCore(BrowserTab t)
        {
            var core = t.View.CoreWebView2;
            var s = core.Settings;
            s.AreDevToolsEnabled = true;
            s.IsStatusBarEnabled = true;
            s.AreDefaultContextMenusEnabled = true;
            s.IsZoomControlEnabled = true;
            s.IsPasswordAutosaveEnabled = true;
            s.IsGeneralAutofillEnabled = true;
            s.AreBrowserAcceleratorKeysEnabled = true;
            core.SetVirtualHostNameToFolderMapping(Pages.Host, Pages.Folder, CoreWebView2HostResourceAccessKind.Deny);
            t.Blocking = new Blocking(t, env);
            t.Blocking.Changed += () => uiDirty = true;
            t.Blocking.Attach(core);
#if AUTOMATION
            // test builds save downloads inside the test data folder, never in the user's Downloads
            core.Profile.DefaultDownloadFolderPath = Path.Combine(Store.DataDir, "Downloads");
#endif

            core.SourceChanged += (o, e) => { t.Url = core.Source; if (t == Current) UpdateUi(); };
            core.DocumentTitleChanged += (o, e) =>
            {
                t.Title = string.IsNullOrEmpty(core.DocumentTitle) ? Pages.ToDisplay(core.Source) : core.DocumentTitle;
                if (Pages.IsInternal(core.Source)) t.Title = BrowserTab.PageTitle(core.Source);
                Store.UpdateHistoryTitle(core.Source, core.DocumentTitle);
                strip.RefreshTabs();
                if (t == Current) UpdateUi();
            };
            core.NavigationStarting += (o, e) => { t.Loading = true; strip.RefreshTabs(); if (t == Current) UpdateUi(); };
            core.NavigationCompleted += (o, e) =>
            {
                t.Loading = false;
                if (e.IsSuccess) Store.AddHistory(core.Source, core.DocumentTitle);
                strip.RefreshTabs();
                if (t == Current) UpdateUi();
            };
            core.HistoryChanged += (o, e) => { if (t == Current) UpdateUi(); };
            core.FaviconChanged += (o, e) => t.UpdateFavicon(() => strip.RefreshTabs());
            core.IsDocumentPlayingAudioChanged += (o, e) => { t.PlayingAudio = core.IsDocumentPlayingAudio; strip.RefreshTabs(); };
            core.WindowCloseRequested += (o, e) => { int i = Tabs.IndexOf(t); if (i >= 0) CloseTab(i); };
            core.ContainsFullScreenElementChanged += (o, e) => SetFullscreen(core.ContainsFullScreenElement);
            core.NewWindowRequested += async (o, e) =>
            {
                if (t.Blocking != null && t.Blocking.BlockPopup(e.Uri, e.IsUserInitiated))
                {
                    e.Handled = true;   // no window: window.open returns null
                    return;
                }
                var deferral = e.GetDeferral();
                try
                {
                    var nt = new BrowserTab(e.Uri) { OpenedByPage = true };
                    int idx = Tabs.IndexOf(t) + 1;
                    Tabs.Insert(idx, nt);
                    if (strip.Selected >= idx) strip.Selected++;
                    await nt.EnsureCreated(env, content, SetupCore);
                    e.NewWindow = nt.View.CoreWebView2;
                    e.Handled = true;
                    Select(Tabs.IndexOf(nt));
                }
                catch { e.Handled = false; }
                finally { deferral.Complete(); }
            };
            core.WebMessageReceived += (o, e) => OnPageMessage(t, e);
            core.DownloadStarting += (o, e) => Downloads.Track(e.DownloadOperation);   // the default download bubble stays
            if (!t.OpenedByPage && !string.IsNullOrEmpty(t.Url) && core.Source != t.Url && (core.Source == "about:blank" || string.IsNullOrEmpty(core.Source)))
                t.Navigate(t.Url);
        }

        async Task SleepHiddenTabs()
        {
            int min = Store.Settings.SleepMinutes;
            if (min <= 0) return;
            foreach (var t in Tabs.ToList())
                if (t != Current && t.Created && (DateTime.UtcNow - t.HiddenSince).TotalMinutes >= min)
                    t.Discard();
            await Task.CompletedTask;
            strip.RefreshTabs();
        }

        void UpdateUi()
        {
            var t = Current;
            if (t == null) return;
            var core = t.Created ? t.View.CoreWebView2 : null;
            string url = core?.Source ?? t.Url ?? "";
            toolbar.CanBack = core?.CanGoBack ?? false;
            toolbar.CanForward = core?.CanGoForward ?? false;
            toolbar.Loading = t.Loading;
            toolbar.Secure = url.StartsWith("https://") || Pages.IsInternal(url);
            toolbar.Bookmarked = Store.IsBookmarked(url);
            toolbar.BlockedCount = t.Blocked;
            string pageHost = Blocking.HostOf(url);
            toolbar.ShieldOn = pageHost == null ? Store.Settings.AdblockEnabled : Blocking.OnFor(pageHost);
            toolbar.SetUrl(Pages.ToDisplay(url));
            var storeId = Extensions.StorePageId(url);
            bool chip = storeId != null;
            toolbar.StoreChipText = chip && Extensions.Index.ContainsKey(storeId) ? "Installed" : "Add to GBrowser";
            if (chip != toolbar.StoreChip) { toolbar.StoreChip = chip; toolbar.LayoutNow(); }
            toolbar.Invalidate();
            Text = (string.IsNullOrEmpty(t.Title) ? "GBrowser" : t.Title + " - GBrowser");
        }

        void SetFullscreen(bool on)
        {
            if (on == fullscreen) return;
            fullscreen = on;
            SuspendLayout();
            if (on)
            {
                preFullState = WindowState; preFullBorder = FormBorderStyle;
                strip.Visible = toolbar.Visible = bookmarksBar.Visible = false;
                FormBorderStyle = FormBorderStyle.None;
                if (WindowState == FormWindowState.Maximized) WindowState = FormWindowState.Normal;
                WindowState = FormWindowState.Maximized;
            }
            else
            {
                strip.Visible = toolbar.Visible = true;
                bookmarksBar.Visible = Store.Settings.ShowBookmarksBar;
                FormBorderStyle = preFullBorder;
                WindowState = FormWindowState.Normal;
                WindowState = preFullState;
            }
            ResumeLayout();
        }

        void ToggleBookmark()
        {
            var t = Current;
            if (t == null) return;
            string url = t.Created ? t.View.CoreWebView2.Source : t.Url;
            if (string.IsNullOrEmpty(url) || Pages.PageName(url) == "newtab") return;
            Store.ToggleBookmark(url, t.Title);
            UpdateUi();
            bookmarksBar.Invalidate();
        }

        void OpenPage(string name)
        {
            string url = Pages.Url(name);
            int existing = Tabs.FindIndex(x => x.Url != null && Pages.PageName(x.Url) == name);
            if (existing >= 0) { Select(existing); if (Tabs[existing].Created) Tabs[existing].View.CoreWebView2.Reload(); return; }
            if (Current != null && Pages.PageName(Current.Url) == "newtab") Current.Navigate(url);
            else NewTab(url);
        }

        // ---------- menus ----------

        ToolStripMenuItem Item(string text, string icon, string keys, Action click)
        {
            var it = new ToolStripMenuItem(text, icon == null ? null : Icons.Bitmap(icon, (int)(18 * DeviceDpi / 96f), Theme.OnSurfaceVariant));
            if (keys != null) it.ShortcutKeyDisplayString = keys;
            it.Click += (s, e) => click();
            it.Padding = new Padding(0, 3, 0, 3);
            return it;
        }

        ContextMenuStrip lastMenu;
        ExtensionPopup lastPopup;

        ContextMenuStrip NewMenu()
        {
            var m = new ContextMenuStrip { Renderer = new MenuRenderer(), Font = new Font("Segoe UI", 9.5f), ShowImageMargin = true };
#if AUTOMATION
            m.AutoClose = false;   // test builds: menus stay open until the test closes them
#endif
            lastMenu = m;
            return m;
        }

        void ShowMenu()
        {
            var m = NewMenu();
            m.Items.Add(Item("New tab", IconData.Tab, "Ctrl+T", () => NewTab(null)));
            m.Items.Add(Item("Reopen closed tab", IconData.Restore, "Ctrl+Shift+T", ReopenClosed));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Item("Bookmarks", IconData.Bookmarks, "Ctrl+Shift+O", () => OpenPage("bookmarks")));
            m.Items.Add(Item(Store.Settings.ShowBookmarksBar ? "Hide bookmarks bar" : "Show bookmarks bar", IconData.Star, "Ctrl+Shift+B", ToggleBookmarksBar));
            m.Items.Add(Item("History", IconData.History, "Ctrl+H", () => OpenPage("history")));
            m.Items.Add(Item("Downloads", IconData.Download, "Ctrl+J", OpenDownloads));
            m.Items.Add(Item("Extensions", IconData.Extension, null, () => OpenPage("extensions")));
            string keepSite = Blocking.SiteOf(Current?.Created == true ? Current.View.CoreWebView2.Source : Current?.Url);
            if (keepSite != null)
            {
                bool kept = Store.Settings.KeepActiveSites.Contains(keepSite);
                var keep = new ToolStripMenuItem("Keep " + keepSite + " active") { Checked = kept, Padding = new Padding(0, 3, 0, 3) };
                keep.Click += (s, e) =>
                {
                    Store.Settings.KeepActiveSites.Remove(keepSite);
                    if (!kept) Store.Settings.KeepActiveSites.Add(keepSite);
                    Store.SaveSettings();
                };
                m.Items.Add(keep);
            }
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Item("Zoom in", IconData.ZoomIn, "Ctrl++", () => Zoom(1.1)));
            m.Items.Add(Item("Zoom out", IconData.ZoomOut, "Ctrl+-", () => Zoom(1 / 1.1)));
            m.Items.Add(Item("Find on page", IconData.Find, "Ctrl+F", () => SendKeysToPage("^f")));
            m.Items.Add(Item("Print", IconData.Print, "Ctrl+P", () => { if (Current?.Created == true) Current.View.CoreWebView2.ShowPrintUI(); }));
            m.Items.Add(Item("Full screen", IconData.Fullscreen, "F11", () => SetFullscreen(!fullscreen)));
            m.Items.Add(Item("Developer tools", IconData.Code, "F12", () => { if (Current?.Created == true) Current.View.CoreWebView2.OpenDevToolsWindow(); }));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Item("Settings", IconData.Settings, null, () => OpenPage("settings")));
            m.Items.Add(Item("About GBrowser", IconData.Info, null, () => OpenPage("about")));
            m.Items.Add(Item("Exit", IconData.Close, null, Close));
            var r = toolbar.ButtonRect("menu");
            m.Show(toolbar, new Point(r.Right - m.PreferredSize.Width, r.Bottom + 2));
        }

        void SendKeysToPage(string keys)
        {
            if (Current?.Created != true) return;
            Current.View.Focus();
            SendKeys.Send(keys);
        }

        void Zoom(double f)
        {
            if (Current?.Created != true) return;
            Current.View.ZoomFactor = Math.Max(0.25, Math.Min(5, Current.View.ZoomFactor * f));
        }

        void OpenDownloads() => OpenPage("downloads");

        void ToggleBookmarksBar()
        {
            Store.Settings.ShowBookmarksBar = !Store.Settings.ShowBookmarksBar;
            Store.SaveSettings();
            bookmarksBar.Visible = Store.Settings.ShowBookmarksBar && !fullscreen;
        }

        void ReopenClosed()
        {
            if (closedTabs.Count > 0) NewTab(closedTabs.Pop());
        }

        CoreWebView2Profile Profile => Tabs.FirstOrDefault(t => t.Created)?.View.CoreWebView2.Profile;
        CoreWebView2 AnyCore => Tabs.FirstOrDefault(t => t.Created)?.View.CoreWebView2;

        bool extensionsLoaded;
        async Task LoadExtensionsOnce()
        {
            if (extensionsLoaded) return;
            for (int i = 0; i < 100 && Profile == null; i++) await Task.Delay(100);
            if (Profile == null) return;
            extensionsLoaded = true;
            // WebView2 keeps added extensions in the profile; nothing else to do here. Drop index entries whose folder is gone.
            try
            {
                var installed = await Profile.GetBrowserExtensionsAsync();
                foreach (var kv in Extensions.Index.ToList())
                    if (!installed.Any(e => e.Id == kv.Key) && !Directory.Exists(kv.Value)) Extensions.Index.Remove(kv.Key);
            }
            catch { }
        }

        async void ShowExtensionsMenu()
        {
            var m = NewMenu();
            var profile = Profile;
            if (profile != null)
            {
                IReadOnlyList<CoreWebView2BrowserExtension> list = null;
                try { list = await profile.GetBrowserExtensionsAsync(); } catch { }
                foreach (var ext in list ?? new List<CoreWebView2BrowserExtension>())
                {
                    if (!ext.IsEnabled || !Extensions.Index.ContainsKey(ext.Id)) continue;
                    var man = Extensions.Manifest(ext.Id);
                    string popup = Extensions.PopupPage(man), options = Extensions.OptionsPage(man);
                    var it = new ToolStripMenuItem(ext.Name, Extensions.Icon(ext.Id) ?? Icons.Bitmap(IconData.Extension, 16, Theme.OnSurfaceVariant));
                    it.Padding = new Padding(0, 3, 0, 3);
                    string id = ext.Id;
                    if (popup != null) it.Click += (s, e) => ShowExtensionPopup(id, popup);
                    else if (options != null) it.Click += (s, e) => NewTab("chrome-extension://" + id + "/" + options);
                    else it.Enabled = false;
                    m.Items.Add(it);
                }
                if (m.Items.Count == 0) m.Items.Add(new ToolStripMenuItem("No extensions") { Enabled = false });
            }
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Item("Chrome Web Store", IconData.OpenNew, null, () => NewTab("https://chromewebstore.google.com/category/extensions")));
            m.Items.Add(Item("Manage extensions", IconData.Settings, null, () => OpenPage("extensions")));
            var r = toolbar.ButtonRect("ext");
            m.Show(toolbar, new Point(r.Right - m.PreferredSize.Width, r.Bottom + 2));
        }

        void ShowShieldMenu()
        {
            var st = Store.Settings;
            var t = Current;
            string url = t?.Created == true ? t.View.CoreWebView2.Source : t?.Url;
            string site = Blocking.SiteOf(url);
            var m = NewMenu();
            string head = !st.AdblockEnabled ? "Ad blocking off"
                : site != null && !Blocking.OnFor(Blocking.HostOf(url)) ? "Off on this site"
                : (t?.Blocked ?? 0) + " blocked on this page";
            m.Items.Add(new ToolStripLabel(head, Icons.Bitmap(IconData.Shield, (int)(18 * DeviceDpi / 96f), Theme.Primary))
                { Font = new Font(m.Font, FontStyle.Bold), ForeColor = Theme.OnSurface, Padding = new Padding(0, 4, 0, 4) });
            m.Items.Add(new ToolStripSeparator());
            void Toggle(string text, bool on, Action<bool> set, bool enabled = true)
            {
                var it = new ToolStripMenuItem(text) { Checked = on, Enabled = enabled, Padding = new Padding(0, 3, 0, 3) };
                it.Click += (s, e) => { set(!on); Store.SaveSettings(); ReloadAfterSettingChange(); };
                m.Items.Add(it);
            }
            if (site != null)
                Toggle("Block on " + site, !st.AllowedSites.Contains(site), on =>
                {
                    st.AllowedSites.Remove(site);
                    if (!on) st.AllowedSites.Add(site);
                }, st.AdblockEnabled);
            Toggle("Ad blocking", st.AdblockEnabled, v => st.AdblockEnabled = v);
            Toggle("Annoyances", st.BlockAnnoyances, v => st.BlockAnnoyances = v, st.AdblockEnabled);
            Toggle("Trackers", st.BlockTrackers, v => st.BlockTrackers = v, st.AdblockEnabled);
            Toggle("Hide ad leftovers", st.HideAdLeftovers, v => st.HideAdLeftovers = v, st.AdblockEnabled);
            Toggle("Stealth mode", st.Stealth, v => st.Stealth = v, st.AdblockEnabled);
            Toggle("Block pages of ad sites", st.BlockAdSitePages, v => st.BlockAdSitePages = v, st.AdblockEnabled);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripLabel("Total blocked: " + st.TotalBlocked.ToString("N0")) { ForeColor = Theme.OnSurfaceVariant, Padding = new Padding(0, 3, 0, 3) });
            m.Items.Add(Item("Ad blocking settings", IconData.Settings, null, () => OpenPage("settings")));
            var r = toolbar.ButtonRect("shield");
            m.Show(toolbar, new Point(r.Right - m.PreferredSize.Width, r.Bottom + 2));
        }

        /// <summary>Blocking settings apply to new requests; reload the current page so it shows the change.</summary>
        void ReloadAfterSettingChange()
        {
            UpdateUi();
            if (Current?.Created == true && !Pages.IsInternal(Current.View.CoreWebView2.Source)) Current.View.CoreWebView2.Reload();
        }

        void ShowExtensionPopup(string id, string page)
        {
            var r = toolbar.ButtonRect("ext");
            var p = toolbar.PointToScreen(new Point(r.Right, r.Bottom + 2));
            lastPopup = new ExtensionPopup(env, "chrome-extension://" + id + "/" + page, p, url => NewTab(url));
            lastPopup.Show(this);
        }

        async void InstallFromStorePage()
        {
            var url = Current?.Created == true ? Current.View.CoreWebView2.Source : null;
            var id = Extensions.StorePageId(url);
            if (id == null) return;
            if (Extensions.Index.ContainsKey(id)) { OpenPage("extensions"); return; }
            toolbar.StoreChipText = "Installing...";
            toolbar.LayoutNow();
            try
            {
                var core = Current.View.CoreWebView2;
                var ext = await Extensions.InstallFromStore(core.Profile, id, core.Settings.UserAgent, env.BrowserVersionString);
                toolbar.StoreChipText = "Installed";
            }
            catch (Exception ex)
            {
                toolbar.StoreChipText = "Add to GBrowser";
                MessageBox.Show(this, "Install failed: " + ex.Message, "GBrowser", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            toolbar.LayoutNow();
        }

        // ---------- keyboard ----------

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Control | Keys.T: NewTab(null); return true;
                case Keys.Control | Keys.W:
                case Keys.Control | Keys.F4: CloseTab(strip.Selected); return true;
                case Keys.Control | Keys.Shift | Keys.T: ReopenClosed(); return true;
                case Keys.Control | Keys.Tab:
                case Keys.Control | Keys.PageDown: if (Tabs.Count > 1) Select((strip.Selected + 1) % Tabs.Count); return true;
                case Keys.Control | Keys.Shift | Keys.Tab:
                case Keys.Control | Keys.PageUp: if (Tabs.Count > 1) Select((strip.Selected - 1 + Tabs.Count) % Tabs.Count); return true;
                case Keys.Control | Keys.L:
                case Keys.Alt | Keys.D:
                case Keys.F6: toolbar.Address.Focus(); toolbar.Address.SelectAll(); return true;
                case Keys.Control | Keys.D: ToggleBookmark(); return true;
                case Keys.Control | Keys.H: OpenPage("history"); return true;
                case Keys.Control | Keys.Shift | Keys.O: OpenPage("bookmarks"); return true;
                case Keys.Control | Keys.Shift | Keys.B: ToggleBookmarksBar(); return true;
                case Keys.Control | Keys.J: OpenDownloads(); return true;
                case Keys.Alt | Keys.Left: toolbar_Back(); return true;
                case Keys.Alt | Keys.Right: toolbar_Forward(); return true;
                case Keys.Alt | Keys.Home: Current?.Navigate(Search.ToUrl(Store.Settings.HomePage) ?? Pages.Url("newtab")); return true;
                case Keys.F5:
                case Keys.Control | Keys.R: if (Current?.Created == true) Current.View.CoreWebView2.Reload(); return true;
                case Keys.F11: SetFullscreen(!fullscreen); return true;
                case Keys.Escape:
                    if (fullscreen && Current?.Created == true && Current.View.CoreWebView2.ContainsFullScreenElement) break;
                    if (Current?.Loading == true && Current.Created) { Current.View.CoreWebView2.Stop(); return true; }
                    break;
            }
            if ((keyData & Keys.Control) == Keys.Control && (keyData & Keys.Shift) == 0 && (keyData & Keys.Alt) == 0)
            {
                var k = keyData & Keys.KeyCode;
                if (k >= Keys.D1 && k <= Keys.D8) { int i = k - Keys.D1; if (i < Tabs.Count) Select(i); return true; }
                if (k == Keys.D9) { Select(Tabs.Count - 1); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void toolbar_Back() { if (Current?.Created == true && Current.View.CoreWebView2.CanGoBack) Current.View.CoreWebView2.GoBack(); }
        void toolbar_Forward() { if (Current?.Created == true && Current.View.CoreWebView2.CanGoForward) Current.View.CoreWebView2.GoForward(); }

        // ---------- internal pages bridge ----------

        async void OnPageMessage(BrowserTab tab, CoreWebView2WebMessageReceivedEventArgs e)
        {
            // only GBrowser's own pages (top document) may use the bridge
            var core = tab.View?.CoreWebView2;
            if (core == null || !Pages.IsInternal(e.Source) || !Pages.IsInternal(core.Source)) return;
            Dictionary<string, object> msg;
            try { msg = Store.FromJson<Dictionary<string, object>>(e.TryGetWebMessageAsString()); } catch { return; }
            object rid = msg.TryGetValue("rid", out var r) ? r : null;
            string cmd = Str(msg, "cmd");
            object result = null; string error = null;
            try { result = await HandleCommand(tab, cmd, msg); }
            catch (Exception ex) { error = ex.Message; }
            try { core.PostWebMessageAsJson(Store.ToJson(new Dictionary<string, object> { { "rid", rid }, { "result", result }, { "error", error } })); } catch { }
        }

        static string Str(Dictionary<string, object> m, string k) => m.TryGetValue(k, out var v) && v != null ? v.ToString() : null;

        async Task<object> HandleCommand(BrowserTab tab, string cmd, Dictionary<string, object> m)
        {
            var st = Store.Settings;
            switch (cmd)
            {
                case "go":
                {
                    var url = Search.ToUrl(Str(m, "text"));
                    if (url != null) tab.Navigate(url);
                    return null;
                }
                case "open":
                {
                    var url = Str(m, "url");
                    if (string.IsNullOrEmpty(url)) return null;
                    if (m.TryGetValue("newTab", out var nt) && nt is bool b && b) NewTab(url, false);
                    else tab.Navigate(url);
                    return null;
                }
                case "newtab.data":
                {
                    var tiles = new List<object>();
                    foreach (var b in Store.Bookmarks.Take(8)) tiles.Add(new Dictionary<string, object> { { "url", b.Url }, { "title", b.Title } });
                    if (tiles.Count < 8)
                    {
                        var seen = new HashSet<string>(tiles.Select(x => (string)((Dictionary<string, object>)x)["url"]));
                        var top = Store.History.Where(h => h.Url.StartsWith("http"))
                            .GroupBy(h => SafeHost(h.Url)).Where(g => g.Key != null)
                            .OrderByDescending(g => g.Count()).Take(16);
                        foreach (var g in top)
                        {
                            if (tiles.Count >= 8) break;
                            var h = g.First();
                            var home = new Uri(h.Url).GetLeftPart(UriPartial.Authority) + "/";
                            if (seen.Add(home)) tiles.Add(new Dictionary<string, object> { { "url", home }, { "title", g.Key.StartsWith("www.") ? g.Key.Substring(4) : g.Key } });
                        }
                    }
                    return new Dictionary<string, object> { { "tiles", tiles }, { "engine", Search.Engines.TryGetValue(st.SearchEngine ?? "", out var en) ? en[0] : "Google" } };
                }
                case "bookmarks.list": return Store.Bookmarks;
                case "bookmarks.remove":
                    Store.Bookmarks.RemoveAll(b => b.Url == Str(m, "url")); Store.SaveBookmarks(); bookmarksBar.Invalidate(); UpdateUi(); return null;
                case "bookmarks.rename":
                {
                    var b = Store.Bookmarks.FirstOrDefault(x => x.Url == Str(m, "url"));
                    if (b != null) { b.Title = Str(m, "title") ?? b.Title; var nu = Str(m, "newUrl"); if (!string.IsNullOrWhiteSpace(nu)) b.Url = Search.ToUrl(nu) ?? b.Url; Store.SaveBookmarks(); bookmarksBar.Invalidate(); }
                    return null;
                }
                case "history.list":
                {
                    string q = (Str(m, "q") ?? "").Trim().ToLowerInvariant();
                    int limit = m.TryGetValue("limit", out var l) && l is int li ? li : 500;
                    var items = Store.History.Where(h => q.Length == 0 || (h.Title ?? "").ToLowerInvariant().Contains(q) || h.Url.ToLowerInvariant().Contains(q)).Take(limit);
                    return items.ToList();
                }
                case "history.remove":
                {
                    long time = Convert.ToInt64(m.TryGetValue("time", out var tv) ? tv : 0);
                    Store.History.RemoveAll(h => h.Url == Str(m, "url") && h.Time == time); Store.SaveHistory(); return null;
                }
                case "history.clear":
                    Store.History.Clear(); Store.SaveHistory();
                    if (Profile != null) await Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.BrowsingHistory);
                    return null;
                case "settings.get":
                    return new Dictionary<string, object>
                    {
                        { "settings", st },
                        { "engines", Search.Engines.Select(kv => new Dictionary<string, object> { { "id", kv.Key }, { "name", kv.Value[0] } }).ToList() },
                    };
                case "settings.set":
                {
                    string key = Str(m, "key"); m.TryGetValue("value", out var v);
                    switch (key)
                    {
                        case "HomePage": st.HomePage = string.IsNullOrWhiteSpace(v as string) ? "gbrowser://newtab" : ((string)v).Trim(); break;
                        case "SearchEngine": if (v is string se && Search.Engines.ContainsKey(se)) st.SearchEngine = se; break;
                        case "RestoreTabs": st.RestoreTabs = v is bool rb && rb; break;
                        case "SleepMinutes": st.SleepMinutes = Math.Max(0, Math.Min(240, Convert.ToInt32(v))); break;
                        case "LowMemoryHiddenTabs": st.LowMemoryHiddenTabs = v is bool lb && lb; break;
                        case "ShowBookmarksBar": st.ShowBookmarksBar = v is bool sb && sb; bookmarksBar.Visible = st.ShowBookmarksBar && !fullscreen; break;
                        case "AdblockEnabled": st.AdblockEnabled = v is bool a1 && a1; break;
                        case "BlockAnnoyances": st.BlockAnnoyances = v is bool a2 && a2; break;
                        case "BlockTrackers": st.BlockTrackers = v is bool a3 && a3; break;
                        case "HideAdLeftovers": st.HideAdLeftovers = v is bool a4 && a4; break;
                        case "Stealth": st.Stealth = v is bool a5 && a5; break;
                        case "BlockAdSitePages": st.BlockAdSitePages = v is bool a6 && a6; break;
                        case "DoNotTrack": st.DoNotTrack = v is bool a7 && a7; break;
                        default: throw new Exception("Unknown setting");
                    }
                    Store.SaveSettings();
                    return null;
                }
                case "clearData":
                {
                    var kinds = (m.TryGetValue("kinds", out var k) ? k as System.Collections.IEnumerable : null) ?? new object[0];
                    CoreWebView2BrowsingDataKinds flags = 0;
                    foreach (var o in kinds)
                        switch (o as string)
                        {
                            case "cookies": flags |= CoreWebView2BrowsingDataKinds.AllSite; break;
                            case "cache": flags |= CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.CacheStorage; break;
                            case "history": flags |= CoreWebView2BrowsingDataKinds.BrowsingHistory | CoreWebView2BrowsingDataKinds.DownloadHistory; Store.History.Clear(); Store.SaveHistory(); break;
                            case "passwords": flags |= CoreWebView2BrowsingDataKinds.PasswordAutosave; break;
                            case "autofill": flags |= CoreWebView2BrowsingDataKinds.GeneralAutofill; break;
                        }
                    if (flags != 0 && Profile != null) await Profile.ClearBrowsingDataAsync(flags);
                    return null;
                }
                case "info":
                    return new Dictionary<string, object>
                    {
                        { "version", AppVersion }, { "engine", env?.BrowserVersionString ?? "" },
                        { "dataDir", Store.DataDir }, { "userAgent", tab.View?.CoreWebView2?.Settings.UserAgent ?? "" },
                    };
                case "openDataDir":
                    Process.Start("explorer.exe", "\"" + Store.DataDir + "\""); return null;
                case "memory": return MemoryReport();
                case "downloads.list":
                    return Downloads.All.Select(d => new Dictionary<string, object> {
                        { "id", d.Id }, { "url", d.Url }, { "path", d.Path }, { "name", Path.GetFileName(d.Path ?? "") },
                        { "total", d.Total }, { "received", d.Received }, { "state", d.State }, { "reason", d.Reason ?? "" },
                        { "time", d.Time }, { "exists", !string.IsNullOrEmpty(d.Path) && File.Exists(d.Path) }, { "live", d.Op != null } }).ToList();
                case "downloads.do":
                {
                    var d = Downloads.Find(Convert.ToInt32(m["id"]));
                    if (d == null) return null;
                    switch (Str(m, "action"))
                    {
                        case "open": if (File.Exists(d.Path)) Process.Start(new ProcessStartInfo(d.Path) { UseShellExecute = true }); break;
                        case "folder": if (File.Exists(d.Path)) Process.Start("explorer.exe", "/select,\"" + d.Path + "\""); break;
                        case "cancel": d.Op?.Cancel(); break;
                        case "pause": d.Op?.Pause(); d.State = "paused"; break;
                        case "resume": if (d.Op != null && d.Op.CanResume) { d.Op.Resume(); d.State = "in_progress"; } break;
                        case "retry": NewTab(d.Url, false); break;
                        case "remove": Downloads.All.Remove(d); Downloads.Save(); break;
                    }
                    return null;
                }
                case "downloads.clear":
                    Downloads.All.RemoveAll(d => d.Op == null); Downloads.Save(); return null;
                case "adblock.info":
                {
                    var eng = FilterEngine.Instance;
                    var rows = (eng?.ListInfo ?? "").Split('\n').Select(l => l.Split('\t')).ToList();
                    var lists = rows.Where(p => p.Length >= 3 && p[0] != "built")
                        .Select(p => new Dictionary<string, object> { { "name", p[0] }, { "version", p[1] }, { "rules", p[2] } }).ToList();
                    string built = rows.FirstOrDefault(p => p.Length >= 2 && p[0] == "built")?[1] ?? "";
                    return new Dictionary<string, object>
                    {
                        { "ready", eng != null }, { "error", FilterEngine.LoadError ?? "" }, { "rules", eng?.RuleCount ?? 0 },
                        { "loadMs", FilterEngine.LoadMs }, { "built", built }, { "lists", lists },
                        { "total", st.TotalBlocked }, { "allowed", st.AllowedSites },
                    };
                }
                case "allow.remove": st.AllowedSites.Remove(Str(m, "site")); Store.SaveSettings(); return null;
                case "keep.add":
                {
                    // accepts a site, an address or a full URL; stores the registrable domain (www.bbc.co.uk -> bbc.co.uk)
                    string text = (Str(m, "site") ?? "").Trim();
                    string url = text.Contains("://") ? text : "https://" + text;
                    string site = Blocking.SiteOf(url);
                    if (site == null || !site.Contains(".")) throw new Exception("Not a site");
                    if (!st.KeepActiveSites.Contains(site)) st.KeepActiveSites.Add(site);
                    Store.SaveSettings();
                    return site;
                }
                case "keep.remove": st.KeepActiveSites.Remove(Str(m, "site")); Store.SaveSettings(); return null;
                case "ext.list": return Profile == null ? new List<Dictionary<string, object>>() : await Extensions.List(Profile);
                case "ext.install":
                {
                    var id = Extensions.ParseId(Str(m, "text"));
                    if (id == null) throw new Exception("No extension ID found");
                    var core = tab.View.CoreWebView2;
                    var ext = await Extensions.InstallFromStore(core.Profile, id, core.Settings.UserAgent, env.BrowserVersionString);
                    return ext.Name;
                }
                case "ext.pick":
                {
                    using (var dlg = new FolderBrowserDialog { Description = "Choose the extension folder (with manifest.json)", ShowNewFolderButton = false })
                    {
                        if (dlg.ShowDialog(this) != DialogResult.OK) return null;
                        var ext = await Extensions.LoadUnpacked(tab.View.CoreWebView2.Profile, dlg.SelectedPath);
                        return ext.Name;
                    }
                }
                case "ext.enable":
                {
                    var ext = (await Profile.GetBrowserExtensionsAsync()).FirstOrDefault(x => x.Id == Str(m, "id"));
                    if (ext != null) await ext.EnableAsync(m.TryGetValue("on", out var on) && on is bool ob && ob);
                    return null;
                }
                case "ext.remove": await Extensions.Remove(Profile, Str(m, "id")); return null;
                case "ext.options":
                {
                    var o = Extensions.OptionsPage(Extensions.Manifest(Str(m, "id")));
                    if (o != null) NewTab("chrome-extension://" + Str(m, "id") + "/" + o);
                    return null;
                }
                case "ext.popup":
                {
                    var p = Extensions.PopupPage(Extensions.Manifest(Str(m, "id")));
                    if (p != null) ShowExtensionPopup(Str(m, "id"), p);
                    return null;
                }
                default: throw new Exception("Unknown command");
            }
        }

        static string SafeHost(string url) { try { return new Uri(url).Host; } catch { return null; } }

        /// <summary>Memory of GBrowser and all its WebView2 processes (private bytes, like Task Manager's "Memory").</summary>
        object MemoryReport()
        {
            var rows = new List<Dictionary<string, object>>();
            long total = 0;
            void Add(string kind, int pid)
            {
                try
                {
                    using (var p = Process.GetProcessById(pid))
                    {
                        long mb = p.PrivateMemorySize64;
                        total += mb;
                        rows.Add(new Dictionary<string, object> { { "kind", kind }, { "pid", pid }, { "mb", Math.Round(mb / 1048576.0, 1) } });
                    }
                }
                catch { }
            }
            Add("GBrowser window", Process.GetCurrentProcess().Id);
            if (env != null)
                foreach (var pi in env.GetProcessInfos()) Add(pi.Kind.ToString(), pi.ProcessId);
            int live = Tabs.Count(t => t.Created), sleeping = Tabs.Count(t => t.Sleeping), unloaded = Tabs.Count(t => !t.Created && !t.Sleeping);
            return new Dictionary<string, object>
            {
                { "rows", rows }, { "totalMb", Math.Round(total / 1048576.0, 1) },
                { "tabs", Tabs.Count }, { "live", live }, { "sleeping", sleeping }, { "unloaded", unloaded },
            };
        }

#if AUTOMATION
        // ---------- local test channel (see Automation.cs; not in release builds) ----------

        public async Task<string> TestCommand(string line)
        {
            int sp = line.IndexOf(' ');
            string cmd = sp < 0 ? line : line.Substring(0, sp), arg = sp < 0 ? "" : line.Substring(sp + 1);
            switch (cmd)
            {
                case "key":
                {
                    var m = new Message();
                    bool handled = ProcessCmdKey(ref m, Automation.ParseKeys(arg));
                    return handled ? "ok" : "unhandled";
                }
                case "go": toolbar.Address.Text = arg; toolbar_Go(arg); return "ok";
                case "newtab": NewTab(arg.Length == 0 ? null : Search.ToUrl(arg)); return "ok";
                case "select": Select(int.Parse(arg)); return "ok";
                case "close": CloseTab(int.Parse(arg)); return "ok";
                case "page": OpenPage(arg); return "ok";
                case "back": toolbar_Back(); return "ok";
                case "star": ToggleBookmark(); return "ok";
                case "sleepnow": foreach (var t in Tabs) if (t != Current) { t.HiddenSince = DateTime.MinValue; } await SleepHiddenTabs(); return "ok";
                case "state":
                    return Store.ToJson(new Dictionary<string, object>
                    {
                        { "selected", strip.Selected }, { "address", toolbar.Address.Text }, { "title", Text },
                        { "tabs", Tabs.Select(t => new Dictionary<string, object> {
                            { "title", t.Title }, { "url", t.Created ? t.View.CoreWebView2.Source : t.Url }, { "created", t.Created },
                            { "loading", t.Loading }, { "sleeping", t.Sleeping }, { "blocked", t.Blocked } }).ToList() },
                    });
                case "menu":
                    if (arg == "shield") ShowShieldMenu(); else if (arg == "ext") ShowExtensionsMenu(); else ShowMenu();
                    await Task.Delay(400);
                    return lastMenu == null ? "none" : lastMenu.Handle.ToString();
                case "menuitems":
                    return lastMenu == null ? "none" : string.Join(" | ", lastMenu.Items.Cast<ToolStripItem>().Select(i =>
                        i is ToolStripSeparator ? "---" : i.Text + (i is ToolStripMenuItem mi && mi.Checked ? " [x]" : "") + (i.Enabled ? "" : " (off)")));
                case "menuclick":
                {
                    var it = lastMenu?.Items.Cast<ToolStripItem>().FirstOrDefault(i => i.Text == arg);
                    if (it == null) return "no item";
                    lastMenu.Close();
                    it.PerformClick();
                    return "ok";
                }
                case "menuclose": lastMenu?.Close(); return "ok";
                case "extinstall":
                {
                    var core = Current.View.CoreWebView2;
                    var ext = await Extensions.InstallFromStore(core.Profile, arg, core.Settings.UserAgent, env.BrowserVersionString);
                    return ext.Id + " " + ext.Name;
                }
                case "extpopup":
                {
                    var pp = Extensions.PopupPage(Extensions.Manifest(arg));
                    if (pp == null) return "no popup";
                    ShowExtensionPopup(arg, pp);
                    await Task.Delay(2500);
                    return lastPopup.Handle.ToString() + " " + lastPopup.Width + "x" + lastPopup.Height;
                }
                case "popupclose": lastPopup?.Close(); return "ok";
                case "set":
                {
                    int sp2 = arg.IndexOf(' ');
                    var v = Store.FromJson<object>(arg.Substring(sp2 + 1));
                    await HandleCommand(Current, "settings.set", new Dictionary<string, object> { { "key", arg.Substring(0, sp2) }, { "value", v } });
                    UpdateUi();
                    return "ok";
                }
                case "cdp":
                {
                    int sp2 = arg.IndexOf(' ');
                    return await Current.View.CoreWebView2.CallDevToolsProtocolMethodAsync(arg.Substring(0, sp2), arg.Substring(sp2 + 1));
                }
                case "testlog": return string.Join(" | ", Blocking.TestLog);
                case "fullscreen": return fullscreen + " " + WindowState + " strip=" + strip.Visible;
                case "eval":
                    if (Current?.Created != true) return "no tab";
                    return await Current.View.CoreWebView2.ExecuteScriptAsync(arg);
                case "memory": return Store.ToJson(MemoryReport());
                default: return "unknown";
            }
        }

        void toolbar_Go(string text)
        {
            var url = Search.ToUrl(text);
            if (url != null) Current?.Navigate(url);
        }
#endif

        // ---------- closing ----------

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            var st = Store.Settings;
            st.Session = Tabs.Select(t => t.Created ? t.View.CoreWebView2.Source : t.Url).Where(u => !string.IsNullOrEmpty(u)).ToList();
            st.SessionIndex = Math.Max(0, strip.Selected);
            st.WindowMax = WindowState == FormWindowState.Maximized || fullscreen;
            var b = WindowState == FormWindowState.Normal && !fullscreen ? Bounds : RestoreBounds;
            st.WindowX = b.X; st.WindowY = b.Y; st.WindowW = b.Width; st.WindowH = b.Height;
            Store.SaveSettings();
            Store.SaveHistory();
            base.OnFormClosing(e);
        }
    }
}
