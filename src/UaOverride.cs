using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace GBrowser
{
    /// <summary>Pages see a genuine Microsoft Edge. Measured 19-11-2026: WebView2 157 adds the brand "Microsoft Edge WebView2"
    /// to the Sec-CH-UA header and navigator.userAgentData (Chromium, Microsoft Edge WebView2, Not-A.Brand, Microsoft
    /// Edge), which real Edge 157 on the same PC never sends (Not-A.Brand 99, Microsoft Edge 157, Chromium 157). Any site
    /// could tell an embedded browser from Edge by it. WebView2 has no setting for it, so the engine's own user-agent
    /// override (DevTools Emulation.setUserAgentOverride with full client-hint metadata) gives every page, frame and
    /// worker real Edge's values: the engine's own versions, platform and architecture, without the WebView2 brand,
    /// in the order Chromium itself uses for Edge (the GREASE brand and order follow from the major version). It
    /// changes headers and JavaScript together, natively: no page script involved.</summary>
    internal static class UaOverride
    {
        static string json;   // the override (cached in Settings for the engine version it was measured on)
        static readonly List<CoreWebView2> waiting = new List<CoreWebView2>();
        static bool measuring;

        public static async Task Apply(CoreWebView2 core)
        {
            try
            {
                var st = Store.Settings;
                string ver = core.Environment.BrowserVersionString;
                if (json == null && st.UaOverrideVersion == ver && !string.IsNullOrEmpty(st.UaOverride)) json = st.UaOverride;
                if (json == null)
                {
                    // measured once on the first secure page this engine shows (the raw values exist only there)
                    lock (waiting) waiting.Add(core);
                    core.NavigationCompleted += Measure;
                    return;
                }
                await Set(core);
            }
            catch { }
        }

        static async void Measure(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            var core = (CoreWebView2)sender;
            if (json != null || measuring || !core.Source.StartsWith("https://")) return;
            measuring = true;
            try
            {
                string j = await Build(core);
                if (j == null) return;
                json = j;
                Store.Settings.UaOverride = j;
                Store.Settings.UaOverrideVersion = core.Environment.BrowserVersionString;
                Store.SaveSettings();
                List<CoreWebView2> all;
                lock (waiting) { all = waiting.ToList(); waiting.Clear(); }
                foreach (var c in all) { try { c.NavigationCompleted -= Measure; await Set(c); } catch { } }
            }
            catch { }
            finally { measuring = false; }
        }

        static async Task Set(CoreWebView2 core)
        {
            var rcv = core.GetDevToolsProtocolEventReceiver("Target.attachedToTarget");
            rcv.DevToolsProtocolEventReceived += async (s, e) =>
            {
                string sid = null;
                try
                {
                    var d = Store.FromJson<Dictionary<string, object>>(e.ParameterObjectAsJson);
                    sid = d["sessionId"] as string;
                    var ti = d["targetInfo"] as Dictionary<string, object>;
                    string type = ti?["type"] as string ?? "";
                    if (type == "iframe" || type == "page")
                    {
                        await core.CallDevToolsProtocolMethodForSessionAsync(sid, "Emulation.setUserAgentOverride", json);
                        // its own frames and workers too
                        await core.CallDevToolsProtocolMethodForSessionAsync(sid, "Target.setAutoAttach", AutoAttach);
                    }
                    else await core.CallDevToolsProtocolMethodForSessionAsync(sid, "Network.setUserAgentOverride", json);
                }
                catch { }
                finally
                {
                    // the frame / worker waits for this before it runs
                    if (sid != null) try { await core.CallDevToolsProtocolMethodForSessionAsync(sid, "Runtime.runIfWaitingForDebugger", "{}"); } catch { }
                }
            };
            await core.CallDevToolsProtocolMethodAsync("Emulation.setUserAgentOverride", json);
            await core.CallDevToolsProtocolMethodAsync("Target.setAutoAttach", AutoAttach);
        }

        const string AutoAttach = "{\"autoAttach\":true,\"waitForDebuggerOnStart\":true,\"flatten\":true}";

        static readonly int[][] Orders3 = { new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 1, 0, 2 }, new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 1, 0 } };

        /// <summary>The engine's real values (read once from a page), minus the WebView2 brand, in Edge's order.</summary>
        static async Task<string> Build(CoreWebView2 core)
        {
            const string probe = "(async()=>{const v=await navigator.userAgentData.getHighEntropyValues(['architecture','bitness','fullVersionList','model','platformVersion','wow64','formFactors']);" +
                "return JSON.stringify({ua:navigator.userAgent,lang:navigator.language,v})})()";
            var r = Store.FromJson<Dictionary<string, object>>(await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                Store.ToJson(new Dictionary<string, object> { { "expression", probe }, { "awaitPromise", true }, { "returnByValue", true } })));
            string text = ((r?["result"] as Dictionary<string, object>)?["value"]) as string;
            if (text == null) return null;
            var o = Store.FromJson<Dictionary<string, object>>(text);
            var v = o["v"] as Dictionary<string, object>;
            string Ver(System.Collections.IEnumerable list, Func<string, bool> pick)
            {
                foreach (var x in list) if (x is Dictionary<string, object> b && pick(b["brand"] as string)) return b["version"] as string;
                return null;
            }
            var full = (System.Collections.IEnumerable)v["fullVersionList"];
            string edgeFull = Ver(full, b => b == "Microsoft Edge"), chromFull = Ver(full, b => b == "Chromium");
            if (edgeFull == null || chromFull == null) return null;
            int major = int.Parse(edgeFull.Split('.')[0]);
            // Chromium's GREASE brand for this major version (user_agent_utils GetGreasedUserAgentBrandVersion)
            string[] chars = { " ", "(", ":", "-", ".", "/", ")", ";", "=", "?", "_" };
            string[] greaseVer = { "8", "99", "24" };
            string greaseBrand = "Not" + chars[major % chars.Length] + "A" + chars[(major + 1) % chars.Length] + "Brand";
            string gv = greaseVer[major % greaseVer.Length];
            var order = Orders3[major % 6];
            object B(string b, string ver) => new Dictionary<string, object> { { "brand", b }, { "version", ver } };
            var brands = new object[3]; var fulls = new object[3];
            brands[order[0]] = B(greaseBrand, gv); brands[order[1]] = B("Chromium", major.ToString()); brands[order[2]] = B("Microsoft Edge", major.ToString());
            fulls[order[0]] = B(greaseBrand, gv + ".0.0.0"); fulls[order[1]] = B("Chromium", chromFull); fulls[order[2]] = B("Microsoft Edge", edgeFull);
            var meta = new Dictionary<string, object>
            {
                { "brands", brands }, { "fullVersionList", fulls }, { "fullVersion", edgeFull },
                { "platform", "Windows" }, { "platformVersion", v["platformVersion"] }, { "architecture", v["architecture"] },
                { "model", v["model"] ?? "" }, { "mobile", false }, { "bitness", v["bitness"] }, { "wow64", v.ContainsKey("wow64") && v["wow64"] is bool w && w },
            };
            if (v.TryGetValue("formFactors", out var ff) && ff != null) meta["formFactors"] = ff;
            return Store.ToJson(new Dictionary<string, object> { { "userAgent", o["ua"] }, { "platform", "Win32" }, { "userAgentMetadata", meta } });
        }
    }
}
