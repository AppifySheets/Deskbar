using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
using Xunit;
// System.Threading.Tasks (an implicit using) has a TaskStatus too; the alias says which one this file means.
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Tasks;

// TaskService: the live board, saved on every change and announced through Changed. Design:
// docs/superpowers/specs/2026-11-22-tasks-board-design.md. The clock is a captured field the test moves, so
// nothing here waits.
public sealed class TaskServiceTests
{
    DateTimeOffset clock = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
    readonly FakeTaskStore store = new();
    readonly Guid ws = Guid.NewGuid();
    readonly Guid other = Guid.NewGuid();

    TaskService NewService() => new(store, () => clock);

    static TaskSession Session(string id, DateTimeOffset at) => new(id, @"C:\repos\TaskSpaces", at, Maybe<DateTimeOffset>.None);

    // Every change the service offers, each run once against a board that has a note and a Claude task, counting a
    // save and a pulse for each.
    [Fact]
    public void Every_change_saves_and_pulses()
    {
        var s = NewService();
        var note = s.AddNote("Dishwasher tablets", ws).Value;
        var claudes = s.Create("Tasks redesign", ws, Session("s1", clock)).Value;
        var changes = new Func<Result>[]
        {
            () => s.Join(note.Id, Session("s2", clock)),
            () => s.SetStatusLine(claudes.Id, "waiting for CI"),
            () => s.Ask(claudes.Id, "OK to merge?"),
            () => { s.Answered("s1"); return Result.Success(); },
            () => s.MarkDone(claudes.Id),
            () => s.Close(claudes.Id),
            () => s.Reopen(claudes.Id),
            () => s.SetStatus(claudes.Id, TaskStatus.Backlog),
            () => s.Rename(note.Id, "Soap"),
            () => s.MoveTo(note.Id, other),
            () => s.Comment(note.Id, Maybe<string>.None, "Get the big box"),
            () => s.SetFlag(note.Id, TaskFlag.Urgent, true),
            () => s.SetQuadrant(note.Id, Quadrant.II),
            () => s.Place(claudes.Id, Quadrant.II, TaskPlace.Before(note.Id)),
            () => s.Close(note.Id),
            () => s.UndoClose(note.Id, TaskStatus.Backlog),
            () => { s.SessionEnded("s2"); return Result.Success(); },
            () => { s.Prune(new HashSet<Guid>()); return Result.Success(); },
            () => s.Delete(note.Id),
        };
        var pulses = 0;
        using var sub = s.Changed.Subscribe(_ => pulses++);

        changes.Select((change, i) => (change, i)).ToList().ForEach(c =>
        {
            var saves = store.Saves;
            var before = pulses;
            Assert.True(c.change().IsSuccess, $"change {c.i}");
            Assert.Equal(saves + 1, store.Saves);
            Assert.Equal(before + 1, pulses);
        });
        Assert.Equal(s.Board, store.Stored);
    }

    // A toggle set to what it already is, or a drop where the task already was, is no change: no save, no pulse,
    // so the bar does not redraw under a click that went nowhere.
    [Fact]
    public void A_flag_or_a_place_that_changes_nothing_saves_nothing()
    {
        var s = NewService();
        var note = s.AddNote("Dishwasher tablets", ws).Value;
        var saves = store.Saves;
        var pulses = 0;
        using var sub = s.Changed.Subscribe(_ => pulses++);

        Assert.True(s.SetFlag(note.Id, TaskFlag.Important, false).IsSuccess);
        Assert.True(s.SetQuadrant(note.Id, Quadrant.IV).IsSuccess);
        Assert.True(s.Place(note.Id, Quadrant.IV, TaskPlace.Top).IsSuccess);

        Assert.Equal(saves, store.Saves);
        Assert.Equal(0, pulses);
    }

    [Fact]
    public void A_refused_change_saves_nothing()
    {
        var s = NewService();
        var note = s.AddNote("Dishwasher tablets", ws).Value;
        var saves = store.Saves;

        Assert.True(s.Reopen(note.Id).IsFailure);
        Assert.True(s.Rename(Guid.NewGuid(), "x").IsFailure);
        Assert.Equal(saves, store.Saves);
    }

