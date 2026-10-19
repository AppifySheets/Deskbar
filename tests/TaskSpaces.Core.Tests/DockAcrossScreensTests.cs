namespace TaskSpaces.Core.Tests;

// Docking across the line between two screens (Petre: "so left half is on the left, right on the right, as an
// option"). A setting the bar follows, like docking itself, so the menu, Manage and `deskbar.cmd bar across`
// all go through one path.
public class DockAcrossScreensTests
{
    WorkspaceManager Started()
    {
        var manager = new WorkspaceManager(new FakeDesktops(), new FakeMonitor(), new FakeTitles(), new FakeStore());
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    [Fact]
    public void It_is_off_by_default() =>
        Assert.False(Started().State.DockAcrossScreens);

    // The seam means nothing without docking, so asking for it turns docking on in the same write.
    [Fact]
    public void Asking_for_the_seam_turns_docking_on()
    {
        var manager = Started();
        Assert.True(manager.SetDockAcrossScreens(true).IsSuccess);
        Assert.True(manager.State.DockAcrossScreens);
        Assert.True(manager.State.DockBar);
    }

    // Back to one edge keeps the bar docked: it is a choice of WHERE, not whether.
    [Fact]
    public void Going_back_to_one_edge_stays_docked()
    {
        var manager = Started();
        manager.SetDockAcrossScreens(true);
        Assert.True(manager.SetDockAcrossScreens(false).IsSuccess);
        Assert.False(manager.State.DockAcrossScreens);
        Assert.True(manager.State.DockBar);
    }
}
