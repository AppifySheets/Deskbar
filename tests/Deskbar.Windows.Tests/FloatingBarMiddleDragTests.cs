using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Deskbar.App;
using Deskbar.Core.Geometry;
using Deskbar.Windows.Monitoring;

namespace Deskbar.Windows.Tests;

// A middle drag across two monitors of different scale made the bar flicker and jump (MiddleDragTests has the
// numbers). Windows hands the bar a new scale when it crosses and WPF resizes it, so what these pin on a real
// window is that the drag works in pixels and places the bar from the size it has NOW. Parked off screen like
// every bar here and driven through the two entry points the mouse handlers use; the cursor is wherever the
// test says. A second monitor of another scale cannot be had in a test, so the change of scale is the message
// Windows sends for it, WM_DPICHANGED, sent to the window.
public class FloatingBarMiddleDragTests
{
    const int WmDpiChanged = 0x02E0;

    [DllImport("user32.dll")] static extern nint SendMessage(nint hwnd, int message, nint wParam, nint lParam);

    static nint HandleOf(FloatingBar bar) => new WindowInteropHelper(bar).Handle;

    static NativeMethods.RECT RectOf(FloatingBar bar)
    {
        Assert.True(NativeMethods.GetWindowRect(HandleOf(bar), out var rect));
        return rect;
    }

    [Fact]
    public void The_bar_moves_by_exactly_the_cursors_travel_in_pixels() => StaThread.Run(() =>
    {
        using var shown = Harness.Build().ShowBar();
        var bar = shown.Bar;
        var before = RectOf(bar);
        var (cursorX, cursorY) = (before.Left + 20, before.Top + 30);

        Assert.True(bar.GrabBarAt(cursorX, cursorY));
        bar.MoveBarTo(cursorX + 120, cursorY + 45);

        var after = RectOf(bar);
        Assert.Equal((before.Left + 120, before.Top + 45), (after.Left, after.Top));
        Assert.Equal((before.Right - before.Left, before.Bottom - before.Top), (after.Right - after.Left, after.Bottom - after.Top));

        // What the drop reads: WPF's own Left and Top have followed a move it did not make itself, because
        // SnapToEdges and Save work from them when the button comes up.
        var scale = VisualTreeHelper.GetDpi(bar);
        Assert.InRange(bar.Left * scale.DpiScaleX, after.Left - 1, after.Left + 1);
        Assert.InRange(bar.Top * scale.DpiScaleY, after.Top - 1, after.Top + 1);
    });

