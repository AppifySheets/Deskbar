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
// THE WINDOW IS THE DOCKED TASKS PANEL (docs/superpowers/specs/2026-11-23-tasks-panel-design.md, "The panel window" and
// "Opening and closing"; plan 2026-11-23-tasks-panel, Task 6). It used to float, a page with a title bar and a place of
// its own; now it is a full-height panel just inside the docked bar. TasksPanel places it and reserves its strip; this
// opens and closes it (Show, Hide, Toggle), keeps AppState.TasksPanel (open or not, and the width) in step with every
// open, close and grip drag, and hands the window the edge it docks on, for its grip. The bar's "All tasks" line
// toggles it, `tasks-window show|hide` shows or hides it, and the × and Alt+F4 close it. AppState.TasksWindow, the
// floating window's place, is never read or written any more (old files still load).
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
//   Go            a line's click: through `goTo`. The panel stays open through the switch: it is pinned to every
//                 desktop, so it is on the one switched to as well. The App points goTo at the bar's own GoToTask path: the task's workspace, then its
//                 Claude session's window (ClaudeTaskActions.Go, Task 9; TaskViews.SessionToGoTo names the session). A
//                 note on no workspace has nowhere to go, so its click opens its thread instead.
//   Close         at once through TaskService.Close (only Petre closes, and this is his click), with the bar's undo
//                 window: the line stays in its place for six seconds, struck through, with Undo (Ruling 10).
//   Undo          exactly undo (Ruling 31): the status it had, its question if it waited on Petre, its place and the
//                 selection (TaskBoard.UndoClose).
//   Reopen        a closed task under the Closed filter: TaskService.Reopen.
//   SetStatus     Backlog, In progress, Waiting on you or Done by hand. Closed is refused there (Close is the way).
//   Rename, MoveTo, Delete, AddNote, Comment (as Petre, no session; shown as "you").
//   StartClaude   "Start Claude on this", from the line's menu and the thread's button: through `startClaude`, the App's
//                 ClaudeTaskActions.Offer (asks first on a task with a running session, then the folder and the start).
//   OpenSession   the thread's Open on one of its sessions: through `openSession` (ClaudeTaskActions.OpenSession), what
//                 going to that session does. Task 9 of docs/superpowers/specs/2026-11-23-tasks-open-claude-sessions-design.md.
//
// THE THREAD'S SESSIONS (Task 9, spec "Task history"). An open thread lists the task's sessions, newest first, each with
// when it joined and whether it is working, idle, ended or expired (`sessionsOf`, the App's ClaudeTaskActions.SessionsOf
// over the latest `claude agents --json`), with Open on each but an expired one. The button that starts a new one is in
// the row at the thread's top, and only where Open Claude is not (TasksWindow.ThreadActions, task 5e02e99f). Only an
// open thread's list is made, since an ended session's state needs its transcript looked up on disk;
// `sessionsChanged` (each new list) redraws, and the window redraws only when a state it shows changed.
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
//
// SEARCH AND CLAUDE WORKING (docs/superpowers/specs/2026-11-23-tasks-panel-design.md, "The new filters"; plan
// 2026-11-23-tasks-panel, Task 7). The panel's search box (TaskSearch: case-insensitive, every typed word must match, in
// the title, the status line or the question) and its "Claude working N" chip (ClaudeWorking: only tasks with a live
// session) narrow the board right after the goal filter, so, like it, BEFORE the groups, the Matrix's cells and every
// chip's count are made: what a chip says is always what it would show. The Claude working chip's own count is taken on
// the board narrowed by every OTHER filter (goal, search, and the status, workspace and assignee filters the chips
// apply), so it too says how many it would show. The search is not saved and starts over on a fresh open. Claude working
// is KEPT across opens and restarts, as the "Current workspace" checkbox is (task 93fa0b9b, 2026-11-25, Petre: "i also
// want a filter on claude working on tasks at the top, just like with current workspace"; AppState.TasksPanel.ClaudeWorking).
// Text that starts a task's id (task 6e4dc116, TaskSearch.Found) is a lookup instead: that task in whatever section it
// is in, closed included, past every filter, and in the List even when the Matrix is on.
//
// THE PANEL'S WIDTH (spec "Lines at panel width" and "The Matrix"; plan 2026-11-23-tasks-panel, Task 8). The view says
// whether lines are slim (below 710 DIP) and whether the Matrix is allowed (from 780), both from the width the panel
// docked at (TasksPanel.DockedWidth; View has why that one), and shows the Matrix only when the setting asks for it AND
// it is allowed. That width changes only when the panel docks (the grip's drag end, another screen, a scale change),
// whose pulse makes the view again, so every line redraws once then and never during a drag.
public sealed class TasksBoard : IDisposable
{
    // How long a line just closed stays, struck through with Undo: the bar's own window, so the two agree.
    static readonly TimeSpan UndoWindow = FloatingBar.UndoWindow;

    // The chips' order, the open view's group order with Open first and Closed last.
    static readonly IReadOnlyList<TaskFilter> FilterOrder =
        [TaskFilter.Open, TaskFilter.WaitingOnYou, TaskFilter.Done, TaskFilter.InProgress, TaskFilter.Backlog, TaskFilter.Closed, TaskFilter.Snoozed];

    readonly TasksWindow window;
    readonly TasksPanel panel;
    readonly TaskService tasks;
    readonly WorkspaceManager manager;
    readonly Func<TaskItem, Result> goTo;
    readonly Func<Guid, Brush?> colourOf;
    readonly Func<nint, bool>? bringForward;
    readonly Func<TimeSpan, Action, IDisposable> undoTimer;
    readonly Func<DateTimeOffset> now;
    readonly TaskSelection selection;
    readonly GoalService? goals;
    readonly Func<TaskItem, IReadOnlyList<SessionView>> sessionsOf;
    readonly Func<TaskItem, string, Result>? openSession;
    readonly Func<TaskItem, int> workingOf;
    // "Stop Claude and close" (task 37cd7081): the App's ClaudeTaskActions.StopAndClose. None (the tests): the close alone.
    readonly Func<TaskItem, Result>? stopClaudeAndClose;
    readonly IDisposable subscriptions;

    // The lines closed in this window and still in their undo window: the status each had, and its timer.
    readonly Dictionary<Guid, PendingClose> pendingCloses = [];
    readonly HashSet<Guid> expanded = [];
    TaskFilter filter = TaskFilter.Open;
    TaskWorkspaceFilter where = TaskWorkspaceFilter.All;
    TaskGoalFilter goalFilter = TaskGoalFilter.All;
    // The toolbar's "Assigned" filter (Ruling 57); like the others it starts at "all" whenever the window opens.
    TaskAssigneeFilter assigneeFilter = TaskAssigneeFilter.All;
    // The search box's text and the "Claude working" chip (Task 7, see the header); both start over on a fresh open.
    TaskSearch search = TaskSearch.None;
    bool claudeWorking;
    // Whether the Stats view shows in place of the List or the Matrix (task 521d2b07, TasksWindow.Stats.cs). Not
    // remembered: a fresh open starts on the List or the Matrix, whichever AppState.TasksMatrix says, as before.
    bool statsShown;
    // THE REFUSAL ON A TASK'S OWN LINE (task 41f08dba; Petre: "show error on the task, not at the top of the bar"). The
    // last action's refusal, when the action was on one task (Report's `task`): that task and the words. Its line draws
    // them under its title (TaskRowView.Problem) and its window on its problem line, where the panel's problem line at the
    // top was easy to miss and did not say which line it meant. Null when the last action worked, was on no task, or the
    // refusal went to the panel's top line because the task has no line on show there (see Report).
    (Guid Task, string Text)? lineProblem;
    // Set by Shutdown, which runs once (see there).
    bool shutDown;

    // The actions handed to the panel, handed to every task window as well: a click in either is the same method here.
    readonly TasksWindowActions actions;

    // THE TASK WINDOWS (task dca4ebeb, "a separate view that will open a task in, especially for longer tasks"), one per
    // task, by task id. A task already open in one is brought forward rather than opened twice. Each window says when it
    // closes (Leaving), and leaves this map then. See OpenWindow.
    readonly Dictionary<Guid, TaskDetailWindow> taskWindows = [];

    /// <summary>
    /// Makes a task window. The tests hand one of their own; the app's is a plain TaskDetailWindow, whose pictures and
    /// links behave as the panel's do.
    /// </summary>
    internal Func<TaskDetailWindow> NewTaskWindow { get; init; } = () => new TaskDetailWindow();

    /// <summary>
    /// Brings a task window to Petre by its handle: the App moves it onto the desktop in front when it is on another one,
    /// then raises and activates it through an activator (a `deskbar.cmd task open` typed in a terminal carries no right
    /// to the foreground). None (the tests): the window activates itself where it is.
    /// </summary>
    internal Action<nint>? BringTaskWindowForward { get; init; }

    /// <summary>
    /// Opens the manager session (the bar's Manager button, ManagerActions.Open): the manager line's click (task 61e4d143).
    /// None (the tests, a copy without Claude sessions): the panel draws no manager line, as there is nothing to open.
    /// </summary>
    internal Func<Result>? OpenManager { get; init; }

    /// <summary>
    /// What finally opens a link once OpenLink has gone to the task's workspace: MarkdownBlocks.Open, the default browser
    /// for a web address and FileLink's plan for a file. The tests replace it, so a run never starts a browser.
    /// </summary>
    internal Action<Uri> OpenUri { get; init; } = MarkdownBlocks.Open;

    // What Undo gives back (Ruling 31): the status, the question of a task that waited on Petre, and the selection.
    sealed record PendingClose(TaskStatus Was, IDisposable Timer)
    {
        public Maybe<string> Question { get; init; } = Maybe<string>.None;

        public bool Selected { get; init; }
    }

