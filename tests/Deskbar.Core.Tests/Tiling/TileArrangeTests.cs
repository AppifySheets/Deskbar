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
}
