using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Deskbar.App;

namespace Deskbar.Windows.Tests;

// The tasks panel's scrollers move twice WPF's wheel step (task 85df5e3f, FastScrollViewer): the step's arithmetic, and a
// real viewer laid out off screen taking wheel events, one at a time and several before a layout. No window is shown.
public class FastScrollViewerTests
{
    // WPF's own step at Windows' default of three lines a notch: 3 lines of 16 DIP.
    const double WpfStep = 3 * 16;

    [Theory]
    [InlineData(-120, 3, 2 * WpfStep)]   // one notch down: twice WPF's 48
    [InlineData(120, 3, -2 * WpfStep)]   // one notch up
    [InlineData(-240, 3, 2 * WpfStep)]   // any Delta is one event's step, as in stock WPF
    [InlineData(-15, 3, 2 * WpfStep)]    // a touchpad's small Delta is not slowed below stock
    [InlineData(-120, 5, 2 * 5 * 16.0)]  // the user's own lines-per-notch setting is followed
    public void An_event_moves_twice_wpfs_step(int delta, int lines, double expected) =>
        Assert.Equal(expected, FastScrollViewer.WheelStep(delta, lines, viewportHeight: 300), precision: 6);

    [Fact]
    public void One_screen_at_a_time_moves_one_screen() =>
        Assert.Equal(300, FastScrollViewer.WheelStep(-120, -1, viewportHeight: 300), precision: 6);

    [Fact]
    public void A_wheel_notch_scrolls_the_view_twice_as_far() => StaThread.Run(() =>
    {
        var viewer = Laid(out _);
        Wheel(viewer, -120);
        viewer.UpdateLayout();
        Assert.Equal(FastScrollViewer.WheelStep(-120, SystemParameters.WheelScrollLines, viewer.ViewportHeight), viewer.VerticalOffset, precision: 3);
    });

    // Three notches before the next layout: each starts where the last one asked to go, not from the stale offset, so the
    // view moves three steps and not one.
    [Fact]
    public void Notches_before_a_layout_add_up() => StaThread.Run(() =>
    {
        var viewer = Laid(out _);
        Wheel(viewer, -120);
        Wheel(viewer, -120);
        Wheel(viewer, -120);
        viewer.UpdateLayout();
        Assert.Equal(3 * FastScrollViewer.WheelStep(-120, SystemParameters.WheelScrollLines, viewer.ViewportHeight), viewer.VerticalOffset, precision: 3);
    });

    [Fact]
    public void The_view_stops_at_its_ends() => StaThread.Run(() =>
    {
        var viewer = Laid(out _);
        Wheel(viewer, 120);
        viewer.UpdateLayout();
        Assert.Equal(0, viewer.VerticalOffset);
        Enumerable.Range(0, 200).ToList().ForEach(_ => Wheel(viewer, -120));
        viewer.UpdateLayout();
        Assert.Equal(viewer.ScrollableHeight, viewer.VerticalOffset, precision: 3);
    });

    // A 300 DIP view over 5000 DIP of content, measured and arranged without a window.
    static FastScrollViewer Laid(out FrameworkElement content)
    {
        content = new Border { Height = 5000 };
        var viewer = new FastScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        viewer.Measure(new Size(200, 300));
        viewer.Arrange(new Rect(0, 0, 200, 300));
        viewer.UpdateLayout();
        return viewer;
    }

    static void Wheel(UIElement target, int delta) =>
        target.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta) { RoutedEvent = UIElement.MouseWheelEvent });
}
