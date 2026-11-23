using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Deskbar.App;
using Deskbar.Core.Domain;
using Deskbar.Core.Tiling;
using Deskbar.Windows.Monitoring;

namespace Deskbar.Windows.Tests;

// The lock mark's own window (LockMarkWindow): a padlock laid over a tile's title bar, never owned by the tile (spec
// 2026-11-22-tile-lock-and-other-screens-design.md, "The pin on the window"). What is pinned down here is the part a
// screenshot cannot show: where it sits in z-order relative to its tile, the rectangle it lands on, the styles that
// keep it from ever taking focus, and that a click is reported. Run on a hidden desktop (scripts/test-hidden.ps1).
public class LockMarkWindowTests
{
    static void Pump(TimeSpan span)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = span };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    // A plain window standing in for a tile.
    static (Window Window, nint Handle) Target(string title, int left = 200)
    {
        var window = new Window { Title = title, Width = 400, Height = 300, Left = left, Top = 200, ShowActivated = false };
        window.Show();
        return (window, new WindowInteropHelper(window).Handle);
    }

    static LockMark MarkOn(nint target, bool locked)
    {
        GetWindowRect(target, out var r);
        return new LockMark(new WindowHandle(target), new WindowRect(r.Right - 80, r.Top + 4, r.Right - 44, r.Top + 40), locked);
    }

    [Fact]
    public void Put_lands_directly_above_its_target() => StaThread.Run(() =>
    {
        var (window, target) = Target("lock mark target");
        var mark = new LockMarkWindow();
        mark.Put(MarkOn(target, locked: true), target);
        Pump(TimeSpan.FromMilliseconds(100));

        Assert.True(mark.Handle == VisibleAbove(target), Describe(target, mark.Handle));
        mark.Close();
        window.Close();
    });

    [Fact]
    public void Put_lands_on_the_rectangle_asked_for() => StaThread.Run(() =>
    {
        var (window, target) = Target("lock mark target");
        var mark = new LockMarkWindow();
        var wanted = MarkOn(target, locked: true);
        mark.Put(wanted, target);
        Pump(TimeSpan.FromMilliseconds(100));

        GetWindowRect(mark.Handle, out var r);
        Assert.Equal(wanted.Frame, new WindowRect(r.Left, r.Top, r.Right, r.Bottom));
        mark.Close();
        window.Close();
    });

    // The tile in front of every other is the first non-topmost window, so the window above it is a TOPMOST one (the
    // taskbar, the bar, a screen strip). Inserting the mark after that window must still put it directly above the
    // tile, in the normal band, not somewhere else. Found when the full suite ran with a test's topmost window open.
    [Fact]
    public void Put_lands_directly_above_a_tile_at_the_front_of_its_band() => StaThread.Run(() =>
    {
        var topmost = new Window { Title = "lock mark topmost", Width = 100, Height = 100, Left = 50, Top = 50, ShowActivated = false, Topmost = true };
        topmost.Show();
        var (window, target) = Target("lock mark front tile");
        // The target to the front of the normal band, right under the topmost one.
        SetWindowPos(target, 0, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        Pump(TimeSpan.FromMilliseconds(100));
        Assert.Equal(new WindowInteropHelper(topmost).Handle, VisibleAbove(target));

        var mark = new LockMarkWindow();
        mark.Put(MarkOn(target, locked: true), target);
        Pump(TimeSpan.FromMilliseconds(100));

        Assert.Equal(mark.Handle, VisibleAbove(target));
        Assert.Equal(0, (long)NativeMethods.GetWindowLongPtr(mark.Handle, NativeMethods.GWL_EXSTYLE) & WS_EX_TOPMOST);
        mark.Close();
        window.Close();
        topmost.Close();
    });

    // A floating window over the tile covers the mark too: a second Put of the same mark must not lift it back over the
    // window that came in front of its tile.
    [Fact]
    public void A_second_put_keeps_a_window_raised_in_between_in_front() => StaThread.Run(() =>
    {
        var (window, target) = Target("lock mark target");
        var mark = new LockMarkWindow();
        var wanted = MarkOn(target, locked: true);
        mark.Put(wanted, target);
        Pump(TimeSpan.FromMilliseconds(100));

        var (cover, coverHandle) = Target("lock mark cover", left: 220);
        Pump(TimeSpan.FromMilliseconds(100));
        mark.Put(wanted, target);
        Pump(TimeSpan.FromMilliseconds(100));

        Assert.True(Above(coverHandle, mark.Handle), "the window in front of the tile stays in front of its mark");
        Assert.Equal(mark.Handle, VisibleAbove(target));
        cover.Close();
        mark.Close();
        window.Close();
    });

    [Fact]
    public void The_mark_is_a_tool_window_that_never_activates_and_takes_clicks() => StaThread.Run(() =>
    {
        var mark = new LockMarkWindow();
        var ex = (long)NativeMethods.GetWindowLongPtr(mark.Handle, NativeMethods.GWL_EXSTYLE);
        Assert.Equal(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, ex & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
        Assert.Equal(0, ex & WS_EX_TRANSPARENT);
        mark.Close();
    });

    [Fact]
    public void The_look_follows_the_lock() => StaThread.Run(() =>
    {
        var (window, target) = Target("lock mark target");
        var mark = new LockMarkWindow();
        mark.Put(MarkOn(target, locked: true), target);
        Assert.True(mark.LockedLook);
        mark.Put(MarkOn(target, locked: false), target);
        Assert.False(mark.LockedLook);
        mark.Close();
        window.Close();
    });

    [Fact]
    public void A_click_is_reported() => StaThread.Run(() =>
    {
        var mark = new LockMarkWindow();
        var clicks = 0;
        using var _ = mark.Clicked.Subscribe(_ => clicks++);
        var element = (UIElement)mark.Content;
        element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonUpEvent,
        });
        Assert.Equal(1, clicks);
        mark.Close();
    });

    // RingFrame shares the same "directly above" placement (ZOrder.PlaceDirectlyAbove), so it is held to the same rule.
    [Fact]
    public void A_ring_frame_still_lands_directly_above_its_target() => StaThread.Run(() =>
    {
        var (window, target) = Target("ring frame target");
        var frame = new RingFrame();
        frame.Show();
        GetWindowRect(target, out var r);
        frame.Place(new NativeMethods.RECT { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom }, "#FFFFFF", target, pixels: 1);
        Pump(TimeSpan.FromMilliseconds(100));

        Assert.True(frame.Handle == VisibleAbove(target), Describe(target, frame.Handle));
        frame.Close();
        window.Close();
    });

    // The first VISIBLE window above `target` in z-order: the one that would show over it. Invisible windows are
    // skipped because WPF keeps an invisible helper ("Hidden Window") per thread, which the z-order can put anywhere
    // and which covers nothing. Found when the full suite first ran: the helper sat between a tile and its mark.
    static nint VisibleAbove(nint target)
    {
        var w = GetWindow(target, GW_HWNDPREV);
        while (w != 0 && !IsWindowVisible(w)) w = GetWindow(w, GW_HWNDPREV);
        return w;
    }

    // The windows from the top of the z-order down to `target`, for a failure message: which window came between.
    static string Describe(nint target, nint overlay)
    {
        var lines = new List<string>();
        for (var w = GetWindow(target, GW_HWNDPREV); w != 0 && lines.Count < 8; w = GetWindow(w, GW_HWNDPREV))
        {
            var c = new System.Text.StringBuilder(128); GetClassName(w, c, 128);
            var t = new System.Text.StringBuilder(128); GetWindowText(w, t, 128);
            var ex = (long)NativeMethods.GetWindowLongPtr(w, NativeMethods.GWL_EXSTYLE);
            lines.Add($"{w:X} {(w == overlay ? "OVERLAY " : "")}{c} '{t}' visible={IsWindowVisible(w)} topmost={(ex & 8) != 0}");
        }
        GetWindowRect(overlay, out var r);
        return $"overlay {overlay:X} at {r.Left},{r.Top},{r.Right},{r.Bottom} visible={IsWindowVisible(overlay)}; above target: " + string.Join(" | ", lines);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(nint hwnd, System.Text.StringBuilder name, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(nint hwnd, System.Text.StringBuilder text, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool IsWindowVisible(nint hwnd);

    // Is `upper` somewhere above `lower` in z-order? Walks up from `lower`.
    static bool Above(nint upper, nint lower)
    {
        for (var w = GetWindow(lower, GW_HWNDPREV); w != 0; w = GetWindow(w, GW_HWNDPREV))
            if (w == upper) return true;
        return false;
    }

    const uint GW_HWNDPREV = 3;
    const long WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetWindowRect(nint hwnd, out RECT rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern nint GetWindow(nint hwnd, uint command);
}
