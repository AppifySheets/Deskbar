using System.Windows;
using System.Windows.Controls;

namespace Deskbar.App;

// THE PINS' LINE: fixed cells packed from the left, wrapping onto a new line when the next would not fit.
// Petre, looking at the pins spread evenly over two rows under Start: "i don't really like this distribution,
// also sizes of those icons against start and search", then "b" from four mockups: one size for Start, search
// and the pins, the pins packed left in one line, like the bottom strip's buttons, and on to a new line,
// still packed, when there are more than fit.
//
// It replaces a UniformGrid, whose cells share the width out: the gaps grew with the bar, and the even split
// of lines is exactly what was not wanted any more. A WrapPanel would pack, but cannot keep a cell off the
// seam, which is the one thing this panel adds:
//
// ACROSS TWO SCREENS (Seam set to the x of the line between them, in this panel's coordinates) a cell that
// would straddle the seam starts at the seam instead, so the pins fill the left screen's half and carry on in
// the right half, and no icon is ever cut in two ("icons at the bottom shouldn't be broken into halves").
public sealed class PackedPinPanel : Panel
{
    double cellWidth = 26, cellHeight = 26;
    double? seam;

    public double CellWidth
    {
        get => cellWidth;
        set { cellWidth = value; InvalidateMeasure(); }
    }

    public double CellHeight
    {
        get => cellHeight;
        set { cellHeight = value; InvalidateMeasure(); }
    }

    // Where the line between two screens crosses this panel, or null when the bar is on one screen. Setting the
    // same value again does nothing, so a caller that recomputes it on every layout cannot start a layout loop.
    public double? Seam
    {
        get => seam;
        set
        {
            if (Nullable.Equals(seam, value) || (seam is { } a && value is { } b && Math.Abs(a - b) < 0.5)) return;
            seam = value;
            InvalidateMeasure();
        }
    }

    // Where each of `count` cells goes in `width`: left to right, a new line when the next cell would pass the
    // right edge (unless it is the first on its line, which always goes, however narrow the panel), and never
    // across the seam. Pure, so the tests can check the placement without a window.
    internal static IReadOnlyList<Point> Layout(int count, double width, double cellWidth, double cellHeight, double? seam) =>
        Enumerable.Range(0, count).Aggregate(
            (Points: new List<Point>(), X: 0.0, Y: 0.0),
            (state, _) =>
            {
                var (x, y) = (state.X, state.Y);
                if (seam is { } s && x < s && x + cellWidth > s && x > 0) x = s;
                if (x + cellWidth > width && x > 0)
                {
                    (x, y) = (0, y + cellHeight);
                }
                state.Points.Add(new Point(x, y));
                return (state.Points, x + cellWidth, y);
            },
            state => (IReadOnlyList<Point>)state.Points);

    protected override Size MeasureOverride(Size availableSize)
    {
        var cell = new Size(cellWidth, cellHeight);
        InternalChildren.OfType<UIElement>().ToList().ForEach(child => child.Measure(cell));
        var visible = InternalChildren.OfType<UIElement>().Count(c => c.Visibility != Visibility.Collapsed);
        if (visible == 0) return new Size(0, 0);
        // Unbounded (measured for its natural size): one line of every cell.
        var width = double.IsInfinity(availableSize.Width) ? visible * cellWidth : availableSize.Width;
        var points = Layout(visible, width, cellWidth, cellHeight, seam);
        return new Size(Math.Min(width, points.Max(p => p.X) + cellWidth), points.Max(p => p.Y) + cellHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var visible = InternalChildren.OfType<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();
        var points = Layout(visible.Count, finalSize.Width, cellWidth, cellHeight, seam);
        visible.Zip(points).ToList().ForEach(pair => pair.First.Arrange(new Rect(pair.Second, new Size(cellWidth, cellHeight))));
        return finalSize;
    }
}
