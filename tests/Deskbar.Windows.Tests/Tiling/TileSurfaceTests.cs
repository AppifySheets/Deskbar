using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Deskbar.Core.Domain;
using Deskbar.Windows.Monitoring;
using Deskbar.Windows.Tiling;

namespace Deskbar.Windows.Tests.Tiling;

using static NativeMethods;

// TileSurface against REAL windows, because every claim here is about what Windows does: which styles a WPF
// window really carries, where DWM says its visible frame is, and whether a WinEvent hook hears a move. No
// fake can answer any of those.
//
// Every window these tests move is one the test itself created, on its own STA thread (see StaThread), and
// none is activated: they appear briefly on the machine running the tests and are closed before the test
// ends. Nothing here touches any other window.
public class TileSurfaceTests
{
    // A plain resizable top-level window, shown without taking focus. ShowInTaskbar stays TRUE on purpose:
    // WPF implements ShowInTaskbar=false by giving the window a hidden OWNER, and an owned window cannot tile,
    // so a "normal" window built that way would fail CanTile for a reason the test did not mean to test.
    static Window Shown(Action<Window>? configure = null)
    {
        var window = new Window
        {
            Title = "tile surface test",
            Width = 420, Height = 300, Left = 120, Top = 120,
            ShowActivated = false,
        };
        configure?.Invoke(window);
        window.Show();
        return window;
    }

    static WindowHandle HandleOf(Window window) => new(new WindowInteropHelper(window).Handle);

