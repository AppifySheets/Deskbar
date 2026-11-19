using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;
using Deskbar.Core.Tiling;

namespace Deskbar.Core.Tests.Tiling;

// Where a window goes when a drag or resize settles. Screen 1 holds A on the left and B on the right.
public class SettledMoveTests
{
    static readonly WindowHandle A = new(1);
    static readonly WindowHandle B = new(2);

    static readonly TileBoard Board = TileBoard.Empty.Join(1, A).Join(1, B);

    static readonly IReadOnlyDictionary<WindowHandle, WindowRect> Slots = new Dictionary<WindowHandle, WindowRect>
    {
        [A] = new(0, 0, 500, 600),
        [B] = new(500, 0, 1000, 600),
    };

    static MoveOutcome Decide(WindowHandle moved, WindowRect frame, int screenNow) =>
        SettledMove.Decide(moved, frame, screenNow, Board, Slots);

    [Fact]
    public void Centre_over_another_tile_swaps() =>
        Assert.Equal(new MoveOutcome.Swap(B), Decide(A, new(450, 100, 950, 500), 1));

    [Fact]
    public void Centre_over_its_own_slot_goes_back() =>
        Assert.IsType<MoveOutcome.BackToSlot>(Decide(A, new(10, 10, 400, 500), 1));

    // A resize that left the centre (x=-250) outside every tile.
    [Fact]
    public void Centre_in_no_tile_goes_back() =>
        Assert.IsType<MoveOutcome.BackToSlot>(Decide(A, new(-400, 0, -100, 600), 1));

    [Fact]
    public void Another_screen_joins_it() =>
        Assert.Equal(new MoveOutcome.JoinScreen(2), Decide(A, new(2000, 0, 2400, 600), 2));

    // --- keyboard snaps on a split tree -------------------------------------------------------------------------
    //
    // Win+Left / Win+Right swap with the tile beside, found by GEOMETRY: a tree has no columns to count, so the
    // neighbour is the nearest tile lying wholly on that side and spanning the moved tile's vertical middle. The shape
    // is Petre's after the drop that made the tree necessary: A and C side by side on top, B full width below.
    static readonly WindowHandle C = new(3);

    static readonly TileBoard Stacked = TileBoard.Empty.Join(1, A).Join(1, C).Join(1, B);

    static readonly IReadOnlyDictionary<WindowHandle, WindowRect> StackedSlots = new Dictionary<WindowHandle, WindowRect>
    {
        [A] = new(0, 0, 500, 300),
        [C] = new(500, 0, 1000, 300),
        [B] = new(0, 300, 1000, 600),
    };

    static MoveOutcome Snap(WindowHandle moved, TileArrange.Zone side, Maybe<int> beyond = default) =>
        SettledMove.Snapped(moved, side, 1, Stacked, StackedSlots, beyond);

    [Fact]
    public void A_snap_swaps_with_the_tile_wholly_on_that_side() =>
        Assert.Equal(new MoveOutcome.Swap(C), Snap(A, TileArrange.Zone.Right));

    [Fact]
    public void A_snap_with_nothing_on_that_side_goes_to_the_screen_beyond() =>
        Assert.Equal(new MoveOutcome.JoinScreen(2), Snap(A, TileArrange.Zone.Left, Maybe.From(2)));

    // B spans the whole width: no tile lies wholly beside it, and there is no screen beyond.
    [Fact]
    public void A_full_width_tile_with_no_screen_beyond_goes_back() =>
        Assert.IsType<MoveOutcome.BackToSlot>(Snap(B, TileArrange.Zone.Right));

    // With gaps, a tile's middle can fall in the gap between two tiles beside it. Then the nearer one by height wins,
    // and on a tie the first in slot order, as the column rule did.
    [Fact]
    public void A_middle_in_a_gap_picks_the_nearest_tile_by_height()
    {
        var board = TileBoard.Empty.Join(1, A).Join(1, B).Join(1, C);
        var slots = new Dictionary<WindowHandle, WindowRect>
        {
            [A] = new(8, 8, 496, 296),
            [B] = new(8, 304, 496, 592),
            [C] = new(504, 8, 992, 592),
        };

        Assert.Equal(new MoveOutcome.Swap(A), SettledMove.Snapped(C, TileArrange.Zone.Left, 1, board, slots, Maybe<int>.None));
        Assert.Equal(new MoveOutcome.Swap(B),
            SettledMove.Snapped(C, TileArrange.Zone.Left, 1, board, new Dictionary<WindowHandle, WindowRect>(slots) { [C] = new(504, 100, 992, 592) }, Maybe<int>.None));
    }
}
