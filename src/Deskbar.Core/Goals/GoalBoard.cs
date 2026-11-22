using CSharpFunctionalExtensions;

namespace Deskbar.Core.Goals;

// Every rule for changing goals, as pure functions, the way TaskBoard holds the task rules: a method returns a new
// board and never reads a clock (callers pass "now" and new ids), so the rules are tested without waiting. Each change
// stamps UpdatedAt = now on exactly the goals it touched. Design: docs/superpowers/specs/2026-11-22-goals-planner-design.md
// ("The goal", "Calls made in writing this up") and the plan's "Calls this plan makes" (goals ledger rulings G4, G5).
//
// The rules that shape this file:
//   * A weekly goal may name ONE monthly goal as its parent, and only a goal of a month its week touches. The week of
//     Monday 30 November 2026 runs into December, so a goal of either month may be its parent (GoalPeriod.Touches).
//     Only a weekly goal has a parent; only a monthly goal is one. Enforced here, on SetParent and on Carry.
//   * Carrying moves a goal to another period of its OWN kind and keeps its id (its tasks stay attached). It goes to
//     the end of its new period. A carried weekly goal keeps its parent only while the new week still touches the
//     parent's month; a carried monthly goal leaves its weekly goals where they were, still naming it, because their
//     weeks served it and that history stays (G4). So a weekly goal can name a parent whose month its week no longer
//     touches. That link is history, not a claim on the new month: GoalProgress counts only the weekly goals whose week
//     touches the monthly goal's CURRENT period (ruling G6, review I1), and picking that same parent again is no change
//     rather than a refusal (SetParent compares before it checks).
//   * Delete is for mistakes; Drop is the normal end of a goal that did not happen. Deleting a monthly goal frees its
//     weekly children (their ParentId becomes none). The tasks that named the goal are freed by GoalService through
//     TaskService.DetachGoal: the task's link lives on the task, in tasks.json, not here.
//   * Goals have an order within their period but no way to change it yet: a new or carried goal goes to the end.
//   * The review state rides along: WeekSeen and MonthSeen (the Monday and the 1st Petre last reviewed or dismissed)
//     are kept by every change and written to goals.json with the goals (GoalReview reads them).
//   * A change that changes nothing (the title or the parent it already has, carrying to the period it is in, marking
//     seen what was seen) returns this same board, so GoalService can tell by reference and skip the save.
//   * Refusals take no full stop, like the task commands' ("The goal is open").
public sealed record GoalBoard(IReadOnlyList<Goal> Goals)
{
    public static readonly GoalBoard Empty = new(Array.Empty<Goal>());

    /// <summary>The shortest id prefix <see cref="Find"/> accepts, so a typo cannot hit a goal by luck (as tasks).</summary>
    const int MinPrefix = 4;

    /// <summary>The Monday of the last week Petre reviewed or dismissed the prompt for; none before the first time.</summary>
    public Maybe<DateOnly> WeekSeen { get; init; }

    /// <summary>The 1st of the last month Petre reviewed or dismissed the prompt for; none before the first time.</summary>
    public Maybe<DateOnly> MonthSeen { get; init; }

    // --- making and changing goals ----------------------------------------------------------------------

    /// <summary>A new open goal at the end of <paramref name="period"/>, with no parent.</summary>
    public Result<(GoalBoard Board, Goal Goal)> Add(string title, GoalPeriod period, DateTimeOffset now, Guid newId) =>
        CleanTitle(title).Map(clean =>
        {
            var goal = new Goal(newId, clean, period, GoalStatus.Open, EndOrder(period, newId), Maybe<Guid>.None, now, now);
            return (this with { Goals = Goals.Append(goal).ToArray() }, goal);
        });

    public Result<GoalBoard> Rename(Guid id, string title, DateTimeOffset now) =>
        CleanTitle(title).Bind(clean => Lookup(id).Map(goal =>
            goal.Title == clean ? this : Replaced(goal with { Title = clean, UpdatedAt = now })));

    /// <summary>Petre's call, from Open or Dropped; never automatic (spec, "Tasks contribute").</summary>
    public Result<GoalBoard> MarkDone(Guid id, DateTimeOffset now) => ToStatus(id, GoalStatus.Done, "The goal is already done", now);

    /// <summary>The normal end of a goal that did not happen, from Open or Done.</summary>
    public Result<GoalBoard> Drop(Guid id, DateTimeOffset now) => ToStatus(id, GoalStatus.Dropped, "The goal is already dropped", now);

    /// <summary>A done or dropped goal back to Open, in place. An open goal is refused.</summary>
    public Result<GoalBoard> Reopen(Guid id, DateTimeOffset now) => ToStatus(id, GoalStatus.Open, "The goal is open", now);

    /// <summary>
    /// Moves the goal to <paramref name="to"/>, a period of its own kind, at the end of it, keeping its id and status.
    /// A weekly goal keeps its parent only when the new week touches the parent's month (a parent that no longer exists
    /// is let go too); a monthly goal's weekly children are left as they are. Carrying to the period the goal is already
    /// in returns this same board.
    /// </summary>
    public Result<GoalBoard> Carry(Guid id, GoalPeriod to, DateTimeOffset now) =>
        Lookup(id).Bind(goal =>
            goal.Kind != to.Kind ? Result.Failure<GoalBoard>(goal.Kind == GoalKind.Week ? "A weekly goal moves to another week" : "A monthly goal moves to another month")
            : goal.Period == to ? this
            : Replaced(goal with
            {
                Period = to,
                Order = EndOrder(to, goal.Id),
                ParentId = goal.ParentId.Where(parent => Get(parent).Map(p => to.Touches(p.Period)).GetValueOrDefault(false)),
                UpdatedAt = now,
            }));

