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
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnRequest;
            core.NavigationStarting += OnNavigationStarting;
            var bridge = new PageBridge(this);
            core.AddHostObjectToScript("gb", bridge);
            core.FrameCreated += (s, e) =>
            {
                try { e.Frame.AddHostObjectToScript("gb", bridge, new[] { "*" }); } catch { }
            };
            var script = PageScript();
            if (script.Length > 0) _ = core.AddScriptToExecuteOnDocumentCreatedAsync(script);
        }

        void OnNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            var core = (CoreWebView2)sender;
            string uri = e.Uri ?? "";
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
            PageUrl = uri;
            PageHost = host;
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

        void OnRequest(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            string url = e.Request.Uri;
            if (S.DoNotTrack) try { e.Request.Headers.SetHeader("DNT", "1"); } catch { }
            string host = HostOf(url);
            if (host == null || !S.AdblockEnabled) return;
            bool main = e.ResourceContext == CoreWebView2WebResourceContext.Document && url == pendingMainUrl;
            string page = PageHost;
            if (page != null && !OnFor(page)) return;
            var eng = FilterEngine.Instance;
            if (eng == null) return;   // still loading (about 1 s after start)
            if (main && (!S.BlockAdSitePages || host == sessionAllowedHost)) return;
            int type = main ? FilterEngine.T_DOCUMENT : TypeOf(e.ResourceContext);
            string decision = eng.Match(url, host, main ? null : page, type, Groups());
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

        /// <summary>A body that fails while being read: the page sees a network error (like a blocked request in Chrome).</summary>
        sealed class FailStream : Stream
        {
            public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
            public override long Length => throw new IOException("blocked");
            public override long Position { get => 0; set { } }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new IOException("blocked");
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        CoreWebView2WebResourceResponse Fail() => env.CreateWebResourceResponse(new FailStream(), 200, "OK", "Content-Type: text/plain");

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
                + "border:1px solid #7f7667;color:#785a00;text-decoration:none;font-weight:600}a:hover{background:#f4ede1}</style></head><body><main>"
                + "<svg viewBox='0 -960 960 960'><path d='" + IconData.Block + "'/></svg>"
                + "<h1>Blocked</h1><p>" + System.Net.WebUtility.HtmlEncode(host) + "</p>"
                + "<a href=\"" + System.Net.WebUtility.HtmlEncode(allow) + "\">Open anyway</a></main></body></html>";
            return env.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(html)), 200, "OK",
                "Content-Type: text/html; charset=utf-8\nCache-Control: no-store");
        }

        // ---------------------------------------------------------------- page script

        static string script;

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
                    "dnt:c('dnt'),p:c('p'),keys:c('keys'),blocked:c('blocked')},configurable:true,writable:true});}catch(e){}})();\n";
                script = shim + gbab;
            }
            catch { script = ""; }
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

        public string dbg() => "";
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
