using System.Windows;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// WHERE A DRAGGED TASK LINE LANDS, one rule for the bar (FloatingBar.TaskDrag.cs) and the tasks window
// (TasksWindow.Drag.cs), so the two cannot disagree. Task 12, progress.md Rulings 24, 25 and 28.
//
// The pointer is over a line (the nearest one, between lines): over its top half the task goes just BEFORE it, over
// its bottom half just AFTER it, in that line's quadrant, so a drop among another quadrant's lines takes that
// quadrant's flags (TaskBoard.Place sets them). Waiting on you is pinned: nothing drops above or among its lines; a
// drop on one goes to the TOP of `pinnedTo`, the dragged task's own quadrant on the bar and in the List, the cell's
// quadrant in the Matrix. It never takes the quadrant of whatever line happens to sit under the block (review of
// 4fc4441..a1acd60, item 4: a plain task dragged to the top quietly became important and urgent). A caption of an empty place (the window's Matrix cell with no line) is given as an
// `Empty` area: a drop inside it goes to the top of that quadrant.
internal static class TaskDropTarget
{
    /// <summary>A line on show: its task, its quadrant, whether it waits on Petre, and its box in the drag's coordinates.</summary>
    internal sealed record Placed(Guid Id, Quadrant Quadrant, bool Waiting, Rect Bounds);

    /// <summary>An area that stands for a whole quadrant (an empty Matrix cell): a drop in it goes to that quadrant's top.</summary>
    internal sealed record Area(Quadrant Quadrant, Rect Bounds);

    /// <summary>What a release would do, and where its line is drawn (a y in the drag's coordinates).</summary>
    internal sealed record Drop(Quadrant Quadrant, TaskPlace Place, double LineY);

    /// <summary>The drop for a pointer at <paramref name="at"/>, or none when no line or area can answer.</summary>
    internal static Drop? At(IReadOnlyList<Placed> lines, IReadOnlyList<Area> areas, Quadrant pinnedTo, Point at)
    {
        if (areas.FirstOrDefault(a => a.Bounds.Contains(at)) is { } area)
            return new Drop(area.Quadrant, TaskPlace.Top, area.Bounds.Top);
        if (lines.Count == 0) return null;

        var nearest = lines.OrderBy(p => Distance(p.Bounds, at.Y)).First();
        if (nearest.Waiting)
            return new Drop(pinnedTo, TaskPlace.Top, lines.Where(p => p.Waiting).Max(p => p.Bounds.Bottom));
        return at.Y < nearest.Bounds.Top + nearest.Bounds.Height / 2
            ? new Drop(nearest.Quadrant, TaskPlace.Before(nearest.Id), nearest.Bounds.Top)
            : new Drop(nearest.Quadrant, TaskPlace.After(nearest.Id), nearest.Bounds.Bottom);
    }

    // How far a y is from a box, vertically: 0 inside it.
    static double Distance(Rect bounds, double y) =>
        y < bounds.Top ? bounds.Top - y : y > bounds.Bottom ? y - bounds.Bottom : 0;
}
