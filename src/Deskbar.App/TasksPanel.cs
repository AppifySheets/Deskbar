using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;
using Deskbar.Core.Geometry;
using Deskbar.Core.Tasks;
using Deskbar.Windows.Monitoring;

namespace Deskbar.App;

// THE TASKS PANEL'S APP BAR, AND ITS ORDER BEHIND THE BAR'S. The tasks window docks as a full-height panel just
// inside the docked bar, registered with the shell as a second app bar so maximized windows, Snap and new windows
// keep clear of it (spec docs/superpowers/specs/2026-11-23-tasks-panel-design.md, "Where it docks", "Keeping the bar
// outside", "Instrumentation"). This class owns that registration and the window's place; it does not build the
// window's content, pin it to every desktop or keep it out of tiling (the App does, Task 6).
//
// THE ORDER IS THE WHOLE POINT. The shell places a NEWER registration inside an older one on the same edge, so the bar
// stays the outer strip only while the panel registers after it, and that has to hold every time the bar's app bar
// registers again: its 1 s guard, Explorer restarting, a dock after undock, minimize or a display change. The bar
// announces each of those through FloatingBar.Strip (BarStrip notices, AppBar.RegisteredOnLastDock), and the panel
// answers each with Undock then DockWith, a fresh registration of its own, newer than the bar's again. The shell's
// plain position notice (ABN_POSCHANGED) only re-asks, without giving the registration up: the bar does the same and
// does not announce it, so neither side's re-ask can set off the other's for ever.
//
// A NOTICE IS ANSWERED ON THE DISPATCHER, never inside it. The bar sends it from the end of its own dock, and a
// panel that registered right there would be docking in the middle of the bar's dock. Queued instead, and a burst
// (a display change lets the strip go and docks it again before the queue runs) is answered once, with the latest:
// `pending` holds the newest notice not yet answered, and only the notice that finds it empty queues an answer.
//
// WHAT "SHOWN" MEANS. IsShown and Shown are the USER's word: open since Show, closed since Hide. The panel can be open
// and still not on screen, while the bar itself is gone (minimized or closed, BarStrip.Gone) or before the bar has
// docked at all; it comes back by itself on the next notice with an edge. A bar that only floats (undocked) keeps the
// panel on screen, docked by the floating rule (see Answer). Shutdown is not the user
// closing it either, so the saved state still says "open" and the next start opens it again.
//
// THE SEAMS, as ScreenStrip has them, so no test registers a real app bar (that reserves a strip of the real screen
// and moves every maximized window on it). Set by an object initializer, which runs AFTER the constructor; that is
// safe even though FloatingBar.Strip replays its last notice inside the constructor's Subscribe, because the answer is
// queued and runs only once the initializer is done.
internal sealed class TasksPanel : IDisposable
{
    // SetWindowPos' "out of the topmost band". FloatingBar and ScreenStrip have their own; they are one value.
    static readonly nint HwndNoTopmost = -2;

    // MonitorFromPoint's "no monitor" flag: an empty answer instead of the nearest screen. See MonitorOf.
    const uint MonitorDefaultToNull = 0;

    readonly Window window;
    readonly Func<BarStrip, PanelPlacement> placementOf;
    readonly IDisposable subscription;

    // The user's open or closed, for the bar's "on" look and for the saved state. Starts with the current value, so a
    // late subscriber learns where things stand; says only changes after that.
    readonly BehaviorSubject<bool> shown = new(false);

    // Reserves a strip `thickness` physical pixels wide on the edge of the screen (physical pixels, Right and Bottom
    // exclusive) and returns the rectangle the shell granted. Registers first when not registered. AppBar.Dock in the
    // app; a recording fake in the tests.
    internal Func<PanelEdge, WindowRect, int, WindowRect> DockWith { get; init; }

    // Gives the strip back (ABM_REMOVE). Safe when nothing is held: AppBar.Remove does nothing then.
    internal Action Undock { get; init; }

