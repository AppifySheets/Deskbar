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

    // Task a5e77647: --important and --urgent take no value, so the `-` after one is the title's place, not the flag's
    // value. It used to be skipped like --session's value: stdin was never read and the note was titled "-". The
    // trace had the same blind spot and wrote the title after such a flag in full.
    [Fact]
    public void A_dash_after_a_valueless_flag_reads_stdin()
    {
        const string title = "Buy \"the good\" soap & tablets > 2";

        Assert.Equal(["task", "add", "--important", title, "--from", Repo],
            ControlArguments.WithInput(["task", "add", "--important", "-", "--from", Repo], () => title + "\n"));
        Assert.Equal(["task", "add", "--urgent", "--important", title],
            ControlArguments.WithInput(["task", "add", "--urgent", "--important", "-"], () => title));
        Assert.Equal(["task", "add", "--workspace", "-", "--urgent", title],
            ControlArguments.WithInput(["task", "add", "--workspace", "-", "--urgent", "-"], () => title));
        Assert.Equal("task add --important (6 characters) --from C:\\x",
            ControlArguments.ForTrace(["task", "add", "--important", "secret", "--from", @"C:\x"]));
        Assert.Equal("task add --urgent (6 characters) --workspace none",
            ControlArguments.ForTrace(["task", "add", "--urgent", "secret", "--workspace", "none"]));
    }

    // The same note made end to end: the title from stdin, the flag set.
    [Fact]
    public void Task_add_important_takes_its_title_from_stdin()
    {
        var manager = Started();
        Editor(manager, 0x10, "TaskSpaces", "Deskbar");
        const string title = "Call \"them\" back & confirm";

        var reply = Ran(manager, [.. ControlArguments.WithInput(["task", "add", "--important", "-", "--from", Repo], () => title + "\n")]);

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.True(Titled(title).Important);
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

    // Task e52e33cb: the model task sessions start on. Opus until set, any alias or full name after, and never a
    // value that would hand the choice back to Claude Code's own settings.
    [Fact]
    public void Claude_model_reads_and_sets_the_model_task_sessions_start_on()
    {
        var manager = Started();
        Assert.Equal("opus", manager.State.ClaudeTaskModelInEffect);
        Assert.Contains("opus", Run(manager, "claude-model").Output);

        Assert.Equal(ControlReply.Ok, Run(manager, "claude-model", "sonnet").ExitCode);
        Assert.Equal("sonnet", manager.State.ClaudeTaskModelInEffect);
        Assert.Contains("sonnet", Run(manager, "claude-model").Output);

        Assert.Equal(ControlReply.Failed, Run(manager, "claude-model", "--bare").ExitCode);
        Assert.Equal("sonnet", manager.State.ClaudeTaskModelInEffect);

        Assert.Contains(ControlUsage.All, c => c.Verb == "claude-model");
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

    // `extra` is more of the hook's fields, each with its leading comma (the Stop hook's stop_hook_active, say).
    static string HookOf(string hookEvent, string session, string? source, string extra = "") =>
        $$"""{"session_id":"{{session}}","cwd":"C:\\repos\\TaskSpaces","hook_event_name":"{{hookEvent}}"{{(source is null ? "" : $",\"source\":\"{source}\"")}}{{extra}}}""";

    // One hook, as the script runs it for an attended VS Code session (attended "0" for a headless one).
    string Hooked(WorkspaceManager manager, string hookEvent, string session = "s1", string? source = null,
        string from = Repo, string attended = "1", string extra = "")
    {
        var reply = Ran(manager, "claude-hook", "--entry", "claude-vscode", "--attended", attended, "--from", from,
            "--json", HookOf(hookEvent, session, source, extra));
        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        return reply.Output;
    }

    // A session-facing `task` form, as a session runs it from its folder (the client appends --from).
    ControlReply Said(WorkspaceManager manager, string session, string from, params string[] form) =>
        Ran(manager, ["task", .. form, "--session", session, "--from", from]);

    Guid IdOf(WorkspaceManager manager, string name) => manager.State.Workspaces.Single(w => w.Name == name).Id;

    TaskItem Titled(string title) => tasks.Board.Tasks.Single(t => t.Title == title);

    // Recurring tasks (docs/superpowers/specs/2026-11-26-recurring-tasks-design.md, task 0b78269e): a session sets a repeat
    // on its OWN task, in the repo of the folder it was started in (a worktree is stored as its repo).
    [Fact]
    public void A_session_sets_a_repeat_on_its_own_task_in_its_repo()
    {
        var manager = Started();
        Hooked(manager, "SessionStart");
        Said(manager, "s1", Repo + @"\.claude\worktrees\x", "new", "Check the inbox");

        var reply = Said(manager, "s1", Repo + @"\.claude\worktrees\x", "repeat", "daily", "09:00", "--model", "haiku");

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        var repeat = Titled("Check the inbox").Repeat.Value;
        Assert.Equal("daily 09:00", repeat.Schedule.Text);
        Assert.Equal("haiku", repeat.Model);
        Assert.Equal(Repo, repeat.Folder);

        Assert.Equal(ControlReply.Ok, Said(manager, "s1", Repo, "repeat", "off").ExitCode);
        Assert.True(Titled("Check the inbox").Repeat.HasNoValue);
    }

    [Fact]
    public void A_session_without_a_task_is_refused()
    {
        var manager = Started();
        var before = tasks.Board;

        var reply = Said(manager, "nobody", Repo, "repeat", "daily", "09:00");

        Assert.Equal(ControlReply.Failed, reply.ExitCode);
        Assert.Contains("has no open task", reply.Output);
        Assert.Same(before, tasks.Board);
    }

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

    // Task d8c152cf: a session started outside VS Code (Petre's run in the terminal windows Deskbar opens) was offered
    // only the free Claude tasks, because the offer list asked the VS Code window alone for the workspace. It now falls
    // back the way `task new` does (task f905b1a7): the workspace of the latest task worked on in the session's repo, a
    // worktree counting as its repo.
    [Fact]
    public void A_session_without_a_vs_code_window_is_offered_its_repos_workspaces_tasks()
    {
        var manager = Started();
        tasks.Create("Store listing", IdOf(manager, "Deskbar"), tasks.NewSession("old", Repo));
        tasks.Assign(tasks.AddNote("Fix the strip", IdOf(manager, "Deskbar")).Value.Id, Assignee.Claude);
        tasks.Assign(tasks.AddNote("Portal accounts", IdOf(manager, "iam.ge")).Value.Id, Assignee.Claude);

        var briefing = Hooked(manager, "SessionStart", source: "startup", from: Repo + @"\.claude\worktrees\strip");

        Assert.Contains("Fix the strip", briefing);
        Assert.DoesNotContain("Portal accounts", briefing);
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
        // The ask put the question into the thread first (task 968c2216), so the session's two comments come after it.
        Assert.True(Titled("Tasks board").Comments[0].Asks);
        var detail = Titled("Tasks board").Comments[1].Id.ToString();
        var second = Titled("Tasks board").Comments[2].Id.ToString();

        // Another session cannot settle it, and nothing changes.
        var refused = Said(manager, "s2", Repo, "comment-settle", detail);
        Assert.Equal(ControlReply.Failed, refused.ExitCode);
        Assert.True(Titled("Tasks board").Comments[1].Settled.HasNoValue);

        Assert.Equal(ControlReply.Ok, Said(manager, "s1", Repo, "comment-settle", second).ExitCode);
        Assert.True(Titled("Tasks board").Comments[2].Settled.HasValue);

        Said(manager, "s1", Repo, "answered");
        Assert.All(Titled("Tasks board").Comments, c => Assert.True(c.Settled.HasValue));
        var thread = System.Text.Json.JsonDocument.Parse(Ran(manager, "task", "list").Output).RootElement[0].GetProperty("thread");
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, thread[0].GetProperty("settled").ValueKind);
    }

    // --- the Stop hook asks a parked session to say so (stalled tasks, spec 2026-11-23-stalled-claude-tasks-design.md) ---
    //
    // When a session's turn ends with its task still In progress and Claude's, and nothing of its own running in the
    // background, the Stop hook blocks the stop ONCE with a reason telling it to run `task ask` if the next move is Petre's.

    WorkspaceManager WithTask(string session = "s1")
    {
        var manager = Started();
        Editor(manager, 0x10, "TaskSpaces", "Deskbar");
        Said(manager, session, Repo, "new", "Tasks board");
        return manager;
    }

    static System.Text.Json.JsonElement Json(string output) => System.Text.Json.JsonDocument.Parse(output).RootElement;

    [Fact]
    public void Stop_on_a_parked_task_asks_the_session_to_say_so()
    {
        var output = Hooked(WithTask(), "Stop");

        Assert.Equal("block", Json(output).GetProperty("decision").GetString());
        Assert.Equal(SessionBriefing.ParkedNudge, Json(output).GetProperty("reason").GetString());
    }

    [Fact]
    public void Stop_after_a_nudge_lets_the_session_stop() =>
        Assert.Equal("", Hooked(WithTask(), "Stop", extra: ",\"stop_hook_active\":true"));

    [Fact]
    public void Stop_with_background_work_lets_the_session_stop() =>
        Assert.Equal("", Hooked(WithTask(), "Stop", extra: ",\"background_tasks\":[{\"type\":\"monitor\"}]"));

    [Fact]
    public void Stop_on_a_task_waiting_on_Petre_lets_the_session_stop()
    {
        var manager = WithTask();
        Said(manager, "s1", Repo, "ask", "Merge?");

        Assert.Equal("", Hooked(manager, "Stop"));
    }

    [Fact]
    public void Stop_from_a_headless_session_never_blocks()
    {
        var manager = WithTask();

        Assert.Equal("", Hooked(manager, "Stop", attended: "0"));
    }

    [Fact]
    public void Stop_without_a_task_lets_the_session_stop()
    {
        var manager = Started();
        Editor(manager, 0x10, "TaskSpaces", "Deskbar");

        Assert.Equal("", Hooked(manager, "Stop"));
    }

    [Fact]
    public void Stop_while_sessions_keep_no_tasks_lets_the_session_stop()
    {
        var manager = WithTask();
        Run(manager, "claude-tasks", "off");

        Assert.Equal("", Hooked(manager, "Stop"));
    }

    [Fact]
    public void Stop_still_blinks_when_it_blocks()
    {
        var manager = WithTask();
        StandOn(manager, "iam.ge");

        var output = Hooked(manager, "Stop");

        Assert.Equal("block", Json(output).GetProperty("decision").GetString());
        Assert.True(Blinks(manager, "Deskbar"));
    }

    // --- the quiet road (task 8710ac09): a session with a live waiter gets its Stop nudge in its inbox, not a block ---
    //
    // Claude Code shows every blocking Stop hook as "Stop hook reported a problem" in yellow, and a waiter's wake as one
    // line (measured 2026-11-26). So where a waiter can take it, the nudge goes into the inbox and the stop goes through.

    string HookedWithInbox(WorkspaceManager manager, string hookEvent, string extra = "") =>
        new RemoteControl(manager, tasks: tasks, longPath: LongPath, inbox: new CommentInbox(InboxFolder, () => DateTimeOffset.UtcNow))
            .Execute(["claude-hook", "--entry", "claude-vscode", "--attended", "1", "--from", Repo, "--json", HookOf(hookEvent, "s1", null, extra)])
            .Output;

    static string PromptField(string prompt) => ",\"prompt\":" + System.Text.Json.JsonSerializer.Serialize(prompt);

    string Inbox(string session = "s1") =>
        File.Exists(Path.Combine(InboxFolder, session + ".md")) ? File.ReadAllText(Path.Combine(InboxFolder, session + ".md")) : "";

    [Fact]
    public void Stop_with_a_waiter_puts_the_nudge_in_the_inbox_and_lets_the_stop_through()
    {
        try
        {
            var manager = WithTask();
            Waited(manager, "s1");

            Assert.Equal("", HookedWithInbox(manager, "Stop"));
            Assert.Equal(SessionBriefing.ParkedNudge, Inbox().Trim());
        }
        finally { if (Directory.Exists(InboxFolder)) Directory.Delete(InboxFolder, true); }
    }

    [Fact]
    public void Stop_without_a_waiter_still_blocks()
    {
        try
        {
            var output = HookedWithInbox(WithTask(), "Stop");

            Assert.Equal("block", Json(output).GetProperty("decision").GetString());
            Assert.Equal("", Inbox());
        }
        finally { if (Directory.Exists(InboxFolder)) Directory.Delete(InboxFolder, true); }
    }

    // The loop guard: the waiter's wake is a new turn, whose Stop carries no stop_hook_active. A turn that began with a
    // nudge is not nudged again; the next turn that did not is.
    [Fact]
    public void A_turn_begun_by_a_nudge_is_not_nudged_again()
    {
        try
        {
            var manager = WithTask();
            Waited(manager, "s1");
            var woken = "<task-notification>\n<summary>Deskbar</summary>\n</task-notification>\nStop hook blocking error from command \"Stop\": "
                + SessionBriefing.ParkedNudge;

            HookedWithInbox(manager, "UserPromptSubmit", PromptField(woken));
            Assert.Equal("", HookedWithInbox(manager, "Stop"));
            Assert.Equal("", Inbox());

            HookedWithInbox(manager, "UserPromptSubmit", PromptField("carry on"));
            Assert.Equal("", HookedWithInbox(manager, "Stop"));
            Assert.Equal(SessionBriefing.ParkedNudge, Inbox().Trim());
        }
        finally { if (Directory.Exists(InboxFolder)) Directory.Delete(InboxFolder, true); }
    }

    // Review of the branch: a comment that shared the waiter's wake with a nudge is work, so that turn may be nudged.
    [Fact]
    public void A_turn_begun_by_a_nudge_and_a_comment_may_be_nudged()
    {
        try
        {
            var manager = WithTask();
            Waited(manager, "s1");
            var both = SessionBriefing.ParkedNudge + "\n\n" + SessionBriefing.Commented(Titled("Tasks board"), "look at this");

            HookedWithInbox(manager, "UserPromptSubmit", PromptField(both));
            Assert.Equal("", HookedWithInbox(manager, "Stop"));
            Assert.Equal(SessionBriefing.ParkedNudge, Inbox().Trim());
        }
        finally { if (Directory.Exists(InboxFolder)) Directory.Delete(InboxFolder, true); }
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

    // Ruling 34: resuming a session whose task is Done keeps it Done. Since 2026-11-23 the user's next message keeps it
    // Done as well (it may only ask about the finished work); the session reopens it with `task join` when it is more work.
    [Fact]
    public void A_done_task_stays_done_through_a_resume_and_a_prompt_until_the_session_joins_it()
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

        Assert.Equal(TaskStatus.Done, Titled("Tasks board").Status);

        Said(manager, "s1", Repo, "join", Titled("Tasks board").Id.ToString());

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

    // Petre closed the task under a live session: its next prompt is told so, and that `task join` reopens it when the
    // message carries on with that work (task 327e3cd0), or `task new` for anything else. The prompt itself reopens
    // nothing; the join does.
    [Fact]
    public void A_closed_task_tells_the_session_it_can_join_it_again_or_start_a_new_one()
    {
        var manager = Started();
        Editor(manager, 0x10, "TaskSpaces", "Deskbar");
        Said(manager, "s1", Repo, "new", "Tasks board");
        var id = Titled("Tasks board").Id;
        Assert.True(tasks.Close(id, stoppingClaude: true).IsSuccess);

        var line = Hooked(manager, "UserPromptSubmit");

        Assert.Contains("closed", line);
        Assert.Contains($"task join {id.ToString()[..8]} --session s1", line);
        Assert.Contains("task new - --session s1", line);
        Assert.Equal(TaskStatus.Closed, Titled("Tasks board").Status);

        Assert.Equal(ControlReply.Ok, Said(manager, "s1", Repo, "join", id.ToString()[..8]).ExitCode);

        Assert.Equal(TaskStatus.InProgress, Titled("Tasks board").Status);
        Assert.Single(tasks.Board.Tasks);
    }

    // Task 327e3cd0: Petre closed the task, then resumed its session to keep talking about the same subject. The resume
    // reopens the task and puts the session back on it, and the briefing says so, instead of offering nothing and
    // leaving the session to make a duplicate.
    [Fact]
    public void A_resumed_session_reopens_its_closed_task()
    {
        var manager = Started();
        Editor(manager, 0x10, "TaskSpaces", "Deskbar");
        Said(manager, "s1", Repo, "new", "Tasks board");
        Said(manager, "s1", Repo, "done");
        Assert.True(tasks.Close(Titled("Tasks board").Id, stoppingClaude: true).IsSuccess);
        Hooked(manager, "SessionEnd");

        var briefing = Hooked(manager, "SessionStart", source: "resume");

        var task = Titled("Tasks board");
        Assert.Equal(TaskStatus.InProgress, task.Status);
        Assert.True(task.HasLiveSession);
        Assert.True(task.Assignee.IsClaude);
        Assert.Contains("resuming this session reopened it", briefing);
        Assert.Contains("Tasks board", briefing);
        Assert.DoesNotContain("no open tasks", briefing);
        Assert.DoesNotContain("closed your task", Hooked(manager, "UserPromptSubmit"));
        Assert.Single(tasks.Board.Tasks);
    }

    // Only a resume reopens: a compact is the same session carrying on, so a task closed under it stays closed and its
    // prompt line still offers the choice. A resume of a session that moved on to an open task rejoins that one.
    [Fact]
    public void A_compact_leaves_a_closed_task_closed_and_a_later_open_task_wins_a_resume()
    {
        var manager = Started();
        Editor(manager, 0x10, "TaskSpaces", "Deskbar");
        Said(manager, "s1", Repo, "new", "Tasks board");
        Assert.True(tasks.Close(Titled("Tasks board").Id, stoppingClaude: true).IsSuccess);

        var compacted = Hooked(manager, "SessionStart", source: "compact");

        Assert.Equal(TaskStatus.Closed, Titled("Tasks board").Status);
        Assert.DoesNotContain("reopened", compacted);

        Said(manager, "s1", Repo, "new", "Store listing");
        Hooked(manager, "SessionEnd");
        Hooked(manager, "SessionStart", source: "resume");

        Assert.Equal(TaskStatus.Closed, Titled("Tasks board").Status);
        Assert.True(Titled("Store listing").HasLiveSession);
    }

    // Task f905b1a7: "tasks added to the deskbar by claude sessions should be given proper workspaces, not none". With no
    // VS Code window on its folder (Petre's sessions run in terminal windows), a session's new task goes where the
    // latest task worked on in its repo went, and a worktree of the repo counts as the repo.
    [Fact]
    public void A_session_without_a_vs_code_window_makes_its_task_where_its_repos_latest_task_went()
    {
        var manager = Started();
        tasks.Create("Store listing", IdOf(manager, "Deskbar"), tasks.NewSession("old", Repo));

        var reply = Said(manager, "s1", Repo + @"\.claude\worktrees\task-workspace", "new", "Tasks board");

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Equal(IdOf(manager, "Deskbar"), Titled("Tasks board").WorkspaceId.Value);
    }

    // A session's follow-up note goes where the session's own task is, when no VS Code window says otherwise: the note
    // belongs with the work it follows up, even in a folder the board has never seen.
    [Fact]
    public void A_note_for_later_goes_on_the_sessions_own_tasks_workspace()
    {
        var manager = Started();
        var note = tasks.AddNote("Fix the strip", IdOf(manager, "iam.ge")).Value;
        Said(manager, "s1", @"C:\scratch\probe", "join", note.Id.ToString());

        Assert.Equal(ControlReply.Ok, Said(manager, "s1", @"C:\scratch\probe", "later", "Rename the guard").ExitCode);

        Assert.Equal(IdOf(manager, "iam.ge"), Titled("Rename the guard").WorkspaceId.Value);
    }

    // Task fa1e6a74: a note a session adds while it works says where it came from. `later` links it as related to the
    // session's own task, stored on the note and read both ways, so the task lists the note too.
    [Fact]
    public void A_note_for_later_is_related_to_the_sessions_own_task()
    {
        var manager = Started();
        var source = tasks.AddNote("Fix the strip", IdOf(manager, "iam.ge")).Value;
        Said(manager, "s1", @"C:\scratch\probe", "join", source.Id.ToString());

        Assert.Equal(ControlReply.Ok, Said(manager, "s1", @"C:\scratch\probe", "later", "Rename the guard").ExitCode);

        var note = Titled("Rename the guard");
        Assert.Equal([source.Id], note.Related);
        Assert.Contains(tasks.Board.RelatedOf(source.Id), t => t.Id == note.Id);
    }

    // A session with no open task has nothing to link to: `later` still adds the plain note, as before.
    [Fact]
    public void A_note_for_later_from_a_session_without_a_task_is_a_plain_note()
    {
        var manager = Started();

        Assert.Equal(ControlReply.Ok, Said(manager, "s1", Repo, "later", "Rename the guard").ExitCode);

        Assert.Empty(Titled("Rename the guard").Related);
    }

    // `task add` with --session is `later` with the script form's flags: the note keeps its flags and workspace and is
    // related to the session's own task.
    [Fact]
    public void A_task_added_with_a_session_is_related_to_the_sessions_own_task()
    {
        var manager = Started();
        var source = tasks.AddNote("Fix the strip", IdOf(manager, "iam.ge")).Value;
        Said(manager, "s1", Repo, "join", source.Id.ToString());

        Assert.Equal(ControlReply.Ok, Said(manager, "s1", Repo, "add", "Exempt the dev host", "--workspace", "Deskbar", "--important").ExitCode);

        var note = Titled("Exempt the dev host");
        Assert.Equal([source.Id], note.Related);
        Assert.Equal(IdOf(manager, "Deskbar"), note.WorkspaceId.Value);
        Assert.True(note.Important);
    }

    // A subtask of the session's own task already says where it came from on its parent line: no related link on top.
    [Fact]
    public void A_task_added_with_a_session_under_its_own_task_is_not_also_related()
    {
        var manager = Started();
        var source = tasks.AddNote("Fix the strip", IdOf(manager, "iam.ge")).Value;
        Said(manager, "s1", Repo, "join", source.Id.ToString());

        Assert.Equal(ControlReply.Ok, Said(manager, "s1", Repo, "add", "Rename the guard", "--parent", source.Id.ToString()).ExitCode);

        var note = Titled("Rename the guard");
        Assert.Equal(source.Id, note.ParentId.Value);
        Assert.Empty(note.Related);
    }

    // The script form, without --session, has no source task and links nothing.
    [Fact]
    public void A_task_added_without_a_session_is_not_related()
    {
        var manager = Started();
        var source = tasks.AddNote("Fix the strip", IdOf(manager, "iam.ge")).Value;
        Said(manager, "s1", Repo, "join", source.Id.ToString());

        Assert.Equal(ControlReply.Ok, Ran(manager, "task", "add", "Rename the guard", "--workspace", "Deskbar", "--from", Repo).ExitCode);

        Assert.Empty(Titled("Rename the guard").Related);
        Assert.Empty(tasks.Board.RelatedOf(source.Id));
    }

    // The VS Code window still comes first: the repo's history only fills in when no window answers.
    [Fact]
    public void The_vs_code_window_still_beats_the_repos_history()
    {
        var manager = Started();
        tasks.Create("Store listing", IdOf(manager, "iam.ge"), tasks.NewSession("old", Repo));
        Editor(manager, 0x10, "TaskSpaces", "Deskbar");

        Said(manager, "s1", Repo, "new", "Tasks board");

        Assert.Equal(IdOf(manager, "Deskbar"), Titled("Tasks board").WorkspaceId.Value);
    }

    // Review focus 5: a session in a folder no VS Code window shows, and no task of the board was worked on, makes a
    // task on no workspace, still Claude's.
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

    // --- the manager session is left out of tasks (manager session design, Task 6) ---

    const string ManagerDir = @"C:\State\manager";

    // A hook run from the manager's own folder, against a control that knows where that is.
    string ManagerHook(WorkspaceManager manager, string hookEvent, string session = "mgr", string source = "startup", string extra = "") =>
        new RemoteControl(manager, tasks: tasks, longPath: LongPath,
                taskSessions: new TaskSessionCommands(_ => Result.Success(), _ => [], (_, _, _) => Result.Success(), (_, _) => Result.Success())
                {
                    ManagerFolder = ManagerDir,
                })
            .Execute(["claude-hook", "--entry", "claude-vscode", "--attended", "1", "--from", ManagerDir,
                "--json", HookOf(hookEvent, session, source, extra)]).Output;

    [Fact]
    public void Manager_session_start_gets_the_manager_line_not_the_join_offer()
    {
        var manager = Started();
        tasks.AddNote("Fix the strip", IdOf(manager, "Deskbar"));

        var briefing = ManagerHook(manager, "SessionStart");

        Assert.Contains("manager session", briefing);
        Assert.Contains("mgr", briefing);
        Assert.DoesNotContain("has no task yet", briefing);
        Assert.DoesNotContain("task join", briefing);
        Assert.DoesNotContain("Fix the strip", briefing);
    }

    [Fact]
    public void Manager_prompt_gets_nothing() =>
        Assert.Equal("", ManagerHook(Started(), "UserPromptSubmit"));

    // The user's reply to the manager clears its open question (task 61e4d143, ManagerQuestion.AfterPrompt); the hook
    // still answers nothing, so a prompt costs no tokens.
    static WorkspaceManager WithOpenQuestion(WorkspaceManager manager)
    {
        manager.SetManagerSession(new ManagerRecord("mgr", new DateOnly(2026, 11, 25), Question: "Which first?", AskedAt: DateTimeOffset.UnixEpoch));
        return manager;
    }

    [Fact]
    public void Manager_prompt_from_the_user_clears_its_question_and_answers_nothing()
    {
        var manager = WithOpenQuestion(Started());

        var answer = ManagerHook(manager, "UserPromptSubmit", extra: ",\"prompt\":\"yes, close it\"");

        Assert.Equal("", answer);
        Assert.Null(manager.State.Manager!.Question);
        Assert.Null(manager.State.Manager.AskedAt);
    }

    [Fact]
    public void Manager_prompt_that_is_a_deskbar_wake_leaves_the_question()
    {
        var manager = WithOpenQuestion(Started());

        ManagerHook(manager, "UserPromptSubmit", extra: ",\"prompt\":\"Deskbar wake. Since your last look:\"");

        Assert.Equal("Which first?", manager.State.Manager!.Question);
    }

    [Fact]
    public void Prompt_from_another_session_in_the_manager_folder_leaves_the_question()
    {
        var manager = WithOpenQuestion(Started());

        ManagerHook(manager, "UserPromptSubmit", session: "someone-else", extra: ",\"prompt\":\"yes\"");

        Assert.Equal("Which first?", manager.State.Manager!.Question);
    }

    [Fact]
    public void Manager_session_is_not_marked_attended()
    {
        var manager = Started();

        ManagerHook(manager, "SessionStart");
        ManagerHook(manager, "UserPromptSubmit");

        Assert.False(tasks.WasAttended("mgr"));
    }

    // The waiting Stop hook registers a waiter for the manager too (event wakes, docs/superpowers/specs/2026-11-25-manager-
    // event-wakes-design.md): Deskbar's wake of a running, idle manager goes into its inbox, as a comment goes to a task's.
    [Fact]
    public void Manager_gets_a_waiter_for_its_wakes()
    {
        try
        {
            var reply = new RemoteControl(Started(), tasks: tasks, longPath: LongPath, inbox: new CommentInbox(InboxFolder, () => DateTimeOffset.UtcNow),
                    taskSessions: new TaskSessionCommands(_ => Result.Success(), _ => [], (_, _, _) => Result.Success(), (_, _) => Result.Success())
                    {
                        ManagerFolder = ManagerDir,
                    })
                .Execute(["claude-wait", "--entry", "claude-vscode", "--attended", "1", "--from", ManagerDir, "--json", HookOf("Stop", "mgr", null)]);

            Assert.StartsWith("mgr ", reply.Output);
            Assert.True(File.Exists(Path.Combine(InboxFolder, "mgr.gen")));
        }
        finally
        {
            if (Directory.Exists(InboxFolder)) Directory.Delete(InboxFolder, true);
        }
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

    // --- claude-wait: a session's comment waiter registers (plan 2026-11-24-comment-wakes-session, Task 3) ------------

    string InboxFolder { get; } = Path.Combine(Path.GetTempPath(), "deskbar-wait-" + Guid.NewGuid().ToString("N"));

    ControlReply Waited(WorkspaceManager manager, string session, string attended = "1") =>
        new RemoteControl(manager, tasks: tasks, longPath: LongPath, inbox: new CommentInbox(InboxFolder, () => DateTimeOffset.UtcNow))
            .Execute(["claude-wait", "--entry", "claude-vscode", "--attended", attended, "--from", Repo, "--json", HookOf("Stop", session, null)]);

    // The waiter script reads three lines: the inbox it watches, the gen file it checks, and its own generation.
    [Fact]
    // Final review, C1 and I2: ONE line of ASCII, the session id and the generation. The script builds the two paths
    // itself from its own folder (%~dp0), because a path read back through `set /p` or `for /f` comes out garbled when it
    // holds a letter outside ASCII, and the client's LF line endings broke `set /p`.
    public void Claude_wait_answers_the_session_and_its_generation()
    {
        try
        {
            var words = Waited(Started(), "s1").Output.Trim().Split(' ');

            Assert.Equal(2, words.Length);
            Assert.Equal("s1", words[0]);
            Assert.Equal(File.ReadAllText(Path.Combine(InboxFolder, "s1.gen")).Trim(), words[1]);
            Assert.All(Waited(Started(), "s1").Output, c => Assert.True(c < 128));
        }
        finally { if (Directory.Exists(InboxFolder)) Directory.Delete(InboxFolder, true); }
    }

    // A headless run (`claude -p`, the SDK) gets no waiter: nobody would read a comment there.
    [Fact]
    public void Claude_wait_answers_nothing_for_a_headless_session() =>
        Assert.Equal("", Waited(Started(), "s1", attended: "0").Output);

    // A session that ends leaves no inbox or gen file behind.
    [Fact]
    public void A_session_end_forgets_its_inbox()
    {
        try
        {
            var manager = Started();
            Waited(manager, "s1");
            File.WriteAllText(Path.Combine(InboxFolder, "s1.md"), "unread");

            new RemoteControl(manager, tasks: tasks, longPath: LongPath, inbox: new CommentInbox(InboxFolder, () => DateTimeOffset.UtcNow))
                .Execute(["claude-hook", "--entry", "claude-vscode", "--attended", "1", "--from", Repo, "--json", HookOf("SessionEnd", "s1", null)]);

            Assert.False(File.Exists(Path.Combine(InboxFolder, "s1.md")));
            Assert.False(File.Exists(Path.Combine(InboxFolder, "s1.gen")));
        }
        finally { if (Directory.Exists(InboxFolder)) Directory.Delete(InboxFolder, true); }
    }

    // Final review, C2: a background session sends SessionEnd at the end of EVERY turn while it lives on (the board
    // ignores it for the same reason), so its inbox and gen file stay: its waiter keeps watching, and a comment waiting
    // there is not deleted.
    // A background session's id starts with its job folder's name (ClaudeHookEvent.OwnJob).
    const string Bg = "884c127f-9633-40eb-bed7-593800bca40f";

    [Fact]
    public void A_background_sessions_turn_end_keeps_its_inbox()
    {
        try
        {
            var manager = Started();
            Waited(manager, Bg);
            File.WriteAllText(Path.Combine(InboxFolder, Bg + ".md"), "unread");

            new RemoteControl(manager, tasks: tasks, longPath: LongPath, inbox: new CommentInbox(InboxFolder, () => DateTimeOffset.UtcNow))
                .Execute(["claude-hook", "--entry", "cli", "--attended", "0", "--job", @"C:\jobs\884c127f", "--from", Repo,
                    "--json", HookOf("SessionEnd", Bg, null)]);

            Assert.True(File.Exists(Path.Combine(InboxFolder, Bg + ".md")));
            Assert.True(File.Exists(Path.Combine(InboxFolder, Bg + ".gen")));
        }
        finally { if (Directory.Exists(InboxFolder)) Directory.Delete(InboxFolder, true); }
    }

    // The client hands claude-wait its stdin and stays silent when Deskbar is not running, as for claude-hook.
    [Fact]
    public void Claude_wait_reads_stdin_like_a_hook() =>
        Assert.Equal(["claude-wait", "--json", "{}"], ControlArguments.WithInput(["claude-wait"], () => "{}"));

    // --- the answer options of `task ask` (AnswerOptions) -----------------------------------------------------------
    //
    // The options are more lines of the ask's stdin text, after a line `--options`: free text never goes on cmd's command
    // line, so no `--option <text>` word exists. The client sends the whole text as the ask's one word.

    [Fact]
    public void An_ask_with_options_stores_them_and_writes_the_question_alone_into_the_thread()
    {
        var manager = Started();
        Editor(manager, 0x10, "TaskSpaces", "Deskbar");
        Said(manager, "s1", Repo, "new", "Tasks board");

        var reply = Said(manager, "s1", Repo, "ask", "Merge [PR 512](https://example.com/pull/512)?\n--options\nMerge it\nNot yet");

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        var task = Titled("Tasks board");
        Assert.Equal(TaskStatus.WaitingOnYou, task.Status);
        Assert.Equal("Merge [PR 512](https://example.com/pull/512)?", task.Question.Value);
        Assert.Equal(["Merge it", "Not yet"], task.QuestionOptions);
        Assert.Equal("Merge [PR 512](https://example.com/pull/512)?", Assert.Single(task.Comments).Markdown);
    }

    [Fact]
    public void An_ask_with_a_bad_option_is_refused_with_the_limit_and_stores_nothing()
    {
        var manager = Started();
        Editor(manager, 0x10, "TaskSpaces", "Deskbar");
        Said(manager, "s1", Repo, "new", "Tasks board");

        var refused = Said(manager, "s1", Repo, "ask", "Merge?\n--options\na\nb\nc\nd\ne");

        Assert.Equal(ControlReply.Failed, refused.ExitCode);
        Assert.Contains("at most 4 options", refused.Output);
        Assert.Contains("40 characters", refused.Output);
        var task = Titled("Tasks board");
        Assert.Equal(TaskStatus.InProgress, task.Status);
        Assert.True(task.Question.HasNoValue);
        Assert.Empty(task.QuestionOptions);
        Assert.Empty(task.Comments);
    }

    [Fact]
    public void An_ask_with_a_marker_and_no_options_is_refused_too()
    {
        var manager = Started();
        Editor(manager, 0x10, "TaskSpaces", "Deskbar");
        Said(manager, "s1", Repo, "new", "Tasks board");

        Assert.Equal(ControlReply.Failed, Said(manager, "s1", Repo, "ask", "Merge?\n--options").ExitCode);

        Assert.True(Titled("Tasks board").Question.HasNoValue);
    }

    [Fact]
    public void An_ask_without_a_marker_is_as_it_was_and_answered_clears_the_options()
    {
        var manager = Started();
        Editor(manager, 0x10, "TaskSpaces", "Deskbar");
        Said(manager, "s1", Repo, "new", "Tasks board");
        Said(manager, "s1", Repo, "ask", "Merge?\n--options\nMerge it\nNot yet");
        Assert.Equal(2, Titled("Tasks board").QuestionOptions.Count);

        Said(manager, "s1", Repo, "answered");
        Said(manager, "s1", Repo, "ask", "Delete the branch?");

        var task = Titled("Tasks board");
        Assert.Equal("Delete the branch?", task.Question.Value);
        Assert.Empty(task.QuestionOptions);
    }
}
