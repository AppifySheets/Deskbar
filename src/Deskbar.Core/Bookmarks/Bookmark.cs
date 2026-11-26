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
// Id is stable across renames and reorders; Arguments and WorkingDirectory only mean something for a Program.
public sealed record Bookmark(
    Guid Id,
    string Name,
    BookmarkKind Kind,
    string Target,
    string? Arguments = null,
    string? WorkingDirectory = null);
