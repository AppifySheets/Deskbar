using System.Windows;
using System.Windows.Controls;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// A TASK'S OWN WINDOW IN THE INBOX LOOK, THE PAGE'S HALF (the task window's page, TaskDetailWindow.Inbox.cs). While the
// Inbox is the chosen view, a task window shows the very page the panel shows for the task: this TasksWindow is made for
// it, never shown, and used only to draw that page. The task window takes its page border (TakePageHost) and puts it in
// its own tree, so what is on screen is the panel's drawing, with the panel's own clicks (every one calls what it calls
// in the panel: the board decides, the page draws), and nothing of the panel's state is shared with it.
//
// What differs from a page in the panel is only what a window has no use for, and all of it is switched by one flag:
//   * no back arrow, and Esc goes nowhere: there is no list behind it (PageTop, InboxBack);
//   * a subtask opens in a window of its own, as it always did in a task window (OpensInOwnWindow);
//   * the thread shows every comment, whole (ShowPage), where the panel's page shows the latest three, each cut short;
//   * a refused close can be undone from the page, and Important and Urgent can be toggled (WindowStateRow).
// A page in the panel never sets the flag, so it draws and behaves exactly as before.
public partial class TasksWindow
{
    // Whether this is a task window's page (ShowPage sets it, once, and nothing clears it: the window is made for this).
    bool pageInWindow;

    /// <summary>For the tests: whether this window is only the renderer of a task window's page.</summary>
    internal bool PageInWindow => pageInWindow;

    /// <summary>
    /// Draws the task's page from a view of that one task (TasksBoard.WithInboxPage), and again on every change. The page is
    /// the task's, whatever else the view says; its thread is whole: the earlier comments unfolded and every comment in full,
    /// as a task window always showed it (the panel's page shows the latest three, each cut to three lines).
    /// </summary>
    internal void ShowPage(TasksView view, Guid id)
    {
        pageInWindow = true;
        inboxPage = id;
        if (view.Groups.SelectMany(g => g.Rows).FirstOrDefault(r => r.Id == id) is { } row)
        {
            earlierOpen.Add(id);
            row.Comments.ToList().ForEach(c => longOpen.Add(c.Id));
        }
        Present(view);
    }

    /// <summary>
    /// The page's border, taken out of this window's tree to be put in the task window's. The page draws into it, whoever
    /// holds it, and lays itself out on it (DrawInboxPage), so it works wherever it stands.
    /// </summary>
    internal Border TakePageHost()
    {
        if (InboxPage.Parent is Panel parent) parent.Children.Remove(InboxPage);
        return InboxPage;
    }

    // A subtask's line or entry in the window's page: its own window, as the old task window's subtasks always opened.
    bool OpensInOwnWindow(Guid id)
    {
        Actions?.OpenWindow?.Invoke(id);
        return true;
    }
}
