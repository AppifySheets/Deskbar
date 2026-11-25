using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests.Tasks;

// `task overview` (src/Deskbar.Core/Tasks/TaskOverview.cs): one line per open task, for the manager session.
public class TaskOverviewTests
{
    readonly DateTimeOffset clock = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
    readonly TaskService service;
    readonly Guid deskbar = Guid.NewGuid();

    public TaskOverviewTests() =>
        service = new TaskService(new MemoryStore(), () => clock, filesRoot: @"C:\S");

    sealed class MemoryStore : ITaskStore
    {
        public Result<TaskBoard> Load() => TaskBoard.Empty;

        public Result Save(TaskBoard board) => Result.Success();
    }

    string Text(Func<string, Maybe<bool>>? busy = null) =>
        TaskOverview.Text(service.Board, clock, _ => "Deskbar", busy ?? (_ => Maybe<bool>.None));

    TaskItem Note(string title, Maybe<Guid> workspace = default, Maybe<Guid> parent = default) =>
        service.AddNote(title, workspace, Quadrant.IV, parent).Value;

    // A task with a session that joined and has not ended, or has ended.
    TaskItem WithSession(string title, string session, bool ended = false)
    {
        var task = Note(title);
        Assert.True(service.Join(task.Id, service.NewSession(session, @"C:\r")).IsSuccess);
        if (ended) service.SessionEnded(session);
        return service.Board.Tasks.Single(t => t.Id == task.Id);
    }

    [Fact]
    public void One_line_per_open_task_in_pile_order()
    {
        var first = Note("First");
        var second = Note("Second");
        var closed = Note("Closed one");
        Assert.True(service.Close(closed.Id).IsSuccess);

        var lines = Text().Split('\n');

        Assert.Equal(service.Board.Pile().Select(t => t.Id.ToString()[..8]), lines.Select(l => l[..8]));
        Assert.Equal(2, lines.Length);
        Assert.DoesNotContain("Closed one", Text());
        Assert.Contains(first.Id.ToString()[..8], lines[0] + lines[1]);
        Assert.Contains(second.Id.ToString()[..8], lines[0] + lines[1]);
    }

    [Fact]
    public void Line_shows_id8_status_flags_workspace_title_statusline_question_and_sessions()
    {
        var task = Note("Manager session", deskbar);
        Assert.True(service.Join(task.Id, service.NewSession("s1", @"C:\r")).IsSuccess);
        Assert.True(service.SetFlag(task.Id, TaskFlag.Important, true).IsSuccess);
        Assert.True(service.SetFlag(task.Id, TaskFlag.Urgent, true).IsSuccess);
        Assert.True(service.SetStatusLine(task.Id, "Design written").IsSuccess);
        Assert.True(service.Ask(task.Id, "Approve the design?").IsSuccess);

        Assert.Equal(
            $"{task.Id.ToString()[..8]}  waiting      !*  Deskbar: Manager session | Design written | asks: Approve the design? | claude: idle",
            Text());
    }

    [Fact]
    public void Title_is_cut_to_60_characters_with_an_ellipsis()
    {
        var task = Note(new string('x', 100));

        var line = Text();
        // id8, status padded to 11, two flag columns, each followed by two spaces: the title starts at column 27.
        var cut = line[27..].Split(" | ")[0];

        Assert.Equal(60, cut.Length);
        Assert.EndsWith("...", cut);
        Assert.Contains(task.Id.ToString()[..8], line);
    }

    [Fact]
    public void Subtasks_are_indented_two_spaces_under_their_parent()
    {
        var parent = Note("Parent");
        var child = Note("Child", parent: parent.Id);
        var other = Note("Other");

        var lines = Text().Split('\n');

        Assert.Equal(3, lines.Length);
        var parentAt = Array.FindIndex(lines, l => l.StartsWith(parent.Id.ToString()[..8]));
        Assert.StartsWith("  " + child.Id.ToString()[..8], lines[parentAt + 1]);
        Assert.StartsWith(other.Id.ToString()[..8], lines.Single(l => l.Contains("Other")));
    }

    [Fact]
    public void Snoozed_tasks_are_counted_on_one_closing_line()
    {
        Note("Awake");
        new[] { "a", "b", "c" }.Select(title => Note(title)).ToList()
            .ForEach(t => Assert.True(service.Snooze(t.Id, clock.AddHours(3)).IsSuccess));

        var lines = Text().Split('\n');

        Assert.Equal(2, lines.Length);
        Assert.Equal("+ 3 snoozed", lines[^1]);
    }

    [Fact]
    public void Sessions_read_none_working_idle_or_ended()
    {
        Note("Alone");
        WithSession("Busy one", "s-busy");
        WithSession("Quiet one", "s-idle");
        WithSession("Over one", "s-over", ended: true);

        var lines = Text(id => id == "s-busy" ? Maybe.From(true) : id == "s-idle" ? Maybe.From(false) : Maybe<bool>.None).Split('\n');

        Assert.EndsWith("claude: none", lines.Single(l => l.Contains("Alone")));
        Assert.EndsWith("claude: working", lines.Single(l => l.Contains("Busy one")));
        Assert.EndsWith("claude: idle", lines.Single(l => l.Contains("Quiet one")));
        Assert.EndsWith("claude: ended", lines.Single(l => l.Contains("Over one")));
    }
}
