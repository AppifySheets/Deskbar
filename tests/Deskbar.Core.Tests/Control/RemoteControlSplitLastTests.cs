using Deskbar.Core.Abstractions;
using Deskbar.Core.Control;
using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;

namespace Deskbar.Core.Tests.Control;

// `split-last <hwnd> on|off`: the pipe's twin of the window icon menu's "Split <app> windows last" (spec
// 2026-11-23-split-recent-app-windows-last-design). The setting lives in the manager's state, by process name, like
// "Remember <app>'s workspace", so the pipe reaches it directly.
public class RemoteControlSplitLastTests
{
    static readonly WindowInfo Code =
        new(new WindowHandle(0x10), 700, "Code", @"C:\apps\Code.exe", "main.cs - Visual Studio Code", null);

    // One of Deskbar's own windows: its process is this one.
    static readonly WindowInfo Ours =
        new(new WindowHandle(0x20), Environment.ProcessId, "Deskbar", @"C:\apps\Deskbar.exe", "Manage", null);

    readonly FakeStore store = new();

    WorkspaceManager Started()
    {
        var desktops = new FakeDesktops();
        var monitor = new FakeMonitor();
        var work = new Workspace(Guid.NewGuid(), "Work", Guid.NewGuid());
        desktops.Desktops.Add(new DesktopInfo(work.DesktopId!.Value, work.Name));
        new[] { Code, Ours }.ToList().ForEach(w =>
        {
            desktops.WindowPlacements[w.Handle] = work.DesktopId!.Value;
            monitor.InitialWindows.Add(w);
        });
        store.Stored = AppState.Empty with { Workspaces = [work] };
        var manager = new WorkspaceManager(desktops, monitor, new FakeTitles(), store);
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    [Fact]
    public void Split_last_on_lists_the_windows_app()
    {
        var reply = new RemoteControl(Started()).Execute(["split-last", "0x10", "on"]);

        Assert.True(reply.ExitCode == ControlReply.Ok, reply.Output);
        Assert.Equal(["Code"], store.Stored.SplitLastApps);
    }

    [Fact]
    public void Split_last_off_takes_it_off_again()
    {
        var remote = new RemoteControl(Started());
        remote.Execute(["split-last", "0x10", "on"]);

        var reply = remote.Execute(["split-last", "0x10", "off"]);

        Assert.True(reply.ExitCode == ControlReply.Ok, reply.Output);
        Assert.Empty(store.Stored.SplitLastApps);
    }

    [Fact]
    public void Another_word_prints_the_usage_line()
    {
        var reply = new RemoteControl(Started()).Execute(["split-last", "0x10", "maybe"]);

        Assert.NotEqual(ControlReply.Ok, reply.ExitCode);
        Assert.Contains("split-last", reply.Output);
    }

    [Fact]
    public void An_unknown_window_is_refused()
    {
        var reply = new RemoteControl(Started()).Execute(["split-last", "0x99", "on"]);

        Assert.NotEqual(ControlReply.Ok, reply.ExitCode);
        Assert.Empty(store.Stored.SplitLastApps);
    }

    // Plan review focus 4: Deskbar's own windows are never tiles, so the setting is refused for them.
    [Fact]
    public void Deskbars_own_window_is_refused()
    {
        var reply = new RemoteControl(Started()).Execute(["split-last", "0x20", "on"]);

        Assert.NotEqual(ControlReply.Ok, reply.ExitCode);
        Assert.Contains("never tiles", reply.Output);
        Assert.Empty(store.Stored.SplitLastApps);
    }
}
