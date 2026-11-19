using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace GBrowser
{
    /// <summary>Ad blocking for one tab: network requests (WebResourceRequested), popups, tracking parameters, the
    /// block page, and the bridge the page script (assets/gbab.js) uses for cosmetic rules and scriptlets.</summary>
    internal sealed class Blocking
    {
        readonly BrowserTab tab;
        readonly CoreWebView2Environment env;
        public string PageUrl, PageHost;
        string pendingMainUrl, sessionAllowedHost;
        public event Action Changed;

        public Blocking(BrowserTab tab, CoreWebView2Environment env) { this.tab = tab; this.env = env; }

        // ---------------------------------------------------------------- hiding CSS the page cannot see
        // Element hiding used to add <style> elements, which a page can list (document.styleSheets, querySelectorAll).
        // Each document's hiding CSS now goes into a stylesheet made through the engine's DevTools interface
        // (CSS.createStyleSheet): it applies like any author sheet but is in neither the DOM nor the CSSOM
        // (measured 19-11-2026), and it stays after CSS / DOM tracking is switched off again, so tracking is on
        // only while a sheet is written. Frames get one too: frames of the page's own process by frame id, frames
        // in other processes through their own DevTools session (Target.attachToTarget). Rules sit in
        // "@layer gbh" so Stealth can lift them for one synchronous read (gbab.js unhidden()). When a frame cannot
        // be found the page script is told (cssok) and goes back to style elements, so hiding never stops.
        sealed class CssDoc
        {
            public string Doc, Href, Session, FrameId, SheetId;
            public bool Top, Dirty, Broken;
            public int Tries;
            public readonly StringBuilder Text = new StringBuilder();
        }
        CoreWebView2 core;
        System.Threading.SynchronizationContext ui;
        readonly Dictionary<string, CssDoc> cssDocs = new Dictionary<string, CssDoc>();
        readonly Dictionary<string, string> attached = new Dictionary<string, string>();   // targetId -> sessionId
        bool cssFlushing, cssAgain;
        public string CssState => string.Join(" | ", cssDocs.Values.Select(d => (d.Top ? "top" : "frame") + " " +
            (d.Session == null ? "" : "(session " + d.Session + ") ") + (d.SheetId ?? "-") + " " + d.Text.Length + (d.Broken ? " broken" : "") + " " +
            (d.Href.Length > 50 ? d.Href.Substring(0, 50) : d.Href)));

        /// <summary>From the page script: CSS to add for its document. False = use a style element instead.</summary>
        internal bool QueueCss(string doc, string href, bool top, string css)
        {
            if (core == null || ui == null || string.IsNullOrEmpty(doc) || string.IsNullOrEmpty(href)) return false;
            if (!cssDocs.TryGetValue(doc, out var d))
            {
                // a new top document: the previous page and its frames are gone
                if (top) { cssDocs.Clear(); attached.Clear(); }
                if (cssDocs.Count >= 64) return false;
                d = new CssDoc { Doc = doc, Href = href, Top = top };
                cssDocs[doc] = d;
#if AUTOMATION
                TestLog.Add("css new doc " + doc + (top ? " top " : " frame ") + href + " (blocker " + GetHashCode() + ")");
#endif
            }
            if (d.Broken) return false;
            if (css.Length > 0) d.Text.Append(css).Append('\n');
            d.Dirty = true;
            ui.Post(_ => FlushCss(), null);
            return true;
        }

        /// <summary>Hiding CSS for this tab's top document right away (a rule just made with the element picker).</summary>
        internal bool HideNow(string css)
        {
            var d = cssDocs.Values.FirstOrDefault(x => x.Top && !x.Broken);
            return d != null && QueueCss(d.Doc, d.Href, true, css);
        }

        internal bool CssOk(string doc)
        {
            bool ok = cssDocs.TryGetValue(doc ?? "", out var d) && !d.Broken;
#if AUTOMATION
            TestLog.Add("cssok " + doc + " " + ok + " (blocker " + GetHashCode() + ")" + (d == null ? " (unknown doc; known: " + string.Join(",", cssDocs.Keys) + ")" : ""));
#endif
            return ok;
        }

        Task<string> Cdp(string session, string method, object args) =>
            session == null ? core.CallDevToolsProtocolMethodAsync(method, Store.ToJson(args))
                            : core.CallDevToolsProtocolMethodForSessionAsync(session, method, Store.ToJson(args));

        static readonly Dictionary<string, object> NoArgs = new Dictionary<string, object>();
        static Dictionary<string, object> Obj(object o) => o as Dictionary<string, object>;
        // the JSON reader gives nested arrays as ArrayList, top-level ones as object[]
        static IEnumerable<object> List(object o) => o is System.Collections.IEnumerable e && !(o is string) ? e.Cast<object>() : Enumerable.Empty<object>();

        /// <summary>Frames (id, url) of a frame tree, depth first.</summary>
        static IEnumerable<KeyValuePair<string, string>> FramesOf(Dictionary<string, object> node)
        {
            var f = Obj(node?["frame"]);
            if (f != null) yield return new KeyValuePair<string, string>(f["id"] as string, f.TryGetValue("url", out var u) ? u as string : "");
            if (node != null && node.TryGetValue("childFrames", out var kids))
                foreach (var k in List(kids)) foreach (var x in FramesOf(Obj(k))) yield return x;
        }

        async Task<bool> Resolve(CssDoc d)
        {
            var claimed = new HashSet<string>(cssDocs.Values.Where(x => x != d && x.FrameId != null).Select(x => (x.Session ?? "") + "/" + x.FrameId));
            var tree = Obj(Store.FromJson<Dictionary<string, object>>(await Cdp(null, "Page.getFrameTree", NoArgs))["frameTree"]);
            var frames = FramesOf(tree).ToList();
            if (d.Top) { d.FrameId = frames[0].Key; return true; }
            // a frame of the page's own process
            foreach (var f in frames.Skip(1))
                if (f.Value == d.Href && !claimed.Contains("/" + f.Key)) { d.FrameId = f.Key; return true; }
            // a frame in another process: its own DevTools target
            var targets = Store.FromJson<Dictionary<string, object>>(await Cdp(null, "Target.getTargets", NoArgs))["targetInfos"];
            foreach (var t in List(targets))
            {
                var ti = Obj(t);
                if (ti == null || ti["type"] as string != "iframe" || ti["url"] as string != d.Href) continue;
                string tid = ti["targetId"] as string;
                if (!attached.TryGetValue(tid, out var session))
                {
                    var a = Store.FromJson<Dictionary<string, object>>(await Cdp(null, "Target.attachToTarget",
                        new Dictionary<string, object> { { "targetId", tid }, { "flatten", true } }));
                    session = a["sessionId"] as string;
                    attached[tid] = session;
                }
                var st = Obj(Store.FromJson<Dictionary<string, object>>(await Cdp(session, "Page.getFrameTree", NoArgs))["frameTree"]);
                string fid = FramesOf(st).First().Key;
                if (claimed.Contains(session + "/" + fid)) continue;
                d.Session = session; d.FrameId = fid;
                return true;
            }
            return false;
        }

        async void RetryCss(CssDoc d)
        {
            await Task.Delay(250);
            if (!cssDocs.ContainsKey(d.Doc) || d.Broken) return;
            d.Dirty = true;
            FlushCss();
        }

        async void FlushCss()
        {
            if (cssFlushing) { cssAgain = true; return; }
            cssFlushing = true;
            try
            {
                do
                {
                    cssAgain = false;
                    foreach (var d in cssDocs.Values.Where(x => x.Dirty && !x.Broken).ToList())
                    {
                        d.Dirty = false;
                        try
                        {
                            if (d.FrameId == null && !await Resolve(d))
                            {
                                // the frame's script runs as its document starts, before the browser side lists the
                                // frame under its new address: look again for about 2 s before giving up
                                if (++d.Tries < 8) { RetryCss(d); continue; }
                                d.Broken = true; TestNote("css frame not found: " + d.Href); continue;
                            }
                            await Cdp(d.Session, "DOM.enable", NoArgs);
                            await Cdp(d.Session, "CSS.enable", NoArgs);
                            try
                            {
                                if (d.SheetId == null)
                                    d.SheetId = Store.FromJson<Dictionary<string, object>>(await Cdp(d.Session, "CSS.createStyleSheet",
                                        new Dictionary<string, object> { { "frameId", d.FrameId } }))["styleSheetId"] as string;
                                await Cdp(d.Session, "CSS.setStyleSheetText",
                                    new Dictionary<string, object> { { "styleSheetId", d.SheetId }, { "text", "@layer gbh{\n" + d.Text + "}" } });
                            }
                            finally
                            {
                                try { await Cdp(d.Session, "CSS.disable", NoArgs); } catch { }
                                try { await Cdp(d.Session, "DOM.disable", NoArgs); } catch { }
                            }
                        }
                        catch (Exception ex)
                        {
                            // a document replaced while writing, or a frame that went away: the page script falls back
                            d.Broken = true;
                            TestNote("css sheet: " + ex.Message + " " + d.Href);
                        }
                    }
                } while (cssAgain);
            }
            finally { cssFlushing = false; }
        }
        PageBridge bridge;

        // ---------------------------------------------------------------- same-origin frames without the page script
        // Measured 19-11-2026: a same-origin frame that a script creates and then navigates (iframe src="/widget")
        // keeps the window of its first about:blank document. Chromium runs no document-created script for the new
        // document (WebView2's and DevTools' addScriptToEvaluateOnNewDocument alike), and chrome.webview does not come
        // back there even when the host object is added again. Scriptlets carry over with the window; element
        // hiding does not. Such a frame gets its site's hiding rules from here, in the same invisible sheet.
        readonly Dictionary<string, DateTime> initSeen = new Dictionary<string, DateTime>();
        internal void NoteInit(string href)
        {
            if (initSeen.Count > 200) initSeen.Clear();
            initSeen[href ?? ""] = DateTime.UtcNow;
        }

        void WatchFrame(CoreWebView2Frame fr)
        {
            string uri = null; DateTime started = DateTime.MinValue;
            try
            {
                fr.NavigationStarting += (s, e) => { uri = e.Uri; started = DateTime.UtcNow; };
                fr.NavigationCompleted += (s, e) =>
                {
                    try
                    {
                        if (!e.IsSuccess || uri == null || !uri.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
                        if (initSeen.TryGetValue(uri, out var t) && t >= started.AddSeconds(-1)) return;
                        var eng = FilterEngine.Instance; string host = HostOf(uri);
                        if (eng == null || host == null || !Store.Settings.HideAdLeftovers || !OnFor(host)) return;
                        string init = eng.CosmeticInit(uri, host, Groups());
                        string css = init.Length > 1 ? init.Substring(1) : "";
                        TestNote("frame without page script: " + uri + " (" + css.Length + " chars of hiding)");
                        if (css.Length > 0) QueueCss("f" + Guid.NewGuid().ToString("N"), uri, false, css);
                    }
                    catch { }
                };
            }
            catch { }
        }
#if AUTOMATION
        public static readonly List<string> TestLog = new List<string>();
        public static string TestDbg = "";   // test builds: page-script parts to skip (letters, see gbab.js)
#if AUTOMATION
        public readonly List<CoreWebView2Frame> TestFrames = new List<CoreWebView2Frame>();
        public async Task<string> TestFramesExec(string js, bool readd)
        {
            var sb = new StringBuilder();
            foreach (var f in TestFrames.ToList())
            {
                try
                {
                    if (readd) f.AddHostObjectToScript("gb", bridge, new[] { "*" });
                    sb.Append(f.Name).Append(": ").Append(await f.ExecuteScriptAsync(js)).Append(" | ");
                }
                catch (Exception ex) { sb.Append("err ").Append(ex.Message).Append(" | "); }
            }
            return sb.ToString();
        }
#endif
        public static string[] TestScriptlets;   // test builds: scriptlet calls for pages whose address has slcase=<line>
#endif
        /// <summary>A note for the test channel's log; nothing in normal builds.</summary>
        static void TestNote(string s)
        {
#if AUTOMATION
            TestLog.Add(s);
#endif
        }

        static Settings S => Store.Settings;

        /// <summary>The filter lists in use, as the engine's list mask: the switched-on groups' lists minus the ones
        /// turned off in Settings > Filter lists.</summary>
        public static int Groups()
        {
            int g = FilterEngine.G_ADS;
            if (S.BlockAnnoyances) g |= FilterEngine.G_ANNOY;
            if (S.BlockTrackers) g |= FilterEngine.G_TRACK;
            var e = FilterEngine.Instance;
            return e == null ? 0 : e.Mask(g, S.DisabledLists);
        }

        /// <summary>Tracker lists (for cleaning tracking codes from links opened from other apps, always on).</summary>
        public static int TrackerLists() => FilterEngine.Instance?.Mask(FilterEngine.G_TRACK, S.DisabledLists) ?? 0;

        public static bool OnFor(string host)
        {
            if (!S.AdblockEnabled || string.IsNullOrEmpty(host)) return false;
            var e = FilterEngine.Instance;
            string site = e != null ? e.Registrable(host) : host;
            return !S.AllowedSites.Contains(site);
        }

        public static string SiteOf(string url)
        {
            string h = HostOf(url);
            if (h == null) return null;
            var e = FilterEngine.Instance;
            return e != null ? e.Registrable(h) : h;
        }

        public static string HostOf(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            try { var u = new Uri(url); return u.Scheme == "http" || u.Scheme == "https" || u.Scheme == "ws" || u.Scheme == "wss" ? u.IdnHost.ToLowerInvariant() : null; }
            catch { return null; }
        }

        // ---------------------------------------------------------------- request log (shield menu > Request log)
        public sealed class LogEntry { public long Time; public string Url, Type, Result, Page; }
        readonly LinkedList<LogEntry> log = new LinkedList<LogEntry>();
        public List<LogEntry> LogSnapshot() { lock (log) return log.ToList(); }
        void Record(string url, int type, string result)
        {
            var e = new LogEntry { Time = Store.Now(), Url = url, Type = TypeName(type), Result = result, Page = PageHost };
            lock (log) { log.AddLast(e); while (log.Count > 500) log.RemoveFirst(); }
        }
        static string TypeName(int t)
        {
            switch (t)
            {
                case FilterEngine.T_SCRIPT: return "script"; case FilterEngine.T_IMAGE: return "image"; case FilterEngine.T_CSS: return "stylesheet";
                case FilterEngine.T_XHR: return "xhr / fetch"; case FilterEngine.T_MEDIA: return "media"; case FilterEngine.T_FONT: return "font";
                case FilterEngine.T_SUBDOC: return "frame"; case FilterEngine.T_WEBSOCKET: return "websocket"; case FilterEngine.T_PING: return "ping";
                case FilterEngine.T_DOCUMENT: return "page"; case FilterEngine.T_POPUP: return "popup"; default: return "other";
            }
        }

        void Count()
        {
            tab.Blocked++;
            S.TotalBlocked++;
            Changed?.Invoke();
        }

        // ---------------------------------------------------------------- setup

        public void Attach(CoreWebView2 core)
        {
            this.core = core;
            ui = System.Threading.SynchronizationContext.Current;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnRequest;
            core.NavigationStarting += OnNavigationStarting;
            core.ContentLoading += OnContentLoading;
            core.NavigationCompleted += OnNavigationCompleted;
            core.FrameNavigationStarting += OnFrameNavigationStarting;
            var bridge = new PageBridge(this);
            core.AddHostObjectToScript("gb", bridge);
            this.bridge = bridge;
            core.FrameCreated += (s, e) =>
            {
                try { e.Frame.AddHostObjectToScript("gb", bridge, new[] { "*" }); } catch { }
                WatchFrame(e.Frame);
#if AUTOMATION
                var fr = e.Frame;
                TestFrames.Add(fr);
                fr.Destroyed += (s2, e2) => TestFrames.Remove(fr);
#endif
            };
            _ = core.AddScriptToExecuteOnDocumentCreatedAsync(PageScript());
        }

        /// <summary>Blocked frames (any depth) are cancelled before their request. Measured 18-11-2026 (WebView2 157):
        /// answering a blocked frame with an empty page (or 204) commits a document at the ad's address, and site
        /// isolation then keeps a renderer process (~6.6 MB) per ad site; a cancelled frame stays an empty frame in
        /// its parent's process. Frames whose rule names a $redirect stub still load it through OnRequest.</summary>
        void OnFrameNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            string url = e.Uri ?? "";
#if AUTOMATION
            if (url.Contains(".invalid/cancel/")) { e.Cancel = true; TestLog.Add("frame cancelled: " + url); return; }
#endif
            string host = HostOf(url);
            if (host == null || !S.AdblockEnabled) return;
            if (PageHost != null && !OnFor(PageHost)) return;
            var eng = FilterEngine.Instance;
            if (eng == null) return;
            string decision = eng.Match(url, host, PageHost, FilterEngine.T_SUBDOC, Groups());
            if (decision == null || (decision.Length > 0 && eng.Redirect(decision) != null)) return;
            e.Cancel = true;
            Record(url, FilterEngine.T_SUBDOC, "blocked");
            Count();
        }

        void OnNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            var core = (CoreWebView2)sender;
            string uri = e.Uri ?? "";
#if AUTOMATION
            TestLog.Add("nav " + uri + " user=" + e.IsUserInitiated + " redirect=" + e.IsRedirected + " kind=" + e.NavigationKind);
#endif
            // only GBrowser's own pages may message the app (the bridge also checks the sender)
            try { core.Settings.IsWebMessageEnabled = Pages.IsInternal(uri); } catch { }
            // HTTPS-First: http:// addresses open over https://; sites without HTTPS get a warning page first
            if (S.HttpsFirst && uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                string h = HostOf(uri);
                if (h != null && !Security.IsLocal(h) && !Security.HttpAllowed(h))
                {
                    e.Cancel = true;
                    // the https address sent us back to http (a site that redirects to http): no secure version
                    if (e.IsRedirected && h == upgradedHost && (DateTime.UtcNow - upgradedAt).TotalSeconds < 20)
                        core.Navigate(Pages.Url("insecure") + "?u=" + Uri.EscapeDataString(uri));
                    else
                    {
                        upgradedHost = h; upgradedAt = DateTime.UtcNow; upgradedFrom = uri;
                        upgradedTo = "https://" + uri.Substring(7);
                        core.Navigate(upgradedTo);
                    }
                    return;
                }
            }
            if (upgradedTo != null && uri == upgradedTo) upgradedNavId = e.NavigationId;
            // "Open anyway" on the block page
            if (uri.StartsWith(Pages.Origin + "allow?", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                string target = null;
                try { target = Uri.UnescapeDataString(uri.Substring(uri.IndexOf("u=", StringComparison.Ordinal) + 2)); } catch { }
                if (target != null && (target.StartsWith("http://") || target.StartsWith("https://")))
                {
                    sessionAllowedHost = HostOf(target);
                    core.Navigate(target);
                }
                return;
            }
            // tracking parameters / tracking redirect links: open the clean address instead
            var eng = FilterEngine.Instance;
            string host = HostOf(uri);
            if (eng != null && host != null && OnFor(host) && S.BlockTrackers && !e.IsRedirected)
            {
                string clean = eng.CleanUrl(uri, null, Groups() | TrackerLists());
                if (clean != uri)
                {
                    e.Cancel = true;
                    core.Navigate(clean);
                    return;
                }
            }
            pendingMainUrl = uri;
            // the page (and its count) changes only when the new document starts loading (OnContentLoading):
            // a navigation can also end as a download, which keeps the current page
            if (PageHost == null) { PageUrl = uri; PageHost = host; }
        }

        string upgradedHost, upgradedFrom, upgradedTo;
        DateTime upgradedAt;
        ulong upgradedNavId;

        /// <summary>The upgraded https:// address failed (no HTTPS, bad certificate ...): ask before using http.</summary>
        void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (upgradedFrom == null || e.NavigationId != upgradedNavId) return;
            string from = upgradedFrom;
            upgradedFrom = upgradedTo = null;
            upgradedNavId = 0;
            if (e.IsSuccess || e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled) return;
            ((CoreWebView2)sender).Navigate(Pages.Url("insecure") + "?u=" + Uri.EscapeDataString(from));
        }

        void OnContentLoading(object sender, CoreWebView2ContentLoadingEventArgs e)
        {
            var core = (CoreWebView2)sender;
            PageUrl = core.Source;
            PageHost = HostOf(core.Source);
            tab.Blocked = 0;
            // a new page: its own log (the request for the page itself stays)
            lock (log) { var keep = log.LastOrDefault(x => x.Type == "page"); log.Clear(); if (keep != null && keep.Url == PageUrl) log.AddLast(keep); }
            Changed?.Invoke();
        }

        static int TypeOf(CoreWebView2WebResourceContext c)
        {
            switch (c)
            {
                case CoreWebView2WebResourceContext.Stylesheet: return FilterEngine.T_CSS;
                case CoreWebView2WebResourceContext.Image: return FilterEngine.T_IMAGE;
                case CoreWebView2WebResourceContext.Media: return FilterEngine.T_MEDIA;
                case CoreWebView2WebResourceContext.Font: return FilterEngine.T_FONT;
                case CoreWebView2WebResourceContext.Script: return FilterEngine.T_SCRIPT;
                case CoreWebView2WebResourceContext.XmlHttpRequest:
                case CoreWebView2WebResourceContext.Fetch:
                case CoreWebView2WebResourceContext.EventSource: return FilterEngine.T_XHR;
                case CoreWebView2WebResourceContext.Websocket: return FilterEngine.T_WEBSOCKET;
                case CoreWebView2WebResourceContext.Ping: return FilterEngine.T_PING;
                case CoreWebView2WebResourceContext.TextTrack: return FilterEngine.T_MEDIA;
                case CoreWebView2WebResourceContext.Document: return FilterEngine.T_SUBDOC;
                default: return FilterEngine.T_OTHER;
            }
        }

        /// <summary>fetch / XHR / "other" requests say little about what they load: like the Android app
        /// (Blocker.requestType), look at the Accept header and the file extension; otherwise T_UNKNOWN
        /// (script | xhr | other | ping), so rules for any of those kinds still apply.</summary>
        static int GuessType(CoreWebView2WebResourceRequest r, int context)
        {
            string accept = null;
            try { if (r.Headers.Contains("Accept")) accept = r.Headers.GetHeader("Accept"); } catch { }
            if (accept != null)
            {
                if (accept.StartsWith("text/css")) return FilterEngine.T_CSS;
                if (accept.StartsWith("image/")) return FilterEngine.T_IMAGE;
                if (accept.Contains("video/") || accept.Contains("audio/")) return FilterEngine.T_MEDIA;
            }
            string path;
            try { path = new Uri(r.Uri).AbsolutePath; } catch { path = ""; }
            int dot = path.LastIndexOf('.');
            if (dot > path.LastIndexOf('/'))
                switch (path.Substring(dot + 1).ToLowerInvariant())
                {
                    case "js": case "mjs": return FilterEngine.T_SCRIPT;
                    case "css": return FilterEngine.T_CSS;
                    case "png": case "jpg": case "jpeg": case "gif": case "webp": case "svg": case "ico": case "avif": case "bmp": return FilterEngine.T_IMAGE;
                    case "woff": case "woff2": case "ttf": case "otf": case "eot": return FilterEngine.T_FONT;
                    case "mp4": case "webm": case "m3u8": case "mpd": case "ts": case "m4s": case "mp3": case "aac": case "m4a": case "ogg": return FilterEngine.T_MEDIA;
                    case "swf": return FilterEngine.T_OBJECT;
                }
            try { if (r.Headers.Contains("Range")) return FilterEngine.T_MEDIA; } catch { }
            return FilterEngine.T_UNKNOWN;
        }

        void OnRequest(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            string url = e.Request.Uri;
            if (S.DoNotTrack) try { e.Request.Headers.SetHeader("DNT", "1"); } catch { }
            string host = HostOf(url);
#if AUTOMATION
            // test builds: https://gb-block-test.invalid/<variant>/... answers with one kind of "blocked" response
            if (host == "gb-block-test.invalid" || host == "gb-block-test2.invalid")
            {
                string v = new Uri(url).AbsolutePath.Split('/')[1];
                try
                {
                    switch (v)
                    {
                        case "fail": e.Response = Fail(); break;
                        case "s403": e.Response = env.CreateWebResourceResponse(null, 403, "Forbidden", ""); break;
                        case "s0": e.Response = env.CreateWebResourceResponse(null, 0, "", ""); break;
                        case "s999": e.Response = env.CreateWebResourceResponse(null, 999, "Blocked", ""); break;
                        case "nullok": e.Response = env.CreateWebResourceResponse(null, 200, "OK", ""); break;
                        case "r0000": e.Response = env.CreateWebResourceResponse(null, 307, "Temporary Redirect", "Location: https://0.0.0.0/"); break;
                        case "rscheme": e.Response = env.CreateWebResourceResponse(null, 307, "Temporary Redirect", "Location: gbblocked:x"); break;
                        case "rabout": e.Response = env.CreateWebResourceResponse(null, 307, "Temporary Redirect", "Location: about:blank"); break;
                        case "rinvalid": e.Response = env.CreateWebResourceResponse(null, 307, "Temporary Redirect", "Location: https://blocked.invalid/"); break;
                        case "e200": e.Response = EmptyFrame(); break;
                        case "s204": e.Response = env.CreateWebResourceResponse(null, 204, "No Content", ""); break;
                        case "page":
                        {
                            // a cross-site page that itself holds a frame of variant ?f= on another test site (nested frames)
                            string f = url.Contains("?f=") ? url.Substring(url.IndexOf("?f=") + 3) : "";
                            string html = "<!doctype html><title>t</title><p>frame</p>" +
                                (f.Length > 0 ? "<iframe src=\"https://gb-block-test2.invalid/" + f + "/nested\"></iframe>" : "");
                            e.Response = env.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(html)), 200, "OK", "Content-Type: text/html; charset=utf-8");
                            break;
                        }
                    }
                }
                catch (Exception ex) { TestLog.Add(v + ": " + ex.GetType().Name + " " + ex.Message); }
                return;
            }
