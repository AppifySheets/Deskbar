using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// THE TASK STATS (task 521d2b07; spec docs/superpowers/specs/2026-11-25-task-stats-design.md). Petre asked for "stats for
// tasks, like a burndown or similar, with links to each task". A burndown assumes a fixed amount of work shrinking to zero,
// and his list grows by about twenty tasks a day, so this is a burn-up: per day, the tasks added, the tasks finished, and
// how many were still open at the end of it. The tasks panel's Stats view draws it (TasksWindow.Stats.cs) and
// `task stats` prints it.
//
// FINISHED means Done or Closed, as GoalProgress counts it: Done is Claude's "finished", Closed is Petre's.
//
// A FINISH TIME is when the task's current finished run began. The status log (TaskStatusLog) says it exactly for every
// change since logging began; before that, the file only has two hints, used as the fallback and flagged as estimated:
//   * Done: Raised, which TaskBoard sets the moment a task becomes Done (TaskBoard.BecameDone).
//   * Closed: UpdatedAt, which closing moves (and so does an edit after the close, which is rare). If the log saw the task
//     go Done -> Closed, it was finished when it became Done, which is its Raised.
// A finish time is never before the task's creation.
//
// TWO DELIBERATE SIMPLIFICATIONS (spec, "Where the history comes from"): a task reopened and finished again counts once,
// at its last finish; and a day's open count treats a task as open from its creation to its current finish, so a task
// closed, reopened and still open counts as open on the days it was closed too.

/// <summary>One day of the stats: the tasks added that day and finished that day (latest first), and how many were open at its end.</summary>
public sealed record TaskStatsDay(DateOnly Date, IReadOnlyList<TaskItem> Added, IReadOnlyList<TaskItem> Finished, int OpenAtEnd);

/// <summary>
/// The stats view's numbers. <see cref="Days"/> runs oldest first and ends today. <see cref="Estimated"/> says whether
/// any finish time counted in the days leans on the fallback rather than the status log.
/// </summary>
public sealed record TaskStatsView(
    IReadOnlyList<TaskStatsDay> Days,
    int OpenNow,
    int AddedThisWeek,
    int FinishedThisWeek,
    Maybe<TimeSpan> MedianToFinish,
    bool Estimated);

/// <summary>When a task finished, and whether that comes from the fallback rather than the status log.</summary>
public sealed record TaskFinish(DateTimeOffset At, bool Estimated);

public static class TaskStats
{
    /// <summary>The number of days the stats cover by default: two weeks, today included.</summary>
    public const int DefaultDays = 14;

    /// <summary>The fewest days the panel shows, however young the list is.</summary>
    public const int MinDays = 7;

