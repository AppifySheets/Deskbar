using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Deskbar.App;

// THE TASK WINDOW IN THE INBOX LOOK (Deskbar task 740a2231 and the window of task dca4ebeb). While the Inbox is the chosen
// view of the tasks panel (TasksBoard.WithInboxPage hands the window a Page), a task window shows the very page the panel
// shows for the task: the top row (state mark, title, "⋯"), the state in words, the workspace chip and the pull requests,
// the description in full, the question in a bubble, and the whole thread with its comment box at the end. The window is
// still the same window: title bar, place, size, one per task, the tiler, closing with its task. Only what is inside it
// changes, and back when the Inbox is left.
//
// HOW. The page is drawn by a TasksWindow of its own per task window (the renderer), made for it and never shown
// (TasksWindow.InboxWindow.cs): its page border is put in this window's tree, so what is drawn there is the panel's own
// drawing with the panel's own clicks. Every click calls what it calls in the panel, through the same TasksWindowActions
// (a copy, below, that sends what would act on the panel's lists to this window's own world instead). Nothing of the
// panel's state is shared: the panel's page, its drafts and its scroll are the panel's.
//
// WHAT THE WINDOW KEEPS. The shell (place, cascade, tiling, dark title bar, one window per task, close on delete) and
// ShowProblem's text, which a refusal on no task writes. What is hidden while the page shows: the old header, the body, the
// side column and the editor; they come back, drawn once more, when the Inbox is left, and a comment typed meanwhile goes
// with the window across (a picture waiting under the box, or an editor open, does not: the editor keeps nothing, as Esc).
public partial class TaskDetailWindow
{
    // How wide the page's column is, at most, in a wide window: a page is a chat, and a chat read at 1400 DIP is a line
    // too long to follow. The window's own width stays the user's.
    internal const double InboxColumnWidth = 880;

    // The page's drawing window, and what holds its page in this window: the column (the problem line over the page) and
    // the frame around it. All null while the old look shows.
    TasksWindow? renderer;
    Border? inboxFrame;
    TextBlock? inboxProblem;

    /// <summary>The click guard's clock of the page (TasksWindow.ClickClock): a seam for the tests, which click right after a redraw.</summary>
    internal Func<DateTime> PageClickClock { get; init; } = () => DateTime.UtcNow;

    // What the window was before the page took it, to put back.
    Brush? oldBackground;

    /// <summary>For the tests: the window showing the Inbox's page (not the old window's header and body).</summary>
    internal bool ShowsInboxPage => renderer is not null;

    /// <summary>For the tests: the page's drawing window.</summary>
    internal TasksWindow? PageRenderer => renderer;

    // The first hook of Present. True: the page drew the task, and the old window has nothing to draw. False: the old
    // window draws (and, if the page was showing, has been put back first).
    bool PresentInboxPage(TaskDetailView view)
    {
        if (view.Page is not { } page)
        {
            if (renderer is not null) LeaveInboxLook();
            return false;
        }
        // The old window's own view, kept as the old window keeps it, since TaskId and the links read it.
        latest = view;
        if (renderer is null) EnterInboxLook(view);
        Title = $"{FloatingBar.FirstLine(view.Row.Title)} · Deskbar";
        renderer!.ShowPage(page, view.Row.Id);
        return true;
    }