#endif
            if (host == null || !S.AdblockEnabled) return;
            bool main = e.ResourceContext == CoreWebView2WebResourceContext.Document && url == pendingMainUrl;
            string page = main ? host : PageHost;   // a new page is judged by its own site's switch
            if (page != null && !OnFor(page)) return;
            var eng = FilterEngine.Instance;
            if (eng == null) return;   // still loading (about 1 s after start)
            if (main && (!S.BlockAdSitePages || host == sessionAllowedHost)) return;
            int type = main ? FilterEngine.T_DOCUMENT : TypeOf(e.ResourceContext);
            if (!main && (type == FilterEngine.T_XHR || type == FilterEngine.T_OTHER))
                type = GuessType(e.Request, type);
            string decision = eng.Match(url, host, main ? null : PageHost, type, Groups());
            if (decision == null) { Record(url, type, "allowed"); return; }
            if (main) { Record(url, type, "page blocked"); e.Response = BlockPage(url, host); return; }
            Count();
            if (decision.Length > 0)
            {
                var res = eng.Redirect(decision);
                if (res != null) { Record(url, type, "replaced (" + decision + ")"); e.Response = Stub(res); return; }
            }
            Record(url, type, "blocked");
            e.Response = type == FilterEngine.T_SUBDOC ? EmptyFrame() : Fail();
        }

        /// <summary>window.open / target=_blank: true when it must not open (ad popups, popups without a click).</summary>
        public bool BlockPopup(string url, bool userInitiated)
        {
            if (!S.AdblockEnabled || (PageHost != null && !OnFor(PageHost))) return false;
            var eng = FilterEngine.Instance;
            string host = HostOf(url);
            bool block = !userInitiated;
            if (!block && eng != null && host != null)
                block = eng.Match(url, host, PageHost, FilterEngine.T_POPUP | FilterEngine.T_DOCUMENT, Groups()) != null;
            if (block) Count();
            return block;
        }

        // ---------------------------------------------------------------- responses

        /// <summary>Blocked request = network error, as with Chrome's own blocking. Measured 2026-11-18 (WebView2 157):
        /// any response WebView2 gets counts as the server's answer (no-cors fetch resolves, and with a failing body a
        /// script element even fires "load"); a 307 redirect to about:blank is refused by Chromium as an unsafe
        /// redirect, so fetch (cors and no-cors) rejects and img/script fire "error". No network request, no DNS.</summary>
        CoreWebView2WebResourceResponse Fail() => env.CreateWebResourceResponse(null, 307, "Temporary Redirect", "Location: about:blank");

        CoreWebView2WebResourceResponse Stub(FilterEngine.Resource r)
        {
            string mime = r.Mime;
            int semi = mime.IndexOf(';');
            if (semi > 0) mime = mime.Substring(0, semi).Trim();
            bool text = mime.StartsWith("text/") || mime.Contains("javascript") || mime.Contains("json") || mime.Contains("xml");
            return env.CreateWebResourceResponse(new MemoryStream(r.Body), 200, "OK",
                "Content-Type: " + mime + (text ? "; charset=utf-8" : "") + "\nAccess-Control-Allow-Origin: *\nCache-Control: no-store");
        }

        CoreWebView2WebResourceResponse EmptyFrame() =>
            env.CreateWebResourceResponse(new MemoryStream(new byte[0]), 200, "OK", "Content-Type: text/html; charset=utf-8\nCache-Control: no-store");

        CoreWebView2WebResourceResponse BlockPage(string url, string host)
        {
            string allow = Pages.Origin + "allow?u=" + Uri.EscapeDataString(url);
            string html = "<!doctype html><html><head><meta charset=utf-8><title>Blocked</title><style>"
                + "body{margin:0;font:15px 'Segoe UI',system-ui,sans-serif;display:flex;align-items:center;justify-content:center;"
                + "min-height:100vh;background:#fff8f2;color:#1f1b13}main{text-align:center;padding:24px;max-width:460px}"
                + "svg{width:72px;height:72px;fill:#ba1a1a}h1{font-size:24px;font-weight:500;margin:16px 0 4px}"
                + "p{color:#4d4639;margin:0 0 28px;word-break:break-all}a{display:inline-block;padding:10px 24px;border-radius:20px;"
                + "border:1px solid #7f7667;color:#785a00;text-decoration:none;font-weight:600}a:hover{background:#f4ede1}"
                + "@media(prefers-color-scheme:dark){body{background:#17130b;color:#ece1d4}p{color:#d3c5b0}svg{fill:#ffb4ab}"
                + "a{color:#f0bf48;border-color:#9c8f7c}a:hover{background:#241f17}}</style></head><body><main>"
                + "<svg viewBox='0 -960 960 960'><path d='" + IconData.Block + "'/></svg>"
                + "<h1>Blocked</h1><p>" + System.Net.WebUtility.HtmlEncode(host) + "</p>"
                + "<a href=\"" + System.Net.WebUtility.HtmlEncode(allow) + "\">Open anyway</a></main></body></html>";
            return env.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(html)), 200, "OK",
                "Content-Type: text/html; charset=utf-8\nCache-Control: no-store");
        }

        // ---------------------------------------------------------------- page script

        static string script;

        /// <summary>Web pages must not see WebView2's chrome.webview (it marks the browser as an embedded app and
        /// would let page scripts reach the host bridge). The page script already holds its own handle; this runs
        /// after it, before any page script. GBrowser's own pages and extension pages keep it.</summary>
        const string HideWebView = "(function(){try{var o=location.origin;if(o==='" + "https://" + Pages.Host + "'||location.protocol==='chrome-extension:')return;" +
            "var c=window.chrome;if(c&&('webview' in c)){try{delete c.webview;}catch(e){}" +
            "if('webview' in c){try{Object.defineProperty(c,'webview',{value:undefined,configurable:true,enumerable:false});}catch(e){}}}}catch(e){}})();";

        /// <summary>The AdGuard scriptlet library without its page-visible marks (measured 19-11-2026, tools/local/slaudit):
        /// every scriptlet calls window.__debug(details) when it acts, so a page that defines that function is told
        /// each time we block something; trusted-click-element and href-sanitizer leave Symbol.for("adg-...") /
        /// ("adguard...") registry symbols on EventTarget.prototype and links; close-window fires "adguard:..." events.
        /// Replaced with holders inside the page script (adgSpoof, adgHrefKey) and random event names.</summary>
        internal static string QuietLib(string lib)
        {
            if (lib.Length == 0) return lib;
            string rnd = Guid.NewGuid().ToString("N");
            return lib
                .Replace("\"function\"==typeof window.__debug&&window.__debug(e)", "void 0")
                .Replace("Object.defineProperty(EventTarget.prototype,r,{value:e,writable:!1,enumerable:!1,configurable:!1})",
                         "(function(){if(adgSpoof.v)throw 0;adgSpoof.v=e})()")
                .Replace("var t=EventTarget.prototype[r];", "var t=adgSpoof.v;")
                .Replace("Symbol.for(\"adg-spoof-click-isTrusted\")", "0")
                .Replace("Symbol.for(\"adguard.href-sanitizer.writes\")", "adgHrefKey")
                .Replace("adguard:scriptlet-close-window", "x" + rnd.Substring(0, 12))
                .Replace("adguard:subscribed-to-close-window", "x" + rnd.Substring(12, 12));
        }

        /// <summary>assets/gbab.js with the scriptlet library, AdGuard JS rules and ExtendedCss filled in, behind a
        /// small shim that hands it the bridge (chrome.webview.hostObjects.sync.gb) under the name it expects.</summary>
        static string PageScript()
        {
            if (script != null) return script;
            var eng = FilterEngine.Instance;
            try
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets");
                string gbab = File.ReadAllText(Path.Combine(dir, "gbab.js"), Encoding.UTF8);
                string ec = File.ReadAllText(Path.Combine(dir, "extcss.js"), Encoding.UTF8);
                string lib = QuietLib(eng?.Scriptlets ?? ""), raw = eng?.RawJs ?? "";
                gbab = gbab.Replace("/*SCRIPTLETS*/null", lib.Length == 0 ? "null" : lib)
                           .Replace("/*RAWJS*/null", raw.Length == 0 ? "null" : raw)
                           .Replace("/*EXTCSS*/null", ec.Length == 0 ? "null" : "(function(){" + ec + "\n;return ExtendedCss;})()");
                // WebView2's bridge looks up window.chrome.webview at every call, and HideWebView deletes it after this
                // script (measured 19-11-2026: every later call then failed with "Cannot read properties of undefined
                // (reading 'hostObjects')", so hiding for elements added later and collapsing blocked frames never
                // happened). Each call puts chrome.webview back for its own duration only and restores whatever the
                // page had there; no page code runs in between, and only built-ins captured now are used.
                const string shim = "(function(){try{var ch=window.chrome,w=ch&&ch.webview;var h=w&&w.hostObjects&&w.hostObjects.sync&&w.hostObjects.sync.gb;" +
                    "if(!h)return;var gd=Object.getOwnPropertyDescriptor,dp=Object.defineProperty,rd=Reflect.deleteProperty," +
                    "ap=Function.prototype.call.bind(Function.prototype.apply);" +
                    "var c=function(n){return function(){var d0=null,put=false;try{d0=gd(ch,'webview');" +
                    "if(!d0||d0.value!==w){dp(ch,'webview',{value:w,writable:true,enumerable:true,configurable:true});put=true;}" +
                    "return ap(h[n],h,arguments);}catch(e){return '';}finally{if(put){try{if(d0)dp(ch,'webview',d0);else rd(ch,'webview');}catch(e){}}}};};" +
                    "Object.defineProperty(window,'__gbAdblock',{value:{init:c('init'),js:c('js'),extra:c('extra'),rr:c('rr'),dbg:c('dbg')," +
                    "dnt:c('dnt'),p:c('p'),keys:c('keys'),blocked:c('blocked'),css:c('css'),cssok:c('cssok')},configurable:true,writable:true});}catch(e){}})();\n";
                script = shim + gbab + "\n" + HideWebView;
            }
            catch { script = HideWebView; }
            return script;
        }
    }

    // ---------------------------------------------------------------- bridge (called by gbab.js, synchronously)

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.AutoDual)]
    public sealed class PageBridge
    {
        readonly Blocking b;
        internal PageBridge(Blocking b) { this.b = b; }

        static Settings S => Store.Settings;
        static string Host(string href) => Blocking.HostOf(href);

        public string init(string href)
        {
            if (!S.HideAdLeftovers) return "";
            string host = Host(href); var e = FilterEngine.Instance;
            if (host == null || e == null || !Blocking.OnFor(host)) return "";
            b.NoteInit(href);
            return (S.Stealth ? "S" : "s") + e.CosmeticInit(href, host, Blocking.Groups());
        }

        public string js(string href)
        {
#if AUTOMATION
            var tc = Blocking.TestScriptlets;
            int ci = href.IndexOf("slcase=");
            if (tc != null && ci >= 0)
            {
                int n = 0, k = ci + 7;
                while (k < href.Length && char.IsDigit(href[k])) n = n * 10 + (href[k++] - '0');
                return n < tc.Length && tc[n].Length > 0 ? "[" + tc[n] + "]" : "";
            }
#endif
            string host = Host(href); var e = FilterEngine.Instance;
            return host == null || e == null || !Blocking.OnFor(host) ? "" : e.ScriptletCalls(href, host, Blocking.Groups());
        }

        public string extra(string href)
        {
            string host = Host(href); var e = FilterEngine.Instance;
            return host == null || e == null || !Blocking.OnFor(host) ? "" : e.Extra(href, host, Blocking.Groups());
        }

        /// <summary>$cookie / $csp rules for this page: {"c":[cookie specs],"p":[csp values]} or "".</summary>
        public string rr(string href)
        {
            string host = Host(href); var e = FilterEngine.Instance;
            if (host == null || e == null) return "";
            // the lists' $cookie / $csp rules only where ad blocking is on; your cookie rules always
            var rules = Blocking.OnFor(host) ? e.ResponseRules(href, host, host, 0xFFF, Blocking.Groups()) : new List<FilterEngine.RespRule>();
            var c = new StringBuilder(); var p = new StringBuilder();
            foreach (var r in rules)
            {
                if (r.Kind == "cookie") { if (c.Length > 0) c.Append(','); FilterEngine.JsonString(c, r.Value); }
                else if (r.Kind == "csp" && r.Value.Length > 0) { if (p.Length > 0) p.Append(','); FilterEngine.JsonString(p, r.Value); }
            }
            // cookies you blocked for this site (Settings / the cookie manager): the page script hides and refuses them
            foreach (var n in Cookies.BlockedNames(host)) { if (c.Length > 0) c.Append(','); FilterEngine.JsonString(c, n); }
            if (c.Length == 0 && p.Length == 0) return "";
            return "{\"c\":[" + c + "],\"p\":[" + p + "]}";
        }

#if AUTOMATION
        public string dbg() => Blocking.TestDbg;
#else
        public string dbg() => "";
#endif
        public bool css(string doc, string href, bool top, string css) => b.QueueCss(doc, href, top, css);
        public bool cssok(string doc) => b.CssOk(doc);
        public bool dnt() => S.DoNotTrack;
        public string p(string href) => "";   // privacy masking: WebView2 already is Edge, its values are genuine

        public string keys(string href, string keys)
        {
            if (!S.HideAdLeftovers) return "";
            string host = Host(href); var e = FilterEngine.Instance;
            return host == null || e == null ? "" : e.CosmeticKeys(href, host, keys, Blocking.Groups());
        }

        public bool blocked(string href, string url, string tag)
        {
            var e = FilterEngine.Instance;
            string page = Host(href), host = Host(url);
            if (e == null || host == null || page == null || !Blocking.OnFor(page)) return false;
            return e.ShouldBlock(url, host, page, TagType(tag), Blocking.Groups());
        }

        static int TagType(string tag)
        {
            switch (tag)
            {
                case "IMG": case "INPUT": return FilterEngine.T_IMAGE;
                case "IFRAME": case "FRAME": return FilterEngine.T_SUBDOC;
                case "VIDEO": case "AUDIO": return FilterEngine.T_MEDIA;
                case "EMBED": case "OBJECT": return FilterEngine.T_OBJECT;
                case "SCRIPT": return FilterEngine.T_SCRIPT;
                default: return FilterEngine.T_UNKNOWN;
            }
        }
    }
}
