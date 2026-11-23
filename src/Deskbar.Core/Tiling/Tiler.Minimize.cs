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
//
// A MAXIMIZED TILE IS AWAY TOO (Petre, 2026-11-20: "let's not give space to the maximized window"). It gives its slot
// up the same way, so the other tiles fill the screen, and its spot is remembered the same way (NoteAway, called by
// Evaluate while the board still holds it). Its un-maximize is a Settled rather than a MinimizeChanged, but it reaches
// the same Evaluate, where ComesBack finds the entry and puts it back beside what it was next to. A maximized window
// that is minimized and restored (Windows restores it maximized) stays away throughout and keeps its first entry.
//
// SEVERAL TILES AWAY AT ONCE (Petre, 2026-11-23: "when i minimize tiles and maximize them, it seems their position may
// change. shouldn't"). His trace: four windows of one workspace minimized within 50 ms (a minimize-all) and restored
// together. Two things went wrong, and both are fixed here:
//   * only the first window had its place remembered. Windows minimizes them all before the first event is handled,
//     so the evaluate that event causes takes EVERY minimized tile off the board, and the next events found nothing to
//     note. So a minimize event now notes every tile of the board that is minimized, not just its own window;
//   * a tile coming back while others of its screen were still away failed "the same screen it left" (some of its old
//     neighbours were missing) and went to the changed-screen rules: the first back took the screen and the others were
//     split in beside it. Now, when the screen holds nothing but windows it left there, in the order it left them,
//     it comes back to the layout it remembers with the absent windows' leaves taken out (TileArrange.Kept). So the
//     ones back keep where they were relative to each other, whichever order they come back in, and the last one back
//     puts the whole shape back.
public sealed partial class Tiler
{
    // `Layout` is what was stored for the screen (None: nothing, the automatic layout), put back as it was when the
    // screen did not change. `Shown` is the layout the screen SHOWED for `Order` (stored or automatic), which a return
    // to a screen some of those windows are still away from is laid out from. `Spot` is the window's place in it, read
    // when the screen did change; None when it had nothing beside it.
    sealed record Away(Guid Workspace, int Screen, IReadOnlyList<WindowHandle> Order, Maybe<TileLayout> Layout,
        Maybe<TileLayout> Shown, Maybe<TileArrange.ReturnSpot> Spot);

    readonly Dictionary<WindowHandle, Away> away = [];

    // Called BEFORE the evaluate that takes the window out of the grid, so the board and the layout still hold it: on
    // MinimizeChanged for a minimized tile, and from Evaluate itself for a tile that is now maximized. A window that is
    // neither, or is no longer on the board (a maximized window minimized: it left when it was maximized), notes
    // nothing, and an entry it already has stands.
    void NoteAway(WindowHandle window)
    {
        if (!(surface.IsMinimized(window) || surface.IsMaximized(window))
            || !Acting().TryGetValue(out var workspace) || !Board.ScreenOf(window).TryGetValue(out var screen))
            return;
        var order = Board.On(screen);
        var here = surface.Screens().TryFirst(s => s.Number == screen);
        var layout = here.Bind(s => StoredOn(workspace.Id, KeyOf(s)));
        var shown = here.Map(s => ShownLayout(workspace, s, order.Count));
        away[window] = shown.Bind(s => EarlierScreen(workspace.Id, screen, order, s))
            .Map(earlier => earlier with { Spot = TileArrange.SpotOf(earlier.Order, earlier.Shown.Value, window) })
            .GetValueOrDefault(() => new Away(workspace.Id, screen, order, layout, shown,
                shown.Bind(s => TileArrange.SpotOf(order, s, window))));
    }

    // The screen another tile still away from it remembers, when the screen now is exactly that screen without the
    // tiles still away: the same windows in the same order, and the layout it showed then with their leaves taken out
    // (TileArrange.Kept) is the layout it shows now. A tile minimized onto such a screen shares that memory instead of
    // remembering the smaller screen (see "Several tiles away at once"): W1 minimized and then W2, each with its own
    // click, both remember the screen with all three, so W1 coming back first, then W2, puts the whole shape back. The
    // largest such memory wins. None when no other tile is away from this screen, or the screen changed since (a split
    // dragged, a window opened or closed, a swap), and the tile then remembers the screen as it is.
    Maybe<Away> EarlierScreen(Guid workspace, int screen, IReadOnlyList<WindowHandle> order, TileLayout shown) =>
        away.Values
            .Where(a => a.Workspace == workspace && a.Screen == screen && a.Shown.HasValue)
            .Where(a => a.Order.Count > order.Count && a.Order.Where(order.Contains).SequenceEqual(order))
            .Where(a => StillAway(a.Order.Where(w => !order.Contains(w)), workspace, screen))
            .Where(a => TileArrange.Kept(a.Order, a.Shown.Value, order).Map(kept => kept.Near(shown)).GetValueOrDefault(false))
            .OrderByDescending(a => a.Order.Count)
            .TryFirst();

