using Deskbar.Core.Domain;

namespace Deskbar.Core.Tiling;

// What the Tiler should do with a window once a drag or resize has settled.
public abstract record MoveOutcome
{
    // The window's centre landed on another window's tile: they trade slots.
    public sealed record Swap(WindowHandle With) : MoveOutcome;

    // The centre is over its own slot, or over no tile at all (a resize that left it in a gap):
    // snap it back where it was.
    public sealed record BackToSlot : MoveOutcome;

    // The window now sits on a different screen: it joins that screen's grid.
    public sealed record JoinScreen(int Screen) : MoveOutcome;
}

// The rule for a settled move, kept out of the Win32 plumbing so it can be tested. It decides by the
// frame's CENTRE rather than by overlap: one point has one answer, where overlap can tie between two
// tiles.
public static class SettledMove
{
    // `slots` are the current tile rectangles on `screenNow`, by the window that holds each one.
    public static MoveOutcome Decide(
        WindowHandle moved,
        WindowRect frame,
        int screenNow,
        TileBoard board,
        IReadOnlyDictionary<WindowHandle, WindowRect> slots) =>
        board.ScreenOf(moved).TryGetValue(out var home) && home == screenNow
            ? Within(moved, frame, slots)
            : new MoveOutcome.JoinScreen(screenNow);

    // Same screen: the tile containing the centre decides. Rectangles are half-open (Right and Bottom
    // are exclusive), so a centre exactly on a shared edge belongs to the tile on its right/below.
    static MoveOutcome Within(WindowHandle moved, WindowRect frame, IReadOnlyDictionary<WindowHandle, WindowRect> slots)
    {
        var cx = frame.Left + frame.Width / 2;
        var cy = frame.Top + frame.Height / 2;
        var hit = slots.Where(s => s.Value.Left <= cx && cx < s.Value.Right && s.Value.Top <= cy && cy < s.Value.Bottom)
            .Select(s => s.Key)
            .Where(w => w != moved)
            .ToList();
        return hit.Count > 0 ? new MoveOutcome.Swap(hit[0]) : new MoveOutcome.BackToSlot();
    }
}
