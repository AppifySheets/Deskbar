using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using CSharpFunctionalExtensions;
using Deskbar.Core;
using Deskbar.Core.Goals;
using Deskbar.Core.Persistence;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// THE TASKS WINDOW'S CONTROLLER. Design: docs/superpowers/specs/2026-11-22-tasks-board-design.md, sections "The window",
// "Comments", "Statuses" and "Going to a session". Mockup: .superpowers/brainstorm/876-1795339550/content/tasks-window.html.
// The window (TasksWindow) only draws; this decides what it shows and what each click does, the split Quick find has
// (QuickFind and QuickFindWindow).
//
// WHAT IT SHOWS. Every task, grouped as the bar groups them (Task 12, Ruling 28): the open ones by default, Waiting on
// you first, then Covey's quadrants I to IV under captions in his words, each in Petre's own order (TaskViews.Grouped);
// a status filter keeps the quadrants and shows that status alone in each; the closed ones only under "Closed", the
// latest closed first. Each line has the important and urgent toggles (SetFlag) and drags to reorder (Place), as on
// the bar. A workspace filter narrows any of them to one workspace or to none. Each line carries its title, the
// question or status line, its sessions (live or ended), its workspace and its comment count; a line's arrow opens its
// thread. The window opens onto this board whenever it was hidden (filters reset, threads closed, no text box
// focused): the first version opened onto an entry form and Petre did not like it (spec, "Anti-goals"). It is a page
// of its own (Petre: "can i have a separate page for all tasks?"), so an open window asked again only comes forward.
//
// WHAT A CLICK DOES. Each action is one method here, calling the same TaskService method the bar and the control pipe
// call, so a change from the window obeys the rules a click on the bar or a `deskbar.cmd task` does, and every view
// redraws from TaskService.Changed. A refusal shows on the window's problem line until the next action works.
//   Go            a line's click: through `goTo`. The window, a page of its own, stays open behind the switch on the
//                 desktop it is on. The App points goTo at the bar's own
//                 GoToTask path (the task's workspace). Controller ruling 13: a Claude task goes to its workspace too
//                 until a later change measures the `vscode://` session link; that change swaps goTo alone, and
//                 TaskViews.SessionToGoTo already names the session it will open. A note on no workspace has nowhere
//                 to go, so its click opens its thread instead.
//   Close         at once through TaskService.Close (only Petre closes, and this is his click), with the bar's undo
//                 window: the line stays in its place for six seconds, struck through, with Undo (Ruling 10).
//   Undo          exactly undo (Ruling 31): the status it had, its question if it waited on Petre, its place and the
//                 selection (TaskBoard.UndoClose).
//   Reopen        a closed task under the Closed filter: TaskService.Reopen.
//   SetStatus     Backlog, In progress, Waiting on you or Done by hand. Closed is refused there (Close is the way).
//   Rename, MoveTo, Delete, AddNote, Comment (as Petre, no session; shown as "you").
//
// WHEN IT REDRAWS. On every TaskService.Changed and WorkspaceManager.StateChanged while the window is open (a workspace
// renamed or recoloured changes the chips), and after each of its own actions. The window compares what it would
// draw with what it drew and redraws nothing when they match, and it waits while one of its menus is open, a rename
// is under way or a press is in flight (TasksWindow.MustWait): the bar's #77 lesson about redrawing under an open
// menu, applied to this window's own refresh.
//
// GOALS (goals plan, Task 6; docs/superpowers/specs/2026-11-22-goals-planner-design.md, "Linking a task to a goal
// elsewhere"). With a GoalService, a line's More menu gains "Goal" (None, then this month's and this week's OPEN goals,
// the task's own goal checked), which links through GoalService.LinkTask (SetGoal), the one place a task is linked; a
// refusal (a goal done meanwhile) shows on the problem line. The toolbar gains a goal filter beside the workspace one:
// all goals, no goal (a dangling link counts as no goal), or one goal (a monthly goal also shows its weekly goals'
// tasks), applied to the board BEFORE the groups, the Matrix and the chips' counts are made (TaskGoalFilter.Narrow),
// so every view and every count agrees with it. Ruling G1: both appear only while at least one goal exists, so a copy
// with no goals looks exactly as before. A filter whose goal no longer exists falls back to every goal (review M7),
// rather than showing an empty list under a filter nobody can see. The window redraws on GoalService.Changed too, since
// the filter's label and choices show goal titles.
public sealed class TasksBoard : IDisposable
{
    // How long a line just closed stays, struck through with Undo: the bar's own window, so the two agree.
    static readonly TimeSpan UndoWindow = FloatingBar.UndoWindow;

    // The chips' order, the open view's group order with Open first and Closed last.
    static readonly IReadOnlyList<TaskFilter> FilterOrder =
        [TaskFilter.Open, TaskFilter.WaitingOnYou, TaskFilter.Done, TaskFilter.InProgress, TaskFilter.Backlog, TaskFilter.Closed];

    readonly TasksWindow window;
    readonly TaskService tasks;
    readonly WorkspaceManager manager;
    readonly Func<TaskItem, Result> goTo;
    readonly Func<Guid, Brush?> colourOf;
    readonly Func<nint, bool>? bringForward;
    readonly Func<TimeSpan, Action, IDisposable> undoTimer;
    readonly Func<DateTimeOffset> now;
    readonly TaskSelection selection;
    readonly GoalService? goals;
    readonly IDisposable subscriptions;

