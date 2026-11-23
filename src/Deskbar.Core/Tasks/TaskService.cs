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
public sealed class TaskService(ITaskStore store, Func<DateTimeOffset> now, Func<Guid>? newId = null, Action<string>? trace = null)
{
    readonly Func<Guid> makeId = newId ?? Guid.NewGuid;
    readonly Subject<Unit> changed = new();

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
    public Result<TaskItem> AddNote(string title, Maybe<Guid> workspace, Quadrant quadrant = Quadrant.IV) =>
        Board.AddNote(title, workspace, now(), makeId(), quadrant).Tap(r => Commit(r.Board)).Map(r => r.Task);

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

    public Result MarkDone(Guid id) => Apply(Board.MarkDone(id, now()));

    public Result Close(Guid id) => Apply(Board.Close(id, now()));

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

    public Result Delete(Guid id) => Apply(Board.Delete(id));

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

    public Result Comment(Guid id, Maybe<string> sessionId, string markdown) =>
        Apply(Board.Comment(id, sessionId, markdown, now(), makeId()));

    /// <summary>
    /// Petre sent a session a message. These two run on every prompt and every session end, nearly always with
    /// nothing to change, so they save and pulse only when the board really changed (TaskBoard returns the same
    /// board otherwise).
    /// </summary>
    public void Answered(string sessionId) => CommitIfChanged(Board.Answered(sessionId, now()));

    public void SessionEnded(string sessionId) => CommitIfChanged(Board.SessionEnded(sessionId, now()));

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

    // The quiet clock of StalledTasks: when each watched Claude task last had a session working on it. In memory only;
    // after a restart StalledTasks measures from the task's UpdatedAt instead (see its header).
    ImmutableDictionary<Guid, DateTimeOffset> lastBusy = ImmutableDictionary<Guid, DateTimeOffset>.Empty;

    /// <summary>
    /// Stalled tasks (StalledTasks; spec docs/superpowers/specs/2026-11-23-stalled-claude-tasks-design.md): called on
    /// every poll of Claude Code's session list with the ids of the sessions working now. A Claude task quiet for
    /// StalledTasks.After goes to Petre, a handed-back task whose session works again goes back to Claude. Saves and
    /// pulses only when a task changed hands, and traces each move.
    /// </summary>
    public void Stalled(IReadOnlySet<string> working)
    {
        var step = StalledTasks.Step(Board, working, lastBusy, now());
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
        Board = board;
        var saved = store.Save(board);
        if (saved.IsFailure) trace?.Invoke($"tasks: save failed: {saved.Error}");
        changed.OnNext(Unit.Default);
    }
}
