using Deskbar.Core.Domain;

namespace Deskbar.App;

// ONE OLE drag payload for every Deskbar drag surface -- WindowGroupsView rows
// (switcher panel + Manage Windows tab) and FloatingBar icons. Promoted out of
// WindowGroupsView (where it started life as a private record) when the floating bar
// gained icon drag: sharing the format string AND the group-key vocabulary means a drag
// can start on one surface and drop on another, and every surface's "dropped onto its
// own group is a no-op" guard stays a plain string comparison.
//
// Carrying the source group alongside the handle (rather than just the bare handle) is
// what makes that no-op guard possible without any extra desktop query at drop time.
sealed record DraggedWindow(WindowHandle Handle, string SourceGroupKey)
{
    internal const string DragFormat = "Deskbar.DraggedWindow";

    // Group-key vocabulary, shared verbatim by every surface: 📌 Pinned, a workspace
    // (by workspace id), a plain OS desktop that isn't a workspace (by desktop id --
    // includes the "Unplaced" catch-all, whose DesktopId is Guid.Empty).
    internal const string PinnedGroupKey = "pinned";
    internal static string WorkspaceGroupKey(Guid workspaceId) => $"workspace:{workspaceId}";
    internal static string DesktopGroupKey(Guid desktopId) => $"desktop:{desktopId}";
}

// A BOOKMARK CHIP being dragged (the second bookmarks PR): to a screen half of its row, so the bookmark remembers that
// screen, or onto another workspace's row, so it moves there. A format of its own, never DraggedWindow's, so no surface
// that takes windows (a strip, the pinned row, a header drop) can mistake a chip for a window, and BookmarkDropData
// (things dragged in from outside) does not read it either. `Chip` is the button the drag started on, kept so a drop
// that lands back on the chip itself can be told apart as the slipped click it is (#48).
sealed record DraggedBookmark(Guid WorkspaceId, Guid BookmarkId, System.Windows.FrameworkElement Chip)
{
    internal const string DragFormat = "Deskbar.DraggedBookmark";
}
