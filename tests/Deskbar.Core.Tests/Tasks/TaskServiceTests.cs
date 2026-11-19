using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
using Xunit;

namespace Deskbar.Core.Tests.Tasks;

// TaskService: ticking, coming back and coming due. Design: docs/superpowers/specs/2026-11-19-tasks-in-the-bar-design.md
// ("Coming back ends the timer"). The clock is a captured field the test moves, so nothing here waits.
public sealed class TaskServiceTests
{
    // A noon start keeps "today" and "yesterday" unambiguous in any local time zone.
    DateTimeOffset clock = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
    readonly FakeTaskStore store = new();
    readonly Guid ws = Guid.NewGuid();
    readonly Guid other = Guid.NewGuid();

    TaskService NewService() => new(store, () => clock);

    void At(TimeSpan after) => clock += after;

    static Maybe<Guid> On(Guid id) => id;

    TaskItem Only(TaskService s, string title) => s.Board.Tasks.Single(t => t.Title == title);

    [Fact]
    public void The_first_tick_is_not_an_arrival()
    {
        var s = NewService();
        s.Park("Review", On(ws), TimeSpan.FromMinutes(5));
        s.Tick(On(ws));
        At(TimeSpan.FromSeconds(10));
        s.Tick(On(ws));
        Assert.Equal(TaskState.Running, Only(s, "Review").StateAt(clock));
    }

    [Fact]
    public void Arriving_and_staying_two_seconds_ends_the_timer()
    {
        var s = NewService();
        s.Park("Review", On(ws), TimeSpan.FromMinutes(5));
        s.Tick(On(other));
        s.Tick(On(ws));
        At(TimeSpan.FromSeconds(1.9));
        s.Tick(On(ws));
        Assert.Equal(TaskState.Running, Only(s, "Review").StateAt(clock));
        At(TimeSpan.FromSeconds(0.1));
        s.Tick(On(ws));
        var task = Only(s, "Review");
        Assert.Equal(TaskState.Open, task.StateAt(clock));
        Assert.Equal(TimerEnd.CameBack, task.History.Last().How);
    }

    [Fact]
    public void Passing_through_does_not_end_the_timer()
    {
        var s = NewService();
        s.Park("Review", On(ws), TimeSpan.FromMinutes(5));
        s.Tick(On(other));
        s.Tick(On(ws));
        At(TimeSpan.FromSeconds(1));
        s.Tick(On(other));
        At(TimeSpan.FromSeconds(9));
        s.Tick(On(other));
        Assert.Equal(TaskState.Running, Only(s, "Review").StateAt(clock));
    }

    [Fact]
    public void A_second_arrival_restarts_the_dwell()
    {
        var s = NewService();
        s.Park("Review", On(ws), TimeSpan.FromMinutes(5));
        s.Tick(On(other));
        s.Tick(On(ws));
        At(TimeSpan.FromSeconds(1));
        s.Tick(On(other));
        At(TimeSpan.FromSeconds(0.5));
        s.Tick(On(ws));
        At(TimeSpan.FromSeconds(1.5));
        s.Tick(On(ws));
        Assert.Equal(TaskState.Running, Only(s, "Review").StateAt(clock));
        At(TimeSpan.FromSeconds(0.5));
        s.Tick(On(ws));
        Assert.Equal(TaskState.Open, Only(s, "Review").StateAt(clock));
    }

    [Fact]
    public void Due_while_present_stays_due_until_an_arrival()
    {
        var s = NewService();
        s.Park("Review", On(ws), TimeSpan.FromMinutes(1));
        s.Tick(On(ws));
        At(TimeSpan.FromMinutes(2));
        s.Tick(On(ws));
        Assert.Equal(TaskState.Due, Only(s, "Review").StateAt(clock));
        At(TimeSpan.FromMinutes(3));
        s.Tick(On(ws));
        Assert.Equal(TaskState.Due, Only(s, "Review").StateAt(clock));
    }

    [Fact]
    public void Each_due_task_is_announced_once()
    {
        var s = NewService();
        var announced = new List<TaskItem>();
        using var sub = s.CameDue.Subscribe(announced.Add);
        s.Park("One", On(ws), TimeSpan.FromMinutes(1));
        s.Park("Five", On(ws), TimeSpan.FromMinutes(5));
        s.Tick(On(other));
        At(TimeSpan.FromHours(2));
        s.Tick(On(other));
        s.Tick(On(other));
        s.Tick(On(other));
        Assert.Equal(2, announced.Count);
    }

