using System.Text.Json;
using Deskbar.Core.Persistence;

namespace Deskbar.Core.Tests;

// Where the current workspace's titled list is drawn: in its own row, at the bottom of the bar, or as a strip
// along the top of every screen. Stored as two bools (WindowListAtBottom and WindowListOnScreens) so a file
// written by any older build keeps its meaning; these tests pin that reading and the setter.
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

    // A fresh install: neither bool is set, so the row.
    [Fact]
    public void Place_reads_row_by_default() =>
        Assert.Equal(WindowListPlace.Row, AppState.Empty.WindowListPlace);

    // A file from before the third place holds only WindowListAtBottom, and must keep meaning the bottom.
    [Fact]
    public void Place_reads_bottom_from_an_old_file()
    {
        var state = JsonSerializer.Deserialize<AppState>(
            """{"Workspaces":[],"WorkspaceRules":[],"RenameRules":[],"Inventory":{},"WindowListAtBottom":true}""");

        Assert.Equal(WindowListPlace.Bottom, state!.WindowListPlace);
    }

    // The screens win over the bottom flag, which is only kept so that another build lands where he was.
    [Fact]
    public void Place_reads_screens_when_both_are_set() =>
        Assert.Equal(WindowListPlace.Screens,
            (AppState.Empty with { WindowListAtBottom = true, WindowListOnScreens = true }).WindowListPlace);

    [Fact]
    public void Setting_the_same_place_does_not_persist()
    {
        var manager = Started();
        var saves = store.SaveCount;

        Assert.True(manager.SetWindowListPlace(WindowListPlace.Row).IsSuccess);

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
        Assert.True(store.Stored.WindowListOnScreens);
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
