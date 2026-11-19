using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Tiling;

// A MINIMIZED TILE COMES BACK TO ITS PLACE. Petre, testing tiling live: "minimizing and maximizing windows shouldn't
// change their position, if possible. i'd like to bring back the window from the top back to the top", "after it
// restores". A minimize still gives the slot up (the spec: the grid closes up), but a restore used to JOIN, at the
// end of the order, so the top tile came back at the bottom.
//
// So the moment a tile is minimized, its screen's order and stored layout are remembered. When it comes back:
//   * the screen still holds exactly the same other windows: the order and the layout come back as they were, so a
//     top tile is on top again, and a stack made by a drop is a stack again. On the split tree that is the whole
//     tree: minimizing C in Across[Down[A, B], C] and restoring it gives the same layout back, never C at half height
//     (docs/superpowers/specs/2026-11-19-tiling-split-tree-design.md, "Arrival and minimize");
//   * the screen changed meanwhile (a window opened or closed there): FOR NOW it arrives as a new window does
//     (Tiler.Arrival.cs: an opening, else a split of the tile last used or the largest). The grid version put its
//     cell back into its old column (final review, I1); the tree's rule, beside the subtree it was next to
//     (TileArrange.SpotOf and TileArrange.Returned), is wired by task 6 of the split tree plan.
// A tile coming back is placed BEFORE any opening is filled and is never pulled into one (the design's precedence,
// step 1; final review, I2): Evaluate hands these windows to Keep, whose IntoOpenings leaves them alone, so a kept
// space stays for the next new window. (Until task 6, a tile coming back to a CHANGED screen arrives, and an arrival
// does take an opening.)
// Maximizing already keeps the slot (OnSettled puts a restored window straight back into it).
public sealed partial class Tiler
{
    sealed record Away(Guid Workspace, int Screen, IReadOnlyList<WindowHandle> Order, Maybe<TileLayout> Layout);

    readonly Dictionary<WindowHandle, Away> away = [];

    // Called on MinimizeChanged BEFORE the evaluate that takes the window out of the grid.
    void NoteMinimized(WindowHandle window)
    {
        if (!surface.IsMinimized(window) || !Acting().TryGetValue(out var workspace) || !Board.ScreenOf(window).TryGetValue(out var screen))
            return;
        var layout = surface.Screens().TryFirst(s => s.Number == screen).Bind(s => StoredOn(workspace.Id, KeyOf(s)));
        away[window] = new Away(workspace.Id, screen, Board.On(screen), layout);
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
            if (others.Count == 0 || !surface.Screens().Any(s => s.Number == gone.Screen))
            {
                trace?.Invoke($"tile: {w.Value:X} came back to screen {gone.Screen}, which has no other tile of the ones it left");
                return current.Join(gone.Screen, w);
            }

            // task 6: the screen changed meanwhile. Until the tree's return rule is wired (Away keeps the spot
            // TileArrange.SpotOf reads at minimize, and TileArrange.Returned puts the window back beside the subtree it
            // was next to, falling back to this), the window arrives as a new window does: on the board at its screen,
            // then placed by Arrived, which fills an opening or splits a tile and stores the layout.
            trace?.Invoke($"tile: {w.Value:X} came back to screen {gone.Screen}, which changed meanwhile, and arrives as a new window");
            return Arrived(workspace, current.Join(gone.Screen, w), [w]);
        });

    void RestoreLayout(Workspace workspace, Away gone) =>
        surface.Screens().TryFirst(s => s.Number == gone.Screen).Tap(screen =>
            StoreLayout(workspace, KeyOf(screen), gone.Layout.GetValueOrDefault(TileLayout.Automatic(gone.Order.Count)))
                .TapError(error => trace?.Invoke($"tile: restoring the layout of screen {gone.Screen} failed: {error}")));
}
