using System.Text.Json;
using Deskbar.Core.Persistence;

namespace Deskbar.Core.Tests;

// Where the current workspace's titled list is drawn: in its own row, at the bottom of the bar, or as a strip
// along the top of every screen, the default. Stored as two bools (WindowListAtBottom and WindowListOffScreens)
// so a build that knows only the first still lands where he was; these tests pin that reading and the setter.
public class WindowListPlaceTests
{
    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();

    WorkspaceManager Started()
    {
        var manager = new WorkspaceManager(desktops, monitor, titles, store);
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    // A fresh install: neither bool is set, so the screens. Petre: "let's make the strip the default for all users".
    [Fact]
    public void Place_reads_screens_by_default() =>
        Assert.Equal(WindowListPlace.Screens, AppState.Empty.WindowListPlace);

    // An existing file has no WindowListOffScreens key, so it lands on the screens too, whatever its bottom flag
    // says: the default reaches every install with the update, which is what was asked for.
    [Fact]
    public void Place_reads_screens_from_an_old_file_that_had_the_bottom()
    {
        var state = JsonSerializer.Deserialize<AppState>(
            """{"Workspaces":[],"WorkspaceRules":[],"RenameRules":[],"Inventory":{},"WindowListAtBottom":true}""");

        Assert.Equal(WindowListPlace.Screens, state!.WindowListPlace);
    }

    // With the strips turned off, the bottom flag decides between the bottom and the row again.
    [Theory]
    [InlineData(true, WindowListPlace.Bottom)]
    [InlineData(false, WindowListPlace.Row)]
    public void Place_with_the_strips_off_reads_the_bottom_flag(bool atBottom, WindowListPlace place) =>
        Assert.Equal(place, (AppState.Empty with { WindowListOffScreens = true, WindowListAtBottom = atBottom }).WindowListPlace);

    [Fact]
    public void Setting_the_same_place_does_not_persist()
    {
        var manager = Started();
        var saves = store.SaveCount;

        Assert.True(manager.SetWindowListPlace(WindowListPlace.Screens).IsSuccess);

        Assert.Equal(saves, store.SaveCount);
    }

    [Fact]
    public void Setting_screens_keeps_the_bottom_choice()
    {
        var manager = Started();
        manager.SetWindowListPlace(WindowListPlace.Bottom);

        manager.SetWindowListPlace(WindowListPlace.Screens);

        Assert.Equal(WindowListPlace.Screens, store.Stored.WindowListPlace);
        Assert.True(store.Stored.WindowListAtBottom);
        Assert.False(store.Stored.WindowListOffScreens);
    }

    // Only the row expands the current workspace's own line; the bottom list and the strips both leave it alone.
    [Theory]
    [InlineData(WindowListPlace.Row, true)]
    [InlineData(WindowListPlace.Bottom, false)]
    [InlineData(WindowListPlace.Screens, false)]
    public void Expand_is_true_only_for_row(WindowListPlace place, bool expand)
    {
        var manager = Started();
        manager.SetWindowListPlace(place);

        Assert.Equal(expand, manager.State.ExpandCurrentWorkspace);
    }
}
