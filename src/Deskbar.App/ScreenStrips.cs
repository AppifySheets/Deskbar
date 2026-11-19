using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Subjects;
using System.Windows.Interop;
using CSharpFunctionalExtensions;
using Deskbar.Core.Abstractions;
using Deskbar.Core.Domain;
using Deskbar.Core.Geometry;

namespace Deskbar.App;

// THE STRIP SET: exactly one ScreenStrip per connected screen while the window list is on the screens
// (WindowListPlace.Screens), and none otherwise. Owned by App beside the bar, for the reason App owns the bar's own
// hwnd work: every strip's handle must go to WindowMonitor.Ignore and be pinned to every virtual desktop, and neither
// is the strip's business nor the bar's. The design is docs/superpowers/specs/2026-11-19-screen-strips-design.md
// ("The parts", the strip set; "Constraints").
//
// What it decides and what it does not. WHICH strips exist is StripPlan.Reconcile's (Deskbar.Core, pure, tested
// without a second monitor): this applies the plan. What a strip SHOWS is the bar's: the bar reads Current through
// FloatingBar.Strips and fills every strip inside its own guarded rebuild. How a strip keeps its band is the strip's
// (ScreenStrip.KeepStrip); this only gives it the beat.
//
// Every seam is a delegate, so the tests drive the whole set with made-up screens and strips whose dock is faked, and
// no test ever reserves a band on a real screen:
//
//   wanted            the setting is on the screens
//   screens           every connected screen's rcMonitor, physical pixels, from the sweep that numbers the screens
//                     (ScreenLayout.Screens), so a strip's Screen is == to a value of Overview.Screens
//   create            a new strip for one screen, with its menu and drop wired to the bar
//   ignore            WindowMonitor.Ignore, before the strip's first show
//   pinToAllDesktops  the virtual-desktop pin, after the first show; its Result decides whether it is tried again
//   windows           the windows the bar knows about, without Deskbar's own: the ones a new strip may move off its band
//   layout            where those windows are, which are minimized, and the quiet move (Nudge: no raise, no show state)
//   shellArranged     maximized or snapped (ScreenLayout.IsShellArranged): the shell sizes those, never moved here
//
// Not gated on the bar's visibility anywhere: the strips stay while the bar is minimized (spec, "What it does"), and
// a strip that left with the bar would move every screen's work area twice.
public sealed class ScreenStrips
{
    readonly Func<bool> wanted;
    readonly Func<IReadOnlyList<MonitorBounds>> screens;
    readonly Func<MonitorBounds, ScreenStrip> create;
    readonly Action<nint> ignore;
    readonly Func<nint, Result> pinToAllDesktops;
    readonly Func<IReadOnlyList<WindowHandle>> windows;
    readonly IScreenLayout layout;
    readonly Func<WindowHandle, bool> shellArranged;

    // Raised after a reconcile that added or removed a strip, so App can ask the bar to rebuild: a new strip is empty
    // until the bar's next rebuild hands it its tabs. Not raised by a reconcile that changed nothing, which is most of
    // them (every settle on the same desk), so those cost no rebuild.
    readonly Subject<Unit> changed = new();

    // The strips pinned to every desktop so far: recorded only once a pin has WORKED. Pinned once each, and only once
    // shown: a window that has never been shown is on no desktop, and the pin fails with "Element not found"
    // (App.PinFloatingBar has the story).
    //
    // A pin that failed is tried again by Tick, at most once every FloatingBar.RetryEvery (the docked bar's guard's
    // interval), until it works. It has to be: the band a strip reserves is the shell's, on every desktop, but an
    // unpinned strip is cloaked on every desktop but the one it was made on, so each of the others shows an empty band
    // of wallpaper at the top of that screen, and nothing else repairs it (the strip's guard sees its window inside its
    // screen and the work area intact, and calls it held). The second anti-goal exactly (review of task 8). Strips are
    // made on unlock, on a replug and on the setting toggled, often after a long idle, which is when the desktop COM
    // object is known to go stale (#148), and the pin also answers a transient "not ready".
    ImmutableHashSet<ScreenStrip> pinned = ImmutableHashSet<ScreenStrip>.Empty;

