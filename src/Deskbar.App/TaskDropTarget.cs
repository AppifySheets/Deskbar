using System.Windows;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// WHERE A DRAGGED TASK LINE LANDS, one rule for the bar (FloatingBar.TaskDrag.cs) and the tasks window
// (TasksWindow.Drag.cs), so the two cannot disagree. Task 12, progress.md Rulings 24, 25 and 28.
//
// The pointer is over a line (the nearest one, between lines): over its top half the task goes just BEFORE it, over
// its bottom half just AFTER it, in that line's quadrant, so a drop among another quadrant's lines takes that
// quadrant's flags (TaskBoard.Place sets them). Waiting on you, its questions and its Done tasks, is pinned: nothing drops above or among its lines; a
// drop on one goes to the TOP of `pinnedTo`, the dragged task's own quadrant on the bar and in the List, the cell's
// quadrant in the Matrix. It never takes the quadrant of whatever line happens to sit under the block (review of
// 4fc4441..a1acd60, item 4: a plain task dragged to the top quietly became important and urgent). A caption of an empty place (the window's Matrix cell with no line) is given as an
// `Empty` area: a drop inside it goes to the top of that quadrant.
//
// DRAG TO NEST (Petre: "can i put a task as a subtask by drag and dropping it?" ... "build it"; follow-up to
// docs/superpowers/specs/2026-11-24-subtasks-design.md). A line that can take the dragged task as its subtask
// (Placed.TakesChild, which the caller works out from TaskBoard.ParentCandidates) is split in three bands: over its
// middle half the drop makes the dragged task its subtask (Drop.Into), over its top or bottom quarter the drop reorders
// as above. The bands are the way file trees and Todoist tell "into" from "beside", and they keep a reorder a reorder
// on every line that cannot take a child (a subtask, a pinned task waiting on Petre or Done, the dragged task itself, any line while a
// task with subtasks of its own is dragged).
internal static class TaskDropTarget
{
    /// <summary>A line on show: its task, its quadrant, whether it waits on Petre, and its box in the drag's coordinates.</summary>
    internal sealed record Placed(Guid Id, Quadrant Quadrant, bool Waiting, Rect Bounds)
    {
        /// <summary>Whether a drop on this line's middle makes the dragged task its subtask (see the header).</summary>
        public bool TakesChild { get; init; }

        /// <summary>
        /// The bottom of the block this line heads (itself and the subtasks drawn under it), or null for just the line. A
        /// task dropped after a parent lands below its whole block, so the drop line is drawn there (review of the
        /// drag-to-nest branch, finding 2).
        /// </summary>
        public double? BlockBottom { get; init; }
    }

    /// <summary>A line as it is drawn, in order: its target and whether it is a subtask drawn under its parent.</summary>
    internal sealed record Drawn(Placed Placed, bool Nested);

    /// <summary>
    /// The lines that can be aimed at, from the lines as drawn: every line but the nested ones (their rank is not where
    /// they are drawn; review of #467, finding 2), each with the bottom of the block it heads.
    /// </summary>
    internal static IReadOnlyList<Placed> Anchors(IReadOnlyList<Drawn> drawn) =>
        drawn.Select((d, i) => (d, i)).Where(x => !x.d.Nested)
            .Select(x => x.d.Placed with
            {
                BlockBottom = drawn.Skip(x.i + 1).TakeWhile(n => n.Nested).Select(n => (double?)n.Placed.Bounds.Bottom).LastOrDefault(),
            })
            .ToArray();

