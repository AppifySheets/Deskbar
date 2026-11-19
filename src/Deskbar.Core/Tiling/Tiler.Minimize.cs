using CSharpFunctionalExtensions;
using Deskbar.Core.Abstractions;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Tiling;

// A MINIMIZED TILE COMES BACK TO ITS PLACE. Petre, testing tiling live: "minimizing and maximizing windows shouldn't
// change their position, if possible. i'd like to bring back the window from the top back to the top", "after it
// restores". A minimize still gives the slot up (the spec: the grid closes up), but a restore used to JOIN, at the
// end of the order, so the top tile came back at the bottom.
//
// So the moment a tile is minimized, its screen's order and stored layout are remembered, and so is its SPOT in the
// split tree (TileArrange.SpotOf): the windows of the subtree next to it in its parent split, which side of them it
// was on, and its share of that pair. When it comes back (docs/superpowers/specs/2026-11-19-tiling-split-tree-design.md,
// "Arrival and minimize"):
//   * the screen still holds exactly the same other windows: the order and the layout come back as they were, so a
//     top tile is on top again, and a stack made by a drop is a stack again. On the split tree that is the whole
//     tree: minimizing C in Across[Down[A, B], C] and restoring it gives the same layout back, never C at half height;
//   * the screen changed meanwhile (a window opened or closed there): it goes back BESIDE WHAT IT WAS NEXT TO
//     (TileArrange.Returned). The largest subtree of today's tree whose windows are all among the remembered ones is
//     split on the remembered side, the window taking its remembered share, and only that subtree changes size. So C
//     above comes back as a full-height column while A over B is still a stack, and beside A alone once the stack
//     was broken up. This replaces the grid's "old column still there" rule and keeps its intent;
//   * none of the windows it was next to is on the screen any more (or it had none: it was alone): it ARRIVES as a
//     new window does (Tiler.Arrival.cs: an opening, else a split of the tile last used or the largest). That
//     fallback is the only return that goes through the arrival rule; a return beside its neighbours is never offered
//     the tile last used (task 5 review);
//   * every other window of the screen has gone: it takes the screen, as any lone window does.
// A tile coming back is placed BEFORE any opening is filled and is never pulled into one (the design's precedence,
// step 1; final review, I2): Evaluate hands these windows to Keep, whose IntoOpenings leaves them alone, and a return
// beside its neighbours keeps the opening open (Returned moves it on with the new leaf), so a kept space stays for the
// next new window. Only the arrival fallback takes an opening, as an arrival does.
// Maximizing already keeps the slot (OnSettled puts a restored window straight back into it).
public sealed partial class Tiler
{
    // `Layout` is what was stored for the screen (None: nothing, the automatic layout), put back as it was when the
    // screen did not change. `Spot` is the window's place in the layout the screen SHOWED (stored or automatic), read
    // when the screen did change; None when it had nothing beside it.
    sealed record Away(Guid Workspace, int Screen, IReadOnlyList<WindowHandle> Order, Maybe<TileLayout> Layout,
        Maybe<TileArrange.ReturnSpot> Spot);

    readonly Dictionary<WindowHandle, Away> away = [];

    // Called on MinimizeChanged BEFORE the evaluate that takes the window out of the grid, so the board and the
    // layout still hold it.
    void NoteMinimized(WindowHandle window)
    {
        if (!surface.IsMinimized(window) || !Acting().TryGetValue(out var workspace) || !Board.ScreenOf(window).TryGetValue(out var screen))
            return;
        var order = Board.On(screen);
        var here = surface.Screens().TryFirst(s => s.Number == screen);
        var layout = here.Bind(s => StoredOn(workspace.Id, KeyOf(s)));
        var spot = here.Bind(s => TileArrange.SpotOf(order, ShownLayout(workspace, s, order.Count), window));
        away[window] = new Away(workspace.Id, screen, order, layout, spot);
    }

    // Is `window`, new to the board on `screen`, a minimized tile coming back to the workspace and the screen it left?
    // Asked by Evaluate before anything is placed. One that comes back somewhere else has no place to go back to and
    // arrives like any new window (Evaluate forgets its entry), since nothing appends to the end of a screen any more.
    bool ComesBack(WindowHandle window, Workspace workspace, Maybe<int> screen) =>
        away.TryGetValue(window, out var gone) && gone.Workspace == workspace.Id && screen == Maybe.From(gone.Screen);

