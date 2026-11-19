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

    public static string Text(TileNode node) =>
        node is TileBranch b
            ? $"{(b.Axis == Axis.Across ? "A" : "D")}[{string.Join(", ", b.Children.Select(c => $"{Text(c.Node)} {Math.Round(c.Share, 6)}"))}]"
            : "L";

    // The tree as text with the opening appended, so one string pins both.
    public static string Text(TileLayout layout) =>
        Text(layout.Root) + (layout.Opening is { } open ? $" open {open}" : "");
}
