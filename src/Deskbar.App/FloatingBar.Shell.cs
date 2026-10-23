using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Deskbar.Core.Persistence;
using Deskbar.Windows.Monitoring;

namespace Deskbar.App;

// THE TASKBAR'S OWN BUTTONS, on the bar. Petre: "i like how the app replaces my taskbar, but taskbar had
// some useful things, like a clock, start button, etc." His taskbar is on auto-hide at the bottom of one
// screen, so the bar is what he looks at, and the clock and Start had gone with the taskbar.
//
// Picked from three mockups ("c", split ends): Start and search join the header row, which already exists
// and costs no height, and a strip under the last row holds the tray chevron, quick settings, the bell, the
// clock and Windows' show desktop sliver.
//
// EVERY BUTTON SENDS THE WINDOWS SHORTCUT, so the real Windows flyout opens and nothing of the shell is
// rebuilt here. Measured with a throwaway probe on his machine before a line of this was written:
//
//   Win    -> "Start menu"           DV2ControlHost               bottom left of the taskbar's screen
//   Win+S  -> "Search"               Windows.UI.Core.CoreWindow   bottom left, over the docked bar
//   Win+N  -> "Notification Center"  Windows.UI.Core.CoreWindow   right edge of the taskbar's screen
//   Win+A  -> "Quick settings"       ControlCenterWindow          right edge of the taskbar's screen
//   Win+B  -> the auto-hidden taskbar slides up with the tray focused (Shell_TrayWnd)
//
// All of them open on the screen that HAS the taskbar, wherever the pointer is, because Windows anchors
// them to the taskbar rather than to us. Nothing here can change that.
//
// The tray icons themselves (Teams, OneDrive and the rest) are deliberately NOT drawn: an app hands its
// icon to Explorer's taskbar window and to nothing else, so showing them would mean impersonating that
// window, which is the most fragile thing a taskbar replacement does. Win+B shows the real tray instead.
//
// Two traps the probe also showed, both handled below:
//
//   * A keystroke can be lost. The first probe run got nothing from two of ten shortcuts and the rerun got
//     four of four, so after sending, the button looks at what is in front and sends ONCE more if its
//     flyout is not there. See SendAndConfirm.
//   * Clicking a button on this bar ACTIVATES the bar (it is a normal top-level window), and an open flyout
//     closes the moment it loses the foreground. So by the time Click fires, the Start menu the user meant
//     to close has already closed, and sending Win would open it again. The bar therefore remembers which
//     flyout was in front when the press arrived (flyoutInFrontAtPress), and a click on that flyout's own
//     button just lets the press do the closing, which is what the taskbar's buttons do.
public partial class FloatingBar
{
    // What each button opens. Desktop is Win+D, which toggles by itself and opens nothing to look for.
    internal enum ShellFlyout { Start, Search, Tray, QuickSettings, Notifications, Desktop }

    // The shell flyout last reported in front by the foreground hook (NoteForeground), or null when
    // something else is. Cleared by a press on the bar, because that press takes the foreground from it.
    ShellFlyout? flyoutInFront;

    // ...and what it was when the current press arrived, which is what a button's Click reads.
    ShellFlyout? flyoutInFrontAtPress;

    // What actually presses the keys. A seam for the tests and nothing else: a test that pressed the real
    // Windows key would open the Start menu on the screen of whoever runs the suite.
    internal Action<ShellFlyout> SendShellKeys { get; init; } = ShellKeys.Send;

    // ...and the Space that opens the tray's hidden icons once Win+B has focused it (see OpenTrayOverflow),
    // a seam for the same reason.
    internal Action PressSpace { get; init; } = () => ShellKeys.Press(ShellKeys.VK_SPACE);

    // Ticks every second but writes only when the text changes, so the clock turns over on the minute
    // without the bar ever re-laying out between minutes.
    readonly DispatcherTimer clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    TextBlock? clockTime;
    TextBlock? clockDate;

    static readonly Brush ShellGlyphBrush = Frozen(0xD9, 0xFF, 0xFF, 0xFF);
    static readonly Brush StartBlue = Frozen(0xFF, 0x4C, 0xC2, 0xFF);
    static readonly Brush ClockForeground = Frozen(0xE6, 0xFF, 0xFF, 0xFF);
    static readonly Brush SliverLine = Frozen(0x29, 0xFF, 0xFF, 0xFF);

    // Fills the two panels the XAML declares for this: ShellStart in the header, ShellStrip at the bottom.
    // Called once from the constructor; nothing here is rebuilt with the rows, so the buttons keep their
    // identity (and the clock its timer) across every rebuild.
    void BuildShellStrip()
    {
        ShellStart.Children.Add(ShellButton(ShellFlyout.Start, "Start", StartGlyph()));
        ShellStart.Children.Add(ShellButton(ShellFlyout.Search, "Search", Glyph(SearchShape, size: HeaderGlyphSize)));

        // The same pair again, where the right half of the header begins, shown only across the seam (see
        // LayOutHeader). Their own buttons, wired to the same OnShellClick, so a press on either pair behaves
        // identically, including closing a flyout that is already open.
        shellStartTwin = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            // In from the seam by what the left pair sits in from the bar's edge (the bar's padding and the
            // row's ring), so each screen's pair has the same breathing room. Measured without it: the left
            // pair 8 px from its edge, this one at 0, pressed against the left screen.
            Margin = new Thickness(RootPadding + RowRingThickness, 0, 0, 0),
            Children =
            {
                ShellButton(ShellFlyout.Start, "Start", StartGlyph()),
                ShellButton(ShellFlyout.Search, "Search", Glyph(SearchShape, size: HeaderGlyphSize)),
            },
        };
        BarHeader.Children.Add(AtRightHalf(shellStartTwin));
        LayOutHeader(acrossSeam: false);