    // The screen's scale (1.5 at 150%), from the screen itself with GetDpiForMonitor, for the reason MonitorBounds
    // gives (a window's own DPI can be stale just after a show). NaN when the screen cannot be read right now (it dropped
    // out for a moment, as a waking screen does): the dock then waits rather than convert at a guessed scale, for the
    // reason ScreenStrip.Scale gives. Faked in the tests.
    internal Func<WindowRect, double> ScaleOf { get; init; }

    // The screen's work area (rcWork), physical pixels, for the guard. An EMPTY rectangle when the screen cannot be
    // read right now, which the guard reads as "no verdict": never judge the panel's strip by another screen's work area
    // (the docked bar once moved to the primary on exactly that, FloatingBar.DockGuard.cs). Faked in the tests.
    internal Func<WindowRect, WindowRect> WorkAreaOf { get; init; }

    // The time the guard reads, UTC. Faked in the tests to step over the settle time without waiting for it.
    internal Func<DateTime> Clock { get; init; } = () => DateTime.UtcNow;

    // Whether tracing is on, asked BEFORE a line is built, and where the lines go: ClickTrace in the app (the one switch,
    // %APPDATA%\Deskbar\trace.on, and the log %TEMP%\deskbar-trace.log, CLAUDE.md "Instrumentation"), a list in the tests.
    internal Func<bool> Tracing { get; init; } = () => ClickTrace.On;
    internal Action<string> Trace { get; init; } = ClickTrace.Write;

    // Created on the first real dock and kept, like the bar's: its hook on the window's message loop goes in once.
    AppBar? appBar;

    // The newest notice not yet answered (see the top of this file). Swapped with Interlocked, so the one that finds it
    // empty is the one that queues the answer, whichever thread the bar's stream speaks on.
    BarStrip? pending;

    // Where the panel docks while the bar's strip stands, and the bar's edge for the trace; null before the bar's first
    // notice and while the bar is gone (minimized or closed). `barEdge` is "floating" while the bar floats.
    PanelPlacement? placement;
    string? barEdge;

    // The width the user chose, DIPs, unclamped: it is clamped only when docking, against the screen it docks on, so a
    // width chosen on a wide screen comes back whole after a stay on a narrow one.
    double width = TasksPanelLayout.DefaultWidth;

    // The user's open (see the top of this file).
    bool wanted;

    // Holding, or trying to hold, a registration: set by every dock, cleared only when the strip is given back. The guard
    // and the shell's re-ask act only while it is set.
    bool docking, docked;

    // Explorer restarted (TaskbarCreated): every app bar was forgotten, and the panel waits for the bar to register
    // first rather than register on its own and end up outside it. Neither the shell's re-ask nor the guard docks
    // meanwhile; the bar's next notice ends the wait, and so does RetryEvery passing, in case that notice came before the
    // panel heard TaskbarCreated (the order two windows hear a broadcast in is the shell's, not ours).
    bool awaitingBar;
    DateTime awaitingSince = DateTime.MinValue;

    // The shell said a full-screen window is up (ABN_FULLSCREENAPP): out of the topmost band until it closes, and
    // ReclaimTopmost leaves it there meanwhile.
    bool fullScreenOpen;

    bool shutDown, windowClosed;

    // What the shell granted at the last dock, physical pixels; null while nothing is granted.
    WindowRect? granted;

    // The guard's two clocks, the docked bar's (FloatingBar.SettleFor, RetryEvery), and how often it has repaired.
    DateTime lastDocked = DateTime.MinValue;
    DateTime lastRepair = DateTime.MinValue;
    int repairs;

    // The last geometry line written, so it is written again only when it changes.
    string lastGeometry = "";

