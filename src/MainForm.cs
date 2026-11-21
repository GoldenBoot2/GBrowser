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
        readonly StatusBubble bubble = new StatusBubble();
        readonly FindBar findBar = new FindBar();
        string lastFindText = "";
        readonly Panel outer = new Panel { Dock = DockStyle.Fill };   // page area + docked DevTools
        Omnibox omni;
        PageMenu.Host pageMenuHost;
        string lastContextInfo;   // test builds: what the last page right-click offered
        DevToolsDock devDock;
        string pendingDevToolsAction;   // what the DevTools window should do once it is open (console / inspect)

        /// <summary>F12 / Ctrl+Shift+I and the menu item: open or close this tab's DevTools (Chrome).</summary>
        void ToggleDevTools()
        {
            if (devDock.Docked(Current)) { devDock.CloseCurrent(); return; }
            if (Current?.Created == true) Current.View.CoreWebView2.OpenDevToolsWindow();
        }

        /// <summary>Ctrl+Shift+J (console) / Ctrl+Shift+C (inspect an element): DevTools opens (if needed) and shows it.</summary>
        void OpenDevTools(string action)
        {
            var t = Current;
            if (t?.Created != true) return;
            if (devDock.Docked(t)) { _ = DevToolsGuard.Apply(t.View.CoreWebView2, t.View.CoreWebView2.Source, action, 3000); return; }
            pendingDevToolsAction = action;
            t.View.CoreWebView2.OpenDevToolsWindow();
        }
        readonly Timer sleepTimer = new Timer { Interval = 30000 };
        readonly Stack<string> closedTabs = new Stack<string>();
        CoreWebView2Environment env;
        bool fullscreen; FormWindowState preFullState; FormBorderStyle preFullBorder;

        List<BrowserTab> Tabs => strip.Tabs;
        BrowserTab Current => strip.Selected >= 0 && strip.Selected < Tabs.Count ? Tabs[strip.Selected] : null;
        static readonly string AppVersion = typeof(MainForm).Assembly.GetName().Version.ToString(3);

        readonly Timer uiTimer = new Timer { Interval = 300 };
        readonly Timer badgeTimer = new Timer { Interval = 1500 };   // extension badges / titles (ExtensionBadges)
        WindowFrame frame;
        bool uiDirty;
        long savedTotal;

        readonly List<string> startUrls;     // addresses this window opens with (new window, a second start)
        readonly WindowSession restoreWs;    // a window of the last session to bring back
        readonly bool isFirst;               // the window GBrowser starts with (restores the session)

        public MainForm(List<string> urls, WindowSession ws)
        {
            var prev = App.Active;
            isFirst = App.Windows.Count == 0;
            App.Windows.Add(this);
            startUrls = urls ?? new List<string>();
            var st0 = Store.Settings;
            if (isFirst && ws == null)
                ws = st0.Windows != null && st0.Windows.Count > 0 ? st0.Windows[0] : new WindowSession
                {
                    Tabs = st0.Session ?? new List<string>(), Index = st0.SessionIndex, Pinned = st0.SessionPinned,
                    X = st0.WindowX, Y = st0.WindowY, W = st0.WindowW, H = st0.WindowH, Max = st0.WindowMax
                };
            restoreWs = ws;
            Activated += (s, e) => App.Activated(this);
            if (!bookmarksHooked) { bookmarksHooked = true; Bm.Changed += OnBookmarksChanged; }
            FilterEngine.Preload();
            Theme.Update();
            Text = "GBrowser";
            BackColor = Theme.Frame;
            AutoScaleMode = AutoScaleMode.Dpi;
            KeyPreview = true;
            MinimumSize = new Size(480, 360);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            var st = Store.Settings;
            StartPosition = FormStartPosition.Manual;
            var wa = Screen.PrimaryScreen.WorkingArea;
            Rectangle bounds;
            bool max = false;
            if (ws != null)
            {
                bounds = new Rectangle(ws.X, ws.Y, Math.Max(600, ws.W), Math.Max(400, ws.H));
                max = ws.Max;
                if (ws.X < 0) bounds.X = -1;
            }
            else if (prev != null && !prev.IsDisposed)
            {
                // a new window opens a little below and right of the one it came from, like Chrome
                var pb = prev.WindowState == FormWindowState.Normal ? prev.Bounds : prev.RestoreBounds;
                bounds = new Rectangle(pb.X + 30, pb.Y + 30, pb.Width, pb.Height);
                max = prev.WindowState == FormWindowState.Maximized;
            }
            else bounds = new Rectangle(-1, -1, 1200, 800);
            if (bounds.X < 0 || !Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(bounds)))
                bounds = new Rectangle(wa.X + (wa.Width - Math.Min(1200, wa.Width)) / 2, wa.Y + (wa.Height - Math.Min(800, wa.Height)) / 2,
                    Math.Min(1200, wa.Width), Math.Min(800, wa.Height));
            Bounds = bounds;
            if (max) WindowState = FormWindowState.Maximized;

            outer.Controls.Add(content);
            Controls.Add(outer);
            devDock = new DevToolsDock(this, content, () => Current, () => Tabs);
            devDock.Opened += t =>
            {
                string act = pendingDevToolsAction; pendingDevToolsAction = null;
                if (t.Created) _ = DevToolsGuard.Apply(t.View.CoreWebView2, t.View.CoreWebView2.Source, act);
            };
            content.Controls.Add(bubble);
            content.Resize += (s, e) => bubble.Place();
            content.Controls.Add(findBar);
            content.Resize += (s, e) => { if (findBar.Visible) findBar.Place(); };
            findBar.Search += FindRun;
            findBar.Step += FindStep;
            findBar.Closed += () => CloseFind(true);
            Controls.Add(bookmarksBar);
            Controls.Add(toolbar);
            Controls.Add(strip);
            bookmarksBar.Visible = st.ShowBookmarksBar;
            ApplyHeights();

            strip.TabSelected += i => Select(i);
            strip.TabClosed += i => CloseTab(i);
            strip.NewTabClicked += () => NewTab(null);
            strip.TabSearchClicked += ShowTabSearch;
            strip.TabMenu += ShowTabMenu;
            strip.GroupToggled += ToggleGroup;
            strip.GroupMenu += ShowGroupMenu;
            strip.TabMuteClicked += i => SetMuted(Tabs[i], !Tabs[i].Muted);
            strip.TabDraggedOut += (i, pt) => MoveTabToNewWindow(i, pt, true);
            // links / text / files dropped on the tab row: on a tab = open there, between tabs = a new tab there (Chrome)
            strip.Dropped += (i, intoTab, text) =>
            {
                var url = Search.ToUrl(text);
                if (url == null) return;
                if (intoTab && i >= 0 && i < Tabs.Count) { Select(i); Tabs[i].Navigate(url); }
                else NewTab(url, true, i);
            };
            // the page dragged out of the address bar (web pages and files; GBrowser's own pages have no address to share)
            toolbar.DragPage = () =>
            {
                var t = Current;
                string u = t?.Created == true ? t.View.CoreWebView2.Source : t?.Url;
                if (string.IsNullOrEmpty(u) || !(u.StartsWith("http://") || u.StartsWith("https://") || u.StartsWith("file:///"))) return null;
                return Tuple.Create(u, t.Title);
            };
            toolbar.DroppedText += text => { var url = Search.ToUrl(text); if (url != null && Current != null) { Current.Navigate(url); Current.View?.Focus(); } };
            ResizeEnd += (s, e) => DropDraggedWindow();
            frame = new WindowFrame(this, strip);
            strip.TabMoved += (from, to) =>
            {
                if (Tabs[from].Pinned != Tabs[to].Pinned) return;   // pinned and normal tabs keep their own block
                var t = Tabs[from]; Tabs.RemoveAt(from); Tabs.Insert(to, t);
                string lg = to > 0 ? Tabs[to - 1].Group : null, rg = to + 1 < Tabs.Count ? Tabs[to + 1].Group : null;
                if (lg != null && lg == rg) t.Group = lg;
                else if (t.Group != null && t.Group != lg && t.Group != rg) t.Group = null;
                strip.RefreshTabs();
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
            toolbar.ZoomClicked += () => { if (Current?.Created == true) { Current.View.ZoomFactor = Store.Settings.DefaultZoom; SaveSiteZoom(Current); } };
            toolbar.MenuClicked += ShowMenu;
            // address bar suggestions (history, bookmarks, open tabs, search suggestions)
            omni = new Omnibox(toolbar.Address, toolbar, () => toolbar.RectangleToScreen(toolbar.PillRect), () => Tabs, () => Current);
            omni.Go += (text, newTab) =>
            {
                var url = Search.ToUrl(text);
                if (url == null) return;
                if (newTab || Current == null) NewTab(url);
                else { Current.Navigate(url); Current.View?.Focus(); }
            };
            omni.SwitchTab += tab => { int i = Tabs.IndexOf(tab); if (i >= 0) Select(i); };
            toolbar.Omni = omni;
            pageMenuHost = new PageMenu.Host
            {
                // like Chrome: a link opens in a background tab right after the current one
                NewTab = url => NewTab(url, false, Current == null ? -1 : Tabs.IndexOf(Current) + 1),
                NewPrivateTab = url => NewPrivateTab(url),
                HideElement = p => PickElement(p),
                SearchUrl = text => (Search.Engines.TryGetValue(Store.Settings.SearchEngine ?? "google", out var en) ? en[1] : Search.Engines["google"][1]) + Uri.EscapeDataString(text),
            };
            toolbar.DownloadsClicked += () => ShowDownloadBubble(false);
            toolbar.ExtensionsClicked += ShowExtensionsMenu;
            toolbar.ActionClicked += (id, r) => OpenExtension(id, r);
            toolbar.ActionMenu += (id, r) => ShowExtensionContextMenu(id, r);
            Extensions.Changed += OnExtensionsChanged;
            badgeTimer.Tick += async (s2, e2) => await RefreshBadges();
            badgeTimer.Start();
            FormClosed += (s2, e2) => Extensions.Changed -= OnExtensionsChanged;
            toolbar.StoreChipClicked += InstallFromStorePage;
            toolbar.ShieldClicked += ShowShieldMenu;
            toolbar.SiteInfoClicked += () => { _ = ShowSiteInfo(); };
            uiTimer.Tick += (s, e) =>
            {
                if (!uiDirty) return;
                uiDirty = false;
                strip.Invalidate();
                UpdateUi();
            };
            uiTimer.Start();
            bookmarksBar.Open += (url, newTab) => { if (newTab) NewTab(url, false); else Current?.Navigate(url); };
            bookmarksBar.OpenWindow += url => App.NewWindow(new[] { url });
            bookmarksBar.OpenPrivate += url => NewPrivateTab(url);
            bookmarksBar.Edit += b => EditBookmarkDialog(b, b.Folder ? "Edit folder" : "Edit bookmark");
            bookmarksBar.AddPage += AddPageDialog;
            bookmarksBar.AddFolder += AddFolderDialog;
            bookmarksBar.Manager += () => OpenPage("bookmarks");
            bookmarksBar.ImportNow += () => NewTab(Pages.Url("bookmarks") + "#import");   // opens the Import dialog
            bookmarksBar.HideBar += ToggleBookmarksBar;

            sleepTimer.Tick += async (s, e) =>
            {
                await SleepHiddenTabs();
                if (Store.Settings.TotalBlocked != savedTotal) { savedTotal = Store.Settings.TotalBlocked; Store.SaveSettings(); }
            };
            sleepTimer.Start();
            Load += async (s, e) => await Start();
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
        }

        /// <summary>Restored or opened from another window / a second start: show it in front.</summary>
        public void BringToFront2()
        {
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        /// <summary>This window's part of the session (none when it has only private tabs).</summary>
        internal WindowSession SessionState()
        {
            var kept = Tabs.Where(t => !t.Private && !string.IsNullOrEmpty(t.Created ? t.View.CoreWebView2.Source : t.Url)).ToList();
            var tabs = kept.Select(t => t.Created ? t.View.CoreWebView2.Source : t.Url).ToList();
            if (tabs.Count == 0) return null;
            var groupIds = kept.Select(t => t.Group != null && strip.Groups.ContainsKey(t.Group) ? t.Group : "").ToList();
            var b = WindowState == FormWindowState.Normal && !fullscreen ? Bounds : RestoreBounds;
            return new WindowSession
            {
                Tabs = tabs, Index = Math.Max(0, Math.Min(strip.Selected, tabs.Count - 1)), Pinned = Tabs.TakeWhile(t => t.Pinned).Count(t => !t.Private),
                X = b.X, Y = b.Y, W = b.Width, H = b.Height, Max = WindowState == FormWindowState.Maximized || fullscreen,
                TabGroups = groupIds.Any(x => x.Length > 0) ? groupIds : null,
                Groups = groupIds.Any(x => x.Length > 0) ? strip.Groups.Where(kv => groupIds.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value) : null,
            };
        }

        // ---------- theme ----------

        void OnSystemPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
        {
            // Windows light/dark switch arrives as a "General" preference change
            // Windows light/dark and accent colour changes arrive as General / Color preference changes
            if ((e.Category == Microsoft.Win32.UserPreferenceCategory.General || e.Category == Microsoft.Win32.UserPreferenceCategory.Color) && IsHandleCreated)
                BeginInvoke((Action)(() => ApplyTheme(false)));
        }

        protected override void WndProc(ref Message m)
        {
            if (frame != null && frame.Handle(ref m, BaseWndProc)) return;
            base.WndProc(ref m);
        }

        void BaseWndProc(ref Message m) => base.WndProc(ref m);

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            bool max = WindowState == FormWindowState.Maximized;
            if (strip.WindowMaximized != max) { strip.WindowMaximized = max; strip.Invalidate(); }
        }

        // ---------- crashed / frozen pages ----------

        bool unresponsiveAsked, engineAsked;

        /// <summary>The engine reports a failed process. A page's process that ended: GBrowser's crashed page (Reload),
        /// like Chrome's sad tab. A page that stopped responding: "Wait" or "Exit page" (ends that process). The whole
        /// engine ended: every page is gone, GBrowser restarts and brings the tabs back. Other processes (GPU,
        /// utilities, frames) are restarted by the engine itself.</summary>
        string lastProcessFailed = "";   // test builds: what the engine reported

        async void OnProcessFailed(BrowserTab t, CoreWebView2 core, CoreWebView2ProcessFailedEventArgs e)
        {
            lastProcessFailed += e.ProcessFailedKind + "/" + e.Reason + "/exit " + e.ExitCode + "; ";
            switch (e.ProcessFailedKind)
            {
                case CoreWebView2ProcessFailedKind.RenderProcessExited:
                {
                    string url = t.Url;
                    if (string.IsNullOrEmpty(url) || Pages.PageName(url) == "crashed") return;
                    t.Loading = false;
                    try { core.Navigate(Pages.Url("crashed") + "?u=" + Uri.EscapeDataString(url) + "&r=" + e.Reason); } catch { }
                    strip.RefreshTabs();
                    break;
                }
                case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                {
                    if (unresponsiveAsked) return;
                    unresponsiveAsked = true;
                    try
                    {
                        string host = Blocking.HostOf(t.Url) ?? "This page";
                        int i = Tabs.IndexOf(t);
                        if (i >= 0 && t != Current) Select(i);
                        if (MaterialDialog.Ask(this, "Page unresponsive", null, new[] {
                                Tuple.Create(IconData.Warning, host + " isn't responding. You can wait for it or exit the page.") }, "Exit page", "Wait"))
                            await EndPageProcess(core);
                    }
                    finally { unresponsiveAsked = false; }
                    break;
                }
                case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                {
                    if (engineAsked || App.RestartAfter) return;
                    engineAsked = true;
                    if (App.Windows.Count(w => w.engineAsked) == 1)
                        MaterialDialog.Info(this, "GBrowser needs to restart", "The browser engine stopped. GBrowser will restart and open your tabs again.");
                    App.Quit(true);
                    break;
                }
            }
        }


        /// <summary>Ends the process showing this page (like Chrome's "Exit page"); the engine then reports it ended and
        /// GBrowser shows the crashed page with Reload.</summary>
        async Task EndPageProcess(CoreWebView2 core)
        {
            try
            {
                string src = core.Source;
                var infos = await env.GetProcessExtendedInfosAsync();
                var hit = infos.FirstOrDefault(pi => pi.ProcessInfo.Kind == CoreWebView2ProcessKind.Renderer &&
                    pi.AssociatedFrameInfos.Any(f => f.Source == src));
                if (hit != null) Process.GetProcessById(hit.ProcessInfo.ProcessId).Kill();
                else core.Reload();
            }
            catch { try { core.Reload(); } catch { } }
        }

        // ---------- find in page ----------

        void ShowFind()
        {
            var t = Current;
            if (t?.Created != true) return;
            t.FindOpen = true;
            string text = t.FindText ?? lastFindText;
            findBar.Place();
            findBar.Visible = true;
            findBar.BringToFront();
            if (findBar.Box.Text != text) findBar.Box.Text = text;
            findBar.Box.Focus();
            findBar.Box.SelectAll();
            if (text.Length > 0) FindRun(text);
            else findBar.SetResult(-1, -1);
        }

        async void FindRun(string text)
        {
            var t = Current;
            if (t?.Created != true) return;
            t.FindText = text;
            lastFindText = text;
            var f = t.View.CoreWebView2.Find;
            if (string.IsNullOrEmpty(text)) { try { f.Stop(); } catch { } findBar.SetResult(-1, -1); return; }
            var o = env.CreateFindOptions();
            o.FindTerm = text;
            o.SuppressDefaultFindDialog = true;   // no engine (Microsoft Edge) find box
            o.ShouldHighlightAllMatches = true;
            try { await f.StartAsync(o); } catch { }
            UpdateFindResult(t);
        }

        void UpdateFindResult(BrowserTab t)
        {
            if (t != Current || !findBar.Visible || !t.Created) return;
            try { var f = t.View.CoreWebView2.Find; findBar.SetResult(f.ActiveMatchIndex, f.MatchCount); } catch { }
        }

        void FindStep(bool next)
        {
            var t = Current;
            if (t?.Created != true) return;
            if (!findBar.Visible) { ShowFind(); return; }
            var f = t.View.CoreWebView2.Find;
            if (f.MatchCount <= 0) { FindRun(findBar.Box.Text); return; }
            if (next) f.FindNext(); else f.FindPrevious();
        }

        void CloseFind(bool focusPage)
        {
            var t = Current;
            if (t != null) t.FindOpen = false;
            findBar.Visible = false;
            if (t?.Created != true) return;
            try { t.View.CoreWebView2.Find.Stop(); } catch { }
            if (focusPage) t.View.Focus();
        }

        /// <summary>Each tab keeps its own find bar: switching tabs shows or hides it with that tab's text.</summary>
        void SyncFind(BrowserTab t)
        {
            if (t.FindOpen && t.Created) ShowFind();
            else findBar.Visible = false;
        }

        // ---------- page dialogs, sign-in, app links ----------

        static string DialogOrigin(string uri)
        {
            if (Pages.IsInternal(uri)) return "GBrowser";
            try { var u = new Uri(uri); return u.Host.Length > 0 ? u.Host : u.Scheme + ":"; } catch { return "This page"; }
        }

        /// <summary>alert / confirm / prompt / "Leave site?" in GBrowser's own dialog (the engine's are Microsoft Edge's).</summary>
        void OnScriptDialog(BrowserTab t, CoreWebView2ScriptDialogOpeningEventArgs e)
        {
            var deferral = e.GetDeferral();
            BeginInvoke((Action)(() =>
            {
                try
                {
                    int i = Tabs.IndexOf(t);
                    if (i >= 0 && t != Current) Select(i);
                    string msg = e.Message ?? "";
                    if (msg.Length > 2000) msg = msg.Substring(0, 2000) + "...";
                    string title = DialogOrigin(e.Uri) + " says";
                    var lines = msg.Length > 0 ? new[] { Tuple.Create((string)null, msg) } : new Tuple<string, string>[0];
                    switch (e.Kind)
                    {
                        case CoreWebView2ScriptDialogKind.Alert:
                            MaterialDialog.Ask(this, title, null, lines, "OK", null);
                            e.Accept();
                            break;
                        case CoreWebView2ScriptDialogKind.Confirm:
                            if (MaterialDialog.Ask(this, title, null, lines, "OK")) e.Accept();
                            break;
                        case CoreWebView2ScriptDialogKind.Prompt:
                            var r = MaterialDialog.Prompt(this, title, lines, new[] { Tuple.Create("", e.DefaultText ?? "", false) }, "OK");
                            if (r != null) { e.ResultText = r[0]; e.Accept(); }
                            break;
                        case CoreWebView2ScriptDialogKind.Beforeunload:
                            if (MaterialDialog.Ask(this, "Leave site?", null, new[] { Tuple.Create((string)null, "Changes you made may not be saved.") }, "Leave"))
                                e.Accept();
                            break;
                    }
                }
                catch { }
                finally { try { deferral.Complete(); } catch { } }
            }));
        }

        /// <summary>Sites that ask for a user name and password (HTTP authentication): GBrowser's own "Sign in".</summary>
        void OnBasicAuth(BrowserTab t, CoreWebView2BasicAuthenticationRequestedEventArgs e)
        {
            var deferral = e.GetDeferral();
            BeginInvoke((Action)(() =>
            {
                try
                {
                    string origin;
                    try { var u = new Uri(e.Uri); origin = u.Scheme + "://" + u.Authority; } catch { origin = e.Uri; }
                    var lines = new List<Tuple<string, string>> { Tuple.Create((string)null, origin) };
                    if (origin.StartsWith("http://")) lines.Add(Tuple.Create(IconData.Warning, "Your connection to this site isn't private"));
                    int i = Tabs.IndexOf(t);
                    if (i >= 0 && t != Current) Select(i);
                    var r = MaterialDialog.Prompt(this, "Sign in", lines,
                        new[] { Tuple.Create("Username", "", false), Tuple.Create("Password", "", true) }, "Sign in");
                    if (r == null) e.Cancel = true;
                    else { e.Response.UserName = r[0]; e.Response.Password = r[1]; }
                }
                catch { try { e.Cancel = true; } catch { } }
                finally { try { deferral.Complete(); } catch { } }
            }));
        }

        /// <summary>Links that open another app (mailto:, zoommtg: ...): "Open [app]?" like Chrome, with "Always allow"
        /// for secure sites (remembered per site and link type). GBrowser opens the app itself: no engine dialog.</summary>
        void OnExternalApp(BrowserTab t, CoreWebView2LaunchingExternalUriSchemeEventArgs e)
        {
            string uri = e.Uri, origin = (e.InitiatingOrigin ?? "").TrimEnd('/');
            e.Cancel = true;
            string scheme = ExternalApp.SchemeOf(uri);
            BeginInvoke((Action)(() =>
            {
                string app = ExternalApp.AppName(scheme);
                if (app == null) return;   // nothing installed for this link type (Chrome does nothing either)
                string key = origin + "|" + scheme;
                if (!(Store.Settings.ExternalAllowed.TryGetValue(key, out var always) && always))
                {
                    var lines = new[] { Tuple.Create((string)null, (origin.Length > 0 ? origin : "A website") + " wants to open this application.") };
                    bool remember = false, ok;
                    if (origin.StartsWith("https://") && !t.Private)
                        ok = MaterialDialog.AskWithCheck(this, "Open " + app + "?", lines,
                            "Always allow " + DialogOrigin(origin) + " to open links of this type in the associated app", out remember, "Open " + app);
                    else ok = MaterialDialog.Ask(this, "Open " + app + "?", null, lines, "Open " + app);
                    if (!ok) return;
                    if (remember) { Store.Settings.ExternalAllowed[key] = true; Store.SaveSettings(); }
                }
                try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
            }));
        }

        // ---------- tab menu, pin, mute ----------

        /// <summary>Right-click on a tab: Chrome's tab menu.</summary>
        void ShowTabMenu(int i, Point screen)
        {
            if (i < 0 || i >= Tabs.Count) return;
            var t = Tabs[i];
            var m = NewMenu();
            m.Add("New tab to the right", IconData.Add, null, () => NewTab(null, true, Tabs.IndexOf(t) + 1));
            m.AddSeparator();
            m.Add("Reload", IconData.Refresh, "Ctrl+R", () => { if (t.Created) t.View.CoreWebView2.Reload(); else Select(Tabs.IndexOf(t)); });
            m.Add("Duplicate", IconData.Duplicate, null, () => DuplicateTab(t)).Enabled = !string.IsNullOrEmpty(t.Created ? t.View.CoreWebView2.Source : t.Url);
            if (!t.Pinned)
            {
                m.Add("Add tab to new group", IconData.Add, null, () => AddToGroup(t, null));
                foreach (var kv in strip.Groups.Where(kv => kv.Key != t.Group).Take(6).ToList())
                {
                    var gid = kv.Key;
                    m.Add("Add to " + (string.IsNullOrEmpty(kv.Value.Name) ? TabStrip.GroupColorNames[kv.Value.Color].ToLowerInvariant() + " group" : "\u201c" + kv.Value.Name + "\u201d"), IconData.Tab, null, () => AddToGroup(t, gid));
                }
                if (t.Group != null) m.Add("Remove from group", IconData.Close, null, () => { t.Group = null; MoveOutOfGroups(t); strip.RefreshTabs(); });
            }
            m.Add(t.Pinned ? "Unpin" : "Pin", t.Pinned ? IconData.Unpin : IconData.Pin, null, () => SetPinned(t, !t.Pinned));
            m.Add(t.Muted ? "Unmute tab" : "Mute tab", t.Muted ? IconData.Volume : IconData.VolumeOff, null, () => SetMuted(t, !t.Muted));
            m.Add("Move tab to new window", IconData.TabMove, null, () => MoveTabToNewWindow(Tabs.IndexOf(t), null, false)).Enabled = Tabs.Count > 1;
            var others = App.Windows.Where(w => w != this).ToList();
            for (int k = 0; k < others.Count && k < 4; k++)
            {
                var w = others[k];
                string title = w.Current?.Title ?? "window";
                if (title.Length > 32) title = title.Substring(0, 31) + "\u2026";
                m.Add("Move tab to \u201c" + title + "\u201d", IconData.TabMove, null, () => MoveTabTo(t, w, -1));
            }
            m.AddSeparator();
            m.Add("Close", IconData.Close, "Ctrl+W", () => CloseTab(Tabs.IndexOf(t)));
            m.Add("Close other tabs", null, null, () => { foreach (var x in Tabs.Where(x => x != t && !x.Pinned).ToList()) CloseTab(Tabs.IndexOf(x)); })
                .Enabled = Tabs.Any(x => x != t && !x.Pinned);
            m.Add("Close tabs to the right", null, null, () => { foreach (var x in Tabs.Skip(Tabs.IndexOf(t) + 1).Where(x => !x.Pinned).ToList()) CloseTab(Tabs.IndexOf(x)); })
                .Enabled = Tabs.Skip(i + 1).Any(x => !x.Pinned);
            var p = strip.PointToClient(screen);
            m.ShowAt(strip, new Rectangle(p.X, p.Y, 1, 1), false, null);
        }

        // ---------- tab groups (like Chrome's) ----------

        /// <summary>Adds the tab to a group (gid null = a new one with the next colour); the tab moves next to the
        /// group's other tabs, so a group's tabs always stay together.</summary>
        void AddToGroup(BrowserTab t, string gid)
        {
            var cur = Current;
            if (gid == null)
            {
                // a new group starts where the tab is (out of any group it was in: right after that group)
                gid = Guid.NewGuid().ToString("N").Substring(0, 8);
                int color = Enumerable.Range(0, 9).FirstOrDefault(c => !strip.Groups.Values.Any(x => x.Color == c));
                strip.Groups[gid] = new TabGroup { Color = color };
                string oldGroup = t.Group;
                t.Group = null;
                if (oldGroup != null) MoveOutOfGroups(t);
                t.Group = gid;
            }
            else
            {
                Tabs.Remove(t);
                t.Group = gid;
                Tabs.Insert(Tabs.FindLastIndex(x => x.Group == gid) + 1, t);
            }
            strip.Selected = Tabs.IndexOf(cur);
            strip.RefreshTabs();
            if (strip.Groups[gid].Name.Length == 0 && Tabs.Count(x => x.Group == gid) == 1) BeginInvoke((Action)(() => ShowGroupMenu(gid, Cursor.Position)));
        }

        /// <summary>A tab taken out of its group goes right after the group (the group stays together).</summary>
        void MoveOutOfGroups(BrowserTab t)
        {
            int i = Tabs.IndexOf(t);
            string lg = i > 0 ? Tabs[i - 1].Group : null;
            if (lg == null) return;
            var cur = Current;
            Tabs.Remove(t);
            int last = Tabs.FindLastIndex(x => x.Group == lg);
            Tabs.Insert(last + 1, t);
            strip.Selected = Tabs.IndexOf(cur);
        }

        /// <summary>Chip click: collapse / expand. The selected tab never hides: another tab is selected first.</summary>
        void ToggleGroup(string gid)
        {
            if (!strip.Groups.TryGetValue(gid, out var g)) return;
            if (!g.Collapsed && Current?.Group == gid)
            {
                int other = Tabs.FindIndex(x => x.Group != gid && (x.Group == null || !strip.Groups[x.Group].Collapsed));
                if (other < 0) return;   // nothing else to show
                g.Collapsed = true;
                Select(other);
            }
            else g.Collapsed = !g.Collapsed;
            strip.RefreshTabs();
        }

        void ShowGroupMenu(string gid, Point screen)
        {
            if (!strip.Groups.TryGetValue(gid, out var g)) return;
            var m = NewMenu();
            m.AddHeader(string.IsNullOrEmpty(g.Name) ? "Tab group" : g.Name, IconData.Tab);
            m.Add("Name group...", IconData.Edit, null, () =>
            {
                if (MaterialDialog.Edit(this, "Name group", new Tuple<string, string>[0], new[] { Tuple.Create("Name", g.Name ?? "", (string[])null) },
                        out var vals, "Save", null, "Cancel") == 1)
                { g.Name = (vals[0] ?? "").Trim(); strip.RefreshTabs(); }
            });
            for (int c = 0; c < TabStrip.GroupColorNames.Length; c++)
            {
                int cc = c;
                m.AddToggle(TabStrip.GroupColorNames[c], g.Color == c, true, () => { g.Color = cc; strip.RefreshTabs(); });
            }
            m.AddSeparator();
            m.Add("New tab in group", IconData.Add, null, () =>
            {
                NewTab(null, true, Tabs.FindLastIndex(x => x.Group == gid) + 1);
                if (Current != null) { Current.Group = gid; g.Collapsed = false; strip.RefreshTabs(); }
            });
            m.Add(g.Collapsed ? "Expand group" : "Collapse group", IconData.ExpandMore, null, () => ToggleGroup(gid));
            m.Add("Ungroup", IconData.Close, null, () => { foreach (var x in Tabs.Where(x => x.Group == gid)) x.Group = null; strip.RefreshTabs(); });
            m.Add("Close group", IconData.Delete, null, () => { foreach (var x in Tabs.Where(x => x.Group == gid).ToList()) CloseTab(Tabs.IndexOf(x)); });
            var p = strip.PointToClient(screen);
            m.ShowAt(strip, new Rectangle(p.X, p.Y, 1, 1), false, null);
        }

        void DuplicateTab(BrowserTab t)
        {
            string url = t.Created ? t.View.CoreWebView2.Source : t.Url;
            if (string.IsNullOrEmpty(url)) return;
            if (t.Private) NewPrivateTab(url); else NewTab(url, true, Tabs.IndexOf(t) + 1);
        }

        /// <summary>Pinned tabs are a block at the left: pinning moves the tab to the end of that block.</summary>
        void SetPinned(BrowserTab t, bool pin)
        {
            var cur = Current;
            Tabs.Remove(t);
            t.Pinned = pin;
            if (pin) t.Group = null;
            int at = Tabs.TakeWhile(x => x.Pinned).Count();
            Tabs.Insert(at, t);
            strip.Selected = Tabs.IndexOf(cur);
            strip.RefreshTabs();
        }

        /// <summary>Muted when the tab is muted or its site's sound is off (Site settings > Sound).</summary>
        void ApplySiteSound(BrowserTab t)
        {
            if (!t.Created) return;
            string s = Blocking.SiteOf(t.View.CoreWebView2.Source);
            bool mute = t.Muted || (s != null && Store.Settings.SoundMutedSites.Contains(s));
            try { if (t.View.CoreWebView2.IsMuted != mute) t.View.CoreWebView2.IsMuted = mute; } catch { }
        }

        /// <summary>A short note in the status bubble (e.g. "Link copied", "Pop-up blocked").</summary>
        void Notice(string text)
        {
            bubble.SetText(text);
            var tm = new Timer { Interval = 2500 };
            tm.Tick += (o, e) => { tm.Dispose(); bubble.SetText(Current?.Created == true ? Current.View.CoreWebView2.StatusBarText : ""); };
            tm.Start();
        }

        void SetMuted(BrowserTab t, bool mute)
        {
            t.Muted = mute;
            ApplySiteSound(t);
            strip.RefreshTabs();
        }

        /// <summary>Tab search (the down-arrow at the left of the tab row, Ctrl+Shift+A): all open tabs.</summary>
        void ShowTabSearch()
        {
            var m = NewMenu();
            for (int i = 0; i < Tabs.Count; i++)
            {
                var t = Tabs[i];
                int idx = i;
                var it = m.AddToggle(t.Title ?? "New tab", i == strip.Selected, true, () => Select(idx));
                if (t.Private) it.Icon = IconData.Private;
                else if (t.Favicon != null) it.Image = t.Favicon;
                else it.Icon = IconData.Public;
            }
            m.ShowAt(strip, strip.SearchRect, false, "tabsearch");
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyTheme(true);   // controls were built before the theme was read
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

        /// <summary>The colour painted under a page where the page itself paints nothing. Browsers use white for web
        /// pages (pages that support dark mode paint their own dark background); a dark base here made light-only
        /// sites with a transparent background (bbc.com: dark text, no background) unreadable in dark mode.
        /// GBrowser's own pages are themed, so they keep the theme colour (no white flash while they load).</summary>
        static Color PageBase(string url) => url == null || Pages.PageName(url) == "newtab" ? Theme.ToolbarBack   // Chrome: new tab page = toolbar colour
            : Pages.IsInternal(url) || url.StartsWith("about:") ? Theme.Surface : Color.White;

        /// <summary>Applies the current theme to the window, title bar, toolbars and all web content.</summary>
        void ApplyTheme(bool force)
        {
            if (!Theme.Update() && !force) return;
            BackColor = Theme.Frame;
            content.BackColor = Theme.Surface;
            bookmarksBar.BackColor = Theme.ToolbarBack;
            strip.BackColor = Theme.Frame;
            toolbar.ApplyTheme();
            Theme.TitleBar(Handle);
            SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0027);   // SWP_FRAMECHANGED|NOZORDER|NOMOVE|NOSIZE: redraw the title bar
            string themeMsg = Store.ToJson(new Dictionary<string, object> { { "type", "theme" }, { "vars", Theme.CssVars() } });
            foreach (var t in Tabs.Where(x => x.Created))
            {
                t.View.DefaultBackgroundColor = PageBase(t.View.CoreWebView2.Source);
                ApplyColorScheme(t.View.CoreWebView2);
                if (Pages.IsInternal(t.View.CoreWebView2.Source)) try { t.View.CoreWebView2.PostWebMessageAsJson(themeMsg); } catch { }
            }
            Invalidate(true);
            strip.Invalidate(); toolbar.Invalidate(true); bookmarksBar.Invalidate();
        }

        /// <summary>prefers-color-scheme for web pages and GBrowser's own pages: Auto follows Windows.</summary>
        static void ApplyColorScheme(CoreWebView2 core)
        {
            string mode = Store.Settings.ThemeMode ?? "system";
            try
            {
                core.Profile.PreferredColorScheme = mode == "dark" ? CoreWebView2PreferredColorScheme.Dark
                    : mode == "light" ? CoreWebView2PreferredColorScheme.Light : CoreWebView2PreferredColorScheme.Auto;
            }
            catch { }
        }

        /// <summary>Microsoft Defender SmartScreen (dangerous sites and downloads) and Edge tracking prevention.</summary>
        static void ApplySecurity(CoreWebView2 core)
        {
            var st = Store.Settings;
            try { core.Settings.IsReputationCheckingRequired = st.SmartScreen; } catch { }
            try
            {
                core.Profile.PreferredTrackingPreventionLevel =
                    st.TrackingPrevention == "strict" ? CoreWebView2TrackingPreventionLevel.Strict
                    : st.TrackingPrevention == "basic" ? CoreWebView2TrackingPreventionLevel.Basic
                    : CoreWebView2TrackingPreventionLevel.Balanced;
            }
            catch { }
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
                // Secure DNS: the engine's own DoH preference, read when the engine starts (see Security.ApplySecureDnsPref)
                // after a restart, the previous engine may still be shutting down: it would save its old settings over ours
                // and the new window would join it, so wait until it is gone (max 15 s)
                if (isFirst && Environment.GetCommandLineArgs().Contains("--restarted")) await Task.Run(() => WaitForEngineExit(15000));
                env = await App.Env();   // one engine for all windows
                Updater.Changed += OnUpdateChanged;
                Downloads.Changed += OnDownloadsChanged;
                reviewWatch.Tick += (s, e) => CheckReviews();
                reviewWatch.Start();
                downloadsSeen = Downloads.All.Any(d => d.State == "in_progress" || d.State == "paused");
                OnDownloadsChanged();
                if (isFirst) Updater.Start();
#if !AUTOMATION
                // registered as a browser from another folder that is gone (moved / reinstalled): point it here
                if (isFirst)
                {
                    string reg = DefaultBrowser.RegisteredExe();
                    if (reg != null && !File.Exists(reg)) try { DefaultBrowser.Register(Application.ExecutablePath); } catch { }
                }
#endif
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
            _ = Downloads.All.Count;   // loads (and if needed encrypts) the downloads list
            var st = Store.Settings;
            var args = isFirst ? Environment.GetCommandLineArgs().Skip(1).Where(a => !a.StartsWith("--")).ToList() : new List<string>();
            bool restarted = Environment.GetCommandLineArgs().Contains("--restarted");
            // the first window brings back the last session (all its windows); a restored window its own tabs
            bool restore = restoreWs != null && restoreWs.Tabs != null && restoreWs.Tabs.Count > 0 && (!isFirst || st.RestoreTabs || restarted);
            if (restore)
            {
                foreach (var u in restoreWs.Tabs) Tabs.Add(new BrowserTab(u, TitleFor(u)) { Pinned = Tabs.Count < restoreWs.Pinned });
                // tab groups of the session
                if (restoreWs.Groups != null && restoreWs.TabGroups != null && restoreWs.TabGroups.Count == restoreWs.Tabs.Count)
                {
                    foreach (var kv in restoreWs.Groups) if (kv.Value != null) strip.Groups[kv.Key] = kv.Value;
                    for (int k = 0; k < Tabs.Count; k++)
                        if (!Tabs[k].Pinned && restoreWs.TabGroups[k].Length > 0 && strip.Groups.ContainsKey(restoreWs.TabGroups[k])) Tabs[k].Group = restoreWs.TabGroups[k];
                }
                int sel = Math.Max(0, Math.Min(restoreWs.Index, Tabs.Count - 1));
                if (args.Count == 0) Select(sel);
            }
            if (isFirst && (st.RestoreTabs || restarted) && st.Windows != null)
                foreach (var w in st.Windows.Skip(1).ToList())
                    if (w.Tabs != null && w.Tabs.Count > 0) new MainForm(null, w).Show();
            foreach (var a in args) NewTab(File.Exists(a) ? new Uri(Path.GetFullPath(a)).AbsoluteUri : Search.ToUrl(a));
            if (pendingAdopt != null) { var pa = pendingAdopt; pendingAdopt = null; AdoptTab(pa, 0); }
            foreach (var u in startUrls) NewTab(u);
            if (Tabs.Count == 0) NewTab(null);
            strip.RefreshTabs();
            // load extensions once at start so their background scripts run (profile is shared by all tabs)
            if (isFirst) { CustomLists.Start(); HostUpdates.Start(); }
            if (isFirst && st.HideIdentity && st.IdentityEachStart) Identity.NewSeed();   // a new identity each start   // your rules + custom lists; daily host lists
            await LoadExtensionsOnce();
        }

        /// <summary>Waits until no WebView2 process uses GBrowser's data folder.</summary>
        static void WaitForEngineExit(int maxMs)
        {
            var sw = Stopwatch.StartNew();
            string marker = Store.WebDataDir;
            while (sw.ElapsedMilliseconds < maxMs)
            {
                bool running = false;
                try
                {
                    using (var q = new System.Management.ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE Name='msedgewebview2.exe'"))
                        foreach (var o in q.Get())
                        {
                            var cl = o["CommandLine"] as string;
                            if (cl != null && cl.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) { running = true; break; }
                        }
                }
                catch { return; }
                if (!running) return;
                System.Threading.Thread.Sleep(250);
            }
        }

        static string TitleFor(string url)
        {
            if (Pages.IsInternal(url)) return BrowserTab.PageTitle(url);
            return Store.History.FirstOrDefault(h => h.Url == url)?.Title ?? url;
        }

        // ---------- tabs ----------

        /// <summary>A page starts loading in the shown tab while the address bar has the focus but nothing was typed
        /// there (a bookmark, a new tab page tile, Back, Home, a menu item): the page gets the focus, like Chrome. Before,
        /// only Enter in the address bar moved it, so after any other way the address bar stayed focused and, being
        /// focused, kept showing an empty or old address. The new tab page itself keeps the address bar focused.</summary>
        void PageTakesFocus(BrowserTab t, string uri)
        {
            if (!toolbar.Address.Focused || Pages.PageName(uri) == "newtab") return;
            string a = toolbar.Address.Text;
            if (a.Length > 0 && a != toolbar.FullUrl && a != toolbar.ShownUrl) return;   // the user is typing: leave it
            BeginInvoke((Action)(() =>
            {
                if (Current != t || !toolbar.Address.Focused) return;
                if (t.View != null && t.View.Visible) t.View.Focus(); else content.Focus();
            }));
        }

        /// <summary>The window that holds this tab now.</summary>
        static MainForm OwnerOf(BrowserTab t) => App.Windows.FirstOrDefault(w => w.Tabs.Contains(t));


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

        public BrowserTab NewPrivateTab(string url)
        {
            var t = new BrowserTab(url ?? Pages.Url("newtab")) { Private = true };
            if (url == null) t.Title = "Private tab";
            int index = Current == null ? Tabs.Count : strip.Selected + 1;
            Tabs.Insert(index, t);
            if (strip.Selected >= index) strip.Selected++;
            Select(index);
            if (url == null) BeginInvoke((Action)(() => toolbar.Address.Focus()));
            return t;
        }

        void Select(int i)
        {
            if (i < 0 || i >= Tabs.Count) return;
            if (Tabs[i].Group != null && strip.Groups.TryGetValue(Tabs[i].Group, out var tg) && tg.Collapsed) { tg.Collapsed = false; strip.Invalidate(); }
            omni?.Close();
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
            bubble.SetText(t.Created ? t.View.CoreWebView2.StatusBarText : "");
            SyncFind(t);
            devDock.Show(t);
            // another tab: what was typed in the address bar belongs to the old one (Chrome moves focus to the page)
            if (old != null && old != t && toolbar.Address.Focused) { if (t.View != null && t.View.Visible) t.View.Focus(); else content.Focus(); }
            UpdateUi();
            strip.RefreshTabs();
        }

#if AUTOMATION
        bool SelectNextControlTo(Control c) { c.Select(); return c.ContainsFocus; }
#endif

        /// <summary>Where the default-browser registration goes: the real place, or (test builds) a test key that
        /// changes nothing in Windows.</summary>
#if AUTOMATION
        internal const string DefaultRoot = @"Software\GoldenBoot\GBrowser-TestRegistration";
#else
        internal const string DefaultRoot = "Software";
#endif

        // ---------- Hide my identity ----------

        async void NewIdentity()
        {
            if (!MaterialDialog.AskWithCheck(this, "New identity?", new[] { Tuple.Create(IconData.Person,
                    "Sites see a new browser: what they stored about this one (site storage, caches, tracking cookies) is cleared and tracking IDs change.") },
                    "Also sign out of all sites", out bool signOut, "New identity")) return;
            var core = AnyCore;
            await Identity.New(core, signOut);
            if (Current?.Created == true) Current.View.CoreWebView2.Reload();
        }

        // ---------- element picker ----------

        bool picking;
        /// <summary>Hide an element: GBrowser's picker (assets/picker.js) in an isolated world of the page; the selector
        /// chosen becomes a rule for this site in "Your rules" and hides it at once. at = right-click point (CSS px).</summary>
        async void PickElement(Point? at, bool auto = false)
        {
            var t = Current;
            if (picking || t?.Created != true) return;
            var core = t.View.CoreWebView2;
            string url = core.Source, host = Blocking.HostOf(url);
            if (host == null || Pages.IsInternal(url)) return;
            picking = true;
            try
            {
                var tree = Store.FromJson<Dictionary<string, object>>(await core.CallDevToolsProtocolMethodAsync("Page.getFrameTree", "{}"));
                string frameId = (((tree["frameTree"] as Dictionary<string, object>)["frame"]) as Dictionary<string, object>)["id"] as string;
                var w = Store.FromJson<Dictionary<string, object>>(await core.CallDevToolsProtocolMethodAsync("Page.createIsolatedWorld",
                    Store.ToJson(new Dictionary<string, object> { { "frameId", frameId }, { "worldName", "gbpicker" } })));
                int ctx = Convert.ToInt32(w["executionContextId"]);
                string js = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "picker.js"));
                // the picker in GBrowser's current colours (Settings > Appearance), not the page's
                var sch = Palette.For(Store.Settings.ThemeColor, Theme.Dark);
                js = js.Replace("/*THEME*/null", Store.ToJson(sch.ToDictionary(kv => kv.Key, kv => (object)Palette.Hex(kv.Value))));
                if (at.HasValue) js = "window.__gbPickAt=[" + at.Value.X + "," + at.Value.Y + "];" + (auto ? "window.__gbPickAuto=true;" : "") + js;
                var r = Store.FromJson<Dictionary<string, object>>(await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                    Store.ToJson(new Dictionary<string, object> { { "expression", js }, { "contextId", ctx }, { "awaitPromise", true }, { "returnByValue", true }, { "userGesture", true } })));
                string sel = ((r["result"] as Dictionary<string, object>)?["value"]) as string;
                if (string.IsNullOrWhiteSpace(sel) || sel.Contains("\n")) return;
                string rule = host + "##" + sel;
                var st = Store.Settings;
                st.UserRules = (string.IsNullOrEmpty(st.UserRules) ? "" : st.UserRules.TrimEnd('\n') + "\n") + rule;
                Store.SaveSettings();
                CustomLists.Rebuild();
                if (!(t.Blocking?.HideNow(sel + "{display:none!important}") ?? false)) core.Reload();
                lastPickRule = rule;
            }
            catch (Exception ex) { lastPickRule = "error " + ex.Message; }
            finally { picking = false; }
        }
        string lastPickRule;   // test builds read it

        // ---------- tabs between windows ----------

        BrowserTab pendingAdopt;    // a tab moved here from another window (adopted once the engine is ready)
        bool dragWindow;            // this window was made by dragging a tab out and follows the mouse

        /// <summary>Takes a tab out of this window without closing it (it moves to another window). The window
        /// closes when it was the last tab.</summary>
        void DetachTab(BrowserTab t)
        {
            int i = Tabs.IndexOf(t);
            if (i < 0) return;
            t.Group = null;   // groups belong to their window
            bool wasSel = i == strip.Selected;
            devDock.CloseFor(t);
            if (t.FindOpen) { try { t.View?.CoreWebView2?.Find.Stop(); } catch { } t.FindOpen = false; findBar.Visible = false; }
            Tabs.RemoveAt(i);
            if (t.View != null) content.Controls.Remove(t.View);
            if (Tabs.Count == 0) { Close(); return; }
            if (wasSel) { strip.Selected = -1; Select(Math.Min(i, Tabs.Count - 1)); }
            else { if (strip.Selected > i) strip.Selected--; strip.RefreshTabs(); }
        }

        void AdoptTab(BrowserTab t, int index)
        {
            if (index < 0 || index > Tabs.Count) index = Tabs.Count;
            if (!t.Pinned) index = Math.Max(index, Tabs.TakeWhile(x => x.Pinned).Count());
            else index = Math.Min(index, Tabs.TakeWhile(x => x.Pinned).Count());
            if (t.View != null) { t.View.Visible = false; content.Controls.Add(t.View); }
            Tabs.Insert(index, t);
            if (strip.Selected >= index) strip.Selected++;
            Select(index);
            if (t.Created) devDock.Attach((uint)t.View.CoreWebView2.BrowserProcessId);
        }

        /// <summary>Tab menu "Move tab to new window", or a tab dragged out of the tab row (the new window then
        /// follows the mouse until it is let go, like Chrome; let go over another window's tab row = moved there).</summary>
        void MoveTabToNewWindow(int i, Point? screen, bool follow)
        {
            if (i < 0 || i >= Tabs.Count) return;
            var t = Tabs[i];
            if (Tabs.Count == 1)
            {
                // the only tab: the window itself is moved
                if (follow && WindowState == FormWindowState.Normal) { dragWindow = true; StartWindowMove(); }
                return;
            }
            var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            var at = screen ?? new Point(b.X + 60, b.Y + 60);
            var ws = new WindowSession { X = at.X - strip.TabRectPublic(0).X - 60, Y = at.Y - strip.Height / 2, W = b.Width, H = b.Height };
            DetachTab(t);
            var w = new MainForm(null, ws) { pendingAdopt = t, dragWindow = follow };
            w.Show();
            if (follow) w.BeginInvoke((Action)w.StartWindowMove);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ReleaseCapture();

        /// <summary>Windows' own move loop (as if the title bar were held): follows the mouse while the button is down.</summary>
        void StartWindowMove()
        {
            if ((MouseButtons & MouseButtons.Left) == 0) { DropDraggedWindow(); return; }
            ReleaseCapture();
            SendMessage(Handle, 0x00A1 /* WM_NCLBUTTONDOWN */, (IntPtr)2 /* HTCAPTION */, IntPtr.Zero);
        }

        /// <summary>A window made by dragging a tab out was let go: over another window's tab row, the tab joins
        /// that window at that place and this window closes.</summary>
        void DropDraggedWindow()
        {
            if (!dragWindow) return;
            dragWindow = false;
            var pos = Cursor.Position;
            var target = App.Windows.FirstOrDefault(w => w != this && w.Visible && w.WindowState != FormWindowState.Minimized &&
                                                         w.strip.RectangleToScreen(w.strip.ClientRectangle).Contains(pos));
            if (target == null || Tabs.Count != 1) return;
            MoveTabTo(Tabs[0], target, target.strip.IndexAt(target.strip.PointToClient(pos)));
        }

        void MoveTabTo(BrowserTab t, MainForm target, int index)
        {
            if (target == null || target == this || target.IsDisposed || !Tabs.Contains(t)) return;
            DetachTab(t);
            target.AdoptTab(t, index);
            target.BringToFront2();
        }

        // ---------- cookies of sites no tab shows any more ----------

        static Timer cookieCleanTimer;
        /// <summary>15 s after a tab closes or leaves a site (like Cookie AutoDelete), "Clear when closed" sites and, with
        /// auto-delete on, other sites without an open tab lose their cookies.</summary>
        void ScheduleCookieClean()
        {
            if (!Store.Settings.CookieAutoDelete && !Store.Settings.CookieRules.Any(r => r.Mode == "clear")) return;
            if (cookieCleanTimer == null)
            {
                cookieCleanTimer = new Timer { Interval = 15000 };
                cookieCleanTimer.Tick += async (s, e) =>
                {
                    cookieCleanTimer.Stop();
                    var open = new HashSet<string>();
                    CoreWebView2 core = null;
                    foreach (var w in App.Windows)
                        foreach (var x in w.Tabs.Where(x => !x.Private))
                        {
                            string u = x.Created ? x.View.CoreWebView2.Source : x.Url;
                            string site = Blocking.SiteOf(u);
                            if (site != null) open.Add(site);
                            if (core == null && x.Created) core = x.View.CoreWebView2;
                        }
                    await Cookies.CleanClosed(core, open);
                };
            }
            cookieCleanTimer.Stop();
            cookieCleanTimer.Start();
        }

        void CloseTab(int i)
        {
            if (i < 0 || i >= Tabs.Count) return;
            ScheduleCookieClean();
            var t = Tabs[i];
            if (!t.Private && !string.IsNullOrEmpty(t.Url) && Pages.PageName(t.Url) != "newtab") closedTabs.Push(t.Url);
            bool wasSel = i == strip.Selected;
            Tabs.RemoveAt(i);
            devDock.CloseFor(t);
            t.Dispose();
            if (Tabs.Count == 0) { Close(); return; }
            if (wasSel) { strip.Selected = -1; Select(Math.Min(i, Tabs.Count - 1)); }
            else { if (strip.Selected > i) strip.Selected--; strip.RefreshTabs(); }
        }

        void SetupCore(BrowserTab t)
        {
            // every handler acts in the window that holds the tab when the event comes (tabs can move between windows)
            MainForm F() => OwnerOf(t) ?? this;
            var core = t.View.CoreWebView2;
            // keys pressed while the page has focus come here (the control never calls ProcessCmdKey); a used key is
            // marked handled, so the engine does not also run its own (Microsoft Edge) action for it
            t.View.KeyDown += (s2, e2) => { if (F().HandleShortcut(e2.KeyData)) { e2.Handled = true; e2.SuppressKeyPress = true; } };
            var s = core.Settings;
            s.AreDevToolsEnabled = true;
            s.AreDefaultScriptDialogsEnabled = false;   // GBrowser's own alert / confirm / prompt / leave-site dialogs
            s.IsStatusBarEnabled = false;   // GBrowser's own, smaller bubble (StatusBubble)
            if (!t.Private) SessionCookies.Restore(core.CookieManager);   // session logins from last time, before any page loads
            // zoom is remembered per site (like Chrome): each site opens at its own zoom, others at 100%
            t.View.ZoomFactorChanged += (o, e) => { if (!t.ApplyingZoom) F().SaveSiteZoom(t); };   // Ctrl + wheel, Ctrl +/-, pinch
            core.SourceChanged += (o, e) => F().ApplySiteZoom(t);
            F().devDock.Attach((uint)core.BrowserProcessId);   // DevTools windows of the engine get docked
            core.StatusBarTextChanged += (o, e) => { if (t == F().Current) F().bubble.SetText(core.StatusBarText); };
            // GBrowser's own right-click menu (Material, Chrome's layout) instead of the engine's Edge menu
            core.ContextMenuRequested += (o, e) => PageMenu.Show(t, core, e, F().pageMenuHost);
            // site permissions in GBrowser's own prompt (Chrome's three choices) instead of the engine's Edge prompt
            core.ProcessFailed += (o, e) => F().OnProcessFailed(t, core, e);
            core.ScriptDialogOpening += (o, e) => F().OnScriptDialog(t, e);
            core.BasicAuthenticationRequested += (o, e) => F().OnBasicAuth(t, e);
            core.LaunchingExternalUriScheme += (o, e) => F().OnExternalApp(t, e);
            core.Find.MatchCountChanged += (o, e) => F().UpdateFindResult(t);
            core.Find.ActiveMatchIndexChanged += (o, e) => F().UpdateFindResult(t);
            core.PermissionRequested += (o, e) => PermissionPrompt.Ask(e, F(), () => F().toolbar.RectangleToScreen(F().toolbar.PillRect));
#if AUTOMATION
            core.ContextMenuRequested += (o, e) =>
            {
                string D(IList<CoreWebView2ContextMenuItem> items) => string.Join(", ", items.Select(i =>
                    i.Kind == CoreWebView2ContextMenuItemKind.Separator ? "|" : i.Name + "[" + i.Label + "]" + (i.Kind == CoreWebView2ContextMenuItemKind.Submenu ? "{" + D(i.Children) + "}" : "")));
                var tg = e.ContextMenuTarget;
                F().lastContextInfo = "target=" + tg.Kind + (tg.HasLinkUri ? " link" : "") + (tg.HasSourceUri ? " source" : "") + (tg.HasSelection ? " selection" : "") + (tg.IsEditable ? " editable" : "") + " :: " + D(e.MenuItems);
            };
#endif
            s.AreDefaultContextMenusEnabled = true;
            s.IsZoomControlEnabled = true;
            s.IsPasswordAutosaveEnabled = true;
            s.IsGeneralAutofillEnabled = true;
            s.AreBrowserAcceleratorKeysEnabled = true;
            core.SetVirtualHostNameToFolderMapping(Pages.Host, Pages.Folder, CoreWebView2HostResourceAccessKind.Deny);
            ApplyColorScheme(core);
            ApplySecurity(core);
            t.Blocking = new Blocking(t, F().env);
            t.Blocking.Changed += () => F().uiDirty = true;
            t.Blocking.Attach(core);
            // The engine proposes download names already made unique against its default folder ("x (1).exe"),
            // which hid existing files from GBrowser's "If the file already exists" choice. Its default folder is an
            // always-empty folder, so it proposes the plain name; GBrowser picks the real folder for every download
            // (OnDownloadStarting sets ResultFilePath), so nothing is ever saved there.
            string names = Path.Combine(Store.DataDir, "DownloadNames");
            Directory.CreateDirectory(names);
            core.Profile.DefaultDownloadFolderPath = names;

            core.SourceChanged += (o, e) => { t.Url = core.Source; if (t == F().Current) F().UpdateUi(); };
            core.DocumentTitleChanged += (o, e) =>
            {
                // a crashed page keeps the page's own title on its tab, like Chrome
                if (Pages.PageName(core.Source) == "crashed") { F().strip.RefreshTabs(); return; }
                t.Title = string.IsNullOrEmpty(core.DocumentTitle) ? Pages.ToDisplay(core.Source) : core.DocumentTitle;
#if AUTOMATION
                Blocking.TestLog.Add("TITLE " + DateTime.Now.ToString("HH:mm:ss.fff") + " " + t.Title + " (doc='" + core.DocumentTitle + "' src=" + core.Source + ")");
#endif
                if (Pages.IsInternal(core.Source)) t.Title = BrowserTab.PageTitle(core.Source);
                if (!t.Private) Store.UpdateHistoryTitle(core.Source, core.DocumentTitle);
                F().strip.RefreshTabs();
                if (t == F().Current) F().UpdateUi();
            };
            core.NavigationStarting += (o, e) =>
            {
                t.Loading = true; F().strip.RefreshTabs(); if (t == F().Current) F().UpdateUi();
                if (t == F().Current) F().PageTakesFocus(t, e.Uri);
                // the colour under the page: white for websites (as in Chrome), the theme colour for GBrowser's pages
                var bg = PageBase(e.Uri);
                if (t.View.DefaultBackgroundColor != bg) t.View.DefaultBackgroundColor = bg;
            };
            string navTarget = null, lastErrUrl = null;
            bool errorShown = false;
            var lastErrStatus = CoreWebView2WebErrorStatus.Unknown;
            var lastErrAt = DateTime.MinValue;
            core.NavigationStarting += (o, e) =>
            {
                // diagnostics buttons on GBrowser's error page: only from a click, only while that error page is shown
                if (e.Uri.StartsWith(Pages.Origin + "act?", StringComparison.OrdinalIgnoreCase))
                {
                    e.Cancel = true;
                    // (core.Source is the failed address while the error page shows, so GBrowser's own flag is used)
                    // run after this event returns: a modal dialog inside a WebView2 event handler blocks the engine
                    string act = e.Uri;
                    if (e.IsUserInitiated && errorShown) BeginInvoke((Action)(() => F().ErrorAction(act)));
                    return;
                }
                // GBrowser's pages under their old name (bookmarks, history, links from before 0.1.7): the new address
                if (Pages.IsOld(e.Uri))
                {
                    e.Cancel = true;
                    string to = Pages.Upgrade(e.Uri);
                    BeginInvoke((Action)(() => core.Navigate(to)));
                    return;
                }
                navTarget = e.Uri; errorShown = false;
            };
            // certificate problems: never proceed; cancel so the load fails like any other and GBrowser's page shows
            // (the engine's own certificate screen cannot be replaced: scripts do not run in it)
            core.ServerCertificateErrorDetected += (o, e) =>
            {
                e.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
                errorShown = true;   // the cancelled load reports "aborted": not the real reason
                string code;
                switch (e.ErrorStatus)
                {
                    case CoreWebView2WebErrorStatus.CertificateExpired: code = "ERR_CERT_DATE_INVALID"; break;
                    case CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect: code = "ERR_CERT_COMMON_NAME_INVALID"; break;
                    case CoreWebView2WebErrorStatus.CertificateRevoked: code = "ERR_CERT_REVOKED"; break;
                    case CoreWebView2WebErrorStatus.CertificateIsInvalid: code = "ERR_CERT_AUTHORITY_INVALID"; break;
                    default: code = "ERR_CERT_INVALID"; break;
                }
                string target = e.RequestUri;
                F().BeginInvoke((Action)(() => core.Navigate(Pages.Url("certerror") + "?u=" + Uri.EscapeDataString(target) + "&c=" + code)));
            };
            // cookie rules (blocked / protected / capped cookies): checked when a page or a response sets cookies
            if (!t.Private)
            {
                core.NavigationCompleted += (o, e) => Cookies.Guard(core);
                core.WebResourceResponseReceived += (o, e) =>
                {
                    if (!Cookies.AnyRules) return;
                    try { if (e.Response.Headers.Contains("Set-Cookie")) Cookies.Guard(core); } catch { }
                };
                core.SourceChanged += (o, e) => F().ScheduleCookieClean();
            }
            core.NavigationCompleted += (o, e) =>
            {
                t.Loading = false;
                // after a failed load the engine starts a second load of its error page, which reports "aborted":
                // the first, real reason for that address is kept (measured 18-11-2026: HostNameNotResolved, then ConnectionAborted)
                if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled && !errorShown)
                {
                    errorShown = true;
                    string u = navTarget ?? core.Source;
                    var status = e.WebErrorStatus;
                    if (u == lastErrUrl && (DateTime.UtcNow - lastErrAt).TotalSeconds < 10 && status == CoreWebView2WebErrorStatus.ConnectionAborted)
                        status = lastErrStatus;
                    else { lastErrUrl = u; lastErrStatus = status; }
                    lastErrAt = DateTime.UtcNow;
                    var shownStatus = status;
                    ErrorPage.Show(core, status, u).ContinueWith(tk =>
                    {
                        if (tk.Result == "replaced" && ErrorPage.IsNetwork(shownStatus)) _ = ErrorPage.ShowDiagnostics(core, u);
                    }, TaskScheduler.FromCurrentSynchronizationContext());
                }
                if (e.IsSuccess && !t.Private) Store.AddHistory(core.Source, core.DocumentTitle);
                F().strip.RefreshTabs();
                if (t == F().Current) F().UpdateUi();
            };
            core.HistoryChanged += (o, e) => { if (t == F().Current) F().UpdateUi(); };
            core.FaviconChanged += (o, e) => t.UpdateFavicon(() => { F().strip.RefreshTabs(); F().bookmarksBar.Invalidate(); });
            core.IsDocumentPlayingAudioChanged += (o, e) => { t.PlayingAudio = core.IsDocumentPlayingAudio; F().strip.RefreshTabs(); };
            if (t.Muted) core.IsMuted = true;
            _ = Fonts.Apply(core);   // Settings > Appearance > fonts (nothing sent while they are Chrome's defaults)
            // site settings: JavaScript off for a site (the engine's own switch, set for each new page), sound muted
            core.NavigationStarting += (o, e) =>
            {
                string js = Blocking.SiteOf(e.Uri);
                bool on = js == null || Pages.IsInternal(e.Uri) || !Store.Settings.JsBlockedSites.Contains(js);
                if (core.Settings.IsScriptEnabled != on) core.Settings.IsScriptEnabled = on;
            };
            core.SourceChanged += (o, e) => F().ApplySiteSound(t);
            if (t.Blocking != null) t.Blocking.MediaChanged += () => { F().strip.RefreshTabs(); if (t == F().Current) F().UpdateUi(); };
            core.WindowCloseRequested += (o, e) => { int i = F().Tabs.IndexOf(t); if (i >= 0) F().CloseTab(i); };
            core.ContainsFullScreenElementChanged += (o, e) => F().SetFullscreen(core.ContainsFullScreenElement);
            core.NewWindowRequested += async (o, e) =>
            {
                if (t.Blocking != null)
                {
                    t.Blocking.PopupBlocked = false;
                    if (t.Blocking.BlockPopup(e.Uri, e.IsUserInitiated))
                    {
                        e.Handled = true;   // no window: window.open returns null
                        if (t.Blocking.PopupBlocked && t == F().Current) F().Notice("Pop-up blocked");
                        return;
                    }
                }
                var deferral = e.GetDeferral();
                try
                {
                    var nt = new BrowserTab(e.Uri) { OpenedByPage = true, Private = t.Private };
                    int idx = F().Tabs.IndexOf(t) + 1;
                    F().Tabs.Insert(idx, nt);
                    if (F().strip.Selected >= idx) F().strip.Selected++;
                    await nt.EnsureCreated(F().env, F().content, SetupCore);
                    e.NewWindow = nt.View.CoreWebView2;
                    e.Handled = true;
                    Select(F().Tabs.IndexOf(nt));
                }
                catch { e.Handled = false; }
                finally { deferral.Complete(); }
            };
            core.WebMessageReceived += (o, e) => F().OnPageMessage(t, e);
            core.DownloadStarting += (o, e) => F().OnDownloadStarting(t, core, e);
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
            // a new engine sits on about:blank until its first page arrives (measured 19-11-2026: the new tab showed
            // "about:blank" for ~40 ms); until then the address is the page the tab is going to
            if (url == "about:blank" && !string.IsNullOrEmpty(t.Url) && t.Url != "about:blank") url = t.Url;
            toolbar.CanBack = core?.CanGoBack ?? false;
            toolbar.CanForward = core?.CanGoForward ?? false;
            toolbar.Loading = t.Loading;
            toolbar.Secure = url.StartsWith("https://") || Pages.IsInternal(url);
            toolbar.PageKind = Pages.IsInternal(url) ? "internal" : url.StartsWith("https://") ? "secure"
                : url.StartsWith("http://") && !Security.IsLocal(Blocking.HostOf(url)) ? "insecure" : "";
            if (toolbar.ShowHome != Store.Settings.ShowHomeButton) { toolbar.ShowHome = Store.Settings.ShowHomeButton; toolbar.LayoutNow(); }
            toolbar.Bookmarked = Store.IsBookmarked(url);
            int zp = t.Created ? (int)Math.Round(t.View.ZoomFactor * 100) : 100, dz = (int)Math.Round(Store.Settings.DefaultZoom * 100);
            bool zs = t.Created && !Pages.IsInternal(url) && zp != dz;
            if (zs != toolbar.ZoomShown || zp != toolbar.ZoomPercent || dz != toolbar.DefaultZoomPercent)
            { toolbar.ZoomShown = zs; toolbar.ZoomPercent = zp; toolbar.DefaultZoomPercent = dz; toolbar.LayoutNow(); }
            toolbar.BlockedCount = t.Blocked;
            string pageHost = Blocking.HostOf(url);
            toolbar.ShieldOn = pageHost == null ? Store.Settings.AdblockEnabled : Blocking.OnFor(pageHost);
            string full = Pages.ToDisplay(url);
            if (Pages.PageName(url) == "insecure" || Pages.PageName(url) == "certerror" || Pages.PageName(url) == "crashed")
            {
                bool crashedPage = Pages.PageName(url) == "crashed";
                try
                {
                    string q = url.Substring(url.IndexOf("?u=", StringComparison.Ordinal) + 3);
                    int amp = q.IndexOf('&');
                    full = Uri.UnescapeDataString(amp >= 0 ? q.Substring(0, amp) : q);
                }
                catch { }
                // a crashed page keeps the page's own address and security state; warnings show as insecure
                toolbar.Secure = crashedPage && full.StartsWith("https://");
                toolbar.PageKind = !crashedPage ? "insecure" : full.StartsWith("https://") ? "secure" : "";
            }
            toolbar.SetUrl(full, ShortUrl(full));
            var storeId = Extensions.StorePageId(url);
            bool chip = storeId != null;
            toolbar.StoreChipText = chip && Extensions.Index.ContainsKey(storeId) ? "Installed" : "Add to GBrowser";
            if (chip != toolbar.StoreChip) { toolbar.StoreChip = chip; toolbar.LayoutNow(); }
            toolbar.Invalidate();
            Text = (string.IsNullOrEmpty(t.Title) ? "GBrowser" : t.Title + " - GBrowser");
        }

        /// <summary>Address as Chrome shows it when not editing: without "https://" / "http://" and "www.", and without
        /// a lone trailing "/" (one.one.one.one/help/ stays as it is).</summary>
        static string ShortUrl(string url)
        {
            if (string.IsNullOrEmpty(url) || url.StartsWith("gbrowser://")) return url;
            string s = url;
            if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) s = s.Substring(8);
            else if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) s = s.Substring(7);
            else return url;
            if (s.StartsWith("www.", StringComparison.OrdinalIgnoreCase) && s.IndexOf('.', 4) > 0) s = s.Substring(4);
            int slash = s.IndexOf('/');
            if (slash >= 0 && slash == s.Length - 1) s = s.Substring(0, slash);
            return s;
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

        /// <summary>Star / Ctrl+D, like Chrome: a new bookmark goes to the folder used last and the "Bookmark added"
        /// dialog lets you rename it, pick its folder or remove it; on a bookmarked page it edits that bookmark.</summary>
        void ToggleBookmark()
        {
            var t = Current;
            if (t == null) return;
            string url = t.Created ? t.View.CoreWebView2.Source : t.Url;
            if (string.IsNullOrEmpty(url) || Pages.PageName(url) == "newtab") return;
            var b = Bm.ForUrl(url);
            bool added = b == null;
            if (added) b = Bm.Add(url, t.Title, Store.Settings.LastBookmarkFolder);
            EditBookmarkDialog(b, added ? "Bookmark added" : "Edit bookmark");
        }

        /// <summary>Name, address (not for folders) and folder of a bookmark; Remove deletes it.</summary>
        internal void EditBookmarkDialog(Bookmark b, string title)
        {
            var folders = Bm.Folders().Where(f => !b.Folder || (f.Item1 != b.Id && !f.Item2.Contains(" / " + b.Title + " / "))).ToList();
            int sel = Math.Max(0, folders.FindIndex(f => f.Item1 == b.Parent));
            var inputs = new List<Tuple<string, string, string[]>> { Tuple.Create("Name", b.Title, (string[])null) };
            if (!b.Folder) inputs.Add(Tuple.Create("URL", b.Url, (string[])null));
            inputs.Add(Tuple.Create("Folder", sel.ToString(), folders.Select(f => f.Item2).ToArray()));
            int r = MaterialDialog.Edit(this, title, new Tuple<string, string>[0], inputs, out var v, "Done", "Remove", null);
            if (r == 2) Bm.Remove(b.Id);
            else if (r == 1)
            {
                string url = b.Folder ? null : (Search.ToUrl(v[1]) ?? b.Url);
                Bm.Edit(b.Id, v[0], url);
                int fi = int.Parse(v[v.Length - 1]);
                if (fi >= 0 && fi < folders.Count && folders[fi].Item1 != b.Parent)
                {
                    Bm.Move(b.Id, folders[fi].Item1, -1);
                    if (!b.Folder) { Store.Settings.LastBookmarkFolder = folders[fi].Item1; Store.SaveSettings(); }
                }
            }
        }

        /// <summary>"Add page" (the current page's name and address to start with, like Chrome).</summary>
        void AddPageDialog(long parent, int index)
        {
            var t = Current;
            string url = t == null ? "" : t.Created ? t.View.CoreWebView2.Source : t.Url;
            if (Pages.IsInternal(url)) url = "";
            var r = MaterialDialog.Edit(this, "Add page", new Tuple<string, string>[0], new[]
            {
                Tuple.Create("Name", url.Length > 0 ? t.Title : "", (string[])null), Tuple.Create("URL", url, (string[])null)
            }, out var v, "Save", null, "Cancel");
            if (r != 1) return;
            string u = Search.ToUrl(v[1] ?? "");
            if (string.IsNullOrWhiteSpace(v[1]) || u == null) return;
            Bm.Add(u, string.IsNullOrWhiteSpace(v[0]) ? u : v[0], parent, index);
        }

        void AddFolderDialog(long parent, int index)
        {
            var v = MaterialDialog.Prompt(this, "New folder", new Tuple<string, string>[0], new[] { Tuple.Create("Name", "New folder", false) }, "Save");
            if (v != null) Bm.AddFolder(v[0], parent, index);
        }

                /// <summary>Every window's star and bookmarks bar follow bookmark changes.</summary>
        static void OnBookmarksChanged()
        {
            foreach (var w in App.Windows.ToList())
            {
                if (w.IsDisposed) continue;
                w.bookmarksBar.Invalidate();
                w.UpdateUi();
                foreach (var t in w.Tabs)
                    if (t.Created && Pages.PageName(t.View.CoreWebView2.Source) == "bookmarks")
                        try { t.View.CoreWebView2.PostWebMessageAsJson("{\"type\":\"bookmarks\"}"); } catch { }
            }
        }
        static bool bookmarksHooked;

        void OpenPage(string name)
        {
            string url = Pages.Url(name);
            int existing = Tabs.FindIndex(x => x.Url != null && Pages.PageName(x.Url) == name);
            if (existing >= 0) { Select(existing); if (Tabs[existing].Created) Tabs[existing].View.CoreWebView2.Reload(); return; }
            if (Current != null && Pages.PageName(Current.Url) == "newtab") Current.Navigate(url);
            else NewTab(url);
        }

        // ---------- menus ----------

        ExtensionPopup lastPopup;

        MaterialMenu NewMenu() => new MaterialMenu();

        void ShowMenu()
        {
            var m = NewMenu();
            if (Updater.UpdateReady)
            {
                m.Add("Relaunch to update", IconData.Restore, "Version " + Updater.Latest, RelaunchToUpdate);
                m.AddSeparator();
            }
            else if (Updater.State == "downloading")
            {
                m.Add("Downloading update " + Updater.Percent + "%", IconData.Download, null, null).Enabled = false;
                m.AddSeparator();
            }
            m.Add("New tab", IconData.Tab, "Ctrl+T", () => NewTab(null));
            m.Add("New window", IconData.NewWindow, "Ctrl+N", () => App.NewWindow(null));
            m.Add("New private tab", IconData.Private, "Ctrl+Shift+N", () => NewPrivateTab(null));
            m.Add("Reopen closed tab", IconData.Restore, "Ctrl+Shift+T", ReopenClosed).Enabled = closedTabs.Count > 0;
            m.AddSeparator();
            // grouped like Chrome's menu (it had grown taller than the screen: 26 rows)
            m.Add("History", IconData.History, "Ctrl+H", () => OpenPage("history"));
            m.Add("Downloads", IconData.Download, "Ctrl+J", OpenDownloads);
            m.AddSub("Bookmarks", IconData.Bookmarks, sm =>
            {
                sm.Add("Bookmark manager", IconData.Bookmarks, "Ctrl+Shift+O", () => OpenPage("bookmarks"));
                sm.Add(Store.Settings.ShowBookmarksBar ? "Hide bookmarks bar" : "Show bookmarks bar", IconData.Star, "Ctrl+Shift+B", ToggleBookmarksBar);
                sm.Add("Bookmark this tab", IconData.Star, "Ctrl+D", ToggleBookmark);
            });
            m.Add("Extensions", IconData.Extension, null, () => OpenPage("extensions"));
            m.AddSeparator();
            string curUrl = Current?.Created == true ? Current.View.CoreWebView2.Source : Current?.Url;
            bool web = curUrl != null && (curUrl.StartsWith("http://") || curUrl.StartsWith("https://"));
            // Chrome's zoom row: - 100% + and full screen in one line (stays open while zooming)
            MaterialMenu.RowButton pct = null;
            Func<string> zoomText = () => (Current?.Created == true ? (int)Math.Round(Current.View.ZoomFactor * 100) : 100) + "%";
            pct = new MaterialMenu.RowButton { Text = zoomText(), Tip = "Reset", KeepOpen = true, Click = () => { if (Current?.Created == true) { Current.View.ZoomFactor = Store.Settings.DefaultZoom; SaveSiteZoom(Current); } pct.Text = zoomText(); } };
            m.AddButtons("Zoom", IconData.ZoomIn,
                new MaterialMenu.RowButton { Icon = IconData.Minus, Tip = "Zoom out", KeepOpen = true, Click = () => { Zoom(1 / 1.1); pct.Text = zoomText(); } },
                pct,
                new MaterialMenu.RowButton { Icon = IconData.Add, Tip = "Zoom in", KeepOpen = true, Click = () => { Zoom(1.1); pct.Text = zoomText(); } },
                new MaterialMenu.RowButton { Icon = IconData.Fullscreen, Tip = "Full screen", Click = () => SetFullscreen(!fullscreen) });
            m.Add("Print", IconData.Print, "Ctrl+P", () => { if (Current?.Created == true) Current.View.CoreWebView2.ShowPrintUI(); });
            m.Add("Find on page", IconData.Find, "Ctrl+F", ShowFind);
            m.AddSub("Save and share", IconData.SaveShare, sm =>
            {
                sm.Add("Save page as...", IconData.Download, "Ctrl+S", SavePageAs).Enabled = web;
                sm.Add("Copy link", IconData.Link, null, CopyLink).Enabled = web;
                sm.Add("Share...", IconData.Share, null, () => { if (WebUrl != null && !WinShare.Share(Handle, Current.Title, WebUrl)) Notice("Sharing is not available"); }).Enabled = web;
                sm.Add("Create QR code", IconData.QrCode, null, () => NewTab(Pages.Url("qr") + "?u=" + Uri.EscapeDataString(WebUrl ?? ""))).Enabled = web;
            });
            m.AddSub("More tools", IconData.Build, sm =>
            {
                sm.Add("Task manager", IconData.Memory, "Shift+Esc", () => OpenPage("tasks"));
                sm.Add("Developer tools", IconData.Code, "Ctrl+Shift+I", ToggleDevTools);   // Chrome's menu shows Ctrl+Shift+I
                sm.Add("View page source", IconData.Code, "Ctrl+U", ViewSource).Enabled = web;
                string keepSite = Pages.IsInternal(curUrl) ? null : Blocking.SiteOf(curUrl);   // GBrowser's own pages never unload
                if (keepSite != null)
                {
                    bool kept = Store.Settings.KeepActiveSites.Contains(keepSite);
                    var keep = sm.AddToggle("Keep " + keepSite + " active", kept, true, () =>
                    {
                        Store.Settings.KeepActiveSites.Remove(keepSite);
                        if (!kept) Store.Settings.KeepActiveSites.Add(keepSite);
                        Store.SaveSettings();
                    });
                    keep.Icon = IconData.Memory;
                }
            });
            m.AddSeparator();
            m.Add("Settings", IconData.Settings, null, () => OpenPage("settings"));
            m.Add("About GBrowser", IconData.Info, null, () => OpenPage("about"));
            if (App.Windows.Count > 1) m.Add("Close window", IconData.Close, "Ctrl+Shift+W", Close);
            m.Add("Exit", IconData.Logout, null, () => App.Quit());
            m.ShowAt(toolbar, toolbar.ButtonRect("menu"), true, "menu");
        }

        // ---------- downloads ----------

        /// <summary>Where downloads go: Settings > Downloads, else the Windows Downloads folder
        /// (test builds: their own test folder unless a folder was chosen).</summary>
        string DownloadFolder()
        {
            string f = Store.Settings.DownloadFolder;
            if (!string.IsNullOrEmpty(f) && Directory.Exists(f)) return f;
#if AUTOMATION
            return Path.Combine(Store.DataDir, "Downloads");
#else
            return FolderPicker.WindowsDownloads();
#endif
        }

        static string UniquePath(string path)
        {
            if (!File.Exists(path) && !File.Exists(path + ".gbdownload")) return path;
            string dir = Path.GetDirectoryName(path), name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
            for (int i = 1; ; i++)
            {
                string p = Path.Combine(dir, name + " (" + i + ")" + ext);
                if (!File.Exists(p) && !File.Exists(p + ".gbdownload")) return p;
            }
        }

        /// <summary>Every download: the folder from Settings (or a Save As window), then GBrowser's multi-part engine
        /// when the server allows it, else the engine's own download. Shown on GBrowser's download button and page.</summary>
        async void OnDownloadStarting(BrowserTab t, CoreWebView2 core, CoreWebView2DownloadStartingEventArgs e)
        {
            var deferral = e.GetDeferral();
            try
            {
                string name = Path.GetFileName(e.ResultFilePath);
                if (string.IsNullOrEmpty(name)) name = "download";
                string folder = DownloadFolder();
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, name), replace = null;
                if (File.Exists(path) || File.Exists(path + ".gbdownload"))
                {
                    // a file with this name is already there: Settings > Downloads > "If the file exists"
                    string mode = Store.Settings.DownloadConflict ?? "ask";
                    if (mode == "ask" && !Store.Settings.AskWhereToSave)
                    {
                        int c = MaterialDialog.Choose(this, "File already exists", null, new[] {
                            Tuple.Create(IconData.File, name + " is already in " + Path.GetFileName(folder.TrimEnd('\\')) + ".") },
                            "Keep both", "Replace");
                        if (c == 0) { e.Cancel = true; return; }
                        mode = c == 2 ? "replace" : "keep";
                    }
                    if (mode == "replace" && File.Exists(path)) replace = path;
                    path = UniquePath(path);   // "Replace" also downloads beside it first; the old file goes only when the new one is complete
                }
                if (Store.Settings.AskWhereToSave)
                {
                    using (var dlg = new SaveFileDialog { FileName = name, InitialDirectory = folder, OverwritePrompt = true, AutoUpgradeEnabled = true, Title = "Save as" })
                    {
                        string ext = Path.GetExtension(name);
                        dlg.Filter = (string.IsNullOrEmpty(ext) ? "File" : ext.TrimStart('.').ToUpperInvariant() + " file") + "|*" + ext + "|All files|*.*";
                        if (dlg.ShowDialog(this) != DialogResult.OK) { e.Cancel = true; return; }
                        path = dlg.FileName;
                        replace = null;
                        if (File.Exists(path)) { replace = path; path = UniquePath(path); }   // Windows already asked "replace it?"
                    }
                }
                e.ResultFilePath = path;
                // GBrowser's own download button and page replace the engine's (Microsoft Edge) download bubble for
                // every download; a file SmartScreen holds is handled in GBrowser too (CheckBlocked)
                e.Handled = true;
                // the page as referrer, only a real web address (not about:blank / GBrowser pages) and never from private tabs
                string referer = t.Private || core.Source == null || !(core.Source.StartsWith("https://") || core.Source.StartsWith("http://")) || Pages.IsInternal(core.Source) ? null : core.Source;
                // read once: every read of e.DownloadOperation returns a new wrapper object
                var op = e.DownloadOperation;
                var fast = await FastDownload.TryTakeOver(op, path, referer, core.CookieManager);
                Downloads.Item tracked;
                if (fast != null) { e.Cancel = true; fast.Item.ReplaceTarget = replace; tracked = fast.Item; }   // GBrowser downloads it in parts
                else
                {
                    var it = Downloads.Track(op, path);
                    it.ReplaceTarget = replace;
                    it.Core = core;
                    tracked = it;
                }
                // "Always open files of this type"
                string autoType = Downloads.AutoOpenType(path);
                if (autoType != null && Store.Settings.AutoOpenTypes.Contains(autoType)) tracked.OpenWhenDone = true;
                downloadsSeen = true;
                OnDownloadsChanged();
                ShowDownloadBubble(true);   // like Chrome: the bubble opens when a download starts
            }
            catch { }
            finally { deferral.Complete(); }
        }

        bool downloadsSeen;
        readonly Timer reviewWatch = new Timer { Interval = 1000 };

        /// <summary>Microsoft Defender SmartScreen holds a flagged download (all bytes in, never completes) until someone
        /// answers in the engine's own (Microsoft Edge) panel; WebView2 has no API to read or answer that warning
        /// (measured 19-11-2026). GBrowser keeps everything in its own UI: such a download is cancelled (the engine
        /// deletes the held file) and shown as "Blocked" with "Download anyway" (after a warning), like Chrome.</summary>
        void CheckReviews()
        {
            foreach (var d in Downloads.All.Where(x => x.Op != null && x.State == "in_progress").ToList())
            {
                // read from the engine itself: a small file can be complete before any progress event reaches GBrowser
                long got = d.Op.BytesReceived, total = (long)(d.Op.TotalBytesToReceive ?? 0);
                bool full = d.Op.State == CoreWebView2DownloadState.InProgress && total > 0 && got >= total;
                if (!full) { d.FullAt = default(DateTime); continue; }
                if (d.FullAt == default(DateTime)) { d.FullAt = DateTime.UtcNow; d.Received = got; d.Total = total; continue; }
                bool held = (DateTime.UtcNow - d.FullAt).TotalSeconds > 4;   // beyond a normal antivirus scan of a finished file
                if (held)
                {
                    d.Blocking = true;
                    Downloads.MarkStopped(d);
                    try { d.Op.Cancel(); } catch { }
                    OnDownloadsChanged();
                }
            }
        }

        /// <summary>"Download anyway" for a file SmartScreen blocked: a clear warning first, then GBrowser downloads it.</summary>
        void DownloadAnyway(Downloads.Item d)
        {
            string name = Path.GetFileName(d.Path ?? "file"), host = Blocking.HostOf(d.Url) ?? "this site";
            if (!MaterialDialog.Ask(this, "Download anyway?", null, new[] {
                    Tuple.Create(IconData.Warning, "Microsoft Defender SmartScreen says " + name + " may be harmful."),
                    Tuple.Create(IconData.Info, "Only keep it if you trust " + host + ".") }, "Download anyway")) return;
            var core = AnyCore;
            if (core == null) return;
            FastDownload.Plain(d, core.CookieManager);
        }
        string lastDownloadStates;

        void OnDownloadsChanged()
        {
            if (IsDisposed) return;
            if (InvokeRequired) { try { BeginInvoke((Action)OnDownloadsChanged); } catch { } return; }
            var live = Downloads.All.Where(d => d.State == "in_progress").ToList();
            long total = live.Sum(d => d.Total), got = live.Sum(d => d.Received);
            toolbar.ShowDownloads = downloadsSeen;
            // an open downloads page refreshes itself (new download, pause, finish...)
            var states = string.Join(",", Downloads.All.Take(50).Select(d => d.Id + d.State));
            if (states != lastDownloadStates)
            {
                lastDownloadStates = states;
                foreach (var tab in Tabs)
                    if (tab.Created && Pages.PageName(tab.View.CoreWebView2.Source) == "downloads")
                        try { tab.View.CoreWebView2.PostWebMessageAsJson("{\"type\":\"downloads\"}"); } catch { }
            }
            toolbar.DownloadProgress = live.Count == 0 ? -1f : total > 0 ? (float)got / total : 0f;
            toolbar.PerformLayout();
            toolbar.Invalidate();
        }

        /// <summary>Pause / resume / cancel / retry from the downloads page, for both kinds of download.</summary>
        void DownloadAction(Downloads.Item d, string action)
        {
            if (d.Engine == "multi")
            {
                if (d.Fast == null && (action == "resume" || action == "retry" || action == "cancel"))
                    FastDownload.Restore(d, AnyCore?.CookieManager);
                switch (action)
                {
                    case "pause": d.Fast?.Pause(); break;
                    case "resume": case "retry":
                        if (action == "retry" && string.IsNullOrEmpty(d.Parts)) { d.Fast = null; NewTab(d.Url, false); break; }
                        d.Fast?.Start(); break;
                    case "cancel": d.Fast?.Cancel(); break;
                }
                return;
            }
            switch (action)
            {
                case "cancel": d.Op?.Cancel(); break;
                case "anyway": DownloadAnyway(d); break;
                case "pause": d.Op?.Pause(); d.State = "paused"; Downloads.MarkStopped(d); break;
                case "resume": if (d.Op != null && d.Op.CanResume) { d.Op.Resume(); d.State = "in_progress"; Downloads.MarkRunning(d); } break;
                case "retry": NewTab(d.Url, false); break;
            }
        }

        /// <summary>Runs the verified new setup in update mode and closes (the setup starts GBrowser again; tabs come back).</summary>
        void RelaunchToUpdate()
        {
            if (Updater.RunSetup()) App.Quit();
        }

        void OnUpdateChanged()
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke((Action)OnUpdateChanged); return; }
            toolbar.UpdateDot = Updater.UpdateReady;
            toolbar.Invalidate();
            // GBrowser's About page shows the state live
            foreach (var t in Tabs)
                if (t.Created && Pages.PageName(t.View.CoreWebView2.Source) == "about")
                    try { t.View.CoreWebView2.PostWebMessageAsJson(Store.ToJson(new Dictionary<string, object> { { "type", "update" }, { "state", UpdateInfo() } })); } catch { }
        }

        static Dictionary<string, object> UpdateInfo() => new Dictionary<string, object>
        {
            { "state", Updater.State }, { "current", Updater.Current }, { "latest", Updater.Latest ?? "" },
            { "percent", Updater.Percent }, { "error", Updater.Error ?? "" },
            { "checked", Updater.LastCheck == DateTime.MinValue ? "" : Updater.LastCheck.ToString("dd MMM yyyy, HH:mm") },
        };

        /// <summary>Zoom is kept per host (www.bbc.com, news.bbc.co.uk...), as Chrome does; GBrowser's pages are not zoomed.</summary>
        static string ZoomKey(string url) => string.IsNullOrEmpty(url) || Pages.IsInternal(url) ? null : Blocking.HostOf(url);

        /// <summary>Remembers the tab's current zoom for its site (100% = forgotten). Called on every zoom change: the
        /// engine raises no event when GBrowser itself sets the zoom (menu, Ctrl+0), only for zooming in the page.</summary>
        void SaveSiteZoom(BrowserTab t)
        {
            if (!t.Created || t.Private) return;
            string key = ZoomKey(t.View.CoreWebView2.Source);
            if (key == null) return;
            double z = Math.Round(t.View.ZoomFactor, 3);
            var sz = Store.Settings.SiteZoom;
            bool changed;
            if (Math.Abs(z - Store.Settings.DefaultZoom) < 0.005) changed = sz.Remove(key);
            else { changed = !sz.TryGetValue(key, out var old) || Math.Abs(old - z) > 0.001; sz[key] = z; }
            if (changed) Store.SaveSettings();
            UpdateUi();
        }

        void ApplySiteZoom(BrowserTab t)
        {
            if (!t.Created) return;
            string key = ZoomKey(t.View.CoreWebView2.Source);
            if (key == null) return;
            double want = Store.Settings.SiteZoom.TryGetValue(key, out var z) ? z : Store.Settings.DefaultZoom;
            if (Math.Abs(t.View.ZoomFactor - want) < 0.001) return;
            t.ApplyingZoom = true;
            try { t.View.ZoomFactor = want; } finally { BeginInvoke((Action)(() => t.ApplyingZoom = false)); }
        }

        void Zoom(double f)
        {
            if (Current?.Created != true) return;
            Current.View.ZoomFactor = Math.Max(0.25, Math.Min(5, Current.View.ZoomFactor * f));
            SaveSiteZoom(Current);
        }

        void OpenDownloads() => OpenPage("downloads");

        // ---------- task manager (Shift+Esc) ----------

        static readonly Dictionary<int, Tuple<TimeSpan, DateTime>> cpuSamples = new Dictionary<int, Tuple<TimeSpan, DateTime>>();

        /// <summary>Memory (private bytes) and CPU (% of one core since the last read) of a process; -1 = unknown.</summary>
        static Tuple<long, double> Usage(int pid)
        {
            try
            {
                using (var p = Process.GetProcessById(pid))
                {
                    long mem = p.PrivateMemorySize64;
                    var now = DateTime.UtcNow; var cpu = p.TotalProcessorTime;
                    double pct = -1;
                    lock (cpuSamples)
                    {
                        if (cpuSamples.TryGetValue(pid, out var last) && (now - last.Item2).TotalMilliseconds > 200)
                            pct = Math.Max(0, (cpu - last.Item1).TotalMilliseconds / (now - last.Item2).TotalMilliseconds * 100);
                        cpuSamples[pid] = Tuple.Create(cpu, now);
                    }
                    return Tuple.Create(mem, pct);
                }
            }
            catch { return Tuple.Create(-1L, -1.0); }
        }

        /// <summary>Chrome's task manager rows: GBrowser, the engine's processes, and for each page process the tabs,
        /// frames of other sites and extensions it serves (matched by frame ID).</summary>
        async Task<List<Dictionary<string, object>>> TaskList()
        {
            var rows = new List<Dictionary<string, object>>();
            var tabsByFrame = new Dictionary<uint, BrowserTab>();
            foreach (var w in App.Windows)
                foreach (var x in w.Tabs)
                    if (x.Created) try { tabsByFrame[x.View.CoreWebView2.FrameId] = x; } catch { }
            void Add(string kind, string task, int pid, int tab, bool canEnd)
            {
                var u = Usage(pid);
                rows.Add(new Dictionary<string, object> { { "kind", kind }, { "task", task }, { "pid", pid }, { "mem", u.Item1 },
                    { "cpu", Math.Round(u.Item2, 1) }, { "tab", tab }, { "canEnd", canEnd } });
            }
            Add("app", "GBrowser", Process.GetCurrentProcess().Id, 0, false);
            IReadOnlyList<CoreWebView2ProcessExtendedInfo> infos;
            try { infos = await env.GetProcessExtendedInfosAsync(); } catch { return rows; }
            foreach (var info in infos.OrderBy(i => i.ProcessInfo.Kind))
            {
                int pid = info.ProcessInfo.ProcessId;
                switch (info.ProcessInfo.Kind)
                {
                    case CoreWebView2ProcessKind.Browser: Add("browser", "Browser", pid, 0, false); break;
                    case CoreWebView2ProcessKind.Gpu: Add("gpu", "GPU process", pid, 0, true); break;
                    case CoreWebView2ProcessKind.Utility: Add("utility", "Utility process", pid, 0, true); break;
                    case CoreWebView2ProcessKind.Renderer:
                    {
                        var frames = info.AssociatedFrameInfos ?? new List<CoreWebView2FrameInfo>();
                        var tabs = frames.Where(f => f.ParentFrameInfo == null && tabsByFrame.ContainsKey(f.FrameId))
                                         .Select(f => tabsByFrame[f.FrameId]).Distinct().ToList();
                        if (tabs.Count > 0)
                            foreach (var tb in tabs) Add("tab", (tb.Private ? "Private tab: " : "Tab: ") + (tb.Title ?? "New tab"), pid, tb.Id, true);
                        else
                        {
                            string src = frames.Select(f => f.Source).FirstOrDefault(s => !string.IsNullOrEmpty(s) && s != "about:blank") ?? "";
                            if (src.StartsWith("chrome-extension://") || src.StartsWith("extension://"))
                            {
                                string extId = src.Split('/')[2];
                                string extName = null;
                                try { extName = (await Profile.GetBrowserExtensionsAsync()).FirstOrDefault(x => x.Id == extId)?.Name; } catch { }
                                Add("extension", "Extension: " + (extName ?? extId), pid, 0, true);
                            }
                            else if (src.Length > 0) Add("frame", "Subframe: " + (Blocking.SiteOf(src) ?? Blocking.HostOf(src) ?? src), pid, 0, true);
                            else Add("spare", "Spare renderer", pid, 0, true);
                        }
                        break;
                    }
                    default: Add("other", info.ProcessInfo.Kind.ToString(), pid, 0, true); break;
                }
            }
            return rows;
        }

        // ---------- download bubble (the toolbar's download button, like Chrome's) ----------

        MaterialMenu dlBubble;
        Timer dlBubbleTimer;

        static string SizeText(long b) => b >= 1L << 30 ? (b / (double)(1L << 30)).ToString("0.0") + " GB"
            : b >= 1 << 20 ? (b / (double)(1 << 20)).ToString("0.0") + " MB" : b >= 1024 ? (b / 1024) + " KB" : b + " B";

        static string StatusText(Downloads.Item d)
        {
            switch (d.State)
            {
                case "in_progress":
                    return d.Total > 0 ? SizeText(d.Received) + " of " + SizeText(d.Total) + ", " + (int)(d.Received * 100 / d.Total) + "%" : SizeText(d.Received);
                case "paused": return "Paused";
                case "completed": return File.Exists(d.Path) ? SizeText(d.Total > 0 ? d.Total : d.Received) : "Deleted";
                case "blocked": return "Blocked";
                default: return "Failed";
            }
        }

        /// <summary>Recent downloads under the download button, with live progress. auto = opened because a download
        /// started: it closes again after a few seconds unless the pointer is on it.</summary>
        void ShowDownloadBubble(bool auto)
        {
            if (auto && dlBubble != null && !dlBubble.IsDisposed) { RefreshDownloadBubble(); return; }
            var recent = Downloads.All.Take(5).ToList();
            if (recent.Count == 0) { OpenDownloads(); return; }
            var m = NewMenu();
            m.MinWidth = 400;   // room for the longest status ("999.9 MB of 999.9 MB, 100%") beside the name
            m.AddHeader("Recent downloads", IconData.Download);
            foreach (var d in recent)
            {
                var item = d;
                var e = m.Add(Path.GetFileName(d.Path ?? "download"), IconData.File, StatusText(d), () =>
                {
                    if (item.State == "completed" && File.Exists(item.Path)) Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
                    else OpenDownloads();
                });
                e.Tag = d;
            }
            m.AddSeparator();
            var newest = recent[0];
            if (File.Exists(newest.Path)) m.Add("Show in folder", IconData.Folder, null, () => Process.Start("explorer.exe", "/select,\"" + newest.Path + "\""));
            m.Add("Full download history", IconData.History, "Ctrl+J", OpenDownloads);
            toolbar.ShowDownloads = downloadsSeen = true;
            toolbar.LayoutNow();
            if (!m.ShowAt(toolbar, toolbar.ButtonRect("downloads"), true, "downloads")) return;
            dlBubble = m;
            dlBubbleTimer?.Dispose();
            dlBubbleTimer = new Timer { Interval = 500 };
            int ticks = 0;
            dlBubbleTimer.Tick += (o, a) =>
            {
                if (m.IsDisposed) { dlBubbleTimer.Stop(); return; }
                RefreshDownloadBubble();
                ticks++;
                // auto-opened: closes after 6 s unless the pointer is over it (then it stays until the user leaves it)
                if (auto && ticks >= 12 && !m.Bounds.Contains(Cursor.Position)) m.Close();
            };
            dlBubbleTimer.Start();
        }

        void RefreshDownloadBubble()
        {
            var m = dlBubble;
            if (m == null || m.IsDisposed) return;
            bool changed = false;
            foreach (var e in m.Items)
                if (e.Tag is Downloads.Item d)
                {
                    string st = StatusText(d);
                    if (st != e.Shortcut) { e.Shortcut = st; changed = true; }
                }
            if (changed) m.Invalidate();
        }

        string WebUrl => Current?.Created == true && (Current.View.CoreWebView2.Source.StartsWith("http://") || Current.View.CoreWebView2.Source.StartsWith("https://"))
            ? Current.View.CoreWebView2.Source : null;

        /// <summary>Ctrl+S: the engine's own "Save as" dialog (complete page, HTML only, or single file MHTML).</summary>
        void SavePageAs() { if (WebUrl != null) _ = Current.View.CoreWebView2.ShowSaveAsUIAsync(); }

        void ViewSource() { if (WebUrl != null) NewTab("view-source:" + WebUrl); }

        void CopyLink()
        {
            if (WebUrl == null) return;
            try { Clipboard.SetText(WebUrl); } catch { return; }
            Notice("Link copied");
        }

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

        CoreWebView2Profile Profile => Tabs.FirstOrDefault(t => t.Created && !t.Private)?.View.CoreWebView2.Profile;
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
            await RefreshPinned();
        }

        readonly HashSet<string> enabledExtensions = new HashSet<string>();

        void OnExtensionsChanged()
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired) { BeginInvoke((Action)OnExtensionsChanged); return; }
            _ = RefreshPinned();
        }

        int ExtIconPx => (int)Math.Round(16 * DeviceDpi / 96f);

        /// <summary>Badge text / colours / title of the pinned extensions, for the page in the current tab.</summary>
        // Every read of an extension's worker counts as activity and restarts its 30 s idle timer (measured 21-11-2026:
        // reads every 1.5 s kept a worker running for good; without reads it slept after 30-40 s like in Chrome). So the
        // worker is read every 1.5 s only for 10 s after something that changes badges (tab switch, a page finished
        // loading, an open popup), otherwise once per 45 s, longer than the idle time, so idle workers still go to sleep.
        DateTime badgeBurstUntil, badgeSlowNext;
        string badgeKey;

        async Task RefreshBadges(bool force = false)
        {
            if (toolbar.Actions.Count == 0 || WindowState == FormWindowState.Minimized) return;
            var t = Current;
            if (t?.Created != true) return;
            var now = DateTime.UtcNow;
            string key = t.GetHashCode() + "|" + t.View.CoreWebView2.Source + "|" + t.Loading;
            bool popupOpen = lastPopup != null && !lastPopup.IsDisposed;
            if (key != badgeKey || popupOpen) { badgeKey = key; badgeBurstUntil = now.AddSeconds(10); }
            if (!force && now > badgeBurstUntil && now < badgeSlowNext) return;
            if (now > badgeBurstUntil) badgeSlowNext = now.AddSeconds(45);
            Dictionary<string, ExtensionBadges.State> st;
            // an open popup is read too (it may set the badge while the extension's worker sleeps)
            if (lastPopup != null && !lastPopup.IsDisposed && lastPopup.Core != null)
                await ExtensionBadges.ReadPage(lastPopup.Core, lastPopup.ExtensionId, t.View.CoreWebView2.Source);
            try { st = await ExtensionBadges.Read(t.View.CoreWebView2, toolbar.Actions.Select(a => a.Id).ToList(), t.View.CoreWebView2.Source); }
            catch { return; }
            if (IsDisposed) return;
            bool changed = false;
            foreach (var a in toolbar.Actions)
            {
                if (!st.TryGetValue(a.Id, out var s)) continue;
                string title = string.IsNullOrEmpty(s.Title) ? null : s.Title;
                if (a.Badge != s.Text || a.BadgeBack != s.Back || a.BadgeFore != s.Fore || a.Title != title)
                { a.Badge = s.Text; a.BadgeBack = s.Back; a.BadgeFore = s.Fore; a.Title = title; changed = true; }
            }
            if (changed) toolbar.Invalidate();
        }

        /// <summary>Pinned extensions on the toolbar (Chrome: left of the extensions button, in the user's order).</summary>
        async Task RefreshPinned()
        {
            var profile = Profile;
            if (profile == null) return;
            IReadOnlyList<CoreWebView2BrowserExtension> list;
            try { list = await profile.GetBrowserExtensionsAsync(); } catch { return; }
            if (IsDisposed) return;
            Extensions.SyncPins(list.Select(e => e.Id));
            enabledExtensions.Clear();
            foreach (var e in list) if (e.IsEnabled && Extensions.Index.ContainsKey(e.Id)) enabledExtensions.Add(e.Id);
            foreach (var a in toolbar.Actions) a.Icon?.Dispose();
            toolbar.Actions.Clear();
            foreach (var id in Store.Settings.PinnedExtensions ?? new List<string>())
            {
                var e = list.FirstOrDefault(x => x.Id == id);
                if (e == null || !enabledExtensions.Contains(id)) continue;
                toolbar.Actions.Add(new Toolbar.ExtAction { Id = id, Name = e.Name, Icon = Extensions.IconOrLetter(id, e.Name, ExtIconPx) });
            }
            toolbar.LayoutNow();
        }

        /// <summary>Where an extension's popup opens: under its pinned icon, else under the extensions button (Chrome).</summary>
        Rectangle ExtAnchor(string id)
        {
            var r = toolbar.ActionRect(id);
            if (r.IsEmpty) r = toolbar.ButtonRect("ext");
            return toolbar.RectangleToScreen(r);
        }

        /// <summary>Clicking an extension (toolbar icon, menu row, shortcut): its popup, else its options page, else its menu.</summary>
        void OpenExtension(string id, Rectangle anchorScreen)
        {
            var man = Extensions.Manifest(id);
            string popup = Extensions.PopupPage(man), options = Extensions.OptionsPage(man);
            if (popup != null) ShowExtensionPopup(id, popup, anchorScreen);
            else if (options != null) NewTab("chrome-extension://" + id + "/" + options);
            else ShowExtensionContextMenu(id, anchorScreen);
        }

        /// <summary>The extension's own menu (Chrome: right-click on its toolbar icon, or its "More options" button).</summary>
        async void ShowExtensionContextMenu(string id, Rectangle anchorScreen)
        {
            var profile = Profile;
            if (profile == null) return;
            string name = id;
            try { name = (await profile.GetBrowserExtensionsAsync()).FirstOrDefault(x => x.Id == id)?.Name ?? id; } catch { }
            var man = Extensions.Manifest(id);
            var d = Extensions.Details(id);
            string home = d != null ? d["homepage"] as string : null;
            var m = NewMenu();
            var top = m.Add(name, IconData.Extension, null, string.IsNullOrEmpty(home) ? (Action)(() => OpenExtensionDetails(id)) : () => NewTab(home));
            top.Image = Extensions.IconOrLetter(id, name, ExtIconPx);
            string url = Current?.Created == true ? Current.View.CoreWebView2.Source : Current?.Url;
            m.AddNote(url != null && Extensions.CanAccess(id, url) ? "Can read and change this site" : "Can't read or change this site");
            m.AddSeparator();
            string options = Extensions.OptionsPage(man);
            if (options != null) m.Add("Options", IconData.Settings, null, () => NewTab("chrome-extension://" + id + "/" + options));
            m.Add("Remove from GBrowser...", IconData.Delete, null, () => AskRemoveExtension(id, name));
            bool pinned = Extensions.IsPinned(id);
            m.Add(pinned ? "Unpin" : "Pin", pinned ? IconData.Unpin : IconData.Pin, null, () => { Extensions.SetPinned(id, !pinned); Extensions.RaiseChanged(); });
            m.Add("Manage extension", IconData.Extension, null, () => OpenExtensionDetails(id));
            m.ShowAt(toolbar, toolbar.RectangleToClient(anchorScreen), true, "extmenu:" + id);
        }

        async void AskRemoveExtension(string id, string name)
        {
            Image img = Extensions.Icon(id, (int)Math.Round(40 * DeviceDpi / 96f));
            bool ok;
            try { ok = MaterialDialog.Ask(this, "Remove \"" + name + "\"?", img, new List<Tuple<string, string>>(), "Remove"); }
            finally { img?.Dispose(); }
            if (!ok || Profile == null) return;
            try { await Extensions.Remove(Profile, id); } catch (Exception ex) { MaterialDialog.Info(this, "Not removed", ex.Message); }
            Extensions.RaiseChanged();
        }

        /// <summary>gbrowser://extensions/?id=... (Chrome's details page of one extension).</summary>
        void OpenExtensionDetails(string id)
        {
            string url = Pages.Url("extensions") + "?id=" + id;
            int existing = Tabs.FindIndex(x => x.Url != null && Pages.PageName(x.Url) == "extensions");
            if (existing >= 0) { Select(existing); Tabs[existing].Navigate(url); return; }
            if (Current != null && Pages.PageName(Current.Url) == "newtab") Current.Navigate(url);
            else NewTab(url);
        }

        async void ShowExtensionsMenu()
        {
            var m = NewMenu();
            var profile = Profile;
            if (profile != null)
            {
                IReadOnlyList<CoreWebView2BrowserExtension> list = null;
                try { list = await profile.GetBrowserExtensionsAsync(); } catch { }
                m.AddHeader("Extensions", IconData.Extension);
                int before = m.Items.Count;
                // Chrome: every turned-on extension, by name; the row opens it, then Pin and More options buttons
                foreach (var ext in (list ?? new List<CoreWebView2BrowserExtension>()).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    if (!ext.IsEnabled || !Extensions.Index.ContainsKey(ext.Id)) continue;
                    string id = ext.Id;
                    bool pinned = Extensions.IsPinned(id);
                    var pin = new MaterialMenu.RowButton { Icon = pinned ? IconData.PinFill : IconData.Pin, Tip = pinned ? "Unpin" : "Pin", KeepOpen = true };
                    pin.Click = () =>
                    {
                        bool now = !Extensions.IsPinned(id);
                        Extensions.SetPinned(id, now);
                        pin.Icon = now ? IconData.PinFill : IconData.Pin;
                        pin.Tip = now ? "Unpin" : "Pin";
                        Extensions.RaiseChanged();
                    };
                    var more = new MaterialMenu.RowButton { Icon = IconData.More, Tip = "More options" };
                    more.Click = () => ShowExtensionContextMenu(id, ExtAnchor(id));
                    var it = m.AddButtons(ext.Name, IconData.Extension, pin, more);
                    it.Image = Extensions.IconOrLetter(id, ext.Name, ExtIconPx);
                    it.Click = () => OpenExtension(id, ExtAnchor(id));
                }
                if (m.Items.Count == before) m.AddNote("No extensions");
            }
            m.AddSeparator();
            m.Add("Chrome Web Store", IconData.OpenNew, null, () => NewTab("https://chromewebstore.google.com/category/extensions"));
            m.Add("Manage extensions", IconData.Settings, null, () => OpenPage("extensions"));
            m.ShowAt(toolbar, toolbar.ButtonRect("ext"), true, "ext");
        }

        /// <summary>Chrome's "View site information": connection state, this site's permissions (switches + reset),
        /// cookies and site data (count, delete), ad blocking on the site, and Site settings.</summary>
        async Task ShowSiteInfo()
        {
            var t = Current;
            string url = t?.Created == true ? t.View.CoreWebView2.Source : t?.Url;
            if (string.IsNullOrEmpty(url)) return;
            var m = NewMenu();
            string pn = Pages.PageName(url);
            bool standIn = pn == "crashed" || pn == "insecure" || pn == "certerror";   // pages that stand for a site
            if (Pages.IsInternal(url) && !standIn)
            {
                m.AddHeader("GBrowser page", IconData.Language);
                m.AddNote("Secure GBrowser page");
                m.ShowAt(toolbar, toolbar.SiteInfoRect, false, "siteinfo");
                return;
            }
            // the crashed / warning pages show the page they stand for
            string page = url;
            if (standIn)
                try { page = Uri.UnescapeDataString(url.Substring(url.IndexOf("?u=", StringComparison.Ordinal) + 3).Split('&')[0]); } catch { }
            Uri u;
            try { u = new Uri(page); } catch { return; }
            string host = u.Host.StartsWith("www.") ? u.Host.Substring(4) : u.Host;
            string origin = u.Scheme + "://" + u.Authority;
            bool secure = u.Scheme == "https" && Pages.PageName(url) != "insecure" && Pages.PageName(url) != "certerror";
            m.AddHeader(host, secure ? IconData.Lock : IconData.Warning);
            m.AddNote(secure ? "Connection is secure" : "Connection is not secure");
            if (u.Scheme == "https" && t?.Created == true && !standIn)
                m.Add(secure ? "Certificate is valid" : "Certificate", IconData.Verified, null, () => CertViewer.Show(this, t.View.CoreWebView2, origin));
            if (t?.Blocking != null && (t.Blocking.Camera > 0 || t.Blocking.Microphone > 0))
                m.AddNote(t.Blocking.Camera > 0 && t.Blocking.Microphone > 0 ? "Using your camera and microphone"
                    : t.Blocking.Camera > 0 ? "Using your camera" : "Using your microphone");

            // permissions this site has a remembered choice for
            var perms = new List<CoreWebView2PermissionSetting>();
            try { if (Profile != null) perms = (await Profile.GetNonDefaultPermissionSettingsAsync()).Where(p => p.PermissionOrigin.TrimEnd('/') == origin).ToList(); } catch { }
            if (perms.Count > 0)
            {
                m.AddSeparator();
                foreach (var p in perms)
                {
                    var kind = p.PermissionKind;
                    bool on = p.PermissionState == CoreWebView2PermissionState.Allow;
                    m.AddToggle(PermissionName(kind), on, true, async () =>
                    {
                        try { await Profile.SetPermissionStateAsync(kind, origin, on ? CoreWebView2PermissionState.Deny : CoreWebView2PermissionState.Allow); } catch { }
                    });
                }
                m.Add("Reset permissions", IconData.Restore, null, async () =>
                {
                    foreach (var p in perms) try { await Profile.SetPermissionStateAsync(p.PermissionKind, origin, CoreWebView2PermissionState.Default); } catch { }
                    if (t.Created) t.View.CoreWebView2.Reload();
                });
            }

            // cookies the page uses, and deleting this site's data
            int cookies = 0;
            try { if (t.Created) cookies = (await t.View.CoreWebView2.CookieManager.GetCookiesAsync(page)).Count; } catch { }
            m.AddSeparator();
            string ckSite = Blocking.SiteOf(u.AbsoluteUri) ?? host;
            m.Add("Cookies and site data", IconData.Cookie, cookies + " in use", () => NewTab(Pages.Url("cookies") + "?site=" + Uri.EscapeDataString(ckSite)));
            m.Add("Delete site data", IconData.Delete, null, () => DeleteSiteData(t, u, host));
            if (Store.Settings.HideIdentity) m.Add("Fresh start for this site", IconData.Person, null, () => DeleteSiteData(t, u, host, true));
            var st = Store.Settings;
            string site = Blocking.SiteOf(page);
            if (site != null)
                m.AddToggle("Block ads on this site", st.AdblockEnabled && !st.AllowedSites.Contains(site), st.AdblockEnabled, () =>
                {
                    if (!st.AllowedSites.Remove(site)) st.AllowedSites.Add(site);
                    Store.SaveSettings();
                    ReloadAfterSettingChange();
                });
            if (site != null)
            {
                // Chrome's per-site settings: on = allowed
                m.AddSeparator();
                void SiteToggle(string text, List<string> list, bool listMeansOff, bool reload)
                {
                    bool inList = list.Contains(site);
                    m.AddToggle(text, listMeansOff ? !inList : inList, true, () =>
                    {
                        if (!list.Remove(site)) list.Add(site);
                        Store.SaveSettings();
                        foreach (var w in App.Windows) foreach (var x in w.Tabs) w.ApplySiteSound(x);
                        if (reload && t.Created) t.View.CoreWebView2.Reload();
                    });
                }
                SiteToggle("JavaScript", st.JsBlockedSites, true, true);
                SiteToggle("Images", st.ImagesBlockedSites, true, true);
                SiteToggle("Pop-ups and redirects", st.PopupAllowedSites, false, false);
                SiteToggle("Sound", st.SoundMutedSites, true, false);
            }
            m.Add("Site settings", IconData.Settings, null, () => OpenPage("settings"));
            m.ShowAt(toolbar, toolbar.SiteInfoRect, false, "siteinfo");
        }

        static string PermissionName(CoreWebView2PermissionKind k)
        {
            switch (k)
            {
                case CoreWebView2PermissionKind.Camera: return "Camera";
                case CoreWebView2PermissionKind.Microphone: return "Microphone";
                case CoreWebView2PermissionKind.Geolocation: return "Location";
                case CoreWebView2PermissionKind.Notifications: return "Notifications";
                case CoreWebView2PermissionKind.ClipboardRead: return "Clipboard";
                case CoreWebView2PermissionKind.MultipleAutomaticDownloads: return "Automatic downloads";
                case CoreWebView2PermissionKind.FileReadWrite: return "File editing";
                case CoreWebView2PermissionKind.Autoplay: return "Sound and autoplay";
                case CoreWebView2PermissionKind.LocalFonts: return "Fonts";
                case CoreWebView2PermissionKind.MidiSystemExclusiveMessages: return "MIDI devices";
                case CoreWebView2PermissionKind.WindowManagement: return "Window management";
                case CoreWebView2PermissionKind.OtherSensors: return "Motion sensors";
                default: return k.ToString();
            }
        }

        /// <summary>Deletes the site's cookies (its own domain and subdomains) and stored data (local storage,
        /// IndexedDB, caches, service workers), then reloads, like Chrome's "Delete data" in site information.</summary>
        async void DeleteSiteData(BrowserTab t, Uri u, string host, bool fresh = false)
        {
            if (fresh)
            {
                if (!MaterialDialog.Ask(this, "Fresh start for this site?", null, new[] { Tuple.Create(IconData.Person,
                        host + " sees a new browser: its cookies and stored data are cleared and it gets new identity values. Other sites are not touched.") }, "Fresh start"))
                    return;
                Identity.NewForSite(Blocking.SiteOf(u.AbsoluteUri) ?? host);
            }
            else if (!MaterialDialog.Ask(this, "Delete site data?", null, new[] { Tuple.Create(IconData.Cookie, "Cookies and stored data of " + host + ". You may be signed out.") }, "Delete"))
                return;
            if (t?.Created != true) return;
            var core = t.View.CoreWebView2;
            string site = Blocking.SiteOf(u.AbsoluteUri) ?? host;
            try
            {
                foreach (var c in await core.CookieManager.GetCookiesAsync(""))
                {
                    string d = c.Domain.TrimStart('.');
                    if (d == site || d.EndsWith("." + site)) core.CookieManager.DeleteCookie(c);
                }
            }
            catch { }
            foreach (var o in new[] { u.Scheme + "://" + u.Authority, "https://" + site, "https://www." + site }.Distinct())
                try
                {
                    await core.CallDevToolsProtocolMethodAsync("Storage.clearDataForOrigin", Store.ToJson(new Dictionary<string, object> {
                        { "origin", o }, { "storageTypes", "local_storage,indexeddb,websql,cache_storage,service_workers,file_systems,shader_cache" } }));
                }
                catch { }
            core.Reload();
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
            m.AddHeader(head, IconData.Shield);
            m.AddSeparator();
            void Toggle(string text, bool on, Action<bool> set, bool enabled = true) =>
                m.AddToggle(text, on, enabled, () => { set(!on); Store.SaveSettings(); ReloadAfterSettingChange(); });
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
            Toggle("Block pages of ad and tracker sites", st.BlockAdSitePages, v => st.BlockAdSitePages = v, st.AdblockEnabled);
            m.AddSeparator();
            m.AddNote("Total blocked: " + st.TotalBlocked.ToString("N0"));
            if (site != null && st.AdblockEnabled) m.Add("Hide an element on this page", IconData.Block, null, () => PickElement(null));
            if (t != null) m.Add("Request log", IconData.Find, null, () => NewTab(Pages.Url("log") + "?tab=" + t.Id));
            if (st.HideIdentity) m.Add("New identity", IconData.Person, "Identity " + Identity.Id, () => NewIdentity());
            if (t != null && site != null) m.Add("Identity check", IconData.Verified, null, () => NewTab(Pages.Url("identity") + "?tab=" + t.Id));
            m.Add("Blocking tests", IconData.Verified, null, () => OpenPage("tests"));
            m.Add("Ad blocking settings", IconData.Settings, null, () => OpenPage("settings"));
            m.ShowAt(toolbar, toolbar.ButtonRect("shield"), true, "shield");
        }

        /// <summary>Blocking settings apply to new requests; reload the current page so it shows the change.</summary>
        void ReloadAfterSettingChange()
        {
            UpdateUi();
            if (Current?.Created == true && !Pages.IsInternal(Current.View.CoreWebView2.Source)) Current.View.CoreWebView2.Reload();
        }

        void ErrorAction(string actUri)
        {
            string q = actUri.Substring(actUri.IndexOf('?') + 1), what = null, url = null;
            foreach (var part in q.Split('&'))
            {
                int eq = part.IndexOf('=');
                if (eq < 0) continue;
                string k = part.Substring(0, eq), v = Uri.UnescapeDataString(part.Substring(eq + 1));
                if (k == "do") what = v; else if (k == "u") url = v;
            }
            switch (what)
            {
                case "netdiag":
                    // Windows Network Diagnostics shows its own window and waits: run it off the UI thread
                    IntPtr owner = Handle;
                    var th = new System.Threading.Thread(() =>
                    {
                        if (!NetDiag.WindowsDiagnostics(url ?? "", owner))
                            try { Process.Start(new ProcessStartInfo("ms-settings:troubleshoot") { UseShellExecute = true }); } catch { }
                    });
                    th.SetApartmentState(System.Threading.ApartmentState.STA);
                    th.IsBackground = true;
                    th.Start();
                    break;
                case "netsettings":
                    try { Process.Start(new ProcessStartInfo("ms-settings:network-status") { UseShellExecute = true }); } catch { }
                    break;
                case "dnsauto":
                    if (MaterialDialog.Ask(this, "Use automatic DNS?", null, new[] {
                            Tuple.Create(IconData.Language, "Secure DNS is blocked on this network. GBrowser will use the network's DNS (encrypted when it supports it) and restart.") },
                            "Use automatic DNS"))
                    {
                        Store.Settings.SecureDns = "off";
                        Store.SaveSettings();
                        App.Quit(true);
                    }
                    break;
            }
        }

        /// <summary>Install prompt with what the extension can access (like Chrome's).</summary>
        bool ConfirmExtension(string name, List<string> access, string iconFile)
        {
            Image img = null;
            try { if (iconFile != null) using (var i = Image.FromFile(iconFile)) img = Extensions.Scale(i, (int)Math.Round(40 * DeviceDpi / 96f)); } catch { }
            var lines = new List<Tuple<string, string>>();
            if (access.Count == 0) lines.Add(Tuple.Create(IconData.Verified, "No special access"));
            else lines.Add(Tuple.Create((string)null, "It can:"));
            foreach (var a in access) lines.Add(Tuple.Create(a.StartsWith("Read and change all") ? IconData.Warning : IconData.Policy, a));
            try { return MaterialDialog.Ask(this, "Add \"" + name + "\"?", img, lines, "Add extension"); }
            finally { img?.Dispose(); }
        }

        void ShowExtensionPopup(string id, string page, Rectangle anchorScreen, bool fromTest = false)
        {
            // Chrome: clicking the icon of the open popup closes it (focus leaves the popup first, so look at that too)
            if (lastPopup != null && !lastPopup.IsDisposed && lastPopup.ExtensionId == id) { lastPopup.Close(); return; }
            if (!fromTest && ExtensionPopup.JustClosed(id)) return;
            if (lastPopup != null && !lastPopup.IsDisposed) lastPopup.Close();
            lastPopup = new ExtensionPopup(env, id, "chrome-extension://" + id + "/" + page, anchorScreen, url => NewTab(url)) { NoActivate = fromTest };
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
                var ext = await Extensions.InstallFromStore(core.Profile, id, core.Settings.UserAgent, env.BrowserVersionString, ConfirmExtension);
                toolbar.StoreChipText = ext == null ? "Add to GBrowser" : "Installed";
                if (ext != null) Extensions.RaiseChanged();
            }
            catch (Exception ex)
            {
                toolbar.StoreChipText = "Add to GBrowser";
                MaterialDialog.Info(this, "Not installed", ex.Message);
            }
            toolbar.LayoutNow();
        }

        // ---------- keyboard ----------

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData) => HandleShortcut(keyData) || base.ProcessCmdKey(ref msg, keyData);

        /// <summary>GBrowser's keyboard shortcuts: from the window (ProcessCmdKey) and from every tab's page (the WebView2
        /// control's KeyDown, raised for the engine's accelerator keys). True when the key was used.</summary>
        internal bool HandleShortcut(Keys keyData)
        {
#if AUTOMATION
            // test builds: which shortcut keys reach GBrowser (also while a page has focus), for the extension-commands check
            if ((keyData & (Keys.Control | Keys.Alt)) != 0) Blocking.TestLog.Add("KEY " + DateTime.Now.ToString("HH:mm:ss") + " " + keyData);
#endif
            var extKey = Store.Settings.ExtensionShortcuts.Count > 0 ? Extensions.KeyText(keyData) : null;
            if (extKey != null)
                foreach (var kv in Store.Settings.ExtensionShortcuts)
                    if (kv.Value == extKey && enabledExtensions.Contains(kv.Key)) { OpenExtension(kv.Key, ExtAnchor(kv.Key)); return true; }
            switch (keyData)
            {
                case Keys.F12:
                case Keys.Control | Keys.Shift | Keys.I: ToggleDevTools(); return true;
                case Keys.Control | Keys.Shift | Keys.J: OpenDevTools("console"); return true;
                case Keys.Control | Keys.Shift | Keys.C: OpenDevTools("inspect"); return true;
                case Keys.Control | Keys.T: NewTab(null); return true;
                case Keys.Control | Keys.N: App.NewWindow(null); return true;
                case Keys.Control | Keys.Shift | Keys.W: Close(); return true;
                case Keys.Control | Keys.F: ShowFind(); return true;
                case Keys.F3:
                case Keys.Control | Keys.G: FindStep(true); return true;
                case Keys.Shift | Keys.F3:
                case Keys.Control | Keys.Shift | Keys.G: FindStep(false); return true;
                case Keys.Control | Keys.W:
                case Keys.Control | Keys.F4: CloseTab(strip.Selected); return true;
                case Keys.Control | Keys.Shift | Keys.T: ReopenClosed(); return true;
                case Keys.Control | Keys.Shift | Keys.N: NewPrivateTab(null); return true;
                case Keys.Control | Keys.Shift | Keys.A: ShowTabSearch(); return true;
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
                case Keys.Control | Keys.S: SavePageAs(); return true;
                case Keys.Control | Keys.U: ViewSource(); return true;
                case Keys.Shift | Keys.Escape: OpenPage("tasks"); return true;
                case Keys.Alt | Keys.Left: toolbar_Back(); return true;
                case Keys.Alt | Keys.Right: toolbar_Forward(); return true;
                case Keys.Alt | Keys.Home: Current?.Navigate(Search.ToUrl(Store.Settings.HomePage) ?? Pages.Url("newtab")); return true;
                case Keys.F5:
                case Keys.Control | Keys.R: if (Current?.Created == true) Current.View.CoreWebView2.Reload(); return true;
                case Keys.F11: SetFullscreen(!fullscreen); return true;
                case Keys.Escape:
                    if (findBar.Visible && findBar.ContainsFocus) { CloseFind(true); return true; }
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
            return false;
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
        /// <summary>The "ids" list of a page command (numbers).</summary>
        static List<long> Ids(Dictionary<string, object> m) =>
            m.TryGetValue("ids", out var v) && v is System.Collections.IEnumerable e && !(v is string)
                ? e.Cast<object>().Select(o => o is IConvertible c ? Convert.ToInt64(c, System.Globalization.CultureInfo.InvariantCulture) : 0).Where(x => x > 0).ToList()
                : new List<long>();
        static long Lng(Dictionary<string, object> m, string k, long def) =>
            m.TryGetValue(k, out var v) && v != null && long.TryParse(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture), out var n) ? n : def;

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
                    foreach (var b in Store.Bookmarks.Where(x => !x.Folder && x.Parent == Bm.Bar).Take(8)) tiles.Add(new Dictionary<string, object> { { "url", b.Url }, { "title", b.Title } });
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
                case "bookmarks.list": return Store.Bookmarks.Where(b => !b.Folder).ToList();
                case "bookmarks.tree":
                    return new Dictionary<string, object>
                    {
                        { "roots", new[] { new Dictionary<string, object> { { "Id", Bm.Bar }, { "Title", Bm.RootName(Bm.Bar) } },
                                           new Dictionary<string, object> { { "Id", Bm.Other }, { "Title", Bm.RootName(Bm.Other) } } } },
                        { "items", Store.Bookmarks },
                    };
                case "bookmarks.add":
                {
                    string u = Search.ToUrl(Str(m, "url") ?? "");
                    if (u == null) throw new Exception("Not a valid address");
                    Bm.Add(u, Str(m, "title"), Lng(m, "parent", Bm.Bar), (int)Lng(m, "index", -1));
                    return null;
                }
                case "bookmarks.addFolder": Bm.AddFolder(Str(m, "title"), Lng(m, "parent", Bm.Bar), (int)Lng(m, "index", -1)); return null;
                case "bookmarks.edit":
                {
                    var nu = Str(m, "url");
                    Bm.Edit(Lng(m, "id", 0), Str(m, "title"), string.IsNullOrWhiteSpace(nu) ? null : (Search.ToUrl(nu) ?? nu));
                    return null;
                }
                case "bookmarks.move": if (!Bm.Move(Lng(m, "id", 0), Lng(m, "parent", Bm.Bar), (int)Lng(m, "index", -1))) throw new Exception("Cannot move there"); return null;
                case "bookmarks.delete": return Bm.RemoveMany(new[] { Lng(m, "id", 0) });
                case "bookmarks.deleteMany": return Bm.RemoveMany(Ids(m));
                case "bookmarks.undo": return Bm.UndoRemove();
                case "bookmarks.moveMany": return Bm.MoveMany(Ids(m), Lng(m, "parent", Bm.Bar), (int)Lng(m, "index", -1));
                case "bookmarks.copyMany": return Bm.CopyMany(Ids(m), Lng(m, "parent", Bm.Bar), (int)Lng(m, "index", -1));
                case "bookmarks.sort": Bm.Sort(Lng(m, "parent", Bm.Bar)); return null;
                case "bookmarks.open":
                {
                    // Chrome's Open all / Open in new tab / new window / Incognito / new tab group
                    var urls = (m.TryGetValue("urls", out var uv2) && uv2 is System.Collections.IEnumerable ua && !(uv2 is string)
                        ? ua.Cast<object>().OfType<string>() : Enumerable.Empty<string>())
                        .Where(u => Uri.TryCreate(u, UriKind.Absolute, out var uu) && uu.Scheme != "javascript").Take(500).ToList();
                    if (urls.Count == 0) return 0;
                    string mode = Str(m, "mode") ?? "tab";
                    if (mode == "window") App.NewWindow(urls);
                    else if (mode == "private") foreach (var u in urls) NewPrivateTab(u);
                    else if (mode == "group")
                    {
                        // the group is made after its tabs exist: the tab row drops groups that have no tabs when a tab opens
                        string gid = Guid.NewGuid().ToString("N").Substring(0, 8);
                        int color = Enumerable.Range(0, 9).FirstOrDefault(c => !strip.Groups.Values.Any(x => x.Color == c));
                        var opened = new List<BrowserTab>();
                        bool first = true;
                        foreach (var u in urls) { opened.Add(NewTab(u, first)); first = false; }
                        strip.Groups[gid] = new TabGroup { Color = color, Name = Str(m, "name") ?? "" };
                        foreach (var nt in opened) nt.Group = gid;
                        strip.RefreshTabs();
                    }
                    else foreach (var u in urls) NewTab(u, false);
                    return urls.Count;
                }
                case "bookmarks.export":
                {
                    using (var d = new SaveFileDialog { Filter = "Bookmarks (*.html)|*.html", FileName = "bookmarks_" + DateTime.Now.ToString("dd_MM_yyyy") + ".html" })
                    {
                        if (d.ShowDialog(this) != DialogResult.OK) return false;
                        File.WriteAllText(d.FileName, Bm.ExportHtml(), new System.Text.UTF8Encoding(false));
                        return true;
                    }
                }
                case "bookmarks.importFile":
                {
                    using (var d = new OpenFileDialog { Filter = "Bookmarks (*.html;*.htm)|*.html;*.htm" })
                    {
                        if (d.ShowDialog(this) != DialogResult.OK) return -1;
                        return Bm.ImportHtml(File.ReadAllText(d.FileName), "file");
                    }
                }
                case "bookmarks.browsers":
                    return Bm.OtherBrowsers().Select((b, i) => new Dictionary<string, object> { { "i", i }, { "browser", b.Item1 }, { "profile", b.Item2 }, { "count", Bm.CountChromium(b.Item3) } }).ToList();
                case "bookmarks.importBrowser":
                {
                    var list = Bm.OtherBrowsers();
                    int i = (int)Lng(m, "i", -1);
                    if (i < 0 || i >= list.Count) throw new Exception("Not found");
                    // the profile folder: its account bookmarks and its local bookmarks (read shared: the browser may be running)
                    return Bm.ImportProfile(list[i].Item3, list[i].Item1);
                }
                case "favicons":
                {
                    // site icons for a page's list (one per site): { "<site key>": "data:image/png;base64,..." }
                    var o = new Dictionary<string, object>();
                    if (m.TryGetValue("urls", out var uv) && uv is System.Collections.IEnumerable arr && !(uv is string))
                        foreach (var u in arr.Cast<object>().OfType<string>().Take(1000))
                        {
                            string k = Favicons.KeyOf(u);
                            if (k == null || o.ContainsKey(k)) continue;
                            var d = Favicons.DataUrl(u);
                            if (d != null) o[k] = d;
                        }
                    return o;
                }
                case "bookmarks.remove":
                    foreach (var x in Store.Bookmarks.Where(x => !x.Folder && x.Url == Str(m, "url")).ToList()) Bm.Remove(x.Id);
                    return null;
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
                    Favicons.ClearExcept(Store.Bookmarks.Where(b => !b.Folder).Select(b => b.Url));
                    if (Profile != null) await Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.BrowsingHistory);
                    return null;
                case "default.info":
                    return new Dictionary<string, object>
                    {
                        { "isDefault", DefaultBrowser.IsDefault() },
                        { "registered", string.Equals(DefaultBrowser.RegisteredExe(DefaultRoot), Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) },
                    };
                case "default.make":
                    // registers GBrowser as a browser for this Windows user and opens Settings > Default apps on its page
                    DefaultBrowser.Register(Application.ExecutablePath, DefaultRoot);