        // Built once and laid out by LayOutStrip, which decides which edge each one docks to. At the strip's own
        // sizes (StripGlyphSize and friends), which are larger than the header's Start and search.
        var chevron = Glyph(ChevronShape, size: StripGlyphSize);
        chevron.RenderTransform = trayChevronTurn;
        chevron.RenderTransformOrigin = new Point(0.5, ChevronMiddle / 16);
        stripTray = ShellButton(ShellFlyout.Tray, ShowTrayTip, chevron, StripButtonWidth, StripButtonHeight);
        stripTray.ToolTip = trayTip;
        stripQuickSettings = ShellButton(ShellFlyout.QuickSettings, "Quick settings: wifi, volume, battery", QuickSettingsGlyph(), double.NaN, StripButtonHeight);
        stripVolume = VolumeDial();
        stripBell = ShellButton(ShellFlyout.Notifications, "Notifications", Glyph(BellShape, size: StripGlyphSize), StripButtonWidth, StripButtonHeight);
        // The sliver's line keeps the share of the button it had at the old height (22 of 28).
        stripSliver = ShellButton(ShellFlyout.Desktop, "Show desktop",
            new Border { Width = 1, Height = StripButtonHeight * 22 / 28, Background = SliverLine }, 7, StripButtonHeight);
        var clock = ShellButton(ShellFlyout.Notifications, "", ClockFace(), width: double.NaN);
        clock.Padding = new Thickness(4, 0, 4, 0);
        // As tall as its two lines need, not the 28 the strip's buttons had then: at 14.5 over 13 the lines are taller than that,
        // and the date spilled out of the button's bottom, cut off wherever the bar met a screen edge (Petre:
        // "but date is half cut"). The strip grows by the few pixels the clock needs.
        clock.Height = double.NaN;
        stripClock = clock;
        // Hidden until there is more than one input language to switch between (FloatingBar.Language.cs).
        stripLanguage = LanguageButton();
        LayOutStrip(acrossSeam: false);

