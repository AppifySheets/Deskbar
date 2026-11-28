using System.Windows;
using System.Windows.Controls;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// A PAGE OPENS WITH THE KEYBOARD IN ITS BOX (Deskbar task 64744611, Petre: "when i open manager or any other session,
// typing box should be active immediately"). A page in the Inbox is a chat, a task's or the manager's, and a chat is
// opened to write in, as a messenger opens one with the caret already in its box. Before this, the box had to be clicked
// first: the panel's toolbar or nothing had the keyboard, and typed keys went nowhere.
//
//   when       the first draw of a page (SettlePageScroll notes it: the page it settles was not the page it settled last),
//              so a line's click, the Manager button and line, back to a parent's page, a subtask's page. A redraw of the
//              same page never moves the keyboard: Draw's own restore keeps it wherever the owner put it.
//   only if    the panel is the active window. A WPF Focus() on an element of a window that is not active activates that
//              window (TasksWindow.Open, measured on the hidden desktop), and the board decides when the panel comes
//              forward (TasksBoard.Arrived: never over a full-screen window, never from another desktop). So a page opened
//              while the panel is not active leaves the keyboard alone, and FocusBoard gives the box the keyboard once the
//              board has brought the panel forward and activated it.
//   the caret  at the end of what is there, so a draft kept from before (drafts) goes on where it stopped.
//
// Esc still leaves the page (InboxBack, ManagerBack) and F2 still renames its task (InboxEditPage) while the box is empty.
// With text in it, both stay the box's, as they were (review M5 of the List): the draft is kept either way, but a
// half-written message is not swept off screen by a stray key.
//
// BACK IN FRONT. The panel redraws every few seconds, and each redraw makes the box anew. While the panel is in the
// background nothing in it has the keyboard, so the redraw's restore (Draw, keyboardWasIn) has nothing to restore, and
// the element WPF would give the keyboard back to on the next activation is a box no longer on screen. So an activation
// that ends with nothing in the panel focused puts the keyboard in the box of the page on show (RefocusPageBox).
//
// A task's own window (pageInWindow) draws its page in a TasksWindow that is never shown and so never active; the task
// window gives the box the keyboard itself when it is activated with nothing focused (TaskDetailWindow.FocusPageBox).
public partial class TasksWindow
{
    // The panel's activation (wired in the constructor): once it has settled (Input priority, after WPF has given back the
    // element focused before), a page on show with nothing in the panel focused gets the keyboard in its box. "Nothing"
    // includes the window itself, where WPF puts the keyboard of an activated window with no focused element (measured on
    // a task window, TaskDetailWindow.FocusPageBox).
    void RefocusPageBox() => Dispatcher.BeginInvoke(() =>
    {
        if (IsActive && (!IsKeyboardFocusWithin || IsKeyboardFocused) && OpenPageBox is { } box) FocusBox(box);
    }, System.Windows.Threading.DispatcherPriority.Input);

    // Set by SettlePageScroll when it settles a page it did not settle last; read once by Draw, after the draw.
    bool pageJustOpened;

    /// <summary>The message box of the page on show (a task's comment box, or the manager's box), null while the list shows.</summary>
    internal TextBox? OpenPageBox =>
        InboxPage.Visibility != Visibility.Visible ? null
        : managerPage ? CommentBoxOf(ManagerSession.PageId)
        : inboxPage is { } id ? CommentBoxOf(id)
        : null;

    // After a draw (Draw, once its own restores ran): the box of a page that just opened takes the keyboard, while the
    // panel is the active window and nothing in the page has it already. A page can open with a box of its own asked for:
    // "Duplicate of…", "Related to…" or "Add subtask…" on a closed line opens its page with the link picker or the subtask
    // box in the thread, and RefocusLink or RefocusSubtaskBox has just given that box the keyboard (review of task
    // 64744611). That box keeps it.
    void FocusOpenedPage()
    {
        if (!pageJustOpened) return;
        pageJustOpened = false;
        if (IsActive && !InboxPage.IsKeyboardFocusWithin && OpenPageBox is { } box) FocusBox(box);
    }

    /// <summary>The keyboard into a page's box, with the caret after its text (a kept draft goes on where it stopped).</summary>
    internal static void FocusBox(TextBox box)
    {
        box.Focus();
        box.CaretIndex = box.Text.Length;
    }

    // Whether Esc belongs to a comment box rather than to the page or thread around it: a box with the keyboard and text in
    // it. An empty box gives Esc up, so a page that opened with the keyboard in its box still goes back on one Esc. The
    // tests' seam (EscapeIgnoresThreadWhile) answers in its place when set.
    bool PageBoxKeepsEscape() =>
        EscapeIgnoresThreadWhile?.Invoke() ?? commentBoxes.Values.Any(box => box.IsKeyboardFocusWithin && box.Text.Length > 0);
}
