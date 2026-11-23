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
}
