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

    // --- search by id (task 6e4dc116) -------------------------------------------------------------------------------

    static readonly Guid storeId = Guid.Parse("173ef268-1111-4222-8333-444455556666");
    static readonly Guid noteId = Guid.Parse("9e85caee-aaaa-4bbb-8ccc-ddddeeeeffff");

    // A closed note, a snoozed note and an open note: one of every section the open view leaves out, and one in it.
    static TaskBoard ThreeSections()
    {
        var store = TaskBoard.Empty.AddNote("Store submission", Maybe<Guid>.None, clock, storeId).Value.Board;
        var closed = store.Close(storeId, clock).Value;
        var snoozed = closed.AddNote("Dishwasher tablets", Maybe<Guid>.None, clock, noteId).Value.Board.Snooze(noteId, clock.AddDays(1), clock).Value;
        return snoozed.AddNote("Decade review", Maybe<Guid>.None, clock, Guid.NewGuid()).Value.Board;
    }

    [Theory]
    [InlineData("173ef268", "173ef268")]
    [InlineData("  173EF268 ", "173ef268")]
    [InlineData("173ef268-1111", "173ef2681111")]
    [InlineData("{173EF268-1111-4222-8333-444455556666}", "173ef268111142228333444455556666")]
    [InlineData("173e", "173e")]
    public void Hex_text_reads_as_the_start_of_an_id(string text, string prefix) =>
        Assert.Equal(prefix, new TaskSearch(text).IdPrefix.Value);

    [Theory]
    [InlineData("")]
    [InlineData("add")]
    [InlineData("store tier")]
    [InlineData("173ef268 store")]
    [InlineData("173g")]
    [InlineData("173ef268111142228333444455556666a")]
    public void Anything_else_is_no_id(string text) => Assert.True(new TaskSearch(text).IdPrefix.HasNoValue);

    [Fact]
    public void An_id_prefix_finds_its_task_whatever_its_section()
    {
        var board = ThreeSections();

        Assert.Equal([storeId], new TaskSearch("173ef268").Found(board).Value.Tasks.Select(t => t.Id));
        Assert.Equal([noteId], new TaskSearch("9E85CAEE-AAAA").Found(board).Value.Tasks.Select(t => t.Id));
        Assert.True(new TaskSearch("173ef268").Admits(board.Tasks.Single(t => t.Id == storeId)));
    }

    // Hex text no id starts with is an ordinary word search: "decade" is all hex digits and still finds the title.
    [Fact]
    public void Hex_text_no_id_starts_with_searches_the_words()
    {
        var board = ThreeSections();
        var decade = new TaskSearch("decade");

        Assert.True(decade.Found(board).HasNoValue);
        Assert.Equal(["Decade review"], decade.Narrow(board).Tasks.Select(t => t.Title));
    }

    // Everywhere groups every task in its own section, the closed and snoozed ones included, with no filter.
    [Fact]
    public void Everywhere_shows_every_section()
    {
        var groups = TaskViews.Everywhere(ThreeSections());

        Assert.Equal([TaskGroupKind.IV, TaskGroupKind.Snoozed, TaskGroupKind.Closed], groups.Select(g => g.Kind));
        Assert.Equal([noteId], groups.Single(g => g.Kind == TaskGroupKind.Snoozed).Tasks.Select(t => t.Id));
        Assert.Equal([storeId], groups.Single(g => g.Kind == TaskGroupKind.Closed).Tasks.Select(t => t.Id));
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
