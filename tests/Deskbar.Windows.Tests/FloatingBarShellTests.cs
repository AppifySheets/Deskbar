using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Deskbar.App;
using Deskbar.Core;
using Deskbar.Core.Abstractions;
using Deskbar.Core.Persistence;
using Xunit.Abstractions;

namespace Deskbar.Windows.Tests;

// The taskbar's own buttons on the bar (Petre: "taskbar had some useful things, like a clock, start
// button, etc.", then "c" from three mockups): Start and search on the header line, and a strip under the
// last row with the tray chevron, quick settings, the bell, the clock and show desktop.
//
// No test here presses a real key. Every bar is built with SendShellKeys swapped for a list, because the
// real sender would open the Start menu on the screen of whoever runs the suite.
public class FloatingBarShellTests(ITestOutputHelper output)
{
    static (FloatingBar Bar, List<FloatingBar.ShellFlyout> Sent) Bar()
    {
        var desktops = new PulsingDesktops { CurrentId = Guid.NewGuid() };
        desktops.Desktops.Add(new DesktopInfo(desktops.CurrentId, "Work"));
        var manager = new WorkspaceManager(desktops, new StubMonitor(), new StubTitles(), new StubStore());
        Assert.True(manager.Start().IsSuccess);
        var sent = new List<FloatingBar.ShellFlyout>();
        // Parked off the virtual screen like every other bar test, so nothing flashes at whoever runs this.
        // ...and PressSpace a no-op, so the chevron's follow-up Space never reaches a real window either.
        var bar = new FloatingBar(manager) { Left = -32000, Top = -32000, SendShellKeys = sent.Add, PressSpace = () => { } };
        bar.ShowBar();
        bar.UpdateLayout();
        return (bar, sent);
    }

    // Petre: "i don't want wifi there", "and i don't think i want notifications either", "make it a setting,
    // what to show". Items switched off in Settings are not in the strip at all, and switching them back on
    // puts them back.
    [Fact]
    public void Items_switched_off_in_settings_leave_the_strip() => StaThread.Run(() =>
    {
        var desktops = new PulsingDesktops { CurrentId = Guid.NewGuid() };
        desktops.Desktops.Add(new DesktopInfo(desktops.CurrentId, "Work"));
        var manager = new WorkspaceManager(desktops, new StubMonitor(), new StubTitles(), new StubStore());
        Assert.True(manager.Start().IsSuccess);
        var bar = new FloatingBar(manager) { Left = -32000, Top = -32000, SendShellKeys = _ => { }, PressSpace = () => { } };
        bar.ShowBar();
        bar.UpdateLayout();
        var strip = (DockPanel)bar.FindName("ShellStrip")!;
        string[] Names() => strip.Children.OfType<Button>().Select(b => AutomationProperties.GetName(b)).ToArray();
        Assert.Contains("Notifications", Names());

        Assert.True(manager.SetHiddenStripItems([StripItem.QuickSettings, StripItem.Notifications]).IsSuccess);
        bar.UpdateLayout();
        Assert.DoesNotContain("Notifications", Names());
        Assert.DoesNotContain("Quick settings: wifi, volume, battery", Names());
        Assert.Contains("Clock", Names());

        Assert.True(manager.SetHiddenStripItems([]).IsSuccess);
        bar.UpdateLayout();
        Assert.Contains("Notifications", Names());
        bar.Close();
    });

    // "including icons at the bottom": the strip's icon buttons follow the icon size setting, and the clock, which
    // is text, does not.
    [Fact]
    public void The_strips_icons_follow_the_icon_size_and_the_clock_does_not() => StaThread.Run(() =>
    {
        var desktops = new PulsingDesktops { CurrentId = Guid.NewGuid() };
        desktops.Desktops.Add(new DesktopInfo(desktops.CurrentId, "Work"));
        var manager = new WorkspaceManager(desktops, new StubMonitor(), new StubTitles(), new StubStore());
        Assert.True(manager.Start().IsSuccess);
        var bar = new FloatingBar(manager) { Left = -32000, Top = -32000, SendShellKeys = _ => { }, PressSpace = () => { } };
        bar.ShowBar();
        bar.UpdateLayout();
        var strip = (DockPanel)bar.FindName("ShellStrip")!;
        Button Named(string name) => strip.Children.OfType<Button>().Single(b => AutomationProperties.GetName(b) == name);
        // DesiredSize, which includes a LayoutTransform; ActualWidth is the size before it.
        var before = Named("Notifications").DesiredSize.Width;

        Assert.True(manager.SetIconScale(1.5).IsSuccess);
        bar.UpdateLayout();

        Assert.Equal(before * 1.5, Named("Notifications").DesiredSize.Width, 1);
        Assert.Equal(1.5, ((ScaleTransform)Named("Show the tray icons").LayoutTransform).ScaleX, 3);
        Assert.True(Named("Clock").LayoutTransform is null || Named("Clock").LayoutTransform == Transform.Identity);
        bar.Close();
    });

