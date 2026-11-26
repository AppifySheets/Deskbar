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
    //
    // A PACKAGED program is never a Program bookmark (Deskbar task e7fb8e04, "no teams, why?"). New Teams' window
    // carries no AppUserModelID, so it used to fall through to a Program bookmark built from its process: the exe
    // in one version's WindowsApps folder, gone at the next update, and the command line Windows gave the
    // autostart at sign-in, "msteams:system-initiated", which tells Teams to stay in the tray. Clicking that chip
    // with Teams closed to the tray started nothing anyone could see. Such a window now takes its PROCESS's
    // AppUserModelID and becomes an App bookmark, started through the Start menu entry the way quick find starts
    // it, which brings a tray-hidden Teams back. A packaged process whose id cannot be read offers nothing.
    public static Maybe<Bookmark> FromWindow(WindowInfo window, string? appId, IBookmarkSources sources) =>
        StartableId(window, appId, sources) is { } id
            ? new Bookmark(Guid.NewGuid(), sources.NameOfApp(id) ?? window.DisplayName, BookmarkKind.App, id)
            : window.ProcessPath is { } path && !PackagedPaths.IsPackaged(path) && RosterIdentity.Of(window).HasValue
                ? new Bookmark(
                    Guid.NewGuid(),
                    sources.DescribeExe(path) ?? window.ProcessName,
                    BookmarkKind.Program,
                    path,
                    CommandLines.ArgumentsOf(window.CommandLine, path) is { Length: > 0 } args ? args : null)
                : Maybe<Bookmark>.None;

    // The window's own id when it has a startable one, else, for a packaged program only, its process's.
    static string? StartableId(WindowInfo window, string? appId, IBookmarkSources sources) =>
        !string.IsNullOrWhiteSpace(appId) && IsStartableAppId(appId)
            ? appId
            : window.ProcessPath is { } path && PackagedPaths.IsPackaged(path)
                && sources.AppIdOfProcess(window.ProcessId) is { } own && IsStartableAppId(own)
                ? own
                : null;

    // HEALING a bookmark saved before the rule above: a Program bookmark on an exe in a WindowsApps folder. Its
    // package family is the one to look up (PackageToHeal, read by the app off the UI thread), and Healed turns it
    // into the App bookmark the window's menu would make now, keeping its id and name. A family that cannot be
    // resolved (uninstalled, or several apps in one package) leaves the bookmark as it was.
    public static string? PackageToHeal(Bookmark bookmark) =>
        bookmark.Kind == BookmarkKind.Program && PackagedPaths.IsPackaged(bookmark.Target)
            ? PackagedPaths.FamilyOf(bookmark.Target)
            : null;

    public static Bookmark Healed(Bookmark bookmark, Func<string, string?> appIdOfPackage) =>
        PackageToHeal(bookmark) is { } family && appIdOfPackage(family) is { } id && IsStartableAppId(id)
            ? bookmark with { Kind = BookmarkKind.App, Target = id, Arguments = null, WorkingDirectory = null }
            : bookmark;
}
