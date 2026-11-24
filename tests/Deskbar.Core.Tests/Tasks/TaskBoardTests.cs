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

    // Ruling 19: /clear marks the session replaced and ended, its task keeps its status, and a session starting with
    // source "clear" in the same folder within a minute finds that task; later, or in another folder, it does not.
    [Fact]
    public void A_cleared_session_is_replaced_and_its_task_found_for_a_minute()
    {
        var board = Claudes(TaskBoard.Empty, id1, ws);
        var cleared = clock;

        board = board.SessionCleared("s1", clock);

        var session = Assert.Single(Get(board, id1).Sessions);
        Assert.Equal(cleared, session.ReplacedAt.GetValueOrDefault());
        Assert.Equal(cleared, session.Ended.GetValueOrDefault());
        Assert.Equal(TaskStatus.InProgress, Get(board, id1).Status);
        Assert.Same(board, board.SessionCleared("nobody", clock));

        bool Here(string folder) => folder == @"C:\repos\TaskSpaces";
        Assert.Equal(id1, board.ClearedTaskIn(Here, cleared.AddSeconds(59)).GetValueOrDefault()?.Id);
        Assert.True(board.ClearedTaskIn(folder => false, cleared.AddSeconds(5)).HasNoValue);
        Assert.True(board.ClearedTaskIn(Here, cleared.AddSeconds(61)).HasNoValue);
        Assert.True(board.ClearedTaskIn(Here, cleared.AddSeconds(-1)).HasNoValue);
    }

    // A session that had already ended keeps its end; the clear is still recorded.
    [Fact]
    public void Clearing_an_ended_session_keeps_its_end()
    {
        var ended = clock;
        var board = Claudes(TaskBoard.Empty, id1, ws).SessionEnded("s1", clock);
        Later();

        var session = Get(board.SessionCleared("s1", clock), id1).Sessions.Single();

        Assert.Equal(ended, session.Ended.GetValueOrDefault());
        Assert.Equal(clock, session.ReplacedAt.GetValueOrDefault());
    }

    // Ruling 34: a resumed session coming back to its OWN task keeps the task's status. Only the user's next message
    // (Answered) moves Done or Waiting on you back to In progress; resuming a session to look at it moves nothing.
    [Theory]
    [InlineData(TaskStatus.Done)]
    [InlineData(TaskStatus.WaitingOnYou)]
    [InlineData(TaskStatus.InProgress)]
    public void Rejoining_its_own_task_keeps_the_status(TaskStatus status)
    {
        var board = Claudes(TaskBoard.Empty, id1, ws);
        board = (status switch
        {
            TaskStatus.Done => board.MarkDone(id1, clock),
            TaskStatus.WaitingOnYou => board.Ask(id1, "OK to merge?", clock),
            _ => Result.Success(board),
        }).Value.SessionEnded("s1", clock);
        Later();

        var task = Get(board.Rejoin(id1, Session("s1"), clock).Value, id1);

        Assert.Equal(status, task.Status);
        Assert.True(Assert.Single(task.Sessions).IsLive);
        Assert.Equal(status == TaskStatus.WaitingOnYou, task.Question.HasValue);
    }

    // A rejoin is still a session taking a task up: it leaves any other open task it was live on, and a closed task
    // refuses it as Join does.
    [Fact]
    public void Rejoining_leaves_the_other_task_and_a_closed_one_refuses()
    {
        var board = Claudes(TaskBoard.Empty, id1, ws);
        board = Claudes(board, id2, ws).SessionEnded("s1", clock);
        board = board.Join(id1, Session("s1"), clock).Value;

        var rejoined = board.Rejoin(id2, Session("s1"), clock).Value;

        Assert.False(Get(rejoined, id1).HasLiveSession);
        Assert.True(Get(rejoined, id2).HasLiveSession);
        Assert.Equal("The task is closed.", rejoined.Close(id2, clock, stoppingClaude: true).Value.Rejoin(id2, Session("s1"), clock).Error);
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
        var board = Claudes(TaskBoard.Empty, id1, ws, "s1").Ask(id1, "Merge?", clock).Value.Close(id1, clock, stoppingClaude: true).Value;
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
        var closed = Claudes(TaskBoard.Empty, id1, ws).Close(id1, clock, stoppingClaude: true).Value;

        Assert.Equal("The task is closed.", closed.MarkDone(id1, clock).Error);
        Assert.Equal("The task is closed.", closed.Ask(id1, "Why?", clock).Error);
        Assert.Equal("The task is closed.", closed.SetStatusLine(id1, "CI", clock).Error);
        Assert.Equal("The task is closed.", closed.Close(id1, clock).Error);
    }

    // Task 37cd7081, Petre: "let's prohibit closing tasks that aren't yet done", narrowed to a live session with an offer
    // to stop it. A task Claude is still working on (open, not Done, a live session) refuses to close in every status
    // short of Done; stopping Claude is the way past it. Done closes whatever its session does, and so do a note and a
    // task whose session has ended.
    [Fact]
    public void A_task_claude_is_still_working_on_closes_only_by_stopping_claude()
    {
        var working = Claudes(TaskBoard.Empty, id1, ws);
        Assert.True(TaskBoard.ClaudeStillWorking(Get(working, id1)));
        Assert.Equal(TaskViews.ClaudeStillWorkingRefusal, working.Close(id1, clock).Error);

        var asking = working.Ask(id1, "Merge?", clock).Value;
        Assert.Equal(TaskViews.ClaudeStillWorkingRefusal, asking.Close(id1, clock).Error);
        var backlog = working.SetStatus(id1, TaskStatus.Backlog, clock).Value;
        Assert.Equal(TaskViews.ClaudeStillWorkingRefusal, backlog.Close(id1, clock).Error);

        var stopped = working.Close(id1, clock, stoppingClaude: true).Value;
        Assert.Equal(TaskStatus.Closed, Get(stopped, id1).Status);
        Assert.False(TaskBoard.ClaudeStillWorking(Get(stopped, id1)));
        // Stopping Claude never skips the other refusals: a closed task still says it is closed.
        Assert.Equal("The task is closed.", stopped.Close(id1, clock, stoppingClaude: true).Error);

        var done = working.MarkDone(id1, clock).Value;
        Assert.False(TaskBoard.ClaudeStillWorking(Get(done, id1)));
        Assert.True(done.Close(id1, clock).IsSuccess);

        var ended = working.SessionEnded("s1", clock);
        Assert.True(ended.Close(id1, clock).IsSuccess);
        Assert.True(Note(TaskBoard.Empty, id2, ws).Close(id2, clock).IsSuccess);
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

        var closed = board.Close(id1, clock, stoppingClaude: true).Value;
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

    // Task 12 (Ruling 25): Done no longer sorts ahead of the rest; it keeps its place in its quadrant, where a newer
    // task sits above an older one until Petre drags them. TaskOrderTests carries the quadrants and the drag.
    [Fact]
    public void The_pile_puts_waiting_first_then_the_rest_newest_first_done_included()
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

        Assert.Equal([id4, id3, id5, id2], board.Pile().Select(t => t.Id));
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
        // Ruling 57: a note is offered once Petre has assigned it to Claude.
        var board = Note(TaskBoard.Empty, id1, ws, "note on ws").Assign(id1, Assignee.Claude, clock).Value;
        board = Claudes(board, id2, other, "s2", "on other");
        board = Note(board, id3, Free, "errand");
        board = Claudes(board, id4, Free, "s4", "free claude task");
        board = Claudes(board, id5, ws, "s5", "closed on ws").Close(id5, clock, stoppingClaude: true).Value;

        Assert.Equal(new[] { id1, id4 }.Order(), board.Offered(ws).Select(t => t.Id).Order());
        Assert.Equal([id4], board.Offered(Free).Select(t => t.Id));
    }

    [Fact]
    public void TaskOf_finds_the_open_task_a_session_works_on()
    {
        var board = Note(Claudes(TaskBoard.Empty, id1, ws, "s1"), id2, ws);

        Assert.Equal(id1, board.TaskOf("s1").Value.Id);
        Assert.True(board.TaskOf("s9").HasNoValue);
        Assert.True(board.Close(id1, clock, stoppingClaude: true).Value.TaskOf("s1").HasNoValue);
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

        Assert.Empty(board.Delete(id1, clock).Value.Tasks);
        Assert.True(board.Delete(id2, clock).IsFailure);
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

    // --- the goal a task counts toward (goals plan, Task 2) -------------------------------------------
    // The link lives on the task. Attaching never changes its flags, rank or status (goals spec, "Tasks contribute"),
    // and TaskBoard checks nothing about goals: GoalService.LinkTask is where a goal must exist and be open.

    static readonly Guid goal = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    static readonly Guid otherGoal = Guid.Parse("cccccccc-0000-0000-0000-000000000002");

    [Fact]
    public void Setting_a_goal_changes_nothing_else()
    {
        var board = Note(TaskBoard.Empty, id1, ws).SetFlag(id1, TaskFlag.Important, true, clock).Value;
        var before = Get(board, id1);
        Later();

        var after = Get(board.SetGoal(id1, goal, clock).Value, id1);

        Assert.Equal(goal, after.GoalId.Value);
        Assert.Equal(Quadrant.II, after.Quadrant);
        Assert.Equal(before.Important, after.Important);
        Assert.Equal(before.Urgent, after.Urgent);
        Assert.Equal(before.Rank, after.Rank);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.Raised, after.Raised);
        Assert.Equal(clock, after.UpdatedAt);
        Assert.True(before.GoalId.HasNoValue);
    }

    [Fact]
    public void Setting_the_same_goal_is_the_same_board()
    {
        var board = Note(TaskBoard.Empty, id1, ws).SetGoal(id1, goal, clock).Value;

        Assert.Same(board, board.SetGoal(id1, goal, clock).Value);
        Assert.True(Get(board.SetGoal(id1, Maybe<Guid>.None, clock).Value, id1).GoalId.HasNoValue);
        Assert.Equal($"No task with id {id2}", board.SetGoal(id2, goal, clock).Error);
    }

    // A closed task stays readable history, so it takes a goal too (and keeps the one it had).
    [Fact]
    public void A_closed_task_takes_a_goal()
    {
        var board = Note(TaskBoard.Empty, id1, ws).Close(id1, clock).Value;

        Assert.Equal(goal, Get(board.SetGoal(id1, goal, clock).Value, id1).GoalId.Value);
    }

    [Fact]
    public void Detaching_a_goal_frees_every_task_that_named_it()
    {
        var board = Note(Note(Note(TaskBoard.Empty, id1, ws), id2, ws), id3, ws)
            .SetGoal(id1, goal, clock).Value
            .SetGoal(id2, goal, clock).Value
            .SetGoal(id3, otherGoal, clock).Value;
        var untouched = Get(board, id3);
        Later();

        var detached = board.DetachGoal(goal, clock);

        Assert.All(new[] { id1, id2 }.Select(id => Get(detached, id)), t =>
        {
            Assert.True(t.GoalId.HasNoValue);
            Assert.Equal(clock, t.UpdatedAt);
        });
        Assert.Equal(untouched, Get(detached, id3));
        Assert.Same(detached, detached.DetachGoal(goal, clock));
    }

    [Fact]
    public void Undo_and_reopen_keep_the_goal()
    {
        var board = Note(TaskBoard.Empty, id1, ws).SetGoal(id1, goal, clock).Value;

        var closed = board.Close(id1, clock).Value;
        Assert.Equal(goal, Get(closed, id1).GoalId.Value);
        Assert.Equal(goal, Get(closed.UndoClose(id1, TaskStatus.Backlog, Maybe<string>.None, clock).Value, id1).GoalId.Value);
        Assert.Equal(goal, Get(closed.Reopen(id1, clock).Value, id1).GoalId.Value);
        Assert.Equal(goal, Get(board.SetFlag(id1, TaskFlag.Urgent, true, clock).Value, id1).GoalId.Value);
    }

    // --- following Claude's own list of live sessions (plan Task 5) ----------------------------------

    static ClaudeAgent Agent(string id, string name = "", string folder = @"C:\repos\TaskSpaces") =>
        new(id, Maybe<string>.None, folder, true, false, name);

    [Fact]
    public void Reconcile_ends_a_live_session_missing_from_the_list()
    {
        var board = Claudes(TaskBoard.Empty, id1, ws, "s1");
        Later();

        var reconciled = board.Reconcile([Agent("other")], clock);

        var session = Get(reconciled, id1).Sessions.Single();
        Assert.False(session.IsLive);
        Assert.Equal(clock, session.Ended.Value);
        Assert.Equal(clock, Get(reconciled, id1).UpdatedAt);
        Assert.Equal(TaskStatus.InProgress, Get(reconciled, id1).Status);
    }

    [Fact]
    public void Reconcile_revives_an_ended_session_the_list_still_shows()
    {
        var board = Claudes(TaskBoard.Empty, id1, ws, "s1").SessionEnded("s1", clock);
        Later();

        var reconciled = board.Reconcile([Agent("s1")], clock);

        Assert.True(Get(reconciled, id1).Sessions.Single().IsLive);
    }

    [Fact]
    public void Reconcile_revives_only_the_latest_entry_for_an_id()
    {
        var first = Claudes(TaskBoard.Empty, id1, ws, "s1").SessionEnded("s1", clock);
        Later();
        var board = Claudes(first, id2, ws, "s1").SessionEnded("s1", clock);
        Later();

        var reconciled = board.Reconcile([Agent("s1")], clock);

        Assert.False(Get(reconciled, id1).HasLiveSession);
        Assert.True(Get(reconciled, id2).HasLiveSession);
    }

    [Fact]
    public void Reconcile_leaves_tasks_without_sessions_alone()
    {
        var board = Note(TaskBoard.Empty, id1, ws);

        Assert.Same(board, board.Reconcile([], clock));
        Assert.Same(board, board.Reconcile([Agent("s1")], clock));
    }

    [Fact]
    public void Reconcile_does_not_change_UpdatedAt_when_nothing_changed()
    {
        var board = Claudes(Note(TaskBoard.Empty, id2, ws), id1, ws, "s1");
        Later();

        var reconciled = board.Reconcile([Agent("s1")], clock);

        Assert.Same(board, reconciled);
        Assert.NotEqual(clock, Get(reconciled, id1).UpdatedAt);
    }

    [Fact]
    public void Reconcile_stamps_only_the_tasks_it_changed()
    {
        var board = Claudes(Claudes(TaskBoard.Empty, id1, ws, "s1"), id2, ws, "s2");
        Later();

        var reconciled = board.Reconcile([Agent("s1")], clock);

        Assert.Equal(Get(board, id1), Get(reconciled, id1));
        Assert.Equal(clock, Get(reconciled, id2).UpdatedAt);
    }

    [Fact]
    public void Adopting_a_session_makes_an_in_progress_Claude_task_titled_by_its_name()
    {
        var (board, task) = TaskBoard.Empty.AdoptSession(Agent("s9", "Fix the strip"), ws, clock, id1).Value;

        Assert.Equal("Fix the strip", task.Title);
        Assert.Equal(TaskStatus.InProgress, task.Status);
        Assert.Equal(ws, task.WorkspaceId.Value);
        var session = Assert.Single(task.Sessions);
        Assert.Equal("s9", session.Id);
        Assert.Equal(@"C:\repos\TaskSpaces", session.Folder);
        Assert.Equal(clock, session.Joined);
        Assert.True(session.IsLive);
        Assert.Equal(task, Assert.Single(board.Tasks));
    }

    [Theory]
    [InlineData(@"C:\repos\TaskSpaces", "TaskSpaces")]
    [InlineData(@"C:\repos\TaskSpaces\", "TaskSpaces")]
    [InlineData("", "Claude session")]
    public void Adopting_a_session_no_name_gives_falls_back_to_the_folder_name(string folder, string title) =>
        Assert.Equal(title, TaskBoard.Empty.AdoptSession(Agent("s9", "", folder), Free, clock, id1).Value.Task.Title);

    [Fact]
    public void A_session_that_already_has_a_task_is_not_adopted_again()
    {
        var open = Claudes(TaskBoard.Empty, id1, ws, "s1");
        var closed = open.Close(id1, clock, stoppingClaude: true).Value;

        Assert.True(open.AdoptSession(Agent("s1"), ws, clock, id2).IsFailure);
        Assert.True(closed.AdoptSession(Agent("s1"), ws, clock, id2).IsFailure);
    }

    // Ruling 11: Deskbar can adopt a session before the session's own hook joins it to the task it was started for.
    // When the session then joins (or creates, or rejoins) its own task, the task Deskbar made for it is left with
    // nothing of its own, and it goes. Only an adopted task, and only one that holds nothing else.
    [Fact]
    public void An_adopted_task_whose_session_joins_another_task_is_deleted()
    {
        var adopted = TaskBoard.Empty.AdoptSession(Agent("s1", "Stray"), ws, clock, id1).Value.Board;
        Assert.True(Get(adopted, id1).Adopted);
        Later();

        var joined = Note(adopted, id2, ws).Join(id2, Session("s1"), clock).Value;
        Assert.DoesNotContain(joined.Tasks, t => t.Id == id1);
        Assert.Equal("s1", Get(joined, id2).Sessions.Single().Id);

        var rejoined = Note(adopted, id2, ws).Rejoin(id2, Session("s1"), clock).Value;
        Assert.DoesNotContain(rejoined.Tasks, t => t.Id == id1);

        var created = adopted.Create("Its own task", ws, Session("s1"), clock, id3).Value.Board;
        Assert.DoesNotContain(created.Tasks, t => t.Id == id1);
        Assert.Single(created.Tasks);
    }

    [Fact]
    public void An_adopted_task_with_a_comment_is_kept_when_its_session_leaves()
    {
        var adopted = TaskBoard.Empty.AdoptSession(Agent("s1", "Stray"), ws, clock, id1).Value.Board
            .Comment(id1, Maybe<string>.None, "keep this", clock, id5).Value;
        Later();

        var joined = Note(adopted, id2, ws).Join(id2, Session("s1"), clock).Value;

        var kept = Get(joined, id1);
        Assert.True(kept.Sessions.Single().Ended.HasValue);
        Assert.Single(kept.Comments);
    }

    // Ruling 11b: "deleted if nothing else was added", and Petre's own edits are things added. Any edit only he makes
    // clears the mark for good, so the task stays when its session leaves.
    [Fact]
    public void A_renamed_adopted_task_is_kept_when_its_session_leaves()
    {
        var adopted = TaskBoard.Empty.AdoptSession(Agent("s1", "Stray"), ws, clock, id1).Value.Board
            .Rename(id1, "Mine now", clock).Value;
        Assert.False(Get(adopted, id1).Adopted);
        Later();

        var joined = Note(adopted, id2, ws).Join(id2, Session("s1"), clock).Value;

        Assert.Equal("Mine now", Get(joined, id1).Title);
    }

    [Theory]
    [InlineData("goal")]
    [InlineData("flag")]
    [InlineData("move")]
    [InlineData("status")]
    [InlineData("comment")]
    // Main's assignee and description edits (Rulings 57 and 58) are Petre's only, so they count too.
    [InlineData("assign")]
    [InlineData("describe")]
    public void An_adopted_task_Petre_edited_is_kept_when_its_session_leaves(string edit)
    {
        var adopted = TaskBoard.Empty.AdoptSession(Agent("s1", "Stray"), ws, clock, id1).Value.Board;
        var edited = (edit switch
        {
            "goal" => adopted.SetGoal(id1, goal, clock),
            "flag" => adopted.SetFlag(id1, TaskFlag.Important, true, clock),
            "move" => adopted.MoveTo(id1, other, clock),
            "status" => adopted.SetStatus(id1, TaskStatus.Backlog, clock),
            "assign" => adopted.Assign(id1, Assignee.Me, clock),
            "describe" => adopted.Describe(id1, "mine", clock),
            _ => adopted.Comment(id1, Maybe<string>.None, "mine", clock, id5),
        }).Value;
        Assert.False(Get(edited, id1).Adopted);
        Later();

        var joined = Note(edited, id2, ws).Join(id2, Session("s1"), clock).Value;

        Assert.Contains(joined.Tasks, t => t.Id == id1);
    }

    // A session's own comment is not Petre's edit: the mark stays (the comment alone still keeps the task).
    [Fact]
    public void A_session_comment_leaves_the_adopted_mark()
    {
        var adopted = TaskBoard.Empty.AdoptSession(Agent("s1", "Stray"), ws, clock, id1).Value.Board
            .Comment(id1, "s1", "from the session", clock, id5).Value;

        Assert.True(Get(adopted, id1).Adopted);
    }

    // Settling a comment (Ruling 62) follows the same line: the session settling its own leaves the mark, Petre settling
    // it is his edit and clears it.
    [Fact]
    public void Settling_a_comment_clears_the_adopted_mark_only_when_Petre_does_it()
    {
        var commented = TaskBoard.Empty.AdoptSession(Agent("s1", "Stray"), ws, clock, id1).Value.Board
            .Comment(id1, "s1", "from the session", clock, id5).Value;

        Assert.True(Get(commented.SettleComment(id5, true, "s1", clock).Value, id1).Adopted);
        Assert.False(Get(commented.SettleComment(id5, true, null, clock).Value, id1).Adopted);
    }

    [Fact]
    public void A_task_that_was_not_adopted_is_kept_when_its_session_leaves()
    {
        var board = Claudes(TaskBoard.Empty, id1, ws, "s1");
        Assert.False(Get(board, id1).Adopted);
        Later();

        var joined = Note(board, id2, ws).Join(id2, Session("s1"), clock).Value;

        Assert.True(Get(joined, id1).Sessions.Single().Ended.HasValue);
    }

    // Task e671709b: a window a hidden session opens goes to its task's workspace, and the session's process names it
    // only by its short id (the pty host's pipe name), so the board answers to the first eight characters.
    [Fact]
    public void A_sessions_workspace_is_found_by_its_short_id()
    {
        var board = Claudes(TaskBoard.Empty, id1, ws, session: "84bee58f-32b0-4349-acfb-59997bb6ba67");

        Assert.Equal(ws, board.WorkspaceOfSession("84bee58f").Value);
        Assert.Equal(ws, board.WorkspaceOfSession("84BEE58F").Value);
        Assert.True(board.WorkspaceOfSession("12345678").HasNoValue);
        Assert.True(board.WorkspaceOfSession("").HasNoValue);
    }

    // A free task has nowhere to send a window, and a closed task is no longer the session's work: both leave the
    // window where it opened.
    [Fact]
    public void A_free_or_closed_task_gives_a_session_no_workspace()
    {
        var free = Claudes(TaskBoard.Empty, id1, Free, session: "aaaa1111-0000");
        var closed = Claudes(TaskBoard.Empty, id1, ws, session: "bbbb2222-0000").Close(id1, clock, stoppingClaude: true).Value;

        Assert.True(free.WorkspaceOfSession("aaaa1111").HasNoValue);
        Assert.True(closed.WorkspaceOfSession("bbbb2222").HasNoValue);
    }

    // A session that moved to another task opens windows for that one: the live entry wins over the ended one.
    [Fact]
    public void The_task_a_session_is_live_on_wins()
    {
        var first = Claudes(TaskBoard.Empty, id1, ws, session: "cccc3333-0000");
        Later();
        var moved = Note(first, id2, other).Join(id2, Session("cccc3333-0000"), clock).Value;

        Assert.Equal(other, moved.WorkspaceOfSession("cccc3333").Value);
    }
}