    // Petre, on a laptop strip with the clock cut to "12:(" and no room left for the language switcher: "i want
    // language as a priority". When not even the time fits beside everything else, the clock goes, rather than
    // the switcher being squeezed out.
    [Fact]
    public void A_strip_too_narrow_even_for_the_time_drops_the_clock() => StaThread.Run(() =>
    {
        var (bar, _) = Bar();
        var strip = (DockPanel)bar.FindName("ShellStrip")!;
        var clock = strip.Children.OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Clock");
        var time = ((StackPanel)clock.Content).Children.OfType<TextBlock>().First();

        strip.Width = 600;
        bar.UpdateLayout();
        var (_, others, _) = bar.lastClockFit;
        var timeOnly = clock.Padding.Left + clock.Padding.Right +
            new FormattedText(time.Text, System.Globalization.CultureInfo.CurrentCulture, time.FlowDirection,
                new Typeface(time.FontFamily, time.FontStyle, time.FontWeight, time.FontStretch), time.FontSize,
                System.Windows.Media.Brushes.White, VisualTreeHelper.GetDpi(time).PixelsPerDip).WidthIncludingTrailingWhitespace;

        strip.Width = others + timeOnly - 0.5;
        bar.UpdateLayout();
        Assert.Equal(Visibility.Collapsed, clock.Visibility);

        strip.Width = others + timeOnly + 0.5;
        bar.UpdateLayout();
        Assert.Equal(Visibility.Visible, clock.Visibility);
        bar.Close();
    });

    static Button Named(FloatingBar bar, string panel, string name) =>
        ((Panel)bar.FindName(panel)!).Children.OfType<Button>().Single(b => AutomationProperties.GetName(b) == name);

    static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    // Start and search at the LEFT end of the header line, the bar's own two commands still at its right,
    // and the line itself inside the pinned row, which is where the header has lived since #173. Measured
    // by position, not just by alignment property, because a header that did not stretch would put all
    // four buttons in a lump at one end while every property still read correctly.
    [Fact]
    public void Start_and_search_sit_at_the_left_of_the_header_line() => StaThread.Run(() =>
    {
        var (bar, _) = Bar();
        var header = (Grid)bar.FindName("BarHeader")!;
        var start = Named(bar, "ShellStart", "Start");
        var search = Named(bar, "ShellStart", "Search");
        var commands = (FrameworkElement)bar.FindName("BarCommands")!;

        var startX = start.TranslatePoint(new Point(0, 0), header).X;
        var searchX = search.TranslatePoint(new Point(0, 0), header).X;
        var commandsRight = commands.TranslatePoint(new Point(commands.ActualWidth, 0), header).X;
        output.WriteLine($"header={header.ActualWidth:F1} start={startX:F1} search={searchX:F1} commandsRight={commandsRight:F1}");

        Assert.Equal(0, startX, 1);
        Assert.True(searchX > startX);
        Assert.Equal(header.ActualWidth, commandsRight, 1);
        Assert.True(commandsRight - commands.ActualWidth > searchX + search.ActualWidth, "the two ends overlap");
        Assert.Contains((Panel)bar.FindName("Rows")!, Ancestors(header).OfType<Panel>());

        bar.Close();
    });

    // The strip is under the LAST row, so the top of the bar never moves for it, and it holds every button
    // the mockup showed.
    [Fact]
    public void The_strip_sits_under_the_rows_with_every_button() => StaThread.Run(() =>
    {
        var (bar, _) = Bar();
        var footer = (FrameworkElement)bar.FindName("ShellFooter")!;
        var rows = (FrameworkElement)bar.FindName("Rows")!;
        var root = (FrameworkElement)bar.FindName("Root")!;

        var rowsBottom = rows.TranslatePoint(new Point(0, rows.ActualHeight), root).Y;
        var footerTop = footer.TranslatePoint(new Point(0, 0), root).Y;
        output.WriteLine($"rowsBottom={rowsBottom:F1} footerTop={footerTop:F1}");
        Assert.True(footerTop >= rowsBottom);

        new[] { "Show the tray icons", "Quick settings: wifi, volume, battery", "Notifications", "Clock", "Show desktop" }
            .ToList().ForEach(name => Assert.NotNull(Named(bar, "ShellStrip", name)));

        bar.Close();
    });

