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

        // No tab: the name tag, then only the previous workspace's item, which every strip carries.
        Assert.Same(h.StripC.NameTag, h.StripC.Row.Children[0]);
        Assert.Empty(TabsOf(h.StripC));
        Assert.Equal(["↩ Sparrow"], JumpsOf(h.StripC).Select(LabelOf));
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

        // A ring round the tab, not an underline: a maximized window's focus ring frame draws its top line right under
        // the strip in the same colour, and an underline merged with it ("fullscreen masks which window is active in
        // the strip"). The fill is inside the ring.
        Assert.Equal(accent, ColourOf(Underline(active).BorderBrush));
        Assert.Equal(ActiveFill, ColourOf(Underline(active).Background));
        Assert.Equal(Colors.White, ColourOf(Title(active).Foreground));

        Assert.Equal(RestUnderline, ColourOf(Underline(resting).BorderBrush));
        Assert.NotEqual(ActiveFill, ColourOf(Underline(resting).Background));
    });

    // A grouped workspace's tag wears the GROUP's colour, the one the bar paints that workspace's row in.
    [Fact]
    public void The_name_tag_wears_the_group_colour() => StaThread.Run(() =>
    {
        using var h = StripHarness.Build(groupColour: "#3070D0");
        h.Fill();

        Assert.Equal(Hex("#3070D0"), ((SolidColorBrush)h.StripA.NameTag.Background).Color);
    });

    // Arranging is the bar's ROWS' business (FloatingBar.Arrange.cs). A strip's tabs share the bar's button code, but
    // they live in windows of their own, where a click still activates the window: they stay at full strength and keep
    // their window drag, while the bar's own row icons dim and refuse it.
    [Fact]
    public void Strip_tabs_stay_whole_while_the_bar_is_arranging() => StaThread.Run(() =>
    {
        using var h = StripHarness.Build();
        h.Fill();

        Assert.True(h.Bar.SetArranging(true).IsSuccess);

        var tabs = h.Strips.SelectMany(TabsOf).ToList();
        Assert.NotEmpty(tabs);
        Assert.All(tabs, tab => Assert.Equal(1.0, tab.Opacity));
        Assert.True(h.Bar.WindowDragAllowed(onStrip: true));
        Assert.False(h.Bar.WindowDragAllowed(onStrip: false));

        Assert.True(h.Bar.SetArranging(false).IsSuccess);
        Assert.True(h.Bar.WindowDragAllowed(onStrip: false));
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

    // Petre: "show a sign next to each window that will tell me if it's maximized or part of a tile", option B: a badge
    // on the icon's bottom-left corner (FloatingBar.WindowState.cs). Maximized wins over tiled; neither, no badge.
    [Fact]
    public void A_maximized_or_tiled_window_carries_its_badge_and_others_none() => StaThread.Run(() =>
    {
        using var h = StripHarness.Build();
        h.Maximized.Add(h.Code.Handle);
        h.Tiled.Add(h.Code.Handle);
        h.Tiled.Add(h.Rdm.Handle);
        h.Fill();

        FloatingBar.WindowFit? Badge(ScreenStrip strip, string title) =>
            Descendants(TabFor(strip, title)).OfType<FrameworkElement>().Any(d => d.Tag is FloatingBar.WindowFitBadgeTag)
                ? Descendants(TabFor(strip, title)).OfType<System.Windows.Shapes.Path>().Any(p => p.Stroke is not null && p.Fill is null)
                    ? FloatingBar.WindowFit.Maximized
                    : FloatingBar.WindowFit.Tiled
                : null;

        Assert.Equal(FloatingBar.WindowFit.Maximized, Badge(h.StripB, h.Code.Title));
        Assert.Equal(FloatingBar.WindowFit.Tiled, Badge(h.StripA, h.Rdm.Title));
        Assert.Null(Badge(h.StripB, h.Pinned.Title));
    });

    // A window maximized by its own title bar raises nothing the bar rebuilds on, so the 1 s tick checks the badges it
    // drew and rebuilds only when one is out of date.
    [Fact]
    public void The_tick_redraws_a_badge_that_went_out_of_date_and_nothing_else() => StaThread.Run(() =>
    {
        using var h = StripHarness.Build();
        h.Fill();
        var before = TabFor(h.StripB, h.Code.Title);

        h.Bar.RefreshWindowFits();
        Assert.Same(before, TabFor(h.StripB, h.Code.Title));

        h.Maximized.Add(h.Code.Handle);
        h.Bar.RefreshWindowFits();

        var after = TabFor(h.StripB, h.Code.Title);
        Assert.NotSame(before, after);
        Assert.Contains(Descendants(after), d => d is FrameworkElement { Tag: FloatingBar.WindowFitBadgeTag });
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
        Assert.Equal(ActiveFill, ColourOf(Underline(tabAfter).Background));
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

    // The hover card for a tab opens below the tab. The bar's placement measures the anchor in the bar's own
    // coordinates and opens the card beside the bar: for a tab, on another screen's edge entirely. That measurement
    // does not throw for an element in another window (WPF translates through screen coordinates, measured here); it
    // only lands the card in the wrong place, which is what this pins.
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
            // Against the strip's window, which outlives the tab (see the test below), at the tab's place in it.
            Assert.Same(h.StripA, popup.PlacementTarget);
            Assert.Equal(PlacementMode.Bottom, popup.Placement);
            Assert.Equal(tab.TranslatePoint(new Point(0, 0), h.StripA).X, popup.HorizontalOffset, 3);
        }
        finally
        {
            popup.IsOpen = false;
        }
    });

    // A rebuild while the card is up (a title or focus change, routine while the pointer rests on a tab) hands the strip
    // new tabs. The card's window is still the one shown, so the hover watch keeps it open, and it must not be left
    // placed against the tab that was thrown away: it stays on the strip's window, under where the tab is now.
    [Fact]
    public void A_hover_card_for_a_tab_stays_on_its_strip_through_a_rebuild() => StaThread.Run(() =>
    {
        using var h = StripHarness.Build();
        h.Fill();
        h.StripB.Left = -32000;
        h.StripB.Top = -32000;
        h.StripB.Show();
        h.StripB.UpdateLayout();
        // The second tab on its strip, so where it sits is more than the strip's left edge.
        var tab = TabFor(h.StripB, h.Pinned.Title);
        h.Bar.ShowHoverCard("GEPHA", (WindowRow)tab.DataContext, tab, here: false);
        var popup = h.Bar.HoverPopup!;
        try
        {
            h.Monitor.Pump.OnNext(new WindowEvent(WindowEventKind.Activated, h.Code));
            h.StripB.UpdateLayout();
            var now = TabFor(h.StripB, h.Pinned.Title);
            Assert.NotSame(tab, now); // precondition: the rebuild really replaced the tab

            Assert.True(popup.IsOpen);
            Assert.Same(h.StripB, Window.GetWindow((DependencyObject)popup.PlacementTarget));
            Assert.Equal(PlacementMode.Bottom, popup.Placement);
            var x = now.TranslatePoint(new Point(0, 0), h.StripB).X;
            Assert.True(x > 0, $"tab at {x}");
            Assert.Equal(x, popup.HorizontalOffset, 3);
        }
        finally
        {
            popup.IsOpen = false;
        }
    });

    // Petre's rule for the hover card: the picture is for a window you cannot see, so a window in the workspace you
    // are on gets its text alone. Every tab is such a window (the current workspace's, the current unnamed desktop's,
    // or a pinned one, which is on every desktop), so a tab's card never carries a picture, and the capture behind
    // one is never made. Armed through the tab's own MouseEnter, then shown from what was armed, because the dwell's
    // own check asks where the real pointer is.
    [Fact]
    public void A_tabs_hover_card_has_no_picture() => StaThread.Run(() =>
    {
        using var h = StripHarness.Build();
        h.Fill();
        h.StripB.Left = -32000;
        h.StripB.Top = -32000;
        h.StripB.Show();

        new[] { TabFor(h.StripB, h.Code.Title), TabFor(h.StripB, h.Pinned.Title) }.ToList().ForEach(tab =>
        {
            tab.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseEnterEvent });
            var armed = h.Bar.Dwelling!.Value;
            Assert.Same(tab, armed.Anchor);
            Assert.True(armed.Here, $"{System.Windows.Automation.AutomationProperties.GetName(tab)} was armed for a picture");

            h.Bar.ShowHoverCard(armed.Label, armed.Row, armed.Anchor, armed.Here);
            var popup = h.Bar.HoverPopup!;
            try
            {
                Assert.DoesNotContain(Descendants(popup.Child), d => d is Image);
            }
            finally
            {
                popup.IsOpen = false;
            }
        });
    });

    // A resize of the bar re-flows its own rows at mouse-move rate (Relayout), and a strip's tabs do not depend on the
    // bar's width at all, so a relayout must not rebuild every strip's tabs on every step of the drag.
    [Fact]
    public void A_relayout_leaves_the_strips_tabs_alone() => StaThread.Run(() =>
    {
        using var h = StripHarness.Build();
        h.Fill();
        h.Bar.UpdateLayout();
        var tab = TabFor(h.StripB, h.Code.Title);
        var rows = h.Rows.Children.Cast<UIElement>().ToList();

        // A width of its own, as a resize drag gives the bar: only then does a width change re-flow it (OnSizeChanged).
        h.Bar.SizeToContent = SizeToContent.Height;
        h.Bar.Width = h.Bar.ActualWidth + 120;
        h.Bar.UpdateLayout();

        Assert.NotEqual(rows, h.Rows.Children.Cast<UIElement>().ToList()); // precondition: the relayout did redraw the bar
        Assert.Same(tab, TabFor(h.StripB, h.Code.Title));
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

    // --- pinned workspaces (FloatingBar.WorkspaceJumps.cs) ---------------------------------------------------------

    // The item at the end of a strip, after the window tabs (Petre: "put it at the end of the list of windows").
    static IReadOnlyList<Button> JumpsOf(ScreenStrip strip) =>
        strip.Row.Children.OfType<Button>().Where(b => b.Tag is FloatingBar.WorkspaceJumpTag).ToList();

    static string LabelOf(Button jump) => System.Windows.Automation.AutomationProperties.GetName(jump);

    static Guid IdOf(StripHarness h, string name) => h.Manager.State.Workspaces.Single(w => w.Name == name).Id;

    // Every strip, so every screen, carries the item, each its own element (an element has one parent), after the tabs.
    [Fact]
    public void Every_strip_gets_the_pinned_item_after_its_tabs() => StaThread.Run(() =>
    {
        using var h = StripHarness.Build();
        Assert.True(h.Manager.SetWorkspacePinned(IdOf(h, "Sparrow"), true).IsSuccess);
        h.Fill();

        Assert.All(h.Strips, strip =>
        {
            Assert.Equal("Sparrow", LabelOf(Assert.Single(JumpsOf(strip))));
            Assert.Same(JumpsOf(strip)[0], strip.Row.Children[^1]);
        });
        Assert.Equal(3, h.Strips.Select(s => JumpsOf(s)[0]).Distinct().Count());
    });

    // Inside the pinned workspace the same item, in the same place, goes back where you came from.
    [Fact]
    public void Inside_the_pinned_workspace_the_item_reads_back() => StaThread.Run(() =>
    {
        using var h = StripHarness.Build();
        Assert.True(h.Manager.SetWorkspacePinned(IdOf(h, "GEPHA"), true).IsSuccess);
        h.Fill();

        Assert.All(h.Strips, strip => Assert.Equal("↩ Sparrow", LabelOf(Assert.Single(JumpsOf(strip)))));
    });

    // A click is the manager's switch to the item's target. Raised directly, for the reason the tab click test gives.
    [Fact]
    public void Clicking_the_item_switches_through_the_manager() => StaThread.Run(() =>
    {
        using var h = StripHarness.Build();
        Assert.True(h.Manager.SetWorkspacePinned(IdOf(h, "Sparrow"), true).IsSuccess);
        h.Fill();

        JumpsOf(h.StripA)[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        Assert.Equal([h.SparrowDesktop], h.Desktops.Switched);
    });

    // Petre, with a screenshot of the header: "i don't want pinned at the top". The pinned workspaces' items are on the
    // strips and the taskbar only; nothing in the bar itself is one.
    [Fact]
    public void The_bar_carries_no_pinned_items() => StaThread.Run(() =>
    {
        using var h = StripHarness.Build();

        Assert.True(h.Manager.SetWorkspacePinned(IdOf(h, "Sparrow"), true).IsSuccess);

        Assert.DoesNotContain(Descendants((DependencyObject)h.Bar.FindName("BarHeader")!), d => d is FrameworkElement { Tag: FloatingBar.WorkspaceJumpTag });
    });

    // Petre, with a screenshot of the expanded row's titled list: "i want to have pinned workspaces in the workspace,
    // in this layout", "and when at the bottom of the bar". With the window list in the row or at the bottom of the
    // bar, the list ends with the same items the strips end with.
    [Theory]
    [InlineData(WindowListPlace.Row)]
    [InlineData(WindowListPlace.Bottom)]
    public void The_titled_list_ends_with_the_items(WindowListPlace place) => StaThread.Run(() =>
    {
        using var h = StripHarness.Build();
        Assert.True(h.Manager.SetWorkspacePinned(IdOf(h, "Sparrow"), true).IsSuccess);
        Assert.True(h.Manager.SetWindowListPlace(place).IsSuccess);

        var items = Descendants(h.Rows).Concat(Descendants(h.CurrentList)).OfType<Button>().Where(b => b.Tag is FloatingBar.WorkspaceJumpTag);
        Assert.Equal(["Sparrow"], items.Select(LabelOf));
    });

    // Petre, with a screenshot of the Deskbar row held open after he had moved to messengers, ending with a
    // "↩ Deskbar" chip: "deskbar in deskbar". While the pointer is on the bar the row you left stays expanded (the
    // hold, see RebuildCore), and every expanded row used to end with the chips, which are about where the CURRENT
    // workspace can take you. So the chips are on the current row alone; the held row keeps the empty chip line.
    [Fact]
    public void A_row_held_open_after_leaving_it_has_no_items() => StaThread.Run(() =>
    {
        using var h = StripHarness.Build();
        Assert.True(h.Manager.SetWorkspacePinned(IdOf(h, "GEPHA"), true).IsSuccess);
        Assert.True(h.Manager.SetWindowListPlace(WindowListPlace.Row).IsSuccess);

        // The pointer arrives on the bar, then the switch to Sparrow: GEPHA stays expanded under the hold.
        h.Bar.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseEnterEvent });
        Assert.True(h.Manager.Switch(h.Manager.State.Workspaces.Single(w => w.Name == "Sparrow").Id).IsSuccess);
        h.Monitor.Pump.OnNext(new WindowEvent(WindowEventKind.Activated, h.Beeper));
        h.Bar.UpdateLayout();

        var rows = Descendants(h.Rows).OfType<Grid>().Where(g => g.Tag is not null).ToList();
        var itemsIn = (string name) => rows
            .Where(g => System.Windows.Automation.AutomationProperties.GetName(g) == name)
            .SelectMany(Descendants).OfType<Button>().Count(b => b.Tag is FloatingBar.WorkspaceJumpTag);
        Assert.Equal(0, itemsIn("GEPHA"));
        Assert.Equal(1, itemsIn("Sparrow"));
    });

    // Petre, with a screenshot of the previous workspace's chip in the current row: "going to messengers immediately
    // brings me back to the current workspace". In the row, the chip sits INSIDE the current workspace's row, whose
    // whole-row click switches to that row's workspace on the bubbling release. The chip's click went to Sparrow,
    // then the same release reached the row and switched straight back to GEPHA. The click trace showed it as
    // "row-up group=GEPHA consumedByChild=False" followed by "switch group=GEPHA". Only Sparrow may be switched to.
    //
    // A full press, click and release on the chip, as the input manager delivers them; the Click is raised by hand
    // because ButtonBase cannot take mouse capture in a test (see A_strip_press_then_release_is_not_an_orphan).
    [Theory]
    [InlineData(WindowListPlace.Row)]
    [InlineData(WindowListPlace.Bottom)]
    public void Clicking_an_item_in_the_titled_list_goes_there_and_stays(WindowListPlace place) => StaThread.Run(() =>
    {
        using var h = StripHarness.Build();
        Assert.True(h.Manager.SetWorkspacePinned(IdOf(h, "Sparrow"), true).IsSuccess);
        Assert.True(h.Manager.SetWindowListPlace(place).IsSuccess);
        var item = Descendants(h.Rows).Concat(Descendants(h.CurrentList)).OfType<Button>()
            .Single(b => b.Tag is FloatingBar.WorkspaceJumpTag);

        Press(item);
        item.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Release(item);

        Assert.Equal([h.SparrowDesktop], h.Desktops.Switched);
    });

    // The items are filled inside the bar's guard, as the tabs are, so a re-entered rebuild leaves one item a strip.
    [Fact]
    public void A_reentrant_rebuild_does_not_double_the_pinned_items() => StaThread.Run(() =>
    {
        using var h = StripHarness.Build();
        Assert.True(h.Manager.SetWorkspacePinned(IdOf(h, "Sparrow"), true).IsSuccess);
        h.Fill();

        h.Desktops.PulseOnNextDesktopOf = () => h.Monitor.Pump.OnNext(new WindowEvent(WindowEventKind.Activated, h.Rdm));
        h.Monitor.Pump.OnNext(new WindowEvent(WindowEventKind.Activated, h.Code));

        Assert.All(h.Strips, strip => Assert.Single(JumpsOf(strip)));
    });

    // The row menu's one flipping item pins and unpins.
    [Fact]
    public void The_row_menu_pins_and_unpins() => StaThread.Run(() =>
    {
        using var h = StripHarness.Build();
        var sparrow = IdOf(h, "Sparrow");
        MenuItem Item(string header) =>
            h.Bar.WorkspaceMenu(sparrow, "Sparrow", folded: false).Items.OfType<MenuItem>().Single(i => i.Header as string == header);

        Item("Pin to strip").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.True(h.Manager.State.Workspaces.Single(w => w.Id == sparrow).Pinned);

        Item("Unpin from strip").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.False(h.Manager.State.Workspaces.Single(w => w.Id == sparrow).Pinned);
    });

    // Final review, Important 1: with the bar minimized and the window list NOT on the screens, the taskbar buttons are
    // the only place the items are, so the bar keeps rebuilding for them: a pin adds a button, an unpin takes it away.
    [Fact]
    public void A_minimized_bar_with_no_strips_keeps_the_taskbar_buttons_current() => StaThread.Run(() =>
    {
        var jumps = new TaskbarJumps(pin: _ => Result.Success(), ignore: _ => { }, switchTo: _ => Result.Success());
        using var h = StripHarness.Build(taskbar: jumps);
        h.Bar.MinimizeToTaskbar();

        Assert.True(h.Manager.SetWorkspacePinned(IdOf(h, "Sparrow"), true).IsSuccess);
        Assert.Equal("Sparrow", Assert.Single(jumps.Windows).Title);

        // Unpinned, Sparrow is still where "back" goes, so its button becomes the previous workspace's.
        Assert.True(h.Manager.SetWorkspacePinned(IdOf(h, "Sparrow"), false).IsSuccess);
        Assert.Equal("↩ Sparrow", Assert.Single(jumps.Windows).Title);
    });

    // Petre: "let's also have previous workspace available in strip", "and taskbar". With nothing pinned, every strip
    // ends with "↩ <previous>", and the taskbar has its button; a click goes there.
    [Fact]
    public void The_previous_workspace_is_the_last_item_on_every_strip_and_on_the_taskbar() => StaThread.Run(() =>
    {
        var jumps = new TaskbarJumps(pin: _ => Result.Success(), ignore: _ => { }, switchTo: _ => Result.Success());
        using var h = StripHarness.Build(taskbar: jumps);
        h.Fill();

        Assert.All(h.Strips, strip => Assert.Equal("↩ Sparrow", LabelOf((Button)strip.Row.Children[^1])));
        Assert.Equal("↩ Sparrow", Assert.Single(jumps.Windows).Title);

        ((Button)h.StripA.Row.Children[^1]).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal([h.SparrowDesktop], h.Desktops.Switched);
        jumps.Close();
    });

    // "Back" from GEPHA is Sparrow, which is pinned: its own item already goes there, so no second item for one place.
    [Fact]
    public void No_back_item_when_the_pinned_item_goes_to_the_same_place() => StaThread.Run(() =>
    {
        using var h = StripHarness.Build();
        Assert.True(h.Manager.SetWorkspacePinned(IdOf(h, "Sparrow"), true).IsSuccess);
        h.Fill();

        Assert.All(h.Strips, strip => Assert.Equal(["Sparrow"], JumpsOf(strip).Select(LabelOf)));
    });

    // Petre: "i may also have multiple pinned workspaces, both should go to the top". The bar draws pinned rows first.
    [Fact]
    public void Pinned_rows_are_drawn_first() => StaThread.Run(() =>
    {
        using var h = StripHarness.Build();
        var texts = TextsIn(h.Rows);
        Assert.True(texts.ToList().IndexOf("GEPHA") < texts.ToList().IndexOf("Sparrow"));

        Assert.True(h.Manager.SetWorkspacePinned(IdOf(h, "Sparrow"), true).IsSuccess);

        texts = TextsIn(h.Rows);
        Assert.True(texts.ToList().IndexOf("Sparrow") < texts.ToList().IndexOf("GEPHA"));
    });

    // Petre: "let's also have the pinned workspace showing that it's pinned". A pin beside the row's name.
    [Fact]
    public void A_pinned_row_carries_the_pin_mark() => StaThread.Run(() =>
    {
        using var h = StripHarness.Build();
        Assert.DoesNotContain(Descendants(h.Rows), d => d is FrameworkElement { Tag: FloatingBar.PinnedWorkspaceMarkTag });

        Assert.True(h.Manager.SetWorkspacePinned(IdOf(h, "Sparrow"), true).IsSuccess);

        Assert.Single(Descendants(h.Rows), d => d is FrameworkElement { Tag: FloatingBar.PinnedWorkspaceMarkTag });
    });

    // Petre: "no grouped workspace should be available for pinning".
    [Fact]
    public void A_grouped_row_offers_no_pin() => StaThread.Run(() =>
    {
        using var h = StripHarness.Build(groupColour: "#5B6CFF");
        var headers = h.Bar.WorkspaceMenu(IdOf(h, "Sparrow"), "Sparrow", folded: false).Items.OfType<MenuItem>().Select(i => i.Header as string);

        Assert.DoesNotContain("Pin to strip", headers);
    });

    // Petre, with the Meta strip showing an inert "Meta" chip: "remove meta from meta". An item that goes nowhere (the
    // workspace you are in, when its way back is another pinned item or there is none) is left off the strip, where the
    // name tag already says where you are, and, "also drop from the taskbar", off the taskbar too.
    [Fact]
    public void An_item_that_goes_nowhere_is_on_neither_the_strip_nor_the_taskbar() => StaThread.Run(() =>
    {
        var jumps = new TaskbarJumps(pin: _ => Result.Success(), ignore: _ => { }, switchTo: _ => Result.Success());
        using var h = StripHarness.Build(taskbar: jumps);
        Assert.True(h.Manager.SetWorkspacePinned(IdOf(h, "GEPHA"), true).IsSuccess);
        Assert.True(h.Manager.SetWorkspacePinned(IdOf(h, "Sparrow"), true).IsSuccess);
        h.Fill();

        Assert.All(h.Strips, strip => Assert.Equal(["Sparrow"], JumpsOf(strip).Select(LabelOf)));
        Assert.Equal(["Sparrow"], jumps.Windows.Select(w => w.Title));
        jumps.Close();
    });

    // --- helpers ------------------------------------------------------------------------------------------------

    static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    static Color ColourOf(Brush? brush) => brush is SolidColorBrush solid ? solid.Color : Colors.Transparent;

    static string TagText(ScreenStrip strip) => ((TextBlock)strip.NameTag.Child).Text;

    // The tabs on a strip, in order: every child of its row after the name tag that is not an item (the pinned
    // workspaces' and the previous workspace's, FloatingBar.WorkspaceJumps.cs).
    static IReadOnlyList<Button> TabsOf(ScreenStrip strip) =>
        strip.Row.Children.Cast<UIElement>().Skip(1).Cast<Button>().Where(b => b.Tag is not FloatingBar.WorkspaceJumpTag).ToList();

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

        // Which windows the bar is told are maximized or in a tile (FloatingBar.IsMaximized, IsTiled): the tests have no
        // real windows to maximize and no Tiler, so they say it here.
        public required HashSet<WindowHandle> Maximized { get; init; }
        public required HashSet<WindowHandle> Tiled { get; init; }

        public IReadOnlyList<ScreenStrip> Strips => [StripA, StripB, StripC];
        public Panel Rows => (Panel)Bar.FindName("Rows")!;
        public Panel CurrentList => (Panel)Bar.FindName("CurrentList")!;

        public void Fill() => Assert.True(Manager.SetWindowListPlace(WindowListPlace.Screens).IsSuccess);

        public static StripHarness Build(string? groupColour = null, TaskbarJumps? taskbar = null)
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
                    // Off at the start, although the strips are the default, so that the tests' own
                    // SetWindowListPlace(Screens) is a real change: that change is what makes the bar fill them.
                    WindowListOffScreens = true,
                },
            };
            var manager = new WorkspaceManager(desktops, monitor, new StubTitles(), store, screenLayout: screens);
            Assert.True(manager.Start().IsSuccess);

            var strips = new[] { Strip(ScreenA), Strip(ScreenB), Strip(ScreenC) };
            var maximized = new HashSet<WindowHandle>();
            var tiled = new HashSet<WindowHandle>();
            var bar = new FloatingBar(manager)
            {
                Left = -32000, Top = -32000, Strips = () => strips, TaskbarJumps = taskbar,
                IsMaximized = handle => maximized.Contains(handle),
                IsTiled = (handle, _) => tiled.Contains(handle),
            };
            bar.Show();

            return new StripHarness
            {
                Maximized = maximized, Tiled = tiled,
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

        // The strip set's quiet move, which nothing in these tests makes: refused, so a test that did reach it would
        // see a failure rather than a silent success.
        public Result Nudge(WindowHandle window, WindowRect rect) => Result.Failure("not used by the bar's tests");

        // Whether a move put this window on this screen.
        public bool LandedOn(WindowHandle window, MonitorBounds screen) =>
            moved.Any(m => m.Window == window && m.Rect.Left >= screen.Left && m.Rect.Right <= screen.Right);

        public string Describe() => $"moves: [{string.Join(", ", moved.Select(m => $"{m.Window.Value}->{m.Rect.Left},{m.Rect.Top}"))}]";
    }
}
