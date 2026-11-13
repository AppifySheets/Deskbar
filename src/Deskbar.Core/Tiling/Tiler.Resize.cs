using System.Globalization;
using CSharpFunctionalExtensions;
using Deskbar.Core.Abstractions;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Tiling;

// Resizable tiles: the Tiler's wiring for docs/superpowers/specs/2026-11-08-tile-resize-design.md. Petre: "i want
// to be able to resize tiles by dragging the separation", "keep my sizes", and sizes belong to "places", not to
// windows. The arithmetic lives in pure classes (TileSizes.Fit, SettledResize, SplitMove, the sized
// GridLayout.Arrange); this file only decides when to ask them and what to do with the answer. Tiler.cs calls in
// from four places:
//   - SlotsOf asks SizesOn for a screen's sizes, fitted to the number of windows it now holds;
//   - Keep calls NoteLeavers before it replaces a board, so the next fit knows which cell a leaving window held;
//   - OnSettled asks MovedASplit before the move rules, and EchoOfOurPlace first of all;
//   - PlaceIfNeeded calls AwaitEcho after a Place, so the Settled that Place causes is never read as a drag.
//
// Everything here runs inside the Tiler's serialised Run, under the same gate as the rest (OnSettled reaches
// MovedASplit only while TilingGate acts, the window is on the board and no bar drop is pending). Storing sizes
// goes through StoreSizes, which calls WorkspaceManager.SetTileSizes (that pulses StateChanged) and then takes the
// new sizes into the Tiler's StateKey as already seen. The sizes are in the key so that "Even out tiles", untile
// and the pipe's `even` re-tile; the Tiler's own store must not, because the re-tile it would cause is a full
// evaluate (WorkspaceOf, a COM call, for every known window) that places nothing (final review, item 2).
public sealed partial class Tiler
{
    // The cell (in the grid as it was BEFORE) of a window that left a screen's grid, per workspace and screen
    // number: TileSizes.Fit gives that row's share to the rows around it, where without it the bottom row would
    // go. Used by the next fit of that screen and then forgotten (SizesOn takes it).
    readonly Dictionary<(Guid Workspace, int Screen), (int Column, int Row)> leavers = [];

    // Windows the Tiler has placed whose own Settled has not come back yet (controller ruling 2). That Settled
    // ends a change that Deskbar's SetWindowPos began, so even when the surface says a button was down during it
    // (ByHand reads the global mouse state, and a button can be held for something else entirely, while the app
    // adjusts itself after our Place) it is not a drag and never moves a split. Set by AwaitEcho, taken by the
    // window's next Settled whatever that Settled turns out to be, and dropped when the window leaves every grid.
    readonly HashSet<WindowHandle> awaitingEcho = [];

    // A screen that comes back (or appears) gets its windows back ONE AT A TIME: Windows moves them, and each
    // joins by its own Settled. Each re-tile in between lays out a smaller grid, and storing that fit would lose
    // columns and rows for good (at one window, the whole entry), breaking the spec's promise that a screen with
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
    // but the trace line only while tiling acts here: with the experiment off it would claim, on every dock and
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

    // The key a screen's sizes are stored under: its monitor bounds as "L,T,R,B". The bounds rather than the
    // number, so a renumbering after a screen is plugged in never hands one screen's sizes to another. Invariant
    // culture, because a screen left of the main one has negative coordinates and some cultures write the minus
    // sign as another character, which would change the key with the user's regional settings.
    static string KeyOf(TileScreen screen) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{screen.Bounds.Left},{screen.Bounds.Top},{screen.Bounds.Right},{screen.Bounds.Bottom}");

    // The sizes a screen's grid of `count` windows is laid out with. A screen with nothing stored is equal (and
    // the sized Arrange with equal sizes is the equal grid exactly, so nothing changes for it). Stored sizes are
    // fitted to the grid's shape now, using the screen's pending leaver; when that changed them (a window joined
    // or left since they were stored) the fitted ones are stored, so the next change starts from them.
    //
    // A REFIT NEVER REMOVES AN ENTRY (final review, item 1). Sizes that fit to equal (a screen down to one window,
    // the most common case being two tiles and one minimized or closed) are laid out but not stored, because
    // SetTileSizes removes the entry for equal sizes and the user's split would be gone when the second window
    // came back. Petre: "keep my sizes". The entry keeps its last real shape, and only "Even out tiles", untile,
    // deleting the workspace or a hand drag back to exactly equal (MovedASplit) ever remove it.
    //
    // Zero windows is answered with no fit at all, without touching the stored sizes or the pending leaver: there is
    // no grid to lay out (OnSettled asks about the screen a window settled on, which may hold no tiles). Nor is a fit
    // stored while the screen is in its grace period after coming back (see ReturningScreenGrace): it is laid out
    // from the stored sizes, which stay as they were.
    //
    // It has side effects (it takes the pending leaver, and may store), so a caller asks it once per layout and
    // passes the answer on rather than asking again (see OnSettled).
    TileSizes SizesOn(Workspace workspace, TileScreen screen, int count)
    {
        var shape = GridLayout.Shape(count);
        if (count == 0) return TileSizes.Equal(shape);

        var leaving = leavers.Remove((workspace.Id, screen.Number), out var cell)
            ? Maybe.From(cell)
            : Maybe<(int Column, int Row)>.None;
        var key = KeyOf(screen);
        // Read from the manager, not from `workspace`: that record was taken when the event arrived, and a split
        // moved in this same handler has to be laid out with the sizes it just stored.
        var stored = manager.State.Workspaces.TryFirst(w => w.Id == workspace.Id)
            .Bind(w => w.TileSizes.TryFirst(s => s.Screen == key))
            .Map(s => s.Sizes);
        if (stored.HasNoValue) return TileSizes.Equal(shape);

        var fitted = stored.Value.Fit(shape, leaving);
        if (fitted != stored.Value && !fitted.IsEqual && !InGrace(key))
            StoreSizes(workspace, key, fitted)
                .Tap(() => trace?.Invoke($"tile: {workspace.Name} screen {screen.Number}: sizes refitted to {count} windows, {Show(fitted)}"))
                .TapError(error => trace?.Invoke($"tile: {workspace.Name} screen {screen.Number}: storing refitted sizes failed: {error}"));
        return fitted;
    }

