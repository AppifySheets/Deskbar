using System.Reactive.Subjects;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core;
using Deskbar.Core.Abstractions;
using Deskbar.Core.Persistence;
using Deskbar.Windows.Monitoring;
using Deskbar.Windows.Performance;

namespace Deskbar.Windows.Tests;

// The stats line in a window of its own (DetachedStatsWindow). The owner wanted the numbers while a game or another
// full-screen window covers the bar. Every window here gets a FakeStats, so nothing depends on the load of whoever
// runs the suite, and sits at -32000 so nothing shows on a screen. The middle drag is driven through the two entry
// points its handlers use (as the bar's tests do), so a real middle press, a drag across screens of different scale,
// the topmost behaviour over a game and the pin across desktops need a real desktop and are checked by hand.
public class DetachedStatsWindowTests
{
    sealed class FakeStats : ISystemStats
    {
        public readonly Subject<SystemSample> Next = new();
        public IObservable<SystemSample> Samples => Next;
    }

    static SystemSample Sample(double cpu = 23, double memory = 61, double disk = 4, double? gpu = 12) =>
        new(cpu, memory, 20UL << 30, 32UL << 30, disk, gpu is { } g ? g : Maybe<double>.None, 120L << 30, 953L << 30);

    static DetachedStatsWindow Window(FakeStats stats, Action? openTaskManager = null)
    {
        var desktops = new PulsingDesktops { CurrentId = Guid.NewGuid() };
        desktops.Desktops.Add(new DesktopInfo(desktops.CurrentId, "Work"));
        var manager = new WorkspaceManager(desktops, new StubMonitor(), new StubTitles(), new StubStore());
        Assert.True(manager.Start().IsSuccess);
        var window = new DetachedStatsWindow(manager, stats, openTaskManager ?? (() => { }))
        {
            Left = -32000,
            Top = -32000,
        };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    static UniformGrid Strip(DetachedStatsWindow window) => (UniformGrid)((Border)window.FindName("Root")!).Child;

    // The samples arrive through the dispatcher, as the real ones do from their pool thread.
    static void Push(DetachedStatsWindow window, FakeStats stats, SystemSample sample)
    {
        stats.Next.OnNext(sample);
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        window.UpdateLayout();
    }

    // Each cell: a top row with the name at the left and the value (in a shrink-only Viewbox) at the right, then the
    // graph, which holds the sparkline alone.
    static IEnumerable<(Grid Top, Grid Graph)> Rows(DetachedStatsWindow window) =>
        Strip(window).Children.OfType<StackPanel>()
            .Where(cell => cell.Visibility == Visibility.Visible)
            .Select(cell => (Top: (Grid)cell.Children[0], Graph: (Grid)cell.Children[1]));

    static IEnumerable<(string Label, string Value)> Cells(DetachedStatsWindow window) =>
        Rows(window).Select(r => (Label: r.Top.Children.OfType<TextBlock>().Single().Text,
            Value: ((TextBlock)r.Top.Children.OfType<Viewbox>().Single().Child).Text));

    static void Press(DetachedStatsWindow window, RoutedEvent routed, MouseButton button = MouseButton.Left) =>
        window.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, button)
        {
            RoutedEvent = routed,
            Source = window,
        });

    static NativeMethods.RECT RectOf(DetachedStatsWindow window)
    {
        Assert.True(NativeMethods.GetWindowRect(new WindowInteropHelper(window).Handle, out var rect));
        return rect;
    }

    [Fact]
    public void A_sample_paints_four_cells_with_their_values() => StaThread.Run(() =>
    {
        var stats = new FakeStats();
        var window = Window(stats);

        Push(window, stats, Sample());

        Assert.Equal([("CPU", "23%"), ("MEM", "61%"), ("DISK", "4%"), ("GPU", "12%")], Cells(window));
        Assert.Equal(4, Strip(window).Columns);
        Assert.Contains("Click for Task Manager", ((TextBlock)((Border)window.FindName("Root")!).ToolTip).Text);
        window.CloseForGood();
    });

    // No GPU counters: three cells sharing the width, not a GPU stuck at 0%.
    [Fact]
    public void No_gpu_reading_paints_three_cells() => StaThread.Run(() =>
    {
        var stats = new FakeStats();
        var window = Window(stats);

        Push(window, stats, Sample(gpu: null));

        Assert.Equal([("CPU", "23%"), ("MEM", "61%"), ("DISK", "4%")], Cells(window));
        Assert.Equal(3, Strip(window).Columns);
        window.CloseForGood();
    });

    // The numbers' names are in the top row beside the values; the graph holds the sparkline and nothing drawn over
    // it, because a reader who cannot tell a name from the coloured fill under it could not read it there.
    [Fact]
    public void Names_sit_beside_the_values_not_over_the_graph() => StaThread.Run(() =>
    {
        var stats = new FakeStats();
        var window = Window(stats);
        Push(window, stats, Sample());

        var rows = Rows(window).ToList();
        Assert.Equal(4, rows.Count);
        foreach (var (top, graph) in rows)
        {
            Assert.Empty(graph.Children.OfType<TextBlock>());
            Assert.Single(graph.Children.OfType<Canvas>());

            var name = top.Children.OfType<TextBlock>().Single();
            var value = top.Children.OfType<Viewbox>().Single();
            Assert.Equal(0, Grid.GetColumn(name));
            Assert.Equal(1, Grid.GetColumn(value));
            Assert.Equal(HorizontalAlignment.Left, name.HorizontalAlignment);
            Assert.Equal(HorizontalAlignment.Right, value.HorizontalAlignment);
            // Same row, name left of the value.
            Assert.True(name.TranslatePoint(new Point(name.ActualWidth, 0), window).X
                <= value.TranslatePoint(new Point(0, 0), window).X);
        }
        window.CloseForGood();
    });

    [Fact]
    public void A_left_click_opens_task_manager() => StaThread.Run(() =>
    {
        var stats = new FakeStats();
        var opened = 0;
        var window = Window(stats, () => opened++);
        Push(window, stats, Sample());

        Press(window, UIElement.PreviewMouseLeftButtonDownEvent);
        Press(window, UIElement.PreviewMouseLeftButtonUpEvent);
        Assert.Equal(1, opened);

        // A release that no press of ours began (the end of a drag, or a menu closing) is not a click.
        Press(window, UIElement.PreviewMouseLeftButtonUpEvent);
        Assert.Equal(1, opened);
        window.CloseForGood();
    });

    // Only the middle button moves the window. Driven through the entry points the handlers use, with the cursor
    // wherever the test says (the bar's FloatingBarMiddleDragTests do the same). A left press followed by a move
    // leaves the window where it was, and only the release opens Task Manager.
    [Fact]
    public void A_middle_drag_moves_the_window_and_a_left_press_does_not() => StaThread.Run(() =>
    {
        var stats = new FakeStats();
        var opened = 0;
        var window = Window(stats, () => opened++);
        Push(window, stats, Sample());
        var before = RectOf(window);
        var (cursorX, cursorY) = (before.Left + 20, before.Top + 10);

        // A left press and a mouse move: nothing moves, nothing opens.
        Press(window, UIElement.PreviewMouseLeftButtonDownEvent);
        window.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
        {
            RoutedEvent = UIElement.PreviewMouseMoveEvent,
            Source = window,
        });
        var afterLeft = RectOf(window);
        Assert.Equal((before.Left, before.Top), (afterLeft.Left, afterLeft.Top));
        Assert.Equal(0, opened);
        Press(window, UIElement.PreviewMouseLeftButtonUpEvent);
        Assert.Equal(1, opened);

        // The middle drag: the window moves by exactly the cursor's travel, in pixels, and keeps its size.
        Assert.True(window.GrabWindowAt(cursorX, cursorY));
        window.MoveWindowTo(cursorX + 120, cursorY + 45);

        var after = RectOf(window);
        Assert.Equal((before.Left + 120, before.Top + 45), (after.Left, after.Top));
        Assert.Equal((before.Right - before.Left, before.Bottom - before.Top), (after.Right - after.Left, after.Bottom - after.Top));
        Assert.Equal(1, opened); // moving it opened nothing more

        // What the drop saves: WPF's own Left and Top have followed a move it did not make itself.
        var scale = System.Windows.Media.VisualTreeHelper.GetDpi(window);
        Assert.InRange(window.Left * scale.DpiScaleX, after.Left - 1, after.Left + 1);
        Assert.InRange(window.Top * scale.DpiScaleY, after.Top - 1, after.Top + 1);

        // A middle release with no drag begun neither moves the window nor opens anything.
        Press(window, UIElement.PreviewMouseUpEvent, MouseButton.Middle);
        Assert.Equal((after.Left, after.Top), (RectOf(window).Left, RectOf(window).Top));
        Assert.Equal(1, opened);
        window.CloseForGood();
    });

    // A closed WPF window can never be shown again, so a close request (Alt+F4) never closes this one: it becomes
    // "put the stats back on the bar" (the next test). In this window-only test nobody is listening, and App is
    // the one that hides the window, so IsVisible stays true after Close(); the point is that the window is still
    // alive, so detaching again must not throw.
    [Fact]
    public void A_close_request_does_not_close_the_window() => StaThread.Run(() =>
    {
        var window = Window(new FakeStats());
        Assert.True(window.IsVisible);

        window.Close();
        Assert.True(window.IsVisible);

        window.Hide();
        window.Show();
        Assert.True(window.IsVisible);
        window.CloseForGood();
    });

    // Alt+F4 reaches the window once a click or a drag has activated it. Hiding it there would leave the state
    // saying "shown" and the bar's line hidden, so it asks to go back to the bar, like the menu item.
    [Fact]
    public void A_close_request_asks_to_return_to_the_bar() => StaThread.Run(() =>
    {
        var window = Window(new FakeStats());
        var returned = 0;
        window.ReturnRequested += () => returned++;

        window.Close();

        Assert.Equal(1, returned);
        window.Show(); // still alive: a closed WPF window would throw here
        Assert.True(window.IsVisible);
        window.CloseForGood();
    });

    // A hand-edited state.json can hold anything. WPF throws for a Left or Top outside the Int32 range, and the
    // restore at startup has no try/catch, so these once crashed the app on every launch.
    [Theory]
    [InlineData(double.PositiveInfinity, 100)]
    [InlineData(100, double.NegativeInfinity)]
    [InlineData(1e300, 1e300)]
    [InlineData(-1e300, 0)]
    [InlineData(double.NaN, double.NaN)]
    public void A_stored_position_that_is_not_a_number_does_not_throw(double left, double top) => StaThread.Run(() =>
    {
        var window = Window(new FakeStats());

        window.ShowAt(new DetachedStatsState(true, left, top));
        var (x, y) = (window.Left, window.Top);
        window.CloseForGood(); // it is on a screen now, so it goes at once

        Assert.True(double.IsFinite(x) && double.IsFinite(y), $"({x}, {y})");
    });

    // DragMove has no clamp, so a window is easily left with its top-left corner just outside the screen. The bar
    // goes to the NEAREST monitor; this window used to jump to the primary's top right instead.
    [Fact]
    public void A_corner_just_past_the_screen_edge_stays_on_that_screen() => StaThread.Run(() =>
    {
        var window = Window(new FakeStats());

        window.ShowAt(new DetachedStatsState(true, -50, 300));
        var (x, y) = (window.Left, window.Top);
        window.CloseForGood();

        var monitor = Monitoring.NativeMethods.MonitorFromPoint(new Monitoring.NativeMethods.POINT { X = -50, Y = 300 }, Monitoring.NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new Monitoring.NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Monitoring.NativeMethods.MONITORINFO>() };
        Assert.True(Monitoring.NativeMethods.GetMonitorInfo(monitor, ref info));
        Monitoring.NativeMethods.GetDpiForMonitor(monitor, Monitoring.NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out _);

        Assert.True(x >= info.rcMonitor.Left / (dpiX / 96.0), $"Left {x}");
        Assert.Equal(300, y);
    });

    [Fact]
    public void The_menu_puts_the_stats_back_on_the_bar() => StaThread.Run(() =>
    {
        var window = Window(new FakeStats());
        var returned = 0;
        window.ReturnRequested += () => returned++;

        var item = Assert.IsType<MenuItem>(Assert.Single(window.ContextMenu.Items));
        Assert.Equal("Put stats back on the bar", item.Header);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        Assert.Equal(1, returned);
        window.CloseForGood();
    });
}
