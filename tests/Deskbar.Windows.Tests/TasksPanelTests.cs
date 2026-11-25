using System.Reactive.Subjects;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core.Domain;
using Deskbar.Core.Tasks;
using Deskbar.Windows.Monitoring;

namespace Deskbar.Windows.Tests;

// The tasks panel's app bar and its ORDER (TasksPanel; spec docs/superpowers/specs/2026-11-23-tasks-panel-design.md,
// "Where it docks", "Keeping the bar outside", "Instrumentation").
//
// The shell places a newer app bar registration INSIDE an older one on the same edge, so the bar stays the outer strip
// only while the panel registers after it, every time the bar's registers. These tests pin that order through the fake
// seams: every Undock and DockWith is recorded in the order it was made, and a BarStrip notice is pushed through a
// plain Subject standing in for FloatingBar.Strip.
//
// No test registers a real app bar: DockWith and Undock are fakes, and the screen is a made-up one far off every real
// monitor, so a panel that is shown never appears anywhere (and never reserves a strip of Petre's screens). Its scale is
// faked through ScaleOf, so the pixel arithmetic does not depend on the machine running the suite.
//
// HOW THE DISPATCHER IS DRIVEN. The panel answers a notice on its window's Dispatcher, never inside the bar's notice
// (the bar is mid-dock then), and coalesces a burst to the latest notice. The tests pump that dispatcher themselves
// with Rig.Pump: a Dispatcher.Invoke at Background priority from the test's own STA thread runs every Normal-priority
// item queued before it (the panel's BeginInvoke) and returns, so "before the pump" and "after the pump" are two
// deterministic moments a test can assert on. ScreenStripTests drives OnShellMovedStrips the same way.
public class TasksPanelTests
{
    // 2000 x 600 px, nowhere near a real screen. At the faked 150% that is 1333 DIP wide, so half of it (the panel's
    // ceiling, TasksPanelLayout.Clamp) is 666 DIP and the default 460 DIP fits. Only 600 px high so a window placed on
    // it stays inside the size Windows allows a top-level window on a small CI desktop (ScreenStripTests has the story).
    static readonly WindowRect Screen = new(-30000, -30000, -28000, -29400);
    const double At150 = 1.5;

    // A work area that keeps clear of a 690 px strip on either edge: the shell is holding the panel's strip.
    static readonly WindowRect Held = new(Screen.Left + 1100, Screen.Top, Screen.Right - 1100, Screen.Bottom);

    // A work area the full width of the screen: the shell is NOT holding the panel's strip.
    static readonly WindowRect Lost = Screen;

    static BarStrip Docked(string edge = "right") => new(edge, Screen, Maybe<WindowRect>.None, Screen.Right - 150, Gone: false);

    // The bar went away (minimized or closed): its strip goes and the bar with it.
    static BarStrip BarGone() => new(null, Screen, Maybe<WindowRect>.None, Screen.Right - 150, Gone: true);

    // The bar let go of its strip but is still on screen, floating with its middle at `middleX`.
    static BarStrip Floats(int middleX) => new(null, Screen, Maybe<WindowRect>.None, middleX, Gone: false);

    // One call the panel made on the shell, in the order it made them.
    sealed record Call(string Name, PanelEdge? Edge = null, WindowRect? On = null, int Px = 0)
    {
        public static readonly Call Undock = new("Undock");
        public static Call Dock(PanelEdge edge, int px) => new("DockWith", edge, Screen, px);
    }

    // The panel on a plain borderless window, with every seam faked. The bar is a Subject unless a test hands its own
    // stream (the replay test). Grants what was asked, on the asked edge, the full height of the screen.
    sealed class Rig
    {
        public readonly Subject<BarStrip> Bar = new();
        public readonly List<Call> Calls = [];
        public readonly List<string> Lines = [];
        public DateTime Now = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
        public WindowRect Work = Held;
        public bool Tracing = true;
        // The tray overflow the bar believes is in front (FloatingBar.OverflowInFront), or 0.
        public nint Overflow;
        // The window covering the panel's screen full screen (TopLevelWindows.CoveringWindow), or 0.
        public nint Cover;
        // The bar's own window (FloatingBar.Hwnd), or 0 for no bar.
        public nint BarHwnd;
        // The restored windows the panel is told about (TasksPanel.RestoredWindows), and every Nudge it made, in order.
        // A nudge that works moves the window in this list too, as the real one would.
        public List<(WindowHandle Window, WindowRect Rect)> Restored = [];
        public readonly List<(WindowHandle Window, WindowRect To)> Nudged = [];
        public readonly Window Window = new()
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
        };
        public readonly TasksPanel Panel;

