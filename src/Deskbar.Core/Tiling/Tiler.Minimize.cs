using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Tiling;

// A MINIMIZED TILE COMES BACK TO ITS PLACE. Petre, testing tiling live: "minimizing and maximizing windows shouldn't
// change their position, if possible. i'd like to bring back the window from the top back to the top", "after it
// restores". A minimize still gives the slot up (the spec: the grid closes up), but a restore used to JOIN, at the
// end of the order, so the top tile came back at the bottom.
//
// So the moment a tile is minimized, its screen's order and stored sizes are remembered. When it comes back:
//   * the screen still holds exactly the same other windows: the order and the sizes come back as they were, so a
//     top tile is on top again, and a stack made by a drop is a stack again;
//   * the screen changed meanwhile (a window opened or closed there): its cell goes back into its old column beside
//     the neighbours it had there, or into a column of its own at its old index when none of them is left
//     (TileArrange.Returned; a tile alone in its column took the column with it), and the order
//     follows the cell. Every other column keeps its width and its rows (final review, I1: it used to go to its old
//     index in the order with the sizes refitted as for a join, which put the new cell at the bottom of the last
//     column and could move a tile from one column to another).
// A tile coming back is placed BEFORE any opening is filled and is never pulled into one (the design's precedence,
// step 1; final review, I2): Evaluate hands these windows to Keep, whose IntoOpenings leaves them alone, so a kept
// space stays for the next new window.
// Maximizing already keeps the slot (OnSettled puts a restored window straight back into it).
public sealed partial class Tiler
{
    sealed record Away(Guid Workspace, int Screen, IReadOnlyList<WindowHandle> Order, Maybe<TileSizes> Sizes);

    readonly Dictionary<WindowHandle, Away> away = [];

    // Called on MinimizeChanged BEFORE the evaluate that takes the window out of the grid.
    void NoteMinimized(WindowHandle window)
    {
        if (!surface.IsMinimized(window) || !Acting().TryGetValue(out var workspace) || !Board.ScreenOf(window).TryGetValue(out var screen))
            return;
        var sizes = surface.Screens().TryFirst(s => s.Number == screen).Bind(s => StoredOn(workspace.Id, KeyOf(s)));
        away[window] = new Away(workspace.Id, screen, Board.On(screen), sizes);
    }

    // Is `window`, new to the board on `screen`, a minimized tile coming back to the workspace and the screen it left?
    // Asked by Evaluate before anything is placed. One that comes back somewhere else has no place to go back to and
    // arrives like any new window (Evaluate forgets its entry), since nothing appends to the end of a screen any more.
    bool ComesBack(WindowHandle window, Workspace workspace, Maybe<int> screen) =>
        away.TryGetValue(window, out var gone) && gone.Workspace == workspace.Id && screen == Maybe.From(gone.Screen);

    // Called by Evaluate with the board as the leaves left it (`stayed`: no newcomer on it yet, every leave already
    // settled), before any new window arrives. Each window in `returning` (ComesBack said yes) is put back in its
    // place, one at a time, so a later one sees the earlier ones where they went. The sizes each return makes are
    // stored BEFORE the re-tile reads them.
    TileBoard ReturnedToTheirPlaces(Workspace workspace, TileBoard stayed, IReadOnlyList<WindowHandle> returning) =>
        returning.Aggregate(stayed, (current, w) =>
        {
            var gone = away[w];
            away.Remove(w);
            var others = current.On(gone.Screen);
            if (others.SequenceEqual(gone.Order.Where(x => x != w)))
            {
                // The same screen it left: everything as it was, sizes included (stored BEFORE the re-tile reads
                // them; a size that was never stored is put back as "nothing stored", which plain sizes mean).
                RestoreSizes(workspace, gone);
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

            // The screen changed meanwhile: its cell goes back where it was (TileArrange.Returned), read from the grid
            // it left (the sizes stored then, or the equal grid, which is what nothing stored meant), into the grid
            // the screen shows now. Its old column is handed over by its MEMBERS (the windows that shared it, top to
            // bottom, itself among them), because whether that column still exists is decided by who is left in it,
            // not by its index, which a column that went with the tile hands to the neighbour that slid into it.
            var left = gone.Sizes.GetValueOrDefault(TileSizes.Equal(GridLayout.Shape(gone.Order.Count)));
            var cell = TileArrange.CellOf(left, gone.Order.ToList().IndexOf(w), gone.Order.Count);
            var oldColumn = gone.Order
                .Where((_, slot) => TileArrange.CellOf(left, slot, gone.Order.Count).Column == cell.Column)
                .ToList();
            var (order, sizes) = TileArrange.Returned(others, ShownSizes(workspace, screen, others.Count), w, cell.Column, oldColumn);
            var into = current.Into(gone.Screen, order);
            StoreSizes(workspace, KeyOf(screen), sizes)
                .Tap(() => trace?.Invoke($"tile: {w.Value:X} came back from column {cell.Column}, row {cell.Row} on screen {gone.Screen}, "
                    + $"{(oldColumn.Any(x => x != w && others.Contains(x)) ? "into the column its neighbours are in" : "as a column of its own")}, "
                    + $"slot {into.On(gone.Screen).ToList().IndexOf(w)}, sizes {Show(sizes)}"))
                .TapError(error => trace?.Invoke($"tile: {w.Value:X} came back to screen {gone.Screen}, storing its sizes failed: {error}"));
            return into;
        });

    void RestoreSizes(Workspace workspace, Away gone) =>
        surface.Screens().TryFirst(s => s.Number == gone.Screen).Tap(screen =>
            StoreSizes(workspace, KeyOf(screen), gone.Sizes.GetValueOrDefault(TileSizes.Equal(GridLayout.Shape(gone.Order.Count))))
                .TapError(error => trace?.Invoke($"tile: restoring the sizes of screen {gone.Screen} failed: {error}")));
}
