using Deskbar.Core.Tasks;
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Goals;

// How a goal's progress counts up the chain. Design: docs/superpowers/specs/2026-11-22-goals-planner-design.md
// ("Tasks contribute") and the plan's "Calls this plan makes" (G4).
//
//   * Tasks: every task whose GoalId is this goal, closed ones included (they are the work that got done). A task is
//     done when its status is Done (Claude finished it) or Closed (Petre closed it).
//   * Weeks, for a monthly goal only: its weekly goals (those naming it as parent) that are not Dropped, done when
//     Done. A dropped weekly goal counts in NEITHER number, so dropping one never makes the month look further behind.
//     A weekly goal's own tasks count toward it, not again toward the month: the month sees them through the week.
//   * Nothing here closes a goal. Marking a goal done is Petre's call, whatever the counts say.
//
// The kind is part of the record (the plan's sketch had only the four counts) because the two kinds word the same
// counts differently: a weekly goal's "1 of 2 done" is a monthly goal's "1 of 2 tasks done", and an empty weekly goal
// has "no tasks yet" where an empty monthly one has "nothing attached yet".

/// <summary>A goal's counts and the one line the planner and the pipe show for them. Weeks are 0 on a weekly goal.</summary>
public sealed record GoalProgress(GoalKind Kind, int TasksDone, int Tasks, int WeeksDone, int Weeks)
{
    public static GoalProgress Of(Goal goal, GoalBoard goals, TaskBoard tasks)
    {
        var own = tasks.Tasks.Where(t => t.GoalId == goal.Id).ToArray();
        var weeks = goal.Kind == GoalKind.Month
            ? goals.ChildrenOf(goal.Id).Where(g => g.Status != GoalStatus.Dropped).ToArray()
            : [];
        return new(goal.Kind, own.Count(t => t.Status is TaskStatus.Done or TaskStatus.Closed), own.Length,
            weeks.Count(g => g.Status == GoalStatus.Done), weeks.Length);
    }

    /// <summary>
    /// A weekly goal: "3 of 5 done", or "no tasks yet". A monthly goal: "2 of 4 weeks' goals done", with ", 1 of 2
    /// tasks" when tasks are attached to it directly; "1 of 2 tasks done" with tasks and no weekly goals; "nothing
    /// attached yet" with neither.
    /// </summary>
    public string Text => Kind == GoalKind.Week
        ? Tasks > 0 ? $"{TasksDone} of {Tasks} done" : "no tasks yet"
        : Weeks > 0 ? $"{WeeksDone} of {Weeks} weeks' goals done" + (Tasks > 0 ? $", {TasksDone} of {Tasks} tasks" : "")
        : Tasks > 0 ? $"{TasksDone} of {Tasks} tasks done"
        : "nothing attached yet";
}
