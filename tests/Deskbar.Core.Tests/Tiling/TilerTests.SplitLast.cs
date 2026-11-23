using System.Text.RegularExpressions;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Tests.Tiling;

// RECENT WINDOWS OF A "SPLIT LAST" APP (docs/superpowers/specs/2026-11-23-split-recent-app-windows-last-design.md).
// Petre: "i think what i need is that the vscode windows are not affected, or at least not the current one when
// oepning a new browser or other window", "so i don't get interrupted", "maybe the last few vscode windows that were
// active, last 3", and of the two rules offered, the middle way: a new window still splits the tile last used, unless
// that tile is one of the three latest windows of an app marked "split last"; those are split only when nothing else
// is free. These tests drive the whole Tiler through the shared harness (TilerTests.cs, TilerTests.Arrival.cs).
//
// The harness's windows all belong to "app"; a window numbered 100 or more belongs to "Code" (TilerTests.Info), so
// Code(n) is a VS Code window.
public sealed partial class TilerTests
{
    static WindowHandle Code(int n) => new(100 + n);

    // The tile `arriving` split, read from its arrival's trace line ("..., split 65 Right (...)").
    WindowHandle SplitOf(WindowHandle arriving)
    {
        var line = traced.Last(l => l.Contains($": {arriving.Value:X} arrived") && l.Contains(", split "));
        return new WindowHandle((nint)Convert.ToInt64(Regex.Match(line, @", split ([0-9A-F]+) ").Groups[1].Value, 16));
    }

    void SplitLast(WindowHandle window) => Assert.True(manager.SetSplitLast(window, true).IsSuccess);

    // The heart of it: you are in VS Code, you open a browser from it, and VS Code keeps its size.
    [Fact]
    public void A_window_opened_from_a_recent_code_window_splits_another_tile()
    {
        Exists(Code(1), alpha);
        Exists(W(2), alpha);
        Start();
        SplitLast(Code(1));
        Uses(W(2));
        Uses(Code(1));

        Opens(W(3), alpha);

        Assert.Equal(W(2), SplitOf(W(3)));
    }

    // The rest of tiling is unchanged: a window opened from a browser still lands beside the browser.
    [Fact]
    public void A_window_opened_from_a_browser_still_splits_the_browser()
    {
        Exists(Code(1), alpha);
        Exists(W(2), alpha);
        Start();
        SplitLast(Code(1));
        Uses(Code(1));
        Uses(W(2));

        Opens(W(3), alpha);

        Assert.Equal(W(2), SplitOf(W(3)));
    }

    // Without the setting, VS Code is a tile like any other (today's rule).
    [Fact]
    public void An_app_without_the_setting_is_not_protected()
    {
        Exists(Code(1), alpha);
        Exists(W(2), alpha);
        Start();
        Uses(W(2));
        Uses(Code(1));

        Opens(W(3), alpha);

        Assert.Equal(Code(1), SplitOf(W(3)));
    }

    // Four VS Code windows, used oldest first C4, C3, C2, C1: the three latest are protected, the fourth is free.
    [Fact]
    public void The_fourth_code_window_back_is_not_protected()
    {
        new[] { 1, 2, 3, 4 }.ToList().ForEach(n => Exists(Code(n), alpha));
        Start();
        SplitLast(Code(1));
        new[] { 4, 3, 2, 1 }.ToList().ForEach(n => Uses(Code(n)));

        Opens(W(9), alpha);

        Assert.Equal(Code(4), SplitOf(W(9)));
    }

    // Plan review focus 2: only protected tiles anywhere. One of them has to give, and it is the one used longer ago,
    // so the window in use now is split last of all.
    [Fact]
    public void With_only_protected_tiles_the_oldest_of_them_is_split()
    {
        Exists(Code(1), alpha);
        Exists(Code(2), alpha);
        Start();
        SplitLast(Code(1));
        Uses(Code(2));
        Uses(Code(1));

        Opens(W(9), alpha);

        Assert.Equal(Code(2), SplitOf(W(9)));
    }

    // Plan review focus 3: the protected three are counted at each arrival, so a closed one hands its place to the
    // next VS Code window back. C1 closes; C2, C3 and C4 are now the three latest, and the only free tile is W5.
    [Fact]
    public void A_protected_window_closing_protects_the_next_one()
    {
        new[] { 1, 2, 3, 4 }.ToList().ForEach(n => Exists(Code(n), alpha));
        Exists(W(5), alpha);
        Start();
        SplitLast(Code(1));
        Uses(W(5));
        new[] { 4, 3, 2, 1 }.ToList().ForEach(n => Uses(Code(n)));

        Closes(Code(1), Later());
        Opens(W(9), alpha);

        Assert.Equal(W(5), SplitOf(W(9)));
    }

    // Plan review focus 1: two windows arriving in one evaluate (opened while the user was elsewhere). The first is
    // offered the tile last used, a protected one, so it splits the free W2; the second, offered nothing, splits the
    // largest free tile, never C1.
    [Fact]
    public void Two_windows_opening_together_leave_protected_tiles_alone()
    {
        Exists(Code(1), alpha);
        Exists(W(2), alpha);
        Start();
        SplitLast(Code(1));
        Uses(W(2));
        Uses(Code(1));

        SwitchTo(beta);
        Opens(W(3), alpha);
        Opens(W(4), alpha);
        SwitchTo(alpha);

        Assert.NotEqual(Code(1), SplitOf(W(3)));
        Assert.NotEqual(Code(1), SplitOf(W(4)));
    }

    // Screen 1 holds only a protected VS Code window: the window goes to screen 2's free tile rather than halving it.
    [Fact]
    public void Protected_tiles_on_the_target_screen_send_the_window_to_a_free_tile_on_another_screen()
    {
        surface.ScreenList.Add(FakeTileSurface.Screen(2, Area2));
        Exists(Code(1), alpha, screen: 1);
        Exists(W(2), alpha, screen: 2);
        Start();
        SplitLast(Code(1));
        Uses(W(2));
        Uses(Code(1));

        Opens(W(3), alpha, screen: 1);

        Assert.Equal(W(2), SplitOf(W(3)));
        Assert.Equal(2, tiler.Board.ScreenOf(W(3)).Value);
    }

    // A window dropped on a screen keeps that screen ("let's respect which monitor i put it to"), even when every tile
    // there is protected: it splits the older of them.
    [Fact]
    public void A_window_dropped_on_a_screen_of_protected_tiles_stays_there()
    {
        surface.ScreenList.Add(FakeTileSurface.Screen(2, Area2));
        Exists(Code(1), alpha, screen: 1);
        Exists(Code(2), alpha, screen: 1);
        Exists(W(3), alpha, screen: 2);
        Exists(W(4), beta, screen: 1);
        Start();
        SplitLast(Code(1));
        Uses(Code(2));
        Uses(Code(1));

        manager.AssignWindow(W(4), alpha.Id, monitor: 1);

        Assert.Equal(1, tiler.Board.ScreenOf(W(4)).Value);
        Assert.Equal(Code(2), SplitOf(W(4)));
    }

    [Fact]
    public void The_setting_survives_a_restart()
    {
        Exists(Code(1), alpha);
        Start();

        SplitLast(Code(1));

        Assert.Contains("Code", store.Stored.SplitLastApps);
        Assert.True(manager.SplitsLast("code"));
    }
}
