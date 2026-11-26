using System.Windows;
using System.Windows.Controls;

namespace Deskbar.App;

/// <summary>
/// Shows at most <see cref="Cap"/> DIP of its child's height and clips the rest, and says whether anything was cut
/// (<see cref="Overflowing"/>). Task e91f8aa5 (Petre, of a waiting task's nine-line question filling the tasks panel:
/// "limit how much can fit here. expanding should open a detailview"): an open line's question and description stop at
/// a few lines, and the line offers its task window for the rest.
/// </summary>
/// <remarks>
/// The child is measured with no height limit, so its full height is known however little of it shows: a TextBlock or a
/// RichTextBox measured against the cap would report the cap back, and the cut could not be seen. It is then arranged at
/// that full height from the top, and ClipToBounds keeps whatever lies below the cap from drawing over the next row.
/// Clicks pass through untouched (a Decorator takes none of its own), so a link in the child stays clickable and a press
/// elsewhere still falls to the line under it.
/// </remarks>
sealed class HeightCap : Decorator
{
    public HeightCap()
    {
        ClipToBounds = true;
        // Snapping needs the child laid out, so it runs once a layout pass is over, never inside one (a RichTextBox's
        // GetPositionFromPoint wants a valid layout). The handler is held weakly by WPF, so a dropped cap is collected.
        LayoutUpdated += (_, _) => Snap();
    }

    /// <summary>
    /// The most of the child that shows, in DIP; positive infinity shows it whole. Settable since task 2088e1ed (Petre: "i
    /// should be able to increase or decrease issue or comment body"): the grip under an open line's description or
    /// question (TasksWindow.BodyGrip.cs) moves it while it is dragged. A new cap forgets the last line edge, so the next
    /// layout pass snaps afresh from it.
    /// </summary>
    public double Cap
    {
        get => cap;
        set
        {
            cap = value;
            shown = double.NaN;
            InvalidateMeasure();
        }
    }

    double cap;

    /// <summary>The child's whole height, from the last measure: what a cap at or past it shows in full.</summary>
    public double FullHeight => Child?.DesiredSize.Height ?? 0;

    /// <summary>
    /// Where the cut really falls, given the laid-out child and <see cref="Cap"/>: the top of a line the cap would cut
    /// through, or the cap itself. Without it the cut is the cap. A RichTextBox needs it (review of task e91f8aa5,
    /// measured): its paragraphs' spacing puts the fourth line of a description with a paragraph break 3 to 15 DIP lower
    /// than four TextBlock lines, so a cut at the cap showed half a line.
    /// </summary>
    public Func<UIElement, double, double>? EdgeOf { get; init; }

    /// <summary>Whether the child is taller than what shows, from the last measure (half a DIP of rounding forgiven).</summary>
    public bool Overflowing { get; private set; }

    // What shows: the cap, or the line edge above it once Snap has found one.
    double shown = double.NaN;

    double Shown => double.IsNaN(shown) ? Cap : shown;

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is null) return default;
        Child.Measure(new Size(constraint.Width, double.PositiveInfinity));
        var full = Child.DesiredSize.Height;
        Overflowing = full > Shown + 0.5;
        return new Size(Child.DesiredSize.Width, Math.Min(full, Shown));
    }

    // The edge is worked out from the cap every time, never from the last edge, so it depends only on the child's width
    // and wrapping: a second pass finds the same edge and stops, and a new width finds its own.
    void Snap()
    {
        if (EdgeOf is null || Child is null || ActualWidth <= 0) return;
        // A cap past the child's foot cuts nothing, so there is no line to snap to (and an infinite one, "show it whole",
        // is no point a RichTextBox can be asked about). An edge found while it still cut (a narrower width, a longer
        // text) is dropped, or it would go on cutting a text that now fits (review of task 2088e1ed).
        if (Cap >= FullHeight)
        {
            if (double.IsNaN(shown)) return;
            shown = double.NaN;
            InvalidateMeasure();
            return;
        }
        // LayoutUpdated is raised on every subscriber after any layout pass, so a cap a redraw has dropped hears it too
        // until it is collected, and its RichTextBox, out of the window's tree, throws when asked for a position ("The
        // specified Visual is not a descendant of this Visual", measured in a test that redraws after a drag, task
        // 2088e1ed). A cap no longer in a window has nothing to snap.
        if (Window.GetWindow(this) is null) return;
        var edge = Math.Min(Cap, EdgeOf(Child, Cap));
        if (Math.Abs(edge - Shown) <= 0.5) return;
        shown = edge;
        InvalidateMeasure();
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        // At its full height, from the top: the part past the cap is clipped, never squeezed into it.
        Child?.Arrange(new Rect(0, 0, arrangeSize.Width, Math.Max(arrangeSize.Height, Child.DesiredSize.Height)));
        return arrangeSize;
    }
}
