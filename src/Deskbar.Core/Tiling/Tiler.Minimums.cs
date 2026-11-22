using Deskbar.Core.Abstractions;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Tiling;

// A WINDOW THAT WILL NOT GO AS SMALL AS ITS TILE (#398). Seen while filming on 2026-11-22: three tiles side by side at
// 125%, and Edge will not go narrower than about 627 px, so it kept a frame about 100 px wider than its column and
// overlapped its neighbour while the other windows kept their columns. The spec's rule for such a window was "accept
// what the window ends up as, do not retry in a loop" (Tiler.Given), which kept the Tiler from fighting the app but
// left the overlap.
//
// Now the frame it kept is taken as its minimum, and its screen is laid out again with that minimum: GridLayout gives
// its cell at least that size and takes the room from the cells beside it in the same split, as far as they can spare
// it (GridLayout.Fit). Learned rather than asked: WM_GETMINMAXINFO carries a pointer into the receiving process, so
// another process cannot send it, and the frame a window keeps is the answer the window actually gives.
//
// Learned only from a Place the window ANSWERED (it moved: Given.Answered) for the slot it has now, and only by more
// than Slack pixels, so a frame off by a border's width is not a minimum. Never from a window the backstop left alone,
// since that frame is wherever the user put it, not what it insists on. Kept per screen, because a minimum is in
// physical pixels and another screen has another scale; a window that moves screens learns again there. Forgotten
// when the window closes or leaves the tiled board (Forget), and when Deskbar restarts: it is relearned on the first
// re-tile, at the cost of one overlap that is put right at once.
public sealed partial class Tiler
{
    // More than this past its slot, in physical pixels, and the frame is a minimum rather than a rounding difference.
    const int Slack = 8;

    // Each window's learned minimum and the screen it was learned on.
    readonly Dictionary<WindowHandle, (int Screen, TileMinimum Minimum)> minimums = [];

    // The minimum of each window on `screen`, in the windows' order, for GridLayout; null for none.
    IReadOnlyList<TileMinimum?> MinimumsOn(TileScreen screen, IReadOnlyList<WindowHandle> windows) =>
        windows.Select(w => minimums.TryGetValue(w, out var known) && known.Screen == screen.Number ? known.Minimum : null).ToList();

    // After a screen's windows were placed: notes each one that kept a frame bigger than its slot. True when a minimum
    // was learned or grew, so the screen has to be laid out again to make room for it.
    bool LearnMinimums(TileScreen screen, IReadOnlyDictionary<WindowHandle, WindowRect> slots, IReadOnlySet<WindowHandle> maximized) =>
        slots.Where(pair => !maximized.Contains(pair.Key) && !leftAlone.Contains(pair.Key))
            .Select(pair => Learn(screen, pair.Key, pair.Value))
            .Aggregate(false, (any, learned) => any | learned);

    bool Learn(TileScreen screen, WindowHandle window, WindowRect slot)
    {
        if (!given.TryGetValue(window, out var last) || !last.Answered || last.Slot != slot) return false;
        var wider = last.Accepted.Width > slot.Width + Slack;
        var taller = last.Accepted.Height > slot.Height + Slack;
        if (!wider && !taller) return false;

        var known = minimums.TryGetValue(window, out var held) && held.Screen == screen.Number ? held.Minimum : new TileMinimum(0, 0);
        var learned = new TileMinimum(
            wider ? Math.Max(known.Width, last.Accepted.Width) : known.Width,
            taller ? Math.Max(known.Height, last.Accepted.Height) : known.Height);
        if (learned == known) return false;

        minimums[window] = (screen.Number, learned);
        trace?.Invoke($"tile: {window.Value:X} will not go below {learned.Width}x{learned.Height} on screen {screen.Number}, making room for it");
        return true;
    }

    void ForgetMinimum(WindowHandle window) => minimums.Remove(window);
}
