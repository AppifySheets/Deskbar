using CSharpFunctionalExtensions;
using Deskbar.Core.Goals;
using Deskbar.Core.Tasks;
// System.Threading.Tasks (an implicit using) has a TaskStatus too; the alias says which one this file means.
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Goals;

// How a goal's progress counts up the chain. Design: docs/superpowers/specs/2026-11-22-goals-planner-design.md
// ("Tasks contribute"): a weekly goal counts its tasks, Done and Closed both done; a monthly goal counts its weekly
// goals and the tasks attached to it directly. A dropped weekly goal counts in neither number (G4).
public sealed class GoalProgressTests
{
    static readonly DateTimeOffset T0 = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);
    static readonly GoalPeriod Week = GoalPeriod.WeekOf(new DateOnly(2026, 11, 23));
    static readonly GoalPeriod Week30 = GoalPeriod.WeekOf(new DateOnly(2026, 11, 30));
    static readonly GoalPeriod November = GoalPeriod.MonthOf(new DateOnly(2026, 11, 1));

    GoalBoard goals = GoalBoard.Empty;
    TaskBoard tasks = TaskBoard.Empty;

    Goal AddGoal(string title, GoalPeriod period, Maybe<Guid> parent = default)
    {
        var (board, goal) = goals.Add(title, period, T0, Guid.NewGuid()).Value;
        goals = parent.HasValue ? board.SetParent(goal.Id, parent, T0).Value : board;
        return goals.Get(goal.Id).Value;
    }

    void AddTask(Goal goal, TaskStatus status)
    {
        var (board, task) = tasks.AddNote("task", Maybe<Guid>.None, T0, Guid.NewGuid()).Value;
        board = board.SetGoal(task.Id, goal.Id, T0).Value;
        tasks = status switch
        {
            TaskStatus.Closed => board.Close(task.Id, T0).Value,
            TaskStatus.Backlog => board,
            _ => board.SetStatus(task.Id, status, T0).Value,
        };
    }

    GoalProgress Of(Goal goal) => GoalProgress.Of(goals.Get(goal.Id).Value, goals, tasks);

    [Fact]
    public void A_weekly_goal_counts_its_tasks_done_and_closed_as_done()
    {
        var goal = AddGoal("Ship it", Week);
        var other = AddGoal("Other", Week);
        AddTask(goal, TaskStatus.Done);
        AddTask(goal, TaskStatus.Closed);
        AddTask(goal, TaskStatus.InProgress);
        AddTask(goal, TaskStatus.WaitingOnYou);
        AddTask(goal, TaskStatus.Backlog);
        AddTask(other, TaskStatus.Done);

        var progress = Of(goal);

        Assert.Equal(new GoalProgress(GoalKind.Week, 2, 5, 0, 0), progress);
        Assert.Equal("2 of 5 done", progress.Text);
    }

    [Fact]
    public void A_weekly_goal_with_no_tasks_says_so()
    {
        Assert.Equal("no tasks yet", Of(AddGoal("Ship it", Week)).Text);
    }

    [Fact]
    public void A_monthly_goal_counts_its_weekly_goals_and_its_own_tasks()
    {
        var month = AddGoal("Store", November);
        var done = AddGoal("w1", Week, month.Id);
        AddGoal("w2", Week30, month.Id);
        var dropped = AddGoal("w3", Week, month.Id);
        AddGoal("w4", Week30, month.Id);
        goals = goals.MarkDone(done.Id, T0).Value.Drop(dropped.Id, T0).Value;
        AddTask(month, TaskStatus.Closed);
        AddTask(month, TaskStatus.Backlog);

        var progress = Of(month);

        Assert.Equal(new GoalProgress(GoalKind.Month, 1, 2, 1, 3), progress);
        Assert.Equal("1 of 3 weeks' goals done, 1 of 2 tasks", progress.Text);
    }

    [Fact]
    public void A_monthly_goal_with_only_weekly_goals()
    {
        var month = AddGoal("Store", November);
        var w1 = AddGoal("w1", Week, month.Id);
        AddGoal("w2", Week30, month.Id);
        goals = goals.MarkDone(w1.Id, T0).Value;
        // A weekly goal's own tasks count toward it, not directly toward the month.
        AddTask(w1, TaskStatus.Done);

        Assert.Equal("1 of 2 weeks' goals done", Of(month).Text);
    }

    [Fact]
    public void A_monthly_goal_with_only_tasks()
    {
        var month = AddGoal("Store", November);
        AddTask(month, TaskStatus.Done);
        AddTask(month, TaskStatus.Backlog);

        Assert.Equal("1 of 2 tasks done", Of(month).Text);
    }

    [Fact]
    public void A_monthly_goal_with_nothing_attached()
    {
        var month = AddGoal("Store", November);
        var dropped = AddGoal("w1", Week, month.Id);
        goals = goals.Drop(dropped.Id, T0).Value;

        Assert.Equal(new GoalProgress(GoalKind.Month, 0, 0, 0, 0), Of(month));
        Assert.Equal("nothing attached yet", Of(month).Text);
    }
}
