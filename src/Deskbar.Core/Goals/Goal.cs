using CSharpFunctionalExtensions;

namespace Deskbar.Core.Goals;

// The goal data model. Design: docs/superpowers/specs/2026-11-22-goals-planner-design.md ("The goal", "Calls made in
// writing this up"). A goal works like an epic over the tasks list: it groups tasks and outlasts any one of them.
//
// What a goal has, and does not: a title, a period (one week or one month), a status and an order within its period.
// No flags (Petre: goals are "all important", quadrant II by definition) and no workspace (its tasks keep their own,
// so a goal can span several). A weekly goal may name one monthly goal as its parent (GoalBoard.SetParent enforces
// which). Sync-ready, as tasks are: a Guid made on the device, an UpdatedAt that moves on every change, UTC times.
// The period alone is a local date (GoalPeriod's header says why).
//
// Everything here is an immutable record; GoalBoard holds the rules that change them.

/// <summary>
/// Where a goal stands. Open is first on purpose: it is the enum's default, so a missing status in goals.json reads as
/// Open (an open goal this build cannot place stays in front of Petre rather than vanishing into history).
/// <list type="bullet">
/// <item>Open: still being worked toward.</item>
/// <item>Done: Petre's call. Finishing every task never closes a goal, and an open task never stops him marking it done.</item>
/// <item>Dropped: the normal end of a goal that did not happen. Delete is only for mistakes.</item>
/// </list>
/// </summary>
public enum GoalStatus { Open, Done, Dropped }

/// <summary>
/// One goal. <see cref="Order"/> is its place within its period (smaller is higher; a new or carried goal goes to the
/// end). <see cref="ParentId"/> is the monthly goal a weekly goal serves, none on a monthly goal and on a weekly goal
/// that serves none. Carrying a goal to another period keeps its <see cref="Id"/>, so its tasks stay attached and its
/// history is one goal, not a chain of copies.
/// </summary>
public sealed record Goal(
    Guid Id,
    string Title,
    GoalPeriod Period,
    GoalStatus Status,
    int Order,
    Maybe<Guid> ParentId,
    DateTimeOffset Created,
    DateTimeOffset UpdatedAt)
{
    public GoalKind Kind => Period.Kind;

    public bool IsOpen => Status == GoalStatus.Open;
}
