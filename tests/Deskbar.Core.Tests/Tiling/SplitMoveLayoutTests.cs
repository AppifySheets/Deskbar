using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;
using Deskbar.Core.Tiling;
using static Deskbar.Core.Tests.Tiling.Trees;

namespace Deskbar.Core.Tests.Tiling;

// SplitMove on the split tree: a dragged edge moves the boundary of the nearest split of its axis in which the tile
// has a neighbour on that side, as a fraction of THAT split's span. The SplitMoveTests cases ported, plus nested ones.
public class SplitMoveLayoutTests
{
    static readonly WindowRect Area = new(0, 0, 1000, 600);

    static IReadOnlyList<double> Shares(TileNode node) => ((TileBranch)node).Children.Select(c => c.Share).ToList();

    static TileNode Child(TileNode node, int i) => ((TileBranch)node).Children[i].Node;

    static void AssertClose(IReadOnlyList<double> expected, IReadOnlyList<double> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        expected.Select((e, i) => (e, i)).ToList().ForEach(p => Assert.InRange(actual[p.i], p.e - 1e-9, p.e + 1e-9));
    }

    // Two full-height tiles, Across[.5, .5].
    static Maybe<TileLayout> Apply(int leaf, Edges moved, WindowRect frame, int gap = 0) =>
        SplitMove.Apply(TileLayout.Automatic(2), Area, gap, leaf, moved, frame);

    // A full-height tile on the left, a stack of two on the right.
    static Maybe<TileLayout> ApplyRows(int leaf, Edges moved, WindowRect frame) =>
        SplitMove.Apply(TileLayout.Automatic(3), Area, 0, leaf, moved, frame);

    static int GapBetweenColumnsAfter(TileLayout layout, int gap = 8)
    {
        var tiles = GridLayout.Arrange(Area, 2, gap, layout);
        return tiles[1].Left - tiles[0].Right;
    }

    [Fact] public void Dragging_the_left_tiles_right_edge_moves_the_split() =>
        AssertClose([0.7, 0.3], Shares(Apply(0, Edges.Right, new(0, 0, 700, 600)).Value.Root));

    [Fact] public void Dragging_the_right_tiles_left_edge_moves_the_same_split() =>
        AssertClose([0.6, 0.4], Shares(Apply(1, Edges.Left, new(600, 0, 1000, 600)).Value.Root));

    [Fact] public void The_split_stops_at_ten_percent() =>
        AssertClose([0.9, 0.1], Shares(Apply(0, Edges.Right, new(0, 0, 990, 600)).Value.Root));

    [Fact] public void An_outer_edge_has_no_split() =>
        Assert.True(Apply(0, Edges.Left, new(50, 0, 500, 600)).HasNoValue);

    // The stack's split is a fraction of the stack's own height, which here is the screen's.
    [Fact] public void A_row_split_moves_within_its_stack() =>
        AssertClose([1.0 / 3, 2.0 / 3], Shares(Child(ApplyRows(1, Edges.Bottom, new(500, 0, 1000, 200)).Value.Root, 1)));

    [Fact] public void With_a_gap_the_gap_stays_exact() =>
        Assert.Equal(8, GapBetweenColumnsAfter(Apply(0, Edges.Right, new(8, 8, 696, 592), gap: 8).Value));

    // Odd gap (7: near 3, far 4): the tile laid out again ends exactly where its edge was released.
    [Fact] public void With_an_odd_gap_a_right_edge_drag_lands_where_it_was_released()
    {
        var tiles = GridLayout.Arrange(Area, 2, 7, Apply(0, Edges.Right, new(7, 7, 696, 593), gap: 7).Value);
        Assert.Equal(696, tiles[0].Right);
        Assert.Equal(7, tiles[1].Left - tiles[0].Right);
    }

    [Fact] public void With_an_odd_gap_a_left_edge_drag_lands_where_it_was_released()
    {
        var tiles = GridLayout.Arrange(Area, 2, 7, Apply(1, Edges.Left, new(703, 7, 993, 593), gap: 7).Value);
        Assert.Equal(703, tiles[1].Left);
        Assert.Equal(7, tiles[1].Left - tiles[0].Right);
    }

    // A corner moves one split per axis: the root's Across and the left stack's Down.
    [Fact] public void A_corner_moves_the_column_split_and_that_columns_row_split()
    {
        var layout = SplitMove.Apply(Tree(Across(C(Down(C(L, .5), C(L, .5)), .5), C(L, .5))), Area, 0, 0, Edges.Right | Edges.Bottom, new(0, 0, 700, 200)).Value;
        AssertClose([0.7, 0.3], Shares(layout.Root));
        AssertClose([1.0 / 3, 2.0 / 3], Shares(Child(layout.Root, 0)));
    }

    [Fact] public void Outer_edges_alongside_an_inner_one_are_ignored()
    {
        var layout = Apply(0, Edges.Right | Edges.Bottom, new(0, 0, 700, 400)).Value;
        Is("A[L 0.7, L 0.3]", layout);
    }

    // Petre's layout, Down[Across[A, C], B]. A's bottom edge belongs to the root Down (A and C are one row), so it moves
    // the boundary between the top pair and B, and the pair keeps its own shares.
    [Fact] public void SplitMove_bottom_edge_of_a_top_pair_moves_the_shared_split()
    {
        var pair = Tree(Down(C(Across(C(L, .5), C(L, .5)), .5), C(L, .5)));

        var layout = SplitMove.Apply(pair, Area, 0, 0, Edges.Bottom, new(0, 0, 500, 400)).Value;

        Is("D[A[L 0.5, L 0.5] 0.666667, L 0.333333]", layout);
    }

    // The same layout as the screen's right half. A's right edge belongs to the inner Across, whose span is 500 px
    // starting at 500: released at 800, the boundary is at 0.6 of THAT span (0.8 of the screen would be wrong).
    [Fact] public void SplitMove_inside_a_nested_branch_uses_that_branch_span()
    {
        var nested = Tree(Across(C(L, .5), C(Down(C(Across(C(L, .5), C(L, .5)), .5), C(L, .5)), .5)));

        var layout = SplitMove.Apply(nested, Area, 0, 1, Edges.Right, new(500, 0, 800, 300)).Value;

        Is("A[L 0.5, D[A[L 0.6, L 0.4] 0.5, L 0.5] 0.5]", layout);
    }

    // The MinShare floor is of the split's own span: the inner pair cannot go under 10% of the 500 px it divides.
    [Fact] public void A_nested_split_stops_at_ten_percent_of_its_own_span()
    {
        var nested = Tree(Across(C(L, .5), C(Down(C(Across(C(L, .5), C(L, .5)), .5), C(L, .5)), .5)));

        var layout = SplitMove.Apply(nested, Area, 0, 1, Edges.Right, new(500, 0, 990, 300)).Value;

        Is("A[L 0.5, D[A[L 0.9, L 0.1] 0.5, L 0.5] 0.5]", layout);
    }

    // The opening rides along: moving a split never changes which leaf is empty.
    [Fact] public void The_opening_is_kept()
    {
        var opened = Tree(Across(C(L, .5), C(L, .5)), opening: 1);

        Assert.Equal(1, SplitMove.Apply(opened, Area, 0, 0, Edges.Right, new(0, 0, 700, 600)).Value.Opening);
    }

    static void Is(string expected, TileLayout layout) => Assert.Equal(expected, Text(layout));
}
