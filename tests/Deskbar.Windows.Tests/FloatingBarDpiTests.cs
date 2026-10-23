using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Deskbar.App;

namespace Deskbar.Windows.Tests;

// Petre: "right now i'm seeing the activity by the bar", "showing hiding". The bar re-laid itself out every
// 1.4 seconds without end, measured in the trace: thousands of "dpi changed 240 -> 240" lines between each
// "display layout settled". The window's DpiChanged handler (added with Per-Monitor V2, #242) took every
// DpiChanged that reached it as the window's own, but an Image raises the same routed event and it bubbles,
// so every rebuild's icons asked for another readjustment, which rebuilt the icons.
public class FloatingBarDpiTests
{
    static readonly DpiScale At150 = new(1.5, 1.5);
    static readonly DpiScale At250 = new(2.5, 2.5);

    // The mechanism, pinned so the explanation above stays true: an Image's DpiChanged IS the window's event,
    // so an Image's reaches the window's handlers as it bubbles.
    [Fact]
    public void An_images_dpi_change_is_the_same_routed_event_as_the_windows() =>
        Assert.Same(Window.DpiChangedEvent, Image.DpiChangedEvent);

    [Fact]
    public void The_windows_own_change_of_scale_counts() => StaThread.Run(() =>
    {
        var window = new Window();
        Assert.True(FloatingBar.IsOwnDpiChange(window, window, At150, At250));
    });

    [Fact]
    public void An_icon_reporting_the_same_scale_does_not() => StaThread.Run(() =>
    {
        var window = new Window();
        Assert.False(FloatingBar.IsOwnDpiChange(window, new Image(), At250, At250));
    });

    // Even a real change reported by an icon is the window's business only through the window's own event.
    [Fact]
    public void An_icon_reporting_a_new_scale_does_not_either() => StaThread.Run(() =>
    {
        var window = new Window();
        Assert.False(FloatingBar.IsOwnDpiChange(window, new Image(), At150, At250));
    });

    [Fact]
    public void The_window_reporting_the_scale_it_already_had_does_not() => StaThread.Run(() =>
    {
        var window = new Window();
        Assert.False(FloatingBar.IsOwnDpiChange(window, window, At250, At250));
    });
}
