using System.Globalization;
using CSharpFunctionalExtensions;
using Deskbar.Core.Abstractions;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Tiling;

// Resizable tiles: the Tiler's wiring for docs/superpowers/specs/2026-11-08-tile-resize-design.md. Petre: "i want
// to be able to resize tiles by dragging the separation", "keep my sizes", and sizes belong to "places", not to
// windows. The layout is a split tree (TileLayout; docs/superpowers/specs/2026-11-19-tiling-split-tree-design.md),
// and the arithmetic lives in pure classes (TileArrange.Refit, SettledResize, SplitMove, GridLayout.Arrange on the
// tree); this file only decides when to ask them and what to do with the answer. Tiler.cs calls in from four places:
//   - SlotsOf asks LayoutOn for a screen's layout, fitted to the number of windows it now holds;
//   - Keep calls NoteLeavers before it replaces a board, so the next fit knows which leaf a leaving window held;
//   - OnSettled asks MovedASplit before the move rules, and EchoOfOurPlace first of all;
//   - PlaceIfNeeded calls AwaitEcho after a Place, so the Settled that Place causes is never read as a drag.
//
// Everything here runs inside the Tiler's serialised Run, under the same gate as the rest (OnSettled reaches
// MovedASplit only while TilingGate acts, the window is on the board and no bar drop is pending). Storing a layout
// goes through StoreLayout, which calls WorkspaceManager.SetTileLayout (that pulses StateChanged) and then takes the
// new layouts into the Tiler's StateKey as already seen. The layouts are in the key so that "Even out tiles",
// "Reset to grid", untile and the pipe's `even` and `reset-grid` re-tile; the Tiler's own store must not, because the re-tile it would cause is a full
// evaluate (WorkspaceOf, a COM call, for every known window) that places nothing (final review, item 2).
//
// "Sizes" in the comments and the trace below is the stored layout: the word predates the tree, and the trace keeps
// it so a log read across the change still reads the same.
public sealed partial class Tiler
{
    // The leaf (in the layout as it was BEFORE) of a window that left a screen's grid, per workspace and screen
    // number: TileArrange.Refit removes that leaf and gives its share to its siblings, where without it the last leaf
    // would go. Used by the next fit of that screen and then forgotten (LayoutOn takes it).
    //
    // `Closed` says the leaver CLOSED (its window disappeared or went to the tray) while its workspace keeps a closed
    // tile's space (Workspace.TileKeepsSpace). LayoutOn then keeps the leaf as the screen's opening instead of removing
    // it (tiling-predictable-placement design, change 3). False for every other way of leaving: a minimize, a move to
    // another workspace or screen, a window that stopped qualifying, or any leave while the switch is off.
    //
    // `OldCount` is how many windows the screen held when the leaf was read (final review, C1). A screen can lose a
    // window and gain another in one change (a cross-screen swap), and then its count, and so its layout, is what it
    // was: the leaf is no leaver for the fit at all. And with nothing stored, the layout the leaf was read from is the
    // automatic layout of OldCount windows, not of "one more than now" (TileArrange.FitStart).
    readonly Dictionary<(Guid Workspace, int Screen), Leaver> leavers = [];

    // One screen's pending leaver: its leaf in the layout as it was, whether it closed with the switch on, and how
    // many windows that layout held (see `leavers`).
    sealed record Leaver(int Leaf, bool Closed, int OldCount);

    // Windows that are closing right now on a workspace that keeps a closed tile's space. OnWindowEvent adds one on
    // Disappeared or Hidden of a window on the board, before the evaluate that takes it off; NoteLeavers reads it
    // into the leaver entry and then clears it, so it never outlives the one board change it was noted for, and
    // OnWindowEvent clears it again when its event is done, for an evaluate that threw or changed no board. Never
    // filled for a minimize: minimizing is not a close (the design says so, and a minimized tile comes back to its
    // own place through Tiler.Minimize.cs, not into an opening).
    readonly HashSet<WindowHandle> closing = [];

    // Windows the Tiler has placed whose own Settled has not come back yet (controller ruling 2). That Settled
    // ends a change that Deskbar's SetWindowPos began, so even when the surface says a button was down during it
    // (ByHand reads the global mouse state, and a button can be held for something else entirely, while the app
    // adjusts itself after our Place) it is not a drag and never moves a split. Set by AwaitEcho, taken by the
    // window's next Settled whatever that Settled turns out to be, and dropped when the window leaves every grid.
    readonly HashSet<WindowHandle> awaitingEcho = [];

