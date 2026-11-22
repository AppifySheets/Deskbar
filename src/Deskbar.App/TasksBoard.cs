using System.Reactive.Disposables;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using CSharpFunctionalExtensions;
using Deskbar.Core;
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
//   Undo          the task comes back with the status it had (SetStatus, which also takes a closed task back).
//   Reopen        a closed task under the Closed filter: TaskService.Reopen.
//   SetStatus     Backlog, In progress, Waiting on you or Done by hand. Closed is refused there (Close is the way).
//   Rename, MoveTo, Delete, AddNote, Comment (as Petre, no session; shown as "you").
//
// WHEN IT REDRAWS. On every TaskService.Changed and WorkspaceManager.StateChanged while the window is open (a workspace
// renamed or recoloured changes the chips), and after each of its own actions. The window compares what it would
// draw with what it drew and redraws nothing when they match, and it waits while one of its menus is open, a rename
// is under way or a press is in flight (TasksWindow.MustWait): the bar's #77 lesson about redrawing under an open
// menu, applied to this window's own refresh.
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
    readonly IDisposable subscriptions;

    // The lines closed in this window and still in their undo window: the status each had, and its timer.
    readonly Dictionary<Guid, PendingClose> pendingCloses = [];
    readonly HashSet<Guid> expanded = [];
    TaskFilter filter = TaskFilter.Open;
    TaskWorkspaceFilter where = TaskWorkspaceFilter.All;

    sealed record PendingClose(TaskStatus Was, IDisposable Timer);

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
    public TasksBoard(TasksWindow window, TaskService tasks, WorkspaceManager manager, Func<TaskItem, Result> goTo,
        Func<Guid, Brush?>? colourOf = null, Func<nint, bool>? bringForward = null, Func<TimeSpan, Action, IDisposable>? undoTimer = null,
        Func<DateTimeOffset>? now = null, TaskSelection? selection = null)
    {
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
            ToggleThread: ToggleThread,
            Close: id => Close(id),
            Undo: id => Undo(id),
            Reopen: id => Reopen(id),
            SetStatus: (id, status) => SetStatus(id, status),
            Rename: (id, title) => Rename(id, title).IsSuccess,
            MoveTo: (id, workspace) => MoveTo(id, workspace),
            Delete: id => Delete(id),
            Comment: (id, markdown) => Comment(id, markdown).IsSuccess,
            AddNote: (title, workspace) => AddNote(title, workspace).IsSuccess,
            SetFlag: (id, flag, on) => SetFlag(id, flag, on),
            Place: (id, quadrant, place) => Place(id, quadrant, place),
            Filter: Filter,
            FilterWorkspace: FilterWorkspace,
            Remember: place => manager.SaveTasksWindow(place));

        subscriptions = new CompositeDisposable(
            tasks.Changed.Subscribe(_ => Refresh()),
            manager.StateChanged.Subscribe(_ => Refresh()),
            this.selection.Changed.Subscribe(_ => window.PaintSelection(this.selection.Current)));
    }

    /// <summary>
    /// The window's handle, so the App can hand it to WindowMonitor.Ignore before it ever shows: the monitor lists the
    /// app's own windows, and the bar must never show the tasks window as a window of its own.
    /// </summary>
    public nint EnsureHandle() => new WindowInteropHelper(window).EnsureHandle();

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
    void Arrived()
    {
        var activated = bringForward is { } seam ? seam(EnsureHandle()) : ActivatedHere();
        if (activated) window.MaximizeIfRemembered();
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
    internal static bool Arrive(nint hwnd, Func<Result> move, Func<bool> onCurrentDesktop, Action activate, Action<string> trace)
    {
        var moved = move();
        if (moved.IsSuccess)
        {
            activate();
            return true;
        }
        trace($"tasks window 0x{hwnd:X}: not moved to the desktop in front: {moved.Error}");
        if (onCurrentDesktop())
        {
            activate();
            return true;
        }
        trace($"tasks window 0x{hwnd:X}: left on its own desktop rather than taking you there");
        return false;
    }

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

    public void ToggleThread(Guid id)
    {
        if (!expanded.Remove(id)) expanded.Add(id);
        Refresh();
    }

    // --- actions ---------------------------------------------------------------------------------------------------

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
        pendingCloses[id] = new PendingClose(task.Status, undoTimer(UndoWindow, () => Forget(id)));
        return Report(tasks.Close(id).TapError(_ => Forget(id)));
    }

    /// <summary>
    /// Undo on a line just closed, by the bar's rule (FloatingBar.UndoClose), so the two Undos agree (fix round 1): a
    /// task that was Done comes back Done; any other comes back In progress while a session is live on it, else
    /// Backlog. Both through TaskBoard.UndoClose, which keeps the task's place in its quadrant (Task 12): the line
    /// stayed on screen, struck through, so it must not jump to the top as a reopened task does.
    /// </summary>
    public Result Undo(Guid id)
    {
        if (!pendingCloses.Remove(id, out var pending)) return Report(Result.Failure("Nothing to undo"));
        pending.Timer.Dispose();
        var back = tasks.UndoClose(id, pending.Was);
        // SetStatus pulses Changed, which redraws; a refusal (the task is gone) still needs the line taken away.
        if (back.IsFailure) Refresh();
        return Report(back);
    }

    public Result Reopen(Guid id) => Report(tasks.Reopen(id));

    public Result SetStatus(Guid id, TaskStatus status) => Report(tasks.SetStatus(id, status));

    public Result Rename(Guid id, string title) => Report(tasks.Rename(id, title));

    public Result MoveTo(Guid id, Maybe<Guid> workspace) => Report(tasks.MoveTo(id, workspace));

    /// <summary>A toggle on a line: the flag on or off; the task goes to the top of the quadrant it lands in.</summary>
    public Result SetFlag(Guid id, TaskFlag flag, bool on) => Report(tasks.SetFlag(id, flag, on));

    /// <summary>A drop: the task goes to that place in that quadrant, taking its flags (TaskBoard.Place).</summary>
    public Result Place(Guid id, Quadrant quadrant, TaskPlace place) => Report(tasks.Place(id, quadrant, place));

    public Result Delete(Guid id)
    {
        expanded.Remove(id);
        return Report(tasks.Delete(id));
    }

    /// <summary>One of Petre's notes, in the backlog, where the add box's chip says.</summary>
    public Result AddNote(string title, Maybe<Guid> workspace) => Report(tasks.AddNote(title, workspace).Bind(_ => Result.Success()));

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
        var board = WithPendingCloses(tasks.Board);
        var groups = TaskViews.Grouped(board, filter, where)
            .Select(g => new TaskGroupView(g.Kind, TaskViews.GroupName(g.Kind), g.Tasks.Select(Row).ToArray()))
            .ToArray();
        var filters = FilterOrder.Select(f => (Filter: f, Count: TaskViews.Count(board, f, where)))
            .Select(x => new TaskFilterChip(x.Filter, $"{FilterName(x.Filter)} {x.Count}", x.Filter == filter,
                x.Filter == TaskFilter.WaitingOnYou && x.Count > 0))
            .ToArray();
        // Every workspace menu of the window (move, the workspace filter, the add box's chip) is the task list's one
        // workspace menu: chips in their colours, in the bar's order, grouped as on the bar, none first.
        var places = WorkspaceChipMenu.Entries(manager.State, withNone: true);
        var addTarget = where.Any ? PlaceOf(manager.CurrentWorkspaceId) : PlaceOf(where.Workspace);
        return new TasksView(filters, WorkspaceFilterLabel(), where, places, groups, EmptyText(filter), addTarget);
    }

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
        Important = task.Important,
        Urgent = task.Urgent,
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

    /// <summary>The task's two flags (Task 12), drawn as the line's toggles.</summary>
    public bool Important { get; init; }

    public bool Urgent { get; init; }

    public Quadrant Quadrant => QuadrantOf.Flags(Important, Urgent);
}

