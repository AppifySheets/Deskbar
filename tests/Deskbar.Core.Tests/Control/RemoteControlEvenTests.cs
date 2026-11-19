using Deskbar.Core.Control;
using Deskbar.Core.Tiling;

namespace Deskbar.Core.Tests.Control;

// `even` on the control pipe: the twin of the row menu's "Even out tiles". It is refused like `tile` is, with
// the same message, where the copy may not tile. On a workspace with no stored layouts it succeeds and changes
// nothing. What it does to a stored layout (equal splits, the shape kept) is pinned in RemoteControlTileTests, beside
// its second step `reset-grid`.
public class RemoteControlEvenTests
{
    static WorkspaceManager Manager()
    {
        var manager = new WorkspaceManager(new FakeDesktops(), new FakeMonitor(), new FakeTitles(), new FakeStore());
        manager.AddWorkspace("Work");
        return manager;
    }

    static Guid Work(WorkspaceManager manager) => manager.State.Workspaces.Single(w => w.Name == "Work").Id;

    static int Stored(WorkspaceManager manager) =>
        manager.State.Workspaces.Single(w => w.Name == "Work").TileLayouts.Count;

    // The automatic shape for two windows (two columns) with the split dragged off the middle.
    static void Store(WorkspaceManager manager) =>
        manager.SetTileLayout(Work(manager), "0,0,1920,1080", TileLayout.Normal(
            new TileBranch(Axis.Across, [new TileChild(TileLeaf.Instance, 0.3), new TileChild(TileLeaf.Instance, 0.7)]), null));

    // Evened, the automatic shape is what no entry means, so the entry goes.
    [Fact]
    public void Even_on_the_automatic_shape_forgets_it_in_pro()
    {
        var manager = Manager();
        Store(manager);

        var reply = new RemoteControl(manager, isPro: () => true).Execute(["even", "Work"]);

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Equal(0, Stored(manager));
    }

    [Fact]
    public void Even_refuses_in_the_free_tier()
    {
        var manager = Manager();
        Store(manager);

        var reply = new RemoteControl(manager, isPro: () => false).Execute(["even", "Work"]);

        Assert.NotEqual(ControlReply.Ok, reply.ExitCode);
        Assert.Contains("Deskbar Pro", reply.Output);
        Assert.Equal(1, Stored(manager));
    }

    [Fact]
    public void Even_on_a_workspace_with_nothing_stored_succeeds()
    {
        var manager = Manager();

        var reply = new RemoteControl(manager, isPro: () => true).Execute(["even", "Work"]);

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Equal(0, Stored(manager));
    }

    [Fact]
    public void Even_an_unknown_workspace_fails()
    {
        var reply = new RemoteControl(Manager(), isPro: () => true).Execute(["even", "Nope"]);

        Assert.NotEqual(ControlReply.Ok, reply.ExitCode);
        Assert.Contains("Nope", reply.Output);
    }
}