#if !AUTOMATION
                    DefaultBrowser.OpenWindowsSettings();
#endif
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
                        case "ThemeColor":
                        {
                            string tc = (v as string ?? "").Trim();
                            if (tc == "chrome" || tc == "accent" || System.Text.RegularExpressions.Regex.IsMatch(tc, "^#[0-9a-fA-F]{6}$")) st.ThemeColor = tc.ToLowerInvariant();
                            else throw new Exception("Bad colour");
                            Store.SaveSettings();
                            ApplyTheme(true);
                            break;
                        }
                        case "ThemeMode":
                            if (v is string tm && (tm == "system" || tm == "light" || tm == "dark")) st.ThemeMode = tm;
                            Store.SaveSettings();
                            ApplyTheme(true);
                            break;
                        case "RestoreTabs": st.RestoreTabs = v is bool rb && rb; break;
                        case "SleepMinutes": st.SleepMinutes = Math.Max(0, Math.Min(240, Convert.ToInt32(v))); break;
                        case "LowMemoryHiddenTabs": st.LowMemoryHiddenTabs = v is bool lb && lb; break;
                        case "SkipSiteDebugger":
                            st.SkipSiteDebugger = v is bool sd && sd;
                            Store.SaveSettings();
                            foreach (var w in App.Windows) foreach (var tt in w.Tabs.Where(x => x.Created)) { _ = DevToolsGuard.ApplyAll(tt.View.CoreWebView2); break; }
                            break;
                        case "ShowHomeButton": st.ShowHomeButton = v is bool hb && hb; UpdateUi(); break;
                        case "ShowBookmarksBar": st.ShowBookmarksBar = v is bool sb && sb; bookmarksBar.Visible = st.ShowBookmarksBar && !fullscreen; break;
                        case "AdblockEnabled": st.AdblockEnabled = v is bool a1 && a1; break;
                        case "BlockAnnoyances": st.BlockAnnoyances = v is bool a2 && a2; break;
                        case "BlockTrackers": st.BlockTrackers = v is bool a3 && a3; break;
                        case "HideAdLeftovers": st.HideAdLeftovers = v is bool a4 && a4; break;
                        case "Stealth": st.Stealth = v is bool a5 && a5; break;
                        case "BlockAdSitePages": st.BlockAdSitePages = v is bool a6 && a6; break;
                        case "DoNotTrack": st.DoNotTrack = v is bool a7 && a7; break;
                        case "HttpsFirst": st.HttpsFirst = v is bool s1 && s1; break;
                        case "ClearOnExit": st.ClearOnExit = v is bool s2 && s2; break;
                        case "HostListUpdates": st.HostListUpdates = v is bool hu && hu; break;
                        case "Languages":
                        {
                            var list = new List<string>();
                            if (v is System.Collections.IEnumerable en && !(v is string))
                                foreach (var o in en) if (o is string code && code.Length >= 2 && code.Length <= 12 && !list.Contains(code)) list.Add(code);
                            st.Languages = list.Count > 0 ? list : null;
                            break;
                        }
                        case "FontSize":
                        case "FontStandard":
                        case "FontSerif":
                        case "FontSans":
                        case "FontFixed":
                        {
                            if (key == "FontSize") { int fs = Convert.ToInt32(v); if (fs >= 6 && fs <= 72) st.FontSize = fs; }
                            else
                            {
                                string fv = v as string;
                                if (string.IsNullOrWhiteSpace(fv)) fv = null;
                                if (key == "FontStandard") st.FontStandard = fv; else if (key == "FontSerif") st.FontSerif = fv;
                                else if (key == "FontSans") st.FontSans = fv; else st.FontFixed = fv;
                            }
                            foreach (var w in App.Windows) foreach (var x in w.Tabs) if (x.Created) _ = Fonts.Apply(x.View.CoreWebView2, true);
                            break;
                        }
                        case "DefaultZoom":
                            if (double.TryParse(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture), System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out var dzv) && dzv >= 0.25 && dzv <= 5)
                            {
                                st.DefaultZoom = dzv;
                                foreach (var w in App.Windows) foreach (var tb in w.Tabs) w.ApplySiteZoom(tb);
                            }
                            break;
                        case "HideIdentity": st.HideIdentity = v is bool hi && hi; if (st.HideIdentity && st.IdentitySeed == 0) Identity.NewSeed(); break;
                        case "IdentityNoise": st.IdentityNoise = v is bool inz && inz; break;
                        case "IdentityEachStart": st.IdentityEachStart = v is bool ies && ies; break;
                        case "AskWhereToSave": st.AskWhereToSave = v is bool aw && aw; break;
                        case "SearchSuggestions": st.SearchSuggestions = v is bool ss && ss; break;
                        case "DownloadConnections": st.DownloadConnections = Math.Max(1, Math.Min(16, Convert.ToInt32(v))); break;
                        case "DownloadSpeedLimit": st.DownloadSpeedLimit = Math.Max(0, Math.Min(1024 * 1024, Convert.ToInt32(v))); break;
                        case "DownloadConflict": if (v is string dc && (dc == "ask" || dc == "keep" || dc == "replace")) st.DownloadConflict = dc; break;
                        case "SecureDns":
                        {
                            string d = (v as string ?? "").Trim();
                            if (d != "off" && Security.DnsTemplate(d) == null) throw new Exception(d.StartsWith("custom:") ? "Enter an https:// address" : "Unknown provider");
                            st.SecureDns = d;
                            break;
                        }
                        case "SmartScreen":
                            st.SmartScreen = v is bool s3 && s3;
                            foreach (var x in Tabs.Where(x => x.Created)) ApplySecurity(x.View.CoreWebView2);
                            break;
                        case "TrackingPrevention":
                            if (v is string tp && (tp == "basic" || tp == "balanced" || tp == "strict")) st.TrackingPrevention = tp;
                            foreach (var x in Tabs.Where(x => x.Created)) ApplySecurity(x.View.CoreWebView2);
                            break;
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
                            case "history": flags |= CoreWebView2BrowsingDataKinds.BrowsingHistory | CoreWebView2BrowsingDataKinds.DownloadHistory; Store.History.Clear(); Store.SaveHistory(); Favicons.ClearExcept(Store.Bookmarks.Where(b => !b.Folder).Select(b => b.Url)); break;
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
                case "update.status": return UpdateInfo();
                case "update.check": await Updater.Check(); return UpdateInfo();
                case "update.install": RelaunchToUpdate(); return null;
                case "permissions.list":
                {
                    if (Profile == null) return new List<object>();
                    var list = await Profile.GetNonDefaultPermissionSettingsAsync();
                    return list.Select(p => new Dictionary<string, object> {
                        { "kind", p.PermissionKind.ToString() }, { "what", PermissionPrompt.What(p.PermissionKind) },
                        { "origin", p.PermissionOrigin }, { "state", p.PermissionState.ToString() } }).ToList();
                }
                case "permissions.reset":
                {
                    if (Profile == null) return null;
                    var kind = (CoreWebView2PermissionKind)Enum.Parse(typeof(CoreWebView2PermissionKind), Str(m, "kind"));
                    await Profile.SetPermissionStateAsync(kind, Str(m, "origin"), CoreWebView2PermissionState.Default);
                    return null;
                }
                case "openDataDir":
                    Process.Start("explorer.exe", "\"" + Store.DataDir + "\""); return null;
                case "memory": return MemoryReport();
                case "theme.vars": return Theme.CssVars();
                case "dns.info":
                    return new Dictionary<string, object>
                    {
                        { "setting", st.SecureDns ?? "off" }, { "active", Security.ActiveSecureDns ?? "off" },
                        { "providers", Security.DnsProviders.Select(p => new Dictionary<string, object> { { "id", p[0] }, { "name", p[1] } }).ToList() },
                    };
                case "app.restart":
                    BeginInvoke((Action)(() => App.Quit(true)));
                    return null;
                case "theme.info":
                {
                    var acc = Palette.WindowsAccent();
                    return new Dictionary<string, object>
                    {
                        { "color", st.ThemeColor ?? "chrome" }, { "accent", acc.HasValue ? Palette.Hex(acc.Value) : "" },
                        { "presets", Palette.Presets.Select(p => new Dictionary<string, object> {
                            { "id", p[0] }, { "name", p[1] }, { "hex", p.Length > 2 ? p[2] : p[0] } }).ToList() },
                    };
                }
                case "downloads.list":
                    return Downloads.All.Select(d => new Dictionary<string, object> {
                        { "id", d.Id }, { "url", d.Url }, { "path", d.Path }, { "name", Path.GetFileName(d.Path ?? "") },
                        { "total", d.Total }, { "received", d.Received }, { "state", d.State }, { "reason", d.Reason ?? "" },
                        { "time", d.Time }, { "exists", !string.IsNullOrEmpty(d.Path) && File.Exists(d.Path) },
                        { "live", d.Op != null || (d.Engine == "multi" && (d.State == "in_progress" || d.State == "paused")) }, { "multi", d.Engine == "multi" },
                        { "replaces", d.ReplaceTarget != null ? Path.GetFileName(d.ReplaceTarget) : "" },
                        { "speed", d.Fast != null ? d.Fast.Speed : d.State == "in_progress" ? d.EngineSpeed : 0 }, { "connections", d.Fast != null ? d.Fast.Connections : 0 },
                        { "segments", d.Fast != null ? d.Fast.Segments() : null }, { "durationMs", d.DurationMs }, { "openWhenDone", d.OpenWhenDone },
                        { "limited", d.Engine == "multi" && st.DownloadSpeedLimit > 0 },
                        { "canAlwaysOpen", Downloads.AutoOpenType(d.Path) != null },
                        { "alwaysOpen", Downloads.AutoOpenType(d.Path) is string at && st.AutoOpenTypes.Contains(at) } }).ToList();
                case "downloads.autoOpen":
                    if (m.ContainsKey("clear")) { st.AutoOpenTypes.Clear(); Store.SaveSettings(); }
                    return st.AutoOpenTypes.ToList();
                case "downloads.folder":
                    return new Dictionary<string, object> { { "path", DownloadFolder() }, { "custom", !string.IsNullOrEmpty(st.DownloadFolder) } };
                case "downloads.pick":
                {
                    string picked = FolderPicker.Pick(Handle, "Choose where downloads are saved", DownloadFolder());
                    if (picked != null) { st.DownloadFolder = picked; Store.SaveSettings(); }
                    return new Dictionary<string, object> { { "path", DownloadFolder() }, { "custom", !string.IsNullOrEmpty(st.DownloadFolder) } };
                }
                case "downloads.resetFolder":
                    st.DownloadFolder = ""; Store.SaveSettings();
                    return new Dictionary<string, object> { { "path", DownloadFolder() }, { "custom", false } };
                case "downloads.openFolder":
                    Process.Start("explorer.exe", "\"" + DownloadFolder() + "\""); return null;
                case "downloads.do":
                {
                    var d = Downloads.Find(Convert.ToInt32(m["id"]));
                    if (d == null) return null;
                    switch (Str(m, "action"))
                    {
                        case "open": if (File.Exists(d.Path)) Process.Start(new ProcessStartInfo(d.Path) { UseShellExecute = true }); break;
                        case "folder": if (File.Exists(d.Path)) Process.Start("explorer.exe", "/select,\"" + d.Path + "\""); break;
                        case "cancel": case "pause": case "resume": case "retry": case "anyway": DownloadAction(d, Str(m, "action")); break;
                        case "openwhendone": d.OpenWhenDone = !d.OpenWhenDone; Downloads.Save(); break;
                        case "alwaysopen":
                        {
                            string ty = Downloads.AutoOpenType(d.Path);
                            if (ty == null) break;
                            if (!st.AutoOpenTypes.Remove(ty)) st.AutoOpenTypes.Add(ty);
                            Store.SaveSettings();
                            break;
                        }
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
                        .Select(p => new Dictionary<string, object> { { "name", p[0] }, { "version", p[1] }, { "rules", p[2] },
                            { "group", p.Length > 3 ? p[3] : "0" }, { "file", p.Length > 4 ? p[4].Trim() : "" },
                            { "on", p.Length <= 4 || !st.DisabledLists.Contains(p[4].Trim()) } }).ToList();
                    string built = rows.FirstOrDefault(p => p.Length >= 2 && p[0] == "built")?[1] ?? "";
                    return new Dictionary<string, object>
                    {
                        { "ready", eng != null }, { "error", FilterEngine.LoadError ?? "" }, { "rules", eng?.RuleCount ?? 0 },
                        { "loadMs", FilterEngine.LoadMs }, { "built", built }, { "lists", lists },
                        { "total", st.TotalBlocked }, { "allowed", st.AllowedSites },
                    };
                }
                case "userrules.get":
                {
                    var last = CustomLists.Last;
                    return new Dictionary<string, object>
                    {
                        { "text", st.UserRules ?? "" },
                        { "problems", (last?.Problems ?? new List<FilterEngine.Problem>()).Where(p => p.Source == "")
                            .Select(p => new Dictionary<string, object> { { "line", p.Line }, { "text", p.Text }, { "reason", p.Reason } }).ToList() },
                        { "rules", last != null && last.Counts.TryGetValue("", out var n) ? n : 0 },
                    };
                }
                case "userrules.set":
                {
                    st.UserRules = (Str(m, "text") ?? "").Replace("\r\n", "\n");
                    Store.SaveSettings();
                    // your rules checked now (the full set is rebuilt in the background)
                    var eng = FilterEngine.Instance;
                    var chk = eng?.BuildUser(new[] { new FilterEngine.FilterSource { Name = "", Text = st.UserRules, Trusted = true } });
                    CustomLists.Rebuild();
                    return new Dictionary<string, object>
                    {
                        { "problems", (chk?.Problems ?? new List<FilterEngine.Problem>()).Select(p => new Dictionary<string, object> { { "line", p.Line }, { "text", p.Text }, { "reason", p.Reason } }).ToList() },
                        { "rules", chk != null && chk.Counts.TryGetValue("", out var n2) ? n2 : 0 },
                    };
                }
                case "lists.custom":
                    return st.CustomLists.Select(l => new Dictionary<string, object>
                    {
                        { "url", l.Url }, { "title", l.Title ?? l.Url }, { "trusted", l.Trusted }, { "known", CustomLists.IsKnown(l.Url) },
                        { "on", l.Enabled }, { "rules", l.Rules }, { "updated", l.Updated }, { "hours", l.Hours }, { "error", l.Error ?? "" },
                    }).ToList();
                case "lists.add":
                {
                    string err = await CustomLists.Add(Str(m, "url"), m.TryGetValue("trust", out var tv) && tv is bool tb && tb);
                    if (err != null) throw new Exception(err);
                    return null;
                }
                case "lists.remove": CustomLists.Remove(Str(m, "url")); return null;
                case "lists.update": await CustomLists.UpdateDue(true); return null;
                case "lists.trust":
                case "lists.toggle":
                {
                    var l = st.CustomLists.FirstOrDefault(x => x.Url == Str(m, "url"));
                    if (l == null) throw new Exception("Not found");
                    bool v = m.TryGetValue("on", out var ov2) && ov2 is bool ob2 && ob2;
                    if (cmd == "lists.trust") l.Trusted = v; else l.Enabled = v;
                    Store.SaveSettings();
                    CustomLists.Rebuild();
                    return null;
                }
                case "cookies.list":
                {
                    var core = tab.View.CoreWebView2;
                    var all = await Cookies.All(core);
                    var rows = new List<object>();
                    foreach (var c in all)
                    {
                        string what = Cookies.Label(c, out bool trk);
                        var d = new Dictionary<string, object>(c) { ["site"] = Cookies.SiteOf(c), ["key"] = Cookies.KeyOf(c), ["tracker"] = trk, ["what"] = what,
                            ["protected"] = st.CookieProtected.Any(p => p.Key == Cookies.KeyOf(c)),
                            ["blocked"] = st.CookieBlocks.Any(b => b.Name == (string)c["name"] && (b.Site == "*" || b.Site == Cookies.SiteOf(c))) };
                        rows.Add(d);
                    }
                    return new Dictionary<string, object>
                    {
                        { "cookies", rows }, { "rules", st.CookieRules }, { "blocks", st.CookieBlocks },
                        { "autoDelete", st.CookieAutoDelete }, { "keep", st.CookieKeep }, { "maxDays", st.CookieMaxDays },
                        { "thirdParty", st.BlockThirdPartyCookies }, { "thirdPartyActive", Cookies.ThirdPartyActive },
                    };
                }
                case "cookies.set":
                {
                    var c = m["cookie"] as Dictionary<string, object>;
                    if (c == null) throw new Exception("No cookie");
                    if (m.TryGetValue("old", out var oldo) && oldo is Dictionary<string, object> old && Cookies.KeyOf(old) != Cookies.KeyOf(c))
                        await Cookies.Delete(tab.View.CoreWebView2, old);
                    string err = await Cookies.Set(tab.View.CoreWebView2, c);
                    if (err != null) throw new Exception(err);
                    return null;
                }
                case "cookies.delete":
                {
                    if (m.TryGetValue("list", out var lo) && lo is System.Collections.IEnumerable le)
                        foreach (var x in le) if (x is Dictionary<string, object> c) await Cookies.Delete(tab.View.CoreWebView2, c);
                    return null;
                }
                case "cookies.rule":
                {
                    string site = Str(m, "site"), mode = Str(m, "mode") ?? "";
                    st.CookieRules.RemoveAll(r => r.Site == site);
                    if (mode == "allow" || mode == "block" || mode == "clear") st.CookieRules.Add(new CookieRule { Site = site, Mode = mode });
                    Store.SaveSettings();
                    if (mode == "block") Cookies.Guard(tab.View.CoreWebView2);
                    return null;
                }
                case "cookies.block":
                {
                    string name = Str(m, "name"), site = Str(m, "site") ?? "*";
                    bool on = m.TryGetValue("on", out var ov3) && ov3 is bool b3 && b3;
                    st.CookieBlocks.RemoveAll(b => b.Name == name && b.Site == site);
                    if (on) st.CookieBlocks.Add(new CookieBlock { Name = name, Site = site });
                    Store.SaveSettings();
                    if (on) Cookies.Guard(tab.View.CoreWebView2);
                    return null;
                }
                case "cookies.protect":
                {
                    var c = m["cookie"] as Dictionary<string, object>;
                    bool on = m.TryGetValue("on", out var ov4) && ov4 is bool b4 && b4;
                    string key = Cookies.KeyOf(c);
                    st.CookieProtected.RemoveAll(p => p.Key == key);
                    if (on) st.CookieProtected.Add(new ProtectedCookie { Key = key, Json = Store.ToJson(c) });
                    Store.SaveSettings();
                    return null;
                }
                case "cookies.options":
                {
                    if (m.TryGetValue("autoDelete", out var ad) && ad is bool adb) st.CookieAutoDelete = adb;
                    if (m.TryGetValue("maxDays", out var md)) st.CookieMaxDays = (int)Lng(m, "maxDays", 0);
                    if (m.TryGetValue("thirdParty", out var tp) && tp is bool tpb) st.BlockThirdPartyCookies = tpb;
                    if (m.TryGetValue("keep", out var kp) && kp is System.Collections.IEnumerable ke && !(kp is string))
                        st.CookieKeep = ke.Cast<object>().Select(x => Convert.ToString(x)).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
                    Store.SaveSettings();
                    return new Dictionary<string, object> { { "restart", st.BlockThirdPartyCookies != Cookies.ThirdPartyActive } };
                }
                case "cookies.export":
                {
                    var list = new List<Dictionary<string, object>>();
                    if (m.TryGetValue("list", out var lo2) && lo2 is System.Collections.IEnumerable le2)
                        foreach (var x in le2) if (x is Dictionary<string, object> c) list.Add(c);
                    string text = Cookies.Export(list, Str(m, "format") ?? "json");
                    if (Str(m, "to") == "clipboard") { Clipboard.SetText(text.Length == 0 ? " " : text); return true; }
                    string ext = Str(m, "format") == "netscape" ? "txt" : Str(m, "format") == "header" ? "txt" : "json";
                    using (var d = new SaveFileDialog { Filter = "Cookies (*." + ext + ")|*." + ext, FileName = "cookies_" + DateTime.Now.ToString("dd_MM_yyyy") + "." + ext })
                    {
                        if (d.ShowDialog(this) != DialogResult.OK) return false;
                        File.WriteAllText(d.FileName, text, new System.Text.UTF8Encoding(false));
                        return true;
                    }
                }
                case "cookies.parse":
                {
                    string text = Str(m, "text");
                    if (text == null)
                        using (var d = new OpenFileDialog { Filter = "Cookies (*.json;*.txt)|*.json;*.txt|All files|*.*" })
                        {
                            if (d.ShowDialog(this) != DialogResult.OK) return null;
                            text = File.ReadAllText(d.FileName);
                        }
                    return Cookies.Parse(text, Str(m, "host"));
                }
                case "cookies.snapshots": return Cookies.Snapshots().Select(x => new Dictionary<string, object> { { "name", x.Name }, { "site", x.Site }, { "time", x.Time } }).ToList();
                case "cookies.snapshot":
                {
                    string action = Str(m, "action"), name = Str(m, "name"), site = Str(m, "site");
                    var snaps = Cookies.Snapshots();
                    var core = tab.View.CoreWebView2;
                    if (action == "save")
                    {
                        var mine = (await Cookies.All(core)).Where(c => Cookies.SiteOf(c) == site).ToList();
                        snaps.RemoveAll(x => x.Name == name && x.Site == site);
                        snaps.Add(new Cookies.Snapshot { Name = name, Site = site, Time = Store.Now(), Json = Store.ToJson(mine) });
                        Cookies.SaveSnapshots(snaps);
                        return mine.Count;
                    }
                    var snap = snaps.FirstOrDefault(x => x.Name == name && x.Site == site);
                    if (snap == null) throw new Exception("Not found");
                    if (action == "delete") { snaps.Remove(snap); Cookies.SaveSnapshots(snaps); return null; }
                    // restore: the site's cookies become exactly the saved ones
                    foreach (var c in (await Cookies.All(core)).Where(c => Cookies.SiteOf(c) == site).ToList()) await Cookies.Delete(core, c);
                    int n = 0;
                    foreach (var c in Store.FromJson<List<Dictionary<string, object>>>(snap.Json)) if (await Cookies.Set(core, c) == null) n++;
                    return n;
                }
                case "sitedata.usage":
                {
                    string origin = Str(m, "origin");
                    var r = await tab.View.CoreWebView2.CallDevToolsProtocolMethodAsync("Storage.getUsageAndQuota", Store.ToJson(new Dictionary<string, object> { { "origin", origin } }));
                    return Store.FromJson<Dictionary<string, object>>(r);
                }
                case "sitedata.clear":
                {
                    string origin = Str(m, "origin"), types = Str(m, "types") ?? "local_storage,indexeddb,cache_storage,service_workers,file_systems,websql,shader_cache";
                    await tab.View.CoreWebView2.CallDevToolsProtocolMethodAsync("Storage.clearDataForOrigin", Store.ToJson(new Dictionary<string, object> { { "origin", origin }, { "storageTypes", types } }));
                    return null;
                }
                case "identity.probe":
                {
                    // what the site in that tab can read: assets/idprobe.js in its page (main world, identity changes applied)
                    int id = (int)Lng(m, "tab", 0);
                    var target = App.Windows.SelectMany(w => w.Tabs).FirstOrDefault(x => x.Id == id);
                    if (target?.Created != true) throw new Exception("The tab was closed");
                    var core2 = target.View.CoreWebView2;
                    string page = core2.Source;
                    if (!page.StartsWith("http")) throw new Exception("Open a website in that tab first");
                    string js = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "idprobe.js"), System.Text.Encoding.UTF8);
                    var res = Store.FromJson<Dictionary<string, object>>(await core2.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                        Store.ToJson(new Dictionary<string, object> { { "expression", js }, { "awaitPromise", true }, { "returnByValue", true } })));
                    var inner = res.TryGetValue("result", out var ro) ? ro as Dictionary<string, object> : null;
                    string json = inner != null && inner.TryGetValue("value", out var vo) ? vo as string : null;
                    if (json == null) throw new Exception("The check could not run on this page");
                    // kept here, not in the page's storage: New identity clears all site storage, the check page's too
                    string siteKey = Blocking.SiteOf(page) ?? Blocking.HostOf(page);
                    object prevCheck = null;
                    if (st.HideIdentity)
                    {
                        if (!st.IdentityChecks.TryGetValue(siteKey, out var hist)) st.IdentityChecks[siteKey] = hist = new List<string>();
                        string idNow = Identity.Id;
                        hist.RemoveAll(x => Store.FromJson<Dictionary<string, object>>(x).TryGetValue("id", out var hid) && (hid as string) == idNow);
                        hist.Insert(0, Store.ToJson(new Dictionary<string, object> { { "id", idNow }, { "at", Store.Now() }, { "values", Store.FromJson<Dictionary<string, object>>(json) } }));
                        if (hist.Count > 5) hist.RemoveRange(5, hist.Count - 5);
                        if (st.IdentityChecks.Count > 50) st.IdentityChecks.Remove(st.IdentityChecks.Keys.First(k => k != siteKey));
                        if (hist.Count > 1) prevCheck = Store.FromJson<Dictionary<string, object>>(hist[1]);
                        Store.SaveSettings();
                    }
                    return new Dictionary<string, object>
                    {
                        { "prev", prevCheck },
                        { "page", page }, { "site", Blocking.SiteOf(page) ?? Blocking.HostOf(page) }, { "title", target.Title },
                        { "on", st.HideIdentity }, { "noise", st.IdentityNoise }, { "id", Identity.Id },
                        { "values", Store.FromJson<Dictionary<string, object>>(json) },
                    };
                }
                case "tasks.list": return await TaskList();
                case "fonts.list": return Fonts.Installed();
                case "languages.all": return Languages.All();
                case "languages.state": return new Dictionary<string, object> { { "restart", Languages.Key != Languages.Active } };
                case "fonts.state": return new Dictionary<string, object> { { "restart", Fonts.Families != Fonts.ActiveFamilies } };
                case "tasks.end":
                {
                    // like Chrome's End process: only engine processes that serve pages, frames, GPU or utilities
                    int pid = (int)Lng(m, "pid", 0);
                    var infos = env.GetProcessInfos();
                    var pi = infos.FirstOrDefault(x => x.ProcessId == pid);
                    if (pi == null || pi.Kind == CoreWebView2ProcessKind.Browser) throw new Exception("Cannot end this process");
                    try { Process.GetProcessById(pid).Kill(); } catch (Exception ex) { throw new Exception(ex.Message); }
                    return null;
                }
                case "tasks.show":
                {
                    int id = (int)Lng(m, "tab", 0);
                    foreach (var w in App.Windows)
                    {
                        int i = w.Tabs.FindIndex(x => x.Id == id);
                        if (i >= 0) { w.Select(i); w.Activate(); break; }
                    }
                    return null;
                }
                case "log.get":
                {
                    int id = (int)Lng(m, "tab", 0);
                    var target = App.Windows.SelectMany(w => w.Tabs).FirstOrDefault(x => x.Id == id);
                    if (target == null) throw new Exception("The tab was closed");
                    string page = target.Created ? target.View.CoreWebView2.Source : target.Url;
                    var entries = target.Blocking?.LogSnapshot() ?? new List<Blocking.LogEntry>();
                    return new Dictionary<string, object>
                    {
                        { "page", page }, { "title", target.Title }, { "blocked", target.Blocked },
                        { "entries", entries.Select(x => new Dictionary<string, object> { { "t", x.Time }, { "u", x.Url }, { "y", x.Type }, { "r", x.Result } }).ToList() },
                    };
                }
                case "hosts.update": { string err = await HostUpdates.Update(); if (err != null) throw new Exception(err); return HostUpdates.State; }
                case "hosts.state":
                    return new Dictionary<string, object> { { "on", st.HostListUpdates }, { "state", HostUpdates.State },
                        { "updated", st.HostListsUpdated }, { "hosts", FilterEngine.ExtraHostCount } };
                case "identity.get":
                    return new Dictionary<string, object> { { "on", st.HideIdentity }, { "noise", st.IdentityNoise }, { "eachStart", st.IdentityEachStart }, { "id", Identity.Id } };
                case "identity.new":
                    await Identity.New(tab.View.CoreWebView2, m.TryGetValue("signOut", out var so) && so is bool sob && sob);
                    return Identity.Id;
                case "stats.reset": st.TotalBlocked = 0; Store.SaveSettings(); return null;
                case "lists.set":
                {
                    string file = Str(m, "file");
                    bool on = m.TryGetValue("on", out var ov) && ov is bool ob && ob;
                    st.DisabledLists.Remove(file);
                    if (!on && !string.IsNullOrEmpty(file)) st.DisabledLists.Add(file);
                    Store.SaveSettings();
                    return null;
                }
                case "insecure.allow":
                {
                    var u = Str(m, "url");
                    if (u == null || !u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return null;
                    var h = Blocking.HostOf(u);
                    if (h != null) Security.AllowHttp(h);
                    tab.Navigate(u);
                    return null;
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
                case "ext.details": return Extensions.Details(Str(m, "id"));
                case "ext.prefs": return new Dictionary<string, object> { { "pinNew", st.PinNewExtensions } };
                case "ext.pinnew": st.PinNewExtensions = m.TryGetValue("on", out var pn) && pn is bool pnb && pnb; Store.SaveSettings(); return null;
                case "ext.pin":
                    Extensions.SetPinned(Str(m, "id"), m.TryGetValue("on", out var pv) && pv is bool pvb && pvb);
                    Extensions.RaiseChanged();
                    return null;
                case "ext.shortcut":
                {
                    string sid = Str(m, "id"), key = Str(m, "key") ?? "";
                    if (key.Length == 0) st.ExtensionShortcuts.Remove(sid);
                    else
                    {
                        foreach (var kv in st.ExtensionShortcuts.ToList()) if (kv.Value == key && kv.Key != sid) throw new Exception("Already used");
                        st.ExtensionShortcuts[sid] = key;
                    }
                    Store.SaveSettings();
                    return null;
                }
                case "ext.install":
                {
                    var id = Extensions.ParseId(Str(m, "text"));
                    if (id == null) throw new Exception("No extension ID found");
                    var core = tab.View.CoreWebView2;
                    var ext = await Extensions.InstallFromStore(core.Profile, id, core.Settings.UserAgent, env.BrowserVersionString, ConfirmExtension);
                    if (ext == null) throw new Exception("Cancelled");
                    Extensions.RaiseChanged();
                    return ext.Name;
                }
                case "ext.pick":
                {
                    using (var dlg = new FolderBrowserDialog { Description = "Choose the extension folder (with manifest.json)", ShowNewFolderButton = false })
                    {
                        if (dlg.ShowDialog(this) != DialogResult.OK) return null;
                        var ext = await Extensions.LoadUnpacked(tab.View.CoreWebView2.Profile, dlg.SelectedPath);
                        Extensions.RaiseChanged();
                        return ext.Name;
                    }
                }
                case "ext.enable":
                {
                    var ext = (await Profile.GetBrowserExtensionsAsync()).FirstOrDefault(x => x.Id == Str(m, "id"));
                    if (ext != null) await ext.EnableAsync(m.TryGetValue("on", out var on) && on is bool ob && ob);
                    Extensions.RaiseChanged();
                    return null;
                }
                case "ext.remove": await Extensions.Remove(Profile, Str(m, "id")); Extensions.RaiseChanged(); return null;
                case "ext.options":
                {
                    var o = Extensions.OptionsPage(Extensions.Manifest(Str(m, "id")));
                    if (o != null) NewTab("chrome-extension://" + Str(m, "id") + "/" + o);
                    return null;
                }
                case "ext.popup":
                {
                    var p = Extensions.PopupPage(Extensions.Manifest(Str(m, "id")));
                    if (p != null) ShowExtensionPopup(Str(m, "id"), p, ExtAnchor(Str(m, "id")));
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
                case "procs":
                {
                    // which frames each engine process holds (for memory analysis)
                    var infos = await env.GetProcessExtendedInfosAsync();
                    return Store.ToJson(infos.Select(pi => new Dictionary<string, object> {
                        { "pid", pi.ProcessInfo.ProcessId }, { "kind", pi.ProcessInfo.Kind.ToString() },
                        { "frames", pi.AssociatedFrameInfos.Select(f => (f.Name ?? "") + " " + f.Source).ToList() } }).ToList());
                }
                case "state":
                    return Store.ToJson(new Dictionary<string, object>
                    {
                        { "selected", strip.Selected }, { "address", toolbar.Address.Text }, { "shown", toolbar.ShownUrl }, { "addressFocused", toolbar.Address.Focused }, { "title", Text },
                        { "tabs", Tabs.Select(t => new Dictionary<string, object> {
                            { "title", t.Title }, { "url", t.Created ? t.View.CoreWebView2.Source : t.Url }, { "created", t.Created },
                            { "loading", t.Loading }, { "sleeping", t.Sleeping }, { "blocked", t.Blocked } }).ToList() },
                    });
                case "menu":
                    if (arg == "shield") ShowShieldMenu(); else if (arg == "ext") ShowExtensionsMenu(); else ShowMenu();
                    await Task.Delay(400);
                    return MaterialMenu.Current == null ? "none" : MaterialMenu.Current.Handle.ToString();
                case "menuitems":
                    return MaterialMenu.Current == null ? "none" : string.Join(" | ", MaterialMenu.Current.Items.Select(i =>
                        i.Separator ? "---" : i.Text + (i.Checked ? " [x]" : "") + (i.Enabled ? "" : " (off)")));
                case "menuclick":
                {
                    var m = MaterialMenu.Current;
                    if (m == null) return "no menu";
                    while (m.OpenChild != null) m = m.OpenChild;   // the innermost open submenu first
                    var it = m.Items.FirstOrDefault(i => i.Text == arg);
                    if (it == null) return "no item";
                    if (it.Sub != null) { var c = m.TestOpenSub(arg); await Task.Delay(300); return c == null ? "no submenu" : c.Handle + " sub=" + c.Bounds + " parent=" + m.Bounds + " row=" + m.RectangleToScreen(it.Bounds); }
                    m.TestClick(arg);
                    return "ok";
                }
                case "menuclose": MaterialMenu.Current?.Close(); return "ok";
                case "menuopen": return MaterialMenu.Current == null ? "closed" : "open";
                case "devtools":
                    if (arg == "open") { Current?.View.CoreWebView2.OpenDevToolsWindow(); await Task.Delay(2500); }
                    else if (arg == "close") devDock.CloseCurrent();
                    else if (arg == "side") devDock.ToggleSide();
                    else if (arg == "undock") devDock.Undock();
                    return "docked=" + devDock.Docked(Current) + " count=" + devDock.DockedCount + " frame=" + devDock.FrameInfo() + " page=" + content.Bounds + " splitter=" + devDock.SplitterOnScreen;
                case "bubble": bubble.SetText(arg); return bubble.Visible ? bubble.Bounds.ToString() : "hidden";
                case "rclick":
                {
                    // a right-click inside the page through the engine's DevTools protocol (no desktop input)
                    var xy = arg.Split(' ');
                    var core = Current.View.CoreWebView2;
                    string p = "{\"x\":" + xy[0] + ",\"y\":" + xy[1] + ",\"button\":\"right\",\"clickCount\":1";
                    await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", p + ",\"type\":\"mousePressed\"}");
                    await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", p + ",\"type\":\"mouseReleased\"}");
                    await Task.Delay(600);
                    return lastContextInfo ?? "no menu event";
                }
                case "tabmenu": ShowTabMenu(int.Parse(arg), strip.PointToScreen(new Point(200, 20))); await Task.Delay(300); return "ok";
                case "tabinfo":
                    return string.Join(" | ", Tabs.Select((x, i) => i + ":" + (x.Title ?? "").Split(' ')[0] + (x.Pinned ? " pinned" : "") + (x.Muted ? " muted" : "") +
                        (x.Created ? (x.View.CoreWebView2.IsMuted ? " engineMuted" : "") : " (not loaded)") + (i == strip.Selected ? " *" : "")));
                case "dbg": Blocking.TestDbg = arg; return "ok";
                case "dropstrip":
                {
                    // test: drop <url or text> at tab <i> ("in" = its middle, "before" = its left edge, "end" = after the last)
                    var a2 = arg.Split(new[] { ' ' }, 3);
                    int i = int.Parse(a2[0]);
                    var r = strip.TestTabRect(Math.Min(i, Tabs.Count - 1));
                    int x = a2[1] == "in" ? r.X + r.Width / 2 : a2[1] == "before" ? r.X + 2 : r.Right + 30;
                    var d = a2[2].StartsWith("http") ? UrlDrag.Make(a2[2], "Test page") : UrlDrag.MakeText(a2[2]);
                    return strip.TestDrop(d, x, r.Y + r.Height / 2);
                }
                case "dropaddress":
                {
                    var d = arg.StartsWith("http") ? UrlDrag.Make(arg, "Test page") : UrlDrag.MakeText(arg);
                    var data = (IDataObject)d;
                    string t = UrlDrag.ReadTarget(data);
                    var url = Search.ToUrl(t);
                    if (url != null) { Current.Navigate(url); Current.View?.Focus(); }
                    return t ?? "nothing";
                }
                case "dragdata":
                {
                    // test: the drag data for the current page, read back the way drop targets read it
                    var pg = toolbar.DragPage?.Invoke();
                    if (pg == null) return "not draggable";
                    var d = UrlDrag.Make(pg.Item1, pg.Item2);
                    string u = UrlDrag.ReadUrl(d, out var title);
                    return "formats=" + string.Join(",", d.GetFormats()) + " | url=" + u + " | title=" + title;
                }
                case "shelldrop":
                {
                    // test: the current page dropped on a folder through Explorer's own drop handler (desktop / folders)
                    var pg = toolbar.DragPage?.Invoke();
                    if (pg == null) return "not draggable";
                    return await ShellDrop.Drop(arg, UrlDrag.Make(pg.Item1, pg.Item2));
                }
                case "focusinfo":
                {
                    // test: why the page does not take the keyboard focus
                    var v = Current.View;
                    string before = "canFocus=" + v.CanFocus + " canSelect=" + v.CanSelect + " visible=" + v.Visible + " enabled=" + v.Enabled +
                        " tabStop=" + v.TabStop + " handle=" + v.IsHandleCreated;
                    bool r = arg == "select" ? SelectNextControlTo(v) : v.Focus();
                    await Task.Delay(300);
                    return before + " | focus()=" + r + " viewFocused=" + v.Focused + " contains=" + v.ContainsFocus + " address=" + toolbar.Address.Focused +
                        " active=" + (ActiveControl?.GetType().Name ?? "none");
                }
                case "wins":   // test: windows and their tabs (* = active)
                    return string.Join(" || ", App.Windows.Select((w, i) => i + (w == App.Active ? "*" : "") + " " + w.Bounds + ": " +
                        string.Join(", ", w.Tabs.Select(t => t.Created ? t.View.CoreWebView2.Source : t.Url))));
                case "movetab": MoveTabToNewWindow(int.Parse(arg), null, false); return "ok";
                case "mergetab": MoveTabTo(Current, App.Windows[int.Parse(arg)], -1); return "ok";
                case "gc":
                    System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    return (GC.GetTotalMemory(false) / 1048576.0).ToString("F1") + " MB managed";
                case "pick": { var a2 = arg.Split(' '); lastPickRule = null; PickElement(new Point(int.Parse(a2[0]), int.Parse(a2[1])), true); return "ok"; }
                case "pickresult": return lastPickRule ?? "(none)";
                case "bmbar": return bookmarksBar.TestItems();
                case "bmimport": return (arg.EndsWith(".json") ? Bm.ImportChromium(File.ReadAllText(arg), "Test") : Bm.ImportHtml(File.ReadAllText(arg), "Test")).ToString();
                case "bmexport": File.WriteAllText(arg, Bm.ExportHtml()); return "ok";
                case "bmbarclick": bookmarksBar.TestClick(int.Parse(arg)); return "ok";
                case "bmbarright": bookmarksBar.TestRightClick(int.Parse(arg)); return "ok";
                case "bmbardrag": { var a2 = arg.Split(' '); bookmarksBar.TestDrag(int.Parse(a2[0]), int.Parse(a2[1])); return "ok"; }
                case "bmbardrop": { var a2 = arg.Split(new[] { ' ' }, 3); bookmarksBar.TestDropUrl(a2[0], a2.Length > 2 ? a2[2] : "", int.Parse(a2[1])); return "ok"; }
                case "bmstar": BeginInvoke((Action)ToggleBookmark); return "ok";
                case "winclose": { var w = App.Windows[int.Parse(arg)]; BeginInvoke((Action)w.Close); return "ok"; }
                case "winactivate": App.Windows[int.Parse(arg)].Activate(); return "ok";
                case "quit": BeginInvoke((Action)(() => App.Quit())); return "ok";
                case "framesx": return await Current.Blocking.TestFramesExec(arg, false);
                case "framesxa": return await Current.Blocking.TestFramesExec(arg, true);
                case "slcases":   // test: file with one scriptlet call per line (JSON array), served to pages with slcase=<line>
                    Blocking.TestScriptlets = arg.Length == 0 ? null : System.IO.File.ReadAllLines(arg); return "ok " + (Blocking.TestScriptlets?.Length ?? 0);
                case "cdps":   // test: DevTools call in an attached frame session: "<session> <method> <json>"
                {
                    var parts = arg.Split(new[] { ' ' }, 3);
                    return await Current.View.CoreWebView2.CallDevToolsProtocolMethodForSessionAsync(parts[0], parts[1], parts[2]);
                }
                case "cssstate": return Current.Blocking?.CssState ?? "no blocker";
                case "siteinfo":
                    await ShowSiteInfo();
                    await Task.Delay(300);
                    return "ok";
                case "find":
                    ShowFind();
                    if (arg.Length > 0) { findBar.Box.Clear(); findBar.Box.TypeForTest(arg); }
                    await Task.Delay(800);
                    return "'" + findBar.ResultText + "' visible=" + findBar.Visible + " active=" + findBar.Active;
                case "findkey":
                    findBar.Box.KeyForTest(arg == "prev" ? Keys.Shift | Keys.Enter : arg == "esc" ? Keys.Escape : Keys.Enter);
                    await Task.Delay(500);
                    return "'" + findBar.ResultText + "' visible=" + findBar.Visible + " active=" + findBar.Active;
                case "dlg": return MaterialDialog.TestInfo();
                case "dlgtype": { int at = arg.IndexOf(' '); MaterialDialog.TestType(int.Parse(arg.Substring(0, at)), arg.Substring(at + 1)); return "ok"; }
                case "dlgcheck": MaterialDialog.TestCheck(); return "ok";
                case "dlgclose": MaterialDialog.TestClose(arg == "ok"); await Task.Delay(500); return "ok";
                case "dlgshot": return MaterialDialog.TestHandle.ToInt64().ToString();
                case "killpage": lastProcessFailed = ""; await EndPageProcess(Current.View.CoreWebView2); await Task.Delay(1500); return Current.View.CoreWebView2.Source + " | events: " + lastProcessFailed;
                case "permchoose":
                {
                    var p = PermissionPrompt.Open;
                    if (p == null) return "no prompt";
                    if (arg == "close") p.Close(); else p.Choose(int.Parse(arg));
                    return "ok";
                }
                case "omnitype":
                    toolbar.Address.Focus();
                    await Task.Delay(150);   // focusing selects the address (queued); real typing comes after that
                    toolbar.Address.Text = ""; toolbar.Address.TypeForTest(arg);
                    await Task.Delay(arg.EndsWith(" ") ? 0 : 900);   // search suggestions arrive after a short pause
                    return "text=" + toolbar.Address.Text + " sel=" + toolbar.Address.SelectionStart + "+" + toolbar.Address.SelectionLength + " | " + omni.Describe();
                case "omnikey":
                    toolbar.Address.KeyForTest(arg == "ShiftDelete" ? Keys.Delete | Keys.Shift : (Keys)Enum.Parse(typeof(Keys), arg));
                    await Task.Delay(300);
                    return "text=" + toolbar.Address.Text + " | " + omni.Describe();
                case "lastmenu": return PageMenu.LastEngineItems;
                case "longmenu":
                {
                    // test: a menu taller than the screen, to check scrolling
                    var lm = NewMenu();
                    for (int k = 1; k <= 40; k++) { int kk = k; lm.Add("Item " + k, k % 2 == 0 ? IconData.Tab : IconData.Star, "Key " + k, () => Notice("Item " + kk)); }
                    lm.ShowAt(toolbar, toolbar.ButtonRect("menu"), true, "longmenu");
                    await Task.Delay(300);
                    return lm.Handle.ToString();
                }
                case "groups":
                    return string.Join(" | ", Tabs.Select(x => (x.Title ?? "?").Split(' ')[0] + (x.Group != null ? "[" + (strip.Groups.TryGetValue(x.Group, out var gg) ? gg.Name + "/" + TabStrip.GroupColorNames[gg.Color] + (gg.Collapsed ? "/collapsed" : "") : "?") + "]" : "")))
                        + " || selected=" + strip.Selected;
                case "grouptoggle": if (strip.Groups.Count > 0) ToggleGroup(strip.Groups.Keys.First()); await Task.Delay(200); return "ok";
                case "hovercard":
                    strip.TestCard(int.Parse(arg));
                    await Task.Delay(300);
                    return strip.CardHandle.ToString();
                case "mediatest":
                {
                    // test: "<camera> <microphone>" live tracks reported for the current tab, as the page script does
                    var mp = arg.Split(' ');
                    Current?.Blocking?.Media("test", int.Parse(mp[0]), int.Parse(mp[1]));
                    await Task.Delay(300);
                    return "ok";
                }
                case "toolbarbtn":
                {
                    // test: "<name>" = its bounds (empty = hidden); "<name> click" = press it
                    var parts = arg.Split(' ');
                    if (parts.Length > 1 && parts[1] == "click") { toolbar.ClickButton(parts[0]); await Task.Delay(300); }
                    var rb = toolbar.ButtonRect(parts[0]);
                    return rb.IsEmpty ? "hidden" : rb.ToString() + " zoom=" + (Current?.View?.ZoomFactor.ToString() ?? "-");
                }
                case "persistinfo":
                {
                    string sc = Store.ReadText("session-cookies.json") ?? "(none)";
                    return "zoomFactor=" + (Current?.View?.ZoomFactor.ToString() ?? "-") + " siteZoom=" + Store.ToJson(Store.Settings.SiteZoom) +
                        " sessionFile=" + (sc.Length > 300 ? sc.Substring(0, 300) + "..." : sc);
                }
                case "dlinfo":
                {
                    var d = Downloads.All.FirstOrDefault();
                    if (d == null) return "none";
                    string opState = "-";
                    try { if (d.Op != null) opState = d.Op.State + " recv=" + d.Op.BytesReceived + " total=" + d.Op.TotalBytesToReceive + " reason=" + d.Op.InterruptReason; } catch (Exception ex) { opState = "error " + ex.Message; }
                    return Path.GetFileName(d.Path ?? "") + " state=" + d.State + " op=" + opState + " fullAt=" + (d.FullAt == default(DateTime) ? "none" : (DateTime.UtcNow - d.FullAt).TotalSeconds.ToString("F1") + "s ago") + " timer=" + reviewWatch.Enabled + " panelOpen=" + (Current?.View?.CoreWebView2?.IsDefaultDownloadDialogOpen.ToString() ?? "-") + " sameCore=" + (d.Core == Current?.View?.CoreWebView2);
                }
                case "extinstall":
                {
                    var core = Current.View.CoreWebView2;
                    var ext = await Extensions.InstallFromStore(core.Profile, arg, core.Settings.UserAgent, env.BrowserVersionString, null);
                    return ext.Id + " " + ext.Name;
                }
                case "devguard": return Current?.Created == true ? await DevToolsGuard.Apply(Current.View.CoreWebView2, Current.View.CoreWebView2.Source, arg.Length > 0 ? arg : null, 5000) : "no tab";
                case "sqlitecheck":   // sqlitecheck <db file>: rows of icon_mapping + favicon_bitmaps, image bytes, md5 of all images
                {
                    using (var r = new SqliteReader(arg))
                    using (var md5 = System.Security.Cryptography.MD5.Create())
                    {
                        int maps = r.Rows("icon_mapping").Count();
                        int bm = 0; long bytes = 0;
                        foreach (var row in r.Rows("favicon_bitmaps").OrderBy(x => x.RowId))
                        {
                            bm++;
                            if (row.Values[3] is byte[] png) { bytes += png.Length; md5.TransformBlock(png, 0, png.Length, null, 0); }
                        }
                        md5.TransformFinalBlock(new byte[0], 0, 0);
                        return "mappings " + maps + " bitmaps " + bm + " bytes " + bytes + " md5 " + BitConverter.ToString(md5.Hash).Replace("-", "").ToLowerInvariant();
                    }
                }
                case "extload":   // load an unpacked extension folder (same path as Load unpacked, without the folder picker)
                {
                    var ext = await Extensions.LoadUnpacked(Profile, arg);
                    Extensions.RaiseChanged();
                    return ext.Id + " " + ext.Name;
                }
                case "extpopup":
                {
                    // extpopup <id> [page]: the page lets tests open any of the extension's pages as its popup
                    var ap = arg.Split(' ');
                    arg = ap[0];
                    var pp = ap.Length > 1 ? ap[1] : Extensions.PopupPage(Extensions.Manifest(arg));
                    if (pp == null) return "no popup";
                    ShowExtensionPopup(arg, pp, ExtAnchor(arg), true);
                    await Task.Delay(2500);
                    return lastPopup.Handle.ToString() + " " + lastPopup.Width + "x" + lastPopup.Height + " css " + lastPopup.CssSize.Width + "x" + lastPopup.CssSize.Height;
                }
                case "popupsize": return lastPopup == null || lastPopup.IsDisposed ? "no popup" : lastPopup.Width + "x" + lastPopup.Height + " css " + lastPopup.CssSize.Width + "x" + lastPopup.CssSize.Height + " at " + lastPopup.Left + "," + lastPopup.Top;
                case "extbar":   // pinned toolbar icons: id name rect [badge]
                    await RefreshBadges(true);
                    return string.Join("; ", toolbar.Actions.Select(a => a.Id + " " + a.Name + " " + a.Rect + (a.Badge.Length > 0 ? " badge=" + a.Badge + " back=" + a.BadgeBack : "") + (a.Title != null ? " title=" + a.Title.Replace("\n", " / ") : "")));
                case "extmenu":  // the extension's own menu (as a right-click on its icon)
                {
                    ShowExtensionContextMenu(arg, ExtAnchor(arg));
                    await Task.Delay(400);
                    return MaterialMenu.Current == null ? "no menu" : string.Join(" | ", MaterialMenu.Current.Items.Select(i => i.Text));
                }
                case "popupclose": lastPopup?.Close(); return "ok";
                case "popupclick":   // real mouse click at CSS x y inside the extension popup
                {
                    if (lastPopup?.Core == null) return "no popup";
                    var xy = arg.Split(' ');
                    string pt = "{\"x\":" + xy[0] + ",\"y\":" + xy[1] + ",\"button\":\"left\",\"clickCount\":1";
                    await lastPopup.Core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", pt + ",\"type\":\"mouseMoved\"}");
                    await lastPopup.Core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", pt + ",\"type\":\"mousePressed\"}");
                    await lastPopup.Core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", pt + ",\"type\":\"mouseReleased\"}");
                    return "ok";
                }
                case "popupeval": return lastPopup?.Core == null ? "no popup" : await lastPopup.Core.ExecuteScriptAsync(arg);
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
                case "testlogclear": Blocking.TestLog.Clear(); return "ok";
                case "ctrls":
                {
                    var sb = new System.Text.StringBuilder();
                    void Walk(Control c, string ind)
                    {
                        sb.Append(ind + c.GetType().Name + " " + c.Bounds + (c.Visible ? "" : " hidden") + " | ");
                        foreach (Control k in c.Controls) if (!(k is Microsoft.Web.WebView2.WinForms.WebView2)) Walk(k, ind + ">");
                    }
                    Walk(this, "");
                    return sb.ToString();
                }
                case "addrtype":
                {
                    // WM_CHAR straight to the field: the same path real typing takes, no keys on the desktop
                    toolbar.Address.Focus();
                    foreach (char ch in arg) SendMessage(toolbar.Address.Handle, 0x0102, (IntPtr)ch, IntPtr.Zero);
                    await Task.Delay(100);
                    return toolbar.Address.Text + " | sel " + toolbar.Address.SelectionStart + "+" + toolbar.Address.SelectionLength;
                }
                case "addrkey":
                {
                    // keys without modifiers (Left, Right, Home, End, Back, Delete) as WM_KEYDOWN
                    var k = (Keys)Enum.Parse(typeof(Keys), arg, true);
                    SendMessage(toolbar.Address.Handle, 0x0100, (IntPtr)(int)k, IntPtr.Zero);
                    await Task.Delay(50);
                    return toolbar.Address.Text + " | sel " + toolbar.Address.SelectionStart + "+" + toolbar.Address.SelectionLength;
                }
                case "addrxof": return toolbar.Address.TestXOf(arg).ToString();
                case "addrclick":
                {
                    // test: n real left clicks (WM_LBUTTONDOWN / UP) at client x of the address box, quick like a double-click
                    var a2 = arg.Split(' ');
                    int x = int.Parse(a2[0]), n = a2.Length > 1 ? int.Parse(a2[1]) : 1, y = toolbar.Address.Height / 2;
                    IntPtr lp = (IntPtr)((y << 16) | (x & 0xFFFF));
                    for (int k = 0; k < n; k++)
                    {
                        SendMessage(toolbar.Address.Handle, 0x0201, (IntPtr)1, lp);
                        Application.DoEvents();
                        SendMessage(toolbar.Address.Handle, 0x0202, IntPtr.Zero, lp);
                        Application.DoEvents();
                        await Task.Delay(60);
                    }
                    await Task.Delay(150);
                    return "sel='" + toolbar.Address.SelectedText + "' text='" + toolbar.Address.Text + "' x now=" + toolbar.Address.TestXOf(toolbar.Address.SelectedText.Length > 0 ? toolbar.Address.SelectedText : "test");
                }
                case "addrmenu": toolbar.Address.ShowMenu(new Point(40, toolbar.Address.Height)); await Task.Delay(300); return MaterialMenu.Current?.Handle.ToString() ?? "none";
                case "addrselect": { var p = arg.Split(' '); toolbar.Address.Focus(); toolbar.Address.Select(int.Parse(p[0]), int.Parse(p[1])); return "ok"; }
                case "newprivate": NewPrivateTab(arg.Length == 0 ? null : Search.ToUrl(arg)); return "ok";
                case "storeinstall":
                {
                    var core = Current.View.CoreWebView2;
                    _ = Extensions.InstallFromStore(core.Profile, arg, core.Settings.UserAgent, env.BrowserVersionString, ConfirmExtension)
                        .ContinueWith(tk => Blocking.TestLog.Add("install: " + (tk.IsFaulted ? "error " + tk.Exception.InnerException.Message : tk.Result == null ? "cancelled" : "added " + tk.Result.Name)),
                            TaskScheduler.FromCurrentSynchronizationContext());
                    return "started";
                }
                case "crxtest":
                {
                    // real package must pass; the same package with one byte changed, or under another ID, must fail
                    string u = "https://clients2.google.com/service/update2/crx?response=redirect&prodversion=157.0.0.0&acceptformat=crx3&x=id%3D" + arg + "%26installsource%3Dondemand%26uc";
                    byte[] crx;
                    using (var http = new System.Net.Http.HttpClient()) crx = await http.GetByteArrayAsync(u);
                    string r1, r2, r3;
                    try { Crx.Parse(crx, arg); r1 = "original: accepted"; } catch (Exception ex) { r1 = "original: REJECTED " + ex.Message; }
                    var bad = (byte[])crx.Clone(); bad[bad.Length - 100] ^= 0x01;
                    try { Crx.Parse(bad, arg); r2 = "tampered: ACCEPTED (bad)"; } catch (Exception ex) { r2 = "tampered: rejected (" + ex.Message + ")"; }
                    try { Crx.Parse(crx, "aaaabbbbccccddddeeeeffffgggghhhh"); r3 = "wrong id: ACCEPTED (bad)"; } catch (Exception ex) { r3 = "wrong id: rejected (" + ex.Message + ")"; }
                    return r1 + " | " + r2 + " | " + r3;
                }
                case "forms": return string.Join(" | ", Application.OpenForms.Cast<Form>().Select(f => f.GetType().Name + "=" + f.Handle));
                case "dialog":
                {
                    var d = Application.OpenForms.OfType<MaterialDialog>().FirstOrDefault();
                    if (d == null) return "none";
                    d.DialogResult = arg == "ok" ? DialogResult.OK : DialogResult.Cancel;
                    d.Close();
                    return "closed";
                }
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
            Current?.View?.Focus();   // as Enter in the address bar does (toolbar.Go)
        }