    // A screen that comes back (or appears) gets its windows back ONE AT A TIME: Windows moves them, and each
    // joins by its own Settled. Each re-tile in between lays out a smaller grid, and storing that fit would lose
    // leaves for good (at one window, the whole entry), breaking the spec's promise that a screen with
    // the same bounds gets its sizes back. So for this long after ScreensChanged shows a screen key that was not
    // there before, fits for that key are laid out but not stored (controller ruling). A split moved by hand in
    // that time is still stored: that is the user's own change.
    static readonly TimeSpan ReturningScreenGrace = TimeSpan.FromSeconds(3);

    // The screen keys the last look at the screens found; null until RememberScreens first runs (Start).
    IReadOnlySet<string>? screenKeys;

    // Until when each newly seen screen key is in its grace period.
    readonly Dictionary<string, DateTimeOffset> graceUntil = [];

    // Called on Start (to know what was there) and on every ScreensChanged. A key not seen at the last look starts
    // its grace period; on Start nothing is new, since nothing was there to come back.
    //
    // The bookkeeping runs whether or not anything is tiled (tiling can be switched on inside the grace period),
    // but the trace line only while tiling acts here: with nothing tiled it would claim, on every dock and
    // undock, that sizes are being held back when nothing is being tiled at all (final review, item 6).
    void RememberScreens()
    {
        var keys = surface.Screens().Select(KeyOf).ToHashSet();
        var at = now();
        var tiling = Acting().HasValue;
        if (screenKeys is not null)
            keys.Where(k => !screenKeys.Contains(k)).ToList().ForEach(k =>
            {
                graceUntil[k] = at + ReturningScreenGrace;
                if (tiling)
                    trace?.Invoke($"tile: screen {k} appeared, its sizes are not refitted until {ReturningScreenGrace.TotalSeconds:0}s have passed");
            });
        graceUntil.Keys.Where(k => graceUntil[k] <= at).ToList().ForEach(k => graceUntil.Remove(k));
        screenKeys = keys;
    }

    bool InGrace(string key) => graceUntil.TryGetValue(key, out var until) && now() < until;

    // The key a screen's layout is stored under: its monitor bounds as "L,T,R,B". The bounds rather than the
    // number, so a renumbering after a screen is plugged in never hands one screen's sizes to another. Invariant
    // culture, because a screen left of the main one has negative coordinates and some cultures write the minus
    // sign as another character, which would change the key with the user's regional settings.
    static string KeyOf(TileScreen screen) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{screen.Bounds.Left},{screen.Bounds.Top},{screen.Bounds.Right},{screen.Bounds.Bottom}");

