using Deskbar.Core.Geometry;

namespace Deskbar.Core.Tests;

// A full-screen window covering the floating bar's screen, and the decision to step out of its way.
//
// Measured on Windows 11 build 26300, two screens (3840x2160 at 0,0 and 1920x1080 at 3840,0), with a
// video player full screen on the 4K one: the player's full-screen window had the rectangle EXACTLY the
// monitor's (0,0,3840,2160), carried WS_EX_TOPMOST, and IsZoomed was FALSE. So "maximized" is not the
// test for "full screen", and a window's rectangle is.
public class FullScreenCoverTests
{
    // The 4K screen, as measured.
    static readonly FullScreenCover.Rect Screen = new(0, 0, 3840, 2160);

    static FullScreenCover.Candidate Window(nint handle, int left, int top, int right, int bottom, bool maximized = false) =>
        new(handle, new FullScreenCover.Rect(left, top, right, bottom), maximized);

    [Fact]
    public void A_window_exactly_on_the_screen_covers_it() =>
        Assert.Equal(1, FullScreenCover.Covering(Screen, [Window(1, 0, 0, 3840, 2160)])?.Handle);

    // Bigger than the screen is still covering it: a full-screen window that bleeds over the edge.
    [Fact]
    public void A_window_bigger_than_the_screen_covers_it() =>
        Assert.Equal(2, FullScreenCover.Covering(Screen, [Window(2, -8, -8, 3848, 2168)])?.Handle);

    // A maximized window stops at the taskbar, so it never reaches the screen's bottom and never counts.
    // The numbers are a maximized VS Code window's, measured on the machine above.
    [Fact]
    public void A_maximized_window_that_stops_at_the_taskbar_does_not_cover_the_screen() =>
        Assert.Null(FullScreenCover.Covering(Screen, [Window(3, -11, -11, 3851, 2099)]));

    // The FIRST window decides: a small one in front of the full-screen one means the full-screen one is
    // not what is being looked at.
    [Fact]
    public void A_small_window_in_front_of_a_full_screen_one_does_not_cover_it() =>
        Assert.Null(FullScreenCover.Covering(Screen, [Window(4, 100, 100, 500, 400), Window(5, 0, 0, 3840, 2160)]));

    // The owner's review: with "show taskbar on main display only" the secondary screen has no taskbar, so
    // a maximized window there has the monitor's rectangle plus its ~11 px frame. It covers by rectangle and
    // IsZoomed says it is maximized, so it is not a full-screen window and the bar stays on top.
    [Fact]
    public void A_maximized_window_that_contains_the_screen_does_not_cover_it() =>
        Assert.Null(FullScreenCover.Covering(Screen, [Window(6, -11, -11, 3851, 2171, maximized: true)]));

    // A maximized window in front of a full-screen video is what the user is looking at: the screen is not covered.
    [Fact]
    public void A_maximized_window_in_front_of_a_full_screen_one_does_not_cover_it() =>
        Assert.Null(FullScreenCover.Covering(Screen, [Window(7, -11, -11, 3851, 2171, maximized: true), Window(8, 0, 0, 3840, 2160)]));

    // A browser in F11 that was maximized before keeps IsZoomed TRUE, but its rectangle is exactly the
    // screen's, with no frame over the edges (measured: Chrome, 0,0,2560,1440 on a 2560x1440 screen). It covers.
    [Fact]
    public void A_maximized_window_exactly_on_the_screen_covers_it() =>
        Assert.Equal(10, FullScreenCover.Covering(Screen, [Window(10, 0, 0, 3840, 2160, maximized: true)])?.Handle);

    // The video player is NOT maximized (IsZoomed FALSE, measured), so a full-screen one still covers.
    [Fact]
    public void A_non_maximized_full_screen_window_covers_the_screen() =>
        Assert.Equal(9, FullScreenCover.Covering(Screen, [Window(9, 0, 0, 3840, 2160, maximized: false)])?.Handle);

    [Fact]
    public void No_windows_cover_the_screen() =>
        Assert.Null(FullScreenCover.Covering(Screen, []));
}