    // The lines closed in this window and still in their undo window: the status each had, and its timer.
    readonly Dictionary<Guid, PendingClose> pendingCloses = [];
    readonly HashSet<Guid> expanded = [];
    TaskFilter filter = TaskFilter.Open;
    TaskWorkspaceFilter where = TaskWorkspaceFilter.All;
    TaskGoalFilter goalFilter = TaskGoalFilter.All;
    // The toolbar's "Assigned" filter (Ruling 57); like the others it starts at "all" whenever the window opens.
    TaskAssigneeFilter assigneeFilter = TaskAssigneeFilter.All;

    // What Undo gives back (Ruling 31): the status, the question of a task that waited on Petre, and the selection.
    sealed record PendingClose(TaskStatus Was, IDisposable Timer)
    {
        public Maybe<string> Question { get; init; } = Maybe<string>.None;

        public bool Selected { get; init; }
    }

    //   goTo          where a line's click goes (see the header)
    //   colourOf      a workspace's lane colour, the one its row on the bar wears; none for a workspace without one
    //   bringForward  brings the window to Petre by its handle: the App moves it to the desktop in front and then
    //                 activates it (Arrive), through an activator, because a `deskbar.cmd tasks-window show` typed in a
    //                 terminal carries no right to take the foreground. It answers whether it activated the window: a
    //                 window left on another desktop is not, and must then not be maximized either (Arrived). The
    //                 window itself never activates while this is set (see Open)
    //   undoTimer     runs `then` once after the undo window unless disposed first; a dispatcher timer by default, a
    //                 seam so the tests move time by hand
    //   now           the clock a comment's time is told against ("11:42" today, a date before)
    //   goals         the goal service, for the "Goal" menu and the goal filter (see the header); none, no goals UI
    public TasksBoard(TasksWindow window, TaskService tasks, WorkspaceManager manager, Func<TaskItem, Result> goTo,
        Func<Guid, Brush?>? colourOf = null, Func<nint, bool>? bringForward = null, Func<TimeSpan, Action, IDisposable>? undoTimer = null,
        Func<DateTimeOffset>? now = null, TaskSelection? selection = null, GoalService? goals = null)
    {
        this.goals = goals;
        this.window = window;
        this.tasks = tasks;
        this.manager = manager;
        this.goTo = goTo;
        this.colourOf = colourOf ?? (_ => null);
        this.bringForward = bringForward;
        this.undoTimer = undoTimer ?? FloatingBar.DispatcherUndoTimer;
        this.now = now ?? (() => DateTimeOffset.Now);
        // The task Petre picked (ruling 21), the bar's own object in the App, so a pick in either view shows in both.
        this.selection = selection ?? new TaskSelection();

        window.Actions = new TasksWindowActions(
            Go: id => Go(id),
            Pick: id => Pick(id),
            ToggleThread: ToggleThread,
            Close: id => Close(id),
            Undo: id => Undo(id),
            Reopen: id => Reopen(id),
            SetStatus: (id, status) => SetStatus(id, status),
            Rename: (id, title) => Rename(id, title).IsSuccess,
            Edit: (id, title, description) => Edit(id, title, description).IsSuccess,
            EditComment: (comment, markdown) => EditComment(comment, markdown).IsSuccess,
            MoveTo: (id, workspace) => MoveTo(id, workspace),
            Delete: id => Delete(id),
            Comment: (id, markdown) => Comment(id, markdown).IsSuccess,
            AddNote: (title, workspace, quadrant) => AddNote(title, workspace, quadrant).IsSuccess,
            SetFlag: (id, flag, on) => SetFlag(id, flag, on),
            Place: (id, quadrant, place) => Place(id, quadrant, place),
            ShowMatrix: on => Report(manager.SetTasksMatrix(on)),
            Filter: Filter,
            FilterWorkspace: FilterWorkspace,
            Remember: place => manager.SaveTasksWindow(place),
            SetGoal: (id, goal) => SetGoal(id, goal),
            FilterGoal: FilterGoal,
            Assign: (id, who) => Assign(id, who),
            FilterAssignee: FilterAssignee);

        subscriptions = new CompositeDisposable(
            tasks.Changed.Subscribe(_ => Refresh()),
            // A goal added, renamed, done or deleted (from the pipe, later the planner) changes the goal filter's label
            // and choices and, with G1, whether goals show at all.
            goals?.Changed.Subscribe(_ => Refresh()) ?? Disposable.Empty,
            // Only a change the window shows (whole-PR review, minor 4): the workspaces (names, colours, groups, order),
            // which workspace is in front (where a new note goes) and the List / Matrix switch. Every other state pulse,
            // a window moving or a stats tick, used to rebuild the whole view for nothing; tasks have their own pulse.
            manager.StateChanged.Select(_ => ShownStateKey()).StartWith(ShownStateKey()).DistinctUntilChanged().Skip(1).Subscribe(_ => Refresh()),
            this.selection.Changed.Subscribe(_ => window.PaintSelection(this.selection.Current)));
    }

