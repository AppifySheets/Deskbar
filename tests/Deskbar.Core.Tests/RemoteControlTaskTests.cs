using System.Text.Json;
using CSharpFunctionalExtensions;
using Deskbar.Core.Control;
using Deskbar.Core.Domain;
using Deskbar.Core.Goals;
using Deskbar.Core.Tasks;
// System.Threading.Tasks (an implicit using) has a TaskStatus too; the alias says which one this file means.
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests;

// `deskbar.cmd task ...`, the forms a script or Petre uses. Design: docs/superpowers/specs/2026-11-22-tasks-board-design.md
// ("Left to the builder": the pipe commands). Add makes a note on the caller's workspace (found from --from like
// `attention`), or on a named one, or on none. Every failure is one line and leaves the board as it was.
public class RemoteControlTaskTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();
    DateTimeOffset clock = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
    readonly TaskService tasks;

    readonly GoalService goals;

    public RemoteControlTaskTests()
    {
        tasks = new TaskService(new MemoryTaskStore(), () => clock);
        goals = new GoalService(new FakeGoalStore(), tasks, () => clock, () => DateOnly.FromDateTime(clock.UtcDateTime));
    }

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

    ControlReply Run(WorkspaceManager manager, params string[] args) => new RemoteControl(manager, tasks: tasks, goals: goals).Execute(args);

    Guid WorkspaceId(WorkspaceManager manager, string name) => manager.State.Workspaces.Single(w => w.Name == name).Id;

    void Editor(WorkspaceManager manager, nint hwnd, string folder, string workspace)
    {
        var window = new WindowInfo(new WindowHandle(hwnd), 100, "Code", @"C:\VS Code\Code.exe", $"main.cs - {folder} - Visual Studio Code", null);
        desktops.WindowPlacements[window.Handle] = manager.State.Workspaces.Single(w => w.Name == workspace).DesktopId!.Value;
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, window));
    }

    static JsonElement Json(ControlReply reply) => JsonDocument.Parse(reply.Output).RootElement;

    static string Prefix(JsonElement task) => task.GetProperty("id").GetString()![..6];

    // A note added on Deskbar, answered as its JSON.
    JsonElement Added(WorkspaceManager manager, string title = "Dishwasher tablets") =>
        Json(Run(manager, "task", "add", title, "--workspace", "Deskbar"));

    TaskItem Only => Assert.Single(tasks.Board.Tasks);

    void AssertRefusedAndUnchanged(ControlReply reply, TaskBoard before)
    {
        Assert.Equal(ControlReply.Failed, reply.ExitCode);
        Assert.DoesNotContain('\n', reply.Output.Trim());
        Assert.Same(before, tasks.Board);
    }

    [Fact]
    public void Add_makes_a_note_on_the_callers_workspace()
    {
        var manager = Started();
        Editor(manager, 0x10, "TaskSpaces", "Deskbar");

        var reply = Run(manager, "task", "add", "Dishwasher tablets", "--from", @"C:\repos\TaskSpaces\.claude\worktrees\x");

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Equal(WorkspaceId(manager, "Deskbar"), Only.WorkspaceId.Value);
        Assert.Equal(TaskStatus.Backlog, Only.Status);
        Assert.False(Only.IsClaudes);
        Assert.Equal("Deskbar", Json(reply).GetProperty("workspace").GetString());
        Assert.Equal("backlog", Json(reply).GetProperty("status").GetString());
        Assert.False(Json(reply).GetProperty("claudes").GetBoolean());
    }

    [Fact]
    public void Add_with_none_makes_a_free_note()
    {
        var manager = Started();

        var reply = Run(manager, "task", "add", "Buy milk", "--workspace", "none");

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.True(Only.WorkspaceId.HasNoValue);
        Assert.Equal(JsonValueKind.Null, Json(reply).GetProperty("workspace").ValueKind);
    }

    [Fact]
    public void Add_by_workspace_name_ignores_case()
    {
        var manager = Started();

        Run(manager, "task", "add", "Review", "--workspace", "IAM.GE");

        Assert.Equal(WorkspaceId(manager, "iam.ge"), Only.WorkspaceId.Value);
    }

    [Fact]
    public void Add_without_a_workspace_and_without_a_caller_is_refused()
    {
        var manager = Started();
        var before = tasks.Board;

        var reply = Run(manager, "task", "add", "Dishwasher tablets", "--from", @"C:\nowhere");

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
    public void Close_and_reopen()
    {
        var manager = Started();
        var id = Prefix(Added(manager));

        var closed = Run(manager, "task", "close", id);
        Assert.Equal(ControlReply.Ok, closed.ExitCode);
        Assert.Equal("closed", Json(closed).GetProperty("status").GetString());
        Assert.Equal(TaskStatus.Closed, Only.Status);

        var before = tasks.Board;
        var again = Run(manager, "task", "close", id);
        AssertRefusedAndUnchanged(again, before);
        Assert.Equal("The task is closed.", again.Output.Trim());

        var reopened = Run(manager, "task", "reopen", id);
        Assert.Equal(ControlReply.Ok, reopened.ExitCode);
        Assert.Equal("backlog", Json(reopened).GetProperty("status").GetString());
        Assert.Equal(TaskStatus.Backlog, Only.Status);
    }

    [Fact]
    public void Status_takes_the_four_names_and_refuses_closed()
    {
        var manager = Started();
        var id = Prefix(Added(manager));

        new[] { ("in-progress", TaskStatus.InProgress), ("waiting", TaskStatus.WaitingOnYou), ("done", TaskStatus.Done), ("backlog", TaskStatus.Backlog) }
            .ToList().ForEach(pair =>
            {
                var reply = Run(manager, "task", "status", id, pair.Item1);
                Assert.Equal(ControlReply.Ok, reply.ExitCode);
                Assert.Equal(pair.Item1, Json(reply).GetProperty("status").GetString());
                Assert.Equal(pair.Item2, Only.Status);
            });

        var before = tasks.Board;
        var closed = Run(manager, "task", "status", id, "closed");
        AssertRefusedAndUnchanged(closed, before);
        Assert.Equal("Use close to close a task", closed.Output.Trim());

        var unknown = Run(manager, "task", "status", id, "snoozed");
        AssertRefusedAndUnchanged(unknown, before);
        Assert.Contains("backlog, in-progress, waiting or done", unknown.Output);
    }

    [Fact]
    public void List_is_the_pile_in_order_as_json()
    {
        var manager = Started();
        var closed = Prefix(Added(manager, "Closed one"));
        Run(manager, "task", "close", closed);
        clock = clock.AddMinutes(1);
        var done = Prefix(Added(manager, "Done one"));
        Run(manager, "task", "status", done, "done");
        clock = clock.AddMinutes(1);
        Added(manager, "Backlog one");
        clock = clock.AddMinutes(1);
        var waiting = Prefix(Added(manager, "Waiting one"));
        Run(manager, "task", "status", waiting, "waiting");
        clock = clock.AddMinutes(1);
        tasks.Create("Claude's one", WorkspaceId(manager, "Deskbar"), new TaskSession("s1", @"C:\repos\TaskSpaces", clock, Maybe<DateTimeOffset>.None));
        var claudes = tasks.Board.Tasks.Single(t => t.Title == "Claude's one");
        tasks.SetStatusLine(claudes.Id, "waiting for CI");
        tasks.Ask(claudes.Id, "OK to merge?");
        tasks.Comment(claudes.Id, "s1", "The detail");
        Run(manager, "task", "comment", claudes.Id.ToString()[..6], "Go ahead");

        var entries = Json(Run(manager, "task", "list")).EnumerateArray().ToList();

        // Task 12: Done no longer sorts ahead; it keeps its place in its quadrant, where the newer note is on top.
        Assert.Equal(["Claude's one", "Waiting one", "Backlog one", "Done one"], entries.Select(e => e.GetProperty("title").GetString()));
        Assert.Equal(["waiting", "waiting", "backlog", "done"], entries.Select(e => e.GetProperty("status").GetString()));
        var first = entries[0];
        Assert.Equal(claudes.Id.ToString(), first.GetProperty("id").GetString());
        Assert.Equal("Deskbar", first.GetProperty("workspace").GetString());
        Assert.Equal("waiting for CI", first.GetProperty("statusLine").GetString());
        Assert.Equal("OK to merge?", first.GetProperty("question").GetString());
        Assert.True(first.GetProperty("claudes").GetBoolean());
        var session = Assert.Single(first.GetProperty("sessions").EnumerateArray());
        Assert.Equal("s1", session.GetProperty("id").GetString());
        Assert.True(session.GetProperty("live").GetBoolean());
        Assert.Equal(2, first.GetProperty("comments").GetInt32());
        Assert.Equal(clock, first.GetProperty("updatedAt").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, entries[1].GetProperty("question").ValueKind);
        Assert.False(entries[1].GetProperty("claudes").GetBoolean());
        Assert.Empty(entries[1].GetProperty("sessions").EnumerateArray());
    }

    [Fact]
    public void Rename_move_comment_delete_by_prefix()
    {
        var manager = Started();
        var id = Prefix(Added(manager));

        var renamed = Run(manager, "task", "rename", id, "Soap");
        Assert.Equal("Soap", Json(renamed).GetProperty("title").GetString());
        Assert.Equal("Soap", Only.Title);

        var moved = Run(manager, "task", "move", id, "iam.ge");
        Assert.Equal("iam.ge", Json(moved).GetProperty("workspace").GetString());
        Assert.Equal(WorkspaceId(manager, "iam.ge"), Only.WorkspaceId.Value);

        Assert.Equal(ControlReply.Ok, Run(manager, "task", "move", id, "none").ExitCode);
        Assert.True(Only.WorkspaceId.HasNoValue);

        var commented = Run(manager, "task", "comment", id, "Get the **big** box");
        Assert.Equal(1, Json(commented).GetProperty("comments").GetInt32());
        var comment = Only.Comments.Single();
        Assert.Equal("Get the **big** box", comment.Markdown);
        Assert.True(comment.SessionId.HasNoValue);

        var deleted = Run(manager, "task", "delete", id);
        Assert.Equal(ControlReply.Ok, deleted.ExitCode);
        Assert.Equal("Soap", Json(deleted).GetProperty("title").GetString());
        Assert.Empty(tasks.Board.Tasks);
    }

    [Fact]
    public void Unknown_id_fails()
    {
        var manager = Started();
        Added(manager);
        var before = tasks.Board;

        AssertRefusedAndUnchanged(Run(manager, "task", "close", "ffffff"), before);
        AssertRefusedAndUnchanged(Run(manager, "task", "reopen", "ffffff"), before);
        AssertRefusedAndUnchanged(Run(manager, "task", "status", "ffffff", "done"), before);
        AssertRefusedAndUnchanged(Run(manager, "task", "rename", "ffffff", "x"), before);
        AssertRefusedAndUnchanged(Run(manager, "task", "move", "ffffff", "none"), before);
        AssertRefusedAndUnchanged(Run(manager, "task", "comment", "ffffff", "x"), before);
        AssertRefusedAndUnchanged(Run(manager, "task", "delete", "ffffff"), before);
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

        var reply = control.Execute(["task", "close", "abcd"]);

        Assert.Equal(ControlReply.Failed, reply.ExitCode);
        Assert.Contains("several", reply.Output);
        Assert.Same(before, service.Board);
    }

    [Fact]
    public void Empty_text_fails()
    {
        var manager = Started();
        var id = Prefix(Added(manager));
        var before = tasks.Board;

        AssertRefusedAndUnchanged(Run(manager, "task", "add", "  ", "--workspace", "none"), before);
        AssertRefusedAndUnchanged(Run(manager, "task", "rename", id, "  "), before);
        AssertRefusedAndUnchanged(Run(manager, "task", "comment", id, "  "), before);
    }

    // The timer build's forms are gone: `timer` is not a form any more, and `--in` is not an option of `add`.
    [Fact]
    public void Timer_and_in_are_gone()
    {
        var manager = Started();
        var id = Prefix(Added(manager));
        var before = tasks.Board;

        var timer = Run(manager, "task", "timer", id, "--in", "5m");
        Assert.Equal(ControlReply.Failed, timer.ExitCode);
        Assert.Contains("usage: deskbar.cmd task list", timer.Output);
        Assert.Same(before, tasks.Board);

        var add = Run(manager, "task", "add", "x", "--workspace", "none", "--in", "5m");
        AssertRefusedAndUnchanged(add, before);
        Assert.Equal("Unexpected --in", add.Output.Trim());

        Assert.DoesNotContain(ControlUsage.All, c => c.Verb == "task" && (c.Arguments.Contains("--in") || c.Arguments.StartsWith("timer")));
    }

    [Fact]
    public void Wrong_arguments_print_every_task_form()
    {
        var manager = Started();

        var reply = Run(manager, "task", "close");

        Assert.Equal(ControlReply.Failed, reply.ExitCode);
        Assert.Contains("usage: deskbar.cmd task list", reply.Output);
        Assert.Contains("usage: deskbar.cmd task status <id> <backlog|in-progress|waiting|done>", reply.Output);
        Assert.Contains("usage: deskbar.cmd task delete <id>", reply.Output);
    }

    [Fact]
    public void Without_a_task_service_every_subcommand_is_refused()
    {
        var reply = new RemoteControl(Started()).Execute(["task", "list"]);

        Assert.Equal(ControlReply.Failed, reply.ExitCode);
        Assert.Equal("Tasks are not available", reply.Output);
    }

    // --- task goal (goals plan, Task 5) ------------------------------------------------------------
    // The pipe twin of the More menu's "Goal": through GoalService.LinkTask, so the goal must be open; none unlinks.

    [Fact]
    public void Task_goal_links_and_none_unlinks()
    {
        var manager = Started();
        var id = Prefix(Added(manager));
        var goal = goals.Add("Ship the goals planner", goals.ThisWeek).Value;

        var linked = Run(manager, "task", "goal", id, goal.Id.ToString()[..6]);

        Assert.Equal(ControlReply.Ok, linked.ExitCode);
        Assert.Equal(goal.Id, Only.GoalId.Value);
        Assert.Equal(goal.Id.ToString(), Json(linked).GetProperty("goal").GetProperty("id").GetString());
        Assert.Equal("Ship the goals planner", Json(linked).GetProperty("goal").GetProperty("title").GetString());

        var unlinked = Run(manager, "task", "goal", id, "none");
        Assert.Equal(ControlReply.Ok, unlinked.ExitCode);
        Assert.True(Only.GoalId.HasNoValue);
        Assert.Equal(JsonValueKind.Null, Json(unlinked).GetProperty("goal").ValueKind);
    }

    [Fact]
    public void Task_goal_refuses_a_done_goal()
    {
        var manager = Started();
        var id = Prefix(Added(manager));
        var goal = goals.Add("Ship it", goals.ThisWeek).Value;
        goals.MarkDone(goal.Id);
        var before = tasks.Board;

        var reply = Run(manager, "task", "goal", id, goal.Id.ToString());

        AssertRefusedAndUnchanged(reply, before);
        Assert.Equal("The goal is done", reply.Output.Trim());
        AssertRefusedAndUnchanged(Run(manager, "task", "goal", id, "ffff"), before);
        AssertRefusedAndUnchanged(Run(manager, "task", "goal", "ffff", goal.Id.ToString()), before);
    }

    // Every task's JSON names its goal, or null; a link to a goal that no longer exists reads as null too.
    [Fact]
    public void Task_list_names_the_goal()
    {
        var manager = Started();
        Added(manager, "free");
        var linked = Prefix(Added(manager, "linked"));
        var goal = goals.Add("Ship it", goals.ThisWeek).Value;
        Run(manager, "task", "goal", linked, goal.Id.ToString());
        var dangling = Prefix(Added(manager, "dangling"));
        tasks.SetGoal(tasks.Board.Find(dangling).Value.Id, Guid.NewGuid());

        var list = Json(Run(manager, "task", "list")).EnumerateArray()
            .ToDictionary(t => t.GetProperty("title").GetString()!, t => t.GetProperty("goal"));

        Assert.Equal(JsonValueKind.Null, list["free"].ValueKind);
        Assert.Equal(JsonValueKind.Null, list["dangling"].ValueKind);
        Assert.Equal("Ship it", list["linked"].GetProperty("title").GetString());
    }

    [Fact]
    public void The_client_adds_its_folder_to_task() =>
        Assert.Equal(["task", "add", "x", "--from", @"C:\work"], ControlArguments.WithCallerFolder(["task", "add", "x"], @"C:\work"));

    // A closed task leaves `task list`, so a note ticked by mistake would otherwise be out of reach from a script:
    // `task list --closed` prints the closed ones, the latest closed first, in the same shape, and `reopen` brings one
    // back. Closing stamps UpdatedAt, which is what orders them.
    [Fact]
    public void Task_list_closed_prints_the_closed_tasks_latest_closed_first()
    {
        var manager = Started();
        var first = Prefix(Added(manager, "Closed first"));
        clock = clock.AddMinutes(1);
        var second = Prefix(Added(manager, "Closed second"));
        clock = clock.AddMinutes(1);
        Added(manager, "Still open");
        clock = clock.AddMinutes(1);
        Run(manager, "task", "close", first);
        clock = clock.AddMinutes(1);
        Run(manager, "task", "close", second);

        var closed = Json(Run(manager, "task", "list", "--closed")).EnumerateArray().ToList();

        Assert.Equal(["Closed second", "Closed first"], closed.Select(e => e.GetProperty("title").GetString()));
        Assert.All(closed, e => Assert.Equal("closed", e.GetProperty("status").GetString()));
        Assert.Equal("Deskbar", closed[0].GetProperty("workspace").GetString());
        Assert.Equal(["Still open"], Json(Run(manager, "task", "list")).EnumerateArray().Select(e => e.GetProperty("title").GetString()));
    }

    // Task 12: the toggles' twin. `on` and `off` set a flag; the task moves to the top of the quadrant it lands in.
    [Fact]
    public void Flag_sets_and_clears_important_and_urgent()
    {
        var manager = Started();
        var id = Prefix(Added(manager));

        var important = Run(manager, "task", "flag", id, "important", "on");
        Assert.Equal(ControlReply.Ok, important.ExitCode);
        Assert.True(Json(important).GetProperty("important").GetBoolean());
        Assert.Equal("II", Json(important).GetProperty("quadrant").GetString());

        Ok(Run(manager, "task", "flag", id, "URGENT", "on"));
        Assert.True(Only.Important && Only.Urgent);

        Ok(Run(manager, "task", "flag", id, "important", "off"));
        Assert.Equal(Quadrant.III, Only.Quadrant);
    }

    [Fact]
    public void Flag_refuses_what_it_does_not_know()
    {
        var manager = Started();
        var id = Prefix(Added(manager));
        var before = tasks.Board;

        AssertRefusedAndUnchanged(Run(manager, "task", "flag", id, "huge", "on"), before);
        AssertRefusedAndUnchanged(Run(manager, "task", "flag", id, "urgent", "maybe"), before);
        AssertRefusedAndUnchanged(Run(manager, "task", "flag", "ffffff", "urgent", "on"), before);
        Assert.Equal(ControlReply.Failed, Run(manager, "task", "flag", id, "urgent").ExitCode);
    }

    // The drag's twin: top, end, or a 1-based position within the task's own quadrant.
    [Fact]
    public void Order_moves_a_task_within_its_quadrant()
    {
        var manager = Started();
        var a = Prefix(Added(manager, "a"));
        clock = clock.AddMinutes(1);
        Added(manager, "b");
        clock = clock.AddMinutes(1);
        var c = Prefix(Added(manager, "c"));
        string[] Titles() => Json(Run(manager, "task", "list")).EnumerateArray().Select(e => e.GetProperty("title").GetString()!).ToArray();
        Assert.Equal(["c", "b", "a"], Titles());

        Ok(Run(manager, "task", "order", a, "top"));
        Assert.Equal(["a", "c", "b"], Titles());

        Ok(Run(manager, "task", "order", a, "end"));
        Assert.Equal(["c", "b", "a"], Titles());

        var second = Run(manager, "task", "order", c, "2");
        Assert.Equal(ControlReply.Ok, second.ExitCode);
        Assert.Equal(2, Json(second).GetProperty("order").GetInt32());
        Assert.Equal(["b", "c", "a"], Titles());
    }

    [Fact]
    public void Order_refuses_a_bad_place_and_a_waiting_task()
    {
        var manager = Started();
        var id = Prefix(Added(manager));
        var before = tasks.Board;

        AssertRefusedAndUnchanged(Run(manager, "task", "order", id, "sideways"), before);
        AssertRefusedAndUnchanged(Run(manager, "task", "order", id, "0"), before);
        AssertRefusedAndUnchanged(Run(manager, "task", "order", "ffffff", "top"), before);

        Run(manager, "task", "status", id, "waiting");
        before = tasks.Board;
        var waiting = Run(manager, "task", "order", id, "top");
        AssertRefusedAndUnchanged(waiting, before);
        Assert.Contains("waiting on you", waiting.Output);
    }

    // `list` says each task's flags, its quadrant and its place in that quadrant (none while it waits on Petre: it is
    // pinned on top until answered, and only then takes its place).
    [Fact]
    public void List_carries_the_flags_the_quadrant_and_the_order()
    {
        var manager = Started();
        var a = Prefix(Added(manager, "a"));
        clock = clock.AddMinutes(1);
        Added(manager, "b");
        clock = clock.AddMinutes(1);
        var w = Prefix(Added(manager, "w"));
        Run(manager, "task", "flag", a, "urgent", "on");
        Run(manager, "task", "flag", a, "important", "on");
        Run(manager, "task", "status", w, "waiting");

        var entries = Json(Run(manager, "task", "list")).EnumerateArray().ToList();

        Assert.Equal(["w", "a", "b"], entries.Select(e => e.GetProperty("title").GetString()));
        Assert.Equal(["IV", "I", "IV"], entries.Select(e => e.GetProperty("quadrant").GetString()));
        Assert.True(entries[1].GetProperty("important").GetBoolean());
        Assert.True(entries[1].GetProperty("urgent").GetBoolean());
        Assert.False(entries[2].GetProperty("important").GetBoolean());
        Assert.Equal(JsonValueKind.Null, entries[0].GetProperty("order").ValueKind);
        Assert.Equal(1, entries[1].GetProperty("order").GetInt32());
        Assert.Equal(1, entries[2].GetProperty("order").GetInt32());
    }

    static void Ok(ControlReply reply) => Assert.True(reply.ExitCode == ControlReply.Ok, reply.Output);

    [Fact]
    public void Task_is_in_the_help() =>
        Assert.Equal(
            ["list [--closed]", "add <title>|- [--workspace <name>|none]", "close <id>", "reopen <id>",
             "status <id> <backlog|in-progress|waiting|done>", "rename <id> <title>", "move <id> <workspace>|none",
             "comment <id> <markdown>|-", "delete <id>", "flag <id> important|urgent on|off", "order <id> top|end|<n>",
             // A Claude session's own forms (plan Task 8, RemoteControl.ClaudeHook.cs); `-` reads the text from stdin.
             "join <id> --session <session>", "new <title>|- --session <session>", "status-line <text>|- --session <session>",
             "ask <question>|- --session <session>", "done --session <session>", "comment <markdown>|- --session <session>",
             "later <title>|- --session <session>",
             "goal <id> <goal-id>|none"],
            ControlUsage.All.Where(c => c.Verb == "task").Select(c => c.Arguments));
}
