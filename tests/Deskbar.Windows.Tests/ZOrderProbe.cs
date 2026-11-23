using System.Runtime.InteropServices;

namespace Deskbar.Windows.Tests;

// Z-order questions the overlay tests ask (LockMarkWindowTests, TileLockMarksTests).
static class ZOrderProbe
{
    // The first VISIBLE window above `target` in z-order: the one that would show over it. Invisible windows are
    // skipped because WPF keeps an invisible helper ("Hidden Window") per thread, which the z-order can put anywhere
    // and which covers nothing. Found when the full suite first ran: the helper sat between a tile and its mark.
    public static nint VisibleAbove(nint target)
    {
        var w = GetWindow(target, GW_HWNDPREV);
        while (w != 0 && !IsWindowVisible(w)) w = GetWindow(w, GW_HWNDPREV);
        return w;
    }

    // Brings `window` to the front of its band without activating it: what Windows does to a tile clicked into.
    public static void ToFront(nint window) =>
        SetWindowPos(window, 0, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    const uint GW_HWNDPREV = 3;
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern nint GetWindow(nint hwnd, uint command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
}

// The z-order tests (LockMarkWindowTests, TileLockMarksTests) run with nothing else in parallel: they ask which window is
// directly above another, and a window some other test class opens at the same moment goes to the front of the band and
// changes the answer. Measured on CI: the window right above a tile brought to the front was another test's, not the
// topmost one the test had opened.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ZOrderCollection
{
    public const string Name = "Z-order, alone";
}
