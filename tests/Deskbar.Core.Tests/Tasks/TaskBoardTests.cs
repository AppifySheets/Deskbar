using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
// System.Threading.Tasks (an implicit using) has a TaskStatus too; the alias says which one this file means.
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Tasks;

// The task rules, pure and clock-free: every method takes "now", and the test moves a captured clock, so nothing
// here waits. Design: docs/superpowers/specs/2026-11-22-tasks-board-design.md ("What Petre decided", "Calls made
// after the builder check").
public class TaskBoardTests
{
    DateTimeOffset clock = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);
    static readonly Guid ws = Guid.Parse("11111111-1111-1111-1111-111111111111");
    static readonly Guid other = Guid.Parse("22222222-2222-2222-2222-222222222222");
    static readonly Guid id1 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    static readonly Guid id2 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    static readonly Guid id3 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");
    static readonly Guid id4 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000004");
    static readonly Guid id5 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000005");

    static readonly Maybe<Guid> Free = Maybe<Guid>.None;

    void Later(int minutes = 1) => clock = clock.AddMinutes(minutes);

    TaskSession Session(string id) => new(id, @"C:\repos\TaskSpaces", clock, Maybe<DateTimeOffset>.None);

    TaskBoard Note(TaskBoard board, Guid id, Maybe<Guid> workspace, string title = "Dishwasher tablets") =>
        board.AddNote(title, workspace, clock, id).Value.Item1;

    TaskBoard Claudes(TaskBoard board, Guid id, Maybe<Guid> workspace, string session = "s1", string title = "Tasks redesign") =>
        board.Create(title, workspace, Session(session), clock, id).Value.Item1;

    static TaskItem Get(TaskBoard board, Guid id) => board.Tasks.Single(t => t.Id == id);

    [Fact]
    public void A_note_starts_in_the_backlog_with_no_sessions()
    {
        var (board, task) = TaskBoard.Empty.AddNote("Dishwasher tablets", Free, clock, id1).Value;

        Assert.Equal(TaskStatus.Backlog, task.Status);
        Assert.False(task.IsClaudes);
        Assert.Equal("", task.StatusLine);
        Assert.True(task.Question.HasNoValue);
        Assert.Empty(task.Comments);
        Assert.Equal(clock, task.Created);
        Assert.Equal(clock, task.UpdatedAt);
        Assert.Equal(clock, task.Raised);
        Assert.Equal(task, Assert.Single(board.Tasks));
    }

    [Fact]
    public void An_empty_title_is_refused() =>
        Assert.Equal("A task needs a title", TaskBoard.Empty.AddNote("  ", Free, clock, id1).Error);

    [Fact]
    public void A_session_creates_a_task_in_progress()
    {
        var (_, task) = TaskBoard.Empty.Create("Tasks redesign", ws, Session("s1"), clock, id1).Value;

        Assert.Equal(TaskStatus.InProgress, task.Status);
        Assert.True(task.IsClaudes);
        Assert.Equal("s1", Assert.Single(task.Sessions).Id);
        Assert.True(task.HasLiveSession);
        Assert.Equal(ws, task.WorkspaceId.Value);
    }

    [Fact]
    public void Joining_a_note_makes_it_claudes()
    {
        var board = Note(TaskBoard.Empty, id1, ws);
        Later();

        var joined = Get(board.Join(id1, Session("s1"), clock).Value, id1);

        Assert.Equal(TaskStatus.InProgress, joined.Status);
        Assert.True(joined.IsClaudes);
        Assert.True(joined.HasLiveSession);
        Assert.Equal(clock, joined.UpdatedAt);
    }

    // A session that comes back (a resumed conversation keeps its id) is one session, live again, not two.
    [Fact]
    public void Joining_again_revives_the_same_session()
    {
        var board = Claudes(TaskBoard.Empty, id1, ws).SessionEnded("s1", clock);
        Later();

        var task = Get(board.Join(id1, Session("s1"), clock).Value, id1);

        Assert.True(Assert.Single(task.Sessions).IsLive);
    }

    [Fact]
    public void A_closed_task_cannot_be_joined()
    {
        var board = Note(TaskBoard.Empty, id1, ws).Close(id1, clock).Value;

        Assert.Equal("The task is closed.", board.Join(id1, Session("s1"), clock).Error);
    }

    [Fact]
    public void Asking_waits_on_you_and_answering_resumes()
    {
        var board = Claudes(TaskBoard.Empty, id1, ws);
        Later(5);

        var asked = board.Ask(id1, "OK to restart?", clock).Value;
        Assert.Equal(TaskStatus.WaitingOnYou, Get(asked, id1).Status);
        Assert.Equal("OK to restart?", Get(asked, id1).Question.Value);
        Assert.Equal(clock, Get(asked, id1).Raised);

        Later();
        var answered = Get(asked.Answered("s1", clock), id1);
        Assert.Equal(TaskStatus.InProgress, answered.Status);
        Assert.True(answered.Question.HasNoValue);
        Assert.Equal(clock, answered.UpdatedAt);
    }

    [Fact]
    public void An_empty_question_is_refused() =>
        Assert.Equal("An ask needs a question", Claudes(TaskBoard.Empty, id1, ws).Ask(id1, " ", clock).Error);

    // Spec, "After Done or Closed": a message to a session whose task is Done means Petre is continuing the work.
    [Fact]
    public void Answering_a_done_task_resumes_it()
    {
        var board = Claudes(TaskBoard.Empty, id1, ws).MarkDone(id1, clock).Value;

        Assert.Equal(TaskStatus.InProgress, Get(board.Answered("s1", clock), id1).Status);
    }

    [Fact]
    public void Answering_never_touches_a_closed_task_or_another_sessions_task()
    {
        var board = Claudes(TaskBoard.Empty, id1, ws, "s1").Ask(id1, "Merge?", clock).Value.Close(id1, clock).Value;
        board = Claudes(board, id2, ws, "s2").Ask(id2, "Deploy?", clock).Value;
        Later();

        var after = board.Answered("s1", clock);

        Assert.Same(board, after);
        Assert.Equal(TaskStatus.Closed, Get(after, id1).Status);
        Assert.Equal(TaskStatus.WaitingOnYou, Get(after, id2).Status);
    }

    [Fact]
    public void A_session_ending_keeps_the_task_open()
    {
        var board = Claudes(TaskBoard.Empty, id1, ws);
        Later();

        var ended = Get(board.SessionEnded("s1", clock), id1);

        Assert.Equal(TaskStatus.InProgress, ended.Status);
        Assert.Equal(clock, ended.Sessions.Single().Ended.Value);
        Assert.False(ended.HasLiveSession);
        Assert.True(ended.IsClaudes);
        Assert.True(ended.IsOpen);
        Assert.Same(board, board.SessionEnded("s9", clock));
    }

    [Fact]
    public void Only_close_closes_and_reopen_goes_back()
    {
        var claudes = Claudes(TaskBoard.Empty, id1, ws);
        var done = claudes.MarkDone(id1, clock).Value;
        Assert.Equal(TaskStatus.Done, Get(done, id1).Status);
        Assert.True(Get(done, id1).IsOpen);

        var closed = done.Close(id1, clock).Value;
        Assert.Equal(TaskStatus.Closed, Get(closed, id1).Status);
        Assert.False(Get(closed, id1).IsOpen);

        Assert.Equal(TaskStatus.InProgress, Get(closed.Reopen(id1, clock).Value, id1).Status);

        var ended = closed.SessionEnded("s1", clock);
        Assert.Equal(TaskStatus.Backlog, Get(ended.Reopen(id1, clock).Value, id1).Status);

        var note = Note(TaskBoard.Empty, id2, ws).Close(id2, clock).Value;
        Assert.Equal(TaskStatus.Backlog, Get(note.Reopen(id2, clock).Value, id2).Status);
    }

    [Fact]
    public void A_closed_task_refuses_the_sessions_changes_and_a_second_close()
    {
        var closed = Claudes(TaskBoard.Empty, id1, ws).Close(id1, clock).Value;

        Assert.Equal("The task is closed.", closed.MarkDone(id1, clock).Error);
        Assert.Equal("The task is closed.", closed.Ask(id1, "Why?", clock).Error);
        Assert.Equal("The task is closed.", closed.SetStatusLine(id1, "CI", clock).Error);
        Assert.Equal("The task is closed.", closed.Close(id1, clock).Error);
    }

    [Fact]
    public void Reopening_an_open_task_is_refused() =>
        Assert.Equal("The task is not closed.", Note(TaskBoard.Empty, id1, ws).Reopen(id1, clock).Error);

    [Fact]
    public void A_status_line_never_moves_a_task()
    {
        var board = Claudes(TaskBoard.Empty, id1, ws);
        var raised = Get(board, id1).Raised;
        Later(10);

        var task = Get(board.SetStatusLine(id1, " waiting for CI on #398 ", clock).Value, id1);

        Assert.Equal("waiting for CI on #398", task.StatusLine);
        Assert.Equal(raised, task.Raised);
        Assert.Equal(clock, task.UpdatedAt);
    }

    [Fact]
    public void Setting_a_status_by_hand_takes_any_open_status_and_leaves_closing_to_close()
    {
        var board = Claudes(TaskBoard.Empty, id1, ws).Ask(id1, "Merge?", clock).Value;

        var backlog = Get(board.SetStatus(id1, TaskStatus.Backlog, clock).Value, id1);
        Assert.Equal(TaskStatus.Backlog, backlog.Status);
        Assert.True(backlog.Question.HasNoValue);

        Assert.Equal("Use close to close a task", board.SetStatus(id1, TaskStatus.Closed, clock).Error);

        var closed = board.Close(id1, clock).Value;
        Assert.Equal(TaskStatus.Done, Get(closed.SetStatus(id1, TaskStatus.Done, clock).Value, id1).Status);
    }

    // Becoming Done is a new moment the list sorts by, like an ask; staying Done is not.
    [Fact]
    public void Becoming_done_stamps_raised()
    {
        var board = Claudes(TaskBoard.Empty, id1, ws);
        board = Note(board, id2, ws);
        Later(5);
        var doneAt = clock;

        board = board.MarkDone(id1, clock).Value.SetStatus(id2, TaskStatus.Done, clock).Value;
        Assert.Equal(doneAt, Get(board, id1).Raised);
        Assert.Equal(doneAt, Get(board, id2).Raised);

        Later();
        board = board.SetStatus(id2, TaskStatus.Done, clock).Value;
        Assert.Equal(doneAt, Get(board, id2).Raised);
    }

    // Review finding: the Done group sorted by UpdatedAt, so a status line, a comment or the session ending moved an
    // older Done task to the top of the group.
    [Fact]
    public void A_done_task_keeps_its_place_through_later_changes()
    {
        var board = Claudes(TaskBoard.Empty, id1, ws, "s1", "older").MarkDone(id1, clock).Value;
        Later();
        board = Claudes(board, id2, ws, "s2", "newer").MarkDone(id2, clock).Value;
        Later();

        board = board.SetStatusLine(id1, "merged", clock).Value
            .Comment(id1, Maybe<string>.None, "Thanks", clock, id3).Value
            .SessionEnded("s1", clock);

        Assert.Equal([id2, id1], board.Pile().Select(t => t.Id));
    }

    // Ruling 4: a session works on one task at a time, so taking up another ends it on the first.
    [Fact]
    public void Joining_a_second_task_ends_the_session_on_the_first()
    {
        var board = Claudes(TaskBoard.Empty, id1, ws, "s1").Ask(id1, "Merge?", clock).Value;
        board = Note(board, id2, ws);
        Later();

        board = board.Join(id2, Session("s1"), clock).Value;

        Assert.False(Get(board, id1).HasLiveSession);
        Assert.Equal(clock, Get(board, id1).Sessions.Single().Ended.Value);
        Assert.Equal(id2, board.TaskOf("s1").Value.Id);
        Later();
        var answered = board.Answered("s1", clock);
        Assert.Equal(TaskStatus.WaitingOnYou, Get(answered, id1).Status);
        Assert.Equal(Get(board, id1), Get(answered, id1));
    }

    [Fact]
    public void Creating_a_task_ends_the_session_on_the_one_before()
    {
        var board = Claudes(TaskBoard.Empty, id1, ws, "s1");
        board = Claudes(board, id2, ws, "s2");
        Later();

        board = Claudes(board, id3, ws, "s1", "follow-up");

        Assert.False(Get(board, id1).HasLiveSession);
        Assert.True(Get(board, id2).HasLiveSession);
        Assert.Equal(id3, board.TaskOf("s1").Value.Id);
    }

    [Fact]
    public void The_pile_puts_waiting_first_then_done_then_newest()
    {
        var board = Note(TaskBoard.Empty, id1, ws, "closed").Close(id1, clock).Value;
        Later();
        board = Claudes(board, id2, ws, "s2", "done").MarkDone(id2, clock).Value;
        Later();
        board = Claudes(board, id3, ws, "s3", "older waiting").Ask(id3, "One?", clock).Value;
        Later();
        board = Note(board, id5, ws, "backlog");
        Later();
        board = Claudes(board, id4, ws, "s4", "newer waiting").Ask(id4, "Two?", clock).Value;

        Assert.Equal([id4, id3, id2, id5], board.Pile().Select(t => t.Id));
    }

    [Fact]
    public void A_newer_task_tops_the_rest_and_a_status_line_does_not()
    {
        var board = Note(TaskBoard.Empty, id1, ws, "older");
        Later();
        board = Note(board, id2, ws, "newer");
        Later();
        board = board.SetStatusLine(id1, "moved?", clock).Value;

        Assert.Equal([id2, id1], board.Pile().Select(t => t.Id));
    }

    [Fact]
    public void Offered_is_this_workspaces_open_tasks_and_claudes_free_ones()
    {
        var board = Note(TaskBoard.Empty, id1, ws, "note on ws");
        board = Claudes(board, id2, other, "s2", "on other");
        board = Note(board, id3, Free, "errand");
        board = Claudes(board, id4, Free, "s4", "free claude task");
        board = Claudes(board, id5, ws, "s5", "closed on ws").Close(id5, clock).Value;

        Assert.Equal(new[] { id1, id4 }.Order(), board.Offered(ws).Select(t => t.Id).Order());
        Assert.Equal([id4], board.Offered(Free).Select(t => t.Id));
    }

    [Fact]
    public void TaskOf_finds_the_open_task_a_session_works_on()
    {
        var board = Note(Claudes(TaskBoard.Empty, id1, ws, "s1"), id2, ws);

        Assert.Equal(id1, board.TaskOf("s1").Value.Id);
        Assert.True(board.TaskOf("s9").HasNoValue);
        Assert.True(board.Close(id1, clock).Value.TaskOf("s1").HasNoValue);
        Assert.True(board.SessionEnded("s1", clock).TaskOf("s1").HasNoValue);
    }

    [Fact]
    public void Comments_keep_their_order_and_author()
    {
        var board = Claudes(TaskBoard.Empty, id1, ws).Comment(id1, "s1", "The **detail** behind the ask", clock, id2).Value;
        Later();
        board = board.Comment(id1, Maybe<string>.None, "Go ahead", clock, id3).Value;

        var comments = Get(board, id1).Comments;
        Assert.Equal([id2, id3], comments.Select(c => c.Id));
        Assert.Equal("s1", comments[0].SessionId.Value);
        Assert.True(comments[1].SessionId.HasNoValue);
        Assert.Equal("Go ahead", comments[1].Markdown);
        Assert.Equal(clock, comments[1].At);
        Assert.Equal(clock, Get(board, id1).UpdatedAt);
        Assert.Equal("A comment needs text", board.Comment(id1, Maybe<string>.None, " ", clock, id4).Error);
    }

    [Fact]
    public void A_task_moves_between_workspaces_and_is_renamed()
    {
        var board = Note(TaskBoard.Empty, id1, ws);
        Later();

        var moved = Get(board.MoveTo(id1, other, clock).Value.Rename(id1, "Soap", clock).Value, id1);

        Assert.Equal(other, moved.WorkspaceId.Value);
        Assert.Equal("Soap", moved.Title);
        Assert.Equal(clock, moved.UpdatedAt);
        Assert.Equal("A task needs a title", board.Rename(id1, " ", clock).Error);
    }

    [Fact]
    public void Unknown_ids_fail()
    {
        Assert.Equal($"No task with id {id1}", TaskBoard.Empty.Close(id1, clock).Error);
        Assert.True(TaskBoard.Empty.Join(id1, Session("s1"), clock).IsFailure);
        Assert.True(TaskBoard.Empty.Comment(id1, Maybe<string>.None, "x", clock, id2).IsFailure);
    }

    [Fact]
    public void Delete_removes_the_task_and_fails_for_unknown_ids()
    {
        var board = Note(TaskBoard.Empty, id1, ws);

        Assert.Empty(board.Delete(id1).Value.Tasks);
        Assert.True(board.Delete(id2).IsFailure);
    }

    [Fact]
    public void Unknown_workspaces_free_their_tasks()
    {
        var board = Claudes(TaskBoard.Empty, id1, ws);
        Later();

        var task = board.Prune(new HashSet<Guid>(), clock).Tasks.Single();

        Assert.True(task.WorkspaceId.HasNoValue);
        Assert.Equal(TaskStatus.InProgress, task.Status);
        Assert.Equal(clock, task.UpdatedAt);
    }

    [Fact]
    public void Find_takes_a_full_id_or_a_unique_prefix_of_four_or_more()
    {
        var board = Note(Note(TaskBoard.Empty, id1, ws, "a"), id2, ws, "b");

        Assert.Equal(id1, board.Find(id1.ToString()).Value.Id);
        Assert.Equal("'aaaaaaaa' matches several tasks", board.Find("aaaaaaaa").Error);
        Assert.True(board.Find("aaaaaaaa-0000-0000-0000-00000000000").IsFailure);
        Assert.True(board.Find("aaa").IsFailure);
        Assert.True(board.Find("ffff").IsFailure);
        Assert.Equal(id2, board.Find("aaaaaaaa-0000-0000-0000-000000000002").Value.Id);
    }
}
