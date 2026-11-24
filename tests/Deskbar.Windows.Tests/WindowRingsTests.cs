using Deskbar.App;

namespace Deskbar.Windows.Tests;

// The focus ring hands Windows its colour as a COLORREF, which is 0x00BBGGRR: the palette's #RRGGBB backwards. A swap
// here would put every workspace's ring in the wrong colour (Indigo would come out a muddy orange) and nothing else
// would notice.
public class WindowRingsTests
{
    [Theory]
    [InlineData("#5B6CFF", 0x00FF6C5B)] // the mockup's Indigo
    [InlineData("#FFFFFF", 0x00FFFFFF)] // white, where there is no workspace colour
    [InlineData("#737373", 0x00737373)] // the dim white on another screen
    [InlineData("#FF0000", 0x000000FF)] // red lands in the low byte
    public void A_hex_colour_becomes_a_COLORREF(string hex, int expected) =>
        Assert.Equal(expected, WindowRings.ColorRef(hex));

    // Where Windows cannot colour a border, the focused window's solid ring is drawn by a frame like the trail's. Server
    // 2022 is the build it was reported from: the focused window had no ring unless it was maximized.
    [Theory]
    [InlineData(19045, false)] // Windows 10 22H2
    [InlineData(20348, false)] // Windows Server 2022
    [InlineData(22000, true)]  // Windows 11 21H2, the first with DWMWA_BORDER_COLOR
    [InlineData(26200, true)]  // Windows 11 25H2
    public void Windows_colours_its_own_border_from_Windows_11_on(int build, bool works) =>
        Assert.Equal(works, WindowRings.BorderColourWorks(build));
}
