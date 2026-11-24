using System.Collections.Immutable;
using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
using Xunit;
// System.Threading.Tasks (an implicit using) has a TaskStatus too; the alias says which one this file means.
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Tasks;

// STALLED TASKS (spec docs/superpowers/specs/2026-11-23-stalled-claude-tasks-design.md). Petre: "i want the tasks that
// haven't had any progress from claude and are still open to reappear for me to act upon". A Claude task no session has
// been busy on for 15 minutes is handed back to him; a busy session takes it back.
public sealed class StalledTasksTests
{
    static readonly DateTimeOffset t0 = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);
    static readonly Guid ws = Guid.Parse("11111111-1111-1111-1111-111111111111");
    static readonly Guid id1 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    static readonly Guid id2 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    static readonly ImmutableDictionary<Guid, DateTimeOffset> NoneBusy = ImmutableDictionary<Guid, DateTimeOffset>.Empty;
    static readonly IReadOnlySet<string> Nobody = new HashSet<string>();
    static readonly IReadOnlySet<string> S1Works = new HashSet<string> { "s1" };

    static TaskSession Session(string id) => new(id, @"C:\repos\TaskSpaces", t0, Maybe<DateTimeOffset>.None);

    // A Claude task created at t0, In progress and Claude's.
    static TaskBoard Claudes(TaskBoard board, Guid id, string session = "s1") =>
        board.Create("Tasks redesign", ws, Session(session), t0, id).Value.Board;

    static TaskItem Get(TaskBoard board, Guid id) => board.Tasks.Single(t => t.Id == id);

    static DateTimeOffset At(int minutes) => t0.AddMinutes(minutes);

    [Fact]
    public void Quiet_for_15_minutes_hands_the_task_back()
    {
        var step = StalledTasks.Step(Claudes(TaskBoard.Empty, id1), Nobody, Nobody, NoneBusy, At(15));

        Assert.Equal(Assignee.Me, Get(step.Board, id1).Assignee);
        Assert.True(Get(step.Board, id1).HandedBack.HasValue);
        Assert.Equal([id1], step.HandedBack);
        Assert.Empty(step.TakenBack);
    }

    [Fact]
    public void Quiet_for_14_minutes_keeps_it_with_Claude()
    {
        var board = Claudes(TaskBoard.Empty, id1);

        var step = StalledTasks.Step(board, Nobody, Nobody, NoneBusy, At(14));

        Assert.Same(board, step.Board);
        Assert.Empty(step.HandedBack);
    }

    [Fact]
    public void A_busy_session_keeps_it_with_Claude_and_restarts_the_clock()
    {
        var board = Claudes(TaskBoard.Empty, id1);

        var busy = StalledTasks.Step(board, S1Works, S1Works, NoneBusy, At(60));
        Assert.Same(board, busy.Board);
        Assert.Equal(At(60), busy.LastBusy[id1]);

        var tenLater = StalledTasks.Step(busy.Board, Nobody, Nobody, busy.LastBusy, At(70));
        Assert.Equal(Assignee.Claude, Get(tenLater.Board, id1).Assignee);

        var fifteenLater = StalledTasks.Step(tenLater.Board, Nobody, Nobody, tenLater.LastBusy, At(75));
        Assert.Equal(Assignee.Me, Get(fifteenLater.Board, id1).Assignee);
    }

    [Fact]
    public void After_a_restart_a_task_quiet_for_an_hour_comes_back_on_the_first_poll()
    {
        var step = StalledTasks.Step(Claudes(TaskBoard.Empty, id1), Nobody, Nobody, NoneBusy, At(60));

        Assert.Equal(Assignee.Me, Get(step.Board, id1).Assignee);
    }

    [Fact]
    public void A_status_line_update_counts_as_progress()
    {
        var board = Claudes(TaskBoard.Empty, id1).SetStatusLine(id1, "Halfway", At(10)).Value;

        var step = StalledTasks.Step(board, Nobody, Nobody, NoneBusy, At(20));

        Assert.Equal(Assignee.Claude, Get(step.Board, id1).Assignee);
    }

    [Fact]
    public void A_handed_back_task_goes_back_to_Claude_when_its_session_works()
    {
        var handed = StalledTasks.Step(Claudes(TaskBoard.Empty, id1), Nobody, Nobody, NoneBusy, At(15));

        var step = StalledTasks.Step(handed.Board, S1Works, S1Works, handed.LastBusy, At(16));

        Assert.Equal(Assignee.Claude, Get(step.Board, id1).Assignee);
        Assert.True(Get(step.Board, id1).HandedBack.HasNoValue);
        Assert.Equal([id1], step.TakenBack);
    }

    [Fact]
    public void A_task_Petre_assigned_to_himself_stays_his_when_the_session_works()
    {
        var board = Claudes(TaskBoard.Empty, id1).Assign(id1, Assignee.Me, t0).Value;

        var step = StalledTasks.Step(board, S1Works, S1Works, NoneBusy, At(1));

        Assert.Same(board, step.Board);
    }

    [Fact]
    public void Notes_and_tasks_waiting_on_Petre_are_left_alone()
    {
        var board = Claudes(TaskBoard.Empty, id1).Ask(id1, "OK?", t0).Value
            .AddNote("Dishwasher tablets", Maybe<Guid>.None, t0, id2).Value.Board
            .Assign(id2, Assignee.Claude, t0).Value;

        var step = StalledTasks.Step(board, Nobody, Nobody, NoneBusy, At(120));

        Assert.Same(board, step.Board);
        Assert.Equal(TaskStatus.WaitingOnYou, Get(step.Board, id1).Status);
    }

    // Final review, Important 1: right after a Deskbar restart every listed session is inside the busy grace. Busy inside
    // the grace must still keep a task with Claude (it only must not TAKE one back), or every task being worked on would
    // bounce onto the bar and off again on each restart.
    [Fact]
    public void Busy_inside_the_grace_keeps_the_task_with_Claude_but_takes_nothing_back()
    {
        var board = Claudes(TaskBoard.Empty, id1);

        var restarted = StalledTasks.Step(board, busy: S1Works, Nobody, NoneBusy, At(60));
        Assert.Same(board, restarted.Board);
        Assert.Equal(At(60), restarted.LastBusy[id1]);

        var handed = StalledTasks.Step(board, Nobody, Nobody, NoneBusy, At(60));
        var resumed = StalledTasks.Step(handed.Board, busy: S1Works, Nobody, handed.LastBusy, At(61));
        Assert.Equal(Assignee.Me, Get(resumed.Board, id1).Assignee);
    }

    // Final review, Important 2: a session that moved to another task leaves an ENDED entry on the old one. That entry
    // is not working on the old task, so it neither keeps it with Claude nor takes it back.
    [Fact]
    public void A_session_that_moved_to_another_task_does_not_count_on_the_old_one()
    {
        var board = Claudes(TaskBoard.Empty, id1)
            .Create("Next thing", ws, Session("s1"), At(1), id2).Value.Board;
        Assert.False(Get(board, id1).HasLiveSession);

        var step = StalledTasks.Step(board, S1Works, S1Works, NoneBusy, At(30));
        Assert.Equal(Assignee.Me, Get(step.Board, id1).Assignee);
        Assert.Equal(Assignee.Claude, Get(step.Board, id2).Assignee);

        var later = StalledTasks.Step(step.Board, S1Works, S1Works, step.LastBusy, At(31));
        Assert.Equal(Assignee.Me, Get(later.Board, id1).Assignee);
    }

    [Fact]
    public void The_clock_forgets_tasks_it_no_longer_watches()
    {
        var busy = StalledTasks.Step(Claudes(TaskBoard.Empty, id1), S1Works, S1Works, NoneBusy, At(1));
        var closed = busy.Board.Close(id1, At(2), stoppingClaude: true).Value;

        var step = StalledTasks.Step(closed, Nobody, Nobody, busy.LastBusy, At(3));

        Assert.False(step.LastBusy.ContainsKey(id1));
    }
}