    // Petre: "pin it to the bottom". Docked, the bar is the screen's full height (SizeToContent Manual and a
    // fixed Height, which is what DockToEdge sets, done here directly so no test registers a real app bar),
    // and the strip sits at the foot of it, where the taskbar was, rather than straight under the last row.
    [Fact]
    public void A_full_height_bar_keeps_the_strip_at_its_foot() => StaThread.Run(() =>
    {
        var (bar, _) = Bar();
        bar.SizeToContent = SizeToContent.Manual;
        bar.Width = 200;
        bar.Height = 900;
        bar.UpdateLayout();

        var footer = (FrameworkElement)bar.FindName("ShellFooter")!;
        var rows = (FrameworkElement)bar.FindName("Rows")!;
        var root = (Border)bar.FindName("Root")!;
        var footerBottom = footer.TranslatePoint(new Point(0, footer.ActualHeight), root).Y;
        var rowsBottom = rows.TranslatePoint(new Point(0, rows.ActualHeight), root).Y;
        output.WriteLine($"root={root.ActualHeight:F1} footerBottom={footerBottom:F1} rowsBottom={rowsBottom:F1}");

        Assert.Equal(root.ActualHeight - root.Padding.Bottom, footerBottom, 1);
        Assert.True(footerBottom - footer.ActualHeight > rowsBottom + 400, "the strip is still under the rows");

        bar.Close();
    });

    // Docked across two screens that are not level (Petre: "when monitors aren't exactly aligned and i have the
    // deskbar in the middle"): the window fills both reserved strips, and the content is inset to the height the
    // screens share, so nothing is half off one screen.
    [Theory]
    [InlineData(0, 2160, 0, 2160, 0, 2160, 0, 0)]          // level: no inset, as before
    [InlineData(-300, 1860, 0, 2160, -300, 2160, 300, 300)] // left screen 300 px higher
    [InlineData(0, 1440, 0, 2160, 0, 2160, 0, 720)]         // left screen shorter, tops level
    [InlineData(200, 1640, 0, 2160, 0, 2160, 200, 520)]     // left screen shorter and lower
    public void Across_uneven_screens_the_bar_fills_both_strips_and_keeps_to_the_shared_height(
        int leftTop, int leftBottom, int rightTop, int rightBottom, int windowTop, int windowBottom, int insetTop, int insetBottom) =>
        Assert.Equal((windowTop, windowBottom, insetTop, insetBottom), FloatingBar.SeamSpan(leftTop, leftBottom, rightTop, rightBottom));

    // The clock shows the time in the user's own short format, the one the taskbar uses. Read before and
    // after, so a minute turning over mid-test cannot fail it.
    [Fact]
    public void The_clock_shows_the_time_in_the_regional_format() => StaThread.Run(() =>
    {
        var before = DateTime.Now.ToString("t");
        var (bar, _) = Bar();
        var after = DateTime.Now.ToString("t");

        var face = (StackPanel)Named(bar, "ShellStrip", "Clock").Content;
        var time = ((TextBlock)face.Children[0]).Text;
        var date = ((TextBlock)face.Children[1]).Text;
        output.WriteLine($"time={time} date={date}");

        Assert.Contains(time, new[] { before, after });
        Assert.Equal(DateTime.Now.ToString("d"), date);

        bar.Close();
    });

    // A click on a button presses its shortcut, once.
    [Fact]
    public void Clicking_start_presses_the_windows_key() => StaThread.Run(() =>
    {
        var (bar, sent) = Bar();
        bar.NoteBarPressed();
        Click(Named(bar, "ShellStart", "Start"));

        Assert.Equal([FloatingBar.ShellFlyout.Start], sent);
        bar.Close();
    });

    // THE trap: the press on the bar takes the foreground from an open Start menu, which closes it, so by the
    // time Click runs the menu is already gone. Pressing Win then would open it again, and Start would be a
    // button that can never close what it opened. The next click, with nothing open, opens it as normal.
    [Fact]
    public void Clicking_start_while_the_start_menu_is_open_leaves_it_closed() => StaThread.Run(() =>
    {
        var (bar, sent) = Bar();
        var start = Named(bar, "ShellStart", "Start");

        bar.NoteFlyoutInFront(FloatingBar.ShellFlyout.Start);
        bar.NoteBarPressed();
        Click(start);
        Assert.Empty(sent);

        bar.NoteBarPressed();
        Click(start);
        Assert.Equal([FloatingBar.ShellFlyout.Start], sent);

        bar.Close();
    });

