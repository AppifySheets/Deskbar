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
    // The values are the shell's ABE_ constants. Top (ABE_TOP, uEdge = 1) is for the screen strips, the window
    // list along the top of every screen. Measured on Petre's two 4K screens at 150%: a top bar asked for
    // [0,0,3840,48] got exactly that from QUERYPOS and SETPOS, the work area became [0,48,3840,2160], and went
    // back on remove.
    public enum Edge : uint { Left = 0, Top = 1, Right = 2 }

    readonly nint hwnd;
    readonly uint callbackMessage;
    readonly Action onPositionChanged;
    readonly Action<bool> onFullScreen;

    public bool Registered { get; private set; }

    // Whether the LAST Dock call registered this window afresh: it sent ABM_NEW and the shell said yes (on the first
    // try or on the remove-and-retry below). False after every Dock that found the window already registered, which
    // is every re-dock the shell's own position notice asks for, and false after a Dock whose ABM_NEW was refused.
    //
    // The tasks panel's order depends on it. The shell places a NEWER registration inside an older one on the same
    // edge, so the bar stays the outer strip only while the panel registers after it. Every time the bar's app bar
    // registers again (the dock guard, Explorer restarting, a dock after undock, minimize or a display change), the
    // bar announces it (FloatingBar.Strip) and the panel removes and re-registers its own behind it. A re-dock that
    // did NOT register must not be announced, or the bar's re-dock and the panel's would answer each other for ever
    // through the position notices both of them send. Registered alone cannot tell the two apart: it is true either
    // way once the dock is done.
    public bool RegisteredOnLastDock { get; private set; }

    // Whether the shell may be holding a strip for this window: set by every Dock, cleared only by Remove. Kept
    // apart from Registered on purpose. Registered is what ABM_NEW last answered, and that answer can be wrong
    // about the shell: ABM_NEW returns FALSE for a window that is ALREADY registered (Microsoft's docs, ABM_NEW),
    // and TaskbarCreated (see Hook) clears Registered on a guess that Explorer forgot us. When the guess was
    // wrong, the strip stayed reserved while Registered said false, and Remove, which used to check Registered,
    // never sent ABM_REMOVE. Petre, from an RDP session: "undocking still keeps the space". Remove keys on this
    // instead, so undocking always gives back whatever Dock may have taken.
    bool claimed;

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

    // Reserves a strip `thicknessPx` thick on `edge` of the monitor `screen` (physical pixels), registering
    // first if needed, and returns the rectangle the shell granted: the full height of the screen for a left or
    // right strip (thickness is its width), the full width for a top strip (thickness is its height), beside
    // anything already docked on that edge. The caller moves the window there.
    public Rect Dock(Edge edge, Rect screen, int thicknessPx)
    {
        var data = Data();
        claimed = true;
        // Set again below only when this call sends ABM_NEW and it is granted (see RegisteredOnLastDock).
        RegisteredOnLastDock = false;
        if (!Registered)
        {
            data.uCallbackMessage = callbackMessage;
            Registered = SHAppBarMessage(ABM_NEW, ref data) != 0;

            // FALSE can mean "already registered": the shell kept us across a TaskbarCreated that was not an
            // Explorer restart. Drop that old registration and make a fresh one, so Registered matches the shell
            // again and Hook goes on answering its notifications. A second FALSE is a real failure, left as is
            // and visible in the dock trace line (registered=False).
            if (!Registered)
            {
                ClickTrace.Write("app bar: ABM_NEW refused (already registered?), removing and registering again");
                SHAppBarMessage(ABM_REMOVE, ref data);
                Registered = SHAppBarMessage(ABM_NEW, ref data) != 0;
            }

            // This call sent ABM_NEW; whether the shell finally granted it is what Registered now says.
            RegisteredOnLastDock = Registered;
        }

        data.uEdge = (uint)edge;
        data.rc = edge switch
        {
            Edge.Left => new Rect(screen.L, screen.T, screen.L + thicknessPx, screen.B),
            Edge.Top => new Rect(screen.L, screen.T, screen.R, screen.T + thicknessPx),
            _ => new Rect(screen.R - thicknessPx, screen.T, screen.R, screen.B),
        };

        // Ask, then keep the thickness: QUERYPOS moves the inner edge if something already sits on this one,
        // and may leave the rectangle thinner or thicker than asked, so the far side is re-derived from
        // the edge the shell settled on.
        //
        // Only that axis. Measured (probe notes, screen strips, section 1): the shell gives a top bar the full
        // width and starts a side bar BELOW it, in either registration order. So for Top, L and R stay as QUERYPOS
        // returned them and only B is re-derived; for Left and Right, T and B stay as QUERYPOS returned them,
        // which is how a side bar ends up under a top strip. Do not "tidy" any of them back to the screen's.
        SHAppBarMessage(ABM_QUERYPOS, ref data);
        switch (edge)
        {
            case Edge.Left: data.rc.R = data.rc.L + thicknessPx; break;
            case Edge.Top: data.rc.B = data.rc.T + thicknessPx; break;
            default: data.rc.L = data.rc.R - thicknessPx; break;
        }

        SHAppBarMessage(ABM_SETPOS, ref data);
        return data.rc;
    }

    // Gives the strip back. Safe to call when nothing was docked. Keyed on `claimed`, not on Registered, for the
    // reason `claimed` gives: a window the shell still holds must get its ABM_REMOVE even when the last ABM_NEW
    // said no. Still skipped when Dock never ran since the last Remove, because DockTo calls Remove on the seam
    // half on every re-dock, and those re-docks come from the shell's own notifications.
    public void Remove()
    {
        if (!claimed) return;
        var data = Data();
        SHAppBarMessage(ABM_REMOVE, ref data);
        Registered = false;
        claimed = false;
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
