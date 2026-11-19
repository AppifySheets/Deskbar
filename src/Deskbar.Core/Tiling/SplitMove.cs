using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Tiling;

// Moves a split of the tiling grid when the user drags a tile's edge. Petre: "i want to be able
// to resize tiles by dragging the separation". Pure arithmetic on TileSizes, no Windows.
//
// The edge a tile's neighbour shares is a SPLIT: dragging the right edge of column 0 and the left
// edge of column 1 move the same split. Edges on the outside of the grid have no split; a drag of
// one alone returns None and the caller snaps the window back.
public static class SplitMove
{
    // No column or row may be narrower than this share of the work area's inner span. On the split tree: no child
    // narrower than this share of its own split. TileArrange.Returned clamps a returning window's share to it too.
    internal const double MinShare = 0.1;

    // The new sizes after `cell` (column, row) was dragged by `moved` and settled at `frame`, or
    // None when no moved edge faces a split. Moved outer edges alongside an inner one are ignored.
    // A corner moves the column split and that column's row split.
    public static Maybe<TileSizes> Apply(
        TileSizes sizes, IReadOnlyList<int> shape, WindowRect area, int gap,
        (int Column, int Row) cell, Edges moved, WindowRect frame)
    {
        // Same shrink and gap split as GridLayout.Arrange (shared helpers): the gap runs around the outside too.
        var inner = GridLayout.InnerArea(area, gap);
        // A shared edge is split in two: the tile on the near side keeps gap/2 of it and the far side
        // gap - gap/2. The split sits in the middle of the gap, so from a tile's released edge it is
        // `far` further on (right/bottom edge) or `near` back (left/top edge). Laying the grid out again
        // from the new fractions then leaves exactly `gap` between the two tiles.
        var (near, far) = GridLayout.HalfGaps(gap);

        // (index of the split between entries i and i+1, its position in pixels), when the edge is inner.
        var column = moved.HasFlag(Edges.Right) && cell.Column < shape.Count - 1 ? Maybe.From((Index: cell.Column, Position: frame.Right + far))
            : moved.HasFlag(Edges.Left) && cell.Column > 0 ? Maybe.From((Index: cell.Column - 1, Position: frame.Left - near))
            : Maybe<(int Index, int Position)>.None;
        var row = moved.HasFlag(Edges.Bottom) && cell.Row < shape[cell.Column] - 1 ? Maybe.From((Index: cell.Row, Position: frame.Bottom + far))
            : moved.HasFlag(Edges.Top) && cell.Row > 0 ? Maybe.From((Index: cell.Row - 1, Position: frame.Top - near))
            : Maybe<(int Index, int Position)>.None;

        return column.HasNoValue && row.HasNoValue
            ? Maybe<TileSizes>.None
            : new TileSizes(
                column.Match(c => Split(sizes.Columns, c.Index, (c.Position - inner.Left) / (double)inner.Width), () => sizes.Columns),
                sizes.Rows.Select((rows, i) => i == cell.Column
                    ? row.Match(r => Split(rows, r.Index, (r.Position - inner.Top) / (double)inner.Height), () => rows)
                    : rows).ToList());
    }

    // ---- the split tree (TileLayout), beside the TileSizes version above until the Tiler is switched over ----

