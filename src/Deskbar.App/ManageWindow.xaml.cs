using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CSharpFunctionalExtensions;
using Deskbar.Core;
using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;
using Deskbar.Core.Time;
using Deskbar.App.Startup;
using Deskbar.Windows.Packaging;

namespace Deskbar.App;

// Code-behind, not MVVM: a settings window, no view-state worth abstracting. Every handler
// is a thin adapter onto WorkspaceManager, which owns all behavior (and all the tests).
public partial class ManageWindow : Window
{
    readonly WorkspaceManager manager;

    // #53: read-only here. Manage shows what has been tracked; the tracker itself is owned by App,
    // which is where the timer that feeds it lives. Optional so the two tests that construct this
    // window without a tracker keep working, and so a compatibility-mode start has one less thing
    // that must exist.
    readonly TimeTracker? timeTracker;

    // Whether Windows' taskbar auto-hides, which decides what the flashing box shows while nobody has made a
    // choice with it (TaskbarFlashing.Wanted). The real reading by default; the tests pass a fixed answer, so
    // what they assert does not depend on the taskbar of the machine running them.
    readonly Func<bool> taskbarAutoHides;

    // `pro` is what the Deskbar Pro tab's buttons do (ManageWindow.Pro.cs). App always passes it; it is optional only
    // so the tests that build this window without App keep their shape.
    //
    // `claudeListProblem` is ClaudeSessions.ListProblem (Ruling 20): why Claude Code's list of sessions cannot be read,
    // shown in one line under the resume box while it lasts. Optional for the same reason as `pro`.
    public ManageWindow(WorkspaceManager manager, bool compatibilityMode, TimeTracker? timeTracker = null, Func<bool>? taskbarAutoHides = null,
        ProActions? pro = null, IObservable<Maybe<string>>? claudeListProblem = null)
    {
        this.manager = manager;
        this.timeTracker = timeTracker;
        this.pro = pro;
        this.taskbarAutoHides = taskbarAutoHides ?? (() => TaskbarFlashingSwitch.TaskbarAutoHides);
        InitializeComponent();
        if (compatibilityMode) CompatBanner.Visibility = Visibility.Visible;
        ShowStartup();
        // Raises Checked, which lands in OnCheckForUpdatesToggled -- harmless, because
        // SetCheckForUpdates ignores a value that has not changed rather than writing state.json
        // every time this window opens.
        CheckForUpdates.IsChecked = manager.State.CheckForUpdates;
        // The Store copy checks the Microsoft Store, not GitHub (Store packaging design:
        // docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md), so the box says so. The
        // XAML keeps the direct build's wording, whose "never installs anything" would be untrue here:
        // saying yes to the Store's offer does install. The one setting governs both checks.
        if (PackageIdentity.IsPackaged)
            CheckForUpdates.Content = "Check the Microsoft Store for new versions";
        // The cadence box (task 5e1e1a12): same harmless Checked as above, SetUpdateEveryRelease ignores an unchanged
        // value. Collapsed in the Store copy, where the Store decides when an update arrives and this rule never runs.
        UpdateEveryRelease.IsChecked = manager.State.UpdateEveryRelease;
        if (PackageIdentity.IsPackaged)
        {
            UpdateEveryRelease.Visibility = Visibility.Collapsed;
            UpdateEveryReleaseNote.Visibility = Visibility.Collapsed;
        }
        // Same pattern, and the same reason it is harmless: SetDockBar ignores an unchanged value.
        DockBar.IsChecked = manager.State.DockBar;
        // Same again: SetClaudeSkill ignores an unchanged value.
        ClaudeSkill.IsChecked = manager.State.ClaudeSkill;
        // ...and the task box under it, which follows it (ShowClaudeTasks). Called here as well as from the skill box's
        // handler, because an unticked skill box raises no Unchecked when the window opens.
        ShowClaudeTasks();
        // ...and SetFocusRing.
        FocusRing.IsChecked = manager.State.FocusRing;
        // ...and SetSeeThrough.
        SeeThrough.IsChecked = manager.State.SeeThrough;
        // ...and SetPreviousWorkspaceItem, which, like SetQuietTaskbar, compares with what the box SHOWS, so a box
        // ticked by the default (list on the screens, taskbar on auto-hide) records no choice when Manage opens.
        PreviousWorkspaceItem.IsChecked = manager.PreviousWorkspaceItemShown(this.taskbarAutoHides);
        // And again: SetQuietTaskbar ignores a value that matches what the box shows, so opening the window with no
        // choice made and the box ticked by default (an auto-hidden taskbar) records nothing. The note is set here
        // as well as in the handler, because an unticked box raises no Checked when the window opens.
        QuietTaskbar.IsChecked = Deskbar.Core.Shell.TaskbarFlashing.Wanted(manager.State.QuietTaskbarChoice, this.taskbarAutoHides());
        ShowQuietTaskbarNote();
        // Tiling's one setting, the gap: the box shows the stored number, and nothing is written until Apply.
        TileGapBox.Text = manager.State.TileGap.ToString();
        // #82. Read from the assembly at runtime, never written as a literal here: the whole reason
        // the question needs answering in the UI is that #71 leaves several versioned exes side by
        // side, and a number typed into the source would report the build it was written in rather
        // than the one running.
        AppVersion.Text = $"Version {UpdateService.DisplayVersion}";
        // #151. Before Reload(), so the tab is populated whether or not the window is ever switched to
        // it, and so the guard flag it sets is honoured from the first raise.
        LoadAppearance();
        Reload();
        // The Deskbar Pro tab, painted now and again when the Store's price arrives (ManageWindow.Pro.cs).
        LoadPro();
        // Ruling 20. ClaudeSessions publishes on the UI thread, where this window lives, and gives its current value
        // first, so the line is right from the moment the window opens; the subscription ends with the window.
        if (claudeListProblem is not null)
        {
            var watch = claudeListProblem.Subscribe(ShowClaudeListProblem);
            Closed += (_, _) => watch.Dispose();
        }
    }

