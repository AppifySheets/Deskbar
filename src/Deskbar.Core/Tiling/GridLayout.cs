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
    public static IReadOnlyList<WindowRect> Arrange(WindowRect area, int count, int gap)
    {
        if (count <= 0)
            return [];

        // The gap runs around the outside as well as between tiles, so shrink the area first.
        var inner = new WindowRect(area.Left + gap, area.Top + gap, area.Right - gap, area.Bottom - gap);
        var columns = (int)Math.Ceiling(Math.Sqrt(count));
        var columnWidth = inner.Width / columns;

        // How many tiles each column holds, filled left to right from what is still unplaced.
        var sizes = Enumerable.Range(0, columns)
            .Aggregate(
                (Remaining: count, Sizes: new List<int>()),
                (state, i) =>
                {
                    var take = state.Remaining / (columns - i);
                    state.Sizes.Add(take);
                    return (state.Remaining - take, state.Sizes);
                })
            .Sizes;

        return sizes
            .SelectMany((rows, column) => Column(inner, column, columns, columnWidth, rows, gap))
            .ToList();
    }

    // One column's tiles, top to bottom.
    static IEnumerable<WindowRect> Column(WindowRect inner, int column, int columns, int columnWidth, int rows, int gap)
    {
        var rowHeight = inner.Height / rows;
        var left = inner.Left + column * columnWidth;
        // The last column and last row run to the edge, absorbing the integer remainder.
        var right = column == columns - 1 ? inner.Right : left + columnWidth;

        return Enumerable.Range(0, rows).Select(row =>
        {
            var top = inner.Top + row * rowHeight;
            var bottom = row == rows - 1 ? inner.Bottom : top + rowHeight;
            return Inset(new WindowRect(left, top, right, bottom), gap,
                hasLeft: column > 0, hasRight: column < columns - 1, hasTop: row > 0, hasBottom: row < rows - 1);
        });
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
