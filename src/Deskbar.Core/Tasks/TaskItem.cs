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

// DONE WAITS ON PETRE TOO (task eefb986a, 2026-11-25, Petre: "tasks marked as done should be in the waiting on me
// section"). A Done task is finished work that sits until he closes it, so it is his next move just as a question is.
// Both statuses are pinned above Covey's quadrants, in the one block the bar and the tasks panel caption "Waiting on
// you": the questions first, then the Done tasks (TaskBoard.Pinned), each part a new one on top and otherwise in the
// order Petre drags it to (TaskItem.Pin; task f41d7857). A pinned task is out of its quadrant's manual order
// (TaskBoard.Group), so it keeps its Rank untouched and comes back to the same place in its quadrant when it goes back
// to In progress.

/// <summary>Which statuses are pinned above the quadrants, in the "Waiting on you" block (see the comment above).</summary>
public static class TaskStatusPinning
{
    /// <summary>Waiting on you and Done: the statuses that wait on Petre and sit in the pinned block on top.</summary>
    public static bool IsPinned(this TaskStatus status) => status is TaskStatus.WaitingOnYou or TaskStatus.Done;
}

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
/// One entry of a task's <see cref="TaskItem.StatusLog"/>: the task went from <see cref="From"/> to <see cref="To"/> at
/// <see cref="At"/>. Both ends are kept because a log that starts in the middle of a task's life (every task made before
/// logging began) has to say whether its first entry left an open status or a finished one: Done to Closed means the
/// task was already finished before the log saw it (TaskStats.FinishedAt).
/// </summary>
public sealed record StatusChange(DateTimeOffset At, TaskStatus From, TaskStatus To);

/// <summary>
/// One entry of a task's <see cref="TaskItem.Deployed"/> (task 8470d176, phase 2): the pull request whose
/// <see cref="PullRequestLink.Key"/> is <see cref="Key"/> was reported running where it is deployed, at
/// <see cref="At"/>, with <see cref="Note"/> saying how that was seen ("2026.11.25.39-3749d21 in the portal logs", or
/// "Marked deployed by hand"). Deskbar cannot see another app's deployment, so this record is the only way a Done task
/// in another repository gets its full tick (RunningChanges).
/// </summary>
public sealed record DeployedPullRequest(string Key, DateTimeOffset At, string Note);

/// <summary>
/// A task's <see cref="TaskItem.DeployUnknown"/> (task 485ba96b): at <see cref="At"/> its session said it cannot tell
/// whether the change was deployed, and <see cref="Reason"/> says what it has no access to ("no access to the Portainer
/// logs on contabo1").
/// </summary>
public sealed record DeployUnknownReport(DateTimeOffset At, string Reason);

/// <summary>
/// One comment in a task's thread, as markdown. <see cref="SessionId"/> names the Claude session that wrote it;
/// none means Petre wrote it, <see cref="DeskbarAuthor"/> means Deskbar itself did (a resume, a stop, a failure), and <see cref="ManagerAuthor"/> means the manager session did.
/// </summary>
public sealed record TaskComment(Guid Id, Maybe<string> SessionId, DateTimeOffset At, string Markdown)
{
    /// <summary>
    /// The author id of Deskbar's own thread lines ("Resumed."). They used to be written with no
    /// author, which is Petre's, so the thread showed them as "you" with an Edit button (Petre's screenshot,
    /// 2026-11-24). A reserved value in the session id's place keeps the file format as it is: an old Deskbar reads
    /// such a line as a session's, which is closer to the truth than Petre's. Not a session id Claude Code can make:
    /// those are GUIDs.
    /// </summary>
    public const string DeskbarAuthor = "deskbar";

    /// <summary>Deskbar wrote it: no Edit (it is not Petre's), and no session can settle or own it.</summary>
    public bool ByDeskbar => SessionId.HasValue && SessionId.Value == DeskbarAuthor;

