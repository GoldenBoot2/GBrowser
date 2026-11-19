using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GBrowser
{
    /// <summary>Single-line text field drawn by GBrowser itself (Material 3), replacing the classic Windows EDIT control:
    /// theme-coloured selection and caret, Material context menu (Undo, Cut, Copy, Paste, Paste and go, Delete,
    /// Select all), placeholder, word-wise keyboard editing, mouse/drag/double-click selection, undo/redo, IME
    /// composition window at the caret.</summary>
    internal sealed class MaterialTextBox : Control
    {
        string text = "";
        int caret, anchor;               // selection = [min(caret, anchor), max(...))
        int scroll;                      // pixels scrolled left
        int[] pos = { 0 };               // x of each caret position (pos.Length == text.Length + 1)
        bool caretOn, mouseSelecting, selectAllOnClick;
        readonly Timer blink = new Timer { Interval = 530 };
        readonly List<Tuple<string, int, int>> undo = new List<Tuple<string, int, int>>(), redo = new List<Tuple<string, int, int>>();
        DateTime lastEdit = DateTime.MinValue;
        int clicks; DateTime lastClick; Point lastClickPt;

        public string Placeholder = "";
        public bool Password;                       // shows dots, never copies
        string Shown => Password ? new string('\u25CF', text.Length) : text;
        public event Action<string> PasteAndGo;
        /// <summary>The user changed the text (typing, deleting, paste). The flag: text was added at the end (the
        /// address bar then completes it inline, like Chrome; not after a deletion).</summary>
        public event Action<bool> UserEdited;

        public MaterialTextBox()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.Selectable | ControlStyles.StandardClick | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.IBeam;
            TabStop = true;
            AccessibleRole = AccessibleRole.Text;
            blink.Tick += (s, e) => { caretOn = !caretOn; InvalidateCaret(); };
        }

        protected override bool CanEnableIme => true;

        // ------------------------------------------------------------ public API (TextBox-like)

        public override string Text
        {
            get => text;
            set
            {
                value = (value ?? "").Replace("\r", "").Replace("\n", " ");
                if (value == text) return;
                text = value;
                caret = anchor = Math.Min(caret, text.Length);
                undo.Clear(); redo.Clear();
                Changed(false);
            }
        }

        public int TextLength => text.Length;

#if AUTOMATION
        /// <summary>Test builds: types like the keyboard (one character at a time, same code path).</summary>
        public void TypeForTest(string s) { foreach (char c in s) Insert(c.ToString()); }
        public void KeyForTest(Keys k) => OnKeyDown(new KeyEventArgs(k));
        /// <summary>Test builds: client x of the middle of the first <paramref name="sub"/> as shown now (-1 if absent).</summary>
        public int TestXOf(string sub) { int i = text.IndexOf(sub, StringComparison.Ordinal); return i < 0 ? -1 : (pos[i] + pos[i + sub.Length]) / 2 - scroll; }
#endif
        public int SelectionStart => Math.Min(caret, anchor);
        public int SelectionLength => Math.Abs(caret - anchor);
        public string SelectedText => text.Substring(SelectionStart, SelectionLength);

        public void SelectAll() { anchor = 0; caret = text.Length; ScrollToCaret(); Invalidate(); }
        public void Select(int start, int length) { anchor = Clamp(start); caret = Clamp(start + length); ScrollToCaret(); Invalidate(); }
        public void Clear() { Edit("", 0, text.Length, 0); }

        public int PreferredHeight => Font.Height + 4;

        int Clamp(int i) => Math.Max(0, Math.Min(text.Length, i));

        // ------------------------------------------------------------ layout / drawing

        void Measure()
        {
            pos = new int[text.Length + 1];
            if (text.Length == 0) return;
            const TextFormatFlags f = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            int n = Math.Min(text.Length, 4096);
            string shown = Shown;
            using (var g = CreateGraphics())
                for (int i = 1; i <= n; i++)
                    pos[i] = TextRenderer.MeasureText(g, shown.Substring(0, i), Font, new Size(int.MaxValue, int.MaxValue), f).Width;
            for (int i = n + 1; i <= text.Length; i++) pos[i] = pos[n];
        }

        int TextTop => (Height - Font.Height) / 2;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            const TextFormatFlags f = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoClipping;
            int y = TextTop;
            if (text.Length == 0)
            {
                if (!string.IsNullOrEmpty(Placeholder))
                    TextRenderer.DrawText(g, Placeholder, Font, new Point(0, y), Theme.Outline, BackColor, f);
            }
            else
            {
                TextRenderer.DrawText(g, Shown, Font, new Point(-scroll, y), ForeColor, BackColor, f);
                if (SelectionLength > 0 && Focused)
                {
                    int x1 = pos[SelectionStart] - scroll, x2 = pos[SelectionStart + SelectionLength] - scroll;
                    var r = new Rectangle(x1, y - 1, Math.Max(1, x2 - x1), Font.Height + 2);
                    using (var b = new SolidBrush(Theme.PrimaryContainer)) g.FillRectangle(b, r);
                    var st = g.Save();
                    g.SetClip(r);
                    TextRenderer.DrawText(g, Shown, Font, new Point(-scroll, y), Theme.OnPrimaryContainer, Theme.PrimaryContainer, f);
                    g.Restore(st);
                }
            }
            if (Focused && caretOn && SelectionLength == 0)
                using (var b = new SolidBrush(Theme.Primary))
                    g.FillRectangle(b, pos[caret] - scroll, y - 1, Math.Max(2, DeviceDpi / 48), Font.Height + 2);
        }

        void InvalidateCaret() => Invalidate(new Rectangle(pos[Clamp(caret)] - scroll - 2, 0, 6, Height));

        bool pinScroll;   // ReplaceKeepingPlace: keep the old text where it was while the caret stays in view

        /// <summary>Replaces the text with a longer one that contains the old text at <paramref name="at"/> (e.g. the
        /// address bar showing the full address on focus) without moving the old text on screen: the new part goes
        /// off to the left, so a double-click still lands on the word that was under the pointer.</summary>
        public void ReplaceKeepingPlace(string full, int at)
        {
            Text = full;
            scroll = pos[Math.Max(0, Math.Min(at, text.Length))];
            pinScroll = true;
            Invalidate();
            // only for a double-click's time: a single click (everything selected) then shows the whole address
            var tm = new Timer { Interval = SystemInformation.DoubleClickTime + 50 };
            tm.Tick += (s, e) =>
            {
                tm.Stop(); tm.Dispose();
                if (!pinScroll || IsDisposed || SelectionLength != text.Length) return;
                pinScroll = false; scroll = 0; ScrollToCaret(); Invalidate();
            };
            tm.Start();
        }

        void ScrollToCaret()
        {
            int x = pos[caret], w = Math.Max(10, Width - 4);
            if (pinScroll && x - scroll >= 0 && x - scroll <= w) { ImePosition(); return; }
            pinScroll = false;
            if (x - scroll > w) scroll = x - w;
            if (x - scroll < 0) scroll = Math.Max(0, x - w / 3);
            int max = Math.Max(0, pos[text.Length] - w);
            if (scroll > max) scroll = max;
            ImePosition();
        }

        void Changed(bool fromUser)
        {
            if (fromUser) pinScroll = false;
            Measure();
            ScrollToCaret();
            RestartBlink();
            Invalidate();
            base.OnTextChanged(EventArgs.Empty);
            AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
        }

        void RestartBlink()
        {
            caretOn = true;
            if (Focused) { blink.Stop(); blink.Start(); }
        }

        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Measure(); Invalidate(); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); ScrollToCaret(); }

        // ------------------------------------------------------------ editing

        void Edit(string insert, int start, int length, int caretAfter, bool coalesce = false)
        {
            if (!(coalesce && (DateTime.UtcNow - lastEdit).TotalMilliseconds < 1000 && undo.Count > 0))
                undo.Add(Tuple.Create(text, caret, anchor));
            if (undo.Count > 100) undo.RemoveAt(0);
            redo.Clear();
            lastEdit = coalesce ? DateTime.UtcNow : DateTime.MinValue;
            text = text.Substring(0, start) + insert + text.Substring(start + length);
            caret = anchor = Clamp(caretAfter);
            Changed(true);
            UserEdited?.Invoke(insert.Length > 0 && start + insert.Length == text.Length);
        }

        void Insert(string s)
        {
            s = s.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
            Edit(s, SelectionStart, SelectionLength, SelectionStart + s.Length, s.Length == 1);
        }

        void DeleteSelection() { if (SelectionLength > 0) Edit("", SelectionStart, SelectionLength, SelectionStart); }

        void Undo()
        {
            if (undo.Count == 0) return;
            redo.Add(Tuple.Create(text, caret, anchor));
            var u = undo[undo.Count - 1]; undo.RemoveAt(undo.Count - 1);
            text = u.Item1; caret = u.Item2; anchor = u.Item3; lastEdit = DateTime.MinValue;
            Changed(true);
        }

        void Redo()
        {
            if (redo.Count == 0) return;
            undo.Add(Tuple.Create(text, caret, anchor));
            var r = redo[redo.Count - 1]; redo.RemoveAt(redo.Count - 1);
            text = r.Item1; caret = r.Item2; anchor = r.Item3;
            Changed(true);
        }

        void Copy() { if (SelectionLength > 0 && !Password) try { Clipboard.SetText(SelectedText); } catch { } }
        void Cut() { Copy(); DeleteSelection(); }
        void Paste() { try { if (Clipboard.ContainsText()) Insert(Clipboard.GetText()); } catch { } }

        static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

        int WordLeft(int i)
        {
            while (i > 0 && !IsWord(text[i - 1])) i--;
            while (i > 0 && IsWord(text[i - 1])) i--;
            return i;
        }

        int WordRight(int i)
        {
            while (i < text.Length && !IsWord(text[i])) i++;
            while (i < text.Length && IsWord(text[i])) i++;
            return i;
        }

        void MoveTo(int i, bool extend)
        {
            caret = Clamp(i);
            if (!extend) anchor = caret;
            ScrollToCaret(); RestartBlink(); Invalidate();
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down: case Keys.Home: case Keys.End:
                case Keys.Enter: case Keys.Escape: return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled || e.SuppressKeyPress) return;
            bool shift = e.Shift, ctrl = e.Control;
            bool handled = true;
            switch (e.KeyCode)
            {
                case Keys.Left:
                    if (!shift && SelectionLength > 0) MoveTo(SelectionStart, false);
                    else MoveTo(ctrl ? WordLeft(caret) : caret - 1, shift);
                    break;
                case Keys.Right:
                    if (!shift && SelectionLength > 0) MoveTo(SelectionStart + SelectionLength, false);
                    else MoveTo(ctrl ? WordRight(caret) : caret + 1, shift);
                    break;
                case Keys.Home: case Keys.Up: MoveTo(0, shift); break;
                case Keys.End: case Keys.Down: MoveTo(text.Length, shift); break;
                case Keys.Back:
                    if (SelectionLength > 0) DeleteSelection();
                    else if (caret > 0) { int from = ctrl ? WordLeft(caret) : caret - 1; Edit("", from, caret - from, from); }
                    break;
                case Keys.Delete:
                    if (shift && SelectionLength > 0) Cut();
                    else if (SelectionLength > 0) DeleteSelection();
                    else if (caret < text.Length) { int to = ctrl ? WordRight(caret) : caret + 1; Edit("", caret, to - caret, caret); }
                    break;
                case Keys.Insert:
                    if (ctrl) Copy(); else if (shift) Paste(); else handled = false;
                    break;
                default:
                    handled = false;
                    if (ctrl && !e.Alt)
                    {
                        handled = true;
                        switch (e.KeyCode)
                        {
                            case Keys.A: SelectAll(); break;
                            case Keys.C: Copy(); break;
                            case Keys.X: Cut(); break;
                            case Keys.V: Paste(); break;
                            case Keys.Z: if (shift) Redo(); else Undo(); break;
                            case Keys.Y: Redo(); break;
                            default: handled = false; break;
                        }
                    }
                    break;
            }
            if (handled) { e.Handled = true; e.SuppressKeyPress = true; }
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);
            if (e.Handled) return;
            if (e.KeyChar < ' ' || e.KeyChar == (char)127) return;          // control characters (Ctrl+letter, Esc, Enter ...)
            var mods = ModifierKeys & (Keys.Control | Keys.Alt);
            if (mods == Keys.Control) return;                                // Ctrl shortcuts; AltGr (Ctrl+Alt) still types
            Insert(e.KeyChar.ToString());
            e.Handled = true;
        }

        // ------------------------------------------------------------ mouse

        int Hit(int x)
        {
            x += scroll;
            for (int i = 0; i < text.Length; i++)
                if (x < (pos[i] + pos[i + 1]) / 2) return i;
            return text.Length;
        }

        /// <summary>Set by the owner (the address bar): text dragged out of the box (the whole text when the box was not
        /// being edited, else the selection); the owner starts the drag. Null = no dragging out.</summary>
        public Action<string, bool> DragOut;   // text, whole (not focused)
        Point? dragFrom; bool dragWhole;

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                var now = DateTime.UtcNow;
                clicks = (now - lastClick).TotalMilliseconds < SystemInformation.DoubleClickTime &&
                         Math.Abs(e.X - lastClickPt.X) < 4 && Math.Abs(e.Y - lastClickPt.Y) < 4 ? clicks + 1 : 1;
                lastClick = now; lastClickPt = e.Location;
            }
            // a single press may start dragging the address out; a double / triple click selects a word / everything
            if (e.Button == MouseButtons.Left && clicks == 1 && DragOut != null && text.Length > 0)
            {
                // not being edited: a press may start a drag of the whole address; a click focuses on release
                if (!Focused) { dragFrom = e.Location; dragWhole = true; Capture = true; return; }
                // inside the selection: may drag the selected text; a click places the caret on release
                int hi = Hit(e.X);
                if (SelectionLength > 0 && hi >= SelectionStart && hi < SelectionStart + SelectionLength)
                { dragFrom = e.Location; dragWhole = false; Capture = true; return; }
            }
            bool wasFocused = Focused;
            if (!wasFocused) { selectAllOnClick = true; Focus(); }
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            int i = Hit(e.X);
            if (clicks == 2)
            {
                int a = i, b = i;
                while (a > 0 && IsWord(text[a - 1])) a--;
                while (b < text.Length && IsWord(text[b])) b++;
                anchor = a; caret = b; Invalidate(); return;
            }
            if (clicks >= 3) { SelectAll(); return; }
            MoveTo(i, (ModifierKeys & Keys.Shift) != 0);
            mouseSelecting = true;
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragFrom.HasValue)
            {
                var ds = SystemInformation.DragSize;
                if ((e.Button & MouseButtons.Left) != 0 && (Math.Abs(e.X - dragFrom.Value.X) > ds.Width || Math.Abs(e.Y - dragFrom.Value.Y) > ds.Height))
                {
                    dragFrom = null; Capture = false;
                    DragOut?.Invoke(dragWhole ? text : SelectedText, dragWhole);
                }
                return;
            }
            base.OnMouseMove(e);
            if (!mouseSelecting) return;
            if (e.X < 0) scroll = Math.Max(0, scroll - 8);
            else if (e.X > Width) scroll = Math.Min(Math.Max(0, pos[text.Length] - Width + 4), scroll + 8);
            caret = Hit(Math.Max(0, Math.Min(Width, e.X)));
            selectAllOnClick = false;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (dragFrom.HasValue && e.Button == MouseButtons.Left)
            {
                // no drag: the click it was
                dragFrom = null; Capture = false;
                if (dragWhole) { selectAllOnClick = true; Focus(); if (SelectionLength == 0) SelectAll(); selectAllOnClick = false; }
                else MoveTo(Hit(e.X), false);
                base.OnMouseUp(e);
                return;
            }
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left)
            {
                mouseSelecting = false;
                Capture = false;
                // first click into the field selects the whole address (like Chrome)
                if (selectAllOnClick && SelectionLength == 0) SelectAll();
                selectAllOnClick = false;
            }
            else if (e.Button == MouseButtons.Right) ShowMenu(e.Location);
        }

        internal void ShowMenu(Point p)
        {
            if (!Focused) Focus();
            var m = new MaterialMenu();
            bool sel = SelectionLength > 0, clip = false;
            try { clip = Clipboard.ContainsText(); } catch { }
            void Add(string t, string keys, bool en, Action a) => m.Add(t, null, keys, a).Enabled = en;
            Add("Undo", "Ctrl+Z", undo.Count > 0, Undo);
            m.AddSeparator();
            Add("Cut", "Ctrl+X", sel, Cut);
            Add("Copy", "Ctrl+C", sel, Copy);
            Add("Paste", "Ctrl+V", clip, Paste);
            if (PasteAndGo != null)
                Add("Paste and go", null, clip, () => { try { PasteAndGo(Clipboard.GetText()); } catch { } });
            Add("Delete", null, sel, DeleteSelection);
            m.AddSeparator();
            Add("Select all", "Ctrl+A", text.Length > 0 && SelectionLength < text.Length, SelectAll);
            // opens at the mouse, like a context menu
            m.ShowAt(this, new Rectangle(p.X, p.Y - 1, 1, 1), false, null);
        }

        // ------------------------------------------------------------ focus

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            RestartBlink();
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            blink.Stop();
            caretOn = false;
            mouseSelecting = false;
            Invalidate();
        }

        /// <summary>Unfocused: show the start of the address (like Chrome), no selection.</summary>
        public void ResetView() { caret = anchor = 0; scroll = 0; Invalidate(); }

        // ------------------------------------------------------------ IME: composition and candidate windows at the caret

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int l, t, r, b; }
        [StructLayout(LayoutKind.Sequential)] struct COMPOSITIONFORM { public int style; public POINT pt; public RECT area; }
        [StructLayout(LayoutKind.Sequential)] struct CANDIDATEFORM { public int index, style; public POINT pt; public RECT area; }
        [DllImport("imm32.dll")] static extern IntPtr ImmGetContext(IntPtr h);
        [DllImport("imm32.dll")] static extern bool ImmReleaseContext(IntPtr h, IntPtr c);
        [DllImport("imm32.dll")] static extern bool ImmSetCompositionWindow(IntPtr c, ref COMPOSITIONFORM f);
        [DllImport("imm32.dll")] static extern bool ImmSetCandidateWindow(IntPtr c, ref CANDIDATEFORM f);

        void ImePosition()
        {
            if (!IsHandleCreated || !Focused) return;
            IntPtr c = ImmGetContext(Handle);
            if (c == IntPtr.Zero) return;
            int x = pos[Clamp(caret)] - scroll, y = TextTop;
            var cf = new COMPOSITIONFORM { style = 2 /* CFS_POINT */, pt = new POINT { x = x, y = y } };
            ImmSetCompositionWindow(c, ref cf);
            var cand = new CANDIDATEFORM { index = 0, style = 0x40 /* CFS_CANDIDATEPOS */, pt = new POINT { x = x, y = y + Font.Height + 2 } };
            ImmSetCandidateWindow(c, ref cand);
            ImmReleaseContext(Handle, c);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x010D /* WM_IME_STARTCOMPOSITION */) ImePosition();
            base.WndProc(ref m);
        }

        // ------------------------------------------------------------ accessibility (screen readers read and set the value)

        protected override AccessibleObject CreateAccessibilityInstance() => new Acc(this);

        sealed class Acc : ControlAccessibleObject
        {
            readonly MaterialTextBox box;
            public Acc(MaterialTextBox b) : base(b) { box = b; }
            public override string Value { get => box.Shown; set => box.Text = value; }
            public override AccessibleRole Role => AccessibleRole.Text;
            public override string Name => box.AccessibleName ?? box.Placeholder;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) blink.Dispose();
            base.Dispose(disposing);
        }
    }
}
