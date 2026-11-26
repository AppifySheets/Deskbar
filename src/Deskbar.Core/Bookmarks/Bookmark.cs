namespace Deskbar.Core.Bookmarks;

// What a bookmark opens. Stored by name in state.json (JsonStringEnumConverter), so reordering members is safe.
public enum BookmarkKind
{
    // A web address (Target) opened in the default browser.
    Link,
    // An executable (Target) started with Arguments in WorkingDirectory.
    Program,
    // A packaged (Store) app, Target being its AppUserModelID.
    App,
}

// One bookmark in a workspace's row (docs/superpowers/specs/2026-11-26-workspace-bookmarks-design.md).
// Id is stable across renames, reorders and a move to another workspace; Arguments and WorkingDirectory only mean
// something for a Program.
//
// Screen (the second bookmarks PR, Petre's option 2: "bookmark remembers that screen") is the DISPLAY NUMBER the bookmark
// was dragged to, the same number a window's screen move takes (WorkspaceManager.MoveWindowToMonitor) and the bar's
// readout says ("→ put on screen 2"). Null means no screen: the chip sits in the row's first lane and an open puts the
// window wherever it opens. Only a Program or an App has one; a link goes into a browser window Deskbar does not
// move, so BookmarkRules.Check clears it on a link. Optional and last, so an old state.json loads with none.
public sealed record Bookmark(
    Guid Id,
    string Name,
    BookmarkKind Kind,
    string Target,
    string? Arguments = null,
    string? WorkingDirectory = null,
    int? Screen = null);

// What a move asks of a bookmark's screen: leave it (Keep, the default), forget it (Any) or set it (On). A struct
// rather than an int? because "leave it" and "forget it" are two different requests that a null cannot tell apart.
public readonly record struct ScreenChange(bool Changes, int? Screen)
{
    public static ScreenChange Keep => default;
    public static ScreenChange Any => new(true, null);
    public static ScreenChange On(int screen) => new(true, screen);
}