/// <summary>One group under its caption: which group (Waiting on you, a quadrant, Closed), the caption, its lines.</summary>
internal sealed record TaskGroupView(TaskGroupKind Kind, string Header, IReadOnlyList<TaskRowView> Rows);

/// <summary>
/// Everything the window draws: the filter chips, the workspace filter (its label and its value), the workspaces for
/// its menus (WorkspaceChipMenu's entries), the groups, what an empty view says, and where a note added now would go.
/// </summary>
internal sealed record TasksView(IReadOnlyList<TaskFilterChip> Filters, string WorkspaceFilterLabel, TaskWorkspaceFilter Where,
    IReadOnlyList<WorkspaceChipMenu.Entry> Places,
    IReadOnlyList<TaskGroupView> Groups, string EmptyText, TaskPlaceChoice AddTarget);

/// <summary>
/// Everything the window can ask for, handed over by the controller once. The window has no events of its own: a click
/// calls one of these. Rename, Comment and AddNote answer whether it worked, so the window knows whether to clear its
/// box; every refusal is shown by the controller on the problem line. Remember is handed the window's place whenever it
/// closes or settles after a move, to keep in AppState.TasksWindow.
/// </summary>
internal sealed record TasksWindowActions(
    Action<Guid> Go,
    Action<Guid> ToggleThread,
    Action<Guid> Close,
    Action<Guid> Undo,
    Action<Guid> Reopen,
    Action<Guid, TaskStatus> SetStatus,
    Func<Guid, string, bool> Rename,
    Action<Guid, Maybe<Guid>> MoveTo,
    Action<Guid> Delete,
    Func<Guid, string, bool> Comment,
    Func<string, Maybe<Guid>, bool> AddNote,
    Action<Guid, TaskFlag, bool> SetFlag,
    Action<Guid, Quadrant, TaskPlace> Place,
    Action<TaskFilter> Filter,
    Action<TaskWorkspaceFilter> FilterWorkspace,
    Action<TasksWindowState> Remember);
