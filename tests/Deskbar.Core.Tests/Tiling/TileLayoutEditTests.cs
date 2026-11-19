using Deskbar.Core.Domain;
using Deskbar.Core.Tiling;

namespace Deskbar.Core.Tests.Tiling;

// Split tree edits: a leaver, a drop beside a tile and a drop in the screen-edge band. Every result is checked for
// normal form, and compared as text ("A[L .5, D[L .5, L .5] .5]") so a failure shows both trees.
public class TileLayoutEditTests
{
    static TileNode L => TileLeaf.Instance;
    static TileChild C(TileNode node, double share) => new(node, share);
    static TileNode Across(params TileChild[] kids) => new TileBranch(Axis.Across, kids.ToValueList());
    static TileNode Down(params TileChild[] kids) => new TileBranch(Axis.Down, kids.ToValueList());
    static TileLayout Tree(TileNode root, int? opening = null) => new(root) { Opening = opening };

    // Shares rounded to 6 places so .3 / .8 style arithmetic compares equal to .375.
    static string Text(TileNode node) =>
        node is TileBranch b
            ? $"{(b.Axis == Axis.Across ? "A" : "D")}[{string.Join(", ", b.Children.Select(c => $"{Text(c.Node)} {Math.Round(c.Share, 6)}"))}]"
            : "L";

    // No branch with fewer than two children, no child branch with its parent's axis, shares sum to 1.
    static TileLayout AssertNormal(TileLayout layout)
    {
        void Check(TileNode node)
        {
            if (node is not TileBranch b) return;
            Assert.True(b.Children.Count >= 2, "a split needs two children: " + Text(layout.Root));
            Assert.All(b.Children, c => Assert.False(c.Node is TileBranch inner && inner.Axis == b.Axis, "same axis nested: " + Text(layout.Root)));
            Assert.Equal(1.0, b.Children.Sum(c => c.Share), 9);
            b.Children.ToList().ForEach(c => Check(c.Node));
        }
        Check(layout.Root);
        return layout;
    }

    static void Is(string expected, TileLayout layout) => Assert.Equal(expected, Text(AssertNormal(layout).Root));

    [Fact]
    public void Lifting_the_right_column_leaves_the_stack() =>
        Is("D[L 0.5, L 0.5]", Tree(Across(C(Down(C(L, .5), C(L, .5)), .5), C(L, .5))).Remove(2));

    [Fact]
    public void Petre_case_dropping_beside_the_top_tile()
    {
        var (layout, leaf) = Tree(Down(C(L, .5), C(L, .5))).Insert(0, TileArrange.Zone.Right);
        Is("D[A[L 0.5, L 0.5] 0.5, L 0.5]", layout);
        Assert.Equal(1, leaf);
    }

    [Fact]
    public void Beside_the_bottom_tile_is_the_mirror()
    {
        var (layout, leaf) = Tree(Down(C(L, .5), C(L, .5))).Insert(1, TileArrange.Zone.Right);
        Is("D[L 0.5, A[L 0.5, L 0.5] 0.5]", layout);
        Assert.Equal(2, leaf);
    }

    [Fact]
    public void Inserting_along_the_parent_axis_halves_only_the_target()
    {
        var (layout, leaf) = Tree(Across(C(L, .25), C(L, .75))).Insert(1, TileArrange.Zone.Left);
        Is("A[L 0.25, L 0.375, L 0.375]", layout);
        Assert.Equal(1, leaf);
    }

    [Fact]
    public void A_lone_leaf_splits()
    {
        var (layout, leaf) = new TileLayout(TileLeaf.Instance).Insert(0, TileArrange.Zone.Bottom);
        Is("D[L 0.5, L 0.5]", layout);
        Assert.Equal(1, leaf);
    }

    [Fact]
    public void Removing_shares_in_proportion() =>
        Is("A[L 0.375, L 0.625]", Tree(Across(C(L, .2), C(L, .3), C(L, .5))).Remove(0));

    [Fact]
    public void Removing_collapses_and_merges() =>
        Is("A[L 0.5, L 0.25, L 0.25]",
            Tree(Across(C(L, .5), C(Down(C(L, .5), C(Across(C(L, .5), C(L, .5)), .5)), .5))).Remove(1));

