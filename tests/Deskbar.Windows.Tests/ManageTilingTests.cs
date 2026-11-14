using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Deskbar.App;
using Deskbar.Core;
using Deskbar.Core.Persistence;

namespace Deskbar.Windows.Tests;

// Manage's tiling settings (docs/superpowers/specs/2026-11-08-tiling-design.md): the experiment's master switch,
// off by default (Petre: "disabled by default, until i have tested it well"), and the gap between tiles, which
// can only be edited while the switch is on. Against the real window, as ManageSettingsTabTests is, because what
// could break is the wiring: the controls showing state, and a change reaching the manager.
public class ManageTilingTests
{
    static WorkspaceManager Built(AppState state)
    {
        var desktops = new PulsingDesktops { CurrentId = Guid.NewGuid() };
        var manager = new WorkspaceManager(desktops, new StubMonitor(), new StubTitles(), new StubStore { Stored = state });
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    static CheckBox Switch(ManageWindow window) => (CheckBox)window.FindName("ExperimentalTiling")!;
    static TextBox GapBox(ManageWindow window) => (TextBox)window.FindName("TileGapBox")!;
    static FrameworkElement GapPanel(ManageWindow window) => (FrameworkElement)window.FindName("TileGapPanel")!;
    static Button GapApply(ManageWindow window) => (Button)window.FindName("TileGapApply")!;

    [Fact]
    public void Off_by_default_with_the_gap_box_greyed_out() => StaThread.Run(() =>
    {
        var manager = Built(AppState.Empty);
        var window = new ManageWindow(manager, compatibilityMode: false);

        Assert.False(Switch(window).IsChecked);
        Assert.Equal("Tiling (experimental)", Switch(window).Content);
        Assert.False(GapPanel(window).IsEnabled);
        Assert.Equal(AppState.DefaultTileGap.ToString(), GapBox(window).Text);
        // Opening Manage writes nothing.
        Assert.False(manager.State.ExperimentalTiling);

        window.Close();
    });

    [Fact]
    public void Shows_what_is_stored() => StaThread.Run(() =>
    {
        var window = new ManageWindow(Built(AppState.Empty with { ExperimentalTiling = true, TileGap = 20 }), compatibilityMode: false);

        Assert.True(Switch(window).IsChecked);
        Assert.True(GapPanel(window).IsEnabled);
        Assert.Equal("20", GapBox(window).Text);

        window.Close();
    });

    [Fact]
    public void Ticking_the_box_turns_the_experiment_on_and_frees_the_gap_box() => StaThread.Run(() =>
    {
        var manager = Built(AppState.Empty);
        var window = new ManageWindow(manager, compatibilityMode: false);

        Switch(window).IsChecked = true;

        Assert.True(manager.State.ExperimentalTiling);
        Assert.True(GapPanel(window).IsEnabled);

        Switch(window).IsChecked = false;

        Assert.False(manager.State.ExperimentalTiling);
        Assert.False(GapPanel(window).IsEnabled);

        window.Close();
    });

    [Fact]
    public void Apply_stores_the_gap() => StaThread.Run(() =>
    {
        var manager = Built(AppState.Empty with { ExperimentalTiling = true });
        var window = new ManageWindow(manager, compatibilityMode: false);

        GapBox(window).Text = "12";
        GapApply(window).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        Assert.Equal(12, manager.State.TileGap);

        window.Close();
    });
}
