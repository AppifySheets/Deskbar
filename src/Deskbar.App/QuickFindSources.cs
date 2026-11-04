using System.IO;
using System.Windows.Media;
using CSharpFunctionalExtensions;
using Deskbar.Core;
using Deskbar.Core.Abstractions;
using Deskbar.Core.Domain;
using Deskbar.Core.QuickFind;
using Deskbar.Windows.Control;

namespace Deskbar.App;

// Where quick find's facts come from in the running app, handed to QuickFind as functions by the composition
// root. Kept out of App.xaml.cs, which is long enough.
public static class QuickFindSources
{
    // Every open window and tab, each placed the way the BAR places it: the bar's own overview sorts windows
    // into workspaces, unnamed desktops ("Desktop 1") and the pinned row, so quick find and the bar never
    // disagree about where something is. A window borrowed by a nested workspace still belongs to the
    // workspace it came from (WorkspaceOf), which is where Enter takes Petre.
    public static IReadOnlyList<FindResult> Open(WorkspaceManager manager, IBrowserTabs? tabs) =>
        manager.WindowsByWorkspace().Map(overview =>
        {
            var state = manager.State;
            var listed = overview.Workspaces
                .SelectMany(g => g.Running.Select(r => (Row: r, Place: Place(state, g.Workspace))))
                .Concat(overview.OtherDesktops.SelectMany(d => d.Windows.Select(r => (Row: r, Place: new FindPlace(null, d.Name)))))
                .Concat(overview.Pinned.Select(r => (Row: r, Place: new FindPlace(null, "all desktops"))))
                .ToList();

            // A window listed more than once is one a nested workspace borrowed (#42): ask which workspace it
            // really belongs to. Only those, since WorkspaceOf costs COM calls and this runs every second while
            // the box is open (final review, finding 4).
            var borrowed = listed.GroupBy(x => x.Row.Window.Handle).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
            var rows = listed
                .Select(x => borrowed.Contains(x.Row.Window.Handle)
                    ? x with { Place = manager.WorkspaceOf(x.Row.Window.Handle).Map(w => Place(state, w)).GetValueOrDefault(x.Place) }
                    : x)
                .DistinctBy(x => x.Row.Window.Handle)
                .ToList();
            var places = rows.ToDictionary(x => x.Row.Window.Handle, x => x.Place);
            var originals = rows.ToDictionary(x => x.Row.Window.Handle, x => x.Row.OriginalTitle);
            return OpenCatalog.Of(
                rows.Select(x => x.Row.Window).ToList(),
                handle => places[handle],
                handle => tabs?.NamesOf(handle) ?? [],
                BrowserTabs.IsBrowser,
                handle => originals[handle]);
        }).GetValueOrDefault([]);

    // The header colour of a place: its workspace's lane colour, or none for an unnamed desktop or the pinned
    // row, which the bar draws without one either.
    public static string? ColourOf(WorkspaceManager manager, FindPlace place) =>
        place.WorkspaceId is { } id && manager.State.Workspaces.TryFirst(w => w.Id == id) is { HasValue: true } found
            ? LaneColour.Of(manager.State, found.Value)
            : null;

    // The icon the bar would draw: by window when there is one, so a PWA shows its own and not the browser's.
    public static ImageSource? IconOf(FindResult result) =>
        result.Window is { } window ? IconCache.For(window, result.IconPath) : IconCache.For(result.IconPath);

    // Enter on an open result: go to its window as clicking its icon on the bar does (JumpTo switches only when
    // the window is on another desktop). A window that has closed fails here, and QuickFind drops its row.
    //
    // For a TAB, the tab is selected as well, on the tab reader's pool thread, started before the jump so it is
    // usually done by the time the window is in front (selection takes 23 to 36 ms, and works on another
    // desktop and while minimised, per the probes). A failed selection leaves Petre on the window, on its old
    // tab, and goes to the trace; the jump itself already succeeded, so it is not reported as a failure.
    public static Result Go(WorkspaceManager manager, IWindowActivator activator, IBrowserTabs? tabs, FindResult result)
    {
        if (result.Launch is not null) return Launch(manager, result);
        if (result.Window is not { } window) return Result.Failure("Nothing to go to.");
        if (tabs is not null && TabToSelect(result, h => manager.KnownWindows.FirstOrDefault(w => w.Handle == h)?.ProcessName) is { } tab)
            tabs.Select(tab.Window, tab.Code, tab.Index, tab.Title)
                .ContinueWith(t => t.Result.TapError(error => ClickTrace.Write($"quick find: {error}")), TaskScheduler.Default);
        return manager.JumpTo(window, activator);
    }

