using System.Text.Json;
using CSharpFunctionalExtensions;
using Deskbar.Core.Control;
using Deskbar.Core.Domain;
using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests;

// `deskbar.cmd task ...`. Design: docs/superpowers/specs/2026-11-19-tasks-in-the-bar-design.md ("Remote control").
// Add parks on the caller's workspace (found from --from like `attention`), or on a named one, or on none.
// Every failure is one line and leaves the board as it was.
public class RemoteControlTaskTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();
    // Noon keeps "today" unambiguous in any local time zone.
    DateTimeOffset clock = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
    readonly TaskService tasks;

    public RemoteControlTaskTests() => tasks = new TaskService(new MemoryTaskStore(), () => clock);

    sealed class MemoryTaskStore : ITaskStore
    {
        public Result<TaskBoard> Load() => TaskBoard.Empty;

        public Result Save(TaskBoard board) => Result.Success();
    }

    WorkspaceManager Started()
    {
        var manager = new WorkspaceManager(desktops, monitor, titles, store, ownProcessId: 4242);
        Assert.True(manager.Start().IsSuccess);
        manager.AddWorkspace("Deskbar");
        manager.AddWorkspace("iam.ge");
        return manager;
    }

    ControlReply Run(WorkspaceManager manager, params string[] args) => new RemoteControl(manager, tasks: tasks).Execute(args);

    Guid WorkspaceId(WorkspaceManager manager, string name) => manager.State.Workspaces.Single(w => w.Name == name).Id;

    void Editor(WorkspaceManager manager, nint hwnd, string folder, string workspace)
    {
        var window = new WindowInfo(new WindowHandle(hwnd), 100, "Code", @"C:\VS Code\Code.exe", $"main.cs - {folder} - Visual Studio Code", null);
        desktops.WindowPlacements[window.Handle] = manager.State.Workspaces.Single(w => w.Name == workspace).DesktopId!.Value;
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, window));
    }

    static JsonElement Json(ControlReply reply) => JsonDocument.Parse(reply.Output).RootElement;

    static string Prefix(JsonElement task) => task.GetProperty("id").GetString()![..6];

    // A task added on Deskbar, answered as its JSON.
    JsonElement Added(WorkspaceManager manager, string title = "Wait for CI") =>
        Json(Run(manager, "task", "add", title, "--workspace", "Deskbar"));

    void AssertRefusedAndUnchanged(ControlReply reply, TaskBoard before)
    {
        Assert.Equal(ControlReply.Failed, reply.ExitCode);
        Assert.DoesNotContain('\n', reply.Output.Trim());
        Assert.Same(before, tasks.Board);
    }

    [Fact]
    public void Add_parks_the_callers_workspace()
    {
        var manager = Started();
        Editor(manager, 0x10, "TaskSpaces", "Deskbar");

        var reply = Run(manager, "task", "add", "Wait for CI", "--in", "10m", "--from", @"C:\repos\TaskSpaces\.claude\worktrees\x");

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        var task = Assert.Single(tasks.Board.Tasks);
        Assert.Equal(WorkspaceId(manager, "Deskbar"), task.WorkspaceId.Value);
        Assert.Equal(TaskState.Running, task.StateAt(clock));
        Assert.Equal("Deskbar", Json(reply).GetProperty("workspace").GetString());
        Assert.Equal("running", Json(reply).GetProperty("state").GetString());
    }

    [Fact]
    public void Add_with_workspace_none_is_free()
    {
        var manager = Started();

        var reply = Run(manager, "task", "add", "Buy milk", "--workspace", "none");

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.True(Assert.Single(tasks.Board.Tasks).WorkspaceId.HasNoValue);
        Assert.Equal(JsonValueKind.Null, Json(reply).GetProperty("workspace").ValueKind);
    }

    [Fact]
    public void Add_by_workspace_name_ignores_case()
    {
        var manager = Started();

        Run(manager, "task", "add", "Review", "--workspace", "IAM.GE");

        Assert.Equal(WorkspaceId(manager, "iam.ge"), Assert.Single(tasks.Board.Tasks).WorkspaceId.Value);
    }

    [Fact]
    public void Adding_the_same_title_again_reuses_the_task()
    {
        var manager = Started();
        var first = Added(manager);

        var second = Added(manager);

        Assert.Single(tasks.Board.Tasks);
        Assert.Equal(first.GetProperty("id").GetString(), second.GetProperty("id").GetString());
    }

    [Fact]
    public void Add_without_a_workspace_and_without_a_caller_is_refused()
    {
        var manager = Started();
        var before = tasks.Board;

        var reply = Run(manager, "task", "add", "Wait for CI", "--from", @"C:\nowhere");

        AssertRefusedAndUnchanged(reply, before);
        Assert.Contains("--workspace", reply.Output);
    }

    [Fact]
    public void Add_to_an_unknown_workspace_is_refused()
    {
        var manager = Started();

        AssertRefusedAndUnchanged(Run(manager, "task", "add", "x", "--workspace", "nope"), tasks.Board);
    }

    [Fact]
    public void List_includes_asks()
    {
        var manager = Started();
        manager.RequestAttention(WorkspaceId(manager, "iam.ge"), Maybe<WindowHandle>.None);
        Added(manager);

        var entries = Json(Run(manager, "task", "list")).EnumerateArray().ToList();

        Assert.Equal(2, entries.Count);
        var ask = Assert.Single(entries, e => e.GetProperty("kind").GetString() == "ask");
        Assert.Equal("iam.ge", ask.GetProperty("workspace").GetString());
        Assert.Equal("due", ask.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, ask.GetProperty("id").ValueKind);
        Assert.Equal(JsonValueKind.Null, ask.GetProperty("title").ValueKind);
        var task = Assert.Single(entries, e => e.GetProperty("kind").GetString() == "task");
        Assert.Equal("open", task.GetProperty("state").GetString());
        Assert.Equal("Wait for CI", task.GetProperty("title").GetString());
    }

    [Fact]
    public void List_shows_a_timers_end_and_a_done_time()
    {
        var manager = Started();
        Run(manager, "task", "add", "Running", "--workspace", "Deskbar", "--in", "5m");
        var finished = Added(manager, "Finished");
        Run(manager, "task", "done", Prefix(finished));

        var entries = Json(Run(manager, "task", "list")).EnumerateArray().ToList();

        var running = Assert.Single(entries, e => e.GetProperty("title").GetString() == "Running");
        Assert.Equal(clock.AddMinutes(5), running.GetProperty("endsAt").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, running.GetProperty("doneAt").ValueKind);
        var done = Assert.Single(entries, e => e.GetProperty("title").GetString() == "Finished");
        Assert.Equal("done", done.GetProperty("state").GetString());
        Assert.Equal(clock, done.GetProperty("doneAt").GetDateTimeOffset());
    }

    [Fact]
    public void Timer_done_rename_delete_by_prefix()
    {
        var manager = Started();
        var id = Prefix(Added(manager));

        var timer = Run(manager, "task", "timer", id, "--in", "5m");
        Assert.Equal(ControlReply.Ok, timer.ExitCode);
        Assert.Equal(TaskState.Running, tasks.Board.Tasks.Single().StateAt(clock));

        var cleared = Run(manager, "task", "timer", id, "--clear");
        Assert.Equal(ControlReply.Ok, cleared.ExitCode);
        Assert.Equal(TaskState.Open, tasks.Board.Tasks.Single().StateAt(clock));

        var renamed = Run(manager, "task", "rename", id, "Wait for the deploy");
        Assert.Equal("Wait for the deploy", Json(renamed).GetProperty("title").GetString());
        Assert.Equal("Wait for the deploy", tasks.Board.Tasks.Single().Title);

        var done = Run(manager, "task", "done", id);
        Assert.Equal("done", Json(done).GetProperty("state").GetString());
        Assert.Equal(TaskState.Done, tasks.Board.Tasks.Single().StateAt(clock));

        var deleted = Run(manager, "task", "delete", id);
        Assert.Equal(ControlReply.Ok, deleted.ExitCode);
        Assert.Equal("Wait for the deploy", Json(deleted).GetProperty("title").GetString());
        Assert.Empty(tasks.Board.Tasks);
    }

    [Fact]
    public void Unknown_id_fails()
    {
        var manager = Started();
        Added(manager);
        var before = tasks.Board;

        AssertRefusedAndUnchanged(Run(manager, "task", "done", "ffffff"), before);
        AssertRefusedAndUnchanged(Run(manager, "task", "delete", "ffffff"), before);
        AssertRefusedAndUnchanged(Run(manager, "task", "timer", "ffffff", "--clear"), before);
        AssertRefusedAndUnchanged(Run(manager, "task", "rename", "ffffff", "x"), before);
    }

    // Final review M2: a done task takes no timer and cannot be done twice; each is exit 1 with the board's reason.
    [Fact]
    public void A_done_task_refuses_a_timer_and_a_second_done()
    {
        var manager = Started();
        var id = Prefix(Added(manager));
        Assert.Equal(ControlReply.Ok, Run(manager, "task", "done", id).ExitCode);
        var before = tasks.Board;

        var timer = Run(manager, "task", "timer", id, "--in", "5m");
        var again = Run(manager, "task", "done", id);

        AssertRefusedAndUnchanged(timer, before);
        AssertRefusedAndUnchanged(again, before);
        Assert.Equal("The task is done.", timer.Output.Trim());
        Assert.Equal("The task is done.", again.Output.Trim());
    }

    [Fact]
    public void Ambiguous_prefix_fails()
    {
        var ids = new Queue<Guid>([Guid.Parse("abcd0000-0000-0000-0000-000000000001"), Guid.Parse("abcd0000-0000-0000-0000-000000000002")]);
        var service = new TaskService(new MemoryTaskStore(), () => clock, ids.Dequeue);
        var manager = Started();
        var control = new RemoteControl(manager, tasks: service);
        control.Execute(["task", "add", "One", "--workspace", "none"]);
        control.Execute(["task", "add", "Two", "--workspace", "none"]);
        var before = service.Board;

        var reply = control.Execute(["task", "done", "abcd"]);

        Assert.Equal(ControlReply.Failed, reply.ExitCode);
        Assert.Contains("several", reply.Output);
        Assert.Same(before, service.Board);
    }

    [Fact]
    public void Empty_title_fails()
    {
        var manager = Started();
        var id = Prefix(Added(manager));
        var before = tasks.Board;

        AssertRefusedAndUnchanged(Run(manager, "task", "add", "  ", "--workspace", "none"), before);
        AssertRefusedAndUnchanged(Run(manager, "task", "rename", id, "  "), before);
    }

    [Fact]
    public void Length_outside_presets_fails()
    {
        var manager = Started();
        var id = Prefix(Added(manager));
        var before = tasks.Board;

        AssertRefusedAndUnchanged(Run(manager, "task", "add", "x", "--workspace", "none", "--in", "7m"), before);
        AssertRefusedAndUnchanged(Run(manager, "task", "timer", id, "--in", "7m"), before);
    }

    [Fact]
    public void Wrong_arguments_print_every_task_form()
    {
        var manager = Started();

        var reply = Run(manager, "task", "done");

        Assert.Equal(ControlReply.Failed, reply.ExitCode);
        Assert.Contains("usage: deskbar.cmd task list", reply.Output);
        Assert.Contains("usage: deskbar.cmd task delete <id>", reply.Output);
    }

    [Fact]
    public void Without_a_task_service_every_subcommand_is_refused()
    {
        var reply = new RemoteControl(Started()).Execute(["task", "list"]);

        Assert.Equal(ControlReply.Failed, reply.ExitCode);
        Assert.Equal("Tasks are not available", reply.Output);
    }

    [Fact]
    public void The_client_adds_its_folder_to_task() =>
        Assert.Equal(["task", "add", "x", "--from", @"C:\work"], ControlArguments.WithCallerFolder(["task", "add", "x"], @"C:\work"));

    [Fact]
    public void Task_is_in_the_help() =>
        Assert.Contains(ControlUsage.All, c => c.Verb == "task");
}
