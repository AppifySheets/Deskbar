using CSharpFunctionalExtensions;
using Deskbar.Core.Abstractions;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Tiling;

// A NEW WINDOW SPLITS THE TILE YOU LAST USED (docs/superpowers/specs/2026-11-19-tiling-predictable-placement-design.md,
// "Change 2"). A window used to join the end of its screen, and the grid was rebuilt from the new count, so every
// tile could move when one window opened. Now a window that arrives takes half of ONE tile and no other tile moves:
//
//   1. A minimized tile coming back goes to its old place (Tiler.Minimize.cs, unchanged).
//   2. An opening (TileLayout.Opening, room kept for the next window): the window goes into it. The screen of the
//      tile last used is looked at first, then the screen the window opened on.
//   3. Otherwise it splits the tile the user last activated, on THAT tile's screen, even when the app opened the
//      window on another one (TileArrival.Choose picks the tile and the side). Not when a by-hand move AIMED the
//      window at a screen (a drop on one screen's half of a bar row): that screen is kept, and only the tile last
//      used there is offered (see Arrived). The same for a window restored from minimized that arrives (it had no
//      remembered place): it keeps the screen Windows restored it to (`unminimized`).
//   4. Otherwise (no tile last used, or the second and later windows of one evaluate) it splits the largest tile
//      of its own screen. TileArrival.Choose does that fallback too, also when the last-used tile is too small.
//
// Since 2026-11-22 (docs/superpowers/specs/2026-11-22-tile-lock-and-other-screens-design.md) two rules come between
// step 2 and step 3 for a window that is free to move (not aimed, not restored): an opening on ANY other screen, in
// screen number order, and then an EMPTY screen (its own first, then the others in number order; empty means nothing of
// this workspace there, a set-aside maximized window included). Petre: "when opening windows, check if there's space on
// another screen too". And in steps 3 and 4 a LOCKED tile is never split (Tiler.Lock.cs): when every tile of the target
// screen is locked the window splits the largest unlocked tile of another screen, and only when nothing anywhere is
// unlocked is the lock ignored for that one window.
//
// "Last used" is the order windows were activated in, kept for every window and filtered when a target is picked:
// a new window usually activates itself BEFORE it joins the board, and once it has joined it is the last-used tile
// like any other. Activations Windows hands out by itself do not count: when a tile closes or is minimized,
// Windows activates whatever is next in z-order, which can be a tile on another screen the user never touched. An
// activation whose Windows event time (WindowEvent.Time) lies within HandOutWindowMs of a tile leaving, before or
// after it, is that hand-out.
//
// Which arrivals split: a window that appeared on the workspace or became tileable there (Evaluate), and one
// brought from another workspace (OnWindowMoved). Not a window dragged by hand (the drop rules decide it), not a
// window re-homed because its screen went away, and not the first fill (TileBoard.FirstFill orders by position).
public sealed partial class Tiler
{
    // How many activations are remembered. The target is the first one still on the board, so a short list is
    // plenty; it only has to outlast the floating windows and dialogs activated since the last tile.
    const int UsedKept = 32;

    // How close in time an activation and a tile leaving must be for the activation to be Windows' hand-out rather
    // than the user's. A person cannot close a tile and then click another one within a quarter of a second, while
    // Windows activates the next window in z-order within milliseconds of the close or minimize. Both times are
    // Windows' own event times (WindowEvent.Time), so it does not matter how late either event reaches the Tiler,
    // or whether the mouse kept moving in between. The value is to be checked live against the trace.
    const uint HandOutWindowMs = 250;

    // Windows in the order they were last activated, most recent first, each with the Windows event time of that
    // activation (0 when the event did not come from a WinEvent). Read by LastUsed, written by NoteUsed and NoteLeft,
    // and a window that disappears is taken off.
    IReadOnlyList<(WindowHandle Window, uint Time)> used = [];

    // The Windows event time of the last tile that left a grid (NoteLeft). None until a tile first leaves.
    Maybe<uint> leftAt = Maybe<uint>.None;

