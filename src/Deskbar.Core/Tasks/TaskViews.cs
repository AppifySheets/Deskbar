using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// THE TASKS WINDOW'S GROUPING AND FILTERS, pure. Design: docs/superpowers/specs/2026-11-22-tasks-board-design.md,
// "The window", with Task 12's order (progress.md Rulings 25 and 28): the window's List view is grouped as the bar is,
// Waiting on you first (the questions, then the Done tasks, each in its own order, TaskBoard.Pinned), then Covey's quadrants I to IV, each in Petre's manual order
// (TaskBoard.Group), each captioned in Covey's words. Filters by status (Closed among them) and by workspace.
//
// CLAUDE WORKING SITS RIGHT AFTER WAITING ON YOU (task 93fa0b9b, 2026-11-25, Petre: "tasks that have a working claude
// session should sit after waiting on me tasks"). A task Claude is at work on (AtWork: open, not Done, not waiting on
// Petre, a session live on it, the same test as TaskBoard.ClaudeStillWorking) leaves its quadrant's group for a pinned
// "Claude working" block between the Waiting on you block (questions, then Done) and quadrant I, in quadrant order and,
// inside a quadrant, Petre's manual order, so the block keeps his priorities. The Matrix does the same inside each cell:
// the pinned ones first, then working, then the rest. Live, not busy: `claude agents --json` says busy only while a turn
// runs, so a busy test would move a line in and out of the block every time Claude paused for a tool or a background wait.
//
// WORKING, THEN IDLE, INSIDE THAT BLOCK (task 10b49554, 2026-11-26, Petre picked option B of the mockup: "separate claude
// sessions that are actively working from the ones that are idle"). Live still decides who is in the block; whether a
// live session is mid-turn decides where in it. Given a `busy` test (the tasks panel passes ClaudeActivity's, which holds
// a session working through one quiet poll so a line does not flicker), the block is drawn as two groups: ClaudeWorking,
// the tasks with a session working, then ClaudeIdle under a divider, the rest, each in quadrant then manual order. Without
// a test (the search by id's Everywhere, older callers) every live task is in ClaudeWorking, as before.
//
// A status filter keeps the groups: "In progress" shows the quadrants with only their tasks in progress, in the
// same order, so a line sits in the same group whatever the filter. The pinned block holds the Done tasks under the
// questions (task eefb986a, TaskStatusPinning), so "Waiting on you" is that block's questions and "Done" its Done
// tasks, each still under the block's caption. The Claude working block is narrowed the same way.
// Closed tasks have no order of their own (closing takes a task out of it): they are one group, the latest closed
// first, since closing stamps UpdatedAt, the same order `task list --closed` prints.
//
// The window's controller (Deskbar.App.TasksBoard) draws what this returns. It puts a task closed in the window back
// under its earlier status for the six seconds of its Undo before calling here, so a line just closed stays in place
// struck through (the bar's Ruling 10); that substitution is the controller's, and this stays a plain function of the
// board.

/// <summary>
/// The window's status filters. <see cref="Open"/> is the default view: every task but the closed ones, grouped.
/// The other five show one status alone. <see cref="Snoozed"/> shows the snoozed tasks, which no other filter shows.
/// </summary>
//
// SNOOZED (task 6da8d7eb): a snoozed task is out of view, so every filter leaves it out (the pinned Waiting on you block,
// the quadrants, the Matrix and every chip's count) except its own, which lists the open snoozed tasks in one group, the
// first to come back on top. Appended last, so no earlier value moves.
public enum TaskFilter { Open, WaitingOnYou, Done, InProgress, Backlog, Closed, Snoozed }

/// <summary>
/// The window's workspace filter: every task (<see cref="All"/>), or only the tasks on one workspace, or only the tasks
/// on none (<see cref="On"/> with no workspace), or only the tasks on whichever workspace is in front
/// (<see cref="Current"/>).
/// </summary>
//
// CURRENT WORKSPACE (task ed1a01fc, 2026-11-25, Petre: "when i change workspace, it should only show me current
// workspace tasks, when selected"). Current is a choice that FOLLOWS: it names no workspace of its own, and whoever
// draws the window turns it into the workspace in front at that moment with Resolved, every time it draws. So a switch
// of desktop narrows the list to the new workspace without anyone picking it again. On a desktop with no name the
// workspace in front is none, and Current then shows the tasks on no workspace, the same as picking "none". Admits on an
// unresolved Current would read it as that "none" filter too; callers resolve it first.
public sealed record TaskWorkspaceFilter(bool Any, Maybe<Guid> Workspace)
{
    public static readonly TaskWorkspaceFilter All = new(true, Maybe<Guid>.None);

    /// <summary>The tasks on whichever workspace is in front, resolved again at every draw (see the comment above).</summary>
    public static readonly TaskWorkspaceFilter Current = new(false, Maybe<Guid>.None) { FollowsCurrent = true };

    /// <summary>Whether this is <see cref="Current"/>: a filter that follows the workspace in front.</summary>
    public bool FollowsCurrent { get; private init; }

    public static TaskWorkspaceFilter On(Maybe<Guid> workspace) => new(false, workspace);

    /// <summary>
    /// The filter as it applies now: <see cref="Current"/> becomes the filter on <paramref name="current"/>, the workspace
    /// in front (none on a desktop with no name); every other filter is itself.
    /// </summary>
    public TaskWorkspaceFilter Resolved(Maybe<Guid> current) => FollowsCurrent ? On(current) : this;

    public bool Admits(TaskItem task) =>
        Any || (Workspace.HasValue
            ? task.WorkspaceId.HasValue && task.WorkspaceId.Value == Workspace.Value
            : task.WorkspaceId.HasNoValue);
}