    /// <summary>
    /// The author id of the manager session's comments (docs/superpowers/specs/2026-11-25-manager-session-design.md).
    /// The manager's `task comment` runs inside a Claude session, so the client marks it --from-claude and it used to be
    /// stored as the user's, with nothing to tell the two apart. A reserved value in the session id's place, like
    /// <see cref="DeskbarAuthor"/>: it is not a GUID, so no real session can have it, and an old Deskbar reads it as a
    /// session's comment, which is nearer the truth than the user's.
    /// </summary>
    public const string ManagerAuthor = "manager";

    /// <summary>The manager session wrote it: not the user's (no Edit), not a task session's (it settles no question).</summary>
    public bool ByManager => SessionId.HasValue && SessionId.Value == ManagerAuthor;

    /// <summary>
    /// Deskbar's line saying it set a question aside (RepliedTasks.Note), in today's wording or the longer one before
    /// task aa42794f. TaskBoard folds these once the question is settled or back on the line, when they say nothing new.
    /// </summary>
    public bool IsSetAsideNote =>
        ByDeskbar
        && (Markdown.StartsWith(RepliedTasks.NoteOpening, StringComparison.Ordinal)
            || Markdown.StartsWith(RepliedTasks.OldNoteOpening, StringComparison.Ordinal));

    /// <summary>A task's Claude session wrote it (not Deskbar, not the manager).</summary>
    public bool BySession => SessionId.HasValue && !ByDeskbar && !ByManager;

    // The openings of every line Deskbar has written into a thread (ClaudeSessions and ClaudeTaskActions). A file
    // saved before DeskbarAuthor existed has these with no author; Attributed gives them back to Deskbar on load.
    static readonly IReadOnlyList<string> DeskbarOpenings =
    [
        "Resumed by Deskbar at ",
        "Stopped by Deskbar at ",
        "Deskbar could not ",
        "Claude Code's list of sessions could not be read",
    ];

    /// <summary>
    /// This comment, with a line Deskbar wrote before <see cref="DeskbarAuthor"/> existed given back to Deskbar: no
    /// author, and it opens the way only Deskbar's lines open (or is the "was resumed, but Claude Code did not list it"
    /// line). Anything else comes back as it is.
    /// </summary>
    public TaskComment Attributed() =>
        SessionId.HasNoValue
        && (DeskbarOpenings.Any(opening => Markdown.StartsWith(opening, StringComparison.Ordinal))
            || (Markdown.StartsWith("Session ", StringComparison.Ordinal) && Markdown.Contains("was resumed, but Claude Code did not list it", StringComparison.Ordinal)))
            ? this with { SessionId = DeskbarAuthor }
            : this;

    /// <summary>
    /// When the comment was settled (Ruling 62), or none. A settled comment is kept, never deleted, and the tasks window
    /// folds it away behind one line. An init property like the task's flags: a comment saved before it existed is not
    /// settled. `task answered` settles the session's comments since its ask; a session settles one of its own, Petre any.
    /// </summary>
    public Maybe<DateTimeOffset> Settled { get; init; }

    /// <summary>
    /// This comment is a question asked of Petre (task 968c2216, Petre: "when asking a question, let that also be present
    /// in the chat"). TaskBoard.Ask writes the question into the thread as well as onto the line, so the thread reads as
    /// a conversation: the question, then his reply under it. Its author is whoever asked (a session, or Deskbar for its
    /// own asks). The tasks panel draws it in the question's orange and, while the line is open and the thread shows it,
    /// leaves the question off the line. An init property like <see cref="Settled"/>: a comment saved before it existed
    /// is not a question.
    /// </summary>
    public bool Asks { get; init; }
}