    //   panel         the panel's app bar and place (TasksPanel), built on the same window by the App, or by the tests
    //                 with every seam faked; this shows and hides it and hands it the width
    //   goTo          where a line's click goes (see the header)
    //   colourOf      a workspace's lane colour, the one its row on the bar wears; none for a workspace without one
    //   bringForward  brings the window to Petre by its handle: the App raises it and then activates it (Arrive),
    //                 through an activator, because a `deskbar.cmd tasks-window show` typed in a terminal carries no
    //                 right to take the foreground. It answers whether it activated the window: not when the panel is
    //                 off the desktop in front (a failed pin), and then the keyboard is left alone too (Arrived). The
    //                 window itself never activates while this is set (see Show)
    //   undoTimer     runs `then` once after the undo window unless disposed first; a dispatcher timer by default, a
    //                 seam so the tests move time by hand
    //   now           the clock a comment's time is told against ("11:42" today, a date before)
    //   goals         the goal service, for the "Goal" menu and the goal filter (see the header); none, no goals UI
    //   sessionsOf    a task's sessions as its thread lists them (see the header); by default the hooks' word alone,
    //                 every transcript taken as there (TaskViews.SessionList with no list)
    //   startClaude   "Start Claude on this" (see the header); none, neither the menu item nor the button shows
    //   openSession   a session's Open (see the header); none, no Open buttons
    //   sessionsChanged  pulses when the sessions' states may have changed, to redraw an open thread
    //   workingOf     how many of a task's live sessions Claude is busy in now (Task 8; the App's ClaudeTaskActions
    //                 .WorkingCount over the latest `claude agents --json`); their squares wear a halo. None, 0 for all.
    //                 A new list pulses sessionsChanged, so a line redraws when only this count moved
    // Internal because TasksPanel is: only the App and the tests build a board.
    internal TasksBoard(TasksWindow window, TasksPanel panel, TaskService tasks, WorkspaceManager manager, Func<TaskItem, Result> goTo,
        Func<Guid, Brush?>? colourOf = null, Func<nint, bool>? bringForward = null, Func<TimeSpan, Action, IDisposable>? undoTimer = null,
        Func<DateTimeOffset>? now = null, TaskSelection? selection = null, GoalService? goals = null,
        Func<TaskItem, IReadOnlyList<SessionView>>? sessionsOf = null, Action<Guid>? startClaude = null,
        Func<TaskItem, string, Result>? openSession = null, IObservable<System.Reactive.Unit>? sessionsChanged = null,
        Func<TaskItem, int>? workingOf = null, Func<TaskItem, Result>? stopClaudeAndClose = null)
    {
        this.workingOf = workingOf ?? (_ => 0);
        this.stopClaudeAndClose = stopClaudeAndClose;
        this.goals = goals;
        this.sessionsOf = sessionsOf ?? (task => TaskViews.SessionList(task, Maybe<IReadOnlyCollection<ClaudeAgent>>.None, _ => true));
        this.openSession = openSession;
        this.window = window;
        this.panel = panel;
        this.tasks = tasks;
        this.manager = manager;
        this.goTo = goTo;
        this.colourOf = colourOf ?? (_ => null);
        this.bringForward = bringForward;
        this.undoTimer = undoTimer ?? FloatingBar.DispatcherUndoTimer;
        this.now = now ?? (() => DateTimeOffset.Now);
        // The task Petre picked (ruling 21), the bar's own object in the App, so a pick in either view shows in both.
        this.selection = selection ?? new TaskSelection();

        window.Actions = actions = new TasksWindowActions(
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
            SettleComment: (comment, settle) => SettleComment(comment, settle),
            MoveTo: (id, workspace) => MoveTo(id, workspace),
            Delete: id => Delete(id),
            Comment: (id, markdown) => Comment(id, markdown).IsSuccess,
            AddNote: (title, workspace, quadrant, describe) => AddNote(title, workspace, quadrant, describe: describe).IsSuccess,
            SetFlag: (id, flag, on) => SetFlag(id, flag, on),
            Place: (id, quadrant, place) => Place(id, quadrant, place),
            // List or Matrix also leaves the Stats view. SetTasksMatrix pulses only when the setting changes, so the
            // view is drawn here as well: "List" pressed on Stats while the List is already the setting must still show it.
            ShowMatrix: on =>
            {
                statsShown = false;
                Report(manager.SetTasksMatrix(on));
                Refresh();
            },
            Filter: Filter,
            FilterWorkspace: FilterWorkspace,
            ClosePanel: Hide,
            ResizePanel: ResizePanel,
            SetGoal: (id, goal) => SetGoal(id, goal),
            FilterGoal: FilterGoal,
            Assign: (id, who) => Assign(id, who),
            FilterAssignee: FilterAssignee,
            Search: Search,
            FilterClaudeWorking: FilterClaudeWorking)
        {
            StartClaude = startClaude,
            // The manager line's click (task 61e4d143): the Manager button's own click, read when it happens, since the
            // App sets OpenManager after this constructor. A refusal shows on the problem line like any other.
            OpenManager = () => Report(OpenManager?.Invoke() ?? Result.Failure("The manager is not available.")),
            StopClaudeAndClose = id => Close(id, stopClaude: true),
            OpenSession = openSession is null ? null : (id, session) => OpenSession(id, session),
            SubtaskParent = id => tasks.Board.SubtaskParentFor(id)
                .Match(p => new SubtaskParentView(p.Id, p.Title, p.WorkspaceId), () => (SubtaskParentView?)null),
            AddSubtaskHere = (lineId, title, describe, inParentThread) => AddSubtaskInThread(lineId, title, describe, inParentThread).IsSuccess,
            // A subtask in its parent's thread that has a line of its own elsewhere (SubtaskView.HasLine; one without opens
            // in place, task 0eb943d8) and the task window's "Show in the tasks panel": both ask for that very task, so it
            // is let past a filter that hides it (task b0526e18), as "Subtask of" lets a parent through.
            ShowTask = ShowTaskPastFilters,
            SetParent = (id, parent) => Report(tasks.SetParent(id, parent), id).IsSuccess,
            NestTargets = id => tasks.Board.ParentCandidates(id).Select(t => t.Id).ToHashSet(),
            Snooze = (id, choice) => Snooze(id, choice),
            Wake = id => Wake(id),
            ShowSubtasks = ShowSubtasks,
            // "Open in window" (task dca4ebeb): the task in a window of its own (OpenWindow).
            OpenWindow = id => OpenWindow(id),
            ShowStats = ShowStats,
            OpenFromStats = OpenFromStats,
            // "Subtask of" in a subtask's thread and "Open parent" in its line's menu (task 54b39c4c).
            OpenParent = OpenParent,
            // A link in a task's text opens in the task's workspace (task 26f15d70, OpenLink).
            OpenLink = OpenLink,
            ResizeColumns = ResizeColumns,
            // Task links: the picker's candidates, and what a pick, a cross or the menu does.
            LinkCandidates = (id, typed, duplicate) => tasks.Board.LinkCandidates(id, typed, duplicate).Take(LinkChoicesShown)
                .Select(t => LinkView(t, duplicate ? TaskLinkKind.DuplicateOf : TaskLinkKind.Related)).ToArray(),
            MarkDuplicate = (id, of) => Report(tasks.MarkDuplicate(id, of), id).IsSuccess,
            Relate = (id, other) => Report(tasks.Relate(id, other), id).IsSuccess,
            Unrelate = (id, other) => Report(tasks.Unrelate(id, other), id),
            // "Mark deployed" on a Done line (task 8470d176, phase 2): Petre saw it running, so the ring fills.
            MarkDeployed = id => Report(tasks.MarkDeployed(id, TaskService.MarkedByHand), id),
        };

        // Taken before the subscriptions below exist, so the first StateChanged pulse is compared with this state
        // (RefreshIfShownStateChanged), as the StartWith(...).Skip(1) it replaces did.
        shownState = ShownStateKey();
        subscriptions = new CompositeDisposable(
            tasks.Changed.Subscribe(_ => Refresh()),
            // A goal added, renamed, done or deleted (from the pipe, later the planner) changes the goal filter's label
            // and choices and, with G1, whether goals show at all.
            goals?.Changed.Subscribe(_ => Refresh()) ?? Disposable.Empty,
            // A new list of Claude's sessions may change what an open thread says of them (working, idle, ended).
            sessionsChanged?.Subscribe(_ => Refresh()) ?? Disposable.Empty,
            // Only a change the window shows (whole-PR review, minor 4): the workspaces (names, colours, groups, order),
            // which workspace is in front (where a new note goes) and the List / Matrix switch. Every other state pulse,
            // a window moving or a stats tick, used to rebuild the whole view for nothing; tasks have their own pulse.
            //
            // ON THE UI THREAD, ALWAYS (task 07d4b83d, "current workspace takes a long time to refresh the tasks when i
            // change workspace"). A desktop switch pulses StateChanged from the thread Windows raises its desktop change
            // on, an MTA one, and this used to redraw right there: WPF threw ("The calling thread must be STA"), the
            // manager's OnDesktopEvent caught and traced it (29 times in one day's trace), and the comparison had already
            // taken the new workspace as seen, so the next pulse on the UI thread matched and drew nothing. The panel
            // stayed on the old workspace's tasks until an unrelated change (a task edited, a session list arriving)
            // happened to redraw it. So the pulse is moved onto the window's dispatcher BEFORE the comparison, which
            // then only ever runs on one thread (OnUiThread, RefreshIfShownStateChanged).
            manager.StateChanged.Subscribe(_ => OnUiThread(RefreshIfShownStateChanged)),
            this.selection.Changed.Subscribe(_ => window.PaintSelection(this.selection.Current)),
            // The grip goes on the edge facing the windows, which follows the edge the panel docks on (TasksPanel.Edge
            // says the current one at once, then each change).
            panel.Edge.Subscribe(window.SetInnerEdge),
            // The width the panel docked at (Task 8 fix round 1): a dock at a new width, on another screen, at another
            // scale or from the grip, may cross 710 or 780, so the view is made again (View reads it). The window's
            // signature holds only the two flags, so a new width that crosses neither draws nothing.
            panel.DockedWidth.Subscribe(width =>
            {
                dockedWidth = width;
                Refresh();
            }));
    }

    // How many tasks the link picker lists under its box (task links): enough to find one by a word or two, few enough
    // to stay inside the thread.
    const int LinkChoicesShown = 8;

    // The width the panel last docked at, DIPs; None before its first dock (View falls back to the saved width then).
    Maybe<double> dockedWidth = Maybe<double>.None;

    // The ShownStateKey the window was last drawn for (or the board was built with). Read and written on the UI thread
    // only, by RefreshIfShownStateChanged; set before the StateChanged subscription exists, so the first pulse compares
    // against the state the board started from.
    string shownState = "";

    // Set by Dispose: a pulse queued onto the dispatcher before the subscriptions went away finds the board gone and
    // draws nothing.
    bool disposed;

    // The StateChanged half of the redraw: draws when, and only when, something the window shows changed.
    void RefreshIfShownStateChanged()
    {
        if (disposed) return;
        var key = ShownStateKey();
        if (key == shownState) return;
        shownState = key;
        Refresh();
    }

    // Runs the action on the window's thread: at once when already there (every pulse but a desktop switch, and every
    // test, so a redraw stays synchronous for them), otherwise queued with InvokeAsync, which does not hold Windows'
    // desktop-change thread while the panel draws (Invoke would, and could deadlock against a UI thread waiting on COM).
    void OnUiThread(Action action)
    {
        if (window.Dispatcher.CheckAccess()) action();
        else window.Dispatcher.InvokeAsync(action);
    }

    // What of the workspace state the window shows, as text: compared, never parsed.
    string ShownStateKey() =>
        string.Join('|', manager.State.Workspaces.Select(w => $"{w}"))
        + "|" + string.Join('|', manager.State.Groups.Select(g => $"{g}"))
        + $"|{manager.CurrentWorkspaceId}|{manager.State.TasksMatrix}"
        // The manager's question (task 61e4d143): `task ask`, `task answered` and the user's reply change only
        // AppState.Manager, so without this the open panel would drop their pulse and show the line late.
        + $"|{manager.State.Manager?.Question}|{manager.State.Manager?.AskedAt}";

    /// <summary>
    /// The window's handle, created if it is not yet: what the App pins to every desktop, opts out of the window list
    /// and brings forward, and what the tests ask tiling about.
    /// </summary>
    public nint EnsureHandle() => new WindowInteropHelper(window).EnsureHandle();

    /// <summary>Whether the panel is open: the user's word, as TasksPanel keeps it (open from Show to Hide).</summary>
    public bool IsOpen => panel.IsShown;

    // The panel's saved state (AppState.TasksPanel), or the default for a file that never had one: closed, 460 DIP.
    TasksPanelState PanelState => manager.State.TasksPanel ?? TasksPanelState.Default;

    /// <summary>The bar's "All tasks" (or "+N more") line: a hidden panel shows, a shown one hides.</summary>
    public void Toggle()
    {
        if (IsOpen) Hide();
        else Show();
    }

    /// <summary>
    /// `tasks-window show`, and a line's single click on the bar (through ShowTask). A panel already open comes forward
    /// with its view as it was. A hidden one opens at the default view (open tasks, every workspace, goal and assignee,
    /// no thread open; the "Current workspace" checkbox stays ticked when it was) and docks at the width saved for it, and the saved state says it is open.
    /// </summary>
    //
    // THE WINDOW NEVER ACTIVATES ITSELF (fix round 1, Critical 1, kept for the panel). It is shown without activation and
    // then handed to Arrived, whose seam (the App's, through Arrive) raises it and then activates it.
    public void Show()
    {
        if (IsOpen)
        {
            Arrived();
            return;
        }
        OpenFresh();
        Arrived();
    }

    /// <summary>
    /// The start-up reopen of a panel left open (AppState.TasksPanel.Open, kept so by Shutdown): drawn and docked like a
    /// Show, but never brought forward or activated, so a start at sign-in takes the keyboard from nothing. Until the
    /// bar's first notice the panel only remembers it is wanted, and docks on that notice (TasksPanel.Show).
    /// </summary>
    public void Restore()
    {
        if (!IsOpen) OpenFresh();
    }

    /// <summary>
    /// `tasks-window hide`, the toolbar's ×, Alt+F4 and the bar's toggle: the panel gives its strip back and hides, the
    /// view ends, and the saved state says it is closed.
    /// </summary>
    public void Hide()
    {
        // Never once the app is going: the state Shutdown saved is the one the next start reads.
        if (shutDown) return;
        panel.Hide();
        window.Dismiss();
        Remember(open: false);
    }

