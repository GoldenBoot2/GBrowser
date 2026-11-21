using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace GBrowser
{
    /// <summary>An extension's toolbar popup (action.default_popup) like Chrome's: a bubble under the icon that opened it,
    /// sized to the popup page the way Chrome does it (assets/extpopup.js: min-content width, height at that width,
    /// 25..800 x 25..600 CSS px, scrollbars beyond) and resized whenever the page changes; closes when it loses focus.</summary>
    internal sealed class ExtensionPopup : Form
    {
        readonly WebView2 view = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.White };
        public CoreWebView2 Core => view.CoreWebView2;   // test channel: read / press things in the popup
        public readonly string ExtensionId;
        readonly Rectangle anchor;                        // screen rectangle of the toolbar icon that opened it
        readonly Timer poll = new Timer { Interval = 80 };
        bool ready, sized, polling;
        public Size CssSize { get; private set; }         // last size the page asked for (CSS px), for tests

        // a click on the icon that opened the popup closes it (focus moves away first); that click must not reopen it
        static string closedId;
        static DateTime closedAt;
        public static bool JustClosed(string id) => id == closedId && (DateTime.UtcNow - closedAt).TotalMilliseconds < 400;

        static string sizer;
        static string Sizer
        {
            get
            {
                if (sizer == null)
                    try { sizer = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "extpopup.js")); }
                    catch { sizer = ""; }
                return sizer;
            }
        }

        public ExtensionPopup(CoreWebView2Environment env, string extensionId, string url, Rectangle anchorScreen, Action<string> openTab)
        {
            ExtensionId = extensionId;
            anchor = anchorScreen;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "";
            BackColor = Color.White;
            // hidden at Chrome's maximum size until the page has been measured
            float sc = DeviceDpi / 96f;
            Size = new Size((int)(800 * sc), (int)(600 * sc));
            Opacity = 0;
            Place();
            Controls.Add(view);
            poll.Tick += async (s, e) => await Poll();
            Load += async (s, e) =>
            {
                await view.EnsureCoreWebView2Async(env);
                var core = view.CoreWebView2;
                core.NewWindowRequested += (o, a) => { a.Handled = true; openTab(a.Uri); };
                core.WindowCloseRequested += (o, a) => Close();
                try { if (Sizer.Length > 0) await core.AddScriptToExecuteOnDocumentCreatedAsync(Sizer); } catch { }
                // in case the engine did not run document-start scripts on this page: the script guards itself
                core.DOMContentLoaded += async (o, a) => { try { if (Sizer.Length > 0) await core.ExecuteScriptAsync(Sizer); } catch { } };
                core.NavigationCompleted += (o, a) =>
                {
                    ready = true;
                    // a page that never reports a size (script blocked, error page) is still shown
                    var t = new Timer { Interval = 1500 };
                    t.Tick += (o2, e2) => { t.Dispose(); if (!IsDisposed) Opacity = 1; };
                    t.Start();
                };
                poll.Start();
                core.Navigate(url);
            };
        }

        /// <summary>Asks the page for its size and applies it (the page answers "" when nothing changed).</summary>
        async System.Threading.Tasks.Task Poll()
        {
            if (polling || Core == null || IsDisposed) return;
            polling = true;
            try
            {
                var r = await Core.ExecuteScriptAsync("window.__gbps ? window.__gbps() : ''");
                if (IsDisposed) return;
                r = r.Trim('"');
                var p = r.Split(',');
                if (p.Length == 2 && int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int w)
                    && int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h))
                {
                    CssSize = new Size(w, h);
                    float k = (float)view.ZoomFactor * DeviceDpi / 96f;
                    ClientSize = new Size((int)Math.Round(w * k), (int)Math.Round(h * k));
                    Place();
                    if (!sized) { sized = true; Opacity = 1; }
                }
            }
            catch { }
            finally { polling = false; }
        }

        /// <summary>Under the anchor icon, right edges lined up (Chrome), kept on the screen.</summary>
        void Place()
        {
            int gap = (int)Math.Round(4 * DeviceDpi / 96f);
            var wa = Screen.FromRectangle(anchor).WorkingArea;
            int x = anchor.Right - Width, y = anchor.Bottom + gap;
            if (y + Height > wa.Bottom) y = Math.Max(wa.Top, wa.Bottom - Height);
            x = Math.Max(wa.Left, Math.Min(x, wa.Right - Width));
            Location = new Point(x, y);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00000080;          // WS_EX_TOOLWINDOW: no taskbar / Alt+Tab entry
                cp.ClassStyle |= 0x00020000;       // CS_DROPSHADOW (Windows 10; Windows 11 adds its own)
                return cp;
            }
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int s);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = 2;   // DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND: rounded bubble with shadow (Windows 11)
            try { DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { }
            int border = ColorTranslator.ToWin32(Theme.OutlineVariant);   // DWMWA_BORDER_COLOR
            try { DwmSetWindowAttribute(Handle, 34, ref border, 4); } catch { }
        }

        /// <summary>Only for popups opened by the test channel: they must not take the user's focus.</summary>
        public bool NoActivate;
        protected override bool ShowWithoutActivation => NoActivate;

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            // the popup's own dialogs (file pickers etc.) also deactivate it; close only after it has loaded
            if (ready) BeginInvoke((Action)Close);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            closedId = ExtensionId;
            closedAt = DateTime.UtcNow;
            poll.Stop();
            poll.Dispose();
            base.OnFormClosed(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
