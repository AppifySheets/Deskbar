using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.App.Startup;
using Deskbar.Core;
using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;
using Deskbar.Windows.Packaging;
using Deskbar.Windows.Tests.Startup;

namespace Deskbar.Windows.Tests;

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

    // "Start Deskbar with Windows" goes through StartupSwitch, the Run value in the direct build and the
    // package's startup task in the Store copy. These drive it with a fake switch, so no test here can
    // touch the real login entry (StartupRegistrationTests carries why that matters).
    // docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md
    static CheckBox StartBox(ManageWindow window) => (CheckBox)window.FindName("StartWithWindows")!;
    static TextBlock StartNote(ManageWindow window) => (TextBlock)window.FindName("StartupNote")!;

    // Policy (or the user, in Settings) turned the startup task off: the app cannot turn it back on, so the
    // box is greyed out and the note says where it can be turned on.
    [Fact]
    public void A_startup_task_off_by_policy_greys_the_box_and_says_where() => StaThread.Run(() =>
    {
        var fake = new FakeStartupSwitch(StartupAvailability.DisabledByPolicy, enabled: false);
        using var _ = StartupSwitch.Override(fake);

        var window = new ManageWindow(Built(AppState.Empty), compatibilityMode: false);

        Assert.False(StartBox(window).IsChecked);
        Assert.False(StartBox(window).IsEnabled);
        Assert.Equal("Startup is turned off by policy. Turn it on under Settings, Apps, Startup.", StartNote(window).Text);
        Assert.Equal(Visibility.Visible, StartNote(window).Visibility);
        Assert.Equal(0, fake.Enables + fake.Disables);

        window.Close();
    });

    [Fact]
    public void A_startup_task_off_in_settings_greys_the_box_and_says_where() => StaThread.Run(() =>
    {
        using var _ = StartupSwitch.Override(new FakeStartupSwitch(StartupAvailability.DisabledByUser, enabled: false));

        var window = new ManageWindow(Built(AppState.Empty), compatibilityMode: false);

        Assert.False(StartBox(window).IsEnabled);
        Assert.Equal("Startup is turned off in Settings. Turn it on under Settings, Apps, Startup.", StartNote(window).Text);

        window.Close();
    });

    // An administrator turned the startup task ON by policy (EnabledByPolicy): it starts with Windows and the
    // app may not turn it off, so the box is ticked, greyed out, and the note says why. Opening writes nothing.
    [Fact]
    public void A_startup_task_on_by_policy_is_ticked_and_greyed() => StaThread.Run(() =>
    {
        var fake = new FakeStartupSwitch(StartupAvailability.LockedOn, enabled: true);
        using var _ = StartupSwitch.Override(fake);

        var window = new ManageWindow(Built(AppState.Empty), compatibilityMode: false);

        Assert.True(StartBox(window).IsChecked);
        Assert.False(StartBox(window).IsEnabled);
        Assert.Equal("Startup is turned on by policy and cannot be changed here.", StartNote(window).Text);
        Assert.Equal(Visibility.Visible, StartNote(window).Visibility);
        Assert.Equal(0, fake.Enables + fake.Disables);

        window.Close();
    });

    // A state that could not be read is not a reason to lock the box: ticking it may well work.
    [Fact]
    public void An_unreadable_startup_state_leaves_the_box_usable() => StaThread.Run(() =>
    {
        using var _ = StartupSwitch.Override(new FakeStartupSwitch(StartupAvailability.Unavailable, enabled: false));

        var window = new ManageWindow(Built(AppState.Empty), compatibilityMode: false);

        Assert.True(StartBox(window).IsEnabled);
        Assert.Equal("Startup could not be read.", StartNote(window).Text);

        window.Close();
    });

    // Opening the window reads the switch and writes nothing: ticking the box from the stored state raises
    // Checked, and before this guard that call rewrote the Run value every time Manage opened. Unticking it
    // afterwards is the one call.
    [Fact]
    public void An_enabled_startup_is_ticked_and_unticking_disables_it_once() => StaThread.Run(() =>
    {
        // Packaged, so the direct build's Store-copy warning cannot appear on a machine that has the Store copy.
        using var packaged = PackageIdentity.Override(true);
        var fake = new FakeStartupSwitch(StartupAvailability.Available, enabled: true);
        using var _ = StartupSwitch.Override(fake);

        var window = new ManageWindow(Built(AppState.Empty), compatibilityMode: false);

        Assert.True(StartBox(window).IsChecked);
        Assert.True(StartBox(window).IsEnabled);
        Assert.Equal(Visibility.Collapsed, StartNote(window).Visibility);
        Assert.Equal(0, fake.Enables + fake.Disables);

        StartBox(window).IsChecked = false;

        Assert.Equal(1, fake.Disables);
        Assert.Equal(0, fake.Enables);

        window.Close();
    });

    // A failed switch puts the box back to what the switch reports, so the box never claims a state that is
    // not true. Here the user turned the task off in Settings between the window opening and the tick.
    [Fact]
    public void A_failed_enable_puts_the_box_back() => StaThread.Run(() =>
    {
        using var packaged = PackageIdentity.Override(true);
        var fake = new FailingStartupSwitch();
        using var _ = StartupSwitch.Override(fake);

        var window = new ManageWindow(Built(AppState.Empty), compatibilityMode: false);
        StartBox(window).IsChecked = true;

        Assert.False(StartBox(window).IsChecked);
        Assert.Equal(1, fake.Enables);

        window.Close();
    });

    // The note's text on its own, for the case a window test cannot set up: whether the Store copy is
    // installed is a file on the real machine (its execution alias), so it is passed in here.
    [Theory]
    [InlineData(StartupAvailability.Available, false, true, "The Microsoft Store copy of Deskbar starts with Windows. Ticking this starts both.")]
    [InlineData(StartupAvailability.Available, false, false, "")]
    // The Store copy itself has the alias; the warning is only for the direct build beside it.
    [InlineData(StartupAvailability.Available, true, true, "")]
    [InlineData(StartupAvailability.DisabledByPolicy, true, true, "Startup is turned off by policy. Turn it on under Settings, Apps, Startup.")]
    [InlineData(StartupAvailability.DisabledByUser, true, false, "Startup is turned off in Settings. Turn it on under Settings, Apps, Startup.")]
    [InlineData(StartupAvailability.LockedOn, true, true, "Startup is turned on by policy and cannot be changed here.")]
    [InlineData(StartupAvailability.Unavailable, true, false, "Startup could not be read.")]
    public void The_startup_note_says_the_right_thing(StartupAvailability availability, bool packaged, bool storeCopyInstalled, string expected) =>
        Assert.Equal(expected, StartupNoteText.For(availability, packaged, storeCopyInstalled));

    static CheckBox Box(ManageWindow window, string name) => (CheckBox)window.FindName(name)!;

    // "Claude Code sessions keep their tasks in the task list" (AppState.ClaudeTasks) sits under "Teach Claude Code about
    // Deskbar" and only works with it, so it is usable only while that box is ticked, and shows unticked while it is not
    // (the effective value). Unticking the teaching keeps the stored choice: ticking it again brings the box back ticked.
    [Fact]
    public void The_claude_tasks_box_follows_the_teach_box() => StaThread.Run(() =>
    {
        var manager = Built(AppState.Empty);
        var window = new ManageWindow(manager, compatibilityMode: false);
        Assert.True(Box(window, "ClaudeTasks").IsChecked);
        Assert.True(Box(window, "ClaudeTasks").IsEnabled);

        Box(window, "ClaudeTasks").IsChecked = false;
        Assert.False(manager.State.ClaudeTasks);
        Box(window, "ClaudeTasks").IsChecked = true;
        Assert.True(manager.State.ClaudeTasks);

        Box(window, "ClaudeSkill").IsChecked = false;
        Assert.False(Box(window, "ClaudeTasks").IsEnabled);
        Assert.False(Box(window, "ClaudeTasks").IsChecked);
        Assert.True(manager.State.ClaudeTasks);

        Box(window, "ClaudeSkill").IsChecked = true;
        Assert.True(Box(window, "ClaudeTasks").IsEnabled);
        Assert.True(Box(window, "ClaudeTasks").IsChecked);

        window.Close();
    });

    // Opened with the teaching off: the tasks box is greyed and unticked, and opening writes nothing.
    [Fact]
    public void With_teaching_off_the_claude_tasks_box_opens_greyed() => StaThread.Run(() =>
    {
        var manager = Built(AppState.Empty with { NoClaudeSkill = true });
        var pulses = 0;
        using var _ = manager.StateChanged.Subscribe(_ => pulses++);

        var window = new ManageWindow(manager, compatibilityMode: false);

        Assert.False(Box(window, "ClaudeTasks").IsEnabled);
        Assert.False(Box(window, "ClaudeTasks").IsChecked);
        Assert.Equal(0, pulses);
        Assert.True(manager.State.ClaudeTasks);
        window.Close();
    });

    // "Resume the sessions of open tasks after a restart or a crash" (AppState.ClaudeResume) sits under the tasks box and
    // follows it the way the tasks box follows the teaching: usable only while sessions keep tasks, unticked while they
    // do not, and the stored choice kept, so ticking the tasks box again brings it back as it was.
    [Fact]
    public void The_claude_resume_box_follows_the_claude_tasks_box() => StaThread.Run(() =>
    {
        var manager = Built(AppState.Empty);
        var window = new ManageWindow(manager, compatibilityMode: false);
        Assert.True(Box(window, "ClaudeResume").IsChecked);
        Assert.True(Box(window, "ClaudeResume").IsEnabled);

        Box(window, "ClaudeResume").IsChecked = false;
        Assert.False(manager.State.ClaudeResume);
        Box(window, "ClaudeResume").IsChecked = true;
        Assert.True(manager.State.ClaudeResume);

        Box(window, "ClaudeTasks").IsChecked = false;
        Assert.False(Box(window, "ClaudeResume").IsEnabled);
        Assert.False(Box(window, "ClaudeResume").IsChecked);
        Assert.True(manager.State.ClaudeResume);

        Box(window, "ClaudeTasks").IsChecked = true;
        Assert.True(Box(window, "ClaudeResume").IsEnabled);
        Assert.True(Box(window, "ClaudeResume").IsChecked);

        // The teaching off takes the tasks off, and so the resume too.
        Box(window, "ClaudeSkill").IsChecked = false;
        Assert.False(Box(window, "ClaudeResume").IsEnabled);
        Assert.False(Box(window, "ClaudeResume").IsChecked);
        Assert.True(manager.State.ClaudeResume);

        window.Close();
    });

    // Opened with the resume off: unticked but usable, and opening writes nothing.
    [Fact]
    public void With_resume_off_the_claude_resume_box_opens_unticked() => StaThread.Run(() =>
    {
        var manager = Built(AppState.Empty with { NoClaudeResume = true });
        var pulses = 0;
        using var _ = manager.StateChanged.Subscribe(_ => pulses++);

        var window = new ManageWindow(manager, compatibilityMode: false);

        Assert.True(Box(window, "ClaudeResume").IsEnabled);
        Assert.False(Box(window, "ClaudeResume").IsChecked);
        Assert.Equal(0, pulses);
        window.Close();
    });

    // Ruling 20: when Claude Code's list of sessions cannot be read, the reason shows in one line under the resume box,
    // from the moment the window opens, and goes when a read works again.
    [Fact]
    public void An_unreadable_Claude_list_shows_under_the_resume_box_until_it_reads_again() => StaThread.Run(() =>
    {
        var manager = Built(AppState.Empty);
        var problem = new System.Reactive.Subjects.BehaviorSubject<Maybe<string>>("Claude Code's list of sessions cannot be read, so nothing is resumed: Could not run claude.");

        var window = new ManageWindow(manager, compatibilityMode: false, claudeListProblem: problem);
        var line = (TextBlock)window.FindName("ClaudeListProblem")!;

        Assert.Equal(Visibility.Visible, line.Visibility);
        Assert.Contains("Could not run claude.", line.Text);

        problem.OnNext(Maybe<string>.None);
        Assert.Equal(Visibility.Collapsed, line.Visibility);

        problem.OnNext("Claude Code's list of sessions cannot be read, so nothing is resumed: not JSON.");
        Assert.Equal(Visibility.Visible, line.Visibility);

        // Closed, the window stops listening.
        window.Close();
        Assert.False(problem.HasObservers);
    });

    // Without a source (the tests that build Manage without App), the line stays hidden.
    [Fact]
    public void Without_Claude_sessions_the_list_line_stays_hidden() => StaThread.Run(() =>
    {
        var window = new ManageWindow(Built(AppState.Empty), compatibilityMode: false);

        Assert.Equal(Visibility.Collapsed, ((TextBlock)window.FindName("ClaudeListProblem")!).Visibility);
        window.Close();
    });

    // Reports Available and off, and refuses to enable, as the startup task does once the user has turned
    // it off in Settings after the window read it.
    sealed class FailingStartupSwitch : IStartupSwitch
    {
        public bool IsEnabled => false;
        public StartupAvailability Availability => StartupAvailability.Available;
        public int Enables { get; private set; }

        public Result Enable()
        {
            Enables++;
            return Result.Failure("the startup task is DisabledByUser");
        }

        public Result Disable() => Result.Success();
    }
}
