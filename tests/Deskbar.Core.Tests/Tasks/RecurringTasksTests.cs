using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
// System.Threading.Tasks (an implicit using) has a TaskStatus too; the alias says which one this file means.
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Tasks;

// Recurring tasks (task 0b78269e, spec docs/superpowers/specs/2026-11-26-recurring-tasks-design.md). This section pins
// the two fields a task gains, Repeat and NextRun, how they are written to tasks.json and how tolerantly they are read
// back (a hand-edited or newer-build value must never fail the whole file), and IsBetweenRuns.
public sealed class RecurringTasksTests : IDisposable
{
    static readonly DateTimeOffset At = new(2026, 11, 26, 10, 0, 0, TimeSpan.Zero);
    static readonly Guid Ws = Guid.Parse("11111111-1111-1111-1111-111111111111");

    readonly string dir = Path.Combine(Path.GetTempPath(), "deskbar-recurring-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    static TaskItem Note(Func<TaskItem, TaskItem>? change = null) =>
        (change ?? (t => t))(TaskBoard.Empty.AddNote("check the deploy", Ws, At, Guid.NewGuid()).Value.Board.Tasks.Single());

    // --- the file ------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_repeat_and_its_next_run_survive_a_save_and_load()
    {
        var schedule = TaskRepeats.Parse("every 1h 09:00-19:00").Value;
        var next = new DateTimeOffset(2026, 11, 26, 11, 0, 0, TimeSpan.FromHours(4));
        var task = Note(t => t with
        {
            Repeat = new TaskRepeat(schedule, "sonnet", @"C:\repos\TaskSpaces"),
            NextRun = next,
        });
        var store = new JsonTaskStore(dir);

        Assert.True(store.Save(new TaskBoard([task])).IsSuccess);
        var loaded = store.Load().Value.Tasks.Single();

        Assert.True(loaded.Repeat.HasValue);
        Assert.Equal(schedule.Text, loaded.Repeat.Value.Schedule.Text);
        Assert.Equal("sonnet", loaded.Repeat.Value.Model);
        Assert.Equal(@"C:\repos\TaskSpaces", loaded.Repeat.Value.Folder);
        Assert.Equal(next, loaded.NextRun.Value);
        Assert.Equal(TimeSpan.Zero, loaded.NextRun.Value.Offset);
    }

    [Fact]
    public void A_task_without_a_repeat_writes_neither_field()
    {
        var store = new JsonTaskStore(dir);

        store.Save(new TaskBoard([Note()]));
        var text = File.ReadAllText(Path.Combine(dir, "tasks.json"));

        Assert.DoesNotContain("\"repeat\"", text);
        Assert.DoesNotContain("\"nextRun\"", text);
    }

    [Theory]
    [InlineData("\"repeat\":42")]
    [InlineData("\"repeat\":{\"schedule\":\"every 2m\",\"model\":\"sonnet\",\"folder\":\"C:\\\\r\"}")]
    [InlineData("\"repeat\":{\"schedule\":\"daily 09:00\"}")]
    [InlineData("\"nextRun\":\"soon\"")]
    public void A_bad_repeat_reads_as_none_and_never_fails_the_file(string field)
    {
        Directory.CreateDirectory(dir);
        var id = Guid.NewGuid();
        File.WriteAllText(Path.Combine(dir, "tasks.json"),
            $$"""{"version":2,"tasks":[{"id":"{{id}}","title":"t","status":"Backlog","created":"2026-11-26T10:00:00Z","updatedAt":"2026-11-26T10:00:00Z",{{field}}}]}""");

        var loaded = new JsonTaskStore(dir).Load();

        Assert.True(loaded.IsSuccess);
        var task = loaded.Value.Tasks.Single();
        Assert.Equal("t", task.Title);
        if (field.StartsWith("\"nextRun\"")) Assert.False(task.NextRun.HasValue);
        else Assert.False(task.Repeat.HasValue);
    }

    // --- between runs --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(TaskStatus.Backlog, true)]
    [InlineData(TaskStatus.InProgress, false)]
    [InlineData(TaskStatus.WaitingOnYou, false)]
    [InlineData(TaskStatus.Done, true)]
    [InlineData(TaskStatus.Closed, false)]
    public void Between_runs_is_an_open_backlog_or_done_task_with_a_repeat(TaskStatus status, bool expected)
    {
        var repeat = new TaskRepeat(TaskRepeats.Parse("daily 09:00").Value, "sonnet", @"C:\r");

        Assert.Equal(expected, Note(t => t with { Status = status, Repeat = repeat }).IsBetweenRuns);
        Assert.False(Note(t => t with { Status = status }).IsBetweenRuns);
    }

    // --- the board's rules (Task 3) ------------------------------------------------------------------------------------
    // A fixed zone (+04:00) and a clock of Wednesday 14:20 local; `every 1h` unless a test says otherwise. Each test that
    // moves the status and the snooze together pins the two-step order, because TaskBoard.Woken (the rule that drops a
    // snooze when the status changes in the same step) would otherwise eat the snooze.

    static readonly TimeZoneInfo Plus4 = TimeZoneInfo.CreateCustomTimeZone("T+4", TimeSpan.FromHours(4), "T+4", "T+4");
    static readonly DateTimeOffset Now = new(2026, 11, 25, 14, 20, 0, TimeSpan.FromHours(4));
    static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    static TaskRepeat Repeat(string schedule = "every 1h") => new(TaskRepeats.Parse(schedule).Value, "sonnet", @"C:\repos\TaskSpaces");

    // A Backlog note and its id, on a board.
    static (TaskBoard Board, Guid Id) Fresh()
    {
        var made = TaskBoard.Empty.AddNote("check the deploy", Ws, Now, Guid.NewGuid()).Value;
        return (made.Board, made.Task.Id);
    }

    static TaskItem Of(TaskBoard board, Guid id) => board.Tasks.Single(t => t.Id == id);

    static TaskSession Session(string id = "s1") => new(id, @"C:\repos\TaskSpaces", Now, Maybe<DateTimeOffset>.None);

    [Fact]
    public void SetRepeat_snoozes_a_backlog_note_until_its_next_run_and_gives_it_to_claude()
    {
        var (board, id) = Fresh();

        var task = Of(board.SetRepeat(id, Repeat(), Now, Plus4).Value, id);

        Assert.Equal(Now + Hour, task.NextRun.Value);
        Assert.Equal(Now + Hour, task.SnoozedUntil.Value);
        Assert.Equal(Assignee.Claude, task.Assignee);
        Assert.Equal(TaskStatus.Backlog, task.Status);
    }

    [Fact]
    public void SetRepeat_on_a_task_in_progress_sets_the_next_run_and_does_not_snooze_it()
    {
        var (board, id) = Fresh();
        board = board.Join(id, Session(), Now).Value;

        var task = Of(board.SetRepeat(id, Repeat(), Now, Plus4).Value, id);

        Assert.Equal(TaskStatus.InProgress, task.Status);
        Assert.Equal(Now + Hour, task.NextRun.Value);
        Assert.False(task.IsSnoozed);
    }

    [Fact]
    public void SetRepeat_refuses_a_closed_task_and_changes_nothing()
    {
        var (board, id) = Fresh();
        board = board.Close(id, Now).Value;

        var result = board.SetRepeat(id, Repeat(), Now, Plus4);

        Assert.True(result.IsFailure);
        Assert.False(Of(board, id).Repeat.HasValue);
        Assert.False(Of(board, id).NextRun.HasValue);
    }

    [Theory]
    [InlineData(RecurringRuns.FolderGoneQuestion)]
    [InlineData(RecurringRuns.StartFailedQuestion)]
    public void SetRepeat_answers_a_repeat_question(string question)
    {
        var (board, id) = Fresh();
        board = board.Ask(id, question, Now).Value;

        var task = Of(board.SetRepeat(id, Repeat(), Now, Plus4).Value, id);

        Assert.Equal(TaskStatus.Backlog, task.Status);
        Assert.False(task.Question.HasValue);
        Assert.Equal(Now + Hour, task.SnoozedUntil.Value);
    }

    [Fact]
    public void SetRepeat_leaves_a_question_that_is_not_about_the_repeat()
    {
        var (board, id) = Fresh();
        board = board.Ask(id, "which branch?", Now).Value;

        var task = Of(board.SetRepeat(id, Repeat(), Now, Plus4).Value, id);

        Assert.Equal(TaskStatus.WaitingOnYou, task.Status);
        Assert.Equal("which branch?", task.Question.Value);
        Assert.False(task.IsSnoozed);
    }

    [Fact]
    public void ClearRepeat_ends_the_repeat_and_the_snooze_it_made_but_not_a_later_snooze_of_petres()
    {
        var (board, id) = Fresh();
        // In progress, so clearing only drops the schedule (a task between runs ends as Done, which clears any snooze).
        var set = board.Join(id, Session(), Now).Value.SetRepeat(id, Repeat(), Now, Plus4).Value;

        var same = Of(set.Snooze(id, Now + Hour, Now).Value.ClearRepeat(id, Now).Value, id);
        Assert.False(same.Repeat.HasValue);
        Assert.False(same.NextRun.HasValue);
        Assert.False(same.IsSnoozed);

        var later = Now + TimeSpan.FromDays(2);
        var petres = Of(set.Snooze(id, later, Now).Value.ClearRepeat(id, Now).Value, id);
        Assert.False(petres.Repeat.HasValue);
        Assert.Equal(later, petres.SnoozedUntil.Value);
    }

    [Fact]
    public void Done_on_a_recurring_task_is_the_end_of_a_run()
    {
        var (board, id) = Fresh();
        board = board.SetRepeat(id, Repeat(), Now, Plus4).Value.Join(id, Session(), Now).Value;
        var raised = Of(board, id).Raised;
        var later = Now + TimeSpan.FromMinutes(5);

        var task = Of(board.MarkDone(id, later, Plus4).Value, id);

        Assert.Equal(TaskStatus.Backlog, task.Status);
        Assert.Equal(raised, task.Raised);
        Assert.False(task.Question.HasValue);
        Assert.Equal(Assignee.Claude, task.Assignee);
        Assert.Equal(later + Hour, task.NextRun.Value);
        Assert.Equal(later + Hour, task.SnoozedUntil.Value);
    }

    [Fact]
    public void Done_on_a_plain_task_is_unchanged()
    {
        var (board, id) = Fresh();

        var task = Of(board.MarkDone(id, Now).Value, id);

        Assert.Equal(TaskStatus.Done, task.Status);
        // Done raises it (stamps Raised); the first Done task's pin is 0, so Raised is what shows the old path ran.
        Assert.Equal(Now, task.Raised);
        Assert.Equal(Assignee.Me, task.Assignee);
    }

    [Fact]
    public void Close_keeps_the_repeat_and_reopen_resumes_it_from_now()
    {
        var (board, id) = Fresh();
        board = board.SetRepeat(id, Repeat(), Now, Plus4).Value.Close(id, Now).Value;
        Assert.True(Of(board, id).Repeat.HasValue);
        var reopened = Now + TimeSpan.FromDays(2);

        var task = Of(board.Reopen(id, reopened, Plus4).Value, id);

        Assert.True(task.IsOpen);
        Assert.Equal(reopened + Hour, task.NextRun.Value);
        Assert.Equal(reopened + Hour, task.SnoozedUntil.Value);
    }

    [Fact]
    public void Wake_now_on_a_task_between_runs_makes_its_run_due_now()
    {
        var (board, id) = Fresh();
        board = board.SetRepeat(id, Repeat(), Now, Plus4).Value;
        var later = Now + TimeSpan.FromMinutes(10);

        var task = Of(board.Wake(id, later).Value, id);

        Assert.Equal(later, task.NextRun.Value);
        Assert.False(task.IsSnoozed);
    }

    [Fact]
    public void RunStarted_wakes_the_task_and_sets_the_following_occurrence()
    {
        var (board, id) = Fresh();
        board = board.SetRepeat(id, Repeat(), Now, Plus4).Value;
        var later = Now + Hour;

        var task = Of(board.RunStarted(id, later, Plus4).Value, id);

        Assert.Equal(later + Hour, task.NextRun.Value);
        Assert.False(task.IsSnoozed);
        Assert.Equal(TaskStatus.Backlog, task.Status);
    }

    [Fact]
    public void Rescheduled_snoozes_only_between_runs()
    {
        var (board, id) = Fresh();
        board = board.SetRepeat(id, Repeat(), Now, Plus4).Value;
        var later = Now + TimeSpan.FromMinutes(30);

        var asleep = Of(board.Rescheduled(id, later, Plus4).Value, id);
        Assert.Equal(later + Hour, asleep.NextRun.Value);
        Assert.Equal(later + Hour, asleep.SnoozedUntil.Value);

        // WaitingOnYou with a question that is not about the repeat: the new NextRun, and still visible.
        var waiting = board.Wake(id, Now).Value.Ask(id, "which branch?", Now).Value;
        var shown = Of(waiting.Rescheduled(id, later, Plus4).Value, id);
        Assert.Equal(later + Hour, shown.NextRun.Value);
        Assert.False(shown.IsSnoozed);
    }

    // A zone that is NOT the machine's: TimeZoneInfo.Local here is +04:00, the same as Plus4, so a test in Plus4 could not
    // tell a service that passes its zone from one that falls back to Local. +13:00 can, with a `daily` schedule.
    static readonly TimeZoneInfo Plus13 = TimeZoneInfo.CreateCustomTimeZone("T+13", TimeSpan.FromHours(13), "T+13", "T+13");

    // 09:00 on Thursday the 26th at +13:00, the first daily 09:00 after the Now above (23:20 on the 25th there).
    static readonly DateTimeOffset NextDaily09In13 = new(2026, 11, 26, 9, 0, 0, TimeSpan.FromHours(13));

    TaskService ServiceIn(TaskBoard board)
    {
        var store = new JsonTaskStore(dir);
        store.Save(board);
        var service = new TaskService(store, () => Now, zone: Plus13);
        service.Load();
        return service;
    }

    [Fact]
    public void The_service_uses_its_zone()
    {
        Assert.NotEqual(Plus13.BaseUtcOffset, TimeZoneInfo.Local.GetUtcOffset(Now));
        var (board, id) = Fresh();
        var service = ServiceIn(board);

        Assert.True(service.SetRepeat(id, Repeat("daily 09:00")).IsSuccess);

        var next = Of(service.Board, id).NextRun.Value;
        Assert.Equal(NextDaily09In13.ToUniversalTime(), next);
        Assert.Equal(next, Of(service.Board, id).SnoozedUntil.Value);
    }

    [Fact]
    public void The_service_passes_its_zone_to_done()
    {
        var (board, id) = Fresh();
        board = board.SetRepeat(id, Repeat("daily 09:00"), Now, Plus13).Value.Join(id, Session(), Now).Value;
        var service = ServiceIn(board);

        Assert.True(service.MarkDone(id).IsSuccess);

        Assert.Equal(NextDaily09In13.ToUniversalTime(), Of(service.Board, id).NextRun.Value);
    }

    // --- every way out of Closed resumes the repeat from now (review fix 2) -----------------------------------------------

    static TaskBoard ClosedRecurring(out Guid id)
    {
        var (board, made) = Fresh();
        id = made;
        return board.SetRepeat(id, Repeat(), Now, Plus4).Value.Close(id, Now).Value;
    }

    static readonly DateTimeOffset TwoDaysOn = Now + TimeSpan.FromDays(2);

    static void AssertResumed(TaskItem task)
    {
        Assert.True(task.IsOpen);
        Assert.Equal(TwoDaysOn + Hour, task.NextRun.Value);
        Assert.Equal(TwoDaysOn + Hour, task.SnoozedUntil.Value);
    }

    [Fact]
    public void A_status_set_by_hand_on_a_closed_recurring_task_resumes_it_from_now()
    {
        var board = ClosedRecurring(out var id);

        AssertResumed(Of(board.SetStatus(id, TaskStatus.Backlog, TwoDaysOn, Plus4).Value, id));
    }

    [Fact]
    public void Undo_close_on_a_recurring_task_resumes_it_from_now()
    {
        var board = ClosedRecurring(out var id);

        AssertResumed(Of(board.UndoClose(id, TaskStatus.Backlog, Maybe<string>.None, TwoDaysOn, Plus4).Value, id));
    }

    [Fact]
    public void A_recurring_parent_brought_back_by_its_subtask_resumes_from_now()
    {
        var (board, parent) = Fresh();
        var child = Guid.NewGuid();
        board = board.SetRepeat(parent, Repeat(), Now, Plus4).Value;
        var added = board.AddNote("child", Ws, Now, child, Quadrant.IV, parent).Value;
        board = added.Board.Close(added.Task.Id, Now).Value.Close(parent, Now).Value;

        var back = board.SetStatus(added.Task.Id, TaskStatus.Backlog, TwoDaysOn, Plus4).Value;

        AssertResumed(Of(back, parent));
    }

    [Fact]
    public void A_status_set_by_hand_on_an_open_recurring_task_does_not_put_it_to_sleep()
    {
        var (board, id) = Fresh();
        board = board.SetRepeat(id, Repeat(), Now, Plus4).Value.Join(id, Session(), Now).Value;

        var task = Of(board.SetStatus(id, TaskStatus.Backlog, Now, Plus4).Value, id);

        Assert.False(task.IsSnoozed);
    }

    [Fact]
    public void ClearRepeat_between_runs_ends_the_job_as_done()
    {
        var (board, id) = Fresh();
        board = board.SetRepeat(id, Repeat(), Now, Plus4).Value;
        var later = Now + TimeSpan.FromMinutes(5);

        var task = Of(board.ClearRepeat(id, later).Value, id);

        Assert.Equal(TaskStatus.Done, task.Status);
        Assert.Equal(Assignee.Me, task.Assignee);
        Assert.Equal(later, task.Raised);
        Assert.False(task.IsSnoozed);
        Assert.False(task.Question.HasValue);
        Assert.False(task.Repeat.HasValue);
        Assert.False(task.NextRun.HasValue);
    }

    [Fact]
    public void ClearRepeat_in_progress_only_drops_the_schedule()
    {
        var (board, id) = Fresh();
        board = board.SetRepeat(id, Repeat(), Now, Plus4).Value.Join(id, Session(), Now).Value;
        var before = Of(board, id);

        var task = Of(board.ClearRepeat(id, Now).Value, id);

        Assert.Equal(TaskStatus.InProgress, task.Status);
        Assert.Equal(before.Assignee, task.Assignee);
        Assert.False(task.Repeat.HasValue);
        Assert.False(task.NextRun.HasValue);
    }
}
