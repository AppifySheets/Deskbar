using System.Text.Json;
using CSharpFunctionalExtensions;
using Deskbar.Core.Control;
using Deskbar.Core.Goals;
using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests;

// `deskbar.cmd goal ...`, the pipe twins of the planner. Design: docs/superpowers/specs/2026-11-22-goals-planner-design.md
// ("Pipe commands") and the plan's Task 5. Ids by full Guid or a prefix of four; a period by --week or --month with an
// optional yyyy-MM-dd date inside it, the current one without. Every failure is one line and changes nothing.
public sealed class RemoteControlGoalTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();
    readonly FakeGoalStore goalStore = new();
    DateTimeOffset clock = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    DateOnly today = new(2026, 11, 24);
    readonly Queue<Guid> ids = new();
    readonly TaskService tasks;
    readonly GoalService goals;

    public RemoteControlGoalTests()
    {
        tasks = new TaskService(new MemoryTaskStore(), () => clock);
        goals = new GoalService(goalStore, tasks, () => clock, () => today, () => ids.Count > 0 ? ids.Dequeue() : Guid.NewGuid());
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
        return manager;
    }

    ControlReply Run(params string[] args) => new RemoteControl(Started(), tasks: tasks, goals: goals).Execute(args);

    static JsonElement Json(ControlReply reply) => JsonDocument.Parse(reply.Output).RootElement;

    static JsonElement Ok(ControlReply reply)
    {
        Assert.True(reply.ExitCode == ControlReply.Ok, reply.Output);
        return Json(reply);
    }

    static string Id(JsonElement goal) => goal.GetProperty("id").GetString()!;

    JsonElement Added(params string[] args) => Ok(Run(["goal", "add", .. args]));

    void AssertRefusedAndUnchanged(ControlReply reply, GoalBoard before)
    {
        Assert.Equal(ControlReply.Failed, reply.ExitCode);
        Assert.DoesNotContain('\n', reply.Output.Trim());
        Assert.Same(before, goals.Board);
    }

    [Fact]
    public void Add_goes_to_this_week_by_default()
    {
        var goal = Added("Ship the goals planner");

        Assert.Equal("Ship the goals planner", goal.GetProperty("title").GetString());
        Assert.Equal("week", goal.GetProperty("kind").GetString());
        Assert.Equal("2026-11-23", goal.GetProperty("period").GetString());
        Assert.Equal("open", goal.GetProperty("status").GetString());
        Assert.Equal(0, goal.GetProperty("order").GetInt32());
        Assert.Equal(JsonValueKind.Null, goal.GetProperty("parent").ValueKind);
        Assert.Equal(goals.ThisWeek, Assert.Single(goals.Board.Goals).Period);

        Assert.Equal("2026-11-23", Added("Second", "--week").GetProperty("period").GetString());
        Assert.Equal("2026-11-30", Added("Next week", "--week", "2026-12-02").GetProperty("period").GetString());
    }

    [Fact]
    public void Add_with_month_and_a_date()
    {
        var goal = Added("Store listing", "--month", "2026-12-15");

        Assert.Equal("month", goal.GetProperty("kind").GetString());
        Assert.Equal("2026-12-01", goal.GetProperty("period").GetString());
        Assert.Equal("2026-11-01", Added("This month", "--month").GetProperty("period").GetString());
    }

    [Fact]
    public void A_date_that_is_not_a_date_is_refused()
    {
        var before = goals.Board;

        var reply = Run("goal", "add", "Ship it", "--week", "30-11");

        AssertRefusedAndUnchanged(reply, before);
        Assert.Equal("'30-11' is not a date; use yyyy-MM-dd", reply.Output.Trim());
        AssertRefusedAndUnchanged(Run("goal", "add", "Ship it", "--fortnight"), before);
        AssertRefusedAndUnchanged(Run("goal", "add", " "), before);
        Assert.Equal("A goal needs a title", Run("goal", "add", " ").Output.Trim());
    }

    [Fact]
    public void List_is_this_week_and_month_as_json()
    {
        var month = Id(Added("Store", "--month"));
        var week = Id(Added("Ship it"));
        Added("Last week", "--week", "2026-11-17");
        Added("Next month", "--month", "2026-12-01");
        Ok(Run("goal", "parent", week, month));
        var a = tasks.AddNote("a", Maybe<Guid>.None).Value;
        var b = tasks.AddNote("b", Maybe<Guid>.None).Value;
        goals.LinkTask(a.Id, Guid.Parse(week));
        goals.LinkTask(b.Id, Guid.Parse(week));
        tasks.Close(a.Id);

        var list = Ok(Run("goal", "list")).EnumerateArray().ToArray();

        Assert.Equal(["Store", "Ship it"], list.Select(g => g.GetProperty("title").GetString()));
        var monthly = list[0];
        Assert.Equal("0 of 1 weeks' goals done", monthly.GetProperty("progress").GetString());
        Assert.Equal(0, monthly.GetProperty("weeksDone").GetInt32());
        Assert.Equal(1, monthly.GetProperty("weeks").GetInt32());
        Assert.Equal(0, monthly.GetProperty("tasks").GetInt32());
        var weekly = list[1];
        Assert.Equal(month, weekly.GetProperty("parent").GetString());
        Assert.Equal("1 of 2 done", weekly.GetProperty("progress").GetString());
        Assert.Equal(1, weekly.GetProperty("tasksDone").GetInt32());
        Assert.Equal(2, weekly.GetProperty("tasks").GetInt32());
        Assert.Equal(JsonValueKind.Null, weekly.GetProperty("weeks").ValueKind);
        Assert.Equal(JsonValueKind.Null, weekly.GetProperty("weeksDone").ValueKind);
        Assert.Equal([a.Id.ToString(), b.Id.ToString()], weekly.GetProperty("taskIds").EnumerateArray().Select(t => t.GetString()));
    }

    [Fact]
    public void List_all_includes_past_periods()
    {
        Added("Last week", "--week", "2026-11-17");
        Added("Ship it");
        Added("Store", "--month");
        Added("October", "--month", "2026-10-05");

        var all = Ok(Run("goal", "list", "--all")).EnumerateArray().Select(g => g.GetProperty("title").GetString());
        Assert.Equal(["October", "Store", "Last week", "Ship it"], all);

        Assert.Equal(["Last week"], Ok(Run("goal", "list", "--week", "2026-11-20")).EnumerateArray().Select(g => g.GetProperty("title").GetString()));
        Assert.Equal(["Ship it"], Ok(Run("goal", "list", "--week")).EnumerateArray().Select(g => g.GetProperty("title").GetString()));
        Assert.Equal(["October"], Ok(Run("goal", "list", "--month", "2026-10-31")).EnumerateArray().Select(g => g.GetProperty("title").GetString()));
    }

    [Fact]
    public void Done_drop_reopen_rename_delete()
    {
        var id = Id(Added("Ship it"))[..6];

        Assert.Equal("done", Ok(Run("goal", "done", id)).GetProperty("status").GetString());
        Assert.Equal("open", Ok(Run("goal", "reopen", id)).GetProperty("status").GetString());
        Assert.Equal("dropped", Ok(Run("goal", "drop", id)).GetProperty("status").GetString());
        Assert.Equal("Ship the planner", Ok(Run("goal", "rename", id, "Ship the planner")).GetProperty("title").GetString());

        var before = goals.Board;
        var again = Run("goal", "drop", id);
        AssertRefusedAndUnchanged(again, before);
        Assert.Equal("The goal is already dropped.", again.Output.Trim());

        var deleted = Ok(Run("goal", "delete", id));
        Assert.Equal("Ship the planner", deleted.GetProperty("title").GetString());
        Assert.Empty(goals.Board.Goals);
    }

    [Fact]
    public void Carry_moves_to_the_current_period_and_keeps_the_id()
    {
        var old = Id(Added("Last week", "--week", "2026-11-17"));
        var oldMonth = Id(Added("October", "--month", "2026-10-01"));

        var carried = Ok(Run("goal", "carry", old));
        Assert.Equal(old, Id(carried));
        Assert.Equal("2026-11-23", carried.GetProperty("period").GetString());
        Assert.Equal("2026-11-01", Ok(Run("goal", "carry", oldMonth)).GetProperty("period").GetString());
        Assert.Equal("2026-11-30", Ok(Run("goal", "carry", old, "--week", "2026-12-06")).GetProperty("period").GetString());
        Assert.Single(goals.Board.Goals, g => g.Id == Guid.Parse(old));
    }

    [Fact]
    public void Carry_refuses_another_kind()
    {
        var id = Id(Added("Ship it"));
        var before = goals.Board;

        var reply = Run("goal", "carry", id, "--month");

        AssertRefusedAndUnchanged(reply, before);
        Assert.Equal("A weekly goal moves to another week", reply.Output.Trim());
    }

    // Review focus 1 from the pipe: the week of 30 November takes a parent from November or December, not October.
    [Fact]
    public void Parent_takes_a_month_the_week_touches()
    {
        var week = Id(Added("Split week", "--week", "2026-11-30"));
        var november = Id(Added("November", "--month"));
        var december = Id(Added("December", "--month", "2026-12-01"));
        var october = Id(Added("October", "--month", "2026-10-01"));

        Assert.Equal(november, Ok(Run("goal", "parent", week, november)).GetProperty("parent").GetString());
        Assert.Equal(december, Ok(Run("goal", "parent", week, december[..8])).GetProperty("parent").GetString());

        var before = goals.Board;
        var refused = Run("goal", "parent", week, october);
        AssertRefusedAndUnchanged(refused, before);
        Assert.Equal("'October' is a goal of October 2026, a month this week does not touch", refused.Output.Trim());

        Assert.Equal(JsonValueKind.Null, Ok(Run("goal", "parent", week, "none")).GetProperty("parent").ValueKind);
    }

    [Fact]
    public void Prompt_says_what_is_due_and_dismiss_clears_it()
    {
        // Ruling G3: nothing seen yet is a week due.
        Assert.Equal("week", Ok(Run("goal", "prompt")).GetProperty("due").GetString());

        Assert.Equal(JsonValueKind.Null, Ok(Run("goal", "prompt", "dismiss")).GetProperty("due").ValueKind);
        Assert.Equal(JsonValueKind.Null, Ok(Run("goal", "prompt")).GetProperty("due").ValueKind);
        Assert.Equal(new DateOnly(2026, 11, 23), goalStore.Stored.WeekSeen.Value);

        today = new DateOnly(2026, 12, 1);
        Assert.Equal("month", Ok(Run("goal", "prompt")).GetProperty("due").GetString());
    }

    [Fact]
    public void Unknown_and_ambiguous_ids_change_nothing()
    {
        ids.Enqueue(Guid.Parse("abcd0000-0000-0000-0000-000000000001"));
        ids.Enqueue(Guid.Parse("abcd0000-0000-0000-0000-000000000002"));
        Added("one");
        Added("two");
        var before = goals.Board;

        var ambiguous = Run("goal", "done", "abcd");
        AssertRefusedAndUnchanged(ambiguous, before);
        Assert.Equal("'abcd' matches several goals", ambiguous.Output.Trim());

        AssertRefusedAndUnchanged(Run("goal", "done", "ffff"), before);
        AssertRefusedAndUnchanged(Run("goal", "parent", "abcd0000-0000-0000-0000-000000000001", "ffff"), before);
        AssertRefusedAndUnchanged(Run("goal", "carry", "ffff", "--week", "2026-12-01"), before);
        AssertRefusedAndUnchanged(Run("goal", "rename", "ffff", "x"), before);
        AssertRefusedAndUnchanged(Run("goal", "delete", "ffff"), before);
    }

    [Fact]
    public void Wrong_arguments_print_every_goal_form()
    {
        var reply = Run("goal", "sideways");

        Assert.Equal(ControlReply.Failed, reply.ExitCode);
        Assert.Contains("usage: deskbar.cmd goal add <title>", reply.Output);
        Assert.Contains("usage: deskbar.cmd goal prompt [dismiss]", reply.Output);
    }

    [Fact]
    public void Goals_are_not_available_without_the_service()
    {
        var reply = new RemoteControl(Started(), tasks: tasks).Execute(["goal", "list"]);

        Assert.Equal(ControlReply.Failed, reply.ExitCode);
        Assert.Equal("Goals are not available", reply.Output.Trim());
        Assert.Equal("Goals are not available",
            new RemoteControl(Started(), tasks: tasks).Execute(["task", "goal", "abcd", "none"]).Output.Trim());
    }

    // No goal form depends on the caller's folder, so the client does not append --from to it.
    [Fact]
    public void The_client_adds_no_folder_to_goal() =>
        Assert.Equal(["goal", "list"], ControlArguments.WithCallerFolder(["goal", "list"], @"C:\repos\x"));
}
