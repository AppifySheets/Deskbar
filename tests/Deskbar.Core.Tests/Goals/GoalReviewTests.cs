using CSharpFunctionalExtensions;
using Deskbar.Core.Goals;

namespace Deskbar.Core.Tests.Goals;

// When the bar's "Plan this week" / "Plan this month" line is due, and which goals the planner's review strip lists.
// Design: docs/superpowers/specs/2026-11-22-goals-planner-design.md ("The weekly and monthly review", "Calls made":
// due on the first day the app runs in the new period) and the goals ledger's ruling G3 (a first run with nothing seen
// is a week due, not a month: the current month counts as seen).
public sealed class GoalReviewTests
{
    static readonly DateTimeOffset T0 = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);
    static DateOnly D(int year, int month, int day) => new(year, month, day);

    static GoalBoard SeenOn(DateOnly day) => GoalBoard.Empty.Seen(GoalPeriod.WeekOf(day), GoalPeriod.MonthOf(day));

    // Ruling G3: no review record at all is the first run after the update. Only the week is due; the month the app
    // first runs in counts as seen, so the first prompt is "Plan this week", not "Plan this month".
    [Fact]
    public void Nothing_seen_is_a_week_due()
    {
        Assert.Equal(ReviewDue.Week, GoalReview.Due(GoalBoard.Empty, D(2026, 11, 24)));
        Assert.Equal(ReviewDue.Week, GoalReview.Due(GoalBoard.Empty, D(2026, 12, 1)));
    }

    [Fact]
    public void Seen_this_week_and_month_is_nothing_due()
    {
        Assert.Equal(ReviewDue.None, GoalReview.Due(SeenOn(D(2026, 11, 23)), D(2026, 11, 29)));
    }

    [Fact]
    public void A_new_week_inside_a_seen_month_is_a_week_due()
    {
        Assert.Equal(ReviewDue.Week, GoalReview.Due(SeenOn(D(2026, 11, 16)), D(2026, 11, 23)));
    }

    [Fact]
    public void A_new_month_is_a_month_due_even_when_the_week_was_seen()
    {
        // Seen on Monday 30 November: that week runs into December, and its week is still the current one on the 1st.
        Assert.Equal(ReviewDue.Month, GoalReview.Due(SeenOn(D(2026, 11, 30)), D(2026, 12, 1)));
        Assert.Equal(ReviewDue.Month, GoalReview.Due(SeenOn(D(2026, 11, 16)), D(2027, 1, 4)));
    }

    // A week seen but no month recorded (a hand edit): the month counts as the seen week's, so a later month is due.
    [Fact]
    public void A_week_seen_without_a_month_judges_the_month_by_that_week()
    {
        var board = GoalBoard.Empty with { WeekSeen = D(2026, 11, 16) };

        Assert.Equal(ReviewDue.None, GoalReview.Due(board, D(2026, 11, 20)));
        Assert.Equal(ReviewDue.Week, GoalReview.Due(board, D(2026, 11, 24)));
        Assert.Equal(ReviewDue.Month, GoalReview.Due(board, D(2026, 12, 2)));
    }

    // Review focus 2: the machine was off all Monday; the prompt still shows on Tuesday, once, and once seen it stays
    // gone for the rest of that week, coming back the next Monday.
    [Fact]
    public void The_prompt_is_due_on_the_first_run_of_a_week_even_on_a_tuesday()
    {
        var board = SeenOn(D(2026, 11, 16));
        var tuesday = D(2026, 11, 24);
        Assert.Equal(ReviewDue.Week, GoalReview.Due(board, tuesday));

        var seen = board.Seen(GoalPeriod.WeekOf(tuesday), GoalPeriod.MonthOf(tuesday));

        Assert.Equal(ReviewDue.None, GoalReview.Due(seen, tuesday));
        Assert.Equal(ReviewDue.None, GoalReview.Due(seen, D(2026, 11, 29)));
        Assert.Equal(ReviewDue.Month, GoalReview.Due(seen, D(2026, 12, 1)));
        Assert.Equal(ReviewDue.Week, GoalReview.Due(seen.Seen(GoalPeriod.WeekOf(D(2026, 11, 23)), GoalPeriod.MonthOf(D(2026, 12, 1))), D(2026, 12, 1)));
    }

    [Fact]
    public void The_prompt_text()
    {
        Assert.Equal("Plan this week", GoalReview.PromptText(ReviewDue.Week));
        Assert.Equal("Plan this month", GoalReview.PromptText(ReviewDue.Month));
        Assert.Equal("", GoalReview.PromptText(ReviewDue.None));
    }

    // G4: every open goal of an earlier period, not only the last one, so a skipped week is not lost. Monthly first,
    // then weekly, each oldest period first, then in its order. Done, dropped and current goals are left out.
    [Fact]
    public void Pending_lists_open_goals_of_earlier_periods_only()
    {
        var today = D(2026, 11, 25);
        var thisWeek = GoalPeriod.WeekOf(today);
        var lastWeek = thisWeek.Previous();
        var twoWeeksBack = lastWeek.Previous();
        var thisMonth = GoalPeriod.MonthOf(today);
        var lastMonth = thisMonth.Previous();
        var ids = Enumerable.Range(1, 9).Select(n => Guid.Parse($"eeeeeeee-0000-0000-0000-00000000000{n}")).ToArray();
        var board = GoalBoard.Empty;
        GoalBoard Add(GoalBoard b, int n, GoalPeriod period) => b.Add($"g{n}", period, T0, ids[n - 1]).Value.Board;
        board = Add(board, 1, lastWeek);
        board = Add(board, 2, lastWeek);
        board = Add(board, 3, twoWeeksBack);
        board = Add(board, 4, thisWeek);
        board = Add(board, 5, lastMonth);
        board = Add(board, 6, thisMonth);
        board = Add(board, 7, lastWeek);
        board = Add(board, 8, lastWeek);
        board = board.MarkDone(ids[6], T0).Value.Drop(ids[7], T0).Value;

        Assert.Equal(["g5", "g3", "g1", "g2"], GoalReview.Pending(board, today).Select(g => g.Title));
        Assert.Empty(GoalReview.Pending(board, D(2026, 11, 1).AddDays(-30)));
    }
}
