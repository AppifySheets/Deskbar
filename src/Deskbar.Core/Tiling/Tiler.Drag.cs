using CSharpFunctionalExtensions;
using Deskbar.Core.Abstractions;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Tiling;

// WHILE A TILE IS BEING DRAGGED. Petre, testing tiling live: "can you resize, or show me how it would end up on the
// screen, before i let it go?", then from a mockup, "Live resize + move preview":
//
//   * Dragging a separation (a tile's inner edge, its corner, or AltSnap's right-drag resize): the tiles beside it
//     follow LIVE, laid out from the same SplitMove arithmetic the drop uses, so what you see while dragging is what
//     you get on release. Nothing is stored until the drop; OnSettled's step 6 (MovedASplit) stores the layout then.
//   * Moving a tile (its title bar, AltSnap's Alt-drag): nothing else moves until you let go, because tiles jumping
//     around under the pointer as it crosses them is worse than useless. Instead the surface draws where the tile
//     will land (filled) and, for a swap, where the other tile goes (outline). The landing place is decided by the
//     same rule the drop uses, at the POINTER (SettledMove.DecideAt), so the preview and the drop never disagree.
//
// Fed by the surface's Moving, which fires only while a mouse button is down and is throttled, so a keyboard snap
// or an app moving itself never shows a preview or moves a neighbour.
public sealed partial class Tiler
{
    // The drag in progress: which window, on which workspace and screen. Set by the first Moving of a drag,
    // cleared when that window settles (OnSettled) or goes. While it is set, the Settled of a neighbour on the
    // same screen is ignored: those are the echoes of our own live placements, and a neighbour that settled before
    // the dragged window would otherwise be put back into its PRE-drag slot (the new layout is stored only when the
    // dragged window settles), and then moved again a moment later.
    (WindowHandle Window, Guid Workspace, int Screen)? dragging;

    // Whether a preview is up, so a drag that never showed one does not ask the surface to hide nothing.
    bool previewShown;

    void OnMoving(WindowHandle window)
    {
        // The tiles a live resize places move too, with the user's button still down, so the surface reports them
        // as Moving as well. Neither is a drag: another window's drag is still on, or this window is exactly where
        // the Tiler just put it.
        if (dragging is { } drag && drag.Window != window && surface.IsSettling(drag.Window)) return;
        if (given.TryGetValue(window, out var placed) && surface.VisibleFrameOf(window).Map(f => f == placed.Accepted).GetValueOrDefault(false))
            return;

        if (!Acting().TryGetValue(out var workspace)
            || !Board.ScreenOf(window).TryGetValue(out var home)
            || !given.TryGetValue(window, out var last)
            || !surface.VisibleFrameOf(window).TryGetValue(out var frame)
            || surface.IsMaximized(window)
            || dropping(window))
            return;

        dragging = (window, workspace.Id, home);
        var screens = surface.Screens().ToDictionary(s => s.Number);
        if (!screens.TryGetValue(home, out var screen)) return;

        // A resize when one or two adjacent edges moved and the opposite ones held (the drop's own test, with the
        // button down by definition here); anything else is a move.
        var moved = SettledResize.Classify(last.Accepted, frame, byHand: true);
        if (moved != Edges.None && surface.ScreenOf(window) == home)
        {
            HidePreview();
            LiveResize(workspace, screen, window, moved, frame);
        }
        else
            ShowMovePreview(workspace, screens, window, home, frame);
    }

    // The neighbours take the slots of the split the drag would make, now. The dragged window itself is never
    // placed: it is under the user's pointer.
    void LiveResize(Workspace workspace, TileScreen screen, WindowHandle window, Edges moved, WindowRect frame)
    {
        var windows = Board.On(screen.Number);
        var shown = ShownLayout(workspace, screen, windows.Count);
        var leaf = TileArrange.LeafOf(shown, windows.ToList().IndexOf(window), windows.Count);
        // At a crossing the move regroups the tree and reorders its leaves (SplitMoved), so the neighbours are laid
        // out in the new order. Nothing is stored and the board is left alone: the drop recomputes the same move from
        // the stored layout and the release frame (Tiler.Resize.MovedASplit), and reorders the board then.
        SplitMove.Apply(shown, screen.WorkArea, GapOn(screen), leaf, moved, frame, Unstretched(screen, windows, shown)).Tap(next =>
            SlotsOf(screen, next.Windows(windows, shown), next.Layout)
                .Where(s => s.Key != window)
                .ToList()
                .ForEach(s => PerWindow(s.Key, "live place", () => PlaceLive(s.Key, s.Value))));
    }

