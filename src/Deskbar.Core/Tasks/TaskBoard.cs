using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// Every rule for changing tasks, as pure functions: a method returns a new board and never reads a clock (callers
// pass "now"), so the rules are tested without waiting. Each change stamps UpdatedAt = now on exactly the tasks it
// touched. Design: docs/superpowers/specs/2026-11-22-tasks-board-design.md ("What Petre decided", "Calls made after
// the builder check").
//
// The rules that shape this file, each from the spec:
//   * Only Petre closes. A session joins, keeps a status line, asks and marks Done; Close is Petre's, so it is the
//     one way to Closed (SetStatus refuses Closed and points at Close).
//   * A closed task takes none of a session's changes (join, status line, ask, done): Petre has closed it. One
//     exception (task 327e3cd0): the session that worked on it may join it again, which reopens it, so resuming a
//     session to carry on with a closed task does not make a duplicate. Renaming, moving, commenting and setting a
//     status by hand stay open to Petre.
//   * The order (Task 12, Rulings 24, 25 and 28; TaskOrder.cs): Waiting on you is pinned on top, and the Done tasks under
//     the questions (task eefb986a; TaskStatusPinning), each part a new one on top and otherwise in the order Petre
//     drags it to (Pin, task f41d7857). Below them
//     come Covey's quadrants I to IV, which the two flags name, each in Petre's manual order, the tasks' Rank. A new
//     task goes to the top of its quadrant, a Done task keeps its rank while pinned and goes back to that place when it
//     is resumed, a closed one leaves and a reopened one comes back at the top. Undo on a close is not a reopening: the line never left its place on
//     screen, so it keeps its rank (UndoClose).
//   * Raised moves when a task is made, when a session asks and when a task becomes Done, and on nothing else; Rank
//     moves only on Petre's drag, a flag, a reopening and a new task. So a status line, a comment or a session ending
//     never reorders the list.
//   * A session works on one task at a time. Joining or creating a task ends that session's live entry on any other
//     open task, so TaskOf and Answered can only ever mean the task it is on now (controller ruling 4, which the
//     hooks rely on).
//   * Answering moves that session's task from Waiting on you back to In progress and touches no other task. Since
//     Ruling 49 it runs only when the session says its question is settled (`task answered`), and since 2026-11-23 a
//     prompt no longer moves a Done task either: the session reopens it with Join when Petre's message is more work.
public sealed record TaskBoard(IReadOnlyList<TaskItem> Tasks)
{
    public static readonly TaskBoard Empty = new(Array.Empty<TaskItem>());

    /// <summary>The shortest id prefix <see cref="Find"/> accepts, so a typo cannot hit a task by luck.</summary>
    const int MinPrefix = 4;

    const string TaskIsClosed = "The task is closed.";
    const string TaskIsNotClosed = "The task is not closed.";
    const string UseClose = "Use close to close a task";
    const string NotPinned = "The task is neither waiting on you nor done";

    // --- making tasks ------------------------------------------------------------------------------

    /// <summary>
    /// One of Petre's notes: Backlog, no sessions. Every add is a new task, even with a title seen before. It takes the
    /// flags of <paramref name="quadrant"/> (Ruling 37: the add box's important and urgent toggles, or `task add
    /// --important --urgent`) and lands at the top of that quadrant; no flags, quadrant IV, is the default.
    ///
    /// With a <paramref name="parent"/> (subtasks spec) the note is that task's subtask: the parent must exist and must not
    /// be a subtask itself (one level). The note goes on the workspace it is GIVEN, none included (final review, finding
    /// 4: "none" picked on the add box's chip, or `--workspace none`, means none). The callers fill in the parent's
    /// workspace where nothing was chosen: the add boxes set their chip to it, `task add --parent` without --workspace
    /// looks it up, and AddSubtask passes it. A CLOSED parent is reopened (<see cref="WithParentReopened"/>): the new
    /// note is open work under it.
    /// </summary>
    public Result<(TaskBoard Board, TaskItem Task)> AddNote(string title, Maybe<Guid> workspace, DateTimeOffset now, Guid newId,
        Quadrant quadrant = Quadrant.IV, Maybe<Guid> parent = default) =>
        // The lambda's parameter is typed: Result<Maybe<T>> has a second Map that maps inside the Maybe.
        CleanTitle(title).Bind(clean => ParentFor(parent).Map((Maybe<TaskItem> up) => WithAppended(
            WithFlags(NewTask(newId, clean, workspace, TaskStatus.Backlog, [], now), quadrant)
                with { Rank = TopRank(quadrant, newId), ParentId = up.Map(p => p.Id) })))
            .Bind(added => added.Board.WithParentReopened(added.Task.Id, now).Map(board => (board, added.Task)));

    /// <summary>
    /// A session splits its work (`task subtask`): a Backlog subtask assigned to Claude, so the next session on the
    /// workspace is offered it. Its parent is the session's own task, or, when that is already a subtask, that task's
    /// parent, so the new one is a SIBLING (one level is the limit, and splitting your own piece is exactly when this is
    /// wanted). It takes the parent's workspace. The session does not join it and its own task is untouched. A session
    /// with no open task is refused.
    /// </summary>
    public Result<(TaskBoard Board, TaskItem Task)> AddSubtask(string sessionId, string title, DateTimeOffset now, Guid newId) =>
        TaskOf(sessionId).ToResult($"Session {sessionId} has no open task")
            // The parent as "Add subtask…" finds it (resolved on the board, so a link naming nothing is no parent), and
            // the new task goes on that parent's workspace.
            .Map(own => SubtaskParentFor(own.Id).GetValueOrDefault(own))
            .Bind(up => AddNote(title, up.WorkspaceId, now, newId, Quadrant.IV, up.Id))
            .Map(added => (added.Task with { Assignee = Assignee.Claude }) is var claudes
                ? (added.Board.Replaced(claudes), claudes)
                : added);

    /// <summary>
    /// A task a session makes for its own work: In progress, with that session on it. The session leaves whatever
    /// other task it was live on (see the header).
    /// </summary>
    public Result<(TaskBoard Board, TaskItem Task)> Create(string title, Maybe<Guid> workspace, TaskSession session, DateTimeOffset now, Guid newId) =>
        CleanTitle(title).Map(clean =>
            WithoutSessionElsewhere(session.Id, newId, now)
                .WithAppended(NewTask(newId, clean, workspace, TaskStatus.InProgress, [session], now) with { Rank = TopRank(Quadrant.IV, newId), Assignee = Assignee.Claude }));

    // --- what a session does -----------------------------------------------------------------------

    /// <summary>
    /// A session takes the task up: it becomes Claude's and In progress. A task already waiting on Petre keeps
    /// waiting, because the question is still unanswered. A session with that id already on the task (a resumed
    /// conversation keeps its id) is replaced, live again, rather than listed twice. The session leaves whatever other
    /// task it was live on (see the header); a refused join (a closed task) leaves it where it was.
    ///
    /// A CLOSED TASK THE SESSION ITSELF WORKED ON is reopened first (task 327e3cd0, <see cref="ReopenedForItsSession"/>),
    /// then joined as any other: the session carries on with the work the user closed, instead of making a duplicate.
    /// </summary>
    public Result<TaskBoard> Join(Guid id, TaskSession session, DateTimeOffset now) =>
        ReopenedForItsSession(id, session.Id, now).Bind(board => board.ChangeOpen(id, t => t with
        {
            Sessions = WithSession(t.Sessions, session),
            Status = t.Status == TaskStatus.WaitingOnYou ? t.Status : TaskStatus.InProgress,
            // Ruling 57: the session works on it now, so it is Claude's; a task still waiting on Petre stays his.
            Assignee = t.Status == TaskStatus.WaitingOnYou ? t.Assignee : Assignee.Claude,
            UpdatedAt = now,
        })).Map(joined => joined.WithoutSessionElsewhere(session.Id, id, now));

    // RESUMING A SESSION ON A CLOSED TASK REOPENS IT (task 327e3cd0). Petre closed a task, then resumed its session to keep
    // talking about the same subject; the hooks told the session its task was closed and new work needed a new task, so it
    // made a duplicate, and `task join` on the closed one answered "The task is closed.". Now a session that was on the
    // task (an entry under its id that /clear did not replace: a replaced session handed the task on, Ruling 19) gets the
    // closed task back through Reopen, which also brings back a closed parent and puts it at the top of its quadrant
    // (Ruling 25); Join then makes it In progress and Claude's. Any other session, and any task that is not closed, is left
    // to the join's usual rules, so a stranger joining a closed task is still refused.
    Result<TaskBoard> ReopenedForItsSession(Guid id, string sessionId, DateTimeOffset now) =>
        Tasks.FirstOrDefault(t => t.Id == id) is { Status: TaskStatus.Closed } task && WasItsSession(task, sessionId)
            ? Reopen(id, now)
            : this;

    static bool WasItsSession(TaskItem task, string sessionId) =>
        task.Sessions.Any(s => s.Id == sessionId && !s.Replaced);

