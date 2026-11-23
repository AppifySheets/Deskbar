using CSharpFunctionalExtensions;
using Deskbar.Core.Control;
using Deskbar.Core.Domain;
using Deskbar.Core.Tasks;
// System.Threading.Tasks (an implicit using) has a TaskStatus too; the alias says which one this file means.
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests;

// `claude-hook`: the one verb Claude Code's hooks run (claude-hook.cmd, ClaudeSkill in the app). The hook's JSON comes
// in as `--json`, the session's folder as `--from`, and how the session was started as `--entry` and `--attended`.
// The reply's output is what the session reads, so for the events that only ask for the user it is empty. Design:
// docs/superpowers/specs/2026-11-22-tasks-board-design.md; plan Task 7.
public class RemoteControlClaudeHookTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();

    WorkspaceManager Started()
    {
        var manager = new WorkspaceManager(desktops, monitor, titles, store, ownProcessId: 4242);
        Assert.True(manager.Start().IsSuccess);
        manager.AddWorkspace("Deskbar");
        manager.AddWorkspace("iam.ge");
        return manager;
    }

    static ControlReply Run(WorkspaceManager manager, params string[] args) => new RemoteControl(manager).Execute(args);

    Guid DesktopOf(WorkspaceManager manager, string workspace) => manager.State.Workspaces.Single(w => w.Name == workspace).DesktopId!.Value;

    void Editor(WorkspaceManager manager, nint hwnd, string folder, string workspace)
    {
        var window = new WindowInfo(new WindowHandle(hwnd), 100, "Code", @"C:\VS Code\Code.exe", $"main.cs - {folder} - Visual Studio Code", null);
        desktops.WindowPlacements[window.Handle] = DesktopOf(manager, workspace);
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, window));
    }

    void StandOn(WorkspaceManager manager, string workspace)
    {
        desktops.CurrentDesktopId = DesktopOf(manager, workspace);
        desktops.CurrentChangedSubject.OnNext(desktops.CurrentDesktopId);
    }

    bool Blinks(WorkspaceManager manager, string workspace) =>
        manager.WindowsByWorkspace().Value.Workspaces.Single(g => g.Workspace.Name == workspace).WantsAttention;

    static string Hook(string hookEvent) =>
        $$"""{"session_id":"92943450","transcript_path":"x","cwd":"C:\\repos\\TaskSpaces","hook_event_name":"{{hookEvent}}"}""";

    static string[] HookArgs(string hookEvent) =>
        ["claude-hook", "--entry", "claude-vscode", "--attended", "1", "--from", @"C:\repos\TaskSpaces", "--json", Hook(hookEvent)];

    // Notification (a permission prompt) and Stop (a reply finished) still ask for the user exactly as `attention` did
    // when the script ran it: the session's workspace blinks. Nothing comes back for the session to read.
    [Theory]
    [InlineData("Notification")]
    [InlineData("Stop")]
    public void Notification_and_stop_still_ask(string hookEvent)
    {
        var manager = Started();
        Editor(manager, 0x10, "TaskSpaces", "Deskbar");
        StandOn(manager, "iam.ge");

        var reply = Run(manager, HookArgs(hookEvent));

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Equal("", reply.Output);
        Assert.True(Blinks(manager, "Deskbar"));
    }

    // Petre: "deskbar workspace was blinking but the app in it was not, why not?" Five VS Code windows showed the
    // TaskSpaces folder, so the folder named none of them. The session's name, read from the transcript the hook
    // points at, picks its window: that one glows, and the others do not.
    [Theory]
    [InlineData("Notification")]
    [InlineData("Stop")]
    public void The_session_name_picks_the_asking_window_out_of_several_on_one_folder(string hookEvent)
    {
        var manager = Started();
        Titled(manager, 0x10, "Deskbar: tasks board, wi… - TaskSpaces - Visual Studio Code", "Deskbar");
        Titled(manager, 0x11, "Deskbar: fix flaky tests - TaskSpaces - Visual Studio Code", "Deskbar");
        StandOn(manager, "iam.ge");
        var transcript = Path.Combine(Path.GetTempPath(), $"deskbar-transcript-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllText(transcript, """{"type":"custom-title","customTitle":"Deskbar: fix flaky tests","sessionId":"s1"}""" + "\n");
            var json = $$"""{"session_id":"s1","transcript_path":{{System.Text.Json.JsonSerializer.Serialize(transcript)}},"cwd":"C:\\repos\\TaskSpaces","hook_event_name":"{{hookEvent}}"}""";

            Run(manager, "claude-hook", "--entry", "claude-vscode", "--attended", "1", "--from", @"C:\repos\TaskSpaces", "--json", json);

            var running = manager.WindowsByWorkspace().Value.Workspaces.Single(g => g.Workspace.Name == "Deskbar").Running;
            Assert.True(Blinks(manager, "Deskbar"));
            Assert.True(running.Single(r => r.Window.Handle == new WindowHandle(0x11)).Asks);
            Assert.False(running.Single(r => r.Window.Handle == new WindowHandle(0x10)).Asks);
        }
        finally
        {
            File.Delete(transcript);
        }
    }

    // Without a name that narrows them (here the transcript path leads nowhere), several windows on one folder still
    // blink the workspace and light no window, as before: a guess would light the wrong one.
    [Fact]
    public void Without_a_name_several_windows_on_one_folder_glow_none()
    {
        var manager = Started();
        Titled(manager, 0x10, "Deskbar: tasks board, wi… - TaskSpaces - Visual Studio Code", "Deskbar");
        Titled(manager, 0x11, "Deskbar: fix flaky tests - TaskSpaces - Visual Studio Code", "Deskbar");
        StandOn(manager, "iam.ge");

        Run(manager, HookArgs("Stop"));

        Assert.True(Blinks(manager, "Deskbar"));
        Assert.DoesNotContain(manager.WindowsByWorkspace().Value.Workspaces.Single(g => g.Workspace.Name == "Deskbar").Running, r => r.Asks);
    }

    void Titled(WorkspaceManager manager, nint hwnd, string title, string workspace)
    {
        var window = new WindowInfo(new WindowHandle(hwnd), 100, "Code", @"C:\VS Code\Code.exe", title, null);
        desktops.WindowPlacements[window.Handle] = DesktopOf(manager, workspace);
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, window));
    }

    // An ask from a folder no VS Code window shows is still exit 0 and silent: a hook must never print an error into
    // the session.
    [Fact]
    public void An_ask_from_an_unknown_folder_is_silent()
    {
        var reply = Run(Started(), "claude-hook", "--from", @"C:\nowhere", "--json", Hook("Stop"));

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Equal("", reply.Output);
    }

    // A copy of Deskbar with no task service (RemoteControl's `tasks` left null) answers the task events with nothing
    // and blinks nothing.
    [Theory]
    [InlineData("SessionStart")]
    [InlineData("UserPromptSubmit")]
    [InlineData("SessionEnd")]
    public void Without_tasks_the_task_events_answer_nothing(string hookEvent)
    {
        var manager = Started();
        Editor(manager, 0x10, "TaskSpaces", "Deskbar");
        StandOn(manager, "iam.ge");

        var reply = Run(manager, HookArgs(hookEvent));

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Equal("", reply.Output);
        Assert.False(Blinks(manager, "Deskbar"));
    }

    // JSON that is not a hook's is the one refusal (exit 1). The script sends stderr nowhere and exits 0 anyway, so
    // the session still sees nothing.
    [Fact]
    public void A_hook_whose_json_does_not_parse_is_refused()
    {
        var reply = Run(Started(), "claude-hook", "--from", @"C:\x", "--json", "not json");

        Assert.Equal(ControlReply.Failed, reply.ExitCode);
    }

    [Fact]
    public void A_hook_with_no_json_is_refused() =>
        Assert.Equal(ControlReply.Failed, Run(Started(), "claude-hook", "--from", @"C:\x").ExitCode);

    // --- the client's half (ControlArguments) ---------------------------------------------------

    // The client adds its own folder, as it does for attention, when the script had no CLAUDE_PROJECT_DIR to pass.
    [Fact]
    public void The_client_adds_its_folder_to_claude_hook() =>
        Assert.Equal(["claude-hook", "--entry", "cli", "--from", @"C:\work"],
            ControlArguments.WithCallerFolder(["claude-hook", "--entry", "cli"], @"C:\work"));

    // The hook's JSON is the client's stdin, sent on as one argument. Only claude-hook reads stdin: any other command
    // run from a terminal would sit waiting on the keyboard.
    [Fact]
    public void The_client_sends_its_stdin_as_the_json_of_claude_hook_only()
    {
        Assert.Equal(["claude-hook", "--from", @"C:\x", "--json", "{}"],
            ControlArguments.WithHookInput(["claude-hook", "--from", @"C:\x"], () => "{}"));
        Assert.Equal(["windows"],
            ControlArguments.WithHookInput(["windows"], () => throw new InvalidOperationException("stdin read for windows")));
        // Already given (a person testing the verb by hand): stdin is left alone.
        Assert.Equal(["claude-hook", "--json", "{}"],
            ControlArguments.WithHookInput(["claude-hook", "--json", "{}"], () => throw new InvalidOperationException("stdin read twice")));
    }

    // Deskbar not running is not the session's business: the hook prints nothing and exits 0. Every other command keeps
    // its exit 2.
    [Fact]
    public void A_hook_that_finds_no_deskbar_is_quiet()
    {
        var down = new ControlReply(ControlReply.NotRunning, "Deskbar is not running");

        Assert.Equal(new ControlReply(ControlReply.Ok, ""), ControlArguments.HookReply(["claude-hook"], down));
        Assert.Equal(down, ControlArguments.HookReply(["windows"], down));
    }

    // Petre: `task add` takes `-` for its title too, read from stdin, so a note with quotes or & in it is safe from a
    // script as well; it lands on the caller's workspace (or --workspace) as any note does.
    [Fact]
    public void Task_add_takes_its_title_from_stdin()
    {
        var manager = Started();
        Editor(manager, 0x10, "TaskSpaces", "Deskbar");
        const string title = "Buy \"the good\" soap & tablets > 2";

        var args = ControlArguments.WithInput(["task", "add", "-", "--from", Repo], () => title + "\n");
        var reply = Ran(manager, [.. args]);

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Equal(IdOf(manager, "Deskbar"), Titled(title).WorkspaceId.Value);
        Assert.Equal(["task", "add", title, "--workspace", "none"],
            ControlArguments.WithInput(["task", "add", "-", "--workspace", "none"], () => title));
        Assert.Equal("task add (33 characters) --workspace none", ControlArguments.ForTrace(["task", "add", title, "--workspace", "none"]));
    }

    // Review of PR B, I-1: free text never rides cmd's command line. Each text-taking form takes `-` for its text and the
    // client puts its stdin there, the heredoc's last line break dropped; a `-` that is an option's value is not text.
    [Theory]
    [InlineData("status-line")]
    [InlineData("ask")]
    [InlineData("comment")]
    [InlineData("new")]
    [InlineData("later")]
    public void A_text_form_takes_its_text_from_stdin(string verb)
    {
        const string text = "set \"a > pwned.txt\" & echo INJECTED | more <x";

        Assert.Equal(["task", verb, text, "--session", "s1", "--from", @"C:\x"],
            ControlArguments.WithInput(["task", verb, "-", "--session", "s1", "--from", @"C:\x"], () => text + "\n"));
        Assert.Equal(["task", verb, "--session", "s1", text],
            ControlArguments.WithInput(["task", verb, "--session", "s1", "-"], () => text + "\r\n"));
    }

    [Fact]
    public void Only_a_text_forms_dash_reads_stdin()
    {
        string Never() => throw new InvalidOperationException("stdin read");

        Assert.Equal(["task", "done", "--session", "-"], ControlArguments.WithInput(["task", "done", "--session", "-"], Never));
        Assert.Equal(["task", "ask", "--session", "-"], ControlArguments.WithInput(["task", "ask", "--session", "-"], Never));
        Assert.Equal(["task", "ask", "plain"], ControlArguments.WithInput(["task", "ask", "plain"], Never));
        Assert.Equal(["move", "-"], ControlArguments.WithInput(["move", "-"], Never));
    }

    // The text arrives at the task exactly as written: quotes, &, |, < and > included.
    [Fact]
    public void Text_with_quotes_and_cmd_operators_arrives_intact()
    {
        var manager = Started();
        Editor(manager, 0x10, "TaskSpaces", "Deskbar");
        const string text = "checking \"x & echo INJECTED\" now | then > pwned.txt <in";
        Said(manager, "s1", Repo, "new", "Tasks board");

        var args = ControlArguments.WithInput(["task", "status-line", "-", "--session", "s1", "--from", Repo], () => text + "\n");
        var reply = Ran(manager, [.. args]);

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Equal(text, Titled("Tasks board").StatusLine);
    }

    // Review of PR B, M-4: a session's words are traced as their length; a comment's task id stays.
    [Fact]
    public void A_tasks_text_is_traced_as_its_length()
    {
        Assert.Equal("task ask (13 characters) --session s1 --from C:\\x",
            ControlArguments.ForTrace(["task", "ask", "OK to merge? ", "--session", "s1", "--from", @"C:\x"]));
        Assert.Equal("task comment 3f2a (6 characters) --from C:\\x",
            ControlArguments.ForTrace(["task", "comment", "3f2a", "secret", "--from", @"C:\x"]));
        Assert.DoesNotContain("secret", ControlArguments.ForTrace(["task", "status-line", "secret", "--session", "s1"]));
        Assert.DoesNotContain("secret", ControlArguments.ForTrace(["task", "new", "secret", "--session", "s1"]));
        Assert.DoesNotContain("secret", ControlArguments.ForTrace(["task", "later", "secret", "--session", "s1"]));
        Assert.Equal("task close 3f2a", ControlArguments.ForTrace(["task", "close", "3f2a"]));
    }

    // Review of Task 7, I-2: the app traces every `ctl` request, and a claude-hook's --json carries the user's prompt
    // and Claude's last reply. Tracing is often on, and a problem report carries the trace, so a hook is traced as its
    // event, its session and the JSON's length only. Every other command is traced as it was.
    [Fact]
    public void A_hook_is_traced_without_its_json()
    {
        var json = """{"session_id":"92943450","cwd":"C:\\x","hook_event_name":"UserPromptSubmit","prompt":"my secret plan"}""";

        var traced = ControlArguments.ForTrace(["claude-hook", "--entry", "cli", "--attended", "1", "--from", @"C:\x", "--json", json]);

        Assert.DoesNotContain("secret", traced);
        Assert.Contains("UserPromptSubmit", traced);
        Assert.Contains("92943450", traced);
        Assert.Contains($"{json.Length}", traced);
        Assert.DoesNotContain("secret", ControlArguments.ForTrace(["claude-hook", "--json", "{\"prompt\":\"my secret plan\""]));
        Assert.Equal("move x --title y", ControlArguments.ForTrace(["move", "x", "--title", "y"]));
    }

    // --- the setting -----------------------------------------------------------------------------

    // On by default (stored as its negation), and `claude-tasks` is the remote twin of Manage's box.
    [Fact]
    public void Claude_tasks_turns_the_setting_off_and_on()
    {
        var manager = Started();
        Assert.True(manager.State.ClaudeTasks);

        Assert.Equal(ControlReply.Ok, Run(manager, "claude-tasks", "off").ExitCode);
        Assert.False(manager.State.ClaudeTasks);
        Assert.Equal(ControlReply.Ok, Run(manager, "claude-tasks", "on").ExitCode);
        Assert.True(manager.State.ClaudeTasks);
        Assert.Equal(ControlReply.Failed, Run(manager, "claude-tasks", "maybe").ExitCode);
    }

    // Sessions keep tasks only while Deskbar also teaches Claude Code: both settings write into the same files. Turning
    // the teaching off leaves the stored choice alone, so turning it back on brings the tasks back as they were.
    [Fact]
    public void Turning_teach_off_turns_the_tasks_off_too()
    {
        var manager = Started();
        Assert.True(manager.State.ClaudeTasksInEffect);

        Assert.True(manager.SetClaudeSkill(false).IsSuccess);
        Assert.False(manager.State.ClaudeTasksInEffect);
        Assert.True(manager.State.ClaudeTasks);

        Assert.True(manager.SetClaudeSkill(true).IsSuccess);
        Assert.True(manager.State.ClaudeTasksInEffect);
    }

    [Fact]
    public void Claude_tasks_is_in_the_help()
    {
        Assert.Contains(ControlUsage.All, c => c.Verb == "claude-tasks");
        Assert.Contains(ControlUsage.All, c => c.Verb == "claude-hook");
    }

    // The automatic resume of open tasks' sessions (AppState.ClaudeResume; spec 2026-11-23-tasks-open-claude-sessions,
    // "The setting"): on by default, `claude-resume` is the remote twin of Manage's box, and it is in effect only while
    // sessions keep tasks at all, which keeps the stored choice when the tasks go off and come back.
    [Fact]
    public void Claude_resume_turns_the_setting_off_and_on()
    {
        var manager = Started();
        Assert.True(manager.State.ClaudeResume);
        Assert.True(manager.State.ClaudeResumeInEffect);

        Assert.Equal(ControlReply.Ok, Run(manager, "claude-resume", "off").ExitCode);
        Assert.False(manager.State.ClaudeResume);
        Assert.Equal(ControlReply.Ok, Run(manager, "claude-resume", "on").ExitCode);
        Assert.True(manager.State.ClaudeResume);
        Assert.Equal(ControlReply.Failed, Run(manager, "claude-resume", "maybe").ExitCode);

        Assert.True(manager.SetClaudeTasks(false).IsSuccess);
        Assert.False(manager.State.ClaudeResumeInEffect);
        Assert.True(manager.State.ClaudeResume);
        Assert.True(manager.SetClaudeTasks(true).IsSuccess);
        Assert.True(manager.State.ClaudeResumeInEffect);

        Assert.Contains(ControlUsage.All, c => c.Verb == "claude-resume");
    }

    // --- sessions join or create a task (plan Task 8) --------------------------------------------
    //
    // The hooks with a real TaskService behind them, and the session-facing `task` forms the briefing tells a session
    // to run. The clock is fixed, the store keeps nothing, and the long-path lookup is a stand-in that knows
    // one 8.3 name.

    readonly TaskService tasks = new(new MemoryTaskStore(), () => new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero));

    sealed class MemoryTaskStore : ITaskStore
    {
        public Result<TaskBoard> Load() => TaskBoard.Empty;

        public Result Save(TaskBoard board) => Result.Success();
    }

    const string Repo = @"C:\repos\TaskSpaces";

    // GetLongPathNameW's answer for the one short form used here.
    static string LongPath(string path) =>
        path.Equals(@"C:\repos\TASKSP~1", StringComparison.OrdinalIgnoreCase) ? Repo : path;

    ControlReply Ran(WorkspaceManager manager, params string[] args) =>
        new RemoteControl(manager, tasks: tasks, longPath: LongPath).Execute(args);

    static string HookOf(string hookEvent, string session, string? source) =>
        $$"""{"session_id":"{{session}}","cwd":"C:\\repos\\TaskSpaces","hook_event_name":"{{hookEvent}}"{{(source is null ? "" : $",\"source\":\"{source}\"")}}}""";

    // One hook, as the script runs it for an attended VS Code session (attended "0" for a headless one).
    string Hooked(WorkspaceManager manager, string hookEvent, string session = "s1", string? source = null,
        string from = Repo, string attended = "1")
    {
        var reply = Ran(manager, "claude-hook", "--entry", "claude-vscode", "--attended", attended, "--from", from,
            "--json", HookOf(hookEvent, session, source));
        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        return reply.Output;
    }

    // A session-facing `task` form, as a session runs it from its folder (the client appends --from).
    ControlReply Said(WorkspaceManager manager, string session, string from, params string[] form) =>
        Ran(manager, ["task", .. form, "--session", session, "--from", from]);

    Guid IdOf(WorkspaceManager manager, string name) => manager.State.Workspaces.Single(w => w.Name == name).Id;

    TaskItem Titled(string title) => tasks.Board.Tasks.Single(t => t.Title == title);

    // The spec's "What a session is offered": the open tasks on the session's own workspace, notes included, plus the
    // open Claude tasks on no workspace. Petre's notes on no workspace (errands) and other workspaces' tasks are not.
    [Fact]
    public void A_new_session_is_offered_its_workspaces_tasks()
    {
        var manager = Started();
        Editor(manager, 0x10, "TaskSpaces", "Deskbar");
        // Ruling 57: only a note Petre assigned to Claude is offered; one still on him is not.
        tasks.Assign(tasks.AddNote("Fix the strip", IdOf(manager, "Deskbar")).Value.Id, Assignee.Claude);
        tasks.AddNote("Still mine", IdOf(manager, "Deskbar"));
        tasks.AddNote("Dishwasher tablets", Maybe<Guid>.None);
        tasks.AddNote("Portal accounts", IdOf(manager, "iam.ge"));
        tasks.Create("Free Claude work", Maybe<Guid>.None, tasks.NewSession("old", @"C:\elsewhere"));

        var briefing = Hooked(manager, "SessionStart", source: "startup");

        Assert.Contains("s1", briefing);
        Assert.Contains("Fix the strip", briefing);
        Assert.Contains("Free Claude work", briefing);
        Assert.DoesNotContain("Dishwasher tablets", briefing);
        Assert.DoesNotContain("Still mine", briefing);
        Assert.DoesNotContain("Portal accounts", briefing);
        Assert.Contains("task join <id> --session s1", briefing);
    }

    // Ruling 62: `task answered` settles the session's comments since its ask; `task comment-settle <comment-id>` settles
    // one of its own and refuses another session's; `task list` shows the settled time in the thread.
    [Fact]
    public void Answering_and_comment_settle_fold_a_sessions_comments()
    {
        var manager = Started();
        Editor(manager, 0x10, "TaskSpaces", "Deskbar");
        Said(manager, "s1", Repo, "new", "Tasks board");
        Said(manager, "s1", Repo, "ask", "Merge?");
        Said(manager, "s1", Repo, "comment", "detail of the question");
        Said(manager, "s1", Repo, "comment", "second");
        var detail = Titled("Tasks board").Comments[0].Id.ToString();
        var second = Titled("Tasks board").Comments[1].Id.ToString();

        // Another session cannot settle it, and nothing changes.
        var refused = Said(manager, "s2", Repo, "comment-settle", detail);
        Assert.Equal(ControlReply.Failed, refused.ExitCode);
        Assert.True(Titled("Tasks board").Comments[0].Settled.HasNoValue);

        Assert.Equal(ControlReply.Ok, Said(manager, "s1", Repo, "comment-settle", second).ExitCode);
        Assert.True(Titled("Tasks board").Comments[1].Settled.HasValue);

        Said(manager, "s1", Repo, "answered");
        Assert.All(Titled("Tasks board").Comments, c => Assert.True(c.Settled.HasValue));
        var thread = System.Text.Json.JsonDocument.Parse(Ran(manager, "task", "list").Output).RootElement[0].GetProperty("thread");
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, thread[0].GetProperty("settled").ValueKind);
    }

    // Ruling 57: the assignee follows the session. `task ask` hands the task to Petre, `task answered` back to Claude, and
    // `task done` to Petre, who closes it.
    [Fact]
    public void A_sessions_forms_move_the_assignee()
    {
        var manager = Started();
        Editor(manager, 0x10, "TaskSpaces", "Deskbar");
        Said(manager, "s1", Repo, "new", "Tasks board");
        Assert.Equal(Assignee.Claude, Titled("Tasks board").Assignee);

        Said(manager, "s1", Repo, "ask", "Ship it?");
        Assert.Equal(Assignee.Me, Titled("Tasks board").Assignee);

        Said(manager, "s1", Repo, "answered");
        Assert.Equal(Assignee.Claude, Titled("Tasks board").Assignee);

        Said(manager, "s1", Repo, "done");
        Assert.Equal(Assignee.Me, Titled("Tasks board").Assignee);
    }

    // Ruling 34: resuming a session whose task is Done keeps it Done; only the user's next message moves it back.
    [Fact]
    public void A_resumed_session_keeps_its_tasks_status_until_the_user_writes()
    {
        var manager = Started();
        Editor(manager, 0x10, "TaskSpaces", "Deskbar");
        Said(manager, "s1", Repo, "new", "Tasks board");
        Said(manager, "s1", Repo, "done");
        Hooked(manager, "SessionEnd");

        Hooked(manager, "SessionStart", source: "resume");

        Assert.Equal(TaskStatus.Done, Titled("Tasks board").Status);
        Assert.True(Titled("Tasks board").HasLiveSession);

        Hooked(manager, "UserPromptSubmit");

        Assert.Equal(TaskStatus.InProgress, Titled("Tasks board").Status);
    }

    // Review focus 4: a resumed session (same id) keeps its task instead of being asked to pick again, even after the
    // session had ended; it is live on it again. Compacting a session that never ended keeps it too.
    [Fact]
    public void A_resumed_session_keeps_its_task()
    {
        var manager = Started();
        Editor(manager, 0x10, "TaskSpaces", "Deskbar");
        tasks.AddNote("Fix the strip", IdOf(manager, "Deskbar"));
        Hooked(manager, "SessionStart", source: "startup");
        Assert.Equal(ControlReply.Ok, Said(manager, "s1", Repo, "new", "Tasks board").ExitCode);
        Hooked(manager, "SessionEnd");
        Assert.False(Titled("Tasks board").HasLiveSession);

        var resumed = Hooked(manager, "SessionStart", source: "resume");

        Assert.Contains("Tasks board", resumed);
        Assert.DoesNotContain("Fix the strip", resumed);
        Assert.DoesNotContain("task join <id>", resumed);
        Assert.True(Titled("Tasks board").HasLiveSession);
        Assert.Contains("Tasks board", Hooked(manager, "SessionStart", source: "compact"));
    }

    // Ruling 49: a prompt no longer settles a question. Any message counts as a prompt, including a /loop or scheduled
    // check-in that is not the user answering, so the task keeps waiting and the blink stays; the one line the session
    // reads names the question and says how to resolve it once it is settled.
    [Fact]
    public void A_prompt_leaves_a_waiting_task_waiting_and_names_the_question()
    {
        var manager = Started();
        Editor(manager, 0x10, "TaskSpaces", "Deskbar");
        StandOn(manager, "iam.ge");
        Said(manager, "s1", Repo, "new", "Tasks board");
        Said(manager, "s1", Repo, "ask", "OK to merge?");
        Assert.True(Blinks(manager, "Deskbar"));

        var line = Hooked(manager, "UserPromptSubmit");

        Assert.Equal(TaskStatus.WaitingOnYou, Titled("Tasks board").Status);
        Assert.Equal("OK to merge?", Titled("Tasks board").Question.Value);
        Assert.True(Blinks(manager, "Deskbar"));
        Assert.Contains("OK to merge?", line);
        Assert.Contains("task answered --session s1", line);
        Assert.Single(line.Trim().Split('\n'));
    }

    // A long question is cut in the reminder, as titles are.
    [Fact]
    public void The_reminder_cuts_a_long_question()
    {
        var manager = Started();
        Editor(manager, 0x10, "TaskSpaces", "Deskbar");
        Said(manager, "s1", Repo, "new", "Tasks board");
        Said(manager, "s1", Repo, "ask", new string('q', 400));

        var line = Hooked(manager, "UserPromptSubmit");

        Assert.DoesNotContain(new string('q', 81), line);
        Assert.Contains("task answered --session s1", line);
    }

    // `task answered`: the session says the question is settled. The task goes back to In progress, the question goes
    // and the workspace stops blinking.
    [Fact]
    public void Answered_resumes_the_task_and_clears_the_blink()
    {
        var manager = Started();
        Editor(manager, 0x10, "TaskSpaces", "Deskbar");
        StandOn(manager, "iam.ge");
        Said(manager, "s1", Repo, "new", "Tasks board");
        Said(manager, "s1", Repo, "ask", "OK to merge?");
        Assert.True(Blinks(manager, "Deskbar"));

        var reply = Said(manager, "s1", Repo, "answered");

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Equal(TaskStatus.InProgress, Titled("Tasks board").Status);
        Assert.True(Titled("Tasks board").Question.HasNoValue);
        Assert.False(Blinks(manager, "Deskbar"));
    }

    // A task that does not wait is left as it is, and the command still succeeds: a session may run it needlessly.
    [Fact]
    public void Answered_on_a_task_that_is_not_waiting_is_a_successful_no_op()
    {
        var manager = Started();
        Editor(manager, 0x10, "TaskSpaces", "Deskbar");
        Said(manager, "s1", Repo, "new", "Tasks board");
        Said(manager, "s1", Repo, "done");
        var before = tasks.Board;

        Assert.Equal(ControlReply.Ok, Said(manager, "s1", Repo, "answered").ExitCode);

        Assert.Same(before, tasks.Board);
        Assert.Equal(TaskStatus.Done, Titled("Tasks board").Status);
    }

    // Only the session's own task: another session's waiting task keeps waiting.
    [Fact]
    public void Answered_touches_only_the_sessions_own_task()
    {
        var manager = Started();
        Editor(manager, 0x10, "TaskSpaces", "Deskbar");
        Said(manager, "s1", Repo, "new", "Tasks board");
        Said(manager, "s2", Repo, "new", "Store listing");
        Said(manager, "s2", Repo, "ask", "Which price?");

        Assert.Equal(ControlReply.Ok, Said(manager, "s1", Repo, "answered").ExitCode);

        Assert.Equal(TaskStatus.WaitingOnYou, Titled("Store listing").Status);
        Assert.Equal("Which price?", Titled("Store listing").Question.Value);
    }

    // Like the other own-task forms, it needs --session, and a session with no task is refused.
    [Fact]
    public void Answered_needs_a_session_with_a_task()
    {
        var manager = Started();

        Assert.Equal(ControlReply.Failed, Said(manager, "s1", Repo, "answered").ExitCode);
        Assert.Equal(ControlReply.Failed, Ran(manager, "task", "answered", "--from", Repo).ExitCode);
    }

    [Fact]
    public void Help_lists_task_answered() =>
        Assert.Contains(ControlUsage.All, c => c.Verb == "task" && c.Arguments.StartsWith("answered --session", StringComparison.Ordinal));

    // Petre closed the task: the session is told on its next prompt, and that new work needs a new task.
    [Fact]
    public void A_closed_task_tells_the_session_to_start_a_new_one()
    {
        var manager = Started();
        Editor(manager, 0x10, "TaskSpaces", "Deskbar");
        Said(manager, "s1", Repo, "new", "Tasks board");
        Assert.True(tasks.Close(Titled("Tasks board").Id).IsSuccess);

        var line = Hooked(manager, "UserPromptSubmit");

        Assert.Contains("closed", line);
        Assert.Contains("task new - --session s1", line);
        Assert.Equal(TaskStatus.Closed, Titled("Tasks board").Status);
    }

    // Review focus 5: a session in a folder no VS Code window shows makes a task on no workspace, still Claude's.
    [Fact]
    public void A_session_outside_any_vs_code_window_makes_a_free_task()
    {
        var manager = Started();
        Editor(manager, 0x10, "TaskSpaces", "Deskbar");

        var reply = Said(manager, "s1", @"C:\scratch\probe", "new", "Probe the hooks");

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.True(Titled("Probe the hooks").WorkspaceId.HasNoValue);
        Assert.True(Titled("Probe the hooks").IsClaudes);
        Assert.Equal(TaskStatus.InProgress, Titled("Probe the hooks").Status);
    }

    // An 8.3 short path with forward slashes (the SDK run in the probes) still finds the VS Code window, and the
    // session's folder is kept in its long form.
    [Fact]
    public void A_short_path_with_forward_slashes_finds_the_sessions_workspace()
    {
        var manager = Started();
        Editor(manager, 0x10, "TaskSpaces", "Deskbar");

        Said(manager, "s1", "C:/repos/TASKSP~1/", "new", "Tasks board");

        Assert.Equal(IdOf(manager, "Deskbar"), Titled("Tasks board").WorkspaceId.Value);
        Assert.Equal(Repo, Titled("Tasks board").Sessions.Single().Folder);
    }

    // `task ask` puts the task on Waiting on you with its question and asks for Petre, as `attention` does.
    [Fact]
    public void Ask_waits_on_you_and_blinks_the_workspace()
    {
        var manager = Started();
        Editor(manager, 0x10, "TaskSpaces", "Deskbar");
        StandOn(manager, "iam.ge");
        Said(manager, "s1", Repo, "new", "Tasks board");

        var reply = Said(manager, "s1", Repo, "ask", "OK to merge?");

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Equal(TaskStatus.WaitingOnYou, Titled("Tasks board").Status);
        Assert.Equal("OK to merge?", Titled("Tasks board").Question.Value);
        Assert.True(Blinks(manager, "Deskbar"));
    }

    // A session settles only its own task: `done` marks the task it is live on, not another session's or a note.
    [Fact]
    public void Done_marks_only_the_sessions_own_task()
    {
        var manager = Started();
        Editor(manager, 0x10, "TaskSpaces", "Deskbar");
        tasks.AddNote("Fix the strip", IdOf(manager, "Deskbar"));
        Said(manager, "s1", Repo, "new", "Tasks board");
        Said(manager, "s2", Repo, "new", "Store listing");

        Assert.Equal(ControlReply.Ok, Said(manager, "s1", Repo, "done").ExitCode);

        Assert.Equal(TaskStatus.Done, Titled("Tasks board").Status);
        Assert.Equal(TaskStatus.InProgress, Titled("Store listing").Status);
        Assert.Equal(TaskStatus.Backlog, Titled("Fix the strip").Status);
    }

    // The rest of a session's forms: join an offered task, keep its status line, comment on it in markdown (signed by
    // the session), and leave a follow-up note for later on its workspace.
    [Fact]
    public void A_session_joins_keeps_its_status_line_comments_and_leaves_a_note()
    {
        var manager = Started();
        Editor(manager, 0x10, "TaskSpaces", "Deskbar");
        var note = tasks.AddNote("Fix the strip", IdOf(manager, "Deskbar")).Value;

        Assert.Equal(ControlReply.Ok, Said(manager, "s1", Repo, "join", note.Id.ToString()[..8]).ExitCode);
        Assert.Equal(ControlReply.Ok, Said(manager, "s1", Repo, "status-line", "waiting for CI").ExitCode);
        Assert.Equal(ControlReply.Ok, Said(manager, "s1", Repo, "comment", "**Found it**: the guard").ExitCode);
        Assert.Equal(ControlReply.Ok, Said(manager, "s1", Repo, "later", "Rename the guard").ExitCode);

        var joined = Titled("Fix the strip");
        Assert.True(joined.IsClaudes);
        Assert.Equal(TaskStatus.InProgress, joined.Status);
        Assert.Equal("waiting for CI", joined.StatusLine);
        Assert.Equal("s1", joined.Comments.Single().SessionId.Value);
        Assert.Equal(IdOf(manager, "Deskbar"), Titled("Rename the guard").WorkspaceId.Value);
        Assert.Equal(TaskStatus.Backlog, Titled("Rename the guard").Status);
    }

    // A form that acts on the session's own task needs one: refused, and nothing changes.
    [Fact]
    public void A_session_with_no_task_is_refused_and_nothing_changes()
    {
        var manager = Started();
        var before = tasks.Board;

        Assert.Equal(ControlReply.Failed, Said(manager, "s1", Repo, "done").ExitCode);
        Assert.Equal(ControlReply.Failed, Said(manager, "s1", Repo, "ask", "OK?").ExitCode);
        Assert.Equal(ControlReply.Failed, Ran(manager, "task", "done", "--from", Repo).ExitCode);
        Assert.Same(before, tasks.Board);
    }

    // Headless sessions (`claude -p`, SDK runs, scheduled agents) read nothing and change nothing.
    [Fact]
    public void A_headless_session_changes_nothing()
    {
        var manager = Started();
        Editor(manager, 0x10, "TaskSpaces", "Deskbar");
        tasks.AddNote("Fix the strip", IdOf(manager, "Deskbar"));
        Said(manager, "s1", Repo, "new", "Tasks board");
        Said(manager, "s1", Repo, "ask", "OK to merge?");
        var before = tasks.Board;

        Assert.Equal("", Hooked(manager, "SessionStart", session: "s1", source: "startup", attended: "0"));
        Assert.Equal("", Hooked(manager, "UserPromptSubmit", session: "s1", attended: "0"));
        Assert.Equal("", Hooked(manager, "SessionEnd", session: "s1", attended: "0"));

        Assert.Same(before, tasks.Board);
    }

    // With "Claude Code sessions keep their tasks" off (or Teach off), a stale task hook says and changes nothing.
    [Fact]
    public void With_the_setting_off_the_task_events_say_nothing()
    {
        var manager = Started();
        Editor(manager, 0x10, "TaskSpaces", "Deskbar");
        Said(manager, "s1", Repo, "new", "Tasks board");
        Assert.True(manager.SetClaudeTasks(false).IsSuccess);
        var before = tasks.Board;

        Assert.Equal("", Hooked(manager, "SessionStart", source: "startup"));
        Assert.Equal("", Hooked(manager, "UserPromptSubmit"));
        Assert.Equal("", Hooked(manager, "SessionEnd"));
        Assert.Same(before, tasks.Board);
    }

    // SessionEnd marks the session ended; the task stays open with "no live session".
    [Fact]
    public void A_session_ending_leaves_its_task_open()
    {
        var manager = Started();
        Editor(manager, 0x10, "TaskSpaces", "Deskbar");
        Said(manager, "s1", Repo, "new", "Tasks board");

        Assert.Equal("", Hooked(manager, "SessionEnd"));

        Assert.False(Titled("Tasks board").HasLiveSession);
        Assert.Equal(TaskStatus.InProgress, Titled("Tasks board").Status);
    }

    // Review focus 1: a background session's SessionEnd arrives when its turn finishes, while `claude agents` still
    // lists it. The hook still ends it (it cannot tell), and the next reconcile with the session listed revives it.
    // Its hooks carry ATTENDED=0 and a job directory (--job), which is what lets them through at all.
    [Fact]
    public void SessionEnd_of_a_background_session_still_listed_keeps_it_live()
    {
        var manager = Started();
        Editor(manager, 0x10, "TaskSpaces", "Deskbar");
        const string session = "884c127f-9633-40eb-bed7-593800bca40f";
        Said(manager, session, Repo, "new", "Tasks board");

        var reply = Ran(manager, "claude-hook", "--entry", "cli", "--attended", "0", "--job", @"C:\jobs\884c127f",
            "--from", Repo, "--json", HookOf("SessionEnd", session, null));

        // Final review, item 13: while sessions keep tasks the list is read every 15 seconds and is the truth, so the
        // turn's SessionEnd no longer ends it at all (the board used to flicker "ended" until the next poll).
        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.True(Titled("Tasks board").HasLiveSession);

        // When it really goes, the list says so.
        tasks.Reconcile([]);
        Assert.False(Titled("Tasks board").HasLiveSession);
    }

    // A SessionEnd whose job folder names ANOTHER session (a `claude -p` run from a background session's shell inherits
    // it) is not that background session's: it ends what it names, as any other SessionEnd does.
    [Fact]
    public void SessionEnd_with_an_inherited_job_folder_still_ends_the_session()
    {
        var manager = Started();
        Editor(manager, 0x10, "TaskSpaces", "Deskbar");
        Said(manager, "s1", Repo, "new", "Tasks board");

        Ran(manager, "claude-hook", "--entry", "claude-vscode", "--attended", "1", "--job", @"C:\jobs\884c127f",
            "--from", Repo, "--json", HookOf("SessionEnd", "s1", null));

        Assert.False(Titled("Tasks board").HasLiveSession);
    }

    static string EndedBy(string session, string reason) =>
        $$"""{"session_id":"{{session}}","cwd":"C:\\repos\\TaskSpaces","hook_event_name":"SessionEnd","reason":"{{reason}}"}""";

    // Ruling 19: /clear starts a fresh conversation in the same window. The cleared session is marked replaced (the
    // keeper never resumes it) and the session that starts with source "clear" in the same folder takes its place on
    // the task, which keeps its status.
    [Fact]
    public void After_clear_the_new_session_takes_the_cleared_ones_place()
    {
        var manager = Started();
        Editor(manager, 0x10, "TaskSpaces", "Deskbar");
        Said(manager, "s1", Repo, "new", "Tasks board");
        Said(manager, "s1", Repo, "ask", "OK to merge?");

        Ran(manager, "claude-hook", "--entry", "claude-vscode", "--attended", "1", "--from", Repo, "--json", EndedBy("s1", "clear"));
        var cleared = Titled("Tasks board").Sessions.Single(s => s.Id == "s1");
        Assert.True(cleared.Replaced);
        Assert.False(cleared.IsLive);

        // The folder arrives in another spelling (forward slashes, a trailing separator): still the same folder.
        var briefing = Hooked(manager, "SessionStart", session: "s2", source: "clear", from: "C:/repos/TaskSpaces/");

        var task = Titled("Tasks board");
        Assert.True(task.Sessions.Single(s => s.Id == "s2").IsLive);
        Assert.Equal(TaskStatus.WaitingOnYou, task.Status);
        // Briefed on its task, not offered a choice.
        Assert.Contains("Tasks board", briefing);
        Assert.Single(tasks.Board.Tasks);
    }

    // A session that starts with source "clear" where nothing was cleared, or in another folder, is offered tasks as
    // any new session is, and joins nothing by itself.
    [Fact]
    public void A_clear_start_with_no_clear_in_its_folder_joins_nothing()
    {
        var manager = Started();
        Editor(manager, 0x10, "TaskSpaces", "Deskbar");
        Said(manager, "s1", Repo, "new", "Tasks board");
        Ran(manager, "claude-hook", "--entry", "claude-vscode", "--attended", "1", "--from", Repo, "--json", EndedBy("s1", "clear"));

        Hooked(manager, "SessionStart", session: "s2", source: "clear", from: @"C:\repos\Other");
        Hooked(manager, "SessionStart", session: "s3", source: "startup");

        Assert.DoesNotContain(tasks.Board.Tasks.SelectMany(t => t.Sessions), s => s.Id is "s2" or "s3");
    }

    // Unmeasured whether /clear keeps the session id. If it does, the same id starts again and rejoins as a fresh
    // entry: no longer replaced, so the keeper may resume it later like any other.
    [Fact]
    public void A_cleared_session_starting_again_under_its_own_id_is_no_longer_replaced()
    {
        var manager = Started();
        Editor(manager, 0x10, "TaskSpaces", "Deskbar");
        Said(manager, "s1", Repo, "new", "Tasks board");
        Ran(manager, "claude-hook", "--entry", "claude-vscode", "--attended", "1", "--from", Repo, "--json", EndedBy("s1", "clear"));

        Hooked(manager, "SessionStart", session: "s1", source: "clear");

        var session = Titled("Tasks board").Sessions.Single();
        Assert.True(session.IsLive);
        Assert.False(session.Replaced);
    }

    // Ruling 17: SessionStart and UserPromptSubmit from a session a person is at record it as attended, which is what
    // ClaudeSessions adopts; a headless one (`claude -p`, listed by `claude agents` as interactive) never is.
    [Fact]
    public void Attended_hooks_record_the_session_and_headless_ones_do_not()
    {
        var manager = Started();
        Editor(manager, 0x10, "TaskSpaces", "Deskbar");

        Hooked(manager, "SessionStart", session: "person", source: "startup");
        Hooked(manager, "UserPromptSubmit", session: "typist");
        Hooked(manager, "SessionStart", session: "script", source: "startup", attended: "0");
        Hooked(manager, "UserPromptSubmit", session: "script", attended: "0");
        Hooked(manager, "SessionEnd", session: "ender");

        Assert.True(tasks.WasAttended("person"));
        Assert.True(tasks.WasAttended("typist"));
        Assert.False(tasks.WasAttended("script"));
        Assert.False(tasks.WasAttended("ender"));
    }

    // Final review, item 14: the hook scripts are rewritten by whichever Deskbar starts last, so a newer script can pass
    // an option an older running copy does not know. It is skipped with its value, and the hook still works.
    [Fact]
    public void An_unknown_option_from_a_newer_hook_script_is_skipped()
    {
        var manager = Started();
        Editor(manager, 0x10, "TaskSpaces", "Deskbar");

        var reply = Ran(manager, "claude-hook", "--entry", "claude-vscode", "--newer", "x", "--attended", "1", "--empty", "",
            "--from", Repo, "--flag-without-value", "--json", HookOf("SessionStart", "s1", "startup"));

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.True(tasks.WasAttended("s1"));
        // A word that is no option at all is still refused, and so is a missing --json.
        Assert.True(ControlArguments.ParseClaudeHook(["stray", "--json", "{}"]).IsFailure);
        Assert.True(ControlArguments.ParseClaudeHook(["--newer", "x"]).IsFailure);
        Assert.Equal("{}", ControlArguments.ParseClaudeHook(["--newer", "--json", "{}"]).Value.Json);
    }
}