        public Rig(IObservable<BarStrip>? bar = null) =>
            Panel = new TasksPanel(Window, bar ?? Bar)
            {
                DockWith = (edge, screen, px) =>
                {
                    Calls.Add(new Call("DockWith", edge, screen, px));
                    return edge == PanelEdge.Left
                        ? new WindowRect(screen.Left, screen.Top, screen.Left + px, screen.Bottom)
                        : new WindowRect(screen.Right - px, screen.Top, screen.Right, screen.Bottom);
                },
                Undock = () => Calls.Add(Call.Undock),
                ScaleOf = _ => At150,
                WorkAreaOf = _ => Work,
                Clock = () => Now,
                Tracing = () => Tracing,
                Trace = Lines.Add,
                OverflowInFront = () => Overflow,
                CoveringWindow = _ => Cover,
                BarWindow = () => BarHwnd,
                RestoredWindows = () => Restored,
                Nudge = (window, to) =>
                {
                    Nudged.Add((window, to));
                    Restored = Restored.Select(w => w.Window == window ? (w.Window, to) : w).ToList();
                    return Result.Success();
                },
            };

        // Runs whatever the panel queued on its dispatcher (see the top of this file).
        public void Pump() => Window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

        // The bar docked and announced it, and the panel was opened: the usual starting point, with the record cleared.
        public Rig Open(double width = TasksPanelLayout.DefaultWidth)
        {
            Bar.OnNext(Docked());
            Pump();
            Panel.Show(width);
            Calls.Clear();
            Lines.Clear();
            return this;
        }