    /// <summary>
    /// A session coming back to its OWN task (a resumed conversation keeps its id; RemoteControl's SessionStart finds
    /// the task by TaskBoard.LastTaskOf): live on it again, as <see cref="Join"/> makes it, but the task KEEPS ITS
    /// STATUS (Ruling 34). Resuming a session to look at it is not continuing the work; only the session itself moves
    /// the task on (<see cref="Join"/> for a Done one, <see cref="Answered"/> for a waiting one). A closed task refuses, and the session
    /// leaves any other open task it was live on, both as Join does.
    /// </summary>
    public Result<TaskBoard> Rejoin(Guid id, TaskSession session, DateTimeOffset now) =>
        ChangeOpen(id, t => t with { Sessions = WithSession(t.Sessions, session), UpdatedAt = now })
            .Map(joined => joined.WithoutSessionElsewhere(session.Id, id, now));

    /// <summary>
    /// The short line a session keeps current. Does not move <see cref="TaskItem.Raised"/>. The same line again returns
    /// this same board (review of PR B, M-3): every prompt's reminder asks a session to keep its line current, so it often
    /// sets what is already there, and that must not save the file or redraw the bar. A closed task still refuses.
    /// </summary>
    public Result<TaskBoard> SetStatusLine(Guid id, string text, DateTimeOffset now) =>
        (text ?? "").Trim() is var line && Tasks.FirstOrDefault(t => t.Id == id) is { IsOpen: true } task && task.StatusLine == line
            ? this
            : ChangeOpen(id, t => t with { StatusLine = line, UpdatedAt = now });

    /// <summary>A session needs a decision: the task waits on Petre, shows the question and moves to the top.</summary>
    public Result<TaskBoard> Ask(Guid id, string question, DateTimeOffset now) =>
        CleanText(question, "An ask needs a question").Bind(q =>
            ChangeOpen(id, t => t with { Status = TaskStatus.WaitingOnYou, Question = q.Trim(), Raised = now, UpdatedAt = now, Assignee = Assignee.Me, WaitingSince = now, Pin = TopPin(TaskStatus.WaitingOnYou, id) }));

    /// <summary>
    /// The session's question is settled (`task answered`). Its task, when Waiting on you or Done, goes back to In progress and drops
    /// the question. Anything else, or a session with no open task, returns this same board, so the caller can
    /// tell nothing changed by reference and skip the save.
    /// </summary>
    public TaskBoard Answered(string sessionId, DateTimeOffset now) =>
        TaskOf(sessionId)
            .Where(t => t.Status is TaskStatus.WaitingOnYou or TaskStatus.Done)
            // RULING 62: answering a QUESTION settles the session's comments written since it was asked (a Done task resuming
            // was no question, so it settles none). Kept, not deleted.
            .Map(t => Replaced(t with
            {
                Status = TaskStatus.InProgress, Question = Maybe<string>.None, UpdatedAt = now, Assignee = Assignee.Claude,
                Comments = t.Status == TaskStatus.WaitingOnYou ? SettledSinceAsk(t, sessionId, now) : t.Comments,
            }))
            .GetValueOrDefault(this);

    /// <summary>
    /// Deskbar settles a waiting task's question itself, because the pull requests it links are merged or closed
    /// (AskedPullRequests, task b7f987b4). What `task answered` does, by the task rather than by a session: the task goes
    /// back to In progress and to Claude, so it leaves the bar, the question goes, and the comments sessions wrote since
    /// the ask are settled (Ruling 62; any session's, since nothing records which one asked). Deskbar's own line,
    /// <paramref name="note"/>, goes into the thread in the same change, saying why.
    /// <para>
    /// Only the question that was read is settled: a task no longer waiting, closed, or waiting on a DIFFERENT question
    /// (the session asked again while GitHub was being asked) returns this same board, so the caller saves nothing.
    /// </para>
    /// </summary>
    public TaskBoard SettleQuestion(Guid id, string question, string note, DateTimeOffset now, Guid noteId) =>
        Tasks.FirstOrDefault(t => t.Id == id) is { IsOpen: true, Status: TaskStatus.WaitingOnYou, Question: { HasValue: true } asked } task
        && asked.Value == question
            ? Replaced(task with
            {
                Status = TaskStatus.InProgress, Question = Maybe<string>.None, UpdatedAt = now, Assignee = Assignee.Claude,
                Comments = task.Comments
                    .Select(c => c.BySession && c.At >= task.WaitingSince.GetValueOrDefault(task.Raised) && c.Settled.HasNoValue ? c with { Settled = now } : c)
                    .Append(new TaskComment(noteId, TaskComment.DeskbarAuthor, now, note))
                    .ToArray(),
            })
            : this;

    /// <summary>
    /// The session ended: it is marked ended on every task it is live on, and those tasks keep their status (a task
    /// whose sessions all ended stays open). No live session with that id returns this same board.
    /// </summary>
    public TaskBoard SessionEnded(string sessionId, DateTimeOffset now) =>
        Tasks.Any(t => LiveOn(t, sessionId))
            ? new TaskBoard(Tasks.Select(t => LiveOn(t, sessionId)
                ? t with
                {
                    Sessions = t.Sessions.Select(s => s.Id == sessionId && s.IsLive ? s with { Ended = now } : s).ToArray(),
                    UpdatedAt = now,
                }
                : t).ToArray())
            : this;

    /// <summary>
    /// How long after a /clear a session starting with source "clear" in the same folder is taken for its successor
    /// (Ruling 19, <see cref="ClearedTaskIn"/>). The two hooks come from one keypress, so a minute is generous.
    /// </summary>
    public static readonly TimeSpan ClearedWithin = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Petre ran /clear in the session (its SessionEnd hook said reason "clear"; Ruling 19): every entry of it on an
    /// open task is marked replaced now (<see cref="TaskSession.ReplacedAt"/>) and ended if it was live, and those tasks
    /// keep their status. The conversation it held is over, so SessionKeeper never resumes it; the fresh one that
    /// follows joins the task through <see cref="ClearedTaskIn"/>. No entry of that id returns this same board.
    /// </summary>
    public TaskBoard SessionCleared(string sessionId, DateTimeOffset now) =>
        Tasks.Any(t => t.IsOpen && t.Sessions.Any(s => s.Id == sessionId))
            ? new TaskBoard(Tasks.Select(t => t.IsOpen && t.Sessions.Any(s => s.Id == sessionId)
                ? t with
                {
                    Sessions = t.Sessions.Select(s => s.Id == sessionId
                        ? s with { Ended = s.Ended.HasValue ? s.Ended : now, ReplacedAt = now }
                        : s).ToArray(),
                    UpdatedAt = now,
                }
                : t).ToArray())
            : this;

    /// <summary>
    /// The open task whose session was cleared (<see cref="SessionCleared"/>) within <see cref="ClearedWithin"/> before
    /// <paramref name="now"/> in a folder <paramref name="inFolder"/> accepts, the latest clear first: where a session
    /// that starts with source "clear" there belongs. The caller compares folders (RemoteControl normalises both sides,
    /// FolderPath.Same), so this needs no file system.
    /// </summary>
    public Maybe<TaskItem> ClearedTaskIn(Func<string, bool> inFolder, DateTimeOffset now) =>
        Tasks.Where(t => t.IsOpen)
            .SelectMany(t => t.Sessions
                .Where(s => s.ReplacedAt.HasValue && s.ReplacedAt.Value <= now && now - s.ReplacedAt.Value <= ClearedWithin && inFolder(s.Folder))
                .Select(s => (Task: t, At: s.ReplacedAt.Value)))
            .OrderByDescending(x => x.At)
            .Select(x => x.Task)
            .TryFirst();

    /// <summary>
    /// Makes the board agree with Claude Code's own list of live sessions (`claude agents --json`, ClaudeAgents), which
    /// is the truth: the SessionEnd hook is only a hint. It never arrives after a crash or a reboot, and a background
    /// session sends it at the end of every turn while the list still shows the session.
    ///   * END: every live session entry whose id the list does not show is marked ended now.
    ///   * REVIVE: for an id the list shows, the LATEST entry (by Joined, across all tasks) that had ended is live again.
    ///     Older entries of that id stay as they were, so one session is live on one task only.
    /// Only sessions change, never a task's status, and a task's UpdatedAt moves only when one of its sessions did, so a
    /// poll that finds nothing returns this same board and the caller saves nothing. Tasks without sessions (Petre's
    /// notes) are not touched. The caller must not pass an unreadable list as an empty one: an empty list here means
    /// every session is gone, and "cannot tell" ends nothing (global constraints), which is the caller's rule.
    /// </summary>
    public TaskBoard Reconcile(IReadOnlyCollection<ClaudeAgent> live, DateTimeOffset now)
    {
        var listed = live.Select(a => a.SessionId).ToHashSet();
        var latest = Tasks
            .SelectMany(t => t.Sessions.Select(s => (Task: t.Id, Session: s)))
            .Where(x => listed.Contains(x.Session.Id))
            .GroupBy(x => x.Session.Id)
            .Select(g => (Task: g.OrderByDescending(x => x.Session.Joined).First().Task, Session: g.Key))
            .ToHashSet();
        var reconciled = Tasks.Select(t => (Old: t, Sessions: t.Sessions.Select(s =>
            listed.Contains(s.Id)
                ? (latest.Contains((t.Id, s.Id)) && s.Ended.HasValue ? s with { Ended = Maybe<DateTimeOffset>.None } : s)
                : (s.IsLive ? s with { Ended = now } : s)).ToArray())).ToArray();
        return reconciled.All(x => x.Sessions.SequenceEqual(x.Old.Sessions))
            ? this
            : new TaskBoard(reconciled.Select(x => x.Sessions.SequenceEqual(x.Old.Sessions) ? x.Old : x.Old with { Sessions = x.Sessions, UpdatedAt = now }).ToArray());
    }