    // What of the workspace state the window shows, as text: compared, never parsed.
    string ShownStateKey() =>
        string.Join('|', manager.State.Workspaces.Select(w => $"{w}"))
        + "|" + string.Join('|', manager.State.Groups.Select(g => $"{g}"))
        + $"|{manager.CurrentWorkspaceId}|{manager.State.TasksMatrix}";

    /// <summary>
    /// The window's handle, created if it is not yet. The bar lists the tasks window like any app window (App.xaml.cs),
    /// and the App's bring-forward and tiling know it by this handle.
    /// </summary>
    public nint EnsureHandle() => handle = new WindowInteropHelper(window).EnsureHandle();

    // The handle once EnsureHandle has made it, and 0 before: what the tiling predicate reads, so asking "is this the tasks
    // window?" never creates a window handle from inside a tiling decision (review, Minor 2).
    nint handle;

    /// <summary>The window's handle if it has one yet, else 0. Never creates it.</summary>
    public nint KnownHandle => handle;

    public bool IsOpen => window.IsOpen;

    /// <summary>
    /// The bar's "+N more" and `tasks-window show`. A window already open comes forward with its view as it was. A
    /// hidden one opens onto the board where Petre last left it (AppState.TasksWindow), or clear of
    /// <paramref name="near"/> (the bar's rectangle on screen, device pixels; empty to centre it) the first time and
    /// when no screen holds that place any more; every such open starts at the default view: open tasks, every
    /// workspace, no thread open.
    /// </summary>
    //
    // THE WINDOW NEVER ACTIVATES ITSELF (fix round 1, Critical 1). Activating a window that lives on another desktop
    // makes Windows follow it there (WorkspaceManager's note), and this window is a page that stays on the desktop it
    // was left on: open it, click a line (which switches workspace), click "+N more", and an Activate first would take
    // Petre straight back. So both paths here only show or restore the window without activating it, and then hand it
    // to Arrived, whose seam (the App's, through Arrive) moves it to the desktop in front and only then activates it.
    public void Open(Rect near)
    {
        if (window.IsOpen)
        {
            Arrived();
            return;
        }
        filter = TaskFilter.Open;
        where = TaskWorkspaceFilter.All;
        goalFilter = TaskGoalFilter.All;
        assigneeFilter = TaskAssigneeFilter.All;
        expanded.Clear();
        window.ShowProblem(null);
        window.PaintSelection(selection.Current);
        window.Present(View());
        window.Open(near, manager.State.TasksWindow is { } place ? Maybe.From(place) : Maybe<TasksWindowState>.None);
        // The selected task's line comes into view (ruling 21: the window scrolls to it when it opens).
        window.ScrollToSelected();
        Arrived();
    }

    // The window, shown or restored without activation, comes to Petre: through the App's seam (move, then activate),
    // or, with no seam (a copy without a desktop service), activated where it is. A window left maximized is maximized
    // only now, after the move (review I-2), and only when it was really activated: setting Maximized on a window
    // still on another desktop activates it, and Windows would follow it there (review minor 1 of 187cf4a). It stays
    // due, so the next arrival that works maximizes it.
    // The keyboard goes to the toolbar only now, after the activation (see TasksWindow.FocusBoard): a window left on
    // another desktop is not focused, since focusing it would activate it there.
    void Arrived()
    {
        var activated = bringForward is { } seam ? seam(EnsureHandle()) : ActivatedHere();
        if (!activated) return;
        window.MaximizeIfRemembered();
        window.FocusBoard();
    }

    bool ActivatedHere()
    {
        window.ActivateHere();
        return true;
    }

    /// <summary>
    /// The App's bring-forward, in its order (fix round 1, Critical 1): <paramref name="move"/> the window to the
    /// desktop in front first, and <paramref name="activate"/> it only after. When the move fails, it is activated only
    /// if it is already on the desktop in front (<paramref name="onCurrentDesktop"/>); a window left on another desktop
    /// is traced and left there, since activating it would take Petre to it. Answers whether it activated the window.
    /// </summary>
    //
    // RAISED BEFORE IT IS ACTIVATED (2026-11-23, Petre: "tasks should become a top window, now it's behind somewhere").
    // His trace had four clicks on "+N more", no failed move, no other window taking the foreground, and in between him
    // dragging and minimizing his tiles to find the window under them. Activation alone does not promise the top of the
    // z-order: SetForegroundWindow on a window that already is the foreground window changes nothing (measured on the
    // hidden desktop: an active window with another raised over it stayed under it after WindowActivator.Activate), and
    // the window could already be the foreground one (TasksWindow.FocusBoard says how). So `raise` puts it on top of
    // the non-topmost windows first, a z-order change Windows allows a process for its own window without any foreground
    // right, and only then is it activated. It is never made topmost: it is an ordinary window that tiles.
    internal static bool Arrive(nint hwnd, Func<Result> move, Func<bool> onCurrentDesktop, Action raise, Action activate, Action<string> trace)
    {
        var moved = move();
        if (moved.IsFailure)
        {
            trace($"tasks window 0x{hwnd:X}: not moved to the desktop in front: {moved.Error}");
            if (!onCurrentDesktop())
            {
                trace($"tasks window 0x{hwnd:X}: left on its own desktop rather than taking you there");
                return false;
            }
        }
        raise();
        activate();
        // One line per arrival, so a live check reads whether the window really ended up in front.
        trace($"tasks window 0x{hwnd:X}: raised and activated, foreground now 0x{Windows.Monitoring.NativeMethods.GetForegroundWindow():X}");
        return true;
    }

