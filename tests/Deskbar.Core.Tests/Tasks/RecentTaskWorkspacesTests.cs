using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Tasks;

// The workspaces the task list's workspace menus put in their Recent row (task 46eddcb0): those of the newest tasks,
// by when each task was made, newest first, each once.
public class RecentTaskWorkspacesTests
{
    static readonly DateTimeOffset T0 = new(2026, 11, 28, 8, 0, 0, TimeSpan.Zero);

    // A task made `minutes` after T0 in `workspace`, or a free task. UpdatedAt is far in the future on purpose for the
    // oldest one, to show that only Created counts.
    static TaskItem Task(Maybe<Guid> workspace, int minutes, int updatedMinutes = 0) =>
        new(Guid.NewGuid(), "t", workspace, TaskStatus.InProgress, "", Maybe<string>.None,
            T0.AddMinutes(minutes), T0.AddMinutes(Math.Max(minutes, updatedMinutes)), T0.AddMinutes(minutes), [], []);

    [Fact]
    public void The_newest_tasks_workspaces_come_first_each_once()
    {
        var (a, b, c) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var tasks = new[] { Task(a, 1), Task(b, 2), Task(a, 3), Task(c, 4), Task(b, 5) };

        Assert.Equal([b, c, a], RecentTaskWorkspaces.Of(tasks, _ => true));
    }

    [Fact]
    public void A_status_line_or_other_update_does_not_make_a_workspace_recent()
    {
        var (old, fresh) = (Guid.NewGuid(), Guid.NewGuid());
        var tasks = new[] { Task(old, 1, updatedMinutes: 500), Task(fresh, 2) };

        Assert.Equal([fresh, old], RecentTaskWorkspaces.Of(tasks, _ => true));
    }

    [Fact]
    public void Free_tasks_and_turned_down_workspaces_take_no_place()
    {
        var (gone, kept1, kept2) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var tasks = new[] { Task(kept2, 1), Task(kept1, 2), Task(gone, 3), Task(Maybe<Guid>.None, 4) };

        Assert.Equal([kept1, kept2], RecentTaskWorkspaces.Of(tasks, id => id != gone, count: 2));
    }

    [Fact]
    public void At_most_count_workspaces()
    {
        var tasks = Enumerable.Range(0, 9).Select(i => Task(Guid.NewGuid(), i)).ToArray();

        Assert.Equal(RecentTaskWorkspaces.Count, RecentTaskWorkspaces.Of(tasks, _ => true).Count);
        Assert.Empty(RecentTaskWorkspaces.Of([], _ => true));
    }
}
