using System.Reactive;
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Input;
using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// THE LINES HOLD STILL WHILE PETRE IS AT AN OPEN THREAD (task 106bc422, Petre: "task i'm interacting with shouldn't
// randomly close and jump to another one, when i'm on the tasks list. wait for me to be idle or when I have collapsed the
// details for it to update").
//
// WHAT HAPPENED. Measured in the trace of 2026-11-27: at 12:05 Petre opened a pull request from task a7eb75b5's thread; at
// 12:06:18 that task's session ran `task answered`, then `task duplicate-of`, which closed it. The redraw that pulse caused
// took the line away, open thread and all, and every line under it moved up a place, so the line where his thread had been
// was now another task. A task stepping into or out of Waiting on you, a session going from working to idle, a new task
// added by a session: each pulse redraws the list in the order the board has NOW, and every one of them can move the line
// he is reading or about to click.
//
// THE RULE. While a thread is open on the list AND he has touched the panel lately (a press, a wheel turn, a key, the
// pointer moving over it; see Touched), the ARRANGEMENT of the lines stays as it was last drawn:
//   - every line keeps its group and its place. Its CONTENT is still current: a new comment, a question, a status line
//     or a status shows in place, so nothing he reads is stale, only where it sits;
//   - a line the board no longer lists (closed, done, moved to a status the filter hides, its session stopped under the
//     Claude switch) stays where it was, drawn as it is now (a closed task drawn closed). A deleted task has nothing to
//     draw and leaves;
//   - a line the board lists that was not drawn waits (a task a session just added, one that came back from Snoozed);
//   - a task HE closed leaves as it always did, once its undo window is over (letGo), and a move HE makes on any line (a
//     drag, a flag toggle, a status, a snooze, a workspace, an assignee or a goal, a reopen) redraws the list as the board
//     has it (Moving): a drop that stayed put for a minute would look like a drop that failed.
// It lets go, and the list is redrawn as the board has it, when:
//   - the last open thread is folded (his "when I have collapsed the details"): Holding reads the open threads, so the
//     fold's own Refresh is the free one;
//   - he has left the panel alone for IdleAfter (his "wait for me to be idle"): see the idle timer below;
//   - he asks for a different list: a filter, the search, the Claude switch, the goal or assignee filter, Subtasks, the
//     List / Matrix switch or the Stats view, or the workspace "Current workspace" follows (HoldShape). The hold is of
//     what he was looking at, and that list is no longer on screen;
//   - his own action brings a task that is not drawn: a note he adds (ShowAdded selects it), a task clicked on the bar or
//     reached by a link (ShowTask), a subtask opened from a thread. The task he picked or opened must be on the list, so
//     the hold gives way rather than hide it (BringsNew).
// Only the panel's list. The bar's own task lines have their own hold (FloatingBar.heldExpanded), and a task window shows
// one task, so it has no arrangement to keep.
public sealed partial class TasksBoard
{
    // How long the panel must go untouched before a held list is let go. Not a few seconds, on purpose: in the case that
    // started this, Petre left the panel for the pull request his thread linked to and came back 70 seconds later, and a
    // line that moved while he was in the browser had moved all the same. The timer is clock-free (see OnIdleTick), so the
    // list is let go between one and two of these after the last touch.
    internal static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(45);

    // The list as last drawn: the shape it was drawn for (HoldShape) and its groups and Matrix cells, lines in order.
    // Null until the first draw. Written by Drawn on every Present, so a held draw's arrangement is the one the next
    // draw keeps.
    sealed record DrawnLines(string Shape, IReadOnlyList<TaskGroupView> Groups, IReadOnlyList<TaskCellView> Cells)
    {
        public HashSet<Guid> Ids { get; } = Groups.SelectMany(g => g.Rows).Concat(Cells.SelectMany(c => c.Rows)).Select(r => r.Id).ToHashSet();
    }

    DrawnLines? drawnLines;

    // Tasks Petre closed himself whose undo window has ended (Forget): their lines leave even from a held list. An id goes
    // once no drawn line carries it (Drawn).
    readonly HashSet<Guid> letGo = [];

    // True while one of Petre's own moves runs (Moving): the redraw its Changed pulse causes is drawn as the board has it.
    bool moving;

