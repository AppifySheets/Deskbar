using Deskbar.Core.Abstractions;
using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;
using Deskbar.Core.Tiling;

namespace Deskbar.Core.Tests.Tiling;

// Stored tile sizes: Workspace.TileSizes, kept per screen. SetTileSizes stores or replaces one screen's
// entry (equal sizes remove it), EvenOutTiles and untiling clear the lot, and turning the experiment off
// keeps them ("keep my sizes").
public sealed class TileSizesSettingTests : IDisposable
{
    const string Left = "0,0,1920,1080";
    const string Right = "1920,0,3840,1080";

    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();
    readonly string dir = Path.Combine(Path.GetTempPath(), $"deskbar-tests-{Guid.NewGuid():N}");

    public void Dispose() { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }

    static readonly TileSizes Uneven = new([0.3, 0.7], [[1.0], [0.25, 0.75]]);
    static readonly TileSizes OtherUneven = new([0.6, 0.4], [[0.5, 0.5], [1.0]]);

    (WorkspaceManager manager, Guid a) Started()
    {
        var a = new Workspace(Guid.NewGuid(), "Alpha", Guid.NewGuid());
        desktops.Desktops.Add(new DesktopInfo(a.DesktopId!.Value, a.Name));
        store.Stored = AppState.Empty with { Workspaces = [a] };
        var manager = new WorkspaceManager(desktops, monitor, titles, store);
        Assert.True(manager.Start().IsSuccess);
        return (manager, a.Id);
    }

    static IReadOnlyList<ScreenTileSizes> SizesOf(AppState state, Guid id) =>
        state.Workspaces.Single(w => w.Id == id).TileSizes;

    [Fact]
    public void Sizes_persist_per_screen()
    {
        var (manager, a) = Started();

        Assert.True(manager.SetTileSizes(a, Left, Uneven).IsSuccess);
        Assert.True(manager.SetTileSizes(a, Right, OtherUneven).IsSuccess);

        Assert.Equal(2, SizesOf(manager.State, a).Count);
        var stored = SizesOf(store.Stored, a);
        Assert.Equal(Uneven, stored.Single(s => s.Screen == Left).Sizes);
        Assert.Equal(OtherUneven, stored.Single(s => s.Screen == Right).Sizes);

        // Setting a screen again replaces its entry rather than adding a second one.
        manager.SetTileSizes(a, Left, OtherUneven);
        Assert.Equal(2, SizesOf(manager.State, a).Count);
        Assert.Equal(OtherUneven, SizesOf(manager.State, a).Single(s => s.Screen == Left).Sizes);
    }

    // Real JSON through JsonPersistenceStore settles that System.Text.Json round-trips a list of records
    // holding nested lists of doubles.
    [Fact]
    public void Sizes_survive_a_real_json_round_trip()
    {
        var id = Guid.NewGuid();
        var state = AppState.Empty with
        {
            Workspaces = [new Workspace(id, "Old", null) { TileSizes = [new ScreenTileSizes(Left, Uneven)] }],
        };
        var jsonStore = new JsonPersistenceStore(dir);
        Assert.True(jsonStore.Save(state).IsSuccess);

        var loaded = SizesOf(jsonStore.Load().Value, id);

        Assert.Equal(Left, loaded.Single().Screen);
        Assert.Equal(Uneven, loaded.Single().Sizes);
    }

    [Fact]
    public void A_state_file_without_sizes_loads_empty()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "state.json"),
            $$$"""{"Workspaces":[{"Id":"{{{Guid.NewGuid()}}}","Name":"Old","DesktopId":null}],"WorkspaceRules":[],"RenameRules":[],"Inventory":{}}""");

        var loaded = new JsonPersistenceStore(dir).Load().Value;

        Assert.Empty(loaded.Workspaces.Single().TileSizes);
    }

    [Fact]
    public void Setting_equal_sizes_removes_the_entry()
    {
        var (manager, a) = Started();
        manager.SetTileSizes(a, Left, Uneven);
        manager.SetTileSizes(a, Right, OtherUneven);

        Assert.True(manager.SetTileSizes(a, Left, TileSizes.Equal([1, 2])).IsSuccess);

        Assert.Equal(Right, SizesOf(manager.State, a).Single().Screen);
        Assert.Equal(Right, SizesOf(store.Stored, a).Single().Screen);
    }

    [Fact]
    public void Evening_out_clears_the_sizes()
    {
        var (manager, a) = Started();
        manager.SetTileSizes(a, Left, Uneven);
        manager.SetTileSizes(a, Right, OtherUneven);

        Assert.True(manager.EvenOutTiles(a).IsSuccess);

        Assert.Empty(SizesOf(manager.State, a));
        Assert.Empty(SizesOf(store.Stored, a));
    }

    [Fact]
    public void Untiling_forgets_the_sizes()
    {
        var (manager, a) = Started();
        manager.SetWorkspaceTiled(a, true);
        manager.SetTileSizes(a, Left, Uneven);

        manager.SetWorkspaceTiled(a, false);

        Assert.Empty(SizesOf(store.Stored, a));
    }

    [Fact]
    public void Tiling_keeps_the_sizes()
    {
        var (manager, a) = Started();
        manager.SetTileSizes(a, Left, Uneven);

        manager.SetWorkspaceTiled(a, true);

        Assert.Single(SizesOf(manager.State, a));
    }

    [Fact]
    public void Turning_the_experiment_off_keeps_the_sizes()
    {
        var (manager, a) = Started();
        manager.SetWorkspaceTiled(a, true);
        manager.SetTileSizes(a, Left, Uneven);
        manager.SetExperimentalTiling(true);

        manager.SetExperimentalTiling(false);

        Assert.Equal(Uneven, SizesOf(manager.State, a).Single().Sizes);
    }

    // Workspace is a record, so its equality must see the sizes, by contents and not by list instance.
    [Fact]
    public void Workspaces_compare_by_their_sizes()
    {
        var id = Guid.NewGuid();
        var bare = new Workspace(id, "W", null);
        var one = bare with { TileSizes = [new ScreenTileSizes(Left, Uneven)] };
        var same = bare with { TileSizes = new[] { new ScreenTileSizes(Left, new TileSizes([0.3, 0.7], [[1.0], [0.25, 0.75]])) }.ToValueList() };
        var other = bare with { TileSizes = [new ScreenTileSizes(Left, OtherUneven)] };

        Assert.Equal(one, same);
        Assert.Equal(one.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(one, other);
        Assert.NotEqual(one, bare);
    }

    [Fact]
    public void An_unknown_workspace_is_a_failure()
    {
        var (manager, _) = Started();

        Assert.True(manager.SetTileSizes(Guid.NewGuid(), Left, Uneven).IsFailure);
        Assert.True(manager.EvenOutTiles(Guid.NewGuid()).IsFailure);
    }
}
