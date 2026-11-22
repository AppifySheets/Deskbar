using CSharpFunctionalExtensions;
using Deskbar.Core.Goals;
using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests.Goals;

// GoalService: the live goal board, saved on every change and announced through Changed, and the one place a task is
// linked to a goal. Design: docs/superpowers/specs/2026-11-22-goals-planner-design.md and the plan's Task 4. The UTC
// clock (`clock`) and the local date (`today`) are captured fields the test moves, so nothing here waits, and the two
// can disagree the way they do near midnight (review focus 2).
public sealed class GoalServiceTests
{
    DateTimeOffset clock = new(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);
    DateOnly today = new(2026, 11, 24);
    readonly FakeGoalStore store = new();
    readonly MemoryTaskStore taskStore = new();
    readonly TaskService tasks;

    public GoalServiceTests() => tasks = new TaskService(taskStore, () => clock);

    GoalService NewService(Action<string>? trace = null) => new(store, tasks, () => clock, () => today, trace: trace);

    static readonly GoalPeriod LastWeek = GoalPeriod.WeekOf(new DateOnly(2026, 11, 16));

    TaskItem Note(string title = "Dishwasher tablets") => tasks.AddNote(title, Maybe<Guid>.None).Value;

    [Fact]
    public void This_week_and_this_month_come_from_today()
    {
        var s = NewService();

        Assert.Equal(GoalPeriod.WeekOf(new DateOnly(2026, 11, 23)), s.ThisWeek);
        Assert.Equal(GoalPeriod.MonthOf(new DateOnly(2026, 11, 1)), s.ThisMonth);
        today = new DateOnly(2026, 12, 1);
        Assert.Equal(GoalPeriod.WeekOf(new DateOnly(2026, 11, 30)), s.ThisWeek);
        Assert.Equal(GoalPeriod.MonthOf(new DateOnly(2026, 12, 1)), s.ThisMonth);
    }

    // Every change the service offers, each run once, counting a save and a pulse for each.
    [Fact]
    public void Every_change_saves_and_pulses()
    {
        var s = NewService();
        var weekly = s.Add("Ship the goals planner", s.ThisWeek).Value;
        var monthly = s.Add("Deskbar Pro in the Store", s.ThisMonth).Value;
        var old = s.Add("Read Covey", LastWeek).Value;
        var changes = new Func<Result>[]
        {
            () => s.Add("Another", s.ThisWeek),
            () => s.Rename(weekly.Id, "Ship it"),
            () => s.MarkDone(weekly.Id),
            () => s.Reopen(weekly.Id),
            () => s.Drop(weekly.Id),
            () => s.SetParent(weekly.Id, monthly.Id),
            () => s.Carry(weekly.Id, s.ThisWeek.Next()),
            () => s.CarryToNow(old.Id),
            () => { s.Seen(); return Result.Success(); },
            () => s.Delete(monthly.Id),
        };
        var pulses = 0;
        using var sub = s.Changed.Subscribe(_ => pulses++);

        changes.Select((change, i) => (change, i)).ToList().ForEach(c =>
        {
            var saves = store.Saves;
            var before = pulses;
            var result = c.change();
            Assert.True(result.IsSuccess, $"change {c.i}: {(result.IsFailure ? result.Error : "")}");
            Assert.Equal(saves + 1, store.Saves);
            Assert.Equal(before + 1, pulses);
        });
        Assert.Same(s.Board, store.Stored);
    }

    [Fact]
    public void A_refused_change_or_no_change_saves_nothing()
    {
        var s = NewService();
        var goal = s.Add("Ship it", s.ThisWeek).Value;
        s.Seen();
        var saves = store.Saves;
        var pulses = 0;
        using var sub = s.Changed.Subscribe(_ => pulses++);

        Assert.True(s.Reopen(goal.Id).IsFailure);
        Assert.True(s.Rename(Guid.NewGuid(), "x").IsFailure);
        Assert.True(s.Add(" ", s.ThisWeek).IsFailure);
        Assert.True(s.CarryToNow(goal.Id).IsSuccess);
        Assert.True(s.SetParent(goal.Id, Maybe<Guid>.None).IsSuccess);
        s.Seen();

        Assert.Equal(saves, store.Saves);
        Assert.Equal(0, pulses);
    }

    [Fact]
    public void A_failed_save_keeps_the_change_in_memory()
    {
        store.SaveFails = true;
        var traced = new List<string>();
        var s = NewService(traced.Add);

        Assert.True(s.Add("Ship it", s.ThisWeek).IsSuccess);
        Assert.Single(s.Board.Goals);
        Assert.Contains(traced, line => line.Contains("save failed"));
    }

    [Fact]
    public void Load_reads_the_store_and_pulses()
    {
        var goal = new Goal(Guid.NewGuid(), "Ship it", LastWeek, GoalStatus.Open, 0, Maybe<Guid>.None, clock, clock);
        store.Stored = new GoalBoard([goal]);
        var s = NewService();
        var pulses = 0;
        using var sub = s.Changed.Subscribe(_ => pulses++);

        Assert.True(s.Load().IsSuccess);

        Assert.Equal(goal, Assert.Single(s.Board.Goals));
        Assert.True(s.LoadProblem.HasNoValue);
        Assert.Equal(1, pulses);
    }