    // Runs one of Petre's own moves on a line (Place, SetFlag, SetStatus, Snooze, Wake, MoveTo, Assign, SetGoal, Reopen).
    // The service pulses Changed inside the call and the board redraws inline, so the flag is up for that redraw; the
    // arrangement it draws is the one a held list keeps from then on.
    Result Moving(Func<Result> move)
    {
        moving = true;
        try
        {
            return move();
        }
        finally
        {
            moving = false;
        }
    }

    // The shape the last View() was made for; Drawn records it with the view the window was handed.
    string viewShape = "";

    // Whether the panel has gone untouched for IdleAfter. True until the first touch, so a list nobody is at redraws as
    // it always did.
    bool idle = true;

    // A touch since the idle timer was last armed or re-armed (OnIdleTick).
    bool touchedSinceTick;

    // The idle timer, while one is running: one at a time, re-armed rather than restarted on every touch, since a
    // pointer moving over the panel touches it many times a second.
    IDisposable? idleTimer;

    // Where the pointer last was over the panel, so a mouse move WPF makes up after a redraw under a pointer that has not
    // moved (it sends one when the element under the pointer changes) is not taken for Petre moving it.
    Point? lastPointer;

    // Every press, wheel turn and key in the panel, and every real pointer move over it. Preview events, so a line, a
    // comment box or a button that handles the event itself still counts as touched.
    IObservable<Unit> Touched() =>
        Observable.Merge(
            Observable.FromEventPattern<MouseEventHandler, MouseEventArgs>(h => window.PreviewMouseMove += h, h => window.PreviewMouseMove -= h)
                .Where(e => PointerMoved(e.EventArgs.GetPosition(window)))
                .Select(_ => Unit.Default),
            Observable.FromEventPattern<MouseButtonEventHandler, MouseButtonEventArgs>(h => window.PreviewMouseDown += h, h => window.PreviewMouseDown -= h)
                .Select(_ => Unit.Default),
            Observable.FromEventPattern<MouseWheelEventHandler, MouseWheelEventArgs>(h => window.PreviewMouseWheel += h, h => window.PreviewMouseWheel -= h)
                .Select(_ => Unit.Default),
            Observable.FromEventPattern<KeyEventHandler, KeyEventArgs>(h => window.PreviewKeyDown += h, h => window.PreviewKeyDown -= h)
                .Select(_ => Unit.Default));

    bool PointerMoved(Point at)
    {
        var moved = lastPointer != at;
        lastPointer = at;
        return moved;
    }

    // A touch: the panel is not idle, and the idle timer runs if it was not running.
    void Touch()
    {
        idle = false;
        touchedSinceTick = true;
        idleTimer ??= undoTimer(IdleAfter, OnIdleTick);
    }

    // The idle timer's tick, IdleAfter after it was armed. Touched since: armed again for another IdleAfter. Untouched for
    // a whole IdleAfter: the panel is idle, the timer stops, and the list is redrawn as the board has it, which draws
    // nothing when the hold kept nothing back (the window skips a draw that changes nothing).
    void OnIdleTick()
    {
        idleTimer = null;
        if (disposed) return;
        if (touchedSinceTick)
        {
            touchedSinceTick = false;
            idleTimer = undoTimer(IdleAfter, OnIdleTick);
            return;
        }
        idle = true;
        Refresh();
    }

    // Whether the list holds still now: he touched the panel lately and a thread is open on the list as last drawn (a
    // line's thread, or a subtask opened inside its parent's thread; an id left in `expanded` for a task no longer drawn
    // opens nothing on screen).
    bool Holding => !idle && drawnLines is { } drawn && expanded.Any(id => drawn.Ids.Contains(id) || NestedOpen(drawn, id));

    static bool NestedOpen(DrawnLines drawn, Guid id) =>
        drawn.Groups.SelectMany(g => g.Rows).Concat(drawn.Cells.SelectMany(c => c.Rows)).Any(r => r.Subtasks.Any(s => s.Id == id));

    // What list is on show, as text: compared, never parsed. Everything Petre chooses that changes which tasks the list
    // holds or how it lays them out; the workspace in front only while "Current workspace" follows it (`shown` is the
    // filter resolved against it).
    string HoldShape(TaskWorkspaceFilter shown, bool byId) =>
        $"{filter}|{shown}|{claudeFilter}|{goalFilter}|{assigneeFilter}|{search.Text}|{byId}|{statsShown}|{PanelState.HideSubtasks}|{manager.State.TasksMatrix}";