    /// <summary>
    /// Sets or clears a weekly goal's monthly parent. Refused: a parent on a monthly goal, a parent that is not a
    /// monthly goal, and a monthly goal of a month the week does not touch. The parent it already has returns this same
    /// board BEFORE any check (G6: after the parent was carried to another month the check would refuse it); so does
    /// clearing the parent of a goal that has none.
    /// </summary>
    public Result<GoalBoard> SetParent(Guid id, Maybe<Guid> parent, DateTimeOffset now) =>
        Lookup(id).Bind(goal =>
            goal.ParentId == parent || parent.HasNoValue ? Result.Success(goal)
            : goal.Kind != GoalKind.Week ? Result.Failure<Goal>("Only a weekly goal has a monthly parent")
            : Lookup(parent.Value).Bind(p =>
                p.Kind != GoalKind.Month ? Result.Failure<Goal>($"'{p.Title}' is not a monthly goal")
                : !goal.Period.Touches(p.Period) ? Result.Failure<Goal>($"'{p.Title}' is a goal of {p.Period.Label}, a month this week does not touch")
                : Result.Success(goal)))
        .Map(goal => goal.ParentId == parent ? this : Replaced(goal with { ParentId = parent, UpdatedAt = now }));

    /// <summary>
    /// Removes the goal, for mistakes (Drop is the normal end). Its weekly children, if it is a monthly goal, lose their
    /// parent and are stamped. The tasks that named it are GoalService's to free (see the header).
    /// </summary>
    public Result<GoalBoard> Delete(Guid id, DateTimeOffset now) =>
        Lookup(id).Map(_ => this with
        {
            Goals = Goals.Where(g => g.Id != id)
                .Select(g => g.ParentId == id ? g with { ParentId = Maybe<Guid>.None, UpdatedAt = now } : g)
                .ToArray(),
        });

    /// <summary>
    /// Petre has reviewed or dismissed the prompt for this week and month: both are recorded (their first days). The
    /// periods already recorded return this same board, so marking seen on every planner open costs no save.
    /// </summary>
    public GoalBoard Seen(GoalPeriod week, GoalPeriod month) =>
        WeekSeen == week.Start && MonthSeen == month.Start
            ? this
            : this with { WeekSeen = week.Start, MonthSeen = month.Start };

    // --- reading ----------------------------------------------------------------------------------------

    /// <summary>The goals of one period in their order: by Order, then the older first, then the id, so it is stable.</summary>
    public IReadOnlyList<Goal> In(GoalPeriod period) =>
        Goals.Where(g => g.Period == period).OrderBy(g => g.Order).ThenBy(g => g.Created).ThenBy(g => g.Id).ToArray();

    /// <summary>The weekly goals naming <paramref name="monthly"/> as their parent, earliest week first, each in its order.</summary>
    public IReadOnlyList<Goal> ChildrenOf(Guid monthly) =>
        Goals.Where(g => g.ParentId == monthly)
            .OrderBy(g => g.Period.Start).ThenBy(g => g.Order).ThenBy(g => g.Created).ThenBy(g => g.Id)
            .ToArray();

    public Maybe<Goal> Get(Guid id) => Goals.TryFirst(g => g.Id == id);

    /// <summary>Looks a goal up by full id or by a unique prefix of at least four characters, as TaskBoard.Find.</summary>
    public Result<Goal> Find(string idOrPrefix) =>
        (idOrPrefix ?? "").Trim() is var text && text.Length >= MinPrefix
            ? Goals.Where(g => g.Id.ToString().StartsWith(text, StringComparison.OrdinalIgnoreCase)).ToArray() switch
            {
                [var only] => only,
                [] => Result.Failure<Goal>(NoGoal(text)),
                _ => Result.Failure<Goal>($"'{text}' matches several goals"),
            }
            : Result.Failure<Goal>(NoGoal(text));

    // --- helpers ----------------------------------------------------------------------------------------

    static Result<string> CleanTitle(string title) =>
        string.IsNullOrWhiteSpace(title) ? Result.Failure<string>("A goal needs a title") : title.Trim();

    static string NoGoal(string id) => $"No goal with id {id}";

    Result<Goal> Lookup(Guid id) => Get(id).ToResult(NoGoal(id.ToString()));

    /// <summary>One past the highest order in <paramref name="period"/> (the goal being placed left out), or 0 in an empty one.</summary>
    int EndOrder(GoalPeriod period, Guid except) =>
        Goals.Where(g => g.Period == period && g.Id != except).Select(g => g.Order + 1).DefaultIfEmpty(0).Max();

    Result<GoalBoard> ToStatus(Guid id, GoalStatus status, string already, DateTimeOffset now) =>
        Lookup(id).Bind(goal => goal.Status == status
            ? Result.Failure<GoalBoard>(already)
            : Replaced(goal with { Status = status, UpdatedAt = now }));

    GoalBoard Replaced(Goal updated) => this with { Goals = Goals.Select(g => g.Id == updated.Id ? updated : g).ToArray() };
}