    void EnterInboxLook(TaskDetailView view)
    {
        // An editor open in the old look keeps nothing, as Esc would (EndEdit); so does a comment edited in place.
        if (editing) EndEdit();
        if (editingComment is not null) EndEditComment();
        var shown = new TasksWindow { Quiet = true, StateDir = StateDir, OpenImage = OpenImage, Now = Now, ClickClock = PageClickClock };
        shown.Actions = Actions is { } actions ? InboxActions(actions) : null;
        shown.ShowInPanel = Actions?.ShowTask;
        // What was typed in the old comment box goes on in the page's.
        if (CommentBox.Text.Length > 0) shown.SeedDraft(view.Row.Id, CommentBox.Text);
        renderer = shown;

        var host = shown.TakePageHost();
        // A release of the mouse draws what a press held back (TasksWindow's own handler is on its window, not on the page).
        host.AddHandler(UIElement.MouseLeftButtonUpEvent, new MouseButtonEventHandler((_, _) => shown.FlushHeld()), handledEventsToo: true);
        inboxProblem = new TextBlock
        {
            Foreground = Frozen(0xFF, 0xFF, 0xA7, 0x26), FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12, 8, 12, 4), Visibility = Visibility.Collapsed,
        };
        DockPanel.SetDock(inboxProblem, Dock.Top);
        var column = new DockPanel { MaxWidth = InboxColumnWidth, HorizontalAlignment = HorizontalAlignment.Stretch };
        column.Children.Add(inboxProblem);
        column.Children.Add(host);
        inboxFrame = new Border { Background = InboxLook.Background, Child = column };
        oldBackground = Background;
        Background = InboxLook.Background;
        Header.Visibility = Visibility.Collapsed;
        Body.Visibility = Visibility.Collapsed;
        ((DockPanel)Content).Children.Add(inboxFrame);
        // The text of a refusal on no task that was already showing.
        inboxProblem.Text = ProblemText.Text;
        inboxProblem.Visibility = ProblemText.Visibility;
        // Before the first ShowPage, so it only sets what that first draw reads (task 2106492b).
        shown.ShowWindowProblem(ProblemText.Visibility == Visibility.Visible ? ProblemText.Text : null);
    }

    // The Inbox was left: the old window comes back and draws once more from the view it is handed.
    void LeaveInboxLook()
    {
        if (renderer is not { } shown) return;
        if (latest?.Row.Id is { } id && CommentBox.Text.Length == 0 && shown.CommentDraft(id) is { Length: > 0 } draft)
            CommentBox.Text = draft;
        ((DockPanel)Content).Children.Remove(inboxFrame);
        Header.Visibility = Visibility.Visible;
        Body.Visibility = Visibility.Visible;
        if (oldBackground is not null) Background = oldBackground;
        renderer = null;
        inboxFrame = null;
        inboxProblem = null;
        // The old window skips a view that is the one it drew last; it has not drawn since the page took over.
        drawn = "";
        shown.CloseForGood();
    }

    // The second hook, in Press: a key in the page's window goes to the renderer, which knows what Esc and F2 do on a page.
    // Null while the old look shows, so the old keys apply.
    bool? InboxPress(Key key, ModifierKeys modifiers) => renderer?.PressInWindow(key, modifiers);

    // The third hook, in ShowProblem: a refusal on no task shows above the page, as it shows in the old header.
    void InboxProblem(string? text)
    {
        if (inboxProblem is null) return;
        inboxProblem.Text = text ?? "";
        inboxProblem.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        // The page lists the open subtasks under a refused close (task 2106492b, TasksWindow.ShowWindowProblem).
        renderer?.ShowWindowProblem(text);
    }

    // The fourth hook, in OnClosed: the page's window goes with this one.
    void DropInboxPage()
    {
        renderer?.CloseForGood();
        renderer = null;
    }

    // The actions of the page in a window: the panel's own, but what would act on the panel's lists is sent to a window of
    // the task's own, as it always was in a task window. A subtask, a parent and a linked task open in their windows
    // (Subtask / ToggleThread / Pick are the page's subtask entries and lines; OpenParent the parent's button and menu item;
    // ShowTask the link entries). "Show in the tasks panel" keeps the board's own ShowTask (TasksWindow.ShowInPanel).
    TasksWindowActions InboxActions(TasksWindowActions actions)
    {
        Action<Guid> own = id => actions.OpenWindow?.Invoke(id);
        return actions with
        {
            Pick = own,
            ToggleThread = own,
            OpenParent = own,
            ShowTask = own,
            OpenPage = own,
        };
    }
}
