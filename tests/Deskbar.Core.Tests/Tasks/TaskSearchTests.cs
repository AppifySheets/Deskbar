using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests.Tasks;

// The tasks panel's search box and its "Claude working" switch, pure (TaskSearch.cs). The panel only draws what these
// decide; the spec is docs/superpowers/specs/2026-11-23-tasks-panel-design.md.
public class TaskSearchTests
{
    static readonly DateTimeOffset clock = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);

    static TaskSession Session(string id) => new(id, @"C:\repos\TaskSpaces", clock, Maybe<DateTimeOffset>.None);

    // A Claude task, asked a question and given a status line, so all three searched fields are filled.
    static (TaskBoard Board, TaskItem Task) StoreTask()
    {
        var made = TaskBoard.Empty.Create("Store submission for 2.6", Maybe<Guid>.None, Session("s1"), clock, Guid.NewGuid()).Value;
        var asked = made.Board.Ask(made.Task.Id, "Which price tier?", clock).Value;
        var task = asked.Tasks.Single() with { StatusLine = "waiting on certification" };
        return (new TaskBoard([task]), task);
    }

    [Fact]
    public void Every_word_must_match_somewhere_in_title_status_line_or_question()
    {
        var (_, task) = StoreTask();

        Assert.True(new TaskSearch("store tier").Admits(task));
        Assert.True(new TaskSearch("STORE   Certification").Admits(task));
        Assert.False(new TaskSearch("store video").Admits(task));
    }

    [Fact]
    public void A_blank_search_admits_everything_and_narrows_nothing()
    {
        var (board, task) = StoreTask();
        var blank = new TaskSearch("   ");

        Assert.True(blank.IsBlank);
        Assert.True(blank.Admits(task));
        Assert.Same(board, blank.Narrow(board));
        Assert.Same(board, TaskSearch.None.Narrow(board));
    }

    [Fact]
    public void A_task_with_no_question_is_matched_on_title_and_status_line()
    {
        var note = TaskBoard.Empty.AddNote("Dishwasher tablets", Maybe<Guid>.None, clock, Guid.NewGuid()).Value.Task with { StatusLine = "buy on friday" };

        Assert.True(new TaskSearch("dishwasher friday").Admits(note));
        Assert.False(new TaskSearch("dishwasher tier").Admits(note));
    }

    [Fact]
    public void Narrow_keeps_only_the_tasks_the_search_admits()
    {
        var (board, _) = StoreTask();
        var plusNote = board.AddNote("Dishwasher tablets", Maybe<Guid>.None, clock, Guid.NewGuid()).Value.Board;

        Assert.Equal(["Dishwasher tablets"], new TaskSearch("dishwasher").Narrow(plusNote).Tasks.Select(t => t.Title));
    }

    [Fact]
    public void Claude_working_keeps_only_tasks_with_a_live_session()
    {
        var ended = TaskBoard.Empty.Create("Ended session", Maybe<Guid>.None, Session("s-ended"), clock, Guid.NewGuid()).Value.Board.SessionEnded("s-ended", clock);
        var live = ended.Create("Live session", Maybe<Guid>.None, Session("s-live"), clock, Guid.NewGuid()).Value.Board;
        var board = live.AddNote("A note", Maybe<Guid>.None, clock, Guid.NewGuid()).Value.Board;

        Assert.Equal(["Live session"], ClaudeWorking.Narrow(board, on: true).Tasks.Select(t => t.Title));
        Assert.Same(board, ClaudeWorking.Narrow(board, on: false));
    }
}