    // The new layout after leaf `leaf` was dragged by `moved` and settled at `frame`, or None when no moved edge
    // belongs to a split (spec, "Dragging a separation"). Per axis, the moved edge's split is found by walking up from
    // the leaf to the nearest split of that edge's axis in which the leaf's subtree has a neighbour on that side
    // (TileLayout.SplitBeside). In Down[Across[A, C], B], A's bottom edge is therefore the ROOT's boundary between
    // the top pair and B, and C's bottom moves with it: on screen they are one line, so they are one split.
    //
    // The boundary goes to the released edge plus `far` (right, bottom) or minus `near` (left, top), as in the grid
    // version, as a fraction of THAT split's rect, laid out with the same Edges arithmetic the layout uses
    // (GridLayout.ChildRects). Only the two children either side of it change, and neither goes under MinShare of
    // that split. A corner moves one split per axis; the two cannot disturb each other, because moving an Across
    // boundary never changes any split's height and moving a Down boundary never changes any width. A moved outer
    // edge alongside an inner one is ignored. The opening, if any, is untouched. A leaf the layout does not have is a
    // caller bug and throws (TileLayout.PathTo).
    public static Maybe<TileLayout> Apply(TileLayout layout, WindowRect area, int gap, int leaf, Edges moved, WindowRect frame)
    {
        var inner = GridLayout.InnerArea(area, gap);
        var (near, far) = GridLayout.HalfGaps(gap);
        var path = layout.PathTo(leaf);
        // Each split's own (un-inset) rect: the inner area for the root, then the child rect holding the leaf.
        var rects = path.Aggregate((IReadOnlyList<WindowRect>)[inner],
            (acc, step) => [.. acc, GridLayout.ChildRects(step.Branch, acc[^1])[step.Child]]);

        // (the step whose split moves, the boundary index in it, the boundary's position as a fraction of that split),
        // for an edge that belongs to a split.
        Maybe<(int Step, int Index, double Position)> Boundary(TileArrange.Zone side, int released) =>
            TileLayout.SplitBeside(path, side).Map(i =>
            {
                var rect = rects[i];
                var across = path[i].Branch.Axis == Axis.Across;
                var start = across ? rect.Left : rect.Top;
                var span = across ? rect.Width : rect.Height;
                var before = side is TileArrange.Zone.Left or TileArrange.Zone.Top;
                return (i, before ? path[i].Child - 1 : path[i].Child, (released - start) / (double)Math.Max(1, span));
            });

        // As in the grid version: the far edge wins when both edges of an axis are flagged and both are inner.
        var across = moved.HasFlag(Edges.Right) && Boundary(TileArrange.Zone.Right, frame.Right + far) is { HasValue: true } right ? right
            : moved.HasFlag(Edges.Left) ? Boundary(TileArrange.Zone.Left, frame.Left - near)
            : Maybe<(int Step, int Index, double Position)>.None;
        var down = moved.HasFlag(Edges.Bottom) && Boundary(TileArrange.Zone.Bottom, frame.Bottom + far) is { HasValue: true } bottom ? bottom
            : moved.HasFlag(Edges.Top) ? Boundary(TileArrange.Zone.Top, frame.Top - near)
            : Maybe<(int Step, int Index, double Position)>.None;

        var boundaries = new[] { across, down }.Where(b => b.HasValue).Select(b => b.Value).ToList();
        return boundaries.Count == 0
            ? Maybe<TileLayout>.None
            : layout with
            {
                Root = boundaries.Aggregate(layout.Root, (root, b) =>
                    Rebuilt(root, path.Take(b.Step).Select(s => s.Child).ToList(), branch => branch with
                    {
                        Children = branch.Children
                            .Zip(Split(branch.Children.Select(c => c.Share).ToList(), b.Index, b.Position))
                            .Select(pair => pair.First with { Share = pair.Second })
                            .ToValueList()
                    }))
            };
    }

    // The tree with the split reached by following `route` (child indexes from the root) replaced by f(split).
    static TileNode Rebuilt(TileNode node, IReadOnlyList<int> route, Func<TileBranch, TileBranch> f) =>
        node is not TileBranch branch ? node
        : route.Count == 0 ? f(branch)
        : branch with
        {
            Children = branch.Children
                .Select((c, i) => i == route[0] ? c with { Node = Rebuilt(c.Node, route.Skip(1).ToList(), f) } : c)
                .ToValueList()
        };

    // Puts the boundary between entries `index` and `index + 1` at `position` (a fraction of the
    // span the entries divide, from its start: the inner area's for the grid, the split's own for the
    // tree). Only those two entries change; they keep their combined share and neither goes below MinShare.
    static IReadOnlyList<double> Split(IReadOnlyList<double> fractions, int index, double position)
    {
        var before = fractions.Take(index).Sum();
        var pair = fractions[index] + fractions[index + 1];
        // A pair already narrower than two minimums splits evenly rather than throwing.
        var first = pair < 2 * MinShare ? pair / 2 : Math.Clamp(position - before, MinShare, pair - MinShare);
        return fractions.Select((f, i) => i == index ? first : i == index + 1 ? pair - first : f).ToList();
    }
}
