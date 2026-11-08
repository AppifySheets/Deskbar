using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;
using Deskbar.Core.Tiling;

namespace Deskbar.Core.Tests.Tiling;

// SplitMove: dragging a tile's edge moves the split between it and its neighbour, in fractions.
public class SplitMoveTests
{
    static readonly WindowRect Area = new(0, 0, 1000, 600);

    static void AssertClose(IReadOnlyList<double> expected, IReadOnlyList<double> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        expected.Select((e, i) => (e, i)).ToList().ForEach(p => Assert.InRange(actual[p.i], p.e - 1e-9, p.e + 1e-9));
    }

    // Two columns of one row each, [0.5, 0.5].
    static Maybe<TileSizes> Apply((int Column, int Row) cell, Edges moved, WindowRect frame, int gap = 0) =>
        SplitMove.Apply(TileSizes.Equal([1, 1]), [1, 1], Area, gap, cell, moved, frame);

    // Two columns, column 1 holding two equal rows.
    static Maybe<TileSizes> ApplyRows((int Column, int Row) cell, Edges moved, WindowRect frame) =>
        SplitMove.Apply(TileSizes.Equal([1, 2]), [1, 2], Area, 0, cell, moved, frame);

    // The distance between the two columns' tiles once the grid is laid out with these sizes.
    static int GapBetweenColumnsAfter(TileSizes sizes, int gap = 8)
    {
        var tiles = GridLayout.Arrange(Area, 2, gap, sizes);
        return tiles[1].Left - tiles[0].Right;
    }

    [Fact] public void Dragging_the_left_columns_right_edge_moves_the_split() =>
        AssertClose([0.7, 0.3], Apply((0, 0), Edges.Right, new(0, 0, 700, 600)).Value.Columns);

    [Fact] public void Dragging_the_right_columns_left_edge_moves_the_same_split() =>
        AssertClose([0.6, 0.4], Apply((1, 0), Edges.Left, new(600, 0, 1000, 600)).Value.Columns);

    [Fact] public void The_split_stops_at_ten_percent() =>
        AssertClose([0.9, 0.1], Apply((0, 0), Edges.Right, new(0, 0, 990, 600)).Value.Columns);

    [Fact] public void An_outer_edge_has_no_split() =>
        Assert.True(Apply((0, 0), Edges.Left, new(50, 0, 500, 600)).HasNoValue);

    [Fact] public void A_row_split_moves_within_its_column() =>
        AssertClose([1.0 / 3, 2.0 / 3], ApplyRows((1, 0), Edges.Bottom, new(500, 0, 1000, 200)).Value.Rows[1]);

    [Fact] public void With_a_gap_the_gap_stays_exact() =>
        Assert.Equal(8, GapBetweenColumnsAfter(Apply((0, 0), Edges.Right, new(8, 8, 696, 592), gap: 8).Value));

    // Odd gap: near and far differ (3 and 4), so swapping them would show here.
    [Fact] public void With_an_odd_gap_a_right_edge_drag_keeps_the_gap_exact() =>
        Assert.Equal(7, GapBetweenColumnsAfter(Apply((0, 0), Edges.Right, new(7, 7, 696, 593), gap: 7).Value, gap: 7));

    [Fact] public void With_an_odd_gap_a_left_edge_drag_keeps_the_gap_exact() =>
        Assert.Equal(7, GapBetweenColumnsAfter(Apply((1, 0), Edges.Left, new(703, 7, 993, 593), gap: 7).Value, gap: 7));

    [Fact] public void A_corner_moves_the_column_split_and_that_columns_row_split()
    {
        var sizes = SplitMove.Apply(TileSizes.Equal([2, 1]), [2, 1], Area, 0, (0, 0), Edges.Right | Edges.Bottom, new(0, 0, 700, 200)).Value;
        AssertClose([0.7, 0.3], sizes.Columns);
        AssertClose([1.0 / 3, 2.0 / 3], sizes.Rows[0]);
    }

    [Fact] public void Outer_edges_alongside_an_inner_one_are_ignored()
    {
        var sizes = Apply((0, 0), Edges.Right | Edges.Bottom, new(0, 0, 700, 400)).Value;
        AssertClose([0.7, 0.3], sizes.Columns);
        AssertClose([1.0], sizes.Rows[0]);
    }
}
