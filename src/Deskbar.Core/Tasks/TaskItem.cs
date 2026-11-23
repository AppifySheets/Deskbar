using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// The task data model. Design and reasoning: docs/superpowers/specs/2026-11-22-tasks-board-design.md, sections
// "What Petre decided" and "Calls made after the builder check". This replaced the timer model of
// 2026-11-19-tasks-in-the-bar-design.md: there are no timers, no countdown and no due state any more. A task is a
// line in one list of what is happening, Petre's notes and Claude sessions' work alike.
//
// Everything here is an immutable record; TaskBoard holds the rules that change them.

/// <summary>
/// Where a task stands. Shown as "Backlog", "In progress", "Waiting on you", "Done" and "Closed".
/// <list type="bullet">
/// <item>Backlog: not started. Petre's notes start here.</item>
/// <item>InProgress: a Claude session has joined it.</item>
/// <item>WaitingOnYou: a session asked Petre something; his next message to that session moves it back.</item>
/// <item>Done: Claude finished it; it stays in the list until Petre closes it.</item>
/// <item>Closed: Petre closed it. Only Petre closes; closed tasks leave the lists but stay readable.</item>
/// </list>
/// Backlog is first on purpose: it is the enum's default, so a missing status in a file reads as Backlog.
/// </summary>
public enum TaskStatus { Backlog, InProgress, WaitingOnYou, Done, Closed }

/// <summary>
/// A Claude Code session that has worked on a task: its session id, the folder it runs in (how its VS Code window is
/// found) and when it joined. <see cref="Ended"/> is set when the session ends; the task stays open regardless.
/// </summary>
public sealed record TaskSession(string Id, string Folder, DateTimeOffset Joined, Maybe<DateTimeOffset> Ended)
{
    public bool IsLive => Ended.HasNoValue;

    /// <summary>
    /// When Petre ran /clear in this session (its SessionEnd hook said reason "clear"; Ruling 19 of the
    /// tasks-open-claude-sessions build). /clear starts a fresh conversation in the same window, so this one is over
    /// for good: SessionKeeper never resumes it, and a session that starts with source "clear" in the same folder
    /// within TaskBoard.ClearedWithin of this moment takes its place on the task. Whether /clear keeps the session id
    /// was not measured; if it does, that session's own SessionStart rejoins it as a fresh entry, which drops the mark.
    /// An init property like TaskItem's flags, so no constructor call has to say it; absent in a file means none.
    /// </summary>
    public Maybe<DateTimeOffset> ReplacedAt { get; init; }

    /// <summary>Whether /clear replaced this session (<see cref="ReplacedAt"/>).</summary>
    public bool Replaced => ReplacedAt.HasValue;
}

/// <summary>
/// One comment in a task's thread, as markdown. <see cref="SessionId"/> names the Claude session that wrote it;
/// none means Petre wrote it.
/// </summary>
public sealed record TaskComment(Guid Id, Maybe<string> SessionId, DateTimeOffset At, string Markdown)
{
    /// <summary>
    /// When the comment was settled (Ruling 62), or none. A settled comment is kept, never deleted, and the tasks window
    /// folds it away behind one line. An init property like the task's flags: a comment saved before it existed is not
    /// settled. `task answered` settles the session's comments since its ask; a session settles one of its own, Petre any.
    /// </summary>
    public Maybe<DateTimeOffset> Settled { get; init; }
}

