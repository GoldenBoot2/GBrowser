using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace GBrowser
{
    /// <summary>Runs assets/devtoolsguard.js inside a tab's DevTools window (the DevTools page is a DevTools target too,
    /// reached from the tab with Target.getTargets / attachToTarget): sites' `debugger;` pauses are resumed at once, and
    /// Chrome's DevTools shortcuts that need the DevTools window itself work (Ctrl+Shift+J console, Ctrl+Shift+C inspect).
    /// Nothing is injected into websites.</summary>
    internal static class DevToolsGuard
    {
        static string script;
        static string Script
        {
            get
            {
                if (script == null)
                    try { script = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "devtoolsguard.js")).Trim().TrimEnd(';'); }
                    catch { script = ""; }
                return script;
            }
        }

        /// <summary>Applies the guard (and action: null | "console" | "inspect" | "elements") to the DevTools window of the
        /// page at pageUrl, retrying while that window is still loading. Returns the script's answer, or the last error.</summary>
        public static async Task<string> Apply(CoreWebView2 core, string pageUrl, string action, int waitMs = 10000)
        {
            if (core == null || Script.Length == 0) return "no script";
            string last = "no DevTools window";
            var until = DateTime.UtcNow.AddMilliseconds(waitMs);
            while (DateTime.UtcNow < until)
            {
                try
                {
                    var tg = Store.FromJson<Dictionary<string, object>>(await core.CallDevToolsProtocolMethodAsync("Target.getTargets", "{}"));
                    var dts = (tg != null && tg.TryGetValue("targetInfos", out var ti) && ti is System.Collections.IEnumerable e ? e.Cast<object>().OfType<Dictionary<string, object>>() : Enumerable.Empty<Dictionary<string, object>>())
                        .Where(t => (t.TryGetValue("url", out var u) ? u as string : "")?.StartsWith("devtools://") == true).ToList();
                    // this tab's DevTools: its title names the page ("DevTools - example.com/"); else the newest one
                    string shortUrl = ShortUrl(pageUrl);
                    var mine = dts.Where(t => shortUrl.Length > 0 && ((t.TryGetValue("title", out var tt) ? tt as string : "") ?? "").IndexOf(shortUrl, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                    var target = mine.LastOrDefault() ?? dts.LastOrDefault();
                    if (target != null)
                    {
                        last = await Run(core, target["targetId"] as string, action);
                        if (last.StartsWith("ok")) return last;
                    }
                }
                catch (Exception ex) { last = ex.Message; }
                await Task.Delay(300);
            }
            return last;
        }

        /// <summary>The setting changed: every open DevTools window gets it (no action).</summary>
        public static async Task ApplyAll(CoreWebView2 core)
        {
            try
            {
                var tg = Store.FromJson<Dictionary<string, object>>(await core.CallDevToolsProtocolMethodAsync("Target.getTargets", "{}"));
                var dts = tg != null && tg.TryGetValue("targetInfos", out var ti) && ti is System.Collections.IEnumerable e ? e.Cast<object>().OfType<Dictionary<string, object>>() : Enumerable.Empty<Dictionary<string, object>>();
                foreach (var t in dts.Where(t => (t.TryGetValue("url", out var u) ? u as string : "")?.StartsWith("devtools://") == true).ToList())
                    await Run(core, t["targetId"] as string, null);
            }
            catch { }
        }

        static async Task<string> Run(CoreWebView2 core, string targetId, string action)
        {
            string sid = null;
            try
            {
                var a = Store.FromJson<Dictionary<string, object>>(await core.CallDevToolsProtocolMethodAsync("Target.attachToTarget",
                    Store.ToJson(new Dictionary<string, object> { { "targetId", targetId }, { "flatten", true } })));
                sid = a != null && a.TryGetValue("sessionId", out var s) ? s as string : null;
                if (sid == null) return "attach failed";
                string expr = "(" + Script + ")(" + (Store.Settings.SkipSiteDebugger ? "true" : "false") + ", " + Store.ToJson(action ?? "") + ")";
                var r = Store.FromJson<Dictionary<string, object>>(await core.CallDevToolsProtocolMethodForSessionAsync(sid, "Runtime.evaluate",
                    Store.ToJson(new Dictionary<string, object> { { "expression", expr }, { "awaitPromise", true }, { "returnByValue", true } })));
                if (r != null && r.TryGetValue("result", out var rr) && rr is Dictionary<string, object> rd && rd.TryGetValue("value", out var v) && v is string str) return str;
                if (r != null && r.TryGetValue("exceptionDetails", out var ex) && ex is Dictionary<string, object> ed)
                    return "error: " + (ed.TryGetValue("exception", out var exo) && exo is Dictionary<string, object> exd && exd.TryGetValue("description", out var de) ? de : ed.TryGetValue("text", out var tx) ? tx : "?");
                return "no answer";
            }
            catch (Exception e) { return "error: " + e.Message; }
            finally
            {
                if (sid != null)
                    try { await core.CallDevToolsProtocolMethodAsync("Target.detachFromTarget", Store.ToJson(new Dictionary<string, object> { { "sessionId", sid } })); } catch { }
            }
        }

        /// <summary>How DevTools names the page in its title: the address without "https://" / "http://".</summary>
        static string ShortUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            int i = url.IndexOf("://", StringComparison.Ordinal);
            return i >= 0 ? url.Substring(i + 3) : url;
        }
    }
}