    [Fact]
    public void A_failed_load_is_reported()
    {
        store.Stored = GoalBoard.Empty.Add("Ship it", LastWeek, clock, Guid.NewGuid()).Value.Board;
        store.LoadFails = "goals.json is unreadable";
        var s = NewService();

        Assert.True(s.Load().IsFailure);
        Assert.Empty(s.Board.Goals);
        Assert.Equal("goals.json is unreadable", s.LoadProblem.Value);

        store.LoadFails = Maybe<string>.None;
        Assert.True(s.Load().IsSuccess);
        Assert.True(s.LoadProblem.HasNoValue);
        Assert.Single(s.Board.Goals);
    }

    // Review focus 3: goals.json cannot be read while tasks.json holds links. Nothing prunes the links, so the task
    // board is untouched and nothing is written to tasks.json; once goals.json reads again, the links work again.
    [Fact]
    public void A_failed_load_leaves_task_links_alone()
    {
        var s = NewService();
        var goal = s.Add("Ship it", s.ThisWeek).Value;
        var task = Note();
        Assert.True(s.LinkTask(task.Id, goal.Id).IsSuccess);
        var board = tasks.Board;
        var saved = store.Stored;
        var taskSaves = taskStore.Saves;
        var taskPulses = 0;
        using var sub = tasks.Changed.Subscribe(_ => taskPulses++);

        store.LoadFails = "goals.json is unreadable";
        Assert.True(s.Load().IsFailure);

        Assert.Same(board, tasks.Board);
        Assert.Equal(taskSaves, taskStore.Saves);
        Assert.Equal(0, taskPulses);
        Assert.True(s.GoalOf(tasks.Board.Tasks.Single()).HasNoValue);

        store.LoadFails = Maybe<string>.None;
        store.Stored = saved;
        s.Load();
        Assert.Equal(goal.Id, s.GoalOf(tasks.Board.Tasks.Single()).Value.Id);
    }

    [Fact]
    public void Linking_needs_an_open_goal()
    {
        var s = NewService();
        var done = s.Add("Done goal", s.ThisWeek).Value;
        var dropped = s.Add("Dropped goal", s.ThisWeek).Value;
        s.MarkDone(done.Id);
        s.Drop(dropped.Id);
        var task = Note();
        var saves = taskStore.Saves;

        Assert.Equal("The goal is done", s.LinkTask(task.Id, done.Id).Error);
        Assert.Equal("The goal is dropped", s.LinkTask(task.Id, dropped.Id).Error);
        var missing = Guid.NewGuid();
        Assert.Equal($"No goal with id {missing}", s.LinkTask(task.Id, missing).Error);
        Assert.Equal($"No task with id {missing}", s.LinkTask(missing, Maybe<Guid>.None).Error);
        Assert.Equal(saves, taskStore.Saves);
        Assert.True(tasks.Board.Tasks.Single().GoalId.HasNoValue);
    }

    // Unlinking never depends on the goal: a task can always be freed, even from a goal that is done or gone.
    [Fact]
    public void Linking_to_none_always_works()
    {
        var s = NewService();
        var goal = s.Add("Ship it", s.ThisWeek).Value;
        var task = Note();
        s.LinkTask(task.Id, goal.Id);
        s.MarkDone(goal.Id);

        Assert.True(s.LinkTask(task.Id, Maybe<Guid>.None).IsSuccess);
        Assert.True(tasks.Board.Tasks.Single().GoalId.HasNoValue);
    }

    [Fact]
    public void Linking_changes_no_flag()
    {
        var s = NewService();
        var goal = s.Add("Ship it", s.ThisMonth).Value;
        var task = Note();
        tasks.SetFlag(task.Id, TaskFlag.Urgent, true);
        var before = tasks.Board.Tasks.Single();
        var goalPulses = 0;
        using var sub = s.Changed.Subscribe(_ => goalPulses++);

        Assert.True(s.LinkTask(task.Id, goal.Id).IsSuccess);

        var after = tasks.Board.Tasks.Single();
        Assert.Equal(goal.Id, after.GoalId.Value);
        Assert.Equal((before.Important, before.Urgent, before.Rank, before.Status), (after.Important, after.Urgent, after.Rank, after.Status));
        // A link is a task change: TaskService pulses, the goal board did not change.
        Assert.Equal(0, goalPulses);
        Assert.Equal(goal.Id, s.GoalOf(after).Value.Id);
    }

