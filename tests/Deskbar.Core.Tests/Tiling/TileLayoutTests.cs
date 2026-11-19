using System.Text.Json;
using CSharpFunctionalExtensions;
using System.Text.Json.Serialization;
using Deskbar.Core.Domain;
using Deskbar.Core.Tiling;

namespace Deskbar.Core.Tests.Tiling;

// The split tree's model and its layout arithmetic. The key promise: a layout migrated from the old columns-of-rows
// sizes lays out pixel-identical to the grid, so switching the Tiler over moved nothing on screen. The grid's own
// arithmetic is gone from the app, so OldGrid below keeps it as the oracle.
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
                Assert.Equal(OldGrid.Arrange(t.area, n, t.gap, OldGrid.Equal(GridLayout.Shape(n))), GridLayout.Arrange(t.area, n, t.gap, TileLayout.Automatic(n)))));

    // Uneven sizes for a shape: column weights 1..k and row weights 1..r, each list normalised to sum 1.
    static TileSizes Uneven(params int[] shape) =>
        new(Weights(shape.Length), shape.Select(Weights).ToList());

    static IReadOnlyList<double> Weights(int count) =>
        Enumerable.Range(1, count).Select(i => (double)i / (count * (count + 1) / 2)).ToList();

    // THE ORACLE for the migration promise: the columns-of-rows grid's arithmetic exactly as GridLayout had it for
    // TileSizes before the grid code was deleted (split tree plan, task 7). Kept here, and only here, so "a migrated
    // grid lays out pixel-identical" still compares against what the old build put on screen, not against the tree
    // code it is meant to check. Shrink by the outer gap, divide the width by the column fractions and each column's
    // height by its row fractions (Edges), then inset each cell on its inner edges by half the gap.
    static class OldGrid
    {
        public static TileSizes Equal(IReadOnlyList<int> shape) =>
            new(Even(shape.Count), shape.Select(Even).ToList());

        public static IReadOnlyList<WindowRect> Arrange(WindowRect area, int count, int gap, TileSizes sizes) =>
            HasOpeningFor(sizes, count)
                ? Cells(area, count + 1, gap, sizes).Where((_, slot) => slot != sizes.Opening).ToList()
                : Cells(area, count, gap, sizes);

        public static Maybe<WindowRect> OpeningOf(WindowRect area, int count, int gap, TileSizes sizes) =>
            HasOpeningFor(sizes, count) && sizes.Opening is { } open
                ? Cells(area, count + 1, gap, sizes)[open]
                : Maybe<WindowRect>.None;

        static IReadOnlyList<double> Even(int count) => Enumerable.Repeat(1.0 / count, count).ToList();

        static IReadOnlyList<int> Shape(TileSizes sizes) => sizes.Rows.Select(r => r.Count).ToList();

        static bool HasOpeningFor(TileSizes sizes, int count) =>
            sizes.Opening is { } open && Shape(sizes).Sum() == count + 1 && open >= 0 && open <= count;

        static IReadOnlyList<WindowRect> Cells(WindowRect area, int count, int gap, TileSizes sizes)
        {
            if (count <= 0) return [];
            var inner = GridLayout.InnerArea(area, gap);
            var shape = Shape(sizes).Sum() == count ? Shape(sizes) : GridLayout.Shape(count);
            var xs = Edges(inner.Width, sizes.Columns);
            var (near, far) = GridLayout.HalfGaps(gap);
            return shape
                .SelectMany((rows, column) =>
                {
                    var ys = Edges(inner.Height, sizes.Rows[column]);
                    return Enumerable.Range(0, rows).Select(row => new WindowRect(
                        inner.Left + xs[column] + (column > 0 ? near : 0),
                        inner.Top + ys[row] + (row > 0 ? near : 0),
                        inner.Left + xs[column + 1] - (column < shape.Count - 1 ? far : 0),
                        inner.Top + ys[row + 1] - (row < rows - 1 ? far : 0)));
                })
                .ToList();
        }

        // Even fractions take floor(span / n) per entry, uneven ones round the running total; the last boundary is
        // always the span's end.
        static IReadOnlyList<int> Edges(int span, IReadOnlyList<double> fractions)
        {
            var n = fractions.Count;
            var even = fractions.All(f => Math.Abs(f - 1.0 / n) < 1e-9);
            var running = 0.0;
            var inner = fractions.Take(n - 1).Select((f, i) =>
            {
                running += f;
                return even ? (i + 1) * (span / n) : (int)Math.Round(running * span);
            });
            return new[] { 0 }.Concat(inner).Append(span).ToList();
        }
    }

    public static IEnumerable<object[]> Shapes() =>
        new[] { new[] { 1 }, [2], [1, 1], [1, 2], [2, 1], [2, 2], [1, 2, 2], [3, 3, 3] }.Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Migrated_grids_lay_out_pixel_identical(int[] shape)
    {
        var sizes = Uneven(shape);
        var n = shape.Sum();
        Areas.SelectMany(_ => Gaps, (area, gap) => (area, gap)).ToList().ForEach(t =>
            Assert.Equal(OldGrid.Arrange(t.area, n, t.gap, sizes), GridLayout.Arrange(t.area, n, t.gap, TileLayout.FromGrid(sizes))));
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
            Assert.Equal(OldGrid.Arrange(t.area, n - 1, t.gap, sizes), GridLayout.Arrange(t.area, n - 1, t.gap, layout));
            Assert.Equal(OldGrid.OpeningOf(t.area, n - 1, t.gap, sizes), GridLayout.OpeningOf(t.area, n - 1, t.gap, layout));
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
