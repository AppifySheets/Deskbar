using Deskbar.Core.Control;
using Deskbar.Core.Domain;
using Deskbar.Core.Tiling;

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

    // --- even and reset-grid: the two steps of the row menu's tidy item --------------------------------------------

    // A | C over B, the top pair split unevenly: a hand-made shape (not the automatic one for three windows).
    static readonly TileLayout UnevenShape = TileLayout.Normal(
        new TileBranch(Axis.Down, [
            new TileChild(new TileBranch(Axis.Across, [new TileChild(TileLeaf.Instance, 0.3), new TileChild(TileLeaf.Instance, 0.7)]), 0.6),
            new TileChild(TileLeaf.Instance, 0.4)]),
        null);

    const string Screen = "0,0,1920,1080";

    static IReadOnlyList<ScreenTileLayout> Layouts(WorkspaceManager manager) =>
        manager.State.Workspaces.Single(w => w.Name == "Work").TileLayouts;

    static WorkspaceManager WithShape()
    {
        var manager = Manager();
        Assert.True(manager.SetTileLayout(manager.State.Workspaces.Single(w => w.Name == "Work").Id, Screen, UnevenShape).IsSuccess);
        return manager;
    }

    // `even` is the first step: equal splits, the hand-made shape kept.
    [Fact]
    public void Pipe_even_keeps_the_shape()
    {
        var manager = WithShape();

        var reply = new RemoteControl(manager, isPro: () => true).Execute(["even", "Work"]);

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        var layout = Assert.Single(Layouts(manager)).Layout;
        Assert.True(layout.IsEven);
        Assert.True(layout.SameShapeAs(UnevenShape));
    }

    // `reset-grid` is the second: every stored layout forgotten, the screens back to the automatic grid.
    [Fact]
    public void Pipe_reset_grid_forgets_layouts()
    {
        var manager = WithShape();

        var reply = new RemoteControl(manager, isPro: () => true).Execute(["reset-grid", "Work"]);

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Empty(Layouts(manager));
    }

    // A tiling command, so refused like `tile` and `even` where this copy may not tile, and nothing is forgotten.
    [Fact]
    public void Pipe_reset_grid_refuses_in_the_free_tier()
    {
        var manager = WithShape();

        var reply = new RemoteControl(manager, isPro: () => false).Execute(["reset-grid", "Work"]);

        Assert.NotEqual(ControlReply.Ok, reply.ExitCode);
        Assert.Contains("Deskbar Pro", reply.Output);
        Assert.Single(Layouts(manager));
    }

    [Fact]
    public void Pipe_reset_grid_on_an_unknown_workspace_fails()
    {
        var reply = new RemoteControl(Manager(), isPro: () => true).Execute(["reset-grid", "Nope"]);

        Assert.NotEqual(ControlReply.Ok, reply.ExitCode);
        Assert.Contains("Nope", reply.Output);
    }
}
