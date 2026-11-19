using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;
using Deskbar.Core.Tiling;
using static Deskbar.Core.Tests.Tiling.Trees;

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

    // Task 5 review: with a gap, the left tile's middle (y 520 on 1920x1040, gap 8) falls in the gap between the middle
    // column's rows (8..516 and 524..1032), so neither row spans it, while the full-height right tile does. A snap must
    // still take the NEAREST column: the middle one, never jump over it. Its two rows tie on height (4 px each), so the
    // first in slot order, the top row, wins.
    static readonly WindowHandle D = new(4);

    static readonly TileBoard ThreeColumns = TileBoard.Empty.Join(1, A).Join(1, B).Join(1, C).Join(1, D);

    static readonly IReadOnlyDictionary<WindowHandle, WindowRect> ThreeColumnSlots =
        new[] { A, B, C, D }
            .Zip(GridLayout.Arrange(new WindowRect(0, 0, 1920, 1040), 4, 8,
                Tree(Across(C3(L, 1.0 / 3), C3(Down(C3(L, .5), C3(L, .5)), 1.0 / 3), C3(L, 1.0 / 3)))))
            .ToDictionary(p => p.First, p => p.Second);

    static TileChild C3(TileNode node, double share) => new(node, share);

    [Fact]
    public void A_snap_right_with_a_gap_takes_the_stacked_column_beside_it_not_the_one_beyond() =>
        Assert.Equal(new MoveOutcome.Swap(B), SettledMove.Snapped(A, TileArrange.Zone.Right, 1, ThreeColumns, ThreeColumnSlots, Maybe<int>.None));

    [Fact]
    public void A_snap_left_with_a_gap_takes_the_stacked_column_beside_it_not_the_one_beyond() =>
        Assert.Equal(new MoveOutcome.Swap(B), SettledMove.Snapped(D, TileArrange.Zone.Left, 1, ThreeColumns, ThreeColumnSlots, Maybe<int>.None));
}