    [Fact]
    public void A_due_task_on_load_is_announced_on_the_first_tick()
    {
        var id = Guid.NewGuid();
        var started = clock - TimeSpan.FromDays(1);
        store.Stored = new TaskBoard([new TaskItem(id, "Old", On(ws), started, started,
            new TaskTimer(started, TimeSpan.FromMinutes(5)), Maybe<DateTimeOffset>.None, Array.Empty<TimerRecord>())]);
        var s = NewService();
        var announced = new List<TaskItem>();
        using var sub = s.CameDue.Subscribe(announced.Add);
        Assert.True(s.Load().IsSuccess);
        s.Tick(On(other));
        Assert.Single(announced);
        Assert.Equal(id, announced[0].Id);
    }

    [Fact]
    public void Prune_frees_tasks_of_deleted_workspaces()
    {
        var s = NewService();
        s.Park("Review", On(ws), TimeSpan.FromMinutes(5));
        Assert.True(s.RunningWorkspaces().ContainsKey(ws));
        s.Prune(new HashSet<Guid> { other });
        Assert.False(s.RunningWorkspaces().ContainsKey(ws));
        Assert.True(Only(s, "Review").WorkspaceId.HasNoValue);
    }

    [Fact]
    public void Entries_sort_by_what_needs_you_next()
    {
        var wsA = Guid.NewGuid();
        var s = NewService();
        var now = clock;
        // Due 3 minutes ago and due 1 minute ago: park 1 minute timers 4 and 2 minutes back.
        clock = now - TimeSpan.FromMinutes(4);
        s.Park("due3", On(ws), TimeSpan.FromMinutes(1));
        clock = now - TimeSpan.FromMinutes(2);
        s.Park("due1", On(ws), TimeSpan.FromMinutes(1));
        clock = now;
        s.Park("run4", On(ws), TimeSpan.FromMinutes(4));
        s.Park("run22", On(ws), TimeSpan.FromMinutes(22));
        s.Park("openOld", On(ws), Maybe<TimeSpan>.None);
        clock = now + TimeSpan.FromSeconds(1);
        s.Park("openNew", On(ws), Maybe<TimeSpan>.None);
        s.Park("doneToday", On(ws), Maybe<TimeSpan>.None);
        s.Park("doneYesterday", On(ws), Maybe<TimeSpan>.None);
        s.Done(Only(s, "doneToday").Id);
        // Yesterday's done: complete it with the clock a day back, then restore the clock.
        var saved = clock;
        clock -= TimeSpan.FromDays(1);
        s.Done(Only(s, "doneYesterday").Id);
        clock = saved;

        var entries = s.Entries([wsA], DateOnly.FromDateTime(clock.ToLocalTime().DateTime));
        var titles = entries.Select(e => e.Kind == TaskEntryKind.Ask ? "ask" : e.Task.Value.Title).ToArray();
        Assert.Equal(["due3", "due1", "ask", "run4", "run22", "openNew", "openOld", "doneToday"], titles);
        Assert.Equal(TaskState.Due, entries.Single(e => e.Kind == TaskEntryKind.Ask).State);
        Assert.Equal(wsA, entries.Single(e => e.Kind == TaskEntryKind.Ask).WorkspaceId.Value);
    }

    [Fact]
    public void Mutations_save_and_pulse()
    {
        var s = NewService();
        var task = s.Park("Review", On(ws), Maybe<TimeSpan>.None).Value;
        var saves = store.Saves;
        var pulses = 0;
        using var sub = s.Changed.Subscribe(_ => pulses++);
        Assert.True(s.Rename(task.Id, "Review it").IsSuccess);
        Assert.Equal(saves + 1, store.Saves);
        Assert.Equal(1, pulses);
    }

    [Fact]
    public void A_failed_save_is_traced_and_the_board_still_changes()
    {
        store.FailSave = true;
        var traced = new List<string>();
        var s = new TaskService(store, () => clock, trace: traced.Add);
        Assert.True(s.Park("Review", On(ws), Maybe<TimeSpan>.None).IsSuccess);
        Assert.Single(s.Board.Tasks);
        Assert.NotEmpty(traced);
    }

    [Fact]
    public void A_failed_load_leaves_the_board_empty_and_returns_the_failure()
    {
        store.FailLoad = true;
        var s = NewService();
        Assert.True(s.Load().IsFailure);
        Assert.Empty(s.Board.Tasks);
    }

    sealed class FakeTaskStore : ITaskStore
    {
        public TaskBoard Stored { get; set; } = TaskBoard.Empty;
        public int Saves { get; private set; }
        public bool FailSave { get; set; }
        public bool FailLoad { get; set; }

        public Result<TaskBoard> Load() => FailLoad ? Result.Failure<TaskBoard>("load failed") : Stored;

        public Result Save(TaskBoard board)
        {
            Saves++;
            if (FailSave) return Result.Failure("save failed");
            Stored = board;
            return Result.Success();
        }
    }
}