        public void Close()
        {
            Panel.Shutdown();
            Window.Close();
        }
    }

    static NativeMethods.RECT RectOf(Window window)
    {
        Assert.True(NativeMethods.GetWindowRect(new WindowInteropHelper(window).Handle, out var rect));
        return rect;
    }

    static bool IsTopmost(Window window) =>
        (NativeMethods.GetWindowLongPtr(new WindowInteropHelper(window).Handle, NativeMethods.GWL_EXSTYLE) & (nint)NativeMethods.WS_EX_TOPMOST) != 0;

    // The shell speaks physical pixels: 460 DIP on a 150% screen is 690 px, on the bar's edge of the bar's screen. A
    // fresh registration (Undock, then DockWith), so the panel is newer than the bar and sits inside it. The window is
    // then exactly in what was granted, shown and topmost.
    [Fact]
    public void Showing_docks_on_the_bar_edge_with_the_width_in_physical_pixels() => StaThread.Run(() =>
    {
        var rig = new Rig();
        rig.Bar.OnNext(Docked("right"));
        rig.Pump();
        Assert.Empty(rig.Calls);

        rig.Panel.Show(TasksPanelLayout.DefaultWidth);

        Assert.Equal([Call.Undock, Call.Dock(PanelEdge.Right, 690)], rig.Calls);
        Assert.True(rig.Panel.IsShown);
        Assert.True(rig.Window.IsVisible);
        Assert.True(IsTopmost(rig.Window));
        var rect = RectOf(rig.Window);
        Assert.Equal((Screen.Right - 690, Screen.Top, Screen.Right, Screen.Bottom), (rect.Left, rect.Top, rect.Right, rect.Bottom));
        rig.Close();
    });

    // TAKING THE STRIP BACK FROM THE WINDOWS UNDER IT (task cafaae24). A restored window reaching under the panel's strip
    // is moved off it when the panel comes on screen: 690 px strip on the right, a 600 px window reaching 290 px under it
    // moves 290 px left, size kept (TasksPanelClear has the rule). A window clear of the strip is not touched.
    static readonly WindowHandle Under = new(0x1001);
    static readonly WindowHandle Clear = new(0x1002);
    static readonly WindowRect UnderRect = new(Screen.Right - 690 - 310, Screen.Top + 100, Screen.Right - 400, Screen.Top + 400);
    static readonly WindowRect ClearRect = new(Screen.Left + 100, Screen.Top + 100, Screen.Left + 700, Screen.Top + 400);
    static readonly WindowRect MovedRect = UnderRect with { Left = UnderRect.Left - 290, Right = Screen.Right - 690 };

    [Fact]
    public void Showing_moves_a_restored_window_off_the_strip_and_leaves_the_rest() => StaThread.Run(() =>
    {
        var rig = new Rig { Restored = [(Under, UnderRect), (Clear, ClearRect)] };
        rig.Bar.OnNext(Docked("right"));
        rig.Pump();

        rig.Panel.Show(TasksPanelLayout.DefaultWidth);

        Assert.Equal([(Under, MovedRect)], rig.Nudged);
        Assert.Contains(rig.Lines, line => line.Contains("moved 1001") && line.Contains("off the strip"));
        rig.Close();
    });

    // Once per strip taken: the bar registering again, the shell's re-ask and a narrower width all re-dock onto a strip
    // no bigger than the one cleared, and a window under the panel by then was put there by hand, so nothing moves.
    [Fact]
    public void A_re_dock_onto_the_same_or_a_smaller_strip_moves_nothing() => StaThread.Run(() =>
    {
        var rig = new Rig().Open();
        rig.Restored = [(Under, UnderRect)];

        rig.Bar.OnNext(Docked("right"));
        rig.Pump();
        rig.Panel.OnShellMoved();
        rig.Pump();
        rig.Panel.SetWidth(400);

        Assert.Empty(rig.Nudged);
        rig.Close();
    });

    // A wider panel takes ground it did not hold, so the windows under the new strip move again.
    [Fact]
    public void A_wider_panel_clears_its_new_strip() => StaThread.Run(() =>
    {
        var rig = new Rig().Open();
        var reach = UnderRect with { Right = Screen.Right - 690 };
        rig.Restored = [(Under, reach)];

        rig.Panel.SetWidth(500);

        Assert.Equal([(Under, reach with { Left = reach.Left - 60, Right = Screen.Right - 750 })], rig.Nudged);
        rig.Close();
    });

    // Closed and opened again, the panel takes its strip afresh, so it clears it again.
    [Fact]
    public void Opening_again_after_a_close_clears_again() => StaThread.Run(() =>
    {
        var rig = new Rig().Open();
        rig.Panel.Hide();
        rig.Restored = [(Under, UnderRect)];

        rig.Panel.Show(TasksPanelLayout.DefaultWidth);

        Assert.Equal([(Under, MovedRect)], rig.Nudged);
        rig.Close();
    });

    // Every time the bar's app bar registers again it announces it, and the panel gives its own registration up and
    // makes a new one, so it is newer than the bar's again. Never inside the bar's notice: the bar is mid-dock then.
    [Fact]
    public void When_the_bar_registers_again_the_panel_undocks_and_docks_after_it() => StaThread.Run(() =>
    {
        var rig = new Rig().Open();

        rig.Bar.OnNext(Docked("right"));
        Assert.Empty(rig.Calls);

        rig.Pump();
        Assert.Equal([Call.Undock, Call.Dock(PanelEdge.Right, 690)], rig.Calls);
        rig.Close();
    });

    // Review focus 1. Explorer restarting forgets every app bar and broadcasts TaskbarCreated to both windows. The
    // panel must not register on it by itself, or it could register BEFORE the bar and end up outside it: it waits for
    // the bar's re-registration, and docks after that. Neither the shell's re-ask nor the guard docks meanwhile.
    [Fact]
    public void After_an_Explorer_restart_the_panel_docks_only_after_the_bar() => StaThread.Run(() =>
    {
        var rig = new Rig().Open();

        rig.Panel.OnExplorerRestarted();
        rig.Panel.OnShellMoved();
        rig.Pump();
        rig.Work = Lost;
        rig.Now += TimeSpan.FromSeconds(9);
        rig.Panel.Keep();
        // A width change meanwhile is kept, not docked: the in-place re-ask would register before the bar.
        rig.Panel.SetWidth(500);
        rig.Panel.Show(500);
        Assert.Empty(rig.Calls);

        rig.Bar.OnNext(Docked("right"));
        rig.Pump();
        Assert.Equal([Call.Undock, Call.Dock(PanelEdge.Right, 750)], rig.Calls);
        rig.Close();
    });

    // The wait for the bar after an Explorer restart is not for ever. The bar announces its re-registration within
    // moments; if that notice was missed (it reached the panel before the panel heard TaskbarCreated itself), the guard
    // takes over once RetryEvery has passed, and only if the work area really reaches under the panel.
    [Fact]
    public void After_an_Explorer_restart_with_no_word_from_the_bar_the_guard_takes_over_after_the_retry_time() => StaThread.Run(() =>
    {
        var rig = new Rig().Open();
        rig.Now += TimeSpan.FromMinutes(1);

        rig.Panel.OnExplorerRestarted();
        rig.Work = Lost;
        rig.Now += TimeSpan.FromSeconds(9.9);
        rig.Panel.Keep();
        Assert.Empty(rig.Calls);

        rig.Now += TimeSpan.FromSeconds(0.2);
        rig.Panel.Keep();
        Assert.Equal([Call.Undock, Call.Dock(PanelEdge.Right, 690)], rig.Calls);
        rig.Close();
    });

    // Review focus 5. The bar went away (minimized, closed; BarStrip.Gone): the panel gives its strip back and hides,
    // still open as far as the user is concerned, and comes back on the bar's next docked notice.
    [Fact]
    public void A_bar_that_goes_away_hides_the_panel_and_its_next_dock_brings_it_back() => StaThread.Run(() =>
    {
        var rig = new Rig().Open();

        rig.Bar.OnNext(BarGone());
        rig.Pump();
        Assert.Equal([Call.Undock], rig.Calls);
        Assert.False(rig.Window.IsVisible);
        Assert.True(rig.Panel.IsShown);

        rig.Bar.OnNext(Docked("left"));
        rig.Pump();
        Assert.Equal([Call.Undock, Call.Undock, Call.Dock(PanelEdge.Left, 690)], rig.Calls);
        Assert.True(rig.Window.IsVisible);
        rig.Close();
    });

    // The bar undocked and floats (BarStrip.Gone false): the panel stays on screen and docks by the floating rule, on
    // the edge of the bar's screen nearer the bar's middle, as a fresh registration (the only app bar on that edge).
    // Here the bar floats near the LEFT end of the screen, so the panel leaves the right edge it shared with the bar.
    // And a bar floating near the right end gives the right edge.
    [Fact]
    public void A_floating_bar_notice_docks_the_panel_on_the_edge_nearer_the_bar() => StaThread.Run(() =>
    {
        var rig = new Rig().Open();

        rig.Bar.OnNext(Floats(Screen.Left + 300));
        rig.Pump();
        Assert.Equal([Call.Undock, Call.Dock(PanelEdge.Left, 690)], rig.Calls);
        Assert.True(rig.Window.IsVisible);
        Assert.True(rig.Panel.IsShown);

        rig.Calls.Clear();
        rig.Bar.OnNext(Floats(Screen.Right - 300));
        rig.Pump();
        Assert.Equal([Call.Undock, Call.Dock(PanelEdge.Right, 690)], rig.Calls);
        Assert.Contains(rig.Lines, line => line.Contains("(bar floats)") && !line.Contains("given back"));
        rig.Close();
    });

    // A bar that floats from the start (DockBar off, the default) announces that on its first show and never docks: a
    // panel asked for before that notice opens on it, by the floating rule, and no docked notice is ever needed.
    [Fact]
    public void A_floating_notice_as_the_very_first_notice_opens_a_requested_panel() => StaThread.Run(() =>
    {
        var rig = new Rig();
        rig.Panel.Show(TasksPanelLayout.DefaultWidth);
        Assert.Empty(rig.Calls);

        rig.Bar.OnNext(Floats(Screen.Right - 300));
        rig.Pump();

        Assert.Equal([Call.Undock, Call.Dock(PanelEdge.Right, 690)], rig.Calls);
        Assert.True(rig.Window.IsVisible);
        rig.Close();
    });

    // A floating bar minimized: the panel docked by the floating rule goes with it, as it does with a docked bar, and
    // stays open as far as the user is concerned.
    [Fact]
    public void A_floating_bar_that_goes_away_hides_the_panel() => StaThread.Run(() =>
    {
        var rig = new Rig();
        rig.Bar.OnNext(Floats(Screen.Left + 300));
        rig.Pump();
        rig.Panel.Show(TasksPanelLayout.DefaultWidth);
        rig.Calls.Clear();

        rig.Bar.OnNext(BarGone());
        rig.Pump();

        Assert.Equal([Call.Undock], rig.Calls);
        Assert.False(rig.Window.IsVisible);
        Assert.True(rig.Panel.IsShown);
        rig.Close();
    });

    // A bar restored from minimize while floating announces it floats: the panel that went with it comes back, by the
    // floating rule.
    [Fact]
    public void A_bar_that_went_away_and_comes_back_floating_brings_the_panel_back() => StaThread.Run(() =>
    {
        var rig = new Rig().Open();
        rig.Bar.OnNext(BarGone());
        rig.Pump();
        Assert.False(rig.Window.IsVisible);

        rig.Bar.OnNext(Floats(Screen.Left + 300));
        rig.Pump();

        Assert.Equal([Call.Undock, Call.Undock, Call.Dock(PanelEdge.Left, 690)], rig.Calls);
        Assert.True(rig.Window.IsVisible);
        rig.Close();
    });

    // The user closing the panel while the bar's strip is gone (the bar minimized) is the user's word: the next notice
    // with an edge does not bring the panel back.
    [Fact]
    public void Hiding_while_the_bar_strip_is_gone_is_not_undone_by_the_next_notice() => StaThread.Run(() =>
    {
        var rig = new Rig().Open();
        rig.Bar.OnNext(BarGone());
        rig.Pump();

        rig.Panel.Hide();
        rig.Bar.OnNext(Docked("right"));
        rig.Pump();

        Assert.DoesNotContain(rig.Calls, call => call.Name == "DockWith");
        Assert.False(rig.Window.IsVisible);
        Assert.False(rig.Panel.IsShown);
        rig.Close();
    });

    // Closing the panel gives the strip back (ABM_REMOVE) and hides the window, and says so through Shown, which the
    // bar's "on" look and the saved state follow. The bar re-registering later does not open it again.
    [Fact]
    public void Hiding_gives_the_strip_back() => StaThread.Run(() =>
    {
        var rig = new Rig();
        var shown = new List<bool>();
        using var _ = rig.Panel.Shown.Subscribe(shown.Add);
        rig.Open();

        rig.Panel.Hide();

        Assert.Equal([Call.Undock], rig.Calls);
        Assert.False(rig.Window.IsVisible);
        Assert.False(rig.Panel.IsShown);
        Assert.Equal([false, true, false], shown);

        rig.Bar.OnNext(Docked("right"));
        rig.Pump();
        Assert.Equal([Call.Undock], rig.Calls);
        rig.Close();
    });

    // Review focus 2. Shutting down while shown leaves nothing registered; a second Shutdown, or one after Hide, is
    // harmless, and a notice still on its way does nothing afterwards. It does NOT count as the user closing the
    // panel: IsShown stays true and Shown says nothing, so the saved state still reads "open" and the next start opens
    // it again (spec "Opening and closing").
    [Fact]
    public void Shutdown_gives_the_strip_back_while_shown() => StaThread.Run(() =>
    {
        var rig = new Rig().Open();
        var shown = new List<bool>();
        using var _ = rig.Panel.Shown.Subscribe(shown.Add);

        rig.Bar.OnNext(Docked("right"));
        rig.Panel.Shutdown();
        rig.Pump();
        Assert.Equal([Call.Undock], rig.Calls);
        Assert.False(rig.Window.IsVisible);
        Assert.True(rig.Panel.IsShown);
        Assert.Equal([true], shown);

        rig.Panel.Shutdown();
        rig.Panel.Dispose();
        Assert.Equal([Call.Undock], rig.Calls);

        var hidden = new Rig().Open();
        hidden.Panel.Hide();
        hidden.Panel.Shutdown();
        Assert.Equal(Call.Undock, hidden.Calls.Last());
        Assert.DoesNotContain(hidden.Calls, call => call.Name == "DockWith");
        hidden.Close();
        rig.Window.Close();
    });

    // The 1 s guard, on the docked bar's two clocks: nothing within SettleFor of a dock, then a fresh registration when
    // the screen's work area reaches under the granted strip, and then not again within RetryEvery. Traced.
    [Fact]
    public void The_guard_waits_out_the_settle_time_and_then_docks_again_when_the_work_area_runs_under_the_panel() => StaThread.Run(() =>
    {
        var rig = new Rig().Open();
        rig.Work = Lost;

        rig.Now += TimeSpan.FromSeconds(2.9);
        rig.Panel.Keep();
        Assert.Empty(rig.Calls);

        rig.Now += TimeSpan.FromSeconds(0.2);
        rig.Panel.Keep();
        Assert.Equal([Call.Undock, Call.Dock(PanelEdge.Right, 690)], rig.Calls);
        Assert.Contains(rig.Lines, line => line.Contains("guard"));

        // Still not held: but within RetryEvery of the repair, so nothing yet.
        rig.Now += TimeSpan.FromSeconds(9);
        rig.Panel.Keep();
        Assert.Equal(2, rig.Calls.Count);

        rig.Now += TimeSpan.FromSeconds(1.1);
        rig.Panel.Keep();
        Assert.Equal(4, rig.Calls.Count);

        // Held: nothing, however long it has been.
        rig.Work = Held;
        rig.Now += TimeSpan.FromMinutes(5);
        rig.Panel.Keep();
        Assert.Equal(4, rig.Calls.Count);
        rig.Close();
    });

    // The stored width is the user's, unclamped; the dock clamps it to the panel's minimum and to half the screen.
    // A width change re-docks once, in place: the registration stays, so the order with the bar is unchanged.
    [Fact]
    public void A_width_below_the_minimum_docks_at_the_minimum() => StaThread.Run(() =>
    {
        var rig = new Rig();
        rig.Bar.OnNext(Docked());
        rig.Pump();

        rig.Panel.Show(200);
        Assert.Equal([Call.Undock, Call.Dock(PanelEdge.Right, 540)], rig.Calls);

        rig.Calls.Clear();
        rig.Panel.SetWidth(500);
        Assert.Equal([Call.Dock(PanelEdge.Right, 750)], rig.Calls);

        // Wider than half the 1333 DIP screen: half of it, 666.7 DIP, 1000 px.
        rig.Calls.Clear();
        rig.Panel.SetWidth(5000);
        Assert.Equal([Call.Dock(PanelEdge.Right, 1000)], rig.Calls);
        rig.Close();
    });

    // Opened before the bar has docked (the app starting with the panel left open): nothing is docked until the bar's
    // first notice with an edge, and then the panel docks after it.
    [Fact]
    public void Showing_before_the_bar_has_docked_waits_for_the_bar() => StaThread.Run(() =>
    {
        var rig = new Rig();

        rig.Panel.Show(TasksPanelLayout.DefaultWidth);
        Assert.Empty(rig.Calls);
        Assert.False(rig.Window.IsVisible);
        Assert.True(rig.Panel.IsShown);

        rig.Bar.OnNext(Docked("right"));
        rig.Pump();
        Assert.Equal([Call.Undock, Call.Dock(PanelEdge.Right, 690)], rig.Calls);
        Assert.True(rig.Window.IsVisible);
        rig.Close();
    });

    // FloatingBar.Strip replays its last notice to a late subscriber, which reaches the panel INSIDE its constructor,
    // before the object initializer has set the seams. Answered on the dispatcher, it goes through the fakes, never the
    // production DockWith (which would register a real app bar).
    [Fact]
    public void A_replayed_notice_is_answered_through_the_seams_set_after_construction() => StaThread.Run(() =>
    {
        var replay = new ReplaySubject<BarStrip>(1);
        replay.OnNext(Docked("left"));
        var rig = new Rig(replay);

        rig.Panel.Show(TasksPanelLayout.DefaultWidth);
        rig.Pump();

        Assert.Equal([Call.Undock, Call.Dock(PanelEdge.Left, 690)], rig.Calls);
        rig.Close();
    });

    // A burst of notices before the dispatcher runs (the bar let go and docked again on a display change) is answered
    // once, with the latest.
    [Fact]
    public void A_burst_of_bar_notices_is_answered_once_with_the_latest() => StaThread.Run(() =>
    {
        var rig = new Rig().Open();

        rig.Bar.OnNext(Docked("right"));
        rig.Bar.OnNext(BarGone());
        rig.Bar.OnNext(Docked("left"));
        Assert.Empty(rig.Calls);

        rig.Pump();
        Assert.Equal([Call.Undock, Call.Dock(PanelEdge.Left, 690)], rig.Calls);
        rig.Close();
    });

    // The shell's plain position notice (ABN_POSCHANGED) re-asks without giving the registration up, as the bar does:
    // a remove-and-register here would answer the bar's own re-ask for ever. Queued; nothing while closed.
    [Fact]
    public void The_shells_position_notice_re_asks_without_giving_the_strip_up() => StaThread.Run(() =>
    {
        var rig = new Rig().Open();

        rig.Panel.OnShellMoved();
        Assert.Empty(rig.Calls);
        rig.Pump();
        Assert.Equal([Call.Dock(PanelEdge.Right, 690)], rig.Calls);

        rig.Panel.Hide();
        rig.Calls.Clear();
        rig.Panel.OnShellMoved();
        rig.Pump();
        Assert.Empty(rig.Calls);
        rig.Close();
    });

    // A full-screen window on the panel's screen takes it out of the topmost band, as the bar steps aside; the 1 s
    // re-assert leaves it there, and the window closing puts it back.
    [Fact]
    public void A_full_screen_window_takes_the_panel_out_of_topmost_and_its_closing_puts_it_back() => StaThread.Run(() =>
    {
        var rig = new Rig().Open();
        Assert.True(IsTopmost(rig.Window));

        rig.Panel.StepAside(fullScreen: true);
        Assert.False(IsTopmost(rig.Window));
        rig.Panel.ReclaimTopmost();
        Assert.False(IsTopmost(rig.Window));

        rig.Panel.StepAside(fullScreen: false);
        Assert.True(IsTopmost(rig.Window));
        rig.Close();
    });

    // A full-screen window still up when the panel comes back (final review, Minor 2). The shell says ABN_FULLSCREENAPP
    // only when the full-screen state changes, so a panel registering while a video is already full screen (the bar
    // minimized and restored under it, or the panel opened then) hears nothing. It looks for itself as it shows, stays
    // behind the covering window, and is back on top on the first tick after that window has gone. The cover is a real
    // ordinary window, so "behind" is checked in the z-order.
    [Fact]
    public void A_panel_that_comes_back_while_a_full_screen_window_is_up_stays_behind_it() => StaThread.Run(() =>
    {
        var rig = new Rig().Open();
        var panel = new WindowInteropHelper(rig.Window).Handle;
        var video = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
        };
        video.Show();
        var cover = new WindowInteropHelper(video).Handle;
        rig.Panel.StepAside(fullScreen: true);
        rig.Cover = cover;

        // The bar minimized and restored while the video plays.
        rig.Bar.OnNext(BarGone());
        rig.Pump();
        Assert.False(rig.Window.IsVisible);
        rig.Bar.OnNext(Docked());
        rig.Pump();
        Assert.True(rig.Window.IsVisible);
        Assert.False(IsTopmost(rig.Window));
        Assert.True(Above(cover, panel));
        rig.Panel.ReclaimTopmost();
        Assert.False(IsTopmost(rig.Window));
        Assert.Contains(rig.Lines, line => line.Contains("shown behind the full-screen window"));

        // The video ends: back on top on the next tick.
        rig.Cover = 0;
        rig.Panel.ReclaimTopmost();
        Assert.True(IsTopmost(rig.Window));
        Assert.Contains(rig.Lines, line => line.Contains("has gone, back on top"));

        // Opened while a full-screen window is already up: the same.
        rig.Panel.Hide();
        rig.Cover = cover;
        rig.Panel.Show(TasksPanelLayout.DefaultWidth);
        Assert.False(IsTopmost(rig.Window));
        Assert.True(Above(cover, panel));

        video.Close();
        rig.Close();
    });

    // Task b488e2e9: an ORDINARY full-screen window (Remote Desktop, a browser in F11) on the panel's screen. With the
    // shell's notice the panel used to leave the topmost band and land at the top of the ordinary one, over that window;
    // without a notice it stayed topmost over it. Either way it now stands directly behind it, and comes back once the
    // window has gone. The cover is a real ordinary window, so "behind" is checked in the z-order.
    [Fact]
    public void An_ordinary_full_screen_window_has_the_panel_behind_it_with_the_shells_notice_or_without() => StaThread.Run(() =>
    {
        var rig = new Rig().Open();
        var panel = new WindowInteropHelper(rig.Window).Handle;
        var remote = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false };
        remote.Show();
        var cover = new WindowInteropHelper(remote).Handle;
        rig.Cover = cover;

        // No notice from the shell: the 1 s tick finds the window itself.
        rig.Panel.ReclaimTopmost();
        Assert.False(IsTopmost(rig.Window));
        Assert.True(Above(cover, panel));

        // The window goes, and the panel is back on top.
        rig.Cover = 0;
        rig.Panel.ReclaimTopmost();
        Assert.True(IsTopmost(rig.Window));

        // The shell's notice with the window up: behind it at once, not over it.
        rig.Cover = cover;
        rig.Panel.StepAside(fullScreen: true);
        Assert.False(IsTopmost(rig.Window));
        Assert.True(Above(cover, panel));

        remote.Close();
        rig.Close();
    });

    // Task 4836eca2, "Undocked bar sits behind its own tasks' window": a floating bar parked on the edge the panel docks on
    // overlaps it, and the 1 s timer re-asserts the bar first and the panel after it, so the panel landed over the bar on
    // every tick. The panel's place is directly BEHIND the bar instead, still in the topmost band. A bar that is hidden
    // (minimized) is no place to stand behind, and the panel takes the top of the band as before. The "bar" is a real
    // topmost window over the panel's inner edge on the made-up screen, shown without the focus.
    [Fact]
    public void The_panel_stays_behind_a_floating_bar_over_it_and_takes_the_top_while_the_bar_is_hidden() => StaThread.Run(() =>
    {
        var rig = new Rig().Open();
        var panel = new WindowInteropHelper(rig.Window).Handle;
        var at = RectOf(rig.Window);
        var barWindow = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
        };
        barWindow.Show();
        var bar = new WindowInteropHelper(barWindow).Handle;
        NativeMethods.SetWindowPos(bar, NativeMethods.HWND_TOPMOST, at.Left - 200, at.Top + 100, 400, 300, NativeMethods.SWP_NOACTIVATE);
        rig.BarHwnd = bar;

        // The timer's order: the bar re-asserts, then the panel. The bar stays on top, the panel right behind it.
        rig.Panel.ReclaimTopmost();
        Assert.Equal(panel, NativeMethods.GetWindow(bar, GwHwndNext));
        Assert.True(IsTopmost(rig.Window));

        // The panel clicked (it climbs to the top of the band), the bar's re-assert, then the panel's: behind the bar again.
        NativeMethods.SetWindowPos(panel, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        Assert.True(Above(panel, bar));
        NativeMethods.SetWindowPos(bar, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        rig.Panel.ReclaimTopmost();
        Assert.Equal(panel, NativeMethods.GetWindow(bar, GwHwndNext));
        Assert.True(IsTopmost(rig.Window));

        // The bar hidden: the panel takes the top of the band again.
        barWindow.Hide();
        rig.Panel.ReclaimTopmost();
        Assert.True(IsTopmost(rig.Window));

        barWindow.Close();
        rig.Close();
    });

    // A bar out of the topmost band (stepped below a full-screen window on its own screen) is no place for the panel
    // either: inserting behind a window outside the band would take the panel out of it too.
    [Fact]
    public void A_bar_outside_the_topmost_band_does_not_take_the_panel_out_of_it() => StaThread.Run(() =>
    {
        var rig = new Rig().Open();
        var barWindow = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
        };
        barWindow.Show();
        rig.BarHwnd = new WindowInteropHelper(barWindow).Handle;

        rig.Panel.ReclaimTopmost();
        Assert.True(IsTopmost(rig.Window));

        barWindow.Close();
        rig.Close();
    });

    // The tray's hidden-icons flyout over the panel (final review, Important 1): the 1 s re-assert puts the panel directly
    // BEHIND it instead of climbing over it every second, the bug the bar had and fixed (FloatingBar.StepBelowOverflow),
    // and the panel is back on top on the first tick after the flyout has left the front. A flyout somewhere else on the
    // screen is none of the panel's business: it keeps the top. The flyout is a real topmost window, placed in physical
    // pixels over the panel's inner edge on the made-up screen, shown without the focus.
    [Fact]
    public void The_panel_steps_behind_the_tray_overflow_over_it_and_is_back_on_top_once_it_has_gone() => StaThread.Run(() =>
    {
        var rig = new Rig().Open();
        var panel = new WindowInteropHelper(rig.Window).Handle;
        var at = RectOf(rig.Window);
        var flyout = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
        };
        flyout.Show();
        var over = new WindowInteropHelper(flyout).Handle;
        NativeMethods.SetWindowPos(over, NativeMethods.HWND_TOPMOST, at.Left - 200, at.Bottom - 300, 400, 300, NativeMethods.SWP_NOACTIVATE);
        Assert.True(Above(over, panel));

        rig.Overflow = over;
        rig.Panel.ReclaimTopmost();
        Assert.Equal(panel, NativeMethods.GetWindow(over, GwHwndNext));
        Assert.True(IsTopmost(rig.Window));
        rig.Panel.ReclaimTopmost();
        Assert.True(Above(over, panel));
        Assert.Single(rig.Lines, line => line.Contains("steps below the tray overflow"));

        rig.Overflow = 0;
        rig.Panel.ReclaimTopmost();
        Assert.True(Above(panel, over));
        Assert.Single(rig.Lines, line => line.Contains("back on top of the tray overflow"));

        // In front, but nowhere near the panel: the panel keeps the top.
        NativeMethods.SetWindowPos(over, NativeMethods.HWND_TOPMOST, at.Left - 900, at.Top, 400, 300, NativeMethods.SWP_NOACTIVATE);
        Assert.True(Above(over, panel));
        rig.Overflow = over;
        rig.Panel.ReclaimTopmost();
        Assert.True(Above(panel, over));

        flyout.Close();
        rig.Close();
    });

    // GetWindow's "the window below this one in the z-order".
    const uint GwHwndNext = 2;

    // Whether `upper` is above `lower` in the z-order: walking down from `upper` reaches `lower`.
    static bool Above(nint upper, nint lower) =>
        NativeMethods.GetWindow(upper, GwHwndNext) is var next && next != 0 && (next == lower || Above(next, lower));

    // Petre's screenshot of 2026-11-24: a line's menu half under the panel. A ContextMenu's popup has no owner window, so
    // the 1 s re-assertion put the panel over it (OwnMenus has the measurement). While a menu is open the panel stays where
    // it is, and once it has closed the panel climbs back on top.
    [Fact]
    public void An_open_menu_stays_above_the_panel_and_the_panel_climbs_back_once_it_closes() => StaThread.Run(() =>
    {
        OwnMenus.Start();
        var rig = new Rig().Open();
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = rig.Window };
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "Edit" });
        menu.IsOpen = true;
        rig.Pump();
        var menuHwnd = ((HwndSource)PresentationSource.FromVisual(menu)!).Handle;
        var panelHwnd = new WindowInteropHelper(rig.Window).Handle;
        Assert.True(OwnMenus.AnyOpen);

        rig.Panel.ReclaimTopmost();
        Assert.True(Above(menuHwnd, panelHwnd));

        menu.IsOpen = false;
        rig.Pump();
        Assert.False(OwnMenus.AnyOpen);
        rig.Panel.ReclaimTopmost();
        Assert.True(IsTopmost(rig.Window));
        rig.Close();
    });

    // The trace (CLAUDE.md, "Instrumentation"): one line per dock with its reason, and the geometry line (the granted
    // rectangle and the bar's edge) only when it changes. Nothing at all with tracing off.
    [Fact]
    public void Each_dock_is_traced_with_its_reason_and_the_geometry_only_when_it_changes() => StaThread.Run(() =>
    {
        var rig = new Rig();
        rig.Bar.OnNext(Docked());
        rig.Pump();
        rig.Panel.Show(TasksPanelLayout.DefaultWidth);
        Assert.Contains(rig.Lines, line => line.Contains("(shown)"));
        Assert.Single(rig.Lines, line => line.Contains("granted") && line.Contains("bar right"));

        rig.Panel.OnShellMoved();
        rig.Pump();
        rig.Bar.OnNext(Docked());
        rig.Pump();
        Assert.Contains(rig.Lines, line => line.Contains("(position changed)"));
        Assert.Contains(rig.Lines, line => line.Contains("(bar registered)"));
        Assert.Single(rig.Lines, line => line.Contains("granted"));

        rig.Panel.SetWidth(500);
        Assert.Contains(rig.Lines, line => line.Contains("(width)"));
        Assert.Equal(2, rig.Lines.Count(line => line.Contains("granted")));

        rig.Panel.Hide();
        Assert.Contains(rig.Lines, line => line.Contains("given back"));
        rig.Close();

        var quiet = new Rig { Tracing = false };
        quiet.Bar.OnNext(Docked());
        quiet.Pump();
        quiet.Panel.Show(TasksPanelLayout.DefaultWidth);
        quiet.Panel.OnShellMoved();
        quiet.Pump();
        quiet.Panel.Hide();
        Assert.Empty(quiet.Lines);
        quiet.Close();
    });
}