    /// <summary>
    /// A live session Claude Code lists that no task knows (started in a terminal, or by another tool): it becomes a
    /// new In progress Claude task so it shows on the board. The title is the agent's name, else the last segment of
    /// its folder, else "Claude session". A session that already has a task, open or closed, is refused, so one session
    /// never makes two tasks (a closed one is Petre's decision to leave alone).
    /// </summary>
    public Result<(TaskBoard Board, TaskItem Task)> AdoptSession(ClaudeAgent agent, Maybe<Guid> workspace, DateTimeOffset now, Guid newId) =>
        Tasks.Any(t => t.Sessions.Any(s => s.Id == agent.SessionId))
            ? Result.Failure<(TaskBoard, TaskItem)>($"Session {agent.SessionId} already has a task")
            : Create(AdoptedTitle(agent), workspace, new TaskSession(agent.SessionId, agent.Folder, now, Maybe<DateTimeOffset>.None), now, newId)
                // Marked, so it can go again if the session joins its own task after all (WithoutSessionElsewhere).
                .Map(made => (made.Board.Replaced(made.Task with { Adopted = true }), made.Task with { Adopted = true }));

    static string AdoptedTitle(ClaudeAgent agent) =>
        !string.IsNullOrWhiteSpace(agent.Name) ? agent.Name
        : agent.Folder.TrimEnd('\\', '/').Split('\\', '/').Last() is { Length: > 0 } folder ? folder
        : "Claude session";

    /// <summary>
    /// Claude's clear finish. The task stays in the list, waiting for Petre to close it. Becoming Done stamps
    /// <see cref="TaskItem.Raised"/>, which orders the Done group; a task already Done keeps its stamp.
    /// </summary>
    public Result<TaskBoard> MarkDone(Guid id, DateTimeOffset now) =>
        ChangeOpen(id, t => BecameDone(t, now) with { Question = Maybe<string>.None, UpdatedAt = now, Assignee = Assignee.Me });

    // --- what Petre does ---------------------------------------------------------------------------

    /// <summary>
    /// Petre closes the task: it leaves the lists. Closing a closed task is refused, and so is closing a parent while any
    /// of its subtasks is still open (task 842e1ba4, Petre: "closing a parent task shouldn't be possible if not all
    /// children are also closed"): the parent stands for the whole piece of work, so it cannot be ticked off while part
    /// of that work is still open. The refusal says how many are left (<see cref="TaskViews.CloseSubtasksFirst"/>), and
    /// every way to close (the bar's tick and menu, the window's, `task close`) comes through here, so none can skip it.
    /// </summary>
    //
    // CLAUDE STILL AT IT (task 37cd7081, 2026-11-25, Petre: "let's prohibit closing tasks that aren't yet done", then,
    // offered the choice, "live session, but offer stop"). Closing ends no session, so a task closed under a session
    // still working left that session running with nowhere to report: every one of its task commands is refused on a
    // closed task, and a PR it had in flight could finish or stall with no word on the task. So an open task that is
    // not Done and has a live session (<see cref="ClaudeStillWorking"/>) refuses too, with
    // <see cref="TaskViews.ClaudeStillWorkingRefusal"/>. Only that: Petre's own notes, and tasks whose sessions have
    // all ended, close as before, and a Done task closes whatever its session is doing (the keeper stops it once idle).
    // <paramref name="stoppingClaude"/> is the way past it, for "Stop Claude and close" (ClaudeTaskActions.StopAndClose
    // closes with it and then stops the sessions, and `task close --stop-claude`). It never skips the subtask rule.
    public Result<TaskBoard> Close(Guid id, DateTimeOffset now, bool stoppingClaude = false) =>
        // Only an OPEN task is checked for open subtasks, so closing a closed task still says it is closed.
        Tasks.FirstOrDefault(t => t.Id == id) is { IsOpen: true } && OpenSubtaskCount(id) is var open and > 0
            ? Result.Failure<TaskBoard>(TaskViews.CloseSubtasksFirst(open))
            // ClaudeStillWorking is false for a closed task, so closing a closed task still says it is closed.
            : !stoppingClaude && Tasks.FirstOrDefault(t => t.Id == id) is { } task && ClaudeStillWorking(task)
                ? Result.Failure<TaskBoard>(TaskViews.ClaudeStillWorkingRefusal)
                : ChangeOpen(id, t => t with { Status = TaskStatus.Closed, Question = Maybe<string>.None, UpdatedAt = now });

    /// <summary>
    /// Whether closing the task would leave Claude working on it (see <see cref="Close"/>): it is open, not Done, and a
    /// session is live on it. The bar's and the window's ticks ask this first and offer "Stop Claude and close" instead.
    /// </summary>
    public static bool ClaudeStillWorking(TaskItem task) =>
        task.IsOpen && task.Status != TaskStatus.Done && task.HasLiveSession;

    /// <summary>
    /// A closed task comes back: In progress while a session is live on it, else Backlog, at the top of its quadrant
    /// (Ruling 25: it left the list, so it comes back the way a new task arrives). A subtask brings its closed parent
    /// back with it (<see cref="WithParentReopened"/>).
    /// </summary>
    public Result<TaskBoard> Reopen(Guid id, DateTimeOffset now) =>
        Tasks.FirstOrDefault(t => t.Id == id) is { IsOpen: true }
            ? Result.Failure<TaskBoard>(TaskIsNotClosed)
            : Change(id, t => Reopened(t, now)).Bind(board => board.WithParentReopened(id, now));

    // What a reopening makes of a closed task, shared by Reopen and by a parent coming back with its subtask. The
    // assignee is derived from what the task is now (Ruling 57 review): a session still live on it is Claude's work in
    // progress, otherwise it is back in the backlog and his.
    TaskItem Reopened(TaskItem t, DateTimeOffset now) => OnTopIfClosed(t) with
    {
        Status = t.HasLiveSession ? TaskStatus.InProgress : TaskStatus.Backlog,
        Assignee = Assignee.DerivedFor(t.HasLiveSession, t.HasLiveSession ? TaskStatus.InProgress : TaskStatus.Backlog),
        UpdatedAt = now,
    };

    /// <summary>
    /// Undo on a line just closed (the bar's and the window's six seconds). UNDO MEANS EXACTLY UNDO (Ruling 31): the
    /// task comes back with the status it had before the close, <paramref name="was"/>, whatever it was (a note set to
    /// In progress by hand stays In progress), and a task that waited on Petre gets its <paramref name="question"/> back
    /// and goes back into the pinned block. Neither Raised nor the rank moves, so the line is exactly where it was: it
    /// never left the screen. A task that is not closed is refused, and so is Closed as the status to come back to. A
    /// subtask whose parent was closed in those six seconds brings the parent back (<see cref="WithParentReopened"/>).
    /// </summary>
    public Result<TaskBoard> UndoClose(Guid id, TaskStatus was, Maybe<string> question, DateTimeOffset now) =>
        Tasks.FirstOrDefault(t => t.Id == id) is { IsOpen: true } ? Result.Failure<TaskBoard>(TaskIsNotClosed)
        : was == TaskStatus.Closed ? Result.Failure<TaskBoard>("A closed task cannot come back closed")
        : Change(id, t => t with
        {
            Status = was,
            Question = was == TaskStatus.WaitingOnYou ? question : Maybe<string>.None,
            UpdatedAt = now,
        }).Bind(board => board.WithParentReopened(id, now));

    /// <summary>
    /// Any status but Closed, by hand, from any status (a closed task set to Done is reopened as Done, at the top of
    /// its quadrant as <see cref="Reopen"/> brings it). Closed goes through <see cref="Close"/>, the one way to close.
    /// Leaving Waiting on you drops the question; setting it by hand does not move <see cref="TaskItem.Raised"/>, since
    /// no session asked anything. Becoming Done by hand stamps it, as <see cref="MarkDone"/> does. A closed subtask
    /// set to a status is open again, so its closed parent comes back too (<see cref="WithParentReopened"/>).
    /// </summary>
    public Result<TaskBoard> SetStatus(Guid id, TaskStatus status, DateTimeOffset now) =>
        status == TaskStatus.Closed
            ? Result.Failure<TaskBoard>(UseClose)
            : Change(id, t => (status == TaskStatus.Done ? BecameDone(OnTopIfClosed(t), now) : OnTopIfClosed(t)) with
            {
                Status = status,
                Question = status == TaskStatus.WaitingOnYou ? t.Question : Maybe<string>.None,
                // The assignee follows the status set by hand (Ruling 57 review): waiting on him or done is his; leaving
                // either for in progress or the backlog derives it again (a Claude task goes back to Claude, a note stays
                // his); any other change of status leaves it alone.
                Assignee = status is TaskStatus.WaitingOnYou or TaskStatus.Done ? Assignee.Me
                    : t.Status is TaskStatus.WaitingOnYou or TaskStatus.Done ? Assignee.DerivedFor(t.IsClaudes, status)
                    : t.Assignee,
                // When it began waiting (Ruling 62 review): what `answered` settles comments since.
                WaitingSince = status == TaskStatus.WaitingOnYou && t.Status != TaskStatus.WaitingOnYou ? now : t.WaitingSince,
                // Joining a part of the pinned block (the questions, or the Done tasks) puts it on top of that part, as
                // an ask or Claude's finish does (task f41d7857); a task already there keeps its place.
                Pin = status.IsPinned() && t.Status != status ? TopPin(status, id) : t.Pin,
                Adopted = false,
                UpdatedAt = now,
            }).Bind(board => board.WithParentReopened(id, now));