    // Every store the Tiler makes itself goes through here. SetTileSizes persists and pulses StateChanged, and the
    // stored sizes are in the Tiler's StateKey, so without the second step that pulse would run a full evaluate
    // (a COM WorkspaceOf for every known window) that places nothing (final review, item 2). So after a successful
    // store the key's Sizes are brought up to date, and the pulse compares equal and returns early.
    //
    // Only the Sizes part of the key is refreshed, not the whole key: any OTHER fact that moved (tiling switched
    // off, the gap changed, while a placement pumped messages) must still make its own pulse evaluate. A key that
    // is None (a handler threw, see Guarded) stays None, so the next pulse still repairs everything.
    Result StoreSizes(Workspace workspace, string key, TileSizes sizes) =>
        manager.SetTileSizes(workspace.Id, key, sizes)
            .Tap(() => stateKey = stateKey.Map(k => k with { Sizes = CurrentStateKey().Sizes }));

    // Called by Keep just before `after` replaces the workspace's board. For each screen of the old board that
    // lost a window and still has some, the first leaver's cell (by its slot in the old order) becomes that
    // screen's pending leaver. TileSizes.Fit can use one leaver per change; when several windows go at once the
    // rest are taken from the bottom, which is Fit's documented fallback. A screen left empty forgets its leaver,
    // because no fit follows (RetileScreen skips an empty screen) and a stale one must not meet a later grid.
    // A brand-new board (none before: first fill, or tiling turned back on) starts with no leavers at all.
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
                    leavers.Remove((workspace, s.Key));
                else if (gone.Count > 0)
                    leavers[(workspace, s.Key)] = GridLayout.CellOf(gone[0].Slot, GridLayout.Shape(s.Value.Count));
            });
        else
            leavers.Keys.Where(k => k.Workspace == workspace).ToList().ForEach(k => leavers.Remove(k));

        var gridded = boards.Where(b => b.Key != workspace).Select(b => b.Value).Append(after)
            .SelectMany(b => b.Screens.Values).SelectMany(w => w).ToHashSet();
        awaitingEcho.RemoveWhere(w => !gridded.Contains(w));
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
    // `sizes` is the screen's fit that OnSettled already laid `slots` out with (None when the screen is unknown).
    // It is passed in, never asked for again (final review, item 4): SizesOn takes the pending leaver, so a second
    // call could fit differently (dropping the bottom row instead of the leaver's) and the split would be moved on
    // a layout the user never saw.
    bool MovedASplit(Workspace workspace, WindowHandle window, WindowRect frame, int screenNow,
        IReadOnlyDictionary<int, TileScreen> screens, TileBoard board,
        IReadOnlyDictionary<WindowHandle, WindowRect> slots, Maybe<TileSizes> sizes, bool echo)
    {
        if (echo
            || surface.IsSettling(window)
            || !given.TryGetValue(window, out var last)
            || !slots.TryGetValue(window, out var slot)
            || !screens.TryGetValue(screenNow, out var screen)
            || !sizes.TryGetValue(out var shown)
            || !board.ScreenOf(window).Map(home => home == screenNow).GetValueOrDefault(false))
            return false;

        var moved = SettledResize.Classify(last.Accepted, frame, surface.ByHand(window));
        if (moved == Edges.None) return false;

        var windows = board.On(screenNow);
        var shape = GridLayout.Shape(windows.Count);
        var cell = GridLayout.CellOf(windows.ToList().IndexOf(window), shape);

        SplitMove.Apply(shown, shape, screen.WorkArea, GapOn(screen), cell, moved, frame).Match(
            next =>
            {
                // A drag held against the 10% clamp, or one that comes out at the split there already, changes
                // nothing: no store (final review, item 3). The re-tile below still runs and puts the window back.
                if (next == shown)
                    trace?.Invoke($"tile: {workspace.Name} screen {screen.Number}: {window.Value:X} resized by its {moved} edge, split unchanged");
                else
                    StoreSizes(workspace, KeyOf(screen), next)
                        .Tap(() => trace?.Invoke($"tile: {workspace.Name} screen {screen.Number}: {window.Value:X} resized by its {moved} edge, sizes {Show(next)}"))
                        .TapError(error => trace?.Invoke($"tile: {workspace.Name} screen {screen.Number}: storing the moved split failed: {error}"));
                // One placement per window whose slot changed; the resized window is usually in its new slot already.
                Retile(workspace, [screenNow]);
            },
            // Only outer edges moved: there is no split behind them (spec: "dragging it snaps back, as today").
            () => PutBack(window, slot, frame));
        return true;
    }

    // Sizes for the trace: column fractions, then each column's row fractions, to three places.
    static string Show(TileSizes sizes) =>
        string.Create(CultureInfo.InvariantCulture,
            $"columns {Fractions(sizes.Columns)} rows {string.Join(" ", sizes.Rows.Select(Fractions))}");

    static string Fractions(IReadOnlyList<double> fractions) =>
        "[" + string.Join(",", fractions.Select(f => f.ToString("0.###", CultureInfo.InvariantCulture))) + "]";
}
