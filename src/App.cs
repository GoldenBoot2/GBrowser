using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Drawing;
using Microsoft.Web.WebView2.Core;

namespace GBrowser
{
    /// <summary>The running GBrowser: one process per data folder with any number of windows (like Chrome). A second
    /// start (a link opened from another app once GBrowser is the default browser, the Start menu, the taskbar) hands
    /// its addresses to the running one and ends: they open as tabs in the last used window, or a new window when
    /// there are none. All windows share one engine (environment) and one profile.</summary>
    internal static class App
    {
        public static readonly List<MainForm> Windows = new List<MainForm>();
        static MainForm lastActive;
        public static MainForm Active => lastActive != null && !lastActive.IsDisposed && Windows.Contains(lastActive) ? lastActive : Windows.LastOrDefault();
        public static void Activated(MainForm f) { lastActive = f; }

        /// <summary>Closing every window (Exit, restart, update): the session is saved once, for all windows.</summary>
        public static bool Quitting;
        public static bool RestartAfter;
        static bool sessionDone;

        static string Id
        {
            get
            {
                using (var sha = SHA256.Create())
                {
                    var h = sha.ComputeHash(Encoding.UTF8.GetBytes(Store.DataDir.ToLowerInvariant()));
                    return BitConverter.ToString(h, 0, 8).Replace("-", "");
                }
            }
        }
        public static string MutexName => "Local\\GBrowser-" + Id;
        static string PipeName => "GBrowserOpen-" + Id;

        // ---------------------------------------------------------------- engine (shared by all windows)

        static Task<CoreWebView2Environment> envTask;
        public static Task<CoreWebView2Environment> Env()
        {
            if (envTask == null) envTask = CreateEnv();
            return envTask;
        }

        static async Task<CoreWebView2Environment> CreateEnv()
        {
            // Secure DNS: the engine's own DoH preference, read when the engine starts (see Security.ApplySecureDnsPref)
            Security.ActiveSecureDns = Store.Settings.SecureDns ?? "off";
            Security.ApplySecureDnsPref(Path.Combine(Store.WebDataDir, "EBWebView", "Local State"), Security.ActiveSecureDns);
            Cookies.ThirdPartyActive = Store.Settings.BlockThirdPartyCookies;
            Cookies.ApplyThirdPartyPref(Path.Combine(Store.WebDataDir, "EBWebView", "Default", "Preferences"), Cookies.ThirdPartyActive);
            var opts = new CoreWebView2EnvironmentOptions { AreBrowserExtensionsEnabled = true };
            return await CoreWebView2Environment.CreateAsync(null, Store.WebDataDir, opts);
        }

        // ---------------------------------------------------------------- windows

        public static MainForm NewWindow(IList<string> urls = null, WindowSession ws = null, bool show = true)
        {
            var f = new MainForm(urls?.ToList() ?? new List<string>(), ws);
            if (show) f.Show();
            return f;
        }

        /// <summary>Exit: every window closes, the session of all of them is kept for the next start.</summary>
        public static void Quit(bool restart = false)
        {
            if (Quitting) return;
            var first = Windows.FirstOrDefault();
            if (first != null && !first.ConfirmExit()) return;   // downloads running and the user keeps GBrowser open
            foreach (var w in Windows) w.CloseConfirmed = true;
            Quitting = true;
            RestartAfter = restart;
            SaveSession(Windows.ToList());
            foreach (var w in Windows.ToList()) { try { w.Close(); } catch { } }
        }

        /// <summary>Tabs and bounds of the given windows (the active one first). Saved when the last window closes or
        /// at Exit; the first window's tabs also go to the old single-window fields (older versions read those).</summary>
        public static void SaveSession(List<MainForm> forms)
        {
            if (sessionDone) return;
            sessionDone = true;
            var st = Store.Settings;
            var a = Active;
            if (a != null && forms.Remove(a)) forms.Insert(0, a);
            st.Windows = forms.Select(f => f.SessionState()).Where(w => w != null).ToList();
            var first = st.Windows.FirstOrDefault();
            st.Session = first?.Tabs ?? new List<string>();
            st.SessionIndex = first?.Index ?? 0;
            st.SessionPinned = first?.Pinned ?? 0;
            if (first != null) { st.WindowX = first.X; st.WindowY = first.Y; st.WindowW = first.W; st.WindowH = first.H; st.WindowMax = first.Max; }
            Store.SaveSettings();
        }

        // ---------------------------------------------------------------- one running copy

        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);

        /// <summary>Second start: hand the addresses to the running GBrowser. true = it took them.</summary>
        public static bool HandOver(string[] args)
        {
            try
            {
                AllowSetForegroundWindow(-1);   // ASFW_ANY: the running GBrowser may bring its window to the front
                using (var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut))
                {
                    pipe.Connect(3000);
                    var w = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
                    var r = new StreamReader(pipe, Encoding.UTF8);
                    w.WriteLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join("\n", args))));
                    return r.ReadLine() == "ok";
                }
            }
            catch { return false; }
        }

        public static void Listen(SynchronizationContext ui)
        {
            var th = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using (var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte))
                        {
                            pipe.WaitForConnection();
                            var r = new StreamReader(pipe, Encoding.UTF8);
                            var w = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
                            string line = r.ReadLine();
                            if (Quitting || line == null) { w.WriteLine("quit"); continue; }
                            var args = Encoding.UTF8.GetString(Convert.FromBase64String(line)).Split('\n').Where(a => a.Length > 0).ToArray();
                            w.WriteLine("ok");
                            ui.Post(_ => Open(args), null);
                        }
                    }
                    catch { Thread.Sleep(200); }
                }
            }) { IsBackground = true, Name = "GBrowser open requests" };
            th.Start();
        }

        /// <summary>Addresses from a second start: tabs in the last used window (like Chrome); no address = a new window.</summary>
        public static void Open(string[] args)
        {
            var urls = args.Where(a => !a.StartsWith("--")).Select(a => File.Exists(a) ? new Uri(Path.GetFullPath(a)).AbsoluteUri : Search.ToUrl(a))
                           .Where(u => u != null).ToList();
            var win = Active;
            if (urls.Count == 0 || win == null) { NewWindow(urls); return; }
            foreach (var u in urls) win.NewTab(u);
            win.BringToFront2();
        }
    }

    /// <summary>Runs until the last window closes.</summary>
    internal sealed class BrowserContext : ApplicationContext
    {
        public BrowserContext(MainForm first)
        {
            first.Show();
        }

        public void WindowClosed()
        {
            if (App.Windows.Count == 0) ExitThread();
        }
    }
}
