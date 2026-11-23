using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
// System.Threading.Tasks (an implicit using) has a TaskStatus too; the alias says which one this file means.
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Tasks;

// The tasks window's grouping and filters, pure (TaskViews; docs/superpowers/specs/2026-11-22-tasks-board-design.md,
// "The window"). The window itself is tested in Deskbar.Windows.Tests (TasksBoardTests); these pin the rules it draws.
public class TaskViewsTests
{
    DateTimeOffset clock = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);
    static readonly Guid ws = Guid.Parse("11111111-1111-1111-1111-111111111111");
    static readonly Guid other = Guid.Parse("22222222-2222-2222-2222-222222222222");
    static readonly Maybe<Guid> Free = Maybe<Guid>.None;

    TaskBoard board = TaskBoard.Empty;

    Guid Note(string title, Maybe<Guid> workspace)
    {
        var id = Guid.NewGuid();
        board = board.AddNote(title, workspace, clock, id).Value.Board;
        clock = clock.AddMinutes(1);
        return id;
    }

    Guid Claudes(string title, Maybe<Guid> workspace, string session = "s1")
    {
        var id = Guid.NewGuid();
        board = board.Create(title, workspace, Session(session), clock, id).Value.Board;
        clock = clock.AddMinutes(1);
        return id;
    }

    TaskSession Session(string id) => new(id, @"C:\repos\TaskSpaces", clock, Maybe<DateTimeOffset>.None);

    void Change(Func<TaskBoard, Result<TaskBoard>> change)
    {
        board = change(board).Value;
        clock = clock.AddMinutes(1);
    }

    string Title(Guid id) => board.Tasks.Single(t => t.Id == id).Title;

    // Each group as "Kind: title, title", so two lists of groups compare by value. Task 12: the open view is Waiting on
    // you, then Covey's quadrants I to IV (TaskGroupKind), as the bar.
    IReadOnlyList<string> Shown(TaskFilter filter, TaskWorkspaceFilter where) =>
        TaskViews.Grouped(board, filter, where).Select(g => $"{g.Kind}: {string.Join(", ", g.Tasks.Select(t => t.Title))}").ToList();

    [Fact]
    public void The_open_view_is_waiting_then_the_quadrants_done_keeping_its_place()
    {
        var note = Note("Dishwasher tablets", Free);
        var working = Claudes("DurableFile move guard", ws, "s1");
        var asking = Claudes("Tasks redesign", ws, "s2");
        var done = Claudes("Store screenshots", ws, "s3");
        var closed = Note("Old errand", Free);
        Change(b => b.Ask(asking, "OK to restart?", clock));
        Change(b => b.MarkDone(done, clock));
        Change(b => b.Close(closed, clock));

        Assert.Equal(
            [
                $"WaitingOnYou: {Title(asking)}",
                $"IV: {Title(done)}, {Title(working)}, {Title(note)}",
            ],
            Shown(TaskFilter.Open, TaskWorkspaceFilter.All));
    }

    // Task 12 (Ruling 28): each quadrant is a group of its own, in Covey's order, captioned in his words; a status filter
    // keeps the quadrants, showing only that status in each.
    [Fact]
    public void The_quadrants_are_groups_of_their_own_and_a_status_filter_keeps_them()
    {
        var a = Note("a", Free);
        var b = Note("b", Free);
        var c = Note("c", Free);
        var d = Claudes("d", ws);
        Change(x => x.SetFlag(a, TaskFlag.Important, true, clock));
        Change(x => x.SetFlag(b, TaskFlag.Urgent, true, clock));
        Change(x => x.SetFlag(c, TaskFlag.Important, true, clock));
        Change(x => x.SetFlag(c, TaskFlag.Urgent, true, clock));

        Assert.Equal(["I: c", "II: a", "III: b", "IV: d"], Shown(TaskFilter.Open, TaskWorkspaceFilter.All));
        Assert.Equal(["I: c", "II: a", "III: b"], Shown(TaskFilter.Backlog, TaskWorkspaceFilter.All));
        Assert.Equal(["IV: d"], Shown(TaskFilter.InProgress, TaskWorkspaceFilter.All));
        Assert.Equal("I  Urgent and important", TaskViews.GroupName(TaskGroupKind.I));
        Assert.Equal("II  Important, not urgent", TaskViews.GroupName(TaskGroupKind.II));
        Assert.Equal("III  Urgent, not important", TaskViews.GroupName(TaskGroupKind.III));
        Assert.Equal("IV  Not urgent, not important", TaskViews.GroupName(TaskGroupKind.IV));
        Assert.Equal("Waiting on you", TaskViews.GroupName(TaskGroupKind.WaitingOnYou));
        Assert.Equal(Quadrant.II, TaskViews.QuadrantOf(TaskGroupKind.II).Value);
        Assert.True(TaskViews.QuadrantOf(TaskGroupKind.Closed).HasNoValue);
    }

    // The Matrix (Ruling 28): four cells, I to IV, always, each its quadrant's open tasks in their order, with the tasks
    // waiting on Petre on top of their own quadrant's cell (pinned, as everywhere).
    [Fact]
    public void The_matrix_has_four_cells_with_waiting_tasks_on_top_of_their_own()
    {
        var a = Note("a", Free);
        var b = Note("b", Free);
        var w = Note("w", Free);
        Note("closed", Free);
        Change(x => x.SetFlag(a, TaskFlag.Important, true, clock));
        Change(x => x.SetFlag(w, TaskFlag.Important, true, clock));
        Change(x => x.SetStatus(w, TaskStatus.WaitingOnYou, clock));
        Change(x => x.Close(board.Tasks.Single(t => t.Title == "closed").Id, clock));

        var cells = TaskViews.Matrix(board, TaskFilter.Open, TaskWorkspaceFilter.All);

        Assert.Equal([Quadrant.I, Quadrant.II, Quadrant.III, Quadrant.IV], cells.Select(c => c.Quadrant));
        Assert.Equal([[], ["w", "a"], [], ["b"]], cells.Select(c => c.Tasks.Select(t => t.Title).ToArray()));
        Assert.Equal([[], ["a"], [], ["b"]], TaskViews.Matrix(board, TaskFilter.Backlog, TaskWorkspaceFilter.All).Select(c => c.Tasks.Select(t => t.Title).ToArray()));
        Assert.All(TaskViews.Matrix(board, TaskFilter.Closed, TaskWorkspaceFilter.All), c => Assert.Empty(c.Tasks));
    }

    [Fact]
    public void A_group_lists_its_tasks_newest_raised_first()
    {
        Note("first", Free);
        Note("second", Free);
        Note("third", Free);

        Assert.Equal(["IV: third, second, first"], Shown(TaskFilter.Open, TaskWorkspaceFilter.All));
    }

    [Fact]
    public void Closed_tasks_show_only_under_their_filter_the_latest_closed_first()
    {
        var a = Note("a", Free);
        var b = Note("b", Free);
        Note("open", Free);
        Change(x => x.Close(b, clock));
        Change(x => x.Close(a, clock));

        Assert.Equal(["IV: open"], Shown(TaskFilter.Open, TaskWorkspaceFilter.All));
        Assert.Equal(["Closed: a, b"], Shown(TaskFilter.Closed, TaskWorkspaceFilter.All));
    }

    [Fact]
    public void A_status_filter_shows_that_status_alone()
    {
        Note("note", Free);
        var asking = Claudes("asking", ws);
        Change(b => b.Ask(asking, "?", clock));

        Assert.Equal(["WaitingOnYou: asking"], Shown(TaskFilter.WaitingOnYou, TaskWorkspaceFilter.All));
        Assert.Equal(["IV: note"], Shown(TaskFilter.Backlog, TaskWorkspaceFilter.All));
        Assert.Empty(Shown(TaskFilter.Done, TaskWorkspaceFilter.All));
    }

    [Fact]
    public void The_workspace_filter_takes_all_one_workspace_or_none()
    {
        Note("here", ws);
        Note("there", other);
        Note("nowhere", Free);

        Assert.Equal(["IV: nowhere, there, here"], Shown(TaskFilter.Open, TaskWorkspaceFilter.All));
        Assert.Equal(["IV: here"], Shown(TaskFilter.Open, TaskWorkspaceFilter.On(ws)));
        Assert.Equal(["IV: nowhere"], Shown(TaskFilter.Open, TaskWorkspaceFilter.On(Free)));
    }

    [Fact]
    public void Counts_follow_the_workspace_filter()
    {
        Note("here", ws);
        var asking = Claudes("asking", other);
        Change(b => b.Ask(asking, "?", clock));
        var gone = Note("gone", ws);
        Change(b => b.Close(gone, clock));

        Assert.Equal(2, TaskViews.Count(board, TaskFilter.Open, TaskWorkspaceFilter.All));
        Assert.Equal(1, TaskViews.Count(board, TaskFilter.WaitingOnYou, TaskWorkspaceFilter.All));
        Assert.Equal(1, TaskViews.Count(board, TaskFilter.Open, TaskWorkspaceFilter.On(ws)));
        Assert.Equal(1, TaskViews.Count(board, TaskFilter.Closed, TaskWorkspaceFilter.On(ws)));
        Assert.Equal(0, TaskViews.Count(board, TaskFilter.WaitingOnYou, TaskWorkspaceFilter.On(ws)));
    }

    // The session a click on a Claude task would open (spec, "Going to a session": the one waiting on Petre, else the
    // most recent). Nothing records which session asked, and while a task waits it is a live session that asked, so
    // a live session comes first, the latest joined; with none live, the latest joined.
    [Fact]
    public void The_session_to_go_to_is_the_latest_live_one_else_the_latest()
    {
        var id = Claudes("work", ws, "old");
        clock = clock.AddMinutes(5);
        Change(b => b.Join(id, Session("live"), clock));
        Assert.Equal("live", TaskViews.SessionToGoTo(board.Tasks.Single(t => t.Id == id)).Value.Id);

        // The later one ends: the older one, still live, comes first.
        board = board.SessionEnded("live", clock);
        Assert.Equal("old", TaskViews.SessionToGoTo(board.Tasks.Single(t => t.Id == id)).Value.Id);

        // Both ended: the latest joined.
        board = board.SessionEnded("old", clock.AddMinutes(1));
        Assert.Equal("live", TaskViews.SessionToGoTo(board.Tasks.Single(t => t.Id == id)).Value.Id);
    }

    // Task 9: the thread's session list (spec, "Task history"). Newest joined first; Working or Idle while `claude agents`
    // lists it (by its Busy); a session it does not list is Ended while its transcript is there and Expired once Claude
    // Code has deleted it.
    [Fact]
    public void The_session_list_is_newest_first_with_its_states()
    {
        var id = Claudes("work", ws, "oldest");
        Change(b => b.Join(id, Session("gone"), clock));
        Change(b => b.Join(id, Session("idle"), clock));
        Change(b => b.Join(id, Session("busy"), clock));
        var task = board.Tasks.Single(t => t.Id == id);
        IReadOnlyCollection<ClaudeAgent> listed =
        [
            new("busy", Maybe.From("busy"), @"C:\repos\TaskSpaces", true, true, ""),
            new("idle", Maybe.From("idle"), @"C:\repos\TaskSpaces", true, false, ""),
        ];

        var list = TaskViews.SessionList(task, Maybe.From(listed), s => s.Id != "gone");

        Assert.Equal(["busy:Working", "idle:Idle", "gone:Expired", "oldest:Ended"], list.Select(s => $"{s.Id}:{s.State}"));
        Assert.Equal(task.Sessions.Single(s => s.Id == "busy").Joined, list[0].Joined);
    }

    // Without a list ("cannot tell": the feature is off, or `claude` could not be read) the hooks' word stands: a session
    // they call live is Idle (nothing says it works), an ended one Ended or Expired by its transcript.
    [Fact]
    public void Without_a_list_the_session_list_takes_the_hooks_word()
    {
        var id = Claudes("work", ws, "live");
        Change(b => b.Join(id, Session("ended"), clock));
        board = board.SessionEnded("ended", clock);
        var task = board.Tasks.Single(t => t.Id == id);

        var list = TaskViews.SessionList(task, Maybe<IReadOnlyCollection<ClaudeAgent>>.None, _ => true);

        Assert.Equal(["ended:Ended", "live:Idle"], list.Select(s => $"{s.Id}:{s.State}"));
    }

    [Fact]
    public void A_note_has_no_session_to_go_to()
    {
        var id = Note("note", ws);

        Assert.True(TaskViews.SessionToGoTo(board.Tasks.Single(t => t.Id == id)).HasNoValue);
    }

    // Review: the bar shows a description's first line as text, without markdown markers.
    [Theory]
    [InlineData("**Use** the _November_ `invoice`", "Use the November invoice")]
    [InlineData("- a list item\nsecond", "a list item")]
    [InlineData("# A heading", "A heading")]
    [InlineData("> quoted words", "quoted words")]
    [InlineData("1. first step", "first step")]
    [InlineData("see [the docs](https://example.com/x) now", "see the docs now")]
    [InlineData("\n\n  __bold__ and *italic*", "bold and italic")]
    [InlineData("snake_case_name stays", "snake_case_name stays")]
    [InlineData("", "")]
    public void The_first_line_of_markdown_is_plain_text(string markdown, string expected) =>
        Assert.Equal(expected, TaskViews.PlainFirstLine(markdown));
}