    // A window was activated (OnWindowEvent, before RaiseTogether). It goes to the front of the list, unless the
    // activation is within HandOutWindowMs of a tile leaving: then Windows activated it by itself after the close or
    // minimize, and the user's choice of tile stands. This covers an activation that FOLLOWS the leave; one that came
    // first is dropped by NoteLeft. Every window is recorded, tiles or not; LastUsed filters.
    void NoteUsed(WindowHandle window, uint time)
    {
        if (leftAt.TryGetValue(out var left) && Near(time, left))
        {
            trace?.Invoke($"tile: {window.Value:X} activated {Apart(time, left)} ms from a tile leaving, handed out by Windows, not counted as used");
            return;
        }
        used = used.Where(u => u.Window != window).Prepend((window, time)).Take(UsedKept).ToList();
    }

    // A tile is leaving its grid: it closed (Disappeared), went to the tray (Hidden) or was minimized. Called from
    // OnWindowEvent with that event's time, before the evaluate that takes it off the board, and only for a window on
    // the board. Moves between screens and windows re-homed when a screen goes are not leaves here: Windows activates
    // nothing for them. The time is saved, so an activation that follows within HandOutWindowMs is ignored
    // (NoteUsed), and every listed activation within HandOutWindowMs of it is dropped, which covers the hand-out
    // activation that ARRIVED BEFORE the leave (Windows can raise it before the window's own event reaches us).
    //
    // Known imprecision, accepted: the time tells only that the two happened together, not who caused the
    // activation. If the user clicks tile A and a tile closes or minimizes BY ITSELF within a quarter of a second,
    // A's activation is dropped too, and the target falls back to the window used before A.
    //
    // A leave with no event time (0, not from a WinEvent) matches nothing, so it changes nothing either: it must not
    // overwrite the time of a real leave just before it, or that leave's hand-out activation would count as the user's
    // (final review, M3).
    void NoteLeft(WindowHandle window, uint time)
    {
        if (time == 0) return;
        leftAt = time;
        var dropped = used.Where(u => Near(u.Time, time)).ToList();
        used = used.Where(u => !Near(u.Time, time)).ToList();
        dropped.ForEach(u => trace?.Invoke($"tile: {u.Window.Value:X} was activated {Apart(u.Time, time)} ms from {window.Value:X} leaving, handed out by Windows, not counted as used"));
    }

    // Two Windows event times within HandOutWindowMs of each other, in either order. The GetTickCount clock wraps, so
    // the difference is taken both ways with unsigned wrap-around. 0 is "not from a WinEvent" and never matches.
    static bool Near(uint a, uint b) =>
        a != 0 && b != 0 && (unchecked(a - b) <= HandOutWindowMs || unchecked(b - a) <= HandOutWindowMs);

    // How far apart two event times are, for the trace.
    static uint Apart(uint a, uint b) => Math.Min(unchecked(a - b), unchecked(b - a));

    // A window that disappeared is no longer anyone's target; its handle may be recycled for another window.
    void ForgetUsed(WindowHandle window) =>
        used = used.Where(u => u.Window != window).ToList();

    // The tile a new window splits: the most recently activated window that is on `board`, is not the arriving
    // window itself and is not maximized (a maximized tile covers its screen, and halving it would be invisible).
    // A floating or pinned window is never on the board, so activating one leaves the target as it was.
    Maybe<WindowHandle> LastUsed(TileBoard board, WindowHandle except) =>
        used.Select(u => u.Window)
            .Where(w => w != except && board.ScreenOf(w).HasValue && !surface.IsMaximized(w))
            .TryFirst();

