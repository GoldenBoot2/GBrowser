using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace GBrowser
{
    /// <summary>Site info menu > "Certificate is valid": the page's certificate (issued to / by, validity, SHA-256) in a
    /// Material dialog, "Details" = Windows' certificate viewer with the whole chain. The chain comes from the engine
    /// over DevTools (Network.getCertificate when the network domain was on; else the Security domain's state).</summary>
    internal static class CertViewer
    {
        public static async Task<List<X509Certificate2>> Chain(CoreWebView2 core, string origin)
        {
            var list = new List<X509Certificate2>();
            try
            {
                var r = Store.FromJson<Dictionary<string, object>>(await core.CallDevToolsProtocolMethodAsync("Network.getCertificate",
                    Store.ToJson(new Dictionary<string, object> { { "origin", origin } })));
                if (r.TryGetValue("tableNames", out var tn) && tn is System.Collections.IEnumerable names)
                    foreach (var n in names) if (n is string b64 && b64.Length > 0) list.Add(new X509Certificate2(Convert.FromBase64String(b64)));
            }
            catch { }
            if (list.Count > 0) return list;
            // the Security domain reports the visible page's state (with its certificate chain) as soon as it is enabled
            var rcv = core.GetDevToolsProtocolEventReceiver("Security.visibleSecurityStateChanged");
            var got = new TaskCompletionSource<string>();
            EventHandler<CoreWebView2DevToolsProtocolEventReceivedEventArgs> h = (s, e) => got.TrySetResult(e.ParameterObjectAsJson);
            rcv.DevToolsProtocolEventReceived += h;
            try
            {
                await core.CallDevToolsProtocolMethodAsync("Security.enable", "{}");
                if (await Task.WhenAny(got.Task, Task.Delay(3000)) == got.Task)
                {
                    var d = Store.FromJson<Dictionary<string, object>>(got.Task.Result);
                    var vs = d["visibleSecurityState"] as Dictionary<string, object>;
                    var cs = vs != null && vs.TryGetValue("certificateSecurityState", out var c) ? c as Dictionary<string, object> : null;
                    if (cs != null && cs.TryGetValue("certificate", out var certs) && certs is System.Collections.IEnumerable arr)
                        foreach (var x in arr) if (x is string b64 && b64.Length > 0) list.Add(new X509Certificate2(Convert.FromBase64String(b64)));
                }
            }
            catch { }
            finally
            {
                rcv.DevToolsProtocolEventReceived -= h;
                try { await core.CallDevToolsProtocolMethodAsync("Security.disable", "{}"); } catch { }
            }
            return list;
        }

        static string Part(string dn, string key)
        {
            foreach (var p in dn.Split(','))
            {
                var t = p.Trim();
                if (t.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) return t.Substring(key.Length + 1).Trim('"');
            }
            return null;
        }

        public static async void Show(IWin32Window owner, CoreWebView2 core, string origin)
        {
            var chain = await Chain(core, origin);
            if (chain.Count == 0)
            {
                MaterialDialog.Ask(owner, "Certificate", null, new[] { Tuple.Create(IconData.Info, "Not available for this page.") }, "Close", null);
                return;
            }
            var c = chain[0];
            string sha;
            using (var h = System.Security.Cryptography.SHA256.Create())
                sha = string.Join(" ", h.ComputeHash(c.RawData).Select(b => b.ToString("X2")));   // spaced like Chrome, so it wraps
            var lines = new List<Tuple<string, string>>
            {
                Tuple.Create(IconData.Language, "Issued to: " + (Part(c.Subject, "CN") ?? c.Subject) + (Part(c.Subject, "O") is string o ? " (" + o + ")" : "")),
                Tuple.Create(IconData.Verified, "Issued by: " + (Part(c.Issuer, "CN") ?? c.Issuer) + (Part(c.Issuer, "O") is string io ? " (" + io + ")" : "")),
                Tuple.Create(IconData.History, "Valid from " + c.NotBefore.ToString("dd-MM-yyyy") + " to " + c.NotAfter.ToString("dd-MM-yyyy")),
                Tuple.Create(IconData.Lock, "SHA-256: " + sha),
            };
            if (MaterialDialog.Choose(owner, "Certificate", null, lines, "Close", "Details", null) == 2)
            {
                // Windows' own viewer: general, details and the certification path
                try { X509Certificate2UI.DisplayCertificate(c, owner is Control ctl ? ctl.Handle : IntPtr.Zero); } catch { }
            }
        }
    }
}
