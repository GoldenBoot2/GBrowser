using System;
using System.Collections.Generic;
using System.Linq;
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
        string extraText;   // optional third choice, between Cancel and OK (DialogResult.Retry)
        readonly Image icon;
        readonly List<Tuple<string, string>> lines;   // (icon path data or null, text)
        Rectangle okRect, cancelRect, extraRect;

        // optional text fields (page prompt(), sign-in) and a checkbox ("Always allow ...")
        sealed class Field { public string Label; public MaterialTextBox Box; public Rectangle Outline; public int LabelY; public string[] Options; public int Sel; }
        readonly List<Field> fields = new List<Field>();
        string checkText; bool isChecked; Rectangle checkRect;

        void AddField(string label, string value, bool password)
        {
            var f = new Field { Label = label ?? "", Box = new MaterialTextBox { Font = Font, Password = password, AccessibleName = label } };
            f.Box.BackColor = Theme.SurfaceContainerHigh;
            f.Box.ForeColor = Theme.OnSurface;
            f.Box.Text = value ?? "";
            f.Box.GotFocus += (s, e) => Invalidate();
            f.Box.LostFocus += (s, e) => Invalidate();
            fields.Add(f);
            Controls.Add(f.Box);
        }
        // a choice among options (e.g. a bookmark's folder): opens a Material menu
        void AddChoice(string label, string[] options, int sel)
        {
            fields.Add(new Field { Label = label ?? "", Options = options, Sel = Math.Max(0, Math.Min(sel, options.Length - 1)) });
        }

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
                foreach (var f in fields)
                {
                    y += S(4);
                    if (f.Label.Length > 0) { f.LabelY = y; y += S(24); }
                    f.Outline = new Rectangle(S(24), y, w - S(48), S(44));
                    if (f.Box != null)
                    {
                        int bh = f.Box.PreferredHeight;
                        f.Box.Bounds = new Rectangle(f.Outline.X + S(14), f.Outline.Y + (f.Outline.Height - bh) / 2, f.Outline.Width - S(28), bh);
                    }
                    y += S(44) + S(8);
                }
                if (checkText != null)
                {
                    int th = TextRenderer.MeasureText(g, checkText, Font, new Size(w - S(48) - S(32), 0), TextFormatFlags.WordBreak).Height;
                    checkRect = new Rectangle(S(24), y + S(4), w - S(48), Math.Max(S(28), th + S(4)));
                    y += checkRect.Height + S(8);
                }
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
            foreach (var f in fields)
            {
                if (f.Label.Length > 0)
                    TextRenderer.DrawText(g, f.Label, Font, new Rectangle(f.Outline.X, f.LabelY, f.Outline.Width, S(20)), Theme.OnSurfaceVariant,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                bool foc = f.Box != null && f.Box.Focused;
                using (var p = Theme.Round(f.Outline, S(8)))
                using (var pen = new Pen(foc ? Theme.Primary : Theme.Outline, foc ? S(2) : 1)) g.DrawPath(pen, p);
                if (f.Options != null)
                {
                    TextRenderer.DrawText(g, f.Options[f.Sel], Font, new Rectangle(f.Outline.X + S(14), f.Outline.Y, f.Outline.Width - S(52), f.Outline.Height),
                        Theme.OnSurface, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                    Icons.Draw(g, IconData.ArrowDrop, f.Outline.Right - S(36), f.Outline.Y + (f.Outline.Height - S(24)) / 2, S(24), Theme.OnSurfaceVariant);
                }
            }
            if (checkText != null)
            {
                var box = new Rectangle(checkRect.X + S(2), checkRect.Y + S(4), S(18), S(18));
                using (var p = Theme.Round(box, S(3)))
                {
                    if (isChecked)
                    {
                        using (var b = new SolidBrush(Theme.Primary)) g.FillPath(b, p);
                        Icons.Draw(g, IconData.Check, box.X + S(1), box.Y + S(1), S(16), Theme.OnPrimary);
                    }
                    else using (var pen = new Pen(Theme.OnSurfaceVariant, S(2))) g.DrawPath(pen, p);
                }
                TextRenderer.DrawText(g, checkText, Font, new Rectangle(box.Right + S(12), checkRect.Y + S(2), checkRect.Width - S(32), checkRect.Height),
                    Theme.OnSurface, TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            }
            // buttons: [Cancel (text)] [OK (filled)] right-aligned
            using (var bf = new Font("Segoe UI Semibold", 10f))
            {
                int bh = S(40), by = ClientSize.Height - S(20) - bh, right = w - pad;
                int okW = TextRenderer.MeasureText(okText, bf).Width + S(40);
                okRect = new Rectangle(right - okW, by, okW, bh);
                using (var p = Theme.Round(okRect, bh / 2f)) using (var b = new SolidBrush(hover == 1 ? ControlPaint.Dark(Theme.Primary, 0.05f) : Theme.Primary)) g.FillPath(b, p);
                TextRenderer.DrawText(g, okText, bf, okRect, Theme.OnPrimary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                int left = okRect.X;
                if (extraText != null)
                {
                    int ew = TextRenderer.MeasureText(extraText, bf).Width + S(40);
                    extraRect = new Rectangle(okRect.X - S(8) - ew, by, ew, bh);
                    using (var p = Theme.Round(extraRect, bh / 2f)) using (var b = new SolidBrush(hover == 2 ? ControlPaint.Dark(Theme.PrimaryContainer, 0.05f) : Theme.PrimaryContainer)) g.FillPath(b, p);
                    TextRenderer.DrawText(g, extraText, bf, extraRect, Theme.OnPrimaryContainer, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    left = extraRect.X;
                }
                if (cancelText != null)
                {
                    int cw = TextRenderer.MeasureText(cancelText, bf).Width + S(32);
                    cancelRect = new Rectangle(left - S(8) - cw, by, cw, bh);
                    if (hover == 0) using (var p = Theme.Round(cancelRect, bh / 2f)) using (var b = new SolidBrush(Theme.Hover)) g.FillPath(b, p);
                    TextRenderer.DrawText(g, cancelText, bf, cancelRect, Theme.Primary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = okRect.Contains(e.Location) ? 1 : extraText != null && extraRect.Contains(e.Location) ? 2 : cancelRect.Contains(e.Location) ? 0 :
                checkText != null && checkRect.Contains(e.Location) ? 3 : -1;
            if (h != hover) { hover = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (checkText != null && checkRect.Contains(e.Location)) { isChecked = !isChecked; Invalidate(); return; }
            if (okRect.Contains(e.Location)) { DialogResult = DialogResult.OK; Close(); }
            else if (extraText != null && extraRect.Contains(e.Location)) { DialogResult = DialogResult.Retry; Close(); }
            else if (cancelText != null && cancelRect.Contains(e.Location)) { DialogResult = DialogResult.Cancel; Close(); }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); return true; }
            if (keyData == Keys.Enter) { DialogResult = DialogResult.OK; Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            foreach (var f in fields)
            {
                if (!f.Outline.Contains(e.Location)) continue;
                if (f.Box != null) { f.Box.Focus(); return; }
                var m = new MaterialMenu();
                var fld = f;
                for (int i = 0; i < f.Options.Length; i++)
                {
                    int k = i;
                    var it = m.AddToggle(f.Options[i], i == f.Sel, true, () => { fld.Sel = k; Invalidate(); });
                }
                m.ShowAt(this, f.Outline, false, null);
                return;
            }
        }

        static MaterialDialog open;   // the dialog on screen (test builds answer it through the test channel)

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            open = this;
            if (fields.Count > 0 && fields[0].Box != null) { fields[0].Box.Focus(); fields[0].Box.SelectAll(); }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (open == this) open = null;
            base.OnFormClosed(e);
        }

#if AUTOMATION
        public static string TestInfo() => open == null ? "none" : open.title + " | " + string.Join(" / ", open.lines.Select(l => l.Item2)) +
            " | fields " + open.fields.Count + (open.checkText != null ? " | check: " + open.checkText + (open.isChecked ? " [x]" : " [ ]") : "") +
            " | buttons " + open.cancelText + " / " + open.okText;
        public static void TestType(int i, string s)
        {
            if (open == null || i >= open.fields.Count) return;
            var f = open.fields[i];
            if (f.Box == null) { if (int.TryParse(s, out var n) && n >= 0 && n < f.Options.Length) { f.Sel = n; open.Invalidate(); } return; }
            f.Box.Clear(); f.Box.TypeForTest(s);
        }
        public static void TestCheck() { if (open != null) { open.isChecked = !open.isChecked; open.Invalidate(); } }
        public static void TestClose(bool ok) { if (open != null) { open.DialogResult = ok ? DialogResult.OK : DialogResult.Cancel; open.Close(); } }
        public static IntPtr TestHandle => open != null && open.IsHandleCreated ? open.Handle : IntPtr.Zero;
#endif

        /// <summary>A dialog with text fields (label, value, password): their texts when the user chose OK, else null.</summary>
        public static string[] Prompt(IWin32Window owner, string title, IEnumerable<Tuple<string, string>> lines, IEnumerable<Tuple<string, string, bool>> inputs,
            string ok, string cancel = "Cancel")
        {
            using (var d = new MaterialDialog(title, null, new List<Tuple<string, string>>(lines), ok, cancel))
            {
                foreach (var i in inputs) d.AddField(i.Item1, i.Item2, i.Item3);
                if (d.ShowDialog(owner) != DialogResult.OK) return null;
                return d.fields.Select(f => f.Box.Text).ToArray();
            }
        }

        /// <summary>Text fields and choices (label, value, options: null = text field, value = the chosen index). Returns 1
        /// for ok, 2 for extra, 0 for cancel / Esc; values = the texts / chosen indexes.</summary>
        public static int Edit(IWin32Window owner, string title, IEnumerable<Tuple<string, string>> lines, IEnumerable<Tuple<string, string, string[]>> inputs,
            out string[] values, string ok, string extra, string cancel)
        {
            using (var d = new MaterialDialog(title, null, new List<Tuple<string, string>>(lines), ok, cancel) { extraText = extra })
            {
                foreach (var i in inputs)
                {
                    if (i.Item3 == null) d.AddField(i.Item1, i.Item2, false);
                    else d.AddChoice(i.Item1, i.Item3, int.TryParse(i.Item2, out var n) ? n : 0);
                }
                var r = d.ShowDialog(owner);
                values = d.fields.Select(f => f.Box != null ? f.Box.Text : f.Sel.ToString()).ToArray();
                return r == DialogResult.OK ? 1 : r == DialogResult.Retry ? 2 : 0;
            }
        }

        /// <summary>A dialog with a checkbox under the text: true when the user chose OK; isChecked = the box.</summary>
        public static bool AskWithCheck(IWin32Window owner, string title, IEnumerable<Tuple<string, string>> lines, string check, out bool isChecked,
            string ok, string cancel = "Cancel")
        {
            using (var d = new MaterialDialog(title, null, new List<Tuple<string, string>>(lines), ok, cancel) { checkText = check })
            {
                bool r = d.ShowDialog(owner) == DialogResult.OK;
                isChecked = r && d.isChecked;
                return r;
            }
        }

        /// <summary>Shows the dialog; true when the user chose OK.</summary>
        public static bool Ask(IWin32Window owner, string title, Image icon, IEnumerable<Tuple<string, string>> lines, string ok, string cancel = "Cancel")
        {
            using (var d = new MaterialDialog(title, icon, new List<Tuple<string, string>>(lines), ok, cancel))
                return d.ShowDialog(owner) == DialogResult.OK;
        }

        /// <summary>Three choices: returns 1 for ok (filled button), 2 for extra (tonal), 0 for cancel / Esc.</summary>
        public static int Choose(IWin32Window owner, string title, Image icon, IEnumerable<Tuple<string, string>> lines, string ok, string extra, string cancel = "Cancel")
        {
            using (var d = new MaterialDialog(title, icon, new List<Tuple<string, string>>(lines), ok, cancel) { extraText = extra })
            {
                var r = d.ShowDialog(owner);
                return r == DialogResult.OK ? 1 : r == DialogResult.Retry ? 2 : 0;
            }
        }

        public static void Info(IWin32Window owner, string title, string text) =>
            Ask(owner, title, null, new[] { Tuple.Create((string)null, text) }, "OK", null);
    }
}
