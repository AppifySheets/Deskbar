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

    // Link: never (a page is not a process). App: only by AppUserModelID, because a PWA shares msedge.exe's
    // process and command line with plain browser windows, so nothing else about a window can tell it apart
    // (probe notes, step 1). Program: by exe path, or for a Chromium browser by profile plus app id, so a
    // PWA-style Program bookmark is not hidden by a plain browser window. A tray-only program has no window
    // and so keeps showing.
    public static bool IsRunning(Bookmark bookmark, IEnumerable<WindowInfo> windows, IReadOnlySet<string> openAppIds) =>
        bookmark.Kind switch
        {
            BookmarkKind.App => openAppIds.Any(id => string.Equals(id, bookmark.Target, StringComparison.OrdinalIgnoreCase)),
            BookmarkKind.Program => ProgramRuns(bookmark, windows),
            _ => false,
        };

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
