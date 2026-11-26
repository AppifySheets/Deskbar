using System.Windows;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// AN INBOX TASK'S PAGE (Deskbar task 740a2231, slice 1; spec "The page"). Not drawn yet: a line's click opens the task on
// the board (picked, its thread open) and the page that shows it comes in the next step.
public partial class TasksWindow
{
    // The task whose page is open, none for the list.
    Guid? inboxPage;

    /// <summary>For the tests: the task whose page is open, if any.</summary>
    internal Guid? InboxPageShown => inboxPage;

    // A line's click: the task picked and its thread open on the board (TasksBoard.OpenPage), and its page open here.
    void OpenInboxPage(Guid id)
    {
        inboxPage = id;
        if (Actions?.OpenPage is { } open) open(id);
        else Actions?.Pick(id);
    }

    void LeaveInboxPage()
    {
        inboxPage = null;
        InboxPage.Visibility = Visibility.Collapsed;
    }

    string InboxPageSignature(TasksView view) => "";

    void DrawInboxPage(TasksView view) => Scroller.Visibility = Visibility.Visible;
}
