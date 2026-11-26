using System.Reactive.Disposables;
using System.Reactive.Linq;
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
//
// OPENED LINKS (option B, Petre's pick after the first live try): Deskbar sees windows, not tabs, so a Link chip never
// hides. Instead, when a link is opened, `linkWindows.Watch` reports the browser window it went into and OpenedLinks
// notes it; while that window lives the link counts as running, and the bar draws its chip OPEN (full strength, solid
// outline) instead of at rest. Closing only the tab is not noticed. Without `linkWindows` (tests, and any copy that
// cannot see windows) a link never counts as running, as before.
public sealed class BookmarkOpener
{
    readonly WorkspaceManager manager;
    readonly BookmarkStarts starts;
    readonly Func<IReadOnlySet<string>> openAppIds;
    readonly LinkWindows? linkWindows;
    readonly Func<WindowHandle, string?> windowAppId;

    // `windowAppId` is the shell's AppUserModelID of one window (null when it has none), asked only to recognise the
    // window of an App bookmark that remembers a screen (Start). Without it (tests) only a packaged exe is recognised.
    public BookmarkOpener(WorkspaceManager manager, BookmarkStarts starts, Func<IReadOnlySet<string>> openAppIds,
        LinkWindows? linkWindows = null, Func<WindowHandle, string?>? windowAppId = null)
    {
        this.manager = manager;
        this.starts = starts;
        this.openAppIds = openAppIds;
        this.linkWindows = linkWindows;
        this.windowAppId = windowAppId ?? (_ => null);

        // Both subscriptions live as long as the opener, which the app makes once and keeps for its whole run.
        // A window gone for real takes its notes with it.
        linkWindows?.Gone.Subscribe(OpenedLinks.Forget);
        // A removed bookmark (by its chip menu, the pipe, or with its workspace) takes its note too. The manager pulses
        // StateChanged on every persisted change, so this is the one place that sees every way a bookmark can go. Only
        // the noted ids are looked at, a handful at most.
        manager.StateChanged.Subscribe(_ =>
        {
            var existing = manager.State.Workspaces.SelectMany(w => w.Bookmarks).Select(b => b.Id).ToHashSet();
            OpenedLinks.Noted.Where(id => !existing.Contains(id)).ToList().ForEach(OpenedLinks.Forget);
        });
    }

    // Which browser window each opened link went into. The bar redraws on its Changed.
    public OpenedLinks OpenedLinks { get; } = new();

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

    // What `bookmark list` reports as `running`. For a program or an app, the answer that hides its chip; for a link,
    // whether it is open (IsLinkOpen), the answer that draws its chip open. A link's chip shows either way.
    public bool IsRunning(Bookmark bookmark) =>
        bookmark.Kind == BookmarkKind.Link
            ? IsLinkOpen(bookmark)
            : BookmarkRunning.IsRunning(bookmark, manager.KnownWindows, openAppIds());

    // A link whose browser window, noted when it was opened, still lives. False for anything else.
    public bool IsLinkOpen(Bookmark bookmark) =>
        bookmark.Kind == BookmarkKind.Link && linkWindows is { } windows && OpenedLinks.IsOpen(bookmark.Id, windows.Alive);

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
    // start is handed to a copy already running; an app or a link has no window of its own to expect by name (an app's
    // belongs to ApplicationFrameHost, a link's to the browser), so it relies on the switch just made.
    //
    // A bookmark that REMEMBERS A SCREEN (Bookmark.Screen, the second bookmarks PR) puts its window there: a program's
    // through the same expectation, which carries the screen; an app's through an expectation of its own that knows the
    // window by its app id or package (BookmarkRunning.IsAppWindow), registered before the start and withdrawn if the
    // start fails, as StartIn does. A link never has a screen (BookmarkRules.Check). Only a start asked for here is
    // placed: nothing moves a window that was already open.
    Result Start(Guid workspaceId, Bookmark bookmark) =>
        (bookmark.Kind switch
        {
            BookmarkKind.Program => manager
                .StartIn(workspaceId,
                    new LaunchTarget(new ProgramLaunch(bookmark.Target, [], bookmark.WorkingDirectory), bookmark.Target),
                    _ => starts.Program(bookmark.Target, bookmark.Arguments ?? "", bookmark.WorkingDirectory),
                    bookmark.Screen)
                .Bind(_ => Result.Success()),
            BookmarkKind.App => StartApp(workspaceId, bookmark),
            _ => StartLink(workspaceId, bookmark),
        }).MapError(e => Named(bookmark, e));

    Result StartApp(Guid workspaceId, Bookmark bookmark)
    {
        var expectation = bookmark.Screen is { } screen
            ? manager.ExpectAppWindow(bookmark.Name, window => BookmarkRunning.IsAppWindow(bookmark.Target, window, windowAppId(window.Handle)),
                workspaceId, screen)
            : (Guid?)null;
        return starts.App(bookmark.Target).TapError(_ =>
        {
            if (expectation is { } id) manager.CancelExpectation(id);
        });
    }

    // The watch begins BEFORE the link is handed to the browser, not after: the start goes through the shell, which can
    // pump this thread's messages, and the browser's foreground change could otherwise be delivered before anyone
    // listens. A start that fails ends the watch at once, so nothing is noted for a link that never opened.
    Result StartLink(Guid workspaceId, Bookmark bookmark)
    {
        var watch = WatchFor(workspaceId, bookmark);
        return starts.Link(bookmark.Target).TapError(_ => watch.Dispose());
    }

    // Notes the window the link went into, if the watch finds one and the bookmark still exists by then (it can be
    // removed within the wait). A workspace with no desktop has nowhere to look. A watch that fails is a watch that
    // found nothing: the error handler keeps an exception from reaching the subscription.
    IDisposable WatchFor(Guid workspaceId, Bookmark bookmark) =>
        linkWindows is { } windows && DesktopOf(workspaceId) is { HasValue: true } desktop
            ? windows.Watch(desktop.Value)
                .Where(_ => manager.BookmarkOf(workspaceId, bookmark.Id).HasValue)
                .Subscribe(window => OpenedLinks.Note(bookmark.Id, window), _ => { })
            : Disposable.Empty;

    Maybe<Guid> DesktopOf(Guid workspaceId) =>
        manager.State.Workspaces.TryFirst(w => w.Id == workspaceId)
            .Bind(w => w.DesktopId is { } desktop ? Maybe.From(desktop) : Maybe<Guid>.None);

    static string Named(Bookmark bookmark, string error) => $"{bookmark.Name}: {error}";
}
