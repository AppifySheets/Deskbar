using CSharpFunctionalExtensions;
using Deskbar.Core.Control;
using Deskbar.Core.Domain;
using Deskbar.Core.QuickFind;

namespace Deskbar.Core.Bookmarks;

// How the three kinds of bookmark are actually started. Supplied by the app (the real ProgramStarter, the shell's
// AppUserModelID activation) and by tests (fakes that record the calls), so this class stays in Core and nothing
// in it can start a process by itself.
//
//   Program  (exe, arguments VERBATIM, working folder). The arguments are the bookmark's own string, handed on
//            unchanged, never split and re-joined here, so a quoted "a b" arrives as it was written.
//   App      (AppUserModelID) a packaged app, started through the shell.
//   Link     (url) opened through ProgramStarter the way `deskbar.cmd open` does (ruling P14), not through
//            App.OpenUrl, so the same code path serves the bar and the pipe.
public sealed record BookmarkStarts(
    Func<string, string, string?, Result<int?>> Program,
    Func<string, Result> App,
    Func<string, Result> Link);

// OPENING A BOOKMARK, and "Open all". Everything here runs only from an explicit call: switching to a workspace
// opens nothing ("no launching" stands; a bookmark is a button, never an autostart).
//
// The bar and the control pipe both ask this class, so Showing and IsRunning live here too and cannot drift
// between the two.
public sealed class BookmarkOpener(WorkspaceManager manager, BookmarkStarts starts, Func<IReadOnlySet<string>> openAppIds)
{
    // The bookmarks whose chips show: the ones not already running (see BookmarkRunning), in the order added.
    public IReadOnlyList<Bookmark> Showing(Workspace workspace) =>
        BookmarkRunning.Showing(workspace.Bookmarks, manager.KnownWindows, openAppIds());

    // The same answer as Showing for every workspace of one bar rebuild, from ONE reading of the windows and the open
    // app ids. The bar draws a row per workspace, and the app-id set is built afresh each time it is asked
    // (FloatingBar.OpenAppIds makes a new HashSet from its cache), so asking per row would build it once per row for
    // nothing. Take the snapshot at the top of a rebuild and ask it for each row.
    public Func<Workspace, IReadOnlyList<Bookmark>> ShowingNow()
    {
        var windows = manager.KnownWindows;
        var ids = openAppIds();
        return workspace => BookmarkRunning.Showing(workspace.Bookmarks, windows, ids);
    }

    public bool IsRunning(Bookmark bookmark) =>
        BookmarkRunning.IsRunning(bookmark, manager.KnownWindows, openAppIds());

    // One click: switch to the workspace unless it is the one in front (as TasksBoard.OpenLink does), then start.
    // A stale id (workspace or bookmark gone) is an ordinary failure, not an exception.
    public Result Open(Guid workspaceId, Guid bookmarkId) =>
        manager.BookmarkOf(workspaceId, bookmarkId).ToResult("That bookmark is gone.")
            .Bind(bookmark => SwitchTo(workspaceId, bookmark).Bind(() => Start(workspaceId, bookmark)));

    // Opens every bookmark that is showing (not running), in order, switching once.
    //
    // PARTIAL FAILURE (ruling P9): a bookmark that fails does NOT stop the ones after it, because a dead link must
    // not keep the rest of the row from opening. Every failure is collected, each already named by its bookmark,
    // and reported together; on any failure the result is a failure whose message lists them all. On success the
    // answer is what was opened.
    public Result<IReadOnlyList<Bookmark>> OpenAll(Guid workspaceId) =>
        manager.State.Workspaces.TryFirst(w => w.Id == workspaceId).ToResult("That workspace is gone.").Bind(workspace =>
        {
            var showing = Showing(workspace);
            // Nothing to open means nothing to switch for.
            var switched = showing.Count == 0 || IsCurrent(workspaceId) ? Result.Success() : manager.Switch(workspaceId);
            if (switched.IsFailure) return Result.Failure<IReadOnlyList<Bookmark>>(switched.Error);

            var outcomes = showing.Select(b => (Bookmark: b, Outcome: Start(workspaceId, b))).ToList();
            var failures = outcomes.Where(o => o.Outcome.IsFailure).Select(o => o.Outcome.Error).ToList();
            return failures.Count > 0
                ? Result.Failure<IReadOnlyList<Bookmark>>(string.Join(Environment.NewLine, failures))
                : Result.Success<IReadOnlyList<Bookmark>>(outcomes.Select(o => o.Bookmark).ToList());
        });

    bool IsCurrent(Guid workspaceId) =>
        manager.CurrentWorkspaceId.Map(current => current == workspaceId).GetValueOrDefault(false);

    Result SwitchTo(Guid workspaceId, Bookmark bookmark) =>
        IsCurrent(workspaceId) ? Result.Success() : manager.Switch(workspaceId).MapError(e => Named(bookmark, e));

    // A program goes through StartIn, so its window is expected for the workspace and placed there even when the
    // start is handed to a copy already running; an app or a link has no window of its own to expect (an app's
    // belongs to ApplicationFrameHost, a link's to the browser), so it relies on the switch just made.
    Result Start(Guid workspaceId, Bookmark bookmark) =>
        (bookmark.Kind switch
        {
            BookmarkKind.Program => manager
                .StartIn(workspaceId,
                    new LaunchTarget(new ProgramLaunch(bookmark.Target, [], bookmark.WorkingDirectory), bookmark.Target),
                    _ => starts.Program(bookmark.Target, bookmark.Arguments ?? "", bookmark.WorkingDirectory))
                .Bind(_ => Result.Success()),
            BookmarkKind.App => starts.App(bookmark.Target),
            _ => starts.Link(bookmark.Target),
        }).MapError(e => Named(bookmark, e));

    static string Named(Bookmark bookmark, string error) => $"{bookmark.Name}: {error}";
}
