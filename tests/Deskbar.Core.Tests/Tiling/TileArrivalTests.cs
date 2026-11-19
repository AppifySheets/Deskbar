using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;
using Deskbar.Core.Tiling;

namespace Deskbar.Core.Tests.Tiling;

// Which tile a newly arrived window splits (TileArrival). Tiles are 1920x1040 screens laid out with no gap, so the
// pixel shapes are easy to reason about, while the floor and the largest-tile rule read the fractions in the sizes.
public sealed class TileArrivalTests
{
    static WindowHandle W(int n) => new(n);

    static IReadOnlyList<WindowHandle> Order(int count) => Enumerable.Range(1, count).Select(W).ToList();

    static IReadOnlyList<WindowRect> Tiles(int count, TileSizes sizes) =>
        GridLayout.Arrange(new WindowRect(0, 0, 1920, 1040), count, 0, sizes);

    static TileSizes Sized(double[] columns, double[][] rows) =>
        new(columns, rows.Select(r => (IReadOnlyList<double>)r).ToList());

    static Maybe<TileSplit> Choose(int count, TileSizes sizes, Maybe<WindowHandle> lastUsed) =>
        TileArrival.Choose(Order(count), sizes, Tiles(count, sizes), lastUsed);

    [Fact]
    public void One_wide_tile_is_split_to_its_right() =>
        Assert.Equal(Maybe.From(new TileSplit(W(1), TileArrange.Zone.Right)),
            Choose(1, TileSizes.Equal([1]), Maybe.From(W(1))));

    [Fact]
    public void A_tall_tile_is_split_below() =>
        Assert.Equal(Maybe.From(new TileSplit(W(1), TileArrange.Zone.Bottom)),
            Choose(2, TileSizes.Equal([1, 1]), Maybe.From(W(1))));

    [Fact]
    public void A_wide_tile_in_a_column_of_several_rows_is_split_below() =>
        Assert.Equal(Maybe.From(new TileSplit(W(2), TileArrange.Zone.Bottom)),
            Choose(2, Sized([1.0], [[0.5, 0.5]]), Maybe.From(W(2))));

    [Fact]
    public void A_last_used_tile_below_the_floor_hands_the_split_to_the_largest() =>
        Assert.Equal(Maybe.From(new TileSplit(W(1), TileArrange.Zone.Bottom)),
            Choose(3, Sized([0.5, 0.5], [[1.0], [0.75, 0.25]]), Maybe.From(W(3))));

    [Fact]
    public void With_no_last_used_tile_the_largest_is_split() =>
        Assert.Equal(Maybe.From(new TileSplit(W(1), TileArrange.Zone.Bottom)),
            Choose(3, Sized([0.5, 0.5], [[1.0], [0.5, 0.5]]), Maybe<WindowHandle>.None));

    [Fact]
    public void A_tie_goes_to_the_tile_latest_in_slot_order() =>
        Assert.Equal(Maybe.From(new TileSplit(W(2), TileArrange.Zone.Bottom)),
            Choose(2, TileSizes.Equal([1, 1]), Maybe<WindowHandle>.None));

    [Fact]
    public void When_every_tile_is_below_the_floor_the_largest_is_still_split() =>
        Assert.Equal(Maybe.From(new TileSplit(W(6), TileArrange.Zone.Bottom)),
            Choose(6, Sized([0.5, 0.5], [[0.3, 0.3, 0.4], [0.3, 0.3, 0.4]]), Maybe<WindowHandle>.None));

    [Fact]
    public void A_last_used_window_not_on_this_screen_is_ignored() =>
        Assert.Equal(Maybe.From(new TileSplit(W(2), TileArrange.Zone.Bottom)),
            Choose(2, TileSizes.Equal([1, 1]), Maybe.From(W(9))));

    [Fact]
    public void Nothing_to_split_on_an_empty_screen() =>
        Assert.Equal(Maybe<TileSplit>.None,
            TileArrival.Choose([], TileSizes.Equal([]), [], Maybe.From(W(1))));
}
