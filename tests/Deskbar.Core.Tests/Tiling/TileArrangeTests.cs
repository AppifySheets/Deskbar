using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;
using Deskbar.Core.Tiling;

namespace Deskbar.Core.Tests.Tiling;

// Drop zones (TileArrange). Petre: "i still can't drag below another window". The top or bottom quarter of a tile
// stacks the dropped window in that tile's column; the left or right quarter makes a new column beside it; the
// middle swaps. The shape that makes is kept with the sizes.
public sealed class TileArrangeTests
{
    static WindowHandle W(int n) => new(n);

    static readonly WindowRect Tile = new(0, 0, 1000, 800);

    [Theory]
    [InlineData(500, 100, TileArrange.Zone.Top)]
    [InlineData(500, 700, TileArrange.Zone.Bottom)]
    [InlineData(100, 400, TileArrange.Zone.Left)]
    [InlineData(900, 400, TileArrange.Zone.Right)]
    [InlineData(500, 400, TileArrange.Zone.Middle)]
    // A corner goes to the nearer edge: 50 from the left beats 100 from the top.
    [InlineData(50, 100, TileArrange.Zone.Left)]
    public void A_point_falls_in_the_zone_of_the_nearest_edge(int x, int y, TileArrange.Zone zone) =>
        Assert.Equal(zone, TileArrange.ZoneOf(Tile, x, y));

    // Petre's case: two side by side, the right one dropped on the bottom of the left one. One column, the left
    // window on top, the dropped one under it, each half the height.
    [Fact]
    public void Dropping_below_a_tile_stacks_the_window_under_it_in_that_column()
    {
        var (order, sizes) = TileArrange.Drop([W(1), W(2)], TileSizes.Equal([1, 1]), W(2), W(1), TileArrange.Zone.Bottom);

        Assert.Equal([W(1), W(2)], order);
        Assert.Equal([2], sizes.Shape);
        Assert.Equal([1.0], sizes.Columns);
        Assert.Equal([0.5, 0.5], sizes.Rows[0]);
    }

    [Fact]
    public void Dropping_above_a_tile_stacks_the_window_over_it()
    {
        var (order, sizes) = TileArrange.Drop([W(1), W(2)], TileSizes.Equal([1, 1]), W(2), W(1), TileArrange.Zone.Top);

        Assert.Equal([W(2), W(1)], order);
        Assert.Equal([2], sizes.Shape);
    }

    // Beside a tile makes a new column, taking half that tile's column; the column the window left goes, and the
    // other columns keep their widths.
    [Fact]
    public void Dropping_beside_a_tile_makes_a_column_from_half_of_its_width()
    {
        // Three windows: W1 alone in a 40% column, W2 over W3 in a 60% column.
        var start = new TileSizes([0.4, 0.6], [[1.0], [0.5, 0.5]]);

        var (order, sizes) = TileArrange.Drop([W(1), W(2), W(3)], start, W(3), W(1), TileArrange.Zone.Right);

        Assert.Equal([W(1), W(3), W(2)], order);
        Assert.Equal([1, 1, 1], sizes.Shape);
        Assert.Equal([0.2, 0.2, 0.6], sizes.Columns.Select(c => Math.Round(c, 9)));
    }

    [Fact]
    public void The_middle_changes_nothing()
    {
        var start = TileSizes.Equal([1, 1]);
        var (order, sizes) = TileArrange.Drop([W(1), W(2)], start, W(2), W(1), TileArrange.Zone.Middle);

        Assert.Equal([W(1), W(2)], order);
        Assert.Equal(start, sizes);
    }

    // A custom shape outlives windows coming and going: a leaver takes its own cell, a joiner goes under the last
    // column (where the board appends it). The automatic shape still refits as before.
    [Fact]
    public void A_custom_shape_survives_a_window_leaving_and_one_joining()
    {
        var stacked = TileSizes.Equal([2]);
        Assert.True(TileArrange.IsCustom(stacked));

        Assert.Equal([3], TileArrange.Refit(stacked, 3, Maybe<(int, int)>.None).Shape);
        Assert.Equal([1], TileArrange.Refit(stacked, 1, (0, 1)).Shape);
        Assert.Equal(GridLayout.Shape(3), TileArrange.Refit(TileSizes.Equal([1, 1]), 3, Maybe<(int, int)>.None).Shape);
    }

