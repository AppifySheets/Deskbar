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
        tasks = new TaskService(new MemoryTaskStore(), () => clock, filesRoot: @"C:\S");
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

    // Ruling 37's pipe twin: `task add <title> [--important] [--urgent]`, in any order after the title; the note tops
    // the quadrant its flags give.
    [Theory]
    [InlineData(new string[0], false, false)]
    [InlineData(new[] { "--important" }, true, false)]
    [InlineData(new[] { "--urgent" }, false, true)]
    [InlineData(new[] { "--urgent", "--important" }, true, true)]
    public void Add_takes_the_flags(string[] flags, bool important, bool urgent)
    {
        var manager = Started();

        var reply = Run(manager, ["task", "add", "Pay rent", .. flags, "--workspace", "none"]);

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Equal(important, Only.Important);
        Assert.Equal(urgent, Only.Urgent);
        Assert.Equal(Only.Id, tasks.Board.Group(QuadrantOf.Flags(important, urgent))[0].Id);
    }

    [Fact]
    public void A_flag_given_twice_is_refused()
    {
        var manager = Started();
        var before = tasks.Board;

        AssertRefusedAndUnchanged(Run(manager, "task", "add", "Pay rent", "--urgent", "--urgent", "--workspace", "none"), before);
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

    // Task f905b1a7: with no VS Code window on the caller's folder, a note goes where the latest task worked on in the
    // caller's repo went, rather than being refused. Petre runs sessions in terminal windows, so often there is no
    // VS Code window at all, and a session's `task add` used to end up on `--workspace none`.
    [Fact]
    public void Add_without_a_vs_code_window_goes_where_the_repos_latest_task_went()
    {
        var manager = Started();
        tasks.Create("Tasks board", WorkspaceId(manager, "Deskbar"), tasks.NewSession("s1", @"C:\repos\TaskSpaces"));

        var reply = Run(manager, "task", "add", "Flaky test", "--from", @"C:\repos\TaskSpaces\.claude\worktrees\flaky");

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Equal(WorkspaceId(manager, "Deskbar"), tasks.Board.Tasks.Single(t => t.Title == "Flaky test").WorkspaceId.Value);
    }

    // Ruling 57: `task assign <id> me|claude` and `assignee` in the JSON.
    [Fact]
    public void Assign_hands_a_task_to_claude_or_back_and_list_says_who_has_it()
    {
        var manager = Started();
        var added = Added(manager);
        Assert.Equal("me", added.GetProperty("assignee").GetString());

        var reply = Run(manager, "task", "assign", Prefix(added), "claude");

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Equal(Assignee.Claude, Only.Assignee);
        Assert.Equal("claude", Json(reply).GetProperty("assignee").GetString());
        Assert.Equal("claude", Json(Run(manager, "task", "list"))[0].GetProperty("assignee").GetString());

        Run(manager, "task", "assign", Prefix(added), "ME");
        Assert.Equal(Assignee.Me, Only.Assignee);
    }

    [Fact]
    public void Assign_refuses_a_name_that_is_not_me_or_claude_and_changes_nothing()
    {
        var manager = Started();
        var added = Added(manager);
        var before = tasks.Board;

        AssertRefusedAndUnchanged(Run(manager, "task", "assign", Prefix(added), "anna"), before);
    }

    // Ruling 58's pipe twins: `task describe <id> <markdown>` (or `-` for stdin) and `task comment-edit <comment-id> <markdown>`,
    // which refuses a comment that is not his. `task list` carries the description and the thread's comment ids.
    [Fact]
    public void Describe_sets_the_description_and_list_shows_it()
    {
        var manager = Started();
        var id = Prefix(Added(manager));

        var reply = Run(manager, "task", "describe", id, "The **landlord** wants it");

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Equal("The **landlord** wants it", Only.Description);
        Assert.Equal("The **landlord** wants it", Json(reply).GetProperty("description").GetString());
        Assert.Equal("The **landlord** wants it", Json(Run(manager, "task", "list"))[0].GetProperty("description").GetString());
    }

    // Images in tasks (docs/superpowers/specs/2026-11-24-task-images-design.md): `task list` carries each comment's text, so
    // a session can read Petre's comments, with every image line's path made absolute for its Read tool; the description
    // the same way.
    [Fact]
    public void Task_list_thread_carries_each_comments_markdown_with_absolute_image_paths()
    {
        var manager = Started();
        var id = Prefix(Added(manager));
        Run(manager, "task", "comment", id, "Look\n\n![image](task-images/t/1.png)");
        Run(manager, "task", "describe", id, "Brief\n![image](task-images/t/2.png)");

        var task = Json(Run(manager, "task", "list"))[0];

        Assert.Equal("Look\n\n![image](<C:\\S\\task-images\\t\\1.png>)", task.GetProperty("thread")[0].GetProperty("markdown").GetString());
        Assert.Equal("Brief\n![image](<C:\\S\\task-images\\t\\2.png>)", task.GetProperty("description").GetString());
    }

    // Final review, Important 3: a session runs `task status-line` every turn, and every verb answers with the task; the
    // thread's text is for `task list` only, or each status line would print the whole thread back to the session.
    [Fact]
    public void A_verbs_reply_carries_the_thread_without_its_text()
    {
        var manager = Started();
        var id = Prefix(Added(manager));
        Run(manager, "task", "comment", id, "a long comment");

        var reply = Json(Run(manager, "task", "flag", id, "urgent", "on"));

        var entry = reply.GetProperty("thread")[0];
        Assert.False(entry.TryGetProperty("markdown", out _));
        Assert.Equal("you", entry.GetProperty("by").GetString());
    }

    // A comment wakes its session (docs/superpowers/plans/2026-11-24-comment-wakes-session.md, Task 2): Petre's own
    // `task comment <id>` is announced for waking; the same command run inside a Claude session (the client sees CLAUDECODE
    // and adds --from-claude) is stored the same but announced to no one, so one session cannot wake another.
    [Fact]
    public void His_task_comment_is_announced_and_a_sessions_by_id_is_not()
    {
        var manager = Started();
        var id = Prefix(Added(manager));
        var heard = new List<string>();
        using var _ = tasks.Commented.Subscribe(c => heard.Add(c.Comment.Markdown));

        Assert.Equal(ControlReply.Ok, Run(manager, "task", "comment", id, "from Petre").ExitCode);
        Assert.Equal(ControlReply.Ok, Run(manager, "task", "comment", id, "from a session", "--from-claude").ExitCode);

        Assert.Equal(["from Petre"], heard);
        Assert.Equal(["from Petre", "from a session"], Only.Comments.Select(c => c.Markdown));
    }

    // The manager session (docs/superpowers/specs/2026-11-25-manager-session-design.md) is told by --from its own folder.
    const string ManagerDir = @"C:\State\manager";

    // A control that knows the manager's folder, called from `from` (the manager's own by default).
    ControlReply RunFrom(WorkspaceManager manager, string managerFolder, string from, params string[] args) =>
        new RemoteControl(manager, tasks: tasks, goals: goals, taskSessions: new TaskSessionCommands(_ => Result.Success(), _ => [], (_, _, _) => Result.Success(), (_, _) => Result.Success())
        {
            ManagerFolder = managerFolder,
        }).Execute([.. args, "--from", from]);

    ControlReply RunAsManager(WorkspaceManager manager, params string[] args) => RunFrom(manager, ManagerDir, ManagerDir, args);

    [Fact]
    public void Manager_comment_without_the_claude_marker_is_still_the_managers()
    {
        var manager = Started();
        var id = Prefix(Added(manager));

        Assert.Equal(ControlReply.Ok, RunAsManager(manager, "task", "comment", id, "hello").ExitCode);

        Assert.True(Only.Comments.Single().ByManager);
    }

    // --- the manager's own question (task 61e4d143, ManagerQuestion) ---

    // A manager on record, as ManagerActions stores one when it starts the day's session.
    static WorkspaceManager WithManagerRecord(WorkspaceManager manager)
    {
        manager.SetManagerSession(new ManagerRecord("mgr", new DateOnly(2026, 11, 22)));
        return manager;
    }

    [Fact]
    public void Manager_ask_stores_the_question_on_its_record()
    {
        var manager = WithManagerRecord(Started());

        var reply = RunAsManager(manager, "task", "ask", "Which comes first?");

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Equal("Which comes first?", manager.State.Manager!.Question);
        Assert.Equal(clock, manager.State.Manager.AskedAt);
        Assert.Equal("mgr", manager.State.Manager.SessionId);
    }

    [Fact]
    public void A_second_manager_ask_replaces_the_first()
    {
        var manager = WithManagerRecord(Started());
        RunAsManager(manager, "task", "ask", "first");

        Assert.Equal(ControlReply.Ok, RunAsManager(manager, "task", "ask", "second").ExitCode);

        Assert.Equal("second", manager.State.Manager!.Question);
    }

    [Fact]
    public void Manager_answered_clears_the_question()
    {
        var manager = WithManagerRecord(Started());
        RunAsManager(manager, "task", "ask", "open");

        Assert.Equal(ControlReply.Ok, RunAsManager(manager, "task", "answered").ExitCode);

        Assert.Null(manager.State.Manager!.Question);
        Assert.Null(manager.State.Manager.AskedAt);
    }

    [Fact]
    public void Manager_ask_with_no_record_stored_fails_and_changes_nothing()
    {
        var manager = Started();
        var before = tasks.Board;

        var reply = RunAsManager(manager, "task", "ask", "anyone there?");

        AssertRefusedAndUnchanged(reply, before);
        Assert.Equal(ManagerQuestion.NoManager, reply.Output.Trim());
        Assert.Null(manager.State.Manager);
    }

    [Fact]
    public void A_non_manager_ask_without_its_session_is_still_refused()
    {
        var manager = WithManagerRecord(Started());

        var reply = Run(manager, "task", "ask", "x");

        Assert.Equal(ControlReply.Failed, reply.ExitCode);
        Assert.Contains("task ask is a session's form and needs --session <id>", reply.Output);
        Assert.Null(manager.State.Manager!.Question);
    }

    [Fact]
    public void Manager_may_not_finish_a_task_through_a_session_form()
    {
        var manager = Started();
        var id = Prefix(Added(manager));
        var before = tasks.Board;

        var reply = RunAsManager(manager, "task", "done", "--session", "s1");

        AssertRefusedAndUnchanged(reply, before);
        Assert.Equal(ManagerSession.Refusal, reply.Output.Trim());
    }

    [Theory]
    [InlineData("join")]
    [InlineData("ask")]
    [InlineData("answered")]
    [InlineData("comment")]
    [InlineData("repeat")]
    public void Manager_may_use_no_other_session_form(string form)
    {
        var manager = Started();
        var id = Prefix(Added(manager));
        var before = tasks.Board;
        string[] args = form is "join" ? ["task", "join", id, "--session", "s1"] : form is "comment" ? ["task", "comment", "x", "--session", "s1"] : ["task", form, "--session", "s1"];

        var reply = RunAsManager(manager, args);

        AssertRefusedAndUnchanged(reply, before);
        Assert.Equal(ManagerSession.JoinRefusal, reply.Output.Trim());
    }

    [Fact]
    public void Manager_comment_is_stored_as_the_managers_and_wakes()
    {
        var manager = Started();
        var id = Prefix(Added(manager));
        var heard = new List<TaskComment>();
        using var _ = tasks.Commented.Subscribe(c => heard.Add(c.Comment));

        Assert.Equal(ControlReply.Ok, RunAsManager(manager, "task", "comment", id, "please rebase", "--from-claude").ExitCode);

        Assert.Equal(["please rebase"], heard.Select(c => c.Markdown));
        Assert.True(Only.Comments.Single().ByManager);
        Assert.False(Only.Comments.Single().BySession);
        Assert.Equal("manager", Json(Run(manager, "task", "show", id)).GetProperty("thread")[0].GetProperty("by").GetString());
    }

    [Fact]
    public void A_sessions_comment_from_elsewhere_still_wakes_no_one()
    {
        var manager = Started();
        var id = Prefix(Added(manager));
        var heard = new List<string>();
        using var _ = tasks.Commented.Subscribe(c => heard.Add(c.Comment.Markdown));

        Assert.Equal(ControlReply.Ok, RunFrom(manager, ManagerDir, @"C:\repos\other", "task", "comment", id, "from a session", "--from-claude").ExitCode);

        Assert.Empty(heard);
        Assert.Equal("you", Json(Run(manager, "task", "show", id)).GetProperty("thread")[0].GetProperty("by").GetString());
    }

    [Theory]
    [InlineData("close")]
    [InlineData("delete")]
    public void Manager_may_not_close_or_delete(string verb)
    {
        var manager = Started();
        var id = Prefix(Added(manager));
        var before = tasks.Board;

        AssertRefusedAndUnchanged(RunAsManager(manager, "task", verb, id), before);
        Assert.Equal(ManagerSession.Refusal, RunAsManager(manager, "task", verb, id).Output.Trim());
    }

    [Fact]
    public void Manager_may_not_close_and_stop_claude()
    {
        var manager = Started();
        var id = Prefix(Added(manager));
        var before = tasks.Board;

        AssertRefusedAndUnchanged(RunAsManager(manager, "task", "close", id, "--stop-claude"), before);
    }

    [Theory]
    [InlineData("done")]
    [InlineData("closed")]
    public void Manager_may_not_finish_a_task(string status)
    {
        var manager = Started();
        var id = Prefix(Added(manager));
        var before = tasks.Board;

        var reply = RunAsManager(manager, "task", "status", id, status);

        AssertRefusedAndUnchanged(reply, before);
        Assert.Equal(ManagerSession.Refusal, reply.Output.Trim());
    }

    // Final review, item 1: EditComment refuses only a session's comment, so the manager could rewrite the user's own
    // words, and the edit would count as the user's. It is refused before anything is looked up, and the board is the
    // very same object afterwards.
    [Fact]
    public void Manager_may_not_edit_the_users_comment()
    {
        var manager = Started();
        var id = Prefix(Added(manager));
        Assert.Equal(ControlReply.Ok, Run(manager, "task", "comment", id, "the user's words").ExitCode);
        var comment = Only.Comments.Single().Id.ToString();
        var before = tasks.Board;

        var reply = RunAsManager(manager, "task", "comment-edit", comment, "rewritten");

        AssertRefusedAndUnchanged(reply, before);
        Assert.Equal(ManagerSession.CommentRefusal, reply.Output.Trim());
        Assert.Equal("the user's words", Only.Comments.Single().Markdown);
    }

    // Settling a comment folds it away, which is the user's call: refused from the manager folder with the same answer,
    // with its --session (the one form the pipe has) and without.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Manager_may_not_settle_a_comment(bool withSession)
    {
        var manager = Started();
        var id = Prefix(Added(manager));
        Assert.Equal(ControlReply.Ok, Run(manager, "task", "comment", id, "the user's words").ExitCode);
        var comment = Only.Comments.Single().Id.ToString();
        var before = tasks.Board;
        string[] args = withSession ? ["task", "comment-settle", comment, "--session", "s1"] : ["task", "comment-settle", comment];

        var reply = RunAsManager(manager, args);

        AssertRefusedAndUnchanged(reply, before);
        Assert.Equal(ManagerSession.CommentRefusal, reply.Output.Trim());
        Assert.False(Only.Comments.Single().Settled.HasValue);
    }

    // Final review, item 4: the caller is the LAST --from, the one the client appends; one the caller typed before it is
    // dropped. The manager typing `--from C:/x` is still the manager, and is refused.
    [Fact]
    public void A_typed_from_does_not_hide_the_manager()
    {
        var manager = Started();
        var id = Prefix(Added(manager));
        var before = tasks.Board;

        var reply = RunAsManager(manager, "task", "close", id, "--from", @"C:\repos\other");

        AssertRefusedAndUnchanged(reply, before);
        Assert.Equal(ManagerSession.Refusal, reply.Output.Trim());
    }

    // The other way round: the user typing the manager's folder does not become the manager, and the typed pair is gone
    // from the words, so the command reads as usual.
    [Fact]
    public void A_typed_from_is_dropped_and_the_last_one_is_the_caller()
    {
        var manager = Started();
        var id = Prefix(Added(manager));

        Assert.Equal(ControlReply.Ok, RunFrom(manager, ManagerDir, @"C:\repos\other", "task", "comment", id, "hello", "--from", ManagerDir).ExitCode);

        var comment = Only.Comments.Single();
        Assert.Equal("hello", comment.Markdown);
        Assert.False(comment.ByManager);
    }

    [Fact]
    public void The_user_may_still_close_from_elsewhere()
    {
        var manager = Started();
        var id = Prefix(Added(manager));

        Assert.Equal(ControlReply.Ok, RunFrom(manager, ManagerDir, @"C:\repos\other", "task", "close", id).ExitCode);
        Assert.Equal(TaskStatus.Closed, Only.Status);
    }

    // A session can `cd` into a subfolder of its own working folder, so the caller folder arrives as that subfolder.
    [Fact]
    public void A_close_from_a_subfolder_of_the_manager_folder_is_refused()
    {
        var manager = Started();
        var id = Prefix(Added(manager));
        var before = tasks.Board;

        var reply = RunFrom(manager, ManagerDir, Path.Combine(ManagerDir, ".claude"), "task", "close", id);

        AssertRefusedAndUnchanged(reply, before);
        Assert.Equal(ManagerSession.Refusal, reply.Output.Trim());
    }

    [Fact]
    public void With_no_manager_folder_nobody_is_the_manager()
    {
        var manager = Started();
        var id = Prefix(Added(manager));

        Assert.Equal(ControlReply.Ok, RunFrom(manager, "", ManagerDir, "task", "close", id).ExitCode);
    }

    [Fact]
    public void WithCaller_marks_a_comment_run_inside_a_claude_session()
    {
        Assert.Equal(["task", "comment", "3f2a", "-", "--from-claude"], ControlArguments.WithCaller(["task", "comment", "3f2a", "-"], "1"));
        Assert.Equal(["task", "comment", "3f2a", "-"], ControlArguments.WithCaller(["task", "comment", "3f2a", "-"], null));
        Assert.Equal(["task", "rename", "3f2a", "x"], ControlArguments.WithCaller(["task", "rename", "3f2a", "x"], "1"));
        // A session's own form says who it is already.
        Assert.Equal(["task", "comment", "-", "--session", "s1"], ControlArguments.WithCaller(["task", "comment", "-", "--session", "s1"], "1"));
    }

    [Fact]
    public void Comment_edit_changes_his_comment_and_refuses_claudes()
    {
        var manager = Started();
        var id = Prefix(Added(manager));
        Run(manager, "task", "comment", id, "first draft");
        tasks.Comment(Only.Id, "s1", "Claude's finding");
        var thread = Json(Run(manager, "task", "list"))[0].GetProperty("thread");
        Assert.Equal(["you", "claude"], thread.EnumerateArray().Select(c => c.GetProperty("by").GetString()));
        var (hers, his) = (thread[1].GetProperty("id").GetString()!, thread[0].GetProperty("id").GetString()!);

        var edited = Run(manager, "task", "comment-edit", his, "second draft");
        Assert.Equal(ControlReply.Ok, edited.ExitCode);
        Assert.Equal("second draft", Only.Comments[0].Markdown);

        var before = tasks.Board;
        AssertRefusedAndUnchanged(Run(manager, "task", "comment-edit", hers, "rewritten"), before);
        Assert.Equal("Only your own comments can be edited", Run(manager, "task", "comment-edit", hers, "x").Output.Trim());
    }

    [Fact]
    public void Describe_and_comment_edit_read_their_text_from_stdin()
    {
        Assert.Equal(["task", "describe", "3f2a", "a & b\nline two"],
            ControlArguments.WithInput(["task", "describe", "3f2a", "-"], () => "a & b\nline two\n"));
        Assert.Equal(["task", "comment-edit", "3f2a", "it's \"quoted\""],
            ControlArguments.WithInput(["task", "comment-edit", "3f2a", "-"], () => "it's \"quoted\"\r\n"));
        Assert.Equal("task describe 3f2a (5 characters)", ControlArguments.ForTrace(["task", "describe", "3f2a", "hello"]));
        Assert.Equal("task comment-edit 3f2a (5 characters)", ControlArguments.ForTrace(["task", "comment-edit", "3f2a", "hello"]));
    }

    // `task rename <id> -` once set the title to a literal "-": rename was missing from the verbs that read stdin. The
    // goals' two title forms had the same hole, so they are covered here too.
    [Fact]
    public void Rename_and_goal_titles_read_their_text_from_stdin()
    {
        Assert.Equal(["task", "rename", "3f2a", "a & \"b\""],
            ControlArguments.WithInput(["task", "rename", "3f2a", "-"], () => "a & \"b\"\n"));
        Assert.Equal(["goal", "rename", "9c1d", "ship <it>"],
            ControlArguments.WithInput(["goal", "rename", "9c1d", "-"], () => "ship <it>\r\n"));
        Assert.Equal(["goal", "add", "plan | trip", "--week", "2026-12-07"],
            ControlArguments.WithInput(["goal", "add", "-", "--week", "2026-12-07"], () => "plan | trip\n"));
        // The id stays readable in the trace; the title is written as its length.
        Assert.Equal("task rename 3f2a (5 characters)", ControlArguments.ForTrace(["task", "rename", "3f2a", "hello"]));
        Assert.Equal("goal rename 9c1d (5 characters)", ControlArguments.ForTrace(["goal", "rename", "9c1d", "hello"]));
        Assert.Equal("goal add (5 characters) --week 2026-12-07", ControlArguments.ForTrace(["goal", "add", "hello", "--week", "2026-12-07"]));
        // A verb that takes no text never reads stdin, so a terminal never sits waiting on the keyboard.
        Assert.Equal(["goal", "done", "-"], ControlArguments.WithInput(["goal", "done", "-"], () => throw new InvalidOperationException("read")));
    }

    [Fact]
    public void Renaming_a_task_from_stdin_keeps_the_text_as_given()
    {
        var manager = Started();
        Run(manager, "task", "add", "first", "--workspace", "none");
        var id = Only.Id.ToString();

        var renamed = Run(manager, [.. ControlArguments.WithInput(["task", "rename", id, "-"], () => "it's \"quoted\" & done\n")]);

        Assert.Equal(ControlReply.Ok, renamed.ExitCode);
        Assert.Equal("it's \"quoted\" & done", Only.Title);
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

        // Task eefb986a: Done is pinned under the questions, above the quadrants, and has no order of its own there.
        Assert.Equal(["Claude's one", "Waiting one", "Done one", "Backlog one"], entries.Select(e => e.GetProperty("title").GetString()));
        Assert.Equal(["waiting", "waiting", "done", "backlog"], entries.Select(e => e.GetProperty("status").GetString()));
        Assert.Equal(JsonValueKind.Null, entries[2].GetProperty("order").ValueKind);
        Assert.Equal(1, entries[3].GetProperty("order").GetInt32());
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

    // Final review, item 4: a `task` call ALWAYS gets the client's folder last, even when the caller typed a --from, so
    // the server's caller (the last pair) is the folder the shim really ran in. Other verbs keep a --from they were given.
    [Fact]
    public void The_client_adds_its_folder_to_task_even_after_a_typed_from()
    {
        Assert.Equal(["task", "close", "3f2a", "--from", @"C:\x", "--from", @"C:\state\manager"],
            ControlArguments.WithCallerFolder(["task", "close", "3f2a", "--from", @"C:\x"], @"C:\state\manager"));
        Assert.Equal(["claude-hook", "--from", @"C:\given"], ControlArguments.WithCallerFolder(["claude-hook", "--from", @"C:\given"], @"C:\work"));
    }

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

    // Task 6da8d7eb: the snooze button's twin. `task snooze` hides a task until then and `list` says when it comes back;
    // `task wake` brings it back; a length the pipe cannot read changes nothing.
    [Fact]
    public void Snooze_and_wake_hide_a_task_and_bring_it_back()
    {
        var manager = Started();
        var id = Prefix(Added(manager));

        var snoozed = Run(manager, "task", "snooze", id, "2h");
        Assert.Equal(ControlReply.Ok, snoozed.ExitCode);
        Assert.Equal(clock.AddHours(2), Only.SnoozedUntil.Value);
        Assert.Equal(clock.AddHours(2), Json(snoozed).GetProperty("snoozedUntil").GetDateTimeOffset());

        var before = tasks.Board;
        AssertRefusedAndUnchanged(Run(manager, "task", "snooze", id, "a while"), before);

        var woken = Run(manager, "task", "wake", id);
        Assert.Equal(ControlReply.Ok, woken.ExitCode);
        Assert.False(Only.IsSnoozed);
        Assert.Equal(JsonValueKind.Null, Json(woken).GetProperty("snoozedUntil").ValueKind);
    }

    // Task 3dd07539: the Reevaluate button's twin. `task reevaluate` on a task a session worked on announces it for the
    // session (Reevaluations, which the app's CommentWaker delivers) and writes Deskbar's line in the thread; a note no
    // session ever had has no one to ask and is refused with the board unchanged, and so is a closed task.
    [Fact]
    public void Reevaluate_asks_the_tasks_session_and_refuses_without_one()
    {
        var manager = Started();
        var note = Prefix(Added(manager, "Just a note"));
        var claudes = tasks.Create("Claude's one", WorkspaceId(manager, "Deskbar"),
            new TaskSession("s1", @"C:\repos\TaskSpaces", clock, Maybe<DateTimeOffset>.None)).Value.Id;
        var heard = new List<Guid>();
        using var _ = tasks.Reevaluations.Subscribe(heard.Add);

        Assert.Equal(ControlReply.Ok, Run(manager, "task", "reevaluate", claudes.ToString()[..6]).ExitCode);
        Assert.Equal([claudes], heard);
        var thread = tasks.Board.Tasks.Single(t => t.Id == claudes).Comments;
        Assert.Equal(TaskService.ReevaluateNote, Assert.Single(thread).Markdown);
        Assert.True(thread[0].ByDeskbar);

        var before = tasks.Board;
        AssertRefusedAndUnchanged(Run(manager, "task", "reevaluate", note), before);
        // Done first: closing a task Claude is still working on is refused (TaskBoard.ClaudeStillWorking).
        Assert.True(tasks.SetStatus(claudes, TaskStatus.Done).IsSuccess);
        Assert.True(tasks.Close(claudes).IsSuccess);
        before = tasks.Board;
        AssertRefusedAndUnchanged(Run(manager, "task", "reevaluate", claudes.ToString()[..6]), before);
        Assert.Equal([claudes], heard);
    }

    // The manager session's cheap look (TaskOverview): the reply is text, not JSON, one line per open task.
    [Fact]
    public void Overview_prints_the_text()
    {
        var manager = Started();
        var id = Prefix(Added(manager, "Overview me"));

        var reply = Run(manager, "task", "overview");

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.StartsWith(id, reply.Output);
        Assert.Contains("Deskbar: Overview me", reply.Output);
    }

    // `show` answers what `list` prints for the one task, thread text included.
    [Fact]
    public void Show_prints_one_task_as_list_describes_it()
    {
        var manager = Started();
        var id = Prefix(Added(manager, "Show me"));
        Run(manager, "task", "comment", id, "a word in the thread");

        var listed = Json(Run(manager, "task", "list")).EnumerateArray().Single();
        var shown = Json(Run(manager, "task", "show", id));

        Assert.Equal(listed.GetProperty("id").GetString(), shown.GetProperty("id").GetString());
        Assert.Equal(listed.GetProperty("title").GetString(), shown.GetProperty("title").GetString());
        Assert.Equal("a word in the thread", shown.GetProperty("thread")[0].GetProperty("markdown").GetString());
    }

    [Fact]
    public void Show_unknown_id_fails()
    {
        var manager = Started();
        var before = tasks.Board;

        AssertRefusedAndUnchanged(Run(manager, "task", "show", "ffffffff"), before);
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
    public void Order_refuses_a_bad_place()
    {
        var manager = Started();
        var id = Prefix(Added(manager));
        var before = tasks.Board;

        AssertRefusedAndUnchanged(Run(manager, "task", "order", id, "sideways"), before);
        AssertRefusedAndUnchanged(Run(manager, "task", "order", id, "0"), before);
        AssertRefusedAndUnchanged(Run(manager, "task", "order", "ffffff", "top"), before);
    }

    // Task f41d7857: a task waiting on Petre is ordered within the Waiting on you block, which stays on top.
    [Fact]
    public void Order_moves_a_waiting_task_within_the_waiting_block()
    {
        var manager = Started();
        var first = Prefix(Added(manager, "first"));
        var second = Prefix(Added(manager, "second"));
        Run(manager, "task", "status", first, "waiting");
        Run(manager, "task", "status", second, "waiting");
        Assert.Equal(["second", "first"], tasks.Board.PinnedPart(Deskbar.Core.Tasks.TaskStatus.WaitingOnYou).Select(t => t.Title));

        Assert.Equal(ControlReply.Ok, Run(manager, "task", "order", second, "end").ExitCode);

        Assert.Equal(["first", "second"], tasks.Board.PinnedPart(Deskbar.Core.Tasks.TaskStatus.WaitingOnYou).Select(t => t.Title));
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
            ["list [--closed]",
             // Task 521d2b07: the Stats view's numbers.
             "stats [--days <n>]",
             "overview", "show <id>",
             "add <title>|- [--workspace <name>|none] [--important] [--urgent] [--parent <id>] [--session <session>]", "close <id> [--stop-claude]", "reopen <id>",
             "status <id> <backlog|in-progress|waiting|done>", "rename <id> <title>|-", "move <id> <workspace>|none",
             "comment <id> <markdown>|-", "delete <id>", "flag <id> important|urgent on|off", "order <id> top|end|<n>",
             // Task 9: going to a task, starting Claude on it and opening one of its sessions.
             // Task dca4ebeb: `open` is the menus' "Open in window", between go and start.
             "go <id>", "open <id>",
             // Task 5016d533: "Copy link"'s twin.
             "link <id>", "start <id> [--folder <path>] [--model <model>]", "open-session <id> <session>",
             // The manager session: the bar's Manager button's twin.
             "manager",
             // A Claude session's own forms (plan Task 8, RemoteControl.ClaudeHook.cs); `-` reads the text from stdin.
             "join <id> --session <session>", "new <title>|- --session <session>", "status-line <text>|- --session <session>",
             "ask <question>|- --session <session>", "answered --session <session>",
             "ask <question>|-   (the manager session, from its folder)", "answered   (the manager session, from its folder)",
             "done --session <session>", "comment <markdown>|- --session <session>",
             "later <title>|- --session <session>",
             // Subtasks: a session splits its work and reads its family.
             "subtask <title>|- [--no-start] [--model <model>] --session <session>",
             // Task e7aa49b1: a pull request the session opened another way than gh pr create.
             "pr <url> --session <session>",
             // Task 8470d176, phase 2: a session reports the deploy it checked.
             "deployed <note>|- --session <session>",
             "family --session <session>",
             // Task links: a session links its own task, or closes it as a duplicate.
             "duplicate-of <id> --session <session>", "relate <id> --session <session>", "unrelate <id> --session <session>",
             "comment-settle <comment-id> --session <session>",
             "assign <id> me|claude", "describe <id> <markdown>|-", "comment-edit <comment-id> <markdown>|-",
             // Subtasks: put a task under another, and read a task's family.
             "parent <id> <parent-id>|none", "family <id>",
             // Task links: the panel's "Duplicate of..." and "Related to..." twins.
             "duplicate <id> <of-id>", "relate <id> <other-id>", "unrelate <id> <other-id>",
             "goal <id> <goal-id>|none",
             // Task 6da8d7eb: the snooze button's twins.
             "snooze <id> 1h|3h|tomorrow|monday|<n>m|<n>h|<n>d", "wake <id>",
             "repeat <id> <schedule>|off [--model <model>] [--folder <path>]",
             // Task 3dd07539: the Reevaluate button's twin.
             "reevaluate <id>",
             // Task e7aa49b1: a pull request on a task by hand.
             "pr <id> <url>",
             // Task 8470d176, phase 2: "Mark deployed"'s twin.
             "deployed <id> <note>|-"],
            ControlUsage.All.Where(c => c.Verb == "task").Select(c => c.Arguments));

    // --- Task 9: go, start, open-session ---------------------------------------------------------------------------

    // What the three verbs asked the App to do, in order, as "verb:title[:argument]".
    sealed class FakeSessionCommands
    {
        public List<string> Calls { get; } = [];

        public IReadOnlyList<string> Folders { get; set; } = [];

        // `task close --stop-claude` (task 37cd7081): the app closes past the refusal and stops the sessions; the fake
        // closes the same way and records the stop.
        public TaskService? Tasks { get; init; }

        public TaskSessionCommands Commands => new(
            Go: t => Done($"go:{t.Title}"),
            Folders: _ => Folders,
            Start: (t, folder, model) => Done($"start:{t.Title}:{folder}{model.Match(m => ":" + m, () => "")}"),
            OpenSession: (t, session) => Done($"open:{t.Title}:{session}"))
        {
            StopAndClose = t => (Tasks?.Close(t.Id, stoppingClaude: true) ?? Result.Success()).Bind(() => Done($"stop-and-close:{t.Title}")),
        };

        Result Done(string call)
        {
            Calls.Add(call);
            return Result.Success();
        }
    }

    ControlReply RunWith(WorkspaceManager manager, FakeSessionCommands commands, params string[] args) =>
        new RemoteControl(manager, tasks: tasks, goals: goals, taskSessions: commands.Commands).Execute(args);

    // Task 37cd7081: a plain `task close` on a task Claude is still working on is refused, leaves it open and names the
    // flag; `--stop-claude` closes it through the app, which stops its sessions.
    [Fact]
    public void Task_close_refuses_while_claude_works_and_stop_claude_closes()
    {
        var manager = Started();
        var commands = new FakeSessionCommands { Tasks = tasks };
        var id = tasks.Create("Fix the bar", Maybe<Guid>.None,
            new TaskSession("884c127f-9633-40eb-bed7-593800bca40f", @"C:\repos\Deskbar", clock, Maybe<DateTimeOffset>.None)).Value.Id;
        var prefix = id.ToString()[..6];

        var before = tasks.Board;
        var refused = RunWith(manager, commands, "task", "close", prefix);
        AssertRefusedAndUnchanged(refused, before);
        Assert.Equal($"{TaskViews.ClaudeStillWorkingRefusal} (task close {prefix} --stop-claude)", refused.Output.Trim());

        var closed = RunWith(manager, commands, "task", "close", prefix, "--stop-claude");
        Ok(closed);
        Assert.Equal("closed", Json(closed).GetProperty("status").GetString());
        Assert.Equal(["stop-and-close:Fix the bar"], commands.Calls);
    }

    // The manager session design ("The button"): `task manager` is the bar button's twin, so both go through the App's
    // one Manager command. It answers at once, the window coming later; a refusal (the feature off, say) is the reply;
    // a copy without the command refuses.
    [Fact]
    public void Manager_verb_calls_the_manager_command()
    {
        var manager = Started();
        var calls = 0;
        var answer = Result.Success();
        var commands = new FakeSessionCommands().Commands with
        {
            Manager = () =>
            {
                calls++;
                return answer;
            },
        };
        ControlReply RunManager() => new RemoteControl(manager, tasks: tasks, goals: goals, taskSessions: commands).Execute(["task", "manager"]);

        var opened = RunManager();
        Ok(opened);
        Assert.Equal(1, calls);
        Assert.Equal(true, Json(opened).GetProperty("opening").GetBoolean());

        answer = Result.Failure("Claude sessions are off.");
        var refused = RunManager();
        Assert.Equal(ControlReply.Failed, refused.ExitCode);
        Assert.Equal("Claude sessions are off.", refused.Output.Trim());
        Assert.Equal(2, calls);

        // A copy whose App never handed a Manager command in, and a copy with no session commands at all.
        Assert.Equal(ControlReply.Failed, RunWith(manager, new FakeSessionCommands(), "task", "manager").ExitCode);
        Assert.Contains("The manager is not available", RunWith(manager, new FakeSessionCommands(), "task", "manager").Output);
        Assert.Equal(ControlReply.Failed, Run(manager, "task", "manager").ExitCode);
    }

    // The three pipe twins of the double-click, "Start Claude on this" and the thread's Open (spec, "Where the clicks
    // are"): each is in ControlUsage, each reaches the App's command with the task found by its id prefix, and each
    // answers with the task's JSON. `start` with exactly one folder starts there; with several, it refuses and names
    // them, since a script cannot answer a pick list; --folder chooses. `open-session` takes the session's full id or
    // its short id, and a session that is not the task's is refused.
    [Fact]
    public void Task_go_start_and_open_session_are_in_ControlUsage_and_dispatch()
    {
        Assert.Contains(ControlUsage.All, c => c.Verb == "task" && c.Arguments == "go <id>");
        Assert.Contains(ControlUsage.All, c => c.Verb == "task" && c.Arguments == "start <id> [--folder <path>] [--model <model>]");
        Assert.Contains(ControlUsage.All, c => c.Verb == "task" && c.Arguments == "open-session <id> <session>");

        var manager = Started();
        var commands = new FakeSessionCommands { Folders = [@"C:\repos\Deskbar"] };
        var id = tasks.Create("Fix the bar", Maybe<Guid>.None,
            new TaskSession("884c127f-9633-40eb-bed7-593800bca40f", @"C:\repos\Deskbar", clock, Maybe<DateTimeOffset>.None)).Value.Id;
        var prefix = id.ToString()[..6];

        var went = RunWith(manager, commands, "task", "go", prefix);
        Ok(went);
        Assert.Equal(id.ToString(), Json(went).GetProperty("id").GetString());

        Ok(RunWith(manager, commands, "task", "start", prefix));
        Ok(RunWith(manager, commands, "task", "start", prefix, "--folder", @"D:\other"));
        Ok(RunWith(manager, commands, "task", "open-session", prefix, "884c127f"));
        Ok(RunWith(manager, commands, "task", "open-session", prefix, "884c127f-9633-40eb-bed7-593800bca40f"));

        Assert.Equal(
            ["go:Fix the bar", @"start:Fix the bar:C:\repos\Deskbar", @"start:Fix the bar:D:\other",
             "open:Fix the bar:884c127f-9633-40eb-bed7-593800bca40f", "open:Fix the bar:884c127f-9633-40eb-bed7-593800bca40f"],
            commands.Calls);

        // Task e52e33cb: --model names the model of that one session, with or without a folder, in either order;
        // a name that is not one is refused and starts nothing.
        commands.Calls.Clear();
        Ok(RunWith(manager, commands, "task", "start", prefix, "--model", "sonnet"));
        Ok(RunWith(manager, commands, "task", "start", prefix, "--folder", @"D:\other", "--model", "fable"));
        Ok(RunWith(manager, commands, "task", "start", prefix, "--model", "fable", "--folder", @"D:\other"));
        Assert.Equal(ControlReply.Failed, RunWith(manager, commands, "task", "start", prefix, "--model", "--resume").ExitCode);
        Assert.Equal(
            [@"start:Fix the bar:C:\repos\Deskbar:sonnet", @"start:Fix the bar:D:\other:fable", @"start:Fix the bar:D:\other:fable"],
            commands.Calls);

        // Several folders: refused, naming them, nothing started.
        commands.Folders = [@"C:\repos\Deskbar", @"E:\work\iam.ge"];
        var several = RunWith(manager, commands, "task", "start", prefix);
        Assert.Equal(ControlReply.Failed, several.ExitCode);
        Assert.Contains(@"C:\repos\Deskbar", several.Output);
        Assert.Contains(@"E:\work\iam.ge", several.Output);
        Assert.Contains("--folder", several.Output);

        // None known: refused, asking for --folder.
        commands.Folders = [];
        Assert.Contains("--folder", RunWith(manager, commands, "task", "start", prefix).Output);

        // A session that is not the task's.
        var stranger = RunWith(manager, commands, "task", "open-session", prefix, "deadbeef");
        Assert.Equal(ControlReply.Failed, stranger.ExitCode);
        // The three starts with a model, since the list was cleared before them; none of the refusals added a call.
        Assert.Equal(3, commands.Calls.Count);

        // A copy without the commands refuses every one of the three.
        Assert.Equal(ControlReply.Failed, Run(manager, "task", "go", prefix).ExitCode);
    }

    // Final review, item 12: `task start` on a closed task is refused up front, with or without --folder, and nothing
    // reaches the App.
    [Fact]
    public void Task_start_refuses_a_closed_task()
    {
        var manager = Started();
        var commands = new FakeSessionCommands { Folders = [@"C:\repos\Deskbar"] };
        var id = tasks.AddNote("Old errand", Maybe<Guid>.None).Value.Id;
        Assert.True(tasks.Close(id).IsSuccess);
        var prefix = id.ToString()[..6];

        var refused = RunWith(manager, commands, "task", "start", prefix);
        var refusedWithFolder = RunWith(manager, commands, "task", "start", prefix, "--folder", @"D:\other");

        Assert.Equal(ControlReply.Failed, refused.ExitCode);
        Assert.Contains(RemoteControl.ClosedTaskRefusal, refused.Output);
        Assert.Equal(ControlReply.Failed, refusedWithFolder.ExitCode);
        Assert.Empty(commands.Calls);
    }

    // Task dca4ebeb: `task open <id>` hands the task the id names (any unique start of it) to the app's task window, and
    // answers with the task; an unknown id refuses before anything opens, and a copy with no task windows refuses.
    [Fact]
    public void Task_open_hands_the_task_to_the_window_and_refuses_without_one()
    {
        var manager = Started();
        var id = tasks.AddNote("A long brief", Maybe<Guid>.None).Value.Id;
        var opened = new List<Guid>();
        var control = new RemoteControl(manager, tasks: tasks, openTaskWindow: task =>
        {
            opened.Add(task.Id);
            return Result.Success();
        });

        var reply = control.Execute(["task", "open", id.ToString()[..6]]);
        Ok(reply);
        Assert.Equal(id.ToString(), Json(reply).GetProperty("id").GetString());
        Assert.Equal([id], opened);

        Assert.Equal(ControlReply.Failed, control.Execute(["task", "open", "ffffffff"]).ExitCode);
        Assert.Single(opened);

        var without = Run(manager, "task", "open", id.ToString());
        Assert.Equal(ControlReply.Failed, without.ExitCode);
        Assert.Contains(RemoteControl.TaskWindowUnavailable, without.Output);
    }

    // Task 5016d533: `task link <id>` answers with the markdown link a session puts in a pull request, any unique start
    // of the id finding the task; an unknown id refuses.
    [Fact]
    public void Task_link_answers_with_the_markdown_link_to_the_task()
    {
        var manager = Started();
        var id = tasks.AddNote("Linked from a pull request", Maybe<Guid>.None).Value.Id;
        var control = new RemoteControl(manager, tasks: tasks);

        var reply = control.Execute(["task", "link", id.ToString()[..8]]);
        Ok(reply);
        Assert.Equal($"[Deskbar task {id.ToString()[..8]}](https://deskbar.app/task#{id})", reply.Output);

        Assert.Equal(ControlReply.Failed, control.Execute(["task", "link", "ffffffff"]).ExitCode);
    }

    // `task stats` (task 521d2b07): the Stats view's numbers as JSON, each day's tasks with their ids so a caller can
    // follow them; --days narrows the days, and a wrong count is refused.
    [Fact]
    public void Task_stats_prints_the_burn_up_with_each_task()
    {
        var manager = Started();
        var id = Guid.Parse(Json(Run(manager, "task", "add", "Soap", "--workspace", "Deskbar")).GetProperty("id").GetString()!);
        clock = clock.AddHours(2);
        Ok(Run(manager, "task", "close", id.ToString()));

        var reply = Run(manager, "task", "stats", "--days", "3");
        Ok(reply);
        var json = Json(reply);
        Assert.Equal(3, json.GetProperty("days").GetArrayLength());
        Assert.Equal(1, json.GetProperty("finishedLast7Days").GetInt32());
        Assert.Equal(0, json.GetProperty("openNow").GetInt32());
        Assert.Equal(2.0, json.GetProperty("medianHoursToFinish").GetDouble());
        Assert.False(json.GetProperty("estimated").GetBoolean());
        var finished = json.GetProperty("days").EnumerateArray().SelectMany(d => d.GetProperty("finished").EnumerateArray()).Single();
        Assert.Equal(id.ToString(), finished.GetProperty("id").GetString());
        Assert.Equal("closed", finished.GetProperty("status").GetString());

        Assert.Equal(14, Json(Run(manager, "task", "stats")).GetProperty("days").GetArrayLength());
        Assert.Equal(ControlReply.Failed, Run(manager, "task", "stats", "--days", "0").ExitCode);
        Assert.Equal(ControlReply.Failed, Run(manager, "task", "stats", "--days", "many").ExitCode);
    }

    // --- task repeat (recurring tasks, docs/superpowers/specs/2026-11-26-recurring-tasks-design.md, task 0b78269e) ---
    //
    // `task repeat <id> <schedule>|off [--model <m>] [--folder <p>]`. The folder must exist (FolderExists), the model is a
    // name `claude` cannot misread, and a refusal changes nothing, so the board is compared by reference.

    const string RepoDir = @"C:\repos\TaskSpaces";

    // A control with the app's folder lookups stood in: the candidates the task offers and the folders that exist.
    ControlReply RepeatRun(WorkspaceManager manager, string[] candidates, Func<string, bool> exists, params string[] args) =>
        new RemoteControl(manager, tasks: tasks, goals: goals, taskSessions: new TaskSessionCommands(_ => Result.Success(), _ => candidates, (_, _, _) => Result.Success(), (_, _) => Result.Success())
        {
            ManagerFolder = ManagerDir,
            FolderExists = exists,
        }).Execute([.. args, "--from", RepoDir]);

    ControlReply Repeated(WorkspaceManager manager, params string[] args) => RepeatRun(manager, [], _ => true, args);

    [Fact]
    public void Repeat_sets_the_schedule_the_default_model_and_the_folder()
    {
        var manager = Started();
        var id = Prefix(Added(manager));

        var reply = Repeated(manager, "task", "repeat", id, "every", "1h", "09:00-19:00", "--folder", RepoDir);

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        var repeat = Json(reply).GetProperty("repeat");
        Assert.Equal("every 1h 09:00-19:00", repeat.GetProperty("schedule").GetString());
        Assert.Equal("sonnet", repeat.GetProperty("model").GetString());
        Assert.Equal(RepoDir, repeat.GetProperty("folder").GetString());
        Assert.NotEqual(JsonValueKind.Null, Json(reply).GetProperty("nextRun").ValueKind);
        Assert.True(Only.Repeat.HasValue);
    }

    [Fact]
    public void Repeat_takes_a_model_and_stores_a_worktree_as_its_repo()
    {
        var manager = Started();
        var id = Prefix(Added(manager));

        var reply = Repeated(manager, "task", "repeat", id, "daily", "09:00", "--model", "haiku", "--folder", RepoDir + @"\.claude\worktrees\x");

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Equal("haiku", Json(reply).GetProperty("repeat").GetProperty("model").GetString());
        Assert.Equal(RepoDir, Json(reply).GetProperty("repeat").GetProperty("folder").GetString());
    }

    [Fact]
    public void Repeat_without_a_folder_uses_the_only_candidate_and_refuses_several()
    {
        var manager = Started();
        var id = Prefix(Added(manager));

        var several = RepeatRun(manager, [RepoDir, @"C:\repos\Other"], _ => true, "task", "repeat", id, "daily", "09:00");
        Assert.Equal(ControlReply.Failed, several.ExitCode);
        Assert.Contains("Several folders", several.Output);
        Assert.True(Only.Repeat.HasNoValue);

        var one = RepeatRun(manager, [RepoDir], _ => true, "task", "repeat", id, "daily", "09:00");
        Assert.Equal(ControlReply.Ok, one.ExitCode);
        Assert.Equal(RepoDir, Json(one).GetProperty("repeat").GetProperty("folder").GetString());
    }

    [Theory]
    [InlineData("every", "5m", "--folder", RepoDir)]
    [InlineData("every", "1h", "--model", "--bad", "--folder", RepoDir)]
    [InlineData("every", "1h", "--folder", @"C:\gone")]
    [InlineData("never")]
    public void Repeat_refuses_a_bad_schedule_a_bad_model_and_a_missing_folder_and_changes_nothing(params string[] words)
    {
        var manager = Started();
        var id = Prefix(Added(manager));
        var before = tasks.Board;

        var reply = RepeatRun(manager, [], folder => folder == RepoDir, ["task", "repeat", id, .. words]);

        AssertRefusedAndUnchanged(reply, before);
    }

    [Fact]
    public void Repeat_off_ends_it()
    {
        var manager = Started();
        var id = Prefix(Added(manager));
        Repeated(manager, "task", "repeat", id, "daily", "09:00", "--folder", RepoDir);
        Assert.True(Only.Repeat.HasValue);

        var reply = Repeated(manager, "task", "repeat", id, "off");

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Equal(JsonValueKind.Null, Json(reply).GetProperty("repeat").ValueKind);
        Assert.True(Only.Repeat.HasNoValue);
    }

    [Fact]
    public void List_shows_repeat_and_next_run_and_null_without_them()
    {
        var manager = Started();
        Added(manager, "Plain");
        var recurring = Prefix(Added(manager, "Recurring"));
        Repeated(manager, "task", "repeat", recurring, "daily", "09:00", "--folder", RepoDir);

        var list = Json(Run(manager, "task", "list")).EnumerateArray().ToArray();

        var withRepeat = list.Single(t => t.GetProperty("title").GetString() == "Recurring");
        Assert.Equal("daily 09:00", withRepeat.GetProperty("repeat").GetProperty("schedule").GetString());
        Assert.Equal("every day at 09:00", withRepeat.GetProperty("repeat").GetProperty("label").GetString());
        Assert.NotEqual(JsonValueKind.Null, withRepeat.GetProperty("nextRun").ValueKind);
        var without = list.Single(t => t.GetProperty("title").GetString() == "Plain");
        Assert.Equal(JsonValueKind.Null, without.GetProperty("repeat").ValueKind);
        Assert.Equal(JsonValueKind.Null, without.GetProperty("nextRun").ValueKind);
    }

    [Fact]
    public void Manager_may_set_a_repeat_by_id()
    {
        var manager = Started();
        var id = Prefix(Added(manager));

        var reply = RunAsManager(manager, "task", "repeat", id, "daily", "09:00", "--folder", RepoDir);

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.True(Only.Repeat.HasValue);
    }

    [Fact]
    public void Overview_names_the_repeat()
    {
        var manager = Started();
        var id = Prefix(Added(manager));
        Repeated(manager, "task", "repeat", id, "daily", "09:00", "--folder", RepoDir);
        // Between runs the task is snoozed and the overview counts it; a run in progress is a line, naming the repeat.
        Assert.Equal(ControlReply.Ok, Run(manager, "task", "status", id, "in-progress").ExitCode);
        Assert.True(Only.Repeat.HasValue);

        Assert.Contains("repeats every day at 09:00", Run(manager, "task", "overview").Output);
    }

    [Fact]
    public void Usage_lists_task_repeat() =>
        Assert.Contains(ControlUsage.All, c => c.Verb == "task" && c.Arguments.StartsWith("repeat <id>"));
}