/// <summary>
/// The window's "Assigned" filter (Ruling 57): every task (<see cref="All"/>), or only the tasks assigned to Petre or to
/// Claude. It narrows the List's groups and the Matrix's cells the way the workspace filter does.
/// </summary>
public sealed record TaskAssigneeFilter(Maybe<AssigneeKind> Kind)
{
    public static readonly TaskAssigneeFilter All = new(Maybe<AssigneeKind>.None);
    public static readonly TaskAssigneeFilter Me = new(AssigneeKind.Me);
    public static readonly TaskAssigneeFilter Claude = new(AssigneeKind.Claude);

    public bool Admits(TaskItem task) => Kind.HasNoValue || task.Assignee.Kind == Kind.Value;
}

/// <summary>
/// Which group of the window a task is drawn in: the pinned Waiting on you block, the pinned Claude working block under it
/// (task 93fa0b9b), one of Covey's quadrants (named as the book numbers them), the closed tasks, or the snoozed ones (the
/// Snoozed filter's one group).
/// </summary>
// ClaudeWorking is appended last so no earlier value moves; the order the groups are drawn in is Grouped's, not the enum's.
// ClaudeIdle (task 10b49554) is the idle part of that block, drawn right after ClaudeWorking under a divider.
public enum TaskGroupKind { WaitingOnYou, I, II, III, IV, Closed, Snoozed, ClaudeWorking, ClaudeIdle }

/// <summary>One group of the window: which group, and its tasks in the order they are drawn.</summary>
public sealed record TaskGroup(TaskGroupKind Kind, IReadOnlyList<TaskItem> Tasks);

/// <summary>One cell of the window's Matrix: its quadrant, and its tasks in the order they are drawn.</summary>
public sealed record TaskCell(Quadrant Quadrant, IReadOnlyList<TaskItem> Tasks);

// Both of the window's filters at once, so the helpers below take one thing to ask.
sealed record Admit(TaskWorkspaceFilter Workspace, TaskAssigneeFilter Assignee)
{
    public bool Admits(TaskItem task) => Workspace.Admits(task) && Assignee.Admits(task);
}

/// <summary>
/// Where one of a task's Claude sessions stands, as its thread shows it (TaskViews.SessionList): working or idle while
/// `claude agents --json` lists it, ended once it does not, expired once its transcript is gone too (Claude Code deletes
/// transcripts after 30 days by default), which is the one state with no Open button, since nothing is left to resume.
/// </summary>
public enum SessionState { Working, Idle, Ended, Expired }

/// <summary>One line of a task's session list: the session's full id, when it joined the task, and where it stands.</summary>
public sealed record SessionView(string Id, DateTimeOffset Joined, SessionState State);

public static class TaskViews
{
    static readonly IReadOnlyList<(TaskGroupKind Kind, Quadrant Quadrant)> Quadrants =
        [(TaskGroupKind.I, Quadrant.I), (TaskGroupKind.II, Quadrant.II), (TaskGroupKind.III, Quadrant.III), (TaskGroupKind.IV, Quadrant.IV)];

    /// <summary>
    /// The groups the window draws for a filter, empty groups left out (see the header): Waiting on you in its order
    /// (TaskBoard.Pinned), each quadrant in its manual order, the closed tasks the latest closed first.
    /// </summary>
    /// <param name="busy">
    /// Whether a session of the task is working right now (task 10b49554): the Claude working block's tasks it says no to
    /// are drawn in ClaudeIdle after it. None: every task of the block is in ClaudeWorking.
    /// </param>
    /// <param name="hollow">
    /// Whether a Done task's tick is the hollow ring (task a1debf34, TaskBoard.Pinned): the Done tasks show the full discs
    /// first, then the rings. None: the Done tasks in their pin order alone.
    /// </param>
    public static IReadOnlyList<TaskGroup> Grouped(TaskBoard board, TaskFilter filter, TaskWorkspaceFilter workspaceFilter, TaskAssigneeFilter? who = null,
        Func<TaskItem, bool>? busy = null, Func<TaskItem, bool>? hollow = null) =>
        Grouped(board, filter, new Admit(workspaceFilter, who ?? TaskAssigneeFilter.All), busy, hollow);

    static IReadOnlyList<TaskGroup> Grouped(TaskBoard board, TaskFilter filter, Admit where, Func<TaskItem, bool>? busy = null,
        Func<TaskItem, bool>? hollow = null) =>
        (filter switch
        {
            TaskFilter.Closed => [new TaskGroup(TaskGroupKind.Closed,
                board.Tasks.Where(t => t.Status == TaskStatus.Closed && where.Admits(t)).OrderByDescending(t => t.UpdatedAt).ToArray())],
            TaskFilter.Snoozed => [new TaskGroup(TaskGroupKind.Snoozed,
                board.Tasks.Where(t => t.IsOpen && t.IsSnoozed && where.Admits(t)).OrderBy(t => t.SnoozedUntil.Value).ThenBy(t => t.Id).ToArray())],
            // Waiting on you (questions, then Done), then Claude working (task 93fa0b9b) with its idle part (task 10b49554),
            // then the quadrants without the tasks Claude is at work on. A status filter narrows them all alike; Claude
            // working never holds a question or a Done task, so under those two filters it is empty and left out.
            TaskFilter.Open => Pinned(board, where, _ => true, busy, hollow),
            _ => Pinned(board, where, t => StatusesOf(filter).Contains(t.Status), busy, hollow),
        })
        .Where(g => g.Tasks.Count > 0)
        .ToArray();

