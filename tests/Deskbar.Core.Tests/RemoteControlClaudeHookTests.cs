using Deskbar.Core.Control;
using Deskbar.Core.Domain;

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

    // An ask from a folder no VS Code window shows is still exit 0 and silent: a hook must never print an error into
    // the session.
    [Fact]
    public void An_ask_from_an_unknown_folder_is_silent()
    {
        var reply = Run(Started(), "claude-hook", "--from", @"C:\nowhere", "--json", Hook("Stop"));

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Equal("", reply.Output);
    }

    // The task events are wired here and answered in Task 8; for now they say nothing and blink nothing.
    [Theory]
    [InlineData("SessionStart")]
    [InlineData("UserPromptSubmit")]
    [InlineData("SessionEnd")]
    public void The_task_events_answer_nothing_yet(string hookEvent)
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
}
