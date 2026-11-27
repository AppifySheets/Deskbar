using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Deskbar.App;

/// <summary>
/// A ScrollViewer whose mouse wheel moves twice as far as WPF's own (task 85df5e3f, "make scrolling twice as fast", about
/// the tasks panel). Everything else is the stock ScrollViewer, so code that casts to ScrollViewer, binds to its
/// ViewportHeight or walks up to "the nearest ScrollViewer" (TasksWindow.ThreadCap's ListAround) works unchanged.
/// </summary>
/// <remarks>
/// WHY A SUBCLASS: a ScrollViewer takes the wheel in its class handler (OnMouseWheel), which runs before any handler added
/// on the instance and marks the event handled, so an instance handler cannot change the step after the fact; and a
/// PreviewMouseWheel on an outer list tunnels before the inner scrollers (a capped thread, the subtask shelf) see the
/// wheel, so it would steal their steps. Overriding OnMouseWheel keeps the bubbling order intact: the innermost scroller
/// under the pointer takes the step first, exactly as before, only bigger.
///
/// THE STEP: WPF scrolls a pixel-scrolling viewer by WheelScrollLines × 16 DIP per wheel event (48 DIP at Windows' default
/// of three lines), whatever the event's Delta. Here a notch (Delta 120) moves twice that, and a Delta that is a fraction
/// or a multiple of a notch moves that fraction or multiple, so a fast flick that Windows reports as one event of 240
/// moves two notches' worth. Windows' "one screen at a time" setting (WheelScrollLines -1) scrolls one view per notch in
/// WPF and twice that here.
///
/// SEVERAL EVENTS IN ONE FRAME: ScrollToVerticalOffset is queued and VerticalOffset only changes at the next layout, so a
/// burst of wheel events before that layout would each start from the same stale offset and collapse into one step. The
/// target is therefore kept between events and dropped once the viewer reports a scroll (ScrollChanged runs after layout,
/// when VerticalOffset is current again).
/// </remarks>
internal sealed class FastScrollViewer : ScrollViewer
{
    // How many times WPF's own wheel step one notch moves. The task asked for "twice as fast".
    internal const double Speed = 2;

    // WPF's line height for a pixel-scrolling viewer (ScrollViewer._scrollLineDelta, 16 DIP), which its wheel step is
    // measured in.
    const double LineDip = 16;

    // One wheel notch as Windows reports it (WHEEL_DELTA).
    const double Notch = 120;

    // Where the wheel has asked the view to go but layout has not taken it yet; null when VerticalOffset is current.
    double? pendingOffset;

    public FastScrollViewer() =>
        // After layout the offset is real again, so the next wheel event starts from it. Only this viewer's own scrolls:
        // ScrollChanged bubbles, and a nested viewer's scroll says nothing about this one's offset.
        ScrollChanged += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, this)) pendingOffset = null;
        };

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        // Taken already by a viewer nested inside this one (or by the PassWheelAtEnd hand-off, which marks the original
        // handled and raises a fresh event here): the stock viewer does nothing either.
        if (e.Handled) return;
        e.Handled = true;
        if (ScrollableHeight <= 0) return;
        var target = Math.Clamp((pendingOffset ?? VerticalOffset) + WheelStep(e.Delta, SystemParameters.WheelScrollLines, ViewportHeight), 0, ScrollableHeight);
        pendingOffset = target;
        ScrollToVerticalOffset(target);
    }

    /// <summary>
    /// How far, in DIP, a wheel event of <paramref name="delta"/> moves the view: positive scrolls down. Twice WPF's step
    /// per notch, scaled by how many notches the event carries.
    /// </summary>
    internal static double WheelStep(int delta, int wheelScrollLines, double viewportHeight) =>
        -delta / Notch * Speed * (wheelScrollLines < 0 ? viewportHeight : wheelScrollLines * LineDip);
}