    // --- the flags and the order (Task 12; TaskOrder.cs) -------------------------------------------

    /// <summary>
    /// A toggle on a line, or `task flag`: one flag on or off. The task goes to the top of the quadrant it lands in, as
    /// a new arrival there. A flag set to what it already is returns this same board. A task waiting on Petre takes
    /// its flags (it shows them) but stays pinned until answered; a closed task is refused.
    /// </summary>
    public Result<TaskBoard> SetFlag(Guid id, TaskFlag flag, bool on, DateTimeOffset now) =>
        Lookup(id).Bind(task => ToQuadrant(task, task.Quadrant.With(flag, on), now));

    // SetFlag's move: the task to the top of `quadrant`, both flags set to match; the same quadrant returns this same
    // board. A drag between quadrants (the Matrix's cells included) goes through Place instead, which takes a place.
    Result<TaskBoard> ToQuadrant(TaskItem task, Quadrant quadrant, DateTimeOffset now) =>
        !task.IsOpen ? Result.Failure<TaskBoard>(TaskIsClosed)
        : task.Quadrant == quadrant ? this
        : Replaced(WithFlags(task, quadrant) with { Rank = TopRank(quadrant, task.Id), UpdatedAt = now, Adopted = false });

    /// <summary>
    /// A drag, or `task order`: the task goes to <paramref name="place"/> in <paramref name="quadrant"/>, and dropping
    /// it in another quadrant than its own sets that quadrant's flags. It takes the middle of its new neighbours'
    /// ranks, so only the moved task changes; when the numbers between two neighbours run out (the same gap halved
    /// some fifty times) the quadrant is numbered afresh and every task whose rank changed is stamped. A drop where the
    /// task already is returns this same board. Refused: a closed task, and a place beside a task that is not in that
    /// quadrant's order.
    ///
    /// A task waiting on Petre or Done stays pinned above the quadrants until it is answered or closed (Ruling 25, task
    /// eefb986a), but it is no longer fixed there (task f41d7857, Petre: "drag and drop of tasks waiting on me doesn't
    /// work ... it should"). In its own quadrant the place is a place in its part of the pinned block
    /// (<see cref="PlacePinned"/>), which is what a drag on the bar or in the List and `task order` mean; in another
    /// quadrant (a drag to another cell of the Matrix) the task takes that quadrant's flags, as a toggle would, and stays
    /// pinned.
    /// </summary>
    public Result<TaskBoard> Place(Guid id, Quadrant quadrant, TaskPlace place, DateTimeOffset now) =>
        Lookup(id).Bind(task =>
            !task.IsOpen ? Result.Failure<TaskBoard>(TaskIsClosed)
            : task.Status.IsPinned()
                ? task.Quadrant == quadrant ? PlacePinned(id, place, now) : ToQuadrant(task, quadrant, now)
            : IndexFor(task, quadrant, place).Map(index => Placed(task, quadrant, index, now)));

    /// <summary>
    /// A drag within the pinned block (task f41d7857): the task goes to <paramref name="place"/> among the tasks of its
    /// own part of the block, the questions or the Done tasks (<see cref="PinnedPart"/>), whatever their quadrants. The
    /// questions stay above the Done tasks (task eefb986a), so neither part is dragged into the other. It takes the
    /// middle of its new neighbours' pins (<see cref="TaskItem.Pin"/>) as <see cref="Place"/> takes ranks, numbering
    /// the part afresh when they run out. Its flags and its rank are left alone, so once answered or put back to work
    /// it goes back to its place in its quadrant. A drop where it already is returns this same board. Refused: a closed
    /// task, a task that is not pinned, and a place beside a task outside its part.
    /// </summary>
    public Result<TaskBoard> PlacePinned(Guid id, TaskPlace place, DateTimeOffset now) =>
        Lookup(id).Bind(task =>
            !task.IsOpen ? Result.Failure<TaskBoard>(TaskIsClosed)
            : !task.Status.IsPinned() ? Result.Failure<TaskBoard>(NotPinned)
            : IndexIn(PinnedPart(task.Status), task, place,
                    task.Status == TaskStatus.Done ? "the done tasks on top" : "the tasks waiting on you")
                .Map(index => Repinned(task, index, now)));

    /// <summary>
    /// One quadrant's tasks in their manual order: open, not waiting on Petre and not Done (both are pinned above every
    /// quadrant, <see cref="Pinned"/>), smallest rank first. Two equal ranks (a hand-edited file) fall back to the newest raised first, then the id, so
    /// the order is always the same.
    /// </summary>
    public IReadOnlyList<TaskItem> Group(Quadrant quadrant) =>
        Tasks.Where(t => t.IsOpen && !t.Status.IsPinned() && t.Quadrant == quadrant)
            .OrderBy(t => t.Rank).ThenByDescending(t => t.Raised).ThenBy(t => t.Id)
            .ToArray();

    public Result<TaskBoard> Rename(Guid id, string title, DateTimeOffset now) =>
        CleanTitle(title).Bind(clean => Change(id, t => t with { Title = clean, UpdatedAt = now, Adopted = false }));

    public Result<TaskBoard> MoveTo(Guid id, Maybe<Guid> workspace, DateTimeOffset now) =>
        Change(id, t => t with { WorkspaceId = workspace, UpdatedAt = now, Adopted = false });

    /// <summary>
    /// Petre hands a task to someone (Ruling 57), in the tasks window or by `task assign`. Any task, closed ones too (it
    /// stays readable history, as moving and commenting do). Only the assignee and UpdatedAt change, so the line keeps
    /// its place; the assignee it already has returns this same board.
    /// </summary>
    public Result<TaskBoard> Assign(Guid id, Assignee assignee, DateTimeOffset now) =>
        // Handing it over is an edit only Petre makes, so it clears the adopted mark (Ruling 11b).
        // A handed-back task (StalledTasks) he assigns, even to himself, is his decision from now on, so the mark goes and a
        // busy session no longer takes it back.
        Lookup(id).Map(task => task.Assignee == assignee && task.HandedBack.HasNoValue
            ? this
            : Replaced(task with { Assignee = assignee, UpdatedAt = now, Adopted = false, HandedBack = Maybe<DateTimeOffset>.None }));

    /// <summary>
    /// A Claude task no session has worked on for a while comes back to Petre (StalledTasks; spec
    /// docs/superpowers/specs/2026-11-23-stalled-claude-tasks-design.md). It stays In progress, so the bar shows Claude's
    /// status line, and carries the mark that lets <see cref="TakeBack"/> return it. Only an open, In progress task with
    /// sessions that is Claude's is handed back; anything else returns this same board.
    /// </summary>
    public TaskBoard HandBack(Guid id, DateTimeOffset now) =>
        Tasks.FirstOrDefault(t => t.Id == id) is { IsOpen: true, Status: TaskStatus.InProgress, IsClaudes: true } task && task.Assignee.IsClaude
            ? Replaced(task with { Assignee = Assignee.Me, HandedBack = now, UpdatedAt = now })
            : this;

    /// <summary>
    /// A handed-back task's session works on it again: it is Claude's again and the mark goes. Only a task that carries
    /// the mark and is still Petre's moves, so one he assigned to himself stays his; anything else returns this same board.
    /// </summary>
    public TaskBoard TakeBack(Guid id, DateTimeOffset now) =>
        Tasks.FirstOrDefault(t => t.Id == id) is { HandedBack.HasValue: true } task && task.Assignee.IsMe
            ? Replaced(task with { Assignee = Assignee.Claude, HandedBack = Maybe<DateTimeOffset>.None, UpdatedAt = now })
            : this;

    // --- snoozing (task 6da8d7eb; TaskItem.SnoozedUntil, TaskSnooze.cs) -----------------------------------------------

    /// <summary>
    /// Petre snoozes a task until <paramref name="until"/>: it leaves the bar and the tasks panel's usual views until then.
    /// Nothing else about it changes (status, flags, rank, sessions), so it comes back exactly where it was. Snoozing again
    /// moves the time; the same time returns this same board. Refused: a closed task (it is out of view already) and a
    /// time that is not after <paramref name="now"/>. An edit only Petre makes, so it clears the adopted mark (Ruling 11b).
    ///
    /// A TASK WAITING ON PETRE CAN BE SNOOZED (Petre, 2026-11-25: "snooze should also be available on waiting on me; i may
    /// snooze something that's waiting"; the first build refused it). He has seen the question and decided it can wait:
    /// it leaves the pinned Waiting on you block, its chip's count and the bar, and keeps its status and its question, so
    /// it comes back still waiting, in the block at the place its ask gives it (its Raised is untouched). The session asking again, or the
    /// question being answered, wakes it at once (Woken), so a NEW question is never hidden.
    /// </summary>
    public Result<TaskBoard> Snooze(Guid id, DateTimeOffset until, DateTimeOffset now) =>
        until <= now ? Result.Failure<TaskBoard>("A snooze has to end later than now")
        : Lookup(id).Bind(task =>
            !task.IsOpen ? Result.Failure<TaskBoard>(TaskIsClosed)
            : task.SnoozedUntil.HasValue && task.SnoozedUntil.Value == until ? this
            : Result.Success(Replaced(task with { SnoozedUntil = until, UpdatedAt = now, Adopted = false })));

