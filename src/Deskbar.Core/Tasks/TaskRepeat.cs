using System.Globalization;
using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// HOW A RECURRING TASK SAYS WHEN IT COMES ROUND (task 0b78269e, spec docs/superpowers/specs/2026-11-26-recurring-tasks-design.md,
// plan task 1). A task can carry a schedule, and Deskbar starts a Claude session on it each time the schedule comes
// round. This file is only the schedule: its text, its words, and the function that says when it next fires. Nothing
// here reads a clock or a file, so tests need neither.
//
// ONE TEXT, FOR THE CLI AND THE MENU. `task repeat <id> every 1h` and the tasks panel's menu entries go through the same
// parser, so a menu choice is just a canned line of text and a "Custom..." box takes the same words as the CLI:
//
//   every <n>m          every n minutes, 15 to 1440
//   every <n>h          every n hours, 1 to 24
//   every ... HH:MM-HH:MM   either of the two above, only inside a same-day window (start before end)
//   daily HH:MM         at that time every day
//   weekdays HH:MM      at that time Monday to Friday
//   weekly <day> HH:MM  at that time on one day (monday or mon)
//
// Case and extra spaces are ignored and H:MM is accepted; the canonical text (RepeatSchedule.Text) is what gets stored
// and shown back, so "every 120m" reads "every 2h" and "9:00" reads "09:00". The floor of 15 minutes is there because a
// Claude session is not a cheap thing to start (each run costs a context and a model call); the ceiling of 24 hours is
// there because a longer period is `daily` or `weekly`, which say it better and survive a daylight saving change.
//
// LOCAL WALL-CLOCK TIME. "daily 09:00" means 09:00 on the clock of this machine, so Next takes the time zone and works
// on the local wall clock, then turns the wall time back into an instant. Two awkward wall times exist: one that is
// skipped (the hour the clocks jump forward over) runs at the first minute that does exist, and one that happens twice
// (the hour they fall back over) runs at its first occurrence. Both are decisions, pinned by tests, not accidents.
//
// ALWAYS FROM NOW, NEVER CATCH UP. Next is the first occurrence strictly after "now", where the caller decides what now
// is (when the repeat is set, when a run starts, when it ends, when an occurrence is skipped). A laptop that slept
// through three 09:00s runs once when it wakes, not three times: the schedule is "about every", not a ledger.
//
// A WINDOW IS SAME-DAY ONLY. "every 1h 09:00-19:00" runs inside one day's window and starts the next day at its start;
// a window that crosses midnight (19:00-02:00) is refused at parse time rather than given a meaning nobody asked for.

/// <summary>A same-day stretch of the clock, <see cref="From"/> before <see cref="To"/>, that an interval schedule stays inside.</summary>
public sealed record RepeatWindow(TimeOnly From, TimeOnly To);

/// <summary>When a recurring task comes round: an interval (optionally inside a window), daily, weekdays or weekly.</summary>
public abstract record RepeatSchedule
{
    /// <summary>The canonical text `task repeat` takes and the tasks file stores ("every 1h 09:00-19:00", "weekly monday 09:00").</summary>
    public abstract string Text { get; }

    /// <summary>The same schedule in words for a menu or a tooltip ("every hour, 09:00-19:00", "every Monday at 09:00").</summary>
    public abstract string Label { get; }

    /// <summary>The first occurrence strictly after <paramref name="now"/>, as a UTC instant, on <paramref name="zone"/>'s wall clock.</summary>
    public abstract DateTimeOffset Next(DateTimeOffset now, TimeZoneInfo zone);

    /// <summary>Every <paramref name="Interval"/> from now, and with a window only inside it (the day starts at its start).</summary>
    public sealed record Every(TimeSpan Interval, Maybe<RepeatWindow> Window) : RepeatSchedule
    {
        // Whole hours read as hours ("every 2h"), anything else as minutes ("every 90m"), so the text is canonical.
        public override string Text =>
            $"every {(Interval.TotalMinutes % 60 == 0 ? $"{(int)Interval.TotalHours}h" : $"{(int)Interval.TotalMinutes}m")}"
            + Window.Match(w => $" {Clock(w.From)}-{Clock(w.To)}", () => "");

