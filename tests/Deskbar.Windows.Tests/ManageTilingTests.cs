using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Deskbar.App;
using Deskbar.Core;
using Deskbar.Core.Persistence;

namespace Deskbar.Windows.Tests;

// Manage's tiling settings (docs/superpowers/specs/2026-11-08-tiling-design.md): only the gap between tiles. The
// "Tiling (experimental)" on/off box was removed on 2026-11-19 (Petre: "enable tiling in the settings and remove
// it as an option; it's enough to have a button at the top"), so the gap row is always editable. Against the real window, as ManageSettingsTabTests is, because what
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

    static TextBox GapBox(ManageWindow window) => (TextBox)window.FindName("TileGapBox")!;
    static FrameworkElement GapPanel(ManageWindow window) => (FrameworkElement)window.FindName("TileGapPanel")!;
    static Button GapApply(ManageWindow window) => (Button)window.FindName("TileGapApply")!;

    // No on/off box, and the gap row editable from the start with the default in it.
    [Fact]
    public void There_is_no_tiling_switch_and_the_gap_box_is_editable() => StaThread.Run(() =>
    {
        var window = new ManageWindow(Built(AppState.Empty), compatibilityMode: false);

        Assert.Null(window.FindName("ExperimentalTiling"));
        Assert.True(GapPanel(window).IsEnabled);
        Assert.Equal(AppState.DefaultTileGap.ToString(), GapBox(window).Text);

        window.Close();
    });

    [Fact]
    public void Shows_what_is_stored() => StaThread.Run(() =>
    {
        var window = new ManageWindow(Built(AppState.Empty with { TileGap = 20 }), compatibilityMode: false);

        Assert.Equal("20", GapBox(window).Text);

        window.Close();
    });

    [Fact]
    public void Apply_stores_the_gap() => StaThread.Run(() =>
    {
        var manager = Built(AppState.Empty);
        var window = new ManageWindow(manager, compatibilityMode: false);

        GapBox(window).Text = "12";
        GapApply(window).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        Assert.Equal(12, manager.State.TileGap);

        window.Close();
    });
}