    /// <summary>Petre wakes a snoozed task early ("Wake now"). A task that is not snoozed returns this same board.</summary>
    public Result<TaskBoard> Wake(Guid id, DateTimeOffset now) =>
        Lookup(id).Map(task => task.IsSnoozed ? Replaced(task with { SnoozedUntil = Maybe<DateTimeOffset>.None, UpdatedAt = now }) : this);

    /// <summary>
    /// Every snooze whose time has come ends: the app's timer calls this, so a task comes back on its own. Nothing due
    /// returns this same board, so the caller saves nothing.
    /// </summary>
    public TaskBoard WakeDue(DateTimeOffset now) =>
        Tasks.Any(t => Due(t, now))
            ? new TaskBoard(Tasks.Select(t => Due(t, now) ? t with { SnoozedUntil = Maybe<DateTimeOffset>.None, UpdatedAt = now } : t).ToArray())
            : this;

    static bool Due(TaskItem task, DateTimeOffset now) => task.SnoozedUntil.HasValue && task.SnoozedUntil.Value <= now;

    // A SNOOZE ENDS WHEN SOMETHING HAPPENS THAT PETRE SHOULD SEE: the task's status changed (a session joined his note, a
    // question was answered, he set it by hand, it was closed) or it was raised (a session asked, or it became Done).
    // A snooze hides what Petre has already seen and put off, never something new: a fresh question or a finish comes
    // back at once, even on a task he snoozed while it waited. A status line, a comment, a flag or a drag
    // leaves the snooze alone. Applied by Change and Replaced, the two ways a task is rewritten, so no rule can forget it.
    static TaskItem Woken(TaskItem was, TaskItem now) =>
        now.IsSnoozed && (now.Status != was.Status || now.Raised != was.Raised)
            ? now with { SnoozedUntil = Maybe<DateTimeOffset>.None }
            : now;

    /// <summary>
    /// Deletes a task for good. Its subtasks stay and become plain tasks (their parent link goes, stamped), as
    /// DetachGoal frees a deleted goal's tasks, so no link is left naming nothing.
    /// </summary>
    public Result<TaskBoard> Delete(Guid id, DateTimeOffset now) =>
        Tasks.Any(t => t.Id == id)
            ? new TaskBoard(Tasks.Where(t => t.Id != id)
                .Select(t => t.ParentId == id ? t with { ParentId = Maybe<Guid>.None, UpdatedAt = now } : t).ToArray())
            : Result.Failure<TaskBoard>(NoTask(id.ToString()));

    /// <summary>
    /// Adds a comment to the end of the thread, from a session or (<paramref name="sessionId"/> none) from Petre. A
    /// closed task takes comments too: it stays readable, and a note on why it closed belongs on it. Trailing
    /// whitespace and leading blank lines are dropped; leading spaces on the first line are kept, since markdown
    /// reads them (an indented code block).
    /// </summary>
    public Result<TaskBoard> Comment(Guid id, Maybe<string> sessionId, string markdown, DateTimeOffset now, Guid newId) =>
        CleanText(markdown, "A comment needs text").Bind(_ => Change(id, t => t with
        {
            Comments = t.Comments.Append(new TaskComment(newId, sessionId, now, markdown.TrimEnd().TrimStart('\r', '\n'))).ToArray(),
            UpdatedAt = now,
            // Petre's comment is his edit (Ruling 11b, see WithoutSessionElsewhere); a session's leaves the mark.
            Adopted = t.Adopted && sessionId.HasValue,
        }));

    // --- Petre's own words on a task (Ruling 58) ---------------------------------------------------------

    /// <summary>
    /// Petre's description of a task, markdown (empty or blank clears it). Only the description and UpdatedAt change, so
    /// the line keeps its place; the text it already has returns this same board. Never touches a Claude task's status
    /// line, which stays the session's. A closed task takes it too, as comments do.
    /// </summary>
    public Result<TaskBoard> Describe(Guid id, string markdown, DateTimeOffset now) =>
        Lookup(id).Map(task => (markdown ?? "").Trim() is var text && task.Description == text
            ? this
            // His description is his edit, so it clears the adopted mark (Ruling 11b).
            : Replaced(task with { Description = text, UpdatedAt = now, Adopted = false }));

    /// <summary>
    /// A comment by full id or by a unique prefix of at least four characters, as <see cref="Find"/> does for tasks:
    /// `task comment-edit` names a comment this way.
    /// </summary>
    public Result<TaskComment> FindComment(string idOrPrefix) =>
        (idOrPrefix ?? "").Trim() is var text && text.Length >= MinPrefix
            ? Tasks.SelectMany(t => t.Comments).Where(c => c.Id.ToString().StartsWith(text, StringComparison.OrdinalIgnoreCase)).ToArray() switch
            {
                [var only] => only,
                [] => Result.Failure<TaskComment>($"No comment with id {text}"),
                _ => Result.Failure<TaskComment>($"'{text}' matches several comments")
            }
            : Result.Failure<TaskComment>($"No comment with id {text}");

    /// <summary>
    /// Petre rewrites one of HIS comments. A comment a session wrote is refused ("i should only be able to edit my own
    /// comments, not claude's"); so is an empty one. The thread keeps its order and the comment its time, so a late edit
    /// does not reorder anything; the same text returns this same board. A closed task's comments can be edited, since
    /// it stays readable and a note on why it closed belongs to it.
    /// </summary>
    public Result<TaskBoard> EditComment(Guid commentId, string markdown, DateTimeOffset now) =>
        Tasks.FirstOrDefault(t => t.Comments.Any(c => c.Id == commentId)) is not { } task ? Result.Failure<TaskBoard>($"No comment with id {commentId}")
        : task.Comments.First(c => c.Id == commentId) is { SessionId.HasValue: true } ? Result.Failure<TaskBoard>("Only your own comments can be edited")
        : CleanText(markdown, "A comment needs text").Map(_ => markdown.TrimEnd().TrimStart('\r', '\n') is var clean
            && task.Comments.First(c => c.Id == commentId).Markdown == clean
                ? this
                : Replaced(task with
                {
                    Comments = task.Comments.Select(c => c.Id == commentId ? c with { Markdown = clean } : c).ToArray(),
                    UpdatedAt = now,
                    // Only Petre edits a comment, so it is his edit (Ruling 11b); his comment already cleared the mark.
                    Adopted = false,
                }));

    /// <summary>
    /// Settles a comment, or (<paramref name="settle"/> false) brings it back (Ruling 62). <paramref name="bySession"/> is the
    /// session asking, none (null) for Petre: a session settles only its own comments, Petre any. The state it already
    /// has returns this same board; the settled time is "now" and the comment keeps its place in the thread.
    /// </summary>
    public Result<TaskBoard> SettleComment(Guid commentId, bool settle, string? bySession, DateTimeOffset now) =>
        Tasks.FirstOrDefault(t => t.Comments.Any(c => c.Id == commentId)) is not { } task ? Result.Failure<TaskBoard>($"No comment with id {commentId}")
        : task.Comments.First(c => c.Id == commentId) is var comment && bySession is not null && comment.SessionId.GetValueOrDefault() != bySession
            ? Result.Failure<TaskBoard>("A session can settle only its own comments")
        : comment.Settled.HasValue == settle ? this
        : Replaced(task with
        {
            Comments = task.Comments.Select(c => c.Id == commentId ? c with { Settled = settle ? now : Maybe<DateTimeOffset>.None } : c).ToArray(),
            UpdatedAt = now,
            // Petre settling a comment is his edit (Ruling 11b); a session settling its own leaves the mark, as its comment does.
            Adopted = task.Adopted && bySession is not null,
        });

    // The session's own unsettled comments written at or after the question was asked (WaitingSince, falling back to Raised for a
    // file saved before it existed): the ask's detail and the follow-ups while it was open.
    static IReadOnlyList<TaskComment> SettledSinceAsk(TaskItem task, string sessionId, DateTimeOffset now) =>
        task.Comments.Select(c => c.SessionId.HasValue && c.SessionId.Value == sessionId && c.At >= task.WaitingSince.GetValueOrDefault(task.Raised) && c.Settled.HasNoValue
            ? c with { Settled = now }
            : c).ToArray();

    // --- the goal a task counts toward (goals spec, "Tasks contribute") -----------------------------------

    /// <summary>
    /// Links the task to a goal, or (<paramref name="goal"/> none) unlinks it. Only the link and UpdatedAt change:
    /// attaching never moves a task's flags, rank, status or Raised, so its line stays where it is. A closed task takes
    /// it too, since it stays readable history. Nothing here checks the goal (GoalService.LinkTask does); the goal the
    /// task already has returns this same board.
    /// </summary>
    public Result<TaskBoard> SetGoal(Guid id, Maybe<Guid> goal, DateTimeOffset now) =>
        Lookup(id).Map(task => task.GoalId == goal ? this : Replaced(task with { GoalId = goal, UpdatedAt = now, Adopted = false }));

