using System.Text.Json.Serialization;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Tiling;

// A screen's tiling layout as a SPLIT TREE (spec: docs/superpowers/specs/2026-11-19-tiling-split-tree-design.md).
// It sits BESIDE TileSizes for now: nothing that reads TileSizes changes, and later tasks switch the Tiler over.
//
// Why a tree: "columns of rows" cannot say "A and C side by side on top, B full width underneath", so a drop
// beside a stacked tile had nowhere to go. A tree can: every split divides its span between children, and a child
// can be a split in the other direction.
//
//   Across: children side by side, left to right.   Down: children stacked, top to bottom.
//
// Slot order is the leaves depth-first, children in order. For an Across of Down columns (today's grid) that is
// column by column, top to bottom, which is today's slot order, so everything that maps a window to a slot keeps
// its meaning.
public enum Axis { Across, Down }

// One node of the tree. The discriminator is "kind": "leaf" or "split" in state.json.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(TileLeaf), "leaf")]
[JsonDerivedType(typeof(TileBranch), "split")]
public abstract record TileNode;

// One cell. It holds a window, or is the layout's opening (TileLayout.Opening names which leaf).
public sealed record TileLeaf : TileNode
{
    public static TileLeaf Instance { get; } = new();
}

// A split: children laid along Axis, each taking its Share of the span. ValueList gives the record value equality.
public sealed record TileBranch(Axis Axis, ValueList<TileChild> Children) : TileNode;

// A child and the fraction of its parent's span it takes. A branch's shares sum to 1.
public sealed record TileChild(TileNode Node, double Share);

// The tree plus the opening. Equality is Root and Opening (the record's own), and TileBranch compares by content.
//
// Normal form, enforced by Normal() and so by every operation that builds an edited tree:
//   - a split has at least two children; a split left with one is replaced by that child;
//   - a split's child is never a split of the same axis: it is merged into the parent, its shares scaled by its
//     own share. Three columns are ONE Across of three, exactly as today's Columns list.
public sealed record TileLayout(TileNode Root)
{
    // Two shares closer than this are the same (also what "even" means), as in TileSizes.
    const double Tolerance = 1e-9;

    // The leaf (by its index in depth-first order) that holds no window; null for none. Petre: "keep the opening for
    // the new window to jump into". The layout then describes one cell more than the screen has windows.
    public int? Opening { get; init; }

    // Number of leaves, the opening counted. Computed, so not stored: the stored JSON is the tree and the opening
    // (ruling R3).
    [JsonIgnore]
    public int Count => LeafCount(Root);

    // The automatic layout for n windows: GridLayout.Shape(n) as a tree, Across of Down columns with even shares.
    // A one-row column is a leaf; Normal turns one column into a Down root and one window into a lone leaf.
    public static TileLayout Automatic(int count) =>
        Normal(
            Split(Axis.Across, GridLayout.Shape(count)
                .Select(rows => Split(Axis.Down, Enumerable.Repeat(TileLeaf.Instance, rows).Cast<TileNode>().ToList()))
                .ToList()),
            null);

    // Migration: the stored columns-of-rows sizes as a tree, an Across of columns each a Down of its rows, in normal
    // form, the opening's slot mapped to the same leaf index (slots and leaves are numbered alike).
    public static TileLayout FromGrid(TileSizes grid) =>
        Normal(
            new TileBranch(Axis.Across, grid.Columns
                .Select((share, column) => new TileChild(
                    new TileBranch(Axis.Down, grid.Rows[column]
                        .Select(row => new TileChild(TileLeaf.Instance, row))
                        .ToValueList()),
                    share))
                .ToValueList()),
            grid.Opening);

    // The only way to build an edited tree: applies normal form. A tree with no leaf at all becomes a lone leaf.
    public static TileLayout Normal(TileNode root, int? opening) =>
        new(Flatten(root)) { Opening = opening };