        public override string Label =>
            (Interval.TotalMinutes, Interval.TotalHours) switch
            {
                (60, _) => "every hour",
                (_, var h) when Interval.TotalMinutes % 60 == 0 => $"every {(int)h} hours",
                (var m, _) => $"every {(int)m} minutes",
            } + Window.Match(w => $", {Clock(w.From)}-{Clock(w.To)}", () => "");

        public override DateTimeOffset Next(DateTimeOffset now, TimeZoneInfo zone) =>
            Window.Match(w => InWindow(w, now, zone), () => (now + Interval).ToUniversalTime());

        // The window rule (spec "Schedules"): before today's start, today's start; otherwise now plus the interval when
        // that is still the same local day and not past the end; otherwise tomorrow's start. Working it from the
        // candidate instant converted back to local time keeps a daylight saving change inside the day honest.
        DateTimeOffset InWindow(RepeatWindow w, DateTimeOffset now, TimeZoneInfo zone)
        {
            var local = TimeZoneInfo.ConvertTime(now, zone);
            var candidate = TimeZoneInfo.ConvertTime(now + Interval, zone);
            return local.TimeOfDay < w.From.ToTimeSpan() ? Instant(local.Date, w.From, zone)
                : candidate.Date == local.Date && candidate.TimeOfDay <= w.To.ToTimeSpan() ? candidate.ToUniversalTime()
                : Instant(local.Date.AddDays(1), w.From, zone);
        }
    }

    /// <summary>At <paramref name="At"/> every day.</summary>
    public sealed record Daily(TimeOnly At) : RepeatSchedule
    {
        public override string Text => $"daily {Clock(At)}";
        public override string Label => $"every day at {Clock(At)}";
        public override DateTimeOffset Next(DateTimeOffset now, TimeZoneInfo zone) => NextAt(now, zone, At, _ => true);
    }

    /// <summary>At <paramref name="At"/> Monday to Friday.</summary>
    public sealed record Weekdays(TimeOnly At) : RepeatSchedule
    {
        public override string Text => $"weekdays {Clock(At)}";
        public override string Label => $"every weekday at {Clock(At)}";
        public override DateTimeOffset Next(DateTimeOffset now, TimeZoneInfo zone) =>
            NextAt(now, zone, At, d => d is not (DayOfWeek.Saturday or DayOfWeek.Sunday));
    }

    /// <summary>At <paramref name="At"/> on one day of the week.</summary>
    public sealed record Weekly(DayOfWeek Day, TimeOnly At) : RepeatSchedule
    {
        public override string Text => $"weekly {Day.ToString().ToLowerInvariant()} {Clock(At)}";
        public override string Label => $"every {Day} at {Clock(At)}";
        public override DateTimeOffset Next(DateTimeOffset now, TimeZoneInfo zone) => NextAt(now, zone, At, d => d == Day);
    }

    static string Clock(TimeOnly t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);

    // The first day, from today's local date on, that suits and whose clock time is strictly after now. Eight days is
    // enough for any weekly schedule (the same day next week is the furthest), so the search always ends.
    static DateTimeOffset NextAt(DateTimeOffset now, TimeZoneInfo zone, TimeOnly at, Func<DayOfWeek, bool> suits) =>
        Enumerable.Range(0, 8)
            .Select(i => TimeZoneInfo.ConvertTime(now, zone).Date.AddDays(i))
            .Where(day => suits(day.DayOfWeek))
            .Select(day => Instant(day, at, zone))
            .First(instant => instant > now);

