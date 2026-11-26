using System.Text.Json;
using CSharpFunctionalExtensions;
using Deskbar.Core.Control;
using Deskbar.Core.Domain;
using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests;

// The subtask commands (docs/superpowers/specs/2026-11-24-subtasks-design.md, "Making subtasks", "What a session reads"):
// `task add --parent`, `task parent`, `task family` in both forms, a session's `task subtask`, the family in the
// `task join` reply and the SessionStart briefing, and `parent`/`subtasks` in every task's JSON.
public class RemoteControlSubtaskTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();
    readonly TaskService tasks = new(new MemoryTaskStore(), () => new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero));

    sealed class MemoryTaskStore : ITaskStore
    {
        public Result<TaskBoard> Load() => TaskBoard.Empty;

        public Result Save(TaskBoard board) => Result.Success();
    }

    const string Repo = @"C:\repos\TaskSpaces";

    // The app's transcript lookup, stood in for: every session's transcript is "T:<id>".
    static readonly TaskSessionCommands Sessions = new(_ => Result.Success(), _ => [], (_, _, _) => Result.Success(), (_, _) => Result.Success())
    {
        Transcript = s => $"T:{s.Id}",
    };

    WorkspaceManager Started()
    {
        var manager = new WorkspaceManager(desktops, monitor, titles, store, ownProcessId: 4242);
        Assert.True(manager.Start().IsSuccess);
        manager.AddWorkspace("Deskbar");
        manager.AddWorkspace("iam.ge");
        var window = new WindowInfo(new WindowHandle(0x10), 100, "Code", @"C:\VS Code\Code.exe", "main.cs - TaskSpaces - Visual Studio Code", null);
        desktops.WindowPlacements[window.Handle] = manager.State.Workspaces.Single(w => w.Name == "Deskbar").DesktopId!.Value;
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, window));
        return manager;
    }

    ControlReply Run(WorkspaceManager manager, params string[] args) =>
        new RemoteControl(manager, tasks: tasks, taskSessions: Sessions).Execute(args);

    ControlReply Said(WorkspaceManager manager, string session, params string[] form) =>
        Run(manager, ["task", .. form, "--session", session, "--from", Repo]);

    static JsonElement Json(ControlReply reply) => JsonDocument.Parse(reply.Output).RootElement;

    Guid IdOf(WorkspaceManager manager, string name) => manager.State.Workspaces.Single(w => w.Name == name).Id;

    TaskItem Titled(string title) => tasks.Board.Tasks.Single(t => t.Title == title);

    // A parent on iam.ge made by session p1, with one subtask.
    (TaskItem Parent, TaskItem Child) Family(WorkspaceManager manager)
    {
        var parent = tasks.Create("Parent work", IdOf(manager, "iam.ge"), tasks.NewSession("p1", Repo)).Value;
        var child = tasks.AddNote("Child work", parent.WorkspaceId, parent: parent.Id).Value;
        return (parent, child);
    }

    // --- task add --parent -----------------------------------------------------------------------

    [Fact]
    public void Add_with_a_parent_and_no_workspace_takes_the_parents()
    {
        var manager = Started();
        var parent = tasks.AddNote("Parent", IdOf(manager, "iam.ge")).Value;

        var reply = Run(manager, "task", "add", "Child", "--parent", parent.Id.ToString()[..8], "--from", Repo);

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Equal("iam.ge", Json(reply).GetProperty("workspace").GetString());
        Assert.Equal(Maybe.From(parent.Id), Titled("Child").ParentId);
    }

    [Fact]
    public void Add_with_a_parent_and_a_workspace_keeps_the_workspace()
    {
        var manager = Started();
        var parent = tasks.AddNote("Parent", IdOf(manager, "iam.ge")).Value;

        var reply = Run(manager, "task", "add", "Child", "--parent", parent.Id.ToString(), "--workspace", "Deskbar");

        Assert.Equal("Deskbar", Json(reply).GetProperty("workspace").GetString());
    }

    // Final review, finding 4: --workspace none keeps a subtask off every workspace.
    [Fact]
    public void Add_with_a_parent_and_workspace_none_is_on_none()
    {
        var manager = Started();
        var parent = tasks.AddNote("Parent", IdOf(manager, "iam.ge")).Value;

        var reply = Run(manager, "task", "add", "Child", "--parent", parent.Id.ToString(), "--workspace", "none");

        Assert.Equal(JsonValueKind.Null, Json(reply).GetProperty("workspace").ValueKind);
    }

    [Fact]
    public void Add_with_an_unknown_parent_fails_and_adds_nothing()
    {
        var manager = Started();

        var reply = Run(manager, "task", "add", "Child", "--parent", "ffffffff", "--workspace", "Deskbar");

        Assert.Equal(ControlReply.Failed, reply.ExitCode);
        Assert.Empty(tasks.Board.Tasks);
    }

    // --- task parent -----------------------------------------------------------------------------

    [Fact]
    public void Parent_sets_and_clears_the_link()
    {
        var manager = Started();
        var parent = tasks.AddNote("Parent", IdOf(manager, "Deskbar")).Value;
        var child = tasks.AddNote("Child", IdOf(manager, "Deskbar")).Value;

        var set = Run(manager, "task", "parent", child.Id.ToString()[..8], parent.Id.ToString()[..8]);
        Assert.Equal(parent.Id.ToString(), Json(set).GetProperty("parent").GetProperty("id").GetString());

        var cleared = Run(manager, "task", "parent", child.Id.ToString()[..8], "none");
        Assert.Equal(JsonValueKind.Null, Json(cleared).GetProperty("parent").ValueKind);
    }

    [Fact]
    public void Parent_reports_the_one_level_refusal()
    {
        var manager = Started();
        var (parent, child) = Family(manager);
        var other = tasks.AddNote("Other", IdOf(manager, "Deskbar")).Value;

        var reply = Run(manager, "task", "parent", other.Id.ToString(), child.Id.ToString());

        Assert.Equal(ControlReply.Failed, reply.ExitCode);
        Assert.StartsWith("Subtasks go one level deep", reply.Output);
    }

    // --- task family -----------------------------------------------------------------------------

    [Fact]
    public void Family_prints_the_family_text()
    {
        var manager = Started();
        var (parent, child) = Family(manager);

        var reply = Run(manager, "task", "family", child.Id.ToString()[..8]);

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Contains("This task is a subtask of", reply.Output);
        Assert.Contains("\"Parent work\"", reply.Output);
        Assert.Contains("transcript T:p1", reply.Output);
    }

    [Fact]
    public void Family_of_a_lone_task_says_so()
    {
        var manager = Started();
        var lone = tasks.AddNote("Lone", IdOf(manager, "Deskbar")).Value;

        var reply = Run(manager, "task", "family", lone.Id.ToString());

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Equal("This task has no parent, no subtasks and no linked tasks.", reply.Output.Trim());
    }

    [Fact]
    public void A_sessions_family_is_of_its_own_task()
    {
        var manager = Started();
        var (_, child) = Family(manager);
        Said(manager, "c1", "join", child.Id.ToString());

        var reply = Said(manager, "c1", "family");

        Assert.Contains("This task is a subtask of", reply.Output);
    }

    // --- a session's subtask ---------------------------------------------------------------------

    [Fact]
    public void A_session_makes_a_subtask_of_its_own_task()
    {
        var manager = Started();
        var (parent, _) = Family(manager);

        var reply = Said(manager, "p1", "subtask", "Split piece");

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Equal(Maybe.From(parent.Id), Titled("Split piece").ParentId);
        Assert.True(Titled("Split piece").Assignee.IsClaude);
    }

    // --- the subtask's own session starts at once (task fa056b2f) -----------------------------------

    // Every start the pipe asks for: the task, the folder, the model and whether it was the quiet one (StartQuietly).
    // `refuse` stands in for a start the app refuses (Claude sessions off); `withQuiet: false` is a copy that does not
    // set StartQuietly.
    sealed class Starts
    {
        public readonly List<(Guid Task, string Folder, Maybe<string> Model, bool Quiet)> Made = [];

        Func<TaskItem, string, Maybe<string>, Result> Recorder(string? refuse, bool quiet) => (task, folder, model) =>
        {
            if (refuse is not null) return Result.Failure(refuse);
            Made.Add((task.Id, folder, model, quiet));
            return Result.Success();
        };

        public TaskSessionCommands Commands(string? refuse = null, bool withQuiet = true) =>
            new(_ => Result.Success(), _ => [], Recorder(refuse, quiet: false), (_, _) => Result.Success())
            {
                StartQuietly = withQuiet ? Recorder(refuse, quiet: true) : null,
            };
    }

    ControlReply SaidWith(WorkspaceManager manager, TaskSessionCommands commands, string from, params string[] form) =>
        new RemoteControl(manager, tasks: tasks, taskSessions: commands).Execute(["task", .. form, "--session", "p1", "--from", from]);

    [Fact]
    public void A_subtask_starts_a_session_on_itself_in_the_callers_repo_not_its_worktree()
    {
        var manager = Started();
        Family(manager);
        var starts = new Starts();

        var reply = SaidWith(manager, starts.Commands(), Repo + @"\.claude\worktrees\topic", "subtask", "Split piece");

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.True(Json(reply).GetProperty("started").GetBoolean());
        Assert.False(Json(reply).TryGetProperty("notStarted", out _));
        var start = Assert.Single(starts.Made);
        Assert.Equal(Titled("Split piece").Id, start.Task);
        Assert.Equal(Repo, start.Folder);
        Assert.Equal(Maybe<string>.None, start.Model);
        // Nobody clicked for it, so the app is asked for the start that opens no window.
        Assert.True(start.Quiet);
    }

    // The folder the calling session was started in (its own task's record) wins over the shell's, which is wherever
    // the session last cd'd to; a worktree folder still starts at its repo.
    [Fact]
    public void The_start_folder_is_the_calling_sessions_recorded_repo_not_its_shell_folder()
    {
        var manager = Started();
        tasks.Create("Parent work", IdOf(manager, "iam.ge"), tasks.NewSession("p1", Repo + @"\.claude\worktrees\topic"));
        var starts = new Starts();

        SaidWith(manager, starts.Commands(), @"C:\repos\Other\src", "subtask", "Split piece");

        Assert.Equal(Repo, Assert.Single(starts.Made).Folder);
    }

    [Fact]
    public void A_copy_without_a_quiet_start_uses_the_plain_one()
    {
        var manager = Started();
        Family(manager);
        var starts = new Starts();

        SaidWith(manager, starts.Commands(withQuiet: false), Repo, "subtask", "Split piece");

        Assert.False(Assert.Single(starts.Made).Quiet);
    }

    [Fact]
    public void A_title_starting_with_dashes_is_taken_as_it_is()
    {
        var manager = Started();
        Family(manager);

        var reply = SaidWith(manager, new Starts().Commands(), Repo, "subtask", "--model flag ignored by X");

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Equal("--model flag ignored by X", Json(reply).GetProperty("title").GetString());
    }

    [Fact]
    public void No_start_leaves_the_subtask_in_the_backlog()
    {
        var manager = Started();
        Family(manager);
        var starts = new Starts();

        var reply = SaidWith(manager, starts.Commands(), Repo, "subtask", "--no-start", "Later piece");

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.False(Json(reply).GetProperty("started").GetBoolean());
        Assert.False(Json(reply).TryGetProperty("notStarted", out _));
        Assert.Empty(starts.Made);
        Assert.Equal(Deskbar.Core.Tasks.TaskStatus.Backlog, Titled("Later piece").Status);
    }

    [Fact]
    public void Model_names_the_started_sessions_model()
    {
        var manager = Started();
        Family(manager);
        var starts = new Starts();

        SaidWith(manager, starts.Commands(), Repo, "subtask", "Cheap piece", "--model", "haiku");

        Assert.Equal(Maybe.From("haiku"), Assert.Single(starts.Made).Model);
    }

    [Theory]
    [InlineData("subtask", "Piece", "--model", "not a model")]
    [InlineData("subtask", "Piece", "--no-start", "--model", "haiku")]
    [InlineData("subtask", "Piece", "Another title")]
    [InlineData("subtask", "Piece", "--soon")]
    [InlineData("subtask", "--no-start")]
    public void A_bad_subtask_form_changes_nothing(params string[] form)
    {
        var manager = Started();
        Family(manager);
        var before = tasks.Board.Tasks.Count;
        var starts = new Starts();

        Assert.Equal(ControlReply.Failed, SaidWith(manager, starts.Commands(), Repo, form).ExitCode);
        Assert.Equal(before, tasks.Board.Tasks.Count);
        Assert.Empty(starts.Made);
    }

    [Fact]
    public void A_refused_start_keeps_the_subtask_and_says_why()
    {
        var manager = Started();
        Family(manager);

        var reply = SaidWith(manager, new Starts().Commands(refuse: "Claude sessions are off"), Repo, "subtask", "Split piece");

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.False(Json(reply).GetProperty("started").GetBoolean());
        Assert.Equal("Claude sessions are off", Json(reply).GetProperty("notStarted").GetString());
        Assert.True(Titled("Split piece").Assignee.IsClaude);
    }

    [Fact]
    public void No_start_before_the_stdin_dash_still_reads_the_title()
    {
        var args = ControlArguments.WithTextInput(["task", "subtask", "--no-start", "-", "--session", "p1"], () => "Piece\n");

        Assert.Equal(["task", "subtask", "--no-start", "Piece", "--session", "p1"], args);
    }

    [Fact]
    public void A_subtask_form_without_a_session_is_refused()
    {
        var manager = Started();
        Assert.Equal(ControlReply.Failed, Run(manager, "task", "subtask", "Piece").ExitCode);
    }

    [Fact]
    public void A_subtask_title_from_stdin_keeps_its_special_characters()
    {
        const string title = "Fix \"a\" & b | c < d > e";
        var args = ControlArguments.WithTextInput(["task", "subtask", "-", "--session", "p1"], () => title + "\n");

        Assert.Equal(["task", "subtask", title, "--session", "p1"], args);
    }

    // --- join, SessionStart, JSON ----------------------------------------------------------------

    [Fact]
    public void Joining_a_subtask_answers_with_its_family()
    {
        var manager = Started();
        var (_, child) = Family(manager);

        var reply = Json(Said(manager, "c1", "join", child.Id.ToString()[..8]));

        Assert.Contains("\"Parent work\"", reply.GetProperty("family").GetString());
    }

    [Fact]
    public void Joining_a_lone_task_has_no_family()
    {
        var manager = Started();
        var lone = tasks.AddNote("Lone", IdOf(manager, "Deskbar")).Value;

        var reply = Json(Said(manager, "c1", "join", lone.Id.ToString()));

        Assert.Equal(JsonValueKind.Null, reply.GetProperty("family").ValueKind);
    }

    [Fact]
    public void A_resumed_session_on_a_subtask_reads_its_family_at_start()
    {
        var manager = Started();
        var (_, child) = Family(manager);
        Said(manager, "c1", "join", child.Id.ToString());

        var briefing = Run(manager, "claude-hook", "--entry", "claude-vscode", "--attended", "1", "--from", Repo, "--json",
            """{"session_id":"c1","cwd":"C:\\repos\\TaskSpaces","hook_event_name":"SessionStart","source":"resume"}""").Output;

        Assert.Contains("Your task is", briefing);
        Assert.Contains("This task is a subtask of", briefing);
    }

    [Fact]
    public void Every_tasks_json_has_its_parent_and_subtasks()
    {
        var manager = Started();
        var (parent, child) = Family(manager);

        var list = Json(Run(manager, "task", "list")).EnumerateArray().ToArray();
        var p = list.Single(t => t.GetProperty("id").GetString() == parent.Id.ToString());
        var c = list.Single(t => t.GetProperty("id").GetString() == child.Id.ToString());

        Assert.Equal(JsonValueKind.Null, p.GetProperty("parent").ValueKind);
        Assert.Equal([child.Id.ToString()], p.GetProperty("subtasks").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("Parent work", c.GetProperty("parent").GetProperty("title").GetString());
        Assert.Empty(c.GetProperty("subtasks").EnumerateArray());
    }

    [Fact]
    public void The_usage_lists_the_subtask_forms()
    {
        Assert.Contains(ControlUsage.All, c => c.Verb == "task" && c.Arguments.StartsWith("family"));
        Assert.Contains(ControlUsage.All, c => c.Verb == "task" && c.Arguments.StartsWith("parent"));
        Assert.Contains(ControlUsage.All, c => c.Verb == "task" && c.Arguments.StartsWith("subtask"));
        Assert.Contains(ControlUsage.All, c => c.Verb == "task" && c.Arguments.StartsWith("add") && c.Arguments.Contains("--parent"));
    }
}
