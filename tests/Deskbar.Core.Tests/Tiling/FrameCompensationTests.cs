using Deskbar.Core.Domain;
using Deskbar.Core.Tiling;

namespace Deskbar.Core.Tests.Tiling;

// The tiling grid speaks in VISIBLE frames (DWMWA_EXTENDED_FRAME_BOUNDS); SetWindowPos speaks in window
// rectangles, which include the invisible resize borders Windows 10 and 11 draw around a sizable window.
// FrameCompensation is the one conversion between them, so a slot lands where the user can see it rather
// than seven pixels inside it.
public class FrameCompensationTests
{
    // A window with no invisible border (a borderless app, or Windows with the borders turned off): the
    // window rectangle IS the visible frame, so what was asked for is what is set.
    [Fact]
    public void No_border_passes_through()
    {
        var window = new WindowRect(10, 20, 810, 620);

        var outer = FrameCompensation.Outer(new WindowRect(100, 100, 600, 400), window, window);

        Assert.Equal(new WindowRect(100, 100, 600, 400), outer);
    }

    // The common case on Windows 10 and 11 at 100%: seven invisible pixels on the left, right and bottom,
    // none on top (the title bar is the top edge). The window rectangle has to be that much larger than the
    // slot on those three sides for the visible frame to land exactly on it.
    [Fact]
    public void A_seven_pixel_invisible_border_is_added_on_left_right_and_bottom()
    {
        var outer = FrameCompensation.Outer(
            wantedVisible: new WindowRect(100, 100, 600, 400),
            windowRect: new WindowRect(0, 0, 1014, 607),
            visibleFrame: new WindowRect(7, 0, 1007, 600));

        Assert.Equal(new WindowRect(93, 100, 607, 407), outer);
    }
}
