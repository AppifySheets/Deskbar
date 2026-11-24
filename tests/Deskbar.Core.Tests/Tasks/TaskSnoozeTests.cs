using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
// System.Threading.Tasks (an implicit using) has a TaskStatus too; the alias says which one this file means.
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Tasks;

// Snoozing a task (task 6da8d7eb, 2026-11-25): Petre's "snooze button, which will hide it from view for a while". These pin
// the rules: what a snooze hides and what it keeps (TaskBoard.Snooze, TaskViews), what ends one early (TaskBoard.Woken),
// the sweep that ends it on time (WakeDue), the four lengths and the pipe's lengths (TaskSnooze), and the file (JsonTaskStore).
public sealed class TaskSnoozeTests : IDisposable
{
    DateTimeOffset clock = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);
    static readonly Guid ws = Guid.Parse("11111111-1111-1111-1111-111111111111");
    TaskBoard board = TaskBoard.Empty;

    readonly string dir = Path.Combine(Path.GetTempPath(), "deskbar-snooze-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    Guid Note(string title)
    {
        var id = Guid.NewGuid();
        board = board.AddNote(title, ws, clock, id).Value.Board;
        clock = clock.AddMinutes(1);
        return id;
    }

    Guid Claudes(string title, string session)
    {
        var id = Guid.NewGuid();
        board = board.Create(title, ws, new TaskSession(session, @"C:\repos\TaskSpaces", clock, Maybe<DateTimeOffset>.None), clock, id).Value.Board;
        clock = clock.AddMinutes(1);
        return id;
    }

    TaskItem Get(Guid id) => board.Tasks.Single(t => t.Id == id);

    void Snooze(Guid id, TimeSpan length) => board = board.Snooze(id, clock + length, clock).Value;

    IReadOnlyList<string> Shown(TaskFilter filter) =>
        TaskViews.Grouped(board, filter, TaskWorkspaceFilter.All).Select(g => $"{g.Kind}: {string.Join(", ", g.Tasks.Select(t => t.Title))}").ToList();

    // --- what a snooze hides, and what it keeps -----------------------------------------------------------------------

    [Fact]
    public void A_snoozed_task_leaves_every_view_but_the_snoozed_filter_and_keeps_its_place()
    {
        var milk = Note("milk");
        Note("bread");
        board = board.SetFlag(milk, TaskFlag.Important, true, clock).Value;
        var rankBefore = Get(milk).Rank;

        Snooze(milk, TimeSpan.FromHours(1));

        Assert.Equal(["IV: bread"], Shown(TaskFilter.Open));
        Assert.Equal(["IV: bread"], Shown(TaskFilter.Backlog));
        Assert.Equal(["Snoozed: milk"], Shown(TaskFilter.Snoozed));
        Assert.All(TaskViews.Matrix(board, TaskFilter.Open, TaskWorkspaceFilter.All), cell => Assert.DoesNotContain(cell.Tasks, t => t.Id == milk));
        Assert.Equal(1, TaskViews.Count(board, TaskFilter.Open, TaskWorkspaceFilter.All));
        Assert.Equal(1, TaskViews.Count(board, TaskFilter.Snoozed, TaskWorkspaceFilter.All));
        // Nothing else about it moved: status, flags and rank are as they were, so it comes back where it was.
        Assert.Equal(TaskStatus.Backlog, Get(milk).Status);
        Assert.True(Get(milk).Important);
        Assert.Equal(rankBefore, Get(milk).Rank);
    }

    [Fact]
    public void The_snoozed_filter_lists_the_first_to_come_back_on_top()
    {
        var later = Note("later");
        var sooner = Note("sooner");
        Snooze(later, TimeSpan.FromDays(1));
        Snooze(sooner, TimeSpan.FromHours(1));

        Assert.Equal(["Snoozed: sooner, later"], Shown(TaskFilter.Snoozed));
        Assert.Equal("Snoozed", TaskViews.GroupName(TaskGroupKind.Snoozed));
    }

    [Fact]
    public void Snooze_refuses_a_closed_task_a_task_waiting_on_you_and_a_time_already_past()
    {
        var closed = Note("closed");
        board = board.Close(closed, clock).Value;
        var asked = Claudes("asked", "s1");
        board = board.Ask(asked, "merge?", clock).Value;
        var note = Note("note");

        Assert.True(board.Snooze(closed, clock.AddHours(1), clock).IsFailure);
        Assert.True(board.Snooze(asked, clock.AddHours(1), clock).IsFailure);
        Assert.True(board.Snooze(note, clock, clock).IsFailure);
    }

    [Fact]
    public void Snoozing_again_to_the_same_time_changes_nothing()
    {
        var note = Note("note");
        var until = clock.AddHours(1);
        board = board.Snooze(note, until, clock).Value;

        Assert.Same(board, board.Snooze(note, until, clock).Value);
    }

    // --- what ends a snooze ----------------------------------------------------------------------------------------------

    [Fact]
    public void WakeDue_ends_the_snoozes_whose_time_has_come_and_no_other()
    {
        var hour = Note("hour");
        var day = Note("day");
        Snooze(hour, TimeSpan.FromHours(1));
        Snooze(day, TimeSpan.FromDays(1));

        Assert.Same(board, board.WakeDue(clock.AddMinutes(30)));
        board = board.WakeDue(clock.AddHours(1));

        Assert.False(Get(hour).IsSnoozed);
        Assert.True(Get(day).IsSnoozed);
        Assert.Equal(["IV: hour"], Shown(TaskFilter.Open));
    }

    [Fact]
    public void Wake_brings_a_task_back_now_and_waking_an_awake_one_changes_nothing()
    {
        var note = Note("note");
        Assert.Same(board, board.Wake(note, clock).Value);

        Snooze(note, TimeSpan.FromDays(3));
        board = board.Wake(note, clock).Value;

        Assert.False(Get(note).IsSnoozed);
    }

    // A snooze hides a task "for a while", never a question: a session asking, or finishing, brings it back at once.
    [Fact]
    public void A_session_asking_or_finishing_wakes_its_task()
    {
        var asks = Claudes("asks", "s1");
        var finishes = Claudes("finishes", "s2");
        Snooze(asks, TimeSpan.FromDays(1));
        Snooze(finishes, TimeSpan.FromDays(1));

        board = board.Ask(asks, "merge?", clock).Value;
        board = board.MarkDone(finishes, clock).Value;

        Assert.False(Get(asks).IsSnoozed);
        Assert.False(Get(finishes).IsSnoozed);
    }

    [Fact]
    public void A_status_change_wakes_a_task_but_a_status_line_a_comment_or_a_flag_does_not()
    {
        var note = Note("note");
        var work = Claudes("work", "s1");
        Snooze(note, TimeSpan.FromDays(1));
        Snooze(work, TimeSpan.FromDays(1));

        board = board.SetStatusLine(work, "waiting for CI", clock).Value;
        board = board.Comment(work, "s1", "a note", clock, Guid.NewGuid()).Value;
        board = board.SetFlag(work, TaskFlag.Urgent, true, clock).Value;
        Assert.True(Get(work).IsSnoozed);

        // A session joining his note moves it from the backlog to in progress.
        board = board.Join(note, new TaskSession("s9", @"C:\repos\TaskSpaces", clock, Maybe<DateTimeOffset>.None), clock).Value;
        Assert.False(Get(note).IsSnoozed);
    }

    // --- the lengths ---------------------------------------------------------------------------------------------------

    static readonly TimeSpan Tbilisi = TimeSpan.FromHours(4);
    static TimeSpan Fixed(DateTime _) => Tbilisi;

    // Wednesday 25 November 2026, 14:20 local.
    static readonly DateTimeOffset Wednesday = new(2026, 11, 25, 14, 20, 0, Tbilisi);

    [Fact]
    public void The_four_lengths_end_an_hour_on_three_hours_on_tomorrow_at_nine_and_monday_at_nine()
    {
        Assert.Equal(Wednesday.AddHours(1), TaskSnooze.Until(SnoozeChoice.Hour, Wednesday, Fixed));
        Assert.Equal(Wednesday.AddHours(3), TaskSnooze.Until(SnoozeChoice.ThreeHours, Wednesday, Fixed));
        Assert.Equal(new DateTimeOffset(2026, 11, 26, 9, 0, 0, Tbilisi), TaskSnooze.Until(SnoozeChoice.Tomorrow, Wednesday, Fixed));
        Assert.Equal(new DateTimeOffset(2026, 11, 30, 9, 0, 0, Tbilisi), TaskSnooze.Until(SnoozeChoice.Monday, Wednesday, Fixed));
    }

    // "Monday" is always the NEXT Monday: from a Sunday that is tomorrow, from a Monday a whole week on.
    [Theory]
    [InlineData(29, 30)] // Sunday 29 November -> Monday 30
    [InlineData(30, 7)]  // Monday 30 November -> Monday 7 December
    public void Monday_is_the_next_monday(int today, int monday)
    {
        var now = new DateTimeOffset(2026, 11, today, 12, 0, 0, Tbilisi);
        var month = today == 30 && monday == 7 ? 12 : 11;

        Assert.Equal(new DateTimeOffset(2026, month, monday, 9, 0, 0, Tbilisi), TaskSnooze.Until(SnoozeChoice.Monday, now, Fixed));
    }

    // 09:00 on the clock, with the offset in force THEN: a daylight saving change overnight is honoured.
    [Fact]
    public void Tomorrow_at_nine_takes_the_offset_in_force_tomorrow()
    {
        var now = new DateTimeOffset(2026, 3, 28, 20, 0, 0, TimeSpan.FromHours(1));
        TimeSpan Summer(DateTime wall) => wall >= new DateTime(2026, 3, 29, 2, 0, 0) ? TimeSpan.FromHours(2) : TimeSpan.FromHours(1);

        Assert.Equal(new DateTimeOffset(2026, 3, 29, 9, 0, 0, TimeSpan.FromHours(2)), TaskSnooze.Until(SnoozeChoice.Tomorrow, now, Summer));
    }

    [Fact]
    public void The_menu_says_when_the_clock_choices_end()
    {
        Assert.Equal(["1 hour", "3 hours", "Tomorrow, Thu 09:00", "Monday, 30 Nov 09:00"],
            TaskSnooze.Choices.Select(c => TaskSnooze.Label(c, Wednesday, Fixed)));
    }

    [Theory]
    [InlineData(0, 15, 40, "until 15:40")]
    [InlineData(1, 9, 0, "until tomorrow 09:00")]
    [InlineData(5, 9, 0, "until Mon 09:00")]
    [InlineData(13, 9, 0, "until 8 Dec 09:00")]
    public void A_snoozed_line_says_when_it_comes_back(int days, int hour, int minute, string expected) =>
        Assert.Equal(expected, TaskSnooze.UntilText(new DateTimeOffset(Wednesday.Date.AddDays(days).AddHours(hour).AddMinutes(minute), Tbilisi), Wednesday));

    [Theory]
    [InlineData("1h", 60)]
    [InlineData("3H", 180)]
    [InlineData("45m", 45)]
    [InlineData("2d", 2880)]
    public void The_pipe_takes_a_plain_length(string text, int minutes) =>
        Assert.Equal(Wednesday.AddMinutes(minutes), TaskSnooze.Parse(text, Wednesday, Fixed).Value);

    [Fact]
    public void The_pipe_takes_tomorrow_and_monday_by_name_and_refuses_nonsense()
    {
        Assert.Equal(TaskSnooze.Until(SnoozeChoice.Tomorrow, Wednesday, Fixed), TaskSnooze.Parse("tomorrow", Wednesday, Fixed).Value);
        Assert.Equal(TaskSnooze.Until(SnoozeChoice.Monday, Wednesday, Fixed), TaskSnooze.Parse("Monday", Wednesday, Fixed).Value);
        Assert.True(TaskSnooze.Parse("later", Wednesday, Fixed).IsFailure);
        Assert.True(TaskSnooze.Parse("0h", Wednesday, Fixed).IsFailure);
        Assert.True(TaskSnooze.Parse("400d", Wednesday, Fixed).IsFailure);
    }

    // --- the service and the file --------------------------------------------------------------------------------------

    [Fact]
    public void The_service_saves_a_snooze_and_its_sweep_saves_only_when_something_came_due()
    {
        var store = new JsonTaskStore(dir);
        var service = new TaskService(store, () => clock);
        var id = service.AddNote("milk", ws).Value.Id;
        var pulses = 0;
        using var _ = service.Changed.Subscribe(_ => pulses++);

        Assert.True(service.Snooze(id, clock.AddHours(1)).IsSuccess);
        Assert.Equal(clock.AddHours(1), store.Load().Value.Tasks.Single().SnoozedUntil.Value);

        service.WakeDue();
        Assert.Equal(1, pulses);

        clock = clock.AddHours(1);
        service.WakeDue();
        Assert.Equal(2, pulses);
        Assert.False(store.Load().Value.Tasks.Single().IsSnoozed);
    }

    [Fact]
    public void A_file_without_the_field_reads_as_awake_and_a_wrong_typed_one_does_not_fail_the_file()
    {
        Directory.CreateDirectory(dir);
        var id = Guid.NewGuid();
        File.WriteAllText(Path.Combine(dir, "tasks.json"),
            $$"""{"version":2,"tasks":[{"id":"{{id}}","title":"t","status":"Backlog","created":"2026-11-25T10:00:00Z","updatedAt":"2026-11-25T10:00:00Z","snoozedUntil":42}]}""");

        var loaded = new JsonTaskStore(dir).Load();

        Assert.True(loaded.IsSuccess);
        Assert.False(loaded.Value.Tasks.Single().IsSnoozed);
    }
}
