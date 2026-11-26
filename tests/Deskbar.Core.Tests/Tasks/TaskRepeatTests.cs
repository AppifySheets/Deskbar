using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests.Tasks;

// Recurring task schedules (task 0b78269e, spec 2026-11-26-recurring-tasks-design.md, plan task 1). These pin the text
// forms the CLI and the menu share, the words the UI shows, and the next-occurrence rules, including the two daylight
// saving edge cases: a wall time that does not exist and one that happens twice.
public sealed class TaskRepeatTests
{
    // A fixed zone four hours ahead of UTC, so the arithmetic in the tests is plain.
    static readonly TimeZoneInfo T4 = TimeZoneInfo.CreateCustomTimeZone("T+4", TimeSpan.FromHours(4), "T+4", "T+4");
    static readonly TimeSpan Plus4 = TimeSpan.FromHours(4);
    static readonly DateTimeOffset Wednesday = new(2026, 11, 25, 14, 20, 0, Plus4);

    // Central Europe: 2026 clocks go forward on 29 March at 02:00 and back on 25 October at 03:00.
    static TimeZoneInfo Berlin => TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");

    static RepeatSchedule Ok(string text) => TaskRepeats.Parse(text).Value;

    static DateTimeOffset Local(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0, Plus4);

    [Fact]
    public void Every_interval_is_an_interval_from_now()
    {
        Assert.Equal(Local(2026, 11, 25, 15, 20), Ok("every 1h").Next(Wednesday, T4));
        Assert.Equal(Local(2026, 11, 25, 14, 35), Ok("every 15m").Next(Wednesday, T4));
    }

    [Theory]
    [InlineData(2026, 11, 25, 14, 20, 2026, 11, 25, 15, 20)]
    [InlineData(2026, 11, 25, 18, 30, 2026, 11, 26, 9, 0)]
    [InlineData(2026, 11, 25, 19, 0, 2026, 11, 26, 9, 0)]
    [InlineData(2026, 11, 25, 7, 0, 2026, 11, 25, 9, 0)]
    [InlineData(2026, 11, 25, 20, 0, 2026, 11, 26, 9, 0)]
    public void A_window_runs_inside_it_and_starts_the_day_at_its_start(int y, int mo, int d, int h, int mi, int ey, int emo, int ed, int eh, int emi) =>
        Assert.Equal(Local(ey, emo, ed, eh, emi), Ok("every 1h 09:00-19:00").Next(Local(y, mo, d, h, mi), T4));

    [Fact]
    public void Daily_weekdays_and_weekly_are_the_next_clock_time_strictly_after_now()
    {
        Assert.Equal(Local(2026, 11, 26, 9, 0), Ok("daily 09:00").Next(Wednesday, T4));
        Assert.Equal(Local(2026, 11, 25, 15, 0), Ok("daily 15:00").Next(Wednesday, T4));
        Assert.Equal(Local(2026, 11, 30, 9, 0), Ok("weekdays 09:00").Next(Local(2026, 11, 27, 10, 0), T4));
        Assert.Equal(Local(2026, 11, 30, 9, 0), Ok("weekly monday 09:00").Next(Local(2026, 11, 30, 8, 0), T4));
        Assert.Equal(Local(2026, 12, 7, 9, 0), Ok("weekly monday 09:00").Next(Local(2026, 11, 30, 9, 0), T4));
    }

    [Fact]
    public void Daily_nine_keeps_nine_across_spring_forward() =>
        Assert.Equal(
            new DateTimeOffset(2026, 3, 29, 9, 0, 0, TimeSpan.FromHours(2)),
            Ok("daily 09:00").Next(new DateTimeOffset(2026, 3, 28, 20, 0, 0, TimeSpan.FromHours(1)), Berlin));

