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
        readonly Timer sleepTimer = new Timer { Interval = 30000 };
        readonly Stack<string> closedTabs = new Stack<string>();
        CoreWebView2Environment env;
        bool fullscreen; FormWindowState preFullState; FormBorderStyle preFullBorder;

        List<BrowserTab> Tabs => strip.Tabs;
        BrowserTab Current => strip.Selected >= 0 && strip.Selected < Tabs.Count ? Tabs[strip.Selected] : null;
        static readonly string AppVersion = typeof(MainForm).Assembly.GetName().Version.ToString(3);

        readonly Timer uiTimer = new Timer { Interval = 300 };
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
            BackColor = Theme.Surface;
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
            strip.TabMuteClicked += i => SetMuted(Tabs[i], !Tabs[i].Muted);
            strip.TabDraggedOut += (i, pt) => MoveTabToNewWindow(i, pt, true);
            ResizeEnd += (s, e) => DropDraggedWindow();
            frame = new WindowFrame(this, strip);
            strip.TabMoved += (from, to) =>
            {
                if (Tabs[from].Pinned != Tabs[to].Pinned) return;   // pinned and normal tabs keep their own block
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
                SearchUrl = text => (Search.Engines.TryGetValue(Store.Settings.SearchEngine ?? "google", out var en) ? en[1] : Search.Engines["google"][1]) + Uri.EscapeDataString(text),
            };
            toolbar.DownloadsClicked += OpenDownloads;
            toolbar.ExtensionsClicked += ShowExtensionsMenu;
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
            bookmarksBar.ImportNow += () => NewTab(Pages.Url("bookmarks") + "#import");
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
            var tabs = Tabs.Where(t => !t.Private).Select(t => t.Created ? t.View.CoreWebView2.Source : t.Url).Where(u => !string.IsNullOrEmpty(u)).ToList();
            if (tabs.Count == 0) return null;
            var b = WindowState == FormWindowState.Normal && !fullscreen ? Bounds : RestoreBounds;
            return new WindowSession
            {
                Tabs = tabs, Index = Math.Max(0, Math.Min(strip.Selected, tabs.Count - 1)), Pinned = Tabs.TakeWhile(t => t.Pinned).Count(t => !t.Private),
                X = b.X, Y = b.Y, W = b.Width, H = b.Height, Max = WindowState == FormWindowState.Maximized || fullscreen
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
            int at = Tabs.TakeWhile(x => x.Pinned).Count();
            Tabs.Insert(at, t);
            strip.Selected = Tabs.IndexOf(cur);
            strip.RefreshTabs();
        }

        void SetMuted(BrowserTab t, bool mute)
        {
            t.Muted = mute;
            if (t.Created) try { t.View.CoreWebView2.IsMuted = mute; } catch { }
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
        static Color PageBase(string url) => url == null || Pages.IsInternal(url) || url.StartsWith("about:") ? Theme.Surface : Color.White;

        /// <summary>Applies the current theme to the window, title bar, toolbars and all web content.</summary>
        void ApplyTheme(bool force)
        {
            if (!Theme.Update() && !force) return;
            BackColor = content.BackColor = bookmarksBar.BackColor = Theme.Surface;
            strip.BackColor = Theme.SurfaceContainerHigh;
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
            UpdateUi();
            strip.RefreshTabs();
        }

        /// <summary>Where the default-browser registration goes: the real place, or (test builds) a test key that
        /// changes nothing in Windows.</summary>
#if AUTOMATION
        internal const string DefaultRoot = @"Software\GoldenBoot\GBrowser-TestRegistration";
#else
        internal const string DefaultRoot = "Software";
#endif

        // ---------- tabs between windows ----------

        BrowserTab pendingAdopt;    // a tab moved here from another window (adopted once the engine is ready)
        bool dragWindow;            // this window was made by dragging a tab out and follows the mouse

        /// <summary>Takes a tab out of this window without closing it (it moves to another window). The window
        /// closes when it was the last tab.</summary>
        void DetachTab(BrowserTab t)
        {
            int i = Tabs.IndexOf(t);
            if (i < 0) return;
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

        void CloseTab(int i)
        {
            if (i < 0 || i >= Tabs.Count) return;
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
                if (Pages.IsInternal(core.Source)) t.Title = BrowserTab.PageTitle(core.Source);
                if (!t.Private) Store.UpdateHistoryTitle(core.Source, core.DocumentTitle);
                F().strip.RefreshTabs();
                if (t == F().Current) F().UpdateUi();
            };
            core.NavigationStarting += (o, e) =>
            {
                t.Loading = true; F().strip.RefreshTabs(); if (t == F().Current) F().UpdateUi();
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
            core.WindowCloseRequested += (o, e) => { int i = F().Tabs.IndexOf(t); if (i >= 0) F().CloseTab(i); };
            core.ContainsFullScreenElementChanged += (o, e) => F().SetFullscreen(core.ContainsFullScreenElement);
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
            toolbar.CanBack = core?.CanGoBack ?? false;
            toolbar.CanForward = core?.CanGoForward ?? false;
            toolbar.Loading = t.Loading;
            toolbar.Secure = url.StartsWith("https://") || Pages.IsInternal(url);
            toolbar.PageKind = Pages.IsInternal(url) ? "internal" : url.StartsWith("https://") ? "secure"
                : url.StartsWith("http://") && !Security.IsLocal(Blocking.HostOf(url)) ? "insecure" : "";
            if (toolbar.ShowHome != Store.Settings.ShowHomeButton) { toolbar.ShowHome = Store.Settings.ShowHomeButton; toolbar.LayoutNow(); }
            toolbar.Bookmarked = Store.IsBookmarked(url);
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
            m.Add("Bookmarks", IconData.Bookmarks, "Ctrl+Shift+O", () => OpenPage("bookmarks"));
            m.Add(Store.Settings.ShowBookmarksBar ? "Hide bookmarks bar" : "Show bookmarks bar", IconData.Star, "Ctrl+Shift+B", ToggleBookmarksBar);
            m.Add("History", IconData.History, "Ctrl+H", () => OpenPage("history"));
            m.Add("Downloads", IconData.Download, "Ctrl+J", OpenDownloads);
            m.Add("Extensions", IconData.Extension, null, () => OpenPage("extensions"));
            string curUrl = Current?.Created == true ? Current.View.CoreWebView2.Source : Current?.Url;
            string keepSite = Pages.IsInternal(curUrl) ? null : Blocking.SiteOf(curUrl);   // GBrowser's own pages never unload
            if (keepSite != null)
            {
                bool kept = Store.Settings.KeepActiveSites.Contains(keepSite);
                var keep = m.AddToggle("Keep " + keepSite + " active", kept, true, () =>
                {
                    Store.Settings.KeepActiveSites.Remove(keepSite);
                    if (!kept) Store.Settings.KeepActiveSites.Add(keepSite);
                    Store.SaveSettings();
                });
                keep.Icon = IconData.Memory;
            }
            m.AddSeparator();
            m.Add("Zoom in", IconData.ZoomIn, "Ctrl++", () => Zoom(1.1));
            m.Add("Zoom out", IconData.ZoomOut, "Ctrl+-", () => Zoom(1 / 1.1));
            m.Add("Find on page", IconData.Find, "Ctrl+F", ShowFind);
            m.Add("Print", IconData.Print, "Ctrl+P", () => { if (Current?.Created == true) Current.View.CoreWebView2.ShowPrintUI(); });
            m.Add("Full screen", IconData.Fullscreen, "F11", () => SetFullscreen(!fullscreen));
            m.Add("Developer tools", IconData.Code, "F12", () =>
            {
                // like Chrome: toggles this tab's DevTools (it docks itself when it opens)
                if (devDock.Docked(Current)) devDock.CloseCurrent();
                else if (Current?.Created == true) Current.View.CoreWebView2.OpenDevToolsWindow();
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
                if (fast != null) { e.Cancel = true; fast.Item.ReplaceTarget = replace; }   // GBrowser downloads it in parts
                else
                {
                    var it = Downloads.Track(op, path);
                    it.ReplaceTarget = replace;
                    it.Core = core;
                }
                downloadsSeen = true;
                OnDownloadsChanged();
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
            if (Math.Abs(z - 1) < 0.005) changed = sz.Remove(key);
            else { changed = !sz.TryGetValue(key, out var old) || Math.Abs(old - z) > 0.001; sz[key] = z; }
            if (changed) Store.SaveSettings();
            UpdateUi();
        }

        void ApplySiteZoom(BrowserTab t)
        {
            if (!t.Created) return;
            string key = ZoomKey(t.View.CoreWebView2.Source);
            if (key == null) return;
            double want = Store.Settings.SiteZoom.TryGetValue(key, out var z) ? z : 1.0;
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
                foreach (var ext in list ?? new List<CoreWebView2BrowserExtension>())
                {
                    if (!ext.IsEnabled || !Extensions.Index.ContainsKey(ext.Id)) continue;
                    var man = Extensions.Manifest(ext.Id);
                    string popup = Extensions.PopupPage(man), options = Extensions.OptionsPage(man);
                    string id = ext.Id;
                    Action open = popup != null ? (Action)(() => ShowExtensionPopup(id, popup))
                        : options != null ? () => NewTab("chrome-extension://" + id + "/" + options) : (Action)null;
                    var it = m.Add(ext.Name, IconData.Extension, null, open);
                    it.Image = Extensions.Icon(ext.Id);
                    it.Enabled = open != null;
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
            m.Add("Cookies and site data", IconData.Cookie, cookies + " in use", () => DeleteSiteData(t, u, host));
            var st = Store.Settings;
            string site = Blocking.SiteOf(page);
            if (site != null)
                m.AddToggle("Block ads on this site", st.AdblockEnabled && !st.AllowedSites.Contains(site), st.AdblockEnabled, () =>
                {
                    if (!st.AllowedSites.Remove(site)) st.AllowedSites.Add(site);
                    Store.SaveSettings();
                    ReloadAfterSettingChange();
                });
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
        async void DeleteSiteData(BrowserTab t, Uri u, string host)
        {
            if (!MaterialDialog.Ask(this, "Delete site data?", null, new[] { Tuple.Create(IconData.Cookie, "Cookies and stored data of " + host + ". You may be signed out.") }, "Delete"))
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
            Toggle("Block pages of ad sites", st.BlockAdSitePages, v => st.BlockAdSitePages = v, st.AdblockEnabled);
            m.AddSeparator();
            m.AddNote("Total blocked: " + st.TotalBlocked.ToString("N0"));
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
            try { if (iconFile != null) using (var i = Image.FromFile(iconFile)) img = new Bitmap(i, 40, 40); } catch { }
            var lines = new List<Tuple<string, string>>();
            if (access.Count == 0) lines.Add(Tuple.Create(IconData.Verified, "No special access"));
            else lines.Add(Tuple.Create((string)null, "It can:"));
            foreach (var a in access) lines.Add(Tuple.Create(a.StartsWith("Read and change all") ? IconData.Warning : IconData.Policy, a));
            try { return MaterialDialog.Ask(this, "Add \"" + name + "\"?", img, lines, "Add extension"); }
            finally { img?.Dispose(); }
        }

        void ShowExtensionPopup(string id, string page, bool fromTest = false)
        {
            var r = toolbar.ButtonRect("ext");
            var p = toolbar.PointToScreen(new Point(r.Right, r.Bottom + 2));
            lastPopup = new ExtensionPopup(env, "chrome-extension://" + id + "/" + page, p, url => NewTab(url)) { NoActivate = fromTest };
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
            }
            catch (Exception ex)
            {
                toolbar.StoreChipText = "Add to GBrowser";
                MaterialDialog.Info(this, "Not installed", ex.Message);
            }
            toolbar.LayoutNow();
        }

        // ---------- keyboard ----------

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
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
                case "bookmarks.delete": Bm.Remove(Lng(m, "id", 0)); return null;
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
                    return Bm.OtherBrowsers().Select((b, i) => new Dictionary<string, object> { { "i", i }, { "browser", b.Item1 }, { "profile", b.Item2 } }).ToList();
                case "bookmarks.importBrowser":
                {
                    var list = Bm.OtherBrowsers();
                    int i = (int)Lng(m, "i", -1);
                    if (i < 0 || i >= list.Count) throw new Exception("Not found");
                    string text;
                    // the browser may be running: read a shared copy
                    using (var fs = new FileStream(list[i].Item3, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var rd = new StreamReader(fs)) text = rd.ReadToEnd();
                    return Bm.ImportChromium(text, list[i].Item1);
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
                            if (tc == "gold" || tc == "accent" || System.Text.RegularExpressions.Regex.IsMatch(tc, "^#[0-9a-fA-F]{6}$")) st.ThemeColor = tc.ToLowerInvariant();
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
                        { "color", st.ThemeColor ?? "gold" }, { "accent", acc.HasValue ? Palette.Hex(acc.Value) : "" },
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
                        { "limited", d.Engine == "multi" && st.DownloadSpeedLimit > 0 } }).ToList();
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
                case "ext.install":
                {
                    var id = Extensions.ParseId(Str(m, "text"));
                    if (id == null) throw new Exception("No extension ID found");
                    var core = tab.View.CoreWebView2;
                    var ext = await Extensions.InstallFromStore(core.Profile, id, core.Settings.UserAgent, env.BrowserVersionString, ConfirmExtension);
                    if (ext == null) throw new Exception("Cancelled");
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
                        { "selected", strip.Selected }, { "address", toolbar.Address.Text }, { "title", Text },
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
                    var it = m?.Items.FirstOrDefault(i => i.Text == arg);
                    if (it == null) return "no item";
                    m.Close();
                    it.Click?.Invoke();
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
                case "wins":   // test: windows and their tabs (* = active)
                    return string.Join(" || ", App.Windows.Select((w, i) => i + (w == App.Active ? "*" : "") + " " + w.Bounds + ": " +
                        string.Join(", ", w.Tabs.Select(t => t.Created ? t.View.CoreWebView2.Source : t.Url))));
                case "movetab": MoveTabToNewWindow(int.Parse(arg), null, false); return "ok";
                case "mergetab": MoveTabTo(Current, App.Windows[int.Parse(arg)], -1); return "ok";
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
                case "extpopup":
                {
                    var pp = Extensions.PopupPage(Extensions.Manifest(arg));
                    if (pp == null) return "no popup";
                    ShowExtensionPopup(arg, pp, true);
                    await Task.Delay(2500);
                    return lastPopup.Handle.ToString() + " " + lastPopup.Width + "x" + lastPopup.Height;
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

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            devDock?.Shutdown();   // docked DevTools windows go back to the engine before the window closes
            bool last = App.Windows.Count <= 1;   // GBrowser closes with this window
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
