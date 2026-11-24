using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// THE TASKS WINDOW'S GROUPING AND FILTERS, pure. Design: docs/superpowers/specs/2026-11-22-tasks-board-design.md,
// "The window", with Task 12's order (progress.md Rulings 25 and 28): the window's List view is grouped as the bar is,
// Waiting on you first (the latest ask on top), then Covey's quadrants I to IV, each in Petre's manual order
// (TaskBoard.Group), each captioned in Covey's words. Filters by status (Closed among them) and by workspace.
//
// A status filter keeps the quadrants: "In progress" shows the quadrants with only their tasks in progress, in the
// same order, so a line sits in the same group whatever the filter. "Waiting on you" is the pinned block alone.
// Closed tasks have no order of their own (closing takes a task out of it): they are one group, the latest closed
// first, since closing stamps UpdatedAt, the same order `task list --closed` prints.
//
// The window's controller (Deskbar.App.TasksBoard) draws what this returns. It puts a task closed in the window back
// under its earlier status for the six seconds of its Undo before calling here, so a line just closed stays in place
// struck through (the bar's Ruling 10); that substitution is the controller's, and this stays a plain function of the
// board.

/// <summary>
/// The window's status filters. <see cref="Open"/> is the default view: every task but the closed ones, grouped.
/// The other five show one status alone.
/// </summary>
public enum TaskFilter { Open, WaitingOnYou, Done, InProgress, Backlog, Closed }

/// <summary>
/// The window's workspace filter: every task (<see cref="All"/>), or only the tasks on one workspace, or only the tasks
/// on none (<see cref="On"/> with no workspace).
/// </summary>
public sealed record TaskWorkspaceFilter(bool Any, Maybe<Guid> Workspace)
{
    public static readonly TaskWorkspaceFilter All = new(true, Maybe<Guid>.None);

    public static TaskWorkspaceFilter On(Maybe<Guid> workspace) => new(false, workspace);

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
/// Which group of the window a task is drawn in: the pinned Waiting on you block, one of Covey's quadrants (named as
/// the book numbers them), or the closed tasks.
/// </summary>
public enum TaskGroupKind { WaitingOnYou, I, II, III, IV, Closed }

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
    /// The groups the window draws for a filter, empty groups left out (see the header): Waiting on you by its latest
    /// ask, each quadrant in its manual order, the closed tasks the latest closed first.
    /// </summary>
    public static IReadOnlyList<TaskGroup> Grouped(TaskBoard board, TaskFilter filter, TaskWorkspaceFilter workspaceFilter, TaskAssigneeFilter? who = null) =>
        Grouped(board, filter, new Admit(workspaceFilter, who ?? TaskAssigneeFilter.All));

    static IReadOnlyList<TaskGroup> Grouped(TaskBoard board, TaskFilter filter, Admit where) =>
        (filter switch
        {
            TaskFilter.Closed => [new TaskGroup(TaskGroupKind.Closed,
                board.Tasks.Where(t => t.Status == TaskStatus.Closed && where.Admits(t)).OrderByDescending(t => t.UpdatedAt).ToArray())],
            TaskFilter.WaitingOnYou => [Waiting(board, where)],
            TaskFilter.Open => QuadrantGroups(board, where, _ => true).Prepend(Waiting(board, where)),
            _ => QuadrantGroups(board, where, t => StatusesOf(filter).Contains(t.Status)),
        })
        .Where(g => g.Tasks.Count > 0)
        .ToArray();

    /// <summary>
    /// The Matrix (Ruling 28): Covey's four cells, I to IV, always all four. Each lists its quadrant's open tasks in
    /// their order, the ones waiting on Petre first, on top of their own quadrant's cell (pinned, as everywhere,
    /// the latest ask first), each drawn orange as everywhere. The status filter narrows every cell as it narrows the
    /// List; the closed tasks have no quadrant order, so under "Closed" every cell is empty.
    /// </summary>
    public static IReadOnlyList<TaskCell> Matrix(TaskBoard board, TaskFilter filter, TaskWorkspaceFilter workspaceFilter, TaskAssigneeFilter? who = null) =>
        Matrix(board, filter, new Admit(workspaceFilter, who ?? TaskAssigneeFilter.All));

