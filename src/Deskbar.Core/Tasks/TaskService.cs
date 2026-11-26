using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Subjects;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// Owns the live TaskBoard and saves every change through ITaskStore. It is where the clock and new ids enter:
// TaskBoard stays pure and takes "now" and ids from here. Design: docs/superpowers/specs/2026-11-22-tasks-board-design.md.
// The timer build's ticking (Tick, the dwell, CameDue) is gone with the timers: nothing about a task changes with
// time alone any more, so there is nothing to tick.
//
// Everything runs on the UI thread (the bar, the window and the control pipe all dispatch there), so there is no
// locking.
//
// `filesRoot` is the state folder (beside tasks.json), where task-images\<task id>\ holds the pictures pasted into a
// task (docs/superpowers/specs/2026-11-24-task-images-design.md). It is what ExpandImages makes paths absolute against,
// for whatever a Claude session reads, and what Delete clears a task's pictures from. Null (tests, mostly) leaves image
// paths as stored and deletes no files.
public sealed class TaskService(ITaskStore store, Func<DateTimeOffset> now, Func<Guid>? newId = null, Action<string>? trace = null,
    string? filesRoot = null)
{
    readonly Func<Guid> makeId = newId ?? Guid.NewGuid;
    readonly Subject<Unit> changed = new();
    readonly Subject<(Guid TaskId, TaskComment Comment)> commented = new();

    public TaskBoard Board { get; private set; } = TaskBoard.Empty;

    /// <summary>Pulses after every change to the board, loaded or edited, so views refresh.</summary>
    public IObservable<Unit> Changed => changed;

    /// <summary>
    /// Why the last <see cref="Load"/> failed, in the store's words, or none. A failed load starts the board empty,
    /// and an empty list must not read as "nothing to do" when the truth is "could not read your tasks", so the
    /// task UI shows this until Petre dismisses it (<see cref="DismissLoadProblem"/>) or a later load works.
    /// </summary>
    public Maybe<string> LoadProblem { get; private set; } = Maybe<string>.None;

    /// <summary>Replaces the board with what the store holds. A store failure leaves the board empty.</summary>
    public Result Load()
    {
        var loaded = store.Load();
        Board = loaded.IsSuccess ? loaded.Value : TaskBoard.Empty;
        LoadProblem = loaded.IsSuccess ? Maybe<string>.None : loaded.Error;
        changed.OnNext(Unit.Default);
        return loaded.IsSuccess ? Result.Success() : Result.Failure(loaded.Error);
    }

    /// <summary>
    /// Petre has read the load problem: it stops being shown. No <see cref="Changed"/> pulse, because the board did
    /// not change; the view that offered the dismissal redraws itself after calling this.
    /// </summary>
    public void DismissLoadProblem() => LoadProblem = Maybe<string>.None;

    // One wrapper per TaskBoard change, with the clock and a new id supplied here. A refused change saves nothing.

    // `quadrant`: the flags the note is made with (Ruling 37), none by default.
    // `parent`: the task the note is a subtask of (subtasks spec), none for a plain note.
    public Result<TaskItem> AddNote(string title, Maybe<Guid> workspace, Quadrant quadrant = Quadrant.IV, Maybe<Guid> parent = default) =>
        Board.AddNote(title, workspace, now(), makeId(), quadrant, parent).Tap(r => Commit(r.Board)).Map(r => r.Task);

    /// <summary>
    /// A note made with its description: the tasks panel's add box with pictures pasted into it. <paramref name="describe"/>
    /// gets the new task's id, because the pictures are filed under it (task-images\&lt;task id&gt;\), and answers the
    /// description, or why the pictures could not be written. The note and its description are ONE save and one pulse, so
    /// nothing (the panel, the bar, a session's `task list`) ever sees the task without its pictures. A failed
    /// <paramref name="describe"/> adds nothing; cleaning up whatever it wrote is its own job (TaskImageFiles.Save is all or
    /// nothing).
    /// </summary>
    public Result<TaskItem> AddNote(string title, Maybe<Guid> workspace, Quadrant quadrant, Maybe<Guid> parent, Func<Guid, Result<string>> describe)
    {
        var at = now();
        return Board.AddNote(title, workspace, at, makeId(), quadrant, parent)
            .Bind(added => describe(added.Task.Id)
                .Bind(description => added.Board.Describe(added.Task.Id, description, at))
                .Map(board => (Board: board, Task: board.Tasks.Single(t => t.Id == added.Task.Id))))
            .Tap(r => Commit(r.Board))
            .Map(r => r.Task);
    }

    /// <summary>
    /// A note a session adds (`task later`, `task add --session`), related to the session's own task when it has one
    /// (TaskBoard.AddNoteFrom, task fa1e6a74).
    /// </summary>
    public Result<TaskItem> AddNoteFrom(string sessionId, string title, Maybe<Guid> workspace, Quadrant quadrant = Quadrant.IV, Maybe<Guid> parent = default) =>
        Board.AddNoteFrom(sessionId, title, workspace, now(), makeId(), quadrant, parent).Tap(r => Commit(r.Board)).Map(r => r.Task);

    /// <summary>A session splits its work (`task subtask`, TaskBoard.AddSubtask).</summary>
    public Result<TaskItem> AddSubtask(string sessionId, string title) =>
        Board.AddSubtask(sessionId, title, now(), makeId()).Tap(r => Commit(r.Board)).Map(r => r.Task);

    public Result<TaskItem> Create(string title, Maybe<Guid> workspace, TaskSession session) =>
        Board.Create(title, workspace, session, now(), makeId()).Tap(r => Commit(r.Board)).Map(r => r.Task);

    public Result Join(Guid id, TaskSession session) => Apply(Board.Join(id, session, now()));

    /// <summary>A session back on its own task, the task's status kept (TaskBoard.Rejoin, Ruling 34).</summary>
    public Result Rejoin(Guid id, TaskSession session) => Apply(Board.Rejoin(id, session, now()));

    /// <summary>
    /// A live session joining now, for the control pipe, which has no clock of its own: the Claude hooks and the
    /// session-facing `task` forms (RemoteControl.ClaudeHook.cs) make every session they hand to Join and Create here.
    /// </summary>
    public TaskSession NewSession(string id, string folder) => new(id, folder, now(), Maybe<DateTimeOffset>.None);

    // The same line again changes nothing, so it saves and pulses nothing (TaskBoard.SetStatusLine, review of PR B, M-3).
    public Result SetStatusLine(Guid id, string text) => ApplyIfChanged(Board.SetStatusLine(id, text, now()));

    public Result Ask(Guid id, string question) => Apply(Board.Ask(id, question, now()));

    // A pull request the task's session opened (task e7aa49b1); one it already lists saves and pulses nothing.
    public Result AddPullRequest(Guid id, PullRequestLink link) => ApplyIfChanged(Board.AddPullRequest(id, link, now()));

    public Result MarkDone(Guid id) => Apply(Board.MarkDone(id, now()));

    // `stoppingClaude`: past the refusal for a task Claude is still working on (TaskBoard.Close, task 37cd7081), for
    // "Stop Claude and close", which stops the sessions right after.
    public Result Close(Guid id, bool stoppingClaude = false) => Apply(Board.Close(id, now(), stoppingClaude));

    public Result Reopen(Guid id) => Apply(Board.Reopen(id, now()));

    public Result SetStatus(Guid id, TaskStatus status) => Apply(Board.SetStatus(id, status, now()));

    // Ruling 57: Petre's hand-over from the tasks window or `task assign`; the same assignee saves and pulses nothing.
    public Result Assign(Guid id, Assignee assignee) => ApplyIfChanged(Board.Assign(id, assignee, now()));

    public Result Rename(Guid id, string title) => Apply(Board.Rename(id, title, now()));

    // Ruling 58: Petre's description and his own comments. The same text saves and pulses nothing.
    public Result Describe(Guid id, string markdown) => ApplyIfChanged(Board.Describe(id, markdown, now()));

    public Result EditComment(Guid commentId, string markdown) => ApplyIfChanged(Board.EditComment(commentId, markdown, now()));

    // Ruling 62: a session settles its own comment (bySession), Petre any (null). The state it has saves and pulses nothing.
    public Result SettleComment(Guid commentId, bool settle, string? bySession = null) => ApplyIfChanged(Board.SettleComment(commentId, settle, bySession, now()));

    public Result MoveTo(Guid id, Maybe<Guid> workspace) => Apply(Board.MoveTo(id, workspace, now()));

    // A deleted task takes its pictures with it (closing keeps them: a closed task can be reopened). The board's delete
    // comes first, so a refused delete removes no files; a folder that cannot be removed (a viewer holding a file open)
    // is traced and left, and the delete still stands, because a stray folder is harmless and a task that will not
    // delete is not. The board frees the task's subtasks (subtasks spec), stamped now.
    public Result Delete(Guid id) => Apply(Board.Delete(id, now())).Tap(() => DeleteImages(id));

    // Snoozing (task 6da8d7eb, TaskBoard.Snooze): the same time again, or waking a task that is awake, saves nothing.
    public Result Snooze(Guid id, DateTimeOffset until) => ApplyIfChanged(Board.Snooze(id, until, now()));

    public Result Wake(Guid id) => ApplyIfChanged(Board.Wake(id, now()));

    /// <summary>
    /// Ends every snooze whose time has come (TaskBoard.WakeDue). The app calls it on a timer; nearly always nothing is
    /// due, and then it saves and pulses nothing.
    /// </summary>
    public void WakeDue() => CommitIfChanged(Board.WakeDue(now()));

    /// <summary>The clock this service stamps changes with, for a caller that works out a snooze's end from it.</summary>
    public DateTimeOffset Now => now();

    // WHETHER A DONE TASK'S CHANGE RUNS HERE YET (task 8470d176; the rules are in RunningChanges). What GitHub said about
    // the recorded pull requests lives here rather than on the board: it is a fact about THIS running build, not about
    // the task, so it is never saved (a restart into a newer build must ask again) and it never touches tasks.json.
    // RunningChangeWatch writes it (Arrived); the bar and the tasks panel read it per line (DeliveryOf) when they draw,
    // and they redraw on Changed, which Arrived pulses only when an answer changed. Worked out on every read rather
    // than kept per task, so a task that turns Done, or gains a pull request, is right at its next drawing with nothing
    // to recompute.
    Maybe<RunningBuild> runningBuild = Maybe<RunningBuild>.None;
    IReadOnlyDictionary<string, PullRequestArrival> arrivals = ImmutableDictionary<string, PullRequestArrival>.Empty;

    /// <summary>Where a task's change stands against the running build (RunningChanges.Of); None until the watch has spoken.</summary>
    public TaskDelivery DeliveryOf(TaskItem task) =>
        runningBuild.Map(build => RunningChanges.Of(task, build, arrivals)).GetValueOrDefault(TaskDelivery.None);

    /// <summary>
    /// What GitHub said about the recorded pull requests against <paramref name="build"/>, keyed by
    /// PullRequestLink.Key: the whole set known so far, replacing the last. Pulses Changed only when it differs, so an
    /// unchanged look redraws nothing.
    /// </summary>
    public void Arrived(RunningBuild build, IReadOnlyDictionary<string, PullRequestArrival> facts)
    {
        var same = runningBuild.HasValue && runningBuild.Value == build
            && facts.Count == arrivals.Count
            && facts.All(f => arrivals.TryGetValue(f.Key, out var known) && known == f.Value);
        if (same) return;
        runningBuild = build;
        arrivals = facts.ToImmutableDictionary();
        changed.OnNext(Unit.Default);
    }

    /// <summary>Makes a task a subtask of another, or (none) a plain task again (TaskBoard.SetParent).</summary>
    public Result SetParent(Guid id, Maybe<Guid> parent) => ApplyIfChanged(Board.SetParent(id, parent, now()));

    // Task links (docs/superpowers/specs/2026-11-25-task-links-design.md). A link that is already there, or one already
    // gone, saves and pulses nothing.

    /// <summary>
    /// Closes a task as a duplicate of another (TaskBoard.MarkDuplicate). `stoppingClaude` is the session's own form: the
    /// session asking is the one working, and it is done.
    /// </summary>
    public Result MarkDuplicate(Guid id, Guid of, bool stoppingClaude = false) => ApplyIfChanged(Board.MarkDuplicate(id, of, now(), stoppingClaude));

    public Result Relate(Guid id, Guid other) => ApplyIfChanged(Board.Relate(id, other, now()));

    public Result Unrelate(Guid id, Guid other) => ApplyIfChanged(Board.Unrelate(id, other, now()));

    void DeleteImages(Guid id)
    {
        if (filesRoot is null) return;
        var folder = Path.Combine(filesRoot, TaskImages.Folder, id.ToString());
        Result.Try(() => { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }, e => e.Message)
            .TapError(e => trace?.Invoke($"tasks: the images of deleted task {id} were not removed: {e}"));
    }

    /// <summary>
    /// The markdown with every image path of ours made absolute under the state folder, for a Claude session's Read
    /// tool (TaskImages.Expand); as stored when there is no state folder.
    /// </summary>
    public string ExpandImages(string markdown) => filesRoot is null ? markdown : TaskImages.Expand(markdown, filesRoot);

    /// <summary>Undo on a line just closed: back exactly as it was, status, question and place (TaskBoard.UndoClose).</summary>
    public Result UndoClose(Guid id, TaskStatus was, Maybe<string> question) => Apply(Board.UndoClose(id, was, question, now()));

    // The flags and the manual order (Task 12). A change that changes nothing (a flag set to what it is, a drop where
    // the task already was) saves and pulses nothing, so a click that went nowhere costs no redraw.

    public Result SetFlag(Guid id, TaskFlag flag, bool on) => ApplyIfChanged(Board.SetFlag(id, flag, on, now()));

    public Result Place(Guid id, Quadrant quadrant, TaskPlace place) => ApplyIfChanged(Board.Place(id, quadrant, place, now()));

    /// <summary>
    /// Links a task to a goal or (none) unlinks it; the goal it already has saves and pulses nothing. Checks nothing
    /// about the goal: GoalService.LinkTask is the one way the UI and the pipe link a task, and it checks the goal is
    /// open before it calls this (goals plan, "Calls this plan makes").
    /// </summary>
    public Result SetGoal(Guid id, Maybe<Guid> goal) => ApplyIfChanged(Board.SetGoal(id, goal, now()));

    /// <summary>A goal was deleted: its tasks are freed. Saves only when some task named it.</summary>
    public void DetachGoal(Guid goalId) => CommitIfChanged(Board.DetachGoal(goalId, now()));

    /// <summary>A line Deskbar itself writes into the thread (a resume, a stop, a failure), shown as "Deskbar".</summary>
    public Result Note(Guid id, string markdown) => Comment(id, TaskComment.DeskbarAuthor, markdown);

    // A COMMENT WAKES ITS SESSION (docs/superpowers/plans/2026-11-24-comment-wakes-session.md): Petre's own new comment
    // (no session) is announced on Commented once it is saved, and the app's CommentWaker hands it to the task's Claude
    // session. The manager's comment (TaskComment.ManagerAuthor) is announced too: it exists to hand a session work, and
    // the app's CommentWaker words it differently (SessionBriefing.Commented, byManager). `wakes` false is his form of
    // the command run from inside a Claude session (`task comment <id>` there carries --from-claude): stored as his,
    // announced to no one, so one session cannot wake another. A session's comment and Deskbar's own lines (Note) are
    // never announced.
    public Result Comment(Guid id, Maybe<string> sessionId, string markdown, bool wakes = true)
    {
        var commentId = makeId();
        return Apply(Board.Comment(id, sessionId, markdown, now(), commentId))
            .Tap(() =>
            {
                if ((sessionId.HasValue && sessionId.Value != TaskComment.ManagerAuthor) || !wakes) return;
                Board.Tasks.SelectMany(t => t.Comments).TryFirst(c => c.Id == commentId).Tap(comment => commented.OnNext((id, comment)));
            });
    }

    /// <summary>The user's own new comments and the manager's, each with its task's id, after they are saved (see Comment).</summary>
    public IObservable<(Guid TaskId, TaskComment Comment)> Commented => commented;

    /// <summary>
    /// Petre sent a session a message. These two run on every prompt and every session end, nearly always with
    /// nothing to change, so they save and pulse only when the board really changed (TaskBoard returns the same
    /// board otherwise).
    /// </summary>
    public void Answered(string sessionId) => CommitIfChanged(Board.Answered(sessionId, now()));

    public void SessionEnded(string sessionId) => CommitIfChanged(Board.SessionEnded(sessionId, now()));

    /// <summary>
    /// Deskbar settles a question whose pull requests are merged or closed (PullRequestWatch, task b7f987b4). Saves and
    /// pulses only when the task still waited on that very question (TaskBoard.SettleQuestion).
    /// </summary>
    public void SettleQuestion(Settlement settlement) =>
        CommitIfChanged(Board.SettleQuestion(settlement.TaskId, settlement.Question, settlement.Note, now(), makeId()));

    /// <summary>/clear ended the session (Ruling 19, TaskBoard.SessionCleared). Saves only when it was on a task.</summary>
    public void SessionCleared(string sessionId) => CommitIfChanged(Board.SessionCleared(sessionId, now()));

    /// <summary>
    /// A session that started with source "clear": when a session was cleared in its folder within
    /// TaskBoard.ClearedWithin, it rejoins that task in the cleared one's place, status kept as a resumed session's is
    /// (TaskBoard.Rejoin). Returns the task it joined, or none.
    /// </summary>
    public Maybe<TaskItem> JoinAfterClear(string sessionId, string folder, Func<string, bool> inFolder) =>
        Board.ClearedTaskIn(inFolder, now()) is { HasValue: true } cleared && Rejoin(cleared.Value.Id, NewSession(sessionId, folder)).IsSuccess
            ? cleared
            : Maybe<TaskItem>.None;

    // --- which sessions a person is at (Ruling 17) ---
    //
    // `claude agents --json` lists a `claude -p` run as "interactive", so the list cannot tell a person's session from a
    // script's (measured 2026-11-23), and adopting a headless run would put a task on the board for every scripted call.
    // The hooks can: ClaudeHookEvent.Headless. So the hooks record every session they hear from as attended
    // (SessionStart or UserPromptSubmit with Headless false) and ClaudeSessions adopts only those. Kept in memory: a
    // session not heard from since Deskbar started is not adopted until its next prompt, the ruling's stated cost.
    // Written by the control pipe and read by the poll, both on the UI thread; the immutable set keeps a stray reader on
    // another thread safe anyway.

    ImmutableHashSet<string> attended = ImmutableHashSet<string>.Empty;

    /// <summary>A hook from this session said a person is at it (Ruling 17).</summary>
    public void HeardAttended(string sessionId) => ImmutableInterlocked.Update(ref attended, set => set.Add(sessionId));

    /// <summary>Whether a hook has said, since Deskbar started, that a person is at this session (Ruling 17).</summary>
    public bool WasAttended(string sessionId) => attended.Contains(sessionId);

    /// <summary>
    /// The board follows Claude Code's list of live sessions (TaskBoard.Reconcile). Called on every poll, nearly always
    /// with nothing to change, so it saves and pulses only when a session really changed.
    /// </summary>
    public void Reconcile(IReadOnlyCollection<ClaudeAgent> live) => CommitIfChanged(Board.Reconcile(live, now()));

    // When each session last had a prompt submitted (its UserPromptSubmit hook), for RepliedTasks (task 8bd18246): a prompt
    // after a task began waiting is Petre replying in the terminal. In memory only, like the attended set above; a session
    // that has gone keeps its entry, which costs a time per session until Deskbar restarts.
    ImmutableDictionary<string, DateTimeOffset> prompted = ImmutableDictionary<string, DateTimeOffset>.Empty;

    /// <summary>A prompt was submitted in this session (RemoteControl.Prompted): remembered for RepliedTasks.</summary>
    public void Prompted(string sessionId)
    {
        var at = now();
        ImmutableInterlocked.Update(ref prompted, map => map.SetItem(sessionId, at));
    }

    // The quiet clock of StalledTasks: when each watched Claude task last had a session working on it. In memory only;
    // after a restart StalledTasks measures from the task's UpdatedAt instead (see its header).
    ImmutableDictionary<Guid, DateTimeOffset> lastBusy = ImmutableDictionary<Guid, DateTimeOffset>.Empty;

    /// <summary>
    /// Stalled tasks (StalledTasks; spec docs/superpowers/specs/2026-11-23-stalled-claude-tasks-design.md): called on
    /// every poll of Claude Code's session list with the ids of the sessions working now. A Claude task quiet for
    /// StalledTasks.After goes to Petre, a handed-back task whose session works again goes back to Claude. Saves and
    /// pulses only when a task changed hands, and traces each move.
    /// </summary>
    public void Stalled(IReadOnlySet<string> busy, IReadOnlySet<string> working)
    {
        // First the questions Deskbar set aside whose sessions are quiet again without having asked or settled them: they
        // wait on Petre again, as they stood (RepliedTasks.Restore, task 8bd18246).
        RepliedTasks.Restore(Board, busy).ToList().ForEach(id =>
        {
            var next = Board.RestoreQuestion(id, now());
            if (ReferenceEquals(next, Board)) return;
            trace?.Invoke($"tasks: restored the set-aside question of {Line(next, id)}, no session of it is busy");
            Commit(next);
        });
        // Then the waiting tasks Claude works on again after Petre's reply (RepliedTasks.SetAside): set aside, they are In
        // progress and Claude's, so the stalled step below already sees them worked on and starts their clock.
        RepliedTasks.SetAside(Board, working, prompted).ToList().ForEach(found =>
        {
            var at = now();
            var next = Board.SetQuestionAside(found.TaskId, found.Question, RepliedTasks.Note(found.Question), at, makeId());
            if (ReferenceEquals(next, Board)) return;
            trace?.Invoke($"tasks: set the question of {Line(next, found.TaskId)} aside, its session works on it again after a reply");
            Commit(next);
        });
        var step = StalledTasks.Step(Board, busy, working, lastBusy, now());
        lastBusy = step.LastBusy;
        step.HandedBack.Select(id => $"tasks: handed back {Line(step.Board, id)}, no session worked on it for {StalledTasks.After.TotalMinutes:0} minutes")
            .Concat(step.TakenBack.Select(id => $"tasks: taken back {Line(step.Board, id)}, its session works on it again"))
            .ToList().ForEach(line => trace?.Invoke(line));
        CommitIfChanged(step.Board);
    }

    static string Line(TaskBoard board, Guid id) =>
        $"{id.ToString()[..8]} \"{board.Tasks.Single(t => t.Id == id).Title}\"";

    /// <summary>An unknown live session becomes a task (TaskBoard.AdoptSession); one that has a task is refused.</summary>
    public Result<TaskItem> Adopt(ClaudeAgent agent, Maybe<Guid> workspace) =>
        Board.AdoptSession(agent, workspace, now(), makeId()).Tap(r => Commit(r.Board)).Map(r => r.Task);

    /// <summary>Frees the tasks of workspaces that no longer exist. Saves only when something changed.</summary>
    public void Prune(IReadOnlySet<Guid> knownWorkspaces)
    {
        if (Board.Tasks.Any(t => t.WorkspaceId.HasValue && !knownWorkspaces.Contains(t.WorkspaceId.Value)))
            Commit(Board.Prune(knownWorkspaces, now()));
    }

    // --- helpers ---

    /// <summary>Takes a successful change, saves and pulses; passes a failure through untouched.</summary>
    Result Apply(Result<TaskBoard> change) => change.Tap(Commit);

    Result ApplyIfChanged(Result<TaskBoard> change) => change.Tap(CommitIfChanged);

    void CommitIfChanged(TaskBoard board)
    {
        if (!ReferenceEquals(board, Board)) Commit(board);
    }

    /// <summary>
    /// The in-memory board is the truth for this run, so it changes even when the save fails: a full disk must
    /// not make the bar lie about what Petre just did. The failure is traced, never thrown.
    /// </summary>
    void Commit(TaskBoard board)
    {
        // Every status this change moved goes into its task's status log here, the one place every change passes
        // through (TaskStatusLog.cs has why it is not done in TaskBoard's own paths).
        Board = TaskStatusLog.Logged(Board, board, now());
        var saved = store.Save(board);
        if (saved.IsFailure) trace?.Invoke($"tasks: save failed: {saved.Error}");
        changed.OnNext(Unit.Default);
    }
}
