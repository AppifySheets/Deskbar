using CSharpFunctionalExtensions;
using Deskbar.Core.Abstractions;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Tiling;

// A WINDOW THAT LEAVES TAKES ITS LAYOUT CHANGE WITH IT. Petre, 2026-11-20: "if a window is added to the tiled screen
// and then removed, closed, minimized, we should have the same layout as was active before the new window appeared",
// and "same should happen if i minimize and restore a window".
//
// Why removing the leaf was not enough. A window that arrives splits one tile (Tiler.Arrival.cs), and the tree is kept
// in normal form (TileLayout), so a split along the same axis as the tile's parent MERGES into it: a row [0.474, 0.526]
// whose second tile is split to the right is one row of three, [0.474, 0.263, 0.263], and nothing records that the two
// last ones were one tile. When the newcomer leaves, TileLayout.Remove gives its share to every sibling in proportion,
// [0.644, 0.356]. Measured on Petre's machine (trace, 2026-11-20 13:51:44): a burst of short-lived windows wore a row
// down like that a little more with each one.
//
// So the moment a window joins a screen, the screen as it was is remembered: its windows in order and the layout stored
// for it (None: nothing stored, the automatic layout), together with the layout the screen shows right after the join.
// When that window is the only one to leave the screen (it closed, went to the tray, was minimized or maximized, moved
// to another workspace or screen), and the screen is still exactly what the join left (the same other windows in the
// same order, and the same layout shown), the remembered layout is put back instead of removing the leaf
// (NoteLeavers hands it to LayoutOn as the leaver's `Back`). Anything that changed the screen in between (a split
// dragged, an even-out, another window opening or closing there, a swap) fails that check, and the leaf is removed as
// before: putting the old layout back would undo the user's own change.
//
// Minimize and restore come out right with no extra rule: the minimize is a leave, so the layout from before the
// window joined comes back; Tiler.Minimize.cs remembered the layout WITH the window (NoteAway runs before the leave)
// and puts it back on the restore; the restore is a join, so it is remembered again here, and a later close goes back
// to the layout without the window once more.
//
// Not on a workspace that keeps a closed tile's space (Workspace.TileKeepsSpace): a tile that closes there leaves its
// leaf as the screen's opening, which is the point of that switch, and LayoutOn checks the close rule first.
public sealed partial class Tiler
{
    // A window's join of a screen: what the screen was before (`Before`, the windows in order; `Stored`, its stored
    // layout) and the layout it showed right after (`After`), which is how the leave tells that nothing changed since.
    sealed record Joined(Guid Workspace, int Screen, IReadOnlyList<WindowHandle> Before, Maybe<TileLayout> Stored, TileLayout After);

    // The last join of each window still worth undoing. Overwritten by the window's next join, dropped when it
    // disappears (its handle can be recycled) or its board is forgotten. A stale entry is harmless: Back checks it.
    readonly Dictionary<WindowHandle, Joined> joined = [];

    // Runs `join`, which puts `window` on a screen of `before` and stores whatever layout that makes, then remembers
    // the screen as it was. Called around every way a window arrives (Arrive) or comes back (ReturnedToTheirPlaces).
    // The stored layouts are read before the join, for every screen, because which screen it lands on is only known
    // after (an arrival can go to the screen of the tile last used).
    TileBoard Joining(Workspace workspace, TileBoard before, WindowHandle window, Func<TileBoard> join)
    {
        var screens = surface.Screens().ToList();
        var storedBefore = screens.ToDictionary(s => s.Number, s => StoredOn(workspace.Id, KeyOf(s)));
        var after = join();
        after.ScreenOf(window)
            .Bind(n => screens.TryFirst(s => s.Number == n))
            .Tap(screen => joined[window] = new Joined(workspace.Id, screen.Number,
                before.On(screen.Number).Where(w => w != window).ToList(),
                storedBefore[screen.Number],
                ShownOn(workspace.Id, screen, after.On(screen.Number).Count)));
        return after;
    }

    // Asked by NoteLeavers for a screen that `window` alone left, now holding `staying`: the layout to put back, when
    // the window's last join is undone by this leave exactly (see the class comment). `count` is how many windows the
    // screen held before this leave, so the layout shown then can be compared with the one the join left. Nothing
    // stored before the join comes back as the automatic layout, which SetTileLayout stores as no entry at all, so the
    // screen follows its count again.
    Maybe<TileLayout> Back(Guid workspace, int screen, WindowHandle window, IReadOnlyList<WindowHandle> staying, int count) =>
        joined.TryGetValue(window, out var join)
        && join.Workspace == workspace && join.Screen == screen
        && join.Before.SequenceEqual(staying)
        && surface.Screens().TryFirst(s => s.Number == screen).TryGetValue(out var here)
        && ShownOn(workspace, here, count) == join.After
            ? Maybe.From(join.Stored.GetValueOrDefault(TileLayout.Automatic(staying.Count)))
            : Maybe<TileLayout>.None;

    // ShownLayout by workspace id: NoteLeavers knows workspaces only by id. Stores nothing and takes no leaver.
    TileLayout ShownOn(Guid workspace, TileScreen screen, int count) =>
        StoredOn(workspace, KeyOf(screen))
            .Map(s => TileArrange.Refit(s, count, Maybe<int>.None))
            .GetValueOrDefault(TileLayout.Automatic(count));
}