    /// <summary>
    /// The tiling surface's "ours" (2026-11-23, Petre: "maybe even tile it"): Deskbar keeps its own windows out of the
    /// grid (the bar, Manage, its dialogs), except the tasks window, a page he works in that tiles like any app window.
    /// </summary>
    internal static bool KeptFromTiling(nint hwnd, nint tasksWindow) => hwnd != tasksWindow && FloatingBar.IsOwnWindow(hwnd);

    /// <summary>
    /// Puts the window at the top of the z-order among the windows that are not topmost, without activating it and
    /// without moving or sizing it (HWND_TOP, SWP_NOACTIVATE).
    /// </summary>
    internal static void RaiseToTop(nint hwnd) =>
        Windows.Monitoring.NativeMethods.SetWindowPos(hwnd, 0, 0, 0, 0, 0,
            Windows.Monitoring.NativeMethods.SWP_NOMOVE | Windows.Monitoring.NativeMethods.SWP_NOSIZE | Windows.Monitoring.NativeMethods.SWP_NOACTIVATE);

    /// <summary>`tasks-window hide`, as the title bar's X does: hidden, its place remembered.</summary>
    public void Dismiss() => window.Dismiss();

    /// <summary>Redraws an open window from the board as it is now; the window skips it when nothing it shows changed.</summary>
    internal void Refresh()
    {
        if (window.IsOpen) window.Present(View());
    }

    public void Filter(TaskFilter shown)
    {
        filter = shown;
        Refresh();
    }

    public void FilterWorkspace(TaskWorkspaceFilter shown)
    {
        where = shown;
        Refresh();
    }

    /// <summary>The toolbar's goal filter: every goal, no goal, or one goal (see the header).</summary>
    public void FilterGoal(TaskGoalFilter shown)
    {
        goalFilter = shown;
        Refresh();
    }

    /// <summary>The toolbar's "Assigned" filter (Ruling 57): me, Claude or all.</summary>
    public void FilterAssignee(TaskAssigneeFilter shown)
    {
        assigneeFilter = shown;
        Refresh();
    }

    public void ToggleThread(Guid id)
    {
        if (!expanded.Remove(id)) expanded.Add(id);
        Refresh();
    }

    // --- actions ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// A single click on a line (Ruling 54, Petre: "clicking a task should open that task; double clicking takes me to the
    /// workspace and window"): the task is picked, in both views, and its comment thread opens. It goes nowhere; a
    /// double-click does (<see cref="Go"/>).
    /// </summary>
    public void Pick(Guid id)
    {
        selection.Select(id);
        expanded.Add(id);
        Refresh();
    }

    /// <summary>
    /// The bar's single click on a task line (Ruling 54): the window comes forward (opening clear of
    /// <paramref name="near"/> when it was hidden) on that task, picked, scrolled into view and expanded to its thread.
    /// It goes nowhere.
    /// </summary>
    public void ShowTask(Guid id, Rect near)
    {
        selection.Select(id);
        Open(near);
        // Open starts a hidden window at the default view (no thread open); the task's own thread opens after it.
        expanded.Add(id);
        window.PaintSelection(selection.Current);
        Refresh();
        window.ScrollToSelected();
    }

    /// <summary>A line's click (see the header). The window stays open; a refusal shows on its problem line.</summary>
    public Result Go(Guid id)
    {
        var task = tasks.Board.Tasks.TryFirst(t => t.Id == id);
        if (task.HasNoValue) return Report(Result.Failure($"No task with id {id}"));
        // The click picks the task, in both views (ruling 21), whether or not it has anywhere to go.
        selection.Select(id);
        if (!task.Value.IsClaudes && task.Value.WorkspaceId.HasNoValue)
        {
            ToggleThread(id);
            return Report(Result.Success());
        }
        return Report(goTo(task.Value));
    }

    /// <summary>Closes the task at once, keeping its line in place with Undo for the undo window (see the header).</summary>
    public Result Close(Guid id)
    {
        if (tasks.Board.Tasks.FirstOrDefault(t => t.Id == id) is not { IsOpen: true } task) return Report(tasks.Close(id));
        // Pending before Close pulses Changed, so the redraw that pulse causes already draws the line struck through.
        pendingCloses[id] = new PendingClose(task.Status, undoTimer(UndoWindow, () => Forget(id)))
        {
            Question = task.Question,
            Selected = selection.IsSelected(id),
        };
        return Report(tasks.Close(id).TapError(_ => Forget(id)));
    }

    /// <summary>
    /// Undo on a line just closed, by the bar's rule (FloatingBar.UndoClose), so the two Undos agree: exactly undo
    /// (Ruling 31). The task comes back with the status it had before the close and, when it waited on Petre, its
    /// question, in its place (TaskBoard.UndoClose), and selected again if it was the task selected.
    /// </summary>
    public Result Undo(Guid id)
    {
        if (!pendingCloses.Remove(id, out var pending)) return Report(Result.Failure("Nothing to undo"));
        pending.Timer.Dispose();
        var back = tasks.UndoClose(id, pending.Was, pending.Question);
        if (back.IsSuccess && pending.Selected) selection.Select(id);
        // SetStatus pulses Changed, which redraws; a refusal (the task is gone) still needs the line taken away.
        if (back.IsFailure) Refresh();
        return Report(back);
    }

