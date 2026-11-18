using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace GBrowser
{
    /// <summary>Network checks for error pages: adapter, internet (raw TCP to public resolvers, no web request),
    /// Windows DNS, the Secure DNS provider, the site's name, proxy and VPN. Each check has a time limit.
    /// Also starts Windows Network Diagnostics (ndfapi.dll, the API Chrome uses on Windows).</summary>
    internal static class NetDiag
    {
        public sealed class Check
        {
            public string Name, Detail;
            public bool Ok;
            public bool Info;   // informational (proxy / VPN present), not a failure
        }

        public sealed class Result
        {
            public List<Check> Checks = new List<Check>();
            public bool SecureDnsBlocked;   // the DoH provider is unreachable while plain DNS works
        }

        static async Task<bool> Tcp(string host, int port, int ms = 3000)
        {
            try
            {
                using (var c = new TcpClient())
                {
                    var t = c.ConnectAsync(host, port);
                    return await Task.WhenAny(t, Task.Delay(ms)) == t && !t.IsFaulted && c.Connected;
                }
            }
            catch { return false; }
        }

        static async Task<IPAddress[]> Resolve(string host, int ms = 3000)
        {
            try
            {
                var t = Dns.GetHostAddressesAsync(host);
                return await Task.WhenAny(t, Task.Delay(ms)) == t && !t.IsFaulted ? t.Result : null;
            }
            catch { return null; }
        }

        public static async Task<Result> Run(string url)
        {
            var r = new Result();
            string host = Blocking.HostOf(url);

            // 1. adapter
            var up = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up &&
                n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel).ToList();
            var phys = up.Where(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 || n.NetworkInterfaceType == NetworkInterfaceType.Ethernet).ToList();
            r.Checks.Add(new Check { Name = "Network connection", Ok = phys.Count > 0,
                Detail = phys.Count > 0 ? string.Join(", ", phys.Select(n => n.Name).Distinct()) : "No Wi-Fi or cable connection" });

            // 2. internet: TCP to public resolvers by IP (no DNS needed)
            bool net = await Tcp("1.1.1.1", 443) || await Tcp("8.8.8.8", 443);
            r.Checks.Add(new Check { Name = "Internet", Ok = net, Detail = net ? "Reachable" : "No route to the internet" });

            // 3. Windows DNS
            var g = await Resolve("www.google.com");
            bool winDns = g != null && g.Length > 0;
            r.Checks.Add(new Check { Name = "DNS (Windows)", Ok = winDns, Detail = winDns ? "Working" : "Not answering" });

            // 4. Secure DNS provider
            string tpl = Security.DnsTemplate(Store.Settings.SecureDns);
            if (tpl != null)
            {
                string dohHost = new Uri(tpl.Replace("{?dns}", "")).Host;
                bool doh = await Tcp(dohHost, 443);
                r.Checks.Add(new Check { Name = "Secure DNS", Ok = doh, Detail = doh ? dohHost + " reachable" : dohHost + " not reachable" });
                r.SecureDnsBlocked = !doh && net;
            }

            // 5. the site's name and server
            if (host != null && !Security.IsLocal(host))
            {
                var a = await Resolve(host);
                bool exists = a != null && a.Length > 0;
                bool reach = exists && (await Tcp(host, 443) || await Tcp(host, 80));
                r.Checks.Add(new Check { Name = host, Ok = exists && reach,
                    Detail = !exists ? (winDns ? "This name does not exist" : "Name could not be checked") : reach ? "Server answers" : "Server does not answer" });
            }

            // 6. proxy / VPN (information)
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
                    if (k?.GetValue("ProxyEnable") is int pe && pe == 1)
                        r.Checks.Add(new Check { Name = "Proxy", Info = true, Ok = true, Detail = "In use: " + (k.GetValue("ProxyServer") as string ?? "") });
            }
            catch { }
            var vpn = up.Where(n => n.NetworkInterfaceType == NetworkInterfaceType.Ppp ||
                n.Description.IndexOf("vpn", StringComparison.OrdinalIgnoreCase) >= 0 || n.Name.IndexOf("vpn", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.Description.IndexOf("wintun", StringComparison.OrdinalIgnoreCase) >= 0 || n.Description.IndexOf("TAP-", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.Description.IndexOf("WireGuard", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (vpn.Count > 0) r.Checks.Add(new Check { Name = "VPN", Info = true, Ok = true, Detail = "Active: " + string.Join(", ", vpn.Select(v => v.Name)) });
            return r;
        }

        // ---------------------------------------------------------------- Windows Network Diagnostics (ndfapi)
        [DllImport("ndfapi.dll", CharSet = CharSet.Unicode)] static extern int NdfCreateWebIncident(string url, out IntPtr handle);
        [DllImport("ndfapi.dll")] static extern int NdfExecuteDiagnosis(IntPtr handle, IntPtr hwnd);
        [DllImport("ndfapi.dll")] static extern int NdfCloseIncident(IntPtr handle);

        /// <summary>Opens Windows Network Diagnostics for this address (like Chrome); false when unavailable.</summary>
        public static bool WindowsDiagnostics(string url, IntPtr owner)
        {
            try
            {
                if (NdfCreateWebIncident(url, out IntPtr h) != 0 || h == IntPtr.Zero) return false;
                try { return NdfExecuteDiagnosis(h, owner) == 0; }
                finally { NdfCloseIncident(h); }
            }
            catch { return false; }
        }
    }
}