    /// <summary>
    /// Every task on the board in the section it belongs to, whatever its status: the open view's groups (Waiting on you,
    /// then the quadrants), then Snoozed, then Closed, empty groups left out. No filter applies. The search by id's view
    /// (TaskSearch.Found, task 6e4dc116): a task found by its id is shown wherever it is, closed included.
    /// </summary>
    // Its Done tasks in the List's order, full discs before rings (task a1debf34, hollow as Grouped takes it).
    public static IReadOnlyList<TaskGroup> Everywhere(TaskBoard board, Func<TaskItem, bool>? hollow = null) =>
        new[] { TaskFilter.Open, TaskFilter.Snoozed, TaskFilter.Closed }
            .SelectMany(filter => Grouped(board, filter, TaskWorkspaceFilter.All, hollow: hollow))
            .ToArray();

    /// <summary>
    /// The Matrix (Ruling 28): Covey's four cells, I to IV, always all four. Each lists its quadrant's open tasks in
    /// their order, the pinned ones first (waiting on Petre, then Done; TaskBoard.Pinned), on top of their own
    /// quadrant's cell, the questions drawn orange as everywhere, then the ones Claude is at work on (task 93fa0b9b),
    /// then the rest. The status filter narrows every cell as it narrows the
    /// List; the closed tasks have no quadrant order, so under "Closed" every cell is empty, and a snoozed task is out of
    /// view, so under "Snoozed" too (the List shows them) and under every other filter it is left out.
    /// </summary>
    // Not split into working and idle (task 10b49554): a compact Matrix line shows no sessions, so a line moving between
    // the two would jump inside its cell with nothing on screen to say why.
    // A cell's Done tasks show the full discs first, then the rings, as the List does (task a1debf34, hollow).
    public static IReadOnlyList<TaskCell> Matrix(TaskBoard board, TaskFilter filter, TaskWorkspaceFilter workspaceFilter, TaskAssigneeFilter? who = null,
        Func<TaskItem, bool>? hollow = null) =>
        Matrix(board, filter, new Admit(workspaceFilter, who ?? TaskAssigneeFilter.All), hollow);

    static IReadOnlyList<TaskCell> Matrix(TaskBoard board, TaskFilter filter, Admit where, Func<TaskItem, bool>? hollow) =>
        Quadrants.Select(q => new TaskCell(q.Quadrant, filter is TaskFilter.Closed or TaskFilter.Snoozed ? []
            : board.Pinned(hollow).Where(t => t.Quadrant == q.Quadrant)
                .Concat(board.Group(q.Quadrant).Where(AtWork))
                .Concat(board.Group(q.Quadrant).Where(t => !AtWork(t)))
                .Where(t => where.Admits(t) && StatusesOf(filter).Contains(t.Status) && !t.IsSnoozed)
                .ToArray()))
            .ToArray();

    /// <summary>The quadrant a group stands for; none for Waiting on you and Closed.</summary>
    public static Maybe<Quadrant> QuadrantOf(TaskGroupKind kind) =>
        Quadrants.TryFirst(q => q.Kind == kind).Map(q => q.Quadrant);

    /// <summary>The group a quadrant's tasks are drawn in.</summary>
    public static TaskGroupKind KindOf(Quadrant quadrant) => Quadrants.First(q => q.Quadrant == quadrant).Kind;

    /// <summary>
    /// A group's caption: "Waiting on you", "Closed", or the quadrant's numeral and Covey's words, two spaces apart
    /// ("I  Urgent and important"; Ruling 28).
    /// </summary>
    public static string GroupName(TaskGroupKind kind) =>
        QuadrantOf(kind).Match(
            q => $"{q.Numeral()}  {q.Words()}",
            () => kind switch
            {
                TaskGroupKind.WaitingOnYou => StatusName(TaskStatus.WaitingOnYou),
                TaskGroupKind.Snoozed => "Snoozed",
                TaskGroupKind.ClaudeWorking => "Claude working",
                TaskGroupKind.ClaudeIdle => "Claude idle",
                _ => StatusName(TaskStatus.Closed),
            });

    // The pinned block (TaskBoard.Pinned: the questions, then the Done tasks, full discs before rings), narrowed by the filters.
    static TaskGroup Waiting(TaskBoard board, Admit where, Func<TaskItem, bool> shows, Func<TaskItem, bool>? hollow) =>
        new(TaskGroupKind.WaitingOnYou, board.Pinned(hollow).Where(t => !t.IsSnoozed && where.Admits(t) && shows(t)).ToArray());

    /// <summary>
    /// Whether Claude is at work on the task (task 93fa0b9b, see the header): it is open, not Done and not waiting on
    /// Petre, and a session is live on it. Such a task is drawn in the pinned Claude working block, not its quadrant.
    /// </summary>
    // ClaudeStillWorking leaves a waiting task out itself since task 3cdd0a52, so the two are one test.
    public static bool AtWork(TaskItem task) => TaskBoard.ClaudeStillWorking(task);

    // The pinned Claude working block: each quadrant's tasks Claude is at work on, I to IV, each quadrant in its manual
    // order (TaskBoard.Group), so the block keeps Petre's priorities.
    // With a busy test (task 10b49554) it is two groups: ClaudeWorking holds the tasks it says yes to, ClaudeIdle the rest,
    // each still in that order.
    static IEnumerable<TaskGroup> Working(TaskBoard board, Admit where, Func<TaskItem, bool> shows, Func<TaskItem, bool>? busy)
    {
        var atWork = Quadrants.SelectMany(q => board.Group(q.Quadrant))
            .Where(t => AtWork(t) && where.Admits(t) && !t.IsSnoozed && shows(t)).ToArray();
        return busy is null
            ? [new TaskGroup(TaskGroupKind.ClaudeWorking, atWork)]
            : [new TaskGroup(TaskGroupKind.ClaudeWorking, atWork.Where(busy).ToArray()),
                new TaskGroup(TaskGroupKind.ClaudeIdle, atWork.Where(t => !busy(t)).ToArray())];
    }