    /// <summary>
    /// How many days the panel shows: from the day the oldest task was added to today, at least <see cref="MinDays"/> and
    /// at most <see cref="DefaultDays"/>. Measured on Petre's list on 2026-11-25: its tasks began on 19 November, so a
    /// fixed 14 days left the first half of both charts empty, a flat line saying nothing.
    /// </summary>
    public static int DaysToShow(TaskBoard board, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (board.Tasks.Count == 0) return MinDays;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(board.Tasks.Min(t => t.Created), zone).DateTime);
        return Math.Clamp(today.DayNumber - first.DayNumber + 1, MinDays, DefaultDays);
    }

    /// <summary>Done or Closed: what the stats count as finished.</summary>
    public static bool IsFinished(TaskStatus status) => status is TaskStatus.Done or TaskStatus.Closed;

    /// <summary>When the task's current finished run began, or none for a task that is not finished (see the header).</summary>
    public static Maybe<TaskFinish> FinishedAt(TaskItem task)
    {
        if (!IsFinished(task.Status)) return Maybe<TaskFinish>.None;
        // The trailing run of the log that ends in finished statuses: everything after the last move into an open one.
        var run = task.StatusLog.OrderBy(c => c.At).Reverse().TakeWhile(c => IsFinished(c.To)).Reverse().ToArray();
        var finish = run.Length > 0 && !IsFinished(run[0].From)
            ? new TaskFinish(run[0].At, Estimated: false)
            : new TaskFinish(Fallback(task, run), Estimated: true);
        return finish with { At = finish.At < task.Created ? task.Created : finish.At };
    }

    // The fallback (see the header): Raised for a Done task, or for a Closed one the log saw leave Done; UpdatedAt otherwise.
    static DateTimeOffset Fallback(TaskItem task, IReadOnlyList<StatusChange> run) =>
        task.Status == TaskStatus.Done || (run.Count > 0 && run[0].From == TaskStatus.Done) ? task.Raised : task.UpdatedAt;

    /// <summary>
    /// The stats for the <paramref name="days"/> days ending today, today being <paramref name="now"/>'s date in
    /// <paramref name="zone"/> (the days are Petre's local days, not UTC's). Every task counts, closed and snoozed ones and
    /// subtasks included: the stats are about the work, not about what a filter shows.
    /// </summary>
    public static TaskStatsView Of(TaskBoard board, DateTimeOffset now, TimeZoneInfo zone, int days = DefaultDays)
    {
        var count = Math.Max(1, days);
        var finishes = board.Tasks.Select(t => (Task: t, Finish: FinishedAt(t))).ToArray();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var dates = Enumerable.Range(0, count).Select(i => today.AddDays(i - count + 1)).ToArray();
        var first = StartOf(dates[0], zone);

        TaskStatsDay Day(DateOnly date)
        {
            var (start, end) = (StartOf(date, zone), StartOf(date.AddDays(1), zone));
            var close = end < now ? end : now;
            bool In(DateTimeOffset at) => at >= start && at < end;
            return new TaskStatsDay(date,
                board.Tasks.Where(t => In(t.Created)).OrderByDescending(t => t.Created).ToArray(),
                finishes.Where(f => f.Finish.HasValue && In(f.Finish.Value.At)).OrderByDescending(f => f.Finish.Value.At).Select(f => f.Task).ToArray(),
                finishes.Count(f => f.Task.Created <= close && !(f.Finish.HasValue && f.Finish.Value.At <= close)));
        }

        var week = now.AddDays(-7);
        var recent = finishes.Where(f => f.Finish.HasValue && f.Finish.Value.At > now.AddDays(-14))
            .Select(f => f.Finish.Value.At - f.Task.Created).OrderBy(span => span).ToArray();
        return new TaskStatsView(
            dates.Select(Day).ToArray(),
            OpenNow: board.Tasks.Count(t => !IsFinished(t.Status)),
            AddedThisWeek: board.Tasks.Count(t => t.Created > week),
            FinishedThisWeek: finishes.Count(f => f.Finish.HasValue && f.Finish.Value.At > week),
            MedianToFinish: recent.Length == 0 ? Maybe<TimeSpan>.None : Median(recent),
            Estimated: finishes.Any(f => f.Finish.HasValue && f.Finish.Value.Estimated && f.Finish.Value.At >= first));
    }

    // The middle of a sorted list, or the mean of the two middle ones for an even count.
    static TimeSpan Median(IReadOnlyList<TimeSpan> sorted) =>
        sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;

    // Local midnight at the start of a date, as an instant (the zone's offset on that date, so a DST change is honoured).
    static DateTimeOffset StartOf(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    /// <summary>
    /// A span as the stats show it: minutes under an hour, hours under two days, days beyond ("40 min", "5 h", "2.5 d").
    /// </summary>
    public static string Span(TimeSpan span) =>
        span.TotalHours < 1 ? FormattableString.Invariant($"{Math.Max(1, (int)Math.Round(span.TotalMinutes))} min")
        : span.TotalDays < 2 ? FormattableString.Invariant($"{Math.Round(span.TotalHours, span.TotalHours < 10 ? 1 : 0):0.#} h")
        : FormattableString.Invariant($"{Math.Round(span.TotalDays, 1):0.#} d");
}