    // The cascade Normal handles: removing leaf 2 leaves a Down with one Across child inside an Across parent. The
    // Down collapses to its Across, which has the parent's axis, so it merges into the parent with shares scaled.
    [Fact]
    public void A_collapse_that_cascades_into_the_grandparent_axis() =>
        Is("A[L 0.4, L 0.3, L 0.3]",
            Tree(Across(C(L, .4), C(Down(C(Across(C(L, .5), C(L, .5)), .6), C(L, .4)), .6))).Remove(3));

    [Fact]
    public void Edge_band_on_a_matching_root_halves_the_outer_child()
    {
        var (layout, leaf) = Tree(Across(C(L, .5), C(L, .5))).InsertAtEdge(TileArrange.Zone.Right);
        Is("A[L 0.5, L 0.25, L 0.25]", layout);
        Assert.Equal(2, leaf);
    }

    [Fact]
    public void Edge_band_on_a_crossing_root_wraps_it()
    {
        var down = Tree(Down(C(L, .5), C(L, .5)));
        var (right, rightLeaf) = down.InsertAtEdge(TileArrange.Zone.Right);
        Is("A[D[L 0.5, L 0.5] 0.5, L 0.5]", right);
        Assert.Equal(2, rightLeaf);

        var (left, leftLeaf) = down.InsertAtEdge(TileArrange.Zone.Left);
        Is("A[L 0.5, D[L 0.5, L 0.5] 0.5]", left);
        Assert.Equal(0, leftLeaf);
        Assert.Equal(3, left.Count);
    }

    [Fact]
    public void Edge_band_on_the_left_of_a_matching_root_is_first()
    {
        var (layout, leaf) = Tree(Across(C(L, .5), C(L, .5))).InsertAtEdge(TileArrange.Zone.Left);
        Is("A[L 0.25, L 0.25, L 0.5]", layout);
        Assert.Equal(0, leaf);
    }

    [Fact]
    public void Opening_moves_with_inserts_and_removes()
    {
        var across = Tree(Across(C(L, .25), C(L, .25), C(L, .5)), 1);
        // Insert at index 1 (Left puts the new leaf at 1): an opening at 1 moves up to 2.
        Assert.Equal(2, across.Insert(1, TileArrange.Zone.Left).Layout.Opening);
        // Insert at index 1 with Right: the new leaf is 2, an opening at 1 stays.
        Assert.Equal(1, across.Insert(1, TileArrange.Zone.Right).Layout.Opening);
        // The edge band at the left is leaf 0, so every opening moves up.
        Assert.Equal(2, across.InsertAtEdge(TileArrange.Zone.Left).Layout.Opening);
        Assert.Equal(1, across.InsertAtEdge(TileArrange.Zone.Right).Layout.Opening);
        // Removing a leaf below the opening moves it down; above it, it stays.
        Assert.Equal(0, across.Remove(0).Opening);
        Assert.Equal(1, across.Remove(2).Opening);
    }

    [Fact]
    public void Removing_the_opening_clears_it() =>
        Assert.Null(Tree(Across(C(L, .5), C(L, .5)), 1).Remove(1).Opening);

    [Fact]
    public void Middle_is_a_caller_error()
    {
        var layout = Tree(Across(C(L, .5), C(L, .5)));
        Assert.Throws<ArgumentException>(() => layout.Insert(0, TileArrange.Zone.Middle));
        Assert.Throws<ArgumentException>(() => layout.InsertAtEdge(TileArrange.Zone.Middle));
    }

    // A share of 0 or 1 makes a leaf of no size, and NaN or one outside (0, 1) a negative one: a caller bug, so it
    // throws rather than storing a layout nothing can lay out.
    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    public void A_share_outside_zero_to_one_throws(double share) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Tree(Across(C(L, .5), C(L, .5))).Insert(0, TileArrange.Zone.Left, share));

    [Fact]
    public void Leaf_indexes_out_of_range_throw()
    {
        var layout = Tree(Across(C(L, .5), C(L, .5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.ShareOf(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.ShareOf(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.Remove(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.Insert(-1, TileArrange.Zone.Left));
    }
}