    // The open view's groups in their order: Waiting on you, the Claude working block (one group, or its working and idle
    // parts), then the quadrants without the tasks Claude is at work on.
    static IEnumerable<TaskGroup> Pinned(TaskBoard board, Admit where, Func<TaskItem, bool> shows, Func<TaskItem, bool>? busy,
        Func<TaskItem, bool>? hollow) =>
        new[] { Waiting(board, where, shows, hollow) }.Concat(Working(board, where, shows, busy)).Concat(QuadrantGroups(board, where, shows));

    // A quadrant's group leaves out the tasks Claude is at work on: they are in the Claude working block above.
    static IEnumerable<TaskGroup> QuadrantGroups(TaskBoard board, Admit where, Func<TaskItem, bool> shows) =>
        Quadrants.Select(q => new TaskGroup(q.Kind, board.Group(q.Quadrant).Where(t => where.Admits(t) && !t.IsSnoozed && !AtWork(t) && shows(t)).ToArray()));

    /// <summary>How many tasks a filter shows, for its chip: a snoozed task counts under Snoozed and nowhere else.</summary>
    public static int Count(TaskBoard board, TaskFilter filter, TaskWorkspaceFilter where, TaskAssigneeFilter? who = null) =>
        board.Tasks.Count(t => StatusesOf(filter).Contains(t.Status) && (filter == TaskFilter.Snoozed) == t.IsSnoozed
            && where.Admits(t) && (who ?? TaskAssigneeFilter.All).Admits(t));

    /// <summary>
    /// The session a click on a Claude task goes to (spec, "Going to a session": the one waiting on Petre, else the
    /// most recent). Nothing records which session asked, and while a task waits it is a live session that asked, so
    /// the latest joined live session comes first; with none live, the latest joined. None for a note. The bar's
    /// line uses the same rule for the icon it shows, so the icon and the click agree.
    /// </summary>
    public static Maybe<TaskSession> SessionToGoTo(TaskItem task) =>
        task.Sessions.OrderByDescending(s => s.IsLive).ThenByDescending(s => s.Joined).TryFirst();

    /// <summary>
    /// The first non-blank line of a markdown text as plain text, for the bar's one-line description: leading list, quote
    /// and heading markers go, a link keeps its words, and emphasis and code markers (**, __, *, _, `) are dropped. An
    /// underscore inside a word (snake_case) is not emphasis and stays. Not a markdown parser, just enough for a line.
    /// A pasted image's line (TaskImages) is never the line shown, so a path never reaches the bar; text that is only
    /// images reads "image".
    /// </summary>
    public static string PlainFirstLine(string markdown)
    {
        var parts = TaskImages.Split(markdown ?? "");
        if (parts.Text.Trim().Length == 0 && parts.Images.Count > 0) return "image";
        var line = parts.Text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        line = System.Text.RegularExpressions.Regex.Replace(line, @"^(#{1,6}\s+|[-*+]\s+|>\s*|\d+[.)]\s+)", "");
        return PlainInline(line).Trim();
    }

    /// <summary>
    /// A line of markdown with its inline markers taken out: a link keeps its words, and emphasis and code markers
    /// (**, __, *, _, `) are dropped. The bar shows a session's question and status line through this (task 08cdb052):
    /// a session writes the pull request it asks to have merged as a link, "[Deskbar #490](https://...)", which the tasks
    /// panel draws as a link to click, and the bar, whose line is one click target picking the task, as "Deskbar #490".
    /// Line breaks and the text around the markers are kept as they are.
    /// </summary>
    public static string PlainInline(string markdown)
    {
        var line = System.Text.RegularExpressions.Regex.Replace(markdown ?? "", @"\[([^\]\n]*)\]\([^)\n]*\)", "$1");
        line = System.Text.RegularExpressions.Regex.Replace(line, @"(\*\*|__)(.+?)\1", "$2");
        line = System.Text.RegularExpressions.Regex.Replace(line, @"(?<![\w])([*_])(.+?)\1(?![\w])", "$2");
        return line.Replace("`", "");
    }

    /// <summary>
    /// The task's sessions as its thread lists them (spec, "Task history"; tasks-open-claude-sessions design): newest
    /// joined first, each Working or Idle while <paramref name="listed"/> (the latest `claude agents --json`) shows it,
    /// by its Busy; a session the list does not show is Ended while its transcript is there and Expired once Claude Code
    /// has deleted it (<paramref name="transcriptExists"/>), and only an Expired one cannot be opened.
    /// </summary>
    //
    // No list (None) is "cannot tell" (Global Constraints): the feature is off, or `claude` could not be read. The hooks'
    // word stands then, as everywhere else: a session they call live is Idle, since nothing says it is working, and one
    // they ended is Ended or Expired by its transcript. Nothing is ever shown ended BECAUSE the list was unreadable.
    //
    // `working` (task 10b49554) is ClaudeActivity's set: a listed session is Working when it is in it, so the thread says
    // what the line's halo and its part of the Claude working block say. Without it, the row's Busy decides, as before.
    public static IReadOnlyList<SessionView> SessionList(TaskItem task, Maybe<IReadOnlyCollection<ClaudeAgent>> listed,
        Func<TaskSession, bool> transcriptExists, IReadOnlySet<string>? working = null) =>
        task.Sessions
            .OrderByDescending(s => s.Joined)
            .Select(s => new SessionView(s.Id, s.Joined, listed.Match(
                list => list.FirstOrDefault(a => a.SessionId == s.Id) is { } agent
                    ? (working?.Contains(s.Id) ?? agent.Busy) ? SessionState.Working : SessionState.Idle
                    : Gone(s, transcriptExists),
                () => s.IsLive ? SessionState.Idle : Gone(s, transcriptExists))))
            .ToList();

