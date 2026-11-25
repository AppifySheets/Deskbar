using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests.Tasks;

// RepoWorkspace.Latest (task f905b1a7): where a repo's work lives, read from the board, for a session's task when no
// VS Code window shows its folder. The workspace of the task whose session in that repo joined last; worktrees count
// as their repo; tasks on no workspace and workspaces that are gone are passed over.
public class RepoWorkspaceTests
{
    static readonly Guid Deskbar = Guid.NewGuid();
    static readonly Guid Iam = Guid.NewGuid();
    static readonly Guid Gone = Guid.NewGuid();

    DateTimeOffset clock = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);
    readonly TaskService tasks;

    public RepoWorkspaceTests() => tasks = new TaskService(new MemoryTaskStore(), () => clock);

    sealed class MemoryTaskStore : ITaskStore
    {
        public Result<TaskBoard> Load() => TaskBoard.Empty;

        public Result Save(TaskBoard board) => Result.Success();
    }

    // A task worked on in `folder`, on `workspace`, one minute after the last one.
    void Worked(string title, Maybe<Guid> workspace, string folder)
    {
        clock = clock.AddMinutes(1);
        Assert.True(tasks.Create(title, workspace, tasks.NewSession(Guid.NewGuid().ToString(), folder)).IsSuccess);
    }

    Maybe<Guid> Latest(string folder) => RepoWorkspace.Latest(tasks.Board, folder, id => id != Gone);

    [Fact]
    public void The_latest_task_in_the_repo_names_the_workspace()
    {
        Worked("Store listing", Deskbar, @"C:\repos\TaskSpaces");
        Worked("Probe", Iam, @"C:\repos\TaskSpaces\.claude\worktrees\probe");
        Worked("Elsewhere", Deskbar, @"C:\repos\iam.ge");

        Assert.Equal(Iam, Latest(@"C:\repos\TaskSpaces").Value);
    }

    [Fact]
    public void A_worktree_spelled_any_way_counts_as_its_repo()
    {
        Worked("Store listing", Deskbar, @"C:\repos\TaskSpaces");

        Assert.Equal(Deskbar, Latest(@"c:\repos\taskspaces\.claude\worktrees\new-topic").Value);
        Assert.Equal(Deskbar, Latest(@"C:\repos\TaskSpaces\").Value);
    }

    [Fact]
    public void Tasks_on_no_workspace_or_a_gone_one_say_nothing()
    {
        Worked("Store listing", Deskbar, @"C:\repos\TaskSpaces");
        Worked("Free", Maybe<Guid>.None, @"C:\repos\TaskSpaces");
        Worked("Deleted workspace", Gone, @"C:\repos\TaskSpaces");

        Assert.Equal(Deskbar, Latest(@"C:\repos\TaskSpaces").Value);
    }

    [Fact]
    public void A_repo_the_board_has_never_seen_has_no_workspace()
    {
        Worked("Store listing", Deskbar, @"C:\repos\TaskSpaces");

        Assert.True(Latest(@"C:\scratch\probe").HasNoValue);
        Assert.True(Latest(@"C:\repos\TaskSpacesOld").HasNoValue);
    }
}
