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

    public Result<TaskItem> AddNote(string title, Maybe<Guid> workspace) =>
        Board.AddNote(title, workspace, now(), makeId()).Tap(r => Commit(r.Board)).Map(r => r.Task);

    public Result<TaskItem> Create(string title, Maybe<Guid> workspace, TaskSession session) =>
        Board.Create(title, workspace, session, now(), makeId()).Tap(r => Commit(r.Board)).Map(r => r.Task);

    public Result Join(Guid id, TaskSession session) => Apply(Board.Join(id, session, now()));

    public Result SetStatusLine(Guid id, string text) => Apply(Board.SetStatusLine(id, text, now()));

    public Result Ask(Guid id, string question) => Apply(Board.Ask(id, question, now()));

    public Result MarkDone(Guid id) => Apply(Board.MarkDone(id, now()));

    public Result Close(Guid id) => Apply(Board.Close(id, now()));

    public Result Reopen(Guid id) => Apply(Board.Reopen(id, now()));

    public Result SetStatus(Guid id, TaskStatus status) => Apply(Board.SetStatus(id, status, now()));

    public Result Rename(Guid id, string title) => Apply(Board.Rename(id, title, now()));

    public Result MoveTo(Guid id, Maybe<Guid> workspace) => Apply(Board.MoveTo(id, workspace, now()));

    public Result Delete(Guid id) => Apply(Board.Delete(id));

    /// <summary>Undo on a line just closed: back as it was, in its place (TaskBoard.UndoClose).</summary>
    public Result UndoClose(Guid id, TaskStatus was) => Apply(Board.UndoClose(id, was, now()));

    // The flags and the manual order (Task 12). A change that changes nothing (a flag set to what it is, a drop where
    // the task already was) saves and pulses nothing, so a click that went nowhere costs no redraw.

    public Result SetFlag(Guid id, TaskFlag flag, bool on) => ApplyIfChanged(Board.SetFlag(id, flag, on, now()));

    public Result SetQuadrant(Guid id, Quadrant quadrant) => ApplyIfChanged(Board.SetQuadrant(id, quadrant, now()));

    public Result Place(Guid id, Quadrant quadrant, TaskPlace place) => ApplyIfChanged(Board.Place(id, quadrant, place, now()));

    public Result Comment(Guid id, Maybe<string> sessionId, string markdown) =>
        Apply(Board.Comment(id, sessionId, markdown, now(), makeId()));

    /// <summary>
    /// Petre sent a session a message. These two run on every prompt and every session end, nearly always with
    /// nothing to change, so they save and pulse only when the board really changed (TaskBoard returns the same
    /// board otherwise).
    /// </summary>
    public void Answered(string sessionId) => CommitIfChanged(Board.Answered(sessionId, now()));

    public void SessionEnded(string sessionId) => CommitIfChanged(Board.SessionEnded(sessionId, now()));

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
