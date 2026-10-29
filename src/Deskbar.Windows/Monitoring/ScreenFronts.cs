using Deskbar.Core.Domain;

namespace Deskbar.Windows.Monitoring;

using static NativeMethods;

// THE WINDOW IN FRONT ON EACH OTHER SCREEN. Petre: "in 2monitor setup, can you identify the window that's on top on
// ANOTHER screen, not the one that's active?", then "mark it, too", "with a lesser glow, but similar", and "c" from
// the mockups: a halo and a faint fill, no ring and no side bar (FloatingBar.Marker.cs).
//
// EnumWindows walks the top-level windows in z-order, top first, so on each screen the first window that is actually
// showing there is the one in front. Measured on Petre's machine on 2026-10-29 with a throwaway probe: VS Code
// foreground on the left screen, and the Edge window he could see on the right came first for the right screen.
//
// What counts as showing:
//   * a window the bar lists (`counts`, the manager's known windows). That is what keeps the bar itself, tooltips,
//     menus and helper windows out: none of them is a row, and one of them coming first would hide the real front
//     window behind it. A window the bar does not list cannot be marked anyway.
//   * visible and not minimized: a minimized window keeps its place in the z-order but shows nowhere.
//   * not cloaked at all. Windows on OTHER virtual desktops stay in EnumWindows' list, cloaked by the shell, which is
//     why TopLevelWindows keeps reason 2 for its own purposes; here the question is what is on screen now, and a
//     cloaked window of any reason is not.
//
// A window's screen is the one it overlaps most (MonitorFromWindow, nearest), so a window across both screens counts
// once, on its larger side. The foreground window's screen is skipped: its front window is the active one, which has
// its own marker. One screen, or no foreground window, gives an empty set.
//
// An always-on-top window counts like any other: it really is in front.
public static class ScreenFronts
{
    public static IReadOnlySet<WindowHandle> AwayFromForeground(Func<WindowHandle, bool> counts)
    {
        var foreground = GetForegroundWindow();
        if (foreground == 0) return new HashSet<WindowHandle>();
        var activeScreen = MonitorFromWindow(foreground, MONITOR_DEFAULTTONEAREST);

        var inOrder = new List<nint>();
        EnumWindows((hwnd, _) => { inOrder.Add(hwnd); return true; }, 0);

        return inOrder
            .Where(hwnd => counts(new WindowHandle(hwnd)) && IsWindowVisible(hwnd) && !IsIconic(hwnd) && !Cloaked(hwnd))
            .Select(hwnd => (Window: hwnd, Screen: MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST)))
            .Where(w => w.Screen != activeScreen)
            .GroupBy(w => w.Screen)
            .Select(screen => new WindowHandle(screen.First().Window))
            .ToHashSet();
    }

    static bool Cloaked(nint hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0;
}