    // The layout a screen of `count` windows is laid out with. A screen with nothing stored is laid out on the
    // automatic layout for the count (TileLayout.Automatic, the equal grid exactly). A stored layout is fitted to the
    // count now, using the screen's pending leaver; when that changed it (a window joined or left since it was stored)
    // the fitted one is stored, so the next change starts from it.
    //
    // A REFIT NEVER REMOVES AN ENTRY (final review, item 1). A layout that fits to a plain one (a screen down to one
    // window, the most common case being two tiles and one minimized or closed) is laid out but not stored, because
    // SetTileLayout removes the entry for a plain layout and the user's split would be gone when the second window
    // came back. Petre: "keep my sizes". The entry keeps its last real shape, and only "Reset to grid", "Even out tiles"
    // on the automatic shape (evened, it is plain), untile, deleting the workspace or a hand drag back to exactly equal
    // (MovedASplit) ever remove it.
    //
    // A fit that is NOT plain is stored, stored layout or not. With nothing stored and two windows gone at once the
    // fit drops the last leaves of the automatic layout (TileArrange.Refit), which is a custom shape (four down to
    // two leaves W1 over W2, not two columns); not stored, the next re-tile would start from nothing and flip the
    // screen to the automatic layout (task 3's review; Two_tiles_leaving_at_once_keep_the_fitted_shape_on_the_next_retile).
    //
    // Zero windows is answered with no fit at all, without touching the stored layout or the pending leaver: there is
    // no grid to lay out (OnSettled asks about the screen a window settled on, which may hold no tiles). Nor is a fit
    // stored while the screen is in its grace period after coming back (see ReturningScreenGrace): it is laid out
    // from the stored layout, which stays as it was.
    //
    // It has side effects (it takes the pending leaver, and may store), so a caller asks it once per layout and
    // passes the answer on rather than asking again (see OnSettled).
    TileLayout LayoutOn(Workspace workspace, TileScreen screen, int count)
    {
        if (count == 0) return TileLayout.Automatic(0);

        var pending = leavers.Remove((workspace.Id, screen.Number), out var leaver)
            ? Maybe.From(leaver)
            : Maybe<Leaver>.None;
        var key = KeyOf(screen);
        // Read from the manager, not from `workspace`: that record was taken when the event arrived, and a split
        // moved in this same handler has to be laid out with the layout it just stored.
        var stored = StoredOn(workspace.Id, key);
        // Nothing stored means the automatic layout. With a leaver pending, that is the automatic layout of the count
        // the leaver's leaf was read from, and it loses only the leaver's leaf (Petre: "on closing, i sometimes want
        // not to move other windows"), the same rule Refit applies to a stored layout. A leaver whose screen's count did
        // not drop (a cross-screen swap) is no leaver at all (final review, C1). TileArrange.FitStart has the rule.
        var (start, leaving) = TileArrange.FitStart(stored, count, pending.Map(l => l.Leaf),
            pending.Map(l => l.OldCount).GetValueOrDefault(count));

        // KEEP THE SPACE (change 3): a tile that CLOSED on a workspace with the switch on leaves its leaf as the
        // screen's opening, and no other tile moves. Only when exactly that one window left (final review, M1: with two
        // gone at once the start is a layout nobody saw with this count) and the start describes exactly the layout
        // before the close (one leaf per window then, no opening yet), which is the layout the leaver's leaf was read
        // from. A screen that already has its one opening closes up as in change 1: Refit's `count + 2` branch takes
        // only the second leaver's leaf and the first opening stays. Not in a returning screen's grace period, where
        // nothing LayoutOn makes is stored: an opening laid out but not stored would vanish at the next re-tile, so the
        // close takes the change 1 path there, as it would have before the switch existed.
        if (pending.TryGetValue(out var left) && left.Closed && left.OldCount == count + 1 && start.Opening is null
            && start.Count == count + 1 && !InGrace(key)
            && TileArrange.KeptOpen(start, left.Leaf) is var kept && kept.HasOpeningFor(count))
        {
            StoreLayout(workspace, key, kept)
                .Tap(() => trace?.Invoke($"tile: {workspace.Name} screen {screen.Number}: a tile closed, its space kept at slot {kept.Opening}, sizes {Show(kept)}"))
                .TapError(error => trace?.Invoke($"tile: {workspace.Name} screen {screen.Number}: storing the kept space failed: {error}"));
            return kept;
        }

        // Refit keeps a shape while windows leave (the leaver's leaf, else the last leaves) and while they join (the
        // last leaf split at the bottom). Stored unless plain: even splits on the automatic shape are what nothing
        // stored means already, but even splits on a custom shape are not.
        var fitted = TileArrange.Refit(start, count, leaving);
        if (fitted != start && !TileArrange.IsPlain(fitted) && !InGrace(key))
            StoreLayout(workspace, key, fitted)
                .Tap(() => trace?.Invoke($"tile: {workspace.Name} screen {screen.Number}: sizes refitted to {count} windows, {Show(fitted)}"))
                .TapError(error => trace?.Invoke($"tile: {workspace.Name} screen {screen.Number}: storing refitted sizes failed: {error}"));
        return fitted;
    }

    // Every store the Tiler makes itself goes through here. SetTileLayout persists and pulses StateChanged, and the
    // stored layouts are in the Tiler's StateKey, so without the second step that pulse would run a full evaluate
    // (a COM WorkspaceOf for every known window) that places nothing (final review, item 2). So after a successful
    // store the key's Layouts are brought up to date, and the pulse compares equal and returns early.
    //
    // Only the Layouts part of the key is refreshed, not the whole key: any OTHER fact that moved (tiling switched
    // off, the gap changed, while a placement pumped messages) must still make its own pulse evaluate. A key that
    // is None (a handler threw, see Guarded) stays None, so the next pulse still repairs everything.
    Result StoreLayout(Workspace workspace, string key, TileLayout layout) =>
        manager.SetTileLayout(workspace.Id, key, layout)
            .Tap(() => stateKey = stateKey.Map(k => k with { Layouts = CurrentStateKey().Layouts }));

