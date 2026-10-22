using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using TaskSpaces.App;
using TaskSpaces.Core;
using TaskSpaces.Core.Domain;
using TaskSpaces.Core.Persistence;

namespace TaskSpaces.Windows.Tests;

// The Settings tab (#151). Petre: "make things configurable, like dimming opacity, timeout, etc."
//
// Against the real window, because what could break here is the wiring rather than the arithmetic:
// controls populated from state, a change reaching the manager, and above all a window that can be
// OPENED without writing anything. That last one is not hypothetical -- setting four sliders from state
// raises four change events, and if they were taken at face value merely opening Manage would rewrite
// state.json and rebuild the bar.
public class ManageSettingsTabTests
{
    static WorkspaceManager Built(AppState state)
    {
        var desktops = new PulsingDesktops { CurrentId = Guid.NewGuid() };
        var manager = new WorkspaceManager(desktops, new StubMonitor(), new StubTitles(), new StubStore { Stored = state });
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    static Slider SliderNamed(ManageWindow window, string name) => (Slider)window.FindName(name)!;
    static CheckBox Inherit(ManageWindow window) => (CheckBox)window.FindName("InheritHoverDwell")!;
    static string ValueText(ManageWindow window, string name) => ((TextBlock)window.FindName(name)!).Text;

    [Fact]
    public void The_sliders_show_what_is_stored() => StaThread.Run(() =>
    {
        var manager = Built(AppState.Empty with
        {
            BarIdleOpacity = 0.5,
            BarFadeGraceSeconds = 20,
            BarFadeDurationMs = 2500,
            HoverDwellMs = 300,
        });

        var window = new ManageWindow(manager, compatibilityMode: false);

        Assert.Equal(0.5, SliderNamed(window, "IdleOpacitySlider").Value);
        Assert.Equal(20, SliderNamed(window, "FadeGraceSlider").Value);
        // Shown in seconds, stored in milliseconds: seconds is how the value is thought about.
        Assert.Equal(2.5, SliderNamed(window, "FadeDurationSlider").Value);
        Assert.Equal(300, SliderNamed(window, "HoverDwellSlider").Value);

        window.Close();
    });

    [Fact]
    public void With_nothing_stored_they_show_the_defaults() => StaThread.Run(() =>
    {
        var window = new ManageWindow(Built(AppState.Empty), compatibilityMode: false);

        Assert.Equal(BarFading.Default, SliderNamed(window, "IdleOpacitySlider").Value);
        Assert.Equal(BarFading.GraceDefault, SliderNamed(window, "FadeGraceSlider").Value);
        Assert.Equal(BarFading.DurationDefaultMs / 1000, SliderNamed(window, "FadeDurationSlider").Value);

        window.Close();
    });

    // The bottom strip's boxes (Petre: "make it a setting, what to show"): ticked from state, and opening the
    // window with items hidden writes nothing, the same promise the sliders keep.
    [Fact]
    public void The_strip_boxes_show_what_is_hidden_and_opening_stores_nothing() => StaThread.Run(() =>
    {
        var manager = Built(AppState.Empty with { HiddenStripItems = [StripItem.QuickSettings, StripItem.Notifications] });
        var pulses = 0;
        using var _ = manager.StateChanged.Subscribe(_ => pulses++);

        var window = new ManageWindow(manager, compatibilityMode: false);
        var boxes = StripBoxes(window);

        Assert.Equal(Enum.GetValues<StripItem>().Length, boxes.Count);
        Assert.False(boxes[StripItem.QuickSettings].IsChecked);
        Assert.False(boxes[StripItem.Notifications].IsChecked);
        Assert.True(boxes[StripItem.Language].IsChecked);
        Assert.True(boxes[StripItem.Clock].IsChecked);
        Assert.Equal(0, pulses);

        window.Close();
    });

    // Unticking hides the item at once, ticking brings it back, and Reset shows everything.
    [Fact]
    public void Unticking_a_strip_box_hides_that_item() => StaThread.Run(() =>
    {
        var manager = Built(AppState.Empty);
        var window = new ManageWindow(manager, compatibilityMode: false);
        var boxes = StripBoxes(window);

        boxes[StripItem.Notifications].IsChecked = false;
        boxes[StripItem.QuickSettings].IsChecked = false;
        Assert.Equal([StripItem.QuickSettings, StripItem.Notifications], manager.State.HiddenStripItems);

        boxes[StripItem.QuickSettings].IsChecked = true;
        Assert.Equal([StripItem.Notifications], manager.State.HiddenStripItems);

        ((Button)window.FindName("ResetAppearanceButton")!).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Empty(manager.State.HiddenStripItems);
        Assert.True(StripBoxes(window)[StripItem.Notifications].IsChecked);

        window.Close();
    });

    // The icon size slider: in percent, from state, saved as a factor, and cleared by Reset.
    [Fact]
    public void The_icon_size_slider_shows_and_saves_the_size() => StaThread.Run(() =>
    {
        var manager = Built(AppState.Empty with { IconScale = 1.25 });
        var window = new ManageWindow(manager, compatibilityMode: false);
        var slider = SliderNamed(window, "IconSizeSlider");
        Assert.Equal(125, slider.Value);

        slider.Value = 80;
        Assert.Equal(0.8, manager.State.IconScale!.Value, 3);

        ((Button)window.FindName("ResetAppearanceButton")!).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Null(manager.State.IconScale);
        Assert.Equal(100, SliderNamed(window, "IconSizeSlider").Value);

        window.Close();
    });

    // Petre: "settings page can't be scrolled and it doesn't fit on a page in smaller res screens".
    [Fact]
    public void The_settings_scroll() => StaThread.Run(() =>
    {
        var window = new ManageWindow(Built(AppState.Empty), compatibilityMode: false);
        var scroll = (ScrollViewer)window.FindName("SettingsScroll")!;

        Assert.Equal(ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);
        // The controls are inside it, the reset button is not, so the way back never scrolls out of view. By the
        // LOGICAL tree: the window is never shown here, so the tab's visual tree does not exist yet.
        Assert.True(Inside((FrameworkElement)window.FindName("IdleOpacitySlider")!, scroll));
        Assert.True(Inside((FrameworkElement)window.FindName("StripItemsPanel")!, scroll));
        Assert.False(Inside((FrameworkElement)window.FindName("ResetAppearanceButton")!, scroll));

        window.Close();
    });

    static bool Inside(DependencyObject element, DependencyObject container) =>
        LogicalTreeHelper.GetParent(element) is { } parent && (parent == container || Inside(parent, container));

    static Dictionary<StripItem, CheckBox> StripBoxes(ManageWindow window) =>
        ((Panel)window.FindName("StripItemsPanel")!).Children.OfType<CheckBox>().ToDictionary(box => (StripItem)box.Tag);

    // The one that matters: opening the window must not be a change.
    [Fact]
    public void Opening_the_window_stores_nothing() => StaThread.Run(() =>
    {
        var manager = Built(AppState.Empty);
        var pulses = 0;
        using var _ = manager.StateChanged.Subscribe(_ => pulses++);

        var window = new ManageWindow(manager, compatibilityMode: false);

        Assert.Equal(0, pulses);
        Assert.Null(manager.State.BarIdleOpacity);

        window.Close();
    });

    [Fact]
    public void Moving_a_slider_stores_the_new_value() => StaThread.Run(() =>
    {
        var manager = Built(AppState.Empty);
        var window = new ManageWindow(manager, compatibilityMode: false);

        SliderNamed(window, "IdleOpacitySlider").Value = 0.6;

        Assert.Equal(0.6, manager.State.BarIdleOpacity);

        window.Close();
    });

    [Fact]
    public void The_duration_slider_stores_milliseconds() => StaThread.Run(() =>
    {
        var manager = Built(AppState.Empty);
        var window = new ManageWindow(manager, compatibilityMode: false);

        SliderNamed(window, "FadeDurationSlider").Value = 2;

        Assert.Equal(2000, manager.State.BarFadeDurationMs);

        window.Close();
    });

    // Inheriting is the default, and it is stored as null rather than as Windows' number, so the bar
    // keeps following the OS if that setting later changes.
    [Fact]
    public void Inheriting_windows_hover_time_is_the_default_and_stores_nothing() => StaThread.Run(() =>
    {
        var manager = Built(AppState.Empty);
        var window = new ManageWindow(manager, compatibilityMode: false);

        Assert.True(Inherit(window).IsChecked);
        Assert.Null(manager.State.HoverDwellMs);
        // The slider is dead while the box is ticked, or it would offer a choice that does nothing.
        Assert.False(SliderNamed(window, "HoverDwellSlider").IsEnabled);

        window.Close();
    });

    [Fact]
    public void Unticking_inherit_stores_the_sliders_value() => StaThread.Run(() =>
    {
        var manager = Built(AppState.Empty);
        var window = new ManageWindow(manager, compatibilityMode: false);
        SliderNamed(window, "HoverDwellSlider").Value = 300;

        Inherit(window).IsChecked = false;

        Assert.Equal(300, manager.State.HoverDwellMs);
        Assert.True(SliderNamed(window, "HoverDwellSlider").IsEnabled);

        window.Close();
    });

    [Fact]
    public void A_stored_dwell_shows_as_not_inheriting() => StaThread.Run(() =>
    {
        var window = new ManageWindow(Built(AppState.Empty with { HoverDwellMs = 250 }), compatibilityMode: false);

        Assert.False(Inherit(window).IsChecked);

        window.Close();
    });

    // Reset has to be expressible or the tab is a one-way door, and it clears rather than writing
    // today's numbers, so a later change of default still reaches anyone who pressed it.
    [Fact]
    public void Reset_clears_every_stored_value() => StaThread.Run(() =>
    {
        var manager = Built(AppState.Empty with
        {
            BarIdleOpacity = 0.5, BarFadeGraceSeconds = 20, BarFadeDurationMs = 2500, HoverDwellMs = 300,
        });
        var window = new ManageWindow(manager, compatibilityMode: false);

        ((Button)window.FindName("ResetAppearanceButton")!).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        Assert.Null(manager.State.BarIdleOpacity);
        Assert.Null(manager.State.BarFadeGraceSeconds);
        Assert.Null(manager.State.BarFadeDurationMs);
        Assert.Null(manager.State.HoverDwellMs);
        // And the controls follow, or the tab would show numbers that are no longer stored.
        Assert.Equal(BarFading.Default, SliderNamed(window, "IdleOpacitySlider").Value);
        Assert.True(Inherit(window).IsChecked);

        window.Close();
    });

    // Opacity 1.0 is not "100%", it is the absence of the feature, and the readout is the only place
    // anyone finds out that this is where "stop dimming" lives.
    [Fact]
    public void Full_opacity_reads_as_never_dimming() => StaThread.Run(() =>
    {
        var window = new ManageWindow(Built(AppState.Empty with { BarIdleOpacity = 1.0 }), compatibilityMode: false);

        Assert.Equal("never dims", ValueText(window, "IdleOpacityValue"));

        window.Close();
    });

    [Fact]
    public void A_zero_grace_reads_as_at_once() => StaThread.Run(() =>
    {
        var window = new ManageWindow(Built(AppState.Empty with { BarFadeGraceSeconds = 0 }), compatibilityMode: false);

        Assert.Equal("at once", ValueText(window, "FadeGraceValue"));

        window.Close();
    });
}
