using System.Windows.Controls;
using Deskbar.App;
using Deskbar.Core;
using Deskbar.Core.Persistence;

namespace Deskbar.Windows.Tests;

// Manage's box for holding Windows' taskbar flashing off while Deskbar runs
// (docs/superpowers/specs/2026-11-18-taskbar-flashing-switch-design.md). Against the real window, as
// ManageTilingTests is, because what could break is the wiring: the box showing the state, and a change reaching
// the manager. The window only records the wish; the registry is the app's to write (TaskbarFlashingSwitch), so
// nothing here can change the real setting.
public class ManageQuietTaskbarTests
{
    static WorkspaceManager Built(AppState state)
    {
        var desktops = new PulsingDesktops { CurrentId = Guid.NewGuid() };
        var manager = new WorkspaceManager(desktops, new StubMonitor(), new StubTitles(), new StubStore { Stored = state });
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    static CheckBox Box(ManageWindow window) => (CheckBox)window.FindName("QuietTaskbar")!;

    [Fact]
    public void Off_by_default_and_opening_manage_writes_nothing() => StaThread.Run(() =>
    {
        var manager = Built(AppState.Empty);
        var window = new ManageWindow(manager, compatibilityMode: false);

        Assert.False(Box(window).IsChecked);
        Assert.Equal("Stop apps flashing on Windows' taskbar while Deskbar runs (the bar's amber dot shows them instead)", Box(window).Content);
        Assert.False(manager.State.QuietTaskbar);

        window.Close();
    });

    [Fact]
    public void Shows_what_is_stored_and_a_ticked_box_carries_no_suggestion() => StaThread.Run(() =>
    {
        var window = new ManageWindow(Built(AppState.Empty with { QuietTaskbar = true }), compatibilityMode: false);

        Assert.True(Box(window).IsChecked);
        Assert.Equal("", ((TextBlock)window.FindName("QuietTaskbarNote")!).Text);

        window.Close();
    });

    [Fact]
    public void Ticking_and_unticking_reach_the_manager_once_each() => StaThread.Run(() =>
    {
        var manager = Built(AppState.Empty);
        var window = new ManageWindow(manager, compatibilityMode: false);
        var pulses = 0;
        using var counting = manager.StateChanged.Subscribe(_ => pulses++);

        Box(window).IsChecked = true;

        Assert.True(manager.State.QuietTaskbar);
        Assert.Equal(1, pulses);

        Box(window).IsChecked = false;

        Assert.False(manager.State.QuietTaskbar);
        Assert.Equal(2, pulses);

        window.Close();
    });
}