    // A pin in flight. The pin is a COM call, which pumps messages, so the 1 s timer can tick inside it; that Tick must
    // not pin the same strip a second time while the first call is still out.
    ImmutableHashSet<ScreenStrip> pinning = ImmutableHashSet<ScreenStrip>.Empty;

    // When each unpinned strip's pin was last tried, for the retry interval.
    ImmutableDictionary<ScreenStrip, DateTime> lastPinTry = ImmutableDictionary<ScreenStrip, DateTime>.Empty;

    // The time the retry interval is read against, UTC. Faked in the tests to step over the interval without waiting.
    internal Func<DateTime> Clock { get; init; } = () => DateTime.UtcNow;

    // A reconcile in progress, and another one asked for while it ran. The pin is a virtual-desktop COM call, and COM
    // calls on an STA thread pump the message queue (CLAUDE.md, traps), so a settle or a state pulse queued on the
    // dispatcher can run in the middle of a reconcile. Run then, it would plan against a Current that is half-applied
    // and could open a second strip on one screen (the second anti-goal). So it is remembered and run after.
    bool reconciling;
    bool reconcileAgain;

    // Set by RemoveAll: the app is going away, and a strip docked after this would hold its band with nothing left to
    // undock it until the process died.
    bool removedForGood;

    public ScreenStrips(Func<bool> wanted, Func<IReadOnlyList<MonitorBounds>> screens, Func<MonitorBounds, ScreenStrip> create,
        Action<nint> ignore, Func<nint, Result> pinToAllDesktops,
        Func<IReadOnlyList<WindowHandle>> windows, IScreenLayout layout, Func<WindowHandle, bool> shellArranged)
    {
        this.wanted = wanted;
        this.screens = screens;
        this.create = create;
        this.ignore = ignore;
        this.pinToAllDesktops = pinToAllDesktops;
        this.windows = windows;
        this.layout = layout;
        this.shellArranged = shellArranged;
    }

    // The strips there are now, one per screen, in no particular order (the bar puts them in reading order itself).
    // Replaced, never mutated, so a caller holding the list it was handed is never surprised by it changing underneath.
    public IReadOnlyList<ScreenStrip> Current { get; private set; } = [];

    // See `changed`.
    public IObservable<Unit> Changed => changed;

    // Makes the strips match the screens and the setting: StripPlan.Reconcile says which screens gain a strip and
    // which lose theirs, and this applies it, removals first. First because a screen whose rectangle changed (a new
    // resolution) is a removal and an addition of the same top edge, and the old band must be given back before the
    // new one is asked for, or the shell would grant the new strip below the old one.
    //
    // A new strip: its handle to `ignore` BEFORE its first show, or WindowMonitor sees its EVENT_OBJECT_SHOW and the
    // bar lists the strip as a window; then Dock, which shows it; then the pin, after the show. A strip whose dock
    // could not show it (its screen could not be read just then) is pinned by Tick once it is up, and a pin that failed
    // is tried again by Tick until it works.
    //
    // A removed strip: Undock, which gives the band back and hides the window, then Close. In that order, so a band is
    // never left reserved with no window in it.
    public void Reconcile()
    {
        if (removedForGood) return;
        if (reconciling)
        {
            reconcileAgain = true;
            return;
        }

        reconciling = true;
        try
        {
            do
            {
                reconcileAgain = false;
                if (ReconcileOnce()) changed.OnNext(Unit.Default);
            } while (reconcileAgain && !removedForGood);
        }
        finally
        {
            reconciling = false;
        }
    }