    public Result Reopen(Guid id) => Report(tasks.Reopen(id));

    public Result SetStatus(Guid id, TaskStatus status) => Report(tasks.SetStatus(id, status));

    public Result Rename(Guid id, string title) => Report(tasks.Rename(id, title));

    /// <summary>
    /// Edit in the window (Ruling 58): the title and the description together. The title first: a refused one (empty) stops
    /// there and the description is left alone, so a failed save changes nothing. A title that is the same is not renamed
    /// again, and a description that is the same saves nothing (TaskService.Describe).
    /// </summary>
    public Result Edit(Guid id, string title, string description) =>
        Report(tasks.Board.Tasks.TryFirst(t => t.Id == id).Match(
            task => (title.Trim() == task.Title ? Result.Success() : tasks.Rename(id, title)).Bind(() => tasks.Describe(id, description)),
            () => Result.Failure($"No task with id {id}")));

    /// <summary>One of HIS comments rewritten (Ruling 58); a session's is refused by the board.</summary>
    public Result EditComment(Guid comment, string markdown) => Report(tasks.EditComment(comment, markdown));

    public Result MoveTo(Guid id, Maybe<Guid> workspace) => Report(tasks.MoveTo(id, workspace));

    /// <summary>
    /// The "Assign to" menu (Ruling 57): hands the task to Petre or to Claude. Only here and in `task assign`, never on
    /// the bar. Nothing else about the task changes, so its line keeps its place.
    /// </summary>
    public Result Assign(Guid id, Assignee who) => Report(tasks.Assign(id, who));

    /// <summary>A toggle on a line: the flag on or off; the task goes to the top of the quadrant it lands in.</summary>
    public Result SetFlag(Guid id, TaskFlag flag, bool on) => Report(tasks.SetFlag(id, flag, on));

    /// <summary>A drop: the task goes to that place in that quadrant, taking its flags (TaskBoard.Place).</summary>
    public Result Place(Guid id, Quadrant quadrant, TaskPlace place) => Report(tasks.Place(id, quadrant, place));

    /// <summary>
    /// The "Goal" menu: links the task to an open goal, or (none) unlinks it, through GoalService.LinkTask. Flags, rank
    /// and status stay as they are, so the line does not move.
    /// </summary>
    public Result SetGoal(Guid id, Maybe<Guid> goal) =>
        Report(goals is { } service ? service.LinkTask(id, goal) : Result.Failure("Goals are not available"));

    public Result Delete(Guid id)
    {
        expanded.Remove(id);
        return Report(tasks.Delete(id));
    }

    /// <summary>
    /// One of Petre's notes, in the backlog, where the add box's chip says, with the flags its toggles say (Ruling 37):
    /// it lands at the top of that quadrant.
    /// </summary>
    public Result AddNote(string title, Maybe<Guid> workspace, Quadrant quadrant = Quadrant.IV) =>
        Report(tasks.AddNote(title, workspace, quadrant).Bind(_ => Result.Success()));

    /// <summary>A comment from Petre (no session), shown as "you". Its thread stays open to show it.</summary>
    public Result Comment(Guid id, string markdown)
    {
        expanded.Add(id);
        return Report(tasks.Comment(id, Maybe<string>.None, markdown));
    }

    public void Dispose()
    {
        subscriptions.Dispose();
        pendingCloses.Values.ToList().ForEach(p => p.Timer.Dispose());
        pendingCloses.Clear();
    }

    /// <summary>
    /// The app is going away: an open window's place is remembered (it opens there after the restart), and the window
    /// is closed for good.
    /// </summary>
    public void Shutdown()
    {
        if (window.IsOpen) manager.SaveTasksWindow(window.CurrentPlace());
        Dispose();
        window.CloseForGood();
    }

    // --- the view ----------------------------------------------------------------------------------------------------