    // A hidden panel opens: every filter back to its default, every thread folded, the view drawn, then the panel docked
    // at its saved width (it waits for the bar's first notice if there has been none) and the selected task scrolled into
    // view (ruling 21: the window scrolls to it when it opens).
    void OpenFresh()
    {
        filter = TaskFilter.Open;
        // The "Current workspace" checkbox (AppState.TasksPanel.CurrentWorkspace, task ed1a01fc) and the "Claude working"
        // chip (AppState.TasksPanel.ClaudeWorking, task 93fa0b9b) are the filters a fresh open keeps.
        where = PanelState.CurrentWorkspace ? TaskWorkspaceFilter.Current : TaskWorkspaceFilter.All;
        claudeWorking = PanelState.ClaudeWorking;
        goalFilter = TaskGoalFilter.All;
        assigneeFilter = TaskAssigneeFilter.All;
        // The window empties its search box in its own Open (TasksWindow.Open), so the box and this agree.
        search = TaskSearch.None;
        statsShown = false;
        expanded.Clear();
        window.ShowProblem(null);
        lineProblem = null;
        window.PaintSelection(selection.Current);
        window.Present(View());
        window.Open();
        panel.Show(PanelState.Width);
        Remember(open: true);
        window.ScrollToSelected();
    }

    // The grip's drag end (TasksWindowActions.ResizePanel): the width saved, then the panel re-docked at it in place. The
    // width goes in unclamped; TasksPanel clamps it against the screen it docks on.
    // The new width may cross one of the lines' thresholds (slim below 710, the Matrix from 780; Task 8), so every line
    // redraws once, here at the drag's end and never during it. The re-dock's DockedWidth pulse does that (see the
    // constructor); the Refresh after it draws nothing then (the signature is the same), and covers a panel not docked
    // yet, whose view still reads the saved width. A drag that crosses neither threshold draws nothing.
    void ResizePanel(double widthDips)
    {
        _ = manager.SaveTasksPanel(PanelState with { Width = widthDips });
        panel.SetWidth(widthDips);
        Refresh();
    }

    // Whether the panel is open, saved with the width as it is (a no-op when nothing changed, WorkspaceManager).
    void Remember(bool open) => _ = manager.SaveTasksPanel(PanelState with { Open = open });

    // The window, shown without activation, comes to Petre: through the App's seam (raise, then activate), or, with no
    // seam, activated where it is. The keyboard goes to the toolbar only then (see TasksWindow.FocusBoard). Nothing to
    // bring forward while the window is not on screen yet, before the bar's first notice: activating a hidden window
    // would only take the foreground from Petre's own.
    // The seam answers whether it activated the window: it does not when the panel is not on the desktop in front (a
    // failed pin, see Arrive), and then the keyboard is left alone too, since focusing the window would activate it.
    //
    // NOT OVER A FULL-SCREEN WINDOW (manual step 64n, measured 2026-11-25). While the panel stands behind a video or
    // another full-screen window on its screen (TasksPanel.BehindFullScreen), raising it would put it over that window
    // and activating it would take the keyboard from it: the trace read "shown behind the full-screen window" and then,
    // 40 ms later, "raised and activated". So it stays where the panel put it, unfocused, and comes on top by itself on
    // the first tick after the full-screen window has gone (TasksPanel.ReclaimTopmost).
    void Arrived()
    {
        if (!window.IsVisible) return;
        if (panel.BehindFullScreen)
        {
            ClickTrace.Write("tasks panel: behind a full-screen window, not brought forward");
            return;
        }
        var activated = bringForward is { } seam ? seam(EnsureHandle()) : ActivatedHere();
        if (activated) window.FocusBoard();
    }

    bool ActivatedHere()
    {
        window.ActivateHere();
        return true;
    }

    /// <summary>
    /// The App's bring-forward, in its order: <paramref name="raise"/> the window to the top of the z-order, then
    /// <paramref name="activate"/> it, then trace the arrival. Only when the panel is on the desktop in front
    /// (<paramref name="onCurrentDesktop"/>); otherwise it is traced and left alone. Answers whether it activated it.
    /// </summary>
    //
    // RAISED BEFORE IT IS ACTIVATED (2026-11-23, Petre: "tasks should become a top window, now it's behind somewhere").
    // Activation alone does not promise the top of the z-order: SetForegroundWindow on a window that already is the
    // foreground window changes nothing (measured on the hidden desktop), and the window could already be the foreground
    // one (TasksWindow.FocusBoard says how). So `raise` puts it on top first, a z-order change Windows allows a process
    // for its own window without any foreground right, and only then is it activated.
    //
    // NO MOVE ANY MORE. The floating window lived on one desktop and was moved to the desktop in front before it was
    // activated, since activating a window on another desktop takes Windows there (fix round 1, Critical 1). The panel
    // is pinned to every desktop (the App, after its first show), so it is normally on the desktop in front.
    //
    // BUT A PIN CAN FAIL (it is only traced, and retried on the next show; Task 6 review, Minor 1). A panel whose pin
    // failed stays on the desktop it was first shown on, and IsVisible still says true from another desktop, so a Show
    // from there would activate it and Windows would follow it back: the old trap. So it is activated only when it is
    // on the desktop in front, which a pinned window always is (App.OnDesktopInFront).
    internal static bool Arrive(nint hwnd, Func<bool> onCurrentDesktop, Action raise, Action activate, Action<string> trace)
    {
        if (!onCurrentDesktop())
        {
            trace($"tasks panel 0x{hwnd:X}: not on the desktop in front (its pin failed?), left there rather than taking you to it");
            return false;
        }
        raise();
        activate();
        // One line per arrival, so a live check reads whether the window really ended up in front.
        trace($"tasks panel 0x{hwnd:X}: raised and activated, foreground now 0x{Windows.Monitoring.NativeMethods.GetForegroundWindow():X}");
        return true;
    }

    /// <summary>
    /// The tiling surface's "ours": Deskbar keeps its own windows out of the grid (the bar, Manage, its dialogs), and the
    /// tasks panel with them, since a docked panel is not a tile (spec "The panel window"). The floating window was the
    /// one exception ("maybe even tile it", 2026-11-23) until it became the panel.
    /// </summary>
    internal static bool KeptFromTiling(nint hwnd) => FloatingBar.IsOwnWindow(hwnd);

    /// <summary>
    /// Puts the window at the top of the z-order of its band (the topmost one, for the panel), without activating it and
    /// without moving or sizing it (HWND_TOP, SWP_NOACTIVATE).
    /// </summary>
    internal static void RaiseToTop(nint hwnd) =>
        Windows.Monitoring.NativeMethods.SetWindowPos(hwnd, 0, 0, 0, 0, 0,
            Windows.Monitoring.NativeMethods.SWP_NOMOVE | Windows.Monitoring.NativeMethods.SWP_NOSIZE | Windows.Monitoring.NativeMethods.SWP_NOACTIVATE);

    /// <summary>Redraws an open window from the board as it is now; the window skips it when nothing it shows changed.</summary>
    internal void Refresh()
    {
        if (window.IsOpen) window.Present(View());
        RefreshTaskWindows();
    }

    // --- task windows (task dca4ebeb) ---------------------------------------------------------------------------------
    //
    // WHAT THEY ARE. A task in a window of its own: an ordinary, resizable window with a title bar, on the taskbar and in
    // Alt+Tab, showing the whole description, the question or status line in full, every comment (none cut short), the
    // sessions and the subtasks, and editing the title and description in a large box. The panel is 360 DIP and more,
    // which is too narrow to read or write a long brief in; this is where a long task is read (mockup approved
    // 2026-11-25, docs/superpowers/specs/2026-11-25-task-window-design.md).
    //
    // WHERE THEY OPEN. On the desktop in front (Petre's pick): a new window simply shows there; one already open on
    // another desktop is moved here before it is brought forward (BringTaskWindowForward), since activating it where it
    // is would take Windows to that desktop. One place, shared by every task window, is remembered across restarts
    // (AppState.TaskWindow, "remember size and position"); a second window opened while one is up is cascaded a little
    // so it does not sit exactly over the first.
    //
    // HOW THEY STAY CURRENT. Every redraw of the board (a task changed, a session list arrived, a workspace renamed)
    // redraws each open task window from the board as it is, whether or not the panel is open, and the window skips a
    // redraw that would draw the same thing. A task that is deleted closes its window. A refusal on a task shows on
    // its line in the panel and in its own window only; one on no task on the panel's problem line and every window's

    /// <summary>
    /// "Open in window" on a line's menu (the panel's and the bar's), the thread's button and `task open <id>`: the task
    /// in a window of its own, or its window brought forward when it already has one. Fails for an unknown task.
    /// </summary>
    public Result OpenWindow(Guid id)
    {
        if (shutDown) return Result.Failure("Deskbar is closing");
        var task = tasks.Board.Tasks.TryFirst(t => t.Id == id);
        if (task.HasNoValue) return Report(Result.Failure($"No task with id {id}"));
        if (!taskWindows.TryGetValue(id, out var shown))
        {
            shown = NewTaskWindow();
            shown.Actions = actions;
            // Each move, resize or close saves the one shared place (a no-op when unchanged); a close also lets go of it.
            shown.PlaceChanged = place => _ = manager.SaveTaskWindow(place);
            shown.Leaving = () => taskWindows.Remove(id);
            shown.Present(DetailOf(task.Value));
            shown.Open(manager.State.TaskWindow, cascade: taskWindows.Count);
            taskWindows[id] = shown;
        }
        if (BringTaskWindowForward is { } bring) bring(shown.EnsureHandle());
        else shown.ActivateHere();
        return Result.Success();
    }

    /// <summary>Whether the task has a window open (for the tests and the bar's menu).</summary>
    internal bool HasWindow(Guid id) => taskWindows.ContainsKey(id);

    /// <summary>For the tests: the task's open window, if any.</summary>
    internal TaskDetailWindow? WindowOf(Guid id) => taskWindows.GetValueOrDefault(id);

    // Each open task window drawn again from the board; one whose task is gone (deleted) closes.
    void RefreshTaskWindows() =>
        taskWindows.ToList().ForEach(pair => tasks.Board.Tasks.TryFirst(t => t.Id == pair.Key).Match(
            task => pair.Value.Present(DetailOf(task)),
            () => pair.Value.CloseForGood()));

    // What a task window draws: the task's line with its thread open (comments, sessions, subtasks), the workspaces for
    // its "Move to…" menu, and the clock its times are told against.
    TaskDetailView DetailOf(TaskItem task) =>
        new(RowOf(task, open: true), WorkspaceChipMenu.Entries(manager.State, withNone: true), now());

    public void Filter(TaskFilter shown)
    {
        filter = shown;
        Refresh();
    }