    // One pass of the plan. True when a strip came or went.
    bool ReconcileOnce()
    {
        var plan = StripPlan.Reconcile(wanted(), screens(), Current.Select(strip => strip.Screen).ToList());
        if (plan.Add.Count == 0 && plan.Remove.Count == 0) return false;

        var leaving = Current.Where(strip => plan.Remove.Contains(strip.Screen)).ToList();
        // Out of Current before they go, so nothing that runs while they are being closed hands them to the bar.
        Current = Current.Except(leaving).ToList();
        leaving.ForEach(Remove);

        // One at a time into Current, each as soon as it is open, so Current is true at every moment a pumped message
        // could read it.
        plan.Add.ToList().ForEach(screen => Current = Current.Append(Open(screen)).ToList());
        return true;
    }

    // A trace line, built only when tracing is on, so the strip set costs nothing with it off (see ScreenStrip.Tracing).
    static void Say(Func<string> line)
    {
        if (ClickTrace.On) ClickTrace.Write(line());
    }

    ScreenStrip Open(MonitorBounds screen)
    {
        var strip = create(screen);
        // EnsureHandle creates the window without showing it, and runs the strip's OnSourceInitialized, which makes it
        // a tool window before anything can list it.
        var hwnd = new WindowInteropHelper(strip).EnsureHandle();
        ignore(hwnd);
        Say(() => $"strip on {strip.Screen.Left},{strip.Screen.Top}: registered, screen {strip.Screen.Left},{strip.Screen.Top} to {strip.Screen.Right},{strip.Screen.Bottom}");
        // A failed dock is not fatal and not retried here: the strip stays in the set, docked-but-waiting, and its own
        // guard (KeepStrip, on Tick) docks it once its screen can be read. The strip traces its own refusal (ScreenStrip.
        // Refuse), so a strip that never appeared can be told from one that was never made.
        // A dock that worked takes a band some windows' title bars may now be under: those are moved down, here and only
        // here, so once per created strip (see ClearUnder).
        strip.Dock().Tap(() => ClearUnder(strip));
        PinIfShown(strip);
        return strip;
    }

    // THE WINDOWS UNDER A NEW STRIP. The shell resizes maximized windows to the new work area, but it leaves a restored
    // window whose title bar the new band covers exactly where it was, and that title bar is the only handle to drag
    // it by (measured: a window at [450,6,1350,456] stayed put under a 48 px top bar; probe notes 2026-11-19, section 1).
    // So each such window is moved down to start at the strip's bottom edge (StripPlan.Clear has the rule), once, when
    // the strip is created. Never again for that strip: after this, a window under it was put there by hand, and
    // moving it back every tick would fight the person dragging it.
    //
    // Which windows: the ones the bar knows (`windows`, Deskbar's own left out) that the layout snapshot lists, because
    // the snapshot is what says which are minimized, and a minimized window's rectangle is -32000 nonsense (CLAUDE.md,
    // traps). The snapshot lists windows on EVERY virtual desktop, not only the one in view: TopLevelWindows keeps
    // shell-cloaked windows on purpose, and a window on another desktop is shell-cloaked. So those are moved too, which
    // is right, the band being reserved on every desktop. A window the shell sizes itself, maximized or snapped
    // (`shellArranged`), is skipped: the shell resized a maximized one already, and moving a snapped one would break
    // the snap and push its bottom off the work area.
    //
    // HOW: with IScreenLayout.Nudge, never MoveTo. MoveTo raises the window to the front, which is right for the
    // drag-to-screen gesture and wrong for a move nobody asked for: every window shifted here would jump in front of
    // the one being worked in (task 9 review). Nudge keeps the z-order, the focus and the show state.
    //
    // How far down: to the bottom of the band the shell actually GRANTED, measured from the screen's top. Not a height
    // recomputed from a DPI here, and not the grant's height alone: a taskbar on the top edge would make the shell grant
    // the band below it, and a window moved to "screen top + band height" would still be under the strip. No grant (the
    // dock was refused, or granted nothing) moves nothing.
    //
    // A window that will not move (an elevated process, a handle that died since the snapshot) is traced and skipped;
    // the rest still move. Each move is traced too, so "why did my window jump" has its answer in the log.
    void ClearUnder(ScreenStrip strip) =>
        strip.Granted.Tap(band =>
        {
            var stripPx = band.B - strip.Screen.Top;
            var facts = layout.Snapshot();
            windows()
                .Where(window => facts.ZOrder.ContainsKey(window) && !facts.Minimized.Contains(window) && !shellArranged(window))
                .SelectMany(window => layout.RectOf(window).GetValueOrDefault() is { } from && StripPlan.Clear(from, strip.Screen, stripPx) is { } to
                    ? [(Window: window, From: from, To: to)]
                    : Array.Empty<(WindowHandle Window, WindowRect From, WindowRect To)>())
                .ToList()
                .ForEach(move => layout.Nudge(move.Window, move.To)
                    .Tap(() => Say(() =>
                        $"strip on {strip.Screen.Left},{strip.Screen.Top}: moved {move.Window.Value:X} from {move.From.Left},{move.From.Top} to {move.To.Left},{move.To.Top}, off the band"))
                    .TapError(error => Say(() =>
                        $"strip on {strip.Screen.Left},{strip.Screen.Top}: could not move {move.Window.Value:X} off the band: {error}")));
        });

