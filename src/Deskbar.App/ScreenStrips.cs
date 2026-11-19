using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Subjects;
using System.Windows.Interop;
using CSharpFunctionalExtensions;
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
//   pinToAllDesktops  the virtual-desktop pin, after the first show
//
// Not gated on the bar's visibility anywhere: the strips stay while the bar is minimized (spec, "What it does"), and
// a strip that left with the bar would move every screen's work area twice.
public sealed class ScreenStrips
{
    readonly Func<bool> wanted;
    readonly Func<IReadOnlyList<MonitorBounds>> screens;
    readonly Func<MonitorBounds, ScreenStrip> create;
    readonly Action<nint> ignore;
    readonly Action<nint> pinToAllDesktops;

    // Raised after a reconcile that added or removed a strip, so App can ask the bar to rebuild: a new strip is empty
    // until the bar's next rebuild hands it its tabs. Not raised by a reconcile that changed nothing, which is most of
    // them (every settle on the same desk), so those cost no rebuild.
    readonly Subject<Unit> changed = new();

    // The strips pinned to every desktop so far. Pinned once each, and only once shown: a window that has never been
    // shown is on no desktop, and the pin fails with "Element not found" (App.PinFloatingBar has the story).
    ImmutableHashSet<ScreenStrip> pinned = ImmutableHashSet<ScreenStrip>.Empty;

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
        Action<nint> ignore, Action<nint> pinToAllDesktops)
    {
        this.wanted = wanted;
        this.screens = screens;
        this.create = create;
        this.ignore = ignore;
        this.pinToAllDesktops = pinToAllDesktops;
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
    // could not show it (its screen could not be read just then) is pinned by Tick once it is up.
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

    ScreenStrip Open(MonitorBounds screen)
    {
        var strip = create(screen);
        // EnsureHandle creates the window without showing it, and runs the strip's OnSourceInitialized, which makes it
        // a tool window before anything can list it.
        var hwnd = new WindowInteropHelper(strip).EnsureHandle();
        ignore(hwnd);
        // A failed dock is not fatal and not retried here: the strip stays in the set, docked-but-waiting, and its own
        // guard (KeepStrip, on Tick) docks it once its screen can be read. Traced so a strip that never appeared can be
        // told from one that was never made.
        strip.Dock().TapError(error => ClickTrace.Write($"strip dock on start: {error}"));
        PinIfShown(strip);
        return strip;
    }

    void Remove(ScreenStrip strip)
    {
        strip.Undock();
        strip.Close();
        pinned = pinned.Remove(strip);
    }

    void PinIfShown(ScreenStrip strip)
    {
        if (!strip.IsVisible || pinned.Contains(strip)) return;
        pinned = pinned.Add(strip);
        pinToAllDesktops(new WindowInteropHelper(strip).Handle);
    }

    // The 1 s beat, from App's topmost timer: each strip's guard (KeepStrip, which acts only while the strip's screen
    // is among the connected ones and never docks it anywhere else), then its place at the top of the topmost band.
    // And the pin for a strip that was shown after its first dock (see Open). Not gated on the bar being visible.
    public void Tick()
    {
        var connected = screens();
        Current.ToList().ForEach(strip =>
        {
            strip.KeepStrip(connected);
            strip.ReclaimTopmost();
            PinIfShown(strip);
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
