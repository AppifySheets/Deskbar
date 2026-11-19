using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core;
using Deskbar.Core.Abstractions;
using Deskbar.Core.Domain;
using Deskbar.Core.Overview;
using Deskbar.Core.Persistence;

namespace Deskbar.Windows.Tests;

// The bar hands every screen's strip that screen's tabs (FloatingBar.Strips.cs; spec
// docs/superpowers/specs/2026-11-19-screen-strips-design.md, "What a tab does" and "The parts"). The tabs are built by
// the code that builds the bar's own window buttons, so these tests check the handing over, the tab's own dressing,
// and the few places where a button living in ANOTHER window needed the bar to look past its own edges: the press
// state and the hover card (docs/superpowers/notes/2026-11-19-screen-strips-probes.md, section 2).
//
// No strip here is docked: each one gets a recording DockWith, as ScreenStripTests' do, so no test reserves a band of
// a real screen. Screens are made-up rectangles far off every real monitor, and the screen layout is a fake, so the
// display numbers and where each window sits are the test's to say.
public class FloatingBarStripsTests
{
    // Three made-up screens side by side, 1200 px wide at 100%. Screen 3 holds no window at all, which is the case
    // the drop onto an empty screen needs.
    static readonly MonitorBounds ScreenA = new(-30000, -30000, -28800, -29200);
    static readonly MonitorBounds ScreenB = new(-28800, -30000, -27600, -29200);
    static readonly MonitorBounds ScreenC = new(-27600, -30000, -26400, -29200);

    static readonly Color RestUnderline = Color.FromArgb(0xFF, 0x3A, 0x3F, 0x49);
    static readonly Color ActiveFill = Color.FromArgb(0xFF, 0x1F, 0x23, 0x2B);

    // With the place Screens the bar draws no list section and no expanded row: the strips are the list.
    [Fact]
    public void Screens_place_builds_no_list_and_expands_no_row() => StaThread.Run(() =>
    {
        using var h = StripHarness.Build();
        h.Fill();

        Assert.Empty(h.CurrentList.Children);
        // A titled line carries the window's title as text; an icon row carries none.
        Assert.DoesNotContain(TextsIn(h.Rows), t => t == h.Rdm.Title || t == h.Code.Title);
    });

    // Each strip lists the current workspace's windows on ITS screen, under the workspace's name.
    [Fact]
    public void Each_strip_gets_its_own_screens_windows() => StaThread.Run(() =>
    {
        using var h = StripHarness.Build();
        h.Fill();

        Assert.Equal([h.Rdm.Title], NamesOf(h.StripA));
        // The pinned window is on screen 2 too, after the workspace's own window, as in the titled list.
        Assert.Equal([h.Code.Title, h.Pinned.Title], NamesOf(h.StripB));
        Assert.All(h.Strips, strip => Assert.Equal("GEPHA", TagText(strip)));
        // Sparrow's window is on screen 1 but not in the workspace you are on.
        Assert.DoesNotContain(h.Beeper.Title, NamesOf(h.StripA));
    });

    // A screen with no window of the current workspace keeps its strip, with only the name tag on it.
    [Fact]
    public void A_screen_with_no_windows_gets_an_empty_tab_list_and_the_name() => StaThread.Run(() =>
    {
        using var h = StripHarness.Build();
        h.Fill();

        Assert.Same(h.StripC.NameTag, Assert.Single(h.StripC.Row.Children.Cast<UIElement>()));
        Assert.Equal("GEPHA", TagText(h.StripC));
        // ...on the colour the bar gives GEPHA's row: the first workspace, no colour of its own.
        Assert.Equal(Hex(WorkspacePalette.For((string?)null, 0)), ((SolidColorBrush)h.StripC.NameTag.Background).Color);
    });

