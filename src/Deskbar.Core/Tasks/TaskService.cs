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
    string? filesRoot = null, TimeZoneInfo? zone = null)
{
    readonly Func<Guid> makeId = newId ?? Guid.NewGuid;

    // The zone a repeat's wall-clock schedule is read in (recurring tasks spec, task 0b78269e): the machine's own unless a
    // test fixes one, so "daily 09:00" means 09:00 where Petre sits.
    public TimeZoneInfo Zone { get; } = zone ?? TimeZoneInfo.Local;
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
    // It is still heard (statusLinesHeard): a session answering Deskbar's ask with the line it already had ("still waiting
    // on CI") has answered, and StalledTasks must not hand its task to Petre for saying nothing (task 8710ac09).
    public Result SetStatusLine(Guid id, string text) =>
        ApplyIfChanged(Board.SetStatusLine(id, text, now()))
            .Tap(() => ImmutableInterlocked.Update(ref statusLinesHeard, heard => heard.SetItem(id, now())));

    // `by`: who asked, a session's id or TaskComment.DeskbarAuthor; named, the question goes into the thread too (task
    // 968c2216, TaskBoard.Ask). Not announced on Commented: a question is not a comment of Petre's that wakes a session.
    // `options`: the answers offered with the question (AnswerOptions), checked by the board; a bad list refuses the ask.
    public Result Ask(Guid id, string question, Maybe<string> by = default, IReadOnlyList<string>? options = null) =>
        Apply(Board.Ask(id, question, now(), by.Map(asker => (asker, makeId())), options));

    // A pull request the task's session opened (task e7aa49b1); one it already lists saves and pulses nothing.
    public Result AddPullRequest(Guid id, PullRequestLink link) => ApplyIfChanged(Board.AddPullRequest(id, link, now()));

    // A deploy seen running (task 8470d176, phase 2: `task deployed` and "Mark deployed"); reporting it again saves and
    // pulses nothing (TaskBoard.MarkDeployed returns the same board).
    //
    // The report also reaches the sessions waiting on a deploy of the same repository (task 43765c61, DeployNotices):
    // each such task gets a Deskbar line in its thread and is announced on DeployNoticed, which the app's CommentWaker
    // hands to its Claude session on the roads a comment takes. Worked out here, where the report lands, against the
    // board before and after it and GitHub's answers (arrivals), so no poll and no stored state is needed.
    public Result MarkDeployed(Guid id, string note)
    {
        var before = Board;
        return ApplyIfChanged(Board.MarkDeployed(id, note, now()))
            .Tap(() =>
            {
                var at = now();
                DeployNotices.NotToldRecently(DeployNotices.For(before, Board, id, arrivals, SelfChecked), deployTold, at).ToList().ForEach(notice =>
                {
                    deployTold = notice.Repositories.Aggregate(deployTold, (told, repository) => told.SetItem((notice.TaskId, repository), at));
                    Note(notice.TaskId, DeployNotices.ThreadLine(notice));
                    deployNoticed.OnNext(notice);
                });
            });
    }

    // When each task was last told about each repository's deploy (DeployNotices.Quiet). In memory only: a deploy
    // report needs the running Deskbar (it arrives through its pipe or its menu), so a restart loses nothing worth
    // keeping beyond, at worst, one repeated notice.
    ImmutableDictionary<(Guid Task, string Repository), DateTimeOffset> deployTold =
        ImmutableDictionary<(Guid Task, string Repository), DateTimeOffset>.Empty;

    // A pull request of the repository this build comes from: decided by the running build, never by a report.
    bool SelfChecked(PullRequestLink link) => runningBuild.HasValue && runningBuild.Value.Builds(link);

    readonly Subject<DeployNotice> deployNoticed = new();

    /// <summary>A waiting task told about a deploy another task reported (MarkDeployed), after its thread line is saved.</summary>
    public IObservable<DeployNotice> DeployNoticed => deployNoticed;

    /// <summary>
    /// Whether the noticed task still waits on a deploy of one of the notice's repositories (DeployNotices.WaitingOn): the
    /// app's CommentWaker asks just before delivery, so a notice that waited in its queue is dropped once moot.
    /// </summary>
    public bool StillWaitsOnDeploy(DeployNotice notice) =>
        Board.Tasks.FirstOrDefault(t => t.Id == notice.TaskId) is { } task
        && DeployNotices.WaitingOn(task, notice.Repositories, arrivals, SelfChecked).Count > 0;

    /// <summary>The note "Mark deployed" on a line's menu records (TaskService.MarkDeployed).</summary>
    public const string MarkedByHand = "Marked deployed by hand";

    public Result MarkDone(Guid id) => Apply(Board.MarkDone(id, now(), Zone));

    // `stoppingClaude`: past the refusal for a task Claude is still working on (TaskBoard.Close, task 37cd7081), for
    // "Stop Claude and close", which stops the sessions right after. `anyway`: past the refusal for a task that is not
    // finished (task 579f2e4c), for "Close anyway". Whether its work is merged comes from what GitHub said (Merged).
    public Result Close(Guid id, bool stoppingClaude = false, bool anyway = false) =>
        Apply(Board.Close(id, now(), stoppingClaude, anyway, Merged));

    /// <summary>
    /// Whether a plain close of the task is refused because it is not finished (TaskBoard.Unfinished, task 579f2e4c), with
    /// what GitHub said about its pull requests: the bar's and the window's ticks ask this before they close.
    /// </summary>
    //
    // A PARENT WITH AN OPEN SUBTASK IS REFUSED FOR THAT FIRST (TaskBoard.Close checks subtasks before anything else), so
    // it is not "refused because it is not finished": the tick must report "Close its open subtask first", not offer
    // "Close anyway", which the board would refuse in turn. Found on main, where the subtask refusal (task 452487b4) and
    // this rule merged the same morning and a waiting parent's tick opened the menu instead of naming its subtask.
    public bool Unfinished(TaskItem task) => Board.OpenSubtaskCount(task.Id) == 0 && TaskBoard.Unfinished(task, Merged(task));

    /// <summary>
    /// Whether the task's work is merged, for the close refusal (TaskBoard.Close, task 579f2e4c): it recorded at least one
    /// pull request, and every one that was not closed unmerged is merged by GitHub's word, or (none known) was reported
    /// deployed. One still open, or one the watch has not answered yet and nobody reported, is not merged.
    /// </summary>
    //
    // From the same answers the merged ring reads (RunningChanges), but not through DeliveryOf: that leaves a task back
    // from Done unmarked so an old pull request does not draw a disc on new work, and here an old merged pull request on a
    // reopened task still counts. The cost is one close on one tick that might have asked; the refusal is a guard against
    // a slip, not a lock. Unknown (gh missing, offline) is not merged, so on such a machine the tick asks: "Close anyway"
    // is one click further, where a guess the other way would let the slip through.
    public bool Merged(TaskItem task)
    {
        var shipped = RunningChanges.Watched(task)
            .Where(l => !(arrivals.TryGetValue(l.Key, out var fact) && fact.State == PullRequestState.Closed))
            .ToArray();
        return shipped.Length > 0 && shipped.All(l => arrivals.TryGetValue(l.Key, out var fact)
            ? fact.State == PullRequestState.Merged
            : task.Deployed.Any(d => d.Key == l.Key));
    }

    public Result Reopen(Guid id) => Apply(Board.Reopen(id, now(), Zone));

    public Result SetStatus(Guid id, TaskStatus status) => Apply(Board.SetStatus(id, status, now(), Zone));

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

    // Recurring tasks (spec 2026-11-26-recurring-tasks-design.md, task 0b78269e): the board's rules, with this service's
    // clock and zone. A change that leaves the board as it was saves and pulses nothing.
    public Result SetRepeat(Guid id, TaskRepeat repeat) => ApplyIfChanged(Board.SetRepeat(id, repeat, now(), Zone));

    public Result ClearRepeat(Guid id) => ApplyIfChanged(Board.ClearRepeat(id, now()));

    public Result RunStarted(Guid id) => ApplyIfChanged(Board.RunStarted(id, now(), Zone));

    public Result Rescheduled(Guid id) => ApplyIfChanged(Board.Rescheduled(id, now(), Zone));

    /// <summary>
    /// Ends every snooze whose time has come (TaskBoard.WakeDue). The app calls it on a timer; nearly always nothing is
    /// due, and then it saves and pulses nothing.
    /// </summary>
    public void WakeDue() => CommitIfChanged(Board.WakeDue(now()));

    /// <summary>
    /// What the recurring-task sweep should do now (RecurringRuns.Plan; task 0b78269e). Read only: the caller supplies the
    /// two machine questions (is a session of the task listed, does the folder exist) and applies what comes back, so this
    /// saves and pulses nothing.
    /// </summary>
    public IReadOnlyList<RunDecision> DueRuns(Func<TaskItem, bool> running, Func<string, bool> folderExists) =>
        RecurringRuns.Plan(Board, now(), running, folderExists);

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
    // The pull requests whose last look failed (task 8db4c78e): their tasks keep the full disc, where one still to be
    // asked draws the Checking ring (RunningChanges' header).
    ImmutableHashSet<string> unreachable = ImmutableHashSet<string>.Empty;
    // Whether the watch's last look failed for everything it asked: a machine that cannot ask GitHub (RunningChanges.Of).
    bool blind;

    /// <summary>Where a task's change stands against the running build (RunningChanges.Of); None until the watch has spoken.</summary>
    public TaskDelivery DeliveryOf(TaskItem task) =>
        runningBuild.Map(build => RunningChanges.Of(task, build, arrivals, unreachable, blind)).GetValueOrDefault(TaskDelivery.None);

    /// <summary>
    /// Whether "Mark deployed" belongs on the task's menu (task 8470d176, phase 2): its change is merged but not running
    /// (DeliveryStage.Merged), and at least one of its recorded pull requests is outside this build's repository and not
    /// reported yet, the only kind a report can fill. A Deskbar change waits for the running build, which no report
    /// overrides (RunningChanges' header), so the item would do nothing on one. False until the watch has spoken.
    /// </summary>
    public bool CanMarkDeployed(TaskItem task) =>
        runningBuild.HasValue
        && DeliveryOf(task).Stage == DeliveryStage.Merged
        && task.PullRequests.Any(p => !runningBuild.Value.Builds(p) && task.Deployed.All(d => d.Key != p.Key)
            // Merged by GitHub's word: an unreported one closed without a merge would be stamped and change nothing.
            && arrivals.TryGetValue(p.Key, out var arrival) && arrival.State == PullRequestState.Merged);

    /// <summary>
    /// What GitHub said about the recorded pull requests against <paramref name="build"/>, keyed by
    /// PullRequestLink.Key: the whole set known so far, replacing the last, with the keys whose last look failed
    /// (<paramref name="failed"/>, none when not given), and whether the last look failed for everything it asked
    /// (<paramref name="cannotAsk"/>). Pulses Changed only when something differs, so an unchanged look redraws nothing.
    /// </summary>
    public void Arrived(RunningBuild build, IReadOnlyDictionary<string, PullRequestArrival> facts, IEnumerable<string>? failed = null,
        bool cannotAsk = false)
    {
        var failing = (failed ?? []).ToImmutableHashSet();
        var same = runningBuild.HasValue && runningBuild.Value == build
            && facts.Count == arrivals.Count
            && facts.All(f => arrivals.TryGetValue(f.Key, out var known) && known == f.Value)
            && failing.SetEquals(unreachable)
            && cannotAsk == blind;
        if (same) return;
        runningBuild = build;
        arrivals = facts.ToImmutableDictionary();
        unreachable = failing;
        blind = cannotAsk;
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
    public Result UndoClose(Guid id, TaskStatus was, Maybe<string> question) => Apply(Board.UndoClose(id, was, question, now(), Zone));

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

    // REEVALUATE (task 3dd07539; Petre asked for "a button on a task" that makes its Claude session look at the task again,
    // after a session kept its task waiting on him for a pull request already merged). The press is announced on
    // Reevaluations, and the app's CommentWaker hands its session SessionBriefing.Reevaluate on the roads a comment
    // takes, so an idle or ended session is reached too. A Deskbar line in the thread says it was asked, so the press has
    // a visible effect even before the session answers. Refused for a task no session has worked on (nobody to ask) and
    // for a closed one (history; Reopen comes first). Every caller, the button at the top of an open task, the bar's menu and
    // `task reevaluate <id>`, comes through here.
    public Result Reevaluate(Guid id) =>
        Board.Tasks.TryFirst(t => t.Id == id).ToResult("no such task")
            .Ensure(t => t.IsClaudes, "no Claude session has worked on this task, so there is no one to reevaluate it")
            .Ensure(t => t.Status != TaskStatus.Closed, "the task is closed; reopen it first")
            .Bind(t => Note(t.Id, ReevaluateNote).Tap(() => reevaluations.OnNext(t.Id)));

    /// <summary>The thread's line for a press of Reevaluate (Reevaluate).</summary>
    public const string ReevaluateNote = "Asked Claude to reevaluate the task.";

    readonly Subject<Guid> reevaluations = new();

    /// <summary>The tasks whose session is to reevaluate them (Reevaluate), each after its thread line is saved.</summary>
    public IObservable<Guid> Reevaluations => reevaluations;

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

    // --- the session's model, effort and permission mode (TaskBoard's operations, SettingsSwitch) ---
    // Each saves only when it changed something. The app's ClaudeSessions runs the switch and calls these.

    public Result Want(Guid id, SessionSettings wanted) => ApplyIfChanged(Board.Want(id, wanted, now()));

    public Result Unwant(Guid id) => ApplyIfChanged(Board.Unwant(id, now()));

    public Result SwitchStarted(Guid id, PendingSwitch pending) => ApplyIfChanged(Board.SwitchStarted(id, pending, now()));

    public Result SwitchIssued(Guid id, SessionSettings to, string shortId, DateTimeOffset issuedAt) =>
        ApplyIfChanged(Board.SwitchIssued(id, to, shortId, issuedAt, now()));

    public Result SessionSwitched(Guid id, string fromSessionId, TaskSession copy) => ApplyIfChanged(Board.SessionSwitched(id, fromSessionId, copy, now()));

    public Result SwitchConfirmed(Guid id) => ApplyIfChanged(Board.SwitchConfirmed(id, now()));

    public Result SwitchReverted(Guid id) => ApplyIfChanged(Board.SwitchReverted(id, now()));

    public Result SessionRelaunched(Guid id, string sessionId, SessionSettings launched) =>
        ApplyIfChanged(Board.SessionRelaunched(id, sessionId, launched, now()));

    public Result SwitchAbandoned(Guid id, bool keepWanted = false) => ApplyIfChanged(Board.SwitchAbandoned(id, now(), keepWanted));

    /// <summary>
    /// A session that started with source "fork" (a copy, measured 2026-11-26) and is on no task: when it is the copy a
    /// switch issued in its folder is waiting for (TaskBoard.SwitchingTaskFor), it joins that task in the original's
    /// place (TaskBoard.SessionSwitched), launched with what the switch asked for. Returns the task it joined, or none.
    /// </summary>
    public Maybe<TaskItem> JoinAfterSwitch(string sessionId, string folder, Func<string, bool> inFolder)
    {
        if (Board.SwitchingTaskFor(sessionId, inFolder, now()) is not { HasValue: true } task
            || task.Value.Switching is not { HasValue: true } pending
            || SessionSwitched(task.Value.Id, pending.Value.FromSession, NewSession(sessionId, folder) with { LaunchedWith = pending.Value.To }).IsFailure)
            return Maybe<TaskItem>.None;
        Note(task.Value.Id, SettingsSwitch.JoinedNote(pending.Value.To));
        return Board.Tasks.TryFirst(t => t.Id == task.Value.Id);
    }

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

    // When each session last had ANY prompt submitted, Deskbar's and Claude Code's own included (task 10b49554): a prompt
    // starts a turn, so the tasks panel shows the session working at once instead of waiting out the resume grace
    // (ClaudeActivity). In memory only, kept per session until Deskbar restarts, like `prompted` above.
    ImmutableDictionary<string, DateTimeOffset> turnsStarted = ImmutableDictionary<string, DateTimeOffset>.Empty;

    // The sessions whose current turn began with a Stop nudge their waiter delivered (task 8710ac09; RemoteControl.Nudge).
    // In memory only: a restart forgets it, and the worst that costs is one more nudge on one turn.
    ImmutableHashSet<string> turnsFromNudge = ImmutableHashSet<string>.Empty;

    // When each session was last handed a Stop nudge through its inbox (NudgeSent), for Stalled's grace.
    ImmutableDictionary<string, DateTimeOffset> nudgesSent = ImmutableDictionary<string, DateTimeOffset>.Empty;

    /// <summary>How long a session handed a Stop nudge counts as busy while its waiter wakes it (measured: under 2 s).</summary>
    public static readonly TimeSpan NudgeGrace = TimeSpan.FromSeconds(30);

    /// <summary>A Stop nudge went into the session's inbox now (RemoteControl.Nudge).</summary>
    public void NudgeSent(string sessionId)
    {
        var at = now();
        ImmutableInterlocked.Update(ref nudgesSent, map => map.SetItem(sessionId, at));
    }

    /// <summary>Records whether the turn that just started in this session began with a Stop nudge.</summary>
    public void NudgedTurn(string sessionId, bool byNudge) =>
        ImmutableInterlocked.Update(ref turnsFromNudge, set => byNudge ? set.Add(sessionId) : set.Remove(sessionId));

    /// <summary>Whether the session's current turn began with a Stop nudge, so its Stop must not nudge again.</summary>
    public bool TurnFromNudge(string sessionId) => turnsFromNudge.Contains(sessionId);

    /// <summary>A prompt of any kind was submitted in this session (RemoteControl.Prompted): a turn started.</summary>
    public void TurnStarted(string sessionId)
    {
        var at = now();
        ImmutableInterlocked.Update(ref turnsStarted, map => map.SetItem(sessionId, at));
    }

    /// <summary>When each session last had a prompt of any kind (TurnStarted), for ClaudeActivity.</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> TurnsStarted => turnsStarted;

    // The quiet clock of StalledTasks: when each watched Claude task last had a session working on it. In memory only;
    // after a restart StalledTasks measures from the task's UpdatedAt instead (see its header).
    ImmutableDictionary<Guid, DateTimeOffset> lastBusy = ImmutableDictionary<Guid, DateTimeOffset>.Empty;

    // ASK BEFORE HANDING BACK (task 8710ac09, StalledTasks' header): when each quiet task's session was asked whether it
    // waits on Petre, and when a session last set each task's status line (SetStatusLine), changed or not. In memory only:
    // a restart forgets an ask, and the session is simply asked again after its next 15 quiet minutes.
    ImmutableDictionary<Guid, DateTimeOffset> nudged = ImmutableDictionary<Guid, DateTimeOffset>.Empty;
    ImmutableDictionary<Guid, DateTimeOffset> statusLinesHeard = ImmutableDictionary<Guid, DateTimeOffset>.Empty;
    readonly Subject<Guid> stalledNudges = new();

    /// <summary>
    /// The quiet tasks whose session is to be asked now whether it waits on Petre (StalledTasks), for the app's
    /// CommentWaker to deliver on a comment's roads with SessionBriefing.StalledNudge.
    /// </summary>
    public IObservable<Guid> StalledNudges => stalledNudges;

    // DESKBAR WATCHES THE PULL REQUESTS (Deskbar task c8e6967d, spec docs/superpowers/specs/2026-11-27-repo-watcher-design.md,
    // rules in RepoWatch). The watch's memory lives here, IN MEMORY ONLY and never saved: a restart starts from
    // RepoWatchState.Empty, so base heads are recorded again without a nudge, a merge in the middle of being followed is
    // left to its session, and with it the StalledTasks exception lapses (the stalled ask then reaches the session within
    // 15 minutes). RepoWatchPoll feeds Watched and Followed with the pure rules' answers; CommentWaker delivers WatchNudges.
    RepoWatchState watch = RepoWatchState.Empty;
    readonly Subject<WatchNudge> watchNudges = new();
    IReadOnlyList<RedRun> redRuns = [];

    /// <summary>The watch's memory: what it saw, which merges it follows and when each repository last answered.</summary>
    public RepoWatchState Watch => watch;

    /// <summary>The messages the watch sends to sessions. They leave no thread line, delivered or not.</summary>
    public IObservable<WatchNudge> WatchNudges => watchNudges;

    /// <summary>The failed runs on followed merge commits that were not yet a day old, the newest 50 (ManagerWakes reads them).</summary>
    public IReadOnlyList<RedRun> RedRuns => redRuns;

    /// <summary>Takes one repository's poll: stores the new memory, then announces each nudge (after the state is stored).</summary>
    public void Watched(RepoWatchStep step)
    {
        watch = step.State;
        step.Nudges.ToList().ForEach(watchNudges.OnNext);
    }

    /// <summary>Takes one look at a followed merge: stores the memory, keeps any red run (50 at most, a day old at most) and announces the nudges.</summary>
    public void Followed(FollowStep step)
    {
        watch = step.State;
        var since = now() - TimeSpan.FromDays(1);
        redRuns = [.. redRuns.Concat(step.Red).Where(r => r.At > since).TakeLast(50)];
        step.Nudges.ToList().ForEach(watchNudges.OnNext);
    }

    /// <summary>Whether Deskbar watches this task's pull request now (RepoWatch.Watched): the prompt line says so to the session.</summary>
    public bool IsWatched(TaskItem task) => RepoWatch.Watched(watch, task, now());

    // A SET-ASIDE QUESTION IS WRITTEN AFRESH, NOT PUT BACK AS IT WAS (task 0cac3082). Petre, 2026-11-26: "claude should
    // regenerate the message if it needs my attention when the task gets to waiting on me", and "previous messages are
    // not good enough". Seen on task f7ac687c: its question "merge 629 and deskbar-site 16" was set aside while the session
    // worked on his comment, the session stopped without asking again, and the quiet poll put the old text back twenty
    // minutes after deskbar-site 16 had merged, so the bar asked him to merge a pull request that was already in.
    //
    // So when RepliedTasks.Restore finds a set-aside question whose sessions are all quiet, Deskbar first asks the session
    // to look at the facts and ask again with what the user must do NOW (Regenerations, delivered by the app's
    // CommentWaker with SessionBriefing.Regenerate), and leaves the task In progress meanwhile. The old text comes back
    // only as a fallback: at once when no session can be reached (RegenerateUndelivered), or once RegenerateGrace has
    // passed with every session quiet again and still no `task ask` or `task answered`. A question is never lost, it is
    // only no longer put back without a look. In memory only: after a restart the session is simply asked once more.
    ImmutableDictionary<Guid, DateTimeOffset> regenerating = ImmutableDictionary<Guid, DateTimeOffset>.Empty;
    readonly Subject<Guid> regenerations = new();

    /// <summary>
    /// How long a session asked to write its set-aside question afresh has before the old text comes back anyway. Long
    /// enough for a resume to start and the session to show busy (a few seconds, measured for comments), short enough that
    /// a question nobody rewrites is not hidden for long.
    /// </summary>
    public static readonly TimeSpan RegenerateGrace = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The tasks whose session is to write its set-aside question afresh now (see the comment on `regenerating`), for the
    /// app's CommentWaker to deliver on a comment's roads with SessionBriefing.Regenerate. Announced after the board is saved.
    /// </summary>
    public IObservable<Guid> Regenerations => regenerations;

    /// <summary>
    /// The request to write the question afresh could not reach any session of the task (CommentWake found none): nobody
    /// can rewrite it, so the question comes back to Petre now, as it stood, instead of after RegenerateGrace.
    /// </summary>
    /// <summary>
    /// Whether the task's session is still to write its set-aside question afresh: asked (Regenerations) and the question
    /// still set aside. A request that waited in the waker's queue past RegenerateGrace, or behind the session's own
    /// `task ask`, is no longer due, and the waker drops it rather than tell the session something no longer true.
    /// </summary>
    public bool IsRegenerating(Guid id) =>
        regenerating.ContainsKey(id)
        && Board.Tasks.Any(t => t.Id == id && t is { IsOpen: true, Status: TaskStatus.InProgress, QuestionSetAside.HasValue: true });

    public void RegenerateUndelivered(Guid id)
    {
        if (!regenerating.ContainsKey(id)) return;
        regenerating = regenerating.Remove(id);
        var next = Board.RestoreQuestion(id, now());
        if (ReferenceEquals(next, Board)) return;
        trace?.Invoke($"tasks: restored the set-aside question of {Line(next, id)}, no session could be asked to write it afresh");
        Commit(next);
    }

    /// <summary>
    /// The ask could not reach any session of the task (CommentWake found none to wake): nobody can answer, so the task
    /// comes to Petre now instead of after StalledTasks.Answer. Only a task still Claude's and still asked is moved.
    /// </summary>
    public void NudgeUndelivered(Guid id)
    {
        if (!nudged.ContainsKey(id)) return;
        nudged = nudged.Remove(id);
        var next = Board.HandBack(id, now());
        if (ReferenceEquals(next, Board)) return;
        trace?.Invoke($"tasks: handed back {Line(next, id)}, the ask could not reach any of its sessions");
        Commit(next);
    }

    /// <summary>
    /// Stalled tasks (StalledTasks; spec docs/superpowers/specs/2026-11-23-stalled-claude-tasks-design.md): called on
    /// every poll of Claude Code's session list with the ids of the sessions working now. A Claude task quiet for
    /// StalledTasks.After goes to Petre, a handed-back task whose session works again goes back to Claude. Saves and
    /// pulses only when a task changed hands, and traces each move.
    /// </summary>
    public void Stalled(IReadOnlySet<string> polledBusy, IReadOnlySet<string> working)
    {
        // A session just handed a Stop nudge through its inbox (task 8710ac09, RemoteControl.Nudge) is idle for a second or
        // two until its waiter wakes it. Counted busy for NudgeGrace, so a poll in that gap does not take it for a session
        // that went quiet without settling its question and set off a second ask on top of the nudge (review of the branch).
        var since = now() - NudgeGrace;
        var busy = polledBusy.Union(nudgesSent.Where(pair => pair.Value > since).Select(pair => pair.Key)).ToHashSet();
        // First the questions Deskbar set aside whose sessions are quiet again without having asked or settled them
        // (RepliedTasks.Restore, task 8bd18246). The session is asked once to write the question afresh (task 0cac3082, see
        // `regenerating`); only when that ask has had RegenerateGrace and the sessions are quiet again does the question
        // wait on Petre as it stood. A task that is no longer set aside (asked again, answered, done) is forgotten here.
        var quiet = RepliedTasks.Restore(Board, busy);
        regenerating = regenerating.RemoveRange(regenerating.Keys.Where(id =>
            !Board.Tasks.Any(t => t.Id == id && t is { IsOpen: true, Status: TaskStatus.InProgress, QuestionSetAside.HasValue: true })).ToArray());
        var toRegenerate = quiet.Where(id => !regenerating.ContainsKey(id)).ToArray();
        regenerating = regenerating.SetItems(toRegenerate.Select(id => KeyValuePair.Create(id, now())));
        toRegenerate.ToList().ForEach(id => trace?.Invoke($"tasks: asked the session of {Line(Board, id)} to write its set-aside question afresh"));
        quiet.Where(id => !toRegenerate.Contains(id) && now() - regenerating[id] >= RegenerateGrace).ToList().ForEach(id =>
        {
            regenerating = regenerating.Remove(id);
            var next = Board.RestoreQuestion(id, now());
            if (ReferenceEquals(next, Board)) return;
            trace?.Invoke($"tasks: restored the set-aside question of {Line(next, id)}, its session was asked to write it afresh and did not");
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
        // A task whose merge Deskbar follows is quiet on purpose and is neither asked nor handed back (Deskbar task c8e6967d).
        var step = StalledTasks.Step(Board, busy, working, lastBusy, now(), nudged, statusLinesHeard,
            Watch.Following.Select(m => m.TaskId).ToHashSet());
        lastBusy = step.LastBusy;
        // A task whose session is asked to write its set-aside question afresh is not ALSO asked whether it is stalled
        // (review of task 0cac3082): after a Deskbar restart the quiet clock starts from UpdatedAt, so a task set aside long
        // ago would get both requests in one poll. The regenerate request has its own fallback (RegenerateGrace).
        var asking = regenerating.Keys.ToHashSet();
        nudged = step.Nudged.RemoveRange(asking);
        step = step with { Nudge = step.Nudge.Where(id => !asking.Contains(id)).ToArray() };
        // Only the tasks still watched keep their heard time; the rest would only grow the map.
        statusLinesHeard = statusLinesHeard.RemoveRange(statusLinesHeard.Keys.Where(id => !step.Board.Tasks.Any(t => t.Id == id && t.IsOpen)).ToArray());
        step.Nudge.Select(id => $"tasks: asked the session of {Line(step.Board, id)} whether it waits on you, no session worked on it for {StalledTasks.After.TotalMinutes:0} minutes")
            .Concat(step.HandedBack.Select(id => $"tasks: handed back {Line(step.Board, id)}, its session did not answer the ask in {StalledTasks.Answer.TotalMinutes:0} minutes"))
            .Concat(step.TakenBack.Select(id => $"tasks: taken back {Line(step.Board, id)}, its session works on it again"))
            .ToList().ForEach(line => trace?.Invoke(line));
        CommitIfChanged(step.Board);
        // Announced after the board is saved, so the waker reads the task as it stands.
        step.Nudge.ToList().ForEach(stalledNudges.OnNext);
        toRegenerate.ToList().ForEach(regenerations.OnNext);
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
