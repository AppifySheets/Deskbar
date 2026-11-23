using Deskbar.Core.Domain;

namespace Deskbar.Core.Tiling;

// RECENT WINDOWS OF A "SPLIT LAST" APP (docs/superpowers/specs/2026-11-23-split-recent-app-windows-last-design.md).
// Petre: "i think what i need is that the vscode windows are not affected, or at least not the current one when
// oepning a new browser or other window", "so i don't get interrupted", "maybe the last few vscode windows that were
// active, last 3". Of the two rules offered he took the middle way: a new window still splits the tile last used
// (predictable placement: it lands beside what you were using), unless that tile is PROTECTED; then the largest free
// tile gives the room, on this screen or another, and protected tiles are split only when nothing else is free, the
// oldest first.
//
// Protected: for every app in AppState.SplitLastApps, its three tiles on the board activated most recently. Counted
// from the activation order the arrival rule already keeps (`used`, Tiler.Arrival.cs), which leaves out activations
// Windows hands out by itself after a close or minimize. A tile never activated since Deskbar started has no place in
// that order and is not protected: protection is about the windows you have been working in.
//
// Computed afresh at every arrival, so a protected window that closes hands its place to the next one back.
public sealed partial class Tiler
{
    // How many windows of one app are protected. Petre: "last 3, let's say". A constant until live use asks otherwise.
    const int ProtectedPerApp = 3;

    // A window's place in the activation order: 0 for the one activated most recently. A window not in the order (never
    // activated, or activated so long ago it fell off the list) is older than every window in it.
    int AgeOf(WindowHandle window)
    {
        var index = used.Select(u => u.Window).ToList().IndexOf(window);
        return index < 0 ? int.MaxValue : index;
    }

    // The protected tiles of `board`: per split-last app, its three tiles activated most recently.
    IReadOnlySet<WindowHandle> ProtectedAmong(TileBoard board)
    {
        if (manager.State.SplitLastApps.Count == 0) return new HashSet<WindowHandle>();
        return board.Screens.Values.SelectMany(s => s)
            .Select(w => (Window: w, App: manager.ProcessNameOf(w), Age: AgeOf(w)))
            .Where(t => t.App.HasValue && t.Age != int.MaxValue && manager.SplitsLast(t.App.Value))
            .GroupBy(t => t.App.Value, StringComparer.OrdinalIgnoreCase)
            .SelectMany(g => g.OrderBy(t => t.Age).Take(ProtectedPerApp))
            .Select(t => t.Window)
            .ToHashSet();
    }
}
