using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace GBrowser
{
    /// <summary>GBrowser's find-in-page bar (instead of the engine's own find box), like Chrome's: a card at the top
    /// right of the page with the search text, "3/12" (active match / matches), previous, next and close.
    /// Enter = next, Shift+Enter = previous, Esc = close.</summary>
    internal sealed class FindBar : Control
    {
        public readonly MaterialTextBox Box = new MaterialTextBox { Placeholder = "Find on page" };
        public event Action<string> Search;      // the text changed
        public event Action<bool> Step;          // true = next match, false = previous
        public event Action Closed;

        int active = -1, count = -1;             // count -1 = nothing searched yet
        int hover = -1;                          // 0 previous, 1 next, 2 close
        Rectangle prevRect, nextRect, closeRect, countRect;
        readonly Font countFont = new Font("Segoe UI", 9f);

        public FindBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Visible = false;
            Font = new Font("Segoe UI", 10f);
            Box.Font = Font;
            Box.AccessibleName = "Find on page";
            Controls.Add(Box);
            Box.UserEdited += _ => Search?.Invoke(Box.Text);
            Box.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { Step?.Invoke(!e.Shift); e.Handled = e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Escape) { Closed?.Invoke(); e.Handled = e.SuppressKeyPress = true; }
            };
            AccessibleRole = AccessibleRole.ToolBar;
            AccessibleName = "Find bar";
        }

        int S(float v) => (int)Math.Round(v * DeviceDpi / 96f);

        public int Active => active;
        public int Count => count;

        /// <summary>Engine result: active match (1-based, 0 / -1 none) and number of matches (-1 = not searched).</summary>
        public void SetResult(int activeIndex, int matches)
        {
            if (activeIndex == active && matches == count) return;
            active = activeIndex; count = matches;
            Invalidate();
        }

        public string ResultText => count < 0 || Box.TextLength == 0 ? "" : (count == 0 ? 0 : Math.Max(1, Math.Min(count, active))) + "/" + count;   // the engine counts matches from 1

        /// <summary>Top right of the page area, like Chrome.</summary>
        public void Place()
        {
            if (Parent == null) return;
            Size = new Size(Math.Min(S(380), Math.Max(S(220), Parent.ClientSize.Width - S(32))), S(52));
            Location = new Point(Parent.ClientSize.Width - Width - S(24), S(8));
            using (var p = Theme.Round(new Rectangle(0, 0, Width, Height), S(14))) Region = new Region(p);
            Layout2();
        }

        void Layout2()
        {
            int bs = S(32), y = (Height - bs) / 2, right = Width - S(8);
            closeRect = new Rectangle(right - bs, y, bs, bs); right -= bs + S(2);
            nextRect = new Rectangle(right - bs, y, bs, bs); right -= bs + S(2);
            prevRect = new Rectangle(right - bs, y, bs, bs); right -= bs + S(12);   // divider space
            countRect = new Rectangle(right - S(56), 0, S(56), Height); right -= S(56) + S(6);
            int bh = Box.PreferredHeight;
            Box.Bounds = new Rectangle(S(18), (Height - bh) / 2, Math.Max(S(40), right - S(18)), bh);
            Box.BackColor = Theme.SurfaceContainerHigh;
            Box.ForeColor = Theme.OnSurface;
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Layout2(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.SurfaceContainerHigh);
            using (var p = Theme.Round(new Rectangle(0, 0, Width - 1, Height - 1), S(14)))
            using (var pen = new Pen(Theme.OutlineVariant)) g.DrawPath(pen, p);
            string rt = ResultText;
            if (rt.Length > 0)
                TextRenderer.DrawText(g, rt, countFont, countRect, Theme.OnSurfaceVariant,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            using (var pen = new Pen(Theme.OutlineVariant)) g.DrawLine(pen, prevRect.X - S(7), S(14), prevRect.X - S(7), Height - S(14));
            bool any = count > 0;
            DrawButton(g, prevRect, IconData.ExpandLess, 0, any);
            DrawButton(g, nextRect, IconData.ExpandMore, 1, any);
            DrawButton(g, closeRect, IconData.Close, 2, true);
        }

        void DrawButton(Graphics g, Rectangle r, string icon, int id, bool enabled)
        {
            if (hover == id && enabled) using (var b = new SolidBrush(Theme.Hover)) g.FillEllipse(b, r);
            int s = S(20);
            Icons.Draw(g, icon, r.X + (r.Width - s) / 2, r.Y + (r.Height - s) / 2, s, enabled ? Theme.OnSurfaceVariant : Theme.Outline);
        }

        int Hit(Point p) => prevRect.Contains(p) ? 0 : nextRect.Contains(p) ? 1 : closeRect.Contains(p) ? 2 : -1;

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = Hit(e.Location);
            if (h != hover) { hover = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hover != -1) { hover = -1; Invalidate(); } }

        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (Hit(e.Location) < 0) Box.Focus(); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            switch (Hit(e.Location))
            {
                case 0: if (count > 0) Step?.Invoke(false); Box.Focus(); break;
                case 1: if (count > 0) Step?.Invoke(true); Box.Focus(); break;
                case 2: Closed?.Invoke(); break;
            }
        }
    }
}