    void ShowClaudeListProblem(Maybe<string> problem)
    {
        ClaudeListProblem.Text = problem.GetValueOrDefault("");
        ClaudeListProblem.Visibility = problem.HasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    // Fills the Time tab and the shortcut boxes from state. Run when the window opens and after Apply or
    // Reset on a shortcut.
    void Reload()
    {
        ReloadTime();

        // Reads through WorkspaceManager, not AppState, so the box shows what is actually
        // BOUND -- including the fallback to the default when state.json holds something
        // unusable. Assigning Text raises TextChanged, which validates it for free.
        SwitcherShortcutBox.Text = manager.SwitcherShortcut;
        QuickFindShortcutBox.Text = manager.QuickFindShortcut;
    }

    // --- Shortcuts tab ------------------------------------------------------------------
    // Petre: "i want it configurable".

    // Validated as it is typed, which is precisely what Chord.Parse's Result was built for:
    // "so the editor UI can validate what someone typed BEFORE anything tries to register
    // it". Nothing is bound here -- this only says whether Apply would work.
    void OnSwitcherShortcutTyped(object s, RoutedEventArgs e) =>
        Chord.Parse(SwitcherShortcutBox.Text).Match(
            chord => Status(Describe(chord), ok: true),
            error => Status(error, ok: false));

    // Says what the chord will DO, not just that it parsed: a chord that already contains
    // Shift has no free modifier left to mean "backwards", so the walk becomes forward-only.
    // Better to say so here than to let it be discovered as a missing feature.
    static string Describe(Chord chord) =>
        (chord.Modifiers & Chord.Shift) == 0
            ? $"Hold {chord.ModifiersText}, tap {chord.KeyText} to walk forwards, add Shift to walk backwards."
            : $"Hold {chord.ModifiersText}, tap {chord.KeyText} to walk. Forwards only: Shift is already part of the chord, so it cannot also mean \"backwards\".";

    void Status(string message, bool ok)
    {
        SwitcherShortcutStatus.Text = message;
        SwitcherShortcutStatus.Foreground = ok ? SystemColors.GrayTextBrush : Brushes.Firebrick;
    }

    // Persisting is all this does. App re-registers off StateChanged, so the new chord is
    // live immediately and a chord another app already owns reports itself from there.
    void OnApplySwitcherShortcut(object s, RoutedEventArgs e) =>
        Report(manager.SetSwitcherShortcut(SwitcherShortcutBox.Text).Map(() => true)).Tap(Reload);

    void OnResetSwitcherShortcut(object s, RoutedEventArgs e) =>
        Report(manager.SetSwitcherShortcut(AppState.DefaultSwitcherShortcut).Map(() => true)).Tap(Reload);

    // Quick find's chord, edited the same way. The manager refuses the switcher's chord (and its Shift
    // variant), and says so in the same report as any other refusal.
    void OnQuickFindShortcutTyped(object s, RoutedEventArgs e) =>
        Chord.Parse(QuickFindShortcutBox.Text).Match(
            chord => QuickFindStatus($"Press {chord} to open quick find.", ok: true),
            error => QuickFindStatus(error, ok: false));

    void QuickFindStatus(string message, bool ok)
    {
        QuickFindShortcutStatus.Text = message;
        QuickFindShortcutStatus.Foreground = ok ? SystemColors.GrayTextBrush : Brushes.Firebrick;
    }

    void OnApplyQuickFindShortcut(object s, RoutedEventArgs e) =>
        Report(manager.SetQuickFindShortcut(QuickFindShortcutBox.Text).Map(() => true)).Tap(Reload);

    void OnResetQuickFindShortcut(object s, RoutedEventArgs e) =>
        Report(manager.SetQuickFindShortcut(AppState.DefaultQuickFindShortcut).Map(() => true)).Tap(Reload);

    // --- tracked time (#53) -----------------------------------------------------------------

    // Each duration twice over: once formatted for the eye, once as the TimeSpan the column SORTS by.
    //
    // Petre: "time should be sortable correctly, not by text." The columns bound to the formatted
    // strings, so the grid compared them as text -- which puts "1h 45m" above "47m" because '1' sorts
    // before '4', and reads as the sort being broken rather than as being asked the wrong question.
    // The XAML now points each column's SortMemberPath at these, and a TimeSpan compares as a
    // duration. Keeping the pair means the format stays free to change without dragging the ordering
    // with it, which is what caused this.
    public sealed class TimeRow
    {
        public required string Workspace { get; init; }
        // The group this workspace belongs to, or empty. Petre: "workspace should have group name."
        // Its own column rather than a prefix on the name: it sorts, so a group's rows can be brought
        // together, and it never has to be picked back out of a composed string.
        public required string Group { get; init; }
        public required string Today { get; init; }
        public required string ThisWeek { get; init; }
        public required string LastMonth { get; init; }
        public required TimeSpan TodayTime { get; init; }
        public required TimeSpan ThisWeekTime { get; init; }
        public required TimeSpan LastMonthTime { get; init; }
    }

    readonly ObservableCollection<TimeRow> times = [];

    void ReloadTime()
    {
        TimeGrid.ItemsSource ??= times;
        times.Clear();

        if (timeTracker is null)
        {
            TimeStatus.Text = "Time tracking is not running.";
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        // The week Petre is IN, not the last seven days: "this week" means since Monday to
        // everyone who says it, and a rolling window would make Monday morning read as a full week
        // of work carried over from the last one.
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));

        manager.State.Workspaces.ToList().ForEach(w =>
        {
            var todayTime = timeTracker.Time.On(w.Id, today);
            var weekTime = timeTracker.Time.Between(w.Id, monday, today);
            var monthTime = timeTracker.Time.Between(w.Id, today.AddDays(-29), today);

            times.Add(new TimeRow
            {
                Workspace = w.Name,
                Group = manager.State.GroupOf(w.Id)?.Name ?? "",
                Today = Format(todayTime),
                ThisWeek = Format(weekTime),
                LastMonth = Format(monthTime),
                TodayTime = todayTime,
                ThisWeekTime = weekTime,
                LastMonthTime = monthTime,
            });
        });

        // Says what the numbers cannot: a fresh install shows zeroes everywhere, and without this
        // line that is indistinguishable from tracking being broken.
        TimeStatus.Text = times.All(t => t.LastMonthTime == TimeSpan.Zero)
            ? "Nothing tracked yet. Time accrues while you work in a workspace, in 15-second steps, and is written to time.json beside state.json."
            : $"Counted in 15-second steps while you are active; idle after {ActivityAccrual.IdleAfter.TotalMinutes:0} minutes without input.";
    }