    /// <summary>
    /// Whether <paramref name="at"/> is inside the block of the nested line <paramref name="dragged"/> (its parent's line to
    /// the last subtask under it). A drop there changes nothing, so a wiggle of a subtask never frees it (review of the
    /// drag-to-nest branch, finding 1). False when the dragged line is not nested.
    /// </summary>
    internal static bool InOwnBlock(IReadOnlyList<Drawn> drawn, Guid dragged, Point at)
    {
        var index = drawn.Select((d, i) => (d, i)).FirstOrDefault(x => x.d.Placed.Id == dragged);
        if (index.d is not { Nested: true }) return false;
        var start = drawn.Take(index.i).Select((d, i) => (d, i)).LastOrDefault(x => !x.d.Nested).i;
        var end = start + 1 + drawn.Skip(start + 1).TakeWhile(d => d.Nested).Count() - 1;
        return at.Y >= drawn[start].Placed.Bounds.Top && at.Y <= drawn[end].Placed.Bounds.Bottom;
    }

    /// <summary>An area that stands for a whole quadrant (an empty Matrix cell): a drop in it goes to that quadrant's top.</summary>
    internal sealed record Area(Quadrant Quadrant, Rect Bounds);

    /// <summary>What a release would do, and where its line is drawn (a y in the drag's coordinates).</summary>
    internal sealed record Drop(Quadrant Quadrant, TaskPlace Place, double LineY)
    {
        /// <summary>The task the dragged one becomes a subtask of, instead of being placed; null for a reorder.</summary>
        public Guid? Into { get; init; }

        /// <summary>The box of the line <see cref="Into"/> names, outlined while the drag hovers it.</summary>
        public Rect IntoBounds { get; init; }
    }

    /// <summary>The drop for a pointer at <paramref name="at"/>, or none when no line or area can answer.</summary>
    internal static Drop? At(IReadOnlyList<Placed> lines, IReadOnlyList<Area> areas, Quadrant pinnedTo, Point at)
    {
        if (areas.FirstOrDefault(a => a.Bounds.Contains(at)) is { } area)
            return new Drop(area.Quadrant, TaskPlace.Top, area.Bounds.Top);
        if (lines.Count == 0) return null;

        var nearest = lines.OrderBy(p => Distance(p.Bounds, at.Y)).First();
        if (nearest.Waiting) return new Drop(pinnedTo, TaskPlace.Top, TopOf(lines, pinnedTo));
        if (nearest.TakesChild && InMiddle(nearest.Bounds, at.Y))
            return new Drop(nearest.Quadrant, TaskPlace.Top, nearest.Bounds.Top) { Into = nearest.Id, IntoBounds = nearest.Bounds };
        return at.Y < nearest.Bounds.Top + nearest.Bounds.Height / 2
            ? new Drop(nearest.Quadrant, TaskPlace.Before(nearest.Id), nearest.Bounds.Top)
            : new Drop(nearest.Quadrant, TaskPlace.After(nearest.Id), nearest.BlockBottom ?? nearest.Bounds.Bottom);
    }

    // Where the top of `quadrant` is among the lines on show, so the drop line is drawn where the task will land (review
    // of the fix round, minor 2): above the quadrant's first line; with none on show, under the last line of the
    // quadrants before it (where its first line would be); with no open line at all, under the waiting block.
    static double TopOf(IReadOnlyList<Placed> lines, Quadrant quadrant)
    {
        var open = lines.Where(p => !p.Waiting).OrderBy(p => p.Bounds.Top).ToList();
        return open.FirstOrDefault(p => p.Quadrant >= quadrant) is { } first ? first.Bounds.Top
            : open.Count > 0 ? open[^1].Bounds.Bottom
            : lines.Max(p => p.Bounds.Bottom);
    }

    // The middle of a line: below its top quarter and above its bottom quarter, each edge at least 5 DIP (a bar line is
    // about 18 DIP, where a quarter is a 4 DIP target; review of the drag-to-nest branch, minor 4) and at most a third.
    static bool InMiddle(Rect bounds, double y)
    {
        var edge = Math.Min(Math.Max(bounds.Height / 4, 5), bounds.Height / 3);
        return y >= bounds.Top + edge && y <= bounds.Bottom - edge;
    }

    // How far a y is from a box, vertically: 0 inside it.
    static double Distance(Rect bounds, double y) =>
        y < bounds.Top ? bounds.Top - y : y > bounds.Bottom ? y - bounds.Bottom : 0;
}
