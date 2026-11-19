using Deskbar.Core.Abstractions;
using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;
using Deskbar.Core.Tiling;

namespace Deskbar.Core.Tests.Tiling;

// Stored split tree layouts: Workspace.TileLayouts, per screen, beside the old Workspace.TileSizes. A plain layout
// (even, the automatic shape, no opening) removes its entry, Even out keeps the shape and equalises the shares,
// Reset to grid and untiling forget every layout. Load turns an old TileSizes entry into a layout.
public sealed class TileLayoutSettingTests : IDisposable
{
    const string Left = "0,0,1920,1080";
    const string Right = "1920,0,3840,1080";

    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();
    readonly string dir = Path.Combine(Path.GetTempPath(), $"deskbar-tests-{Guid.NewGuid():N}");

    public void Dispose() { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }

    static TileNode Leaf => TileLeaf.Instance;

    // The automatic shape for three windows (one column, then a column of two), with uneven shares: not plain.
    static readonly TileLayout UnevenAutomatic = TileLayout.Normal(
        new TileBranch(Axis.Across, [new TileChild(Leaf, 0.3), new TileChild(new TileBranch(Axis.Down, [new TileChild(Leaf, 0.5), new TileChild(Leaf, 0.5)]), 0.7)]),
        null);

    // Not the automatic shape for three windows (a column of two, then one), uneven: stays stored once evened.
    static readonly TileLayout UnevenCustom = TileLayout.Normal(
        new TileBranch(Axis.Across, [new TileChild(new TileBranch(Axis.Down, [new TileChild(Leaf, 0.5), new TileChild(Leaf, 0.5)]), 0.7), new TileChild(Leaf, 0.3)]),
        null);

    (WorkspaceManager manager, Guid a) Started()
    {
        var a = new Workspace(Guid.NewGuid(), "Alpha", Guid.NewGuid());
        desktops.Desktops.Add(new DesktopInfo(a.DesktopId!.Value, a.Name));
        store.Stored = AppState.Empty with { Workspaces = [a] };
        var manager = new WorkspaceManager(desktops, monitor, titles, store);
        Assert.True(manager.Start().IsSuccess);
        return (manager, a.Id);
    }

    static IReadOnlyList<ScreenTileLayout> LayoutsOf(AppState state, Guid id) =>
        state.Workspaces.Single(w => w.Id == id).TileLayouts;

    [Fact]
    public void SetTileLayout_stores_per_screen_and_replaces_in_place()
    {
        var (manager, a) = Started();

        Assert.True(manager.SetTileLayout(a, Left, UnevenAutomatic).IsSuccess);
        Assert.True(manager.SetTileLayout(a, Right, UnevenCustom).IsSuccess);
        Assert.True(manager.SetTileLayout(a, Left, UnevenCustom).IsSuccess);

        var stored = LayoutsOf(store.Stored, a);
        Assert.Equal([Left, Right], stored.Select(s => s.Screen));
        Assert.Equal(UnevenCustom, stored[0].Layout);
    }

    [Fact]
    public void SetTileLayout_removes_a_plain_layout()
    {
        var (manager, a) = Started();
        manager.SetTileLayout(a, Left, UnevenAutomatic);

        Assert.True(manager.SetTileLayout(a, Left, TileLayout.Automatic(3)).IsSuccess);

        Assert.Empty(LayoutsOf(manager.State, a));
        Assert.Empty(LayoutsOf(store.Stored, a));
    }

    [Fact]
    public void EvenOutTiles_keeps_the_shape()
    {
        var (manager, a) = Started();
        manager.SetTileLayout(a, Left, UnevenCustom);

        Assert.True(manager.EvenOutTiles(a).IsSuccess);

        var layout = LayoutsOf(store.Stored, a).Single().Layout;
        Assert.True(layout.IsEven);
        Assert.True(layout.SameShapeAs(UnevenCustom));
    }

    [Fact]
    public void EvenOutTiles_drops_an_even_automatic_layout()
    {
        var (manager, a) = Started();
        manager.SetTileLayout(a, Left, UnevenAutomatic);
        manager.SetTileLayout(a, Right, UnevenCustom);

        Assert.True(manager.EvenOutTiles(a).IsSuccess);

        // The automatic shape evened is what the screen gets with no entry, so its entry goes; the custom shape stays.
        Assert.Equal([Right], LayoutsOf(store.Stored, a).Select(s => s.Screen));
    }

    // Ruling R1: until the Tiler stops reading TileSizes, Even out still clears them too.
    [Fact]
    public void EvenOutTiles_still_clears_the_old_sizes()
    {
        var (manager, a) = Started();
        manager.SetTileSizes(a, Left, new TileSizes([0.3, 0.7], [[1.0], [0.25, 0.75]]));

        Assert.True(manager.EvenOutTiles(a).IsSuccess);

        Assert.Empty(store.Stored.Workspaces.Single().TileSizes);
    }

    [Fact]
    public void ResetTileGrid_forgets_every_layout()
    {
        var (manager, a) = Started();
        manager.SetTileLayout(a, Left, UnevenAutomatic);
        manager.SetTileLayout(a, Right, UnevenCustom);
        manager.SetTileSizes(a, Left, new TileSizes([0.3, 0.7], [[1.0], [0.25, 0.75]]));

        Assert.True(manager.ResetTileGrid(a).IsSuccess);

        Assert.Empty(LayoutsOf(store.Stored, a));
        Assert.Empty(store.Stored.Workspaces.Single().TileSizes);
    }

    [Fact]
    public void Untiling_forgets_layouts()
    {
        var (manager, a) = Started();
        manager.SetWorkspaceTiled(a, true);
        manager.SetTileLayout(a, Left, UnevenCustom);

        manager.SetWorkspaceTiled(a, false);

        Assert.Empty(LayoutsOf(store.Stored, a));
    }

    [Fact]
    public void Tiling_keeps_layouts()
    {
        var (manager, a) = Started();
        manager.SetTileLayout(a, Left, UnevenCustom);

        manager.SetWorkspaceTiled(a, true);

        Assert.Single(LayoutsOf(manager.State, a));
    }

    [Fact]
    public void An_unknown_workspace_is_a_failure()
    {
        var (manager, _) = Started();

        Assert.True(manager.SetTileLayout(Guid.NewGuid(), Left, UnevenCustom).IsFailure);
        Assert.True(manager.ResetTileGrid(Guid.NewGuid()).IsFailure);
    }

    // A layout survives state.json, tree, shares and opening, and Workspace equality sees it by contents.
    [Fact]
    public void Layouts_roundtrip_through_state_json()
    {
        var withOpening = UnevenCustom with { Opening = 1 };
        var w = new Workspace(Guid.NewGuid(), "W", null) { TileLayouts = [new ScreenTileLayout(Left, withOpening)] };
        var persistence = new JsonPersistenceStore(dir);

        Assert.True(persistence.Save(AppState.Empty with { Workspaces = [w] }).IsSuccess);
        var loaded = persistence.Load().Value.Workspaces.Single();

        Assert.Equal(w, loaded);
    }
}
