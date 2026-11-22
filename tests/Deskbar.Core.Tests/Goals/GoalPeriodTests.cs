using Deskbar.Core.Goals;

namespace Deskbar.Core.Tests.Goals;

// Calendar arithmetic for a goal's period: a week runs Monday to Sunday, a month is the calendar month, and a period
// is a local date, never an instant. Design: docs/superpowers/specs/2026-11-22-goals-planner-design.md ("Constraints",
// "The goal").
public sealed class GoalPeriodTests
{
    static DateOnly D(int year, int month, int day) => new(year, month, day);

    [Fact]
    public void A_week_starts_on_its_monday()
    {
        // 2026-11-22 is a Sunday: the last day of the week that began on Monday the 16th.
        var sunday = GoalPeriod.WeekOf(D(2026, 11, 22));
        Assert.Equal(GoalKind.Week, sunday.Kind);
        Assert.Equal(D(2026, 11, 16), sunday.Start);
        Assert.Equal(D(2026, 11, 22), sunday.End);

        Assert.Equal(D(2026, 11, 23), GoalPeriod.WeekOf(D(2026, 11, 23)).Start);
        Assert.Equal(GoalPeriod.WeekOf(D(2026, 11, 23)), GoalPeriod.WeekOf(D(2026, 11, 26)));
    }

    [Fact]
    public void A_month_starts_on_the_first()
    {
        var november = GoalPeriod.MonthOf(D(2026, 11, 22));
        Assert.Equal(GoalKind.Month, november.Kind);
        Assert.Equal(D(2026, 11, 1), november.Start);
        Assert.Equal(D(2026, 11, 30), november.End);

        Assert.Equal(D(2024, 2, 29), GoalPeriod.MonthOf(D(2024, 2, 10)).End);
        Assert.Equal(GoalPeriod.MonthOf(D(2026, 11, 22)), GoalPeriod.Of(GoalKind.Month, D(2026, 11, 3)));
        Assert.Equal(GoalPeriod.WeekOf(D(2026, 11, 22)), GoalPeriod.Of(GoalKind.Week, D(2026, 11, 18)));
    }

    // Review focus 1: Monday 30 November to Sunday 6 December 2026 lies in two months.
    [Fact]
    public void A_week_across_two_months_touches_both()
    {
        var split = GoalPeriod.WeekOf(D(2026, 11, 30));
        var november = GoalPeriod.MonthOf(D(2026, 11, 1));
        var december = GoalPeriod.MonthOf(D(2026, 12, 1));

        Assert.Equal([november, december], split.Months());
        Assert.True(split.Touches(november));
        Assert.True(split.Touches(december));
        Assert.True(november.Touches(split));
        Assert.False(split.Touches(GoalPeriod.MonthOf(D(2026, 10, 1))));

        Assert.Equal([november], GoalPeriod.WeekOf(D(2026, 11, 23)).Months());
        Assert.Equal([november], november.Months());
    }

    [Fact]
    public void Contains_is_inclusive_at_both_ends()
    {
        var week = GoalPeriod.WeekOf(D(2026, 11, 23));

        Assert.True(week.Contains(D(2026, 11, 23)));
        Assert.True(week.Contains(D(2026, 11, 29)));
        Assert.False(week.Contains(D(2026, 11, 30)));
        Assert.False(week.Contains(D(2026, 11, 22)));
    }

    [Fact]
    public void Next_and_previous_step_one_period()
    {
        Assert.Equal(D(2026, 11, 30), GoalPeriod.WeekOf(D(2026, 11, 23)).Next().Start);
        Assert.Equal(D(2026, 11, 16), GoalPeriod.WeekOf(D(2026, 11, 23)).Previous().Start);
        Assert.Equal(D(2026, 11, 1), GoalPeriod.MonthOf(D(2026, 12, 1)).Previous().Start);
        Assert.Equal(D(2026, 12, 1), GoalPeriod.MonthOf(D(2027, 1, 1)).Previous().Start);
        Assert.Equal(D(2027, 1, 1), GoalPeriod.MonthOf(D(2026, 12, 31)).Next().Start);
        Assert.Equal(GoalKind.Month, GoalPeriod.MonthOf(D(2026, 12, 1)).Next().Kind);
    }

    [Fact]
    public void Labels_and_keys()
    {
        Assert.Equal("Week of 23 Nov 2026", GoalPeriod.WeekOf(D(2026, 11, 25)).Label);
        Assert.Equal("Week of 7 Dec 2026", GoalPeriod.WeekOf(D(2026, 12, 7)).Label);
        Assert.Equal("November 2026", GoalPeriod.MonthOf(D(2026, 11, 25)).Label);
        Assert.Equal("2026-11-23", GoalPeriod.WeekOf(D(2026, 11, 25)).Key);
        Assert.Equal("2026-11-01", GoalPeriod.MonthOf(D(2026, 11, 25)).Key);
    }
}