    TasksView View()
    {
        ForgetReopened();
        // A task closed or deleted is not selected any more (ruling 21).
        selection.KeepOnly(tasks.Board);
        // The goal filter narrows the board before anything is grouped or counted (see the header), so the List, the
        // Matrix and the chips' counts all follow it. Ruling G1: no goals, no goal UI and no narrowing.
        var goalBoard = goals?.Board ?? GoalBoard.Empty;
        var showsGoals = goals is not null && goalBoard.Goals.Count > 0;
        if (!showsGoals || goalFilter.Goal.Map(id => goalBoard.Get(id).HasNoValue).GetValueOrDefault(false))
            goalFilter = TaskGoalFilter.All;
        var board = goalFilter.Narrow(WithPendingCloses(tasks.Board), goalBoard);
        var groups = TaskViews.Grouped(board, filter, where, assigneeFilter)
            .Select(g => new TaskGroupView(g.Kind, TaskViews.GroupName(g.Kind), g.Tasks.Select(Row).ToArray()))
            .ToArray();
        var filters = FilterOrder.Select(f => (Filter: f, Count: TaskViews.Count(board, f, where, assigneeFilter)))
            .Select(x => new TaskFilterChip(x.Filter, $"{FilterName(x.Filter)} {x.Count}", x.Filter == filter,
                x.Filter == TaskFilter.WaitingOnYou && x.Count > 0))
            .ToArray();
        // Every workspace menu of the window (move, the workspace filter, the add box's chip) is the task list's one
        // workspace menu: chips in their colours, in the bar's order, grouped as on the bar, none first.
        var places = WorkspaceChipMenu.Entries(manager.State, withNone: true);
        var addTarget = where.Any ? PlaceOf(manager.CurrentWorkspaceId) : PlaceOf(where.Workspace);
        // The Matrix's four cells (Ruling 28), drawn instead of the groups while the switch says so (AppState.TasksMatrix).
        var cells = TaskViews.Matrix(board, filter, where, assigneeFilter)
            .Select(c => new TaskCellView(c.Quadrant, TaskViews.GroupName(TaskViews.KindOf(c.Quadrant)), c.Tasks.Select(Row).ToArray()))
            .ToArray();
        var (menuGoals, filterGoals) = showsGoals ? GoalChoices(goalBoard) : NoGoalChoices;
        return new TasksView(filters, WorkspaceFilterLabel(), where, places, groups, EmptyText(filter), addTarget)
        {
            Matrix = manager.State.TasksMatrix,
            Cells = cells,
            ShowsGoals = showsGoals,
            Goals = menuGoals,
            GoalFilterChoices = filterGoals,
            GoalFilter = goalFilter,
            GoalFilterLabel = showsGoals ? GoalFilterLabel(goalBoard) : "",
            AssigneeFilter = assigneeFilter,
            AssigneeFilterLabel = AssigneeFilterLabel(),
        };
    }

    // STALE AT MIDNIGHT (review M7, known): "this week" and "this month" are read when a view is made, and nothing makes
    // a view when the day changes, so a window left open over midnight into a new week offers last week's goals until
    // the next redraw (any task or goal change, a filter click, reopening the window). Pull request 2 redraws on a day
    // change, with the planner's review.
    //
    // The goals the window offers, both from ONE read of today (review M2): the "Goal" menu's (this month's then this
    // week's OPEN goals, the ones a task can be linked to) and the goal filter's (those two periods' goals of every
    // status, since a done goal's tasks are still worth finding, plus the goal in force when it is of another period, so
    // the filter in force is always on its own menu).
    (IReadOnlyList<GoalChoice> Menu, IReadOnlyList<GoalChoice> Filter) GoalChoices(GoalBoard board)
    {
        var today = goals!.Today;
        var current = board.In(GoalPeriod.MonthOf(today)).Concat(board.In(GoalPeriod.WeekOf(today))).ToArray();
        var inForce = goalFilter.Goal.Bind(board.Get).Where(g => current.All(c => c.Id != g.Id));
        return (current.Where(g => g.IsOpen).Select(ChoiceOf).ToArray(),
            current.Concat(inForce.HasValue ? [inForce.Value] : Array.Empty<Goal>()).Select(ChoiceOf).ToArray());
    }

    static readonly (IReadOnlyList<GoalChoice> Menu, IReadOnlyList<GoalChoice> Filter) NoGoalChoices = ([], []);

    static GoalChoice ChoiceOf(Goal goal) => new(goal.Id, goal.Title, goal.Kind == GoalKind.Month ? "month" : "week");

    string GoalFilterLabel(GoalBoard board) =>
        goalFilter.Any ? "Goal: all"
        : goalFilter.Goal.Bind(board.Get).Map(g => $"Goal: {g.Title}").GetValueOrDefault("Goal: none");

    // A task closed here shows under the status it had while its undo window lasts, so its line keeps its place.
    // Closing moves neither Raised nor anything else the groups sort by, so the line is drawn exactly where it was.
    TaskBoard WithPendingCloses(TaskBoard board) =>
        pendingCloses.Count == 0
            ? board
            : new TaskBoard(board.Tasks.Select(t => t.Status == TaskStatus.Closed && pendingCloses.TryGetValue(t.Id, out var p)
                ? t with { Status = p.Was }
                : t).ToArray());

    // A line closed here whose task is open again (reopened from the pipe, or from the bar) is not pending any more.
    void ForgetReopened() =>
        pendingCloses.Where(p => tasks.Board.Tasks.All(t => t.Id != p.Key || t.Status != TaskStatus.Closed)).ToList()
            .ForEach(p =>
            {
                p.Value.Timer.Dispose();
                pendingCloses.Remove(p.Key);
            });

    // The undo window is over: the line leaves.
    void Forget(Guid id)
    {
        if (!pendingCloses.Remove(id, out var pending)) return;
        pending.Timer.Dispose();
        Refresh();
    }

