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
//   * the screen changed meanwhile (a window opened or closed there): it goes back to its old position in the
//     order, rather than to the end, and the sizes are refitted as for any join.
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

    // Called by Evaluate with the board it just reconciled, before it is kept and re-tiled. Every window that has
    // just come back and was remembered is put back in its place; the rest of the board is untouched.
    TileBoard ReturnedToTheirPlaces(Workspace workspace, TileBoard before, TileBoard board) =>
        board.Screens.Values.SelectMany(w => w)
            .Where(w => !before.ScreenOf(w).HasValue && away.ContainsKey(w))
            .ToList()
            .Aggregate(board, (current, w) =>
            {
                var gone = away[w];
                away.Remove(w);
                if (gone.Workspace != workspace.Id || current.ScreenOf(w) != Maybe.From(gone.Screen)) return current;

                var now = current.On(gone.Screen);
                var others = now.Where(x => x != w).ToList();
                if (others.SequenceEqual(gone.Order.Where(x => x != w)))
                {
                    // The same screen it left: everything as it was, sizes included (stored BEFORE the re-tile reads
                    // them; a size that was never stored is put back as "nothing stored", which plain sizes mean).
                    RestoreSizes(workspace, gone);
                    trace?.Invoke($"tile: {w.Value:X} came back to its place on screen {gone.Screen}");
                    return current.Reordered(gone.Screen, gone.Order);
                }

                // The screen changed meanwhile: its old position in the order, as near as the new order allows.
                var at = Math.Min(gone.Order.ToList().IndexOf(w), others.Count);
                others.Insert(at, w);
                trace?.Invoke($"tile: {w.Value:X} came back to position {at} on screen {gone.Screen}");
                return current.Reordered(gone.Screen, others);
            });

    void RestoreSizes(Workspace workspace, Away gone) =>
        surface.Screens().TryFirst(s => s.Number == gone.Screen).Tap(screen =>
            StoreSizes(workspace, KeyOf(screen), gone.Sizes.GetValueOrDefault(TileSizes.Equal(GridLayout.Shape(gone.Order.Count))))
                .TapError(error => trace?.Invoke($"tile: restoring the sizes of screen {gone.Screen} failed: {error}")));
}