/// <summary>
/// One task. <see cref="WorkspaceId"/> is none for a free task. <see cref="StatusLine"/> is the short line a session
/// keeps current ("waiting for CI on #398"), "" when there is none. <see cref="Question"/> is what a session asked
/// Petre while the task is <see cref="TaskStatus.WaitingOnYou"/>. <see cref="UpdatedAt"/> moves on every change so a
/// later sync can tell which copy is newer. <see cref="Raised"/> is set when the task is made, when a session asks and
/// when the task becomes Done, and by nothing else; the pinned block is ordered by <see cref="Pin"/>, with Raised
/// breaking ties (the latest on top, the block's whole order before it could be dragged).
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
    /// The task's place in its part of the pinned "Waiting on you" block, the questions or the Done tasks (task f41d7857,
    /// Petre: "drag and drop of tasks waiting on me doesn't work ... it should"): smaller is higher, like
    /// <see cref="Rank"/>, and only the order of the pins within one part means anything. Joining a part (a session's
    /// ask, Claude's finish, a status set by hand) puts the task on top of it (TaskBoard.TopPin); a drag within the part
    /// (TaskBoard.PlacePinned) takes the middle of its new neighbours. Its quadrant rank is left alone meanwhile, so the
    /// task goes back to where it was in its quadrant once it is answered or put back to work. Two equal pins (tasks
    /// made in code, a file from before the field) fall back to the latest raised first, the block's order before it
    /// could be dragged.
    /// </summary>
    public double Pin { get; init; }

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
    /// The task this one is a subtask of, or none (subtasks spec, docs/superpowers/specs/2026-11-24-subtasks-design.md).
    /// One level only: TaskBoard.SetParent refuses a parent that is itself a subtask and a task that has subtasks, so a
    /// family is a parent and its children. Why it exists: a session joining a subtask is briefed on the parent, the
    /// siblings and their sessions (SessionBriefing.Family), so Petre can add one with only a title. An init property
    /// like GoalId, stored as an optional `parentId`; a link naming a task that no longer exists reads as no parent
    /// (TaskBoard.Family), and nothing prunes it on load, for the same reason GoalId is not pruned.
    /// </summary>
    public Maybe<Guid> ParentId { get; init; }

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
    /// The question Deskbar set aside because Claude is working on the task again after Petre's reply (RepliedTasks,
    /// task 8bd18246), or none. While it is set the task is In progress and Claude's, so it is off the "Waiting on you"
    /// block; the session is asked at the end of its turn whether the question still holds (the Stop hook's
    /// SessionBriefing.SetAsideNudge): `task ask` asks again, `task answered` settles it, and either clears this. If no
    /// session of the task is busy any more and it did neither, the question is restored and the task waits again
    /// (RepliedTasks.Restore, TaskBoard.RestoreQuestion). Any status change away from In progress clears it
    /// (TaskBoard.Change), and so does Petre assigning the task (TaskBoard.Assign). A stored optional field, read tolerantly.
    /// </summary>
    public Maybe<string> QuestionSetAside { get; init; }

    /// <summary>
    /// The answers the session offered with the question the task waits on (AnswerOptions.cs; the inbox's answer buttons),
    /// in the order it gave them, or none. They belong to the OPEN question only: TaskBoard clears them whenever
    /// <see cref="Question"/> goes, whichever change took it (answered, done, a close, a status by hand, the user's reply
    /// setting the question aside), and a new ask brings its own or none, so a button never answers a question the user
    /// is no longer being asked. A question that comes back from being set aside (Restored) comes back without them: the
    /// user had already answered it once. An init property like the flags, stored as an optional `questionOptions` list
    /// of strings, left out of the file while empty; absent in a file means none.
    /// </summary>
    public IReadOnlyList<string> QuestionOptions { get; init; } = [];

    /// <summary>
    /// Whether the question is a DEPLOY HAND-OVER rather than a question (`task ask - --deploy`, task a87891ca): the change
    /// is merged and built and only the user's deploy is left ("Redeploy the portal stack in Portainer"), so there is
    /// nothing to answer. Petre, on an iam.ge task drawn as "Waits for your answer": "it's not a question. it should
    /// qualify as merged, built, waiting for deploy". The task still waits on him (Waiting on you, at the top with the
    /// questions), but the Inbox draws it as waiting for the deploy (InboxMark.WaitsForDeploy, DeployWait, task 1565789d)
    /// even before Deskbar has measured the merge, Deskbar's deploy check finds it by this flag (DeployWaits), and `task deployed` settles it
    /// (TaskBoard.MarkDeployed). It lives as long as the question or the question set aside: TaskBoard drops it once both
    /// are gone (HandOverKept), and every ask sets it afresh. Stored as an optional `questionIsDeploy`, written only when
    /// true; absent means a plain question.
    /// </summary>
    public bool QuestionIsDeploy { get; init; }

    /// <summary>Whether the task waits on the user for a deploy hand-over (<see cref="QuestionIsDeploy"/>), not a question.</summary>
    public bool AwaitsDeploy => Status == TaskStatus.WaitingOnYou && Question.HasValue && QuestionIsDeploy;

    /// <summary>
    /// This task with Deskbar's set-aside lines (TaskComment.IsSetAsideNote) folded, the way a settled comment is (Ruling
    /// 62: kept, behind one line), unless the task still holds a set-aside question. Petre, task aa42794f: "too much bla
    /// bla", over a thread with two set-aside lines and the merged line under them. A set-aside line matters only while
    /// its question is out of sight; once the question is back, answered, settled by a merge or dropped by done or a
    /// close, it is old news. TaskBoard applies this to every change and JsonTaskStore to every task it loads, so the
    /// lines already in tasks.json fold too. The fold time is UpdatedAt (the change's "now"; on load, the last change,
    /// which keeps a load repeatable). A task with nothing to fold comes back as this same instance.
    /// </summary>
    public TaskItem WithSetAsideNotesFolded() =>
        QuestionSetAside.HasNoValue && Comments.Any(c => c.IsSetAsideNote && c.Settled.HasNoValue)
            ? this with { Comments = Comments.Select(c => c.IsSetAsideNote && c.Settled.HasNoValue ? c with { Settled = UpdatedAt } : c).ToArray() }
            : this;

    /// <summary>
    /// Deskbar made this task for a live session no task knew (TaskBoard.AdoptSession; design
    /// docs/superpowers/specs/2026-11-23-tasks-open-claude-sessions-design.md, "Every live session has a task"). Kept so
    /// that when the session joins its own task a moment later (its SessionStart hook can come after the adoption), the
    /// task made for it can go, if nothing else was added to it (TaskBoard.WithoutSessionElsewhere). An init property like
    /// the flags: every other task, and every task in a file written before the mark existed, is not adopted.
    /// </summary>
    public bool Adopted { get; init; }

    /// <summary>
    /// When a snoozed task comes back, or none (task 6da8d7eb, 2026-11-25, Petre: "task should have a snooze button, which
    /// will hide it from view for a while"; TaskSnooze.cs holds the lengths). While set, the task is off the bar and off
    /// every view of the tasks panel but its "Snoozed" filter; it stays open, keeps its status, its flags and its place
    /// in its quadrant, and a session can still work on it. It comes back when the time is up (TaskBoard.WakeDue, which
    /// the app runs on a timer), when Petre wakes it (TaskBoard.Wake), or as soon as something happens that he should
    /// see: its status changes or it is raised (a session asks or marks it done), TaskBoard.Woken. An init property like
    /// the flags, stored as an optional `snoozedUntil`; absent in a file means awake.
    /// </summary>
    public Maybe<DateTimeOffset> SnoozedUntil { get; init; }

    /// <summary>Whether the task is snoozed (<see cref="SnoozedUntil"/>): hidden from the bar and the panel's usual views.</summary>
    public bool IsSnoozed => SnoozedUntil.HasValue;

    /// <summary>
    /// The schedule this task comes round on, with the model and folder its runs use, or none (task 0b78269e, recurring
    /// tasks; spec docs/superpowers/specs/2026-11-26-recurring-tasks-design.md, plan task 2). While set, Deskbar starts
    /// a Claude session on the task each time <see cref="NextRun"/> passes. An init property like the flags, stored as
    /// an optional `repeat`; absent in a file, or unreadable there, means the task does not repeat.
    /// </summary>
    public Maybe<TaskRepeat> Repeat { get; init; }

    /// <summary>
    /// When the next run is due, a UTC instant, or none (task 0b78269e). Always computed from a clock reading by the
    /// schedule (RepeatSchedule.Next), never caught up: it is kept on the task, not worked out on the fly, so a
    /// restart keeps the time the last start promised. Stored as an optional `nextRun`; a task with no repeat has none.
    /// </summary>
    public Maybe<DateTimeOffset> NextRun { get; init; }

    /// <summary>
    /// Whether the task is waiting for its next run (task 0b78269e): it repeats, is open, and sits in Backlog or Done,
    /// the two statuses a finished or not yet started run leaves it in. A run in progress (InProgress, WaitingOnYou) is
    /// not between runs, so the schedule skips an occurrence that falls while one is going rather than queueing it.
    /// </summary>
    public bool IsBetweenRuns => Repeat.HasValue && IsOpen && Status is TaskStatus.Backlog or TaskStatus.Done;

    /// <summary>
    /// Every status change the task has been through since status logging began, oldest first (task 521d2b07, the task
    /// stats; spec docs/superpowers/specs/2026-11-25-task-stats-design.md, "Where the history comes from"). Written in
    /// one place only, TaskService.Commit through TaskStatusLog.Logged, which compares the board before and after each
    /// change, so no path that moves a status can forget it. What it is for: the stats view's finish times and its
    /// open-tasks line. An init property like the flags, stored as an optional `statusLog`; a task from before it existed
    /// has none, and the stats fall back on Raised and UpdatedAt for it (TaskStats.FinishedAt).
    /// </summary>
    public IReadOnlyList<StatusChange> StatusLog { get; init; } = [];

    /// <summary>
    /// The GitHub pull requests a session working on this task opened, oldest first, each once (task e7aa49b1, Petre:
    /// "when a session creates a pr, add it to the task so that i can search by pr id in tasks"). Added by the session's
    /// PostToolUse hook when a `gh pr create` prints the new pull request's link (TaskPullRequests.Created), or by the
    /// session itself with `task pr &lt;url&gt; --session &lt;id&gt;`, through TaskBoard.AddPullRequest. What it is for: the
    /// tasks panel's search finds the task by the pull request's number (TaskSearch), in every section, closed included,
    /// and the task window lists them as links. An init property like the flags, stored as an optional `pullRequests`
    /// list of URLs; absent in a file means none.
    /// </summary>
    public IReadOnlyList<PullRequestLink> PullRequests { get; init; } = [];

    /// <summary>
    /// The task's DEPLOY pull requests, oldest first, each once (task 1565789d). Petre, on an iam.ge task whose release
    /// build opened a pull request in the stacks repository that he had to merge before redeploying in Portainer: "we need
    /// to watch that pr as the deploy pr". Such a pull request is not the task's own work: the session did not open it
    /// (CI did, so the `gh pr create` hook never saw it), it lives in another repository, and its merge is the step BEFORE
    /// the deploy, not the deploy. So it is kept apart from <see cref="PullRequests"/>: the minute poll (RepoWatch) asks
    /// GitHub about it and tells the session once when it is merged or closed, even when it was merged already by the time
    /// it was registered, but never asks the session to rebase it, never follows its merge commit's runs, never counts it
    /// for the merged ring or a deploy report (RunningChanges, DeployNotices), and its merge does not settle the question
    /// that links it, since that question usually goes on to ask for the redeploy (AskedPullRequests.Watched). Added by
    /// `task pr &lt;url&gt; --deploy`, through TaskBoard.AddDeployPullRequest; stored as an optional `deployPullRequests`
    /// list of URLs.
    /// </summary>
    public IReadOnlyList<PullRequestLink> DeployPullRequests { get; init; } = [];

    /// <summary>
    /// The keys (PullRequestLink.Key) of the <see cref="DeployPullRequests"/> whose merge or close the session has been
    /// told (task 1565789d). Saved, unlike the minute poll's memory, so a restart of Deskbar does not tell the session the
    /// same merge again: a told deploy pull request leaves the poll for good. Written by TaskBoard.DeployPullRequestsEnded;
    /// stored as an optional `deployPullRequestsEnded` list.
    /// </summary>
    public IReadOnlyList<string> DeployPullRequestsEnded { get; init; } = [];

    /// <summary>
    /// The recorded pull requests reported running where their app is deployed, oldest first, each once (task 8470d176,
    /// phase 2; Petre: "it's not about deskbar only, but all other software tasks"). Deskbar checks its own repository by
    /// itself against the build it runs, but it cannot see another app's servers, so the session that did the work checks
    /// the deployed app's logs or health page for the version carrying the merge and reports it (`task deployed`), or
    /// Petre marks it by hand ("Mark deployed" on a line's menu). Written only by TaskBoard.MarkDeployed. What it is for:
    /// a Done task's tick stays a hollow ring until each of its pull requests outside this build's repository is here
    /// (RunningChanges.Of). An init property like PullRequests, stored as an optional `deployed` list; absent means none.
    /// </summary>
    public IReadOnlyList<DeployedPullRequest> Deployed { get; init; } = [];

    // THE SESSION CANNOT TELL WHETHER IT DEPLOYED (task 485ba96b, 2026-11-28). Petre: "if the task has no way of knowing if
    // it's been deployed, show a chip for not knowing, not a question mark, and state that the session has no access to
    // logs or whatever else it needs to identify whether deploy was successful". A session whose change runs somewhere it
    // cannot see (no logs, no health page, no deploy output it can reach) had two ways out, both wrong: ask "did it
    // deploy?", which drew the "?" bubble for a check rather than a decision, or report a deploy it never saw. Now it says
    // so (`task deployed - --unknown`, the reason on stdin), the task's delivery is DeliveryStage.Unchecked (a mark of its
    // own on every surface, with the reason as its words), and the session may finish with `task done`: the deploy step
    // is as settled as the session can make it. A later deploy report (the session's, or "Mark deployed") clears it.
    /// <summary>
    /// Why the task's session cannot tell whether its change was deployed, and when it said so, or none (task 485ba96b,
    /// the comment above). Written only by TaskBoard.MarkDeployUnknown and cleared by TaskBoard.MarkDeployed. Stored as the
    /// optional `deployUnknown` and `deployUnknownAt`; absent means none.
    /// </summary>
    public Maybe<DeployUnknownReport> DeployUnknown { get; init; }

    /// <summary>
    /// The task this one duplicates, or none (task links, docs/superpowers/specs/2026-11-25-task-links-design.md; Petre:
    /// "introduce a duplicate status in tasks"). Set only together with Closed, by TaskBoard.MarkDuplicate: a duplicate is
    /// a kind of close, so every rule about closed tasks holds for it unchanged, and the views say "Duplicate" where they
    /// would say "Closed" (<see cref="IsDuplicate"/>). Any change that opens the task again drops it (TaskBoard.Unmarked).
    /// An init property like ParentId, stored as an optional `duplicateOf`; a link naming a task that no longer exists
    /// reads as a plain close.
    /// </summary>
    public Maybe<Guid> DuplicateOf { get; init; }

    /// <summary>
    /// The tasks this one was linked to as related (task links spec; Petre: "also introduce related to"). Stored on the
    /// task the link was made FROM only, and read both ways by TaskBoard.RelatedOf, so a link never has two copies to
    /// disagree. An init property, stored as an optional `related` array; absent in a file means none.
    /// </summary>
    public IReadOnlyList<Guid> Related { get; init; } = [];

    /// <summary>A closed task that duplicates another: the views call it "Duplicate", not "Closed".</summary>
    public bool IsDuplicate => Status == TaskStatus.Closed && DuplicateOf.HasValue;

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
