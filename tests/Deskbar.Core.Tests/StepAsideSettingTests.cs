using Deskbar.Core.Persistence;

namespace Deskbar.Core.Tests;

// The setting that lets a full-screen window cover the floating bar (AppState.StepAsideForFullScreen).
// Off by default: the bar is on top of everything until somebody asks for this.
public sealed class StepAsideSettingTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), $"deskbar-tests-{Guid.NewGuid():N}");
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();

    public void Dispose() { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }

    WorkspaceManager Started()
    {
        var manager = new WorkspaceManager(desktops, monitor, titles, store);
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    [Fact]
    public void It_is_off_by_default() =>
        Assert.False(Started().State.StepAsideForFullScreen);

    // A state.json written before the setting existed has no such field and loads as off.
    [Fact]
    public void A_state_file_without_the_setting_loads_with_it_off()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "state.json"),
            """{"Workspaces":[],"WorkspaceRules":[],"RenameRules":[],"Inventory":{}}""");

        Assert.False(new JsonPersistenceStore(dir).Load().Value.StepAsideForFullScreen);
    }

    // Saved, and the same value again writes nothing: Manage sets the box from state when it opens, which
    // raises Checked and lands here.
    [Fact]
    public void It_is_saved_and_the_same_value_again_writes_nothing()
    {
        var manager = Started();

        Assert.True(manager.SetStepAsideForFullScreen(true).IsSuccess);
        Assert.True(manager.State.StepAsideForFullScreen);
        Assert.True(store.Stored.StepAsideForFullScreen);

        var saves = store.SaveCount;
        manager.SetStepAsideForFullScreen(true);
        Assert.Equal(saves, store.SaveCount);
    }
}
