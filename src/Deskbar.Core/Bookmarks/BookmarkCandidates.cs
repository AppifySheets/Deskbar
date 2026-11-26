using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;
using Deskbar.Core.Rehydration;

namespace Deskbar.Core.Bookmarks;

// Which bookmark, if any, a window's menu can offer ("Bookmark this in <workspace>").
public static class BookmarkCandidates
{
    // An AppUserModelID can be started through shell:AppsFolder\<id> when it has the packaged form
    // "<family>!<app>". That takes in every PWA and Store app, and it leaves out the plain browsers without a
    // special case, because "MSEdge", "Chrome" and "Chrome.<profile>.<name>" have no "!" (probes note, decision a).
    // Windows Terminal's id has one and starts fine, so a terminal window offers an App bookmark.
    public static bool IsStartableAppId(string appId) =>
        !string.IsNullOrWhiteSpace(appId) && appId.Contains('!');

    // The App first (it covers a PWA that shares the browser's process, which has no roster identity), then a
    // Program for a window with a roster identity, else nothing: a plain browser window, the shell and an
    // elevated window with no readable path cannot say what to start.
    public static Maybe<Bookmark> FromWindow(WindowInfo window, string? appId, IBookmarkSources sources) =>
        !string.IsNullOrWhiteSpace(appId) && IsStartableAppId(appId)
            ? new Bookmark(Guid.NewGuid(), sources.NameOfApp(appId) ?? window.DisplayName, BookmarkKind.App, appId)
            : window.ProcessPath is { } path && RosterIdentity.Of(window).HasValue
                ? new Bookmark(
                    Guid.NewGuid(),
                    sources.DescribeExe(path) ?? window.ProcessName,
                    BookmarkKind.Program,
                    path,
                    CommandLines.ArgumentsOf(window.CommandLine, path) is { Length: > 0 } args ? args : null)
                : Maybe<Bookmark>.None;
}