    /// <summary>
    /// How many of the task's sessions Claude is working in right now (tasks panel plan, Task 8): its LIVE sessions that
    /// <paramref name="listed"/> (the latest `claude agents --json`) shows Busy. The tasks panel draws each as a session
    /// square with a halo. No list ("cannot tell") is 0: nothing is shown working that nothing says is working.
    /// </summary>
    //
    // Live by the hooks' word AND busy by the list: a session the hooks have ended can linger in the list for a poll, and
    // a halo on an ended session's square would contradict the square's own dim colour.
    public static int WorkingCount(TaskItem task, Maybe<IReadOnlyCollection<ClaudeAgent>> listed) =>
        listed.Match(
            list => task.Sessions.Count(s => s.IsLive && list.Any(a => a.Busy && a.SessionId == s.Id)),
            () => 0);

    static SessionState Gone(TaskSession session, Func<TaskSession, bool> transcriptExists) =>
        transcriptExists(session) ? SessionState.Ended : SessionState.Expired;

    // --- subtasks under their parent in the List (follow-up to the subtasks spec) --------------------------------------

    /// <summary>
    /// The List's groups with every subtask moved to right after its parent (SubtaskOrder), into the parent's group, when
    /// both are shown (<see cref="NestsUnder"/> says which pairs). A group the move empties is left out. <c>Moved</c> is
    /// the subtasks drawn under their parent, whose line then needs no parent's name. The Matrix keeps each task in its
    /// own quadrant's cell.
    /// </summary>
    public static (IReadOnlyList<TaskGroup> Groups, IReadOnlySet<Guid> Moved) UnderParents(IReadOnlyList<TaskGroup> groups)
    {
        var flat = groups.SelectMany(g => g.Tasks.Select(t => (Task: t, g.Kind))).ToArray();
        // GroupBy: a duplicate id (a hand-edited file) must not throw here (review of #467, finding 5).
        var kindOf = flat.GroupBy(x => x.Task.Id).ToDictionary(g => g.Key, g => g.First().Kind);
        bool Movable((TaskItem Task, TaskGroupKind Kind) x) =>
            x.Task.ParentId.Map(p => kindOf.TryGetValue(p, out var k) && NestsUnder(x.Kind, k)).GetValueOrDefault(false);
        var moved = SubtaskOrder.Nested(flat, x => x.Task.Id, x => x.Task.ParentId, Movable);
        var ordered = SubtaskOrder.UnderParents(flat, x => x.Task.Id, x => x.Task.ParentId, Movable);
        // A moved subtask belongs to its parent's group; every other task to its own.
        var placed = ordered.Select(x => (x.Task, Kind: moved.Contains(x.Task.Id) ? kindOf[x.Task.ParentId.Value] : x.Kind)).ToArray();
        return (groups.Select(g => new TaskGroup(g.Kind, placed.Where(x => x.Kind == g.Kind).Select(x => x.Task).ToArray()))
            .Where(g => g.Tasks.Count > 0).ToArray(), moved);
    }

    /// <summary>
    /// Whether a subtask in group <paramref name="own"/> is drawn under its parent in group <paramref name="parent"/>, the
    /// rule the bar's FloatingBar.UnderParents keeps too. Under a parent in a quadrant or in the pinned block (waiting on
    /// Petre, or Done), yes: a task dropped into a pinned one (task f41d7857, Petre: "neither moving them into
    /// each-other as subtasks. it should") is drawn under it in the pinned block, as it would be under any parent. Two
    /// exceptions. A pinned subtask stays in the pinned block unless its parent is there too: the block is what makes a
    /// question (or finished work) findable, and drawn under a plain parent it would sink below the quadrants' lines. And
    /// nothing moves out of or into the Closed group, which is history. The Claude working block (task 93fa0b9b) keeps
    /// its subtasks the same way: one Claude is at work on stays in the block unless its parent is in the block too, and in
    /// the same part of it, working or idle (task 10b49554): under a parent in the other part it would sit on the wrong
    /// side of the divider.
    /// </summary>
    public static bool NestsUnder(TaskGroupKind own, TaskGroupKind parent) =>
        own != TaskGroupKind.Closed && parent != TaskGroupKind.Closed
        && (own is not (TaskGroupKind.WaitingOnYou or TaskGroupKind.ClaudeWorking or TaskGroupKind.ClaudeIdle) || parent == own);

    // --- subtasks folded into their parent (task 173ef268, the tasks panel's "Subtasks" checkbox) ----------------------
    //
    // Petre's screenshot: a parent with eight subtasks under it filled the panel. Unticked, the checkbox folds subtasks
    // into the parent's line, which already counts them ("· 8 subtasks", SubtaskCountText) and lists them in its thread.
    // Only a subtask whose parent is ON THE LIST folds, so nothing becomes unreachable: a parent filtered out (another
    // workspace, another status) leaves its subtasks on show. One exception, a SNOOZED parent (task 4c01747c, see
    // UnderSnoozedParent below): its subtasks fold though it is off the list. A subtask waiting on Petre never folds, because a
    // question must stay findable, and that is what the pinned block is for.

    /// <summary>
    /// The List's groups with every subtask drawn under its parent (<paramref name="nested"/>, from
    /// <see cref="UnderParents"/>) left out, except one waiting on Petre. UnderParents never nests a closed one, and it
    /// nests a subtask waiting on Petre only under a parent that is pinned too (task f41d7857), where it stays on show,
    /// under that parent. A group the folding empties is left out, as UnderParents leaves out one its move empties.
    /// <para>
    /// <paramref name="keep"/> never folds either (task b0526e18, Petre: "i can't interact with the subtask. i should be
    /// able to"): the panel passes the task it has selected, so a subtask shown from its parent's thread (or picked any
    /// other way) gets a line of its own, with its thread, menu and buttons, until another task is picked.
    /// </para>
    /// </summary>
    /// <para>
    /// <paramref name="snoozedFamily"/> folds too (task 4c01747c, <see cref="UnderSnoozedParent"/>): a subtask whose
    /// parent is snoozed is not nested, because the parent is off the list, yet it folds with the same two exceptions,
    /// and never out of the pinned block or the Claude working block (FamilyFolds).
    /// </para>
    public static IReadOnlyList<TaskGroup> FoldSubtasks(IReadOnlyList<TaskGroup> groups, IReadOnlySet<Guid> nested, Maybe<Guid> keep = default,
        IReadOnlySet<Guid>? snoozedFamily = null) =>
        groups.Select(g => g with
            {
                Tasks = g.Tasks.Where(t => !(nested.Contains(t.Id) || FamilyFolds(snoozedFamily, t))
                    || t.Status == TaskStatus.WaitingOnYou || Kept(keep, t)).ToArray(),
            })
            .Where(g => g.Tasks.Count > 0)
            .ToArray();

