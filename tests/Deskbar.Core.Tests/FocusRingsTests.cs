using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;
using Deskbar.Core.Overview;

namespace Deskbar.Core.Tests;

// Which windows get an outline on the screen, and in which colour (FocusRings). Petre: "one that has focus, yes; and
// another on another screen, with a dimmer white", then "go with proposed": the focused window in its workspace's bar
// colour, the window in front on another screen in white at 45%, white where there is no workspace colour.
public class FocusRingsTests
{
    const string Indigo = "#5B6CFF";

    static WindowRow Row(int hwnd, int monitor, bool front, bool active = false, bool minimized = false) =>
        new(new WindowInfo(new WindowHandle(hwnd), hwnd, $"app{hwnd}", $@"C:\app{hwnd}.exe", $"Window {hwnd}", null),
            Maybe<string>.None,
            IsActive: active,
            Monitor: monitor,
            IsMinimized: minimized,
            IsFrontmostOnMonitor: front);

    static WorkspaceGroup Workspace(bool current, params WindowRow[] rows) =>
        new(new Workspace(Guid.NewGuid(), "W", Guid.NewGuid()), current, rows, []);

    static IReadOnlyList<(int Window, FocusRings.Kind Kind, string Colour)> Rings(Overview.Overview overview, string? colour = Indigo) =>
        FocusRings.Of(overview, _ => colour).Select(r => ((int)r.Window.Value, r.Kind, r.Colour)).ToList();

    // The mockup's two screens: VS Code focused on screen 1 in the workspace's colour, the window in front on screen 2
    // in dim white, and the windows behind either of them left alone.
    [Fact]
    public void The_focused_window_takes_the_workspace_colour_and_the_front_window_of_the_other_screen_dim_white()
    {
        var overview = new Overview.Overview([], [Workspace(true,
            Row(1, monitor: 1, front: true, active: true),
            Row(2, monitor: 1, front: false),
            Row(3, monitor: 2, front: true),
            Row(4, monitor: 2, front: false))], []);

        Assert.Equal([(1, FocusRings.Kind.Focused, Indigo), (3, FocusRings.Kind.Elsewhere, FocusRings.DimWhite)], Rings(overview));
    }

    // A workspace whose colour is none still shows where focus is, in white.
    [Fact]
    public void A_workspace_with_no_colour_rings_its_focused_window_white()
    {
        var overview = new Overview.Overview([], [Workspace(true, Row(1, 1, front: true, active: true))], []);

        Assert.Equal([(1, FocusRings.Kind.Focused, FocusRings.White)], Rings(overview, colour: null));
    }

    // A window pinned to every desktop belongs to no workspace in particular, so it is white even on a coloured
    // workspace, and the other screen's front window is still found from it.
    [Fact]
    public void A_focused_pinned_window_is_white_and_still_anchors_the_other_screen()
    {
        var overview = new Overview.Overview(
            [Row(9, monitor: 1, front: true, active: true)],
            [Workspace(true, Row(3, monitor: 2, front: true))],
            []);

        Assert.Equal([(9, FocusRings.Kind.Focused, FocusRings.White), (3, FocusRings.Kind.Elsewhere, FocusRings.DimWhite)], Rings(overview));
    }

    // A desktop that is not a workspace has no colour to show: white, and dim white on the other screen.
    [Fact]
    public void An_unnamed_desktop_rings_white()
    {
        var overview = new Overview.Overview([], [Workspace(false, Row(7, 1, front: true))],
            [new DesktopGroup(Guid.NewGuid(), "Desktop 3", true, [Row(1, 1, front: true, active: true), Row(2, 2, front: true)])]);

        Assert.Equal([(1, FocusRings.Kind.Focused, FocusRings.White), (2, FocusRings.Kind.Elsewhere, FocusRings.DimWhite)], Rings(overview));
    }

    // Only the desktop you are on: another workspace's windows are cloaked, so an outline there would show nothing.
    // And nothing at all when no window has focus (the desktop or the taskbar was clicked).
    [Fact]
    public void Other_workspaces_and_a_desktop_with_nothing_focused_get_no_rings()
    {
        var elsewhere = new Overview.Overview([], [
            Workspace(true, Row(1, 1, front: true, active: true)),
            Workspace(false, Row(5, 1, front: true), Row(6, 2, front: true))], []);
        var nothingFocused = new Overview.Overview([], [Workspace(true, Row(1, 1, front: true), Row(2, 2, front: true))], []);

        Assert.Equal([(1, FocusRings.Kind.Focused, Indigo)], Rings(elsewhere));
        Assert.Empty(Rings(nothingFocused));
    }

    // A minimized window has nothing on the screen to outline, even while Windows still calls it active.
    [Fact]
    public void A_minimized_focused_window_gets_no_ring()
    {
        var overview = new Overview.Overview([], [Workspace(true, Row(1, 1, front: false, active: true, minimized: true))], []);

        Assert.Empty(Rings(overview));
    }

    // THE BLINKING BORDER (FocusRings.WithAsking): Petre's "a" from the mockup. Window 1 has focus (its ring is the
    // workspace's colour), window 2 is asking for him, window 3 is on a workspace he is not on.
    static readonly Overview.Overview Arrived = new([], [
        Workspace(true, Row(1, 1, front: true, active: true), Row(2, 1, front: false)),
        Workspace(false, Row(3, 1, front: true)),
    ], []);

    static IReadOnlyList<(int Window, string Colour)> Asking(IReadOnlyList<FocusRings.Ring> rings, int[] blinking, bool lit, string? unlit) =>
        FocusRings.WithAsking(rings, Arrived, blinking.Select(w => new WindowHandle(w)).ToList(), lit, unlit)
            .Select(r => ((int)r.Window.Value, r.Colour)).OrderBy(r => r.Item1).ToList();

    [Fact]
    public void On_the_lit_beat_an_asking_window_is_orange()
    {
        var rings = FocusRings.Of(Arrived, _ => Indigo);

        Assert.Equal([(1, Indigo), (2, FocusRings.Asking)], Asking(rings, [2], lit: true, FocusRings.DimWhite));
    }

    // On the dark beat it goes back to the ring it would have had: the focused one to its workspace colour...
    [Fact]
    public void On_the_dark_beat_a_focused_asking_window_has_its_focus_ring() =>
        Assert.Equal([(1, Indigo)], Asking(FocusRings.Of(Arrived, _ => Indigo), [1], lit: false, FocusRings.DimWhite));

    // ...and one with no ring to the dim white, so the line flips between two colours rather than coming and going.
    [Fact]
    public void On_the_dark_beat_an_asking_window_with_no_ring_is_dim_white() =>
        Assert.Equal([(1, Indigo), (2, FocusRings.DimWhite)], Asking(FocusRings.Of(Arrived, _ => Indigo), [2], lit: false, FocusRings.DimWhite));

    // With the focus ring setting off there are no other rings, and the dark beat draws nothing: Windows' own border.
    [Fact]
    public void With_the_setting_off_only_the_lit_beat_draws()
    {
        Assert.Equal([(2, FocusRings.Asking)], Asking([], [2], lit: true, unlit: null));
        Assert.Empty(Asking([], [2], lit: false, unlit: null));
    }

    // A window asking on a workspace you are not on is cloaked: nothing to outline.
    [Fact]
    public void An_asking_window_on_another_workspace_gets_no_border() =>
        Assert.Empty(Asking([], [3], lit: true, unlit: null));
}
