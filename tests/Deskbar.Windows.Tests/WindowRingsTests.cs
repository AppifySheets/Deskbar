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
}