    // Issue #227: on Windows 11's newer tray the chevron opened the hidden icons but never closed them. The
    // overflow there has a class the bar did not know, so the second press read as "nothing open" and sent the
    // shortcut again. Now the window that appeared after the bar's own Space is remembered as the tray: with it
    // in front, the chevron's next click stands down (the press already closed it), and the click after opens
    // it again. A window that never came from the chevron is not mistaken for it.
    [Fact]
    public void The_chevron_closes_an_overflow_whose_class_it_does_not_know() => StaThread.Run(() =>
    {
        var (bar, sent) = Bar();
        var chevron = Named(bar, "ShellStrip", "Show the tray icons");
        var overflow = (nint)0x7E57;

        bar.RememberTrayOverflow(overflow);
        bar.NoteForeground(overflow);
        bar.NoteBarPressed();
        Click(chevron);
        Assert.Empty(sent);

        bar.NoteForeground(0);
        bar.NoteBarPressed();
        Click(chevron);
        Assert.Equal([FloatingBar.ShellFlyout.Tray], sent);

        sent.Clear();
        bar.NoteForeground(0x0BAD);
        bar.NoteBarPressed();
        Click(chevron);
        Assert.Equal([FloatingBar.ShellFlyout.Tray], sent);
        bar.Close();
    });

    // Petre, with a narrow bar cutting the clock to "13:" over "20": "date. don't show it if it's like that".
    // A strip too narrow for its buttons and both clock lines drops the date line, and the time then fits;
    // given room again, the date comes back.
    [Fact]
    public void A_strip_too_narrow_for_the_date_shows_only_the_time() => StaThread.Run(() =>
    {
        var (bar, _) = Bar();
        var strip = (DockPanel)bar.FindName("ShellStrip")!;
        var clock = strip.Children.OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Clock");
        var lines = ((StackPanel)clock.Content).Children.OfType<TextBlock>().ToList();

        strip.Width = 600;
        bar.UpdateLayout();
        Assert.Equal(Visibility.Visible, lines[1].Visibility);

        // What the strip needs WITH the date line, as the check itself reckons it. Widths come from that rather
        // than from the two lines' sizes, because how much wider the date is than the time depends on the
        // regional format the suite runs under (half a DIP on one machine, measured).
        var (_, others, clockWithDate) = bar.lastClockFit;
        var needed = others + clockWithDate;
        output.WriteLine($"others {others:0.00}, clock with date {clockWithDate:0.00}, needed {needed:0.00}");

        // A hair short of that: the date goes, the time stays.
        strip.Width = needed - 0.5;
        bar.UpdateLayout();
        Assert.Equal(Visibility.Collapsed, lines[1].Visibility);
        Assert.Equal(Visibility.Visible, lines[0].Visibility);

        // And it STAYS hidden on the next check: a collapsed date measures 0 wide, and a check that measured the
        // element would read that as "fits" and show it again, flickering every minute.
        strip.Width = needed - 0.25;
        bar.UpdateLayout();
        Assert.Equal(Visibility.Collapsed, lines[1].Visibility);

        // Room for it again: back it comes.
        strip.Width = needed + 0.5;
        bar.UpdateLayout();
        Assert.Equal(Visibility.Visible, lines[1].Visibility);
        bar.Close();
    });

    // Only the button for the flyout that was open stands down: with the Start menu up, a click on search
    // still opens search.
    [Fact]
    public void Another_flyout_being_open_does_not_stop_a_button() => StaThread.Run(() =>
    {
        var (bar, sent) = Bar();
        bar.NoteFlyoutInFront(FloatingBar.ShellFlyout.Start);
        bar.NoteBarPressed();
        Click(Named(bar, "ShellStart", "Search"));

        Assert.Equal([FloatingBar.ShellFlyout.Search], sent);
        bar.Close();
    });

    // The clock opens the calendar and notifications, as the taskbar's clock does.
    [Fact]
    public void Clicking_the_clock_opens_notifications() => StaThread.Run(() =>
    {
        var (bar, sent) = Bar();
        bar.NoteBarPressed();
        Click(Named(bar, "ShellStrip", "Clock"));

        Assert.Equal([FloatingBar.ShellFlyout.Notifications], sent);
        bar.Close();
    });