    TaskRowView Row(TaskItem task) => new(
        task.Id,
        task.Title,
        FloatingBar.DetailOf(task),
        task.Status,
        task.IsClaudes,
        SessionsText(task),
        task.Sessions.Count,
        task.Sessions.Count(s => s.IsLive),
        PlaceOf(task.WorkspaceId),
        task.Comments.Count,
        expanded.Contains(task.Id),
        pendingCloses.ContainsKey(task.Id),
        expanded.Contains(task.Id) ? task.Comments.Select(CommentView).ToArray() : [])
    {
        Created = task.Created,
        Assignee = task.Assignee,
        Description = task.Description,
        Important = task.Important,
        Urgent = task.Urgent,
        // Null for no goal and for a link to a goal that no longer exists, which reads as no goal everywhere.
        Goal = goals?.GoalOf(task).Map(ChoiceOf).GetValueOrDefault(),
    };

    // The mockup's sessions column: how many, and whether any is still live. Empty for a note.
    internal static string SessionsText(TaskItem task)
    {
        var (count, live) = (task.Sessions.Count, task.Sessions.Count(s => s.IsLive));
        var noun = count == 1 ? "1 session" : $"{count} sessions";
        return count == 0 ? ""
            : live == count ? noun
            : live == 0 ? $"{noun}, ended"
            : $"{noun}, {live} live";
    }

    TaskCommentView CommentView(TaskComment comment)
    {
        var at = comment.At.ToLocalTime();
        var today = now().ToLocalTime().Date == at.Date;
        return new TaskCommentView(comment.Id, comment.SessionId.HasValue ? "Claude" : "you", comment.SessionId.HasValue,
            today ? at.ToString("HH:mm") : at.ToString("d MMM HH:mm"), comment.Markdown);
    }

    // A workspace as a chip: its name and lane colour, or "none". A workspace that no longer exists reads as none, the
    // way TaskService.Prune will soon make it.
    TaskPlaceChoice PlaceOf(Maybe<Guid> workspace) =>
        workspace.Bind(id => manager.State.Workspaces.TryFirst(w => w.Id == id))
            .Map(w => new TaskPlaceChoice(w.Id, w.Name, colourOf(w.Id)))
            .GetValueOrDefault(new TaskPlaceChoice(Maybe<Guid>.None, "none", null));

    // "Assigned: all", "Assigned: me" or "Assigned: Claude" (Ruling 57).
    string AssigneeFilterLabel() =>
        assigneeFilter.Kind.Match(kind => kind == AssigneeKind.Me ? "Assigned: me" : "Assigned: Claude", () => "Assigned: all");

    string WorkspaceFilterLabel() =>
        where.Any ? "Workspace: all" : $"Workspace: {PlaceOf(where.Workspace).Name}";

    static string FilterName(TaskFilter shown) => shown switch
    {
        TaskFilter.Open => "Open",
        TaskFilter.WaitingOnYou => TaskViews.StatusName(TaskStatus.WaitingOnYou),
        TaskFilter.Done => TaskViews.StatusName(TaskStatus.Done),
        TaskFilter.InProgress => TaskViews.StatusName(TaskStatus.InProgress),
        TaskFilter.Backlog => TaskViews.StatusName(TaskStatus.Backlog),
        _ => TaskViews.StatusName(TaskStatus.Closed),
    };

    static string EmptyText(TaskFilter shown) => shown switch
    {
        TaskFilter.Open => "Nothing open. \"+ Add\" writes a note.",
        TaskFilter.WaitingOnYou => "Nothing is waiting on you.",
        TaskFilter.Done => "Nothing done and waiting to be closed.",
        TaskFilter.InProgress => "Nothing in progress.",
        TaskFilter.Backlog => "Nothing in the backlog.",
        _ => "Nothing closed.",
    };

    // The problem line follows the last action: its refusal, or nothing once one works.
    Result Report(Result result)
    {
        window.ShowProblem(result.IsFailure ? result.Error : null);
        return result;
    }
}

// What the window draws, resolved here so the window never looks anything up.

/// <summary>A filter chip: which filter, its words with the count ("Open 9"), whether it is on, and whether it asks
/// something of Petre (Waiting on you with anything in it, drawn orange).</summary>
internal sealed record TaskFilterChip(TaskFilter Filter, string Label, bool On, bool Asks);

/// <summary>A workspace as a chip or a menu item: its id (none for "none"), its name and its lane colour.</summary>
internal sealed record TaskPlaceChoice(Maybe<Guid> Workspace, string Name, Brush? Colour);

/// <summary>One comment: "Claude" or "you", its time as shown, and its markdown.</summary>
internal sealed record TaskCommentView(Guid Id, string Author, bool ByClaude, string When, string Markdown);

/// <summary>
/// One line: the task's title and detail (the question while it waits, else its status line, plus "no live session"),
/// its status, whether it is Claude's, its sessions in words with how many there are and how many are live, its
/// workspace, its comment count, whether its thread is open (and the thread when it is), and whether it was just
/// closed and waits out its undo window.
/// </summary>
internal sealed record TaskRowView(Guid Id, string Title, string Detail, TaskStatus Status, bool Claudes, string Sessions, int SessionCount, int LiveCount,
    TaskPlaceChoice Place, int CommentCount, bool Expanded, bool Closing, IReadOnlyList<TaskCommentView> Comments)
{
    /// <summary>When the task appeared, for its age (TaskAge), which the window rewrites in place once a minute.</summary>
    public DateTimeOffset Created { get; init; }

    /// <summary>Who the task is on (Ruling 57): the assignee column and the "Assign to" menu's tick.</summary>
    public Assignee Assignee { get; init; } = Assignee.Me;

    /// <summary>Petre's own markdown under the title (Ruling 58), "" for none.</summary>
    public string Description { get; init; } = "";

    /// <summary>The task's two flags (Task 12), drawn as the line's toggles.</summary>
    public bool Important { get; init; }

    public bool Urgent { get; init; }

    public Quadrant Quadrant => QuadrantOf.Flags(Important, Urgent);

    /// <summary>
    /// The goal the task is linked to (goals plan, Task 6), for the "Goal" menu: its check mark, and the goal itself when
    /// the menu would not otherwise list it (review M6). Null for no goal. Not drawn on the line, so not in the window's
    /// redraw signature.
    /// </summary>
    public GoalChoice? Goal { get; init; }
}