    // Petre: "on closing, i sometimes want not to move other windows". A leaver takes only its own cell, on the
    // automatic shape too (it used to refit to GridLayout.Shape(3) = [1, 2]).
    [Fact]
    public void A_leaver_on_the_automatic_shape_takes_only_its_own_cell()
    {
        var refit = TileArrange.Refit(TileSizes.Equal([2, 2]), 3, Maybe.From((Column: 1, Row: 1)));
        Assert.Equal([2, 1], refit.Shape);
        Assert.Equal(new TileSizes([0.5, 0.5], [[0.5, 0.5], [1.0]]), refit);
    }

    // Keep-the-space (tiling-predictable-placement, change 3): a closed tile's cell stays and becomes the opening, so
    // the sizes still describe the old grid and lay the remaining windows out exactly where they were.
    [Fact]
    public void Keeping_a_cell_open_makes_it_the_opening()
    {
        var kept = TileArrange.KeptOpen(TileSizes.Equal([2, 2]), (1, 1));

        Assert.Equal(TileSizes.Equal([2, 2]) with { Opening = 3 }, kept);
        Assert.True(kept.HasOpeningFor(3));
        // An opening is never plain, so SetTileSizes stores it even on an otherwise even grid.
        Assert.False(TileArrange.IsPlain(kept));
    }

    // A cell the sizes do not have names no slot; nothing is kept.
    [Fact]
    public void Keeping_a_cell_the_sizes_do_not_have_changes_nothing()
    {
        Assert.Equal(TileSizes.Equal([2, 2]), TileArrange.KeptOpen(TileSizes.Equal([2, 2]), (2, 0)));
        Assert.Equal(TileSizes.Equal([2, 2]), TileArrange.KeptOpen(TileSizes.Equal([2, 2]), (0, 2)));
    }

    [Fact]
    public void A_leaver_alone_in_its_column_takes_the_column_and_the_rest_keep_their_rows()
    {
        var refit = TileArrange.Refit(TileSizes.Equal([1, 2]), 2, Maybe.From((Column: 0, Row: 0)));
        Assert.Equal(new TileSizes([1.0], [[0.5, 0.5]]), refit);
    }

    // Even sizes on a custom shape are still worth storing; even sizes on the automatic shape are not.
    [Fact]
    public void Only_even_sizes_on_the_automatic_shape_are_plain()
    {
        Assert.True(TileArrange.IsPlain(TileSizes.Equal([1, 1])));
        Assert.False(TileArrange.IsPlain(TileSizes.Equal([2])));
    }

    // --- openings (TileSizes.Opening) ------------------------------------------------------------------------

    static readonly WindowRect Screen = new(0, 0, 1920, 1040);
    const int Gap = 8;

    // Two columns; the right tile's top edge pulled down to 400 of an inner area that starts at 8 and is 1024 high.
    static TileSizes OpenedAboveTheRightTile() =>
        TileArrange.Open(TileSizes.Equal([1, 1]), 2, Screen, Gap, (1, 0), Edges.Top, new WindowRect(964, 400, 1912, 1032)).Value;

    [Fact]
    public void Pulling_a_top_edge_in_opens_a_row_above_the_tile()
    {
        var sizes = OpenedAboveTheRightTile();

        Assert.Equal(1, sizes.Opening);
        Assert.Equal([1, 2], sizes.Shape);
        Assert.Equal(392.0 / 1024, sizes.Rows[1][0], 9);
        Assert.Equal(1 - 392.0 / 1024, sizes.Rows[1][1], 9);
        Assert.True(sizes.HasOpeningFor(2));
        Assert.False(TileArrange.IsPlain(sizes));
    }

    [Fact]
    public void Pulling_a_left_edge_in_opens_a_column_before_the_tile()
    {
        var sizes = TileArrange.Open(TileSizes.Equal([1, 1]), 2, Screen, Gap, (0, 0), Edges.Left, new WindowRect(400, 8, 956, 1032)).Value;

        Assert.Equal(0, sizes.Opening);
        Assert.Equal([1, 1, 1], sizes.Shape);
        Assert.Equal(392.0 / 1904, sizes.Columns[0], 9);
        Assert.Equal(0.5 - 392.0 / 1904, sizes.Columns[1], 9);
    }

    [Fact]
    public void A_nudge_or_a_second_opening_opens_nothing()
    {
        Assert.True(TileArrange.Open(TileSizes.Equal([1, 1]), 2, Screen, Gap, (1, 0), Edges.Top, new WindowRect(964, 40, 1912, 1032)).HasNoValue);
        Assert.True(TileArrange.Open(OpenedAboveTheRightTile(), 2, Screen, Gap, (0, 0), Edges.Top, new WindowRect(8, 400, 956, 1032)).HasNoValue);
    }

