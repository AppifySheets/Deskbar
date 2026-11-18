using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Tiling;

// What the Tiler should do with a window once a drag or resize has settled.
public abstract record MoveOutcome
{
    // The window's centre landed on another window's tile: they trade slots.
    public sealed record Swap(WindowHandle With) : MoveOutcome;

    // The centre is over its own slot, or over no tile at all (a resize that left it in a gap):
    // snap it back where it was.
    public sealed record BackToSlot : MoveOutcome;

    // The window now sits on a different screen: it joins that screen's grid.
    public sealed record JoinScreen(int Screen) : MoveOutcome;

    // Dropped on an EDGE of another tile: it goes beside that tile on that side, and the shape changes
    // (TileArrange.Drop).
    public sealed record Beside(WindowHandle Target, TileArrange.Zone Side) : MoveOutcome;
}

// The rule for a settled move, kept out of the Win32 plumbing so it can be tested. It decides by the
// frame's CENTRE rather than by overlap: one point has one answer, where overlap can tie between two
// tiles.
public static class SettledMove
{
    // `slots` are the current tile rectangles on `screenNow`, by the window that holds each one.
    public static MoveOutcome Decide(
        WindowHandle moved,
        WindowRect frame,
        int screenNow,
        TileBoard board,
        IReadOnlyDictionary<WindowHandle, WindowRect> slots) =>
        board.ScreenOf(moved).TryGetValue(out var home) && home == screenNow
            ? Within(moved, frame, slots)
            : new MoveOutcome.JoinScreen(screenNow);

    // KEYBOARD SNAPS AS DIRECTIONS. Petre: "windows + left right keys don't seem to move windows to other screen".
    // Measured in the trace: Win+Left snapped the tile to the left half of its screen (0,0 to 1920,2160), the Tiler
    // put it back into its tile, and that put-back cleared Windows' own "snapped left" state, so the next Win+Left
    // snapped it left AGAIN instead of moving it to the next screen. On a tiled screen the half Windows snaps to is
    // meaningless, so the snap is read as what the key says: a direction. Left or Right when `frame` is the left or
    // right half of `workArea` (within a small tolerance, for DWM's rounding), None for anything else.
    public static Maybe<TileArrange.Zone> SnapSide(WindowRect frame, WindowRect workArea)
    {
        var tolerance = Math.Max(8, workArea.Width / 100);
        bool Near(int a, int b) => Math.Abs(a - b) <= tolerance;
        var middle = workArea.Left + workArea.Width / 2;
        if (!Near(frame.Top, workArea.Top) || !Near(frame.Bottom, workArea.Bottom)) return Maybe<TileArrange.Zone>.None;
        if (Near(frame.Left, workArea.Left) && Near(frame.Right, middle)) return TileArrange.Zone.Left;
        if (Near(frame.Left, middle) && Near(frame.Right, workArea.Right)) return TileArrange.Zone.Right;
        return Maybe<TileArrange.Zone>.None;
    }

    // Where a keyboard snap to `side` takes a tile: a swap with the tile one column over (the one level with it),
    // or, from the outermost column, the screen next door. Back to its slot when there is neither.
    public static MoveOutcome Snapped(
        WindowHandle moved,
        TileArrange.Zone side,
        int home,
        TileBoard board,
        IReadOnlyList<int> shape,
        IReadOnlyDictionary<WindowHandle, WindowRect> slots,
        Maybe<int> screenBeyond)
    {
        var order = board.On(home);
        var index = order.ToList().IndexOf(moved);
        if (index < 0 || shape.Sum() != order.Count || !slots.TryGetValue(moved, out var mine)) return new MoveOutcome.BackToSlot();
        var (column, _) = GridLayout.CellOf(index, shape);
        var next = side == TileArrange.Zone.Left ? column - 1 : column + 1;
        if (next < 0 || next >= shape.Count)
            return screenBeyond.HasValue ? new MoveOutcome.JoinScreen(screenBeyond.Value) : new MoveOutcome.BackToSlot();

        // The tile in the next column whose height spans this tile's middle, or the nearest one to it.
        var middle = (mine.Top + mine.Bottom) / 2;
        var start = shape.Take(next).Sum();
        var neighbour = order.Skip(start).Take(shape[next])
            .Where(slots.ContainsKey)
            .OrderBy(w => slots[w].Top <= middle && middle < slots[w].Bottom ? 0 : Math.Min(Math.Abs(slots[w].Top - middle), Math.Abs(slots[w].Bottom - middle)))
            .First();
        return new MoveOutcome.Swap(neighbour);
    }

    // The same rule judged at a POINT: where the pointer was when a drag by hand ended. Petre, dragging the right
    // tile down to the bottom of the screen: "it still goes back to the right". Held by its title bar and pulled
    // down, the window's centre was below the screen (measured: y 2595 and 2999 on a 2148 px work area), under no
    // tile at all, so it went back. The pointer is where the user is looking and what they meant; the centre is
    // kept for moves made without a mouse (a keyboard snap), which have no pointer to speak of.
    // `screenAt` is the screen the point is on.
    public static MoveOutcome DecideAt(
        WindowHandle moved,
        int x,
        int y,
        int screenAt,
        TileBoard board,
        IReadOnlyDictionary<WindowHandle, WindowRect> slots) =>
        board.ScreenOf(moved).TryGetValue(out var home) && home == screenAt
            ? Zoned(At(moved, x, y, slots), x, y, slots)
            : new MoveOutcome.JoinScreen(screenAt);

    // A hit on another tile's EDGE zone puts the window beside it (drop zones, TileArrange); its middle swaps.
    // Only for a drop by hand at the pointer: a window's centre has no aim, so Decide keeps plain swaps.
    static MoveOutcome Zoned(MoveOutcome hit, int x, int y, IReadOnlyDictionary<WindowHandle, WindowRect> slots) =>
        hit is MoveOutcome.Swap swap && TileArrange.ZoneOf(slots[swap.With], x, y) is var zone && zone != TileArrange.Zone.Middle
            ? new MoveOutcome.Beside(swap.With, zone)
            : hit;

    // Same screen: the tile containing the centre decides.
    static MoveOutcome Within(WindowHandle moved, WindowRect frame, IReadOnlyDictionary<WindowHandle, WindowRect> slots) =>
        At(moved, frame.Left + frame.Width / 2, frame.Top + frame.Height / 2, slots);

    // The tile containing the point. Rectangles are half-open (Right and Bottom are exclusive), so a point exactly
    // on a shared edge belongs to the tile on its right/below.
    static MoveOutcome At(WindowHandle moved, int x, int y, IReadOnlyDictionary<WindowHandle, WindowRect> slots)
    {
        var hit = slots.Where(s => s.Value.Left <= x && x < s.Value.Right && s.Value.Top <= y && y < s.Value.Bottom)
            .Select(s => s.Key)
            .Where(w => w != moved)
            .ToList();
        return hit.Count > 0 ? new MoveOutcome.Swap(hit[0]) : new MoveOutcome.BackToSlot();
    }
}
