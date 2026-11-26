using Deskbar.Core.Tasks;

namespace Deskbar.App;

// THE INBOX VIEW, THE WINDOW'S HALF (Deskbar task 740a2231, slice 1). Drawn from the view's Inbox (TasksBoard.Inbox.cs),
// which the board fills only while the Inbox shows.
public partial class TasksWindow
{
    /// <summary>For the tests: the Inbox's lines as last handed over, null while another view shows.</summary>
    internal InboxView? InboxShown => latest?.Inbox;
}