    [Fact]
    public void Deleting_a_goal_detaches_its_tasks_and_children()
    {
        var s = NewService();
        var monthly = s.Add("Store", s.ThisMonth).Value;
        var weekly = s.Add("Ship it", s.ThisWeek).Value;
        s.SetParent(weekly.Id, monthly.Id);
        var mine = Note("mine");
        var childs = Note("child's");
        s.LinkTask(mine.Id, monthly.Id);
        s.LinkTask(childs.Id, weekly.Id);

        Assert.True(s.Delete(monthly.Id).IsSuccess);

        Assert.True(s.Board.Get(monthly.Id).HasNoValue);
        Assert.True(s.Board.Get(weekly.Id).Value.ParentId.HasNoValue);
        Assert.True(tasks.Board.Tasks.Single(t => t.Id == mine.Id).GoalId.HasNoValue);
        Assert.Equal(weekly.Id, tasks.Board.Tasks.Single(t => t.Id == childs.Id).GoalId.Value);
        Assert.True(taskStore.Stored.Tasks.Single(t => t.Id == mine.Id).GoalId.HasNoValue);
        Assert.True(s.Delete(monthly.Id).IsFailure);
    }

    // Review focus 4: a task names a goal that does not exist (deleted elsewhere, a file restored by hand). It reads as
    // no goal everywhere, and can be linked again.
    [Fact]
    public void A_dangling_link_reads_as_no_goal()
    {
        var s = NewService();
        var goal = s.Add("Ship it", s.ThisWeek).Value;
        var task = Note();
        tasks.SetGoal(task.Id, Guid.NewGuid());
        var dangling = tasks.Board.Tasks.Single();

        Assert.True(s.GoalOf(dangling).HasNoValue);
        Assert.True(TaskGoalFilter.NoGoal.Admits(dangling, s.Board));
        Assert.False(TaskGoalFilter.On(goal).Admits(dangling, s.Board));

        Assert.True(s.LinkTask(task.Id, goal.Id).IsSuccess);
        Assert.Equal(goal.Id, s.GoalOf(tasks.Board.Tasks.Single()).Value.Id);
    }

    // Review focus 2: at 00:30 UTC on Monday 23 November it is still Sunday the 22nd where Petre is. "This week" is
    // his local week, the one ending, and a goal added now goes there.
    [Fact]
    public void This_week_is_the_local_week_not_the_utc_one()
    {
        clock = new DateTimeOffset(2026, 11, 23, 0, 30, 0, TimeSpan.Zero);
        today = new DateOnly(2026, 11, 22);
        var s = NewService();

        Assert.Equal(new DateOnly(2026, 11, 16), s.ThisWeek.Start);
        Assert.Equal(new DateOnly(2026, 11, 16), s.Add("Ship it", s.ThisWeek).Value.Period.Start);
        Assert.Equal(clock, s.Board.Goals.Single().Created);
    }

    [Fact]
    public void Carry_to_now_goes_to_the_current_period_of_its_kind()
    {
        var s = NewService();
        var week = s.Add("Old week", LastWeek).Value;
        var month = s.Add("Old month", GoalPeriod.MonthOf(new DateOnly(2026, 10, 1))).Value;

        Assert.True(s.CarryToNow(week.Id).IsSuccess);
        Assert.True(s.CarryToNow(month.Id).IsSuccess);

        Assert.Equal(s.ThisWeek, s.Board.Get(week.Id).Value.Period);
        Assert.Equal(s.ThisMonth, s.Board.Get(month.Id).Value.Period);
        Assert.Equal($"No goal with id {Guid.Empty}", s.CarryToNow(Guid.Empty).Error);
    }

    // The review: due state and pending list read from the live board and today; Seen records this week and month.
    [Fact]
    public void Seen_clears_the_prompt_and_saves_once()
    {
        var s = NewService();
        var old = s.Add("Read Covey", LastWeek).Value;
        Assert.Equal(ReviewDue.Week, s.PromptDue());
        Assert.Equal([old.Id], s.ToReview().Select(g => g.Id));
        var saves = store.Saves;

        s.Seen();
        s.Seen();

        Assert.Equal(ReviewDue.None, s.PromptDue());
        Assert.Equal(saves + 1, store.Saves);
        Assert.Equal(new DateOnly(2026, 11, 23), store.Stored.WeekSeen.Value);
        Assert.Equal(new DateOnly(2026, 11, 1), store.Stored.MonthSeen.Value);
        // Seeing the prompt does not empty the review: the strip lists earlier open goals until each is handled.
        Assert.Equal([old.Id], s.ToReview().Select(g => g.Id));

        today = new DateOnly(2026, 12, 1);
        Assert.Equal(ReviewDue.Month, s.PromptDue());
    }

    [Fact]
    public void Progress_reads_the_live_task_board()
    {
        var s = NewService();
        var goal = s.Add("Ship it", s.ThisWeek).Value;
        var a = Note("a");
        var b = Note("b");
        s.LinkTask(a.Id, goal.Id);
        s.LinkTask(b.Id, goal.Id);
        tasks.Close(a.Id);

        Assert.Equal("1 of 2 done", s.ProgressOf(s.Board.Get(goal.Id).Value).Text);
    }

    sealed class MemoryTaskStore : ITaskStore
    {
        public TaskBoard Stored { get; private set; } = TaskBoard.Empty;
        public int Saves { get; private set; }

        public Result<TaskBoard> Load() => Stored;

        public Result Save(TaskBoard board)
        {
            Saves++;
            Stored = board;
            return Result.Success();
        }
    }
}