    // The window you are in: the lighter fill, the white title and the underline in the workspace's colour. Every
    // other tab: the grey underline and no fill.
    [Fact]
    public void The_active_window_tab_carries_the_accent_underline() => StaThread.Run(() =>
    {
        using var h = StripHarness.Build();
        h.Fill();
        h.Monitor.Pump.OnNext(new WindowEvent(WindowEventKind.Activated, h.Code));

        var active = TabFor(h.StripB, h.Code.Title);
        var resting = TabFor(h.StripB, h.Pinned.Title);
        var accent = ((SolidColorBrush)h.StripB.NameTag.Background).Color;

        Assert.Equal(accent, ColourOf(Underline(active).Background));
        Assert.Equal(ActiveFill, ColourOf(((Panel)active.Content).Background));
        Assert.Equal(Colors.White, ColourOf(Title(active).Foreground));

        Assert.Equal(RestUnderline, ColourOf(Underline(resting).Background));
        Assert.NotEqual(ActiveFill, ColourOf(((Panel)resting.Content).Background));
    });

    // A grouped workspace's tag wears the GROUP's colour, the one the bar paints that workspace's row in.
    [Fact]
    public void The_name_tag_wears_the_group_colour() => StaThread.Run(() =>
    {
        using var h = StripHarness.Build(groupColour: "#3070D0");
        h.Fill();

        Assert.Equal(Hex("#3070D0"), ((SolidColorBrush)h.StripA.NameTag.Background).Color);
    });

    // A pinned window is listed on the screen it is on, with the pin badge on its icon, as in the titled list.
    [Fact]
    public void A_pinned_window_is_on_its_screens_strip_with_the_badge() => StaThread.Run(() =>
    {
        using var h = StripHarness.Build();
        h.Fill();

        Assert.Contains(Descendants(TabFor(h.StripB, h.Pinned.Title)), d => d is FrameworkElement { Tag: FloatingBar.PinBadgeTag });
        Assert.DoesNotContain(Descendants(TabFor(h.StripB, h.Code.Title)), d => d is FrameworkElement { Tag: FloatingBar.PinBadgeTag });
    });

    // The strips are the window list, so they stay current while the bar stands behind its taskbar button; the bar
    // itself skips its own rows then.
    [Fact]
    public void A_minimized_bar_still_fills_the_strips() => StaThread.Run(() =>
    {
        using var h = StripHarness.Build();
        h.Fill();
        h.Bar.MinimizeToTaskbar();
        var rowsBefore = h.Rows.Children.Cast<UIElement>().ToList();
        var tabBefore = TabFor(h.StripB, h.Code.Title);

        h.Monitor.Pump.OnNext(new WindowEvent(WindowEventKind.Activated, h.Code));

        var tabAfter = TabFor(h.StripB, h.Code.Title);
        Assert.NotSame(tabBefore, tabAfter);
        Assert.Equal(ActiveFill, ColourOf(((Panel)tabAfter.Content).Background));
        Assert.Equal(rowsBefore, h.Rows.Children.Cast<UIElement>().ToList());
    });

    // A window event landing in the middle of a rebuild's query (a COM call pumps messages) re-enters the rebuild.
    // The strips are filled inside the bar's guard, so each still holds one set of tabs.
    [Fact]
    public void A_reentrant_rebuild_does_not_double_the_tabs() => StaThread.Run(() =>
    {
        using var h = StripHarness.Build();
        h.Fill();

        h.Desktops.PulseOnNextDesktopOf = () => h.Monitor.Pump.OnNext(new WindowEvent(WindowEventKind.Activated, h.Rdm));
        h.Monitor.Pump.OnNext(new WindowEvent(WindowEventKind.Activated, h.Code));

        Assert.Equal([h.Rdm.Title], NamesOf(h.StripA));
        Assert.Equal([h.Code.Title, h.Pinned.Title], NamesOf(h.StripB));
    });

    // A click on a tab is the button's own click: the manager's toggle, which lands on the window's desktop. The
    // fixture moves "where you are" behind the bar's back, so the toggle has a switch to make and the switch shows.
    //
    // Click is raised directly, as the back button's test does. Synthesised mouse events cannot press a ButtonBase
    // here: it reads the REAL mouse's button state on press, which is up, so it never takes capture and never raises
    // Click (measured: IsPressed and IsMouseCaptured both stay false, for a bar icon as much as a tab).
    [Fact]
    public void Clicking_a_tab_toggles_the_window_through_the_manager() => StaThread.Run(() =>
    {
        using var h = StripHarness.Build();
        h.Fill();
        var tab = TabFor(h.StripB, h.Code.Title);
        h.Desktops.CurrentId = h.SparrowDesktop;

        tab.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        Assert.Equal([h.GephaDesktop], h.Desktops.Switched);
    });

