using Deskbar.Core.Domain;
using Deskbar.Core.Tiling;
using static Deskbar.Core.Tests.Tiling.Trees;

namespace Deskbar.Core.Tests.Tiling;

// SplitMove at a "+" crossing (docs/superpowers/specs/2026-11-23-tile-local-split-design.md). Petre: "the split could
// be between a and c, which keeps b and d as they are". Where the dragged line is crossed straight through by a line
// of the other direction, only the piece beside the dragged tile moves: the two children either side of the dragged
// boundary are regrouped into bands at the crossings around the tile, and the band's own split moves. A T join, a
// corner drag and boundaries more than 2 px apart keep today's whole-line behaviour.
//
// The 2x2 cases name the tiles as on screen: A B on top, C D underneath. Grouped as columns the leaves are A C B D
// (depth first); grouped as rows they are A B C D. Order[newLeaf] is the old leaf now in that place.
public class SplitMoveCrossingTests
{
    static readonly WindowRect Area = new(0, 0, 1000, 600);

    static WindowHandle W(int n) => new(n);

    static IReadOnlyList<double> Shares(TileNode node) => ((TileBranch)node).Children.Select(c => c.Share).ToList();

    static void AssertClose(IReadOnlyList<double> expected, IReadOnlyList<double> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        expected.Select((e, i) => (e, i)).ToList().ForEach(p => Assert.InRange(actual[p.i], p.e - 1e-9, p.e + 1e-9));
    }

    // Two even columns, each two even rows: leaves A=0, C=1, B=2, D=3.
    static readonly TileLayout Grid2x2Columns =
        Tree(Across(C(Down(C(L, .5), C(L, .5)), .5), C(Down(C(L, .5), C(L, .5)), .5)));

    // Two even rows, each two even columns: leaves A=0, B=1, C=2, D=3.
    static readonly TileLayout Grid2x2Rows =
        Tree(Down(C(Across(C(L, .5), C(L, .5)), .5), C(Across(C(L, .5), C(L, .5)), .5)));

    static TileNode Even(Axis axis, int n) =>
        new TileBranch(axis, Enumerable.Repeat(new TileChild(L, 1.0 / n), n).ToValueList());

    // Two columns of n even rows each, so every row line is a crossing.
    static TileLayout Columns(int rows) => Tree(Across(C(Even(Axis.Down, rows), .5), C(Even(Axis.Down, rows), .5)));

    [Fact]
    public void Dragging_A_B_in_columns_moves_only_the_top_piece()
    {
        var moved = SplitMove.Apply(Grid2x2Columns, Area, 0, 0, Edges.Right, new(0, 0, 700, 300)).Value;
        Assert.Equal("D[A[L 0.7, L 0.3] 0.5, A[L 0.5, L 0.5] 0.5]", Text(moved.Layout.Root));
        Assert.Equal([0, 2, 1, 3], moved.Order);
    }

    [Fact]
    public void Dragging_A_C_in_rows_moves_only_the_left_piece()
    {
        var moved = SplitMove.Apply(Grid2x2Rows, Area, 0, 0, Edges.Bottom, new(0, 0, 500, 400)).Value;
        Assert.Equal("A[D[L 0.666667, L 0.333333] 0.5, D[L 0.5, L 0.5] 0.5]", Text(moved.Layout.Root));
        Assert.Equal([0, 2, 1, 3], moved.Order);
    }

    // C and D (old leaves 1 and 3) are laid out in exactly the same pixels before and after, gap and all.
    [Fact]
    public void The_other_pieces_tiles_do_not_move_a_pixel()
    {
        var before = GridLayout.Arrange(Area, 4, 8, Grid2x2Columns);
        var moved = SplitMove.Apply(Grid2x2Columns, Area, 8, 0, Edges.Right, new(8, 8, 696, 296)).Value;
        var after = GridLayout.Arrange(Area, 4, 8, moved.Layout);
        Assert.Equal(before[1], after[moved.LeafOf(1)]);
        Assert.Equal(before[3], after[moved.LeafOf(3)]);
        Assert.Equal(696, after[moved.LeafOf(0)].Right);
    }

    // Three aligned rows each side: a drag from the middle row moves only the middle piece.
    [Fact]
    public void A_middle_row_drag_cuts_three_bands()
    {
        var moved = SplitMove.Apply(Columns(3), Area, 0, 1, Edges.Right, new(0, 200, 700, 400)).Value;
        Assert.Equal("D[A[L 0.5, L 0.5] 0.333333, A[L 0.7, L 0.3] 0.333333, A[L 0.5, L 0.5] 0.333333]", Text(moved.Layout.Root));
        Assert.Equal([0, 3, 1, 4, 2, 5], moved.Order);
    }

