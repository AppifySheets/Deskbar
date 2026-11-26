using System.Globalization;
using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// HOW LONG A SNOOZE LASTS (task 6da8d7eb, 2026-11-25). Petre asked for a snooze button and left the length to us ("you
// tell me how long"). One fixed length cannot fit both "not this minute" and "not today", so the button opens a menu of
// four, shortest first:
//
//   1 hour            out of the way for a meeting or a focused stretch
//   3 hours           the rest of the morning or the afternoon
//   Tomorrow 09:00    not today; back at the start of the next working morning
//   Monday 09:00      not this week; back at the start of the next one (on a Sunday that is tomorrow)
//
// The two "at 09:00" choices are wall-clock times on this machine, so they are worked out from local time, and the menu
// says the actual time ("Tomorrow, Thu 09:00") so a snooze taken at 01:00 is not a surprise. `task snooze` on the control
// pipe takes the same four by name and also a plain length ("45m", "2h", "3d") for anything else.
//
// Pure: callers pass "now" (local, with its offset) and the time zone's offset rule, so tests need no clock.

/// <summary>The four snooze lengths the tasks panel's menu offers, shortest first.</summary>
public enum SnoozeChoice { Hour, ThreeHours, Tomorrow, Monday }

public static class TaskSnooze
{
    /// <summary>The menu's order.</summary>
    public static readonly IReadOnlyList<SnoozeChoice> Choices = [SnoozeChoice.Hour, SnoozeChoice.ThreeHours, SnoozeChoice.Tomorrow, SnoozeChoice.Monday];

    /// <summary>The hour of the morning the two "at a time" choices wake at.</summary>
    public const int MorningHour = 9;

    /// <summary>
    /// When a snooze taken at <paramref name="now"/> ends. <paramref name="offsetAt"/> gives the local UTC offset in force
    /// at a local wall-clock time (TimeZoneInfo.Local.GetUtcOffset in the app), so "tomorrow 09:00" is 09:00 on the clock
    /// even across a daylight saving change.
    /// </summary>
    public static DateTimeOffset Until(SnoozeChoice choice, DateTimeOffset now, Func<DateTime, TimeSpan> offsetAt) => choice switch
    {
        SnoozeChoice.Hour => now.AddHours(1),
        SnoozeChoice.ThreeHours => now.AddHours(3),
        SnoozeChoice.Tomorrow => Morning(now.Date.AddDays(1), offsetAt),
        _ => Morning(now.Date.AddDays(DaysToMonday(now.DayOfWeek)), offsetAt),
    };

    // Days from today to the NEXT Monday, 1 to 7: a Monday snoozed "to Monday" goes a whole week.
    static int DaysToMonday(DayOfWeek today) => ((int)DayOfWeek.Monday - (int)today + 6) % 7 + 1;

    // MorningHour on `day`'s wall clock, with the offset in force then (a DST change between now and then is honoured).
    static DateTimeOffset Morning(DateTime day, Func<DateTime, TimeSpan> offsetAt) =>
        WallClock(DateTime.SpecifyKind(day.Date.AddHours(MorningHour), DateTimeKind.Unspecified), offsetAt);

    static DateTimeOffset WallClock(DateTime wall, Func<DateTime, TimeSpan> offsetAt) => new(wall, offsetAt(wall));

    /// <summary>
    /// A menu item's words: the length and, for the two "at a time" choices, the day and time it wakes
    /// ("1 hour", "3 hours", "Tomorrow, Thu 09:00", "Monday, 1 Dec 09:00").
    /// </summary>
    public static string Label(SnoozeChoice choice, DateTimeOffset now, Func<DateTime, TimeSpan> offsetAt) => choice switch
    {
        SnoozeChoice.Hour => "1 hour",
        SnoozeChoice.ThreeHours => "3 hours",
        SnoozeChoice.Tomorrow => $"Tomorrow, {Until(choice, now, offsetAt).ToString("ddd HH:mm", CultureInfo.InvariantCulture)}",
        _ => $"Monday, {Until(choice, now, offsetAt).ToString("d MMM HH:mm", CultureInfo.InvariantCulture)}",
    };

    /// <summary>
    /// How a snoozed task says when it comes back, from <paramref name="now"/>: "until 15:40" today, "until tomorrow
    /// 09:00", "until Mon 09:00" within the week, "until 8 Dec 09:00" further off. Both times local.
    /// </summary>
    public static string UntilText(DateTimeOffset until, DateTimeOffset now) => $"until {WhenText(until, now)}";

    /// <summary>
    /// The time part of <see cref="UntilText"/> without its "until": "15:40" today, "tomorrow 09:00", "Mon 09:00" within
    /// the week, "8 Dec 09:00" further off. Shared with the repeat mark's "next run ..." (task 0b78269e, Task 9), so a
    /// snooze and a repeat say a time the same way. Both times local.
    /// </summary>
    public static string WhenText(DateTimeOffset when, DateTimeOffset now) =>
        (when.Date - now.Date).Days switch
        {
            <= 0 => when.ToString("HH:mm", CultureInfo.InvariantCulture),
            1 => $"tomorrow {when.ToString("HH:mm", CultureInfo.InvariantCulture)}",
            < 7 => when.ToString("ddd HH:mm", CultureInfo.InvariantCulture),
            _ => when.ToString("d MMM HH:mm", CultureInfo.InvariantCulture),
        };

    /// <summary>
    /// What `task snooze` takes: one of the menu's four by name ("1h" and "3h" are the first two, then "tomorrow" and
    /// "monday"), or a plain length, a whole number of minutes, hours or days ("45m", "2h", "3d"). The answer is when
    /// the snooze ends. A length of nothing, or longer than a year, is refused.
    /// </summary>
    public static Result<DateTimeOffset> Parse(string text, DateTimeOffset now, Func<DateTime, TimeSpan> offsetAt) =>
        (text ?? "").Trim().ToLowerInvariant() switch
        {
            "tomorrow" => Until(SnoozeChoice.Tomorrow, now, offsetAt),
            "monday" => Until(SnoozeChoice.Monday, now, offsetAt),
            var length => Regex.Match(length, @"^(\d{1,6})\s*([mhd])$") is { Success: true } m
                ? Length(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), m.Groups[2].Value[0])
                    .Ensure(span => span > TimeSpan.Zero && span <= TimeSpan.FromDays(366), "A snooze lasts from a minute to a year")
                    .Map(span => now + span)
                : Result.Failure<DateTimeOffset>($"'{text}' is not a snooze length: use 1h, 3h, tomorrow, monday, or a number with m, h or d (45m, 2h, 3d)"),
        };

    static Result<TimeSpan> Length(int count, char unit) => unit switch
    {
        'm' => TimeSpan.FromMinutes(count),
        'h' => TimeSpan.FromHours(count),
        _ => TimeSpan.FromDays(count),
    };
}
