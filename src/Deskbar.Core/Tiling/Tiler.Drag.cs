using CSharpFunctionalExtensions;
using Deskbar.Core.Abstractions;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Tiling;

// WHILE A TILE IS BEING DRAGGED. Petre, testing tiling live: "can you resize, or show me how it would end up on the
// screen, before i let it go?", then from a mockup, "Live resize + move preview":
//
//   * Dragging a separation (a tile's inner edge, its corner, or AltSnap's right-drag resize): the tiles beside it
//     follow LIVE, laid out from the same SplitMove arithmetic the drop uses, so what you see while dragging is what
//     you get on release. Nothing is stored until the drop; OnSettled's step 6 (MovedASplit) stores the sizes then.
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
    // the dragged window would otherwise be put back into its PRE-drag slot (the new sizes are stored only when the
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
        var shown = ShownSizes(workspace, screen, windows.Count);
        var shape = shown.Shape;
        var cell = GridLayout.CellOf(windows.ToList().IndexOf(window), shape);
        SplitMove.Apply(shown, shape, screen.WorkArea, GapOn(screen), cell, moved, frame).Tap(next =>
            SlotsOf(screen, windows, next)
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
            ? SlotsOf(homeScreen, Board.On(home), ShownSizes(workspace, homeScreen, Board.On(home).Count))
            : new Dictionary<WindowHandle, WindowRect>();

        var previews = SettledMove.DecideAt(window, x, y, target, Board, homeSlots) switch
        {
            MoveOutcome.Swap swap when homeSlots.TryGetValue(swap.With, out var theirs) && homeSlots.TryGetValue(window, out var mine) =>
                new[] { new TilePreview(theirs, Dragged: true), new TilePreview(mine, Dragged: false) },
            MoveOutcome.JoinScreen join when screens.TryGetValue(join.Screen, out var there) =>
                JoinedSlot(workspace, there, window).Map(slot => new[] { new TilePreview(slot, Dragged: true) })
                    .GetValueOrDefault([]),
            // Beside another tile: where the window lands in the new shape, and that tile's new, smaller place.
            MoveOutcome.Beside beside when homeScreen is not null => BesidePreview(workspace, homeScreen, window, beside),
            _ => homeSlots.TryGetValue(window, out var own) ? [new TilePreview(own, Dragged: true)] : [],
        };
        surface.Preview(previews);
        previewShown = previews.Length > 0;
    }

    // The rearrangement a Beside drop would make (TileArrange.Drop), with its slots. The drop and the preview both
    // come here, so what the preview shows is what the drop does.
    (IReadOnlyList<WindowHandle> Order, TileSizes Sizes, IReadOnlyDictionary<WindowHandle, WindowRect> Slots) Rearranged(
        Workspace workspace, TileScreen screen, WindowHandle window, MoveOutcome.Beside beside)
    {
        var order = Board.On(screen.Number);
        var (next, sizes) = TileArrange.Drop(order, ShownSizes(workspace, screen, order.Count), window, beside.Target, beside.Side);
        return (next, sizes, SlotsOf(screen, next, sizes));
    }

    TilePreview[] BesidePreview(Workspace workspace, TileScreen screen, WindowHandle window, MoveOutcome.Beside beside)
    {
        var slots = Rearranged(workspace, screen, window, beside).Slots;
        return slots.TryGetValue(window, out var mine) && slots.TryGetValue(beside.Target, out var theirs)
            ? [new TilePreview(mine, Dragged: true), new TilePreview(theirs, Dragged: false)]
            : [];
    }

    // The slot a window would get joining `screen`'s grid at the end, as JoinScreen does.
    Maybe<WindowRect> JoinedSlot(Workspace workspace, TileScreen screen, WindowHandle window)
    {
        var windows = Board.On(screen.Number).Where(w => w != window).Append(window).ToList();
        return SlotsOf(screen, windows, ShownSizes(workspace, screen, windows.Count)).TryGetValue(window, out var slot)
            ? slot
            : Maybe<WindowRect>.None;
    }

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

    // The sizes the screen shows right now: the stored ones fitted to this count, or equal. Unlike SizesOn this
    // stores nothing and takes no pending leaver, because a drag changes neither the windows nor the count.
    TileSizes ShownSizes(Workspace workspace, TileScreen screen, int count) =>
        StoredOn(workspace.Id, KeyOf(screen))
            .Map(s => TileArrange.Refit(s, count, Maybe<(int Column, int Row)>.None))
            .GetValueOrDefault(TileSizes.Equal(GridLayout.Shape(count)));

    // The shape a screen (by number) of a workspace has for `count` windows: its stored shape if that fits the
    // count, otherwise the automatic one. For NoteLeavers, which knows screens only by number.
    IReadOnlyList<int> ShapeOn(Guid workspace, int screen, int count) =>
        TileArrange.ShapeFor(
            surface.Screens().TryFirst(s => s.Number == screen).Bind(s => StoredOn(workspace, KeyOf(s))),
            count);

    Maybe<TileSizes> StoredOn(Guid workspace, string key) =>
        manager.State.Workspaces.TryFirst(w => w.Id == workspace)
            .Bind(w => w.TileSizes.TryFirst(s => s.Screen == key))
            .Map(s => s.Sizes);

    // The pointer, or the frame's centre if Windows will not say where the pointer is.
    (int X, int Y) PointOf(WindowRect frame) =>
        surface.Pointer().GetValueOrDefault((frame.Left + frame.Width / 2, frame.Top + frame.Height / 2));

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