    // Placed straight away, past PlaceIfNeeded's "still moving" check: the neighbour IS moving, but only because
    // the previous live placement moved it. Its `given` and its expected echo are kept up to date, so its Settled
    // after the drop reads as the echo of our own placement.
    void PlaceLive(WindowHandle window, WindowRect slot)
    {
        var before = surface.VisibleFrameOf(window);
        if (before.HasValue && before.Value == slot) return;
        surface.Place(window, slot).Tap(() =>
        {
            var accepted = surface.VisibleFrameOf(window).GetValueOrDefault(slot);
            given[window] = new Given(slot, accepted);
            AwaitEcho(window, before, accepted);
        });
    }

    // Where the dragged tile lands if let go now, decided at the pointer exactly as the drop is.
    void ShowMovePreview(Workspace workspace, IReadOnlyDictionary<int, TileScreen> screens, WindowHandle window, int home, WindowRect frame)
    {
        var (x, y) = PointOf(frame);
        var target = ScreenAt(screens.Values, x, y).GetValueOrDefault(home);
        var homeSlots = screens.TryGetValue(home, out var homeScreen)
            ? SlotsOf(homeScreen, Board.On(home), ShownLayout(workspace, homeScreen, Board.On(home).Count))
            : new Dictionary<WindowHandle, WindowRect>();

        var thereSlots = SlotsThere(workspace, screens, target, home);
        var hole = homeScreen is null ? Maybe<WindowRect>.None : OpeningOn(workspace, homeScreen);
        var outcome = SettledMove.DecideAt(window, x, y, target, Board, homeSlots, thereSlots, hole, InnerOf(screens, target));
        var previews = outcome switch
        {
            // A swap lands in the other tile's slot, on this screen or the one the pointer is on.
            MoveOutcome.Swap swap when homeSlots.TryGetValue(swap.With, out var theirs) || thereSlots.TryGetValue(swap.With, out theirs) =>
                new[] { new TilePreview(theirs, Dragged: true) },
            MoveOutcome.JoinScreen join when screens.TryGetValue(join.Screen, out var there) =>
                JoinedSlot(workspace, there, window).Map(slot => new[] { new TilePreview(slot, Dragged: true) })
                    .GetValueOrDefault([]),
            // Beside another tile, on whichever screen that tile is: where the window lands in the new shape.
            MoveOutcome.Beside beside when Board.ScreenOf(beside.Target).TryGetValue(out var on) && screens.TryGetValue(on, out var besideScreen) =>
                BesidePreview(workspace, besideScreen, window, beside),
            // In a screen-edge band, on whichever screen the pointer is: the column or row the window becomes.
            MoveOutcome.AtEdge edge when screens.TryGetValue(edge.Screen, out var edgeScreen) =>
                Banded(workspace, edgeScreen, window, edge).Slots.TryGetValue(window, out var column)
                    ? [new TilePreview(column, Dragged: true)]
                    : [],
            // Into the opening: the window takes the opening's place (the room it leaves is shared out after).
            MoveOutcome.IntoOpening when hole.TryGetValue(out var room) => [new TilePreview(room, Dragged: true)],
            _ => homeSlots.TryGetValue(window, out var own) ? [new TilePreview(own, Dragged: true)] : [],
        };

        // One line whenever the preview CHANGES (never per Moving), so a preview that lands somewhere unexpected can
        // be read back: what the pointer was over and what was chosen.
        var line = $"{outcome} at {string.Join(" ", previews.Where(p => p.Dragged).Select(p => Show(p.Frame)))}";
        if (line != lastPreviewLine)
        {
            lastPreviewLine = line;
            trace?.Invoke($"tile: {window.Value:X} preview: pointer {x},{y} on screen {target}: {line}");
        }
        surface.Preview(previews);
        previewShown = previews.Length > 0;
    }

    string? lastPreviewLine;