    /// <summary>
    /// The toolbar's workspace filter: all, one workspace or none from its menu, or Current from the "Current workspace"
    /// checkbox, which follows the workspace in front and is remembered across opens (AppState.TasksPanel.CurrentWorkspace;
    /// see OpenFresh).
    /// </summary>
    public void FilterWorkspace(TaskWorkspaceFilter shown)
    {
        where = shown;
        // A no-op write when the choice keeps it as it was (SaveTasksPanel compares).
        _ = manager.SaveTasksPanel(PanelState with { CurrentWorkspace = shown.FollowsCurrent });
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

    /// <summary>
    /// The search box's text, on every keystroke (see the header). Text that is the same as before (the window emptying
    /// an already empty box on a fresh open) redraws nothing.
    /// </summary>
    //
    // NO DEBOUNCE, ON PURPOSE. A board holds tens of tasks, so narrowing, grouping and counting them is far under a
    // keystroke's time, and the window draws nothing when what it would draw is unchanged (TasksWindow.Draw). Narrowing
    // at once is what makes the box feel live; a delay would only add lag. Revisit if a board ever holds thousands.
    public void Search(string text)
    {
        if (search.Text == text) return;
        search = new TaskSearch(text);
        // A search by id that finds a task (task 6e4dc116) leaves the Stats view: the box stays on screen there, and Stats
        // counts the whole board, so the task asked for would otherwise never appear. A word search leaves it alone.
        if (statsShown && search.Found(tasks.Board).HasValue) statsShown = false;
        Refresh();
    }

    /// <summary>
    /// The "Claude working" chip: only the tasks with a live session while on, remembered across opens and restarts
    /// (AppState.TasksPanel.ClaudeWorking, task 93fa0b9b; see the header).
    /// </summary>
    public void FilterClaudeWorking(bool on)
    {
        KeepClaudeWorking(on);
        Refresh();
    }

    /// <summary>
    /// The toolbar's "Subtasks" checkbox (task 173ef268): ticked, subtasks are drawn under their parent; unticked, they
    /// fold into the parent's line, which counts them (TaskViews.FoldSubtasks has the rule). Saved at once and kept
    /// across opens and restarts (AppState.TasksPanel.HideSubtasks), so the redraw reads it back from the state.
    /// </summary>
    public void ShowSubtasks(bool on)
    {
        // A no-op write when it is already so (SaveTasksPanel compares); the redraw below then still runs, harmlessly.
        _ = manager.SaveTasksPanel(PanelState with { HideSubtasks = !on });
        Refresh();
    }

    // The chip's state and its saved copy move together, so whatever turns it off (the chip, a task it hides being shown)
    // is what the next open starts with. A no-op write when nothing changes (SaveTasksPanel compares).
    void KeepClaudeWorking(bool on)
    {
        claudeWorking = on;
        _ = manager.SaveTasksPanel(PanelState with { ClaudeWorking = on });
    }

    // THE HEADER'S DIVIDER DRAG END (task 3df2d28f): the full line's column widths saved with the panel, then a redraw so
    // the view carries them. The window already shows them (it resized every line live during the drag); the redraw
    // draws nothing new unless something else changed meanwhile. A no-op write when the drag ended where it began.
    public void ResizeColumns(TaskColumnWidths widths)
    {
        _ = manager.SaveTasksPanel(PanelState with { Columns = widths });
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
    /// double-click does (<see cref="Go"/>). RULING 63 (Petre: "it should also be collapsible"): a click TOGGLES, so a click on
    /// a line whose thread is open collapses it again (the task stays picked). Esc collapses the selected one too
    /// (TasksWindow.Escape).
    /// </summary>
    public void Pick(Guid id)
    {
        selection.Select(id);
        if (!expanded.Remove(id)) expanded.Add(id);
        Refresh();
    }

    /// <summary>
    /// The bar's single click on a task line (Ruling 54): the panel comes forward (opening when it was hidden) on that
    /// task, picked, scrolled into view and expanded to its thread. It goes nowhere.
    /// </summary>
    /// <summary>
    /// The selected task's line scrolled into view while the panel is open (TasksWindow.RevealSelected); closed, nothing
    /// happens and the panel is not opened. For a selection made from outside the panel (task 4846fde1: a Claude task's
    /// terminal brought to the front).
    /// </summary>
    public void Reveal()
    {
        if (IsOpen) window.RevealSelected();
    }

    public void ShowTask(Guid id)
    {
        // A linked task in a thread can be closed (task links): show it under the Closed filter, and an open one away
        // from it, or the click picks a task the panel does not draw.
        tasks.Board.Tasks.TryFirst(t => t.Id == id).Tap(t =>
            filter = !t.IsOpen ? TaskFilter.Closed : filter == TaskFilter.Closed ? TaskFilter.Open : filter);
        selection.Select(id);
        RevealPastSearch(id);
        Show();
        // Open starts a hidden window at the default view (no thread open); the task's own thread opens after it.
        expanded.Add(id);
        window.PaintSelection(selection.Current);
        Refresh();
        window.ScrollToSelected();
    }

    /// <summary>
    /// The toolbar's Stats switch and `tasks-window stats` (task 521d2b07; spec
    /// docs/superpowers/specs/2026-11-25-task-stats-design.md): the panel opens if it was hidden, then shows the Stats view
    /// in place of the List or the Matrix. Opened fresh first, so the fresh open's reset does not undo it.
    /// </summary>
    public void ShowStats()
    {
        if (!IsOpen) Show();
        statsShown = true;
        Refresh();
    }

    /// <summary>
    /// A task's line in the Stats view, the "link to each task" Petre asked for: the panel leaves Stats and shows that
    /// task, picked, scrolled to and opened to its thread (ShowTask). A task the filters in force would hide is let
    /// through first: the status filter becomes the one that lists it (Closed for a closed task, Snoozed for a snoozed
    /// one, Open otherwise, unless the one in force already shows it), and a workspace, assignee or goal filter that
    /// leaves it out goes back to all. The search and "Claude working" are ShowTask's own business (RevealPastSearch).
    /// </summary>
    public void OpenFromStats(Guid id)
    {
        statsShown = false;
        ShowTaskPastFilters(id);
    }

    /// <summary>
    /// A subtask's way to its parent (task 54b39c4c; Petre: "subtask should show its parent and have a way to open the
    /// parent easily"): the "Subtask of" link at the top of a subtask's open thread, and "Open parent" in the line's menu
    /// here and on the bar. The parent is shown the way a Stats line's task is (ShowTaskPastFilters): picked, scrolled
    /// to, its thread open, and let through any filter that would hide it, since Petre asked for that very task.
    /// </summary>
    public void OpenParent(Guid parentId) => ShowTaskPastFilters(parentId);

    // A LINK IN A TASK'S TEXT (task 26f15d70, Petre: "when opening links from the tasks, they should be opened in the
    // task's workspace"). The panel shows the tasks of every workspace, so a link on a Services task clicked while Petre
    // stands in Deskbar used to open on Deskbar, among that workspace's windows. Now the click goes to the task's
    // workspace first, the switch a click on the task's line makes, and only then opens the link.
    //
    // Why switch-then-open rather than a new browser window started in the background (what `deskbar.cmd open` does for
    // a session in a background workspace, RemoteControl.Open): this is Petre's own click, so he wants to see the page,
    // and a page opened somewhere he is not looking would look like a click that did nothing. Once his desktop is the
    // task's, the usual open is the right one: a Chromium browser puts the link in its window on the desktop in front, or
    // opens a new one there when that desktop has none, so the workspace does not gain a browser window per click. A file
    // link's program or Explorer window opens on the desktop in front in the same way.
    //
    // Stays as before when there is nowhere to go: a note on no workspace, or a task already in the current workspace. A
    // switch that fails (the workspace's desktop was lost, say) is said on the problem line, and the link still opens
    // where Petre is, since a link that opens on the wrong desktop is better than one that does not open at all.
    void OpenLink(Guid id, Uri uri)
    {
        // A TASK LINK (task 5016d533, TaskLink): https://deskbar.app/task#<id> in a task's text, pasted from a pull request
        // or written by a session, opens that task here, in its window, as the link does from a browser. Going out to the
        // browser and back through the deskbar: protocol would end in the same window, after a browser tab and its
        // prompt. No workspace switch: the task window comes to the desktop in front (OpenWindow). A task this board
        // does not have (one on another PC's board) is said on the problem line.
        if (TaskLink.TaskIdOf(uri.OriginalString) is { HasValue: true } linked)
        {
            Report(tasks.Board.Find(linked.Value).Bind(task => OpenWindow(task.Id)));
            return;
        }

        var target = tasks.Board.Tasks.TryFirst(t => t.Id == id).Bind(t => t.WorkspaceId);
        var elsewhere = target.HasValue && manager.CurrentWorkspaceId.Map(current => current != target.Value).GetValueOrDefault(true);
        if (elsewhere) Report(manager.Switch(target.Value), id);
        OpenUri(uri);
    }

    // OpenFromStats's and OpenParent's shared half: a filter that would hide the task is let through first (see
    // OpenFromStats for which and how), then the task is shown (ShowTask).
    void ShowTaskPastFilters(Guid id)
    {
        LetPastFilters(id);
        ShowTask(id);
    }

    // Every filter that would hide the task is let through: the status filter switches to one that lists it, and the
    // workspace, assignee and goal filters go back to all. Nothing is drawn here; the caller's redraw shows the change.
    void LetPastFilters(Guid id) =>
        tasks.Board.Tasks.TryFirst(t => t.Id == id).Tap(task =>
        {
            if (!TaskViews.Lists(filter, task)) filter = TaskViews.FilterListing(task);
            if (!where.Resolved(manager.CurrentWorkspaceId).Admits(task)) where = TaskWorkspaceFilter.All;
            if (!assigneeFilter.Admits(task)) assigneeFilter = TaskAssigneeFilter.All;
            if (goals is not null && !goalFilter.Admits(task, goals.Board)) goalFilter = TaskGoalFilter.All;
        });

    // THE TASK PETRE CLICKED ON THE BAR IS ON SCREEN (Task 7 review, ruling on ShowTask). On an open panel the search and
    // the Claude working chip stay as they were, and either could hide the very task asked for, so the one that hides it
    // is cleared: the search (emptied in the box too, TasksWindow.ClearSearch) when it does not admit the task, the chip
    // when the task has no live session. Only those two: the status, workspace, goal and assignee filters keep their
    // behaviour, as ruled. On a hidden panel this is moot, since OpenFresh resets everything anyway.
    void RevealPastSearch(Guid id)
    {
        var task = tasks.Board.Tasks.TryFirst(t => t.Id == id);
        if (task.HasNoValue) return;
        if (!search.Admits(task.Value))
        {
            // The controller's search first, so the TextChanged the box raises finds nothing to change (Search).
            search = TaskSearch.None;
            window.ClearSearch();
        }
        if (claudeWorking && !task.Value.HasLiveSession) KeepClaudeWorking(false);
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
            return Report(Result.Success(), id);
        }
        return Report(goTo(task.Value), id);
    }

    /// <summary>
    /// A session's Open in the thread (see the header): what going to that session does, through `openSession`. The
    /// task is picked, as a click on its line picks it; a refusal shows on the problem line.
    /// </summary>
    public Result OpenSession(Guid id, string sessionId)
    {
        if (openSession is null) return Report(Result.Failure("Sessions cannot be opened in this copy"));
        var task = tasks.Board.Tasks.TryFirst(t => t.Id == id);
        if (task.HasNoValue) return Report(Result.Failure($"No task with id {id}"));
        selection.Select(id);
        return Report(openSession(task.Value, sessionId), id);
    }

    /// <summary>
    /// Closes the task at once, keeping its line in place with Undo for the undo window (see the header). A task Claude is
    /// still working on (task 37cd7081, TaskBoard.ClaudeStillWorking) is not closed: the window opens its stop menu, whose
    /// "Stop Claude and close" comes back here with <paramref name="stopClaude"/>, through the App's StopAndClose.
    /// </summary>
    public Result Close(Guid id, bool stopClaude = false)
    {
        if (tasks.Board.Tasks.FirstOrDefault(t => t.Id == id) is not { IsOpen: true } task) return Report(tasks.Close(id), id);
        if (!stopClaude && TaskBoard.ClaudeStillWorking(task))
        {
            window.OpenStopClaudeMenu(id);
            return Result.Success();
        }
        // Pending before Close pulses Changed, so the redraw that pulse causes already draws the line struck through.
        pendingCloses[id] = new PendingClose(task.Status, undoTimer(UndoWindow, () => Forget(id)))
        {
            Question = task.Question,
            Selected = selection.IsSelected(id),
        };
        var closed = !stopClaude ? tasks.Close(id)
            : stopClaudeAndClose is { } stop ? stop(task)
            : tasks.Close(id, stoppingClaude: true);
        return Report(closed.TapError(_ => Forget(id)), id);
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
        return Report(back, id);
    }

    public Result Reopen(Guid id) => Report(tasks.Reopen(id), id);

    public Result SetStatus(Guid id, TaskStatus status) => Report(tasks.SetStatus(id, status), id);

    public Result Rename(Guid id, string title) => Report(tasks.Rename(id, title), id);

    /// <summary>
    /// Edit in the window (Ruling 58): the title and the description together. The title first: a refused one (empty) stops
    /// there and the description is left alone, so a failed save changes nothing. A title that is the same is not renamed
    /// again, and a description that is the same saves nothing (TaskService.Describe).
    /// </summary>
    public Result Edit(Guid id, string title, string description) =>
        Report(tasks.Board.Tasks.TryFirst(t => t.Id == id).Match(
            task => (title.Trim() == task.Title ? Result.Success() : tasks.Rename(id, title)).Bind(() => tasks.Describe(id, description)),
            () => Result.Failure($"No task with id {id}")), id);

    /// <summary>One of HIS comments rewritten (Ruling 58); a session's is refused by the board.</summary>
    public Result EditComment(Guid comment, string markdown) => Report(tasks.EditComment(comment, markdown), TaskOfComment(comment));

    /// <summary>Ruling 62: Petre settles any comment from its button (or brings a settled one back); kept either way.</summary>
    public Result SettleComment(Guid comment, bool settle) => Report(tasks.SettleComment(comment, settle), TaskOfComment(comment));

    public Result MoveTo(Guid id, Maybe<Guid> workspace) => Report(tasks.MoveTo(id, workspace), id);

    /// <summary>
    /// The "Assign to" menu (Ruling 57): hands the task to Petre or to Claude. Only here and in `task assign`, never on
    /// the bar. Nothing else about the task changes, so its line keeps its place.
    /// </summary>
    public Result Assign(Guid id, Assignee who) => Report(tasks.Assign(id, who), id);

    /// <summary>A toggle on a line: the flag on or off; the task goes to the top of the quadrant it lands in.</summary>
    public Result SetFlag(Guid id, TaskFlag flag, bool on) => Report(tasks.SetFlag(id, flag, on), id);

    // SNOOZE (task 6da8d7eb; TaskSnooze.cs has the four lengths): the line's snooze button and its "⋯" menu. The end is
    // worked out on the LOCAL clock, so "Tomorrow" and "Monday" mean 09:00 where Petre is; the service stores it as an
    // instant. A refusal (a task waiting on him, a closed one) shows on the problem line like any other.
    public Result Snooze(Guid id, SnoozeChoice choice) =>
        Report(tasks.Snooze(id, TaskSnooze.Until(choice, now().ToLocalTime(), TimeZoneInfo.Local.GetUtcOffset)), id);

    public Result Wake(Guid id) => Report(tasks.Wake(id), id);

    /// <summary>A drop: the task goes to that place in that quadrant, taking its flags (TaskBoard.Place).</summary>
    public Result Place(Guid id, Quadrant quadrant, TaskPlace place) => Report(tasks.Place(id, quadrant, place), id);

    /// <summary>
    /// The "Goal" menu: links the task to an open goal, or (none) unlinks it, through GoalService.LinkTask. Flags, rank
    /// and status stay as they are, so the line does not move.
    /// </summary>
    public Result SetGoal(Guid id, Maybe<Guid> goal) =>
        Report(goals is { } service ? service.LinkTask(id, goal) : Result.Failure("Goals are not available"), id);

    public Result Delete(Guid id)
    {
        expanded.Remove(id);
        return Report(tasks.Delete(id), id);
    }

    /// <summary>
    /// One of Petre's notes, in the backlog, where the add box's chip says, with the flags its toggles say (Ruling 37):
    /// it lands at the top of that quadrant. <paramref name="describe"/>, when the add box holds pasted pictures, writes them
    /// under the new task's id and answers its description (TaskService.AddNote); a failure there adds nothing and shows
    /// on the problem line like any other refusal.
    /// </summary>
    public Result AddNote(string title, Maybe<Guid> workspace, Quadrant quadrant = Quadrant.IV, Maybe<Guid> parent = default,
        Func<Guid, Result<string>>? describe = null) =>
        Report((describe is null
            ? tasks.AddNote(title, workspace, quadrant, parent)
            : tasks.AddNote(title, workspace, quadrant, parent, describe)).Tap(task => ShowAdded(task.Id)).Bind(_ => Result.Success()));

    /// <summary>
    /// A subtask typed into the box in a thread, where "Add subtask" was (task 0eb943d8, Petre: "make it right there, at
    /// the add subtask button", "and then make that same space open and show the subtask"). It goes under the task
    /// <paramref name="lineId"/> names (TaskBoard.SubtaskParentFor: that task, or its parent when it is a subtask itself,
    /// since subtasks are one level deep), on the parent's workspace, as a plain backlog note. Its own thread opens. With
    /// "Subtasks" unticked and the box in the parent's thread, it opens there, in place, as a folded subtask does (Petre:
    /// "let subtasks stay where they are, expand their current place"). Otherwise the thread the box was in closes: the
    /// subtask is drawn right under its parent, so it opens in the space the thread took, and it is picked, glows and is
    /// scrolled to, as any task just added is (ShowAdded).
    /// </summary>
    public Result AddSubtaskInThread(Guid lineId, string title, Func<Guid, Result<string>>? describe, bool inParentThread = false) =>
        Report(tasks.Board.SubtaskParentFor(lineId).ToResult("That task is gone")
            .Bind(parent => describe is null
                ? tasks.AddNote(title, parent.WorkspaceId, Quadrant.IV, parent.Id)
                : tasks.AddNote(title, parent.WorkspaceId, Quadrant.IV, parent.Id, describe))
            .Tap(task =>
            {
                expanded.Add(task.Id);
                // SUBTASKS FOLDED ("Subtasks" unticked), added from the parent's own thread: the new subtask stays where
                // a folded subtask lives, listed in that thread, and opens there in place (Petre: "let subtasks stay
                // where they are, expand their current place"). The thread stays open and the selection is left alone:
                // picking the subtask would unfold it onto a line of its own (TaskViews.FoldSubtasks keeps the selected
                // task), which is the move Petre did not want. Not for a sibling added from a subtask's own line: its
                // parent may be filtered out, with no thread on show to hold the new one (review), so that takes the
                // line path below, which lets it past the filters and picks it.
                // ADDED FROM A SUBTASK'S THREAD INSIDE ITS PARENT'S (task 249a1e8a, `inParentThread`): the subtask line in
                // the parent's thread has Add subtask now, which adds a sibling (subtasks are one level deep). The parent's
                // thread is on show, so the sibling stays in it too and opens there, and the thread the box was in folds,
                // as the thread a box was in does below.
                if (PanelState.HideSubtasks && inParentThread) expanded.Remove(lineId);
                if (PanelState.HideSubtasks && (inParentThread || task.ParentId.Map(p => p == lineId).GetValueOrDefault(false)))
                {
                    Refresh();
                    return;
                }
                // SUBTASKS SHOWN: the new subtask has a line right under its parent. The thread the box was in closes
                // and the subtask opens on that line, in the space the thread took.
                expanded.Remove(lineId);
                // A new backlog note is hidden by the Waiting on you, In progress or Done chip, by an Assigned or goal
                // filter, and so on (review of #567). Opened where the thread was, it must be on the list, so those
                // filters let it through, as a subtask clicked in a thread is let through.
                LetPastFilters(task.Id);
                ShowAdded(task.Id);
            })
            .Bind(_ => Result.Success()), lineId);

    // THE TASK JUST ADDED IS HIGHLIGHTED, as the bar's add box does it (task ee75f046, Petre's "yes" to option 3 of the
    // mockup; TaskGlow has the rest). The add's pulse has already redrawn the panel (tasks.Changed runs Refresh inline).
    // The new task is picked in both views, a search or the Claude working chip that would hide it is cleared
    // (RevealPastSearch, as for a task clicked on the bar), its line glows and the panel scrolls to it.
    void ShowAdded(Guid id)
    {
        selection.Select(id);
        RevealPastSearch(id);
        Refresh();
        window.Glow(id);
        window.ScrollToSelected();
    }

    /// <summary>A comment from Petre (no session), shown as "you". Its thread stays open to show it.</summary>
    public Result Comment(Guid id, string markdown)
    {
        expanded.Add(id);
        return Report(tasks.Comment(id, Maybe<string>.None, markdown), id);
    }

    public void Dispose()
    {
        disposed = true;
        subscriptions.Dispose();
        pendingCloses.Values.ToList().ForEach(p => p.Timer.Dispose());
        pendingCloses.Clear();
    }

    /// <summary>
    /// The app is going away: whether the panel is open is saved FIRST, as it is (an open panel opens again on the next
    /// start, Restore), then the panel gives its strip back for good and the window is closed for good. The app exiting is
    /// not the user closing the panel, so TasksPanel.Shutdown leaves IsShown alone, and the order here keeps the save from
    /// ever reading the window after it has gone.
    /// </summary>
    //
    // EVERY WAY THE APP GOES CALLS THIS FIRST (Task 6 review, Important 1): ExitApp (and so Restart), the in-app update's
    // hand-over and Windows' sign-out or shutdown (SessionEnding), each before Application.Shutdown. That matters
    // because Application.Shutdown closes every window, and a close of this window that is not CloseForGood's is
    // read as the user's Alt+F4 (TasksWindow.OnClosing, ClosePanel, Hide), which saves the panel as closed: an open
    // panel would not come back after a reboot. Once this has run the window is closed for good, so Application's own
    // close of it later finds nothing to do. Safe twice.
    public void Shutdown()
    {
        if (shutDown) return;
        shutDown = true;
        Remember(open: panel.IsShown);
        Dispose();
        // The task windows close with the app (their place is already saved, on every move and close); they are not
        // reopened at the next start.
        taskWindows.Values.ToList().ForEach(w => w.CloseForGood());
        taskWindows.Clear();
        panel.Shutdown();
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
        // The search and Claude working come right after it (Task 7, see the header), for the same reason. `searched` is the
        // board before Claude working: the chip's own count is taken on it, so the chip says how many it would show
        // under every other filter, whether it is on or off.
        var whole = WithPendingCloses(tasks.Board);
        var searched = search.Narrow(goalFilter.Narrow(whole, goalBoard));
        var board = ClaudeWorking.Narrow(searched, claudeWorking);
        // SEARCH BY ID (task 6e4dc116; TaskSearch has the rule): text that starts a task's id finds that task in whatever
        // section it is in, closed and snoozed included, past every filter, so the lookup is taken on the whole board
        // and grouped by TaskViews.Everywhere, not by the status filter. The chips keep their counts from `board`.
        var foundById = search.Found(whole);
        // The workspace filter as it applies now: "Current workspace" becomes the workspace in front (task ed1a01fc, see
        // TaskWorkspaceFilter.Current), so the groups, the Matrix, every chip's count and the add box all follow a
        // desktop switch, which redraws through ShownStateKey (it holds CurrentWorkspaceId).
        var shown = where.Resolved(manager.CurrentWorkspaceId);
        var claudeWorkingCount = TaskViews.Count(ClaudeWorking.Narrow(searched, true), filter, shown, assigneeFilter);
        // Follow-up to the subtasks spec: a subtask is drawn right under its parent, in the parent's group (TaskViews.UnderParents);
        // such a line is Nested, so it shows the arrow without the parent's name.
        var (underParents, nested) = TaskViews.UnderParents(foundById.Match(TaskViews.Everywhere, () => TaskViews.Grouped(board, filter, shown, assigneeFilter)));
        // The "Subtasks" checkbox unticked (task 173ef268): the lines drawn under a parent are left out, and the parent's
        // own "· N subtasks" says they are there. Read from the saved state, as OpenFresh reads CurrentWorkspace.
        // A search by id never folds (task 6e4dc116): the subtask asked for by its id would vanish under its parent.
        var showsSubtasks = !PanelState.HideSubtasks || foundById.HasValue;
        // The selected task never folds (task b0526e18, Petre: "i can't interact with the subtask. i should be able to"):
        // a subtask shown from its parent's thread is selected, and folded it had no line to open, act on or scroll to.
        // It folds again once another task is picked.
        // A subtask of a snoozed parent folds as well (task 4c01747c, TaskViews.UnderSnoozedParent), only under a parent
        // these filters admit, so the Snoozed chip leads to it. Not while words are typed in the search box (the parent
        // is off the list then too, so a subtask the words found would vanish), and not in the Snoozed view, which lists
        // snoozed tasks: there a snoozed subtask whose own parent is awake must keep its line.
        var snoozedFamily = search.IsBlank && filter != TaskFilter.Snoozed
            ? TaskViews.UnderSnoozedParent(whole, shown, assigneeFilter)
            : new HashSet<Guid>();
        var listed = showsSubtasks ? underParents : TaskViews.FoldSubtasks(underParents, nested, selection.Current, snoozedFamily);
        // A SUBTASK SHOWS ONCE (task 0eb943d8, Petre: "it shows up twice", "clicking the one above takes me to the one
        // below"). A parent's open thread lists its subtasks, so one folded away (Subtasks unticked) or hidden by a filter
        // can still be reached. A subtask that has a line of its own right under the parent is already on screen, and
        // listed in the thread as well it showed twice, one above the other, the entry only jumping to the line. So the
        // thread leaves out every subtask drawn under its parent (TaskViews.DrawnUnderParents). A folded one clicked in
        // the thread opens there, in place (Petre: "let subtasks stay where they are, expand their current place"); one
        // with a line of its own elsewhere goes to that line (WithLinedSubtasks).
        var drawnUnderParents = TaskViews.DrawnUnderParents(listed, nested);
        var listedIds = listed.SelectMany(g => g.Tasks).Select(t => t.Id).ToHashSet();
        var groups = listed
            .Select(g => new TaskGroupView(g.Kind, TaskViews.GroupName(g.Kind), g.Tasks.Select(Row).Select(row => WithLinedSubtasks(row with
            {
                Nested = nested.Contains(row.Id),
                Subtasks = row.Subtasks.Where(sub => !drawnUnderParents.Contains(sub.Id)).ToArray(),
            }, listedIds)).ToArray()))
            .ToArray();
        var filters = FilterOrder.Select(f => (Filter: f, Count: TaskViews.Count(board, f, shown, assigneeFilter)))
            // The Snoozed chip (task 6da8d7eb) shows only while something is snoozed, or while it is the filter in force (so
            // the last snooze running out does not pull the chip from under the view it is showing).
            .Where(x => x.Filter != TaskFilter.Snoozed || x.Count > 0 || filter == TaskFilter.Snoozed)
            .Select(x => new TaskFilterChip(x.Filter, $"{FilterName(x.Filter)} {x.Count}", x.Filter == filter,
                x.Filter == TaskFilter.WaitingOnYou && x.Count > 0))
            .ToArray();
        // Every workspace menu of the window (move, the workspace filter, the add box's chip) is the task list's one
        // workspace menu: chips in their colours, in the bar's order, grouped as on the bar, none first.
        var places = WorkspaceChipMenu.Entries(manager.State, withNone: true);
        var addTarget = shown.Any ? PlaceOf(manager.CurrentWorkspaceId) : PlaceOf(shown.Workspace);
        // The Matrix's four cells (Ruling 28), drawn instead of the groups while the switch says so (AppState.TasksMatrix).
        var matrix = TaskViews.Matrix(board, filter, shown, assigneeFilter);
        var cellTasks = showsSubtasks ? matrix : TaskViews.FoldSubtasks(matrix, selection.Current, snoozedFamily);
        var cellIds = cellTasks.SelectMany(c => c.Tasks).Select(t => t.Id).ToHashSet();
        var cells = cellTasks
            .Select(c => new TaskCellView(c.Quadrant, TaskViews.GroupName(TaskViews.KindOf(c.Quadrant)),
                c.Tasks.Select(Row).Select(row => WithLinedSubtasks(row, cellIds)).ToArray()))
            .ToArray();
        var (menuGoals, filterGoals) = showsGoals ? GoalChoices(goalBoard) : NoGoalChoices;
        // THE PANEL'S WIDTH DECIDES TWO THINGS (Task 8; spec "Lines at panel width", "The Matrix"): slim lines below 710
        // DIP, and the List / Matrix switch only from 780. It is the width the panel DOCKED at (TasksPanel.DockedWidth,
        // fix round 1), which is the saved width clamped to half the screen it docks on: after a move to a smaller
        // screen or a scale change the panel is narrower than the saved width, and the lines must follow what is drawn
        // (review Important 1: a saved 800 on a 640 DIP panel offered the Matrix). Before the first dock (OpenFresh draws
        // before the panel docks) it is the saved width; the first dock's pulse then redraws if the clamp moved it.
        var width = dockedWidth.GetValueOrDefault(PanelState.Width);
        var matrixAllowed = TasksPanelLayout.MatrixAllowed(width);
        // The view carries the CHOICE (where), not the resolved filter, so the toolbar can tick its "Current workspace" checkbox.
        return new TasksView(filters, WorkspaceFilterLabel(shown), where, places, groups, EmptyText(), addTarget)
        {
            // The manager's open question (task 61e4d143, ManagerQuestion), whatever the filters: it is not a task, and it
            // has priority. Only while there is a manager to open; the Stats view leaves it out (TasksWindow draws it).
            ManagerAsks = OpenManager is null ? null : App.ManagerAskOf(manager.State.Manager),
            // The setting is Petre's and the width never changes it: a panel too narrow for the Matrix shows the List and
            // leaves AppState.TasksMatrix as it is, so widening it again brings the Matrix back.
            // A search by id shows the List even under the Matrix setting: the Matrix has no Closed or Snoozed cell, and
            // the task asked for could be either (task 6e4dc116).
            Matrix = manager.State.TasksMatrix && matrixAllowed && foundById.HasNoValue,
            MatrixAllowed = matrixAllowed,
            Slim = TasksPanelLayout.Slim(width),
            Cells = cells,
            ShowsGoals = showsGoals,
            Goals = menuGoals,
            GoalFilterChoices = filterGoals,
            GoalFilter = goalFilter,
            GoalFilterLabel = showsGoals ? GoalFilterLabel(goalBoard) : "",
            AssigneeFilter = assigneeFilter,
            AssigneeFilterLabel = AssigneeFilterLabel(),
            Search = search.Text,
            ClaudeWorking = claudeWorking,
            ClaudeWorkingCount = claudeWorkingCount,
            ShowsSubtasks = showsSubtasks,
            // The Stats view counts the whole board, not the filtered one: it is about the work, not about what a filter
            // shows (spec 2026-11-25-task-stats-design.md, "What it shows"). Made only while it shows.
            Stats = statsShown ? TaskStats.Of(tasks.Board, now(), TimeZoneInfo.Local, TaskStats.DaysToShow(tasks.Board, now(), TimeZoneInfo.Local)) : null,
            Columns = PanelState.ColumnWidths,
        };
    }

    // What an empty list says: under a search or Claude working, that THEY found nothing, since "Nothing open" would be
    // wrong while open tasks are only hidden by them; otherwise the status filter's own line.
    string EmptyText() =>
        !search.IsBlank ? "No task matches the search."
        : claudeWorking ? "Claude is not working on any of these."
        : EmptyText(filter);

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

    TaskRowView Row(TaskItem task) => RowOf(task, expanded.Contains(task.Id));

    // A line as drawn, its thread (comments, sessions, subtasks) filled in only when `open`: a line whose thread is
    // expanded in the panel, or always for a task window (OpenWindow), which shows the whole task.
    // A row's subtask entries as one view draws them, given every task that has a line of its own in that view (review of
    // task 0eb943d8). A subtask with a line (a waiting one in the pinned block, a closed one under Closed, any in the
    // Matrix's cells) opens its thread on that line, so its entry carries no thread in place, which would draw the same
    // thread twice and leave its comment box half-wired, and the entry goes to the line instead (HasLine).
    static TaskRowView WithLinedSubtasks(TaskRowView row, IReadOnlySet<Guid> lined) =>
        row.Subtasks.Any(sub => lined.Contains(sub.Id))
            ? row with { Subtasks = row.Subtasks.Select(sub => lined.Contains(sub.Id) ? sub with { Row = null, HasLine = true } : sub).ToArray() }
            : row;

    // `inPlace`: whether a subtask whose thread is open carries its own row for the parent's thread to draw it in place.
    // Only the parent's own row does: the subtask's row is built with it off, so a hand-edited file where a task is its
    // own parent, or two tasks are each other's, cannot recurse for ever (review of task 0eb943d8).
    TaskRowView RowOf(TaskItem task, bool open, bool inPlace = true) => new(
        task.Id,
        task.Title,
        // A snoozed line (only the Snoozed filter shows one) says when it comes back, ahead of its usual detail.
        // A closed duplicate (task links) says what it duplicates ahead of its usual detail, so the closed view tells it
        // apart from a plain close at a glance.
        task.SnoozedUntil.Match(
            until => string.Join(" · ", new[] { $"Snoozed {TaskSnooze.UntilText(until.ToLocalTime(), now().ToLocalTime())}", FloatingBar.DetailOf(task) }.Where(part => part.Length > 0)),
            () => tasks.Board.DuplicateTarget(task).Match(
                original => string.Join(" · ", new[] { $"{TaskViews.DuplicateName} of {FloatingBar.FirstLine(original.Title)}", FloatingBar.DetailOf(task) }.Where(part => part.Length > 0)),
                () => FloatingBar.DetailOf(task))),
        task.Status,
        task.IsClaudes,
        SessionsText(task),
        task.Sessions.Count,
        task.Sessions.Count(s => s.IsLive),
        PlaceOf(task.WorkspaceId),
        task.Comments.Count,
        open,
        pendingCloses.ContainsKey(task.Id),
        open ? task.Comments.Select(CommentView).ToArray() : [])
    {
        Created = task.Created,
        Assignee = task.Assignee,
        Description = task.Description,
        Important = task.Important,
        Urgent = task.Urgent,
        SnoozedUntil = task.SnoozedUntil.Map(until => (DateTimeOffset?)until).GetValueOrDefault(),
        PullRequests = task.PullRequests,
        // Whether a Done task's change runs in this Deskbar yet: the ring and the outlined chip until it does (task 8470d176).
        Delivery = tasks.DeliveryOf(task),
        // Whether its menu offers "Mark deployed": merged, and a pull request outside this build's repository not
        // reported yet (task 8470d176, phase 2; TaskService.CanMarkDeployed).
        CanMarkDeployed = tasks.CanMarkDeployed(task),
        // The last action's refusal when it was on this task (task 41f08dba, see lineProblem): drawn under the title.
        Problem = lineProblem is { } refused && refused.Task == task.Id ? refused.Text : null,
        // Null for no goal and for a link to a goal that no longer exists, which reads as no goal everywhere.
        Goal = goals?.GoalOf(task).Map(ChoiceOf).GetValueOrDefault(),
        // Only an open thread shows them, and an ended one's state looks on disk for its transcript (see the header).
        SessionList = open ? sessionsOf(task) : [],
        // Of its live sessions, how many Claude is busy in now (Task 8): their squares wear a halo.
        WorkingCount = workingOf(task),
        // Subtasks (Petre's pick A): the parent's title in front of a subtask's, and a parent's open subtasks' count.
        ParentTitle = tasks.Board.ParentOf(task).Map(p => (string?)FloatingBar.FirstLine(p.Title)).GetValueOrDefault(),
        // ...and the parent's id, for the subtask's way back to it (task 54b39c4c: "Subtask of" in the thread, "Open
        // parent" in the line's menu, the task window's Parent section).
        ParentId = tasks.Board.ParentOf(task).Map(p => (Guid?)p.Id).GetValueOrDefault(),
        SubtaskCount = tasks.Board.OpenSubtaskCount(task.Id),
        // Claude still at it (task 37cd7081): the tick's tip says it will offer to stop Claude.
        ClaudeWorking = TaskBoard.ClaudeStillWorking(task),
        // An open thread lists the task's subtasks (follow-up: an open parent showed none of them).
        Subtasks = open
            // EVERY SUBTASK CARRIES ITS WHOLE ROW (task 249a1e8a; Petre, on a screenshot of a parent's thread where the
            // subtask was a bare "↳ title · status" entry: "subtasks should get all buttons like the parents do; they are
            // regular tasks, upgrade them"). The parent's thread draws it as a full line from this row, with the toggles,
            // the comments, Open Claude and the menu's buttons a line has, and its thread, filled in while it is open, in
            // place under that line (task 0eb943d8, Petre: "let subtasks stay where they are, expand their current
            // place"). Nested, so the line shows "↳" without the parent's name: the parent is the thread around it.
            // Subtasks are one level deep and the subtask's row is built with inPlace off, so this never nests further.
            ? tasks.Board.Family(task.Id).Map(f => f.Subtasks.Select(c => new SubtaskView(c.Id, FloatingBar.FirstLine(c.Title), c.Status, c.StatusLine)
                    {
                        Row = inPlace && c.Id != task.Id ? RowOf(c, open: expanded.Contains(c.Id), inPlace: false) with { Nested = true } : null,
                        // Its tick in the thread (task 0eb943d8, "how do i close subtasks?"): Undo while its close can be
                        // undone, and the tip a line's tick would have.
                        Closing = pendingCloses.ContainsKey(c.Id),
                        // For a drag of its entry out of the thread (task f64d06ae): the drop rule asks for it.
                        Quadrant = c.Quadrant,
                        TickTip = TaskViews.TickTip(c.IsClaudes, tasks.Board.OpenSubtaskCount(c.Id), TaskBoard.ClaudeStillWorking(c)),
                    }).ToArray())
                .GetValueOrDefault([])
            : [],
        // TASK LINKS (docs/superpowers/specs/2026-11-25-task-links-design.md): the original a closed duplicate names, for
        // its status words, and while the thread is open the links it lists: what it duplicates, its duplicates and its
        // related tasks.
        DuplicateOfTitle = tasks.Board.DuplicateTarget(task).Map(o => (string?)FloatingBar.FirstLine(o.Title)).GetValueOrDefault(),
        Links = expanded.Contains(task.Id) ? LinksOf(task) : [],
    };

    // A task's links as its open thread lists them: the task it duplicates first, then its duplicates, then related tasks.
    IReadOnlyList<TaskLinkView> LinksOf(TaskItem task) =>
        tasks.Board.Links(task.Id).Match(links =>
            links.DuplicateOf.ToList().Select(o => LinkView(o, TaskLinkKind.DuplicateOf))
                .Concat(links.Duplicates.Select(d => LinkView(d, TaskLinkKind.Duplicate)))
                .Concat(links.Related.Select(r => LinkView(r, TaskLinkKind.Related)))
                .ToArray(),
            () => Array.Empty<TaskLinkView>());

    static TaskLinkView LinkView(TaskItem task, TaskLinkKind kind) =>
        new(task.Id, FloatingBar.FirstLine(task.Title), task.IsDuplicate ? TaskViews.DuplicateName : TaskViews.StatusName(task.Status),
            !task.IsOpen || task.Status == TaskStatus.Done, kind);

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
        // Four authors: a session ("Claude"), the manager ("Manager"), Deskbar itself (its resume, stop and failure lines) and Petre ("you").
        // Only his own can be edited, so Deskbar's line no longer offers him an Edit as if he had written it.
        return new TaskCommentView(comment.Id, comment.BySession ? "Claude" : comment.ByDeskbar ? "Deskbar" : comment.ByManager ? "Manager" : "you", comment.BySession,
            today ? at.ToString("HH:mm") : at.ToString("d MMM HH:mm"), comment.Markdown)
        {
            Settled = comment.Settled.HasValue,
            Editable = comment.SessionId.HasNoValue,
        };
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

    // "Workspace: all" or "Workspace: Sparrow". Under the "Current workspace" checkbox it names the workspace in front, so
    // the label says which tasks show now; the ticked box beside it says they will change with the next switch.
    string WorkspaceFilterLabel(TaskWorkspaceFilter shown) =>
        shown.Any ? "Workspace: all" : $"Workspace: {PlaceOf(shown.Workspace).Name}";

    static string FilterName(TaskFilter shown) => shown switch
    {
        TaskFilter.Open => "Open",
        TaskFilter.WaitingOnYou => TaskViews.StatusName(TaskStatus.WaitingOnYou),
        TaskFilter.Done => TaskViews.StatusName(TaskStatus.Done),
        TaskFilter.InProgress => TaskViews.StatusName(TaskStatus.InProgress),
        TaskFilter.Backlog => TaskViews.StatusName(TaskStatus.Backlog),
        TaskFilter.Snoozed => "Snoozed",
        _ => TaskViews.StatusName(TaskStatus.Closed),
    };

    static string EmptyText(TaskFilter shown) => shown switch
    {
        TaskFilter.Open => "Nothing open. \"+ Add\" writes a note.",
        TaskFilter.WaitingOnYou => "Nothing is waiting on you.",
        TaskFilter.Done => "Nothing done and waiting to be closed.",
        TaskFilter.InProgress => "Nothing in progress.",
        TaskFilter.Backlog => "Nothing in the backlog.",
        TaskFilter.Snoozed => "Nothing snoozed. Every task is back in view.",
        _ => "Nothing closed.",
    };

    // THE PROBLEM FOLLOWS THE LAST ACTION: its refusal, or nothing once one works. WHERE it shows depends on what the
    // action was on (task 41f08dba; Petre, of "Close its open subtask first" at the top of the panel: "show error on the
    // task, not at the top of the bar"):
    //   - on one task (`task`, a task that still exists): on that task's line in the panel, under its title, and on that
    //     task's window's problem line. Other task windows show nothing, since the refusal is not theirs.
    //   - on no task (the add box's note, the manager, the Matrix switch), or on a task whose line the panel is not
    //     showing (filtered out, folded under its parent, the Stats view): on the panel's problem line at the top, as
    //     every refusal did before, so it is never shown nowhere. Task windows show it too in that case, as before.
    // A refusal on the line redraws the panel when it comes or goes, since a refusal changes nothing on the board and so
    // pulses no redraw of its own.
    Result Report(Result result, Guid? task = null)
    {
        var refused = result.IsFailure && task is { } id && tasks.Board.Tasks.Any(t => t.Id == id)
            ? ((Guid Task, string Text)?)(id, result.Error)
            : null;
        // On the line only where the panel draws that line; otherwise the top line, so the refusal is seen somewhere. A
        // closed panel draws nothing yet: the refusal waits on the line for nothing (OpenFresh clears it) and shows in the
        // task's window, where the action came from.
        var onLine = refused is { } r && (!window.IsOpen || Draws(View(), r.Task));
        var before = lineProblem;
        lineProblem = onLine ? refused : null;
        if (before != lineProblem) Refresh();
        var top = result.IsFailure && !onLine ? result.Error : null;
        window.ShowProblem(top);
        taskWindows.ToList().ForEach(pair => pair.Value.ShowProblem(top ?? (refused is { } own && own.Task == pair.Key ? own.Text : null)));
        return result;
    }

    // The task a comment is on, for a comment's Edit and Settle: their refusal shows on that task's line. Null for a
    // comment no task has (gone meanwhile), whose refusal goes to the top line.
    Guid? TaskOfComment(Guid comment) =>
        tasks.Board.Tasks.FirstOrDefault(t => t.Comments.Any(c => c.Id == comment))?.Id;

    // Whether the panel's view draws the task's line: a line of the List or of the Matrix (whichever is shown), or a
    // subtask drawn in place inside an open thread (SubtaskView.Row). The Stats view draws no line.
    static bool Draws(TasksView view, Guid id) =>
        view.Stats is null
        && (view.Matrix ? view.Cells.SelectMany(c => c.Rows) : view.Groups.SelectMany(g => g.Rows)).Any(row => RowDraws(row, id));

    static bool RowDraws(TaskRowView row, Guid id) =>
        row.Id == id || row.Subtasks.Any(sub => sub.Row is { } inPlace && RowDraws(inPlace, id));
}

// What the window draws, resolved here so the window never looks anything up.

/// <summary>A filter chip: which filter, its words with the count ("Open 9"), whether it is on, and whether it asks
/// something of Petre (Waiting on you with anything in it, drawn orange).</summary>
internal sealed record TaskFilterChip(TaskFilter Filter, string Label, bool On, bool Asks);

/// <summary>A workspace as a chip or a menu item: its id (none for "none"), its name and its lane colour.</summary>
internal sealed record TaskPlaceChoice(Maybe<Guid> Workspace, string Name, Brush? Colour);

/// <summary>One comment: "Claude" or "you", its time as shown, and its markdown.</summary>
internal sealed record TaskCommentView(Guid Id, string Author, bool ByClaude, string When, string Markdown)
{
    /// <summary>Settled (Ruling 62): kept, but folded away behind the thread's "N settled comments" line.</summary>
    public bool Settled { get; init; }