    // Hours and minutes, never seconds: the numbers here are answers to "where did my day go", and
    // a seconds column would invite a precision the 15-second granularity cannot support.
    static string Format(TimeSpan time) =>
        time < TimeSpan.FromMinutes(1) ? "—"
        : time < TimeSpan.FromHours(1) ? $"{time.TotalMinutes:0}m"
        : $"{(int)time.TotalHours}h {time.Minutes:00}m";

    // True while ShowStartup is setting the box from the switch, so the Checked/Unchecked that raises is not
    // taken for the user flipping it. Before this guard, opening Manage with autostart on called Enable,
    // which re-pointed the Run value at whichever build opened the window, a development copy included.
    bool showingStartup;

    // Sets the start-with-Windows box and its note from StartupSwitch.Current (the Run value in the direct
    // build, the package's startup task in the Store copy). When Settings or a policy turned the startup
    // task off, the app is not allowed to turn it back on, so the box is unticked and greyed out and the
    // note says where it can be turned on. When a policy turned it ON (LockedOn), the app may not turn it
    // off, so the box is ticked and greyed out. Unavailable (the state could not be read) leaves the box usable.
    void ShowStartup()
    {
        var startup = StartupSwitch.Current;
        var availability = startup.Availability;
        var lockedOff = availability is StartupAvailability.DisabledByUser or StartupAvailability.DisabledByPolicy;
        var lockedOn = availability is StartupAvailability.LockedOn;
        var locked = lockedOff || lockedOn;

        showingStartup = true;
        StartWithWindows.IsChecked = lockedOn || (!lockedOff && startup.IsEnabled);
        showingStartup = false;
        StartWithWindows.IsEnabled = !locked;

        // Store packaging design (docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md): only
        // the direct build warns about the Store copy, found by the execution alias it installs.
        var note = StartupNoteText.For(availability, PackageIdentity.IsPackaged, File.Exists(PackageIdentity.AliasPath));
        StartupNote.Text = note;
        StartupNote.Visibility = note.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    // A switch that did not flip (the test host refusing the Run value, the startup task turned off in
    // Settings since the window opened, a timeout) puts the box back to what the switch now reports, so the
    // box never claims a state that is not true, and the note gets the chance to say why.
    void OnStartupToggled(object s, RoutedEventArgs e)
    {
        if (showingStartup) return;
        var startup = StartupSwitch.Current;
        var result = StartWithWindows.IsChecked == true ? startup.Enable() : startup.Disable();
        if (result.IsFailure) ShowStartup();
    }

    // #71. Persisted immediately rather than on a Save button, matching the checkbox above it:
    // both are single switches with no editing state to confirm, and a setting that needs saving
    // is one a user can lose by closing the window.
    //
    // Takes effect on the next start, and deliberately says nothing about that. Turning it OFF
    // mid-session leaves at most one already-scheduled check, which cannot surprise anyone -- it
    // can only ever result in a balloon the user has just said they do not want, once. Tearing
    // down a live timer from here to buy that would mean this window reaching into the app's
    // composition root for no outcome anybody would notice.
    void OnCheckForUpdatesToggled(object s, RoutedEventArgs e) =>
        manager.SetCheckForUpdates(CheckForUpdates.IsChecked == true).TapError(Complain);

    // Read by the next automatic check, so it takes effect within five minutes and needs no restart.
    void OnUpdateEveryReleaseToggled(object s, RoutedEventArgs e) =>
        manager.SetUpdateEveryRelease(UpdateEveryRelease.IsChecked == true).TapError(Complain);

    // The bar follows the setting from the state pulse this sends (FloatingBar.FollowDockSetting).
    void OnDockBarToggled(object s, RoutedEventArgs e) =>
        manager.SetDockBar(DockBar.IsChecked == true).TapError(Complain);

    // The app flips Windows' setting from the state pulse this sends (see where App wires
    // TaskbarFlashingSwitch.Sync); this window never writes the registry itself, which is also what keeps the
    // tests that build it away from the real setting.
    void OnQuietTaskbarToggled(object s, RoutedEventArgs e)
    {
        manager.SetQuietTaskbar(QuietTaskbar.IsChecked == true, taskbarAutoHides()).TapError(Complain);
        ShowQuietTaskbarNote();
    }

    // The suggestion under the box: shown while the box is unticked, the taskbar is on auto-hide and Windows
    // flashes, which is exactly when a flashing app holds the taskbar up (TaskbarFlashing.Note). Both readings
    // are read-only. AppBar.Taskbar is null when no shell answers, and then there is no taskbar to hold up.
    void ShowQuietTaskbarNote()
    {
        var note = Deskbar.Core.Shell.TaskbarFlashing.Note(
            ticked: QuietTaskbar.IsChecked == true,
            autoHide: taskbarAutoHides(),
            flashingOn: TaskbarFlashingSwitch.FlashingOn);
        QuietTaskbarNote.Text = note;
        QuietTaskbarNote.Visibility = note.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    // The app writes or removes the skill from the state pulse this sends (see where App wires ClaudeSkill.Sync). The
    // task box under it follows, since it only works while this one is ticked.
    void OnClaudeSkillToggled(object s, RoutedEventArgs e)
    {
        manager.SetClaudeSkill(ClaudeSkill.IsChecked == true).TapError(Complain);
        ShowClaudeTasks();
    }

    // "Claude Code sessions keep their tasks in the task list" (AppState.ClaudeTasks) shows what is IN EFFECT: usable
    // and showing the stored choice while Deskbar teaches Claude Code, greyed and unticked while it does not, because
    // then the task hooks are out of settings.json whatever the choice (AppState.ClaudeTasksInEffect). Greyed first, so
    // the untick that follows is not taken as Petre's choice (OnClaudeTasksToggled ignores a greyed box) and turning the
    // teaching back on brings the box back as it was.
    void ShowClaudeTasks()
    {
        ClaudeTasks.IsEnabled = manager.State.ClaudeSkill;
        ClaudeTasks.IsChecked = manager.State.ClaudeTasksInEffect;
        ShowClaudeResume();
    }

    // The app adds or removes the three task hooks from the state pulse this sends (ClaudeSkill.Sync, wired in App).
    // SetClaudeTasks ignores an unchanged value, so ShowClaudeTasks ticking the box writes nothing.
    void OnClaudeTasksToggled(object s, RoutedEventArgs e)
    {
        if (ClaudeTasks.IsEnabled) manager.SetClaudeTasks(ClaudeTasks.IsChecked == true).TapError(Complain);
        ShowClaudeResume();
    }

    // "Resume the sessions of open tasks after a restart or a crash" (AppState.ClaudeResume) follows the tasks box the
    // way that box follows the teaching: usable and showing the stored choice while sessions keep tasks, greyed and
    // unticked while they do not (AppState.ClaudeResumeInEffect). Greyed first, so the untick is not taken as a choice.
    void ShowClaudeResume()
    {
        ClaudeResume.IsEnabled = manager.State.ClaudeTasksInEffect;
        ClaudeResume.IsChecked = manager.State.ClaudeResumeInEffect;
    }

    // ClaudeSessions reads the setting before every resume, so turning it off drops whatever was waiting its turn.
    // SetClaudeResume ignores an unchanged value, so ShowClaudeResume ticking the box writes nothing.
    void OnClaudeResumeToggled(object s, RoutedEventArgs e)
    {
        if (!ClaudeResume.IsEnabled) return;
        manager.SetClaudeResume(ClaudeResume.IsChecked == true).TapError(Complain);
    }

    // The bar draws or clears the outlines on the next rebuild (FloatingBar.ShowFocusRings).
    void OnFocusRingToggled(object s, RoutedEventArgs e) =>
        manager.SetFocusRing(FocusRing.IsChecked == true).TapError(Complain);

    // The bar repaints the band behind its rows on the next rebuild (FloatingBar.ApplySurfaceSetting).
    void OnSeeThroughToggled(object s, RoutedEventArgs e) =>
        manager.SetSeeThrough(SeeThrough.IsChecked == true).TapError(Complain);

    // The bar draws or clears the item, on every strip, in the titled list and on the taskbar, on the state pulse this
    // sends (FloatingBar.JumpsFor), so it takes effect at once.
    void OnPreviousWorkspaceItemToggled(object s, RoutedEventArgs e) =>
        manager.SetPreviousWorkspaceItem(PreviousWorkspaceItem.IsChecked == true, taskbarAutoHides()).TapError(Complain);

    // The gap between tiles, 0 to 64 pixels at 100% scaling (the Tiler scales it by each screen's DPI). A word
    // that is not a number is refused here; a number out of range is refused by SetTileGap with its own reason.
    // Either way the box goes back to the stored value, so it never shows a gap that is not in force.
    void OnApplyTileGap(object s, RoutedEventArgs e) =>
        (int.TryParse(TileGapBox.Text.Trim(), out var gap)
                ? manager.SetTileGap(gap)
                : Result.Failure("The tile gap must be a whole number of pixels between 0 and 64."))
            .TapError(err =>
            {
                Complain(err);
                TileGapBox.Text = manager.State.TileGap.ToString();
            });

    // --- the Settings tab (#151) -----------------------------------------------------------------
    //
    // Petre: "make things configurable, like dimming opacity, timeout, etc."
    //
    // Set FROM state when the window opens, which raises ValueChanged on every slider -- exactly the
    // situation SetBarAppearance's unchanged-value guard exists for, so merely opening Manage writes
    // nothing and rebuilds nothing.
    //
    // `loading` closes the other half of that. The guard makes a no-change write harmless, and it
    // cannot make the four raises ARRIVE ONE AT A TIME harmless: the first slider to be set would
    // otherwise persist its own new value alongside the other three still holding their XAML defaults,
    // which is a real change and would be saved as one.
    //
    // TRUE TO BEGIN WITH, and that is not belt and braces -- it is the fix for a crash the tests caught
    // before Petre could. A Slider raises ValueChanged while its XAML is being PARSED: setting Minimum
    // coerces Value, which fires the handler from inside InitializeComponent, at which point the other
    // named controls are still null and the readout line threw NullReferenceException. Opening Manage
    // would have died. A field initializer runs before the constructor body, so the handler is inert
    // until LoadAppearance says otherwise.
    bool loading = true;

    void LoadAppearance()
    {
        loading = true;
        try
        {
            IdleOpacitySlider.Value = BarFading.Clamp(manager.State.BarIdleOpacity);
            FadeGraceSlider.Value = BarFading.ClampGraceSeconds(manager.State.BarFadeGraceSeconds);
            // Shown in seconds and stored in milliseconds. Seconds is how the value is thought about
            // ("about four seconds"), and milliseconds is what an animation takes.
            FadeDurationSlider.Value = BarFading.ClampDurationMs(manager.State.BarFadeDurationMs) / 1000;

            // Windows' own number, named in the checkbox rather than left as "the default": "use
            // Windows' hover time" tells you nothing useful without saying what that is, and it is a
            // value the user can change outside this app.
            var system = HoverDwelling.ClampMs(null, SystemParameters.MouseHoverTime.TotalMilliseconds);
            InheritHoverDwell.Content = $"Use Windows' own hover time ({system:0} ms)";
            InheritHoverDwell.IsChecked = manager.State.HoverDwellMs is null;
            // The slider shows the effective value even while inheriting, so unticking the box starts
            // from what the bar is actually doing rather than jumping to some other number.
            HoverDwellSlider.Value = manager.State.HoverDwellMs ?? system;
            TextSizeSlider.Value = BarText.Clamp(manager.State.BarTextOffset);
            // Shown in percent and stored as a factor, the way opacity is.
            IconSizeSlider.Value = IconScaling.Clamp(manager.State.IconScale) * 100;
            FrostBlurSlider.Value = FrostBlur.Clamp(manager.State.FrostBlur);
            ListLinesSlider.Value = WindowListLines.Clamp(manager.State.WindowListMinimumLines);
            // Ticked from state like the sliders, under the same guard, so opening Manage writes nothing.
            StripItemBoxes().ForEach(box => box.IsChecked = !manager.State.HiddenStripItems.Contains((StripItem)box.Tag));
        }
        finally
        {
            loading = false;
        }

        ShowAppearanceValues();
    }

    // The readouts beside each slider. A slider with no number is fine for opacity and useless for a
    // duration, and having all four labelled costs one method.
    void ShowAppearanceValues()
    {
        IdleOpacityValue.Text = IdleOpacitySlider.Value >= 0.999
            // Opacity 1.0 is not "100%", it is the absence of the feature, and saying so is the only
            // way anyone finds out that this is where "stop dimming" lives.
            ? "never dims"
            : $"{IdleOpacitySlider.Value * 100:0}%";
        FadeGraceValue.Text = FadeGraceSlider.Value <= 0.001 ? "at once" : $"{FadeGraceSlider.Value:0} s";
        FadeDurationValue.Text = $"{FadeDurationSlider.Value:0.0} s";
        HoverDwellSlider.IsEnabled = InheritHoverDwell.IsChecked != true;
        HoverDwellValue.Text = HoverDwellSlider.Value <= 0.001 ? "at once" : $"{HoverDwellSlider.Value:0} ms";
        // Signed, so 0 reads as "the code's own sizes" and the default's +0.5 reads as one step up.
        TextSizeValue.Text = TextSizeSlider.Value switch
        {
            0 => "as built",
            > 0 => $"+{TextSizeSlider.Value:0.#} pt",
            _ => $"{TextSizeSlider.Value:0.#} pt",
        };
        ListLinesValue.Text = ListLinesSlider.Value <= 1 ? "no floor" : $"{ListLinesSlider.Value:0} lines";
        IconSizeValue.Text = Math.Abs(IconSizeSlider.Value - 100) < 0.5 ? "as built" : $"{IconSizeSlider.Value:0}%";
        FrostBlurValue.Text = FrostBlurSlider.Value <= 0.001 ? "sharp" : $"{FrostBlurSlider.Value:0} px";
    }

    // The icon size, applied live like the text size: each step rebuilds the bar with its icons at the new size.
    void OnIconSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (loading) return;
        ShowAppearanceValues();
        manager.SetIconScale(IconSizeSlider.Value / 100).TapError(Complain);
    }

    // The frost strength, applied live like the others: each step pulses the state, the bar's next check sees the new
    // strength and makes the frost again (FloatingBar.Frost.cs), within half a second.
    void OnFrostBlurChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (loading) return;
        ShowAppearanceValues();
        manager.SetFrostBlur(FrostBlurSlider.Value).TapError(Complain);
    }

