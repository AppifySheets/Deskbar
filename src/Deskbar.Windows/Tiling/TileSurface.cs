using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using CSharpFunctionalExtensions;
using Deskbar.Core.Abstractions;
using Deskbar.Core.Domain;
using Deskbar.Core.Tiling;
using Deskbar.Windows.Monitoring;
using Microsoft.Win32;

namespace Deskbar.Windows.Tiling;

using static NativeMethods;

// The Windows half of tiling: what the windows are like, where they are, moving them, and hearing them move.
// Every decision about WHEN and WHERE lives in the Tiler (Deskbar.Core.Tiling); this class answers questions
// and does what it is told. See ITileSurface for what each member promises.
//
// Threading: construct it, call Watch and subscribe to ScreensChanged on the UI thread. The LOCATIONCHANGE
// hook is out of context, so Windows delivers its callbacks through the message queue of the thread that
// installed it, and the debounce timers run on the dispatcher captured at construction; both have to be the
// UI thread for Settled to arrive where the Tiler expects it.
//
// All geometry is physical pixels. The app is per-monitor DPI aware, so GetWindowRect, the DWM frame bounds and
// the monitors' work areas all agree without any conversion here.
public sealed class TileSurface : ITileSurface, IDisposable
{
    // The quiet interval before a watched window counts as settled. Long enough to swallow the stream of
    // LOCATIONCHANGE events a drag or an animated maximize produces, short enough that the grid answers a drop
    // before the user has looked away.
    static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(150);

    // How many movability answers to hold before the dead ones are swept out (see Movable).
    const int MovableCacheSweepAt = 256;

    readonly Func<WindowHandle, bool> isOurs;
    readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    readonly Subject<WindowHandle> settled = new();

    // CRITICAL, as in WindowMonitor: the hook delegate lives in a field. A delegate the GC collects while the
    // hook is installed leaves user32 calling freed memory, which takes the process down.
    readonly WinEventProc callback;
    nint hook;

    // The watched windows, a COPY of what Watch was given (the Tiler may reuse or change its own set).
    IReadOnlySet<WindowHandle> watched = new HashSet<WindowHandle>();

    // One restartable timer per watched window that has moved and not yet settled.
    readonly Dictionary<WindowHandle, DispatcherTimer> quiet = [];

    // The answer to "does this window accept SetWindowPos from us", per hwnd, for the window's life. CanTile
    // is asked of every known window on each structural event, and the probe is a SetWindowPos, which sends
    // the window messages and waits for its thread to answer; the answer cannot change (a window does not
    // become elevated or stop being elevated), so it is asked once. The cheap style bits are NOT cached: an
    // app can change those at any time, and reading them is a few user32 calls.
    readonly Dictionary<nint, bool> movable = [];

    // Set by Dispose (final review, item 3). After it, Watch is a no-op, so nothing can put the LOCATIONCHANGE
    // hook back (a Tiler that outlived this surface, a refresh landing during logoff), and Place fails, so
    // nothing is moved during a logoff either.
    bool disposed;

    public TileSurface(Func<WindowHandle, bool> isOurs)
    {
        this.isOurs = isOurs;
        callback = OnLocationChange;
    }

    // Whether a mouse button is down right now, asked before a quiet window is reported as settled. The one
    // seam in this class: a test cannot press a button on the machine running it, so it substitutes this.
    internal Func<bool> ButtonsDown { get; init; } = MouseButtonDown;

    // Whether the LOCATIONCHANGE hook is installed right now. For tests: a hook is the one thing a disposed
    // surface must never put back.
    internal bool IsHooked => hook != 0;

    public IObservable<WindowHandle> Settled => settled.AsObservable();

    // A screen plugged in or out or rearranged (DisplaySettingsChanged), or a work area changed without the
    // display settings changing at all: the taskbar moved, resized or auto-hid, an app bar docked
    // (WM_SETTINGCHANGE with SPI_SETWORKAREA). Both only say "something changed"; the Tiler asks Screens()
    // again for what.
    public IObservable<Unit> ScreensChanged => DisplaySettingsChanged().Merge(WorkAreaChanged());

    // --- Screens ---------------------------------------------------------------------------------------------

