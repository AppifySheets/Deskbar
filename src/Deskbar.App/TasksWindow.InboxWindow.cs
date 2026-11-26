using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
//   * a close can be undone from the page while its six seconds run, and Important and Urgent can be toggled, as the old
//     task window's chips did (WindowStateRow);
//   * "Open in window" is not on the "⋯" menu (it is this window), and "Show in the tasks panel" is (ShapeWindowMenu).
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
        HideThreadFolds();
    }

    // The two folds a page's thread has for the panel's narrow column: "N earlier comments" (here open, so it would read
    // "Hide N earlier comments") and a long comment's "Show less". A window shows the whole thread, so neither is offered;
    // folded, they would only be opened again by the next update. Found by their names, which the thread gives them.
    void HideThreadFolds()
    {
        if (InboxPage.Child is not { } content) return;
        Descendants(content).OfType<Button>().Where(b => AutomationProperties.GetName(b) is "Earlier comments" or "Show less").ToList()
            .ForEach(b => b.Visibility = Visibility.Collapsed);
    }

    static IEnumerable<DependencyObject> Descendants(DependencyObject node) =>
        LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>().SelectMany(child => Descendants(child).Prepend(child));

    /// <summary>
    /// The page's border, taken out of this window's tree to be put in the task window's. The page draws into it, whoever
    /// holds it, and lays itself out on it (DrawInboxPage), so it works wherever it stands.
    /// </summary>
    internal Border TakePageHost()
    {
        if (InboxPage.Parent is Panel parent) parent.Children.Remove(InboxPage);
        return InboxPage;
    }

    /// <summary>What a comment typed before the page took over goes on from: the box's text, as a redraw would restore it.</summary>
    internal void SeedDraft(Guid id, string text) => drafts[id] = text;

    /// <summary>A release of the mouse on the page: what a press held back is drawn (the panel's window does this itself).</summary>
    internal void FlushHeld() => Flush();

    /// <summary>
    /// A key in the task window while its page shows, whether the page took it: Esc undoes the innermost thing (an editor, a
    /// comment edit, the subtask box) and goes nowhere else, F2 edits the page's task. Ctrl+F is not the page's: no search
    /// box is on it.
    /// </summary>
    internal bool PressInWindow(Key key, ModifierKeys modifiers) =>
        (key == Key.Escape && Escape()) || (key == Key.F2 && EditSelected());

    /// <summary>The board's own "Show in the tasks panel" (the page's copy of the actions sends ShowTask to a window).</summary>
    internal Action<Guid>? ShowInPanel { get; set; }

    internal const string UndoText = "Undo";
    internal const string ImportantText = "Important";
    internal const string UrgentText = "Urgent";
    internal const string ShowInPanelText = "Show in the tasks panel";

    /// <summary>The window menu's "Go to its workspace" for a task without a session (the old task window had it).</summary>
    internal const string GoToWorkspaceText = "Go to its workspace";

    // WHAT THE OLD TASK WINDOW'S HEADER DID AND THE PAGE DOES NOT: Undo while a close can still be undone (the page is not
    // left when its task closes, as the panel's is: the window shows the task until it is gone), and the Important and Urgent
    // toggles. After the state's words and the pull requests, in the page's second row (PageTop). Each calls what the old
    // window's chip called (Actions.Undo, Actions.SetFlag). A toggle says its state in words and a mark, not in colour alone.
    IEnumerable<UIElement> WindowStateRow(TaskRowView row)
    {
        if (row.Closing)
        {
            var undo = InboxButton(Text(UndoText, 12, InboxLook.Ink, FontWeights.SemiBold), UndoText, () => Actions?.Undo(row.Id),
                new Thickness(10, 2, 10, 2), InboxLook.Raised, 12, InboxLook.StrongLine, DoubleClickIgnored);
            undo.ToolTip = "Undo the close";
            undo.Margin = new Thickness(0, 0, 8, 4);
            yield return undo;
        }
        foreach (var (flag, name, on) in new[] { (TaskFlag.Important, ImportantText, row.Important), (TaskFlag.Urgent, UrgentText, row.Urgent) })
        {
            var face = new StackPanel { Orientation = Orientation.Horizontal };
            if (on) face.Children.Add(InboxMarks.Icon("checked", 12, InboxLook.Ink, InboxLook.Raised));
            face.Children.Add(Text(name, 12, on ? InboxLook.Ink : InboxLook.Ink3, on ? FontWeights.SemiBold : FontWeights.Normal,
                new Thickness(on ? 4 : 0, 0, 0, 0)));
            var toggle = InboxButton(face, name, () => Actions?.SetFlag(row.Id, flag, !on), new Thickness(10, 2, 10, 2),
                on ? InboxLook.Raised : Brushes.Transparent, 12, InboxLook.StrongLine, ClickIgnored);
            toggle.ToolTip = on ? $"Clear {name.ToLowerInvariant()}" : $"Mark {name.ToLowerInvariant()}";
            toggle.Margin = new Thickness(0, 0, 6, 4);
            yield return toggle;
        }
    }

    // The "⋯" menu in the window (OpenMenuFor, just before it opens): no "Open in window", the page is that; and the old
    // window's "Show in the tasks panel", before the delete at the end. Does nothing for a page in the panel.
    void ShapeWindowMenu(ContextMenu menu, TaskRowView row)
    {
        if (!pageInWindow) return;
        menu.Items.OfType<MenuItem>().Where(item => item.Header is TextBlock { Text: OpenInWindowText }).ToList()
            .ForEach(item => menu.Items.Remove(item));
        // "Go to its workspace" (tester finding L1): the old window's menu had it for an open task. A task with a session has
        // Open Claude, which goes to the workspace first and then to the session; a note has no such button, so this is the
        // only way from its window to its workspace. Run once the menu has closed, as Open Claude is.
        if (row.Status != TaskStatus.Closed && !row.Closing && !OpensClaude(row) && Actions?.Go is { } go)
            menu.Items.Insert(Math.Max(0, menu.Items.Count - 2), Item(GoToWorkspaceText,
                () => Dispatcher.BeginInvoke(() => go(row.Id), System.Windows.Threading.DispatcherPriority.Input)));
        if (ShowInPanel is { } show)
            menu.Items.Insert(Math.Max(0, menu.Items.Count - 2), Item(ShowInPanelText, () => show(row.Id)));
    }

    // A subtask's line or entry in the window's page: its own window, as the old task window's subtasks always opened.
    bool OpensInOwnWindow(Guid id)
    {
        Actions?.OpenWindow?.Invoke(id);
        return true;
    }
}