    /// <summary>
    /// A goal was deleted: every task that named it is freed and stamped; nothing else about them changes. No task
    /// naming it returns this same board.
    /// </summary>
    public TaskBoard DetachGoal(Guid goalId, DateTimeOffset now) =>
        Tasks.Any(t => t.GoalId == goalId)
            ? new TaskBoard(Tasks.Select(t => t.GoalId == goalId ? t with { GoalId = Maybe<Guid>.None, UpdatedAt = now } : t).ToArray())
            : this;

    /// <summary>A workspace that no longer exists frees its tasks; nothing else about them changes.</summary>
    public TaskBoard Prune(IReadOnlySet<Guid> knownWorkspaces, DateTimeOffset now) =>
        new(Tasks.Select(t => t.WorkspaceId.HasValue && !knownWorkspaces.Contains(t.WorkspaceId.Value)
            ? t with { WorkspaceId = Maybe<Guid>.None, UpdatedAt = now }
            : t).ToArray());

    // --- subtasks (docs/superpowers/specs/2026-11-24-subtasks-design.md) ---------------------------------
    //
    // One level: a family is a parent and its subtasks. A subtask cannot be a parent and a parent cannot become a
    // subtask, so the briefing and the breadcrumb never grow with depth. A closed parent is allowed for CLOSED subtasks
    // (its context is still worth reading), but never above an open one: see WithParentReopened. The link never moves a
    // line: Pile and Group do not read it.

    /// <summary>
    /// Makes <paramref name="id"/> a subtask of <paramref name="parent"/>, or (none) a plain task again. Refused: an
    /// unknown task or parent, the task itself, a parent that is a subtask, and a task that has subtasks of its own. The
    /// parent it already has, or clearing one it does not have, returns this same board. Only the link and UpdatedAt
    /// change, so the line keeps its place. Any task, closed ones too, as SetGoal does. An OPEN task put under a closed
    /// parent reopens that parent (<see cref="WithParentReopened"/>).
    /// </summary>
    public Result<TaskBoard> SetParent(Guid id, Maybe<Guid> parent, DateTimeOffset now) =>
        Lookup(id).Bind(task =>
            task.ParentId == parent ? Result.Success(this)
            : parent.HasValue && parent.Value == id ? Result.Failure<TaskBoard>("A task cannot be its own subtask")
            : parent.HasValue && Tasks.Any(t => t.ParentId == id)
                ? Result.Failure<TaskBoard>($"Task {Short(id)} has subtasks of its own, so it cannot become one")
            : ParentFor(parent).Map((Maybe<TaskItem> _) => Replaced(task with { ParentId = parent, UpdatedAt = now, Adopted = false }))
                .Bind(board => board.WithParentReopened(id, now)));

    // THE PARENT COMES BACK WITH ITS WORK (task f47b18df, 2026-11-25, Petre: "reopen parent"). Closing a parent is
    // refused while a subtask is open (Close, task 842e1ba4), because the parent stands for the whole piece of work.
    // The same rule read the other way: an open subtask never sits under a closed parent. So whenever a change leaves
    // the task <paramref name="id"/> OPEN under a CLOSED parent, the parent is reopened in the same step, exactly as
    // Reopen brings a task back (In progress while a session is live on it, else Backlog, on top of its quadrant). The
    // other answer, refusing the change, was weighed and not taken: reopening a subtask is the clearer signal that the
    // work is not over, and a refusal would make Petre reopen the parent by hand first.
    // The changes that can land an open subtask there all call this: AddNote with a parent (and so AddSubtask),
    // SetParent, Reopen, UndoClose and SetStatus on a closed subtask. Anything else (a closed subtask, a plain task, a
    // parent already open) returns this same board. A board from before the rule that already holds an open subtask
    // under a closed parent is left as it is until one of those changes touches that subtask.
    Result<TaskBoard> WithParentReopened(Guid id, DateTimeOffset now) =>
        Tasks.TryFirst(t => t.Id == id)
            .Where(t => t.IsOpen)
            .Bind(ParentOf)
            .Where(up => !up.IsOpen)
            .Map(up => Change(up.Id, t => Reopened(t, now)))
            .GetValueOrDefault(Result.Success(this));

    /// <summary>
    /// The task with its parent, siblings and subtasks (<see cref="TaskFamily"/>), or none for an unknown task and for
    /// a task with neither a parent that exists nor subtasks, so a caller prints nothing for a lone task.
    /// </summary>
    public Maybe<TaskFamily> Family(Guid id) =>
        Tasks.TryFirst(t => t.Id == id).Bind(task =>
            ParentOf(task) is var up && ChildrenOf(task.Id) is var children && (up.HasValue || children.Count > 0)
                ? Maybe.From(new TaskFamily(task, up,
                    up.Map(p => (IReadOnlyList<TaskItem>)ChildrenOf(p.Id).Where(t => t.Id != id).ToArray())
                        .GetValueOrDefault(Array.Empty<TaskItem>()),
                    children))
                : Maybe<TaskFamily>.None);

    /// <summary>
    /// The task "Add subtask…" on a line puts the new task under: the line's own task, or its parent when the line is a
    /// subtask, so the new one is a sibling (one level, as `task subtask` does). None for an unknown line.
    /// </summary>
    public Maybe<TaskItem> SubtaskParentFor(Guid lineId) =>
        Tasks.TryFirst(t => t.Id == lineId).Map(t => ParentOf(t).GetValueOrDefault(t));

    /// <summary>
    /// The tasks a drag can drop <paramref name="id"/> into as their subtask (drag to nest): open tasks that are not
    /// subtasks, not the task itself, those on its own workspace first, each part in the pile's order. Empty for a task
    /// that has subtasks of its own (one level) and for an unknown task.
    /// </summary>
    public IReadOnlyList<TaskItem> ParentCandidates(Guid id) =>
        Tasks.TryFirst(t => t.Id == id) is { HasValue: true } own && !Tasks.Any(t => t.ParentId == id)
            ? Pile().Where(t => t.Id != id && t.ParentId.HasNoValue)
                .OrderBy(t => !t.WorkspaceId.Equals(own.Value.WorkspaceId))
                .ToArray()
            : [];

    /// <summary>The parent a task names, when it is still on the board: what a subtask's line names in front of its title.</summary>
    public Maybe<TaskItem> ParentOf(TaskItem task) => task.ParentId.Bind(p => Tasks.TryFirst(t => t.Id == p));

    /// <summary>
    /// How many OPEN subtasks a task has, the count its line shows ("· 2 subtasks", Petre's pick A). Closed ones are
    /// history and do not count (subtasks spec, "Calls Claude made").
    /// </summary>
    public int OpenSubtaskCount(Guid id) => Tasks.Count(t => t.ParentId == id && t.IsOpen);

    // A task's subtasks: open ones in the pile's order, then closed ones latest closed first (closing stamps UpdatedAt),
    // the same orders `task list` and `task list --closed` print them in.
    IReadOnlyList<TaskItem> ChildrenOf(Guid id) =>
        Pile().Where(t => t.ParentId == id)
            .Concat(Tasks.Where(t => t.ParentId == id && !t.IsOpen).OrderByDescending(t => t.UpdatedAt))
            .ToArray();

    // The parent a new subtask or a SetParent names, checked: it exists and is not itself a subtask. None is fine.
    Result<Maybe<TaskItem>> ParentFor(Maybe<Guid> parent) =>
        parent.HasNoValue ? Result.Success(Maybe<TaskItem>.None)
        : Lookup(parent.Value).Bind(p => p.ParentId.HasValue
            ? Result.Failure<Maybe<TaskItem>>($"Subtasks go one level deep: {Short(p.Id)} is already a subtask")
            : Result.Success(Maybe.From(p)));

    static string Short(Guid id) => id.ToString()[..8];

    // --- reading -----------------------------------------------------------------------------------

    /// <summary>
    /// The open task a session works on: one with that session live on it, the latest joined if there are several.
    /// None once the task is closed or the session has ended.
    /// </summary>
    public Maybe<TaskItem> TaskOf(string sessionId) =>
        Tasks.Where(t => t.IsOpen)
            .SelectMany(t => t.Sessions.Where(s => s.IsLive && s.Id == sessionId).Select(s => (Task: t, s.Joined)))
            .OrderByDescending(x => x.Joined)
            .Select(x => x.Task)
            .TryFirst();

    /// <summary>
    /// The open task a session last worked on, live or ended: how a resumed session (Claude Code keeps its id) finds
    /// its task again after SessionEnd marked it ended (review focus 4 of plan Task 8). The latest joined wins.
    /// </summary>
    public Maybe<TaskItem> LastTaskOf(string sessionId) =>
        Tasks.Where(t => t.IsOpen)
            .SelectMany(t => t.Sessions.Where(s => s.Id == sessionId).Select(s => (Task: t, s.Joined)))
            .OrderByDescending(x => x.Joined)
            .Select(x => x.Task)
            .TryFirst();

