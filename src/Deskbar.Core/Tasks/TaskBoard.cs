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
//   * A closed task takes none of a session's changes (join, status line, ask, done): Petre has closed it and new
//     work needs a new task. Renaming, moving, commenting and setting a status by hand stay open to Petre.
//   * Raised (the sort key) moves when a task is made and when a session asks, and on nothing else, so a status line
//     changing never reorders the list.
//   * Answering is Petre's next message to a session: it moves that session's task from Waiting on you, or from
//     Done (he is continuing the work), back to In progress, and touches no other task.
public sealed record TaskBoard(IReadOnlyList<TaskItem> Tasks)
{
    public static readonly TaskBoard Empty = new(Array.Empty<TaskItem>());

    /// <summary>The shortest id prefix <see cref="Find"/> accepts, so a typo cannot hit a task by luck.</summary>
    const int MinPrefix = 4;

    const string TaskIsClosed = "The task is closed.";
    const string TaskIsNotClosed = "The task is not closed.";
    const string UseClose = "Use close to close a task";

    // --- making tasks ------------------------------------------------------------------------------

    /// <summary>One of Petre's notes: Backlog, no sessions. Every add is a new task, even with a title seen before.</summary>
    public Result<(TaskBoard Board, TaskItem Task)> AddNote(string title, Maybe<Guid> workspace, DateTimeOffset now, Guid newId) =>
        CleanTitle(title).Map(clean => WithAppended(NewTask(newId, clean, workspace, TaskStatus.Backlog, [], now)));

    /// <summary>A task a session makes for its own work: In progress, with that session on it.</summary>
    public Result<(TaskBoard Board, TaskItem Task)> Create(string title, Maybe<Guid> workspace, TaskSession session, DateTimeOffset now, Guid newId) =>
        CleanTitle(title).Map(clean => WithAppended(NewTask(newId, clean, workspace, TaskStatus.InProgress, [session], now)));

    // --- what a session does -----------------------------------------------------------------------

    /// <summary>
    /// A session takes the task up: it becomes Claude's and In progress. A task already waiting on Petre keeps
    /// waiting, because the question is still unanswered. A session with that id already on the task (a resumed
    /// conversation keeps its id) is replaced, live again, rather than listed twice.
    /// </summary>
    public Result<TaskBoard> Join(Guid id, TaskSession session, DateTimeOffset now) =>
        ChangeOpen(id, t => t with
        {
            Sessions = WithSession(t.Sessions, session),
            Status = t.Status == TaskStatus.WaitingOnYou ? t.Status : TaskStatus.InProgress,
            UpdatedAt = now,
        });

    /// <summary>The short line a session keeps current. Does not move <see cref="TaskItem.Raised"/>.</summary>
    public Result<TaskBoard> SetStatusLine(Guid id, string text, DateTimeOffset now) =>
        ChangeOpen(id, t => t with { StatusLine = (text ?? "").Trim(), UpdatedAt = now });

    /// <summary>A session needs a decision: the task waits on Petre, shows the question and moves to the top.</summary>
    public Result<TaskBoard> Ask(Guid id, string question, DateTimeOffset now) =>
        CleanText(question, "An ask needs a question").Bind(q =>
            ChangeOpen(id, t => t with { Status = TaskStatus.WaitingOnYou, Question = q.Trim(), Raised = now, UpdatedAt = now }));

    /// <summary>
    /// Petre sent the session a message. Its task, when Waiting on you or Done, goes back to In progress and drops
    /// the question. Anything else, or a session with no open task, returns this same board, so the caller can
    /// tell nothing changed by reference and skip the save.
    /// </summary>
    public TaskBoard Answered(string sessionId, DateTimeOffset now) =>
        TaskOf(sessionId)
            .Where(t => t.Status is TaskStatus.WaitingOnYou or TaskStatus.Done)
            .Map(t => Replaced(t with { Status = TaskStatus.InProgress, Question = Maybe<string>.None, UpdatedAt = now }))
            .GetValueOrDefault(this);

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

    /// <summary>Claude's clear finish. The task stays in the list, waiting for Petre to close it.</summary>
    public Result<TaskBoard> MarkDone(Guid id, DateTimeOffset now) =>
        ChangeOpen(id, t => t with { Status = TaskStatus.Done, Question = Maybe<string>.None, UpdatedAt = now });

    // --- what Petre does ---------------------------------------------------------------------------

    /// <summary>Petre closes the task: it leaves the lists. Closing a closed task is refused.</summary>
    public Result<TaskBoard> Close(Guid id, DateTimeOffset now) =>
        ChangeOpen(id, t => t with { Status = TaskStatus.Closed, Question = Maybe<string>.None, UpdatedAt = now });

