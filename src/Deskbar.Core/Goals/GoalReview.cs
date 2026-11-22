using CSharpFunctionalExtensions;

namespace Deskbar.Core.Goals;

// When the bar's "Plan this week" / "Plan this month" line is due, and which goals the planner's review strip lists.
// Design: docs/superpowers/specs/2026-11-22-goals-planner-design.md ("The weekly and monthly review", "Calls made in
// writing this up") and the goals ledger's rulings G3 and G4.
//
// DUE IS JUDGED BY WHAT WAS LAST SEEN, NOT BY THE DAY OF THE WEEK. The prompt shows on the first day the app runs in a
// new week or month: a machine off all of Monday still gets it on Tuesday. GoalBoard keeps the Monday and the 1st Petre
// last reviewed or dismissed (WeekSeen, MonthSeen); a period is due when the current one starts after the one seen.
// When both are due, one line, "Plan this month" (spec). Opening the planner or the line's close sign records the
// current week and month as seen (GoalService.Seen), so the line does not come back for that week.
//
// THE FIRST RUN (ruling G3). With no review record at all, only the week is due: the month the app first runs in
// counts as seen, so the first prompt after the update reads "Plan this week" rather than both at once. In general a
// missing MonthSeen is judged by the month of WeekSeen (a hand-edited file with only a week), and by the current month
// when nothing was seen; the first Seen writes both down.
//
// "The previous period's goals" (spec) is read as EVERY open goal of an earlier period, not only the last one (G4), so
// a skipped week is not lost. The strip shows them whether or not the prompt was due.

/// <summary>Which prompt the bar shows: none, "Plan this week", or "Plan this month" (which covers both).</summary>
public enum ReviewDue { None, Week, Month }

public static class GoalReview
{
    /// <summary>
    /// Month when a month after the one seen has started, else Week when a week after the one seen has started (or no
    /// week was ever seen), else None. <paramref name="today"/> is the local date.
    /// </summary>
    public static ReviewDue Due(GoalBoard board, DateOnly today)
    {
        var thisMonth = GoalPeriod.MonthOf(today).Start;
        var monthSeen = board.MonthSeen
            .Or(board.WeekSeen.Map(week => GoalPeriod.MonthOf(week).Start))
            .GetValueOrDefault(thisMonth);
        return monthSeen < thisMonth ? ReviewDue.Month
            : board.WeekSeen.Map(week => week < GoalPeriod.WeekOf(today).Start).GetValueOrDefault(true) ? ReviewDue.Week
            : ReviewDue.None;
    }

    /// <summary>
    /// The open goals whose period ended before the current period of their own kind: monthly goals first, then weekly,
    /// each oldest period first, then in their order. Done, dropped and current goals are left out.
    /// </summary>
    public static IReadOnlyList<Goal> Pending(GoalBoard board, DateOnly today) =>
        board.Goals
            .Where(g => g.IsOpen && g.Period.Start < GoalPeriod.Of(g.Kind, today).Start)
            .OrderBy(g => g.Kind == GoalKind.Month ? 0 : 1)
            .ThenBy(g => g.Period.Start).ThenBy(g => g.Order).ThenBy(g => g.Created).ThenBy(g => g.Id)
            .ToArray();

    /// <summary>The bar's line: "Plan this week", "Plan this month", or "" when nothing is due.</summary>
    public static string PromptText(ReviewDue due) => due switch
    {
        ReviewDue.Week => "Plan this week",
        ReviewDue.Month => "Plan this month",
        _ => "",
    };
}