    // A press on the strip is a click, never the start of dragging the bar around.
    [Fact]
    public void A_press_on_the_strip_does_not_drag_the_bar() => StaThread.Run(() =>
    {
        var (bar, _) = Bar();
        Assert.True(bar.StartedOnClickTarget(Named(bar, "ShellStrip", "Clock")));
        Assert.True(bar.StartedOnClickTarget(Named(bar, "ShellStart", "Start")));
        bar.Close();
    });

    // Petre: "make right-clicking possible in the empty area when docked". The bare background carries a menu
    // of its own, with a new workspace and the bar's own items, and the dock item says what it will do NOW.
    [Fact]
    public void The_bare_background_has_a_menu() => StaThread.Run(() =>
    {
        var (bar, _) = Bar();
        var root = (Border)bar.FindName("Root")!;
        Assert.NotNull(root.ContextMenu);

        bar.FillBarMenu(root.ContextMenu);
        // "Dock across both screens" appears only on a machine with two screens side by side, so it is left out
        // of the comparison: what it depends on is the desk the suite runs on, not the menu.
        var headers = root.ContextMenu.Items.OfType<MenuItem>().Select(i => (string)i.Header).Where(h => h != "Dock across both screens").ToList();
        output.WriteLine(string.Join(" | ", headers));

        Assert.Equal(["New workspace…", "Window list at the bottom of the bar", "Dock to screen edge", "Minimize bar", "Task Manager"], headers);
        bar.Close();
    });

    // Petre: "include all non-workspace specific options here as well". With the app's commands supplied, the
    // tray's items follow the bar's own, and each runs the action it was given.
    [Fact]
    public void The_background_menu_carries_the_app_wide_commands() => StaThread.Run(() =>
    {
        var ran = new List<string>();
        var desktops = new PulsingDesktops { CurrentId = Guid.NewGuid() };
        desktops.Desktops.Add(new DesktopInfo(desktops.CurrentId, "Work"));
        var manager = new WorkspaceManager(desktops, new StubMonitor(), new StubTitles(), new StubStore());
        Assert.True(manager.Start().IsSuccess);
        var bar = new FloatingBar(manager)
        {
            Left = -32000, Top = -32000, SendShellKeys = _ => { },
            AppActions = new FloatingBar.AppCommands(() => ran.Add("manage"), () => ran.Add("updates"), () => ran.Add("exit"), () => null,
                SaveDebugDump: () => ran.Add("dump")),
            OpenTaskManager = () => ran.Add("taskmgr"),
        };
        bar.ShowBar();
        var menu = ((Border)bar.FindName("Root")!).ContextMenu!;

        bar.FillBarMenu(menu);
        var items = menu.Items.OfType<MenuItem>().ToList();
        output.WriteLine(string.Join(" | ", items.Select(i => (string)i.Header)));
        Assert.Equal(["New workspace…", "Window list at the bottom of the bar", "Dock to screen edge", "Minimize bar", "Task Manager", "Manage…", "Check for updates…", "Save debug dump", "Exit"],
            items.Select(i => (string)i.Header).Where(h => h != "Dock across both screens"));

        items.Single(i => (string)i.Header == "Task Manager").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        items.Single(i => (string)i.Header == "Manage…").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        items.Single(i => (string)i.Header == "Check for updates…").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        items.Single(i => (string)i.Header == "Save debug dump").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(["taskmgr", "manage", "updates", "dump"], ran);
        bar.Close();
    });

    // The shortcuts, as measured on Petre's machine (see FloatingBar.Shell.cs). By name, because the enum is
    // internal and an xUnit theory has to be public.
    [Theory]
    [InlineData("Start", new byte[] { 0x5B })]
    [InlineData("Search", new byte[] { 0x5B, (byte)'S' })]
    [InlineData("Tray", new byte[] { 0x5B, (byte)'B' })]
    [InlineData("QuickSettings", new byte[] { 0x5B, (byte)'A' })]
    [InlineData("Notifications", new byte[] { 0x5B, (byte)'N' })]
    [InlineData("Desktop", new byte[] { 0x5B, (byte)'D' })]
    public void Each_button_presses_its_windows_shortcut(string flyout, byte[] keys) =>
        Assert.Equal(keys, ShellKeys.KeysOf(Enum.Parse<FloatingBar.ShellFlyout>(flyout)));

    static IEnumerable<DependencyObject> Ancestors(DependencyObject from)
    {
        for (var node = LogicalTreeHelper.GetParent(from); node is not null; node = LogicalTreeHelper.GetParent(node))
            yield return node;
    }
}
