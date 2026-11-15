using System.Runtime.InteropServices;

namespace VideoTrimmer;

/// <summary>
/// The window has no separate title bar: <see cref="TopBar"/> is the caption. The native frame stays for resizing,
/// snapping and the shadow; the client area is extended over where the caption was, and hit tests over the top bar are
/// answered as the caption, the top resize edge or the maximize button.
/// </summary>
public sealed partial class MainForm
{
    bool maximizePressed;

    void WireCaptionButtons()
    {
        topBar.Minimize.Click += (_, _) => WindowState = FormWindowState.Minimized;
        // Mouse clicks on maximize arrive as frame messages (see WndProc); this is for keyboard and screen readers.
        topBar.Maximize.Click += (_, _) => WindowChrome.ToggleMaximized(this);
        topBar.Close.Click += (_, _) => Close();
        tips.SetToolTip(topBar.Minimize, "Minimize");
        tips.SetToolTip(topBar.Close, "Close");
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WindowChrome.WM_NCCALCSIZE when m.WParam != IntPtr.Zero:
            {
                // Windows takes its borders on every side; then the caption is handed back to the client area. Maximized,
                // the window hangs past the screen's edge by its frame, so the top keeps that much back.
                var top = Marshal.PtrToStructure<WindowChrome.Rect>(m.LParam).Top;
                base.WndProc(ref m);
                var client = Marshal.PtrToStructure<WindowChrome.Rect>(m.LParam);
                client.Top = top + (WindowChrome.Maximized(this) ? WindowChrome.FrameThickness(this) : 0);
                Marshal.StructureToPtr(client, m.LParam, false);
                m.Result = IntPtr.Zero;
                return;
            }
            case WindowChrome.WM_NCHITTEST:
                base.WndProc(ref m);
                if (m.Result == WindowChrome.HTCLIENT) m.Result = CaptionHit(PointToClient(WindowChrome.PointFrom(m.LParam)));
                return;
            case WindowChrome.WM_NCMOUSEMOVE:
                var overMaximize = m.WParam == WindowChrome.HTMAXBUTTON;
                if (overMaximize) WindowChrome.TrackFrameLeave(this);
                else maximizePressed = false;
                topBar.Maximize.ShowPointer(overMaximize, overMaximize && maximizePressed);
                break;
            case WindowChrome.WM_NCMOUSELEAVE:
                maximizePressed = false;
                topBar.Maximize.ShowPointer(false, false);
                break;
            case WindowChrome.WM_NCLBUTTONDOWN or WindowChrome.WM_NCLBUTTONDBLCLK when m.WParam == WindowChrome.HTMAXBUTTON:
                // Handled here, or Windows would draw and track its own old-style button.
                maximizePressed = true;
                topBar.Maximize.ShowPointer(true, true);
                m.Result = IntPtr.Zero;
                return;
            case WindowChrome.WM_NCLBUTTONUP when m.WParam == WindowChrome.HTMAXBUTTON:
                var click = maximizePressed;
                maximizePressed = false;
                topBar.Maximize.ShowPointer(true, false);
                if (click) WindowChrome.ToggleMaximized(this);
                m.Result = IntPtr.Zero;
                return;
        }

        base.WndProc(ref m);
    }

    /// <summary>
    /// What a point in the client area is, for Windows. Only spots no control claims get here: the window's own
    /// background, and the top bar's empty parts and maximize button, which pass hit tests through to the window.
    /// </summary>
    IntPtr CaptionHit(Point point)
    {
        if (!topBar.Bounds.Contains(point)) return WindowChrome.HTCLIENT;
        var edge = WindowChrome.FrameThickness(this);
        if (!WindowChrome.Maximized(this) && point.Y < edge)
            return point.X < edge ? WindowChrome.HTTOPLEFT : point.X >= ClientSize.Width - edge ? WindowChrome.HTTOPRIGHT : WindowChrome.HTTOP;
        var maximize = topBar.Maximize.Bounds;
        maximize.Offset(topBar.Location);
        return maximize.Contains(point) ? WindowChrome.HTMAXBUTTON : WindowChrome.HTCAPTION;
    }
}
