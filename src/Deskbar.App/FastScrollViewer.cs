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
/// of three lines), whatever the event's Delta. Here every event moves twice that, also whatever its Delta. Scaling by
/// Delta/120 was tried and rejected in review: a precision touchpad or a free-spinning wheel sends many events of 10 to
/// 40, so a Delta-scaled step would have made those devices SLOWER than stock WPF, the opposite of the request. Windows'
/// "one screen at a time" setting (WheelScrollLines -1) moves one view per event: two views would skip a whole screen of
/// tasks unseen.
///
/// SEVERAL EVENTS BEFORE ONE LAYOUT: ScrollToVerticalOffset takes an absolute offset, is queued, and VerticalOffset only
/// changes at the next layout, so two wheel events before that layout would both start from the same offset and the
/// second step would be lost. The target is therefore kept between events and dropped once the viewer reports a scroll
/// (ScrollChanged runs after layout, when VerticalOffset is current again).
/// </remarks>
internal sealed class FastScrollViewer : ScrollViewer
{
    // How many times WPF's own wheel step one notch moves. The task asked for "twice as fast".
    internal const double Speed = 2;

    // WPF's line height for a pixel-scrolling viewer (ScrollViewer._scrollLineDelta, 16 DIP), which its wheel step is
    // measured in.
    const double LineDip = 16;

    // Where the wheel has asked the view to go but layout has not taken it yet; null when VerticalOffset is current.
    double? pendingOffset;

    public FastScrollViewer()
    {
        // THE THEME'S LOOK: the app runs WPF's Fluent theme (ThemeMode="System", App.xaml), whose ScrollViewer style is an
        // implicit one keyed by typeof(ScrollViewer). WPF applies an implicit style only to that exact type, so a subclass
        // would fall back to the classic template, whose scroll bar takes 12 DIP of width where Fluent's floats over the
        // content: every task line got 12 DIP narrower and the full line's title fell under TasksPanelLayout.FullTitleReadable
        // (TasksBoardTests.At_the_full_lines_narrowest_width_the_columns_stay_inside_the_viewport, which failed the v2.6
        // release build after this class first merged). A resource reference to the same key takes the theme's style,
        // and follows it when the OS switches light and dark.
        SetResourceReference(StyleProperty, typeof(ScrollViewer));
        // After layout the offset is real again, so the next wheel event starts from it. Only this viewer's own scrolls:
        // ScrollChanged bubbles, and a nested viewer's scroll says nothing about this one's offset.
        ScrollChanged += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, this)) pendingOffset = null;
        };
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        // WPF does not call this for an event a nested viewer already took (the class handler is not registered for
        // handled events), so the innermost viewer wins; PassWheelAtEnd's hand-off arrives here as a fresh event. The
        // check is the stock viewer's own, kept for a caller that invokes the handler by hand.
        if (e.Handled) return;
        e.Handled = true;
        if (ScrollableHeight <= 0) return;
        var target = Math.Clamp((pendingOffset ?? VerticalOffset) + WheelStep(e.Delta, SystemParameters.WheelScrollLines, ViewportHeight), 0, ScrollableHeight);
        pendingOffset = target;
        ScrollToVerticalOffset(target);
    }

    /// <summary>
    /// How far, in DIP, a wheel event of <paramref name="delta"/> moves the view: positive scrolls down. Twice WPF's step
    /// for every event, whatever the size of its Delta (see THE STEP); one view per event under "one screen at a time".
    /// </summary>
    internal static double WheelStep(int delta, int wheelScrollLines, double viewportHeight) =>
        -Math.Sign(delta) * (wheelScrollLines < 0 ? viewportHeight : Speed * wheelScrollLines * LineDip);
}
