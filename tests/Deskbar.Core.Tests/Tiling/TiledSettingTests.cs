using Deskbar.Core.Abstractions;
using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;
using Deskbar.Core.Tiling;

namespace Deskbar.Core.Tests.Tiling;

// The tiling settings: a workspace's Tiled flag, the gap, the experiment switch and the gate that combines
// them. Whether tiling then moves windows is the app's business; this layer only records and decides.
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

    // Loads a state.json written before tiling existed: no "Tiled", "TileGap" or "ExperimentalTiling" key.
    AppState LoadedWithoutKeys()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "state.json"),
            $$$"""{"Workspaces":[{"Id":"{{{Guid.NewGuid()}}}","Name":"Old","DesktopId":null}],"WorkspaceRules":[],"RenameRules":[],"Inventory":{}}""");
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

    [Fact]
    public void The_experiment_starts_off()
    {
        Assert.False(Started().manager.State.ExperimentalTiling);
        Assert.False(LoadedWithoutKeys().ExperimentalTiling);
    }

    [Fact]
    public void Turning_the_experiment_off_keeps_every_tiled_flag()
    {
        var (manager, a, _) = Started();
        manager.SetWorkspaceTiled(a, true);

        manager.SetExperimentalTiling(true);
        Assert.True(store.Stored.ExperimentalTiling);
        manager.SetExperimentalTiling(false);

        Assert.False(manager.State.ExperimentalTiling);
        Assert.True(manager.State.Workspaces.Single(w => w.Id == a).Tiled);
    }

    // Experiment, tiled and Pro must all hold: eight combinations, one of which acts.
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, true)]
    public void The_gate_needs_experiment_tiled_and_pro(bool experiment, bool tiled, bool pro, bool expected) =>
        Assert.Equal(expected, TilingGate.Acts(
            new Workspace(Guid.NewGuid(), "W", null) { Tiled = tiled }, pro, experiment));
}