    // Runs the dispatcher (and with it the thread's Win32 message queue, which is where an out-of-context
    // WinEvent hook delivers its callbacks) for a fixed time. These tests run on a bare STA thread with no
    // message loop of its own, so without this neither the hook nor the debounce timer could ever fire.
    static void Pump(TimeSpan span)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = span };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    // Pumps in short steps until `done` holds or the bound passes. The bound is generous because it costs
    // nothing when the answer comes on time.
    static void PumpUntil(Func<bool> done, TimeSpan bound)
    {
        var clock = Stopwatch.StartNew();
        while (!done() && clock.Elapsed < bound) Pump(TimeSpan.FromMilliseconds(20));
    }

    // Moves one of the TEST's windows by a few pixels, straight through SetWindowPos, so the move raises
    // EVENT_OBJECT_LOCATIONCHANGE exactly as an app or a drag would.
    static void Nudge(WindowHandle window, int dx)
    {
        GetWindowRect(window.Value, out var r);
        SetWindowPos(window.Value, 0, r.Left + dx, r.Top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    static WindowRect ExtendedFrameOf(WindowHandle window)
    {
        Assert.Equal(0, DwmGetWindowAttributeRect(window.Value, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r,
            System.Runtime.InteropServices.Marshal.SizeOf<RECT>()));
        return new WindowRect(r.Left, r.Top, r.Right, r.Bottom);
    }

    [Fact]
    public void A_normal_window_can_tile() => StaThread.Run(() =>
    {
        var window = Shown();
        using var surface = new TileSurface(_ => false);

        Assert.True(surface.CanTile(HandleOf(window)));
        window.Close();
    });

    [Fact]
    public void A_tool_window_cannot() => StaThread.Run(() =>
    {
        var window = Shown(w => w.WindowStyle = WindowStyle.ToolWindow);
        using var surface = new TileSurface(_ => false);

        Assert.False(surface.CanTile(HandleOf(window)));
        window.Close();
    });

    // ResizeMode.NoResize takes WS_THICKFRAME away: a window that cannot be resized by hand cannot be given a
    // slot's size either, or it would sit in its tile at its own size with a hole around it.
    [Fact]
    public void A_fixed_size_window_cannot() => StaThread.Run(() =>
    {
        var window = Shown(w => w.ResizeMode = ResizeMode.NoResize);
        using var surface = new TileSurface(_ => false);

        Assert.False(surface.CanTile(HandleOf(window)));
        window.Close();
    });

    // A dialog or a palette belongs to its owner and floats with it, never in a slot of its own.
    [Fact]
    public void An_owned_window_cannot() => StaThread.Run(() =>
    {
        var owner = Shown();
        var owned = Shown(w => w.Owner = owner);
        using var surface = new TileSurface(_ => false);

        Assert.False(surface.CanTile(HandleOf(owned)));
        owned.Close();
        owner.Close();
    });

    [Fact]
    public void Our_own_windows_cannot() => StaThread.Run(() =>
    {
        var window = Shown();
        using var surface = new TileSurface(_ => true);

        Assert.False(surface.CanTile(HandleOf(window)));
        window.Close();
    });

    // A closed window's handle names nothing: it cannot tile and has no frame, and neither question throws.
    [Fact]
    public void A_window_that_has_gone_cannot_tile_and_has_no_frame() => StaThread.Run(() =>
    {
        var window = Shown();
        var handle = HandleOf(window);
        window.Close();
        using var surface = new TileSurface(_ => false);

        Assert.False(surface.CanTile(handle));
        Assert.False(surface.VisibleFrameOf(handle).HasValue);
        Assert.True(surface.Place(handle, new WindowRect(0, 0, 400, 300)).IsFailure);
    });

    // A minimized window's rectangles are the iconic ones, off at -32000 (#107), so there is no visible frame
    // to report, and None says so rather than handing the Tiler a nonsense rectangle.
    [Fact]
    public void A_minimized_window_has_no_visible_frame() => StaThread.Run(() =>
    {
        var window = Shown();
        window.WindowState = WindowState.Minimized;
        using var surface = new TileSurface(_ => false);

        Assert.True(surface.IsMinimized(HandleOf(window)));
        Assert.False(surface.VisibleFrameOf(HandleOf(window)).HasValue);
        window.Close();
    });

    // The screens come back numbered as the bar numbers them, with a real work area and DPI, and the screen a
    // window is on is one of them.
    [Fact]
    public void Every_screen_has_a_work_area_and_a_dpi_and_a_window_is_on_one_of_them() => StaThread.Run(() =>
    {
        var window = Shown();
        using var surface = new TileSurface(_ => false);

        var screens = surface.Screens();

        Assert.NotEmpty(screens);
        Assert.All(screens, s =>
        {
            Assert.True(s.Number >= 1, $"screen number {s.Number} should be a display number");
            Assert.True(s.WorkArea.Width > 0 && s.WorkArea.Height > 0, $"screen {s.Number} has an empty work area");
            Assert.True(s.Dpi >= 96, $"screen {s.Number} reports dpi {s.Dpi}");
        });
        Assert.Contains(surface.ScreenOf(HandleOf(window)), screens.Select(s => s.Number));
        window.Close();
    });

    // The brief's test, and the Tiler's first requirement: Place is synchronous. The Tiler reads the frame
    // back the moment Place returns and keeps it as the window's accepted frame, its guard against moving a
    // window in a loop, so the frame must ALREADY be the slot here, with no pumping in between. The default
    // WPF window has the real invisible resize border, so this also proves the compensation against DWM.
    [Fact]
    public void Place_puts_the_visible_frame_on_the_slot() => StaThread.Run(() =>
    {
        var window = Shown();
        var handle = HandleOf(window);
        using var surface = new TileSurface(_ => false);
        var work = surface.Screens().Single(s => s.Number == surface.ScreenOf(handle)).WorkArea;
        var slot = new WindowRect(work.Left + 50, work.Top + 60, work.Left + 550, work.Top + 380);

        var placed = surface.Place(handle, slot);

        Assert.True(placed.IsSuccess, placed.IsFailure ? placed.Error : "");
        Assert.Equal(slot, ExtendedFrameOf(handle));
        Assert.Equal(slot, surface.VisibleFrameOf(handle).GetValueOrDefault());
        window.Close();
    });

    // The Tiler's second requirement: Settled is debounced. A burst of moves closer together than the quiet
    // interval is ONE settle, and it comes only after the window has been still for that interval.
    [Fact]
    public void A_burst_of_moves_settles_once_after_the_quiet_interval() => StaThread.Run(() =>
    {
        var window = Shown();
        var handle = HandleOf(window);
        // Buttons up whatever the real mouse is doing, so a button held on the test machine cannot delay this.
        using var surface = new TileSurface(_ => false) { ButtonsDown = () => false };
        var settled = new List<(WindowHandle Window, TimeSpan At)>();
        var clock = Stopwatch.StartNew();
        using var _ = surface.Settled.Subscribe(w => settled.Add((w, clock.Elapsed)));
        surface.Watch(new HashSet<WindowHandle> { handle });

        Nudge(handle, 5);
        Pump(TimeSpan.FromMilliseconds(60));
        Nudge(handle, 5);
        Pump(TimeSpan.FromMilliseconds(60));
        Nudge(handle, 5);
        var lastMove = clock.Elapsed;
        PumpUntil(() => settled.Count > 0, TimeSpan.FromSeconds(3));
        Pump(TimeSpan.FromMilliseconds(400));

        var only = Assert.Single(settled);
        Assert.Equal(handle, only.Window);
        // A little under 150 to allow for timer granularity, never anything like the 60ms the moves were apart.
        Assert.True(only.At - lastMove >= TimeSpan.FromMilliseconds(130),
            $"settled {(only.At - lastMove).TotalMilliseconds:F0}ms after the last move");
        window.Close();
    });

    // The second half of that requirement: no settle while a mouse button is down, because a slow drag that
    // pauses is not a window that has settled. The button state is the one seam in the surface, so the test
    // can hold a button down without pressing anything on the machine.
    [Fact]
    public void Nothing_settles_while_a_mouse_button_is_down() => StaThread.Run(() =>
    {
        var window = Shown();
        var handle = HandleOf(window);
        var down = true;
        using var surface = new TileSurface(_ => false) { ButtonsDown = () => down };
        var settled = new List<WindowHandle>();
        using var _ = surface.Settled.Subscribe(settled.Add);
        surface.Watch(new HashSet<WindowHandle> { handle });

        Nudge(handle, 5);
        Pump(TimeSpan.FromMilliseconds(600));
        Assert.Empty(settled);

        down = false;
        PumpUntil(() => settled.Count > 0, TimeSpan.FromSeconds(3));
        Assert.Equal([handle], settled);
        window.Close();
    });

    // The Tiler's third requirement: Watch copies its argument. The Tiler may change the set it passed, and
    // what is watched must not change with it.
    [Fact]
    public void Watch_copies_the_set_it_is_given() => StaThread.Run(() =>
    {
        var window = Shown();
        var handle = HandleOf(window);
        using var surface = new TileSurface(_ => false) { ButtonsDown = () => false };
        var settled = new List<WindowHandle>();
        using var _ = surface.Settled.Subscribe(settled.Add);
        var set = new HashSet<WindowHandle> { handle };
        surface.Watch(set);

        set.Clear();
        Nudge(handle, 5);
        PumpUntil(() => settled.Count > 0, TimeSpan.FromSeconds(3));

        Assert.Equal([handle], settled);
        window.Close();
    });

    // Final review, item 2: the Tiler asks IsSettling before it places a window, so a window that moved within
    // the last quiet interval (or is held by a button still down) is not yanked out from under the pointer. True
    // right after a watched window moves; false once it has settled.
    [Fact]
    public void A_watched_window_is_settling_after_a_move_until_it_settles() => StaThread.Run(() =>
    {
        var window = Shown();
        var handle = HandleOf(window);
        using var surface = new TileSurface(_ => false) { ButtonsDown = () => false };
        var settled = new List<WindowHandle>();
        using var _ = surface.Settled.Subscribe(settled.Add);
        surface.Watch(new HashSet<WindowHandle> { handle });
        Assert.False(surface.IsSettling(handle));

        Nudge(handle, 5);
        // The hook's callback arrives through the message queue, so pump until it has been heard.
        PumpUntil(() => surface.IsSettling(handle), TimeSpan.FromSeconds(3));
        Assert.True(surface.IsSettling(handle));

        PumpUntil(() => settled.Count > 0, TimeSpan.FromSeconds(3));
        Assert.Equal([handle], settled);
        Assert.False(surface.IsSettling(handle));
        window.Close();
    });

    // Final review, item 3: a Watch after Dispose (a Tiler that outlived its surface, a refresh landing during
    // logoff) must not put the LOCATIONCHANGE hook back, and Place must not move anything.
    [Fact]
    public void Nothing_is_hooked_or_placed_after_dispose() => StaThread.Run(() =>
    {
        var window = Shown();
        var handle = HandleOf(window);
        var surface = new TileSurface(_ => false);
        surface.Dispose();

        surface.Watch(new HashSet<WindowHandle> { handle });

        Assert.False(surface.IsHooked);
        Assert.True(surface.Place(handle, new WindowRect(100, 100, 500, 400)).IsFailure);
        window.Close();
    });

    // An empty set removes the hook, and a window that is not watched never settles.
    [Fact]
    public void An_unwatched_window_never_settles() => StaThread.Run(() =>
    {
        var window = Shown();
        var handle = HandleOf(window);
        using var surface = new TileSurface(_ => false);
        var settled = new List<WindowHandle>();
        using var _ = surface.Settled.Subscribe(settled.Add);
        surface.Watch(new HashSet<WindowHandle> { handle });
        surface.Watch(new HashSet<WindowHandle>());

        Nudge(handle, 5);
        Pump(TimeSpan.FromMilliseconds(500));

        Assert.Empty(settled);
        window.Close();
    });

    // Tile resize: a change counts as a resize only if a mouse button was down during it (Petre: "i want to be
    // able to resize tiles by dragging the separation"). A drag ENDS with the button released, so the button is
    // already up at the quiet tick; the surface has to remember it from the location changes. Here the seam says
    // "down" while the nudge is heard and "up" by the time the window settles, which is exactly a drag and drop.
    // ByHand is read INSIDE the Settled emission, because that is where the Tiler asks it.
    [Fact]
    public void A_change_with_a_button_down_settles_by_hand() => StaThread.Run(() =>
    {
        var window = Shown();
        var handle = HandleOf(window);
        var down = true;
        using var surface = new TileSurface(_ => false) { ButtonsDown = () => down };
        var byHand = new List<bool>();
        using var _ = surface.Settled.Subscribe(w => byHand.Add(surface.ByHand(w)));
        surface.Watch(new HashSet<WindowHandle> { handle });

        Nudge(handle, 5);
        // The hook's callback arrives through the message queue: the button must still be down when it is heard.
        PumpUntil(() => surface.IsSettling(handle), TimeSpan.FromSeconds(3));
        down = false;
        PumpUntil(() => byHand.Count > 0, TimeSpan.FromSeconds(3));

        Assert.Equal([true], byHand);
        window.Close();
    });

    // An app moving itself, a keyboard snap, Deskbar's own Place: no button anywhere, so not by hand, and the
    // Tiler keeps its existing rules for it.
    [Fact]
    public void A_change_without_a_button_does_not() => StaThread.Run(() =>
    {
        var window = Shown();
        var handle = HandleOf(window);
        using var surface = new TileSurface(_ => false) { ButtonsDown = () => false };
        var byHand = new List<bool>();
        using var _ = surface.Settled.Subscribe(w => byHand.Add(surface.ByHand(w)));
        surface.Watch(new HashSet<WindowHandle> { handle });

        Nudge(handle, 5);
        PumpUntil(() => byHand.Count > 0, TimeSpan.FromSeconds(3));

        Assert.Equal([false], byHand);
        window.Close();
    });

    // The remembered button belongs to ONE change. A drag that settled by hand, followed by the window moving on
    // its own (the Tiler's put-back, an app resizing itself), must not carry the hand over into the second
    // settle, or the put-back would be taken for another resize.
    [Fact]
    public void The_next_change_starts_without_the_hand() => StaThread.Run(() =>
    {
        var window = Shown();
        var handle = HandleOf(window);
        var down = true;
        using var surface = new TileSurface(_ => false) { ButtonsDown = () => down };
        var byHand = new List<bool>();
        using var _ = surface.Settled.Subscribe(w => byHand.Add(surface.ByHand(w)));
        surface.Watch(new HashSet<WindowHandle> { handle });

        Nudge(handle, 5);
        PumpUntil(() => surface.IsSettling(handle), TimeSpan.FromSeconds(3));
        down = false;
        PumpUntil(() => byHand.Count > 0, TimeSpan.FromSeconds(3));
        Nudge(handle, 5);
        PumpUntil(() => byHand.Count > 1, TimeSpan.FromSeconds(3));

        Assert.Equal([true, false], byHand);
        window.Close();
    });

    // Sizes are kept per screen keyed by the monitor's bounds, so every screen carries them, and the monitor
    // rectangle always contains the work area (the work area is the monitor less the taskbar and docked bars).
    [Fact]
    public void Screens_carry_their_monitor_bounds() => StaThread.Run(() =>
    {
        using var surface = new TileSurface(_ => false);

        var screens = surface.Screens();

        Assert.NotEmpty(screens);
        Assert.All(screens, s =>
        {
            Assert.True(s.Bounds.Width > 0 && s.Bounds.Height > 0, $"screen {s.Number} has empty bounds {s.Bounds}");
            Assert.True(
                s.Bounds.Left <= s.WorkArea.Left && s.Bounds.Top <= s.WorkArea.Top
                && s.Bounds.Right >= s.WorkArea.Right && s.Bounds.Bottom >= s.WorkArea.Bottom,
                $"screen {s.Number}: bounds {s.Bounds} do not contain work area {s.WorkArea}");
        });
    });
}
