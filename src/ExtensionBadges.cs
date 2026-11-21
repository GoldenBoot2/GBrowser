using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace GBrowser
{
    /// <summary>Badges and titles of extension toolbar icons (chrome.action.setBadgeText / setBadgeBackgroundColor /
    /// setBadgeTextColor / setTitle). WebView2 has no API for them, so they are read from the extension's own service
    /// worker over DevTools (Target.getTargets from a tab, attach, chrome.action.get*, detach at once, so the worker can
    /// still go to sleep like in Chrome). A sleeping worker keeps its last badge. The tab's own badge is read when the
    /// extension can see tab addresses (the tab is matched by address); otherwise the extension's general badge.</summary>
    internal static class ExtensionBadges
    {
        public sealed class State { public string Text = "", Title; public Color? Back, Fore; }

        static readonly Dictionary<string, State> last = new Dictionary<string, State>();
        static bool busy;

        /// <summary>Reads the state of the given extensions whose worker is running; the others keep their last state.</summary>
        public static async Task<Dictionary<string, State>> Read(CoreWebView2 core, ICollection<string> ids, string pageUrl)
        {
            var res = new Dictionary<string, State>();
            foreach (var id in ids) if (last.TryGetValue(id, out var s)) res[id] = s;
            if (busy || core == null || ids.Count == 0) return res;
            busy = true;
            try
            {
                var tg = Store.FromJson<Dictionary<string, object>>(await core.CallDevToolsProtocolMethodAsync("Target.getTargets", "{}"));
                var infos = tg != null && tg.TryGetValue("targetInfos", out var ti) && ti is System.Collections.IEnumerable e ? e.Cast<object>().OfType<Dictionary<string, object>>() : Enumerable.Empty<Dictionary<string, object>>();
                foreach (var t in infos)
                {
                    if ((t.TryGetValue("type", out var ty) ? ty as string : null) != "service_worker") continue;
                    string url = t.TryGetValue("url", out var u) ? u as string : null;
                    string id = url != null && url.StartsWith("chrome-extension://") ? url.Substring(19).Split('/')[0] : null;
                    if (id == null || !ids.Contains(id)) continue;
                    var st = await ReadOne(core, t["targetId"] as string, pageUrl);
                    if (st == null) continue;
                    last[id] = st;
                    res[id] = st;
                }
            }
            catch { }
            finally { busy = false; }
            return res;
        }

        static async Task<State> ReadOne(CoreWebView2 core, string targetId, string pageUrl)
        {
            string sid = null;
            try
            {
                var a = Store.FromJson<Dictionary<string, object>>(await core.CallDevToolsProtocolMethodAsync("Target.attachToTarget",
                    Store.ToJson(new Dictionary<string, object> { { "targetId", targetId }, { "flatten", true } })));
                sid = a != null && a.TryGetValue("sessionId", out var s) ? s as string : null;
                if (sid == null) return null;
                return Parse(await core.CallDevToolsProtocolMethodForSessionAsync(sid, "Runtime.evaluate", EvalArgs(pageUrl)));
            }
            catch { return null; }
            finally
            {
                if (sid != null)
                    try { await core.CallDevToolsProtocolMethodAsync("Target.detachFromTarget", Store.ToJson(new Dictionary<string, object> { { "sessionId", sid } })); } catch { }
            }
        }

        /// <summary>The same read inside one of the extension's own pages (its open popup): a popup often sets the badge
        /// while the extension's worker sleeps.</summary>
        public static async Task ReadPage(CoreWebView2 extensionPage, string id, string pageUrl)
        {
            try
            {
                var st = Parse(await extensionPage.CallDevToolsProtocolMethodAsync("Runtime.evaluate", EvalArgs(pageUrl)));
                if (st != null) last[id] = st;
            }
            catch { }
        }

        // MV3 chrome.action, MV2 chrome.browserAction; the tab is found by its address (needs the "tabs" permission)
        static string EvalArgs(string pageUrl)
        {
            string js = "(async () => { const A = chrome.action || chrome.browserAction; if (!A) return null; let d = {};" +
                " try { const ts = await chrome.tabs.query({}); const m = ts.find(x => x.url === " + Store.ToJson(pageUrl ?? "") + "); if (m) d = { tabId: m.id }; } catch (e) { }" +
                " const r = await Promise.all([A.getBadgeText(d), A.getBadgeBackgroundColor(d), A.getTitle(d), A.getBadgeTextColor ? A.getBadgeTextColor(d).catch(() => null) : null]);" +
                " return JSON.stringify(r); })()";
            return Store.ToJson(new Dictionary<string, object> { { "expression", js }, { "awaitPromise", true }, { "returnByValue", true } });
        }

        static State Parse(string evalResult)
        {
            var ev = Store.FromJson<Dictionary<string, object>>(evalResult);
            var val = ev != null && ev.TryGetValue("result", out var rr) && rr is Dictionary<string, object> rd && rd.TryGetValue("value", out var v) ? v as string : null;
            if (val == null) return null;
            var arr = Store.FromJson<object[]>(val);
            if (arr == null || arr.Length < 3) return null;
            return new State { Text = arr[0] as string ?? "", Back = ToColor(arr[1]), Title = arr[2] as string, Fore = arr.Length > 3 ? ToColor(arr[3]) : null };
        }

        /// <summary>[r, g, b, a] from the extension; null when unset (alpha 0), so the default colour is used.</summary>
        static Color? ToColor(object o)
        {
            if (!(o is System.Collections.IEnumerable e) || o is string) return null;
            var n = e.Cast<object>().Select(x => Convert.ToInt32(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            if (n.Length < 4 || n[3] == 0) return null;
            return Color.FromArgb(n[3], n[0], n[1], n[2]);
        }

        public static void Forget(string id) => last.Remove(id);
    }
}