    // The tiles of the screen the pointer is on, when that is not the dragged window's own screen; empty otherwise
    // (and for an empty screen). Read without side effects, like everything a preview reads.
    IReadOnlyDictionary<WindowHandle, WindowRect> SlotsThere(Workspace workspace, IReadOnlyDictionary<int, TileScreen> screens, int pointerOn, int home) =>
        pointerOn != home && screens.TryGetValue(pointerOn, out var there) && Board.On(pointerOn).Count > 0
            ? SlotsOf(there, Board.On(pointerOn), ShownLayout(workspace, there, Board.On(pointerOn).Count))
            : new Dictionary<WindowHandle, WindowRect>();

    // The rearrangement a Beside drop would make (TileArrange.Drop), with its slots. The drop and the preview both
    // come here, so what the preview shows is what the drop does.
    // On the tree the target leaf alone is split (TileArrange.Drop), so a drop beside a stacked tile goes beside that
    // tile only, and the tiles of the dragged window's old split grow into the room it left.
    (IReadOnlyList<WindowHandle> Order, TileLayout Layout, IReadOnlyDictionary<WindowHandle, WindowRect> Slots) Rearranged(
        Workspace workspace, TileScreen screen, WindowHandle window, MoveOutcome.Beside beside)
    {
        var order = Board.On(screen.Number);
        var (next, layout) = TileArrange.Drop(order, ShownLayout(workspace, screen, order.Count), window, beside.Target, beside.Side);
        return (next, layout, SlotsOf(screen, next, layout));
    }

    // The rearrangement a band drop would make (TileArrange.DropAtEdge), with its slots: the drop and the preview both
    // come here too. The screen's windows are lifted from in place, so a window dragged in from another screen has
    // nothing to lift there; its own screen gives its leaf up as any leaver's.
    (IReadOnlyList<WindowHandle> Order, TileLayout Layout, IReadOnlyDictionary<WindowHandle, WindowRect> Slots) Banded(
        Workspace workspace, TileScreen screen, WindowHandle window, MoveOutcome.AtEdge edge)
    {
        var order = Board.On(screen.Number);
        var (next, layout) = TileArrange.DropAtEdge(order, ShownLayout(workspace, screen, order.Count), window, edge.Side);
        return (next, layout, SlotsOf(screen, next, layout));
    }

    // The inner area (inside the outer gap) of the screen with this number, where its screen-edge bands are measured
    // (SettledMove.DecideAt); None for a screen that is not reported.
    Maybe<WindowRect> InnerOf(IReadOnlyDictionary<int, TileScreen> screens, int number) =>
        screens.TryGetValue(number, out var screen) ? GridLayout.InnerArea(screen.WorkArea, GapOn(screen)) : Maybe<WindowRect>.None;

    TilePreview[] BesidePreview(Workspace workspace, TileScreen screen, WindowHandle window, MoveOutcome.Beside beside)
    {
        var slots = Rearranged(workspace, screen, window, beside).Slots;
        return slots.TryGetValue(window, out var mine) ? [new TilePreview(mine, Dragged: true)] : [];
    }

    // The slot a window would get joining `screen`'s grid, as JoinScreen does: the screen's opening when it has one
    // (Keep puts it there), otherwise the end.
    Maybe<WindowRect> JoinedSlot(Workspace workspace, TileScreen screen, WindowHandle window)
    {
        var others = Board.On(screen.Number).Where(w => w != window).ToList();
        var (windows, layout) = TileArrange.Filled(others, ShownLayout(workspace, screen, others.Count), window);
        return SlotsOf(screen, windows, layout.Opening is null && layout.Count == windows.Count ? layout : ShownLayout(workspace, screen, windows.Count))
            .TryGetValue(window, out var slot)
            ? slot
            : Maybe<WindowRect>.None;
    }

    // Where the screen's opening is right now, if it has one.
    Maybe<WindowRect> OpeningOn(Workspace workspace, TileScreen screen) =>
        GridLayout.OpeningOf(screen.WorkArea, Board.On(screen.Number).Count, GapOn(screen), ShownLayout(workspace, screen, Board.On(screen.Number).Count));

    void HidePreview()
    {
        if (!previewShown) return;
        previewShown = false;
        surface.Preview([]);
    }

