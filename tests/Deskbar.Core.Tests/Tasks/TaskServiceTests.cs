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
            () => s.SetFlag(note.Id, TaskFlag.Important, true),
            () => s.Place(claudes.Id, Quadrant.I, TaskPlace.Before(note.Id)),
            () => s.Close(note.Id),
            () => s.UndoClose(note.Id, TaskStatus.Backlog, Maybe<string>.None),
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

    // Review of PR B, M-3: a session may set the same status line again (each prompt's reminder asks it to keep it
    // current); the same text writes nothing and redraws nothing. A closed task still refuses it.
    [Fact]
    public void The_same_status_line_again_saves_and_pulses_nothing()
    {
        var s = NewService();
        var task = s.Create("Tasks redesign", ws, Session("s1", clock)).Value;
        Assert.True(s.SetStatusLine(task.Id, "waiting for CI").IsSuccess);
        var saves = store.Saves;
        var pulses = 0;
        using var sub = s.Changed.Subscribe(_ => pulses++);

        Assert.True(s.SetStatusLine(task.Id, "  waiting for CI ").IsSuccess);

        Assert.Equal(saves, store.Saves);
        Assert.Equal(0, pulses);
        Assert.True(s.SetStatusLine(task.Id, "merged").IsSuccess);
        Assert.Equal(saves + 1, store.Saves);
        Assert.True(s.Close(task.Id).IsSuccess);
        Assert.True(s.SetStatusLine(task.Id, "merged").IsFailure);
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

    // Goals plan, Task 2: the link to a goal is one more change on the task. Setting the goal it already has, or
    // detaching a goal no task names, is no change: no save, no pulse.
    [Fact]
    public void Setting_a_goal_saves_and_pulses_once_and_not_again_for_the_same_goal()
    {
        var s = NewService();
        var note = s.AddNote("Dishwasher tablets", ws).Value;
        var goal = Guid.NewGuid();
        var saves = store.Saves;
        var pulses = 0;
        using var sub = s.Changed.Subscribe(_ => pulses++);

        Assert.True(s.SetGoal(note.Id, goal).IsSuccess);
        Assert.True(s.SetGoal(note.Id, goal).IsSuccess);

        Assert.Equal(saves + 1, store.Saves);
        Assert.Equal(1, pulses);
        Assert.Equal(goal, store.Stored.Tasks.Single().GoalId.Value);

        s.DetachGoal(Guid.NewGuid());
        Assert.Equal(saves + 1, store.Saves);
        s.DetachGoal(goal);
        Assert.Equal(saves + 2, store.Saves);
        Assert.Equal(2, pulses);
        Assert.True(s.Board.Tasks.Single().GoalId.HasNoValue);
        Assert.True(s.SetGoal(Guid.NewGuid(), goal).IsFailure);
        Assert.Equal(saves + 2, store.Saves);
    }

    // The poll runs every few seconds and nearly always finds nothing to change: it must not save or pulse then.
    [Fact]
    public void Reconcile_saves_and_pulses_only_when_a_session_changed()
    {
        var s = NewService();
        s.Create("Tasks redesign", ws, Session("s1", clock));
        var pulses = 0;
        using var _ = s.Changed.Subscribe(_ => pulses++);
        var saves = store.Saves;
        var agent = new ClaudeAgent("s1", Maybe<string>.None, @"C:\repos\TaskSpaces", true, false, "");

        s.Reconcile([agent]);
        Assert.Equal((saves, 0), (store.Saves, pulses));

        s.Reconcile([]);
        Assert.Equal((saves + 1, 1), (store.Saves, pulses));
        Assert.False(s.Board.Tasks.Single().HasLiveSession);
    }

    [Fact]
    public void Adopt_makes_a_task_saves_it_and_refuses_a_session_twice()
    {
        var s = NewService();
        var agent = new ClaudeAgent("s9", Maybe<string>.None, @"C:\repos\TaskSpaces", true, false, "Fix the strip");

        var task = s.Adopt(agent, ws).Value;

        Assert.Equal("Fix the strip", task.Title);
        Assert.Equal(task, store.Stored.Tasks.Single());
        Assert.True(s.Adopt(agent, ws).IsFailure);
        Assert.Single(s.Board.Tasks);
    }

    // Stalled tasks (spec 2026-11-23-stalled-claude-tasks-design.md): the service keeps the quiet clock between polls,
    // saves only when a task changed hands, and says so in the trace.
    [Fact]
    public void Stalled_keeps_the_clock_saves_on_a_hand_back_and_traces_it()
    {
        var traced = new List<string>();
        var s = new TaskService(store, () => clock, trace: traced.Add);
        var task = s.Create("Tasks redesign", ws, Session("s1", clock)).Value;
        var busy = new HashSet<string> { "s1" };
        var nobody = new HashSet<string>();

        clock = clock.AddMinutes(60);
        var saves = store.Saves;
        s.Stalled(busy, busy);                // busy an hour after creation: the clock starts now, nothing saved
        Assert.Equal(saves, store.Saves);

        clock = clock.AddMinutes(10);
        s.Stalled(nobody, nobody);              // quiet for 10 minutes since the busy poll: still Claude's
        Assert.Equal(Assignee.Claude, s.Board.Tasks.Single().Assignee);
        Assert.Equal(saves, store.Saves);

        clock = clock.AddMinutes(5);
        s.Stalled(nobody, nobody);              // 15 minutes: handed back, saved, traced
        Assert.Equal(Assignee.Me, s.Board.Tasks.Single().Assignee);
        Assert.Equal(saves + 1, store.Saves);
        Assert.Contains(traced, line => line.Contains("handed back") && line.Contains(task.Id.ToString()[..8]));

        s.Stalled(busy, busy);                // the session works again: taken back
        Assert.Equal(Assignee.Claude, s.Board.Tasks.Single().Assignee);
        Assert.Contains(traced, line => line.Contains("taken back"));
    }

    // Images in tasks (docs/superpowers/specs/2026-11-24-task-images-design.md): deleting a task deletes its image folder,
    // task-images\<id> under the state folder, so a deleted task leaves no pictures behind. Closing keeps them.
    [Fact]
    public void Delete_removes_the_tasks_image_folder()
    {
        var root = Directory.CreateTempSubdirectory("deskbar-images-").FullName;
        try
        {
            var s = new TaskService(store, () => clock, filesRoot: root);
            var note = s.AddNote("Screenshot", ws).Value;
            var folder = Path.Combine(root, TaskImages.Folder, note.Id.ToString());
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "a.png"), [1, 2, 3]);

            Assert.True(s.Delete(note.Id).IsSuccess);

            Assert.False(Directory.Exists(folder));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Delete_without_an_image_folder_succeeds()
    {
        var root = Directory.CreateTempSubdirectory("deskbar-images-").FullName;
        try
        {
            var s = new TaskService(store, () => clock, filesRoot: root);
            var note = s.AddNote("No pictures", ws).Value;

            Assert.True(s.Delete(note.Id).IsSuccess);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ExpandImages_makes_image_paths_absolute_under_the_files_root()
    {
        var s = new TaskService(store, () => clock, filesRoot: @"C:\S");

        Assert.Equal(@"![image](<C:\S\task-images\t\1.png>)", s.ExpandImages("![image](task-images/t/1.png)"));
        // With no files root (a test, or a service built before the state folder is known) the markdown is as stored.
        Assert.Equal("![image](task-images/t/1.png)", NewService().ExpandImages("![image](task-images/t/1.png)"));
    }

    // A comment wakes its session (plan 2026-11-24-comment-wakes-session, Task 2): only Petre's own new comments are
    // announced. A session's comment, Deskbar's own line, an edit and a comment marked not to wake are not.
    [Fact]
    public void Only_his_own_new_comments_are_announced()
    {
        var s = NewService();
        var note = s.AddNote("Paste images", ws).Value;
        var heard = new List<(Guid Task, string Markdown)>();
        using var _ = s.Commented.Subscribe(c => heard.Add((c.TaskId, c.Comment.Markdown)));

        s.Comment(note.Id, Maybe<string>.None, "his");
        s.Comment(note.Id, "s1", "a session's");
        s.Note(note.Id, "Deskbar's line");
        s.Comment(note.Id, Maybe<string>.None, "his, by a session", wakes: false);
        s.EditComment(s.Board.Tasks.Single().Comments[0].Id, "his, edited");

        Assert.Equal([(note.Id, "his")], heard);
    }

    [Fact]
    public void A_refused_comment_is_not_announced()
    {
        var s = NewService();
        var heard = 0;
        using var _ = s.Commented.Subscribe(_ => heard++);

        Assert.True(s.Comment(Guid.NewGuid(), Maybe<string>.None, "to no task").IsFailure);

        Assert.Equal(0, heard);
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
