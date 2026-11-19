using Deskbar.Core.Abstractions;
using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;
using Deskbar.Core.Tiling;

namespace Deskbar.Core.Tests.Tiling;

// The tiling settings: a workspace's Tiled flag, the gap and the gate that decides
// whether tiling acts. Whether tiling then moves windows is the app's business; this layer only records and decides.
public sealed class TiledSettingTests : IDisposable
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();
    readonly string dir = Path.Combine(Path.GetTempPath(), $"deskbar-tests-{Guid.NewGuid():N}");

    public void Dispose() { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }

    (WorkspaceManager manager, Guid a, Guid b) Started()
    {
        var a = new Workspace(Guid.NewGuid(), "Alpha", Guid.NewGuid());
        var b = new Workspace(Guid.NewGuid(), "Beta", Guid.NewGuid());
        new[] { a, b }.ToList().ForEach(w => desktops.Desktops.Add(new DesktopInfo(w.DesktopId!.Value, w.Name)));
        store.Stored = AppState.Empty with { Workspaces = [a, b] };
        var manager = new WorkspaceManager(desktops, monitor, titles, store);
        Assert.True(manager.Start().IsSuccess);
        return (manager, a.Id, b.Id);
    }

    // Loads a state.json written before tiling existed: no "Tiled" or "TileGap" key.
    AppState LoadedWithoutKeys()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "state.json"),
            $$$"""{"Workspaces":[{"Id":"{{{Guid.NewGuid()}}}","Name":"Old","DesktopId":null}],"WorkspaceRules":[],"RenameRules":[],"Inventory":{}}""");
        return new JsonPersistenceStore(dir).Load().Value;
    }

    // Loads a state.json written while Manage still had its "Tiling (experimental)" box: one tiled workspace
    // and the old ExperimentalTiling key, ticked or not.
    AppState LoadedWithTheOldSwitch(bool on)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "state.json"),
            $$$"""{"Workspaces":[{"Id":"{{{Guid.NewGuid()}}}","Name":"Old","DesktopId":null,"Tiled":true}],"WorkspaceRules":[],"RenameRules":[],"Inventory":{},"ExperimentalTiling":{{{(on ? "true" : "false")}}}}""");
        return new JsonPersistenceStore(dir).Load().Value;
    }

    [Fact]
    public void A_workspace_starts_untiled() =>
        Assert.DoesNotContain(Started().manager.State.Workspaces, w => w.Tiled);

    [Fact]
    public void Tiling_a_workspace_persists()
    {
        var (manager, a, _) = Started();

        Assert.True(manager.SetWorkspaceTiled(a, true).IsSuccess);

        Assert.True(manager.State.Workspaces.Single(w => w.Id == a).Tiled);
        Assert.True(store.Stored.Workspaces.Single(w => w.Id == a).Tiled);
    }

    [Fact]
    public void Keep_space_is_off_by_default_and_persists_when_turned_on()
    {
        var (manager, a, _) = Started();
        Assert.False(manager.State.Workspaces.Single(w => w.Id == a).TileKeepsSpace);
        Assert.False(LoadedWithoutKeys().Workspaces.Single().TileKeepsSpace);

        Assert.True(manager.SetTileKeepsSpace(a, true).IsSuccess);

        Assert.True(manager.State.Workspaces.Single(w => w.Id == a).TileKeepsSpace);
        Assert.True(store.Stored.Workspaces.Single(w => w.Id == a).TileKeepsSpace);
    }

    [Fact]
    public void Untiling_does_not_clear_keep_space()
    {
        var (manager, a, _) = Started();
        manager.SetWorkspaceTiled(a, true);
        manager.SetTileKeepsSpace(a, true);

        manager.SetWorkspaceTiled(a, false);

        Assert.True(manager.State.Workspaces.Single(w => w.Id == a).TileKeepsSpace);
    }

    [Fact]
    public void Keep_space_on_a_missing_workspace_fails() =>
        Assert.True(Started().manager.SetTileKeepsSpace(Guid.NewGuid(), true).IsFailure);

    [Fact]
    public void A_state_file_without_the_key_loads_untiled() =>
        Assert.DoesNotContain(LoadedWithoutKeys().Workspaces, w => w.Tiled);

    [Fact]
    public void Tiling_one_workspace_leaves_the_others_alone()
    {
        var (manager, a, b) = Started();

        manager.SetWorkspaceTiled(a, true);

        Assert.False(manager.State.Workspaces.Single(w => w.Id == b).Tiled);
    }

    [Fact]
    public void The_gap_defaults_to_8()
    {
        Assert.Equal(8, AppState.DefaultTileGap);
        Assert.Equal(8, Started().manager.State.TileGap);
        Assert.Equal(8, LoadedWithoutKeys().TileGap);
    }

    [Fact]
    public void A_negative_gap_is_refused()
    {
        var (manager, _, _) = Started();

        Assert.True(manager.SetTileGap(-1).IsFailure);
        Assert.True(manager.SetTileGap(65).IsFailure);
        Assert.Equal(8, manager.State.TileGap);

        Assert.True(manager.SetTileGap(0).IsSuccess);
        Assert.Equal(0, store.Stored.TileGap);
    }

    // The ExperimentalTiling key left AppState on 2026-11-19 (TilingGate). A state.json that still carries it,
    // ticked or not, must load, with each workspace's Tiled flag as written.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_state_written_with_the_old_switch_still_loads(bool on) =>
        Assert.True(LoadedWithTheOldSwitch(on).Workspaces.Single().Tiled);

    // Tiled and Pro must both hold: four combinations, one of which acts.
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void The_gate_needs_tiled_and_pro(bool tiled, bool pro, bool expected) =>
        Assert.Equal(expected, TilingGate.Acts(new Workspace(Guid.NewGuid(), "W", null) { Tiled = tiled }, pro));

    // Tiling is free for now (Petre, 2026-11-18): a copy that is not Pro may tile too. When tiling goes back
    // behind Pro, TilingGate.Free turns false and this test is the one to change with it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Tiling_is_allowed_with_or_without_pro_while_it_is_free(bool pro) =>
        Assert.True(TilingGate.Free && TilingGate.Allowed(pro));
}