    /// <summary>
    /// The Matrix's cells with every open subtask left out whose parent is in some cell, unless it waits on Petre or is
    /// <paramref name="keep"/> (the List's exception above). The Matrix draws no subtask under its parent (each stays in
    /// its own quadrant), so the rule is the List's in its own terms: folded where the parent is on screen to count it.
    /// Every cell stays, empty or not (the Matrix is always four).
    /// </summary>
    /// A subtask in <paramref name="snoozedFamily"/> (task 4c01747c) folds too, though its parent is in no cell.
    public static IReadOnlyList<TaskCell> FoldSubtasks(IReadOnlyList<TaskCell> cells, Maybe<Guid> keep = default,
        IReadOnlySet<Guid>? snoozedFamily = null)
    {
        var shown = cells.SelectMany(c => c.Tasks).Select(t => t.Id).ToHashSet();
        bool Folds(TaskItem t) =>
            t.IsOpen && t.Status != TaskStatus.WaitingOnYou && !Kept(keep, t)
            && ((t.ParentId is { HasValue: true } p && p.Value != t.Id && shown.Contains(p.Value))
                || FamilyFolds(snoozedFamily, t));
        return cells.Select(c => c with { Tasks = c.Tasks.Where(t => !Folds(t)).ToArray() }).ToArray();
    }

    // A SUBTASK UNDER AN OPEN THREAD MOVES INTO IT (task 452487b4). A subtask nested under its parent is drawn on its own
    // line right under the parent's line, but the parent's open thread is drawn there too, between the two: so with the
    // thread open the subtask's line sat below the whole thread, under its comments and comment box, out of sight. Petre
    // met "Close its open subtask first" on such a parent and asked "it doesn't have a subtask, does it?", then "where is
    // it?". While the parent's thread is open its nested subtasks leave the list and the thread draws them, as full lines
    // in its head (TasksWindow.SubtaskShelf), the way it already draws a folded one (task 249a1e8a); closing the thread
    // gives them their lines back.
    // Still shown once (task 0eb943d8): DrawnUnderParents, taken after this, no longer counts them.

    /// <summary>
    /// The List's groups with every subtask nested under a parent whose thread is open (<paramref name="openThreads"/>)
    /// left out, so that thread lists it instead of a line below the thread. Other lines are untouched, and so is a
    /// group, since a nested subtask shares its parent's group (UnderParents moves it there).
    /// </summary>
    public static IReadOnlyList<TaskGroup> IntoOpenThreads(IReadOnlyList<TaskGroup> groups, IReadOnlySet<Guid> nested,
        IReadOnlySet<Guid> openThreads) =>
        groups.Select(g => g with
            {
                Tasks = g.Tasks.Where(t => !(nested.Contains(t.Id) && t.ParentId.HasValue && t.ParentId.Value != t.Id
                    && openThreads.Contains(t.ParentId.Value))).ToArray(),
            })
            .Where(g => g.Tasks.Count > 0)
            .ToArray();

    /// <summary>
    /// The subtasks the List draws on a line of their own right under their parent: those <see cref="UnderParents"/>
    /// nested (<paramref name="nested"/>) that are still in <paramref name="listed"/> after any folding. A parent's open
    /// thread leaves these out (task 0eb943d8, Petre: "it shows up twice"), since the line right below already shows
    /// them; it keeps listing the folded ones and those a filter hides, which have no line to find.
    /// </summary>
    public static IReadOnlySet<Guid> DrawnUnderParents(IReadOnlyList<TaskGroup> listed, IReadOnlySet<Guid> nested) =>
        listed.SelectMany(g => g.Tasks).Select(t => t.Id).Where(nested.Contains).ToHashSet();

    // Whether the task is the one the folding keeps on show (the panel's selected task, task b0526e18).
    static bool Kept(Maybe<Guid> keep, TaskItem t) => keep.HasValue && keep.Value == t.Id;

    // THE SNOOZED PARENT (task 4c01747c, Petre with a screenshot: "unselected subtasks still shows subtasks in the main
    // bar"). He had snoozed the Warehouse parent until the next morning, which takes it off the bar and off the panel's
    // usual views, and with it off the list the rule above ("only a subtask whose parent is ON THE LIST folds") left all
    // eight of its subtasks on show, on both, with the box unticked. Snoozing a parent puts its family out of sight, so
    // with the box unticked its open subtasks fold too, except where the nesting rule would also have kept them (the
    // pinned block, Claude working; FamilyFolds). Nothing becomes unreachable: only a parent the view's filters admit
    // holds its family, so the Snoozed chip lists it, its line counts them and its thread links them, and the box ticked
    // shows them all again. The Snoozed view itself folds none this way (TasksBoard), since it lists snoozed tasks.

