using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests.Tasks;

// The task rules, pure and clock-free: every method takes "now" so these tests never sleep.
// Design: docs/superpowers/specs/2026-11-19-tasks-in-the-bar-design.md ("The model").
public class TaskBoardTests
{
    static readonly DateTimeOffset t0 = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);
    static readonly Guid ws = Guid.Parse("11111111-1111-1111-1111-111111111111");
    static readonly Guid other = Guid.Parse("22222222-2222-2222-2222-222222222222");
    static readonly Guid id1 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    static readonly Guid id2 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");

    [Fact]
    public void A_timer_runs_then_comes_due()
    {
        var (_, task) = TaskBoard.Empty.Park("Wait for CI", ws, TimeSpan.FromMinutes(5), t0, id1).Value;
        Assert.Equal(TaskState.Running, task.StateAt(t0.AddMinutes(4)));
        Assert.Equal(TaskState.Due, task.StateAt(t0.AddMinutes(5)));
    }

    [Fact]
    public void Parking_the_same_title_reuses_the_live_task()
    {
        var (b1, _) = TaskBoard.Empty.Park("Wait for CI", ws, TimeSpan.FromMinutes(5), t0, id1).Value;
        var (b2, again) = b1.Park(" wait for ci ", ws, TimeSpan.FromMinutes(10), t0.AddMinutes(1), id2).Value;
        Assert.Single(b2.Tasks);
        Assert.Equal(id1, again.Id);
        Assert.Equal(t0.AddMinutes(11), again.Timer.Value.Ends);
        Assert.Equal(TimerEnd.Replaced, again.History.Single().How);
    }

    [Fact]
    public void Parking_the_same_title_without_a_length_adds_nothing()
    {
        var (b1, first) = TaskBoard.Empty.Park("Wait for CI", ws, TimeSpan.FromMinutes(5), t0, id1).Value;
        var (b2, again) = b1.Park("wait for ci", ws, Maybe<TimeSpan>.None, t0.AddMinutes(1), id2).Value;
        Assert.Single(b2.Tasks);
        Assert.Equal(first, again);
        Assert.Equal(b1, b2);
    }

    [Fact]
    public void Free_tasks_never_match_so_each_park_is_new()
    {
        var b = TaskBoard.Empty.Park("a", Maybe<Guid>.None, Maybe<TimeSpan>.None, t0, id1).Value.Board
            .Park("a", Maybe<Guid>.None, Maybe<TimeSpan>.None, t0, id2).Value.Board;
        Assert.Equal(2, b.Tasks.Count);
    }

    [Fact]
    public void A_new_title_on_the_same_workspace_is_a_new_task()
    {
        var (b1, _) = TaskBoard.Empty.Park("Wait for CI", ws, TimeSpan.FromMinutes(5), t0, id1).Value;
        Assert.Equal(2, b1.Park("Tidy the release notes", ws, Maybe<TimeSpan>.None, t0, id2).Value.Board.LiveOn(ws).Count);
    }

    [Fact]
    public void The_latest_live_title_is_the_parking_default()
    {
        var b = TaskBoard.Empty.Park("a", ws, Maybe<TimeSpan>.None, t0, id1).Value.Board
            .Park("b", ws, Maybe<TimeSpan>.None, t0.AddMinutes(1), id2).Value.Board;
        Assert.Equal("b", b.LatestLiveOn(ws).Value.Title);
    }

    [Fact]
    public void An_empty_title_is_refused() =>
        Assert.True(TaskBoard.Empty.Park("   ", ws, Maybe<TimeSpan>.None, t0, id1).IsFailure);

    [Fact]
    public void Coming_back_ends_a_running_or_due_timer_and_leaves_the_task_open()
    {
        var (b1, _) = TaskBoard.Empty.Park("x", ws, TimeSpan.FromMinutes(1), t0, id1).Value;
        var task = b1.CameBack(ws, t0.AddSeconds(30)).Tasks.Single();
        Assert.Equal(TaskState.Open, task.StateAt(t0.AddMinutes(5)));
        Assert.Equal(TimerEnd.CameBack, task.History.Single().How);
    }

    [Fact]
    public void Coming_back_ends_every_timer_on_the_workspace()
    {
        var b = TaskBoard.Empty.Park("a", ws, TimeSpan.FromMinutes(5), t0, id1).Value.Board
            .Park("b", ws, TimeSpan.FromMinutes(30), t0, id2).Value.Board.CameBack(ws, t0.AddMinutes(1));
        Assert.All(b.Tasks, t => Assert.Equal(TaskState.Open, t.StateAt(t0.AddHours(1))));
    }

    [Fact]
    public void A_task_moves_between_workspaces() =>
        Assert.Equal(other, TaskBoard.Empty.Park("a", ws, Maybe<TimeSpan>.None, t0, id1).Value.Board
            .MoveTo(id1, other, t0).Value.Tasks.Single().WorkspaceId.Value);

    [Fact]
    public void Done_ends_the_timer_and_leaves_the_live_list()
    {
        var b = TaskBoard.Empty.Park("a", ws, TimeSpan.FromMinutes(5), t0, id1).Value.Board.Done(id1, t0.AddMinutes(1)).Value;
        Assert.Equal(TaskState.Done, b.Tasks.Single().StateAt(t0.AddMinutes(9)));
        Assert.Equal(TimerEnd.Done, b.Tasks.Single().History.Single().How);
        Assert.Empty(b.LiveOn(ws));
    }

    [Fact]
    public void Every_change_moves_UpdatedAt() =>
        Assert.Equal(t0.AddMinutes(2), TaskBoard.Empty.Park("a", ws, Maybe<TimeSpan>.None, t0, id1).Value.Board
            .Rename(id1, "b", t0.AddMinutes(2)).Value.Tasks.Single().UpdatedAt);

    [Fact]
    public void Unknown_ids_fail() => Assert.True(TaskBoard.Empty.Done(id1, t0).IsFailure);

    [Fact]
    public void Unknown_workspaces_free_their_tasks_and_keep_the_timer()
    {
        var b = TaskBoard.Empty.Park("a", ws, TimeSpan.FromMinutes(5), t0, id1).Value.Board.Prune(new HashSet<Guid>(), t0);
        Assert.True(b.Tasks.Single().WorkspaceId.HasNoValue);
        Assert.True(b.Tasks.Single().Timer.HasValue);
    }

    [Fact]
    public void Setting_and_clearing_a_timer_records_what_it_replaced()
    {
        var b = TaskBoard.Empty.Park("a", ws, TimeSpan.FromMinutes(5), t0, id1).Value.Board
            .SetTimer(id1, TimeSpan.FromMinutes(10), t0.AddMinutes(1)).Value
            .ClearTimer(id1, t0.AddMinutes(2)).Value;
        var task = b.Tasks.Single();
        Assert.True(task.Timer.HasNoValue);
        Assert.Equal(new[] { TimerEnd.Replaced, TimerEnd.Cleared }, task.History.Select(h => h.How));
    }

    [Fact]
    public void Delete_removes_the_task_and_fails_for_unknown_ids()
    {
        var b = TaskBoard.Empty.Park("a", ws, Maybe<TimeSpan>.None, t0, id1).Value.Board;
        Assert.Empty(b.Delete(id1).Value.Tasks);
        Assert.True(b.Delete(id2).IsFailure);
    }

    [Fact]
    public void Find_takes_a_full_id_or_a_unique_prefix_of_four_or_more()
    {
        var b = TaskBoard.Empty.Park("a", ws, Maybe<TimeSpan>.None, t0, id1).Value.Board
            .Park("b", ws, Maybe<TimeSpan>.None, t0, id2).Value.Board;
        Assert.Equal(id1, b.Find(id1.ToString()).Value.Id);
        Assert.True(b.Find("aaaaaaaa").IsFailure);          // shared by both, so ambiguous
        Assert.True(b.Find("aaaaaaaa-0000-0000-0000-00000000000").IsFailure); // still shared
        Assert.True(b.Find("aaa").IsFailure);               // under four characters
        Assert.True(b.Find("ffff").IsFailure);              // matches nothing
        Assert.Equal(id2, b.Find("aaaaaaaa-0000-0000-0000-000000000002").Value.Id);
    }
}
