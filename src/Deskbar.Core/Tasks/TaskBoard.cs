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
//   * The order (Task 12, Rulings 24, 25 and 28; TaskOrder.cs): Waiting on you is pinned on top, latest ask first, by
//     Raised. Below it come Covey's quadrants I to IV, which the two flags name, each in Petre's manual order, the
//     tasks' Rank. A new task goes to the top of its quadrant, a Done task keeps its place, a closed one leaves and a
//     reopened one comes back at the top. Undo on a close is not a reopening: the line never left its place on
//     screen, so it keeps its rank (UndoClose).
//   * Raised moves when a task is made, when a session asks and when a task becomes Done, and on nothing else; Rank
//     moves only on Petre's drag, a flag, a reopening and a new task. So a status line, a comment or a session ending
//     never reorders the list.
//   * A session works on one task at a time. Joining or creating a task ends that session's live entry on any other
//     open task, so TaskOf and Answered can only ever mean the task it is on now (controller ruling 4, which the
//     hooks rely on).
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
    const string WaitingStaysOnTop = "A task waiting on you stays on top until it is answered; its flags still change";

    // --- making tasks ------------------------------------------------------------------------------

    /// <summary>One of Petre's notes: Backlog, no sessions. Every add is a new task, even with a title seen before.</summary>
    public Result<(TaskBoard Board, TaskItem Task)> AddNote(string title, Maybe<Guid> workspace, DateTimeOffset now, Guid newId) =>
        CleanTitle(title).Map(clean => WithAppended(NewTask(newId, clean, workspace, TaskStatus.Backlog, [], now) with { Rank = TopRank(Quadrant.IV, newId) }));

    /// <summary>
    /// A task a session makes for its own work: In progress, with that session on it. The session leaves whatever
    /// other task it was live on (see the header).
    /// </summary>
    public Result<(TaskBoard Board, TaskItem Task)> Create(string title, Maybe<Guid> workspace, TaskSession session, DateTimeOffset now, Guid newId) =>
        CleanTitle(title).Map(clean =>
            WithoutSessionElsewhere(session.Id, newId, now)
                .WithAppended(NewTask(newId, clean, workspace, TaskStatus.InProgress, [session], now) with { Rank = TopRank(Quadrant.IV, newId) }));

    // --- what a session does -----------------------------------------------------------------------

    /// <summary>
    /// A session takes the task up: it becomes Claude's and In progress. A task already waiting on Petre keeps
    /// waiting, because the question is still unanswered. A session with that id already on the task (a resumed
    /// conversation keeps its id) is replaced, live again, rather than listed twice. The session leaves whatever other
    /// task it was live on (see the header); a refused join (a closed task) leaves it where it was.
    /// </summary>
    public Result<TaskBoard> Join(Guid id, TaskSession session, DateTimeOffset now) =>
        ChangeOpen(id, t => t with
        {
            Sessions = WithSession(t.Sessions, session),
            Status = t.Status == TaskStatus.WaitingOnYou ? t.Status : TaskStatus.InProgress,
            UpdatedAt = now,
        }).Map(joined => joined.WithoutSessionElsewhere(session.Id, id, now));

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

    /// <summary>
    /// Claude's clear finish. The task stays in the list, waiting for Petre to close it. Becoming Done stamps
    /// <see cref="TaskItem.Raised"/>, which orders the Done group; a task already Done keeps its stamp.
    /// </summary>
    public Result<TaskBoard> MarkDone(Guid id, DateTimeOffset now) =>
        ChangeOpen(id, t => BecameDone(t, now) with { Question = Maybe<string>.None, UpdatedAt = now });

    // --- what Petre does ---------------------------------------------------------------------------

    /// <summary>Petre closes the task: it leaves the lists. Closing a closed task is refused.</summary>
    public Result<TaskBoard> Close(Guid id, DateTimeOffset now) =>
        ChangeOpen(id, t => t with { Status = TaskStatus.Closed, Question = Maybe<string>.None, UpdatedAt = now });

    /// <summary>
    /// A closed task comes back: In progress while a session is live on it, else Backlog, at the top of its quadrant
    /// (Ruling 25: it left the list, so it comes back the way a new task arrives).
    /// </summary>
    public Result<TaskBoard> Reopen(Guid id, DateTimeOffset now) =>
        Tasks.FirstOrDefault(t => t.Id == id) is { IsOpen: true }
            ? Result.Failure<TaskBoard>(TaskIsNotClosed)
            : Change(id, t => OnTopIfClosed(t) with { Status = t.HasLiveSession ? TaskStatus.InProgress : TaskStatus.Backlog, UpdatedAt = now });

    /// <summary>
    /// Undo on a line just closed (the bar's and the window's six seconds): the task comes back Done when it was Done,
    /// else as <see cref="Reopen"/> would bring it, In progress with a live session or Backlog, but at the place it had,
    /// not on top, because its line never left the screen. A task that is not closed is refused.
    /// </summary>
    public Result<TaskBoard> UndoClose(Guid id, TaskStatus was, DateTimeOffset now) =>
        Tasks.FirstOrDefault(t => t.Id == id) is { IsOpen: true }
            ? Result.Failure<TaskBoard>(TaskIsNotClosed)
            : Change(id, t => (was == TaskStatus.Done ? BecameDone(t, now) : t) with
            {
                Status = was == TaskStatus.Done ? TaskStatus.Done : t.HasLiveSession ? TaskStatus.InProgress : TaskStatus.Backlog,
                UpdatedAt = now,
            });

    /// <summary>
    /// Any status but Closed, by hand, from any status (a closed task set to Done is reopened as Done, at the top of
    /// its quadrant as <see cref="Reopen"/> brings it). Closed goes through <see cref="Close"/>, the one way to close.
    /// Leaving Waiting on you drops the question; setting it by hand does not move <see cref="TaskItem.Raised"/>, since
    /// no session asked anything. Becoming Done by hand stamps it, as <see cref="MarkDone"/> does.
    /// </summary>
    public Result<TaskBoard> SetStatus(Guid id, TaskStatus status, DateTimeOffset now) =>
        status == TaskStatus.Closed
            ? Result.Failure<TaskBoard>(UseClose)
            : Change(id, t => (status == TaskStatus.Done ? BecameDone(OnTopIfClosed(t), now) : OnTopIfClosed(t)) with
            {
                Status = status,
                Question = status == TaskStatus.WaitingOnYou ? t.Question : Maybe<string>.None,
                UpdatedAt = now,
            });

    // --- the flags and the order (Task 12; TaskOrder.cs) -------------------------------------------

    /// <summary>
    /// A toggle on a line, or `task flag`: one flag on or off. The task goes to the top of the quadrant it lands in, as
    /// a new arrival there. A flag set to what it already is returns this same board. A task waiting on Petre takes
    /// its flags (it shows them) but stays pinned until answered; a closed task is refused.
    /// </summary>
    public Result<TaskBoard> SetFlag(Guid id, TaskFlag flag, bool on, DateTimeOffset now) =>
        Lookup(id).Bind(task => SetQuadrant(id, task.Quadrant.With(flag, on), now));

    /// <summary>
    /// Both flags at once, as a drop on another cell of the window's Matrix does for a task waiting on Petre: the task
    /// goes to the top of <paramref name="quadrant"/>. The same quadrant returns this same board.
    /// </summary>
    public Result<TaskBoard> SetQuadrant(Guid id, Quadrant quadrant, DateTimeOffset now) =>
        Lookup(id).Bind(task =>
            !task.IsOpen ? Result.Failure<TaskBoard>(TaskIsClosed)
            : task.Quadrant == quadrant ? this
            : Replaced(WithFlags(task, quadrant) with { Rank = TopRank(quadrant, id), UpdatedAt = now }));

    /// <summary>
    /// A drag, or `task order`: the task goes to <paramref name="place"/> in <paramref name="quadrant"/>, and dropping
    /// it in another quadrant than its own sets that quadrant's flags. It takes the middle of its new neighbours'
    /// ranks, so only the moved task changes; when the numbers between two neighbours run out (the same gap halved
    /// some fifty times) the quadrant is numbered afresh and every task whose rank changed is stamped. A drop where the
    /// task already is returns this same board. Refused: a closed task, a task waiting on Petre (pinned on top until
    /// answered; Ruling 25), and a place beside a task that is not in that quadrant's order.
    /// </summary>
    public Result<TaskBoard> Place(Guid id, Quadrant quadrant, TaskPlace place, DateTimeOffset now) =>
        Lookup(id).Bind(task =>
            !task.IsOpen ? Result.Failure<TaskBoard>(TaskIsClosed)
            : task.Status == TaskStatus.WaitingOnYou ? Result.Failure<TaskBoard>(WaitingStaysOnTop)
            : IndexFor(task, quadrant, place).Map(index => Placed(task, quadrant, index, now)));

    /// <summary>
    /// One quadrant's tasks in their manual order: open, not waiting on Petre (those are pinned above every quadrant),
    /// smallest rank first. Two equal ranks (a hand-edited file) fall back to the newest raised first, then the id, so
    /// the order is always the same.
    /// </summary>
    public IReadOnlyList<TaskItem> Group(Quadrant quadrant) =>
        Tasks.Where(t => t.IsOpen && t.Status != TaskStatus.WaitingOnYou && t.Quadrant == quadrant)
            .OrderBy(t => t.Rank).ThenByDescending(t => t.Raised).ThenBy(t => t.Id)
            .ToArray();

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
    /// The open tasks in the order the bar shows them (Ruling 25): Waiting on you first, the latest ask on top (by
    /// <see cref="TaskItem.Raised"/>), whatever their flags; then Covey's quadrants I, II, III and IV, each in its
    /// manual order (<see cref="Group"/>). A Done task sits in its quadrant at its place. Nothing here reads UpdatedAt,
    /// so a status line, a comment or a session ending cannot move a line. Closed tasks are left out.
    /// </summary>
    public IReadOnlyList<TaskItem> Pile() =>
        Tasks.Where(t => t.Status == TaskStatus.WaitingOnYou).OrderByDescending(t => t.Raised)
            .Concat(Enum.GetValues<Quadrant>().SelectMany(Group))
            .ToArray();

    /// <summary>
    /// A task's 1-based place in its quadrant's order, as `task list` prints it and `task order &lt;n&gt;` takes it;
    /// none for a task waiting on Petre (pinned above the quadrants until answered) or closed.
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

    /// <summary>Done, with Raised stamped when it was not Done before; an already Done task keeps its stamp.</summary>
    static TaskItem BecameDone(TaskItem task, DateTimeOffset now) =>
        task with { Status = TaskStatus.Done, Raised = task.Status == TaskStatus.Done ? task.Raised : now };

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

    /// <summary>A closed task coming back goes to the top of its quadrant (Ruling 25); an open one keeps its place.</summary>
    TaskItem OnTopIfClosed(TaskItem task) => task.IsOpen ? task : task with { Rank = TopRank(task.Quadrant, task.Id) };

    /// <summary>
    /// Where <paramref name="place"/> puts <paramref name="task"/> among the other tasks of <paramref name="quadrant"/>:
    /// an index into that order with the task itself left out. Placing a task beside itself is its own place.
    /// </summary>
    Result<int> IndexFor(TaskItem task, Quadrant quadrant, TaskPlace place)
    {
        var others = Group(quadrant).Where(t => t.Id != task.Id).Select(t => t.Id).ToList();
        var own = Group(quadrant).Select(t => t.Id).ToList().IndexOf(task.Id);
        Result<int> Beside(Guid neighbour, int after) =>
            neighbour == task.Id && own >= 0 ? own
            : others.IndexOf(neighbour) is var i and >= 0 ? i + after
            : Result.Failure<int>($"Task {neighbour} is not in quadrant {quadrant.Numeral()}'s order");
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
        double? above = index > 0 ? others[index - 1].Rank : null;
        double? below = index < others.Length ? others[index].Rank : null;
        var rank = (above, below) switch
        {
            ({ } a, { } b) => (a + b) / 2,
            ({ } a, null) => a + 1,
            (null, { } b) => b - 1,
            _ => task.Rank,
        };
        var moved = WithFlags(task, quadrant) with { UpdatedAt = now };
        var fits = (above is not { } lo || lo < rank) && (below is not { } hi || rank < hi);
        if (fits) return Replaced(moved with { Rank = rank });

        var ranks = others.Take(index).Append(moved).Concat(others.Skip(index))
            .Select((t, i) => (t.Id, Rank: (double)i))
            .ToDictionary(x => x.Id, x => x.Rank);
        return new TaskBoard(Tasks.Select(t =>
            t.Id == task.Id ? moved with { Rank = ranks[t.Id] }
            : ranks.TryGetValue(t.Id, out var renumbered) && renumbered != t.Rank ? t with { Rank = renumbered, UpdatedAt = now }
            : t).ToArray());
    }

    /// <summary>
    /// Ends the session's live entry on every open task but <paramref name="except"/>, stamping those tasks. Closed
    /// tasks are left alone: SessionEnded still closes the entry there when the session really ends.
    /// </summary>
    TaskBoard WithoutSessionElsewhere(string sessionId, Guid except, DateTimeOffset now) =>
        new(Tasks.Select(t => t.Id != except && t.IsOpen && LiveOn(t, sessionId)
            ? t with
            {
                Sessions = t.Sessions.Select(s => s.Id == sessionId && s.IsLive ? s with { Ended = now } : s).ToArray(),
                UpdatedAt = now,
            }
            : t).ToArray());

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