    // What to select for a row: nothing for a window, the listed tab for a tab, by VS Code's rule when the
    // window belongs to VS Code (its editor tabs are found by class, not by the browser rule).
    internal static (WindowHandle Window, bool Code, int Index, string Title)? TabToSelect(FindResult result, Func<WindowHandle, string?> processOf) =>
        result is { Kind: FindKind.Tab, Window: { } window, TabIndex: { } index }
            ? (window, string.Equals(processOf(window), "Code", StringComparison.OrdinalIgnoreCase), index, result.Title)
            : null;

    public static IReadOnlyList<Guid> Recent(WorkspaceManager manager) =>
        manager.ByRecentUse().Ordered.Select(w => w.Id).ToList();

    // EVERYTHING QUICK FIND COULD START (pull request C of the plan): the roster and the Start menu, minus
    // whatever is running, each with the workspace it would open in. LaunchCatalog decides the rows;
    // this hands it the app's facts.
    public static IReadOnlyList<FindResult> Launchable(WorkspaceManager manager, IReadOnlyList<StartMenuApp> startMenu, Func<string, bool> pathExists) =>
        LaunchCatalog.Of(
            manager.State.Inventory.Values.SelectMany(entries => entries).ToList(),
            startMenu,
            manager.KnownWindows,
            pathExists,
            (program, commandLine) => Destination(manager, manager.LaunchDestination(program, commandLine)));

    // The workspace Petre is on, or null on an unnamed desktop.
    static Guid? Current(WorkspaceManager manager) =>
        manager.CurrentWorkspaceId.HasValue ? manager.CurrentWorkspaceId.Value : null;

    // The workspace a start lands in: the one placement memory or a rule names, else the one Petre is on. On an
    // unnamed desktop there is no workspace to name, and the row says it opens here.
    static FindPlace Destination(WorkspaceManager manager, Guid? decided) =>
        (decided ?? Current(manager)) is { } id && manager.State.Workspaces.TryFirst(w => w.Id == id) is { HasValue: true } found
            ? Place(manager.State, found.Value)
            : new FindPlace(null, "this desktop");

    // Enter on a not-running row: switch to the workspace the row named, then start the program there, held to
    // it by the same expectation `deskbar.cmd start` uses (StartIn). Switching FIRST is what makes a packaged
    // app land right: its window belongs to ApplicationFrameHost, which nothing can expect, so it simply opens
    // on the desktop in front (probes, decision c). With no workspace (an unnamed desktop) it starts here.
    public static Result Launch(WorkspaceManager manager, FindResult result)
    {
        if (result.Launch is not { } target) return Result.Failure("Nothing to start.");
        var started = result.Place.WorkspaceId is { } workspace
            ? (Current(manager) == workspace ? Result.Success() : manager.Switch(workspace))
                .Bind(() => manager.StartIn(workspace, target, ProgramStarter.Start))
            : ProgramStarter.Start(target.Launch);
        return started.IsSuccess ? Result.Success() : Result.Failure(started.Error);
    }

    // Whether a roster argument names a file or folder, asked only of LOCAL rooted paths: this runs on the UI
    // thread while the box refreshes, and a path on a network share can block for seconds when the share is
    // offline. Such a path simply makes no content row.
    internal static bool LocalPathExists(string argument, Func<string, bool> exists) =>
        argument.Length > 2
        && Path.IsPathFullyQualified(argument)
        && !argument.StartsWith(@"\\", StringComparison.Ordinal)
        && !argument.StartsWith("//", StringComparison.Ordinal)
        && exists(argument);

    static FindPlace Place(Core.Persistence.AppState state, Workspace workspace) =>
        new(workspace.Id, LaneColour.FullName(state, workspace));
}
