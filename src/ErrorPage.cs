using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace GBrowser
{
    /// <summary>GBrowser's own page for failed loads, instead of the engine's Microsoft Edge-branded one. After a failed
    /// main-frame navigation the engine shows chrome-error://chromewebdata/ in the same history entry; its content is
    /// replaced here (only when the page really is that error page), so the address bar keeps the failed address and
    /// Reload / Back behave as in Chrome. Wording follows Chrome's error pages.</summary>
    internal static class ErrorPage
    {
        static string[] Text(CoreWebView2WebErrorStatus s, string host)
        {
            // { icon, title, detail, code }
            switch (s)
            {
                case CoreWebView2WebErrorStatus.Disconnected:
                    return new[] { IconData.Public, "No internet", "Check your network cables, modem and router, or reconnect to Wi-Fi.", "ERR_INTERNET_DISCONNECTED" };
                case CoreWebView2WebErrorStatus.HostNameNotResolved:
                    return new[] { IconData.Public, "This site can't be reached", "Can't find the server address of " + host + ".", "ERR_NAME_NOT_RESOLVED" };
                case CoreWebView2WebErrorStatus.CannotConnect:
                case CoreWebView2WebErrorStatus.ServerUnreachable:
                    return new[] { IconData.Public, "This site can't be reached", host + " refused to connect.", "ERR_CONNECTION_REFUSED" };
                case CoreWebView2WebErrorStatus.ConnectionAborted:
                case CoreWebView2WebErrorStatus.ConnectionReset:
                    return new[] { IconData.Public, "This site can't be reached", "The connection to " + host + " was interrupted.", "ERR_CONNECTION_RESET" };
                case CoreWebView2WebErrorStatus.Timeout:
                    return new[] { IconData.Public, "This site can't be reached", host + " took too long to respond.", "ERR_TIMED_OUT" };
                case CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect:
                case CoreWebView2WebErrorStatus.CertificateExpired:
                case CoreWebView2WebErrorStatus.ClientCertificateContainsErrors:
                case CoreWebView2WebErrorStatus.CertificateRevoked:
                case CoreWebView2WebErrorStatus.CertificateIsInvalid:
                    return new[] { IconData.Warning, "Your connection isn't private",
                        "Someone may be trying to steal your information from " + host + " (passwords, messages or cards). The site's security certificate is not valid.",
                        "NET::ERR_CERT_" + s.ToString().Replace("Certificate", "").ToUpperInvariant() };
                case CoreWebView2WebErrorStatus.RedirectFailed:
                    return new[] { IconData.Warning, "This page isn't working", host + " redirected you too many times.", "ERR_TOO_MANY_REDIRECTS" };
                case CoreWebView2WebErrorStatus.ErrorHttpInvalidServerResponse:
                    return new[] { IconData.Warning, "This page isn't working", host + " sent an invalid response.", "ERR_INVALID_RESPONSE" };
                case CoreWebView2WebErrorStatus.Unknown:
                    // the engine gives no detail here (e.g. a refused connection): no specific claim
                    return new[] { IconData.Public, "This site can't be reached", host + " can't be reached right now.", "ERR_FAILED" };
                default:
                    return new[] { IconData.Warning, "This page isn't working", host + " didn't send any data.", "ERR_" + s.ToString().ToUpperInvariant() };
            }
        }

        static string Svg(string path, string cls) =>
            "<svg class=\"" + cls + "\" viewBox=\"0 -960 960 960\" aria-hidden=\"true\"><path d=\"" + path + "\"/></svg>";

        static string E(string s) => WebUtility.HtmlEncode(s ?? "");

        /// <summary>Script that replaces the engine's error page with GBrowser's (no-op on any other page).</summary>
        public static string Script(CoreWebView2WebErrorStatus status, string url)
        {
            string host = Blocking.HostOf(url) ?? url ?? "";
            var t = Text(status, host);
            var c = new Dictionary<string, string>
            {
                { "bg", Palette.Hex(Theme.Surface) }, { "fg", Palette.Hex(Theme.OnSurface) }, { "fg2", Palette.Hex(Theme.OnSurfaceVariant) },
                { "muted", Palette.Hex(Theme.Outline) }, { "primary", Palette.Hex(Theme.Primary) }, { "onprimary", Palette.Hex(Theme.OnPrimary) },
                { "icon", t[0] == IconData.Warning ? (Theme.Dark ? "#ffb4ab" : "#ba1a1a") : Palette.Hex(Theme.OnSurfaceVariant) },
                { "hover", Theme.Dark ? "rgba(236,225,212,.08)" : "rgba(31,27,19,.08)" },
            };
            string css = "html,body{margin:0;background:" + c["bg"] + ";color:" + c["fg"] + ";font:15px/1.5 'Segoe UI Variable Text','Segoe UI',system-ui,sans-serif}" +
                "main{max-width:600px;margin:0 auto;padding:16vh 24px 48px}.big{width:72px;height:72px;fill:" + c["icon"] + "}" +
                "h1{font-size:28px;font-weight:500;margin:20px 0 8px}p{color:" + c["fg2"] + ";margin:0 0 6px;word-break:break-word}" +
                ".code{font-size:12px;color:" + c["muted"] + ";margin-top:14px;letter-spacing:.02em}" +
                ".actions{display:flex;gap:12px;margin-top:28px;flex-wrap:wrap}" +
                "button{height:40px;padding:0 20px 0 16px;border-radius:20px;border:0;font:inherit;font-weight:600;cursor:pointer;display:inline-flex;align-items:center;gap:8px}" +
                "button svg{width:18px;height:18px;fill:currentColor}.filled{background:" + c["primary"] + ";color:" + c["onprimary"] + "}" +
                ".text{background:transparent;color:" + c["primary"] + "}.text:hover{background:" + c["hover"] + "}" +
                "button:focus-visible{outline:2px solid " + c["primary"] + ";outline-offset:2px}" +
                ".diag{margin-top:36px;padding:18px 20px;border-radius:16px;background:" + Palette.Hex(Theme.SurfaceContainer) + "}" +
                ".diag h2{font-size:15px;font-weight:600;margin:0 0 10px;color:" + c["primary"] + "}" +
                ".diag ul{list-style:none;margin:0;padding:0}.diag li{display:flex;gap:12px;align-items:flex-start;padding:6px 0}" +
                ".diag li svg{width:20px;height:20px;flex:none;margin-top:1px}.ok svg{fill:" + c["primary"] + "}.bad svg{fill:" + c["icon"] + "}" +
                ".info svg{fill:" + c["fg2"] + "}.diag .n{font-weight:600}.diag .d{color:" + c["fg2"] + "}" +
                ".tips{margin:12px 0 0;padding:0 0 0 20px;color:" + c["fg2"] + "}.tips li{display:list-item;padding:2px 0}" +
                ".diag .actions{margin-top:16px}.tonal{background:" + Palette.Hex(Theme.PrimaryContainer) + ";color:" + Palette.Hex(Theme.OnPrimaryContainer) + "}";
            string html = "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"color-scheme\" content=\"" + (Theme.Dark ? "dark" : "light") + "\">" +
                "<title>" + E(host) + "</title><style>" + css + "</style></head><body><main>" +
                Svg(t[0], "big") + "<h1>" + E(t[1]) + "</h1><p>" + E(t[2]) + "</p><div class=\"code\">" + E(t[3]) + "</div>" +
                "<div class=\"actions\"><button class=\"filled\" id=\"r\">" + Svg(IconData.Refresh, "") + "Reload</button>" +
                "<button class=\"text\" id=\"b\">" + Svg(IconData.Back, "") + "Go back</button></div>" +
                (IsNetwork(status) ?
                    "<section class=\"diag\"><h2>Checking your connection</h2><ul id=\"dl\"><li class=\"info\">" + Svg(IconData.Search, "") +
                    "<span class=\"d\">Checking...</span></li></ul><ul class=\"tips\" id=\"tips\"></ul><div class=\"actions\">" +
                    "<button class=\"tonal\" id=\"nd\">" + Svg(IconData.Tune, "") + "Windows Network Diagnostics</button>" +
                    "<button class=\"text\" id=\"ns\">" + Svg(IconData.Settings, "") + "Network settings</button>" +
                    "<button class=\"text\" id=\"da\" hidden>" + Svg(IconData.Language, "") + "Use automatic DNS</button></div></section>" : "") +
                "</main></body></html>";
            var sb = new StringBuilder();
            sb.Append("(function(){if(location.protocol!=='chrome-error:')return 'skip';");
            sb.Append("var h=").Append(Store.ToJson(html)).Append(",u=").Append(Store.ToJson(url ?? "")).Append(";");
            // replace the contents in place: document.open() would start a new (aborted) navigation
            sb.Append("var d=new DOMParser().parseFromString(h,'text/html');");
            sb.Append("document.documentElement.replaceChildren(d.head,d.body);document.title=d.title;");
            sb.Append("document.getElementById('r').onclick=function(){location.replace(u);};");
            sb.Append("document.getElementById('b').onclick=function(){history.back();};");
            // diagnostics buttons open GBrowser action addresses (accepted only from a click on this error page)
            sb.Append("var act=function(x){location.href='" + Pages.Origin + "act?do='+x+'&u='+encodeURIComponent(u);};");
            sb.Append("var q=function(i){return document.getElementById(i);};");
            sb.Append("if(q('nd')){q('nd').onclick=function(){act('netdiag');};q('ns').onclick=function(){act('netsettings');};q('da').onclick=function(){act('dnsauto');};}");
            // filled in by the app when the checks finish: {checks:[{n,d,s}], tips:[...], dns:bool}
            sb.Append("var ic={ok:" + Store.ToJson(Svg(IconData.Check, "")) + ",bad:" + Store.ToJson(Svg(IconData.Warning, "")) + ",info:" + Store.ToJson(Svg(IconData.Info, "")) + "};");
            sb.Append("window.gbDiag=function(r){var l=q('dl');if(!l)return;l.textContent='';r.checks.forEach(function(c){var li=document.createElement('li');li.className=c.s;");
            sb.Append("li.innerHTML=ic[c.s];var t=document.createElement('div');var n=document.createElement('div');n.className='n';n.textContent=c.n;");
            sb.Append("var d=document.createElement('div');d.className='d';d.textContent=c.d;t.append(n,d);li.append(t);l.append(li);});");
            sb.Append("var tp=q('tips');tp.textContent='';r.tips.forEach(function(x){var li=document.createElement('li');li.textContent=x;tp.append(li);});");
            sb.Append("q('da').hidden=!r.dns;};");
            sb.Append("return 'replaced';})()");
            return sb.ToString();
        }

        /// <summary>Errors where the network itself may be the cause (diagnostics are offered).</summary>
        public static bool IsNetwork(CoreWebView2WebErrorStatus s) =>
            s == CoreWebView2WebErrorStatus.Disconnected || s == CoreWebView2WebErrorStatus.HostNameNotResolved ||
            s == CoreWebView2WebErrorStatus.CannotConnect || s == CoreWebView2WebErrorStatus.ServerUnreachable ||
            s == CoreWebView2WebErrorStatus.ConnectionAborted || s == CoreWebView2WebErrorStatus.ConnectionReset ||
            s == CoreWebView2WebErrorStatus.Timeout || s == CoreWebView2WebErrorStatus.Unknown;

        /// <summary>Runs the network checks and shows them (with tips) on the error page.</summary>
        public static async Task ShowDiagnostics(CoreWebView2 core, string url)
        {
            var r = await NetDiag.Run(url);
            var tips = new List<string>();
            var get = new Func<string, NetDiag.Check>(n => r.Checks.Find(c => c.Name == n));
            var adapter = get("Network connection"); var inet = get("Internet"); var wdns = get("DNS (Windows)");
            string host = Blocking.HostOf(url);
            var site = host == null ? null : get(host);
            if (adapter != null && !adapter.Ok) tips.Add("Connect to Wi-Fi or plug in a network cable.");
            else if (inet != null && !inet.Ok) tips.Add("Restart your modem or router, or check your mobile data.");
            else if (wdns != null && !wdns.Ok) tips.Add("Your DNS server is not answering: Windows Network Diagnostics can reset it.");
            if (r.SecureDnsBlocked) tips.Add("Your Secure DNS provider is blocked on this network: use automatic DNS.");
            if (site != null && !site.Ok && site.Detail == "This name does not exist") tips.Add("Check the address for typing mistakes.");
            else if (site != null && !site.Ok && inet != null && inet.Ok) tips.Add("The site may be down. Try again later.");
            if (r.Checks.Exists(c => c.Info)) tips.Add("A proxy or VPN may block this site: try without it.");
            var data = new Dictionary<string, object>
            {
                { "checks", r.Checks.Select(c => new Dictionary<string, object> { { "n", c.Name }, { "d", c.Detail }, { "s", c.Info ? "info" : c.Ok ? "ok" : "bad" } }).ToList() },
                { "tips", tips }, { "dns", r.SecureDnsBlocked },
            };
            try { await core.ExecuteScriptAsync("window.gbDiag&&window.gbDiag(" + Store.ToJson(data) + ")"); } catch { }
        }

        /// <summary>Called after a failed main-frame navigation; returns what the script did ("replaced" / "skip").</summary>
        public static async Task<string> Show(CoreWebView2 core, CoreWebView2WebErrorStatus status, string url)
        {
            if (status == CoreWebView2WebErrorStatus.OperationCanceled) return "cancelled";
#if AUTOMATION
            Blocking.TestLog.Add("navfail " + status + " " + url);
#endif
            try
            {
                for (int i = 0; i < 10; i++)   // the error page may still be committing
                {
                    var r = await core.ExecuteScriptAsync(Script(status, url));
                    if (r.Contains("replaced")) return "replaced";
                    await Task.Delay(100);
                }
            }
            catch { }
            return "skip";
        }
    }
}
