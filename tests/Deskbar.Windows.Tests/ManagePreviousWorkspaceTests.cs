using System.Windows.Controls;
using Deskbar.App;
using Deskbar.Core;
using Deskbar.Core.Persistence;

namespace Deskbar.Windows.Tests;

// Manage's box for the previous workspace's item (AppState.PreviousWorkspaceItemChoice). Against the real window, as
// ManageQuietTaskbarTests is, because what could break is the wiring: the box showing what the bar draws (the choice,
// or the desk's default), opening Manage recording nothing, and a change reaching the manager. What the bar then draws
// is FloatingBarStripsTests'. Auto-hide is passed in, so nothing here depends on the taskbar of the machine running it.
public class ManagePreviousWorkspaceTests
{
    static WorkspaceManager Built(AppState state)
    {
        var desktops = new PulsingDesktops { CurrentId = Guid.NewGuid() };
        var manager = new WorkspaceManager(desktops, new StubMonitor(), new StubTitles(), new StubStore { Stored = state });
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    static ManageWindow Opened(WorkspaceManager manager, bool autoHide = false) =>
        new(manager, compatibilityMode: false, taskbarAutoHides: () => autoHide);

    static CheckBox Box(ManageWindow window) => (CheckBox)window.FindName("PreviousWorkspaceItem")!;

    // With no choice made the box shows the default: ticked with the list on the screens (a fresh state's place) and the
    // taskbar on auto-hide, unticked without auto-hide. Either way opening Manage writes nothing and records no choice.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void With_no_choice_the_box_shows_the_default_and_opening_manage_writes_nothing(bool autoHide) => StaThread.Run(() =>
    {
        var manager = Built(AppState.Empty);
        var pulses = 0;
        using var counting = manager.StateChanged.Subscribe(_ => pulses++);
        var window = Opened(manager, autoHide);

        Assert.Equal(autoHide, Box(window).IsChecked);
        Assert.Equal("Show the previous workspace on the strips and the taskbar", Box(window).Content);
        Assert.Null(manager.State.PreviousWorkspaceItemChoice);
        Assert.Equal(0, pulses);

        window.Close();
    });

    // With the list in the bar the default is off, auto-hide or not.
    [Fact]
    public void With_the_list_in_the_bar_the_box_is_unticked_by_default() => StaThread.Run(() =>
    {
        var window = Opened(Built(AppState.Empty with { WindowListOffScreens = true }), autoHide: true);

        Assert.False(Box(window).IsChecked);

        window.Close();
    });

    // A stored choice wins over the default.
    [Fact]
    public void The_box_shows_a_stored_choice() => StaThread.Run(() =>
    {
        var window = Opened(Built(AppState.Empty with { PreviousWorkspaceItemChoice = false }), autoHide: true);

        Assert.False(Box(window).IsChecked);

        window.Close();
    });

    // Ticking and unticking reach the manager, one write each, as choices.
    [Fact]
    public void Ticking_and_unticking_reach_the_manager_once_each() => StaThread.Run(() =>
    {
        var manager = Built(AppState.Empty);
        var window = Opened(manager);
        var pulses = 0;
        using var counting = manager.StateChanged.Subscribe(_ => pulses++);

        Box(window).IsChecked = true;
        Assert.True(manager.State.PreviousWorkspaceItemChoice);
        Assert.Equal(1, pulses);

        Box(window).IsChecked = false;
        Assert.False(manager.State.PreviousWorkspaceItemChoice);
        Assert.Equal(2, pulses);

        window.Close();
    });
}
