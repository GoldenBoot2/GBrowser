using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
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
        // The main document's hiding CSS now goes into a stylesheet made through the engine's DevTools interface
        // (CSS.createStyleSheet): it applies like any author sheet but is in neither the DOM nor the CSSOM
        // (measured 19-11-2026), and it stays after CSS / DOM tracking is switched off again, so tracking is on
        // only while the sheet is written. Rules sit in "@layer gbh" so Stealth can lift them for one synchronous
        // read with an adopted override sheet (gbab.js unhidden()).
        CoreWebView2 core;
        System.Threading.SynchronizationContext ui;
        string cssDoc, sheetDoc, sheetId;          // document id from the page script / the document the sheet is for
        readonly StringBuilder cssText = new StringBuilder();
        bool cssFlushing, cssAgain, cssBroken;
        public string CssState => "doc " + cssDoc + " sheet " + sheetId + " chars " + cssText.Length + (cssBroken ? " broken" : "");

        /// <summary>From the page script (top document only): CSS to add. False = use a style element instead.</summary>
        internal bool QueueCss(string doc, string css)
        {
            if (cssBroken || core == null || ui == null || string.IsNullOrEmpty(doc)) return false;
            if (doc != cssDoc) { cssDoc = doc; cssText.Clear(); }
            cssText.Append(css).Append('\n');
            ui.Post(_ => FlushCss(), null);
            return true;
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
                    string doc = cssDoc, text = "@layer gbh{\n" + cssText + "}";
                    await core.CallDevToolsProtocolMethodAsync("DOM.enable", "{}");
                    await core.CallDevToolsProtocolMethodAsync("CSS.enable", "{}");
                    try
                    {
                        if (sheetDoc != doc || sheetId == null)
                        {
                            var tree = Store.FromJson<Dictionary<string, object>>(await core.CallDevToolsProtocolMethodAsync("Page.getFrameTree", "{}"));
                            string frameId = (((tree["frameTree"] as Dictionary<string, object>)["frame"]) as Dictionary<string, object>)["id"] as string;
                            var made = Store.FromJson<Dictionary<string, object>>(await core.CallDevToolsProtocolMethodAsync("CSS.createStyleSheet",
                                Store.ToJson(new Dictionary<string, object> { { "frameId", frameId } })));
                            sheetId = made["styleSheetId"] as string;
                            sheetDoc = doc;
                        }
                        await core.CallDevToolsProtocolMethodAsync("CSS.setStyleSheetText",
                            Store.ToJson(new Dictionary<string, object> { { "styleSheetId", sheetId }, { "text", text } }));
                    }
                    finally
                    {
                        try { await core.CallDevToolsProtocolMethodAsync("CSS.disable", "{}"); } catch { }
                        try { await core.CallDevToolsProtocolMethodAsync("DOM.disable", "{}"); } catch { }
                    }
                } while (cssAgain);
            }
            catch (Exception ex)
            {
                // a document replaced while writing: the next page's script starts a new sheet; repeated failures
                // switch this tab back to style elements so hiding never stops
                sheetId = null;
                TestLog.Add("css sheet: " + ex.Message);
                if (++cssErrors >= 3) cssBroken = true;
            }
            finally { cssFlushing = false; }
        }
        int cssErrors;
#if AUTOMATION
        public static readonly List<string> TestLog = new List<string>();
        public static string TestDbg = "";   // test builds: page-script parts to skip (letters, see gbab.js)
#endif

        static Settings S => Store.Settings;

        public static int Groups()
        {
            int g = FilterEngine.G_ADS;
            if (S.BlockAnnoyances) g |= FilterEngine.G_ANNOY;
            if (S.BlockTrackers) g |= FilterEngine.G_TRACK;
            return g;
        }

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
            core.FrameCreated += (s, e) =>
            {
                try { e.Frame.AddHostObjectToScript("gb", bridge, new[] { "*" }); } catch { }
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
            Count();
        }

        void OnNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            var core = (CoreWebView2)sender;
            string uri = e.Uri ?? "";
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
                string clean = eng.CleanUrl(uri, null, Groups() | FilterEngine.G_TRACK);
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
            if (decision == null) return;
            if (main) { e.Response = BlockPage(url, host); return; }
            Count();
            if (decision.Length > 0)
            {
                var res = eng.Redirect(decision);
                if (res != null) { e.Response = Stub(res); return; }
            }
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
                string lib = eng?.Scriptlets ?? "", raw = eng?.RawJs ?? "";
                gbab = gbab.Replace("/*SCRIPTLETS*/null", lib.Length == 0 ? "null" : lib)
                           .Replace("/*RAWJS*/null", raw.Length == 0 ? "null" : raw)
                           .Replace("/*EXTCSS*/null", ec.Length == 0 ? "null" : "(function(){" + ec + "\n;return ExtendedCss;})()");
                const string shim = "(function(){try{var w=window.chrome&&window.chrome.webview;var h=w&&w.hostObjects&&w.hostObjects.sync&&w.hostObjects.sync.gb;" +
                    "if(!h)return;var c=function(n){return function(){try{return h[n].apply(h,arguments);}catch(e){return '';}};};" +
                    "Object.defineProperty(window,'__gbAdblock',{value:{init:c('init'),js:c('js'),extra:c('extra'),rr:c('rr'),dbg:c('dbg')," +
                    "dnt:c('dnt'),p:c('p'),keys:c('keys'),blocked:c('blocked'),css:c('css')},configurable:true,writable:true});}catch(e){}})();\n";
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
            return (S.Stealth ? "S" : "s") + e.CosmeticInit(href, host, Blocking.Groups());
        }

        public string js(string href)
        {
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
            if (host == null || e == null || !Blocking.OnFor(host)) return "";
            var rules = e.ResponseRules(href, host, host, 0xFFF, Blocking.Groups());
            if (rules.Count == 0) return "";
            var c = new StringBuilder(); var p = new StringBuilder();
            foreach (var r in rules)
            {
                if (r.Kind == "cookie") { if (c.Length > 0) c.Append(','); FilterEngine.JsonString(c, r.Value); }
                else if (r.Kind == "csp" && r.Value.Length > 0) { if (p.Length > 0) p.Append(','); FilterEngine.JsonString(p, r.Value); }
            }
            if (c.Length == 0 && p.Length == 0) return "";
            return "{\"c\":[" + c + "],\"p\":[" + p + "]}";
        }

        public string dbg() => Blocking.TestDbg;
        public bool css(string doc, string css) => b.QueueCss(doc, css);
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
