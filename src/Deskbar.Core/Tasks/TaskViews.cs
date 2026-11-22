using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// THE TASKS WINDOW'S GROUPING AND FILTERS, pure. Design: docs/superpowers/specs/2026-11-22-tasks-board-design.md,
// "The window": the window opens onto the board, every open task grouped by status (Waiting on you, Done, In
// progress, Backlog), with filters by status (Closed among them) and by workspace.
//
// Why a second ordering next to TaskBoard.Pile: Pile is the bar's order, which leaves Closed out and puts In progress
// and Backlog in one group. The window shows them as two groups and needs Closed under its own filter, so it groups
// over TaskBoard.Tasks itself. Inside each open group the order is still TaskItem.Raised, newest first, the rule that
// keeps a status line or a comment from moving a line (spec, "The bar's order"). Closed tasks are the latest closed
// first: closing stamps UpdatedAt, the same order `task list --closed` prints.
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

/// <summary>One group of the window: its status and its tasks in the order they are drawn.</summary>
public sealed record TaskGroup(TaskStatus Status, IReadOnlyList<TaskItem> Tasks);

public static class TaskViews
{
    // The open view's groups, top to bottom: the one that asks something of Petre, then the finished work waiting for
    // him to close it, then work in progress, then the backlog (spec, "The window").
    static readonly IReadOnlyList<TaskStatus> OpenOrder =
        [TaskStatus.WaitingOnYou, TaskStatus.Done, TaskStatus.InProgress, TaskStatus.Backlog];

    /// <summary>
    /// The groups the window draws for a filter, empty groups left out. Open tasks newest raised first in each group;
    /// closed tasks the latest closed first.
    /// </summary>
    public static IReadOnlyList<TaskGroup> Grouped(TaskBoard board, TaskFilter filter, TaskWorkspaceFilter where) =>
        StatusesOf(filter)
            .Select(status => new TaskGroup(status, Ordered(status, board.Tasks.Where(t => t.Status == status && where.Admits(t)))))
            .Where(g => g.Tasks.Count > 0)
            .ToArray();

    /// <summary>How many tasks a filter shows, for its chip.</summary>
    public static int Count(TaskBoard board, TaskFilter filter, TaskWorkspaceFilter where) =>
        board.Tasks.Count(t => StatusesOf(filter).Contains(t.Status) && where.Admits(t));

    /// <summary>
    /// The session a click on a Claude task goes to (spec, "Going to a session": the one waiting on Petre, else the
    /// most recent). Nothing records which session asked, and while a task waits it is a live session that asked, so
    /// the latest joined live session comes first; with none live, the latest joined. None for a note. The bar's
    /// line uses the same rule for the icon it shows, so the icon and the click agree.
    /// </summary>
    public static Maybe<TaskSession> SessionToGoTo(TaskItem task) =>
        task.Sessions.OrderByDescending(s => s.IsLive).ThenByDescending(s => s.Joined).TryFirst();

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
        TaskFilter.Open => OpenOrder,
        TaskFilter.WaitingOnYou => [TaskStatus.WaitingOnYou],
        TaskFilter.Done => [TaskStatus.Done],
        TaskFilter.InProgress => [TaskStatus.InProgress],
        TaskFilter.Backlog => [TaskStatus.Backlog],
        _ => [TaskStatus.Closed],
    };

    static IReadOnlyList<TaskItem> Ordered(TaskStatus status, IEnumerable<TaskItem> tasks) =>
        (status == TaskStatus.Closed ? tasks.OrderByDescending(t => t.UpdatedAt) : tasks.OrderByDescending(t => t.Raised)).ToArray();
}