    /// <summary>Petre's own comment, the only kind with an Edit button (Ruling 58); not a session's, not Deskbar's.</summary>
    public bool Editable { get; init; }
}

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

    /// <summary>When a snoozed task comes back (task 6da8d7eb), or null while it is awake: the snooze button's lit state.</summary>
    public DateTimeOffset? SnoozedUntil { get; init; }

    /// <summary>
    /// The pull requests the task's sessions opened (task e7aa49b1, TaskItem.PullRequests), oldest first: the task window's
    /// "Pull requests" section, one link each.
    /// </summary>
    public IReadOnlyList<PullRequestLink> PullRequests { get; init; } = [];

    /// <summary>
    /// Whether a Done task's change runs in this Deskbar yet (task 8470d176, RunningChanges): Shipping draws the hollow
    /// ring and the outlined "Merged" chip, and the Note goes in the line's tooltip. None for anything it cannot tell.
    /// </summary>
    public TaskDelivery Delivery { get; init; } = TaskDelivery.None;

    /// <summary>
    /// Whether the line's menu offers "Mark deployed" (task 8470d176, phase 2; TaskService.CanMarkDeployed): the change is
    /// merged, and a recorded pull request outside this build's repository has no deploy reported yet.
    /// </summary>
    public bool CanMarkDeployed { get; init; }