        UpdateClock(clock);
        // ...and the date line follows the strip's width (FitClock).
        ShellStrip.SizeChanged += (_, e) => { if (e.WidthChanged) FitClock(); };
        clockTimer.Tick += (_, _) => UpdateClock(clock);
        clockTimer.Start();
        Closed += (_, _) => clockTimer.Stop();
    }

    // THE STRIP'S SIZES. Petre, on the chevron, wifi and volume dial: "these are kinda small", then "c" from three
    // mockups, the size closest to Windows' own tray. They were 15 DIP glyphs (wifi 13) in 26 by 28 buttons, the
    // same as Start and search in the header; now the strip has its own, and the header keeps the old ones.
    // 36 tall is a little more than the clock's two lines need (about 32), so the strip grows by about 4 DIP.
    // Wifi stays one DIP under the rest because its arcs fill the 16 unit square edge to edge, where the
    // chevron and bell leave a margin, so at 19 it reads the same size as they do at 20.
    //
    // Then a step back down, for the language switcher (FloatingBar.Language.cs): Petre, "make those icons a bit
    // smaller", "so it fits in this". On his bar the strip is about 216 DIP wide, and at 20 DIP glyphs in 34 DIP
    // buttons the left group, the sliver and the clock with its date already took about 200 of it, so the
    // language code arrived clipped to one letter and pushed the date out. 17 (wifi 16) in 28 DIP buttons frees
    // about 24 DIP, which is what the code needs. The height stays, since the clock's two lines set it.
    const double StripGlyphSize = 17, StripWifiSize = 16, StripButtonWidth = 28, StripButtonHeight = 36;

    Button? stripTray, stripQuickSettings, stripVolume, stripBell, stripSliver, stripClock, stripLanguage;
    bool stripAcrossSeam;
    StackPanel? shellStartTwin;

    // START AND SEARCH ON BOTH HALVES, docked across the seam. Petre: "start and search buttons on both sides of
    // the bar, if docked in between screens". The header line is the one row that did not mirror: the captions,
    // rails and group lines are drawn again where the right half begins, but Start and search sat only at the
    // far left, on the left screen. So across the seam the pair appears a second time at the right half's left
    // edge, in the same place the right half's captions start.
    //
    // Where the menus OPEN does not change, and cannot from here: Windows anchors Start and search to the
    // screen that has the taskbar, wherever the press came from (the probe in this file's header). With one
    // taskbar, both pairs open them on that screen. What the second pair saves is the reach across the bar.
    internal void LayOutHeader(bool acrossSeam)
    {
        if (shellStartTwin is not null)
            shellStartTwin.Visibility = acrossSeam ? Visibility.Visible : Visibility.Collapsed;
    }

    // Which side each of the strip's buttons docks to. On one screen: the tray chevron, quick settings, the
    // volume dial and the bell from the left, and from the right edge inwards (the order DockPanel takes
    // right-docked children in) the show desktop sliver at the very edge, where Windows puts it, then the clock.
    //
    // Docked ACROSS two screens the bar's middle is the line between them, and the bell sat on it, half on each
    // screen. Petre: "icons at the bottom shouldn't be broken into halves", "they should either stay or go to
    // another monitor". So across the seam the bell joins the clock on the right half, and the left half keeps
    // the chevron, quick settings and the dial, which fit inside it.
    //
    // The language switcher sits just inside the clock, where Windows puts its own language indicator, on
    // either layout. It is collapsed with a single input language, and a collapsed child takes no room.
    //
    // An item switched off in Manage > Settings (AppState.HiddenStripItems, see StripItem) is simply not added,
    // so the rest close up and nothing of it takes room. Petre: "i don't want wifi there", "and i don't think i
    // want notifications either", "make it a setting, what to show". Left out of the strip rather than
    // collapsed, because the volume dial and the language switcher already set their own Visibility (no
    // device, one language), and a second owner of the same property would have each undo the other.
    void LayOutStrip(bool acrossSeam)
    {
        stripAcrossSeam = acrossSeam;
        stripHidden = manager.State.HiddenStripItems.ToHashSet();
        ShellStrip.Children.Clear();
        Button[] left = acrossSeam ? [stripTray!, stripQuickSettings!, stripVolume!] : [stripTray!, stripQuickSettings!, stripVolume!, stripBell!];
        Button[] right = acrossSeam ? [stripSliver!, stripClock!, stripLanguage!, stripBell!] : [stripSliver!, stripClock!, stripLanguage!];
        left.Where(ShownInStrip).ToList().ForEach(b =>
        {
            DockPanel.SetDock(b, Dock.Left);
            ShellStrip.Children.Add(b);
        });
        right.Where(ShownInStrip).ToList().ForEach(b =>
        {
            DockPanel.SetDock(b, Dock.Right);
            ShellStrip.Children.Add(b);
        });
        // Which buttons share the strip with the clock just changed, so whether its date fits may have too.
        FitClock();
    }

    // The hidden items the strip was last laid out with, so a rebuild can tell whether the setting has changed.
    IReadOnlySet<StripItem> stripHidden = new HashSet<StripItem>();

    bool ShownInStrip(Button button) => !(StripItemOf(button) is { } item && stripHidden.Contains(item));

    StripItem? StripItemOf(Button button) =>
        button == stripTray ? StripItem.Tray
        : button == stripQuickSettings ? StripItem.QuickSettings
        : button == stripVolume ? StripItem.Volume
        : button == stripBell ? StripItem.Notifications
        : button == stripLanguage ? StripItem.Language
        : button == stripClock ? StripItem.Clock
        : button == stripSliver ? StripItem.ShowDesktop
        : null;

    // Everything that has to respect the seam (see LayOutStrip, the pins' PackedPinPanel and the now-playing
    // line), brought into line with the current docking. Called at the start of every rebuild, so it follows
    // the bar onto the seam and off it.
    void LayOutBottomForSeam()
    {
        // ...and the header's second Start and search, which follow the same switch.
        LayOutHeader(AcrossSeam);
        // ...and the strip again when the items shown in it were changed in Manage > Settings, since saving that
        // setting is what triggers this rebuild.
        if (stripAcrossSeam != AcrossSeam || !stripHidden.SetEquals(manager.State.HiddenStripItems)) LayOutStrip(AcrossSeam);
        FitPinsToSeam();
        FitNowPlayingToSeam();
        FitStatsToSeam();
        // ...and the icon size setting on the bar's own buttons, which a rebuild does not recreate.
        ScaleChromeIcons();
    }

    // THE ICON SIZE SETTING ON THE BAR'S OWN BUTTONS (IconScaling). Petre: "let's also make icon sizes
    // configurable", "as a separate setting", "including icons at the bottom". The rows' icons are scaled as
    // they are built (IconButton); these are built once and kept, so they are scaled here instead, whenever
    // the setting has changed since the last time:
    //
    //   the strip    the tray chevron, wifi and battery, the volume dial and the bell. Not the clock and not
    //                the language code: they are TEXT, and the icon setting leaves text alone.
    //   Start, search and the pinned apps, together, because the pins were sized to match Start and search
    //                ("one size with Start and search") and a setting that split them would undo that.
    //
    // The whole BUTTON is scaled, not just its glyph, because the buttons have fixed sizes chosen around their
    // glyphs, and a bigger glyph in the same button would be clipped. A LayoutTransform, so the strip and the
    // pin line make room for what they hold.
    void ScaleChromeIcons()
    {
        if (Math.Abs(IconScale - chromeIconScale) < 0.001) return;
        chromeIconScale = IconScale;
        new FrameworkElement?[] { stripTray, stripQuickSettings, stripVolume, stripBell }
            .Concat(ShellStart.Children.OfType<FrameworkElement>())
            .Concat(shellStartTwin?.Children.OfType<FrameworkElement>() ?? [])
            .Concat(pinButtons.Values.Select(pin => pin.Button))
            .OfType<FrameworkElement>()
            .ToList()
            .ForEach(ScaleAsIcon);
        // The strip's buttons just changed width, so whether the clock and its date fit may have too.
        FitClock();
    }

    // The scale the bar's own buttons were last given; they are built at 1.
    double chromeIconScale = 1.0;

    // One button at the current icon size; also how a pin created after the setting changed gets it.
    void ScaleAsIcon(FrameworkElement element) =>
        element.LayoutTransform = Math.Abs(IconScale - 1) < 0.001 ? Transform.Identity : new ScaleTransform(IconScale, IconScale);

    // 14 over 12.5, after two rounds of Petre's "increase the size of fonts for this": 10 and 9 matched the
    // bar's captions, then 12 and 11 matched the taskbar's clock on paper, but the bar draws through its own
    // scale (BarScale) and on a docked 4K screen that still read small.
    // Time over date, the way the taskbar shows them, in the user's own regional format: .NET's current
    // culture is the same "Region" setting the taskbar reads, so the two can never disagree about it.
    UIElement ClockFace()
    {
        // Line heights set tight to the glyphs, as the taskbar's clock is, so the two lines stack close and the
        // strip only grows by what the text really needs.
        // FIXED sizes, deliberately outside the bar's text size setting: the clock matches the taskbar's, and at
        // the setting's +4 default Petre found it "too big" (see FloatingBar.TextSize.cs).
        //
        // The time is the DATE's size now, not a size above it. Petre, of 14.5 over 13 with "11:59 PM": "make
        // clock smaller, and no pm", "24h", "smaller as the date". The two lines then read as one block, and the
        // 24-hour time (see ClockText) is shorter than the 12-hour one it replaces, so the clock is narrower too.
        clockTime = new TextBlock { FontSize = 13, Foreground = ClockForeground, TextAlignment = TextAlignment.Right, LineHeight = 16, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
        clockDate = new TextBlock { FontSize = 13, Foreground = ClockForeground, TextAlignment = TextAlignment.Right, LineHeight = 16, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
        return new StackPanel { Children = { clockTime, clockDate }, VerticalAlignment = VerticalAlignment.Center };
    }

    // The time on the clock: 24-hour, hours and minutes, whatever the region's own short time is. Petre: "no
    // pm", "24h". The DATE still follows the regional format, as the taskbar's does; only the time stopped
    // following it, because the regional short time here is 12-hour with an AM/PM. Two-digit hours, so the line
    // does not change width at ten o'clock. Internal so a test reads the same format the clock does.
    internal static string ClockText(DateTime at) => at.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    void UpdateClock(FrameworkElement clock)
    {
        var now = DateTime.Now;
        var time = ClockText(now);
        if (clockTime is null || clockDate is null || clockTime.Text == time) return;
        clockTime.Text = time;
        clockDate.Text = now.ToString("d");
        // The long date on hover, which the two short lines leave out (and which still carries the date when
        // FitClock has had to drop its line).
        clock.ToolTip = now.ToString("D");
        FitClock();
    }

    // NO DATE RATHER THAN HALF A DATE. Petre, with a narrow bar whose strip ran out of room: the clock was cut at
    // the right edge, "13:" over "20", and "date. don't show it if it's like that". The strip is a DockPanel, so
    // when its buttons and the clock need more than its width, whatever docks last is clipped, and that was the
    // clock. So when everything with the date's line does not fit, the date line goes, and the clock shrinks
    // to the time's width, which is the narrower of the two lines in every regional format worth worrying
    // about. The time stays, the tooltip keeps the full date. Measured at natural widths, every time the
    // strip's width or the clock's text changes.
    //
    // THE CLOCK GIVES WAY TO THE LANGUAGE. Petre, on a laptop strip where the clock was cut to "12:(" and the
    // language switcher had been squeezed out entirely: "i want language as a priority". The strip hands out
    // its width in the order its children were added, and the language switcher comes after the clock, so a
    // short strip starved it first. So when not even the time fits beside everything else, the clock goes
    // too, and the switcher keeps its room. The clock's place in that order is not the fix, because it is
    // also its place on screen (just right of the language code, where Windows draws them). The other way to
    // make room is the setting above it: switch off what is not wanted (wifi, the bell).
    void FitClock()
    {
        if (clockTime is null || clockDate is null || stripClock is null || ShellStrip.ActualWidth <= 0) return;
        // Switched off in Settings: nothing to fit.
        if (!ShellStrip.Children.Contains(stripClock)) return;
        var infinite = new Size(double.PositiveInfinity, double.PositiveInfinity);
        double Natural(UIElement element)
        {
            element.Measure(infinite);
            return element.DesiredSize.Width;
        }
        // Every button by its MEASURED width, which includes the icon size setting's LayoutTransform (see
        // ScaleChromeIcons); its Width property is the size before that scale, so reading it would be wrong at
        // any icon size but 100%.
        var others = ShellStrip.Children.OfType<FrameworkElement>()
            .Where(b => b != stripClock && b.Visibility != Visibility.Collapsed)
            .Sum(Natural);
        // The clock's lines by their TEXT, not by measuring the elements: a collapsed date measures 0 wide, so
        // measuring it would make the next check think the date fits, show it, and hide it again, every minute.
        var withDate = stripClock.Padding.Left + stripClock.Padding.Right + Math.Max(TextWidth(clockTime), TextWidth(clockDate));
        var timeOnly = stripClock.Padding.Left + stripClock.Padding.Right + TextWidth(clockTime);
        var fits = others + withDate <= ShellStrip.ActualWidth;
        var timeFits = others + timeOnly <= ShellStrip.ActualWidth;
        lastClockFit = (ShellStrip.ActualWidth, others, withDate);
        clockDate.Visibility = fits ? Visibility.Visible : Visibility.Collapsed;
        stripClock.Visibility = timeFits ? Visibility.Visible : Visibility.Collapsed;
        var verdict = fits ? "date shown" : timeFits ? "date hidden" : "clock hidden";
        if (ClickTrace.On && clockVerdict != verdict)
            ClickTrace.Write($"clock: {verdict}, strip={ShellStrip.ActualWidth:0.0} others={others:0.0} clock with date={withDate:0.0} time only={timeOnly:0.0}");
        clockVerdict = verdict;
    }

    // Last FitClock verdict, so the trace says only when it changes.
    string? clockVerdict;

    // ...and the numbers behind it, for the tests.
    internal (double Strip, double Others, double ClockWithDate) lastClockFit;

    // How wide a line's text is in its own font, whether or not the line is showing.
    static double TextWidth(TextBlock line) =>
        new FormattedText(line.Text, System.Globalization.CultureInfo.CurrentCulture, line.FlowDirection,
                new Typeface(line.FontFamily, line.FontStyle, line.FontWeight, line.FontStretch), line.FontSize, Brushes.White,
                VisualTreeHelper.GetDpi(line).PixelsPerDip)
            .WidthIncludingTrailingWhitespace;

    // One button of the strip: bare chrome with a faint rounded hover, so it reads as a button without the
    // stock template's blue square (see BareButton for why the stock chrome is unwanted on this bar).
    // 26 by 28 is the header's size (Start and search); the strip passes its own (StripButtonWidth, -Height).
    Button ShellButton(ShellFlyout flyout, string tip, UIElement content, double width = 26, double height = 28)
    {
        var button = new Button
        {
            Content = content,
            Width = width,
            Height = height,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Template = ShellButtonTemplate,
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = false,
        };
        if (tip.Length > 0) button.ToolTip = tip;
        System.Windows.Automation.AutomationProperties.SetName(button, tip.Length > 0 ? tip : "Clock");
        button.Click += (_, _) => OnShellClick(flyout);
        return button;
    }

    // The one place the bar answers a press on these buttons.
    void OnShellClick(ShellFlyout flyout)
    {
        // Same as an icon: this click is the press's whole meaning, so nothing further up the route (the
        // pinned row Start and search sit in) may treat its release as a click of its own.
        MarkPressConsumed();

        var wasOpen = flyoutInFrontAtPress == flyout;
        flyoutInFrontAtPress = null;
        if (ClickTrace.On) ClickTrace.Write($"shell: {flyout} clicked, open at press={wasOpen}");

        // The press already closed it by taking the foreground; sending the shortcut now would reopen it.
        // Show desktop is the exception: it is not a window that can be in front, Win+D toggles by itself.
        if (wasOpen && flyout is not ShellFlyout.Desktop) return;
        SendAndConfirm(flyout);
    }

    // Sends the shortcut, then looks once more after the flyout has had time to open, and sends again if
    // it is not in front. Once, not in a loop: a flyout that does not open twice running is not going to
    // open, and a third press would most likely land just as the user dismissed it.
    void SendAndConfirm(ShellFlyout flyout)
    {
        SendShellKeys(flyout);
        if (flyout is ShellFlyout.Desktop) return;
        if (flyout is ShellFlyout.Tray)
        {
            OpenTrayOverflow(attempt: 1);
            return;
        }

        var check = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        check.Tick += (_, _) =>
        {
            check.Stop();
            var front = ShellKeys.Recognise(NativeMethods.GetForegroundWindow());
            if (ClickTrace.On) ClickTrace.Write($"shell: {flyout} check, in front={front?.ToString() ?? "other"}");
            if (front != flyout) SendShellKeys(flyout);
        };
        check.Start();
    }

    // Petre, on the chevron: "pressing this should be followed by a single space, can you do that on behalf of
    // the clicker? so it shows immediately". Win+B only FOCUSES the tray: the auto-hidden taskbar slides up
    // with its "show hidden icons" chevron selected, and waits for Space or Enter to open the overflow. So the
    // bar presses Space itself, once the taskbar has actually taken the foreground; a Space sent before that
    // would land in whatever window had focus. If the focus never arrives the shortcut is sent once more,
    // the same single retry every other button gets.
    void OpenTrayOverflow(int attempt)
    {
        var wait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        wait.Tick += (_, _) =>
        {
            wait.Stop();
            var front = NativeMethods.GetForegroundWindow();
            var focused = ShellKeys.Recognise(front) == ShellFlyout.Tray;
            if (ClickTrace.On) ClickTrace.Write($"shell: tray attempt {attempt}, in front={ShellKeys.Describe(front)}, focused={focused}");
            if (focused)
            {
                PressSpace();
                // What opened is remembered as the tray's overflow, whatever its class, so a second click on the
                // chevron closes it rather than opening it again (see RememberTrayOverflow).
                var after = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                after.Tick += (_, _) =>
                {
                    after.Stop();
                    var opened = NativeMethods.GetForegroundWindow();
                    if (ClickTrace.On) ClickTrace.Write($"shell: tray after space, in front={ShellKeys.Describe(opened)}");
                    RememberTrayOverflow(opened);
                };
                after.Start();
            }
            else if (attempt == 1)
            {
                SendShellKeys(ShellFlyout.Tray);
                OpenTrayOverflow(attempt: 2);
            }
        };
        wait.Start();
    }

    // From the composition root, on every foreground change (WindowMonitor.ForegroundChanged, which reports
    // the shell's own windows but never this bar's). Keeps track of which flyout, if any, is in front.
    //
    // A window the bar saw open after its own Space counts as the tray too, whatever class it has (see
    // RememberTrayOverflow).
    //
    // It is also how the language switcher knows which window you were typing in (FloatingBar.Language.cs):
    // every foreground window that is not a flyout.
    public void NoteForeground(nint hwnd)
    {
        var flyout = ShellKeys.Recognise(hwnd) ?? (hwnd != 0 && hwnd == trayOverflow ? ShellFlyout.Tray : null);
        NoteFlyoutInFront(flyout);
        TurnTrayChevron(IsTrayOverflow(hwnd));
        NoteLanguageTarget(hwnd, isFlyout: flyout is not null);
    }

    // THE TRAY'S OVERFLOW, as it actually appeared. Issue #227: "When I press system tray arrow from deskbar it
    // will only pops them out but never pops down". The second press does close the overflow, by taking the
    // foreground from it, and the chevron is meant to stand down then, as every button does for its own open
    // flyout. That depends on recognising the overflow as the tray's, and the class list only knew
    // NotifyIconOverflowWindow, measured on Petre's machine. The reporter's screenshot shows Windows 11's newer
    // overflow, a XAML flyout over a visible taskbar, with a different class. Not recognised, the press read as
    // "nothing open", and the chevron sent Win+B and Space again, reopening at once what the press had closed.
    //
    // So the class does not have to be known in advance: whatever came to the front just after the bar
    // pressed Space IS the overflow, on any build of Windows. Remembered by handle, and marked as in front now,
    // because its foreground event has usually come and gone before this runs. Not the bar itself, and not a
    // flyout recognised as something else, in case the Space landed somewhere unexpected.
    nint trayOverflow;

    internal void RememberTrayOverflow(nint opened)
    {
        if (opened == 0 || opened == new System.Windows.Interop.WindowInteropHelper(this).Handle) return;
        if (ShellKeys.Recognise(opened) is { } known && known != ShellFlyout.Tray) return;
        trayOverflow = opened;
        NoteFlyoutInFront(ShellFlyout.Tray);
        TurnTrayChevron(IsTrayOverflow(opened));
    }

    internal void NoteFlyoutInFront(ShellFlyout? flyout) => flyoutInFront = flyout;

    // From the window's first press handler: the flyout in front right now is the one this press closes.
    internal void NoteBarPressed()
    {
        flyoutInFrontAtPress = flyoutInFront;
        flyoutInFront = null;
        TurnTrayChevron(false);
    }

    // Windows' own chevron points down while the hidden icons are open, so its next press reads as "hide
    // them", and it TURNS there rather than jumping. This one follows the overflow window itself, not
    // flyoutInFront: that counts the focused taskbar as the tray too (Recognise), and the taskbar is in front
    // with nothing open for a moment after Win+B and again after Esc closes the overflow. Set where the overflow
    // comes or goes: a foreground change (NoteForeground), the bar's own Space opening it (RememberTrayOverflow),
    // a press on the bar. The strip keeps one chevron for the bar's whole life (LayOutStrip only moves it), so a
    // rebuild cannot lose this.
    //
    // Only a CHANGE of target starts a turn: this runs on every foreground change, and restarting the animation
    // each time would stall it. A turn that starts while another is running has no From, so it goes on from where
    // the arrow is and a reversal does not jump. A bar that is not shown has nothing to animate, so the angle is
    // just set. All callers are on the UI thread.
    //
    // NOT gated on Windows' "animation effects" (SystemParameters.ClientAreaAnimation). The app's author has that
    // off, so a turn gated on it would never have been seen: the same decision as the glide's, see
    // GlideIfExpansionChanged in FloatingBar.Motion.cs. Asked for by name, it runs whatever the system setting says.
    void TurnTrayChevron(bool open)
    {
        if (open == trayChevronOpen) return;
        trayChevronOpen = open;
        SetTrayWords(open);
        var angle = open ? 180.0 : 0.0;
        TrayChevronTurning = null;
        if (!IsVisible)
        {
            trayChevronTurn.Angle = angle;
            trayChevronTurn.BeginAnimation(RotateTransform.AngleProperty, null);
            return;
        }

        var turn = new DoubleAnimationUsingKeyFrames { Duration = TrayChevronTurnTime };
        turn.KeyFrames.Add(new SplineDoubleKeyFrame(angle, KeyTime.FromPercent(1), ChevronTurnCurve));
        // A turn that was reversed half way is replaced, not finished, so only the current one settles the angle.
        // Settling is what releases the animation: a held one would own the property, and the next plain
        // assignment (or a read of the angle) would see its end value instead of the latest target.
        turn.Completed += (_, _) =>
        {
            if (TrayChevronTurning != turn) return;
            TrayChevronTurning = null;
            trayChevronTurn.Angle = angle;
            trayChevronTurn.BeginAnimation(RotateTransform.AngleProperty, null);
        };
        TrayChevronTurning = turn;
        trayChevronTurn.BeginAnimation(RotateTransform.AngleProperty, turn);
    }

    // What the chevron says goes with where it points: Windows' own says "hide" while the hidden icons are open.
    // Set by TurnTrayChevron, off the same flag, so the words and the arrow cannot disagree. The tooltip is a
    // ToolTip of its own that is changed, not replaced, so one that is showing right now changes with it. The
    // accessible name is the same words, as ShellButton sets it from the tip.
    const string ShowTrayTip = "Show the tray icons";
    const string HideTrayTip = "Hide the tray icons";
    readonly ToolTip trayTip = new() { Content = ShowTrayTip };

    void SetTrayWords(bool open)
    {
        var words = open ? HideTrayTip : ShowTrayTip;
        trayTip.Content = words;
        if (stripTray is { } tray) System.Windows.Automation.AutomationProperties.SetName(tray, words);
    }

    // 0 points up, 180 down, about the chevron's own middle (ChevronMiddle) so the glyph stays in place.
    readonly RotateTransform trayChevronTurn = new(0);

    // Where the chevron is going (false: up), so a call that changes nothing starts nothing...
    bool trayChevronOpen;

    // ...and the turn in progress, or null at rest. Read by the tests, to tell a restart from a turn left alone...
    internal DoubleAnimationUsingKeyFrames? TrayChevronTurning { get; private set; }

    // ...and how long a turn takes, so a test can slow it down enough to catch it half way.
    internal Duration TrayChevronTurnTime { get; init; } = new(TimeSpan.FromMilliseconds(ChevronTurnMs));

    // Frozen, because this one is static and every bar (each on its own thread in the tests) uses it.
    static KeySpline Curve(double x1, double y1, double x2, double y2)
    {
        var spline = new KeySpline(x1, y1, x2, y2);
        spline.Freeze();
        return spline;
    }

    // The overflow: a window of a class it is known by, or the one RememberTrayOverflow saw open. Never the bare
    // taskbar, even if that is what the Space left in front, because the two cannot be told apart then.
    bool IsTrayOverflow(nint hwnd) =>
        ShellKeys.IsTrayOverflow(hwnd) || (hwnd != 0 && hwnd == trayOverflow && !ShellKeys.IsBareTaskbar(hwnd));

    // --- glyphs ---------------------------------------------------------------------------------------
    //
    // Drawn as paths on a 16 unit square, the same shapes as the mockup Petre picked, rather than taken from
    // Segoe Fluent Icons: a private use codepoint that turns out wrong renders as a hollow box (see this
    // bar's MenuGlyph comment), and these are six simple shapes. Each part is (geometry, filled).
    const string SearchShape = "M2,6.5 A4.5,4.5 0 1 1 11,6.5 A4.5,4.5 0 1 1 2,6.5 Z M10,10 L14.5,14.5";
    const string ChevronShape = "M3.5,10 L8,5.5 L12.5,10";
    // Where the tray's turn pivots (TurnTrayChevron): the chevron's own middle, not the square's 8, so it stays put.
    const double ChevronMiddle = (5.5 + 10) / 2;
    // How the turn runs: WinUI's standard "fast" duration and fast-out-slow-in curve (KeySpline 0,0,0,1). Assumed
    // to be what Windows' own arrow does, NOT measured from its taskbar, so change them here if it looks off.
    const int ChevronTurnMs = 167;
    static readonly KeySpline ChevronTurnCurve = Curve(0, 0, 0, 1);
    const string BellShape = "M8,1.8 C5.8,1.8 4,3.6 4,5.8 L4,8.8 L2.6,11.4 L13.4,11.4 L12,8.8 L12,5.8 C12,3.6 10.2,1.8 8,1.8 Z M6.4,13.2 A1.7,1.7 0 0 0 9.6,13.2";
    const string WifiShape = "M1.5,6 A9.5,9.5 0 0 1 14.5,6 M4,8.7 A5.8,5.8 0 0 1 12,8.7 M6.4,11.3 A2.6,2.6 0 0 1 9.6,11.3";
    const string WifiDot = "M7,13.6 A1,1 0 1 1 9,13.6 A1,1 0 1 1 7,13.6 Z";
    const string SpeakerShape = "M2,6 L4.6,6 L8,3 L8,13 L4.6,10 L2,10 Z";
    const string SpeakerWaves = "M10.5,5.5 A3.5,3.5 0 0 1 10.5,10.5 M12.5,3.5 A6.3,6.3 0 0 1 12.5,12.5";
    const string BatteryCase = "M2.2,4.5 L11.8,4.5 A1.2,1.2 0 0 1 13,5.7 L13,10.3 A1.2,1.2 0 0 1 11.8,11.5 L2.2,11.5 A1.2,1.2 0 0 1 1,10.3 L1,5.7 A1.2,1.2 0 0 1 2.2,4.5 Z";
    const string BatteryFill = "M2.6,6.1 L9.6,6.1 L9.6,9.9 L2.6,9.9 Z M13.6,6.5 L15.2,6.5 L15.2,9.5 L13.6,9.5 Z";
    const string StartTiles = "M1,1 L7.5,1 L7.5,7.5 L1,7.5 Z M8.5,1 L15,1 L15,7.5 L8.5,7.5 Z M1,8.5 L7.5,8.5 L7.5,15 L1,15 Z M8.5,8.5 L15,8.5 L15,15 L8.5,15 Z";

    static UIElement Glyph(string stroked, string? filled = null, double size = 15, Brush? brush = null)
    {
        var canvas = new Canvas { Width = 16, Height = 16 };
        canvas.Children.Add(new Path
        {
            Data = Geometry.Parse(stroked),
            Stroke = brush ?? ShellGlyphBrush,
            StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        });
        if (filled is not null) canvas.Children.Add(new Path { Data = Geometry.Parse(filled), Fill = brush ?? ShellGlyphBrush });
        return new Viewbox { Width = size, Height = size, Child = canvas, IsHitTestVisible = false };
    }

    // Start and search at the pins' size, 18 DIP, since they share the top of the bar with them: Petre, on the
    // header, "sizes of those icons against start and search", then "b", one size for all of them. They were 15.
    const double HeaderGlyphSize = 18;

    // Windows' four tiles, in the blue the taskbar draws them in, so the one button you reach for most is
    // also the one you find without reading.
    static UIElement StartGlyph() => new Viewbox
    {
        Width = HeaderGlyphSize,
        Height = HeaderGlyphSize,
        IsHitTestVisible = false,
        Child = new Canvas { Width = 16, Height = 16, Children = { new Path { Data = Geometry.Parse(StartTiles), Fill = StartBlue } } },
    };

    // Wifi and, on a machine that has one, the battery: one button, as on the taskbar, because Win+A opens
    // one panel for both. The speaker that sat here moved out to its own dial beside it (FloatingBar.Volume.cs),
    // which shows the real level; these two are still static glyphs.
    static UIElement QuickSettingsGlyph()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 4, 0), IsHitTestVisible = false };
        panel.Children.Add(Glyph(WifiShape, WifiDot, size: StripWifiSize));
        if (ShellKeys.HasBattery())
        {
            panel.Children.Add(new Border { Width = 4 });
            panel.Children.Add(Glyph(BatteryCase, BatteryFill, size: StripWifiSize));
        }
        return panel;
    }

    // Bare chrome plus the one trigger the rows deliberately do without: a faint rounded hover, since these
    // are plain buttons with nothing else to say they can be pressed.
    static readonly ControlTemplate ShellButtonTemplate = Sealed((ControlTemplate)XamlReader.Parse(
        """
        <ControlTemplate TargetType="Button" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Border x:Name="Chrome" Background="{TemplateBinding Background}" CornerRadius="4"
                  Padding="{TemplateBinding Padding}">
            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" />
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter TargetName="Chrome" Property="Background" Value="#1FFFFFFF" />
            </Trigger>
            <Trigger Property="IsPressed" Value="True">
              <Setter TargetName="Chrome" Property="Background" Value="#33FFFFFF" />
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """));
}