    // Numbered by ScreenLayout.MonitorNumbers, the same method the bar's monitor badges come from, so "screen 2"
    // means one screen to the bar and to the grid (controller ruling: one numbering, not a copy of it).
    public IReadOnlyList<TileScreen> Screens() =>
        ScreenLayout.MonitorNumbers().Numbers
            .Select(m => (Number: m.Value, Work: WorkAreaOf(m.Key), Dpi: DpiOf(m.Key)))
            .Where(m => m.Work.HasValue)
            .Select(m => new TileScreen(m.Number, m.Work.Value, m.Dpi))
            .OrderBy(s => s.Number)
            .ToList();

    // MONITOR_DEFAULTTONEAREST, as ScreenLayout does: a window dragged mostly off screen still belongs to the
    // screen nearest it. 0 only if that monitor is not among the numbered ones, which would mean the monitors
    // changed between the two calls; 0 is no display number, so the Tiler treats it as no known screen until
    // ScreensChanged has it ask again.
    public int ScreenOf(WindowHandle window) =>
        ScreenLayout.MonitorNumbers().Numbers.TryGetValue(
            MonitorFromWindow(window.Value, MONITOR_DEFAULTTONEAREST), out var number) ? number : 0;

    static Maybe<WindowRect> WorkAreaOf(nint monitor)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        return GetMonitorInfo(monitor, ref info)
            ? new WindowRect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom)
            : Maybe<WindowRect>.None;
    }

    // The monitor's effective DPI, which is what scales the gap. 96 (100%) if Windows will not say, which only
    // makes the gap the unscaled default rather than failing the screen.
    static int DpiOf(nint monitor) =>
        GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpi, out _) == 0 ? (int)dpi : 96;

    // --- Which windows tile ------------------------------------------------------------------------------------

    // Cheapest questions first, the probe last and cached (see `movable`). Decided by the window's own styles,
    // never by a rule file (spec, "Which windows tile").
    public bool CanTile(WindowHandle window) =>
        IsWindow(window.Value)
        && !isOurs(window)
        // An owned window is a dialog, a palette or a find bar: it belongs with its owner, not in a slot.
        && GetWindow(window.Value, GW_OWNER) == 0
        // A tool window is not on the taskbar or in Alt+Tab either; Windows itself does not count it as an app.
        && ((long)GetWindowLongPtr(window.Value, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) == 0
        // No resizable border, no slot: a fixed-size window would sit in its tile with a hole around it.
        && ((long)GetWindowLongPtr(window.Value, GWL_STYLE) & WS_THICKFRAME) != 0
        && Movable(window);

    // A zero-size, zero-move SetWindowPos changes nothing, but an elevated window refuses it from a process that
    // is not elevated (UIPI), which is exactly the window the grid could never place.
    bool Movable(WindowHandle window)
    {
        if (movable.TryGetValue(window.Value, out var known)) return known;

        // A hung window is not probed and not cached: the probe would freeze our UI thread with it, and "hung"
        // is a state that ends, so caching "no" would keep a window out of the grid for its whole life. CLAUDE.md:
        // never cache a lookup failure that can succeed later.
        if (IsHungAppWindow(window.Value)) return false;

        // Swept rather than evicted by age: an hwnd stays valid for its window's life, and the dead ones are the
        // only entries that can never be asked about again.
        if (movable.Count > MovableCacheSweepAt)
            movable.Keys.Where(h => !IsWindow(h)).ToList().ForEach(h => movable.Remove(h));

        return movable[window.Value] = SetWindowPos(window.Value, 0, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    public bool IsMaximized(WindowHandle window) => IsZoomed(window.Value);
    public bool IsMinimized(WindowHandle window) => IsIconic(window.Value);

    // A window has a quiet timer from its first LOCATIONCHANGE until it settles (OnQuiet) or stops being watched
    // (Forget), and the timer keeps running while a button is down. So "has a timer" is exactly "moved within
    // the last quiet interval, or held by a button": the window the Tiler must not place (ITileSurface).
    public bool IsSettling(WindowHandle window) => quiet.ContainsKey(window);

    // --- Where windows are, and moving them ---------------------------------------------------------------------

    // None for a window that has gone, and for a MINIMIZED one: its rectangles are the iconic ones, off at
    // -32000,-32000 (#107), and there is no visible frame to report. A maximized window answers with the frame
    // it is maximized to, which is where it visibly is.
    public Maybe<WindowRect> VisibleFrameOf(WindowHandle window) =>
        !IsWindow(window.Value) || IsIconic(window.Value)
            ? Maybe<WindowRect>.None
            : FrameOf(window.Value);

    // DWM's extended frame bounds, or the window rectangle if DWM will not answer for this window (then the
    // window has no invisible border DWM knows of, and the two are the same thing as far as placing goes).
    static Maybe<WindowRect> FrameOf(nint hwnd) =>
        DwmGetWindowAttributeRect(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var frame, Marshal.SizeOf<RECT>()) == 0
            ? new WindowRect(frame.Left, frame.Top, frame.Right, frame.Bottom)
            : WindowRectOf(hwnd);

    static Maybe<WindowRect> WindowRectOf(nint hwnd) =>
        GetWindowRect(hwnd, out var r) ? new WindowRect(r.Left, r.Top, r.Right, r.Bottom) : Maybe<WindowRect>.None;

    // SYNCHRONOUS, and that is load-bearing: the Tiler reads VisibleFrameOf the moment this returns and keeps
    // it as the window's accepted frame, which is its guard against moving a window in a loop. So plain
    // SetWindowPos, never SWP_ASYNCWINDOWPOS and never a deferred placement.
    //
    // SWP_NOZORDER | SWP_NOACTIVATE: a re-tile never raises a tile over a maximized window and never takes focus.
    public Result Place(WindowHandle window, WindowRect visibleFrame)
    {
        if (disposed) return Result.Failure("tiling has stopped.");
        if (!IsWindow(window.Value)) return Result.Failure("that window has gone.");
        // The Tiler never asks this of a maximized or minimized window; refused anyway, because SetWindowPos on
        // one writes a rectangle that a restore then throws away.
        if (IsZoomed(window.Value) || IsIconic(window.Value)) return Result.Failure("that window is maximized or minimized.");
        // SetWindowPos waits for the window's thread to answer; a hung one would freeze ours with it.
        if (IsHungAppWindow(window.Value)) return Result.Failure("that window is not responding.");

        // Placed twice at most. The border is measured BEFORE the move, at the DPI of the screen the window is
        // on now; a window moved onto a screen at another scale comes out with a different border (and a
        // per-monitor aware app may resize itself as it crosses), so its frame misses the slot by a few pixels.
        // A second pass, measured where it now is, puts it right. A window that still misses after that is
        // insisting on its own size, which the Tiler sees in its read-back and handles.
        return PlaceOnce(window.Value, visibleFrame)
            .Tap(() =>
            {
                if (FrameOf(window.Value).GetValueOrDefault() != visibleFrame) PlaceOnce(window.Value, visibleFrame);
            });
    }

    static Result PlaceOnce(nint hwnd, WindowRect wanted)
    {
        if (!WindowRectOf(hwnd).TryGetValue(out var outerNow) || !FrameOf(hwnd).TryGetValue(out var visibleNow))
            return Result.Failure("Windows would not report that window's rectangle.");

        var outer = FrameCompensation.Outer(wanted, outerNow, visibleNow);
        // A dead handle or an elevated window: ordinary, and the Tiler carries on with the rest.
        return SetWindowPos(hwnd, 0, outer.Left, outer.Top, outer.Width, outer.Height, SWP_NOZORDER | SWP_NOACTIVATE)
            ? Result.Success()
            : Result.Failure("Windows would not move that window.");
    }

    // Windows 11's rounded corners, asked for once as a window joins a grid. Windows 10 has no such attribute and
    // answers with an error, which is ignored: square corners there are simply what Windows 10 looks like.
    public void RoundCorners(WindowHandle window)
    {
        var preference = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(window.Value, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }

    // --- Hearing windows move ----------------------------------------------------------------------------------

    // One LOCATIONCHANGE hook, present only while the set is not empty. That is how tiling costs nothing while
    // the experiment is off or the desktop being looked at is not tiled: the Tiler passes an empty set and the
    // hook goes away (spec, "Keeping it tidy").
    public void Watch(IReadOnlySet<WindowHandle> windows)
    {
        // Disposed: the hook is gone for good (see `disposed`).
        if (disposed) return;

        // A copy, never the caller's set: the Tiler may reuse or change the one it passed.
        watched = windows.ToHashSet();

        // A window no longer watched has no business settling: its pending timer is dropped.
        quiet.Keys.Where(w => !watched.Contains(w)).ToList().ForEach(Forget);

        if (watched.Count > 0 && hook == 0)
            // Fails only if Windows is out of resources; then nothing settles until the next Watch tries again.
            hook = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, 0, callback, 0, 0,
                WINEVENT_OUTOFCONTEXT);
        else if (watched.Count == 0 && hook != 0)
            Unhook();
    }

    void OnLocationChange(nint hookHandle, uint @event, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // FIRST LINE, and it has to be: this hook fires for every move of every object on the machine (every
        // window, the caret on each keystroke, the cursor), continuously through any drag, and it runs on the
        // UI thread. Anything that is not a watched window's own event leaves here, before any allocation or
        // any further call.
        if (idObject != OBJID_WINDOW || idChild != CHILDID_SELF || !watched.Contains(new WindowHandle(hwnd))) return;

        // No managed exception may escape a native callback: it would unwind through user32's frames and take
        // the process down (WindowMonitor's invariant). Losing one restart is harmless; the next event restarts it.
        try
        {
            Restart(new WindowHandle(hwnd));
        }
        catch (Exception)
        {
        }
    }

    // Each move restarts the window's quiet timer, so it fires only once the window has been still for Quiet.
    void Restart(WindowHandle window)
    {
        if (!quiet.TryGetValue(window, out var timer))
        {
            timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = Quiet };
            timer.Tick += (_, _) => OnQuiet(window, timer);
            quiet[window] = timer;
        }
        timer.Stop();
        timer.Start();
    }

    // Quiet for the interval. Still not settled while a mouse button is down: a slow drag that pauses mid-way
    // is not a drop, and acting on it would yank the window out from under the pointer. The timer runs again
    // instead and the question is asked after another interval.
    void OnQuiet(WindowHandle window, DispatcherTimer timer)
    {
        timer.Stop();
        if (ButtonsDown())
        {
            timer.Start();
            return;
        }
        Forget(window);
        // A window that closed while it was settling has nothing left to say.
        if (IsWindow(window.Value)) settled.OnNext(window);
    }

    void Forget(WindowHandle window)
    {
        if (!quiet.Remove(window, out var timer)) return;
        timer.Stop();
    }

    // Both buttons, because either can hold a drag (and GetAsyncKeyState reads the physical buttons, so swapped
    // buttons are covered by asking both).
    static bool MouseButtonDown() =>
        (GetAsyncKeyState(VK_LBUTTON) & KeyDownBit) != 0 || (GetAsyncKeyState(VK_RBUTTON) & KeyDownBit) != 0;

    void Unhook()
    {
        UnhookWinEvent(hook);
        hook = 0;
    }

    // --- Screens changing ----------------------------------------------------------------------------------------

    // SystemEvents raises on its own thread, not ours, so each event is posted to the UI thread's dispatcher:
    // the Tiler's entry points all run there. SystemEvents' event is STATIC, so the handler is removed when the
    // subscription ends or it would outlive this class.
    IObservable<Unit> DisplaySettingsChanged() =>
        Observable.FromEventPattern<EventHandler, EventArgs>(
                h => SystemEvents.DisplaySettingsChanged += h,
                h => SystemEvents.DisplaySettingsChanged -= h)
            .Select(_ => Unit.Default)
            .ObserveOn(new DispatcherSynchronizationContext(dispatcher));

    // WM_SETTINGCHANGE is broadcast to top-level windows only, so this listens through a window of its own: a
    // 0x0 one that is never shown, as ShellHookAttentionMonitor's is. Not a message-only window (HWND_MESSAGE),
    // which broadcasts never reach. It exists only while someone is subscribed, and on the subscribing thread,
    // which has to be the UI thread (it needs a message loop).
    static IObservable<Unit> WorkAreaChanged() =>
        Observable.Create<Unit>(observer =>
        {
            var listener = new HwndSource(new HwndSourceParameters("Deskbar tiling work-area listener")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0, // not WS_VISIBLE: created, never shown
            });

            nint OnMessage(nint hwnd, int msg, nint wparam, nint lparam, ref bool handled)
            {
                if (msg == WM_SETTINGCHANGE && wparam == SPI_SETWORKAREA) observer.OnNext(Unit.Default);
                return 0;
            }

            listener.AddHook(OnMessage);
            return Disposable.Create(() =>
            {
                listener.RemoveHook(OnMessage);
                listener.Dispose();
            });
        });

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        watched = new HashSet<WindowHandle>();
        if (hook != 0) Unhook();
        quiet.Values.ToList().ForEach(t => t.Stop());
        quiet.Clear();
        settled.OnCompleted();
    }
}