    // Places windows that just arrived on the workspace's board. `board` already holds them, each on the screen it
    // opened on (Reconciled or TileBoard.Join put it at the end there); they are taken off and placed again, one at a
    // time in the order given, so each later one sees the earlier ones in their new places. Only the first is offered
    // the tile last used; the rest split the largest tile of their own screen (the design: "the first splits the
    // target (or fills the opening) and each of the rest splits the largest tile on its screen").
    //
    // Layouts are stored here, before the caller hands the board to Keep, so the re-tile lays the new shape out. An
    // opening is filled HERE as well (step 2), so Keep's IntoOpenings finds no opening left and never fills twice.
    //
    // AN AIMED ARRIVAL STAYS ON ITS SCREEN. A window a by-hand move aimed at a screen (a drop on one screen's half of
    // the row, WorkspaceManager.TakeAimedScreen) has that screen as its own, whatever it opened on, and is offered
    // only the tile last used ON THAT SCREEN, so step 3 never carries it to another one. Petre: "let's respect which
    // monitor i put it to", after a window dropped on the left screen split the last-used tile on the right. With
    // no tile used on that screen it splits the screen's largest tile (step 4). It is offered its screen's last-used
    // tile whatever its place among the arrivals, because the user named the screen for this window in particular.
    TileBoard Arrived(Workspace workspace, TileBoard board, IReadOnlyList<WindowHandle> arrivals)
    {
        if (arrivals.Count == 0) return board;
        var screens = surface.Screens().ToDictionary(s => s.Number);
        // Taken (and so forgotten) here, once per arrival: the aim decides this arrival and no later one. A window
        // restored from minimized counts as aimed at its own screen (KeptScreenOf).
        var aimed = arrivals.ToDictionary(w => w, KeptScreenOf);
        var own = arrivals.ToDictionary(w => w,
            w => aimed[w].GetValueOrDefault(board.ScreenOf(w).GetValueOrDefault(surface.ScreenOf(w))));
        var without = arrivals.Aggregate(board, (b, w) => b.Leave(w));
        var target = LastUsed(without, arrivals[0]);
        aimed.Where(a => a.Value.HasValue).ToList()
            .ForEach(a => trace?.Invoke($"tile: {workspace.Name}: {a.Key.Value:X} was dropped on screen {a.Value.Value}, it stays there"));
        // Each arrival is remembered with the screen as it was before it (Joining, Tiler.Rejoin.cs), so the window
        // leaving again puts that layout back.
        return arrivals
            .Select((window, i) => (Window: window, Offered: i == 0 ? target : Maybe<WindowHandle>.None))
            .Aggregate(without, (b, a) => Joining(workspace, b, a.Window,
                () => Arrive(workspace, screens, b, a.Window, own[a.Window],
                    aimed[a.Window].HasValue ? LastUsedOn(b, a.Window, own[a.Window]) : a.Offered,
                    aimed[a.Window].HasValue)));
    }

    // The window whose restore from minimized raised the evaluate running now (set and cleared around it by
    // HandleWindowEvent). Petre: "i had a chrome window in the deskbar workspace minimized in the left screen; i
    // clicked on it and it moved to the right screen". It had been minimized since before this Deskbar started, so
    // there was no remembered place (Tiler.Minimize.cs) and the restore arrived like a new window, which splits the
    // tile last used on whatever screen that is. A restored window is not new: the user knows which screen it was
    // on, so it is treated as aimed at the screen Windows restored it to.
    Maybe<WindowHandle> unminimized = Maybe<WindowHandle>.None;

    // The screen an arrival must stay on: the one a by-hand move aimed it at (taken from the manager, so one drop
    // decides one arrival), or, for a window just restored from minimized, the screen it is on.
    Maybe<int> KeptScreenOf(WindowHandle window) =>
        manager.TakeAimedScreen(window)
            .Or(() => unminimized == window ? surface.ScreenOf(window) : Maybe<int>.None);

    // The tile last used among those on screen `number` (LastUsed's rules otherwise), for an aimed arrival.
    Maybe<WindowHandle> LastUsedOn(TileBoard board, WindowHandle except, int number) =>
        used.Select(u => u.Window)
            .Where(w => w != except && board.ScreenOf(w) == number && !surface.IsMaximized(w))
            .TryFirst();

