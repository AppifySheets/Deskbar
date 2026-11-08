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
    // No column or row may be narrower than this share of the work area's inner span.
    const double MinShare = 0.1;

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

    // Puts the boundary between entries `index` and `index + 1` at `position` (a fraction of the
    // inner span from its start). Only those two entries change; they keep their combined share and
    // neither goes below MinShare.
    static IReadOnlyList<double> Split(IReadOnlyList<double> fractions, int index, double position)
    {
        var before = fractions.Take(index).Sum();
        var pair = fractions[index] + fractions[index + 1];
        // A pair already narrower than two minimums splits evenly rather than throwing.
        var first = pair < 2 * MinShare ? pair / 2 : Math.Clamp(position - before, MinShare, pair - MinShare);
        return fractions.Select((f, i) => i == index ? first : i == index + 1 ? pair - first : f).ToList();
    }
}
