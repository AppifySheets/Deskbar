using Deskbar.Core.Domain;

namespace Deskbar.Core.Tests;

// WorkspaceManager.ClearAttention: an ask dismissed without arriving on its workspace. Ported from the abandoned
// feat/task-window branch (commits 78ff230, a12a2f7, 7ba2ab6), as plan Task 8 asks: the glow goes with the blink, and
// the asking windows are snapshotted before the shell is asked where each one is.
public class WorkspaceManagerClearAttentionTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();

    // A clock the tests move, so a focus can come after the arrival's own moment (WorkspaceManager.ArrivalGrace).
    DateTimeOffset now = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);

    WorkspaceManager Started()
    {
        var manager = new WorkspaceManager(desktops, monitor, titles, store, clock: () => now, ownProcessId: 4242);
        Assert.True(manager.Start().IsSuccess);
        manager.AddWorkspace("Deskbar");
        manager.AddWorkspace("iam.ge");
        manager.AddWorkspace("Store");
        return manager;
    }

    static Guid IdOf(WorkspaceManager manager, string name) => manager.State.Workspaces.Single(w => w.Name == name).Id;

    void StandOn(WorkspaceManager manager, string workspace)
    {
        desktops.CurrentDesktopId = manager.State.Workspaces.Single(w => w.Name == workspace).DesktopId!.Value;
        desktops.CurrentChangedSubject.OnNext(desktops.CurrentDesktopId);
    }

    static Overview.WorkspaceGroup Group(WorkspaceManager manager, string workspace) =>
        manager.WindowsByWorkspace().Value.Workspaces.Single(g => g.Workspace.Name == workspace);

    // A window that appeared on a workspace's desktop, as the shell reports it.
    WindowInfo WindowOn(WorkspaceManager manager, nint hwnd, string workspace)
    {
        var window = new WindowInfo(new WindowHandle(hwnd), 100, "Code", @"C:\VS Code\Code.exe", $"window {hwnd} - Visual Studio Code", null);
        desktops.WindowPlacements[window.Handle] = manager.State.Workspaces.Single(w => w.Name == workspace).DesktopId!.Value;
        monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Appeared, window));
        return window;
    }

    // Clearing an ask takes the glow off the windows that asked on that workspace too, not just the row's blink. A
    // window asking on another workspace is another ask, and keeps its glow.
    [Fact]
    public void Clearing_an_ask_also_clears_its_windows_glow()
    {
        var manager = Started();
        StandOn(manager, "Store");
        var editor = WindowOn(manager, 0x10, "Deskbar");
        var browser = WindowOn(manager, 0x20, "iam.ge");
        Assert.True(manager.RequestAttention(IdOf(manager, "Deskbar"), editor.Handle).Value);
        Assert.True(manager.RequestAttention(IdOf(manager, "iam.ge"), browser.Handle).Value);
        Assert.True(Group(manager, "Deskbar").Running.Single(r => r.Window.Handle == editor.Handle).Asks);

        Assert.True(manager.ClearAttention(IdOf(manager, "Deskbar")));

        Assert.False(Group(manager, "Deskbar").WantsAttention);
        Assert.False(Group(manager, "Deskbar").Running.Single(r => r.Window.Handle == editor.Handle).Asks);
        Assert.True(Group(manager, "iam.ge").Running.Single(r => r.Window.Handle == browser.Handle).Asks);
        Assert.True(Group(manager, "iam.ge").WantsAttention);
    }

    // ClearAttention asks the shell where each asking window is, a COM call that pumps the message queue, so the asking
    // set can change in the middle of it: a window looked at leaves it (MarkActive, from a WinEvent), and a session's
    // new ask joins it. The clear must not trip over either, and every window ends up where those events left it.
    [Fact]
    public void The_asks_changing_while_an_ask_is_cleared_do_not_break_the_clear()
    {
        var manager = Started();
        StandOn(manager, "Store");
        var editor = WindowOn(manager, 0x10, "Deskbar");
        var browser = WindowOn(manager, 0x20, "iam.ge");
        var terminal = WindowOn(manager, 0x30, "iam.ge");
        Assert.True(manager.RequestAttention(IdOf(manager, "Deskbar"), editor.Handle).Value);
        Assert.True(manager.RequestAttention(IdOf(manager, "iam.ge"), browser.Handle).Value);
        var delivered = false;
        desktops.OnDesktopOf = _ =>
        {
            if (delivered) return;
            delivered = true;
            // Inside the COM call: a session on iam.ge asks for the user from its terminal...
            Assert.True(manager.RequestAttention(IdOf(manager, "iam.ge"), terminal.Handle).IsSuccess);
            // ...and the user switches to iam.ge and, a moment after arriving, clicks the browser, whose focus event
            // arrives too. (A focus in the arrival's own moment is Windows' doing and clears nothing: ArrivingFocus.)
            StandOn(manager, "iam.ge");
            now += TimeSpan.FromSeconds(2);
            monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Activated, browser));
        };

        var thrown = Record.Exception(() => manager.ClearAttention(IdOf(manager, "Deskbar")));

        Assert.Null(thrown);
        Assert.True(delivered);
        desktops.OnDesktopOf = null;
        Assert.False(Group(manager, "Deskbar").Running.Single(r => r.Window.Handle == editor.Handle).Asks);
        Assert.False(Group(manager, "iam.ge").Running.Single(r => r.Window.Handle == browser.Handle).Asks);
        Assert.True(Group(manager, "iam.ge").Running.Single(r => r.Window.Handle == terminal.Handle).Asks);
    }
}