    static IReadOnlyList<TaskCell> Matrix(TaskBoard board, TaskFilter filter, Admit where) =>
        Quadrants.Select(q => new TaskCell(q.Quadrant, filter == TaskFilter.Closed ? []
            : board.Tasks.Where(t => t.Status == TaskStatus.WaitingOnYou && t.Quadrant == q.Quadrant).OrderByDescending(t => t.Raised)
                .Concat(board.Group(q.Quadrant))
                .Where(t => where.Admits(t) && StatusesOf(filter).Contains(t.Status))
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
            () => kind == TaskGroupKind.WaitingOnYou ? StatusName(TaskStatus.WaitingOnYou) : StatusName(TaskStatus.Closed));

    static TaskGroup Waiting(TaskBoard board, Admit where) =>
        new(TaskGroupKind.WaitingOnYou, board.Tasks.Where(t => t.Status == TaskStatus.WaitingOnYou && where.Admits(t))
            .OrderByDescending(t => t.Raised).ToArray());

    static IEnumerable<TaskGroup> QuadrantGroups(TaskBoard board, Admit where, Func<TaskItem, bool> shows) =>
        Quadrants.Select(q => new TaskGroup(q.Kind, board.Group(q.Quadrant).Where(t => where.Admits(t) && shows(t)).ToArray()));

    /// <summary>How many tasks a filter shows, for its chip.</summary>
    public static int Count(TaskBoard board, TaskFilter filter, TaskWorkspaceFilter where, TaskAssigneeFilter? who = null) =>
        board.Tasks.Count(t => StatusesOf(filter).Contains(t.Status) && where.Admits(t) && (who ?? TaskAssigneeFilter.All).Admits(t));

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
        line = System.Text.RegularExpressions.Regex.Replace(line, @"\[([^\]]*)\]\([^)]*\)", "$1");
        line = System.Text.RegularExpressions.Regex.Replace(line, @"(\*\*|__)(.+?)\1", "$2");
        line = System.Text.RegularExpressions.Regex.Replace(line, @"(?<![\w])([*_])(.+?)\1(?![\w])", "$2");
        return line.Replace("`", "").Trim();
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
    public static IReadOnlyList<SessionView> SessionList(TaskItem task, Maybe<IReadOnlyCollection<ClaudeAgent>> listed,
        Func<TaskSession, bool> transcriptExists) =>
        task.Sessions
            .OrderByDescending(s => s.Joined)
            .Select(s => new SessionView(s.Id, s.Joined, listed.Match(
                list => list.FirstOrDefault(a => a.SessionId == s.Id) is { } agent
                    ? agent.Busy ? SessionState.Working : SessionState.Idle
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
    /// both are shown and the parent is in a quadrant. A subtask waiting on Petre stays pinned, and nothing moves into the
    /// Waiting on you or Closed groups. A group the move empties is left out. <c>Moved</c> is the subtasks drawn under
    /// their parent, whose line then needs no parent's name. The Matrix keeps each task in its own quadrant's cell.
    /// </summary>
    public static (IReadOnlyList<TaskGroup> Groups, IReadOnlySet<Guid> Moved) UnderParents(IReadOnlyList<TaskGroup> groups)
    {
        var flat = groups.SelectMany(g => g.Tasks.Select(t => (Task: t, g.Kind))).ToArray();
        var kindOf = flat.ToDictionary(x => x.Task.Id, x => x.Kind);
        bool InQuadrant(TaskGroupKind kind) => kind is not (TaskGroupKind.WaitingOnYou or TaskGroupKind.Closed);
        bool Movable((TaskItem Task, TaskGroupKind Kind) x) =>
            InQuadrant(x.Kind) && x.Task.ParentId.Map(p => kindOf.TryGetValue(p, out var k) && InQuadrant(k)).GetValueOrDefault(false);
        var moved = SubtaskOrder.Nested(flat, x => x.Task.Id, x => x.Task.ParentId, Movable);
        var ordered = SubtaskOrder.UnderParents(flat, x => x.Task.Id, x => x.Task.ParentId, Movable);
        // A moved subtask belongs to its parent's group; every other task to its own.
        var placed = ordered.Select(x => (x.Task, Kind: moved.Contains(x.Task.Id) ? kindOf[x.Task.ParentId.Value] : x.Kind)).ToArray();
        return (groups.Select(g => new TaskGroup(g.Kind, placed.Where(x => x.Kind == g.Kind).Select(x => x.Task).ToArray()))
            .Where(g => g.Tasks.Count > 0).ToArray(), moved);
    }

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

    /// <summary>What goes between a title and its subtask count: " ·" and a no-break space, so the dot stays with the count.</summary>
    public const string SubtaskCountSeparator = " ·\u00A0";

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

    static IReadOnlyList<TaskStatus> StatusesOf(TaskFilter filter) => filter switch
    {
        TaskFilter.Open => [TaskStatus.WaitingOnYou, TaskStatus.Done, TaskStatus.InProgress, TaskStatus.Backlog],
        TaskFilter.WaitingOnYou => [TaskStatus.WaitingOnYou],
        TaskFilter.Done => [TaskStatus.Done],
        TaskFilter.InProgress => [TaskStatus.InProgress],
        TaskFilter.Backlog => [TaskStatus.Backlog],
        _ => [TaskStatus.Closed],
    };

}