    public Quadrant Quadrant => QuadrantOf.Flags(Important, Urgent);

    /// <summary>
    /// The goal the task is linked to (goals plan, Task 6), for the "Goal" menu: its check mark, and the goal itself when
    /// the menu would not otherwise list it (review M6). Null for no goal. Not drawn on the line, so not in the window's
    /// redraw signature.
    /// </summary>
    public GoalChoice? Goal { get; init; }

    /// <summary>
    /// The task's sessions as its open thread lists them (Task 9): newest first, each working, idle, ended or expired.
    /// Empty while the thread is closed.
    /// </summary>
    public IReadOnlyList<SessionView> SessionList { get; init; } = [];

    /// <summary>
    /// Whether one of the listed sessions is still running, working or idle (task 9b6275bc; Petre, on a thread that
    /// showed an idle session's Open and the start button under it: "do i need start claude on this when there's an
    /// open session?"). While one is, the task's window (TaskDetailWindow) leaves "Start Claude on this" out: the
    /// session's Open is the way in, and a second session beside a running one is rarely meant (`deskbar task start`
    /// still does it). Once every session has ended or expired the window's button is back, for a fresh start. The
    /// thread in the tasks panel no longer reads this: it goes by HasSessionToOpen below, which keeps the start button
    /// away for an ended session too (task 5e02e99f).
    /// </summary>
    public bool HasOpenSession => SessionList.Any(s => s.State is SessionState.Working or SessionState.Idle);