// The keystrokes, and telling the shell's flyouts apart. Static and free of WPF so the mapping can be
// tested on its own.
static class ShellKeys
{
    const byte VK_LWIN = 0x5B;
    internal const byte VK_SPACE = 0x20;
    const uint KEYEVENTF_KEYUP = 0x0002;

    // Which keys each flyout is: the Windows key alone, or with one letter.
    internal static byte[] KeysOf(FloatingBar.ShellFlyout flyout) => flyout switch
    {
        FloatingBar.ShellFlyout.Start => [VK_LWIN],
        FloatingBar.ShellFlyout.Search => [VK_LWIN, (byte)'S'],
        FloatingBar.ShellFlyout.Tray => [VK_LWIN, (byte)'B'],
        FloatingBar.ShellFlyout.QuickSettings => [VK_LWIN, (byte)'A'],
        FloatingBar.ShellFlyout.Notifications => [VK_LWIN, (byte)'N'],
        FloatingBar.ShellFlyout.Desktop => [VK_LWIN, (byte)'D'],
        _ => [],
    };

    // Down in order, up in reverse, which is how a hand presses a chord. keybd_event rather than SendInput:
    // it is the call the probe used, it injects the same input, and it has no struct layout to get wrong.
    public static void Send(FloatingBar.ShellFlyout flyout)
    {
        var keys = KeysOf(flyout);
        keys.ToList().ForEach(k => keybd_event(k, 0, 0, 0));
        Enumerable.Reverse(keys).ToList().ForEach(k => keybd_event(k, 0, KEYEVENTF_KEYUP, 0));
    }