    [Fact]
    public void The_grid_leaves_the_opening_empty()
    {
        var tiles = GridLayout.Arrange(Screen, 2, Gap, OpenedAboveTheRightTile());

        Assert.Equal([new WindowRect(8, 8, 956, 1032), new WindowRect(964, 404, 1912, 1032)], tiles);
        Assert.Equal(new WindowRect(964, 8, 1912, 396), GridLayout.OpeningOf(Screen, 2, Gap, OpenedAboveTheRightTile()).Value);
    }

    [Fact]
    public void A_newcomer_fills_the_opening_and_it_is_gone()
    {
        var (order, sizes) = TileArrange.Filled([W(1), W(2)], OpenedAboveTheRightTile(), W(3));

        Assert.Equal([W(1), W(3), W(2)], order);
        Assert.Null(sizes.Opening);
        Assert.Equal([1, 2], sizes.Shape);
    }

    [Fact]
    public void Without_an_opening_a_newcomer_joins_at_the_end()
    {
        var (order, sizes) = TileArrange.Filled([W(1), W(2)], TileSizes.Equal([1, 1]), W(3));

        Assert.Equal([W(1), W(2), W(3)], order);
        Assert.Equal(TileSizes.Equal([1, 1]), sizes);
    }

    [Fact]
    public void A_leaver_takes_only_its_cell_and_the_opening_stays()
    {
        var opened = OpenedAboveTheRightTile();

        // W(1), the left column, leaves: its cell is (0, 0), counted in the sizes that hold the opening.
        var leaver = TileArrange.CellOf(opened, 0, 2);
        var sizes = TileArrange.Refit(opened, 1, Maybe.From(leaver));

        Assert.Equal((0, 0), leaver);
        Assert.Equal(0, sizes.Opening);
        Assert.Equal([2], sizes.Shape);
        Assert.Equal(opened.Rows[1], sizes.Rows[0]);
    }

    [Fact]
    public void The_window_after_the_opening_is_counted_past_it()
    {
        Assert.Equal((1, 1), TileArrange.CellOf(OpenedAboveTheRightTile(), 1, 2));
    }

    [Fact]
    public void Closing_gives_the_room_back()
    {
        Assert.Equal(TileSizes.Equal([1, 1]), TileArrange.Closed(OpenedAboveTheRightTile()));
    }

    [Fact]
    public void A_drop_beside_a_tile_closes_the_opening_first()
    {
        var (order, sizes) = TileArrange.Drop([W(1), W(2)], OpenedAboveTheRightTile(), W(2), W(1), TileArrange.Zone.Bottom);

        Assert.Equal([W(1), W(2)], order);
        Assert.Null(sizes.Opening);
        Assert.Equal([2], sizes.Shape);
    }

    [Fact]
    public void A_tile_moved_into_the_opening_leaves_its_own_place()
    {
        var (order, sizes) = TileArrange.IntoOpening([W(1), W(2)], OpenedAboveTheRightTile(), W(1));

        Assert.Equal([W(1), W(2)], order);
        Assert.Null(sizes.Opening);
        Assert.Equal([2], sizes.Shape);
        Assert.Equal([1.0], sizes.Columns);
    }

    // --- a minimized tile coming back to a screen that changed (TileArrange.Returned, final review I1) ----------

    // W2 was the top of the right column; meanwhile W4 split W1 below it. W2's cell goes back into the right column
    // at its old row, taking half of W3's height, and the left column is untouched.
    [Fact]
    public void A_returning_tile_gets_a_new_row_in_its_old_column()
    {
        var (order, sizes) = TileArrange.Returned([W(1), W(4), W(3)], new TileSizes([0.5, 0.5], [[0.5, 0.5], [1.0]]), W(2), (1, 0));

        Assert.Equal([W(1), W(4), W(2), W(3)], order);
        Assert.Equal(TileSizes.Equal([2, 2]), sizes);
    }

    // Its old row is past the end of the column now: it goes to the bottom, halving the last row.
    [Fact]
    public void A_returning_tile_whose_row_is_gone_goes_to_the_bottom_of_its_column()
    {
        var (order, sizes) = TileArrange.Returned([W(1), W(3)], TileSizes.Equal([1, 1]), W(2), (0, 5));

        Assert.Equal([W(1), W(2), W(3)], order);
        Assert.Equal(new TileSizes([0.5, 0.5], [[0.5, 0.5], [1.0]]), sizes);
    }

    // Its old column is gone: a new column on the right, halving the last column, and nothing else moves.
    [Fact]
    public void A_returning_tile_whose_column_is_gone_gets_a_new_column()
    {
        var (order, sizes) = TileArrange.Returned([W(1)], TileSizes.Equal([1]), W(2), (1, 0));

        Assert.Equal([W(1), W(2)], order);
        Assert.Equal(TileSizes.Equal([1, 1]), sizes);
    }

