using System.Windows.Threading;
using CSharpFunctionalExtensions;
using Deskbar.Core.Overview;
using Deskbar.Windows.Monitoring;

namespace Deskbar.App;

// DRAWS THE FOCUS RINGS on the screen: the outlines FocusRings (Core) decides on, put on the real windows. Petre: "can
// you give each active window a ring, same in color as in the bar?", "one that has focus, yes; and another on another
// screen, with a dimmer white", "go with proposed", and "let's also have that outline for fullscreen windows if
// possible".
//
// Two ways to draw one, chosen per window, and checked again every second while any ring is shown:
//
//   * Windows' own border (DWMWA_BORDER_COLOR, Windows 11). The 1 px line Windows already draws round every window,
//     in our colour. Windows moves it with the window, so it never lags a drag, and it costs one call. Set on another
//     process's window the way the tiler sets rounded corners (TileSurface.RoundCorners). On Windows 10, or on a
//     window Windows refuses (an elevated one), the call fails and nothing is drawn: ignored, and traced.
//   * A RingFrame, for a maximized or full-screen window: Windows draws no border on those (RingFrame's header). The
//     once-a-second check is what follows such a window when it is restored, maximized or moved, and keeps the frame
//     directly above it in z-order.
//
// Every border this sets is put back to Windows' default (DWMWA_COLOR_DEFAULT) when the ring moves on, when the
// setting is turned off and when the app exits (Dispose). A crash leaves the last outline on its window until that
// window closes; nothing else is changed on it.
//
// The bar calls Show with the rings of each rebuild (FloatingBar.FocusRing.cs). Owned by App, which hands it the two
// steps every window of the app's own needs: WindowMonitor.Ignore, so a frame is never a row on the bar, and the pin to
// every virtual desktop, so a frame made on one desktop can outline a window on the next.
sealed class WindowRings : IDisposable
{
    readonly Action<nint> ignore;
    readonly Func<nint, Result> pinToAllDesktops;
    readonly DispatcherTimer recheck;

    // The rings asked for by the last Show, re-applied by the timer.
    IReadOnlyList<FocusRings.Ring> wanted = [];

    // What is on each window now: its colour, and the frame drawing it, or null where Windows' border does.
    readonly Dictionary<nint, (string Colour, RingFrame? Frame)> shown = new();

    // Frames no window is using, kept for the next maximized window rather than made again: each new one costs an
    // Ignore and a COM call to pin it.
    readonly Stack<RingFrame> idle = new();

    // Frames whose pin has not worked yet, tried again on every Apply (a window never shown is on no desktop, and the
    // pin fails with "Element not found": App.PinFloatingBar has the story).
    readonly HashSet<RingFrame> unpinned = new();

    bool disposed;

    public WindowRings(Action<nint> ignore, Func<nint, Result> pinToAllDesktops)
    {
        this.ignore = ignore;
        this.pinToAllDesktops = pinToAllDesktops;
        // Once a second, like the bar's own topmost check: often enough to follow a window going full screen, and
        // nothing at all while no ring is shown (the timer runs only then).
        recheck = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        recheck.Tick += (_, _) => Apply();
    }

    public void Show(IReadOnlyList<FocusRings.Ring> rings)
    {
        if (disposed) return;
        wanted = rings;
        Apply();
    }

    void Apply()
    {
        var want = wanted
            .Where(r => NativeMethods.IsWindow(r.Window.Value))
            .GroupBy(r => r.Window.Value)
            .ToDictionary(g => g.Key, g => g.First().Colour);

        shown.Keys.Except(want.Keys).ToList().ForEach(Clear);
        want.ToList().ForEach(p => Put(p.Key, p.Value));
        unpinned.ToList().Where(f => pinToAllDesktops(f.Handle).IsSuccess).ToList().ForEach(f => unpinned.Remove(f));

        // Follows the rings: on while any is shown, off when none is.
        recheck.IsEnabled = shown.Count > 0;
    }

    void Put(nint window, string colour)
    {
        var had = shown.TryGetValue(window, out var was) ? was : ((string Colour, RingFrame? Frame)?)null;
        if (FullFrame(window) is { } rect)
        {
            // Windows' border shows nothing here, so it is put back first if it was ours.
            if (had is { Frame: null }) Border(window, null);
            var frame = had?.Frame ?? TakeFrame();
            frame.Place(rect, colour, window);
            shown[window] = (colour, frame);
            if (had?.Frame is null || had?.Colour != colour)
                Trace($"ring 0x{window:X}: frame {colour} {rect.Left},{rect.Top},{rect.Right},{rect.Bottom}");
            return;
        }

        if (had?.Frame is { } old) Release(old);
        if (had is not { Frame: null } || had?.Colour != colour) Border(window, colour);
        shown[window] = (colour, null);
    }

    void Clear(nint window)
    {
        var (_, frame) = shown[window];
        if (frame is not null) Release(frame);
        else if (NativeMethods.IsWindow(window)) Border(window, null);
        shown.Remove(window);
        Trace($"ring 0x{window:X}: cleared");
    }