    // Called by Keep just before `after` replaces the workspace's board. For each screen of the old board that
    // lost a window and still has some, the first leaver's leaf (by its slot in the old order) becomes that
    // screen's pending leaver. Refit can use one leaver per change; when several windows go at once the last leaves
    // go instead, which is Refit's documented fallback. A screen left empty forgets its leaver,
    // because no fit follows (RetileScreen skips an empty screen) and a stale one must not meet a later grid. It
    // loses its stored opening too, for the same reason (CloseOpeningOfEmptied).
    // A brand-new board (none before: first fill, or tiling turned back on) starts with no leavers at all.
    //
    // The entry also says whether that first leaver was closing (in `closing`, so its workspace keeps the space),
    // and the set is cleared here, whichever way this board change went. And it carries the screen's count before
    // the change, so a screen that lost one window and gained another (a cross-screen swap) is not fitted as if it
    // had shrunk (final review, C1).
    //
    // It also forgets the pending echo of a window that is on no grid any more, so a stale one cannot swallow a
    // drag after the window comes back without being placed.
    void NoteLeavers(Guid workspace, TileBoard after)
    {
        if (boards.TryGetValue(workspace, out var before))
            before.Screens.ToList().ForEach(s =>
            {
                var staying = after.On(s.Key);
                var gone = s.Value.Select((w, slot) => (Window: w, Slot: slot)).Where(p => !staying.Contains(p.Window)).ToList();
                if (staying.Count == 0)
                {
                    leavers.Remove((workspace, s.Key));
                    if (gone.Count > 0) CloseOpeningOfEmptied(workspace, s.Key);
                }
                else if (gone.Count > 0)
                    leavers[(workspace, s.Key)] = new Leaver(LeafOn(workspace, s.Key, gone[0].Slot, s.Value.Count),
                        closing.Contains(gone[0].Window), s.Value.Count);
            });
        else
            leavers.Keys.Where(k => k.Workspace == workspace).ToList().ForEach(k => leavers.Remove(k));
        // Read once, by this board change, whatever it turned out to be (see `closing`).
        closing.Clear();

        var gridded = boards.Where(b => b.Key != workspace).Select(b => b.Value).Append(after)
            .SelectMany(b => b.Screens.Values).SelectMany(w => w).ToHashSet();
        awaitingEcho.RemoveWhere(w => !gridded.Contains(w));
    }

    // Fits every screen of `stayed` that has a leaver pending, so the leave is settled (its leaf gone, or kept as the
    // screen's opening) and stored BEFORE anything arrives. Evaluate calls it when windows left and arrived in one
    // change (final review, C2); the arrivals then read this layout (ShownLayout, the opening) as the grid the user
    // sees. The fits themselves are dropped: LayoutOn stores what it made, and the re-tile after the arrivals lays out
    // whatever is stored then. A screen the surface no longer reports, or one left empty, has nothing to fit.
    void SettleLeaves(Workspace workspace, TileBoard stayed)
    {
        var screens = surface.Screens().ToDictionary(s => s.Number);
        leavers.Keys.Where(k => k.Workspace == workspace.Id).Select(k => k.Screen).ToList()
            .Where(n => screens.ContainsKey(n) && stayed.On(n).Count > 0)
            .ToList()
            .ForEach(n => LayoutOn(workspace, screens[n], stayed.On(n).Count));
    }

