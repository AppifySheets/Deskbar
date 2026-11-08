using Deskbar.Core.Domain;

namespace Deskbar.Core.Tiling;

// The arithmetic of the tiling grid: where each of n windows goes inside one screen's work area.
// Pure geometry in physical pixels (Right and Bottom exclusive, like WindowRect everywhere), so it
// is testable without Windows. Later tasks call Arrange once per screen and then hand the slots
// out to windows; the SLOT ORDER is what they rely on: column by column, top to bottom inside a
// column.
//
// The grid rule (from the tiling spec):
//   columns = ceil(sqrt(n)); column i of the remaining takes floor(remaining windows / remaining
//   columns), so the later columns are the fuller ones (3 windows: 1 + 2, 5 windows: 1 + 2 + 2).
//   Columns are equal width and rows equal height; integer remainders go to the last column and
//   the last row, so the grid always reaches the far edge of the area.
public static class GridLayout
{
    // Equal columns and rows: the grid as it was before sizes existed.
    public static IReadOnlyList<WindowRect> Arrange(WindowRect area, int count, int gap) =>
        Arrange(area, count, gap, TileSizes.Equal(Shape(count)));

    // The grid with the column and row fractions the user has dragged (see TileSizes). With even
    // sizes the result is identical to the equal grid. Integer remainders still go to the last
    // column and last row, so the grid always reaches the far edge of the area.
    public static IReadOnlyList<WindowRect> Arrange(WindowRect area, int count, int gap, TileSizes sizes)
    {
        if (count <= 0)
            return [];

        // The gap runs around the outside as well as between tiles, so shrink the area first.
        var inner = new WindowRect(area.Left + gap, area.Top + gap, area.Right - gap, area.Bottom - gap);
        var shape = Shape(count);
        var xs = Edges(inner.Width, sizes.Columns);

        return shape
            .SelectMany((rows, column) =>
            {
                var ys = Edges(inner.Height, sizes.Rows[column]);
                return Enumerable.Range(0, rows).Select(row => Inset(
                    new WindowRect(inner.Left + xs[column], inner.Top + ys[row], inner.Left + xs[column + 1], inner.Top + ys[row + 1]),
                    gap, hasLeft: column > 0, hasRight: column < shape.Count - 1, hasTop: row > 0, hasBottom: row < rows - 1));
            })
            .ToList();
    }

    // How many tiles each column holds for n windows: columns = ceil(sqrt(n)), filled left to
    // right from what is still unplaced, so the later columns are the fuller ones.
    public static IReadOnlyList<int> Shape(int count)
    {
        if (count <= 0)
            return [];

        var columns = (int)Math.Ceiling(Math.Sqrt(count));
        return Enumerable.Range(0, columns)
            .Aggregate(
                (Remaining: count, Sizes: new List<int>()),
                (state, i) =>
                {
                    var take = state.Remaining / (columns - i);
                    state.Sizes.Add(take);
                    return (state.Remaining - take, state.Sizes);
                })
            .Sizes;
    }

    // The (column, row) a slot sits in, given the shape. Slots run column by column, top to bottom.
    public static (int Column, int Row) CellOf(int slot, IReadOnlyList<int> shape)
    {
        var column = Enumerable.Range(0, shape.Count).First(c => slot < shape.Take(c + 1).Sum());
        return (column, slot - shape.Take(column).Sum());
    }

    // The n+1 boundaries (offsets from the start of the span) of n entries with the given fractions.
    // Even fractions use floor(span / n) per entry, exactly as the equal grid always did; uneven
    // ones round the running total. The last boundary is always the span's end, so the integer
    // remainder lands in the last entry.
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

    // Shrinks a cell on its INNER edges only (the outer edges already carry the full gap from the
    // shrunk area). Each side of a shared edge takes half; for an odd gap the odd pixel goes to the
    // tile on the left/top of the edge, so two neighbours are always exactly `gap` apart.
    static WindowRect Inset(WindowRect cell, int gap, bool hasLeft, bool hasRight, bool hasTop, bool hasBottom)
    {
        var near = gap / 2;
        var far = gap - near;
        return new WindowRect(
            cell.Left + (hasLeft ? near : 0),
            cell.Top + (hasTop ? near : 0),
            cell.Right - (hasRight ? far : 0),
            cell.Bottom - (hasBottom ? far : 0));
    }
}
