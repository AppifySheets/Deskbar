using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Tiling;

// LOCKED TILES (docs/superpowers/specs/2026-11-22-tile-lock-and-other-screens-design.md). Petre: "i want to pin the
// window so that it's not moving". Of the ways a tile moves, the one a lock answers is a new window taking half of it,
// so a locked tile is never the tile an arrival splits (Tiler.Arrival.cs adds the locked tiles to the set
// TileArrival.Choose skips). What the lock does NOT cover, on purpose (spec, non-goals): a locked tile still grows when
// a neighbour in its split leaves, still follows a dragged separation, still takes part in "Even out tiles" and "Reset
// to grid", still swaps when dragged, and still moves when a screen or its work area changes.
//
// Set from the bar's window icon menu ("Don't split this tile") and from the control pipe (`lock-tile <hwnd> on|off`).
//
// Kept by window handle, in memory only. It is forgotten when the window closes (HandleWindowEvent's Disappeared calls
// ForgetLock), because Windows reuses handles and a new window must not inherit an old one's lock; and it is forgotten
// when Deskbar restarts. Writing handles to state.json would survive a Deskbar restart but not a reboot, after which a
// stored handle can name an unrelated window; keying on the process and its start time would fix that and costs more
// than the feature is worth until live use says losing the lock on a restart matters.
//
// Neither method goes through Run: no re-tile follows a lock (it only changes what a LATER arrival chooses), and the
// set is swapped whole rather than changed in place, so a call that lands while an evaluate is pumping messages leaves
// that evaluate reading one consistent set.
public sealed partial class Tiler
{
    // The locked windows. Replaced, never mutated, for the reason in the file header.
    IReadOnlySet<WindowHandle> locked = new HashSet<WindowHandle>();

    // None when the window is not a tile of the workspace being looked at (not on the current board), so the menu
    // hides its item; otherwise whether it is locked.
    public Maybe<bool> TileLockOf(WindowHandle window) =>
        Board.ScreenOf(window).HasValue ? locked.Contains(window) : Maybe<bool>.None;

    // Locks or unlocks a tile. Locking needs the window to be a tile now; unlocking always succeeds, so a stale
    // "off" from the pipe is harmless. Moves nothing.
    public Result SetTileLocked(WindowHandle window, bool on)
    {
        if (disposed) return Result.Failure("Tiling is not running.");
        if (on && !Board.ScreenOf(window).HasValue) return Result.Failure("That window is not in a tile.");
        locked = on ? locked.Append(window).ToHashSet() : locked.Where(w => w != window).ToHashSet();
        var where = Acting().Map(w => w.Name).GetValueOrDefault("?");
        trace?.Invoke($"tile: {where}: {window.Value:X} {(on ? "locked" : "unlocked")}");
        return Result.Success();
    }

    // A window that is gone takes its lock with it: its handle may be handed to another window.
    void ForgetLock(WindowHandle window) =>
        locked = locked.Contains(window) ? locked.Where(w => w != window).ToHashSet() : locked;

    // The locked tiles among `windows`, for the arrival's skip set.
    IReadOnlySet<WindowHandle> LockedAmong(IEnumerable<WindowHandle> windows) => windows.Where(locked.Contains).ToHashSet();
}