    RingFrame TakeFrame()
    {
        if (idle.TryPop(out var reused))
        {
            reused.Show();
            return reused;
        }
        var frame = new RingFrame();
        ignore(frame.Handle);
        frame.Show();
        unpinned.Add(frame);
        return frame;
    }

    void Release(RingFrame frame)
    {
        frame.Hide();
        idle.Push(frame);
    }

    // The rectangle a frame should cover, or null where Windows' own border shows (a window that is neither maximized
    // nor full screen). Physical pixels: the app is per-monitor DPI aware, so these are what SetWindowPos speaks.
    //
    //   * Maximized: the VISIBLE rectangle (DWMWA_EXTENDED_FRAME_BOUNDS), cut to its screen's WORK AREA. A maximized
    //     window's GetWindowRect hangs 7 to 11 px over every edge (FullScreenCover's measurements), and a line drawn
    //     there would be off the screen or on the next one. The work area rather than the whole screen because a window
    //     maximized before a strip or a docked bar took its band keeps its old size, under that band: Petre's Edge
    //     window was 0,38,3489,2160 against a work area of 0,54,3472,2160, so the frame's top ran under the strip, its
    //     right edge under the docked bar, and next to nothing of it showed ("not all windows get right border").
    //   * Maximized and covering the screen EXACTLY: a browser in F11 keeps IsZoomed (FullScreenCover's measurements).
    //     That is full screen, and the strips step behind it, so it takes the whole screen like any full-screen window.
    //   * Full screen: a window that is not maximized and covers its whole screen, FullScreenCover's test (a video
    //     player, a game). The screen's rectangle.
    //   * Minimized: nothing to outline; a minimized window's rectangle is nonsense (CLAUDE.md, Traps).
    //   * Cloaked: on another desktop now. The frame is pinned to every desktop, so in the second between a desktop
    //     switch and the bar's rebuild it would otherwise outline nothing on the desktop just arrived on.
    //   * Maximized on a screen that is gone: measured on the first live run, with the second screen disconnected, an
    //     Edge window stayed maximized at -3851,311,-357,2457, wholly off the one screen left, and the frame came out
    //     with its right edge left of its left one. A window with nothing visible on its screen gets no frame.
    static NativeMethods.RECT? FullFrame(nint window)
    {
        if (NativeMethods.IsIconic(window)) return null;
        if (NativeMethods.DwmGetWindowAttribute(window, NativeMethods.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return null;
        var monitor = NativeMethods.MonitorFromWindow(window, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (monitor == 0 || !NativeMethods.GetMonitorInfo(monitor, ref info)) return null;
        var screen = info.rcMonitor;
        var work = info.rcWork;

        if (NativeMethods.IsZoomed(window))
        {
            var known = NativeMethods.DwmGetWindowAttributeRect(window, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out var visible,
                System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.RECT>()) == 0;
            if (known && Same(visible, screen)) return screen;
            var cut = known
                ? new NativeMethods.RECT
                {
                    Left = Math.Max(visible.Left, work.Left), Top = Math.Max(visible.Top, work.Top),
                    Right = Math.Min(visible.Right, work.Right), Bottom = Math.Min(visible.Bottom, work.Bottom),
                }
                : work;
            return cut.Right > cut.Left && cut.Bottom > cut.Top ? cut : null;
        }

        return NativeMethods.GetWindowRect(window, out var rect)
               && rect.Left <= screen.Left && rect.Top <= screen.Top && rect.Right >= screen.Right && rect.Bottom >= screen.Bottom
            ? screen
            : null;
    }

    static bool Same(NativeMethods.RECT a, NativeMethods.RECT b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    // Windows' own border in `colour` (#RRGGBB), or back to Windows' default with null.
    static void Border(nint window, string? colour)
    {
        var value = colour is null ? DWMWA_COLOR_DEFAULT : ColorRef(colour);
        var result = NativeMethods.DwmSetWindowAttribute(window, DWMWA_BORDER_COLOR, ref value, sizeof(int));
        Trace($"ring 0x{window:X}: border {colour ?? "default"}{(result == 0 ? "" : $" refused 0x{result:X8}")}");
    }

    const uint DWMWA_BORDER_COLOR = 34;
    const int DWMWA_COLOR_DEFAULT = unchecked((int)0xFFFFFFFF);

    // #RRGGBB to a COLORREF, which is 0x00BBGGRR.
    internal static int ColorRef(string hex)
    {
        var rgb = Convert.ToInt32(hex.TrimStart('#'), 16);
        return ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);
    }

    // Behind the one trace switch (CLAUDE.md, Instrumentation), and written only on a change: Put and Clear call it
    // only when a window's ring changes, never on the timer's unchanged re-checks.
    static void Trace(string line)
    {
        if (ClickTrace.On) ClickTrace.Write(line);
    }

    // Every border put back, every frame closed.
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        recheck.Stop();
        shown.Keys.ToList().ForEach(Clear);
        idle.ToList().ForEach(f => f.Close());
        idle.Clear();
    }
}
