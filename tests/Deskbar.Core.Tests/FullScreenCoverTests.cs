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

    static FullScreenCover.Candidate Window(nint handle, int left, int top, int right, int bottom) =>
        new(handle, new FullScreenCover.Rect(left, top, right, bottom));

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

    [Fact]
    public void No_windows_cover_the_screen() =>
        Assert.Null(FullScreenCover.Covering(Screen, []));
}
