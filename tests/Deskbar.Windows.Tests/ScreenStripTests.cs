using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Deskbar.App;
using Deskbar.Core.Domain;
using Deskbar.Core.Geometry;
using Deskbar.Windows.Monitoring;

namespace Deskbar.Windows.Tests;

// The strip window (ScreenStrip): one screen's top edge, holding the current workspace's name tag and the tabs the
// bar hands it (spec docs/superpowers/specs/2026-11-19-screen-strips-design.md, "The look" and "The parts").
//
// No test here registers a real app bar: that reserves space on the real screen and moves every maximized window
// on it, which is an integration concern and mutates Petre's machine. Every dock goes through the DockWith seam,
// which records what was asked and grants what the test says. The screen is a made-up one far off every real
// monitor, so a strip that is shown never appears anywhere, and its scale is faked through the Scale seam so the
// pixel arithmetic does not depend on the scale of whoever runs the suite.
public class ScreenStripTests
{
    // 1200 x 800 px, nowhere near a real screen. At the faked 150% that is 800 DIP wide. Kept small on purpose:
    // Windows caps a window's size at about the size of the machine's own desktop, and a 3840 px strip on a small CI
    // screen would come back narrower than it was placed.
    static readonly MonitorBounds Offscreen = new(-30000, -30000, -28800, -29200);
    const double At150 = 1.5;
    const double WidthDip = 1200 / At150;

    static readonly Color Neutral = Color.FromArgb(0xFF, 0x3A, 0x3F, 0x49);

    sealed record Ask(AppBar.Edge Edge, AppBar.Rect Screen, int ThicknessPx);

    // A strip whose docking is recorded and granted by the test: by default exactly what it asked for, as the shell
    // grants a top bar on a screen with nothing else on its top edge (measured, probe notes section 1).
    static ScreenStrip Strip(List<Ask>? asked = null, Func<DateTime>? clock = null, Func<AppBar.Rect, int, AppBar.Rect>? grant = null) =>
        new(Offscreen)
        {
            Scale = () => At150,
            Clock = clock ?? (() => DateTime.UtcNow),
            DockWith = (edge, screen, px) =>
            {
                asked?.Add(new Ask(edge, screen, px));
                return grant?.Invoke(screen, px) ?? new AppBar.Rect(screen.L, screen.T, screen.R, screen.T + px);
            },
        };

    static FrameworkElement Tab(string title = "a window") => new Border { Child = new TextBlock { Text = title } };

    static IReadOnlyList<FrameworkElement> Tabs(int count) => Enumerable.Range(0, count).Select(i => Tab($"window {i}")).ToList();

    static NativeMethods.RECT RectOf(Window window)
    {
        Assert.True(NativeMethods.GetWindowRect(new WindowInteropHelper(window).Handle, out var rect));
        return rect;
    }

    static bool IsTopmost(Window window) =>
        (NativeMethods.GetWindowLongPtr(new WindowInteropHelper(window).Handle, NativeMethods.GWL_EXSTYLE) & (nint)NativeMethods.WS_EX_TOPMOST) != 0;

    // A screen with no windows of the current workspace still has its strip, with only the name tag, so a switch
    // never changes that screen's work area (spec, "What it does").
    [Fact]
    public void An_empty_strip_shows_only_the_name_tag_at_32_dip() => StaThread.Run(() =>
    {
        var strip = Strip();

        strip.SetTabs("deskbar", Brushes.SteelBlue, []);

        Assert.Same(strip.NameTag, Assert.Single(strip.Row.Children.Cast<UIElement>()));
        Assert.Equal("deskbar", ((TextBlock)strip.NameTag.Child).Text);
        Assert.Equal(32, ScreenStrip.HeightDip);
        Assert.Equal(ScreenStrip.HeightDip, strip.Height);
        Assert.Equal(ScreenStrip.HeightDip, strip.Row.Height);
        Assert.Same(Offscreen, strip.Screen);
        strip.Close();
    });

    // A few windows on an 800 DIP screen: every tab at the full 150 DIP, not stretched to fill the strip.
    [Fact]
    public void Tabs_are_150_wide_when_they_fit() => StaThread.Run(() =>
    {
        var strip = Strip();
        var tabs = Tabs(3);

        strip.SetTabs("deskbar", Brushes.SteelBlue, tabs);

        Assert.Equal(4, strip.Row.Children.Count);
        Assert.All(tabs, tab => Assert.Equal(StripPlan.TabMax, tab.Width));
        Assert.Equal(tabs, strip.Row.Children.Cast<UIElement>().Skip(1));
        strip.Close();
    });

