using Deskbar.Core.Control;
using Deskbar.Core.Tiling;

namespace Deskbar.Core.Tests.Control;

// `even` on the control pipe: the twin of the row menu's "Even out tiles". It is refused like `tile` is, with
// the same two messages in the same order (experiment first, then Pro). On a workspace with no stored sizes
// it succeeds and changes nothing.
public class RemoteControlEvenTests
{
    static WorkspaceManager Manager(bool experiment)
    {
        var manager = new WorkspaceManager(new FakeDesktops(), new FakeMonitor(), new FakeTitles(), new FakeStore());
        manager.AddWorkspace("Work");
        if (experiment) manager.SetExperimentalTiling(true);
        return manager;
    }

    static Guid Work(WorkspaceManager manager) => manager.State.Workspaces.Single(w => w.Name == "Work").Id;

    static int Stored(WorkspaceManager manager) =>
        manager.State.Workspaces.Single(w => w.Name == "Work").TileSizes.Count;

    static void Store(WorkspaceManager manager) =>
        manager.SetTileSizes(Work(manager), "0,0,1920,1080", new TileSizes([0.3, 0.7], [[1.0], [0.25, 0.75]]));

    [Fact]
    public void Even_clears_the_sizes_in_pro()
    {
        var manager = Manager(experiment: true);
        Store(manager);

        var reply = new RemoteControl(manager, isPro: () => true).Execute(["even", "Work"]);

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Equal(0, Stored(manager));
    }

    [Fact]
    public void Even_refuses_while_the_experiment_is_off()
    {
        var manager = Manager(experiment: false);
        Store(manager);

        var reply = new RemoteControl(manager, isPro: () => true).Execute(["even", "Work"]);

        Assert.NotEqual(ControlReply.Ok, reply.ExitCode);
        Assert.Contains("Turn it on in Manage", reply.Output);
        Assert.Equal(1, Stored(manager));
    }

    [Fact]
    public void Even_refuses_in_the_free_tier()
    {
        var manager = Manager(experiment: true);
        Store(manager);

        var reply = new RemoteControl(manager, isPro: () => false).Execute(["even", "Work"]);

        Assert.NotEqual(ControlReply.Ok, reply.ExitCode);
        Assert.Contains("Deskbar Pro", reply.Output);
        Assert.Equal(1, Stored(manager));
    }

    [Fact]
    public void Even_on_a_workspace_with_no_sizes_succeeds()
    {
        var manager = Manager(experiment: true);

        var reply = new RemoteControl(manager, isPro: () => true).Execute(["even", "Work"]);

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Equal(0, Stored(manager));
    }

    [Fact]
    public void Even_an_unknown_workspace_fails()
    {
        var reply = new RemoteControl(Manager(experiment: true), isPro: () => true).Execute(["even", "Nope"]);

        Assert.NotEqual(ControlReply.Ok, reply.ExitCode);
        Assert.Contains("Nope", reply.Output);
    }
}