    // The drag that `window` was part of has ended (it settled or went). Called first thing in OnSettled.
    // True when this Settled is a neighbour's echo during someone else's live drag, and is to be ignored.
    bool EndOrIgnoreDuringDrag(WindowHandle window)
    {
        if (dragging is not { } drag) return false;
        if (drag.Window == window)
        {
            dragging = null;
            HidePreview();
            return false;
        }
        return Board.ScreenOf(window).Map(s => s == drag.Screen).GetValueOrDefault(false) && surface.IsSettling(drag.Window);
    }

    // A drag's window that disappeared mid-drag ends the drag, so its neighbours are not ignored for ever.
    void ForgetDrag(WindowHandle window)
    {
        if (dragging is { } drag && drag.Window == window)
        {
            dragging = null;
            HidePreview();
        }
    }

    // The layout the screen shows right now: the stored one fitted to this count, or the automatic one. Unlike
    // LayoutOn this stores nothing and takes no pending leaver, because a drag changes neither the windows nor the
    // count. Band drops (Banded) and a minimized tile's spot and return (Tiler.Minimize.cs) read it too.
    TileLayout ShownLayout(Workspace workspace, TileScreen screen, int count) =>
        ShownOn(workspace.Id, screen, count);

    // The leaf of the window at `slot` on a screen (by number) of a workspace with `count` windows, in the stored
    // layout (counting its opening, TileArrange.LeafOf) or the automatic one, whose leaves are its slots. For
    // NoteLeavers, which knows screens only by number.
    int LeafOn(Guid workspace, int screen, int slot, int count) =>
        surface.Screens().TryFirst(s => s.Number == screen).Bind(s => StoredOn(workspace, KeyOf(s)))
            .Map(stored => TileArrange.LeafOf(stored, slot, count))
            .GetValueOrDefault(slot);

    // The layout stored for a screen (by its key, KeyOf) on a workspace, read from the manager's current state.
    Maybe<TileLayout> StoredOn(Guid workspace, string key) =>
        manager.State.Workspaces.TryFirst(w => w.Id == workspace)
            .Bind(w => w.TileLayouts.TryFirst(s => s.Screen == key))
            .Map(s => s.Layout);

    // The pointer, or the frame's centre if Windows will not say where the pointer is.
    (int X, int Y) PointOf(WindowRect frame) =>
        surface.Pointer().GetValueOrDefault((frame.Left + frame.Width / 2, frame.Top + frame.Height / 2));

    // TILES COME UP TOGETHER. Petre: "if i change to a window that's part of a tile, it should activate all other
    // tiled windows", and "if another window, untiled, maximized, is another one, it would not bring up anything".
    // Activating a tile (a click, Alt+Tab, the bar) puts every other tile of the workspace, on every screen, directly
    // behind it, above any floating window, so the grid is whole again. Nothing is activated or moved, so this makes
    // no further activation and no settle. A window that is not a tile (floating, pinned) brings up
    // nothing, nor does a maximized tile (it covers its screen anyway), and a maximized tile is left where it is.
    void RaiseTogether(WindowHandle active)
    {
        if (!Acting().HasValue || !Board.ScreenOf(active).HasValue || surface.IsMaximized(active)) return;
        var others = Board.Screens.Values.SelectMany(w => w)
            .Where(w => w != active && !surface.IsMaximized(w))
            .ToList();
        if (others.Count > 0) PerWindow(active, "raise the tiles behind", () => surface.RaiseBehind(active, others));
    }

    // The nearest screen wholly to the left or right of `screen`, for a keyboard snap off its outer column.
    static Maybe<int> ScreenBeside(IEnumerable<TileScreen> screens, TileScreen screen, TileArrange.Zone side) =>
        side == TileArrange.Zone.Left
            ? screens.Where(s => s.Bounds.Right <= screen.Bounds.Left).OrderByDescending(s => s.Bounds.Right).TryFirst().Map(s => s.Number)
            : screens.Where(s => s.Bounds.Left >= screen.Bounds.Right).OrderBy(s => s.Bounds.Left).TryFirst().Map(s => s.Number);

    // The screen whose bounds hold the point.
    static Maybe<int> ScreenAt(IEnumerable<TileScreen> screens, int x, int y) =>
        screens.TryFirst(s => s.Bounds.Left <= x && x < s.Bounds.Right && s.Bounds.Top <= y && y < s.Bounds.Bottom)
            .Map(s => s.Number);
}
