using System.Reactive;
using System.Reactive.Subjects;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// Owns the live TaskBoard, saves every change through ITaskStore and is ticked once a second by the app. It is
// where the clock enters: TaskBoard stays pure and takes "now" from here. Design:
// docs/superpowers/specs/2026-11-19-tasks-in-the-bar-design.md ("Coming back ends the timer", "Persistence").
// Everything runs on the UI thread (the app ticks from a dispatcher timer), so there is no locking.
public sealed class TaskService(ITaskStore store, Func<DateTimeOffset> now, Func<Guid>? newId = null, Action<string>? trace = null)
{
    /// <summary>
    /// Arriving on a workspace counts as coming back only after staying this long, so flicking through desktops
    /// on the way elsewhere (Win+Ctrl+Arrow, the switcher) never ends a timer by accident.
    /// </summary>
    public static readonly TimeSpan Dwell = TimeSpan.FromSeconds(2);

    readonly Func<Guid> makeId = newId ?? Guid.NewGuid;
    readonly Subject<Unit> changed = new();
    readonly Subject<TaskItem> cameDue = new();

    // Tick state. previous: the workspace seen on the last tick (unset before the first tick, which is how the
    // first tick knows it is not an arrival: starting the app on a workspace is not coming back to it).
    // pending: an arrival waiting out the dwell. announced: ids already told to CameDue, so a task that stays
    // Due is announced once, not once a second.
    bool ticked;
    Maybe<Guid> previous = Maybe<Guid>.None;
    Maybe<(Guid Workspace, DateTimeOffset At)> pending = Maybe<(Guid Workspace, DateTimeOffset At)>.None;
    readonly HashSet<Guid> announced = [];

    public TaskBoard Board { get; private set; } = TaskBoard.Empty;

    /// <summary>The service's clock, so callers (the control pipe's `task list`) and tests agree on "now".</summary>
    public DateTimeOffset Now => now();

    /// <summary>Pulses after every change to the board, loaded or edited, so views refresh.</summary>
    public IObservable<Unit> Changed => changed;

    /// <summary>A task whose timer ran out. Fires once per task, including tasks already due at load.</summary>
    public IObservable<TaskItem> CameDue => cameDue;

    /// <summary>Replaces the board with what the store holds. A store failure leaves the board empty.</summary>
    public Result Load()
    {
        var loaded = store.Load();
        Board = loaded.IsSuccess ? loaded.Value : TaskBoard.Empty;
        changed.OnNext(Unit.Default);
        return loaded.IsSuccess ? Result.Success() : Result.Failure(loaded.Error);
    }

    public Result<TaskItem> Park(string title, Maybe<Guid> workspace, Maybe<TimeSpan> length) =>
        Board.Park(title, workspace, length, now(), makeId()).Tap(r => Commit(r.Board)).Map(r => r.Task);

    public Result SetTimer(Guid id, TimeSpan length) => Apply(Board.SetTimer(id, length, now()));

    public Result ClearTimer(Guid id) => Apply(Board.ClearTimer(id, now()));

    public Result Rename(Guid id, string title) => Apply(Board.Rename(id, title, now()));

    public Result MoveTo(Guid id, Maybe<Guid> workspace) => Apply(Board.MoveTo(id, workspace, now()));

    public Result Done(Guid id) => Apply(Board.Done(id, now()));

    public Result Delete(Guid id) => Apply(Board.Delete(id));