    // The text size slider, applied live like the others: each step rebuilds the bar at the new size. Behind
    // the same `loading` guard, so opening Manage writes nothing.
    void OnTextSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (loading) return;
        ShowAppearanceValues();
        manager.SetBarTextOffset(TextSizeSlider.Value).TapError(Complain);
    }

    // THE BOTTOM STRIP'S BOXES, one per StripItem, built on first use rather than written out in XAML: the enum
    // is the one list of what the strip can show, so an item added there gets its box here without anyone
    // remembering to. Each box carries its item in Tag. Applied live like everything else on the tab: ticking
    // or unticking saves the whole hidden set, and the bar lays its strip out again.
    List<CheckBox> StripItemBoxes()
    {
        if (StripItemsPanel.Children.Count == 0)
            Enum.GetValues<StripItem>().ToList().ForEach(item =>
            {
                var box = new CheckBox { Content = StripItemName(item), Tag = item, Margin = new Thickness(0, 0, 16, 4) };
                box.Checked += (_, _) => ApplyStripItems();
                box.Unchecked += (_, _) => ApplyStripItems();
                StripItemsPanel.Children.Add(box);
            });
        return StripItemsPanel.Children.OfType<CheckBox>().ToList();
    }

    // What each item is called on the tab: the thing you see on the strip, not the enum's name.
    static string StripItemName(StripItem item) => item switch
    {
        StripItem.Tray => "Tray icons",
        StripItem.QuickSettings => "Wifi and battery",
        StripItem.Volume => "Volume",
        StripItem.Notifications => "Notifications",
        StripItem.Language => "Language",
        StripItem.Clock => "Clock",
        StripItem.ShowDesktop => "Show desktop",
        StripItem.Tasks => "Task list",
        _ => item.ToString(),
    };

    void ApplyStripItems()
    {
        if (loading) return;
        manager.SetHiddenStripItems(StripItemBoxes().Where(box => box.IsChecked != true).Select(box => (StripItem)box.Tag))
            .TapError(Complain);
    }

    // The window list's floor, the same way: applied live, each step rebuilds the bar with that many lines.
    void OnListLinesChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (loading) return;
        ShowAppearanceValues();
        manager.SetWindowListMinimumLines((int)ListLinesSlider.Value).TapError(Complain);
    }

    // Every control on the tab lands here, and it writes on every change rather than on a Save button
    // or a drag-completed event. That is the feature: the bar dims to the new opacity while the thumb
    // is still moving, which is the only way to choose a value you have to look at.
    //
    // The cost is one state.json write per slider step, which is a small file written by a hand
    // dragging a thumb -- tens of writes, not thousands. Debouncing was the obvious alternative and
    // was rejected: it would put a timer between the thumb and the bar, and the whole point is that
    // there is nothing between them.
    // Two entry points rather than one, and not by choice: Slider.ValueChanged is a
    // RoutedPropertyChangedEventHandler<double> while CheckBox.Checked is a plain RoutedEventHandler,
    // and XAML matches the delegate type exactly rather than accepting a base-class signature. Both
    // are one line onto the same method, so there is nothing to keep in step.
    void OnAppearanceChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => ApplyAppearance();
    void OnAppearanceChanged(object sender, RoutedEventArgs e) => ApplyAppearance();

    void ApplyAppearance()
    {
        if (loading) return;

        ShowAppearanceValues();

        manager.SetBarAppearance(
                idleOpacity: IdleOpacitySlider.Value,
                fadeGraceSeconds: FadeGraceSlider.Value,
                fadeDurationMs: FadeDurationSlider.Value * 1000,
                // Null is what "inherit Windows' hover time" means, and it is the reason the checkbox
                // exists rather than a magic value on the slider.
                hoverDwellMs: InheritHoverDwell.IsChecked == true ? null : HoverDwellSlider.Value)
            .TapError(Complain);
    }

    // Clears the stored values rather than writing today's defaults as numbers, which is what keeps a
    // later change of default reaching anyone who pressed this.
    void OnResetAppearance(object sender, RoutedEventArgs e)
    {
        manager.SetBarAppearance(null, null, null, null).TapError(Complain);
        manager.SetBarTextOffset(null).TapError(Complain);
        manager.SetWindowListMinimumLines(null).TapError(Complain);
        // Everything back on the strip, and every icon back to its built size.
        manager.SetHiddenStripItems([]).TapError(Complain);
        manager.SetIconScale(null).TapError(Complain);
        manager.SetFrostBlur(null).TapError(Complain);
        LoadAppearance();
    }


    Result<bool> Report(Result<bool> result) => result.TapError(Complain);

    // Every refusal on this window is shown through here: owned by Manage, so the box cannot open behind it (an
    // ownerless MessageBox belongs to no window and can land under the one the user is looking at), and titled
    // "Deskbar" like the app's other boxes rather than with an empty caption.
    void Complain(string error) => MessageBox.Show(this, error, "Deskbar");
}