    // This layout lays out `windows` windows around its opening: there is one, and exactly one cell more than windows.
    public bool HasOpeningFor(int windows) =>
        Opening is { } open && Count == windows + 1 && open >= 0 && open <= windows;

    // True when every split's shares are 1/n.
    [JsonIgnore]
    public bool IsEven => AllBranches(Root).All(b => IsEvenShares(b.Children));

    // Same structure, every split even, opening kept.
    public TileLayout Evened() => this with { Root = EvenNode(Root) };

    // The structure is equal, shares ignored. The opening is not part of the structure (compare it separately).
    public bool SameShapeAs(TileLayout other) => SameStructure(Root, other.Root);

    // The leaf's share of the INNER area on each axis: the product of the shares of the Across splits (width) and of
    // the Down splits (height) on its path. TileArrival's floor and largest-tile rules read these where they read
    // Columns[c] and Rows[c][r] today.
    public (double Width, double Height) ShareOf(int leaf) =>
        SharesAlong(Root, leaf) ?? (1.0, 1.0);

    // ---- internals -------------------------------------------------------------------------------------------

    static TileNode Split(Axis axis, IReadOnlyList<TileNode> nodes) =>
        new TileBranch(axis, nodes.Select(n => new TileChild(n, 1.0 / nodes.Count)).ToValueList());

    static int LeafCount(TileNode node) =>
        node is TileBranch b ? b.Children.Sum(c => LeafCount(c.Node)) : 1;

    static IEnumerable<TileBranch> AllBranches(TileNode node) =>
        node is TileBranch b ? b.Children.SelectMany(c => AllBranches(c.Node)).Prepend(b) : [];

    static bool IsEvenShares(IReadOnlyList<TileChild> children) =>
        children.All(c => Math.Abs(c.Share - 1.0 / children.Count) < Tolerance);

    static TileNode EvenNode(TileNode node) =>
        node is TileBranch b
            ? b with { Children = b.Children.Select(c => new TileChild(EvenNode(c.Node), 1.0 / b.Children.Count)).ToValueList() }
            : node;

    static bool SameStructure(TileNode a, TileNode b) =>
        (a, b) switch
        {
            (TileLeaf, TileLeaf) => true,
            (TileBranch x, TileBranch y) =>
                x.Axis == y.Axis
                && x.Children.Count == y.Children.Count
                && x.Children.Zip(y.Children, (l, r) => SameStructure(l.Node, r.Node)).All(same => same),
            _ => false
        };

    // Bottom-up normal form. Children are normalized first; then a child that came out as a split of this split's
    // axis (it was one, or it collapsed to one) is merged in with its shares scaled by its own share; then a split
    // with one child is that child. A single-child collapse returns the child with ITS shares untouched, which is
    // what "takes the split's whole area" means.
    static TileNode Flatten(TileNode node) =>
        node is not TileBranch branch
            ? node
            : branch.Children
                .Select(c => new TileChild(Flatten(c.Node), c.Share))
                .SelectMany(c => c.Node is TileBranch inner && inner.Axis == branch.Axis
                    ? inner.Children.Select(g => new TileChild(g.Node, g.Share * c.Share))
                    : [c])
                .ToList() switch
            {
                [] => TileLeaf.Instance,
                [var only] => only.Node,
                var many => new TileBranch(branch.Axis, many.ToValueList())
            };

    // Width and height shares of leaf number `leaf` inside `node`, or null when it is not in there.
    static (double Width, double Height)? SharesAlong(TileNode node, int leaf)
    {
        if (node is not TileBranch b)
            return leaf == 0 ? (1.0, 1.0) : null;

        // Walk the children with the count of leaves before each, and descend into the one holding `leaf`.
        var before = 0;
        foreach (var child in b.Children)
        {
            var n = LeafCount(child.Node);
            if (leaf < before + n)
            {
                var (w, h) = SharesAlong(child.Node, leaf - before) ?? (1.0, 1.0);
                return b.Axis == Axis.Across ? (w * child.Share, h) : (w, h * child.Share);
            }
            before += n;
        }
        return null;
    }
}