    // A local wall time as an instant. A time in the spring gap does not exist, so it moves forward a minute at a time to
    // the first one that does; an ambiguous time (the autumn hour that happens twice) takes its first occurrence, which
    // is the larger of the two offsets.
    static DateTimeOffset Instant(DateTime day, TimeOnly at, TimeZoneInfo zone)
    {
        var wall = DateTime.SpecifyKind(day.Date + at.ToTimeSpan(), DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(wall)) wall = wall.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(wall) ? zone.GetAmbiguousTimeOffsets(wall).Max() : zone.GetUtcOffset(wall);
        return new DateTimeOffset(wall, offset).ToUniversalTime();
    }
}

/// <summary>
/// A task's repeat: when it comes round, the model its runs use (<see cref="DefaultModel"/> unless chosen, checked by
/// ClaudeStart.Model) and the folder they start in (stored as ClaudeStart.RepoOf(folder) by the caller, because a
/// worktree is deleted after its merge).
/// </summary>
public sealed record TaskRepeat(RepeatSchedule Schedule, string Model, string Folder)
{
    // Sonnet, not ClaudeStart.DefaultModel: a repeat runs unattended and often, and the spec (a repeat carries its own
    // model, Sonnet by default) keeps that cheap. Other task sessions keep `claude-model`.
    public const string DefaultModel = "sonnet";
}

/// <summary>What the words after `task repeat &lt;id&gt;` (or a "Custom..." line) ask for.</summary>
public abstract record RepeatRequest
{
    /// <summary>Stop repeating.</summary>
    public sealed record Off : RepeatRequest;

    /// <summary>Repeat on <paramref name="Schedule"/>, with a model and a folder only when the words named them.</summary>
    public sealed record Set(RepeatSchedule Schedule, Maybe<string> Model, Maybe<string> Folder) : RepeatRequest;
}

public static class TaskRepeats
{
    /// <summary>The shortest interval: a Claude session is not cheap to start.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(15);

    /// <summary>The longest interval; longer than a day is `daily` or `weekly`.</summary>
    public static readonly TimeSpan MaxInterval = TimeSpan.FromHours(24);

    /// <summary>The tasks panel's menu entries, shortest period first: the words shown and the text each one parses from.</summary>
    public static readonly IReadOnlyList<(string Label, string Text)> MenuChoices =
    [
        ("Every hour", "every 1h"),
        ("Every hour, 09:00-19:00", "every 1h 09:00-19:00"),
        ("Every day at 09:00", "daily 09:00"),
        ("Every weekday at 09:00", "weekdays 09:00"),
        ("Every Monday at 09:00", "weekly monday 09:00"),
    ];

    const string Forms = "use every <n>m or every <n>h (15 minutes to 24 hours, optionally followed by a window such as 09:00-19:00), daily HH:MM, weekdays HH:MM, or weekly <day> HH:MM";

