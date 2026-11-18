using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GBrowser
{
    /// <summary>Chrome-style window frame: the standard title bar is removed (WM_NCCALCSIZE keeps the side and bottom
    /// borders, the DWM shadow and Windows 11's rounded corners), the tab row takes its place, and WM_NCHITTEST tells
    /// Windows where the caption, the resize edge and the minimise / maximise / close buttons are. Returning
    /// HTMAXBUTTON for our own maximise button gives Windows 11 Snap Layouts on hover.</summary>
    internal sealed class WindowFrame
    {
        const int WM_NCCALCSIZE = 0x0083, WM_NCHITTEST = 0x0084, WM_NCMOUSEMOVE = 0x00A0, WM_NCLBUTTONDOWN = 0x00A1,
                  WM_NCLBUTTONUP = 0x00A2, WM_NCLBUTTONDBLCLK = 0x00A3, WM_NCMOUSELEAVE = 0x02A2, WM_MOUSEMOVE = 0x0200;
        const int HTCLIENT = 1, HTCAPTION = 2, HTMINBUTTON = 8, HTMAXBUTTON = 9, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14, HTCLOSE = 20;

        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)] struct NCCALCSIZE_PARAMS { public RECT r0, r1, r2; public IntPtr pos; }
        [StructLayout(LayoutKind.Sequential)] struct TRACKMOUSEEVENT { public int cbSize, dwFlags; public IntPtr hwnd; public int dwHoverTime; }
        [DllImport("user32.dll")] static extern int GetSystemMetricsForDpi(int index, uint dpi);
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT e);

        readonly Form form;
        readonly TabStrip strip;
        public bool Enabled = true;   // off in full screen (no border at all there)

        public WindowFrame(Form form, TabStrip strip) { this.form = form; this.strip = strip; }

        int FrameY(IntPtr h)
        {
            uint dpi = GetDpiForWindow(h);
            return GetSystemMetricsForDpi(33 /* SM_CYFRAME */, dpi) + GetSystemMetricsForDpi(92 /* SM_CXPADDEDBORDER */, dpi);
        }

        /// <summary>Returns true when the message was fully handled (the window must not call base.WndProc).</summary>
        public delegate void BaseProc(ref Message m);

        public bool Handle(ref Message m, BaseProc callBase)
        {
            if (!Enabled || form.FormBorderStyle == FormBorderStyle.None) return false;
            switch (m.Msg)
            {
                case WM_NCCALCSIZE when m.WParam != IntPtr.Zero:
                {
                    var p = Marshal.PtrToStructure<NCCALCSIZE_PARAMS>(m.LParam);
                    int top = p.r0.T;
                    callBase(ref m);                                    // default: borders + caption
                    p = Marshal.PtrToStructure<NCCALCSIZE_PARAMS>(m.LParam);
                    // drop the caption; a maximised window hangs over the screen edge by the frame size, keep content inside
                    p.r0.T = top + (form.WindowState == FormWindowState.Maximized ? FrameY(form.Handle) : 0);
                    Marshal.StructureToPtr(p, m.LParam, false);
                    m.Result = IntPtr.Zero;
                    return true;
                }
                case WM_NCHITTEST:
                {
                    callBase(ref m);
                    if ((int)m.Result != HTCLIENT) return true;
                    long lp = m.LParam.ToInt64();
                    var screen = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
                    var pc = form.PointToClient(screen);
                    bool max = form.WindowState == FormWindowState.Maximized;
                    int edge = Math.Max(4, FrameY(form.Handle) / 2);
                    var ps = strip.PointToClient(screen);
                    if (!max && pc.Y < edge)   // top resize edge (the caption took it away), also above the buttons
                    {
                        m.Result = (IntPtr)(pc.X < edge * 2 ? HTTOPLEFT : pc.X > form.ClientSize.Width - edge * 2 ? HTTOPRIGHT : HTTOP);
                        return true;
                    }
                    int cap = strip.CaptionAt(ps);
                    if (cap >= 0) { m.Result = (IntPtr)(cap == 0 ? HTMINBUTTON : cap == 1 ? HTMAXBUTTON : HTCLOSE); return true; }
                    if (strip.Visible && strip.ClientRectangle.Contains(ps) && strip.IsDragArea(ps)) m.Result = (IntPtr)HTCAPTION;
                    return true;
                }
                case WM_NCMOUSEMOVE:
                {
                    int ht = (int)m.WParam;
                    int hover = ht == HTMINBUTTON ? 0 : ht == HTMAXBUTTON ? 1 : ht == HTCLOSE ? 2 : -1;
                    if (hover != strip.CaptionHover) { strip.CaptionHover = hover; strip.Invalidate(); }
                    var t = new TRACKMOUSEEVENT { cbSize = Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = 0x02 | 0x10 /* TME_LEAVE | TME_NONCLIENT */, hwnd = form.Handle };
                    TrackMouseEvent(ref t);
                    return hover >= 0 ? Swallow(ref m) : false;
                }
                case WM_NCMOUSELEAVE:
                case WM_MOUSEMOVE:
                    if (strip.CaptionHover >= 0 || strip.CaptionPressed >= 0) { strip.CaptionHover = strip.CaptionPressed = -1; strip.Invalidate(); }
                    return false;
                case WM_NCLBUTTONDOWN:
                case WM_NCLBUTTONDBLCLK:
                {
                    int ht = (int)m.WParam;
                    if (ht != HTMINBUTTON && ht != HTMAXBUTTON && ht != HTCLOSE) return false;
                    strip.CaptionPressed = ht == HTMINBUTTON ? 0 : ht == HTMAXBUTTON ? 1 : 2;
                    strip.Invalidate();
                    return Swallow(ref m);   // no classic button drawing / default action
                }
                case WM_NCLBUTTONUP:
                {
                    int ht = (int)m.WParam, pressed = strip.CaptionPressed;
                    strip.CaptionPressed = -1;
                    strip.Invalidate();
                    if (ht == HTMINBUTTON && pressed == 0) form.WindowState = FormWindowState.Minimized;
                    else if (ht == HTMAXBUTTON && pressed == 1)
                        form.WindowState = form.WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
                    else if (ht == HTCLOSE && pressed == 2) form.Close();
                    else return false;
                    return Swallow(ref m);
                }
            }
            return false;
        }

        static bool Swallow(ref Message m) { m.Result = IntPtr.Zero; return true; }
    }
}