/// <summary>
/// A goal as the "Goal" menu and the goal filter list it: its id (none for "None"), its title and its period's kind in
/// words ("week" or "month"), shown dim beside the title. Goals have no colour, so the items are plain text.
/// </summary>
internal sealed record GoalChoice(Maybe<Guid> Goal, string Title, string Period);

/// <summary>One cell of the Matrix: its quadrant, its caption, its lines (the waiting ones first).</summary>
internal sealed record TaskCellView(Quadrant Quadrant, string Header, IReadOnlyList<TaskRowView> Rows);

/// <summary>One group under its caption: which group (Waiting on you, a quadrant, Closed), the caption, its lines.</summary>
internal sealed record TaskGroupView(TaskGroupKind Kind, string Header, IReadOnlyList<TaskRowView> Rows);

/// <summary>
/// Everything the window draws: the filter chips, the workspace filter (its label and its value), the workspaces for
/// its menus (WorkspaceChipMenu's entries), the groups, what an empty view says, and where a note added now would go.
/// </summary>
internal sealed record TasksView(IReadOnlyList<TaskFilterChip> Filters, string WorkspaceFilterLabel, TaskWorkspaceFilter Where,
    IReadOnlyList<WorkspaceChipMenu.Entry> Places,
    IReadOnlyList<TaskGroupView> Groups, string EmptyText, TaskPlaceChoice AddTarget)
{
    /// <summary>Whether the window shows the Matrix (Task 12) rather than the List; its four cells.</summary>
    public bool Matrix { get; init; }

    public IReadOnlyList<TaskCellView> Cells { get; init; } = [];

    /// <summary>Ruling G1: whether the "Goal" menu and the goal filter show at all (a goal service and at least one goal).</summary>
    public bool ShowsGoals { get; init; }

    /// <summary>The "Goal" menu's goals: this month's then this week's open goals ("None" is the window's own first item).</summary>
    public IReadOnlyList<GoalChoice> Goals { get; init; } = [];

    /// <summary>The goal filter's goals, after its "All goals" and "No goal" (see TasksBoard.GoalChoices).</summary>
    public IReadOnlyList<GoalChoice> GoalFilterChoices { get; init; } = [];

    /// <summary>The goal filter in force, for its menu's check mark.</summary>
    public TaskGoalFilter GoalFilter { get; init; } = TaskGoalFilter.All;

    /// <summary>"Goal: all", "Goal: none" or "Goal: &lt;title&gt;"; "" while ShowsGoals is false.</summary>
    public string GoalFilterLabel { get; init; } = "";

    /// <summary>The "Assigned" filter in force (Ruling 57), and its toolbar label.</summary>
    public TaskAssigneeFilter AssigneeFilter { get; init; } = TaskAssigneeFilter.All;

    public string AssigneeFilterLabel { get; init; } = "Assigned: all";
}

/// <summary>
/// Everything the window can ask for, handed over by the controller once. The window has no events of its own: a click
/// calls one of these. Rename, Comment and AddNote answer whether it worked, so the window knows whether to clear its
/// box; every refusal is shown by the controller on the problem line. Remember is handed the window's place whenever it
/// closes or settles after a move, to keep in AppState.TasksWindow.
/// </summary>
internal sealed record TasksWindowActions(
    Action<Guid> Go,
    Action<Guid> Pick,
    Action<Guid> ToggleThread,
    Action<Guid> Close,
    Action<Guid> Undo,
    Action<Guid> Reopen,
    Action<Guid, TaskStatus> SetStatus,
    Func<Guid, string, bool> Rename,
    Func<Guid, string, string, bool> Edit,
    Func<Guid, string, bool> EditComment,
    Action<Guid, Maybe<Guid>> MoveTo,
    Action<Guid> Delete,
    Func<Guid, string, bool> Comment,
    Func<string, Maybe<Guid>, Quadrant, bool> AddNote,
    Action<Guid, TaskFlag, bool> SetFlag,
    Action<Guid, Quadrant, TaskPlace> Place,
    Action<bool> ShowMatrix,
    Action<TaskFilter> Filter,
    Action<TaskWorkspaceFilter> FilterWorkspace,
    Action<TasksWindowState> Remember,
    Action<Guid, Maybe<Guid>> SetGoal,
    Action<TaskGoalFilter> FilterGoal,
    Action<Guid, Assignee> Assign,
    Action<TaskAssigneeFilter> FilterAssignee);
