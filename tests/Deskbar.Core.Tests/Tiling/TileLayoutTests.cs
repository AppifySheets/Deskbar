using System.Text.Json;
using System.Text.Json.Serialization;
using Deskbar.Core.Domain;
using Deskbar.Core.Tiling;

namespace Deskbar.Core.Tests.Tiling;

// The split tree's model and its layout arithmetic. The key promise: a layout migrated from today's columns-of-rows
// sizes lays out pixel-identical to the grid, so switching the Tiler over moves nothing on screen.
public class TileLayoutTests
{
    static readonly WindowRect[] Areas = [new(0, 0, 1000, 600), new(-1920, 0, 0, 1040)];
    static readonly int[] Gaps = [0, 7, 8];

    static TileNode L => TileLeaf.Instance;
    static TileChild C(TileNode node, double share) => new(node, share);
    static TileNode Across(params TileChild[] kids) => new TileBranch(Axis.Across, kids.ToValueList());
    static TileNode Down(params TileChild[] kids) => new TileBranch(Axis.Down, kids.ToValueList());

    [Fact]
    public void Automatic_matches_the_grid_shape() =>
        Areas.SelectMany(_ => Gaps, (area, gap) => (area, gap)).ToList().ForEach(t =>
            Enumerable.Range(1, 9).ToList().ForEach(n =>
                Assert.Equal(GridLayout.Arrange(t.area, n, t.gap), GridLayout.Arrange(t.area, n, t.gap, TileLayout.Automatic(n)))));

    // Uneven sizes for a shape: column weights 1..k and row weights 1..r, each list normalised to sum 1.
    static TileSizes Uneven(params int[] shape) =>
        new(Weights(shape.Length), shape.Select(Weights).ToList());

    static IReadOnlyList<double> Weights(int count) =>
        Enumerable.Range(1, count).Select(i => (double)i / (count * (count + 1) / 2)).ToList();

    public static IEnumerable<object[]> Shapes() =>
        new[] { new[] { 1 }, [2], [1, 1], [1, 2], [2, 1], [2, 2], [1, 2, 2], [3, 3, 3] }.Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Migrated_grids_lay_out_pixel_identical(int[] shape)
    {
        var sizes = Uneven(shape);
        var n = shape.Sum();
        Areas.SelectMany(_ => Gaps, (area, gap) => (area, gap)).ToList().ForEach(t =>
            Assert.Equal(GridLayout.Arrange(t.area, n, t.gap, sizes), GridLayout.Arrange(t.area, n, t.gap, TileLayout.FromGrid(sizes))));
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Migrated_grids_with_an_opening_agree_on_the_opening_too(int[] shape)
    {
        var n = shape.Sum();
        if (n < 2) return; // an opening needs a window beside it
        var sizes = Uneven(shape) with { Opening = n - 1 };
        var layout = TileLayout.FromGrid(sizes);
        Areas.SelectMany(_ => Gaps, (area, gap) => (area, gap)).ToList().ForEach(t =>
        {
            Assert.Equal(GridLayout.Arrange(t.area, n - 1, t.gap, sizes), GridLayout.Arrange(t.area, n - 1, t.gap, layout));
            Assert.Equal(GridLayout.OpeningOf(t.area, n - 1, t.gap, sizes), GridLayout.OpeningOf(t.area, n - 1, t.gap, layout));
        });
    }

    [Fact]
    public void Normal_form_collapses_single_children_and_merges_same_axis()
    {
        var merged = TileLayout.Normal(Across(C(Across(C(L, .5), C(L, .5)), .5), C(L, .5)), null);
        Assert.Equal(Across(C(L, .25), C(L, .25), C(L, .5)), merged.Root);

        var collapsed = TileLayout.Normal(Down(C(Across(C(L, .5), C(L, .5)), 1.0)), null);
        Assert.Equal(Across(C(L, .5), C(L, .5)), collapsed.Root);
    }

    [Fact]
    public void Petre_layout_cells() =>
        Assert.Equal(
            [new WindowRect(0, 0, 500, 300), new WindowRect(500, 0, 1000, 300), new WindowRect(0, 300, 1000, 600)],
            GridLayout.CellsOf(new WindowRect(0, 0, 1000, 600), 0,
                TileLayout.Normal(Down(C(Across(C(L, .5), C(L, .5)), .5), C(L, .5)), null)));

    [Fact]
    public void ShareOf_multiplies_along_the_path()
    {
        var layout = TileLayout.Normal(Down(C(Across(C(L, .5), C(L, .5)), .5), C(L, .5)), null);
        Assert.Equal((0.5, 0.5), layout.ShareOf(1));
        Assert.Equal((1.0, 0.5), layout.ShareOf(2));
    }

    [Fact]
    public void Count_counts_the_opening_and_the_opening_is_left_out_of_arrange()
    {
        var layout = TileLayout.Normal(Across(C(L, .5), C(L, .5)), 0);
        Assert.Equal(2, layout.Count);
        Assert.True(layout.HasOpeningFor(1));
        Assert.Single(GridLayout.Arrange(new WindowRect(0, 0, 1000, 600), 1, 0, layout));
    }

    [Fact]
    public void Evened_keeps_structure()
    {
        var layout = TileLayout.Normal(Down(C(Across(C(L, .3), C(L, .7)), .2), C(L, .8)), 1);
        Assert.False(layout.IsEven);
        var even = layout.Evened();
        Assert.True(even.IsEven);
        Assert.True(even.SameShapeAs(layout));
        Assert.Equal(1, even.Opening);
        Assert.Equal(Down(C(Across(C(L, .5), C(L, .5)), .5), C(L, .5)), even.Root);
    }

    [Fact]
    public void SameShapeAs_ignores_shares()
    {
        var a = TileLayout.Normal(Across(C(L, .3), C(L, .7)), null);
        var b = TileLayout.Normal(Across(C(L, .5), C(L, .5)), null);
        Assert.True(a.SameShapeAs(b));
        Assert.False(a.SameShapeAs(TileLayout.Normal(Down(C(L, .5), C(L, .5)), null)));
        Assert.False(a.SameShapeAs(TileLayout.Automatic(3)));
    }

    [Fact]
    public void Json_round_trips()
    {
        var options = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
        var layout = TileLayout.Normal(Down(C(Across(C(L, .4), C(L, .6)), .3), C(L, .7)), 2);
        var json = JsonSerializer.Serialize(layout, options);
        Assert.Equal(layout, JsonSerializer.Deserialize<TileLayout>(json, options));
        // Computed members are not stored (ruling R3).
        Assert.DoesNotContain("\"Count\"", json);
        Assert.DoesNotContain("\"IsEven\"", json);
    }
}