    [Fact]
    public void A_failed_save_keeps_the_change_in_memory()
    {
        store.FailSave = true;
        var traced = new List<string>();
        var s = new TaskService(store, () => clock, trace: traced.Add);

        Assert.True(s.AddNote("Dishwasher tablets", ws).IsSuccess);
        Assert.Single(s.Board.Tasks);
        Assert.Contains(traced, line => line.Contains("save failed"));
    }

    [Fact]
    public void A_failed_load_leaves_the_board_empty_and_returns_the_failure()
    {
        store.FailLoad = true;
        var s = NewService();

        Assert.True(s.Load().IsFailure);
        Assert.Empty(s.Board.Tasks);
    }

    // The task UI says when tasks.json could not be read (it starts empty then, and that must not look like
    // "nothing to do"), until Petre dismisses it or a later load works.
    [Fact]
    public void A_failed_load_is_reported_until_dismissed()
    {
        var s = NewService();
        Assert.True(s.LoadProblem.HasNoValue);

        store.FailLoad = true;
        s.Load();
        Assert.Equal("load failed", s.LoadProblem.Value);

        s.DismissLoadProblem();
        Assert.True(s.LoadProblem.HasNoValue);

        s.Load();
        Assert.True(s.LoadProblem.HasValue);
        store.FailLoad = false;
        s.Load();
        Assert.True(s.LoadProblem.HasNoValue);
    }

    // Answered and SessionEnded run on every prompt and every session end, nearly always for a session with no task
    // or nothing to change; those must not write tasks.json or redraw the bar.
    [Fact]
    public void Answering_an_unknown_session_saves_nothing()
    {
        var s = NewService();
        s.Create("Tasks redesign", ws, Session("s1", clock));
        var saves = store.Saves;
        var pulses = 0;
        using var sub = s.Changed.Subscribe(_ => pulses++);

        s.Answered("s9");
        s.Answered("s1");
        s.SessionEnded("s9");

        Assert.Equal(saves, store.Saves);
        Assert.Equal(0, pulses);
    }

    [Fact]
    public void Prune_saves_only_when_a_workspace_went()
    {
        var s = NewService();
        s.AddNote("Dishwasher tablets", ws);
        var saves = store.Saves;

        s.Prune(new HashSet<Guid> { ws });
        Assert.Equal(saves, store.Saves);

        s.Prune(new HashSet<Guid> { other });
        Assert.Equal(saves + 1, store.Saves);
        Assert.True(s.Board.Tasks.Single().WorkspaceId.HasNoValue);
    }

    [Fact]
    public void New_ids_come_from_the_given_source()
    {
        var ids = new Queue<Guid>([Guid.Parse("abcd0000-0000-0000-0000-000000000001"), Guid.Parse("abcd0000-0000-0000-0000-000000000002")]);
        var s = new TaskService(store, () => clock, ids.Dequeue);

        var note = s.AddNote("Dishwasher tablets", ws).Value;
        s.Comment(note.Id, Maybe<string>.None, "Big box");

        Assert.Equal(Guid.Parse("abcd0000-0000-0000-0000-000000000001"), note.Id);
        Assert.Equal(Guid.Parse("abcd0000-0000-0000-0000-000000000002"), s.Board.Tasks.Single().Comments.Single().Id);
    }

    sealed class FakeTaskStore : ITaskStore
    {
        public TaskBoard Stored { get; set; } = TaskBoard.Empty;
        public int Saves { get; private set; }
        public bool FailSave { get; set; }
        public bool FailLoad { get; set; }

        public Result<TaskBoard> Load() => FailLoad ? Result.Failure<TaskBoard>("load failed") : Stored;

        public Result Save(TaskBoard board)
        {
            Saves++;
            if (FailSave) return Result.Failure("save failed");
            Stored = board;
            return Result.Success();
        }
    }
}
