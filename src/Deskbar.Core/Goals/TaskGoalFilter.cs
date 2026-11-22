using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.Core.Goals;

// The tasks window's goal filter, decided here so the window only draws it. Design:
// docs/superpowers/specs/2026-11-22-goals-planner-design.md ("Linking a task to a goal elsewhere": the List and Matrix
// views filter by goal) and the goals ledger's ruling G4.
//
//   * All: every task. The default, and what the window shows without a GoalService.
//   * No goal: tasks with no goal AND tasks whose goal no longer exists. A dangling link reads as no goal in every
//     view (review focus 4), so those tasks are here, where Petre can link them again.
//   * On a goal: the tasks linked to it. A monthly goal also admits the tasks of its weekly goals (G4): filtering by
//     the month shows everything that serves it. Only the weekly goals whose week TOUCHES the monthly goal's current
//     period count, the same rule as GoalProgress (ruling G6): a monthly goal carried from November to December keeps
//     its November weeks naming it, but they served November, so their tasks are not December's.
//
// A filter holds only the goal's id, never a copy of the goal board: whether a task's weekly goal belongs to the
// month is read from the board at the moment of asking, so a parent set or cleared from the pipe shows at the next
// redraw, and two filters compare by value (the window's redraw signature compares them). Shaped like
// TaskWorkspaceFilter (Any, then the id).
public sealed record TaskGoalFilter(bool Any, Maybe<Guid> Goal)
{
    public static readonly TaskGoalFilter All = new(true, Maybe<Guid>.None);

    public static readonly TaskGoalFilter NoGoal = new(false, Maybe<Guid>.None);

    /// <summary>
    /// The tasks counting toward <paramref name="goal"/>, and for a monthly goal its weekly goals' tasks too. The plan
    /// sketched this as On(goal, goals); the board is read in Admits instead, so the filter stays a value.
    /// </summary>
    public static TaskGoalFilter On(Goal goal) => new(false, goal.Id);

    public bool Admits(TaskItem task, GoalBoard goals) =>
        Any || (Goal.HasNoValue
            ? task.GoalId.Bind(goals.Get).HasNoValue
            : task.GoalId.Bind(goals.Get).Map(g => g.Id == Goal.Value || ServesTheMonth(g, goals)).GetValueOrDefault(false));

    // A weekly goal naming the filter's goal, in a week that touches that goal's current period (G6).
    bool ServesTheMonth(Goal weekly, GoalBoard goals) =>
        weekly.ParentId == Goal.Value
        && goals.Get(Goal.Value).Map(parent => weekly.Period.Touches(parent.Period)).GetValueOrDefault(false);

    /// <summary>The board with only the tasks this filter admits; All returns the same board.</summary>
    public TaskBoard Narrow(TaskBoard board, GoalBoard goals) =>
        Any ? board : new TaskBoard(board.Tasks.Where(t => Admits(t, goals)).ToArray());
}
