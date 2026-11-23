using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
using Xunit;
// System.Threading.Tasks (an implicit using) has a TaskStatus too; the alias says which one this file means.
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Tasks;

// RULING 57, the assignee: who a task is on. Petre: "tasks that don't need my attention are assigned to claude; others
// are assigned to me"; "tasks i create are automatically assigned to me, but i can assign them to claude as well";
// "in the future i'll have my colleagues in this task management thing". So the assignee is a person (Assignee), and
// the rules below move it as a session works.
public sealed class TaskAssigneeTests : IDisposable
{
    DateTimeOffset clock = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);
    static readonly Guid ws = Guid.Parse("11111111-1111-1111-1111-111111111111");
    static readonly Guid id1 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    static readonly Guid id2 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    static readonly Guid id3 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");

    readonly string dir = Path.Combine(Path.GetTempPath(), "deskbar-assignee-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    TaskSession Session(string id) => new(id, @"C:\repos\TaskSpaces", clock, Maybe<DateTimeOffset>.None);

    TaskBoard Note(TaskBoard board, Guid id, Maybe<Guid> workspace = default) =>
        board.AddNote("Dishwasher tablets", workspace, clock, id).Value.Board;

    TaskBoard Claudes(TaskBoard board, Guid id, string session = "s1") =>
        board.Create("Tasks redesign", ws, Session(session), clock, id).Value.Board;

    static TaskItem Get(TaskBoard board, Guid id) => board.Tasks.Single(t => t.Id == id);

    [Fact]
    public void A_note_Petre_creates_is_assigned_to_him() =>
        Assert.Equal(Assignee.Me, Get(Note(TaskBoard.Empty, id1), id1).Assignee);

    [Fact]
    public void A_session_task_is_assigned_to_Claude_while_it_works() =>
        Assert.Equal(Assignee.Claude, Get(Claudes(TaskBoard.Empty, id1), id1).Assignee);

    [Fact]
    public void Ask_gives_the_task_to_Petre_and_answered_gives_it_back_to_Claude()
    {
        var board = Claudes(TaskBoard.Empty, id1).Ask(id1, "OK?", clock).Value;
        Assert.Equal(Assignee.Me, Get(board, id1).Assignee);

        board = board.Answered("s1", clock);
        Assert.Equal(Assignee.Claude, Get(board, id1).Assignee);
    }

    [Fact]
    public void Done_gives_the_task_to_Petre_and_his_next_message_gives_it_back()
    {
        var board = Claudes(TaskBoard.Empty, id1).MarkDone(id1, clock).Value;
        Assert.Equal(Assignee.Me, Get(board, id1).Assignee);

        board = board.Answered("s1", clock);
        Assert.Equal(TaskStatus.InProgress, Get(board, id1).Status);
        Assert.Equal(Assignee.Claude, Get(board, id1).Assignee);
    }

    [Fact]
    public void A_session_joining_a_note_takes_it_over_but_a_waiting_task_stays_with_Petre()
    {
        var board = Note(TaskBoard.Empty, id1).Join(id1, Session("s1"), clock).Value;
        Assert.Equal(Assignee.Claude, Get(board, id1).Assignee);

        var waiting = board.Ask(id1, "OK?", clock).Value.Join(id1, Session("s2"), clock).Value;
        Assert.Equal(TaskStatus.WaitingOnYou, Get(waiting, id1).Status);
        Assert.Equal(Assignee.Me, Get(waiting, id1).Assignee);
    }

    [Fact]
    public void Petre_can_reassign_any_task_and_the_same_assignee_changes_nothing()
    {
        var board = Note(TaskBoard.Empty, id1);

        var toClaude = board.Assign(id1, Assignee.Claude, clock.AddMinutes(1)).Value;
        Assert.Equal(Assignee.Claude, Get(toClaude, id1).Assignee);
        Assert.Equal(clock.AddMinutes(1), Get(toClaude, id1).UpdatedAt);

        Assert.Same(toClaude, toClaude.Assign(id1, Assignee.Claude, clock.AddMinutes(2)).Value);
        Assert.True(board.Assign(id2, Assignee.Me, clock).IsFailure);
    }

    [Fact]
    public void A_colleague_is_an_assignee_by_id_and_name()
    {
        var anna = Assignee.Colleague("anna", "Anna");

        var board = Note(TaskBoard.Empty, id1).Assign(id1, anna, clock).Value;

        Assert.Equal(AssigneeKind.Colleague, Get(board, id1).Assignee.Kind);
        Assert.Equal("anna", Get(board, id1).Assignee.Id);
        Assert.Equal("Anna", Get(board, id1).Assignee.Name);
    }

    [Fact]
    public void A_note_assigned_to_Claude_is_offered_to_a_session_on_its_workspace_and_one_of_his_is_not()
    {
        var board = Note(TaskBoard.Empty, id1, ws);
        board = Note(board, id2, ws).Assign(id2, Assignee.Claude, clock).Value;
        board = Note(board, id3, Maybe<Guid>.None).Assign(id3, Assignee.Claude, clock).Value;

        // id3 is on no workspace and has no session, so it is an errand and stays out (as before).
        Assert.Equal([id2], board.Offered(ws).Select(t => t.Id));
    }

    // --- tasks.json ---------------------------------------------------------------------------------

    TaskBoard RoundTrip(TaskBoard board)
    {
        var store = new JsonTaskStore(dir);
        Assert.True(store.Save(board).IsSuccess);
        return new JsonTaskStore(dir).Load().Value;
    }

    [Fact]
    public void The_assignee_round_trips_including_a_colleague()
    {
        var board = Note(TaskBoard.Empty, id1).Assign(id1, Assignee.Claude, clock).Value;
        board = Note(board, id2).Assign(id2, Assignee.Colleague("anna", "Anna"), clock).Value;

        var loaded = RoundTrip(board);

        Assert.Equal(Assignee.Claude, Get(loaded, id1).Assignee);
        Assert.Equal(Assignee.Colleague("anna", "Anna"), Get(loaded, id2).Assignee);
    }

    // A task written before the field existed: a Claude task (it has sessions) that is not waiting, Done or Closed goes
    // to Claude; everything else to Petre. No version bump: the field is optional in version 2.
    [Fact]
    public void A_task_without_an_assignee_gets_one_derived_from_its_state()
    {
        Directory.CreateDirectory(dir);
        string Task(Guid id, string status, bool session) =>
            $$"""{"id":"{{id}}","title":"t","status":"{{status}}","created":"2026-11-22T09:00:00Z","updatedAt":"2026-11-22T09:00:00Z","sessions":[{{(session ? """{"id":"s","folder":"f","joined":"2026-11-22T09:00:00Z"}""" : "")}}]}""";
        var other = Guid.NewGuid();
        var another = Guid.NewGuid();
        var yetAnother = Guid.NewGuid();
        File.WriteAllText(Path.Combine(dir, "tasks.json"),
            $$"""{"version":2,"tasks":[{{Task(id1, "InProgress", true)}},{{Task(id2, "Backlog", false)}},{{Task(id3, "WaitingOnYou", true)}},{{Task(other, "Done", true)}},{{Task(another, "Closed", true)}},{{Task(yetAnother, "InProgress", false)}}]}""");

        var board = new JsonTaskStore(dir).Load().Value;

        Assert.Equal(Assignee.Claude, Get(board, id1).Assignee);
        Assert.Equal(Assignee.Me, Get(board, id2).Assignee);
        Assert.Equal(Assignee.Me, Get(board, id3).Assignee);
        Assert.Equal(Assignee.Me, Get(board, other).Assignee);
        Assert.Equal(Assignee.Me, Get(board, another).Assignee);
        Assert.Equal(Assignee.Me, Get(board, yetAnother).Assignee);
    }

    // --- the window's filter ------------------------------------------------------------------------

    [Fact]
    public void The_windows_assignee_filter_narrows_the_groups_and_the_matrix()
    {
        var board = Note(TaskBoard.Empty, id1);
        board = Claudes(board, id2);
        var all = TaskWorkspaceFilter.All;

        Assert.Equal(2, TaskViews.Grouped(board, TaskFilter.Open, all).SelectMany(g => g.Tasks).Count());
        Assert.Equal([id1], TaskViews.Grouped(board, TaskFilter.Open, all, TaskAssigneeFilter.Me).SelectMany(g => g.Tasks).Select(t => t.Id));
        Assert.Equal([id2], TaskViews.Grouped(board, TaskFilter.Open, all, TaskAssigneeFilter.Claude).SelectMany(g => g.Tasks).Select(t => t.Id));
        Assert.Equal([id2], TaskViews.Matrix(board, TaskFilter.Open, all, TaskAssigneeFilter.Claude).SelectMany(c => c.Tasks).Select(t => t.Id));
    }

    // --- review fixes: the hand paths keep the assignee honest ----------------------------------------------------------

    [Fact]
    public void Setting_waiting_or_done_by_hand_assigns_Petre()
    {
        var working = Claudes(TaskBoard.Empty, id1);
        Assert.Equal(Assignee.Claude, Get(working, id1).Assignee);

        Assert.Equal(Assignee.Me, Get(working.SetStatus(id1, TaskStatus.WaitingOnYou, clock).Value, id1).Assignee);
        Assert.Equal(Assignee.Me, Get(working.SetStatus(id1, TaskStatus.Done, clock).Value, id1).Assignee);
    }

    // Leaving Waiting or Done by hand for in progress or the backlog hands a Claude task back to Claude, so it does not
    // stay on the bar as his.
    [Fact]
    public void Setting_in_progress_by_hand_after_waiting_gives_a_claude_task_back()
    {
        var waiting = Claudes(TaskBoard.Empty, id1).Ask(id1, "OK?", clock).Value;
        Assert.Equal(Assignee.Me, Get(waiting, id1).Assignee);

        Assert.Equal(Assignee.Claude, Get(waiting.SetStatus(id1, TaskStatus.InProgress, clock).Value, id1).Assignee);
        // A note of his stays his.
        var note = Note(TaskBoard.Empty, id2).SetStatus(id2, TaskStatus.WaitingOnYou, clock).Value.SetStatus(id2, TaskStatus.InProgress, clock).Value;
        Assert.Equal(Assignee.Me, Get(note, id2).Assignee);
    }

    [Fact]
    public void Reopening_derives_the_assignee_from_what_the_task_is_now()
    {
        // A session still live on it: in progress, Claude's.
        var live = Claudes(TaskBoard.Empty, id1).Close(id1, clock).Value;
        Assert.Equal(Assignee.Me, Assignee.DerivedFor(false, TaskStatus.Backlog)); // the rule's two ends
        Assert.Equal(Assignee.Claude, Get(live.Reopen(id1, clock).Value, id1).Assignee);

        // No live session: back in the backlog, his.
        var ended = Claudes(TaskBoard.Empty, id2).Assign(id2, Assignee.Claude, clock).Value.SessionEnded("s1", clock).Close(id2, clock).Value;
        Assert.Equal(Assignee.Me, Get(ended.Reopen(id2, clock).Value, id2).Assignee);
    }

    [Fact]
    public void Assigning_a_waiting_task_to_Claude_leaves_it_waiting_with_its_question()
    {
        var waiting = Claudes(TaskBoard.Empty, id1).Ask(id1, "OK?", clock).Value;

        var handed = Get(waiting.Assign(id1, Assignee.Claude, clock).Value, id1);

        Assert.Equal(TaskStatus.WaitingOnYou, handed.Status);
        Assert.Equal("OK?", handed.Question.Value);
        Assert.Equal(Assignee.Claude, handed.Assignee);
    }

    [Fact]
    public void Offering_follows_the_assignee_alone_so_assign_to_me_withdraws_it()
    {
        var board = Note(TaskBoard.Empty, id1, ws).Assign(id1, Assignee.Claude, clock).Value;
        board = Claudes(board, id2); // a Claude task on ws, assigned to Claude
        Assert.Equal(new[] { id1, id2 }.Order(), board.Offered(ws).Select(t => t.Id).Order());

        board = board.Assign(id2, Assignee.Me, clock).Value;
        Assert.Equal([id1], board.Offered(ws).Select(t => t.Id));
        // A free Claude task he took back is not offered either.
        var free = Claudes(TaskBoard.Empty, id3, "s3").Assign(id3, Assignee.Me, clock).Value;
        Assert.Empty(free.Offered(Maybe<Guid>.None));
    }

    // --- tasks.json tolerates a wrong-typed value: absent or derived, never a failed file --------------------------------

    string Written(string taskFields)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "tasks.json"),
            $$"""{"version":2,"tasks":[{"id":"{{id1}}","title":"t","status":"InProgress","created":"2026-11-22T09:00:00Z","updatedAt":"2026-11-22T09:00:00Z","sessions":[{"id":"s","folder":"f","joined":"2026-11-22T09:00:00Z"}]{{taskFields}}}]}""");
        return dir;
    }

    [Theory]
    [InlineData(",\"assignee\":\"me\"")]
    [InlineData(",\"assignee\":{\"kind\":5}")]
    [InlineData(",\"assignee\":[1,2]")]
    [InlineData(",\"assignee\":{\"kind\":\"claude\",\"id\":7}")]
    [InlineData(",\"assignee\":5")]
    public void A_wrong_typed_assignee_is_derived_not_fatal(string fields)
    {
        var board = new JsonTaskStore(Written(fields.Replace("\\\"", "\""))).Load();

        Assert.True(board.IsSuccess, board.IsFailure ? board.Error : "");
        // A Claude task in progress derives to Claude, whatever the garbage said.
        Assert.Equal(Assignee.Claude, Get(board.Value, id1).Assignee);
    }

    [Theory]
    [InlineData(",\"description\":5")]
    [InlineData(",\"description\":{\"a\":1}")]
    [InlineData(",\"description\":[\"x\"]")]
    [InlineData(",\"description\":true")]
    public void A_wrong_typed_description_reads_as_none(string fields)
    {
        var board = new JsonTaskStore(Written(fields.Replace("\\\"", "\""))).Load();

        Assert.True(board.IsSuccess, board.IsFailure ? board.Error : "");
        Assert.Equal("", Get(board.Value, id1).Description);
    }

    [Theory]
    [InlineData("\"garbage\"")]
    [InlineData("5")]
    [InlineData("{\"a\":1}")]
    [InlineData("true")]
    public void A_wrong_typed_settled_time_reads_as_not_settled(string settled)
    {
        var comment = $$"""{"id":"{{Guid.NewGuid()}}","at":"2026-11-22T09:00:00Z","markdown":"x","settled":{{settled}}}""";
        var board = new JsonTaskStore(Written($$""","comments":[{{comment}}]""")).Load();

        Assert.True(board.IsSuccess, board.IsFailure ? board.Error : "");
        Assert.True(Get(board.Value, id1).Comments.Single().Settled.HasNoValue);
    }

    // --- stalled tasks (spec 2026-11-23-stalled-claude-tasks-design.md): the handed-back mark --------------------------

    [Fact]
    public void HandBack_gives_a_quiet_task_to_Petre_and_marks_it()
    {
        var task = Get(Claudes(TaskBoard.Empty, id1).HandBack(id1, clock), id1);

        Assert.Equal(Assignee.Me, task.Assignee);
        Assert.Equal(clock, task.HandedBack.Value);
        Assert.Equal(TaskStatus.InProgress, task.Status);
    }

    [Fact]
    public void TakeBack_returns_a_handed_back_task_to_Claude_and_clears_the_mark()
    {
        var task = Get(Claudes(TaskBoard.Empty, id1).HandBack(id1, clock).TakeBack(id1, clock), id1);

        Assert.Equal(Assignee.Claude, task.Assignee);
        Assert.True(task.HandedBack.HasNoValue);
    }

    [Fact]
    public void TakeBack_leaves_a_task_Petre_assigned_to_himself()
    {
        var board = Claudes(TaskBoard.Empty, id1).Assign(id1, Assignee.Me, clock).Value;

        Assert.Same(board, board.TakeBack(id1, clock));
    }

    [Fact]
    public void Assigning_a_handed_back_task_to_himself_clears_the_mark()
    {
        var task = Get(Claudes(TaskBoard.Empty, id1).HandBack(id1, clock).Assign(id1, Assignee.Me, clock).Value, id1);

        Assert.True(task.HandedBack.HasNoValue);
        Assert.Equal(Assignee.Me, task.Assignee);
    }

    [Theory]
    [InlineData("ask")]
    [InlineData("done")]
    [InlineData("close")]
    [InlineData("status-backlog")]
    [InlineData("join")]
    public void Any_other_change_of_assignee_or_status_clears_the_mark(string change)
    {
        var handed = Claudes(TaskBoard.Empty, id1).HandBack(id1, clock);
        var board = change switch
        {
            "ask" => handed.Ask(id1, "OK?", clock).Value,
            "done" => handed.MarkDone(id1, clock).Value,
            "close" => handed.Close(id1, clock).Value,
            "join" => handed.Join(id1, Session("s2"), clock).Value,
            _ => handed.SetStatus(id1, TaskStatus.Backlog, clock).Value,
        };

        Assert.True(Get(board, id1).HandedBack.HasNoValue);
    }

    [Fact]
    public void A_status_line_keeps_the_mark()
    {
        var board = Claudes(TaskBoard.Empty, id1).HandBack(id1, clock).SetStatusLine(id1, "still here", clock).Value;

        Assert.True(Get(board, id1).HandedBack.HasValue);
    }

    [Fact]
    public void HandBack_ignores_a_note_and_a_task_waiting_on_Petre()
    {
        var note = Note(TaskBoard.Empty, id1);
        Assert.Same(note, note.HandBack(id1, clock));

        var waiting = Claudes(TaskBoard.Empty, id2).Ask(id2, "OK?", clock).Value;
        Assert.Same(waiting, waiting.HandBack(id2, clock));
    }

    [Fact]
    public void The_mark_survives_a_save_and_a_load()
    {
        Directory.CreateDirectory(dir);
        var store = new JsonTaskStore(dir);
        store.Save(Claudes(TaskBoard.Empty, id1).HandBack(id1, clock));

        var loaded = Get(store.Load().Value, id1);

        Assert.Equal(clock, loaded.HandedBack.Value);
        Assert.Equal(Assignee.Me, loaded.Assignee);
    }

    [Theory]
    [InlineData("\"garbage\"")]
    [InlineData("5")]
    [InlineData("{\"a\":1}")]
    public void A_wrong_typed_handed_back_time_reads_as_none(string handedBack)
    {
        var board = new JsonTaskStore(Written($",\"handedBack\":{handedBack}")).Load();

        Assert.True(board.IsSuccess, board.IsFailure ? board.Error : "");
        Assert.True(Get(board.Value, id1).HandedBack.HasNoValue);
    }
}