    // One window's arrival: the target screen is the last-used tile's screen, or the window's own. An opening there,
    // then an opening on its own screen, then a split of a tile on the target screen (SplitSomewhere, which looks at
    // another screen only when every tile on the target is locked). `aimed` is a window dropped on a screen or restored
    // from minimized (KeptScreenOf): it never leaves `own`.
    //
    // OTHER SCREENS FIRST (spec 2026-11-22-tile-lock-and-other-screens, steps 3 and 4). Petre: "when opening windows,
    // check if there's space on another screen too", and of the two meanings of space he picked an opening or an empty
    // screen, not balancing by tile size (which would send windows to a screen he is not looking at). So, for a window
    // that is free to move, after the two openings above: an opening on any other screen in number order, then an
    // empty screen (its own first, then the others in number order), and only then a split. An aimed window skips both.
    //
    // Each later arrival of one evaluate sees the board the earlier ones left (Arrived folds over them), so two windows
    // never both take one empty screen.
    TileBoard Arrive(Workspace workspace, IReadOnlyDictionary<int, TileScreen> screens, TileBoard board,
        WindowHandle window, int own, Maybe<WindowHandle> lastUsed, bool aimed)
    {
        var target = lastUsed.Bind(board.ScreenOf).GetValueOrDefault(own);
        var others = screens.Keys.Where(n => n != target && n != own).Order().ToList();
        // Lazy: Select runs each try only until one answers, so a later one never stores a layout it did not take.
        IEnumerable<Func<Maybe<TileBoard>>> tries =
        [
            () => IntoOpeningOn(workspace, screens, board, window, target, lastUsed.HasValue ? "the screen of the tile last used" : "its own screen"),
            () => target != own ? IntoOpeningOn(workspace, screens, board, window, own, "its own screen") : Maybe<TileBoard>.None,
            .. aimed ? [] : others.Select(n => (Func<Maybe<TileBoard>>)(() => IntoOpeningOn(workspace, screens, board, window, n, "another screen"))),
            .. aimed ? [] : new[] { own }.Concat(screens.Keys.Where(n => n != own).Order())
                .Select(n => (Func<Maybe<TileBoard>>)(() => IntoEmptyScreen(workspace, screens, board, window, n))),
        ];
        return tries.Select(t => t()).FirstOrDefault(m => m.HasValue, Maybe<TileBoard>.None)
            .GetValueOrDefault(() => SplitSomewhere(workspace, screens, board, window, target, lastUsed, aimed));
    }

    // Step 4: screen `number` holds nothing of this workspace, so the window takes all of it. Empty means no tile on the
    // board there AND no maximized window of this workspace set aside there (`aside`): such a window is off the board but
    // fills the screen, and a tile laid there would sit under it or over it.
    Maybe<TileBoard> IntoEmptyScreen(Workspace workspace, IReadOnlyDictionary<int, TileScreen> screens, TileBoard board,
        WindowHandle window, int number)
    {
        if (!screens.ContainsKey(number) || board.On(number).Count > 0 || aside.Any(w => surface.ScreenOf(w) == number))
            return Maybe<TileBoard>.None;
        trace?.Invoke($"tile: {workspace.Name}: {window.Value:X} arrived, took empty screen {number}");
        return board.Join(number, window);
    }