    // A screen whose last tile just left keeps no opening (review finding on change 3; the design: "the last tile on
    // a screen closing leaves nothing to keep"). An opening is room kept beside OTHER tiles. On an empty screen
    // nothing can fill it: IntoOpenings and Arrived look for an opening for the windows that stayed, which is
    // HasOpeningFor(0) and false for any opening with tiles around it. The next window then joins, and Refit keeps
    // the stored layout as it is, because it does have an opening for one window. So it would be laid out beside
    // an empty leaf. This happens with the switch on (two tiles, both close, the first keeps its space) and with an
    // opening pulled open by hand (TileArrange.Open) when the screen's last tile leaves.
    //
    // Only the opening goes (TileArrange.Closed): any split dragged on that screen stays for the windows that come
    // back. Stored through StoreLayout like every store the Tiler makes. If what is left is plain, SetTileLayout
    // removes the entry. Not during a returning screen's grace period, where nothing is stored (see
    // ReturningScreenGrace), and not for a screen the surface no longer reports: a screen that was unplugged keeps
    // its layout for when it comes back.
    void CloseOpeningOfEmptied(Guid id, int number)
    {
        if (!manager.State.Workspaces.TryFirst(w => w.Id == id).TryGetValue(out var workspace)
            || !surface.Screens().TryFirst(s => s.Number == number).TryGetValue(out var screen))
            return;
        var key = KeyOf(screen);
        if (InGrace(key) || !StoredOn(id, key).TryGetValue(out var stored) || stored.Opening is null) return;
        var closed = TileArrange.Closed(stored);
        StoreLayout(workspace, key, closed)
            .Tap(() => trace?.Invoke($"tile: {workspace.Name} screen {number}: emptied, its opening closed, sizes {Show(closed)}"))
            .TapError(error => trace?.Invoke($"tile: {workspace.Name} screen {number}: storing the emptied screen's closed opening failed: {error}"));
    }

    // Called by PlaceIfNeeded after a successful Place. The Place raises LOCATIONCHANGE, so a Settled for this
    // window follows, and that one is ours. A Place that moved nothing (the window refused it outright and its
    // frame is what it was) raises no LOCATIONCHANGE and has no echo to wait for.
    void AwaitEcho(WindowHandle window, Maybe<WindowRect> before, WindowRect accepted)
    {
        if (before.HasValue && before.Value == accepted) return;
        awaitingEcho.Add(window);
    }

    // Is this Settled the echo of our own Place? Asked once, first thing in OnSettled, and the answer is taken.
    bool EchoOfOurPlace(WindowHandle window) => awaitingEcho.Remove(window);

