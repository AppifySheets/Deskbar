using System.Globalization;

namespace Deskbar.Core.Goals;

// The period a goal belongs to: one week or one calendar month. Design:
// docs/superpowers/specs/2026-11-22-goals-planner-design.md ("Constraints", "The goal").
//
// A PERIOD IS A LOCAL CALENDAR DATE, NEVER AN INSTANT. "This week" is Petre's week wherever the clock says UTC midnight
// falls: at 00:30 UTC on a Monday it can still be Sunday evening where he is, and the goal he adds then belongs to the
// week that is ending. So a period is stored as a DateOnly (the Monday of its week, the 1st of its month) and every
// caller hands in the local date (GoalService's injected `today`); nothing here reads a clock.
//
// A week runs Monday to Sunday; a month is the calendar month. The constructor is private, so every period starts on a
// Monday or a 1st: the factories snap any day to the start of the period holding it, which is also how a hand-edited
// date in goals.json is read (JsonGoalStore). Two periods are equal when their kind and start are, so a period is a
// value: In(period) and the planner's week arrows compare them directly.
public enum GoalKind { Week, Month }

public sealed record GoalPeriod
{
    GoalPeriod(GoalKind kind, DateOnly start) => (Kind, Start) = (kind, start);

    public GoalKind Kind { get; }

    /// <summary>The first day: a Monday for a week, the 1st for a month.</summary>
    public DateOnly Start { get; }

    /// <summary>The last day, inclusive: the Sunday of a week, the last day of a month.</summary>
    public DateOnly End => Kind == GoalKind.Week ? Start.AddDays(6) : Start.AddMonths(1).AddDays(-1);

    /// <summary>The week holding <paramref name="day"/>, Monday to Sunday.</summary>
    public static GoalPeriod WeekOf(DateOnly day) =>
        // DayOfWeek counts from Sunday = 0; shifting by six makes Monday 0 and Sunday 6, the days back to Monday.
        new(GoalKind.Week, day.AddDays(-(((int)day.DayOfWeek + 6) % 7)));

    /// <summary>The calendar month holding <paramref name="day"/>.</summary>
    public static GoalPeriod MonthOf(DateOnly day) => new(GoalKind.Month, new DateOnly(day.Year, day.Month, 1));

    public static GoalPeriod Of(GoalKind kind, DateOnly day) => kind == GoalKind.Week ? WeekOf(day) : MonthOf(day);

    public bool Contains(DateOnly day) => Start <= day && day <= End;

    public GoalPeriod Next() => Kind == GoalKind.Week ? new(Kind, Start.AddDays(7)) : new(Kind, Start.AddMonths(1));

    public GoalPeriod Previous() => Kind == GoalKind.Week ? new(Kind, Start.AddDays(-7)) : new(Kind, Start.AddMonths(-1));

    /// <summary>
    /// Whether the two date ranges overlap. A weekly goal may only name a monthly goal of a month its week touches
    /// (spec, "The goal"), and the week of Monday 30 November 2026 touches both November and December.
    /// </summary>
    public bool Touches(GoalPeriod other) => Start <= other.End && other.Start <= End;

    /// <summary>The months this period lies in: one or two for a week, the month itself for a month.</summary>
    public IReadOnlyList<GoalPeriod> Months() =>
        Kind == GoalKind.Month ? [this] : new[] { MonthOf(Start), MonthOf(End) }.Distinct().ToArray();

    /// <summary>"Week of 23 Nov 2026" or "November 2026", in the invariant culture, as the planner and the pipe show it.</summary>
    public string Label =>
        Kind == GoalKind.Week
            ? "Week of " + Start.ToString("d MMM yyyy", CultureInfo.InvariantCulture)
            : Start.ToString("MMMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>The start as yyyy-MM-dd: how goals.json and the pipe write a period.</summary>
    public string Key => Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public override string ToString() => Label;
}