    /// <summary>
    /// The workspace of the open task a session works on, found by the session's SHORT id (its first eight characters):
    /// a hidden session's process names only that (LaunchedBy.BackgroundSession), and a window it opens goes to this
    /// workspace (task e671709b). Live entries first, then the latest joined, as <see cref="TaskOf"/> and
    /// <see cref="LastTaskOf"/> pick; an ended entry still counts, because a running process is better evidence than a
    /// missed hook. None for a free task, a closed one, or a session on no task: the window then stays where it opened.
    /// </summary>
    public Maybe<Guid> WorkspaceOfSession(string shortId) =>
        shortId.Length == 0
            ? Maybe<Guid>.None
            : Tasks.Where(t => t.IsOpen)
                .SelectMany(t => t.Sessions.Where(s => s.Id.StartsWith(shortId, StringComparison.OrdinalIgnoreCase))
                    .Select(s => (Task: t, s.IsLive, s.Joined)))
                .OrderByDescending(x => x.IsLive)
                .ThenByDescending(x => x.Joined)
                .Select(x => x.Task.WorkspaceId)
                .TryFirst()
                .Bind(workspace => workspace);

    /// <summary>
    /// The task a session was on last, live or ended, when the user has closed it: what a RESUMED session's SessionStart
    /// reopens and joins (task 327e3cd0, RemoteControl.SessionStarted). None when the session's latest entry is on an open
    /// task (it joined another since, which LastTaskOf rejoins), and an entry /clear replaced does not count (Ruling 19).
    /// </summary>
    public Maybe<TaskItem> ClosedLastTaskOf(string sessionId) =>
        Tasks.SelectMany(t => t.Sessions.Where(s => s.Id == sessionId && !s.Replaced).Select(s => (Task: t, s.Joined)))
            .OrderByDescending(x => x.Joined)
            // Two joins in the same instant: the open task wins, since reopening is only for a session with nothing open.
            .ThenByDescending(x => x.Task.IsOpen)
            .Select(x => x.Task)
            .TryFirst()
            .Where(t => t.Status == TaskStatus.Closed);

    /// <summary>
    /// The closed task a session is still live on: Petre closed the task under it (closing ends no session). Its next
    /// prompt is told so, and that it can reopen it with `task join` if the prompt continues that work (task 327e3cd0) or
    /// start a new task for anything else; joining or creating ends its live entries on open tasks only, so this keeps
    /// answering, and TaskOf answers first once the session has an open task.
    /// </summary>
    public Maybe<TaskItem> ClosedTaskOf(string sessionId) =>
        Tasks.Where(t => t.Status == TaskStatus.Closed)
            .SelectMany(t => t.Sessions.Where(s => s.IsLive && s.Id == sessionId).Select(s => (Task: t, s.Joined)))
            .OrderByDescending(x => x.Joined)
            .Select(x => x.Task)
            .TryFirst();

    /// <summary>
    /// What a session starting on <paramref name="workspace"/> is offered to join, in the pile's order: the open
    /// tasks on that workspace that are assigned to Claude (Ruling 57; "Assign to Me" withdraws the offer, as the review asked: the
    /// assignee alone decides), plus the open Claude tasks on no workspace that are still Claude's. A note of Petre's that stays his
    /// is not offered, and neither are his notes on no workspace (errands; spec, "What a session is offered").
    ///
    /// A free subtask that is Claude's is offered too when its parent has had sessions (subtasks spec, after the builder
    /// check): a session that split a task on no workspace (`task subtask`) made it, and without this no session would
    /// ever be offered it.
    /// </summary>
    public IReadOnlyList<TaskItem> Offered(Maybe<Guid> workspace) =>
        Pile().Where(t => t.WorkspaceId.HasValue
            ? workspace.HasValue && t.WorkspaceId.Value == workspace.Value && t.Assignee.IsClaude
            : t.Assignee.IsClaude && (t.IsClaudes || ParentOf(t).Map(p => p.IsClaudes).GetValueOrDefault(false))).ToArray();

    /// <summary>
    /// The open tasks in the order the bar shows them (Ruling 25): the pinned block first (<see cref="Pinned"/>: the
    /// questions, then the Done tasks), whatever their flags; then Covey's quadrants I, II, III and IV, each in its
    /// manual order (<see cref="Group"/>). Nothing here reads UpdatedAt, so a status line, a comment or a session ending
    /// cannot move a line. Closed tasks are left out.
    /// </summary>
    public IReadOnlyList<TaskItem> Pile() =>
        Pinned().Concat(Enum.GetValues<Quadrant>().SelectMany(Group)).ToArray();

    /// <summary>
    /// The pinned "Waiting on you" block (task eefb986a, TaskStatusPinning): the tasks waiting on Petre's answer first,
    /// then the Done tasks waiting for him to close them, each part in its own order (<see cref="PinnedPart"/>: a new
    /// one on top, then where he drags it, task f41d7857). A question goes above a Done task because a session is
    /// stopped on it, while a Done task only waits for a look and a close.
    /// </summary>
    public IReadOnlyList<TaskItem> Pinned() =>
        PinnedPart(TaskStatus.WaitingOnYou).Concat(PinnedPart(TaskStatus.Done)).ToArray();

    /// <summary>
    /// One part of the pinned block in its order: the tasks of <paramref name="status"/> (Waiting on you, or Done),
    /// smallest <see cref="TaskItem.Pin"/> first, then the latest raised first (pins that tie: tasks made in code, a file
    /// from before pins), then the id, so the order is always the same. Snoozed ones are in it; each surface leaves them
    /// out as it leaves them out elsewhere. Any other status has no part and gives an empty list.
    /// </summary>
    public IReadOnlyList<TaskItem> PinnedPart(TaskStatus status) =>
        status.IsPinned()
            ? Tasks.Where(t => t.Status == status).OrderBy(t => t.Pin).ThenByDescending(t => t.Raised).ThenBy(t => t.Id).ToArray()
            : [];

    /// <summary>
    /// A task's 1-based place in its quadrant's order, as `task list` prints it and `task order &lt;n&gt;` takes it;
    /// none for a task waiting on Petre or Done (pinned above the quadrants, <see cref="Pinned"/>) or closed.
    /// </summary>
    public Maybe<int> PositionOf(Guid id) =>
        Tasks.TryFirst(t => t.Id == id)
            .Bind(task => Group(task.Quadrant).Select((t, i) => (t.Id, Position: i + 1)).TryFirst(x => x.Id == id))
            .Map(x => x.Position);

    /// <summary>Looks a task up by full id or by a unique prefix of at least four characters.</summary>
    public Result<TaskItem> Find(string idOrPrefix) =>
        (idOrPrefix ?? "").Trim() is var text && text.Length >= MinPrefix
            ? Tasks.Where(t => t.Id.ToString().StartsWith(text, StringComparison.OrdinalIgnoreCase)).ToArray() switch
            {
                [var only] => only,
                [] => Result.Failure<TaskItem>(NoTask(text)),
                _ => Result.Failure<TaskItem>($"'{text}' matches several tasks")
            }
            : Result.Failure<TaskItem>(NoTask(text));

    // --- helpers -----------------------------------------------------------------------------------

    static Result<string> CleanTitle(string title) =>
        string.IsNullOrWhiteSpace(title) ? Result.Failure<string>("A task needs a title") : title.Trim();

    static Result<string> CleanText(string text, string refusal) =>
        string.IsNullOrWhiteSpace(text) ? Result.Failure<string>(refusal) : text;

    static string NoTask(string id) => $"No task with id {id}";

    static bool LiveOn(TaskItem task, string sessionId) => task.Sessions.Any(s => s.Id == sessionId && s.IsLive);

    static TaskItem NewTask(Guid id, string title, Maybe<Guid> workspace, TaskStatus status, IReadOnlyList<TaskSession> sessions, DateTimeOffset now) =>
        new(id, title, workspace, status, "", Maybe<string>.None, now, now, now, sessions, Array.Empty<TaskComment>());

    /// <summary>The session in place of one with the same id, or added at the end.</summary>
    static IReadOnlyList<TaskSession> WithSession(IReadOnlyList<TaskSession> sessions, TaskSession session) =>
        sessions.Any(s => s.Id == session.Id)
            ? sessions.Select(s => s.Id == session.Id ? session : s).ToArray()
            : sessions.Append(session).ToArray();

    /// <summary>
    /// Done, with Raised stamped and on top of the pinned block's Done tasks (task f41d7857) when it was not Done before;
    /// an already Done task keeps its stamp and its place.
    /// </summary>
    TaskItem BecameDone(TaskItem task, DateTimeOffset now) =>
        task.Status == TaskStatus.Done ? task
        : task with { Status = TaskStatus.Done, Raised = now, Pin = TopPin(TaskStatus.Done, task.Id) };

    Result<TaskItem> Lookup(Guid id) =>
        Tasks.TryFirst(t => t.Id == id).ToResult(NoTask(id.ToString()));

    static TaskItem WithFlags(TaskItem task, Quadrant quadrant) =>
        task with { Important = quadrant.IsImportant(), Urgent = quadrant.IsUrgent() };

    /// <summary>
    /// The rank that puts a task on top of <paramref name="quadrant"/>'s order: one above the highest of the others
    /// there (<paramref name="except"/> is the task being placed, so it is not its own neighbour), or 0 in an empty
    /// quadrant.
    /// </summary>
    double TopRank(Quadrant quadrant, Guid except) =>
        Group(quadrant).Where(t => t.Id != except).Select(t => t.Rank).DefaultIfEmpty(1).Min() - 1;

