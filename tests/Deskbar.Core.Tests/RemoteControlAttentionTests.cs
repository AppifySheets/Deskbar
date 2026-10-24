using System.Text.Json;
using Deskbar.Core.Control;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Tests;

// `deskbar.cmd attention`. Petre: "if it needs attention, it should alert the deskbar, which should alert me by having
// that bar on the deskbar blink", "and the app itself should also glow, most likely vscode". The caller's folder names
// its VS Code window (EditorFolder); its workspace's row blinks until you arrive, the window glows until you look at it.
public class RemoteControlAttentionTests
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

    ControlReply Run(WorkspaceManager manager, params string[] args) => new RemoteControl(manager).Execute(args);

    Guid DesktopOf(WorkspaceManager manager, string workspace) => manager.State.Workspaces.Single(w => w.Name == workspace).DesktopId!.Value;

    WindowInfo Editor(WorkspaceManager manager, nint hwnd, string folder, string workspace)
    {
        var window = new WindowInfo(new WindowHandle(hwnd), 100, "Code", @"C:\VS Code\Code.exe", $"main.cs - {folder} - Visual Studio Code", null);
        desktops.WindowPlacements[window.Handle] = DesktopOf(manager, workspace);
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, window));
        return window;
    }

    void StandOn(WorkspaceManager manager, string workspace)
    {
        desktops.CurrentDesktopId = DesktopOf(manager, workspace);
        desktops.CurrentChangedSubject.OnNext(desktops.CurrentDesktopId);
    }

    Overview.WorkspaceGroup Group(WorkspaceManager manager, string workspace) =>
        manager.WindowsByWorkspace().Value.Workspaces.Single(g => g.Workspace.Name == workspace);

    static bool Marked(ControlReply reply) => JsonDocument.Parse(reply.Output).RootElement.GetProperty("marked").GetBoolean();

    [Fact]
    public void A_session_in_a_background_workspace_makes_its_row_blink_and_its_window_glow()
    {
        var manager = Started();
        var editor = Editor(manager, 0x10, "TaskSpaces", "Deskbar");
        StandOn(manager, "iam.ge");

        var reply = Run(manager, "attention", "--from", @"C:\repos\TaskSpaces\.claude\worktrees\debug-dump");

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.True(Marked(reply));
        Assert.True(Group(manager, "Deskbar").WantsAttention);
        Assert.True(Group(manager, "Deskbar").Running.Single(r => r.Window.Handle == editor.Handle).Asks);
        Assert.False(Group(manager, "iam.ge").WantsAttention);
    }

    // On the workspace you are looking at there is nothing to point you to.
    [Fact]
    public void A_session_in_the_workspace_you_are_on_marks_nothing()
    {
        var manager = Started();
        Editor(manager, 0x10, "TaskSpaces", "Deskbar");
        StandOn(manager, "Deskbar");

        var reply = Run(manager, "attention", "--from", @"C:\repos\TaskSpaces");

        Assert.False(Marked(reply));
        Assert.False(Group(manager, "Deskbar").WantsAttention);
    }

    // Arriving stops the row; the window keeps its glow until you look at it.
    [Fact]
    public void Arriving_stops_the_blink_and_looking_at_the_window_stops_the_glow()
    {
        var manager = Started();
        var editor = Editor(manager, 0x10, "TaskSpaces", "Deskbar");
        StandOn(manager, "iam.ge");
        Run(manager, "attention", "--from", @"C:\repos\TaskSpaces");

        StandOn(manager, "Deskbar");
        Assert.False(Group(manager, "Deskbar").WantsAttention);
        Assert.True(Group(manager, "Deskbar").Running.Single(r => r.Window.Handle == editor.Handle).Asks);

        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Activated, editor));
        Assert.False(Group(manager, "Deskbar").Running.Single(r => r.Window.Handle == editor.Handle).Asks);
    }

    // A hook runs this on every notification, so an unknown caller is an answer, not a failure.
    [Fact]
    public void An_unknown_caller_is_answered_without_failing()
    {
        var manager = Started();

        var reply = Run(manager, "attention", "--from", @"C:\nowhere");

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.False(Marked(reply));
        Assert.Contains("no VS Code window", reply.Output);
    }

    [Fact]
    public void The_client_adds_its_folder_to_attention() =>
        Assert.Equal(["attention", "--from", @"C:\work"], ControlArguments.WithCallerFolder(["attention"], @"C:\work"));

    [Fact]
    public void Attention_is_in_the_help() =>
        Assert.Contains(ControlUsage.All, c => c.Verb == "attention");
}
