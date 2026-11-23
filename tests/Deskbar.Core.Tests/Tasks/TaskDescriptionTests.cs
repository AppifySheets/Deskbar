using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
using Xunit;
// System.Threading.Tasks (an implicit using) has a TaskStatus too; the alias says which one this file means.
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Tasks;

// RULING 58, edit in the window covers the title, the description and comments. A task gets an optional Description
// (markdown, Petre's own; a Claude task's status line stays Claude's), and he edits his own comments, never Claude's:
// "i should only be able to edit my own comments, not claude's".
public sealed class TaskDescriptionTests : IDisposable
{
    DateTimeOffset clock = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);
    static readonly Guid id1 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    static readonly Guid mine = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    static readonly Guid claudes = Guid.Parse("cccccccc-0000-0000-0000-000000000002");

    readonly string dir = Path.Combine(Path.GetTempPath(), "deskbar-description-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    TaskSession Session(string id) => new(id, @"C:\repos\TaskSpaces", clock, Maybe<DateTimeOffset>.None);

    static TaskItem Get(TaskBoard board, Guid id = default) => board.Tasks.Single(t => id == default || t.Id == id);

    TaskBoard WithComments()
    {
        var board = TaskBoard.Empty.Create("Tasks redesign", Maybe<Guid>.None, Session("s1"), clock, id1).Value.Board;
        board = board.Comment(id1, Maybe<string>.None, "my **first** thought", clock, mine).Value;
        return board.Comment(id1, "s1", "Claude's finding", clock, claudes).Value;
    }

    [Fact]
    public void A_task_has_no_description_until_one_is_written() =>
        Assert.Equal("", Get(TaskBoard.Empty.AddNote("Pay rent", Maybe<Guid>.None, clock, id1).Value.Board).Description);

    [Fact]
    public void Describe_sets_clears_and_trims_the_description_and_the_same_text_changes_nothing()
    {
        var board = TaskBoard.Empty.AddNote("Pay rent", Maybe<Guid>.None, clock, id1).Value.Board;

        var described = board.Describe(id1, "  the *landlord* wants it by Friday\n\n", clock.AddMinutes(1)).Value;
        Assert.Equal("the *landlord* wants it by Friday", Get(described).Description);
        Assert.Equal(clock.AddMinutes(1), Get(described).UpdatedAt);

        Assert.Same(described, described.Describe(id1, "the *landlord* wants it by Friday", clock.AddMinutes(2)).Value);
        Assert.Equal("", Get(described.Describe(id1, "  ", clock).Value).Description);
        Assert.True(board.Describe(Guid.NewGuid(), "x", clock).IsFailure);
    }

    [Fact]
    public void A_description_never_touches_a_claude_tasks_status_line()
    {
        var board = WithComments().SetStatusLine(id1, "waiting for CI", clock).Value;

        var described = board.Describe(id1, "Petre's own note", clock).Value;

        Assert.Equal("waiting for CI", Get(described).StatusLine);
        Assert.Equal("Petre's own note", Get(described).Description);
    }

    [Fact]
    public void Petre_edits_his_own_comment_and_the_thread_keeps_its_order()
    {
        var board = WithComments().EditComment(mine, "my **second** thought", clock.AddMinutes(3)).Value;

        Assert.Equal(["my **second** thought", "Claude's finding"], Get(board).Comments.Select(c => c.Markdown));
        Assert.Equal(clock.AddMinutes(3), Get(board).UpdatedAt);
    }

    [Fact]
    public void Claudes_comment_cannot_be_edited()
    {
        var board = WithComments();

        var refused = board.EditComment(claudes, "rewritten", clock);

        Assert.True(refused.IsFailure);
        Assert.Equal("Only your own comments can be edited", refused.Error);
    }

    [Fact]
    public void Editing_to_nothing_or_a_missing_comment_is_refused_and_the_same_text_changes_nothing()
    {
        var board = WithComments();

        Assert.True(board.EditComment(mine, "  ", clock).IsFailure);
        Assert.True(board.EditComment(Guid.NewGuid(), "x", clock).IsFailure);
        Assert.Same(board, board.EditComment(mine, "my **first** thought", clock).Value);
    }

    [Fact]
    public void A_comment_can_be_found_by_a_prefix_of_its_id()
    {
        var board = WithComments();

        Assert.Equal(mine, board.FindComment("cccccccc-0000-0000-0000-000000000001").Value.Id);
        Assert.True(board.FindComment("cccc").IsFailure); // two comments share it
        Assert.True(board.FindComment("zzzz").IsFailure);
    }

    [Fact]
    public void The_description_and_an_edited_comment_survive_a_save_and_load()
    {
        var board = WithComments().Describe(id1, "Petre's note", clock).Value.EditComment(mine, "edited", clock).Value;
        var store = new JsonTaskStore(dir);
        Assert.True(store.Save(board).IsSuccess);

        var loaded = new JsonTaskStore(dir).Load().Value;

        Assert.Equal("Petre's note", Get(loaded).Description);
        Assert.Equal("edited", Get(loaded).Comments.Single(c => c.Id == mine).Markdown);
    }

    [Fact]
    public void A_file_with_no_description_loads_as_none()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "tasks.json"),
            $$"""{"version":2,"tasks":[{"id":"{{id1}}","title":"t","status":"Backlog","created":"2026-11-22T09:00:00Z","updatedAt":"2026-11-22T09:00:00Z"}]}""");

        Assert.Equal("", Get(new JsonTaskStore(dir).Load().Value).Description);
    }

    [Fact]
    public void The_briefing_shows_a_session_the_description_of_a_task_it_is_offered()
    {
        var board = TaskBoard.Empty.AddNote("Pay rent", Guid.NewGuid(), clock, id1).Value.Board
            .Describe(id1, "Use the November invoice", clock).Value;
        var task = Get(board);
        var hook = ClaudeHookEvent.Parse("""{"session_id":"s9","cwd":"C:\\x","hook_event_name":"SessionStart"}""", "claude-vscode", "1").Value;

        var text = SessionBriefing.Start(hook, Maybe<TaskItem>.None, [task]);

        Assert.Contains("Use the November invoice", text);
    }
}
