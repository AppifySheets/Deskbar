using System.Windows.Interop;
using Deskbar.Core.Domain;
using Deskbar.Windows.Monitoring;

namespace Deskbar.Windows.Tests;

// The ordering rules are covered purely in MonitorOrderingTests; what CANNOT be covered there is
// whether the P/Invoke underneath actually returns anything. A MONITORINFOEX with a wrong cbSize
// or a missing CharSet.Unicode fails SILENTLY -- GetMonitorInfoEx just returns false, or szDevice
// comes back as garbage -- and the only visible symptom would be badges never appearing, which
// looks identical to the feature being switched off.
//
// This codebase has been bitten by exactly that before (see the probe notes in CLAUDE.md: a
// FindWindowW without CharSet.Unicode silently returned 0 and "proved" whatever was hoped). So
// these run against the real machine and assert the shape of what comes back.
public class ScreenLayoutTests
{
    // Category=Screen: reads the windows and monitors of the desktop being shown. A hidden desktop
    // (scripts/test-hidden.ps1) has neither; CI runs it on the runner's visible desktop.
    [Trait("Category", "Screen")]
    [Fact]
    public void The_layout_resolves_at_least_one_monitor_for_the_windows_it_finds()
    {
        var facts = new ScreenLayout().Snapshot();

        // A machine running this test has a desktop session, so it has windows and at least one
        // display. If the struct marshalling were wrong, MonitorOf would be empty here.
        Assert.NotEmpty(facts.ZOrder);
        Assert.NotEmpty(facts.MonitorOf);
    }

    // Display numbers come out of "\\.\DISPLAYn", so they start at 1. A 0 would mean the parse
    // found no digits and fell through to something meaningless.
    [Fact]
    public void Every_resolved_monitor_number_is_a_real_display_number()
    {
        var facts = new ScreenLayout().Snapshot();

        Assert.All(facts.MonitorOf.Values, number => Assert.True(number >= 1, $"display number {number} should be >= 1"));
    }

    // The main-monitor fallback (WorkspaceManager.FrontmostOnMainMonitor) is dead code without
    // this, and silently so: a dwFlags read that never matched would simply mean the fallback
    // never fires, which looks exactly like "there was nothing to restore".
    //
    // Deliberately NOT asserted to be display 1. On the setup this was built against the primary
    // is DISPLAY2, which is the whole reason "main monitor" is asked of Windows rather than
    // assumed.
    // Category=Screen: reads the windows and monitors of the desktop being shown. A hidden desktop
    // (scripts/test-hidden.ps1) has neither; CI runs it on the runner's visible desktop.
    [Trait("Category", "Screen")]
    [Fact]
    public void Windows_reports_which_display_is_primary()
    {
        var facts = new ScreenLayout().Snapshot();

        Assert.True(facts.PrimaryMonitor.HasValue, "no display reported itself as primary");
        Assert.Contains(facts.PrimaryMonitor.Value, facts.MonitorOf.Values);
    }

    // Z-order is an index into one enumeration, so it must be dense and start at 0 -- if it were
    // not, "smallest index wins" (how the front-most window per monitor is chosen) would be
    // comparing positions from different enumerations.
    [Fact]
    public void Z_order_is_a_dense_front_to_back_ranking()
    {
        var facts = new ScreenLayout().Snapshot();

        Assert.Equal(Enumerable.Range(0, facts.ZOrder.Count), facts.ZOrder.Values.OrderBy(i => i));
    }

    // A window of the test's own, created and never shown (no WS_VISIBLE), far off every real screen: nothing anyone
    // can see, and no real window is touched. WS_POPUP, plus whatever `style` adds.
    const int WsPopup = unchecked((int)0x80000000);
    const int WsMaximize = 0x01000000;

    static HwndSource Hidden(int style = 0) =>
        new(new HwndSourceParameters("Deskbar test window")
        {
            WindowStyle = WsPopup | style,
            PositionX = -30000,
            PositionY = -30000,
            Width = 400,
            Height = 300,
        });

    // A new strip moves the windows under its band, and must leave alone the ones the shell sizes itself: maximized
    // (IsZoomed) AND snapped (IsWindowArranged, for which IsZoomed is false; task 9 review). The snapped half cannot be
    // produced without snapping a real window on a real screen, so it is covered by the declaration's shape (the dead
    // handle below runs the export) and by the live check. A window created maximized must count, a plain one must not.
    [Fact]
    public void A_maximized_window_is_shell_arranged_and_a_plain_one_is_not() => StaThread.Run(() =>
    {
        using var plain = Hidden();
        using var maximized = Hidden(WsMaximize);

        Assert.False(ScreenLayout.IsShellArranged(new WindowHandle(plain.Handle)));
        Assert.True(ScreenLayout.IsShellArranged(new WindowHandle(maximized.Handle)));
    });

    // The IsWindowArranged export is called for real here (it exists from Windows 10 1903; a missing one is caught and
    // read as "not arranged"), and a handle that names no window is simply not arranged.
    [Fact]
    public void A_dead_handle_is_not_arranged_and_asking_does_not_throw() =>
        Assert.False(ScreenLayout.IsShellArranged(new WindowHandle(0)));

    // The strip set's quiet move: the window goes where it is told and keeps its place in the z-order. MoveTo raises the
    // window to the front on purpose (the drag-to-screen gesture); a move nobody asked for must not (task 9 review). Two
    // hidden windows, the second made above the first; after the first is nudged, it is still below the second.
    [Fact]
    public void Nudge_moves_a_window_without_raising_it() => StaThread.Run(() =>
    {
        using var lower = Hidden();
        using var upper = Hidden();
        NativeMethods.SetWindowPos(upper.Handle, NativeMethods.HWND_TOP, 0, 0, 0, 0, NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        Assert.True(Below(lower.Handle, upper.Handle), "setup: the first window should start below the second");

        var to = new WindowRect(-29900, -29950, -29500, -29650);
        var result = new ScreenLayout().Nudge(new WindowHandle(lower.Handle), to);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error : "");
        Assert.Equal(to, new ScreenLayout().RectOf(new WindowHandle(lower.Handle)).GetValueOrDefault());
        Assert.True(Below(lower.Handle, upper.Handle), "the nudged window was raised above the other one");
    });

    // Whether `window` is somewhere below `above` in the z-order: walked down from `above` with GW_HWNDNEXT.
    static bool Below(nint window, nint above)
    {
        const uint gwHwndNext = 2;
        for (var at = NativeMethods.GetWindow(above, gwHwndNext); at != 0; at = NativeMethods.GetWindow(at, gwHwndNext))
            if (at == window) return true;
        return false;
    }
}