    // One key, down and up.
    public static void Press(byte key)
    {
        keybd_event(key, 0, 0, 0);
        keybd_event(key, 0, KEYEVENTF_KEYUP, 0);
    }

    // For the trace: a window as its class and title.
    public static string Describe(nint hwnd) => $"{ClassOf(hwnd)} '{TitleOf(hwnd)}'";

    // The class names measured on Petre's machine (see FloatingBar.Shell's header). Search and the
    // notification centre share a class, so those two are told apart by title, which is English on his
    // machine and may be localised elsewhere; a title that does not match just means the confirming
    // check sends the shortcut a second time, which opens nothing extra on a flyout that is already up.
    public static FloatingBar.ShellFlyout? Recognise(nint hwnd) => (ClassOf(hwnd), TitleOf(hwnd)) switch
    {
        ("DV2ControlHost", _) => FloatingBar.ShellFlyout.Start,
        ("ControlCenterWindow", _) => FloatingBar.ShellFlyout.QuickSettings,
        // The overflow of hidden icons counts as the tray too, measured after the chevron's Win+B and Space,
        // so a second click on the chevron closes it instead of opening it again. NotifyIconOverflowWindow is
        // the classic overflow (Petre's machine); TopLevelWindowForOverflowXamlIsland is Windows 11's newer
        // XAML one (#227), named from the shell, not measured here. The bar also remembers whatever window
        // its Space opened (FloatingBar.RememberTrayOverflow), so a class missing from this list still works.
        (var windowClass, _) when IsBareTaskbarClass(windowClass) || IsOverflowClass(windowClass) =>
            FloatingBar.ShellFlyout.Tray,
        ("Windows.UI.Core.CoreWindow", "Search") => FloatingBar.ShellFlyout.Search,
        ("Windows.UI.Core.CoreWindow", "Notification Center") => FloatingBar.ShellFlyout.Notifications,
        _ => null,
    };

