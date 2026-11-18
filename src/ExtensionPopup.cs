using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace GBrowser
{
    /// <summary>An extension's toolbar popup (action.default_popup) shown under the toolbar, sized to its content,
    /// closed when it loses focus (like Chrome).</summary>
    internal sealed class ExtensionPopup : Form
    {
        readonly WebView2 view = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.White };
        readonly Action<string> openTab;
        bool ready;

        public ExtensionPopup(CoreWebView2Environment env, string url, Point anchorRightTop, Action<string> openTab)
        {
            this.openTab = openTab;
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            ControlBox = false;
            Text = "";
            BackColor = Color.White;
            float sc = DeviceDpi / 96f;
            Size = new Size((int)(380 * sc), (int)(240 * sc));
            Location = new Point(anchorRightTop.X - Width, anchorRightTop.Y);
            Controls.Add(view);
            Load += async (s, e) =>
            {
                await view.EnsureCoreWebView2Async(env);
                var core = view.CoreWebView2;
                core.NewWindowRequested += (o, a) => { a.Handled = true; openTab(a.Uri); };
                core.WindowCloseRequested += (o, a) => Close();
                core.NavigationCompleted += async (o, a) =>
                {
                    try
                    {
                        // size the window to the popup document, within Chrome's popup limits (25..800 x 25..600)
                        await System.Threading.Tasks.Task.Delay(120);
                        var r = await core.ExecuteScriptAsync("[Math.ceil(document.documentElement.scrollWidth), Math.ceil(document.documentElement.scrollHeight)]");
                        var parts = r.Trim('[', ']').Split(',');
                        int w = int.Parse(parts[0]), h = int.Parse(parts[1]);
                        w = Math.Max(25, Math.Min(800, w)); h = Math.Max(25, Math.Min(600, h));
                        var client = new Size((int)(w * view.ZoomFactor * DeviceDpi / 96f), (int)(h * view.ZoomFactor * DeviceDpi / 96f));
                        int right = Right;
                        ClientSize = client;
                        Left = right - Width;
                        var wa = Screen.FromControl(this).WorkingArea;
                        if (Bottom > wa.Bottom) Top = Math.Max(wa.Top, wa.Bottom - Height);
                        if (Left < wa.Left) Left = wa.Left;
                    }
                    catch { }
                    ready = true;
                };
                core.Navigate(url);
            };
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            // the popup's own dialogs (file pickers etc.) also deactivate it; close only after it has loaded
            if (ready) BeginInvoke((Action)Close);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