    /// <summary>
    /// The ids of the open subtasks with a snoozed open parent, or a snoozed open ancestor further up (a subtask of a
    /// subtask of a snoozed task folds as well): the ones the "Subtasks" checkbox unticked folds though the parent is on
    /// no list. Only a snoozed ancestor <paramref name="counts"/> admits holds its family, so a subtask never folds into
    /// a parent the view would not list even awake: the panel passes its workspace and assignee filters (the Snoozed chip
    /// then lists that parent). The bar goes further, see <see cref="UnderOpenParent"/>.
    /// </summary>
    public static IReadOnlySet<Guid> UnderSnoozedParent(TaskBoard board, Func<TaskItem, bool> counts) =>
        UnderAncestor(board, up => up.IsOpen && up.IsSnoozed && counts(up));

    /// <summary>
    /// The ids of the open subtasks whose own parent is open: the bar's fold with the
    /// "Subtasks" box unticked (task 4c01747c, Petre's second screenshot: "↳ test" stayed on the bar under a parent
    /// assigned to Claude, which the bar never lists). The bar is the short view, so there a subtask folds whether or
    /// not its parent has a line of its own; the tasks panel lists every parent and keeps its own rule.
    /// </summary>
    /// Only the direct parent is asked: a grandchild's parent is itself a subtask, open or not, and one under a closed
    /// parent keeps its line (TaskBoard.Close refuses that state, but a hand-edited file can hold it).
    public static IReadOnlySet<Guid> UnderOpenParent(TaskBoard board)
    {
        var open = board.Tasks.Where(t => t.IsOpen).Select(t => t.Id).ToHashSet();
        return board.Tasks
            .Where(t => t.IsOpen && t.ParentId is { HasValue: true } p && p.Value != t.Id && open.Contains(p.Value))
            .Select(t => t.Id).ToHashSet();
    }

    // The open tasks with an ancestor `holds` answers yes for. A broken chain (a parent id with no task, a loop) ends the
    // walk, so it neither hangs nor throws.
    static IReadOnlySet<Guid> UnderAncestor(TaskBoard board, Func<TaskItem, bool> holds)
    {
        var byId = board.Tasks.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
        // Walks up from `at`, one step per task on the board at most, so a loop in the parent ids ends.
        bool Held(Maybe<Guid> at, int left) =>
            left > 0 && at.HasValue && byId.TryGetValue(at.Value, out var up)
            && (holds(up) || Held(up.ParentId, left - 1));
        return board.Tasks.Where(t => t.IsOpen && Held(t.ParentId, byId.Count)).Select(t => t.Id).ToHashSet();
    }

    /// <summary>The panel's <see cref="UnderSnoozedParent(TaskBoard, Func{TaskItem, bool})"/>: a snoozed ancestor its filters admit.</summary>
    public static IReadOnlySet<Guid> UnderSnoozedParent(TaskBoard board, TaskWorkspaceFilter workspaceFilter, TaskAssigneeFilter? who = null) =>
        UnderSnoozedParent(board, new Admit(workspaceFilter, who ?? TaskAssigneeFilter.All).Admits);

    // Whether a task in the snoozed family folds where it is drawn: never a pinned one (a question, or Done work waiting
    // to be closed) and never one Claude is at work on, the two blocks NestsUnder keeps apart for the same reason, so
    // the folding takes nothing out of them that the nesting rule would have left.
    static bool FamilyFolds(IReadOnlySet<Guid>? snoozedFamily, TaskItem t) =>
        (snoozedFamily?.Contains(t.Id) ?? false) && !t.Status.IsPinned() && !AtWork(t);

    // --- subtasks on a line (docs/superpowers/specs/2026-11-24-subtasks-design.md, Petre's pick A) ----------------------

    // How much of a parent's title the breadcrumb keeps, so the subtask's own title keeps most of the line.
    const int BreadcrumbTitle = 22;

    /// <summary>
    /// What goes in front of a subtask's title: "↳ &lt;parent title, cut&gt; › " on a full line (the tasks panel's), "↳ "
    /// alone where there is no room for it (the bar, and the panel's slim line); "" for a task with no parent.
    /// </summary>
    public static string Breadcrumb(string? parentTitle, bool full) =>
        parentTitle is null ? ""
        : !full ? "↳ "
        : $"↳ {(parentTitle.Length <= BreadcrumbTitle ? parentTitle : parentTitle[..BreadcrumbTitle].TrimEnd() + "…")} › ";

    /// <summary>A parent's count of open subtasks in words, "" for none: what follows its title after a " · ".</summary>
    // A no-break space between the number and the word (follow-up: Petre's screenshot had "· 2" end one line and
    // "subtasks" start the next), so the count never splits.
    public static string SubtaskCountText(int count) =>
        count switch { 0 => "", 1 => "1\u00A0subtask", _ => $"{count}\u00A0subtasks" };

    /// <summary>
    /// Why a parent will not close (TaskBoard.Close refuses it while any of its subtasks is open, task 842e1ba4): what
    /// the refusal says, on the window's problem line, in the bar's message and from `task close`.
    /// </summary>
    public static string CloseSubtasksFirst(int open) =>
        open == 1 ? "Close its open subtask first" : $"Close its {open} open subtasks first";

    // How much of a subtask's title the refusal quotes, so the line under the parent's title stays one or two lines.
    const int RefusalTitle = 40;

    /// <summary>
    /// Close's refusal on a parent, naming its open subtasks (task 452487b4, Petre on a screenshot of the refusal with
    /// the subtask out of sight: "it doesn't have a subtask, does it?", then "where is it?"): the count's words followed
    /// by the first subtask's title in quotes, cut at <see cref="RefusalTitle"/>, and "and N more" past the first.
    /// The tick's tooltip keeps the count alone (<see cref="CloseSubtasksFirst(int)"/>), since a tooltip is a hint.
    /// </summary>
    public static string CloseSubtasksFirst(IReadOnlyList<string> openTitles) =>
        openTitles.Count == 0 ? ""
        : $"{CloseSubtasksFirst(openTitles.Count)}: \"{Cut(FirstLineOf(openTitles[0]), RefusalTitle)}\""
          + (openTitles.Count > 1 ? $" and {openTitles.Count - 1} more" : "");

