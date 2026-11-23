using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// DRAGGING A TASK LINE UP OR DOWN (Task 12; progress.md Rulings 24, 25 and 28). Petre: "tasks should be draggable so i
// move the ones that aren't important below". The order lives in Deskbar.Core (TaskBoard.Place); this file only reads
// where the pointer is and hands TaskService.Place a quadrant and a place beside a line.
//
// WHAT A DROP MEANS is TaskDropTarget's rule, shared with the tasks window: before or after the line under the pointer,
// in that line's quadrant, so a line dropped among another quadrant's lines takes that quadrant's flags. Waiting on
// you is pinned: its lines cannot be dragged, and nothing drops above them. A closed line in its undo window is not a
// target either; the nearest open line answers. Below the last line is after the last line. A quadrant with no line on
// show cannot be dropped into on the bar: the toggles set flags, and the window's Matrix has a cell for each quadrant.
//
// THE BAR'S DRAG RULES, the arrange drag's (FloatingBar.ArrangeDrag.cs) and #48's (ClickTrace):
//   * A THRESHOLD, so a click stays a click. The press only arms; the drag starts once the pointer has gone the system
//     drag distance. A press and release in place is the line's ordinary Click.
//   * MOUSE CAPTURE, NEVER DoDragDrop. A started drag captures the mouse on the lines panel (through ArrangeMouseInput,
//     the arrange drag's seam, so a test can hold the button down), which also takes the capture from the line's
//     button, so that button raises no Click on the release.
//   * NO REPAINT DURING A PRESS OR A DRAG. TaskListMustWait and Rebuild both wait while a task drag is armed or its
//     drop is queued; the drop is applied from the dispatcher queue after the release has finished routing (the
//     arrange drag's Important 6: nothing is torn down under an event still on its way), and then the paint and any
//     rebuild that waited are served.
//   * THE PREVIEW IS AN ADORNER, never part of the layout: a 2 DIP line in InsertMarkerBrush where the line would land,
//     and the dragged line dimmed in place. Neither changes a size, so nothing moves under the pointer.
//   * A capture lost to another window, or a move that arrives with the button up, calls the drag off; nothing moves.
public partial class FloatingBar
{
    // The press or drag in progress: the task, where the press was (in the lines panel's coordinates, like every point
    // here), whether the drag has started, and the line's button (dimmed while it is dragged).
    sealed record TaskDragState(Guid Id, Point PressedAt, bool Started, Button Line);

    /// <summary>What a release now would do, drawn as the drop line: the quadrant, the place, and where the line is.</summary>
    internal sealed record TaskDropPreview(Quadrant Quadrant, TaskPlace Place, double LineY);

    TaskDragState? taskDrag;
    TaskDropPreview? taskDropShown;
    Adorner? taskDropAdorner;

    // From a release that queued its drop until the drop has run: the list and the bar keep waiting meanwhile.
    bool taskDropQueued;

    const double TaskDragDimming = 0.45;

    internal bool TaskDragging => taskDrag is { Started: true };
    internal TaskDropPreview? TaskDropShown => taskDropShown;

    // Whether the list must wait for a task drag (TaskListMustWait and Rebuild read it).
    internal bool TaskDragInFlight => taskDrag is not null || taskDropQueued;

