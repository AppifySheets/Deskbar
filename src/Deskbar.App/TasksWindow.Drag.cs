using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// DRAGGING A LINE IN THE TASKS WINDOW (Task 12; progress.md Rulings 24, 25 and 28), the bar's drag
// (FloatingBar.TaskDrag.cs) with the same rules, and the same answer to "where does it land" (TaskDropTarget): before or
// after the line under the pointer, in that line's quadrant, so a drop among another quadrant's lines sets its flags.
// Waiting on you lines cannot be dragged and nothing drops above them; a closed task, or a line in its undo window, is
// neither dragged nor a target.
//
//   * A THRESHOLD: the press only arms; the system drag distance starts the drag, so a click stays a click.
//   * MOUSE CAPTURE on the drag surface (DragMouse, the arrange drag's seam, so a test holds the button down); the
//     line's button loses its own capture with it and raises no Click on the release.
//   * NO REDRAW DURING A PRESS OR A DRAG (MustWait reads DragInFlight); the drop is applied from the dispatcher queue
//     after the release has finished routing, then what waited is drawn (Flush).
//   * THE PREVIEW is an adorner: the drop line, and the dragged line dimmed in place. Nothing changes size.
//   * A capture lost to another window, or a move with the button already up, calls it off; nothing moves.
public partial class TasksWindow
{
    sealed record DragState(Guid Id, Point PressedAt, bool Started);

    // Every line drawn: its quadrant, whether it waits on Petre (pinned), and whether it cannot move at all (closed, or
    // in its undo window). Filled as the lines are drawn (Line), cleared with them.
    readonly Dictionary<Guid, (Quadrant Quadrant, bool Waiting, bool Fixed)> dragLines = [];

    DragState? drag;
    TaskDropTarget.Drop? dropShown;
    Adorner? dropAdorner;
    bool dropQueued;

    const double DragDimming = 0.45;

    /// <summary>The mouse the drag reads; the arrange drag's seam, held down in tests.</summary>
    internal ArrangeMouse DragMouse { get; set; } = ArrangeMouse.Real;

    /// <summary>What the drag measures against and captures: the list's own panel.</summary>
    internal FrameworkElement DragSurface => Rows;

    internal bool Dragging => drag is { Started: true };

    bool DragInFlight => drag is not null || dropQueued;

    void WireDrag()
    {
        Rows.PreviewMouseMove += (_, e) =>
        {
            if (drag is null) return;
            if (!DragMouse.ButtonDown(e)) CancelDrag();
            else DragMove(e.GetPosition(DragSurface));
        };
        Rows.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (drag is null) return;
            if (drag.Started) e.Handled = true;
            DragRelease();
        };
        Rows.LostMouseCapture += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, Rows) && drag is { Started: true }) CancelDrag();
            else if (drag is { Started: false }) drag = null;
        };
    }

    /// <summary>A press on a line: arms a drag, unless the line is pinned (waiting) or fixed (closed).</summary>
    internal void DragPress(Guid id, Point at)
    {
        drag = null;
        if (renaming is not null || !dragLines.TryGetValue(id, out var line) || line.Waiting || line.Fixed) return;
        drag = new DragState(id, at, Started: false);
    }

    /// <summary>A move: past the drag distance the drag starts; then the drop line follows the pointer.</summary>
    internal void DragMove(Point at)
    {
        if (drag is not { } current) return;
        if (!current.Started)
        {
            if (Math.Abs(at.X - current.PressedAt.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(at.Y - current.PressedAt.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            drag = current = current with { Started = true };
            DragMouse.Capture(Rows);
            if (LineButton(current.Id) is { } dragged) dragged.Opacity = DragDimming;
            Cursor = Cursors.SizeNS;
            if (AdornerLayer.GetAdornerLayer(Rows) is { } layer)
            {
                dropAdorner = new DropLine(Rows, PaintDrop) { IsHitTestVisible = false };
                layer.Add(dropAdorner);
            }
        }
        dropShown = DropAt(current.Id, at);
        dropAdorner?.InvalidateVisual();
    }

    /// <summary>
    /// The release: a press that never became a drag is only let go (its Click follows); a drag drops where its line
    /// was last drawn, from the dispatcher queue, and then what waited is drawn.
    /// </summary>
    internal void DragRelease()
    {
        if (drag is not { } current) return;
        var drop = dropShown;
        EndDrag();
        if (!current.Started) return;
        dropQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (drop is not null) Actions?.Place(current.Id, drop.Quadrant, drop.Place);
            }
            finally
            {
                dropQueued = false;
                Flush();
            }
        }));
    }

    void CancelDrag()
    {
        EndDrag();
        Flush();
    }

    // The state first, then the capture: releasing it raises LostMouseCapture, which must find no drag left.
    void EndDrag()
    {
        var current = drag;
        drag = null;
        dropShown = null;
        if (current is not null && LineButton(current.Id) is { } dragged) dragged.Opacity = 1;
        if (dropAdorner is not null) AdornerLayer.GetAdornerLayer(Rows)?.Remove(dropAdorner);
        dropAdorner = null;
        ClearValue(CursorProperty);
        if (Rows.IsMouseCaptured) Rows.ReleaseMouseCapture();
    }

    // TaskDropTarget's answer over the lines on show that can be aimed at.
    TaskDropTarget.Drop? DropAt(Guid dragged, Point at)
    {
        var placed = LineButtons()
            .Where(b => b.Tag is Guid id && dragLines.TryGetValue(id, out var line) && !line.Fixed && b.IsVisible)
            .Select(b => (Id: (Guid)b.Tag, Line: dragLines[(Guid)b.Tag], Bounds: new Rect(b.TranslatePoint(new Point(0, 0), Rows), b.RenderSize)))
            .Select(x => new TaskDropTarget.Placed(x.Id, x.Line.Quadrant, x.Line.Waiting, x.Bounds))
            .ToList();
        var own = dragLines.TryGetValue(dragged, out var mine) ? mine.Quadrant : Quadrant.IV;
        return TaskDropTarget.At(placed, [], own, at);
    }

    void PaintDrop(DrawingContext dc)
    {
        if (dropShown is not { } drop) return;
        dc.DrawRoundedRectangle(DropInk, null, new Rect(0, drop.LineY - 1, Rows.ActualWidth, 2), 1, 1);
    }

    static readonly Brush DropInk = Frozen(0xE6, 0xFF, 0xFF, 0xFF);

    IEnumerable<Button> LineButtons() => Rows.Children.OfType<Button>().Where(b => b.Tag is Guid);

    Button? LineButton(Guid id) => LineButtons().FirstOrDefault(b => (Guid)b.Tag == id);

    sealed class DropLine(UIElement adorned, Action<DrawingContext> paint) : Adorner(adorned)
    {
        protected override void OnRender(DrawingContext drawingContext) => paint(drawingContext);
    }
}
