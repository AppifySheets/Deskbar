using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// THE MANAGER'S PAGE IN THE INBOX (task 6c4c067f; spec docs/superpowers/specs/2026-11-28-manager-inbox-page-design.md; the
// owner: "open a manager session in the same way as you open a task in the inbox", and his picks on the mockup: the page
// replaces the docked terminal, "Open in terminal" stays on its menu, no manager line pinned in the list). That menu has
// since given way to the Claude button a task's page has beside its box (task d04cf9f0; the owner: "give the manager
// similar button to invoke the session easily as the tasks have").
//
// The Manager button and the manager line open the manager over the Inbox's list the way a click on a line opens a task's
// page (TasksBoard.ShowManager calls ShowManagerPage). It is drawn by its own small path, not by DrawInboxPage, because a
// task's page is built around a task row (description, question, sessions, subtasks, pull requests, Close) and the manager
// has none of those. What it shares with a task's page, it takes from there unchanged:
//
//   the top     the back arrow (Esc does the same, unless the keyboard is in the box), the manager's violet tile and
//               "Manager"; under them the state in words;
//   the body    the manager's chat: its transcript's messages and steps (the same poll, InboxTranscript, following
//               ManagerSession.PageId, and the same ChatStreamPanel), with the user's page messages as their bubbles
//               (ManagerRecord.Messages, through TasksWindowActions.ManagerMessages), bottom-aligned and held in place
//               through redraws exactly as a task's page is (HeldPlace, SettlePageScroll);
//   the bottom  the comment box of a task's page (CommentBox, keyed by PageId), docked under the scroller; Enter sends
//               through TasksWindowActions.SendToManager (Post), Shift+Enter breaks the line, a refusal keeps the text.
//               Left of it, the Claude button a task's page has there (WithClaudeButton, task d04cf9f0): Open Claude, the
//               manager's own terminal window (TasksWindowActions.OpenManagerTerminal), always in view.
//
// HOW IT IS LEFT: back (to the task page it was opened over, if any, else the list), a click on an Inbox line, a task shown
// from outside (the bar, a link), or another view. While it shows, no task page is open (inboxPage is null), so the
// manager's prompt hook names no "current task" for a line typed in its terminal; a page message carries the task it was
// opened over itself (ManagerSession.PageMessage).
public partial class TasksWindow
{
    // Whether the manager's page is what the Inbox shows, and the task page it was opened over (back returns to it, and a
    // message names it as "the current task").
    bool managerPage;
    Guid? managerFrom;

    internal const string ManagerPageTag = "manager-page";
    internal const string ManagerPlaceholder = "Message the manager…";
    internal const string ManagerClaudeTip = "Go to the manager's Claude session in its own window, starting it first if none runs today";
    internal const string NoManagerYetText = "No manager yet today: send a message to start it.";

    /// <summary>Whether the manager's page is drawn right now (TasksBoard.ManagerPageInFront, the tests).</summary>
    internal bool ManagerPageShown => managerPage && InboxPage.Visibility == Visibility.Visible;

    /// <summary>For the tests: the manager page's message box.</summary>
    internal TextBox? ManagerBox => CommentBoxOf(ManagerSession.PageId);

    /// <summary>
    /// Opens the manager's page over the Inbox (TasksBoard.ShowManager, after it put the panel on the Inbox). A task page
    /// open now is remembered for back; the list's scroll place is kept as a task page keeps it. Drawn by the redraw that
    /// follows (the board's Refresh).
    /// </summary>
    internal void ShowManagerPage()
    {
        if (!managerPage)
        {
            managerFrom = inboxPage;
            if (inboxPage is null) inboxListOffset = Scroller.VerticalOffset;
            // The task page's poll and stream end here (review of task 6c4c067f): left running, the manager's first items
            // would land in that task's stream until the manager's page had one of its own.
            StopTranscript();
        }
        managerPage = true;
        // As a task's page opened from the list (OpenInboxPage): its empty search row starts hidden.
        inboxSearchOpen = false;
        inboxPage = null;
        inboxPageStack.Clear();
        // Drawn now only while the Inbox already shows: a draw of a List view left over from before the board's switch would
        // leave the Inbox (PaintInboxChrome) and with it this page. The board's Refresh after this draws it either way.
        if (latest?.Inbox is not null) RedrawInbox();
    }

    // The flags only: the caller draws (a line's click, a show from outside, the view leaving the Inbox).
    void LeaveManagerPage()
    {
        managerPage = false;
        managerFrom = null;
    }

