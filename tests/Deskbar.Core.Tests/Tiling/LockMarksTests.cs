using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;
using Deskbar.Core.Tiling;

namespace Deskbar.Core.Tests.Tiling;

// Where a tile's lock mark goes and when it shows (spec 2026-11-22-tile-lock-and-other-screens-design.md, "The pin on
// the window"). The numbers are the probe's (docs/superpowers/notes/2026-11-22-tile-pin-probes.md): every screen at
// 144 DPI, where 24 DIP is 36 px, 4 DIP is 6 px and three 46 DIP caption buttons are 207 px.
public class LockMarksTests
{
    static readonly WindowHandle A = new(0xA);
    static readonly WindowHandle B = new(0xB);

    // Edge, Chrome, Explorer and Terminal: DWM reports the three buttons as 220 x 44 at the right edge minus 8 px, in
    // window coordinates. The mark sits 6 px left of them, centred in their 44 px band.
    [Fact]
    public void Beside_the_buttons_Windows_reports() =>
        Assert.Equal(new LockMarkSpot(new WindowRect(1730, 104, 1766, 140), new WindowRect(1011, 100, 1989, 144)),
            LockMarks.Place(new LockMarkGeometry(new WindowRect(1000, 100, 2000, 900), new WindowRect(1011, 100, 1989, 889),
                new WindowRect(772, 0, 992, 44), 144)).Value);

    // VS Code draws its own buttons and DWM answers with a zero-width rectangle at the right edge, so the buttons are
    // taken to be three standard ones, 207 px from the visible right edge.
    [Fact]
    public void Beside_three_standard_buttons_when_the_app_draws_its_own() =>
        Assert.Equal(new WindowRect(1351, 4, 1387, 40),
            LockMarks.Place(new LockMarkGeometry(new WindowRect(0, 0, 1600, 1000), new WindowRect(0, 0, 1600, 1000),
                new WindowRect(1600, 0, 1600, 44), 144)).Value.Mark);

    // No answer from DWM at all: a 32 DIP title band from the visible top. At 96 DPI the buttons start at
    // 1600 - 138 = 1462, the mark is 1434..1458, and its top is 0 + (32 - 24) / 2 = 4.
    [Fact]
    public void A_32_DIP_title_band_when_DWM_says_nothing() =>
        Assert.Equal(new LockMarkSpot(new WindowRect(1434, 4, 1458, 28), new WindowRect(0, 0, 1600, 32)),
            LockMarks.Place(new LockMarkGeometry(new WindowRect(0, 0, 1600, 1000), new WindowRect(0, 0, 1600, 1000),
                Maybe<WindowRect>.None, 96)).Value);

    // Review focus 4: a tile so narrow the mark would fall off its left edge gets no mark.
    [Fact]
    public void No_mark_on_a_tile_too_narrow_for_it() =>
        Assert.True(LockMarks.Place(new LockMarkGeometry(new WindowRect(0, 0, 220, 600), new WindowRect(0, 0, 220, 600),
            new WindowRect(0, 0, 220, 44), 144)).HasNoValue);

    static readonly LockMarkSpot SpotA = new(new WindowRect(100, 4, 124, 28), new WindowRect(0, 0, 200, 32));
    static readonly LockMarkSpot SpotB = new(new WindowRect(300, 4, 324, 28), new WindowRect(200, 0, 400, 32));

    [Fact]
    public void A_locked_tile_shows_wherever_the_pointer_is() =>
        Assert.Equal([new LockMark(A, SpotA.Mark, true)],
            LockMarks.Wanted([(A, SpotA, true), (B, SpotB, false)], Maybe<(int X, int Y)>.None));

    [Fact]
    public void An_unlocked_tile_shows_while_the_pointer_is_on_its_title_band() =>
        Assert.Equal([new LockMark(A, SpotA.Mark, true), new LockMark(B, SpotB.Mark, false)],
            LockMarks.Wanted([(A, SpotA, true), (B, SpotB, false)], (250, 10)));

    // The band's right and bottom edges belong to the next thing down or across: a pointer one pixel below the title
    // band is on the app's content, not its title.
    [Fact]
    public void The_band_excludes_its_bottom_edge() =>
        Assert.Empty(LockMarks.Wanted([(B, SpotB, false)], (250, 32)));
}