    // `placementOf` turns the bar's notice into where the panel docks; the app's rule is TasksPanelPlacement.For, and
    // it is the default.
    public TasksPanel(Window window, IObservable<BarStrip> barStrip, Func<BarStrip, PanelPlacement>? placementOf = null)
    {
        this.window = window;
        this.placementOf = placementOf ?? (s => TasksPanelPlacement.For(s.Edge, s.Screen, s.SeamRight, s.MiddleX));

        // The real seams; a test's object initializer replaces them after this runs.
        DockWith = (edge, screen, thickness) =>
            WindowRectOf(OwnAppBar().Dock(edge == PanelEdge.Left ? AppBar.Edge.Left : AppBar.Edge.Right,
                new AppBar.Rect(screen.Left, screen.Top, screen.Right, screen.Bottom), thickness));
        Undock = () => appBar?.Remove();
        ScaleOf = ScaleOfScreen;
        WorkAreaOf = WorkAreaOfScreen;

        // A window closed by a path that forgot Shutdown still gives its strip back, as ScreenStrip.OnClosed does.
        window.Closed += (_, _) =>
        {
            windowClosed = true;
            Shutdown();
        };

        subscription = barStrip.Subscribe(Queue);
    }

    // The user's open or closed (see the top of this file).
    public bool IsShown => wanted;

    public IObservable<bool> Shown => shown.AsObservable();

    // Opens the panel `widthDips` wide. Docks at once when the bar's strip stands; otherwise remembers it, and the bar's
    // next notice with an edge docks it. Opening one already on screen at another width re-docks it at the new one.
    public void Show(double widthDips)
    {
        if (shutDown) return;
        var resize = docked && widthDips != width;
        width = widthDips;
        SetWanted(true);
        if (resize) _ = Dock(fresh: false, "width");
        else if (!docked) _ = Dock(fresh: true, "shown");
    }

    // Closes the panel: the strip goes back to the shell and the window hides. The user's word, so a notice from the
    // bar does not bring it back; only Show does.
    public void Hide()
    {
        if (shutDown) return;
        SetWanted(false);
        if (docked) GiveBack("hidden");
        else if (window.IsVisible) window.Hide();
    }

    // A new width from the drag grip, at the drag's end (spec "The panel window"). Re-docks once, IN PLACE: the
    // registration stays, so the order with the bar is unchanged, exactly as the bar's own re-dock after a width drag.
    public void SetWidth(double widthDips)
    {
        width = widthDips;
        if (docked && !shutDown) _ = Dock(fresh: false, "width");
    }

    // THE 1 S GUARD, the docked bar's (FloatingBar.KeepDockedStrip) for the panel: the shell can stop holding a strip
    // without saying so. On the same two clocks: never within SettleFor of a dock, at most once every RetryEvery. It
    // judges the OUTCOME, the screen's work area against the strip the shell granted, and when the work area reaches
    // under the panel it registers afresh (Undock, DockWith), which also puts it behind the bar again.
    public void Keep()
    {
        if (!docked || docking || shutDown || placement is not { } at) return;
        var now = Clock();
        if (awaitingBar)
        {
            if (now - awaitingSince < FloatingBar.RetryEvery) return;
            awaitingBar = false;
            Say(() => "tasks panel: no word from the bar since Explorer restarted, the guard takes over");
        }

        if (now - lastDocked < FloatingBar.SettleFor || now - lastRepair < FloatingBar.RetryEvery) return;
        if (Held(at) is not false) return;

        lastRepair = now;
        if (Dock(fresh: true, "guard").IsFailure) return;
        repairs++;
        Say(() => $"tasks panel: taken back by the guard ({repairs} so far)");
    }