    // One pattern per form, tried in turn (as TaskSnooze.Parse does). Times are H:MM or HH:MM; their range is checked after.
    static readonly Regex EveryForm = new(@"^every (\d{1,4})([mh])(?: (\d{1,2}:\d{2})-(\d{1,2}:\d{2}))?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex DayForm = new(@"^(daily|weekdays) (\d{1,2}:\d{2})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex WeeklyForm = new(@"^weekly ([a-z]+) (\d{1,2}:\d{2})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>A schedule from its text, or a line naming the forms that are accepted.</summary>
    public static Result<RepeatSchedule> Parse(string text)
    {
        var normal = Regex.Replace((text ?? "").Trim().ToLowerInvariant(), @"\s+", " ");
        var parsed =
            EveryForm.Match(normal) is { Success: true } e ? ParseEvery(e)
            : DayForm.Match(normal) is { Success: true } d ? Time(d.Groups[2].Value).Map<TimeOnly, RepeatSchedule>(t => d.Groups[1].Value == "daily" ? new RepeatSchedule.Daily(t) : new RepeatSchedule.Weekdays(t))
            : WeeklyForm.Match(normal) is { Success: true } w ? ParseWeekly(w)
            : Result.Failure<RepeatSchedule>("not a schedule");
        return parsed.IsSuccess ? parsed : Result.Failure<RepeatSchedule>($"'{text}' is not a schedule: {Forms}");
    }

    static Result<RepeatSchedule> ParseEvery(Match m)
    {
        var count = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var interval = m.Groups[2].Value == "m" ? TimeSpan.FromMinutes(count) : TimeSpan.FromHours(count);
        var window = m.Groups[3].Success
            ? Time(m.Groups[3].Value).Bind(from => Time(m.Groups[4].Value)
                .Ensure(to => from < to, "a window starts before it ends")
                .Map(to => Maybe.From(new RepeatWindow(from, to))))
            : Result.Success(Maybe<RepeatWindow>.None);
        return interval >= MinInterval && interval <= MaxInterval
            ? window.Map<Maybe<RepeatWindow>, RepeatSchedule>(win => new RepeatSchedule.Every(interval, win))
            : Result.Failure<RepeatSchedule>("an interval is 15 minutes to 24 hours");
    }

    // A day by full or three-letter English name: "monday" and "mon" both work, "funday" does not.
    static Result<RepeatSchedule> ParseWeekly(Match m) =>
        Enum.GetValues<DayOfWeek>()
            .Where(day => m.Groups[1].Value.Length >= 3 && day.ToString().ToLowerInvariant() is var name && (name == m.Groups[1].Value || name[..3] == m.Groups[1].Value))
            .Select(day => (DayOfWeek?)day)
            .FirstOrDefault() is { } found
            ? Time(m.Groups[2].Value).Map<TimeOnly, RepeatSchedule>(t => new RepeatSchedule.Weekly(found, t))
            : Result.Failure<RepeatSchedule>("not a day");

    static Result<TimeOnly> Time(string text) =>
        TimeOnly.TryParseExact(text, ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t
            : Result.Failure<TimeOnly>("not a time");

    /// <summary>
    /// What the words after `task repeat &lt;id&gt;` ask for: `off` alone, or schedule words with `--model &lt;m&gt;` and
    /// `--folder &lt;p&gt;` at most once each, in any position. The model is checked by ClaudeStart.Model.
    /// </summary>
    public static Result<RepeatRequest> ParseRequest(IReadOnlyList<string> words) =>
        words is [var only] && only.Trim().Equals("off", StringComparison.OrdinalIgnoreCase)
            ? new RepeatRequest.Off()
            : Options(words).Bind(o =>
                Parse(string.Join(' ', o.Rest)).Bind(schedule =>
                    o.Model.Match(m => ClaudeStart.Model(m).Map(Maybe.From), () => Result.Success(Maybe<string>.None))
                        .Map<Maybe<string>, RepeatRequest>(model => new RepeatRequest.Set(schedule, model, o.Folder))));

    sealed record Split(IReadOnlyList<string> Rest, Maybe<string> Model, Maybe<string> Folder);

    // Splits the words into the schedule's own words and the two options. An option given twice, or without a value, fails.
    static Result<Split> Options(IReadOnlyList<string> words)
    {
        var rest = new List<string>();
        Maybe<string> model = default, folder = default;
        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            var isModel = word.Equals("--model", StringComparison.OrdinalIgnoreCase);
            var isFolder = word.Equals("--folder", StringComparison.OrdinalIgnoreCase);
            if (!isModel && !isFolder) { rest.Add(word); continue; }
            if (i + 1 >= words.Count) return Result.Failure<Split>($"{word} needs a value");
            if (isModel ? model.HasValue : folder.HasValue) return Result.Failure<Split>($"{word} is given once");
            if (isModel) model = words[++i]; else folder = words[++i];
        }
        return new Split(rest, model, folder);
    }
}