/// <summary>
/// One task. <see cref="WorkspaceId"/> is none for a free task. <see cref="StatusLine"/> is the short line a session
/// keeps current ("waiting for CI on #398"), "" when there is none. <see cref="Question"/> is what a session asked
/// Petre while the task is <see cref="TaskStatus.WaitingOnYou"/>. <see cref="UpdatedAt"/> moves on every change so a
/// later sync can tell which copy is newer. <see cref="Raised"/> orders the Waiting on you block (the latest ask on
/// top): set when the task is made, when a session asks and when the task becomes Done, and by nothing else.
/// Everything below that block is ordered by the quadrant its two flags name and its <see cref="Rank"/> in it
/// (TaskOrder.cs), so a status line, a comment or a session ending never moves a line (spec, "The bar's order").
/// </summary>
public sealed record TaskItem(
    Guid Id,
    string Title,
    Maybe<Guid> WorkspaceId,
    TaskStatus Status,
    string StatusLine,
    Maybe<string> Question,
    DateTimeOffset Created,
    DateTimeOffset UpdatedAt,
    DateTimeOffset Raised,
    IReadOnlyList<TaskSession> Sessions,
    IReadOnlyList<TaskComment> Comments)
{
    // The two flags and the manual order (Task 12, Rulings 25 and 28). Init properties rather than more positional
    // parameters: every task made before they existed is "neither flag", and its rank is what TaskBoard gives it
    // (a new task) or what JsonTaskStore reads or derives (a loaded one), so no constructor call has to say them.

    /// <summary>Petre's "important" flag, the exclamation toggle on a line.</summary>
    public bool Important { get; init; }

    /// <summary>Petre's "urgent" flag, the clock toggle on a line.</summary>
    public bool Urgent { get; init; }

    /// <summary>
    /// The task's place in its quadrant's manual order: smaller is higher. Only the order of ranks within one quadrant
    /// means anything; the numbers themselves are arbitrary (TaskBoard.Place takes the middle of two neighbours).
    /// </summary>
    public double Rank { get; init; }

    /// <summary>
    /// The goal this task counts toward, weekly or monthly, or none (goals spec,
    /// docs/superpowers/specs/2026-11-22-goals-planner-design.md, "Tasks contribute"). An init property like the flags,
    /// so no constructor call has to say it. The link lives HERE, on the task, while the goals live in goals.json; a
    /// link naming a goal that no longer exists reads as no goal everywhere, and nothing prunes it (pruning on load
    /// would wipe every link the day goals.json fails to load). Only GoalService.LinkTask checks that the goal is open;
    /// TaskBoard.SetGoal checks nothing about goals, so the tasks code never depends on the goals code.
    /// </summary>
    public Maybe<Guid> GoalId { get; init; }

    /// <summary>
    /// Who the task is on (Ruling 57, Assignee.cs). An init property like the flags: a task made by Petre is his, which
    /// is the default; TaskBoard sets Claude where a session makes or takes a task, and JsonTaskStore derives it for a
    /// task saved before the field existed.
    /// </summary>
    public Assignee Assignee { get; init; } = Assignee.Me;

    /// <summary>
    /// Petre's own description of the task, markdown, "" for none (Ruling 58): the second line or block under the title.
    /// An init property like the flags, stored as an optional field. It is HIS: a Claude task's status line stays the
    /// session's and is never edited by him, and a session never writes this (TaskBoard.Describe is Petre's).
    /// </summary>
    public string Description { get; init; } = "";

    /// <summary>
    /// When the task last BEGAN waiting on Petre (a session's ask, or a status set to Waiting by hand), or none. What
    /// `answered` settles a session's comments since: Raised cannot say it, since a status set by hand never moves it. A
    /// stored optional field; a file without it falls back to Raised.
    /// </summary>
    public Maybe<DateTimeOffset> WaitingSince { get; init; }

    /// <summary>
    /// When Deskbar handed this Claude task back to Petre because no session had worked on it for a while
    /// (StalledTasks; spec docs/superpowers/specs/2026-11-23-stalled-claude-tasks-design.md), or none. The mark is what
    /// tells such a task apart from one Petre assigned to himself: only a marked task goes back to Claude when its
    /// session works again (TaskBoard.TakeBack). Any other change of assignee or status clears it (TaskBoard.Change),
    /// and so does Petre assigning it himself (TaskBoard.Assign). A stored optional field, read tolerantly.
    /// </summary>
    public Maybe<DateTimeOffset> HandedBack { get; init; }

    /// <summary>
    /// Deskbar made this task for a live session no task knew (TaskBoard.AdoptSession; design
    /// docs/superpowers/specs/2026-11-23-tasks-open-claude-sessions-design.md, "Every live session has a task"). Kept so
    /// that when the session joins its own task a moment later (its SessionStart hook can come after the adoption), the
    /// task made for it can go, if nothing else was added to it (TaskBoard.WithoutSessionElsewhere). An init property like
    /// the flags: every other task, and every task in a file written before the mark existed, is not adopted.
    /// </summary>
    public bool Adopted { get; init; }

    /// <summary>Covey's quadrant the two flags name (TaskOrder.cs).</summary>
    public Quadrant Quadrant => QuadrantOf.Flags(Important, Urgent);

    /// <summary>
    /// A task is Claude's when at least one session has worked on it. Derived, never stored: a session joining one of
    /// Petre's notes is exactly what makes it Claude's work (spec, "joining a note").
    /// </summary>
    public bool IsClaudes => Sessions.Count > 0;

    /// <summary>Every status but Closed: an open task belongs on the bar's list and the window's default view.</summary>
    public bool IsOpen => Status != TaskStatus.Closed;

    /// <summary>Whether a session is still running on it; a task whose sessions all ended says "no live session".</summary>
    public bool HasLiveSession => Sessions.Any(s => s.IsLive);
}