    // Twenty windows cannot have 150 DIP each in 800: all of them narrow by the same amount, and together with the
    // name tag they still fit the screen, so the strip stays one line.
    [Fact]
    public void Tabs_narrow_equally_when_they_do_not() => StaThread.Run(() =>
    {
        var strip = Strip();
        var tabs = Tabs(20);

        strip.SetTabs("deskbar", Brushes.SteelBlue, tabs);

        var width = tabs[0].Width;
        Assert.All(tabs, tab => Assert.Equal(width, tab.Width));
        Assert.True(width < StripPlan.TabMax, $"width {width}");
        Assert.True(width >= StripPlan.TabIconOnly, $"width {width}");
        strip.NameTag.Measure(new Size(double.PositiveInfinity, ScreenStrip.HeightDip));
        Assert.Equal(StripPlan.TabWidth(WidthDip - strip.NameTag.DesiredSize.Width, 20), width, 3);
        Assert.True(strip.NameTag.DesiredSize.Width + 20 * width <= WidthDip + 0.01);
        strip.Close();
    });

    // The bar hands every strip a new set of tabs on each rebuild; the old set goes, it is not appended to.
    [Fact]
    public void SetTabs_twice_does_not_accumulate() => StaThread.Run(() =>
    {
        var strip = Strip();

        strip.SetTabs("deskbar", Brushes.SteelBlue, Tabs(3));
        var second = Tabs(2);
        strip.SetTabs("deskbar", Brushes.SteelBlue, second);

        Assert.Equal(3, strip.Row.Children.Count);
        Assert.Same(strip.NameTag, strip.Row.Children[0]);
        Assert.Equal(second, strip.Row.Children.Cast<UIElement>().Skip(1));
        strip.Close();
    });

    // An unnamed desktop has no workspace colour: its name sits on a neutral grey (spec, "The look").
    [Fact]
    public void A_null_colour_gives_the_neutral_tag() => StaThread.Run(() =>
    {
        var strip = Strip();

        strip.SetTabs("Desktop 3", null, []);
        Assert.Equal(Neutral, Assert.IsType<SolidColorBrush>(strip.NameTag.Background).Color);

        // And a workspace's colour when it has one, replacing the grey.
        strip.SetTabs("deskbar", Brushes.SteelBlue, []);
        Assert.Same(Brushes.SteelBlue, strip.NameTag.Background);
        strip.Close();
    });

