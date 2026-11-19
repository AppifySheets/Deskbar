using Deskbar.Core.Control;

namespace Deskbar.Core.Tests.Control;

// `tile` and `untile` on the control pipe: the twins of the row menu's tiling item. The one refusal left is
// "not Pro" (there is no on/off switch since 2026-11-19, see TilingGate). `untile` always works, so nobody is ever
// stuck with a workspace they cannot release.
public class RemoteControlTileTests
{
    static WorkspaceManager Manager()
    {
        var manager = new WorkspaceManager(new FakeDesktops(), new FakeMonitor(), new FakeTitles(), new FakeStore());
        manager.AddWorkspace("Work");
        return manager;
    }

    static bool Tiled(WorkspaceManager manager) => manager.State.Workspaces.Single(w => w.Name == "Work").Tiled;

    [Fact]
    public void Tile_marks_the_workspace_tiled_in_pro()
    {
        var manager = Manager();

        var reply = new RemoteControl(manager, isPro: () => true).Execute(["tile", "Work"]);

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.True(Tiled(manager));
    }

    [Fact]
    public void Tile_refuses_in_the_free_tier()
    {
        var manager = Manager();

        var reply = new RemoteControl(manager, isPro: () => false).Execute(["tile", "Work"]);

        Assert.NotEqual(ControlReply.Ok, reply.ExitCode);
        Assert.Contains("Deskbar Pro", reply.Output);
        Assert.False(Tiled(manager));
    }

    [Fact]
    public void Tile_without_an_isPro_callback_is_not_pro()
    {
        var manager = Manager();

        Assert.NotEqual(ControlReply.Ok, new RemoteControl(manager).Execute(["tile", "Work"]).ExitCode);
        Assert.False(Tiled(manager));
    }

    [Fact]
    public void Untile_works_in_the_free_tier()
    {
        var manager = Manager();
        new RemoteControl(manager, isPro: () => true).Execute(["tile", "Work"]);

        var reply = new RemoteControl(manager, isPro: () => false).Execute(["untile", "Work"]);

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.False(Tiled(manager));
    }

    [Fact]
    public void Tile_an_unknown_workspace_fails()
    {
        var reply = new RemoteControl(Manager(), isPro: () => true).Execute(["tile", "Nope"]);

        Assert.NotEqual(ControlReply.Ok, reply.ExitCode);
        Assert.Contains("Nope", reply.Output);
    }
}