    // EVERY TILE ON THE TARGET SCREEN LOCKED (Tiler.Lock.cs; spec 2026-11-22, behaviour step 6). Splitting there would
    // either halve a locked tile or ignore the lock, so a window that is free to move goes to another screen with an
    // unlocked tile instead: the screen whose largest unlocked tile is largest in pixels (screens differ in size, so
    // the layout's fractions cannot compare them), the lower screen number on a tie. It splits that screen's largest
    // unlocked tile; the tile last used is on the locked screen, so none is offered. With no such screen, or for an
    // aimed window, the split happens on the target as before, and TileArrival.Choose, finding every tile skipped,
    // ignores the skip: a window always has to go somewhere.
    //
    // The same redirect for two more targets the final review found (it measured both):
    //   - every tile there is locked OR maximized (a tile the Tiler is un-maximizing itself keeps its slot but is never
    //     split), with at least one locked: Choose would find every tile skipped and halve a locked one anyway.
    //   - no tile there, but a maximized window of this workspace is set aside there (`aside`): the screen is not empty
    //     (IntoEmptyScreen says so), and joining it would put the window under or over the maximized one. This target
    //     comes up whenever no tile last used is offered: the second and later arrivals of one evaluate, or any arrival
    //     before a tile has been activated since Deskbar started.
    // With no other screen to go to (no unlocked tile anywhere else), the window goes to the target as before.
    TileBoard SplitSomewhere(Workspace workspace, IReadOnlyDictionary<int, TileScreen> screens, TileBoard board,
        WindowHandle window, int target, Maybe<WindowHandle> lastUsed, bool aimed)
    {
        var here = board.On(target);
        var blocked = here.Count == 0
            ? aside.Any(w => surface.ScreenOf(w) == target) ? "shows only a maximized window" : null
            : here.Any(locked.Contains) && here.All(w => locked.Contains(w) || surface.IsMaximized(w)) ? "has every tile locked" : null;
        if (aimed || blocked is null)
            return Split(workspace, screens, board, window, target, lastUsed);
        return UnlockedElsewhere(workspace, screens, board, target).Match(
            other =>
            {
                trace?.Invoke($"tile: {workspace.Name}: {window.Value:X} arrived, screen {target} {blocked}, it goes to screen {other}");
                return Split(workspace, screens, board, window, other, Maybe<WindowHandle>.None);
            },
            () => Split(workspace, screens, board, window, target, lastUsed));
    }

    // The other screen whose largest unlocked tile is the largest, in pixels. Maximized tiles (still restoring) do not
    // count, as TileArrival never splits them either.
    Maybe<int> UnlockedElsewhere(Workspace workspace, IReadOnlyDictionary<int, TileScreen> screens, TileBoard board, int except) =>
        screens.Values
            .Where(s => s.Number != except)
            .Select(s => (s.Number, Area: LargestUnlockedArea(workspace, s, board.On(s.Number))))
            .Where(c => c.Area.HasValue)
            .OrderByDescending(c => c.Area.Value)
            .ThenBy(c => c.Number)
            .Select(c => c.Number)
            .TryFirst();

    Maybe<long> LargestUnlockedArea(Workspace workspace, TileScreen screen, IReadOnlyList<WindowHandle> order)
    {
        if (order.Count == 0) return Maybe<long>.None;
        var slots = SlotsOf(screen, order, ShownLayout(workspace, screen, order.Count));
        var areas = order
            .Where(w => !locked.Contains(w) && !surface.IsMaximized(w) && slots.ContainsKey(w))
            .Select(w => (long)slots[w].Width * slots[w].Height)
            .ToList();
        return areas.Count > 0 ? areas.Max() : Maybe<long>.None;
    }

    // Step 2: the screen's opening, when its stored layout keeps one for the windows it has. Filled the way Keep's
    // IntoOpenings fills one (TileArrange.Filled), the layout stored first. None when the screen has no opening.
    Maybe<TileBoard> IntoOpeningOn(Workspace workspace, IReadOnlyDictionary<int, TileScreen> screens, TileBoard board,
        WindowHandle window, int number, string where)
    {
        if (!screens.TryGetValue(number, out var screen)) return Maybe<TileBoard>.None;
        var order = board.On(number);
        if (!StoredOn(workspace.Id, KeyOf(screen)).TryGetValue(out var stored) || !stored.HasOpeningFor(order.Count))
            return Maybe<TileBoard>.None;

        var (next, filled) = TileArrange.Filled(order, stored, window);
        var into = board.Into(number, next);
        if (!into.ScreenOf(window).HasValue) return Maybe<TileBoard>.None;
        StoreLayout(workspace, KeyOf(screen), filled)
            .Tap(() => trace?.Invoke($"tile: {workspace.Name}: {window.Value:X} arrived on screen {number}, took the opening at slot {stored.Opening} on {where}"))
            .TapError(error => trace?.Invoke($"tile: {workspace.Name}: {window.Value:X} arrived on screen {number}, storing the filled opening failed: {error}"));
        return into;
    }