    // OnSettled's step 6. True when this Settled was a resize by hand and has been dealt with here: a split moved
    // and the screen re-tiled, or (no moved edge faces a neighbour) the window put back into its slot, through
    // PutBack so the backstop counts it. False for everything else, which goes on to the move rules unchanged:
    //   - the echo of our own Place (ruling 2), a window with no accepted frame yet, one off its own screen (a
    //     resize that crossed onto another screen is a move to that screen), or one still settling;
    //   - SettledResize.Classify finding a move rather than a resize, or no button down during the change.
    //
    // `layout` is the screen's fit that OnSettled already laid `slots` out with (None when the screen is unknown).
    // It is passed in, never asked for again (final review, item 4): LayoutOn takes the pending leaver, so a second
    // call could fit differently (dropping the last leaf instead of the leaver's) and the split would be moved on
    // a layout the user never saw.
    //
    // On the tree a dragged edge moves the split it belongs to (SplitMove: the nearest split of that edge's axis, up
    // from the tile's leaf, with a neighbour on that side). In Petre's A and C side by side over B, A's bottom edge
    // moves the boundary between the top pair and B, and C's bottom moves with it: they are one line on screen.
    bool MovedASplit(Workspace workspace, WindowHandle window, WindowRect frame, int screenNow,
        IReadOnlyDictionary<int, TileScreen> screens, TileBoard board,
        IReadOnlyDictionary<WindowHandle, WindowRect> slots, Maybe<TileLayout> layout, bool echo)
    {
        if (echo
            || surface.IsSettling(window)
            || !given.TryGetValue(window, out var last)
            || !slots.TryGetValue(window, out var slot)
            || !screens.TryGetValue(screenNow, out var screen)
            || !layout.TryGetValue(out var shown)
            || !board.ScreenOf(window).Map(home => home == screenNow).GetValueOrDefault(false))
            return false;

        var moved = SettledResize.Classify(last.Accepted, frame, surface.ByHand(window));
        if (moved == Edges.None) return false;

        var windows = board.On(screenNow);
        // The window's leaf: its slot, counted past the opening when the layout has one for these windows.
        var leaf = TileArrange.LeafOf(shown, windows.ToList().IndexOf(window), windows.Count);

        // Pulled back over the screen's opening: the room goes back to the tiles and the opening closes.
        if (TileArrange.Closes(shown, windows.Count, screen.WorkArea, GapOn(screen), leaf, moved, frame))
        {
            var closed = TileArrange.Closed(shown);
            StoreLayout(workspace, KeyOf(screen), closed)
                .Tap(() => trace?.Invoke($"tile: {workspace.Name} screen {screen.Number}: {window.Value:X} pulled its {moved} edge back over the opening, closed, sizes {Show(closed)}"))
                .TapError(error => trace?.Invoke($"tile: {workspace.Name} screen {screen.Number}: storing the closed opening failed: {error}"));
            Retile(workspace, [screenNow]);
            return true;
        }

        SplitMove.Apply(shown, screen.WorkArea, GapOn(screen), leaf, moved, frame).Match(
            split =>
            {
                // A CORNER can be half inner, half outer: the top-right corner of a full-height left tile moves the
                // column split with its right edge, while its top edge faces the screen's edge. SplitMove ignores
                // that outer edge, so on its own the tile went back to full height. Petre: "resizing a tiled window
                // with the corner doesn't currently change height or width of the window; it should". So the outer
                // edge is offered to TileArrange.Open on the moved split, as it would be had it moved alone: a pull
                // deep enough leaves an opening, a nudge (or a screen that has its opening already) changes nothing.
                // Open only ever acts on an edge that faces the screen's edge, so passing the whole `moved` is safe:
                // the inner edge SplitMove took is not reconsidered. The split changes shares only, never the tree's
                // structure, so `leaf` still names the dragged tile in it.
                var next = TileArrange.Open(split, windows.Count, screen.WorkArea, GapOn(screen), leaf, moved, frame)
                    .GetValueOrDefault(split);
                // A drag held against the 10% clamp, or one that comes out at the split there already, changes
                // nothing: no store (final review, item 3). The re-tile below still runs and puts the window back.
                if (next == shown)
                    trace?.Invoke($"tile: {workspace.Name} screen {screen.Number}: {window.Value:X} resized by its {moved} edge, split unchanged");
                else
                    StoreLayout(workspace, KeyOf(screen), next)
                        .Tap(() => trace?.Invoke($"tile: {workspace.Name} screen {screen.Number}: {window.Value:X} resized by its {moved} edge, sizes {Show(next)}"))
                        .TapError(error => trace?.Invoke($"tile: {workspace.Name} screen {screen.Number}: storing the moved split failed: {error}"));
                // One placement per window whose slot changed; the resized window is usually in its new slot already.
                Retile(workspace, [screenNow]);
            },
            // Only outer edges moved: there is no split behind them. The spec snapped the tile back; now the room it
            // gave up stays open for the next window (TileArrange.Open). Petre: "if i resize a window in a tiled
            // setting, i want to keep the opening for the new window to jump into". Measured first: pulling the top
            // edge of a full-height right tile down to y 911 and 735 snapped it back to the top both times.
            // A nudge, or a screen that already has its one opening, still snaps back. On the tree the opening is made
            // beside the pulled tile only, not as a whole new column (TileArrange.Open; the split tree design changes
            // that on purpose: "only the tile you touch changes").
            () => TileArrange.Open(shown, windows.Count, screen.WorkArea, GapOn(screen), leaf, moved, frame).Match(
                opened =>
                {
                    StoreLayout(workspace, KeyOf(screen), opened)
                        .Tap(() => trace?.Invoke($"tile: {workspace.Name} screen {screen.Number}: {window.Value:X} pulled its {moved} edge in, opening at slot {opened.Opening}, sizes {Show(opened)}"))
                        .TapError(error => trace?.Invoke($"tile: {workspace.Name} screen {screen.Number}: storing the opening failed: {error}"));
                    Retile(workspace, [screenNow]);
                },
                () => PutBack(window, slot, frame)));
        return true;
    }

    // A layout for the trace: the tree in the text form the tests use, "A[L 0.5, D[L 0.5, L 0.5] 0.5]" (A splits
    // across, D down, L is a leaf, each child followed by its share to three places), then the opening's leaf.
    static string Show(TileLayout layout) =>
        ShowNode(layout.Root) + (layout.Opening is { } open ? $" opening {open}" : "");

    static string ShowNode(TileNode node) =>
        node is TileBranch b
            ? $"{(b.Axis == Axis.Across ? "A" : "D")}[{string.Join(", ", b.Children.Select(c => $"{ShowNode(c.Node)} {c.Share.ToString("0.###", CultureInfo.InvariantCulture)}"))}]"
            : "L";
}