#endif

        // ---------- closing ----------

        bool exitCleared;

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            App.Windows.Remove(this);
            Updater.Changed -= OnUpdateChanged;
            Downloads.Changed -= OnDownloadsChanged;
            if (App.Windows.Count > 0) return;
            // restart for settings that apply only at engine start (Secure DNS); tabs come back via the saved session
            if (App.RestartAfter) try { Process.Start(Application.ExecutablePath, "--restarted"); } catch { }
            Application.ExitThread();
        }

        bool sessionSaved;

        public bool CloseConfirmed;

        /// <summary>Like Chrome: closing GBrowser while downloads run asks first. False = the user keeps GBrowser open.</summary>
        public bool ConfirmExit()
        {
            if (CloseConfirmed) return true;
            int running = Downloads.All.Count(d => d.State == "in_progress");
            if (running > 0 && !MaterialDialog.Ask(this, running == 1 ? "Cancel download?" : "Cancel " + running + " downloads?", null,
                    new[] { Tuple.Create(IconData.Download, running == 1 ? "A download is still running." : running + " downloads are still running.") },
                    "Exit", "Keep downloading"))
                return false;
            CloseConfirmed = true;
            return true;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            bool last = App.Windows.Count <= 1;   // GBrowser closes with this window
            // never asked when Windows itself shuts down
            if (last && e.CloseReason != CloseReason.WindowsShutDown && e.CloseReason != CloseReason.TaskManagerClosing && !ConfirmExit())
            {
                e.Cancel = true;
                return;
            }
            devDock?.Shutdown();   // docked DevTools windows go back to the engine before the window closes
            // session logins (cookies without an expiry date) are kept for next time, like Chrome's session restore
            // (not after the engine itself stopped: it cannot be asked any more, and closing must never be blocked)
            if (last && !sessionSaved && !Store.Settings.ClearOnExit && !engineAsked)
            {
                sessionSaved = true;
                try
                {
                    var normal = Tabs.FirstOrDefault(t => t.Created && !t.Private)?.View.CoreWebView2;
                    if (normal != null)
                    {
                        var save = SessionCookies.Save(normal.CookieManager);
                        e.Cancel = true;
                        save.ContinueWith(_ => BeginInvoke((Action)Close));
                        return;
                    }
                }
                catch { }
            }
            if (last && Store.Settings.ClearOnExit) SessionCookies.Forget();
            // "Clear browsing data when GBrowser closes": cookies, site data, cache, history (passwords kept)
            if (last && Store.Settings.ClearOnExit && !exitCleared && Profile != null)
            {
                e.Cancel = true;
                exitCleared = true;
                var p = Profile;
                Store.History.Clear();
                Store.SaveHistory();
                Favicons.ClearExcept(Store.Bookmarks.Where(b => !b.Folder).Select(b => b.Url));
                p.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllSite | CoreWebView2BrowsingDataKinds.DiskCache |
                    CoreWebView2BrowsingDataKinds.BrowsingHistory | CoreWebView2BrowsingDataKinds.DownloadHistory)
                    .ContinueWith(_ => BeginInvoke((Action)Close));
                return;
            }
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
            // closing the last window keeps its tabs for next time (closing one of several does not, like Chrome);
            // Exit already kept every window's
            if (last) App.SaveSession(new List<MainForm> { this });
            Store.SaveSettings();
            Store.SaveHistory();
            base.OnFormClosing(e);
        }
    }
}
