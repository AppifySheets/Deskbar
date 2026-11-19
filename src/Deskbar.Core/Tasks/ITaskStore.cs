using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// Where the task board lives between runs. Design: docs/superpowers/specs/2026-11-19-tasks-in-the-bar-design.md
// ("Persistence", "Later: a cloud task list"). This is the seam a cloud-syncing store replaces, so it stays
// exactly these two members: anything more would leak how one store happens to work.
public interface ITaskStore
{
    Result<TaskBoard> Load();

    Result Save(TaskBoard board);
}
