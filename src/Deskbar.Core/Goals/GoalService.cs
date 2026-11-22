using System.Reactive;
using System.Reactive.Subjects;
using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.Core.Goals;

// Owns the live GoalBoard and saves every change through IGoalStore, as TaskService owns the tasks. It is where the
// clock, the local date and new ids enter: GoalBoard stays pure. Design:
// docs/superpowers/specs/2026-11-22-goals-planner-design.md ("What Petre decided": the goal, tasks contribute, the
// weekly and monthly review; "Calls made in writing this up") and the plan's "Calls this plan makes".
//
// TWO CLOCKS. `now` is the UTC instant stamped on Created and UpdatedAt. `today` is Petre's LOCAL date, and it alone
// decides "this week" and "this month": at 00:30 UTC on a Monday it can still be Sunday where he is, and the goal he
// adds then belongs to the week that is ending (review focus 2). Nothing in Core reads DateTime.Now.
//
// THE ONE PLACE A TASK IS LINKED TO A GOAL. LinkTask checks the goal exists and is open, then calls
// TaskService.SetGoal, which itself checks nothing about goals, so the tasks code never depends on this code. The link
// lives on the task (tasks.json), so a link is a TASK change: TaskService saves and pulses it, and this service's
// Changed does not pulse (views that show both subscribe to both). Unlinking always works, whatever the goal's state.
//
// NOTHING PRUNES LINKS. A task naming a goal that no longer exists reads as no goal everywhere (GoalOf, TaskGoalFilter)
// and can be linked again. Pruning on load would wipe every link the day goals.json fails to load (review focus 3), so
// a failed Load leaves the task board alone; only Delete frees a goal's tasks, explicitly (TaskService.DetachGoal).
//
// A change that changes nothing (GoalBoard returns the same board) saves and pulses nothing, so Seen() on every
// planner open and a carry to where the goal already is cost no write. A failed save is traced and never thrown: the
// in-memory board is the truth for this run, as in TaskService.
//
// Everything runs on the UI thread (the bar, the windows and the control pipe all dispatch there), so no locking.
//
// LATER (not built yet, see the plan's last section): when the Claude-session work lands, SessionBriefing.Start lists
// Board.In(ThisWeek)'s open goals by title and id, and the skill tells a session it may attach its own task with
// `task goal <id> <goal-id>` (LinkTask through the pipe); a session never adds, drops or marks a goal done.
public sealed class GoalService(
    IGoalStore store,
    TaskService tasks,
    Func<DateTimeOffset> now,
    Func<DateOnly> today,
    Func<Guid>? newId = null,
    Action<string>? trace = null)
{
    readonly Func<Guid> makeId = newId ?? Guid.NewGuid;
    readonly Subject<Unit> changed = new();

    public GoalBoard Board { get; private set; } = GoalBoard.Empty;

    /// <summary>Pulses after every change to the goal board, loaded or edited. Task links pulse TaskService.Changed.</summary>
    public IObservable<Unit> Changed => changed;

    /// <summary>
    /// Why the last <see cref="Load"/> failed, in the store's words, or none. A failed load starts the board empty, and
    /// an empty planner must not read as "no goals" when the truth is "could not read your goals".
    /// </summary>
    public Maybe<string> LoadProblem { get; private set; } = Maybe<string>.None;

    /// <summary>
    /// Today's local date, read once. A caller that needs this week AND this month derives both from one read
    /// (GoalPeriod.WeekOf, MonthOf), so the two cannot straddle midnight between two reads of the clock.
    /// </summary>
    public DateOnly Today => today();

    /// <summary>The local week holding today, Monday to Sunday.</summary>
    public GoalPeriod ThisWeek => GoalPeriod.WeekOf(today());

    /// <summary>The local calendar month holding today.</summary>
    public GoalPeriod ThisMonth => GoalPeriod.MonthOf(today());

    /// <summary>
    /// Replaces the board with what the store holds. A store failure leaves the board empty and touches no task: the
    /// links stay on the tasks, reading as no goal until goals.json reads again (see the header).
    /// </summary>
    public Result Load()
    {
        var loaded = store.Load();
        Board = loaded.IsSuccess ? loaded.Value : GoalBoard.Empty;
        LoadProblem = loaded.IsSuccess ? Maybe<string>.None : loaded.Error;
        if (loaded.IsFailure) trace?.Invoke($"goals: load failed: {loaded.Error}");
        changed.OnNext(Unit.Default);
        return loaded.IsSuccess ? Result.Success() : Result.Failure(loaded.Error);
    }

    // One wrapper per GoalBoard change, with the clock and a new id supplied here. A refused change saves nothing.

    public Result<Goal> Add(string title, GoalPeriod period) =>
        Board.Add(title, period, now(), makeId()).Tap(r => Commit(r.Board)).Map(r => r.Goal);

    public Result Rename(Guid id, string title) => Apply(Board.Rename(id, title, now()));

    public Result MarkDone(Guid id) => Apply(Board.MarkDone(id, now()));

    public Result Drop(Guid id) => Apply(Board.Drop(id, now()));

    public Result Reopen(Guid id) => Apply(Board.Reopen(id, now()));

    /// <summary>To <paramref name="to"/>, a period of the goal's own kind (GoalBoard.Carry has the parent rule).</summary>
    public Result Carry(Guid id, GoalPeriod to) => Apply(Board.Carry(id, to, now()));

    /// <summary>The review's "Carry over": to the current period of the goal's own kind, a week to this week, a month to this month.</summary>
    public Result CarryToNow(Guid id) =>
        Board.Get(id).ToResult($"No goal with id {id}").Bind(goal => Carry(id, GoalPeriod.Of(goal.Kind, today())));

    public Result SetParent(Guid id, Maybe<Guid> parent) => Apply(Board.SetParent(id, parent, now()));

    /// <summary>
    /// Removes the goal (for mistakes; Drop is the normal end), frees its weekly children (GoalBoard.Delete) and the
    /// tasks that named it (TaskService.DetachGoal, saved to tasks.json only when some task did).
    /// </summary>
    public Result Delete(Guid id) => Apply(Board.Delete(id, now())).Tap(() => tasks.DetachGoal(id));

    /// <summary>
    /// Links a task to a goal, or (none) unlinks it. The goal must exist and be open: "The goal is done", "The goal is
    /// dropped". Unlinking always works. Attaching never changes the task's flags, rank or status (TaskBoard.SetGoal).
    /// </summary>
    public Result LinkTask(Guid taskId, Maybe<Guid> goalId) =>
        (goalId.HasNoValue
            ? Result.Success()
            : Board.Get(goalId.Value).ToResult($"No goal with id {goalId.Value}").Bind(goal => goal.Status switch
            {
                GoalStatus.Done => Result.Failure("The goal is done"),
                GoalStatus.Dropped => Result.Failure("The goal is dropped"),
                _ => Result.Success(),
            }))
        .Bind(() => tasks.SetGoal(taskId, goalId));

    // --- reading ------------------------------------------------------------------------------------------

    /// <summary>The goal a task counts toward; none for no link and for a link to a goal that no longer exists.</summary>
    public Maybe<Goal> GoalOf(TaskItem task) => task.GoalId.Bind(Board.Get);

    public GoalProgress ProgressOf(Goal goal) => GoalProgress.Of(goal, Board, tasks.Board);

    /// <summary>
    /// Every task linked to the goal itself (not its weekly goals'), closed ones included, in the task board's order:
    /// the tasks GoalProgress counts, for the pipe's `taskIds`.
    /// </summary>
    public IReadOnlyList<TaskItem> TasksOf(Guid goalId) => tasks.Board.Tasks.Where(t => t.GoalId == goalId).ToArray();

    /// <summary>Which prompt the bar shows now (GoalReview.Due on today's local date).</summary>
    public ReviewDue PromptDue() => GoalReview.Due(Board, today());

    /// <summary>The review strip's goals: every open goal of an earlier period (GoalReview.Pending).</summary>
    public IReadOnlyList<Goal> ToReview() => GoalReview.Pending(Board, today());

    /// <summary>
    /// Petre opened the planner or dismissed the bar's line: this week and this month are recorded as seen. Saves only
    /// when that changed the board. Today is read ONCE (review M2), so the week and the month recorded are those of one
    /// day even when midnight falls between two reads of the clock.
    /// </summary>
    public void Seen() => CommitIfChanged(SeenOn(today()));

    GoalBoard SeenOn(DateOnly day) => Board.Seen(GoalPeriod.WeekOf(day), GoalPeriod.MonthOf(day));

    // --- helpers ------------------------------------------------------------------------------------------

    /// <summary>Takes a successful change and commits it when it changed the board; passes a failure through untouched.</summary>
    Result Apply(Result<GoalBoard> change) => change.Tap(CommitIfChanged);

    void CommitIfChanged(GoalBoard board)
    {
        if (!ReferenceEquals(board, Board)) Commit(board);
    }

    /// <summary>
    /// The in-memory board is the truth for this run, so it changes even when the save fails: a full disk must not make
    /// the planner lie about what Petre just did. The failure is traced, never thrown.
    /// </summary>
    void Commit(GoalBoard board)
    {
        Board = board;
        var saved = store.Save(board);
        if (saved.IsFailure) trace?.Invoke($"goals: save failed: {saved.Error}");
        changed.OnNext(Unit.Default);
    }
}
