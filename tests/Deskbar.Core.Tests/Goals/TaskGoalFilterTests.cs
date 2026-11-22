using CSharpFunctionalExtensions;
using Deskbar.Core.Goals;
using Deskbar.Core.Tasks;
// System.Threading.Tasks (an implicit using) has a TaskStatus too; the alias says which one this file means.
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Goals;

// The tasks window's goal filter, decided in Core. Design: docs/superpowers/specs/2026-11-22-goals-planner-design.md
// ("Linking a task to a goal elsewhere": the List and Matrix views filter by goal) and ruling G4 (filtering by a
// monthly goal includes its weekly goals' tasks; a dangling link reads as no goal).
public sealed class TaskGoalFilterTests
{
    static readonly DateTimeOffset T0 = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);
    static readonly Guid monthly = Guid.Parse("ffffffff-0000-0000-0000-000000000001");
    static readonly Guid weekly = Guid.Parse("ffffffff-0000-0000-0000-000000000002");
    static readonly Guid otherWeekly = Guid.Parse("ffffffff-0000-0000-0000-000000000003");

    readonly GoalBoard goals = GoalBoard.Empty
        .Add("Store", GoalPeriod.MonthOf(new DateOnly(2026, 11, 1)), T0, monthly).Value.Board
        .Add("Ship it", GoalPeriod.WeekOf(new DateOnly(2026, 11, 23)), T0, weekly).Value.Board
        .Add("Other", GoalPeriod.WeekOf(new DateOnly(2026, 11, 23)), T0, otherWeekly).Value.Board
        .SetParent(weekly, monthly, T0).Value;

    // One task per kind of link: on the monthly goal, on its weekly child, on an unrelated weekly goal, none, dangling.
    readonly TaskBoard tasks = new(new (string Title, Maybe<Guid> Goal)[]
    {
        ("on month", monthly), ("on week", weekly), ("on other", otherWeekly), ("free", Maybe<Guid>.None), ("dangling", Guid.NewGuid()),
    }.Select(t => new TaskItem(Guid.NewGuid(), t.Title, Maybe<Guid>.None, TaskStatus.Backlog, "", Maybe<string>.None,
        T0, T0, T0, [], []) { GoalId = t.Goal }).ToArray());

    string[] Shown(TaskGoalFilter filter) => filter.Narrow(tasks, goals).Tasks.Select(t => t.Title).ToArray();

    [Fact]
    public void All_admits_every_task_and_keeps_the_board()
    {
        Assert.Same(tasks, TaskGoalFilter.All.Narrow(tasks, goals));
    }

    [Fact]
    public void No_goal_admits_free_and_dangling_tasks() =>
        Assert.Equal(["free", "dangling"], Shown(TaskGoalFilter.NoGoal));

    [Fact]
    public void A_weekly_goal_admits_its_own_tasks_only() =>
        Assert.Equal(["on week"], Shown(TaskGoalFilter.On(goals.Get(weekly).Value)));

    [Fact]
    public void A_monthly_goal_admits_its_own_tasks_and_its_weekly_goals_tasks() =>
        Assert.Equal(["on month", "on week"], Shown(TaskGoalFilter.On(goals.Get(monthly).Value)));

    // A filter is a value, so the tasks window can compare it in its redraw signature.
    [Fact]
    public void Filters_compare_by_value()
    {
        Assert.Equal(TaskGoalFilter.On(goals.Get(weekly).Value), TaskGoalFilter.On(goals.Get(weekly).Value));
        Assert.NotEqual(TaskGoalFilter.On(goals.Get(weekly).Value), TaskGoalFilter.NoGoal);
        Assert.NotEqual(TaskGoalFilter.All, TaskGoalFilter.NoGoal);
    }
}
