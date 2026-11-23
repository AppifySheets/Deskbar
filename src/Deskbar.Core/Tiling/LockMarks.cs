using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Tiling;

// What a tile's lock mark is measured from, all in physical pixels. `Window` is GetWindowRect (invisible resize borders
// included), `Visible` is DWMWA_EXTENDED_FRAME_BOUNDS, and `CaptionButtons` is DWMWA_CAPTION_BUTTON_BOUNDS, which DWM
// gives in WINDOW coordinates (relative to `Window`'s top-left), or None where DWM refused. `Dpi` is the tile's own.
public sealed record LockMarkGeometry(WindowRect Window, WindowRect Visible, Maybe<WindowRect> CaptionButtons, int Dpi);

// Where one tile's mark goes (`Mark`) and the strip of its title bar that counts as "the mouse is on this tile's title"
// (`TitleBand`), screen coordinates.
public sealed record LockMarkSpot(WindowRect Mark, WindowRect TitleBand);

// One mark to draw: on which tile, where, and which look (closed padlock on a fill, or open padlock in outline).
public sealed record LockMark(WindowHandle Window, WindowRect Frame, bool Locked);

// THE LOCK MARK'S GEOMETRY AND RULE, the part that can be wrong in ways nobody notices, kept here so tests pin it down.
// Petre: "can you have extra buttons on the windows themselves, maybe on top of the window, so i don't have to go to the
// bar?", then "can you also draw the pin on the window?", and from the glyph mockup, B: a padlock, because VS Code's own
// "always on top" pushpin sits exactly where the mark goes. Spec: 2026-11-22-tile-lock-and-other-screens-design.md,
// "The pin on the window". The window that draws it is LockMarkWindow in the App; TileLockMarks keeps it on its tile.
//
// WHERE. Just left of the caption buttons, a 24 DIP square, 4 DIP clear of them, centred in their band. The buttons'
// position comes from DWM where it knows it. The probe (docs/superpowers/notes/2026-11-22-tile-pin-probes.md) measured
// three answers:
//   * Edge, Chrome, Explorer, Terminal: a real rectangle, 220 x 44 at 150%. Used as it is.
//   * VS Code: a ZERO-WIDTH rectangle at the right edge, height 44, because it draws its own buttons. They measured
//     46 DIP each, the same as Windows' own, so the buttons are taken as three 46 DIP ones from the visible right edge,
//     in the band DWM did report.
//   * No answer at all: the same three buttons, in a 32 DIP band from the visible top. A guess, but a harmless one.
// A tile too narrow to hold the mark left of its buttons gets none, rather than one over the app's title or off the tile.
//
// WHEN. A locked tile's mark is always shown, so the lock can be seen. An unlocked tile's is shown only while the
// pointer is on that tile's title band, so a screen of unlocked tiles carries no clutter.
public static class LockMarks
{
    public const int SizeDip = 24, GapDip = 4, CaptionButtonDip = 46, FallbackTitleDip = 32;

    public static Maybe<LockMarkSpot> Place(LockMarkGeometry g)
    {
        int Px(int dip) => (int)Math.Round(dip * g.Dpi / 96.0, MidpointRounding.AwayFromZero);

        // The caption buttons' left edge, and the band they sit in (top, height), per the three answers above.
        var reported = g.CaptionButtons.Where(b => b.Height > 0);
        var buttonsLeft = reported.Where(b => b.Width > 0)
            .Map(b => g.Window.Left + b.Left)
            .GetValueOrDefault(g.Visible.Right - Px(3 * CaptionButtonDip));
        var (bandTop, bandHeight) = reported
            .Map(b => (g.Window.Top + b.Top, b.Height))
            .GetValueOrDefault((g.Visible.Top, Px(FallbackTitleDip)));

        var size = Px(SizeDip);
        var right = buttonsLeft - Px(GapDip);
        var top = bandTop + (bandHeight - size) / 2;
        var mark = new WindowRect(right - size, top, right, top + size);
        return mark.Left < g.Visible.Left
            ? Maybe<LockMarkSpot>.None
            : new LockMarkSpot(mark, new WindowRect(g.Visible.Left, bandTop, g.Visible.Right, bandTop + bandHeight));
    }

    // The marks to draw, in the tiles' order: every locked tile, and an unlocked one while the pointer is on its title
    // band or on its mark. No pointer (Windows would not say) shows the locked ones only.
    public static IReadOnlyList<LockMark> Wanted(
        IEnumerable<(WindowHandle Window, LockMarkSpot Spot, bool Locked)> tiles, Maybe<(int X, int Y)> pointer) =>
        tiles
            .Where(t => t.Locked || pointer.Map(p => Inside(t.Spot.TitleBand, p) || Inside(t.Spot.Mark, p)).GetValueOrDefault(false))
            .Select(t => new LockMark(t.Window, t.Spot.Mark, t.Locked))
            .ToList();

    // Left and top edges in, right and bottom out, so two touching rectangles never both claim one pixel.
    static bool Inside(WindowRect r, (int X, int Y) p) => p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
}
