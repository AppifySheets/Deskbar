using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;
using Deskbar.Core.Overview;

namespace Deskbar.Core.Tests;

// The windows that wear the bar's "fronted" marker (FrontsElsewhere). Petre, with two screens: "mark it, too", then
// "let's do it on collapsed bars as well". Per row, the window in front on each screen other than the row's anchor's:
// the active window in the current workspace's row, the window a switch would bring forward in every other row.
public class FrontsElsewhereTests
{
    static WindowRow Row(int hwnd, int monitor, bool front, bool active = false, bool landing = false, bool minimized = false, bool knownOrder = true) =>
        new(new WindowInfo(new WindowHandle(hwnd), hwnd, $"app{hwnd}", $@"C:\app{hwnd}.exe", $"Window {hwnd}", null),
            Maybe<string>.None,
            IsActive: active,
            WillActivate: landing,
            Monitor: monitor,
            IsMinimized: minimized,
            IsFrontmostOnMonitor: knownOrder ? front : Maybe<bool>.None);

    static Overview.Overview Of(params (bool Current, WindowRow[] Rows)[] groups) =>
        new([],
            groups.Select((g, i) => new WorkspaceGroup(new Workspace(Guid.NewGuid(), $"W{i}", Guid.NewGuid()), g.Current, g.Rows, [])).ToList(),
            []);

    static IEnumerable<int> Marked(Overview.Overview overview) =>
        FrontsElsewhere.Of(overview).Select(h => (int)h.Value).Order();

    // The current workspace: the active window is on screen 1, and the window in front on screen 2 is marked. A
    // window behind it on screen 2 is not, nor is anything on the active window's own screen.
    [Fact]
    public void The_current_row_marks_the_front_window_of_the_other_screen()
    {
        var overview = Of((true, [
            Row(1, monitor: 1, front: true, active: true),
            Row(2, monitor: 1, front: false),
            Row(3, monitor: 2, front: true),
            Row(4, monitor: 2, front: false),
        ]));

        Assert.Equal([3], Marked(overview));
    }

    // A collapsed row of another workspace: its anchor is the window a switch brings forward, and the front window of
    // its other screen is marked, so the row says what both screens will show after the switch.
    [Fact]
    public void A_collapsed_row_marks_the_front_window_of_the_screen_its_landing_window_is_not_on()
    {
        var overview = Of(
            (true, [Row(1, monitor: 1, front: true, active: true)]),
            (false, [
                Row(10, monitor: 2, front: true, landing: true),
                Row(11, monitor: 1, front: true),
                Row(12, monitor: 1, front: false),
            ]));

        Assert.Equal([11], Marked(overview));
    }

    // Nothing to compare against, nothing marked: a row with no active or landing window, a row whose z-order is not
    // known, a minimized window, and a single screen.
    [Fact]
    public void Rows_without_an_anchor_or_an_order_and_minimized_windows_mark_nothing()
    {
        var noAnchor = Of((true, [Row(1, 1, front: true), Row(2, 2, front: true)]));
        var noOrder = Of((true, [Row(1, 1, front: false, active: true, knownOrder: false), Row(2, 2, front: false, knownOrder: false)]));
        var minimized = Of((true, [Row(1, 1, front: true, active: true), Row(2, 2, front: true, minimized: true)]));
        var oneScreen = Of((true, [Row(1, 1, front: true, active: true), Row(2, 1, front: false)]));

        Assert.Empty(Marked(noAnchor));
        Assert.Empty(Marked(noOrder));
        Assert.Empty(Marked(minimized));
        Assert.Empty(Marked(oneScreen));
    }

    // Three screens: one marked window for each screen that is not the anchor's.
    [Fact]
    public void Every_other_screen_gets_its_own_marked_window()
    {
        var overview = Of((true, [
            Row(1, monitor: 1, front: true, active: true),
            Row(2, monitor: 2, front: true),
            Row(3, monitor: 3, front: true),
        ]));

        Assert.Equal([2, 3], Marked(overview));
    }
}