    // Called by Evaluate with the board as the leaves left it (`stayed`: no newcomer on it yet, every leave already
    // settled), before any new window arrives. Each window in `returning` (ComesBack said yes) is put back in its
    // place, one at a time, so a later one sees the earlier ones where they went. The layout each return makes is
    // stored BEFORE the re-tile reads it.
    TileBoard ReturnedToTheirPlaces(Workspace workspace, TileBoard stayed, IReadOnlyList<WindowHandle> returning) =>
        returning.Aggregate(stayed, (current, w) =>
        {
            var gone = away[w];
            away.Remove(w);
            var others = current.On(gone.Screen);
            if (others.SequenceEqual(gone.Order.Where(x => x != w)))
            {
                // The same screen it left: everything as it was, the layout included (stored BEFORE the re-tile reads
                // it; a layout that was never stored is put back as "nothing stored", which a plain layout means).
                RestoreLayout(workspace, gone);
                trace?.Invoke($"tile: {w.Value:X} came back to its place on screen {gone.Screen}");
                return current.Join(gone.Screen, w).Reordered(gone.Screen, gone.Order);
            }

            // Every other window of its screen has gone (or the screen is no longer reported): nothing to keep in
            // place, it takes the screen as any lone window does.
            if (others.Count == 0 || !surface.Screens().TryFirst(s => s.Number == gone.Screen).TryGetValue(out var screen))
            {
                trace?.Invoke($"tile: {w.Value:X} came back to screen {gone.Screen}, which has no other tile of the ones it left");
                return current.Join(gone.Screen, w);
            }

            // The screen changed meanwhile: beside what it was next to, when any of that is still here.
            return gone.Spot
                .Bind(spot => BesideItsNeighbours(workspace, screen, current, w, spot))
                .GetValueOrDefault(() =>
                {
                    // Nothing it was next to is on the screen: it arrives as a new window does, on the board at its
                    // screen and then placed by Arrived, which fills an opening or splits a tile and stores the layout.
                    trace?.Invoke($"tile: {w.Value:X} came back to screen {gone.Screen}, which changed meanwhile and holds none of its old neighbours, and arrives as a new window");
                    return Arrived(workspace, current.Join(gone.Screen, w), [w]);
                });
        });

    // TileArrange.Returned on the layout the screen shows now (its windows are `current`'s, the leaves already
    // settled), the new layout stored first. None when no remembered neighbour is on the screen, or when the board
    // would not take the new order (it always does, by construction; the window must never be lost for it).
    Maybe<TileBoard> BesideItsNeighbours(Workspace workspace, TileScreen screen, TileBoard current, WindowHandle window,
        TileArrange.ReturnSpot spot)
    {
        var others = current.On(screen.Number);
        return TileArrange.Returned(others, ShownLayout(workspace, screen, others.Count), window, spot)
            .Map(returned => (returned.Layout, Board: current.Into(screen.Number, returned.Order)))
            .Where(r => r.Board.ScreenOf(window).HasValue)
            .Map(r =>
            {
                StoreLayout(workspace, KeyOf(screen), r.Layout)
                    .Tap(() => trace?.Invoke($"tile: {window.Value:X} came back beside {string.Join(" ", spot.Siblings.Where(others.Contains).Select(s => s.Value.ToString("X")))} ({spot.Side}) on screen {screen.Number}, which changed meanwhile, sizes {Show(r.Layout)}"))
                    .TapError(error => trace?.Invoke($"tile: {window.Value:X} came back to screen {screen.Number}, storing its place failed: {error}"));
                return r.Board;
            });
    }

    void RestoreLayout(Workspace workspace, Away gone) =>
        surface.Screens().TryFirst(s => s.Number == gone.Screen).Tap(screen =>
            StoreLayout(workspace, KeyOf(screen), gone.Layout.GetValueOrDefault(TileLayout.Automatic(gone.Order.Count)))
                .TapError(error => trace?.Invoke($"tile: restoring the layout of screen {gone.Screen} failed: {error}")));
}