    /// <summary>
    /// Called once a second with the workspace Petre is on (none when it is not known). Detects an arrival that
    /// has stayed for <see cref="Dwell"/> and ends the timers there, then announces newly due tasks.
    /// </summary>
    public void Tick(Maybe<Guid> currentWorkspace)
    {
        var at = now();
        if (!ticked)
        {
            ticked = true;
            previous = currentWorkspace;
        }
        else if (currentWorkspace != previous)
        {
            pending = currentWorkspace.HasValue ? (currentWorkspace.Value, at) : Maybe<(Guid, DateTimeOffset)>.None;
            previous = currentWorkspace;
        }

        if (pending is { HasValue: true, Value: var arrival }
            && currentWorkspace.HasValue && currentWorkspace.Value == arrival.Workspace
            && at - arrival.At >= Dwell)
        {
            pending = Maybe<(Guid, DateTimeOffset)>.None;
            // The arrival's own time, not now: a timer Petre set during the dwell is newer than the arrival and
            // must survive it (TaskBoard.CameBack, final review I1).
            Commit(Board.CameBack(arrival.Workspace, arrival.At, at));
        }

        var due = Board.Tasks.Where(t => t.StateAt(at) == TaskState.Due).ToArray();
        due.Where(t => announced.Add(t.Id)).ToList().ForEach(cameDue.OnNext);
        announced.RemoveWhere(id => due.All(t => t.Id != id));
    }

    /// <summary>Frees the tasks of workspaces that no longer exist. Saves only when something changed.</summary>
    public void Prune(IReadOnlySet<Guid> knownWorkspaces)
    {
        if (Board.Tasks.Any(t => t.WorkspaceId.HasValue && !knownWorkspaces.Contains(t.WorkspaceId.Value)))
            Commit(Board.Prune(knownWorkspaces, now()));
    }

    /// <summary>
    /// The rows to show, in the order that needs Petre next: due first, oldest first (a timer's Ends; an ask
    /// counts as due right now, so it follows timers that came due earlier and leads any that tie), then running
    /// by soonest end, then open by Created newest first, then tasks done today (local time). Older done tasks
    /// are left out.
    /// </summary>
    public IReadOnlyList<TaskEntry> Entries(IReadOnlyCollection<Guid> askingWorkspaces, DateOnly today)
    {
        var at = now();
        var asks = askingWorkspaces.Select(w => (Entry: new TaskEntry(TaskEntryKind.Ask, Maybe<TaskItem>.None, w, TaskState.Due), When: at, Rank: 0));
        var tasks = Board.Tasks.Select(t => (Task: t, State: t.StateAt(at)));
        var dueTasks = tasks.Where(x => x.State == TaskState.Due).Select(x => (Entry: Row(x.Task, x.State), When: x.Task.Timer.Value.Ends, Rank: 1));
        var due = asks.Concat(dueTasks).OrderBy(x => x.When).ThenBy(x => x.Rank).Select(x => x.Entry);
        var running = tasks.Where(x => x.State == TaskState.Running).OrderBy(x => x.Task.Timer.Value.Ends).Select(x => Row(x.Task, x.State));
        var open = tasks.Where(x => x.State == TaskState.Open).OrderByDescending(x => x.Task.Created).Select(x => Row(x.Task, x.State));
        var done = tasks.Where(x => x.State == TaskState.Done && DateOnly.FromDateTime(x.Task.DoneAt.Value.ToLocalTime().DateTime) == today)
            .OrderBy(x => x.Task.DoneAt.Value).Select(x => Row(x.Task, x.State));
        return due.Concat(running).Concat(open).Concat(done).ToArray();
    }

    /// <summary>The workspaces with at least one task whose timer has run out.</summary>
    public IReadOnlySet<Guid> DueWorkspaces() =>
        Board.Tasks.Where(t => t.StateAt(now()) == TaskState.Due && t.WorkspaceId.HasValue)
            .Select(t => t.WorkspaceId.Value).ToHashSet();

    /// <summary>Per workspace, the running timer that ends soonest (what its pie shows).</summary>
    public IReadOnlyDictionary<Guid, TaskTimer> RunningWorkspaces() =>
        Board.Tasks.Where(t => t.StateAt(now()) == TaskState.Running && t.WorkspaceId.HasValue)
            .GroupBy(t => t.WorkspaceId.Value)
            .ToDictionary(g => g.Key, g => g.Select(t => t.Timer.Value).MinBy(timer => timer.Ends)!);

    // --- helpers ---

    static TaskEntry Row(TaskItem task, TaskState state) => new(TaskEntryKind.Task, task, task.WorkspaceId, state);

    /// <summary>Takes a successful change, saves and pulses; passes a failure through untouched.</summary>
    Result Apply(Result<TaskBoard> change) => change.Tap(Commit);

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
