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
//     process's window the way the tiler sets rounded corners (TileSurface.RoundCorners). On a window Windows refuses
//     (an elevated one), the call fails and nothing is drawn: ignored, and traced. Where Windows cannot colour a
//     border at all (before Windows 11, see BorderColourWorks), every ring is a frame instead.
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

    // What is on each window now: its colour, its line (solid, or the trail's dashes or dots), and the frame drawing
    // it, or null where Windows' border does.
    readonly Dictionary<nint, (string Colour, FocusRings.Line Line, RingFrame? Frame)> shown = new();

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
        // One pass a moment after each Show, for a window that resets its own border as it is activated (Windows
        // Terminal does, see Put): Show follows the focus change at once, and the reset lands just after it. A quarter
        // of a second covers the reset without the ring visibly blinking off; the 1 s recheck stays the backstop.
        settle = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        settle.Tick += (_, _) =>
        {
            settle.Stop();
            if (!disposed) Apply();
        };
    }

    readonly DispatcherTimer settle;

    public void Show(IReadOnlyList<FocusRings.Ring> rings)
    {
        if (disposed) return;
        wanted = rings;
        Apply();
        // Restarted on every Show, so a burst of rebuilds settles once, after the last.
        settle.Stop();
        settle.Start();
    }

    void Apply()
    {
        var want = wanted
            .Where(r => NativeMethods.IsWindow(r.Window.Value))
            .GroupBy(r => r.Window.Value)
            .ToDictionary(g => g.Key, g => g.First());

        shown.Keys.Except(want.Keys).ToList().ForEach(Clear);
        want.ToList().ForEach(p => Put(p.Key, p.Value.Colour, p.Value.Line));
        unpinned.ToList().Where(f => pinToAllDesktops(f.Handle).IsSuccess).ToList().ForEach(f => unpinned.Remove(f));

        // Follows the rings: on while any is shown, off when none is.
        recheck.IsEnabled = shown.Count > 0;
    }

    void Put(nint window, string colour, FocusRings.Line line)
    {
        var had = shown.TryGetValue(window, out var was) ? was : ((string Colour, FocusRings.Line Line, RingFrame? Frame)?)null;
        var solid = line == FocusRings.Line.Solid;
        var full = FullFrame(window);

        // THE TRAIL'S RINGS ARE ALWAYS A FRAME. Windows' own border is one solid line with a colour and nothing else: no
        // dash, no dot. So a dashed or dotted ring is drawn by a frame on every window, round its visible rectangle on
        // an ordinary window (VisibleFrame) as round the screen on a maximized one (FullFrame). A trail window is one
        // you are not working in, so it rarely moves while ringed: the second the recheck takes to follow a move,
        // the reason a solid ring stays on Windows' border, hardly ever shows here.
        //
        // AND EVERY RING IS A FRAME WHERE WINDOWS CANNOT COLOUR ITS BORDER (bordersWork, before Windows 11). Reported
        // from a Server 2022 terminal server (build 20348): "it draws dotted and dashed lines, but active window only has
        // a ring if it's fullscreened". Its debug dump showed the focused window marked on the bar and no frame round it,
        // because the solid ring went to DWMWA_BORDER_COLOR, which that build ignores; the trail and a maximized window
        // were frames and showed. There the frame follows a dragged window on the recheck, up to a second late, which is
        // the price of having a ring at all.
        if ((full ?? (solid && bordersWork ? null : VisibleFrame(window))) is { } rect)
        {
            // Windows' border is not the ring here, so it is put back first if it was ours.
            if (had is { Frame: null }) Border(window, null);
            var frame = had?.Frame ?? TakeFrame();
            // One physical pixel for a solid ring: Petre, "can you do 1px?", after 2 DIP and then Windows' own 2 px.
            // Thinner than the border Windows draws on a normal window, which Deskbar can colour but not thin. Two for
            // the trail, Windows' own border at Petre's 225%: a one-pixel dot is a speck, and the dashes must still read
            // on a window edge without outshouting the focused window's solid ring.
            // Rounded only where Windows rounds the window's corners, which came with the same Windows 11 as the border
            // colour: before it, windows are square and a rounded frame would leave their corners bare.
            frame.Place(rect, colour, window, pixels: solid ? 1 : TrailPixels, line, rounded: full is null && bordersWork);
            shown[window] = (colour, line, frame);
            if (had?.Frame is null || had?.Colour != colour || had?.Line != line)
                Trace($"ring 0x{window:X}: frame {colour} {line} {rect.Left},{rect.Top},{rect.Right},{rect.Bottom}");
            return;
        }

        // A trail ring with nothing to go round (minimized, or cloaked in the moment a desktop switch takes): no ring
        // until there is, and no border either, which could only ever be solid.
        if (!solid)
        {
            if (had is not null) Clear(window);
            return;
        }

        if (had?.Frame is { } old) Release(old);
        // EVERY TIME, not only when the colour changes: another program can put its own border colour back over ours.
        // Petre, after "Open" on a task focused its Windows Terminal window: "it doesn't give the ring to the active
        // window". The trace had the border set (no refusal) the moment focus arrived, and nothing after. Windows
        // Terminal resets its frame on every activation (AppHost::_updateTheme in microsoft/terminal: a frame brush that
        // changes with focus, and DWMWA_COLOR_DEFAULT where the theme sets no frame colour), a moment after we set
        // ours. So the recheck sets it again each second, and a settle pass shortly after each Show (see settle) cuts the
        // wait after a focus change. One DwmSetWindowAttribute per ringed window per pass; traced only on a change.
        Border(window, colour, quiet: had is { Frame: null } && had?.Colour == colour);
        shown[window] = (colour, line, null);
    }

    // See Put: thick enough for a dot to be a dot.
    const int TrailPixels = 2;

    // Whether Windows draws a window's border in a colour asked for (DWMWA_BORDER_COLOR). Windows 11 first did, at build
    // 22000; Windows 10 and Server 2022 (build 20348, where it was reported) accept the call or refuse it and draw
    // nothing either way, so a returned code cannot be trusted to say it worked. Hence the build, which is exact:
    // Environment.OSVersion reports the real one on .NET 5 and later (the debug dump's header reads it too).
    internal static bool BorderColourWorks(int build) => build >= 22000;

    static readonly bool bordersWork = BorderColourWorks(Environment.OSVersion.Version.Build);

    void Clear(nint window)
    {
        var (_, _, frame) = shown[window];
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

    // The rectangle a trail ring's frame covers on an ordinary window: what you see of it (DWMWA_EXTENDED_FRAME_BOUNDS),
    // which is where Windows draws its own border. GetWindowRect would be wrong by the invisible resize borders, 7 px or
    // more round most windows (FullScreenCover's measurements), and the dashes would float off the window's edge. Null
    // for a minimized or cloaked window, for FullFrame's reasons.
    static NativeMethods.RECT? VisibleFrame(nint window)
    {
        if (NativeMethods.IsIconic(window)) return null;
        if (NativeMethods.DwmGetWindowAttribute(window, NativeMethods.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return null;
        return NativeMethods.DwmGetWindowAttributeRect(window, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out var visible,
                   System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.RECT>()) == 0
               && visible.Right > visible.Left && visible.Bottom > visible.Top
            ? visible
            : null;
    }

    static bool Same(NativeMethods.RECT a, NativeMethods.RECT b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    // Windows' own border in `colour` (#RRGGBB), or back to Windows' default with null. `quiet` for the re-assertion of
    // a colour already set (see Put), so the trace keeps to changes.
    static void Border(nint window, string? colour, bool quiet = false)
    {
        var value = colour is null ? DWMWA_COLOR_DEFAULT : ColorRef(colour);
        var result = NativeMethods.DwmSetWindowAttribute(window, DWMWA_BORDER_COLOR, ref value, sizeof(int));
        if (!quiet) Trace($"ring 0x{window:X}: border {colour ?? "default"}{(result == 0 ? "" : $" refused 0x{result:X8}")}");
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
        settle.Stop();
        shown.Keys.ToList().ForEach(Clear);
        idle.ToList().ForEach(f => f.Close());
        idle.Clear();
    }
}