    // Wired once, from BuildTaskList, on the lines panel: the moves, the release and a lost capture while a drag is on.
    void WireTaskDrag(StackPanel panel)
    {
        panel.PreviewMouseMove += (_, e) =>
        {
            if (taskDrag is null) return;
            if (!ArrangeMouseInput.ButtonDown(e)) CancelTaskDrag("the button came up unseen");
            else TaskDragMove(ArrangeMouseInput.Position(e, panel));
        };
        panel.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (taskDrag is null) return;
            // A started drag owns the release: the line's button must not read it as a click. A press that never became
            // a drag is only let go here, and its release carries on to the line's Click.
            if (taskDrag.Started)
            {
                e.Handled = true;
                MarkPressConsumed();
            }
            TaskDragRelease();
        };
        panel.LostMouseCapture += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, panel) && taskDrag is { Started: true }) CancelTaskDrag("capture lost");
            // The line's own capture going while a press is still armed (another window took the mouse; an ordinary
            // release has cleared the press before this) ends that press, and serves what it held back: a paint, and
            // any rebuild that waited (review of 4fc4441..a1acd60, item 3). Queued, never inline: this is inside the
            // capture change, the #48 kind of moment.
            else if (taskDrag is { Started: false })
            {
                taskDrag = null;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    PaintTaskList();
                    FlushDeferredRebuild();
                }));
            }
        };
    }

    /// <summary>
    /// A press on a line: arms a drag of its task. A line waiting on Petre or closed in its undo window arms nothing;
    /// so does a list with no service. Nothing is drawn and nothing is captured yet.
    /// </summary>
    internal void TaskDragPress(Guid id, Point inList)
    {
        taskDrag = null;
        if (taskService?.Board.Tasks.FirstOrDefault(t => t.Id == id) is not { IsOpen: true } task || task.Status == TaskStatus.WaitingOnYou) return;
        if (LineButtonOf(id) is not { } line || ((TaskLine)line.Tag).Closing) return;
        taskDrag = new TaskDragState(id, inList, Started: false, line);
        if (ClickTrace.On) ClickTrace.Write($"task press {task.Title}");
    }

    /// <summary>A move: starts the drag past the system drag distance, then shows where a release would drop.</summary>
    internal void TaskDragMove(Point inList)
    {
        if (taskDrag is not { } drag || taskLinesPanel is null) return;
        if (!drag.Started)
        {
            if (Math.Abs(inList.X - drag.PressedAt.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(inList.Y - drag.PressedAt.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            taskDrag = drag = drag with { Started = true };
            var captured = ArrangeMouseInput.Capture(taskLinesPanel);
            drag.Line.Opacity = TaskDragDimming;
            Cursor = Cursors.SizeNS;
            if (AdornerLayer.GetAdornerLayer(taskLinesPanel) is { } layer)
            {
                taskDropAdorner = new ArrangePreview(taskLinesPanel, PaintTaskDrop) { IsHitTestVisible = false };
                layer.Add(taskDropAdorner);
            }
            if (ClickTrace.On) ClickTrace.Write($"task drag-start captured={captured}");
        }
        taskDropShown = TaskDropAt(drag.Id, inList);
        taskDropAdorner?.InvalidateVisual();
    }

    /// <summary>
    /// The release. A press that never became a drag is only let go, painting nothing: its release is on its way to
    /// the line's Click, and a repaint now could take that line away under it (#48); a paint that waited for the press
    /// is served by the rebuild the bar's own release handler queued. A drag drops where its preview was last drawn,
    /// applied from the dispatcher queue (see the header), then the paint and any rebuild that waited are served.
    /// </summary>
    internal void TaskDragRelease()
    {
        if (taskDrag is not { } drag) return;
        var drop = taskDropShown;
        EndTaskDrag();
        if (!drag.Started) return;
        if (ClickTrace.On) ClickTrace.Write($"task drop {drop?.Quadrant} {drop?.Place}");
        taskDropQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (drop is not null && taskService is { } service) Report(service.Place(drag.Id, drop.Quadrant, drop.Place));
            }
            finally
            {
                taskDropQueued = false;
                PaintTaskList();
                FlushDeferredRebuild();
            }
        }));
    }

    /// <summary>Lost capture, a button that came up unseen: nothing is dropped, and what waited is served.</summary>
    internal void CancelTaskDrag(string why)
    {
        if (taskDrag is null) return;
        EndTaskDrag();
        if (ClickTrace.On) ClickTrace.Write($"task drag cancel ({why})");
        PaintTaskList();
        FlushDeferredRebuild();
    }

    // The state first, then the capture: releasing it raises LostMouseCapture, which must find no drag left to cancel.
    void EndTaskDrag()
    {
        var drag = taskDrag;
        taskDrag = null;
        taskDropShown = null;
        if (drag is not null) drag.Line.Opacity = 1;
        if (taskDropAdorner is { } adorner && taskLinesPanel is not null) AdornerLayer.GetAdornerLayer(taskLinesPanel)?.Remove(adorner);
        taskDropAdorner = null;
        ClearValue(CursorProperty);
        if (taskLinesPanel?.IsMouseCaptured == true) taskLinesPanel.ReleaseMouseCapture();
    }

    // Where a release at this point drops `dragged`: TaskDropTarget's rule (see the header), the one the window uses
    // too, over the lines on show. A closed line in its undo window is no target.
    TaskDropPreview? TaskDropAt(Guid dragged, Point inList)
    {
        if (taskLinesPanel is null || taskService is null) return null;
        var placed = taskLinesPanel.Children.OfType<Button>()
            .Where(b => b.Tag is TaskLine { Closing: false } && b.IsVisible)
            .Select(b => (TaskLine)b.Tag is var line
                ? new TaskDropTarget.Placed(line.Id, line.Quadrant, line.Status == TaskStatus.WaitingOnYou,
                    new Rect(b.TranslatePoint(new Point(0, 0), taskLinesPanel), b.RenderSize))
                : null)
            .OfType<TaskDropTarget.Placed>()
            .ToList();
        var own = taskService.Board.Tasks.FirstOrDefault(t => t.Id == dragged)?.Quadrant ?? Quadrant.IV;
        return TaskDropTarget.At(placed, [], own, inList) is { } drop ? new TaskDropPreview(drop.Quadrant, drop.Place, drop.LineY) : null;
    }

    // The drop line across the list, centred on the gap it means.
    void PaintTaskDrop(DrawingContext dc)
    {
        if (taskDropShown is not { } drop || taskLinesPanel is null) return;
        dc.DrawRoundedRectangle(InsertMarkerBrush, null, new Rect(0, drop.LineY - 1, taskLinesPanel.ActualWidth, 2), 1, 1);
    }

    Button? LineButtonOf(Guid id) =>
        taskLinesPanel?.Children.OfType<Button>().FirstOrDefault(b => b.Tag is TaskLine line && line.Id == id);
}