    // The bar's own release handler left "orphan" set (a release with no press seen). A press and release on a
    // strip must reset that state as one on the bar does, or the icon's orphan rescue runs as well as its click and
    // toggles the window twice: once up, once back down.
    //
    // The press and release go through the strip's window as the input manager delivers them, tunnelling preview
    // first, which is where the strip forwards them; the button's own Click, which real input would raise from the
    // release, is raised by hand for the reason the test above gives.
    [Fact]
    public void A_strip_press_then_release_is_not_an_orphan() => StaThread.Run(() =>
    {
        using var h = StripHarness.Build();
        h.Fill();
        // A release on the bar with no press before it: the bar marks it an orphan.
        h.Rows.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent });
        var tab = TabFor(h.StripB, h.Code.Title);
        h.Desktops.CurrentId = h.SparrowDesktop;
        var asked = h.Desktops.DesktopOfCalls;

        Press(tab);
        Release(tab);
        tab.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        // One toggle asks the shell where the window is once. Two would be the click and the orphan rescue both.
        Assert.Equal(1, h.Desktops.DesktopOfCalls - asked);
    });

    // The hover card for a tab opens below the tab, placed against it. The bar's placement measures the anchor in the
    // bar's own coordinates and opens the card beside the bar: for a tab, on another screen's edge entirely. The probe
    // notes expected that measurement to throw for an element in another window; measured here, it does not (WPF
    // translates through screen coordinates), it only lands the card in the wrong place, which is what this pins.
    [Fact]
    public void A_hover_card_for_a_tab_opens_below_the_tab_without_throwing() => StaThread.Run(() =>
    {
        using var h = StripHarness.Build();
        h.Fill();
        // Shown (parked off every screen, never docked), so the tab really is in a window of its own on screen.
        h.StripA.Left = -32000;
        h.StripA.Top = -32000;
        h.StripA.Show();
        var tab = TabFor(h.StripA, h.Rdm.Title);

        h.Bar.ShowHoverCard("GEPHA", (WindowRow)tab.DataContext, tab, here: false);

        var popup = h.Bar.HoverPopup!;
        try
        {
            Assert.True(popup.IsOpen);
            Assert.Same(tab, popup.PlacementTarget);
            Assert.Equal(PlacementMode.Bottom, popup.Placement);
        }
        finally
        {
            popup.IsOpen = false;
        }
    });

    // A tab too narrow for a letter of its title shows its icon alone. The strip sets the width; the tab follows it.
    [Fact]
    public void A_narrow_tab_hides_its_title() => StaThread.Run(() =>
    {
        using var h = StripHarness.Build();
        h.Fill();
        var tab = TabFor(h.StripA, h.Rdm.Title);
        Assert.Equal(Visibility.Visible, Title(tab).Visibility);

        tab.Width = Core.Geometry.StripPlan.TabIconOnly;
        Assert.Equal(Visibility.Collapsed, Title(tab).Visibility);

        tab.Width = Core.Geometry.StripPlan.TabMax;
        Assert.Equal(Visibility.Visible, Title(tab).Visibility);
    });

    // A window dropped on a strip goes to the workspace you are on, on that strip's screen.
    [Fact]
    public void A_drop_on_a_strip_assigns_to_the_current_workspace_on_that_screen() => StaThread.Run(() =>
    {
        using var h = StripHarness.Build();
        h.Fill();

        h.Bar.DropOnStrip(h.StripB, h.Beeper.Handle);

        Assert.Equal(h.GephaDesktop, h.Desktops.Placements[h.Beeper.Handle]);
        Assert.True(h.Screens.LandedOn(h.Beeper.Handle, ScreenB), h.Screens.Describe());
    });

    // ...and a strip on a screen that holds no window still knows its screen's number, so the drop lands there.
    [Fact]
    public void A_drop_on_a_strip_whose_screen_has_no_windows_lands_on_that_screen() => StaThread.Run(() =>
    {
        using var h = StripHarness.Build();
        h.Fill();

        h.Bar.DropOnStrip(h.StripC, h.Rdm.Handle);

        Assert.True(h.Screens.LandedOn(h.Rdm.Handle, ScreenC), h.Screens.Describe());
    });

    // On an unnamed desktop there is no workspace to assign to: the drop moves the window to that strip's screen
    // and leaves its desktop alone. The tag names the desktop on the neutral grey.
    [Fact]
    public void A_drop_on_a_strip_on_an_unnamed_desktop_moves_to_that_monitor() => StaThread.Run(() =>
    {
        using var h = StripHarness.Build();
        h.Fill();
        h.Desktops.CurrentId = h.UnnamedDesktop;
        h.Monitor.Pump.OnNext(new WindowEvent(WindowEventKind.Activated, h.Notepad));
        Assert.Equal("Desktop 1", TagText(h.StripA));
        Assert.Equal([h.Notepad.Title], NamesOf(h.StripA));
        Assert.Equal(RestUnderline, ((SolidColorBrush)h.StripA.NameTag.Background).Color);

        h.Bar.DropOnStrip(h.StripB, h.Notepad.Handle);

        Assert.True(h.Screens.LandedOn(h.Notepad.Handle, ScreenB), h.Screens.Describe());
        Assert.Equal(h.UnnamedDesktop, h.Desktops.Placements[h.Notepad.Handle]);
    });

    // With the list in the row or at the bottom the strips are not the bar's business: nothing is handed over.
    [Theory]
    [InlineData(WindowListPlace.Row)]
    [InlineData(WindowListPlace.Bottom)]
    public void Other_places_fill_nothing(WindowListPlace place) => StaThread.Run(() =>
    {
        using var h = StripHarness.Build();
        Assert.True(h.Manager.SetWindowListPlace(place).IsSuccess);
        h.Monitor.Pump.OnNext(new WindowEvent(WindowEventKind.Activated, h.Code));

        Assert.All(h.Strips, strip =>
        {
            Assert.Single(strip.Row.Children.Cast<UIElement>());
            Assert.Equal("", TagText(strip));
        });
    });

    // --- helpers ------------------------------------------------------------------------------------------------

    static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    static Color ColourOf(Brush? brush) => brush is SolidColorBrush solid ? solid.Color : Colors.Transparent;

    static string TagText(ScreenStrip strip) => ((TextBlock)strip.NameTag.Child).Text;

    // The tabs on a strip, in order: every child of its row after the name tag.
    static IReadOnlyList<Button> TabsOf(ScreenStrip strip) => strip.Row.Children.Cast<UIElement>().Skip(1).Cast<Button>().ToList();

    static IReadOnlyList<string> NamesOf(ScreenStrip strip) =>
        TabsOf(strip).Select(System.Windows.Automation.AutomationProperties.GetName).ToList();

    static Button TabFor(ScreenStrip strip, string title) =>
        TabsOf(strip).Single(t => System.Windows.Automation.AutomationProperties.GetName(t) == title);

    static Border Underline(Button tab) =>
        Descendants(tab).OfType<Border>().Single(b => b.Tag is FloatingBar.StripUnderlineTag);

    static TextBlock Title(Button tab) =>
        Descendants(tab).OfType<TextBlock>().Single(t => t.Tag is FloatingBar.StripTitleTag);

    static IReadOnlyList<string> TextsIn(DependencyObject root) => Descendants(root).OfType<TextBlock>().Select(t => t.Text).ToList();

    // The logical tree, flattened: these elements are built but not all laid out, so templates may not have expanded.
    static IReadOnlyList<DependencyObject> Descendants(DependencyObject root) =>
        LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()
            .SelectMany(child => Descendants(child).Prepend(child))
            .ToList();

    // Both halves of a press and of a release, as the input manager delivers them (see FloatingBarRebuildTests.Press):
    // the tunnelling preview, which is where a strip forwards the press state to the bar, then the bubbling event,
    // where ButtonBase takes capture and later raises Click.
    static void Press(UIElement target)
    {
        target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
        target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseDownEvent });
    }

    static void Release(UIElement target)
    {
        target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent });
        target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent });
    }

    // Two workspaces and an unnamed desktop over three screens:
    //
    //   GEPHA (current)   rdm on screen 1, code on screen 2
    //   Sparrow           beeper on screen 1
    //   pinned            a window on screen 2, on every desktop
    //   Desktop 1         notepad on screen 1, no workspace
    //   screen 3          nothing at all
    //
    // The place starts as Row, so the bar's first rebuild hands nothing over; Fill moves it to Screens, whose pulse
    // is the rebuild that fills the strips.
    sealed class StripHarness : IDisposable
    {
        public required PulsingDesktops Desktops { get; init; }
        public required StubMonitor Monitor { get; init; }
        public required WorkspaceManager Manager { get; init; }
        public required FakeScreens Screens { get; init; }
        public required FloatingBar Bar { get; init; }
        public required ScreenStrip StripA { get; init; }
        public required ScreenStrip StripB { get; init; }
        public required ScreenStrip StripC { get; init; }
        public required WindowInfo Rdm { get; init; }
        public required WindowInfo Code { get; init; }
        public required WindowInfo Beeper { get; init; }
        public required WindowInfo Pinned { get; init; }
        public required WindowInfo Notepad { get; init; }
        public required Guid GephaDesktop { get; init; }
        public required Guid SparrowDesktop { get; init; }
        public required Guid UnnamedDesktop { get; init; }

        public IReadOnlyList<ScreenStrip> Strips => [StripA, StripB, StripC];
        public Panel Rows => (Panel)Bar.FindName("Rows")!;
        public Panel CurrentList => (Panel)Bar.FindName("CurrentList")!;

        public void Fill() => Assert.True(Manager.SetWindowListPlace(WindowListPlace.Screens).IsSuccess);

        public static StripHarness Build(string? groupColour = null)
        {
            var gephaDesktop = Guid.NewGuid();
            var sparrowDesktop = Guid.NewGuid();
            var unnamed = Guid.NewGuid();
            var team = new Group(Guid.NewGuid(), "Team") { Color = groupColour };
            var gepha = new Workspace(Guid.NewGuid(), "GEPHA", gephaDesktop) { GroupId = groupColour is null ? null : team.Id };
            var sparrow = new Workspace(Guid.NewGuid(), "Sparrow", sparrowDesktop) { GroupId = groupColour is null ? null : team.Id };

            var desktops = new PulsingDesktops { CurrentId = gephaDesktop };
            desktops.Desktops.Add(new DesktopInfo(gephaDesktop, "GEPHA"));
            desktops.Desktops.Add(new DesktopInfo(sparrowDesktop, "Sparrow"));
            desktops.Desktops.Add(new DesktopInfo(unnamed, "Desktop 1"));

            var rdm = new WindowInfo(new WindowHandle(101), 11, "rdm", @"C:\rdm.exe", "Remote Desktop Manager", null);
            var code = new WindowInfo(new WindowHandle(102), 12, "code", @"C:\code.exe", "FloatingBar.xaml.cs", null);
            var beeper = new WindowInfo(new WindowHandle(202), 22, "beeper", @"C:\beeper.exe", "Beeper", null);
            var pinned = new WindowInfo(new WindowHandle(303), 33, "clock", @"C:\clock.exe", "World clock", null);
            var notepad = new WindowInfo(new WindowHandle(505), 55, "notepad", @"C:\notepad.exe", "Untitled", null);
            desktops.Placements[rdm.Handle] = gephaDesktop;
            desktops.Placements[code.Handle] = gephaDesktop;
            desktops.Placements[beeper.Handle] = sparrowDesktop;
            desktops.Placements[pinned.Handle] = gephaDesktop;
            desktops.Placements[notepad.Handle] = unnamed;
            desktops.PinnedWindows.Add(pinned.Handle);

            var monitor = new StubMonitor();
            monitor.Initial.AddRange([rdm, code, beeper, pinned, notepad]);

            var screens = new FakeScreens();
            screens.Place(1, ScreenA);
            screens.Place(2, ScreenB);
            screens.Place(3, ScreenC);
            screens.Put(rdm.Handle, 1);
            screens.Put(code.Handle, 2);
            screens.Put(beeper.Handle, 1);
            screens.Put(pinned.Handle, 2);
            screens.Put(notepad.Handle, 1);

            var store = new StubStore
            {
                Stored = AppState.Empty with
                {
                    Workspaces = [gepha, sparrow],
                    Groups = groupColour is null ? [] : [team],
                },
            };
            var manager = new WorkspaceManager(desktops, monitor, new StubTitles(), store, screenLayout: screens);
            Assert.True(manager.Start().IsSuccess);

            var strips = new[] { Strip(ScreenA), Strip(ScreenB), Strip(ScreenC) };
            var bar = new FloatingBar(manager) { Left = -32000, Top = -32000, Strips = () => strips };
            bar.Show();

            return new StripHarness
            {
                Desktops = desktops, Monitor = monitor, Manager = manager, Screens = screens, Bar = bar,
                StripA = strips[0], StripB = strips[1], StripC = strips[2],
                Rdm = rdm, Code = code, Beeper = beeper, Pinned = pinned, Notepad = notepad,
                GephaDesktop = gephaDesktop, SparrowDesktop = sparrowDesktop, UnnamedDesktop = unnamed,
            };
        }

        // Undocked for good: its DockWith grants what is asked and reserves nothing.
        static ScreenStrip Strip(MonitorBounds screen) =>
            new(screen)
            {
                Scale = () => 1.0,
                DockWith = (_, rect, px) => new AppBar.Rect(rect.L, rect.T, rect.R, rect.T + px),
            };

        public void Dispose()
        {
            Strips.ToList().ForEach(s => s.Close());
            Bar.Close();
        }
    }

    // The screen layout, faked: which display each window is on, where each display is, and every move asked for.
    sealed class FakeScreens : IScreenLayout
    {
        readonly Dictionary<WindowHandle, int> monitorOf = [];
        readonly Dictionary<int, MonitorBounds> placement = [];
        readonly Dictionary<WindowHandle, WindowRect> rects = [];
        readonly List<(WindowHandle Window, WindowRect Rect)> moved = [];

        public void Place(int number, MonitorBounds bounds) => placement[number] = bounds;

        // A window on a screen, 400 x 300 px near that screen's top left corner.
        public void Put(WindowHandle window, int number)
        {
            monitorOf[window] = number;
            var screen = placement[number];
            rects[window] = new WindowRect(screen.Left + 100, screen.Top + 100, screen.Left + 500, screen.Top + 400);
        }

        public ScreenFacts Snapshot() =>
            new(monitorOf.ToDictionary(x => x.Key, x => x.Value), new HashSet<WindowHandle>(), new Dictionary<WindowHandle, int>(),
                Maybe<int>.From(1), placement.ToDictionary(x => x.Key, x => x.Value));

        public Maybe<WindowRect> RectOf(WindowHandle window) => rects.TryGetValue(window, out var rect) ? rect : Maybe<WindowRect>.None;

        public Result MoveTo(WindowHandle window, WindowRect rect, bool mayChangeShowState)
        {
            moved.Add((window, rect));
            rects[window] = rect;
            monitorOf[window] = placement.Single(p => rect.Left >= p.Value.Left && rect.Left < p.Value.Right).Key;
            return Result.Success();
        }

        // Whether a move put this window on this screen.
        public bool LandedOn(WindowHandle window, MonitorBounds screen) =>
            moved.Any(m => m.Window == window && m.Rect.Left >= screen.Left && m.Rect.Right <= screen.Right);

        public string Describe() => $"moves: [{string.Join(", ", moved.Select(m => $"{m.Window.Value}->{m.Rect.Left},{m.Rect.Top}"))}]";
    }
}