    /// <summary>
    /// Whether "Open Claude" has a session it can open (task 5e02e99f): the session it goes to, a running one first and
    /// else the newest, is anything but expired. An ended one counts, since Open Claude resumes it. False for a task with
    /// no session and for one whose session to go to has lost its transcript, and those are the two cases where the
    /// thread offers "Start Claude on this" instead: the thread's row of buttons has ONE Claude button, never both
    /// (TasksWindow.ThreadActions).
    /// </summary>
    //
    // NEARLY TaskViews.SessionToGoTo's order, not the same one. That sorts by the hooks' word (TaskSession.IsLive), this
    // by the listed state, which is all a SessionView carries. The two agree once TaskBoard.Reconcile has ended the
    // sessions the list no longer shows, which every readable poll does. In the one poll between, on a task with two or
    // more sessions, the button can be judged on a different session than the click goes to; each session line has its
    // own Open then (two or more sessions), so there is a way round, and the next poll puts it right.
    public bool HasSessionToOpen =>
        (SessionList.FirstOrDefault(s => s.State is SessionState.Working or SessionState.Idle) ?? SessionList.FirstOrDefault())
            is { State: not SessionState.Expired };

    /// <summary>
    /// How many of the task's live sessions Claude is working in right now (tasks panel plan, Task 8): the first that many
    /// live squares on the line wear a halo. From the board's `workingOf`, 0 with none or before the first agents list.
    /// </summary>
    public int WorkingCount { get; init; }

    /// <summary>
    /// Subtasks (Petre's pick A): the parent's title when the task is a subtask, or null; drawn before the title ("↳ Parent
    /// › ", "↳ " on a slim line). In the redraw signature, so renaming the parent redraws its subtasks.
    /// </summary>
    public string? ParentTitle { get; init; }

    /// <summary>
    /// The parent's id when the task is a subtask, or null (task 54b39c4c): what the thread's "Subtask of" link, the
    /// menu's "Open parent" and the task window's Parent section open. Null exactly when ParentTitle is.
    /// </summary>
    public Guid? ParentId { get; init; }

    /// <summary>How many open subtasks the task has, drawn after its title ("· 2 subtasks"); 0 for none.</summary>
    public int SubtaskCount { get; init; }

    /// <summary>Whether Claude is still working on the task (TaskBoard.ClaudeStillWorking): its tick offers to stop it.</summary>
    public bool ClaudeWorking { get; init; }

    /// <summary>Drawn right under its parent (TaskViews.UnderParents), so the arrow needs no parent's name.</summary>
    public bool Nested { get; init; }

    /// <summary>The task's subtasks while its thread is open, for the thread's list; empty while closed.</summary>
    public IReadOnlyList<SubtaskView> Subtasks { get; init; } = [];

