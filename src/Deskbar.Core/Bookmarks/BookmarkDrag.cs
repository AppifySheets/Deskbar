namespace Deskbar.Core.Bookmarks;

// What dropping a dragged bookmark chip at one place would do, and what the bar's readout says about it while the chip
// is held there (the second bookmarks PR; Petre picked option 2, "bookmark remembers that screen"). The bar asks this on
// every DragOver and again at the Drop, so the readout and the drop cannot disagree; the drop then hands To and Screen
// to WorkspaceManager.MoveBookmark, the call `bookmark move` makes.
//
// Allowed false: the drop does nothing, and the readout says why (or where to drag instead).
public sealed record ChipDrop(Guid? To, ScreenChange Screen, string Readout, bool Allowed);

public static class BookmarkDrag
{
    // `from` is the workspace the chip belongs to, `target` the row under the pointer, `aimed` the screen half of that
    // row the pointer is over (null when the row has only one screen to aim at, AimedMonitor's own null), and
    // `targetHasSame` whether the target workspace already holds a bookmark opening the same thing (BookmarkRules.
    // SameTarget), which the move would refuse.
    public static ChipDrop Plan(Bookmark chip, Guid from, Guid target, string targetName, int? aimed, bool targetHasSame) =>
        target != from
            ? targetHasSame
                ? new ChipDrop(null, ScreenChange.Keep, $"can't move: {targetName} already has this bookmark", false)
                // Another workspace. A program or app dropped on a screen half takes that screen too, the way a window
                // dropped there does ("→ move to Work, screen 2"); a link only moves, and the readout does not promise
                // a screen it will not get.
                : aimed is { } screen && chip.Kind != BookmarkKind.Link
                    ? new ChipDrop(target, ScreenChange.On(screen), $"→ move to {targetName}, screen {screen}", true)
                    : new ChipDrop(target, ScreenChange.Keep, $"→ move to {targetName}", true)
            : aimed is not { } here
                ? new ChipDrop(null, ScreenChange.Keep, "→ drag to another bar to move it there", false)
                // The brief left links to the builder: a link opens in whichever browser window the browser picks, and
                // Deskbar does not move that window, so a screen would be a promise it cannot keep. Said, not silently
                // stored.
                : chip.Kind == BookmarkKind.Link
                    ? new ChipDrop(null, ScreenChange.Keep, "a link opens in its browser's window, which keeps its own screen", false)
                    : chip.Screen == here
                        ? new ChipDrop(null, ScreenChange.Keep, $"→ already opens on screen {here}", false)
                        : new ChipDrop(null, ScreenChange.On(here), $"→ put on screen {here}", true);
}
