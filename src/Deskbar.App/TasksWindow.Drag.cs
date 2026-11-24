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
    // Nested: a subtask drawn under its parent (TaskViews.UnderParents). It is dragged (out of its parent's block, to
    // free it), but it is no drop target: its rank is not where it is drawn (review of #467, finding 2).
    readonly Dictionary<Guid, (Quadrant Quadrant, bool Waiting, bool Fixed, bool Nested)> dragLines = [];

    DragState? drag;
    TaskDropTarget.Drop? dropShown;
    Adorner? dropAdorner;
    bool dropQueued;

    const double DragDimming = 0.45;

    /// <summary>The mouse the drag reads; the arrange drag's seam, held down in tests.</summary>
    internal ArrangeMouse DragMouse { get; set; } = ArrangeMouse.Real;

    /// <summary>What the drag measures against and captures: the List's panel, or the Matrix's grid.</summary>
    internal FrameworkElement DragSurface => matrixShown ? MatrixGrid : Rows;

    // Where the drop line is drawn across: the List's width, or the width of the Matrix cell it lands in.
    Rect dropSpan = Rect.Empty;

    internal bool Dragging => drag is { Started: true };

    /// <summary>For the tests: where the drop line is drawn, in the drag's coordinates, while a drag shows one.</summary>
    internal double? DropLineY => dropShown?.LineY;

    bool DragInFlight => drag is not null || dropQueued;

    // Wired on both surfaces once; only the one on show ever sees a press.
    void WireDrag() => new FrameworkElement[] { Rows, MatrixGrid }.ToList().ForEach(surface =>
    {
        surface.PreviewMouseMove += (_, e) =>
        {
            if (drag is null) return;
            if (!DragMouse.ButtonDown(e)) CancelDrag();
            else DragMove(DragMouse.Position(e, DragSurface));
        };
        surface.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (drag is null) return;
            if (drag.Started) e.Handled = true;
            DragRelease();
        };
        surface.LostMouseCapture += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, surface) && drag is { Started: true }) CancelDrag();
            // A press still armed when the capture goes (another window took the mouse) ends, and the redraw it held back
            // is drawn, from the queue rather than inside the capture change (review of 4fc4441..a1acd60, item 3).
            else if (drag is { Started: false })
            {
                drag = null;
                Dispatcher.BeginInvoke(new Action(Flush));
            }
        };
    });

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
            DragMouse.Capture(DragSurface);
            if (LineButton(current.Id) is { } dragged) dragged.Opacity = DragDimming;
            Cursor = Cursors.SizeNS;
            if (AdornerLayer.GetAdornerLayer(DragSurface) is { } layer)
            {
                dropAdorner = new DropLine(DragSurface, PaintDrop) { IsHitTestVisible = false };
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
                if (drop is not null) Dropped(current.Id, drop);
            }
            finally
            {
                dropQueued = false;
                Flush();
            }
        }));
    }

    // What a drop does (drag to nest, TaskDropTarget's header), as on the bar: on a line's middle the task becomes its
    // subtask; a nested subtask dropped anywhere else is freed first, then placed; everything else is placed as before.
    void Dropped(Guid id, TaskDropTarget.Drop drop)
    {
        if (drop.Into is { } parent)
        {
            Actions?.SetParent?.Invoke(id, parent);
            return;
        }
        if (dragLines.TryGetValue(id, out var line) && line.Nested) Actions?.SetParent?.Invoke(id, CSharpFunctionalExtensions.Maybe<Guid>.None);
        Actions?.Place(id, drop.Quadrant, drop.Place);
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
        if (dropAdorner is not null) AdornerLayer.GetAdornerLayer(DragSurface)?.Remove(dropAdorner);
        dropAdorner = null;
        ClearValue(CursorProperty);
        if (DragSurface.IsMouseCaptured) DragSurface.ReleaseMouseCapture();
    }

    // TaskDropTarget's answer over the lines on show that can be aimed at. In the Matrix, over the cell under the
    // pointer (or the nearest one) alone: its lines, and the whole cell as a place to drop when it has no line that
    // moves (empty, or only waiting lines), so every quadrant can be dropped into.
    TaskDropTarget.Drop? DropAt(Guid dragged, Point at)
    {
        var own = dragLines.TryGetValue(dragged, out var mine) ? mine.Quadrant : Quadrant.IV;
        if (!matrixShown)
        {
            dropSpan = new Rect(0, 0, Rows.ActualWidth, Rows.ActualHeight);
            // Drag to nest, in the List only: the Matrix's drag between cells already means "change its quadrant".
            var parents = Actions?.NestTargets?.Invoke(dragged) ?? new HashSet<Guid>();
            return TaskDropTarget.At(Placed(LineButtons())
                .Select(p => p with { TakesChild = !p.Waiting && parents.Contains(p.Id) }).ToList(), [], own, at);
        }
        if (cells.Count == 0) return null;
        var (quadrant, box) = cells
            .Select(c => (Quadrant: c.Key, Bounds: new Rect(c.Value.Box.TranslatePoint(new Point(0, 0), MatrixGrid), c.Value.Box.RenderSize)))
            .OrderBy(c => c.Bounds.Contains(at) ? 0 : Math.Abs(at.X - (c.Bounds.Left + c.Bounds.Width / 2))
                + Math.Abs(at.Y - (c.Bounds.Top + c.Bounds.Height / 2)))
            .First();
        dropSpan = box;
        var lines = Placed(cells[quadrant].Lines.Children.OfType<Button>().Where(b => b.Tag is Guid));
        var areas = lines.All(l => l.Waiting) ? new[] { new TaskDropTarget.Area(quadrant, box) } : [];
        // A drop on the cell's waiting lines goes to the top of the cell's own quadrant.
        return TaskDropTarget.At(lines, areas, quadrant, at);
    }

    // The lines that can be aimed at, with their boxes in the drag's coordinates.
    IReadOnlyList<TaskDropTarget.Placed> Placed(IEnumerable<Button> buttons) =>
        buttons
            .Where(b => b.Tag is Guid id && dragLines.TryGetValue(id, out var line) && !line.Fixed && !line.Nested && b.IsVisible)
            .Select(b => new TaskDropTarget.Placed((Guid)b.Tag, dragLines[(Guid)b.Tag].Quadrant, dragLines[(Guid)b.Tag].Waiting,
                new Rect(b.TranslatePoint(new Point(0, 0), DragSurface), b.RenderSize)))
            .ToList();

    void PaintDrop(DrawingContext dc)
    {
        if (dropShown is not { } drop) return;
        // Drag to nest: the line the task would go under is outlined, instead of a line between two.
        if (drop.Into is not null)
            dc.DrawRoundedRectangle(null, new Pen(DropInk, 1.5), drop.IntoBounds, 4, 4);
        else
            dc.DrawRoundedRectangle(DropInk, null, new Rect(dropSpan.Left, drop.LineY - 1, dropSpan.Width, 2), 1, 1);
    }

    static readonly Brush DropInk = Frozen(0xE6, 0xFF, 0xFF, 0xFF);

    IEnumerable<Button> LineButtons() =>
        (matrixShown ? cells.Values.SelectMany(c => c.Lines.Children.OfType<Button>()) : Rows.Children.OfType<Button>()).Where(b => b.Tag is Guid);

    Button? LineButton(Guid id) => LineButtons().FirstOrDefault(b => (Guid)b.Tag == id);

    sealed class DropLine(UIElement adorned, Action<DrawingContext> paint) : Adorner(adorned)
    {
        protected override void OnRender(DrawingContext drawingContext) => paint(drawingContext);
    }
}
