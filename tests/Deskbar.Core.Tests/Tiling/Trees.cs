using Deskbar.Core.Domain;
using Deskbar.Core.Tiling;

namespace Deskbar.Core.Tests.Tiling;

// Builders and a text form for split trees, shared by the *LayoutTests files. The text form is the one
// TileLayoutEditTests uses ("A[L 0.5, D[L 0.5, L 0.5] 0.5]", shares rounded to 6 places), so a failing assertion
// prints both trees in a form a person can read.
static class Trees
{
    public static TileNode L => TileLeaf.Instance;
    public static TileChild C(TileNode node, double share) => new(node, share);
    public static TileNode Across(params TileChild[] kids) => new TileBranch(Axis.Across, kids.ToValueList());
    public static TileNode Down(params TileChild[] kids) => new TileBranch(Axis.Down, kids.ToValueList());
    public static TileLayout Tree(TileNode root, int? opening = null) => new(root) { Opening = opening };

    // A columns-of-rows layout, the shape the grid stored (column shares, then each column's row shares), as a tree
    // in normal form: a one-row column is a leaf and one column is a Down root. The Tiler's tests were written
    // against grids, and most of them still mean one; this keeps their expectations readable without the grid type.
    public static TileLayout Columns(double[] columns, params double[][] rows) =>
        TileLayout.Normal(
            new TileBranch(Axis.Across, columns
                .Select((share, c) => new TileChild(new TileBranch(Axis.Down, rows[c].Select(r => new TileChild(L, r)).ToValueList()), share))
                .ToValueList()),
            null);

    // The share of the child reached by `path`, child indexes from this node down: ShareAt(1, 0) is the first child of
    // the second child. For the Tiler's tests that pin one split's number (what Columns[c] and Rows[c][r] were).
    public static double ShareAt(this TileNode node, params int[] path) =>
        path.Length == 1
            ? ((TileBranch)node).Children[path[0]].Share
            : ((TileBranch)node).Children[path[0]].Node.ShareAt(path[1..]);

    public static string Text(TileNode node) =>
        node is TileBranch b
            ? $"{(b.Axis == Axis.Across ? "A" : "D")}[{string.Join(", ", b.Children.Select(c => $"{Text(c.Node)} {Math.Round(c.Share, 6)}"))}]"
            : "L";

    // The tree as text with the opening appended, so one string pins both.
    public static string Text(TileLayout layout) =>
        Text(layout.Root) + (layout.Opening is { } open ? $" open {open}" : "");
}