    // The overflow window alone, for the chevron's arrow (FloatingBar.TurnTrayChevron). Recognise counts the bare
    // taskbar as the tray as well, which is right for closing a flyout and wrong for "the hidden icons are open".
    public static bool IsTrayOverflow(nint hwnd) => IsOverflowClass(ClassOf(hwnd));

    public static bool IsBareTaskbar(nint hwnd) => IsBareTaskbarClass(ClassOf(hwnd));

    static bool IsBareTaskbarClass(string windowClass) => windowClass is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";

    static bool IsOverflowClass(string windowClass) =>
        windowClass is "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland";

    // Explorer's Alt+Tab machinery, measured with a foreground hook on Petre's machine (Windows 11): each Alt+Tab
    // brings a ForegroundStaging window and then the XAML switcher, an XamlExplorerHostIslandWindow titled "Task
    // Switching", to the front, and the switcher's event often arrives after the window you switched to. Neither
    // is a window anyone types in, so the language switcher looks past them (FloatingBar.FollowForeground).
    public static bool IsTaskSwitching(nint hwnd) => ClassOf(hwnd) is "ForegroundStaging" or "XamlExplorerHostIslandWindow";

    // 128 is "no system battery" and 255 "unknown"; anything else is a battery worth drawing.
    public static bool HasBattery() => GetSystemPowerStatus(out var status) && status.BatteryFlag is not (128 or 255);

    static string ClassOf(nint hwnd)
    {
        var buffer = new StringBuilder(256);
        return hwnd == 0 ? "" : GetClassNameW(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : "";
    }

    static string TitleOf(nint hwnd)
    {
        var buffer = new StringBuilder(256);
        return hwnd == 0 ? "" : GetWindowTextW(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : "";
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SystemPowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    // CharSet.Unicode on the string calls: without it the W entry points get ANSI buffers and quietly
    // return nothing, which is the trap this project's probes have already fallen into once.
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, nuint extra);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(nint hwnd, StringBuilder name, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(nint hwnd, StringBuilder text, int size);
    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