    // The scale changing in the middle of the drag: the bar is resized by WPF, and the next move must put the
    // same part of it back under the cursor. The old arithmetic held the position in DIPs and divided by the
    // window's current scale, so after this it landed the bar somewhere else entirely.
    [Fact]
    public void A_change_of_scale_in_the_middle_of_a_drag_keeps_the_grab_point_under_the_cursor() => StaThread.Run(() =>
    {
        using var shown = Harness.Build().ShowBar();
        var bar = shown.Bar;
        var start = RectOf(bar);
        var width = start.Right - start.Left;
        var (cursorX, cursorY) = (start.Left + width / 4, start.Top + 60);
        Assert.True(bar.GrabBarAt(cursorX, cursorY));
        bar.MoveBarTo(cursorX + 300, cursorY + 100);

        // Windows now says the bar is on a screen of another scale.
        var oldDpi = (int)Math.Round(VisualTreeHelper.GetDpi(bar).PixelsPerInchX);
        var newDpi = oldDpi == 96 ? 144 : 96;
        var moved = RectOf(bar);
        var suggested = new NativeMethods.RECT
        {
            Left = moved.Left, Top = moved.Top,
            Right = moved.Left + (moved.Right - moved.Left) * newDpi / oldDpi,
            Bottom = moved.Top + (moved.Bottom - moved.Top) * newDpi / oldDpi,
        };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.RECT>());
        try
        {
            Marshal.StructureToPtr(suggested, pointer, false);
            SendMessage(HandleOf(bar), WmDpiChanged, (newDpi << 16) | newDpi, pointer);
        }
        finally { Marshal.FreeHGlobal(pointer); }
        Assert.Equal(newDpi, (int)Math.Round(VisualTreeHelper.GetDpi(bar).PixelsPerInchX));
        Assert.NotEqual(moved.Right - moved.Left, RectOf(bar).Right - RectOf(bar).Left);

        // The next move, the cursor where it was.
        bar.MoveBarTo(cursorX + 300, cursorY + 100);

        var after = RectOf(bar);
        var afterWidth = after.Right - after.Left;
        Assert.InRange(cursorX + 300 - after.Left, afterWidth / 4.0 - 1, afterWidth / 4.0 + 1);
    });

    // Two screens of different scale described around the parked bar (a test machine has no such pair there): the
    // border is `borderOffset` px in from the bar's left edge, 150% to its left and 100% to its right, both
    // taller than the bar. Whatever the real monitors say, the bar is still moved by SetWindowPos.
    static IReadOnlyList<MiddleDrag.Screen> ScreensAround(NativeMethods.RECT bar, int borderOffset, int leftDpi = 144, int rightDpi = 96)
    {
        var border = bar.Left + borderOffset;
        return
        [
            new(border - 3000, bar.Top - 200, border, bar.Bottom + 200, leftDpi),
            new(border, bar.Top - 200, border + 2000, bar.Bottom + 200, rightDpi),
        ];
    }

    // The harness parks the bar at -32000 DIPs, which at 150% Windows clamps to -32768 px: no room to move left.
    // Still far off every real monitor, but with room.
    static NativeMethods.RECT ParkWithRoom(FloatingBar bar)
    {
        NativeMethods.SetWindowPos(HandleOf(bar), nint.Zero, -20000, -20000, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        return RectOf(bar);
    }

    [Fact]
    public void A_bar_dropped_across_screens_of_different_scale_ends_wholly_on_the_one_holding_most_of_it() => StaThread.Run(() =>
    {
        using var shown = Harness.Build().ShowBar();
        var bar = shown.Bar;
        var start = ParkWithRoom(bar);
        var width = start.Right - start.Left;
        // 40 px of the bar on the left screen, the rest on the right one.
        var screens = ScreensAround(start, 40);
        bar.ScreensOf = () => screens;

        bar.SettleOnOneScreen();

        var after = RectOf(bar);
        Assert.Equal(start.Left + 40, after.Left);   // flush with the right screen's left edge
        Assert.Equal(start.Top, after.Top);          // at the height it was dropped
        Assert.Equal(width, after.Right - after.Left);
    });

    [Fact]
    public void A_bar_dropped_mostly_on_the_left_screen_ends_wholly_on_it() => StaThread.Run(() =>
    {
        using var shown = Harness.Build().ShowBar();
        var bar = shown.Bar;
        var start = ParkWithRoom(bar);
        var width = start.Right - start.Left;
        // The border is 40 px short of the bar's right edge: 40 px of it on the right screen.
        var screens = ScreensAround(start, width - 40);
        bar.ScreensOf = () => screens;

        bar.SettleOnOneScreen();

        Assert.Equal(start.Left - 40, RectOf(bar).Left);   // its right edge on the border
    });

    [Fact]
    public void A_bar_dropped_across_screens_of_one_scale_stays_where_it_is() => StaThread.Run(() =>
    {
        using var shown = Harness.Build().ShowBar();
        var bar = shown.Bar;
        var start = ParkWithRoom(bar);
        var screens = ScreensAround(start, 40, leftDpi: 144, rightDpi: 144);
        bar.ScreensOf = () => screens;

        bar.SettleOnOneScreen();

        Assert.Equal((start.Left, start.Top), (RectOf(bar).Left, RectOf(bar).Top));
    });

    // The wiring: the button coming up settles the bar BEFORE it is snapped and saved, so what is stored is the
    // place it was put and not the place the corner was on.
    [Fact]
    public void Releasing_the_middle_button_settles_the_bar_before_it_is_saved() => StaThread.Run(() =>
    {
        var harness = Harness.Build();
        using var shown = harness.ShowBar();
        var bar = shown.Bar;
        var start = ParkWithRoom(bar);
        var screens = ScreensAround(start, 40);
        bar.ScreensOf = () => screens;
        typeof(FloatingBar).GetField("middleDragging", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(bar, true);

        bar.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Middle)
            { RoutedEvent = Mouse.PreviewMouseUpEvent });

        var after = RectOf(bar);
        Assert.Equal(start.Left + 40, after.Left);
        var saved = harness.Manager.State.FloatingBar;
        Assert.NotNull(saved);
        var scale = VisualTreeHelper.GetDpi(bar).DpiScaleX;
        Assert.InRange(saved!.Left * scale, after.Left - 1, after.Left + 1);
    });

    // The debounced readjustment puts the bar at its STORED position, which is the one from before the drag.
    [Fact]
    public void The_layout_is_not_readjusted_under_a_bar_that_is_being_dragged() => StaThread.Run(() =>
    {
        using var shown = Harness.Build().ShowBar();
        var bar = shown.Bar;
        const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
        typeof(FloatingBar).GetField("middleDragging", Private)!.SetValue(bar, true);
        var timer = (System.Windows.Threading.DispatcherTimer)typeof(FloatingBar).GetField("layoutSettling", Private)!.GetValue(bar)!;
        var (left, top) = (bar.Left, bar.Top);

        bar.SettleLayout();

        Assert.Equal((left, top), (bar.Left, bar.Top));
        Assert.True(timer.IsEnabled, "it asks again once the drag is over");
    });
}
