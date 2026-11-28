using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// THE MANAGER IN ITS OWN DOCKED PANEL (task 71aae045). The owner, the morning the manager's page landed in the Inbox
// (task 6c4c067f): "open the manager session separately from the main tasks, as a separate docked panel, as it was done
// before in the terminal version". So the page keeps what it gained (the chat drawn like a task's, a box to message the
// manager) but leaves the tasks panel: it is drawn by a second TasksWindow made with ManagerOnly, on a strip of its own.
//
// WHERE IT DOCKS. Exactly where the manager's terminal docked (ManagerDock, task f171bca6): the App's TasksPanel for it
// re-registers after every registration of the bar's and of the tasks panel's (the same barStrip.Merge(panel.Registered)
// the terminal's strip follows), so the order on the edge is bar, tasks panel, manager. With the tasks panel closed it sits
// just inside the bar. Its width is the terminal dock's (AppState.ManagerDockWidth, 640 DIP until dragged), so the two never
// disagree about how wide "the manager" is, and its grip saves it the same way.
//
// WHAT OPENS AND CLOSES IT. The Manager buttons and the violet manager line (App.OpenManagerPage) open it, or bring it
// forward with the keyboard in its box when it is already open. Its × and Alt+F4 close it. The Claude button left of its box
// closes it too and opens the terminal, which docks in the same slot as it always did: one manager column, not two.
// Whether it is open is saved (AppState.ManagerDockOpen) and a start reopens it without taking the keyboard, as the tasks
// panel is (Restore).
//
// ONE BOARD, TWO WINDOWS. The docked window shares the board's TasksWindowActions, so the transcript poll (ChatSourceOf
// answers ManagerSession.PageId from today's manager), the user's page messages and the send are the ones the Inbox page
// used. Every Refresh presents to it too; it draws only when its page's own signature changed (ManagerOnlySignature).
public sealed partial class TasksBoard
{
    TasksWindow? managerWindow;
    TasksPanel? managerPanel;

    /// <summary>
    /// Gives the board the manager's docked window and the strip that holds it (the App, once, after constructing the board).
    /// From then on the Manager button opens the manager there instead of on a page in the tasks panel's Inbox.
    /// </summary>
    internal void DockManager(TasksWindow window, TasksPanel strip)
    {
        managerWindow = window;
        managerPanel = strip;
        window.Actions = actions;
        window.CloseManagerDock = HideManager;
        window.ResizeManagerDock = ResizeManager;
        // The grip goes on the edge facing the windows, as the tasks panel's does (TasksPanel.Edge, at once and on each change).
        managerSubscriptions.Add(strip.Edge.Subscribe(window.SetInnerEdge));
    }

    readonly System.Reactive.Disposables.CompositeDisposable managerSubscriptions = new();

    /// <summary>Whether the manager's docked panel is open (the user's word, from ShowManager to HideManager).</summary>
    internal bool ManagerDockOpen => managerPanel?.IsShown ?? false;

    /// <summary>For the tests: the manager's docked window, when the board has one.</summary>
    internal TasksWindow? ManagerWindow => managerWindow;

    // The width the docked manager is held at: the user's last drag of either manager dock, else the default.
    double ManagerWidth => manager.State.ManagerDockWidth ?? ManagerDockLayout.DefaultWidth;

    /// <summary>
    /// The Manager buttons and the manager line: the manager's docked panel opens (or, open already, comes forward), its page
    /// drawn and the keyboard in its message box. A board with no dock (the tests of the Inbox page) falls back to the page
    /// over the tasks panel's Inbox (ShowManagerInInbox).
    /// </summary>
    public void ShowManager()
    {
        if (shutDown) return;
        if (managerWindow is not { } shown || managerPanel is not { } strip)
        {
            ShowManagerInInbox();
            return;
        }
        if (!strip.IsShown) OpenManagerDock(shown, strip);
        else shown.Present(lastView ??= View());
        ManagerArrived(shown, strip);
    }

    /// <summary>
    /// The start-up reopen of a manager panel left open (AppState.ManagerDockOpen): drawn and docked, never brought forward,
    /// so a start at sign-in takes the keyboard from nothing (the tasks panel's Restore, for the same reason).
    /// </summary>
    public void RestoreManager()
    {
        if (managerWindow is { } shown && managerPanel is { IsShown: false } strip) OpenManagerDock(shown, strip);
    }

    /// <summary>The docked manager's × and Alt+F4, and its Claude button before it opens the terminal: the strip goes back.</summary>
    public void HideManager()
    {
        // Never once the app is going: the state Shutdown saved is the one the next start reads.
        if (shutDown || managerWindow is not { } shown || managerPanel is not { } strip) return;
        strip.Hide();
        shown.Dismiss();
        _ = manager.SaveManagerDockOpen(false);
    }

    void OpenManagerDock(TasksWindow shown, TasksPanel strip)
    {
        shown.Open();
        shown.ShowProblem(null);
        shown.Present(lastView ??= View());
        strip.Show(ManagerWidth);
        _ = manager.SaveManagerDockOpen(true);
    }

    // The docked manager's grip: the width saved (clamped as the terminal dock clamps it), then the strip re-docked at it.
    void ResizeManager(double widthDips)
    {
        var width = ManagerDockLayout.Clamp(widthDips, double.MaxValue);
        _ = manager.SaveManagerDockWidth(width);
        managerPanel?.SetWidth(width);
    }

    // Brought to the user as the tasks panel is (Arrived): raised and activated through the App's seam, never over a
    // full-screen window, and the keyboard into the box only once it is the active window (focusing an inactive one would
    // activate it from under whatever is in front). The same ComeForward the tasks panel uses (TasksBoard.Arrival.cs),
    // which tries again for a moment while the shell has not yet placed the just-shown window on a desktop: on the first
    // open after a start it had not, every time, and the box never got the keyboard (task b62c4472).
    void ManagerArrived(TasksWindow shown, TasksPanel strip) => ComeForward(shown, strip);

    // Each board redraw reaches the docked manager too (Refresh); it draws only when its page's own signature changed. It
    // ignores the view's rows (review finding: building a whole board for it on every pulse, with the tasks panel closed,
    // cost a full grouping for nothing), so it gets the last view the tasks panel drew, and a fresh one only before the
    // first. The window needs a view at all only so its own redraws (RedrawInbox) have one to present again.
    TasksView? lastView;

    void RefreshManagerDock()
    {
        if (managerWindow is { IsOpen: true } shown) shown.Present(lastView ??= View());
    }

    // A result of the docked manager's own actions (its box, its Claude button): reported as every action is, and its
    // refusal also on the docked window's problem line (review finding), since the tasks panel may be closed or showing
    // something else, and the user is looking at the manager. A success clears that line.
    Result ReportManager(Result result)
    {
        Report(result);
        managerWindow?.ShowProblem(result.IsFailure ? result.Error : null);
        return result;
    }

    // The task named as "the current task" in a message sent from the docked manager: the page open in the tasks panel's
    // Inbox as the message goes (OpenInboxPage). The Inbox page's own sends name the page it was opened over instead.
    Guid? CurrentTaskForManager(Guid? from) => from ?? (OpenInboxPage.HasValue ? OpenInboxPage.Value : null);

    // The app is going: the docked window closes for good after the state says whether it was open (RestoreManager reads it).
    void ShutdownManagerDock()
    {
        managerSubscriptions.Dispose();
        if (managerWindow is not { } shown || managerPanel is not { } strip) return;
        _ = manager.SaveManagerDockOpen(strip.IsShown);
        strip.Shutdown();
        shown.CloseForGood();
    }
}