    // Four aligned rows, drag the top one: the rows past the next crossing stay grouped as one band.
    [Fact]
    public void Rows_past_the_next_crossing_stay_one_band()
    {
        var moved = SplitMove.Apply(Columns(4), Area, 0, 0, Edges.Right, new(0, 0, 700, 150)).Value;
        Assert.Equal(
            "D[A[L 0.7, L 0.3] 0.25, A[D[L 0.333333, L 0.333333, L 0.333333] 0.5, D[L 0.333333, L 0.333333, L 0.333333] 0.5] 0.75]",
            Text(moved.Layout.Root));
        Assert.Equal([0, 4, 1, 2, 3, 5, 6, 7], moved.Order);
    }

    // A and C side by side over a full-width B: A's bottom edge meets a T, so the whole line moves, as today.
    [Fact]
    public void A_T_join_moves_the_whole_line()
    {
        var moved = SplitMove.Apply(Tree(Down(C(Across(C(L, .5), C(L, .5)), .5), C(L, .5))), Area, 0, 0, Edges.Bottom, new(0, 0, 500, 400)).Value;
        AssertClose([2.0 / 3, 1.0 / 3], Shares(moved.Layout.Root));
        Assert.Equal([0, 1, 2], moved.Order);
    }

    [Fact]
    public void Boundaries_three_pixels_apart_are_not_a_crossing()
    {
        var layout = Tree(Across(C(Down(C(L, .5), C(L, .5)), .5), C(Down(C(L, 303.0 / 600), C(L, 297.0 / 600)), .5)));
        var moved = SplitMove.Apply(layout, Area, 0, 0, Edges.Right, new(0, 0, 700, 300)).Value;
        Assert.Equal(Axis.Across, ((TileBranch)moved.Layout.Root).Axis);
        AssertClose([0.7, 0.3], Shares(moved.Layout.Root));
        Assert.Equal([0, 1, 2, 3], moved.Order);
    }

    [Fact]
    public void Boundaries_two_pixels_apart_are_a_crossing()
    {
        var layout = Tree(Across(C(Down(C(L, .5), C(L, .5)), .5), C(Down(C(L, 302.0 / 600), C(L, 298.0 / 600)), .5)));
        var moved = SplitMove.Apply(layout, Area, 0, 0, Edges.Right, new(0, 0, 700, 300)).Value;
        Assert.Equal(Axis.Down, ((TileBranch)moved.Layout.Root).Axis);
        Assert.Equal([0, 2, 1, 3], moved.Order);
    }

    // Moving the crossing point itself moves every tile that meets it: both whole lines, no regroup.
    [Fact]
    public void A_corner_at_a_crossing_moves_both_whole_lines()
    {
        var moved = SplitMove.Apply(Grid2x2Columns, Area, 0, 0, Edges.Right | Edges.Bottom, new(0, 0, 700, 400)).Value;
        Assert.Equal("A[D[L 0.666667, L 0.333333] 0.7, D[L 0.5, L 0.5] 0.3]", Text(moved.Layout.Root));
        Assert.Equal([0, 1, 2, 3], moved.Order);
    }

    // C (old leaf 1) is the opening; after the regroup it is the third leaf.
    [Fact]
    public void The_opening_follows_the_regroup()
    {
        var moved = SplitMove.Apply(Grid2x2Columns with { Opening = 1 }, Area, 0, 0, Edges.Right, new(0, 0, 700, 300)).Value;
        Assert.Equal(2, moved.Layout.Opening);
    }

    // The top-left place is itself two tiles side by side; normal form merges that pair into the band's Across.
    [Fact]
    public void A_band_that_is_already_split_across_is_merged()
    {
        var layout = Tree(Across(
            C(Down(C(Across(C(L, .5), C(L, .5)), .5), C(L, .5)), .5),
            C(Down(C(L, .5), C(L, .5)), .5)));
        var moved = SplitMove.Apply(layout, Area, 0, 1, Edges.Right, new(250, 0, 700, 300)).Value;
        Assert.Equal("D[A[L 0.25, L 0.45, L 0.3] 0.5, A[L 0.5, L 0.5] 0.5]", Text(moved.Layout.Root));
        Assert.Equal([0, 1, 3, 2, 4], moved.Order);
    }

    [Fact]
    public void Windows_follow_the_order()
    {
        var moved = SplitMove.Apply(Grid2x2Columns, Area, 0, 0, Edges.Right, new(0, 0, 700, 300)).Value;
        Assert.Equal([W(1), W(3), W(2), W(4)], moved.Windows([W(1), W(2), W(3), W(4)], Grid2x2Columns));
    }

    // With an opening the windows skip it: C's place is the opening, so three windows map to A, B and D.
    [Fact]
    public void Windows_skip_the_opening()
    {
        var before = Grid2x2Columns with { Opening = 1 };
        var moved = SplitMove.Apply(before, Area, 0, 0, Edges.Right, new(0, 0, 700, 300)).Value;
        Assert.Equal([W(1), W(2), W(3)], moved.Windows([W(1), W(2), W(3)], before));
    }
}
