using CSharpFunctionalExtensions;

namespace Deskbar.Core.Goals;

// Where the goal board lives between runs, with the review state (the last week and month seen). Design:
// docs/superpowers/specs/2026-11-22-goals-planner-design.md ("Constraints": sync-ready). As ITaskStore, this is the
// seam a cloud-syncing store replaces (a later sync includes goals), so it stays exactly these two members: anything
// more would leak how one store happens to work.
public interface IGoalStore
{
    Result<GoalBoard> Load();

    Result Save(GoalBoard board);
}