    /// <summary>A closed task comes back: In progress while a session is live on it, else Backlog.</summary>
    public Result<TaskBoard> Reopen(Guid id, DateTimeOffset now) =>
        Tasks.FirstOrDefault(t => t.Id == id) is { IsOpen: true }
            ? Result.Failure<TaskBoard>(TaskIsNotClosed)
            : Change(id, t => t with { Status = t.HasLiveSession ? TaskStatus.InProgress : TaskStatus.Backlog, UpdatedAt = now });

    /// <summary>
    /// Any status but Closed, by hand, from any status (a closed task set to Done is reopened as Done). Closed goes
    /// through <see cref="Close"/>, the one way to close. Leaving Waiting on you drops the question; setting it by
    /// hand does not move <see cref="TaskItem.Raised"/>, since no session asked anything.
    /// </summary>
    public Result<TaskBoard> SetStatus(Guid id, TaskStatus status, DateTimeOffset now) =>
        status == TaskStatus.Closed
            ? Result.Failure<TaskBoard>(UseClose)
            : Change(id, t => t with
            {
                Status = status,
                Question = status == TaskStatus.WaitingOnYou ? t.Question : Maybe<string>.None,
                UpdatedAt = now,
            });

    public Result<TaskBoard> Rename(Guid id, string title, DateTimeOffset now) =>
        CleanTitle(title).Bind(clean => Change(id, t => t with { Title = clean, UpdatedAt = now }));

    public Result<TaskBoard> MoveTo(Guid id, Maybe<Guid> workspace, DateTimeOffset now) =>
        Change(id, t => t with { WorkspaceId = workspace, UpdatedAt = now });

    public Result<TaskBoard> Delete(Guid id) =>
        Tasks.Any(t => t.Id == id)
            ? new TaskBoard(Tasks.Where(t => t.Id != id).ToArray())
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
        }));

    /// <summary>A workspace that no longer exists frees its tasks; nothing else about them changes.</summary>
    public TaskBoard Prune(IReadOnlySet<Guid> knownWorkspaces, DateTimeOffset now) =>
        new(Tasks.Select(t => t.WorkspaceId.HasValue && !knownWorkspaces.Contains(t.WorkspaceId.Value)
            ? t with { WorkspaceId = Maybe<Guid>.None, UpdatedAt = now }
            : t).ToArray());

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
    /// What a session starting on <paramref name="workspace"/> is offered to join, in the pile's order: the open
    /// tasks on that workspace, notes included, plus the open Claude tasks on no workspace. Petre's notes on no
    /// workspace (errands) are never offered (spec, "What a session is offered").
    /// </summary>
    public IReadOnlyList<TaskItem> Offered(Maybe<Guid> workspace) =>
        Pile().Where(t => t.WorkspaceId.HasValue
            ? workspace.HasValue && t.WorkspaceId.Value == workspace.Value
            : t.IsClaudes).ToArray();

    /// <summary>
    /// The open tasks in the order the bar shows them: Waiting on you first, the latest ask on top; then Done, the
    /// latest first, waiting for Petre to close them; then the rest by <see cref="TaskItem.Raised"/>, newest first.
    /// Closed tasks are left out.
    /// </summary>
    public IReadOnlyList<TaskItem> Pile() =>
        Tasks.Where(t => t.Status == TaskStatus.WaitingOnYou).OrderByDescending(t => t.Raised)
            .Concat(Tasks.Where(t => t.Status == TaskStatus.Done).OrderByDescending(t => t.UpdatedAt))
            .Concat(Tasks.Where(t => t.Status is TaskStatus.Backlog or TaskStatus.InProgress).OrderByDescending(t => t.Raised))
            .ToArray();

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

    (TaskBoard Board, TaskItem Task) WithAppended(TaskItem task) => (new TaskBoard(Tasks.Append(task).ToArray()), task);

    TaskBoard Replaced(TaskItem updated) => new(Tasks.Select(t => t.Id == updated.Id ? updated : t).ToArray());

    Result<TaskBoard> Change(Guid id, Func<TaskItem, TaskItem> change) =>
        Tasks.Any(t => t.Id == id)
            ? new TaskBoard(Tasks.Select(t => t.Id == id ? change(t) : t).ToArray())
            : Result.Failure<TaskBoard>(NoTask(id.ToString()));

    /// <summary><see cref="Change"/> for the changes a closed task refuses (see the header).</summary>
    Result<TaskBoard> ChangeOpen(Guid id, Func<TaskItem, TaskItem> change) =>
        Tasks.FirstOrDefault(t => t.Id == id) is { IsOpen: false }
            ? Result.Failure<TaskBoard>(TaskIsClosed)
            : Change(id, change);
}