    // Back: to the task page it was opened over while that task's thread is still open, else to the list where it was.
    void BackFromManager()
    {
        var from = managerFrom;
        LeaveManagerPage();
        StopTranscript();
        inboxPage = from;
        RedrawInbox();
        if (from is null) RestoreListOffset();
    }

    // Esc on the manager's page (InboxBack): back, unless the keyboard is in the box, where Esc is the box's.
    bool ManagerBack()
    {
        if (!managerPage || latest?.Inbox is null) return false;
        if (EscapeIgnoresThreadWhile?.Invoke() ?? commentBoxes.Values.Any(box => box.IsKeyboardFocusWithin)) return false;
        BackFromManager();
        return true;
    }

    // Part of the redraw signature while the page shows: the user's messages (a send changes only them).
    // The poll's source too: the manager starting, stopping or being replaced changes the state words and the stream.
    string ManagerPageSignature() =>
        !managerPage ? ""
        : $"|manager|{managerFrom}|{transcript?.Source.Kind}|{transcript?.Source.SessionId}|"
          + (Actions?.ManagerMessages?.Invoke() is { } messages ? $"{messages.Count}{messages.LastOrDefault()?.Id}" : "");

    // The page, drawn in DrawInbox's place for a task's page.
    void DrawManagerPage()
    {
        Scroller.Visibility = Visibility.Collapsed;
        EmptyText.Visibility = Visibility.Collapsed;
        InboxHeader.Visibility = Visibility.Collapsed;
        InboxFooter.Visibility = Visibility.Collapsed;
        InboxPage.Visibility = Visibility.Visible;
        var id = ManagerSession.PageId;
        var hold = HeldPlace(id);
        FollowTranscript(id);
        var dock = new DockPanel { LastChildFill = true, Tag = ManagerPageTag };
        var top = ManagerPageTop();
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        var body = ManagerPageBody();
        body.VerticalAlignment = VerticalAlignment.Bottom;
        pageScroller = new FastScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Content = body,
        };
        // The box only where a message can go (the App wired SendToManager); docked before the scroller, which fills the rest.
        if (Actions?.SendToManager is not null)
        {
            var area = ManagerBoxArea();
            DockPanel.SetDock(area, Dock.Bottom);
            dock.Children.Add(area);
        }
        dock.Children.Add(pageScroller);
        InboxPage.Child = dock;
        SettlePageScroll(pageScroller, id, hold);
    }

    UIElement ManagerPageTop()
    {
        var first = new DockPanel { LastChildFill = true };
        var back = GlyphButton("back", InboxPageBackText, BackFromManager, "Back (Esc)");
        DockPanel.SetDock(back, Dock.Left);
        first.Children.Add(back);
        // The manager's violet tile, in the place a task's state mark has (ManagerLook: the colours of its line and window).
        var tile = new Border
        {
            Tag = InboxMarkTag, Width = 28, Height = 28, CornerRadius = new CornerRadius(9), Background = ManagerLook.ChipFill,
            BorderBrush = ManagerLook.Stripe, BorderThickness = new Thickness(1),
            Margin = new Thickness(4, 1, 10, 0), VerticalAlignment = VerticalAlignment.Top, ToolTip = "The manager",
            Child = Centred(new TextBlock { Text = "✳", FontSize = 14, Foreground = ManagerLook.Stripe }),
        };
        DockPanel.SetDock(tile, Dock.Left);
        first.Children.Add(tile);
        first.Children.Add(new TextBlock
        {
            Tag = TitleTag,
            Text = "Manager",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = ManagerLook.TitleInk,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 3, 4, 0),
        });
        var words = Text(ManagerStateWords(), 12, InboxLook.Ink2, FontWeights.SemiBold, new Thickness(36, 6, 0, 4));
        AutomationProperties.SetName(words, "State");
        return new Border
        {
            Padding = new Thickness(8, 10, 10, 6),
            BorderBrush = InboxLook.Hairline,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new StackPanel { Children = { first, words } },
        };
    }

    // The state under the title, from the transcript the poll chose (it asks the board at every tick, so a manager that
    // started or ended since is told at the next redraw).
    string ManagerStateWords() => transcript?.Source.Kind switch
    {
        ChatSourceKind.Live => "Today's manager is running",
        ChatSourceKind.Ended => "Today's manager is asleep: a message wakes it",
        ChatSourceKind.Expired => "Today's manager's record has expired",
        _ => (Actions?.ManagerMessages?.Invoke().Count ?? 0) == 0 ? NoManagerYetText : "Starting the manager…",
    };

    // THE MANAGER'S CLAUDE BUTTON (task d04cf9f0): the button a task's page has left of its box, same picture, same words,
    // same place, so the manager's session is one click away as a task's is. It replaced the "⋯" menu whose one item,
    // "Open in terminal", did the same thing two clicks deep (task 6c4c067f); keeping both would be the duplicate the owner
    // turned down on a task's thread (task 5e02e99f). Always Open Claude, never Start: ManagerActions.Open attaches to
    // today's manager, resumes it or starts it, so one face covers every state. None when the App wired no terminal.
    ClaudeChoice? ManagerClaude() =>
        Actions?.OpenManagerTerminal is { } open ? new ClaudeChoice("terminal", OpenClaudeText, () => open(), ManagerClaudeTip) : null;

    // The chat: the user's page messages as their bubbles among the transcript's items, in one stream kept across redraws.
    FrameworkElement ManagerPageBody()
    {
        var body = new StackPanel { Margin = new Thickness(10, 8, 10, 10) };
        var messages = Actions?.ManagerMessages?.Invoke() ?? [];
        var views = messages.Select(ViewOf).ToList();
        if (transcript is not { Task: { } followed } poll || followed != ManagerSession.PageId || poll.Source.Kind == ChatSourceKind.None)
        {
            // Nothing to read yet (no manager today, or a copy without transcripts): the messages alone, as a plain chat.
            ChatThread.WithDays(ChatThread.Grouped(views), view => CommentOf(view, ManagerSession.PageId)).ToList().ForEach(e => body.Children.Add(e));
            return body;
        }
        var comments = GroupedInStream(views, poll.Latest?.Items ?? [], earlierFolded: false)
            .Select(c => ("comment:" + c.Id, c.At, CommentOf(c, ManagerSession.PageId))).ToList();
        if (stream is null || streamTask != ManagerSession.PageId)
        {
            stream = new ChatStreamPanel(new ChatStreamDeps((ControlTemplate)FindResource("BareRichTextBox"), LinkOpener(ManagerSession.PageId),
                (content, name, press) =>
                {
                    var button = Flat(content, name, press, new Thickness(0), ClickIgnored);
                    button.Background = Brushes.Transparent;
                    button.Focusable = true;
                    return button;
                }, Now, streamState), comments, false);
            streamTask = ManagerSession.PageId;
        }
        else
        {
            if (stream.Parent is Panel old) old.Children.Remove(stream);
            stream.SetComments(comments, false);
        }
        stream.Apply(poll.Latest?.Items ?? [], NoteOf(poll), poll.Source.Kind == ChatSourceKind.Live);
        body.Children.Add(stream);
        return body;
    }

    // One page message as the user's comment in the chat (TasksBoard.CommentView's shape, for "you").
    TaskCommentView ViewOf(ManagerMessage message)
    {
        var at = message.At.ToLocalTime();
        var today = Now().ToLocalTime().Date;
        return new TaskCommentView(message.Id, "you", false, today == at.Date ? at.ToString("HH:mm") : at.ToString("d MMM HH:mm"), message.Text)
        {
            Who = CommentAuthor.You,
            Time = at.ToString("HH:mm"),
            Day = at.Date == today ? "Today" : at.Date == today.AddDays(-1) ? "Yesterday" : at.ToString("d MMM"),
            FullWhen = at.ToString("d MMM yyyy HH:mm"),
            At = message.At,
        };
    }

    // The box docked under the chat: a task page's comment box (CommentBox) keyed by PageId, styled as KeepBoxForDock styles
    // a page's (no minimum height, the chat's text size), in the same framed area DockBottom puts a task's box in, with the
    // manager's Claude button on its left as a task's has (WithClaudeButton).
    UIElement ManagerBoxArea()
    {
        var box = WithClaudeButton(ManagerClaude(), CommentBox(ManagerSession.PageId));
        if (commentBoxes.TryGetValue(ManagerSession.PageId, out var text))
        {
            text.MinHeight = 0;
            text.Padding = new Thickness(0);
            text.FontSize = InboxLook.ChatText;
            TextBlock.SetLineHeight(text, Math.Round(InboxLook.ChatText * 1.5));
        }
        return new StackPanel
        {
            Tag = DockedAreaTag,
            Children =
            {
                new Border
                {
                    Tag = DockedBoxTag,
                    Padding = new Thickness(10, 6, 10, 8),
                    BorderBrush = InboxLook.Hairline,
                    BorderThickness = new Thickness(0, 1, 0, 0),
                    Child = box,
                },
            },
        };
    }

    // Post's road for the manager's box: the text to the manager, with the task page it was opened over.
    bool SendToManager(string text) => Actions?.SendToManager?.Invoke(text, managerFrom).IsSuccess ?? false;
}