    // The way back to the other two places for the window list is the bar's own menu, so it is on the strip itself:
    // a right-click on the name tag or the free space asks for it. A right-click on a tab is the tab's own menu.
    [Fact]
    public void Right_click_on_the_tag_raises_MenuRequested() => StaThread.Run(() =>
    {
        var requested = 0;
        var strip = new ScreenStrip(Offscreen) { Scale = () => At150, DockWith = (_, s, px) => new(s.L, s.T, s.R, s.T + px), MenuRequested = () => requested++ };
        var tab = Tab();
        strip.SetTabs("deskbar", Brushes.SteelBlue, [tab]);

        RightClick(strip.NameTag);
        Assert.Equal(1, requested);

        RightClick((UIElement)((Border)tab).Child);
        Assert.Equal(1, requested);

        RightClick(strip.Row);
        Assert.Equal(2, requested);
        strip.Close();

        // The bubbling MouseUp, which is what a real release raises and carries up the tree; MouseRightButtonUp is a
        // direct event, raised on each element as MouseUp passes it, so raising it here would reach only `on`.
        static void RightClick(UIElement on) =>
            on.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
            {
                RoutedEvent = UIElement.MouseUpEvent,
                Source = on,
            });
    });

    // The shell speaks physical pixels, so 32 DIP on a 150% screen is 48 px, and the strip asks for the top of ITS OWN
    // screen, never the screen Windows thinks the window is on. The window then sits exactly in what was granted.
    [Fact]
    public void Dock_asks_for_the_top_of_its_own_screen_in_pixels() => StaThread.Run(() =>
    {
        var asked = new List<Ask>();
        var strip = Strip(asked);

        Assert.True(strip.Dock().IsSuccess);

        var ask = Assert.Single(asked);
        Assert.Equal(AppBar.Edge.Top, ask.Edge);
        Assert.Equal(new AppBar.Rect(Offscreen.Left, Offscreen.Top, Offscreen.Right, Offscreen.Bottom), ask.Screen);
        Assert.Equal(48, ask.ThicknessPx);

        Assert.True(strip.IsVisible);
        var rect = RectOf(strip);
        Assert.Equal((Offscreen.Left, Offscreen.Top, Offscreen.Right, Offscreen.Top + 48), (rect.Left, rect.Top, rect.Right, rect.Bottom));

        strip.Undock();
        Assert.False(strip.IsVisible);
        strip.Close();
    });

    // The 1 s guard gives the shell three seconds after a dock before it judges anything, as the docked bar's does.
    // The grant here lands the window OFF its own screen, which the guard repairs once it may: so the first call
    // doing nothing is the settle time, not a strip with nothing to repair.
    [Fact]
    public void KeepStrip_does_nothing_within_three_seconds_of_a_dock() => StaThread.Run(() =>
    {
        var asked = new List<Ask>();
        var now = new DateTime(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);
        var strip = Strip(asked, () => now, (s, px) => new AppBar.Rect(s.L + 5000, s.T, s.R + 5000, s.T + px));
        Assert.True(strip.Dock().IsSuccess);

        now += TimeSpan.FromSeconds(2.9);
        strip.KeepStrip([Offscreen]);
        Assert.Single(asked);

        // Settled, the window off its screen: docked again, on its own screen.
        now += TimeSpan.FromSeconds(0.2);
        strip.KeepStrip([Offscreen]);
        Assert.Equal(2, asked.Count);
        Assert.Equal(new AppBar.Rect(Offscreen.Left, Offscreen.Top, Offscreen.Right, Offscreen.Bottom), asked[1].Screen);

        // Its screen gone from the connected ones: nothing, however long it has been. A screen that left is the
        // reconcile's business, on the settled layout (probe notes, section 3).
        now += TimeSpan.FromMinutes(5);
        strip.KeepStrip([]);
        Assert.Equal(2, asked.Count);

        strip.Undock();
        strip.Close();
    });

    // A docked strip is topmost like the taskbar, and steps out of the band while the shell says a full-screen window
    // is up on its screen (AppBar's onFullScreen calls StepAside), so it does not sit over a video. The 1 s re-assert
    // must not put it back meanwhile; the window closing does.
    [Fact]
    public void A_full_screen_window_on_its_screen_takes_the_strip_out_of_topmost_and_its_closing_puts_it_back() => StaThread.Run(() =>
    {
        var strip = Strip();
        Assert.True(strip.Dock().IsSuccess);
        Assert.True(IsTopmost(strip));

        strip.StepAside(fullScreen: true);
        Assert.False(IsTopmost(strip));

        strip.ReclaimTopmost();
        Assert.False(IsTopmost(strip));

        strip.StepAside(fullScreen: false);
        Assert.True(IsTopmost(strip));

        strip.Undock();
        strip.Close();
    });

    // An icon dragged from the bar or from another strip and dropped anywhere on this one: the strip says which window,
    // the bar decides what that means (task 7). Same payload as every other drag of a window icon (WindowDragSource).
    [Fact]
    public void A_window_icon_dropped_on_the_strip_raises_Dropped() => StaThread.Run(() =>
    {
        var dropped = new List<WindowHandle>();
        var strip = new ScreenStrip(Offscreen) { Scale = () => At150, DockWith = (_, s, px) => new(s.L, s.T, s.R, s.T + px), Dropped = dropped.Add };

        Assert.True(ScreenStrip.Accepts(new DataObject(DraggedWindow.DragFormat, new DraggedWindow(new WindowHandle(0x1234), "workspace:x"))));
        strip.TakeDrop(new DataObject(DraggedWindow.DragFormat, new DraggedWindow(new WindowHandle(0x1234), "workspace:x")));
        Assert.Equal([new WindowHandle(0x1234)], dropped);

        // Anything else (a file, text) is not a window and is refused.
        Assert.False(ScreenStrip.Accepts(new DataObject(DataFormats.Text, "hello")));
        strip.TakeDrop(new DataObject(DataFormats.Text, "hello"));
        Assert.Single(dropped);
        strip.Close();
    });

    // The window itself: borderless, never on the taskbar or in Alt+Tab (a tool window, for the reason the bar gives
    // in OnSourceInitialized), topmost, on the bar's own background.
    [Fact]
    public void The_window_is_a_borderless_topmost_tool_window_on_the_bars_background() => StaThread.Run(() =>
    {
        var strip = Strip();
        var hwnd = new WindowInteropHelper(strip).EnsureHandle();

        Assert.Equal(WindowStyle.None, strip.WindowStyle);
        Assert.False(strip.ShowInTaskbar);
        Assert.True(strip.Topmost);
        Assert.NotEqual(0, NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE) & (nint)NativeMethods.WS_EX_TOOLWINDOW);
        Assert.Equal(Color.FromArgb(0xFF, 0x14, 0x15, 0x18), Assert.IsType<SolidColorBrush>(strip.Background).Color);
        strip.Close();
    });
}
