using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GBrowser
{
    /// <summary>Material 3 dialog (rounded surface, title, body lines with icons, text + filled buttons) instead of the
    /// Windows message box. Esc / Cancel = false, Enter / OK = true.</summary>
    internal sealed class MaterialDialog : Form
    {
        readonly string title, okText, cancelText;
        readonly Image icon;
        readonly List<Tuple<string, string>> lines;   // (icon path data or null, text)
        Rectangle okRect, cancelRect;
        int hover = -1;

        MaterialDialog(string title, Image icon, List<Tuple<string, string>> lines, string ok, string cancel)
        {
            this.title = title; this.icon = icon; this.lines = lines; okText = ok; cancelText = cancel;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            KeyPreview = true;
            BackColor = Theme.SurfaceContainerHigh;
            Font = new Font("Segoe UI", 10f);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            AccessibleRole = AccessibleRole.Dialog;
            AccessibleName = title;
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ClassStyle |= 0x00020000; /* CS_DROPSHADOW */ return cp; }
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int s);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = 2;   // DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND (Windows 11)
            try { DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { }
            Theme.TitleBar(Handle);
        }

        int S(float v) => (int)Math.Round(v * DeviceDpi / 96f);

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            using (var g = CreateGraphics())
            using (var tf = new Font("Segoe UI Semibold", 15f))
            {
                int w = S(420), y = S(24);
                if (icon != null) y += S(40) + S(16);
                y += TextRenderer.MeasureText(g, title, tf, new Size(w - S(48), 0), TextFormatFlags.WordBreak).Height + S(16);
                foreach (var l in lines)
                    y += Math.Max(S(24), TextRenderer.MeasureText(g, l.Item2, Font, new Size(w - S(48) - S(36), 0), TextFormatFlags.WordBreak).Height) + S(10);
                y += S(20) + S(40) + S(20);
                ClientSize = new Size(w, y);
            }
            if (Owner != null) Location = new Point(Owner.Left + (Owner.Width - Width) / 2, Owner.Top + Math.Max(S(80), (Owner.Height - Height) / 3));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            int pad = S(24), y = S(24), w = ClientSize.Width;
            if (icon != null) { g.DrawImage(icon, pad, y, S(40), S(40)); y += S(40) + S(16); }
            using (var tf = new Font("Segoe UI Semibold", 15f))
            {
                var r = new Rectangle(pad, y, w - 2 * pad, 0);
                var sz = TextRenderer.MeasureText(g, title, tf, new Size(r.Width, 0), TextFormatFlags.WordBreak);
                TextRenderer.DrawText(g, title, tf, new Rectangle(pad, y, r.Width, sz.Height), Theme.OnSurface, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                y += sz.Height + S(16);
            }
            foreach (var l in lines)
            {
                int tx = pad;
                if (l.Item1 != null) { Icons.Draw(g, l.Item1, pad, y + S(1), S(20), Theme.OnSurfaceVariant); tx += S(36); }
                var sz = TextRenderer.MeasureText(g, l.Item2, Font, new Size(w - pad - tx, 0), TextFormatFlags.WordBreak);
                TextRenderer.DrawText(g, l.Item2, Font, new Rectangle(tx, y, w - pad - tx, sz.Height), Theme.OnSurfaceVariant, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                y += Math.Max(S(24), sz.Height) + S(10);
            }
            // buttons: [Cancel (text)] [OK (filled)] right-aligned
            using (var bf = new Font("Segoe UI Semibold", 10f))
            {
                int bh = S(40), by = ClientSize.Height - S(20) - bh, right = w - pad;
                int okW = TextRenderer.MeasureText(okText, bf).Width + S(40);
                okRect = new Rectangle(right - okW, by, okW, bh);
                using (var p = Theme.Round(okRect, bh / 2f)) using (var b = new SolidBrush(hover == 1 ? ControlPaint.Dark(Theme.Primary, 0.05f) : Theme.Primary)) g.FillPath(b, p);
                TextRenderer.DrawText(g, okText, bf, okRect, Theme.OnPrimary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                if (cancelText != null)
                {
                    int cw = TextRenderer.MeasureText(cancelText, bf).Width + S(32);
                    cancelRect = new Rectangle(okRect.X - S(8) - cw, by, cw, bh);
                    if (hover == 0) using (var p = Theme.Round(cancelRect, bh / 2f)) using (var b = new SolidBrush(Theme.Hover)) g.FillPath(b, p);
                    TextRenderer.DrawText(g, cancelText, bf, cancelRect, Theme.Primary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = okRect.Contains(e.Location) ? 1 : cancelRect.Contains(e.Location) ? 0 : -1;
            if (h != hover) { hover = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (okRect.Contains(e.Location)) { DialogResult = DialogResult.OK; Close(); }
            else if (cancelText != null && cancelRect.Contains(e.Location)) { DialogResult = DialogResult.Cancel; Close(); }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); return true; }
            if (keyData == Keys.Enter) { DialogResult = DialogResult.OK; Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        /// <summary>Shows the dialog; true when the user chose OK.</summary>
        public static bool Ask(IWin32Window owner, string title, Image icon, IEnumerable<Tuple<string, string>> lines, string ok, string cancel = "Cancel")
        {
            using (var d = new MaterialDialog(title, icon, new List<Tuple<string, string>>(lines), ok, cancel))
                return d.ShowDialog(owner) == DialogResult.OK;
        }

        public static void Info(IWin32Window owner, string title, string text) =>
            Ask(owner, title, null, new[] { Tuple.Create((string)null, text) }, "OK", null);
    }
}