    // Every one of `windows` is a tile away from this workspace's screen (minimized or maximized), not one that closed
    // or went elsewhere: a closed tile's leaf is gone for good (or kept as the opening), so a memory that still holds it
    // is not the screen to go back to.
    bool StillAway(IEnumerable<WindowHandle> windows, Guid workspace, int screen) =>
        windows.All(w => away.TryGetValue(w, out var a) && a.Workspace == workspace && a.Screen == screen);

    // A minimize event notes every tile of the board that is minimized now, its own window first (see "Several tiles
    // away at once" above): a minimize-all minimizes them all before the first event is handled, and the evaluate that
    // event causes takes every one of them off the board, so the later events would find none of them left to note.
    void NoteAwayTogether(WindowHandle window) =>
        Board.Screens.Values.SelectMany(w => w)
            .Where(surface.IsMinimized)
            .Prepend(window)
            .Distinct()
            .ToList()
            .ForEach(NoteAway);

    // Is `window`, new to the board on `screen`, a minimized tile coming back to the workspace and the screen it left?
    // Asked by Evaluate before anything is placed. One that comes back somewhere else has no place to go back to and
    // arrives like any new window (Evaluate forgets its entry), since nothing appends to the end of a screen any more.
    bool ComesBack(WindowHandle window, Workspace workspace, Maybe<int> screen) =>
        away.TryGetValue(window, out var gone) && gone.Workspace == workspace.Id && screen == Maybe.From(gone.Screen);

    // Called by Evaluate with the board as the leaves left it (`stayed`: no newcomer on it yet, every leave already
    // settled), before any new window arrives. Each window in `returning` (ComesBack said yes) is put back in its
    // place, one at a time, so a later one sees the earlier ones where they went. The layout each return makes is
    // stored BEFORE the re-tile reads it.
    //
    // Each return is a join like an arrival's (Joining, Tiler.Rejoin.cs): the screen as it was while the window was
    // away is remembered, so minimizing it again, or closing it, puts that layout back.
    TileBoard ReturnedToTheirPlaces(Workspace workspace, TileBoard stayed, IReadOnlyList<WindowHandle> returning) =>
        returning.Aggregate(stayed, (current, w) => Joining(workspace, current, w, () => ReturnTo(workspace, current, w)));

    // One window of ReturnedToTheirPlaces, back on `current`.
    TileBoard ReturnTo(Workspace workspace, TileBoard current, WindowHandle w)
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

            // Some of the windows it left are still away (minimized with it, or since), and nothing else is on the
            // screen: back to the remembered layout with only the windows here now in it.
            var amongThem = AmongTheOnesItLeft(workspace, screen, current, w, gone);
            if (amongThem.HasValue) return amongThem.Value;

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
    }

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

    // `window` back on a screen that holds only windows it left there, in the order it left them, with every one of
    // the others still away (minimized or maximized; a closed one fails this, and the changed-screen rules keep its
    // opening): the order it remembers with the absent ones taken out, and the layout the screen showed then
    // with their leaves taken out (TileArrange.Kept: each absent leaf's share goes to its siblings, so the ones here keep
    // their places relative to each other). The layout is stored first, as every return's is. None when anything else
    // is on the screen (a window opened or came over meanwhile), when the order changed (a swap), or when the remembered
    // layout does not describe the remembered windows; the changed-screen rules then place it.
    Maybe<TileBoard> AmongTheOnesItLeft(Workspace workspace, TileScreen screen, TileBoard current, WindowHandle window, Away gone)
    {
        var others = current.On(gone.Screen);
        var back = gone.Order.Where(x => x == window || others.Contains(x)).ToList();
        if (back.Count != others.Count + 1 || !back.Where(x => x != window).SequenceEqual(others)
            || !StillAway(gone.Order.Where(x => !back.Contains(x)), workspace.Id, gone.Screen))
            return Maybe<TileBoard>.None;
        return gone.Shown
            .Bind(shown => TileArrange.Kept(gone.Order, shown, back))
            .Map(kept =>
            {
                StoreLayout(workspace, KeyOf(screen), kept)
                    .Tap(() => trace?.Invoke($"tile: {window.Value:X} came back to its place on screen {gone.Screen} among the {others.Count} of the tiles it left that are back, sizes {Show(kept)}"))
                    .TapError(error => trace?.Invoke($"tile: {window.Value:X} came back to screen {gone.Screen}, storing its place failed: {error}"));
                return current.Join(gone.Screen, window).Reordered(gone.Screen, back);
            });
    }

    void RestoreLayout(Workspace workspace, Away gone) =>
        surface.Screens().TryFirst(s => s.Number == gone.Screen).Tap(screen =>
            StoreLayout(workspace, KeyOf(screen), gone.Layout.GetValueOrDefault(TileLayout.Automatic(gone.Order.Count)))
                .TapError(error => trace?.Invoke($"tile: restoring the layout of screen {gone.Screen} failed: {error}")));
}
