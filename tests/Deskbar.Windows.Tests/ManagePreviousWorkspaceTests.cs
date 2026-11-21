using System.Windows.Controls;
using Deskbar.App;
using Deskbar.Core;
using Deskbar.Core.Persistence;

namespace Deskbar.Windows.Tests;

// Manage's box for the previous workspace's item (AppState.PreviousWorkspaceItem). Against the real window, as
// ManageQuietTaskbarTests is, because what could break is the wiring: the box showing the stored state, unticked by
// default, and a change reaching the manager. What the bar then draws is FloatingBarStripsTests'.
public class ManagePreviousWorkspaceTests
{
    static WorkspaceManager Built(AppState state)
    {
        var desktops = new PulsingDesktops { CurrentId = Guid.NewGuid() };
        var manager = new WorkspaceManager(desktops, new StubMonitor(), new StubTitles(), new StubStore { Stored = state });
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    static ManageWindow Opened(WorkspaceManager manager) =>
        new(manager, compatibilityMode: false, taskbarAutoHides: () => false);

    static CheckBox Box(ManageWindow window) => (CheckBox)window.FindName("PreviousWorkspaceItem")!;

    // Unticked by default, with the wording the user reads, and opening Manage writes nothing.
    [Fact]
    public void The_box_is_unticked_by_default_and_opening_manage_writes_nothing() => StaThread.Run(() =>
    {
        var manager = Built(AppState.Empty);
        var pulses = 0;
        using var counting = manager.StateChanged.Subscribe(_ => pulses++);
        var window = Opened(manager);

        Assert.False(Box(window).IsChecked);
        Assert.Equal("Show the previous workspace on the strips and the taskbar", Box(window).Content);
        Assert.Equal(0, pulses);

        window.Close();
    });

    [Fact]
    public void The_box_shows_what_is_stored() => StaThread.Run(() =>
    {
        var window = Opened(Built(AppState.Empty with { PreviousWorkspaceItem = true }));

        Assert.True(Box(window).IsChecked);

        window.Close();
    });

    // Ticking and unticking reach the manager, one write each.
    [Fact]
    public void Ticking_and_unticking_reach_the_manager_once_each() => StaThread.Run(() =>
    {
        var manager = Built(AppState.Empty);
        var window = Opened(manager);
        var pulses = 0;
        using var counting = manager.StateChanged.Subscribe(_ => pulses++);

        Box(window).IsChecked = true;
        Assert.True(manager.State.PreviousWorkspaceItem);
        Assert.Equal(1, pulses);

        Box(window).IsChecked = false;
        Assert.False(manager.State.PreviousWorkspaceItem);
        Assert.Equal(2, pulses);

        window.Close();
    });
}
