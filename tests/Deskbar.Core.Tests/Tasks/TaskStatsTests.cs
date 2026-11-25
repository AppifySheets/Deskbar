using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
using Xunit;
// System.Threading.Tasks (an implicit using) has a TaskStatus too; the alias says which one this file means.
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Tasks;

// The task stats (task 521d2b07; spec docs/superpowers/specs/2026-11-25-task-stats-design.md): the status log written in
// TaskService.Commit, its round trip through the file, the finish time with its fallback, and the burn-up's days.
// The days are taken in UTC here, so a test's dates are the stamps' own dates.
public sealed class TaskStatsTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 25, 15, 0, 0, TimeSpan.Zero);
    static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    static DateTimeOffset Day(int day, int hour = 10) => new(2026, 11, day, hour, 0, 0, TimeSpan.Zero);

    static TaskItem Task(string title, TaskStatus status, DateTimeOffset created, DateTimeOffset? updated = null, DateTimeOffset? raised = null) =>
        new(Guid.NewGuid(), title, Maybe<Guid>.None, status, "", Maybe<string>.None, created, updated ?? created, raised ?? created, [], []);

    // --- the log ---

    [Fact]
    public void The_service_logs_every_status_change_with_both_ends()
    {
        var clock = Day(20);
        var s = new TaskService(new MemoryStore(), () => clock);
        var note = s.AddNote("Soap", Maybe<Guid>.None).Value;
        clock = Day(21);
        s.SetStatus(note.Id, TaskStatus.InProgress);
        clock = Day(22);
        s.Close(note.Id);
        // A change that moves no status logs nothing.
        s.Rename(note.Id, "Big soap");

        Assert.Equal(
            [new StatusChange(Day(21), TaskStatus.Backlog, TaskStatus.InProgress), new StatusChange(Day(22), TaskStatus.InProgress, TaskStatus.Closed)],
            s.Board.Tasks.Single().StatusLog);
    }

    [Fact]
    public void A_new_task_has_no_log_entry() =>
        Assert.Empty(TaskStatusLog.Logged(TaskBoard.Empty, new TaskBoard([Task("New", TaskStatus.Backlog, Day(20))]), Day(20)).Tasks.Single().StatusLog);

    [Fact]
    public void No_moved_status_gives_back_the_same_board()
    {
        var board = new TaskBoard([Task("Same", TaskStatus.Backlog, Day(20))]);
        Assert.Same(board, TaskStatusLog.Logged(board, board, Day(21)));
    }

    [Fact]
    public void The_log_survives_the_file_and_an_empty_one_is_left_out()
    {
        var folder = Path.Combine(Path.GetTempPath(), "deskbar-stats-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JsonTaskStore(folder);
            var logged = Task("Logged", TaskStatus.Closed, Day(20)) with { StatusLog = [new StatusChange(Day(21), TaskStatus.Done, TaskStatus.Closed)] };
            var plain = Task("Plain", TaskStatus.Backlog, Day(20));
            Assert.True(store.Save(new TaskBoard([logged, plain])).IsSuccess);

            var text = File.ReadAllText(Path.Combine(folder, "tasks.json"));
            Assert.Equal(1, CountOf(text, "\"statusLog\""));
            var loaded = store.Load().Value.Tasks;
            Assert.Equal(logged.StatusLog, loaded.Single(t => t.Title == "Logged").StatusLog);
            Assert.Empty(loaded.Single(t => t.Title == "Plain").StatusLog);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    static int CountOf(string text, string part) => (text.Length - text.Replace(part, "").Length) / part.Length;

    // --- the finish time ---

    [Fact]
    public void An_open_task_has_not_finished() =>
        Assert.True(TaskStats.FinishedAt(Task("Open", TaskStatus.InProgress, Day(20))).HasNoValue);

    [Fact]
    public void The_log_gives_the_start_of_the_current_finished_run()
    {
        var task = Task("Done then closed", TaskStatus.Closed, Day(18), updated: Day(24)) with
        {
            StatusLog =
            [
                new StatusChange(Day(19), TaskStatus.Backlog, TaskStatus.Done),
                new StatusChange(Day(20), TaskStatus.Done, TaskStatus.InProgress),
                new StatusChange(Day(22), TaskStatus.InProgress, TaskStatus.Done),
                new StatusChange(Day(23), TaskStatus.Done, TaskStatus.Closed),
            ],
        };
        Assert.Equal(new TaskFinish(Day(22), Estimated: false), TaskStats.FinishedAt(task).Value);
    }

    [Fact]
    public void Without_a_log_a_done_task_finished_when_raised_and_a_closed_one_when_updated()
    {
        Assert.Equal(new TaskFinish(Day(21), true), TaskStats.FinishedAt(Task("Done", TaskStatus.Done, Day(20), updated: Day(23), raised: Day(21))).Value);
        Assert.Equal(new TaskFinish(Day(23), true), TaskStats.FinishedAt(Task("Closed", TaskStatus.Closed, Day(20), updated: Day(23), raised: Day(21))).Value);
    }

    // The log began after the task was Done: its first entry, Done to Closed, says it finished earlier, at its Raised.
    [Fact]
    public void A_log_that_starts_at_done_to_closed_falls_back_on_raised()
    {
        var task = Task("Old done", TaskStatus.Closed, Day(18), updated: Day(24), raised: Day(20)) with
        {
            StatusLog = [new StatusChange(Day(24), TaskStatus.Done, TaskStatus.Closed)],
        };
        Assert.Equal(new TaskFinish(Day(20), true), TaskStats.FinishedAt(task).Value);
    }

    [Fact]
    public void A_finish_is_never_before_the_task_was_made() =>
        Assert.Equal(Day(22), TaskStats.FinishedAt(Task("Odd", TaskStatus.Done, Day(22), raised: Day(21))).Value.At);

    // --- the days ---

    [Fact]
    public void Each_day_lists_what_was_added_and_finished_and_counts_what_stayed_open()
    {
        var a = Task("A", TaskStatus.InProgress, Day(23));
        var b = Task("B", TaskStatus.Closed, Day(23, 9)) with { StatusLog = [new StatusChange(Day(24), TaskStatus.InProgress, TaskStatus.Closed)] };
        var c = Task("C", TaskStatus.Backlog, Day(25, 9));
        var stats = TaskStats.Of(new TaskBoard([a, b, c]), Now, Utc, days: 3);

        Assert.Equal([new DateOnly(2026, 11, 23), new DateOnly(2026, 11, 24), new DateOnly(2026, 11, 25)], stats.Days.Select(d => d.Date));
        // Latest first within a day.
        Assert.Equal(["A", "B"], stats.Days[0].Added.Select(t => t.Title));
        Assert.Equal(["B"], stats.Days[1].Finished.Select(t => t.Title));
        Assert.Equal([2, 1, 2], stats.Days.Select(d => d.OpenAtEnd));
        Assert.Equal(2, stats.OpenNow);
        Assert.False(stats.Estimated);
    }

    [Fact]
    public void The_week_counts_and_the_median_time_to_finish()
    {
        var fast = Task("Fast", TaskStatus.Done, Day(24, 10), raised: Day(24, 12));
        var slow = Task("Slow", TaskStatus.Done, Day(22, 10), raised: Day(24, 10));
        var old = Task("Old", TaskStatus.Backlog, Day(1));
        var stats = TaskStats.Of(new TaskBoard([fast, slow, old]), Now, Utc);

        Assert.Equal(2, stats.AddedThisWeek);
        Assert.Equal(2, stats.FinishedThisWeek);
        // 2 h and 48 h: the mean of the two middle ones.
        Assert.Equal(TimeSpan.FromHours(25), stats.MedianToFinish.Value);
        Assert.True(stats.Estimated);
        Assert.Equal(1, stats.OpenNow);
    }

    [Fact]
    public void Nothing_finished_has_no_median() =>
        Assert.True(TaskStats.Of(new TaskBoard([Task("Open", TaskStatus.Backlog, Day(24))]), Now, Utc).MedianToFinish.HasNoValue);

    // The days are the local days: 23:30 UTC on the 24th is already the 25th at UTC+4.
    [Fact]
    public void The_days_are_local()
    {
        var tbilisi = TimeZoneInfo.CreateCustomTimeZone("UTC+4", TimeSpan.FromHours(4), "UTC+4", "UTC+4");
        var late = Task("Late", TaskStatus.Backlog, new DateTimeOffset(2026, 11, 24, 23, 30, 0, TimeSpan.Zero));
        var stats = TaskStats.Of(new TaskBoard([late]), Now, tbilisi, days: 2);
        Assert.Equal(["Late"], stats.Days[1].Added.Select(t => t.Title));
        Assert.Empty(stats.Days[0].Added);
    }

    // The panel's span starts at the oldest task's day, between 7 and 14 days.
    [Fact]
    public void The_panel_shows_from_the_first_tasks_day_between_7_and_14_days()
    {
        Assert.Equal(7, TaskStats.DaysToShow(TaskBoard.Empty, Now, Utc));
        Assert.Equal(7, TaskStats.DaysToShow(new TaskBoard([Task("New", TaskStatus.Backlog, Day(24))]), Now, Utc));
        Assert.Equal(10, TaskStats.DaysToShow(new TaskBoard([Task("Old", TaskStatus.Closed, Day(16)), Task("New", TaskStatus.Backlog, Day(24))]), Now, Utc));
        Assert.Equal(14, TaskStats.DaysToShow(new TaskBoard([Task("Ancient", TaskStatus.Closed, Day(1))]), Now, Utc));
    }

    [Theory]
    [InlineData(10, "10 min")]
    [InlineData(90, "1.5 h")]
    [InlineData(25 * 60, "25 h")]
    [InlineData(60 * 60, "2.5 d")]
    public void Spans_read_in_the_unit_that_fits(int minutes, string expected) =>
        Assert.Equal(expected, TaskStats.Span(TimeSpan.FromMinutes(minutes)));

    // --- the click-through's filter (TaskViews.Lists and FilterListing, for TasksBoard.OpenFromStats) ---

    [Fact]
    public void A_filter_lists_a_task_by_its_status_and_its_snooze()
    {
        var done = Task("Done", TaskStatus.Done, Day(20));
        var closed = Task("Closed", TaskStatus.Closed, Day(20));
        var snoozed = Task("Snoozed", TaskStatus.Backlog, Day(20)) with { SnoozedUntil = Day(26) };

        Assert.True(TaskViews.Lists(TaskFilter.Open, done));
        Assert.True(TaskViews.Lists(TaskFilter.Done, done));
        Assert.False(TaskViews.Lists(TaskFilter.Backlog, done));
        Assert.False(TaskViews.Lists(TaskFilter.Open, closed));
        Assert.True(TaskViews.Lists(TaskFilter.Closed, closed));
        Assert.False(TaskViews.Lists(TaskFilter.Open, snoozed));
        Assert.True(TaskViews.Lists(TaskFilter.Snoozed, snoozed));
        Assert.False(TaskViews.Lists(TaskFilter.Snoozed, done));

        Assert.Equal(TaskFilter.Open, TaskViews.FilterListing(done));
        Assert.Equal(TaskFilter.Closed, TaskViews.FilterListing(closed));
        Assert.Equal(TaskFilter.Snoozed, TaskViews.FilterListing(snoozed));
    }

    sealed class MemoryStore : ITaskStore
    {
        TaskBoard saved = TaskBoard.Empty;
        public Result<TaskBoard> Load() => saved;
        public Result Save(TaskBoard board)
        {
            saved = board;
            return Result.Success();
        }
    }
}
