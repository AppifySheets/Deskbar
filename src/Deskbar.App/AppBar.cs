using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Deskbar.App;

// The bar docked to a screen edge the way the taskbar is: registered with the shell as an "application
// desktop toolbar", so Windows takes its strip out of that screen's work area and maximized windows, Snap
// and new windows all stay clear of it. Petre: "can i dock the app floating window like taskbar, to the
// side of the screen, and have windows respect it?", then "instead of the taskbar, always visible".
//
// Everything here was measured before it was written, with a throwaway probe against his machine:
//
//   * Registering, QUERYPOS and SETPOS on a borderless LAYERED window (what AllowsTransparency makes the
//     bar) work; the work area shrank by exactly the docked width and came back on ABM_REMOVE.
//   * All in PHYSICAL pixels, on his 4K left screen at 150%. The shell speaks physical pixels only, so
//     the conversion to WPF's DIPs belongs to the caller, using that monitor's own DPI.
//   * A process killed while docked, without ABM_REMOVE, leaves nothing behind: Explorer gave the strip
//     back within a second. So a crash cannot strand an empty band on his screen, and nothing here has to
//     guard against it.
//   * QUERYPOS stacks a new bar beside one already on that edge (his vertical taskbar was there), which is
//     why Dock asks first and only then sets.
//
// One instance per bar window, created on the first dock and kept: the hook on the window's message loop
// is installed once.
sealed class AppBar
{
    public enum Edge : uint { Left = 0, Right = 2 }

    readonly nint hwnd;
    readonly uint callbackMessage;
    readonly Action onPositionChanged;
    readonly Action<bool> onFullScreen;

    public bool Registered { get; private set; }

    // `onPositionChanged`: the shell says something moved that may affect us (the taskbar, another app bar,
    // a resolution change); the owner should dock again, which re-asks for a position. `onFullScreen`: a
    // full-screen app opened (true) or closed (false), when an app bar is expected to step out of the
    // topmost band so it does not sit over a video or a game.
    public AppBar(nint hwnd, Action onPositionChanged, Action<bool> onFullScreen)
    {
        this.hwnd = hwnd;
        this.onPositionChanged = onPositionChanged;
        this.onFullScreen = onFullScreen;
        callbackMessage = RegisterWindowMessageW("Deskbar.AppBar");
        taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        HwndSource.FromHwnd(hwnd)?.AddHook(Hook);
    }

    // Explorer broadcasts "TaskbarCreated" when it starts again, and a new Explorer knows nothing of the app
    // bars the old one held: each has to register again. Without this the bar went on believing it was
    // registered while the strip was gone and maximized windows ran under it. A dump from an RDP session
    // showed exactly that state (registered, work area the full width); a restarted Explorer is one way to it.
    readonly uint taskbarCreated;

    // Reserves a strip `widthPx` wide on `edge` of the monitor `screen` (physical pixels), registering
    // first if needed, and returns the rectangle the shell granted: the full height of the screen, beside
    // anything already docked on that edge. The caller moves the window there.
    public Rect Dock(Edge edge, Rect screen, int widthPx)
    {
        var data = Data();
        if (!Registered)
        {
            data.uCallbackMessage = callbackMessage;
            Registered = SHAppBarMessage(ABM_NEW, ref data) != 0;
        }

        data.uEdge = (uint)edge;
        data.rc = edge == Edge.Left
            ? new Rect(screen.L, screen.T, screen.L + widthPx, screen.B)
            : new Rect(screen.R - widthPx, screen.T, screen.R, screen.B);

        // Ask, then keep the width: QUERYPOS moves the inner edge if something already sits on this one,
        // and may leave the rectangle narrower or wider than asked, so the far side is re-derived from
        // the edge the shell settled on.
        SHAppBarMessage(ABM_QUERYPOS, ref data);
        if (edge == Edge.Left) data.rc.R = data.rc.L + widthPx;
        else data.rc.L = data.rc.R - widthPx;

        SHAppBarMessage(ABM_SETPOS, ref data);
        return data.rc;
    }

    // Gives the strip back. Safe to call when not registered.
    public void Remove()
    {
        if (!Registered) return;
        var data = Data();
        SHAppBarMessage(ABM_REMOVE, ref data);
        Registered = false;
    }

    // Where the shell says the Windows taskbar is, and whether it auto-hides, for the debug dump. Null when the
    // shell does not answer (no Explorer running, as in some RDP setups). Edge: 0 left, 1 top, 2 right, 3 bottom.
    public static (Rect Rect, uint Edge, bool AutoHide)? Taskbar()
    {
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
        if (SHAppBarMessage(ABM_GETTASKBARPOS, ref data) == 0) return null;
        var state = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
        var autoHide = (SHAppBarMessage(ABM_GETSTATE, ref state) & ABS_AUTOHIDE) != 0;
        return (data.rc, data.uEdge, autoHide);
    }

    nint Hook(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        // Left unhandled: it is a broadcast, and anything else hooked on this window may want it too. The owner's
        // re-dock goes through Dock, which registers again now that Registered is false.
        if (message == taskbarCreated && Registered)
        {
            ClickTrace.Write("app bar: Explorer restarted (TaskbarCreated), registering again");
            Registered = false;
            onPositionChanged();
            return 0;
        }

        if (message != callbackMessage || !Registered) return 0;

        switch ((int)wParam)
        {
            case ABN_POSCHANGED: onPositionChanged(); break;
            case ABN_FULLSCREENAPP: onFullScreen(lParam != 0); break;
        }

        handled = true;
        return 0;
    }

    APPBARDATA Data() => new() { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = hwnd };

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect(int left, int top, int right, int bottom)
    {
        public int L = left, T = top, R = right, B = bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct APPBARDATA
    {
        public int cbSize;
        public nint hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public Rect rc;
        public nint lParam;
    }

    const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3, ABM_GETSTATE = 4, ABM_GETTASKBARPOS = 5;
    const nuint ABS_AUTOHIDE = 1;
    const int ABN_POSCHANGED = 1, ABN_FULLSCREENAPP = 2;

    [DllImport("shell32.dll")]
    static extern nuint SHAppBarMessage(uint message, ref APPBARDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint RegisterWindowMessageW(string name);
}
