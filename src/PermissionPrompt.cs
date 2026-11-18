using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace GBrowser
{
    /// <summary>Site permission requests (camera, microphone, location, notifications, clipboard...) in GBrowser's own
    /// prompt under the address bar, like Chrome's: "Allow while visiting the site" (remembered), "Allow this time",
    /// "Never allow" (remembered). Closing it (Esc, a click elsewhere) blocks this time only. Remembered choices are
    /// kept by the engine in the profile (Settings > Site settings lists and resets them).</summary>
    internal sealed class PermissionPrompt : Form
    {
        public static string What(CoreWebView2PermissionKind k)
        {
            switch (k)
            {
                case CoreWebView2PermissionKind.Camera: return "Use your camera";
                case CoreWebView2PermissionKind.Microphone: return "Use your microphone";
                case CoreWebView2PermissionKind.Geolocation: return "Know your location";
                case CoreWebView2PermissionKind.Notifications: return "Show notifications";
                case CoreWebView2PermissionKind.ClipboardRead: return "See text and images copied to the clipboard";
                case CoreWebView2PermissionKind.MultipleAutomaticDownloads: return "Download multiple files";
                case CoreWebView2PermissionKind.FileReadWrite: return "Edit files on your device";
                case CoreWebView2PermissionKind.Autoplay: return "Play media automatically";
                case CoreWebView2PermissionKind.LocalFonts: return "Use fonts installed on your device";
                case CoreWebView2PermissionKind.MidiSystemExclusiveMessages: return "Control and reprogram MIDI devices";
                case CoreWebView2PermissionKind.WindowManagement: return "Manage windows on all your displays";
                case CoreWebView2PermissionKind.OtherSensors: return "Use motion or light sensors";
                case CoreWebView2PermissionKind.PersistentStorage: return "Store data on your device";
                default: return "Use a device feature";
            }
        }

        public static string IconFor(CoreWebView2PermissionKind k)
        {
            switch (k)
            {
                case CoreWebView2PermissionKind.Camera: return IconData.Videocam;
                case CoreWebView2PermissionKind.Microphone: return IconData.Mic;
                case CoreWebView2PermissionKind.Geolocation: return IconData.Location;
                case CoreWebView2PermissionKind.Notifications: return IconData.Notifications;
                case CoreWebView2PermissionKind.ClipboardRead: return IconData.Copy;
                case CoreWebView2PermissionKind.MultipleAutomaticDownloads: return IconData.Download;
                case CoreWebView2PermissionKind.FileReadWrite: return IconData.File;
                case CoreWebView2PermissionKind.Autoplay: return IconData.Play;
                default: return IconData.Tune;
            }
        }

        /// <summary>The engine asks: GBrowser shows its prompt and answers when the user chooses.</summary>
        public static void Ask(CoreWebView2PermissionRequestedEventArgs e, Form owner, Func<Rectangle> anchorOnScreen)
        {
            string host;
            try { host = new Uri(e.Uri).Host; } catch { host = e.Uri; }
            if (host.StartsWith("www.")) host = host.Substring(4);
            var deferral = e.GetDeferral();
            e.Handled = true;   // no engine (Microsoft Edge) prompt
            var p = new PermissionPrompt(host, e.PermissionKind);
            bool answered = false;
            p.Chose += (state, remember) =>
            {
                if (answered) return;
                answered = true;
                try { e.State = state; e.SavesInProfile = remember; deferral.Complete(); } catch { }
            };
            p.FormClosed += (s, a) =>
            {
                if (answered) return;   // dismissed: blocked this time, asked again next time
                answered = true;
                try { e.State = CoreWebView2PermissionState.Deny; e.SavesInProfile = false; deferral.Complete(); } catch { }
            };
            var at = anchorOnScreen();
            p.Location = new Point(at.X, at.Bottom + p.S(6));
            p.Show(owner);
        }

        public event Action<CoreWebView2PermissionState, bool> Chose;
        readonly string host, what, icon;
        readonly string[] labels = { "Allow while visiting the site", "Allow this time", "Never allow" };
        Rectangle[] buttons = new Rectangle[3];
        Rectangle closeRect;
        int hover = -1;
        readonly Font titleFont = new Font("Segoe UI Semibold", 11f), font = new Font("Segoe UI", 10f), bold = new Font("Segoe UI Semibold", 10f);

        PermissionPrompt(string host, CoreWebView2PermissionKind kind)
        {
            this.host = host; what = What(kind); icon = IconFor(kind);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            ClientSize = new Size(S(340), S(16 + 28 + 40 + 3 * 48 + 12));
            AccessibleRole = AccessibleRole.Dialog;
            AccessibleName = host + " wants to " + what;
        }

        int S(float v) => (int)Math.Round(v * DeviceDpi / 96f);

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x00000080; cp.ClassStyle |= 0x00020000; return cp; }   // tool window, shadow
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int s);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = 2; try { DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { }
            int border = ColorTranslator.ToWin32(Theme.OutlineVariant); try { DwmSetWindowAttribute(Handle, 34, ref border, 4); } catch { }
        }

        protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); BeginInvoke((Action)Close); }

        protected override bool ProcessCmdKey(ref Message msg, Keys k)
        {
            if (k == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, k);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.SurfaceContainerHigh);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int pad = S(16), y = S(16);
            TextRenderer.DrawText(g, host + " wants to", titleFont, new Rectangle(pad, y, Width - pad * 2 - S(32), S(26)), Theme.OnSurface,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            closeRect = new Rectangle(Width - pad - S(28), y - S(1), S(28), S(28));
            if (hover == 9) using (var b = new SolidBrush(Theme.Hover)) g.FillEllipse(b, closeRect);
            Icons.Draw(g, IconData.Close, closeRect.X + S(5), closeRect.Y + S(5), S(18), Theme.OnSurfaceVariant);
            y += S(28) + S(8);
            Icons.Draw(g, icon, pad, y + S(6), S(20), Theme.OnSurfaceVariant);
            TextRenderer.DrawText(g, what, font, new Rectangle(pad + S(32), y, Width - pad * 2 - S(32), S(32)), Theme.OnSurfaceVariant,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            y += S(40);
            for (int i = 0; i < 3; i++)
            {
                buttons[i] = new Rectangle(pad, y, Width - pad * 2, S(40));
                using (var p = Theme.Round(buttons[i], S(20)))
                using (var b = new SolidBrush(hover == i ? ControlPaint.Dark(Theme.PrimaryContainer, 0.05f) : Theme.PrimaryContainer)) g.FillPath(b, p);
                TextRenderer.DrawText(g, labels[i], bold, buttons[i], Theme.OnPrimaryContainer, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                y += S(48);
            }
        }

        int Hit(Point p)
        {
            if (closeRect.Contains(p)) return 9;
            for (int i = 0; i < 3; i++) if (buttons[i].Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = Hit(e.Location);
            if (h != hover) { hover = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            int h = Hit(e.Location);
            if (h == 9) { Close(); return; }
            if (h < 0) return;
            Choose(h);
        }

        /// <summary>0 = allow while visiting (remembered), 1 = allow this time, 2 = never (remembered).</summary>
        public void Choose(int i)
        {
            Chose?.Invoke(i == 2 ? CoreWebView2PermissionState.Deny : CoreWebView2PermissionState.Allow, i != 1);
            Close();
        }

        public static PermissionPrompt Open => Application.OpenForms.Count == 0 ? null : FindOpen();
        static PermissionPrompt FindOpen() { foreach (Form f in Application.OpenForms) if (f is PermissionPrompt p) return p; return null; }
    }
}
