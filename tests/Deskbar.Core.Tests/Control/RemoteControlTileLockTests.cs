using CSharpFunctionalExtensions;
using Deskbar.Core.Abstractions;
using Deskbar.Core.Control;
using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;

namespace Deskbar.Core.Tests.Control;

// `lock-tile <hwnd> on|off`: the pipe's twin of the window icon menu's "Don't split this tile" (spec
// 2026-11-22-tile-lock-and-other-screens). The lock lives in the Tiler, which the app hands in as a delegate, as it
// hands in everything else the pipe cannot reach from the manager; these tests stand in for it with a recorder.
public class RemoteControlTileLockTests
{
    static readonly WindowInfo Code =
        new(new WindowHandle(0x10), 700, "Code", @"C:\apps\Code.exe", "main.cs - Visual Studio Code", null);

    static WorkspaceManager Started()
    {
        var desktops = new FakeDesktops();
        var monitor = new FakeMonitor();
        var work = new Workspace(Guid.NewGuid(), "Work", Guid.NewGuid());
        desktops.Desktops.Add(new DesktopInfo(work.DesktopId!.Value, work.Name));
        desktops.WindowPlacements[Code.Handle] = work.DesktopId!.Value;
        monitor.InitialWindows.Add(Code);
        var store = new FakeStore { Stored = AppState.Empty with { Workspaces = [work] } };
        var manager = new WorkspaceManager(desktops, monitor, new FakeTitles(), store);
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    [Fact]
    public void Lock_tile_on_locks_the_window()
    {
        var calls = new List<(WindowHandle Window, bool On)>();
        var remote = new RemoteControl(Started(), setTileLocked: (w, on) => { calls.Add((w, on)); return Result.Success(); });

        var reply = remote.Execute(["lock-tile", "0x10", "on"]);

        Assert.True(reply.ExitCode == ControlReply.Ok, reply.Output);
        Assert.Equal([(Code.Handle, true)], calls);
    }

    [Fact]
    public void Lock_tile_off_unlocks_the_window()
    {
        var calls = new List<(WindowHandle Window, bool On)>();
        var remote = new RemoteControl(Started(), setTileLocked: (w, on) => { calls.Add((w, on)); return Result.Success(); });

        var reply = remote.Execute(["lock-tile", "0x10", "off"]);

        Assert.True(reply.ExitCode == ControlReply.Ok, reply.Output);
        Assert.Equal([(Code.Handle, false)], calls);
    }

    // The Tiler's refusal (a window that is not a tile) reaches the caller as the reply.
    [Fact]
    public void A_refused_lock_is_reported()
    {
        var remote = new RemoteControl(Started(), setTileLocked: (_, _) => Result.Failure("That window is not in a tile."));

        var reply = remote.Execute(["lock-tile", "0x10", "on"]);

        Assert.NotEqual(ControlReply.Ok, reply.ExitCode);
        Assert.Contains("That window is not in a tile.", reply.Output);
    }

    [Fact]
    public void Without_a_tiler_lock_tile_refuses()
    {
        var reply = new RemoteControl(Started()).Execute(["lock-tile", "0x10", "on"]);

        Assert.NotEqual(ControlReply.Ok, reply.ExitCode);
        Assert.Contains("Tiling is not running.", reply.Output);
    }

    [Fact]
    public void A_word_other_than_on_or_off_gets_the_usage_line()
    {
        var called = false;
        var remote = new RemoteControl(Started(), setTileLocked: (_, _) => { called = true; return Result.Success(); });

        var reply = remote.Execute(["lock-tile", "0x10", "maybe"]);

        Assert.NotEqual(ControlReply.Ok, reply.ExitCode);
        Assert.Contains(ControlUsage.LineFor("lock-tile"), reply.Output);
        Assert.False(called);
    }

    [Fact]
    public void Help_lists_lock_tile() => Assert.Contains("lock-tile", ControlUsage.Text);
}