    [Fact]
    public void A_time_in_the_spring_gap_runs_at_the_first_valid_minute_and_an_ambiguous_one_at_its_first_occurrence()
    {
        // 02:30 on 29 March does not exist in Berlin; the first valid minute after it is 03:00 (+02:00).
        Assert.Equal(
            new DateTimeOffset(2026, 3, 29, 3, 0, 0, TimeSpan.FromHours(2)),
            Ok("daily 02:30").Next(new DateTimeOffset(2026, 3, 28, 20, 0, 0, TimeSpan.FromHours(1)), Berlin));
        // 02:30 on 25 October happens twice; the first one is still summer time (+02:00).
        Assert.Equal(
            new DateTimeOffset(2026, 10, 25, 2, 30, 0, TimeSpan.FromHours(2)),
            Ok("daily 02:30").Next(new DateTimeOffset(2026, 10, 24, 20, 0, 0, TimeSpan.FromHours(2)), Berlin));
    }

    [Theory]
    [InlineData("every 15m")]
    [InlineData("EVERY 2H")]
    [InlineData("every 90m")]
    [InlineData("every 1h 9:00-19:00")]
    [InlineData("daily 09:00")]
    [InlineData("Weekdays 07:30")]
    [InlineData("weekly Mon 09:00")]
    [InlineData("weekly sunday 18:00")]
    public void Parse_accepts_every_form_and_text_round_trips(string text)
    {
        var parsed = Ok(text);
        Assert.Equal(parsed, Ok(parsed.Text));
    }

    [Fact]
    public void Parse_writes_canonical_text()
    {
        Assert.Equal("every 1h 09:00-19:00", Ok("every 1h 9:00-19:00").Text);
        Assert.Equal("every 2h", Ok("every 120m").Text);
        Assert.Equal("every 90m", Ok("every 90m").Text);
        Assert.Equal("weekly monday 09:00", Ok("weekly Mon 09:00").Text);
        Assert.Equal("weekdays 07:30", Ok("Weekdays 07:30").Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hourly")]
    [InlineData("every 10m")]
    [InlineData("every 25h")]
    [InlineData("every 1h 19:00-09:00")]
    [InlineData("daily 25:00")]
    [InlineData("weekly funday 09:00")]
    [InlineData("every 0h")]
    public void Parse_refuses_nonsense_with_a_line_naming_the_forms(string text)
    {
        var result = TaskRepeats.Parse(text);
        Assert.True(result.IsFailure);
        Assert.Contains("every <n>m", result.Error);
    }

    [Fact]
    public void Labels_say_it_in_words()
    {
        var expected = new[] { "every hour", "every hour, 09:00-19:00", "every day at 09:00", "every weekday at 09:00", "every Monday at 09:00" };
        Assert.Equal(expected, TaskRepeats.MenuChoices.Select(c => Ok(c.Text).Label).ToArray());
        // The menu's own label is the same words with a capital first letter.
        Assert.Equal(expected.Select(e => char.ToUpperInvariant(e[0]) + e[1..]).ToArray(), TaskRepeats.MenuChoices.Select(c => c.Label).ToArray());
        Assert.Equal("every 2 hours", Ok("every 2h").Label);
        Assert.Equal("every 90 minutes", Ok("every 90m").Label);
    }

    [Fact]
    public void ParseRequest_reads_off_and_options_anywhere()
    {
        Assert.IsType<RepeatRequest.Off>(TaskRepeats.ParseRequest(["off"]).Value);

        var a = Assert.IsType<RepeatRequest.Set>(TaskRepeats.ParseRequest(["every", "1h", "--model", "haiku"]).Value);
        Assert.Equal(Ok("every 1h"), a.Schedule);
        Assert.Equal(Maybe.From("haiku"), a.Model);
        Assert.True(a.Folder.HasNoValue);

        var b = Assert.IsType<RepeatRequest.Set>(TaskRepeats.ParseRequest(["--folder", @"C:\r", "daily", "09:00"]).Value);
        Assert.Equal(Ok("daily 09:00"), b.Schedule);
        Assert.True(b.Model.HasNoValue);
        Assert.Equal(Maybe.From(@"C:\r"), b.Folder);

        Assert.True(TaskRepeats.ParseRequest(["every", "1h", "--model", "bad name"]).IsFailure);
        Assert.True(TaskRepeats.ParseRequest(["every", "1h", "--model", "a", "--model", "b"]).IsFailure);
        Assert.True(TaskRepeats.ParseRequest([]).IsFailure);
    }
}
