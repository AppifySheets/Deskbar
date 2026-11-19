using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// Every rule for changing tasks, as pure functions: a method returns a new board and never reads a clock
// (callers pass "now"), so the rules are tested without waiting. Each change stamps UpdatedAt = now on exactly
// the tasks it touched. Design: docs/superpowers/specs/2026-11-19-tasks-in-the-bar-design.md ("The model",
// "Coming back ends the timer").
public sealed record TaskBoard(IReadOnlyList<TaskItem> Tasks)
{
    public static readonly TaskBoard Empty = new(Array.Empty<TaskItem>());

    /// <summary>The shortest id prefix <see cref="Find"/> accepts, so a typo cannot hit a task by luck.</summary>
    const int MinPrefix = 4;

    /// <summary>
    /// Parks a thread: a title matching a live task on the same workspace (trimmed, ignoring case) always
    /// returns that task, so parking the same thread ten times leaves one row. With a length the timer is
    /// replaced (the old one recorded as Replaced); without one nothing changes. Any other title adds a task.
    /// Free tasks (no workspace) never match, so each park of one is new.
    /// </summary>
    public Result<(TaskBoard Board, TaskItem Task)> Park(
        string title, Maybe<Guid> workspace, Maybe<TimeSpan> length, DateTimeOffset now, Guid newId) =>
        CleanTitle(title).Map(clean =>
            LiveMatch(workspace, clean) is { HasValue: true } match
                ? ParkOnto(match.Value, length, now)
                : Add(clean, workspace, length, now, newId));

    public IReadOnlyList<TaskItem> LiveOn(Guid workspace) =>
        Tasks.Where(t => t.IsLive && t.WorkspaceId.HasValue && t.WorkspaceId.Value == workspace).ToArray();

    /// <summary>The live task on a workspace changed most recently: the default title when parking again.</summary>
    public Maybe<TaskItem> LatestLiveOn(Guid workspace) =>
        LiveOn(workspace).OrderByDescending(t => t.UpdatedAt).FirstOrDefault() is { } latest ? latest : Maybe<TaskItem>.None;

    /// <summary>Starts a timer now; one already on the task is recorded as Replaced.</summary>
    public Result<TaskBoard> SetTimer(Guid id, TimeSpan length, DateTimeOffset now) =>
        Change(id, t => Restarted(t, length, now));

    public Result<TaskBoard> ClearTimer(Guid id, DateTimeOffset now) =>
        Change(id, t => Stamped(Ended(t, TimerEnd.Cleared, now), now));

    public Result<TaskBoard> Rename(Guid id, string title, DateTimeOffset now) =>
        CleanTitle(title).Bind(clean => Change(id, t => t with { Title = clean, UpdatedAt = now }));

    public Result<TaskBoard> MoveTo(Guid id, Maybe<Guid> workspace, DateTimeOffset now) =>
        Change(id, t => t with { WorkspaceId = workspace, UpdatedAt = now });

    /// <summary>Marks the task done; a timer on it ends as Done.</summary>
    public Result<TaskBoard> Done(Guid id, DateTimeOffset now) =>
        Change(id, t => Ended(t, TimerEnd.Done, now) with { DoneAt = now, UpdatedAt = now });

    public Result<TaskBoard> Delete(Guid id) =>
        Tasks.Any(t => t.Id == id)
            ? new TaskBoard(Tasks.Where(t => t.Id != id).ToArray())
            : Result.Failure<TaskBoard>(NoTask(id.ToString()));

    /// <summary>
    /// Petre arrived on the workspace and stayed: every live task there with a timer, Running or Due alike,
    /// ends it as CameBack and becomes Open.
    /// </summary>
    public TaskBoard CameBack(Guid workspace, DateTimeOffset now) =>
        new(Tasks.Select(t => t.IsLive && t.Timer.HasValue && t.WorkspaceId.HasValue && t.WorkspaceId.Value == workspace
            ? Stamped(Ended(t, TimerEnd.CameBack, now), now)
            : t).ToArray());

    /// <summary>A workspace that no longer exists frees its tasks; timers keep going, nothing is lost.</summary>
    public TaskBoard Prune(IReadOnlySet<Guid> knownWorkspaces, DateTimeOffset now) =>
        new(Tasks.Select(t => t.WorkspaceId.HasValue && !knownWorkspaces.Contains(t.WorkspaceId.Value)
            ? t with { WorkspaceId = Maybe<Guid>.None, UpdatedAt = now }
            : t).ToArray());

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

    // --- helpers ---

    static Result<string> CleanTitle(string title) =>
        string.IsNullOrWhiteSpace(title) ? Result.Failure<string>("A task needs a title") : title.Trim();

    static bool SameTitle(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    static string NoTask(string id) => $"No task with id {id}";

    /// <summary>The live task on that workspace with that title, if any. A free task has nothing to match.</summary>
    Maybe<TaskItem> LiveMatch(Maybe<Guid> workspace, string cleanTitle) =>
        workspace.HasNoValue
            ? Maybe<TaskItem>.None
            : LiveOn(workspace.Value).FirstOrDefault(t => SameTitle(t.Title, cleanTitle)) is { } hit ? hit : Maybe<TaskItem>.None;

    /// <summary>A matching title returns the existing task; a length replaces its timer, no length changes nothing.</summary>
    (TaskBoard Board, TaskItem Task) ParkOnto(TaskItem existing, Maybe<TimeSpan> length, DateTimeOffset now) =>
        length.HasNoValue
            ? (this, existing)
            : WithReplaced(Restarted(existing, length.Value, now));

    (TaskBoard Board, TaskItem Task) WithReplaced(TaskItem updated) =>
        (new TaskBoard(Tasks.Select(t => t.Id == updated.Id ? updated : t).ToArray()), updated);

    (TaskBoard Board, TaskItem Task) Add(string title, Maybe<Guid> workspace, Maybe<TimeSpan> length, DateTimeOffset now, Guid newId) =>
        WithAppended(new TaskItem(newId, title, workspace, now, now,
            length.Map(l => new TaskTimer(now, l)), Maybe<DateTimeOffset>.None, Array.Empty<TimerRecord>()));

    (TaskBoard Board, TaskItem Task) WithAppended(TaskItem task) => (new TaskBoard(Tasks.Append(task).ToArray()), task);

    static TaskItem Stamped(TaskItem task, DateTimeOffset now) => task with { UpdatedAt = now };

    /// <summary>Moves a timer, if there is one, into history with how it ended. No timer: unchanged.</summary>
    static TaskItem Ended(TaskItem task, TimerEnd how, DateTimeOffset now) =>
        task.Timer.HasNoValue
            ? task
            : task with
            {
                Timer = Maybe<TaskTimer>.None,
                History = task.History.Append(new TimerRecord(task.Timer.Value.Started, task.Timer.Value.Length, now, how)).ToArray()
            };

    static TaskItem Restarted(TaskItem task, TimeSpan length, DateTimeOffset now) =>
        Ended(task, TimerEnd.Replaced, now) with { Timer = new TaskTimer(now, length), UpdatedAt = now };

    Result<TaskBoard> Change(Guid id, Func<TaskItem, TaskItem> change) =>
        Tasks.Any(t => t.Id == id)
            ? new TaskBoard(Tasks.Select(t => t.Id == id ? change(t) : t).ToArray())
            : Result.Failure<TaskBoard>(NoTask(id.ToString()));
}