    // A removal and an addition for one screen close together, in the trace, is a strip that disappeared and came back:
    // what the manual script's lock and unlock check looks for.
    void Remove(ScreenStrip strip)
    {
        Say(() => $"strip on {strip.Screen.Left},{strip.Screen.Top}: removed");
        strip.Undock();
        strip.Close();
        pinned = pinned.Remove(strip);
        lastPinTry = lastPinTry.Remove(strip);
    }

    // Pins a shown strip that is not pinned yet, unless a pin for it is in flight or the last try was within
    // RetryEvery. "In flight" is set BEFORE the COM call and "pinned" only AFTER it worked; a failure is traced, as the
    // docked bar traces each of its attempts, and left for a later Tick.
    void PinIfShown(ScreenStrip strip)
    {
        if (!strip.IsVisible || pinned.Contains(strip) || pinning.Contains(strip)) return;
        var now = Clock();
        if (lastPinTry.TryGetValue(strip, out var last) && now - last < FloatingBar.RetryEvery) return;

        lastPinTry = lastPinTry.SetItem(strip, now);
        pinning = pinning.Add(strip);
        try
        {
            pinToAllDesktops(new WindowInteropHelper(strip).Handle)
                .Tap(() =>
                {
                    pinned = pinned.Add(strip);
                    lastPinTry = lastPinTry.Remove(strip);
                })
                .TapError(error => Say(() =>
                        $"strip pin failed on {strip.Screen.Left},{strip.Screen.Top}: {error}; trying again in {FloatingBar.RetryEvery.TotalSeconds:N0} s"));
        }
        finally
        {
            pinning = pinning.Remove(strip);
        }
    }

    // The 1 s beat, from App's topmost timer: each strip's guard (KeepStrip, which acts only while the strip's screen
    // is among the connected ones and never docks it anywhere else), then its place at the top of the topmost band.
    // And the pin for a strip that was shown after its first dock (see Open), or whose pin failed (see `pinned`). Not
    // gated on the bar being visible.
    public void Tick()
    {
        var connected = screens();
        Current.ToList().ForEach(strip =>
        {
            strip.KeepStrip(connected);
            strip.ReclaimTopmost();
            PinIfShown(strip);
            // After the guard, so a repair shows in the line it just changed. Written only when it differs.
            strip.TraceGeometry();
        });
    }

    // Exit and Restart: every strip's band given back and its window closed, before the app's other windows close.
    // For good: a settle or a state pulse that arrives after this reconciles nothing.
    public void RemoveAll()
    {
        removedForGood = true;
        var all = Current;
        Current = [];
        all.ToList().ForEach(Remove);
    }

    // The debug dump's section: one line per strip (ScreenStrip.Describe), or a line saying why there are none.
    public IEnumerable<string> Describe() =>
        Current.Count > 0 ? Current.Select(strip => strip.Describe()).ToList()
        : [removedForGood ? "none: removed for exit" : wanted() ? "none: no screen could be read" : "none: the window list is not on the screens"];
}
