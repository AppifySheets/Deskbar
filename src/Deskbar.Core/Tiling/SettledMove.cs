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

    // Dropped into its own screen's OPENING (TileLayout.Opening): it moves there, and the place it left closes up.
    public sealed record IntoOpening : MoveOutcome;
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
    //
    // The tolerance is TIGHT on purpose. Windows' snap is exact (measured: frame 0,0,1920,2160 on a 3840x2160 work
    // area, no gap), while a tile always sits inside the gap. A looser one (1% of the width, the first version) read
    // a right-column tile nudged a few pixels by its app as Win+Right, and from the outer column that would have sent
    // it to the next screen (measured too: a test window the running build tiled, "snapped Right by keyboard").
    public static Maybe<TileArrange.Zone> SnapSide(WindowRect frame, WindowRect workArea)
    {
        const int tolerance = 3;
        bool Near(int a, int b) => Math.Abs(a - b) <= tolerance;
        var middle = workArea.Left + workArea.Width / 2;
        if (!Near(frame.Top, workArea.Top) || !Near(frame.Bottom, workArea.Bottom)) return Maybe<TileArrange.Zone>.None;
        if (Near(frame.Left, workArea.Left) && Near(frame.Right, middle)) return TileArrange.Zone.Left;
        if (Near(frame.Left, middle) && Near(frame.Right, workArea.Right)) return TileArrange.Zone.Right;
        return Maybe<TileArrange.Zone>.None;
    }

    // Where a keyboard snap to `side` takes a tile: a swap with the tile beside it on that side, or, when nothing lies
    // on that side (the tile is at the screen's edge), the screen next door. Back to its slot when there is neither.
    //
    // The neighbour is found by GEOMETRY from `slots`, because a split tree has no columns to count (spec,
    // docs/superpowers/specs/2026-11-19-tiling-split-tree-design.md): only tiles lying wholly on that side of this one
    // count, and of those the one whose height spans this tile's vertical middle, else the one nearest to that middle
    // by height; the nearer horizontally first among equals, then slot order. For a grid of columns that is the old
    // rule, "the tile one column over, level with it". For Petre's A and C side by side over a full-width B, Win+Right
    // from A swaps with C, and B, spanning the whole width, has nothing beside it.
    public static MoveOutcome Snapped(
        WindowHandle moved,
        TileArrange.Zone side,
        int home,
        TileBoard board,
        IReadOnlyDictionary<WindowHandle, WindowRect> slots,
        Maybe<int> screenBeyond)
    {
        var order = board.On(home);
        if (!order.Contains(moved) || !slots.TryGetValue(moved, out var mine)) return new MoveOutcome.BackToSlot();

        var middle = (mine.Top + mine.Bottom) / 2;
        bool Spans(WindowRect r) => r.Top <= middle && middle < r.Bottom;
        int Height(WindowRect r) => Spans(r) ? 0 : Math.Min(Math.Abs(r.Top - middle), Math.Abs(r.Bottom - middle));
        int Width(WindowRect r) => side == TileArrange.Zone.Left ? mine.Left - r.Right : r.Left - mine.Right;

        // OrderBy is stable, so a full tie keeps slot order.
        return order
            .Where(w => w != moved && slots.ContainsKey(w))
            .Where(w => side == TileArrange.Zone.Left ? slots[w].Right <= mine.Left : slots[w].Left >= mine.Right)
            .OrderBy(w => Height(slots[w]))
            .ThenBy(w => Width(slots[w]))
            .Select(w => (MoveOutcome)new MoveOutcome.Swap(w))
            .DefaultIfEmpty(screenBeyond.HasValue ? new MoveOutcome.JoinScreen(screenBeyond.Value) : new MoveOutcome.BackToSlot())
            .First();
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
        IReadOnlyDictionary<WindowHandle, WindowRect> slots,
        IReadOnlyDictionary<WindowHandle, WindowRect>? slotsThere = null,
        Maybe<WindowRect> openingHere = default) =>
        board.ScreenOf(moved).TryGetValue(out var home) && home == screenAt
            ? Zoned(At(moved, x, y, slots), x, y, slots) switch
            {
                // Over no tile but over the screen's opening (`openingHere`, the home screen's): into it.
                MoveOutcome.BackToSlot when openingHere.TryGetValue(out var hole) && Contains(hole, x, y) => new MoveOutcome.IntoOpening(),
                var hit => hit,
            }
            : Across(moved, x, y, screenAt, slotsThere ?? new Dictionary<WindowHandle, WindowRect>());

    static bool Contains(WindowRect r, int x, int y) => r.Left <= x && x < r.Right && r.Top <= y && y < r.Bottom;

    // On ANOTHER screen the same rules, against that screen's tiles (`slotsThere`): the middle of a tile trades
    // places with it across the screens, an edge zone puts the window beside it there. Over no tile at all (an
    // empty screen, a gap) it joins that screen's grid at the end, as before. Petre, dragging across: "on the right
    // it only highlighted the right circle", because every drop on another screen joined at the end.
    static MoveOutcome Across(WindowHandle moved, int x, int y, int screenAt, IReadOnlyDictionary<WindowHandle, WindowRect> slotsThere) =>
        Zoned(At(moved, x, y, slotsThere), x, y, slotsThere) switch
        {
            MoveOutcome.BackToSlot => new MoveOutcome.JoinScreen(screenAt),
            var hit => hit,
        };

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