    // The groups and cells to draw: the fresh ones, or, while the list holds, the last drawn arrangement with each line's
    // fresh content (see the header). `whole` is the board with the pending closes, where a line the fresh view no longer
    // lists is found to draw it as it is now.
    (IReadOnlyList<TaskGroupView> Groups, IReadOnlyList<TaskCellView> Cells) Held(
        IReadOnlyList<TaskGroupView> groups, IReadOnlyList<TaskCellView> cells, string shape, TaskBoard whole)
    {
        viewShape = shape;
        if (moving || !Holding || drawnLines is not { } drawn || drawn.Shape != shape) return (groups, cells);
        // The List's rows and the Matrix's rows are made differently (a List row is Nested under its parent and its thread
        // leaves out the subtasks drawn under it; a cell row is neither), so each keeps its own (review).
        static Dictionary<Guid, TaskRowView> ById(IEnumerable<TaskRowView> rows) =>
            rows.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
        var freshLines = ById(groups.SelectMany(g => g.Rows));
        var freshCells = ById(cells.SelectMany(c => c.Rows));
        if (BringsNew(drawn, freshLines.Keys.Concat(freshCells.Keys))) return (groups, cells);
        // A line no longer listed: the task as it is now, Nested as it was drawn (it was under its parent then). A task
        // deleted since has no row and leaves.
        Maybe<TaskRowView> Now(Dictionary<Guid, TaskRowView> fresh, TaskRowView was) =>
            fresh.TryGetValue(was.Id, out var row) ? row
            : letGo.Contains(was.Id) ? Maybe<TaskRowView>.None
            : whole.Tasks.TryFirst(t => t.Id == was.Id).Map(task => Row(task) with { Nested = was.Nested });
        IReadOnlyList<TaskRowView> Kept(Dictionary<Guid, TaskRowView> fresh, IReadOnlyList<TaskRowView> rows) =>
            rows.Select(r => Now(fresh, r)).Where(r => r.HasValue).Select(r => r.Value).ToArray();
        var heldGroups = drawn.Groups.Select(g => g with { Rows = Kept(freshLines, g.Rows) }).Where(g => g.Rows.Count > 0).ToArray();
        var heldCells = drawn.Cells.Select(c => c with { Rows = Kept(freshCells, c.Rows) }).ToArray();
        // A SUBTASK SHOWS ONCE (task 0eb943d8) in the held list too: a subtask whose own line is held (its session closed
        // it, so the fresh list dropped the line and its parent's thread lists it in place again) is marked as having a
        // line against the held lines, not the fresh ones, so it is not drawn a second time inside its parent's thread.
        // In the List, one drawn right under its parent (Nested) is left out of the thread altogether, as View does.
        var groupIds = heldGroups.SelectMany(g => g.Rows).Select(r => r.Id).ToHashSet();
        var underParents = heldGroups.SelectMany(g => g.Rows).Where(r => r.Nested).Select(r => r.Id).ToHashSet();
        var cellIds = heldCells.SelectMany(c => c.Rows).Select(r => r.Id).ToHashSet();
        return (
            heldGroups.Select(g => g with
            {
                Rows = g.Rows.Select(r => WithLinedSubtasks(r with { Subtasks = r.Subtasks.Where(sub => !underParents.Contains(sub.Id)).ToArray() }, groupIds)).ToArray(),
            }).ToArray(),
            heldCells.Select(c => c with { Rows = c.Rows.Select(r => WithLinedSubtasks(r, cellIds)).ToArray() }).ToArray());
    }

    // Whether the fresh list brings a line his own action asked for that the held one lacks: the selected task (a note he
    // added, a task clicked on the bar or reached by a link, a folded subtask picked in its parent's thread). Not every id
    // in `expanded`: it keeps the ids of threads opened long ago, so a task a session brings back would let go with no
    // action of his (review).
    bool BringsNew(DrawnLines drawn, IEnumerable<Guid> fresh) =>
        fresh.Any(id => !drawn.Ids.Contains(id) && selection.IsSelected(id));

    // The window was handed this view: its arrangement is the one a held draw keeps next.
    TasksView Drawn(TasksView view)
    {
        drawnLines = new DrawnLines(viewShape, view.Groups, view.Cells);
        letGo.RemoveWhere(id => !drawnLines.Ids.Contains(id));
        return view;
    }

    // Started with the board's other subscriptions (the constructor), ended with them.
    IDisposable WatchTouches() =>
        new System.Reactive.Disposables.CompositeDisposable(
            Touched().Subscribe(_ => Touch()),
            System.Reactive.Disposables.Disposable.Create(() => idleTimer?.Dispose()));
}
