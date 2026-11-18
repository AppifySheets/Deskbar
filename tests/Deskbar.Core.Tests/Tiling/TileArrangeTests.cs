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

    // Even sizes on a custom shape are still worth storing; even sizes on the automatic shape are not.
    [Fact]
    public void Only_even_sizes_on_the_automatic_shape_are_plain()
    {
        Assert.True(TileArrange.IsPlain(TileSizes.Equal([1, 1])));
        Assert.False(TileArrange.IsPlain(TileSizes.Equal([2])));
    }
}
