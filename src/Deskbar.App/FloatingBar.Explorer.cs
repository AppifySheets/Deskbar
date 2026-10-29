using CSharpFunctionalExtensions;
using Deskbar.Windows.Processes;
using Deskbar.Windows.Shell;

namespace Deskbar.App;

// THE BACKGROUND MENU'S "Restart Explorer". Petre: "add restart explorer in the right click menu of the bar", "which
// restarts the taskbar", then "i want to restart explorer as well", "so that the broken taskbar gets fixed", and of
// the folder windows that close with it: reopened in their workspaces, from four choices.
//
// Three steps, and the order is the point:
//
//   1. On the UI thread, read every open folder (ExplorerFolders, which needs an STA thread) and the workspace its
//      window is in. Afterwards the windows are gone and nothing remembers either.
//   2. On a pool thread, restart Explorer (ExplorerRestart). It can take several seconds and the bar must keep
//      drawing meanwhile. The bar answers TaskbarCreated by itself when the taskbar comes back (see the app bar's
//      registration), so nothing here has to wait for that.
//   3. Still on the pool thread, open each folder again, ONE AT A TIME, each with an expectation for its workspace,
//      the way `deskbar.cmd start` places a window (WorkspaceManager.StartedFor). explorer.exe hands the folder to
//      the Explorer already running and exits, so the window can only be matched by NAME: the first new explorer
//      window answers whichever expectation is waiting. Starting the next folder only once the last one's window
//      has answered (or the wait has run out) is what keeps each window going to its own workspace. Folders whose
//      window was in no workspace are opened last, with no expectation, so they cannot take a waiting one.
//
// A folder whose window never answers within the wait stays on the desktop in front: about one new window in nine
// reaches Deskbar only through the 5s repair sweep, which places nothing (WorkspaceManager.RepairWindowList). The
// trace names every folder and what happened to it.
//
// The outcome goes to the trace, and a failed restart is also shown, since an Explorer that was shut down and did not
// come back is a missing taskbar the user has to fix. Replaced in the tests, which must never restart the shell of
// whoever runs them.
public partial class FloatingBar
{
    // How long a reopened folder's window has to appear and be claimed before the next folder is started. Explorer
    // opens a folder in well under a second when it is warm; just after a restart it is cold, hence the margin.
    static readonly TimeSpan FolderClaimedWithin = TimeSpan.FromSeconds(10);
    static readonly TimeSpan FolderPoll = TimeSpan.FromMilliseconds(100);

    internal Action RestartExplorer { get; init; }

    void RestartExplorerOffThread()
    {
        var folders = ExplorerFolders.Read()
            .Select(f => (f.Folder, Workspace: manager.WorkspaceOf(f.Window).Map(w => w.Id)))
            .OrderBy(f => f.Workspace.HasNoValue)
            .ToList();
        ClickTrace.Write($"restart explorer: {folders.Count} folder(s) to reopen");

        Task.Run(() => ExplorerRestart.Restart()
                .Map(how => $"{how}; {string.Join("; ", folders.Select(f => Reopen(f.Folder, f.Workspace)))}"))
            .ContinueWith(done => Dispatcher.InvokeAsync(() =>
            {
                var outcome = done.Result;
                ClickTrace.Write($"restart explorer: {(outcome.IsSuccess ? outcome.Value : outcome.Error)}");
                Report(outcome.IsSuccess ? Result.Success() : Result.Failure(outcome.Error));
            }), TaskScheduler.Default);
    }

    // One folder, on the pool thread. The manager is the UI thread's, so every question to it goes through the
    // dispatcher. Answers a line for the trace.
    string Reopen(string folder, Maybe<Guid> workspace)
    {
        var expectation = workspace.Map(id => Dispatcher.Invoke(() => manager.ExpectWindow("explorer", id)));
        var started = DetachedLauncher.Start("explorer.exe", [folder], null);
        if (started.IsFailure)
        {
            expectation.Execute(id => Dispatcher.Invoke(() => manager.CancelExpectation(id)));
            return $"{folder}: not reopened ({started.Error})";
        }
        if (expectation.HasNoValue) return $"{folder}: reopened where you are (it was in no workspace)";

        var until = DateTime.UtcNow + FolderClaimedWithin;
        while (DateTime.UtcNow < until && Dispatcher.Invoke(() => manager.Awaits(expectation.Value)))
            Thread.Sleep(FolderPoll);
        var claimed = !Dispatcher.Invoke(() => manager.Awaits(expectation.Value));
        // Cancelled either way: claimed, it is spent already; unclaimed, it must not take some later window.
        Dispatcher.Invoke(() => manager.CancelExpectation(expectation.Value));
        var name = Dispatcher.Invoke(() => manager.State.Workspaces.FirstOrDefault(w => w.Id == workspace.Value)?.Name ?? "?");
        return claimed
            ? $"{folder}: back in {name}"
            : $"{folder}: reopened, but its window did not reach Deskbar within {FolderClaimedWithin.TotalSeconds:0}s, so it stays where it opened";
    }
}
