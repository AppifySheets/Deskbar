using Deskbar.Core.Control;

namespace Deskbar.Core.Tests.Control;

// `tile` and `untile` on the control pipe: the twins of the row menu's tiling item. Refusals are checked in
// the order the brief fixes: experiment off first, then not Pro. `untile` always works, so nobody is ever
// stuck with a workspace they cannot release.
public class RemoteControlTileTests
{
    static WorkspaceManager Manager(bool experiment)
    {
        var manager = new WorkspaceManager(new FakeDesktops(), new FakeMonitor(), new FakeTitles(), new FakeStore());
        manager.AddWorkspace("Work");
        if (experiment) manager.SetExperimentalTiling(true);
        return manager;
    }

    static bool Tiled(WorkspaceManager manager) => manager.State.Workspaces.Single(w => w.Name == "Work").Tiled;

    [Fact]
    public void Tile_marks_the_workspace_tiled_in_pro()
    {
        var manager = Manager(experiment: true);

        var reply = new RemoteControl(manager, isPro: () => true).Execute(["tile", "Work"]);

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.True(Tiled(manager));
    }

    [Fact]
    public void Tile_refuses_while_the_experiment_is_off()
    {
        var manager = Manager(experiment: false);

        var reply = new RemoteControl(manager, isPro: () => true).Execute(["tile", "Work"]);

        Assert.NotEqual(ControlReply.Ok, reply.ExitCode);
        Assert.Contains("Turn it on in Manage", reply.Output);
        Assert.False(Tiled(manager));
    }

    [Fact]
    public void Tile_refuses_in_the_free_tier()
    {
        var manager = Manager(experiment: true);

        var reply = new RemoteControl(manager, isPro: () => false).Execute(["tile", "Work"]);

        Assert.NotEqual(ControlReply.Ok, reply.ExitCode);
        Assert.Contains("Deskbar Pro", reply.Output);
        Assert.False(Tiled(manager));
    }

    [Fact]
    public void Tile_without_an_isPro_callback_is_not_pro()
    {
        var manager = Manager(experiment: true);

        Assert.NotEqual(ControlReply.Ok, new RemoteControl(manager).Execute(["tile", "Work"]).ExitCode);
        Assert.False(Tiled(manager));
    }

    [Fact]
    public void Untile_works_in_the_free_tier_and_with_the_experiment_off()
    {
        var manager = Manager(experiment: true);
        new RemoteControl(manager, isPro: () => true).Execute(["tile", "Work"]);
        manager.SetExperimentalTiling(false);

        var reply = new RemoteControl(manager, isPro: () => false).Execute(["untile", "Work"]);

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.False(Tiled(manager));
    }

    [Fact]
    public void Tile_an_unknown_workspace_fails()
    {
        var reply = new RemoteControl(Manager(experiment: true), isPro: () => true).Execute(["tile", "Nope"]);

        Assert.NotEqual(ControlReply.Ok, reply.ExitCode);
        Assert.Contains("Nope", reply.Output);
    }
}
