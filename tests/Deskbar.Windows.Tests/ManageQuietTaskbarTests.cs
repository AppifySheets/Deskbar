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
//
// With no choice stored, the box follows auto-hide (TaskbarFlashing.Wanted). Every window here is given a FIXED
// auto-hide answer, so the tests do not depend on the taskbar of the machine running them.
public class ManageQuietTaskbarTests
{
    static WorkspaceManager Built(AppState state)
    {
        var desktops = new PulsingDesktops { CurrentId = Guid.NewGuid() };
        var manager = new WorkspaceManager(desktops, new StubMonitor(), new StubTitles(), new StubStore { Stored = state });
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    static ManageWindow Opened(WorkspaceManager manager, bool autoHide) =>
        new(manager, compatibilityMode: false, taskbarAutoHides: () => autoHide);

    static CheckBox Box(ManageWindow window) => (CheckBox)window.FindName("QuietTaskbar")!;

    static string Note(ManageWindow window) => ((TextBlock)window.FindName("QuietTaskbarNote")!).Text;

    // The default both ways, and opening Manage records no choice: a stored choice would pin the default for
    // good, so a later change to auto-hide would no longer move it.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void With_no_choice_the_box_follows_auto_hide_and_opening_manage_writes_nothing(bool autoHide) => StaThread.Run(() =>
    {
        var manager = Built(AppState.Empty);
        var pulses = 0;
        using var counting = manager.StateChanged.Subscribe(_ => pulses++);
        var window = Opened(manager, autoHide);

        Assert.Equal(autoHide, Box(window).IsChecked);
        Assert.Equal("Stop apps flashing on Windows' taskbar while Deskbar runs (the bar's amber dot shows them instead)", Box(window).Content);
        Assert.Null(manager.State.QuietTaskbarChoice);
        Assert.Equal(0, pulses);

        window.Close();
    });

    // A choice holds against auto-hide: unticked stays unticked on an auto-hidden taskbar.
    [Fact]
    public void A_stored_choice_wins_over_auto_hide() => StaThread.Run(() =>
    {
        var manager = Built(AppState.Empty with { QuietTaskbarChoice = false });
        var window = Opened(manager, autoHide: true);

        Assert.False(Box(window).IsChecked);
        Assert.Equal(false, manager.State.QuietTaskbarChoice);

        window.Close();
    });

    [Fact]
    public void Shows_what_is_stored_and_a_ticked_box_carries_no_suggestion() => StaThread.Run(() =>
    {
        var window = Opened(Built(AppState.Empty with { QuietTaskbarChoice = true }), autoHide: true);

        Assert.True(Box(window).IsChecked);
        Assert.Equal("", Note(window));

        window.Close();
    });

    // Unticking the default on an auto-hidden taskbar records "no" as a choice, so it holds; ticking it again
    // records "yes". Each is one write.
    [Fact]
    public void Unticking_the_default_and_ticking_it_again_reach_the_manager_once_each() => StaThread.Run(() =>
    {
        var manager = Built(AppState.Empty);
        var window = Opened(manager, autoHide: true);
        var pulses = 0;
        using var counting = manager.StateChanged.Subscribe(_ => pulses++);

        Box(window).IsChecked = false;

        Assert.Equal(false, manager.State.QuietTaskbarChoice);
        Assert.Equal(1, pulses);

        Box(window).IsChecked = true;

        Assert.Equal(true, manager.State.QuietTaskbarChoice);
        Assert.Equal(2, pulses);

        window.Close();
    });

    // On a taskbar that is always shown the default is off, and ticking records "yes".
    [Fact]
    public void Ticking_on_a_taskbar_that_is_always_shown_records_the_choice() => StaThread.Run(() =>
    {
        var manager = Built(AppState.Empty);
        var window = Opened(manager, autoHide: false);

        Box(window).IsChecked = true;

        Assert.Equal(true, manager.State.QuietTaskbarChoice);

        window.Close();
    });
}