    // An opening after the returning cell moves on one slot with the cells behind it, and stays open.
    [Fact]
    public void A_returning_tile_before_the_opening_moves_the_opening_on()
    {
        var kept = new TileSizes([0.5, 0.5], [[1.0], [0.5, 0.5]]) { Opening = 2 };

        var (order, sizes) = TileArrange.Returned([W(2), W(3)], kept, W(1), (0, 0));

        Assert.Equal([W(1), W(2), W(3)], order);
        Assert.Equal(TileSizes.Equal([2, 2]) with { Opening = 3 }, sizes);
    }

    // An opening before the returning cell keeps its slot, and the window's place in the order skips it.
    [Fact]
    public void A_returning_tile_after_the_opening_is_counted_past_it()
    {
        var kept = new TileSizes([0.5, 0.5], [[1.0], [0.5, 0.5]]) { Opening = 1 };

        var (order, sizes) = TileArrange.Returned([W(2), W(3)], kept, W(1), (1, 1));

        Assert.Equal([W(2), W(1), W(3)], order);
        Assert.Equal(new TileSizes([0.5, 0.5], [[1.0], [0.5, 0.25, 0.25]]) { Opening = 1 }, sizes);
    }

    // --- where a fit starts after a window left (TileArrange.FitStart, final review C1 and M1) -------------------

    // Nothing stored, one of three windows left: the fit starts from the equal grid of THREE, the grid the leaver's
    // cell was read from, and only that cell goes.
    [Fact]
    public void With_nothing_stored_one_leaver_starts_from_the_grid_it_left()
    {
        var (start, leaving) = TileArrange.FitStart(Maybe<TileSizes>.None, 2, Maybe.From((0, 0)), oldCount: 3);

        Assert.Equal(TileSizes.Equal([1, 2]), start);
        Assert.Equal((0, 0), leaving.GetValueOrThrow());
        Assert.Equal(TileSizes.Equal([2]), TileArrange.Refit(start, 2, leaving));
    }

    // C1: a cross-screen swap takes one window off the screen and puts another on, so the count is what it was. The
    // leaver is no leaver for the fit: the grid stays the equal grid of two, two columns.
    [Fact]
    public void A_leaver_with_the_count_unchanged_is_ignored()
    {
        var (start, leaving) = TileArrange.FitStart(Maybe<TileSizes>.None, 2, Maybe.From((0, 0)), oldCount: 2);

        Assert.Equal(TileSizes.Equal([1, 1]), start);
        Assert.True(leaving.HasNoValue);
        Assert.Equal(TileSizes.Equal([1, 1]), TileArrange.Refit(start, 2, leaving));
    }

    // The leaver's cell was read from sizes that are gone by the time the screen is fitted (a stack of three, then
    // nothing stored): (0, 2) is not a cell of the equal grid of three. Refit must not take a cell that grid lacks; it
    // falls back to the equal grid of what is left, and throws nothing.
    [Fact]
    public void A_leaver_whose_cell_the_start_does_not_hold_falls_back_to_the_equal_grid()
    {
        var (start, leaving) = TileArrange.FitStart(Maybe<TileSizes>.None, 2, Maybe.From((0, 2)), oldCount: 3);

        Assert.Equal(TileSizes.Equal(GridLayout.Shape(2)), TileArrange.Refit(start, 2, leaving));
    }

    // M1: two windows left at once with nothing stored. The fit starts from the grid the first leaver's cell was read
    // from (four windows), and two cells go: the leaver's, then by the fallback rule, which ends in the equal grid.
    [Fact]
    public void Two_leavers_at_once_with_nothing_stored_end_in_the_equal_grid()
    {
        var (start, leaving) = TileArrange.FitStart(Maybe<TileSizes>.None, 2, Maybe.From((0, 0)), oldCount: 4);

        Assert.Equal(TileSizes.Equal([2, 2]), start);
        Assert.Equal(TileSizes.Equal(GridLayout.Shape(2)), TileArrange.Refit(start, 2, leaving));
    }

    // Stored sizes are the start whatever the leaver: they already describe the grid the user sees.
    [Fact]
    public void Stored_sizes_are_the_start()
    {
        var stored = new TileSizes([0.7, 0.3], [[1.0], [1.0]]);

        var (start, _) = TileArrange.FitStart(Maybe.From(stored), 2, Maybe.From((0, 0)), oldCount: 2);

        Assert.Equal(stored, start);
    }
}
