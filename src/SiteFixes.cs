using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;

namespace GBrowser
{
    // Fixes for one site's own server faults that make it fail in every browser. Each fix is scoped to that site only.
    static class SiteFixes
    {
        // VFS Global (21-11-2026): the login service lift-api.vfsglobal.com answers with Access-Control-Allow-Origin
        // naming visa.vfsglobal.com even when the login page is services.vfsglobal.com, so every browser drops the
        // answer (CORS origin mismatch) and the app shows "Session Expired or Invalid". The answer keeps coming from the
        // engine's own network stack; only that one header is set to the requesting VFS page's own origin.
        const string VfsApi = "https://lift-api.vfsglobal.com/*";
        static readonly Regex VfsOrigin = new Regex(@"^https://[a-z0-9-]+\.vfsglobal\.com$", RegexOptions.IgnoreCase);

        public static void Attach(CoreWebView2 core)
        {
            var rx = core.GetDevToolsProtocolEventReceiver("Fetch.requestPaused");
            rx.DevToolsProtocolEventReceived += async (o, a) =>
            {
                string id = null;
                try
                {
                    var p = Store.FromJson<Dictionary<string, object>>(a.ParameterObjectAsJson);
                    id = p["requestId"] as string;
                    var req = p["request"] as Dictionary<string, object>;
                    string origin = null;
                    if (req != null && req.TryGetValue("headers", out var hv) && hv is Dictionary<string, object> hd)
                        foreach (var kv in hd) if (string.Equals(kv.Key, "Origin", StringComparison.OrdinalIgnoreCase)) origin = kv.Value as string;
                    var headers = p.TryGetValue("responseHeaders", out var rh) ? rh as System.Collections.IEnumerable : null;
                    string fixedJson = null;
                    List<Dictionary<string, object>> fixedList = null;
                    int code = 200;
#if AUTOMATION
                    {
                        string acao = "-";
                        if (headers != null) foreach (var item in headers) if (item is Dictionary<string, object> h0 && string.Equals(h0["name"] as string, "Access-Control-Allow-Origin", StringComparison.OrdinalIgnoreCase)) acao = h0["value"] as string;
                        Blocking.TestLog.Add("SITEPAUSE " + (req?["method"]) + " " + Regex.Replace(req?["url"] as string ?? "", @"\?.*", "") + " origin=" + origin + " acao=" + acao);
                    }
#endif
                    if (headers != null && origin != null && VfsOrigin.IsMatch(origin))
                    {
                        var list = new List<Dictionary<string, object>>();
                        bool changed = false;
                        foreach (var item in headers)
                        {
                            var h = item as Dictionary<string, object>;
                            if (h == null) continue;
                            string name = h["name"] as string, value = h["value"] as string;
                            // only a VFS origin that is not the page's own: a wildcard or a correct value stays as it is
                            if (string.Equals(name, "Access-Control-Allow-Origin", StringComparison.OrdinalIgnoreCase)
                                && value != null && VfsOrigin.IsMatch(value.Trim()) && !string.Equals(value.Trim(), origin, StringComparison.OrdinalIgnoreCase))
                            {
                                value = origin; changed = true;
                            }
                            list.Add(new Dictionary<string, object> { { "name", name }, { "value", value } });
                        }
                        if (changed)
                        {
                            fixedList = list;
                            code = p.TryGetValue("responseStatusCode", out var sc) ? Convert.ToInt32(sc) : 200;
                            fixedJson = Store.ToJson(new Dictionary<string, object> { { "requestId", id }, { "responseCode", code }, { "responseHeaders", list } });
#if AUTOMATION
                            Blocking.TestLog.Add("SITEFIX vfs ACAO -> " + origin + " " + Regex.Replace(req["url"] as string ?? "", @"\?.*", ""));
#endif
                        }
                    }
                    if (fixedJson != null)
                    {
                        try { await core.CallDevToolsProtocolMethodAsync("Fetch.continueResponse", fixedJson); }
                        catch
                        {
                            // engines that refuse continueResponse: hand back the same answer the engine already
                            // received (its body read from the engine, nothing is fetched again) with the fixed header
                            string body = "";
                            try
                            {
                                var b = Store.FromJson<Dictionary<string, object>>(await core.CallDevToolsProtocolMethodAsync("Fetch.getResponseBody", Store.ToJson(new Dictionary<string, object> { { "requestId", id } })));
                                body = b["body"] as string ?? "";
                                if (!(b.TryGetValue("base64Encoded", out var be) && be is bool isB && isB)) body = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(body));
                            }
                            catch { }   // an answer without a body (preflight)
                            await core.CallDevToolsProtocolMethodAsync("Fetch.fulfillRequest", Store.ToJson(new Dictionary<string, object> {
                                { "requestId", id }, { "responseCode", code }, { "responseHeaders", fixedList }, { "body", body } }));
#if AUTOMATION
                            Blocking.TestLog.Add("SITEFIX via fulfillRequest");
#endif
                        }
                    }
                    else await core.CallDevToolsProtocolMethodAsync("Fetch.continueRequest", Store.ToJson(new Dictionary<string, object> { { "requestId", id } }));
                }
                catch (Exception ex)
                {
#if AUTOMATION
                    Blocking.TestLog.Add("SITEFIX error " + ex.Message);
#endif
                    // never leave a request paused
                    if (id != null) try { await core.CallDevToolsProtocolMethodAsync("Fetch.continueRequest", Store.ToJson(new Dictionary<string, object> { { "requestId", id } })); } catch { }
                }
            };
            // only VFS's API answers are paused (at the response stage); nothing else goes through this
            _ = core.CallDevToolsProtocolMethodAsync("Fetch.enable",
                "{\"patterns\":[{\"urlPattern\":\"" + VfsApi + "\",\"requestStage\":\"Response\"}]}");
#if AUTOMATION
            // test builds: CORS failures on VFS (to see whether a preflight is the one refused)
            var rxf = core.GetDevToolsProtocolEventReceiver("Network.loadingFailed");
            rxf.DevToolsProtocolEventReceived += (o, a) =>
            {
                if (a.ParameterObjectAsJson.IndexOf("corsErrorStatus", StringComparison.Ordinal) >= 0)
                    Blocking.TestLog.Add("CORSFAIL " + a.ParameterObjectAsJson.Substring(0, Math.Min(400, a.ParameterObjectAsJson.Length)));
            };
            var rxs = core.GetDevToolsProtocolEventReceiver("Network.requestWillBeSent");
            rxs.DevToolsProtocolEventReceived += (o, a) =>
            {
                string j = a.ParameterObjectAsJson;
                if (j.IndexOf("lift-api.vfsglobal.com", StringComparison.Ordinal) >= 0 && j.IndexOf("\"Preflight\"", StringComparison.Ordinal) >= 0)
                    Blocking.TestLog.Add("PREFLIGHT " + Regex.Match(j, "\"url\":\"[^\"?]*").Value);
            };
            _ = core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
#endif
        }
    }
}