    /// <summary>The first line of the task a closed duplicate names (task links), or null: its status reads "Duplicate".</summary>
    public string? DuplicateOfTitle { get; init; }

    /// <summary>The task's links while its thread is open (task links), for the thread's list; empty while closed.</summary>
    public IReadOnlyList<TaskLinkView> Links { get; init; } = [];

    /// <summary>
    /// Why the last action on this task was refused (task 41f08dba, TasksBoard.lineProblem), or null: drawn in orange
    /// right under the title, so the refusal sits on the line it is about rather than at the top of the panel.
    /// </summary>
    public string? Problem { get; init; }
}

/// <summary>How a linked task stands to the task whose thread lists it (task links).</summary>
internal enum TaskLinkKind { DuplicateOf, Duplicate, Related }

/// <summary>
/// A linked task as a thread or the link picker lists it (task links): id, first line of its title, its status in words
/// ("Duplicate" for a closed duplicate), whether it is finished (done or closed, drawn dim) and how it is linked.
/// </summary>
internal sealed record TaskLinkView(Guid Id, string Title, string Status, bool Finished, TaskLinkKind Kind);

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
    /// <summary>
    /// Whether the window shows the Matrix (Task 12) rather than the List; its four cells. Only while the panel is wide
    /// enough for it (MatrixAllowed), whatever AppState.TasksMatrix says.
    /// </summary>
    public bool Matrix { get; init; }

    /// <summary>Whether the panel is wide enough for the Matrix (TasksPanelLayout.MatrixAllowed): the List / Matrix switch shows only then.</summary>
    public bool MatrixAllowed { get; init; } = true;

    /// <summary>Whether the panel is narrow enough for slim lines (TasksPanelLayout.Slim; tasks panel plan, Task 8).</summary>
    public bool Slim { get; init; }

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

    /// <summary>
    /// The search in force (Task 7), "" for none. The window never writes it back into its box, which is the box's own
    /// text: a redraw while Petre types must not touch the box (TasksWindow, "the search box").
    /// </summary>
    public string Search { get; init; } = "";

    /// <summary>Whether the "Claude working" chip is on, and how many tasks it would show under every other filter.</summary>
    public bool ClaudeWorking { get; init; }

    public int ClaudeWorkingCount { get; init; }

    /// <summary>Whether the "Subtasks" checkbox is ticked (task 173ef268): subtasks drawn under their parent, not folded.</summary>
    public bool ShowsSubtasks { get; init; } = true;

    /// <summary>The manager's open question (task 61e4d143), drawn as the violet line above the list; null for none.</summary>
    public ManagerAsk? ManagerAsks { get; init; }
    /// <summary>
    /// The Stats view's numbers while it shows in place of the List or the Matrix (task 521d2b07, TasksWindow.Stats.cs),
    /// null otherwise.
    /// </summary>
    public TaskStatsView? Stats { get; init; }

    /// <summary>The full line's resizable column widths (task 3df2d28f), as saved with the panel or the defaults.</summary>
    public TaskColumnWidths Columns { get; init; } = TaskColumnWidths.Default;
}

/// <summary>
/// Everything the window can ask for, handed over by the controller once. The window has no events of its own: a click
/// calls one of these. Rename, Comment and AddNote answer whether it worked, so the window knows whether to clear its
/// box; every refusal is shown by the controller on the problem line. ClosePanel is the toolbar's × and Alt+F4 (the
/// panel's Hide); ResizePanel is the grip's drag end, with the new width in DIPs (saved, and the panel re-docked at it).
/// They replaced Remember, which kept the floating window's place in AppState.TasksWindow (tasks panel plan, Task 6).
/// Search is the search box's text on every keystroke and FilterClaudeWorking the "Claude working" chip (Task 7).
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
    Action<Guid, bool> SettleComment,
    Action<Guid, Maybe<Guid>> MoveTo,
    Action<Guid> Delete,
    Func<Guid, string, bool> Comment,
    // The add box's note: title, workspace, flags, and the writer of its description when pictures were pasted (none
    // for a plain note); whether it worked.
    Func<string, Maybe<Guid>, Quadrant, Func<Guid, Result<string>>?, bool> AddNote,
    Action<Guid, TaskFlag, bool> SetFlag,
    Action<Guid, Quadrant, TaskPlace> Place,
    Action<bool> ShowMatrix,
    Action<TaskFilter> Filter,
    Action<TaskWorkspaceFilter> FilterWorkspace,
    Action ClosePanel,
    Action<double> ResizePanel,
    Action<Guid, Maybe<Guid>> SetGoal,
    Action<TaskGoalFilter> FilterGoal,
    Action<Guid, Assignee> Assign,
    Action<TaskAssigneeFilter> FilterAssignee,
    Action<string> Search,
    Action<bool> FilterClaudeWorking)
{
    /// <summary>"Start Claude on this" (Task 9): the line menu's item and the thread's button. None, neither shows.</summary>
    public Action<Guid>? StartClaude { get; init; }

    /// <summary>The manager line's click (task 61e4d143): opens the manager session.</summary>
    public Action? OpenManager { get; init; }

    /// <summary>The stop menu's "Stop Claude and close" (task 37cd7081): TasksBoard.Close with stopClaude.</summary>
    public Func<Guid, Result>? StopClaudeAndClose { get; init; }

    /// <summary>A session's Open in the thread (Task 9): the task's id and the session's. None, no Open buttons.</summary>
    public Action<Guid, string>? OpenSession { get; init; }

    /// <summary>
    /// "Add subtask…" on a line (subtasks spec, Petre's pick A): the task a subtask added from that line goes under, the
    /// line's own or its parent for a subtask's line (TaskBoard.SubtaskParentFor), or null for an unknown line. None, the
    /// menu has no "Add subtask…".
    /// </summary>
    public Func<Guid, SubtaskParentView?>? SubtaskParent { get; init; }

    /// <summary>
    /// The box in a thread (task 0eb943d8): adds a subtask under the line's task (or its parent, for a subtask's line) with
    /// that title and, when given, that description (pictures included), then opens it where the thread was
    /// (TasksBoard.AddSubtaskInThread); whether it worked.
    /// </summary>
    /// <remarks>The last flag says the box was in a subtask's thread drawn inside its parent's open thread (task 249a1e8a),
    /// where the new sibling opens in place as well.</remarks>
    public Func<Guid, string, Func<Guid, Result<string>>?, bool, bool>? AddSubtaskHere { get; init; }

    /// <summary>
    /// Shows one task: picked, its thread open, scrolled into view, past any filter that hides it
    /// (TasksBoard.ShowTaskPastFilters). A subtask in a thread and the task window's "Show in the tasks panel" use it.
    /// </summary>
    public Action<Guid>? ShowTask { get; init; }

    /// <summary>Puts a task under another, or (none) makes it a plain task again (TaskService.SetParent); whether it worked.</summary>
    public Func<Guid, Maybe<Guid>, bool>? SetParent { get; init; }

    /// <summary>
    /// Drag to nest: the tasks a dragged task could be dropped into as their subtask (TaskBoard.ParentCandidates, every
    /// one, not the menu's first fifteen). None, a drag only reorders.
    /// </summary>
    public Func<Guid, IReadOnlySet<Guid>>? NestTargets { get; init; }

    /// <summary>Snoozes a task for one of the menu's lengths (task 6da8d7eb). None, no snooze button and no menu item.</summary>
    public Action<Guid, SnoozeChoice>? Snooze { get; init; }

    /// <summary>"Wake now" on a snoozed task.</summary>
    public Action<Guid>? Wake { get; init; }

    /// <summary>The "Subtasks" checkbox (task 173ef268): true draws subtasks under their parent, false folds them. None, no checkbox.</summary>
    public Action<bool>? ShowSubtasks { get; init; }

    /// <summary>
    /// "Open in window" (task dca4ebeb): the task in a window of its own (TasksBoard.OpenWindow). None, the item and the
    /// thread's button do not show.
    /// </summary>
    public Action<Guid>? OpenWindow { get; init; }
    /// <summary>The toolbar's Stats switch (task 521d2b07): the Stats view in place of the List or the Matrix (TasksBoard.ShowStats).</summary>
    public Action? ShowStats { get; init; }

    /// <summary>A task's line in the Stats view: leave Stats and show that task (TasksBoard.OpenFromStats).</summary>
    public Action<Guid>? OpenFromStats { get; init; }

    /// <summary>
    /// A subtask's way to its parent, given the parent's id (task 54b39c4c): the "Subtask of" link in its thread and
    /// "Open parent" in its line's menu (TasksBoard.OpenParent). None, neither shows.
    /// </summary>
    public Action<Guid>? OpenParent { get; init; }

    /// <summary>
    /// A click on a link in a task's text (its question or status line, description, comments, pull requests), given the
    /// task's id and the address (task 26f15d70): TasksBoard.OpenLink, which goes to the task's workspace first so the
    /// page opens there. None (a window built without a controller), the link opens on the desktop in front, as before.
    /// </summary>
    public Action<Guid, Uri>? OpenLink { get; init; }

    /// <summary>
    /// The header's divider drag end (task 3df2d28f): the full line's new column widths, saved with the panel. None, the
    /// dividers still resize the lines on show but nothing is kept.
    /// </summary>
    public Action<TaskColumnWidths>? ResizeColumns { get; init; }

    /// <summary>
    /// Task links: the tasks the picker offers for a task and the typed words, for a duplicate (true) or a related link
    /// (TaskBoard.LinkCandidates, the first few). None, the menu has no "Duplicate of…" or "Related to…".
    /// </summary>
    public Func<Guid, string, bool, IReadOnlyList<TaskLinkView>>? LinkCandidates { get; init; }

    /// <summary>Closes the first task as a duplicate of the second (TaskService.MarkDuplicate); whether it worked.</summary>
    public Func<Guid, Guid, bool>? MarkDuplicate { get; init; }

    /// <summary>Links two tasks as related (TaskService.Relate); whether it worked.</summary>
    public Func<Guid, Guid, bool>? Relate { get; init; }

    /// <summary>Removes the related link between two tasks, a related line's cross.</summary>
    public Action<Guid, Guid>? Unrelate { get; init; }

    /// <summary>
    /// "Mark deployed" on a line whose change is merged in another repository but not reported running (task 8470d176,
    /// phase 2; TaskRowView.CanMarkDeployed): TaskService.MarkDeployed with TaskService.MarkedByHand. None, no item.
    /// </summary>
    public Action<Guid>? MarkDeployed { get; init; }
}

/// <summary>
/// What a task window draws (task dca4ebeb): the task's line with its thread open, the workspaces for its "Move to…"
/// menu (WorkspaceChipMenu's entries) and the time "now" is, for the task's age.
/// </summary>
internal sealed record TaskDetailView(TaskRowView Row, IReadOnlyList<WorkspaceChipMenu.Entry> Places, DateTimeOffset Now);

/// <summary>A subtask as its parent's open thread lists it: id, title (first line), status and status line.</summary>
internal sealed record SubtaskView(Guid Id, string Title, TaskStatus Status, string StatusLine)
{
    /// <summary>
    /// The subtask's own row (task 249a1e8a), its thread filled in while that thread is open (task 0eb943d8): the parent's
    /// thread draws it as a full line, and its thread in place under that line. Null when the subtask has a line of its
    /// own elsewhere in the view (HasLine), and for a hand-edited file where a task is its own parent.
    /// </summary>
    public TaskRowView? Row { get; init; }

    /// <summary>
    /// Whether the subtask has a line of its own in the view drawn now (a waiting one in the pinned block, a closed one,
    /// any in the Matrix): its entry then goes to that line rather than opening it in place.
    /// </summary>
    public bool HasLine { get; init; }

    /// <summary>Whether the subtask was just closed and can still be undone: its tick in the thread shows Undo.</summary>
    public bool Closing { get; init; }

    /// <summary>The subtask's own quadrant, which a drag of its entry out of the thread drops by (task f64d06ae).</summary>
    public Quadrant Quadrant { get; init; } = Quadrant.IV;

    /// <summary>What its tick in the thread says on hover, as a line's tick would (TaskViews.TickTip).</summary>
    public string TickTip { get; init; } = "";
}

/// <summary>The parent "Add subtask…" puts a new task under: its id, its title for the chip and its workspace.</summary>
internal sealed record SubtaskParentView(Guid Id, string Title, Maybe<Guid> Workspace);