    // Steps 3 and 4: TileArrival.Choose picks the tile (the last-used one when both halves keep a quarter of the
    // screen, otherwise the largest) and the side, and TileArrange.Drop makes the split exactly as a drop on that
    // tile's edge would. Laid out with the layout the screen shows now (ShownLayout), which is what the user sees.
    //
    // The window joins the end of the screen instead in two cases, each traced: the screen is empty (nothing to
    // split), or its grid does not fit the work area (SlotsOf is empty, controller ruling 1), where RetileScreen's
    // too-small guard then skips the screen as it always has.
    TileBoard Split(Workspace workspace, IReadOnlyDictionary<int, TileScreen> screens, TileBoard board,
        WindowHandle window, int number, Maybe<WindowHandle> lastUsed)
    {
        if (!screens.TryGetValue(number, out var screen))
        {
            // Every arrival decision leaves a trace line, this one too (final review, M2): the screen it was meant for
            // is not reported (unplugged mid-evaluate), and the next evaluate re-homes it like the screen's others.
            trace?.Invoke($"tile: {workspace.Name}: {window.Value:X} arrived on screen {number}, which is not reported, joined the end");
            return board.Join(number, window);
        }
        var order = board.On(number);
        var layout = ShownLayout(workspace, screen, order.Count);
        var slots = SlotsOf(screen, order, layout);
        if (order.Count > 0 && slots.Count == 0)
        {
            trace?.Invoke($"tile: {workspace.Name}: {window.Value:X} arrived on screen {number}, the grid does not fit the work area, joined the end");
            return board.Join(number, window);
        }

        // The screen's maximized tiles are never split (TileArrival has the reason): the newcomer would sit behind one.
        // Nor are its locked tiles (Tiler.Lock.cs): that is what a lock is for. When every tile here is skipped,
        // Choose ignores the skip, because a window has to go somewhere.
        var skipped = order.Where(surface.IsMaximized).Concat(LockedAmong(order)).ToHashSet();
        return TileArrival.Choose(order, layout, order.Select(w => slots[w]).ToList(), lastUsed, skipped).Match(
            split =>
            {
                var (next, splitLayout) = TileArrange.Drop(order, layout, window, split.Target, split.Side);
                var into = board.Into(number, next);
                // Drop hands its input back unchanged when it cannot split (it always can here, by construction); the
                // window must never be lost from the board for that, so it joins the end instead.
                if (!into.ScreenOf(window).HasValue)
                {
                    trace?.Invoke($"tile: {workspace.Name}: {window.Value:X} arrived on screen {number}, splitting {split.Target.Value:X} {split.Side} made no room, joined the end");
                    return board.Join(number, window);
                }
                StoreLayout(workspace, KeyOf(screen), splitLayout)
                    .Tap(() => trace?.Invoke($"tile: {workspace.Name}: {window.Value:X} arrived on screen {number}, split {split.Target.Value:X} {split.Side} ({Why(split, lastUsed, locked)}), sizes {Show(splitLayout)}"))
                    .TapError(error => trace?.Invoke($"tile: {workspace.Name}: {window.Value:X} arrived on screen {number}, storing the split failed: {error}"));
                return into;
            },
            () =>
            {
                trace?.Invoke($"tile: {workspace.Name}: {window.Value:X} arrived on screen {number}, which was empty, joined it");
                return board.Join(number, window);
            });
    }

    // Which rule chose the split's tile, for the trace.
    static string Why(TileSplit split, Maybe<WindowHandle> lastUsed, IReadOnlySet<WindowHandle> locked) =>
        lastUsed.HasValue
            ? split.Target == lastUsed.Value
                ? locked.Contains(split.Target) ? "the tile last used, every tile here is locked" : "the tile last used"
                : locked.Contains(lastUsed.Value)
                    ? $"the largest unlocked tile, the tile last used ({lastUsed.Value.Value:X}) is locked"
                    : $"the largest tile, the tile last used ({lastUsed.Value.Value:X}) is too small to split"
            : "the largest tile, no tile last used";
}