    // A title's first line, trimmed: a pasted title can run to several lines.
    static string FirstLineOf(string title) => title.Split('\n', 2)[0].Trim();

    // The text cut to `max` characters with an ellipsis, whole when it fits.
    // Never between the two halves of a surrogate pair (an emoji), which would leave half a character before the "…".
    static string Cut(string text, int max) =>
        text.Length <= max ? text : text[..(char.IsHighSurrogate(text[max - 1]) ? max - 1 : max)].TrimEnd() + "…";

    /// <summary>
    /// Why a task Claude is still working on will not close (TaskBoard.Close, task 37cd7081): the bar's message, the
    /// window's problem line and `task close` say it, each with its own way past it (the ticks' "Stop Claude and close"
    /// menu, `--stop-claude` on the pipe).
    /// </summary>
    public const string ClaudeStillWorkingRefusal = "Claude is still working on this task. Wait until it is Done, or stop Claude and close it";

    /// <summary>The one item of the menu a tick opens on such a task (FloatingBar.TaskList and TasksWindow).</summary>
    public const string StopClaudeAndClose = "Stop Claude and close";

    /// <summary>
    /// Why a task that is not finished will not close (TaskBoard.Close, task 579f2e4c): it still asks something or has a
    /// live session, and its work is not merged. The bar's message, the window's problem line and `task close` say it,
    /// each with its way past it (the ticks' "Close anyway" menu, `--anyway` on the pipe).
    /// </summary>
    public const string UnfinishedRefusal = "This task is not finished and its work is not merged yet. Answer it, wait for the merge, or close it anyway";

    /// <summary>The one item of the menu a tick opens on a task that is not finished (FloatingBar.TaskList and TasksWindow).</summary>
    public const string CloseAnyway = "Close anyway";

    /// <summary>
    /// The line above "Close anyway" on that menu, which takes no click: what is still open on the task, so the click that
    /// goes past it is made knowing what it drops. A question wins over a session, since the question is what waits on
    /// Petre.
    /// </summary>
    public static string UnfinishedHeader(TaskItem task) =>
        task.Question.HasValue ? "This task still asks you something, and its work is not merged yet."
        : "A Claude session is still on this task, and its work is not merged yet.";

    /// <summary>
    /// The tooltip on a line's tick box, the bar's and the window's alike: what ticking it off does, or, on a parent with
    /// open subtasks, why it will not (the same words the refusal uses), so the reason shows before the click.
    /// </summary>
    // `working` (task 37cd7081): Claude is still at it (TaskBoard.ClaudeStillWorking), so the tick offers to stop it first.
    // `unfinished` (task 579f2e4c): it still asks something or has a live session and is not merged (TaskBoard.Unfinished),
    // so the tick asks before it closes.
    public static string TickTip(bool claudes, int openSubtasks, bool working = false, bool unfinished = false) =>
        openSubtasks > 0 ? CloseSubtasksFirst(openSubtasks)
        : working ? "Claude is still working on this: tick to stop Claude and close"
        : unfinished ? "Not finished and not merged yet: tick asks before closing"
        : claudes ? "Tick off: closes Claude's task"
        : "Tick off: closes the note";

    /// <summary>What goes between a title and its subtask count: " ·" and a no-break space, so the dot stays with the count.</summary>
    public const string SubtaskCountSeparator = " ·\u00A0";

    /// <summary>
    /// What a closed duplicate is called where a closed task says "Closed" (task links,
    /// docs/superpowers/specs/2026-11-25-task-links-design.md): it is stored as Closed plus a link, never as a status.
    /// </summary>
    public const string DuplicateName = "Duplicate";

    /// <summary>The words of each status, as the spec spells them.</summary>
    public static string StatusName(TaskStatus status) => status switch
    {
        TaskStatus.Backlog => "Backlog",
        TaskStatus.InProgress => "In progress",
        TaskStatus.WaitingOnYou => "Waiting on you",
        TaskStatus.Done => "Done",
        TaskStatus.Closed => "Closed",
        _ => status.ToString(),
    };

    /// <summary>
    /// Whether a status filter lists the task, on its status alone (the workspace, assignee, goal and search filters are
    /// asked apart): a snoozed open task only under Snoozed, any other only under a filter naming its status. For the
    /// Stats view's click-through (task 521d2b07, TasksBoard.OpenFromStats), which must land on a list that has the task.
    /// </summary>
    public static bool Lists(TaskFilter filter, TaskItem task) =>
        task.IsOpen && task.IsSnoozed
            ? filter == TaskFilter.Snoozed
            : filter != TaskFilter.Snoozed && StatusesOf(filter).Contains(task.Status);

    /// <summary>The status filter that lists the task (see <see cref="Lists"/>): Closed, Snoozed or Open.</summary>
    public static TaskFilter FilterListing(TaskItem task) =>
        !task.IsOpen ? TaskFilter.Closed : task.IsSnoozed ? TaskFilter.Snoozed : TaskFilter.Open;

    static IReadOnlyList<TaskStatus> StatusesOf(TaskFilter filter) => filter switch
    {
        TaskFilter.Open or TaskFilter.Snoozed => [TaskStatus.WaitingOnYou, TaskStatus.Done, TaskStatus.InProgress, TaskStatus.Backlog],
        TaskFilter.WaitingOnYou => [TaskStatus.WaitingOnYou],
        TaskFilter.Done => [TaskStatus.Done],
        TaskFilter.InProgress => [TaskStatus.InProgress],
        TaskFilter.Backlog => [TaskStatus.Backlog],
        _ => [TaskStatus.Closed],
    };

}
