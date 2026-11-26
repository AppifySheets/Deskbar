using Deskbar.Core.Domain;
using Deskbar.Core.Rehydration;

namespace Deskbar.Core.Bookmarks;

// Decides whether a bookmark's app is already running, in which case its chip is hidden (the window itself
// is the way back to it). Pure: the caller supplies every window on every desktop and the set of
// AppUserModelIDs those windows carry.
public static class BookmarkRunning
{
    // The launcher stubs a classic Chrome/Edge app shortcut targets. The window that appears runs as the
    // sibling real browser exe, so a bookmark on the stub must be compared as that exe. Not exercised by
    // the probe machine's data (see docs/superpowers/notes/2026-11-26-bookmarks-probes.md, decision c), kept
    // as a cheap safeguard for machines whose shortcuts do target the stubs.
    static readonly IReadOnlyDictionary<string, string> ProxyBrowsers =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["msedge_proxy.exe"] = "msedge.exe",
            ["chrome_proxy.exe"] = "chrome.exe",
        };

    // Swaps a stub's file name for the real browser's, in the same folder. Anything else is unchanged.
    static string RealExe(string path) =>
        ProxyBrowsers.TryGetValue(Path.GetFileName(path), out var real)
            ? Path.Combine(Path.GetDirectoryName(path) ?? "", real)
            : path;

    // Link: never (a page is not a process). App: by AppUserModelID, or by a window of its package (PackageRuns), because a PWA shares msedge.exe's
    // process and command line with plain browser windows, so nothing else about a window can tell it apart
    // (probe notes, step 1). Program: by exe path, or for a Chromium browser by profile plus app id, so a
    // PWA-style Program bookmark is not hidden by a plain browser window. A tray-only program has no window
    // and so keeps showing.
    public static bool IsRunning(Bookmark bookmark, IEnumerable<WindowInfo> windows, IReadOnlySet<string> openAppIds) =>
        bookmark.Kind switch
        {
            BookmarkKind.App => openAppIds.Any(id => string.Equals(id, bookmark.Target, StringComparison.OrdinalIgnoreCase))
                || PackageRuns(bookmark.Target, windows, openAppIds),
            BookmarkKind.Program => ProgramRuns(bookmark, windows),
            _ => false,
        };

    // A packaged program whose WINDOW carries no AppUserModelID (new Teams, Deskbar task e7fb8e04) is still open when
    // a window's exe sits in its package's folder: "...\WindowsApps\MSTeams_<version>_x64__8wekyb3d8bbwe\ms-teams.exe"
    // belongs to the app "MSTeams_8wekyb3d8bbwe!MSTeams". A framed app's window runs as ApplicationFrameHost, outside
    // WindowsApps, and a PWA's as the browser, so neither is taken for a package by this. Teams closed to the tray has
    // no window, so its chip shows, and a click brings it back.
    //
    // Only a FALLBACK for a package whose windows say nothing. A package can hold several apps (the Store's Python
    // has Python, IDLE and pip; the old Store Office had Word and Excel), and their windows usually carry their own
    // ids: when any open window carries an id of this family, the ids decide (the exact match above), so an open
    // Excel does not hide the Word chip.
    static bool PackageRuns(string appId, IEnumerable<WindowInfo> windows, IReadOnlySet<string> openAppIds) =>
        PackagedPaths.FamilyOfAppId(appId) is { } family
        && !openAppIds.Any(id => string.Equals(PackagedPaths.FamilyOfAppId(id), family, StringComparison.OrdinalIgnoreCase))
        && windows.Any(w => w.ProcessPath is { } path && PackagedPaths.IsPackaged(path)
            && string.Equals(PackagedPaths.FamilyOf(path), family, StringComparison.OrdinalIgnoreCase));

    // Whether a NEW window is the app an App bookmark just started, for putting it on the bookmark's screen
    // (BookmarkOpener, WorkspaceManager.ExpectAppWindow): the window carries the app's AppUserModelID, or, for a
    // packaged program whose window carries none (new Teams), its exe sits in the app's package. The same two tests
    // IsRunning makes, asked of one window. `windowAppId` is the window's id as the shell gives it, or null.
    public static bool IsAppWindow(string appId, WindowInfo window, string? windowAppId) =>
        string.Equals(windowAppId, appId, StringComparison.OrdinalIgnoreCase)
        || (windowAppId is null && PackagedPaths.FamilyOfAppId(appId) is { } family
            && window.ProcessPath is { } path && PackagedPaths.IsPackaged(path)
            && string.Equals(PackagedPaths.FamilyOf(path), family, StringComparison.OrdinalIgnoreCase));

    static bool ProgramRuns(Bookmark bookmark, IEnumerable<WindowInfo> windows)
    {
        var target = RealExe(bookmark.Target);
        return RosterIdentity.IsChromium(target)
            ? windows.Any(w => w.ProcessPath is not null
                && RosterIdentity.Of(target, $"\"{target}\" {bookmark.Arguments}")
                    == RosterIdentity.Of(RealExe(w.ProcessPath), w.CommandLine))
            : windows.Any(w => w.ProcessPath is not null
                && string.Equals(RealExe(w.ProcessPath), target, StringComparison.OrdinalIgnoreCase));
    }

    // The bookmarks whose chips should show: the ones not running, in the order they were added.
    public static IReadOnlyList<Bookmark> Showing(
        IEnumerable<Bookmark> bookmarks, IReadOnlyList<WindowInfo> windows, IReadOnlySet<string> openAppIds) =>
        bookmarks.Where(b => !IsRunning(b, windows, openAppIds)).ToList();
}