    /// <summary>
    /// The pin that puts a task on top of the pinned block's part for <paramref name="status"/> (the questions, or the
    /// Done tasks): one above the smallest of the other pins there (<paramref name="except"/> is the task joining it), or
    /// 0 in an empty part.
    /// </summary>
    double TopPin(TaskStatus status, Guid except) =>
        PinnedPart(status).Where(t => t.Id != except).Select(t => t.Pin).DefaultIfEmpty(1).Min() - 1;

    /// <summary>A closed task coming back goes to the top of its quadrant (Ruling 25); an open one keeps its place.</summary>
    TaskItem OnTopIfClosed(TaskItem task) => task.IsOpen ? task : task with { Rank = TopRank(task.Quadrant, task.Id) };

    /// <summary>
    /// Where <paramref name="place"/> puts <paramref name="task"/> among the other tasks of <paramref name="quadrant"/>:
    /// an index into that order with the task itself left out. Placing a task beside itself is its own place.
    /// </summary>
    Result<int> IndexFor(TaskItem task, Quadrant quadrant, TaskPlace place) =>
        IndexIn(Group(quadrant), task, place, $"quadrant {quadrant.Numeral()}'s order");

    /// <summary>
    /// Where <paramref name="place"/> puts <paramref name="task"/> in <paramref name="order"/> (a quadrant's, or the
    /// Waiting on you block's), named <paramref name="what"/> in a refusal: an index with the task itself left out.
    /// </summary>
    static Result<int> IndexIn(IReadOnlyList<TaskItem> order, TaskItem task, TaskPlace place, string what)
    {
        var others = order.Where(t => t.Id != task.Id).Select(t => t.Id).ToList();
        var own = order.Select(t => t.Id).ToList().IndexOf(task.Id);
        Result<int> Beside(Guid neighbour, int after) =>
            neighbour == task.Id && own >= 0 ? own
            : others.IndexOf(neighbour) is var i and >= 0 ? i + after
            : Result.Failure<int>($"Task {neighbour} is not in {what}");
        return place switch
        {
            TaskPlace.TopPlace => 0,
            TaskPlace.EndPlace => others.Count,
            TaskPlace.AtPlace at => Math.Clamp(at.Position - 1, 0, others.Count),
            TaskPlace.BeforePlace before => Beside(before.Task, 0),
            TaskPlace.AfterPlace after => Beside(after.Task, 1),
            _ => Result.Failure<int>("Unknown place"),
        };
    }

    /// <summary>
    /// The task at <paramref name="index"/> of <paramref name="quadrant"/>'s order, with that quadrant's flags. Takes
    /// the middle of the new neighbours' ranks (one past the end neighbour at either end), so nothing else changes.
    /// When that middle is not strictly between them (doubles run out after some fifty halvings of one gap, or two
    /// ranks were equal), the quadrant is numbered 0, 1, 2 ... afresh in its new order, stamping each task whose rank
    /// moved. A task left where it already was returns this same board.
    /// </summary>
    TaskBoard Placed(TaskItem task, Quadrant quadrant, int index, DateTimeOffset now)
    {
        var group = Group(quadrant);
        if (task.Quadrant == quadrant && group.Select(t => t.Id).ToList().IndexOf(task.Id) == index) return this;
        var others = group.Where(t => t.Id != task.Id).ToArray();
        return Reordered(others, index, WithFlags(task, quadrant) with { UpdatedAt = now }, t => t.Rank, (t, rank) => t with { Rank = rank }, now);
    }

    /// <summary>
    /// The pinned task at <paramref name="index"/> of its part of the pinned block, by its pin, as <see cref="Placed"/>
    /// does it by rank (task f41d7857). A task left where it already was returns this same board.
    /// </summary>
    TaskBoard Repinned(TaskItem task, int index, DateTimeOffset now)
    {
        var block = PinnedPart(task.Status);
        if (block.Select(t => t.Id).ToList().IndexOf(task.Id) == index) return this;
        var others = block.Where(t => t.Id != task.Id).ToArray();
        return Reordered(others, index, task with { UpdatedAt = now }, t => t.Pin, (t, pin) => t with { Pin = pin }, now);
    }

    /// <summary>
    /// <paramref name="moved"/> at <paramref name="index"/> among <paramref name="others"/>, an order kept by one number
    /// on each task (<paramref name="key"/>, written back by <paramref name="with"/>: a quadrant's ranks, the pinned
    /// block's pins). It takes the middle of its new neighbours' numbers (one past the end neighbour at either end), so
    /// nothing else changes. When that middle is not strictly between them (doubles run out after some fifty halvings
    /// of one gap, or two numbers were equal), the order is numbered 0, 1, 2 ... afresh, stamping each task whose
    /// number moved.
    /// </summary>
    TaskBoard Reordered(IReadOnlyList<TaskItem> others, int index, TaskItem moved, Func<TaskItem, double> key,
        Func<TaskItem, double, TaskItem> with, DateTimeOffset now)
    {
        double? above = index > 0 ? key(others[index - 1]) : null;
        double? below = index < others.Count ? key(others[index]) : null;
        var middle = (above, below) switch
        {
            ({ } a, { } b) => (a + b) / 2,
            ({ } a, null) => a + 1,
            (null, { } b) => b - 1,
            _ => key(moved),
        };
        var fits = (above is not { } lo || lo < middle) && (below is not { } hi || middle < hi);
        if (fits) return Replaced(with(moved, middle));

        var numbers = others.Take(index).Append(moved).Concat(others.Skip(index))
            .Select((t, i) => (t.Id, Number: (double)i))
            .ToDictionary(x => x.Id, x => x.Number);
        return new TaskBoard(Tasks.Select(t =>
            t.Id == moved.Id ? with(moved, numbers[t.Id])
            : numbers.TryGetValue(t.Id, out var renumbered) && renumbered != key(t) ? with(t, renumbered) with { UpdatedAt = now }
            : t).ToArray());
    }

    /// <summary>
    /// Ends the session's live entry on every open task but <paramref name="except"/>, stamping those tasks. Closed
    /// tasks are left alone: SessionEnded still closes the entry there when the session really ends.
    /// An ADOPTED task (<see cref="TaskItem.Adopted"/>) that this session was all of, with no comment on it, is deleted
    /// instead (Ruling 11): Deskbar made it for a session that had no task yet, the session has now joined its own, and
    /// what is left would be an empty duplicate line. Anything added to it (another session, a comment) keeps it, and so
    /// does any edit only Petre makes (Ruling 11b): Rename, MoveTo, SetFlag, SetGoal, SetStatus, Assign, Describe, his own
    /// Comment, EditComment and his SettleComment clear the mark for good. A drag within the order (Place) does not; it
    /// says nothing about wanting the task. A session settling its own comment does not either.
    /// </summary>
    TaskBoard WithoutSessionElsewhere(string sessionId, Guid except, DateTimeOffset now) =>
        new(Tasks
            // Final review, finding 1: an adopted task that has subtasks stays. A subtask is something added to it, and
            // dropping the parent would leave its subtasks naming nothing and lose the context they are briefed from.
            .Where(t => !(t.Id != except && t.IsOpen && LiveOn(t, sessionId) && OnlyAdoptedFor(t, sessionId) && !Tasks.Any(c => c.ParentId == t.Id)))
            .Select(t => t.Id != except && t.IsOpen && LiveOn(t, sessionId)
                ? t with
                {
                    Sessions = t.Sessions.Select(s => s.Id == sessionId && s.IsLive ? s with { Ended = now } : s).ToArray(),
                    UpdatedAt = now,
                }
                : t).ToArray());

    // The task holds nothing but this session: adopted for it, no other session, no comment.
    static bool OnlyAdoptedFor(TaskItem task, string sessionId) =>
        task.Adopted && task.Sessions.All(s => s.Id == sessionId) && task.Comments.Count == 0;

    (TaskBoard Board, TaskItem Task) WithAppended(TaskItem task) => (new TaskBoard(Tasks.Append(task).ToArray()), task);

    TaskBoard Replaced(TaskItem updated) => new(Tasks.Select(t => t.Id == updated.Id ? Woken(t, updated) : t).ToArray());

    Result<TaskBoard> Change(Guid id, Func<TaskItem, TaskItem> change) =>
        Tasks.Any(t => t.Id == id)
            ? new TaskBoard(Tasks.Select(t => t.Id == id ? Woken(t, Unmarked(t, change(t))) : t).ToArray())
            : Result.Failure<TaskBoard>(NoTask(id.ToString()));

    // The handed-back mark (StalledTasks) means "Deskbar gave this to Petre because it went quiet". Once anything else
    // moves the assignee or the status (an ask, done, close, a status by hand, a session joining), that is no longer why
    // the task is where it is, so the mark goes. A status line or a comment moves neither and keeps it. HandBack and
    // TakeBack do not come through here: they set the mark themselves.
    static TaskItem Unmarked(TaskItem was, TaskItem now) =>
        now.HandedBack.HasValue && (now.Assignee != was.Assignee || now.Status != was.Status)
            ? now with { HandedBack = Maybe<DateTimeOffset>.None }
            : now;

    /// <summary><see cref="Change"/> for the changes a closed task refuses (see the header).</summary>
    Result<TaskBoard> ChangeOpen(Guid id, Func<TaskItem, TaskItem> change) =>
        Tasks.FirstOrDefault(t => t.Id == id) is { IsOpen: false }
            ? Result.Failure<TaskBoard>(TaskIsClosed)
            : Change(id, change);
}