    // Topmost is a shared band (CLAUDE.md): the taskbar and the Start menu climb over anything in it when activated, so
    // the App re-asserts it on the 1 s timer beside the bar's. Not while a full-screen window is up. SWP_NOACTIVATE is
    // the load-bearing flag: without it this would take the focus from whatever was just clicked.
    internal void ReclaimTopmost()
    {
        if (!window.IsVisible || fullScreenOpen) return;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0) return;
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    // Gives the strip back for good: the app exiting, or the window closing. Unsubscribes, so a notice still queued does
    // nothing; calls Undock whatever the state (AppBar.Remove does nothing when nothing is held), so nothing stays
    // registered; safe twice and after Hide. Not the user closing the panel (see the top of this file).
    public void Shutdown()
    {
        if (shutDown) return;
        shutDown = true;
        subscription.Dispose();
        var held = docked;
        Undock();
        docked = false;
        granted = null;
        awaitingBar = false;
        if (!windowClosed && window.IsVisible) window.Hide();
        if (held) Say(() => "tasks panel: strip given back (shutdown)");
        shown.OnCompleted();
    }

    public void Dispose() => Shutdown();

    // The shell's plain position notice (ABN_POSCHANGED: something on an edge moved): ask again, WITHOUT giving the
    // registration up, as the bar does (FloatingBar.OnShellMovedStrips). Queued, because it arrives inside the shell's
    // own message. Not while waiting for the bar after an Explorer restart. Internal so a test can send it.
    internal void OnShellMoved() =>
        window.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (docked && !awaitingBar && !shutDown) _ = Dock(fresh: false, "position changed");
        }));

    // Explorer restarted (TaskbarCreated): wait for the bar (see `awaitingBar`). The panel must not answer it by itself:
    // registering now could put it before the bar's own re-registration, and so outside the bar. Internal so a test can
    // send it.
    internal void OnExplorerRestarted()
    {
        if (!docked || shutDown) return;
        awaitingBar = true;
        awaitingSince = Clock();
        Say(() => "tasks panel: Explorer restarted, waiting for the bar to register first");
    }

    // The shell's full-screen notice (AppBar's onFullScreen): out of the topmost band while a full-screen window is up,
    // and back when it closes, as the bar does (FloatingBar.OnFullScreenApp). It steps aside; it does not hide, move or
    // undock, so the work area stays put. Internal so a test can send it.
    internal void StepAside(bool fullScreen)
    {
        fullScreenOpen = fullScreen;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0) return;
        NativeMethods.SetWindowPos(hwnd, fullScreen ? HwndNoTopmost : NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    // A notice from the bar arrives (on any thread): keep it as the newest, and queue an answer unless one is queued
    // already, which will read this newest one when it runs.
    void Queue(BarStrip notice)
    {
        if (Interlocked.Exchange(ref pending, notice) is null)
            window.Dispatcher.BeginInvoke(new Action(Answer));
    }

    // The answer, on the dispatcher, to the newest notice. Three kinds (BarStrip.Gone):
    //   * with an edge: the bar's app bar has just registered (or moved to another edge or screen), so the panel, if
    //     open, registers afresh behind it;
    //   * no edge, Gone: the bar went away (minimized or closed), so the panel gives its strip back and hides, still
    //     open, until the next notice with an edge;
    //   * no edge, not Gone: the bar floats (undocked, or let go by the display readjustment, which docks again right
    //     after). The panel docks by the floating rule, TasksPanelPlacement.For with no edge: the edge of the bar's
    //     screen nearer the bar's middle, as the only app bar there. A fresh registration all the same, so if the bar
    //     docks on that edge afterwards, its own notice puts the panel behind it again. A floating bar dragged elsewhere
    //     sends no notice, so the panel stays on the edge it took when the bar undocked: accepted, Petre always docks
    //     the bar (the coordinator's ruling on the task 5 report).
    void Answer()
    {
        if (Interlocked.Exchange(ref pending, null) is not { } notice || shutDown) return;
        barEdge = notice.Edge ?? (notice.Gone ? null : "floating");
        if (notice is { Edge: null, Gone: true })
        {
            placement = null;
            if (docked) GiveBack("the bar went away");
            return;
        }

        placement = placementOf(notice);
        awaitingBar = false;
        if (wanted) _ = Dock(fresh: true, notice.Edge is null ? "bar floats" : "bar registered");
    }

    // Reserves the panel's strip and puts the window in what the shell granted. `fresh` gives the registration up
    // first, so the dock registers anew and is newer than the bar's (every path that follows a registration of the
    // bar's, and the guard); otherwise the registration stays and the shell is only asked again (a width change, the
    // shell's position notice). Nothing to do before the bar's first notice, or while the bar is gone.
    Result Dock(bool fresh, string reason)
    {
        if (docking || shutDown || placement is not { } at) return Result.Success();
        docking = true;
        try
        {
            // The guard gives the shell SettleFor from here before it judges the work area.
            lastDocked = Clock();
            docked = true;
            if (fresh) Undock();

            var scale = ScaleOf(at.Screen);
            if (!(scale > 0) || double.IsInfinity(scale))
                return Refuse($"the screen {Format(at.Screen)} cannot be read right now");

            // The width clamped to the panel's minimum and half of THIS screen, in this screen's DIPs, then its pixels.
            var thickness = (int)Math.Round(TasksPanelLayout.Clamp(width, at.Screen.Width / scale) * scale);
            var got = DockWith(at.Edge, at.Screen, thickness);
            if (got.Width <= 0 || got.Height <= 0)
                return Refuse($"the shell granted no room on {Format(at.Screen)}");

            granted = got;
            PlaceAt(got, at.Screen, scale);
            if (!window.IsVisible)
            {
                window.Topmost = true;
                window.Show();
            }

            Say(() => $"tasks panel: docked ({reason}), {at.Edge} edge, {thickness} px{(fresh ? ", registered afresh" : "")}");
            TraceGeometry();
            return Result.Success();
        }
        finally
        {
            docking = false;
        }
    }

    // A dock that did not happen. The window hides, because a panel shown with no strip reserved sits topmost over the
    // windows the shell has let back under it (as ScreenStrip.KeepStrip hides after a refusal). Left `docked`, so the
    // guard tries again on its own clock (Held is false with nothing granted).
    Result Refuse(string why)
    {
        granted = null;
        if (window.IsVisible) window.Hide();
        Say(() => $"tasks panel: dock refused: {why}");
        TraceGeometry();
        return Result.Failure(why);
    }

    // Gives the strip back (ABM_REMOVE), then hides the window: in that order, so a window is never left gone with its
    // strip still reserved. The window stays alive and docks again on the next Show or notice.
    void GiveBack(string why)
    {
        Undock();
        docked = false;
        granted = null;
        awaitingBar = false;
        fullScreenOpen = false;
        if (window.IsVisible) window.Hide();
        Say(() => $"tasks panel: strip given back ({why})");
        TraceGeometry();
    }

    void SetWanted(bool value)
    {
        if (wanted == value) return;
        wanted = value;
        shown.OnNext(value);
    }

    // Whether the shell holds the strip: false with nothing granted, or when the work area reaches under the granted
    // strip (StripHeld's rule, for the panel's edge); null when the screen cannot be read, for no verdict.
    bool? Held(PanelPlacement at)
    {
        if (granted is not { } g) return false;
        var work = WorkAreaOf(at.Screen);
        if (work.Width <= 0 || work.Height <= 0) return null;
        return at.Edge == PanelEdge.Left ? work.Left >= g.Right : work.Right <= g.Left;
    }

    // Moves the window, in physical pixels, exactly onto the granted rectangle, the way ScreenStrip.PlaceAt does and
    // for its reason: first onto the screen's scale (ScaleHop), so Windows' rescale on arrival does not resize it, then
    // the final rectangle. Pixels, never Left/Top/Width/Height, which are DIPs at the WINDOW's scale.
    void PlaceAt(WindowRect rect, WindowRect screen, double scale)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        if (NativeMethods.GetWindowRect(hwnd, out var now)
            && ScaleHop.Target(rect.Left / scale, rect.Top / scale, now.Right - now.Left, now.Bottom - now.Top,
                (int)NativeMethods.GetDpiForWindow(hwnd),
                new MiddleDrag.Screen(screen.Left, screen.Top, screen.Right, screen.Bottom, (int)Math.Round(scale * 96))) is { } hop)
            NativeMethods.SetWindowPos(hwnd, 0, hop.Left, hop.Top, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        NativeMethods.SetWindowPos(hwnd, 0, rect.Left, rect.Top, rect.Width, rect.Height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    // One event line, built only when tracing is on.
    void Say(Func<string> line)
    {
        if (Tracing()) Trace(line());
    }

    // The granted rectangle and the bar's edge, written only when it differs from the last one written, so the log stays
    // readable with tracing on all day. Two app bars in a fixed order is the state that needs this line when it goes
    // wrong (spec "Instrumentation").
    void TraceGeometry()
    {
        if (!Tracing()) return;
        var line = $"tasks panel geometry: granted {(granted is { } g ? Format(g) : "nothing")}"
                   + $"{(placement is { } at ? $" on the {at.Edge} edge of {Format(at.Screen)}" : "")}, bar {barEdge ?? "gone"}";
        if (line == lastGeometry) return;
        lastGeometry = line;
        Trace(line);
    }

    static string Format(WindowRect r) => $"{r.Left},{r.Top} to {r.Right},{r.Bottom} ({r.Width}x{r.Height})";

    // The app bar on the panel's window, made on the first real dock. Its one notice callback carries two different
    // events, told apart by Registered: AppBar.Hook clears Registered BEFORE it reports TaskbarCreated, and reports
    // ABN_POSCHANGED only while registered. So a notice with Registered false is Explorer restarting, which the panel
    // must not answer by itself (OnExplorerRestarted), and one with Registered true is a plain re-ask (OnShellMoved).
    AppBar OwnAppBar() =>
        appBar ??= new AppBar(new WindowInteropHelper(window).EnsureHandle(),
            onPositionChanged: () =>
            {
                if (appBar?.Registered == true) OnShellMoved();
                else OnExplorerRestarted();
            },
            onFullScreen: StepAside);

    static WindowRect WindowRectOf(AppBar.Rect r) => new(r.L, r.T, r.R, r.B);

    // The screen with exactly this rectangle and its work area, or null when Windows has none right now. Found from the
    // screen's centre with "no monitor" as the fallback, then the rectangle compared: the nearest-screen answer is the
    // one that once moved the docked bar to the primary (ScreenStrip.OwnMonitor has the same rule and the story).
    static (nint Handle, NativeMethods.RECT Work)? MonitorOf(WindowRect screen)
    {
        var monitor = NativeMethods.MonitorFromPoint(
            new NativeMethods.POINT { X = screen.Left + screen.Width / 2, Y = screen.Top + screen.Height / 2 }, MonitorDefaultToNull);
        if (monitor == 0) return null;
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        return NativeMethods.GetMonitorInfo(monitor, ref info)
               && (info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom) == (screen.Left, screen.Top, screen.Right, screen.Bottom)
            ? (monitor, info.rcWork)
            : null;
    }

    static double ScaleOfScreen(WindowRect screen) =>
        MonitorOf(screen) is { } own && NativeMethods.GetDpiForMonitor(own.Handle, NativeMethods.MDT_EFFECTIVE_DPI, out var dpi, out _) == 0 && dpi > 0
            ? dpi / 96.0
            : double.NaN;

    static WindowRect WorkAreaOfScreen(WindowRect screen) =>
        MonitorOf(screen) is { } own
            ? new WindowRect(own.Work.Left, own.Work.Top, own.Work.Right, own.Work.Bottom)
            : new WindowRect(0, 0, 0, 0);
}
