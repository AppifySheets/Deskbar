using System.Windows;
using System.Windows.Controls;

namespace Deskbar.App;

// THE COMMENT BOX STAYS PUT (the owner, from live use, 2026-11-27: the box was the last thing inside the page's scroller, so it
// scrolled away with the chat). On a task's page, in the panel and in a task window, the comment box is docked at the bottom
// of the page, OUTSIDE the scroller: only the chat above it scrolls, and the page's top row stays where it is. One docked area
// holds what waits at the bottom: the permission card (when one shows) directly above the box, and the box last, with its
// pictures waiting to go with it, which are parts of the box and move with it.
//
// HOW THE BOX GETS THERE, with the smallest change to Thread: Thread calls AddCommentBox where it used to add CommentBox. On a
// page's own thread that builds the very same box (every draft, picture, key and focus rule is CommentBox's and
// untouched: it is registered in commentBoxes as ever) but keeps it aside instead of adding it to the thread; DrawInboxPage
// then asks DockBottom to put it in the page's docked area. Anywhere else, the List's thread and a subtask's thread inside
// a parent's, the box is added to the thread as before.
//
// THE BOX GROWS WITH ITS TEXT up to a third of the page, then scrolls inside itself (the box's own template scrolls; only
// its MaxHeight is set, from the page's height). The chat's scroller takes the rest. A chat that was at its end stays at its
// end when the box grows or shrinks, since the scroller's view changes height: the page follows its newest.
//
// THE SEND BUTTON (the owner: "looks off-centre, low and close to the edge"): the app's theme gives every TextBox a minimum
// height of 32 DIP, sized for an editing box, so a one-line box was taller than the 24 DIP button, which sits at the bottom,
// and looked low with more room above than below. On a page the box has no minimum, so a one-line box is as tall as the
// button and the button is centred, its gap to the right edge the same as to the top and the bottom (the rounded frame's
// padding, 3 DIP, all round); in a taller box it stays at the bottom right with the same gap.
//
// THE CLAUDE BUTTON SITS LEFT OF THE BOX (task 625405cc; the owner: "move the open in claude button to the bottom, where i can
// easily click it if i need to"). Open Claude (or Start Claude on this, its other face) was in the row of buttons at the top
// of the thread, which is inside the scroller: on a long chat, opened at its end, it was a scroll away. On a page it now
// stands beside the docked box (WithClaudeButton), always in view, and the row at the top leaves it out (ThreadActions,
// BoxDocks). The List's thread keeps it in its row, which a capped thread holds in view anyway.
public partial class TasksWindow
{
    /// <summary>The Tag of the page's docked bottom area, for the tests.</summary>
    internal const string DockedAreaTag = "page-docked-bottom";

    /// <summary>The Tag of the frame the docked comment box stands in, for the tests.</summary>
    internal const string DockedBoxTag = "page-docked-box";

    // The box Thread built for the page's own task, waiting for DrawInboxPage to dock it.
    UIElement? dockedBox;
    Guid dockedBoxFor;

    // Whether this task's comment box goes to the page's docked bottom: the thread being built is its own page's. One rule
    // for the box and for the Claude button that stands beside it (ThreadActions leaves the button out of its row then).
    bool BoxDocks(Guid id) => inboxPage == id;

    // Thread's one hook: the box Thread just built goes to the page's bottom on a page's own thread (true: keep it out of the
    // thread), else (false) Thread adds it to the thread as before.
    bool KeepBoxForDock(UIElement box, Guid id)
    {
        if (!BoxDocks(id)) return false;
        // The box of a page has no minimum height and no padding of the theme's (a one-line box was 27 DIP tall, taller than the 24 DIP\r\n        // button, which sat low in it): a one-line box is as tall as the button and the button is centred.
        if (commentBoxes.TryGetValue(id, out var text))
        {
            text.MinHeight = 0;
            text.Padding = new Thickness(0);
            // What is typed reads as the chat above it does (the owner on build 8: "I dont think you made same changes for my
            // chatbox"): the same size, and lines as tall.
            text.FontSize = InboxLook.ChatText;
            TextBlock.SetLineHeight(text, Math.Round(InboxLook.ChatText * 1.5));
        }
        dockedBox = box;
        dockedBoxFor = id;
        return true;
    }

    // The page's docked area: the permission card above the comment box. Inserted right before the scroller, which fills
    // what is left of the page (DockPanel gives its last child the rest).
    void DockBottom(DockPanel dock, TaskRowView row, ScrollViewer scroller)
    {
        var area = new StackPanel { Tag = DockedAreaTag };
        if (PermissionCards(row) is { } cards) area.Children.Add(cards);
        TextBox? text = null;
        if (dockedBox is { } box && dockedBoxFor == row.Id)
        {
            commentBoxes.TryGetValue(row.Id, out text);
            area.Children.Add(new Border
            {
                Tag = DockedBoxTag,
                Padding = new Thickness(10, 6, 10, 8),
                BorderBrush = InboxLook.Hairline,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Child = WithClaudeButton(ClaudeAction(row), box),
            });
        }
        dockedBox = null;
        if (area.Children.Count == 0) return;
        DockPanel.SetDock(area, Dock.Bottom);
        dock.Children.Insert(dock.Children.IndexOf(scroller), area);

        // A third of the page, then the box scrolls inside itself.
        if (text is not null)
        {
            void Limit() => text.MaxHeight = Math.Max(72, (dock.ActualHeight > 0 ? dock.ActualHeight : InboxPage.ActualHeight) / 3);
            Limit();
            dock.SizeChanged += (_, _) => Limit();
        }
        // The chat keeps its end in view while the box (or a card) changes the room it has.
        scroller.ScrollChanged += (_, e) =>
        {
            if (e.ViewportHeightChange == 0 || e.ViewportHeightChange == e.ViewportHeight) return;
            var wasAtEnd = e.VerticalOffset - e.VerticalChange >= e.ExtentHeight - e.ExtentHeightChange - (e.ViewportHeight - e.ViewportHeightChange) - 1;
            if (wasAtEnd) scroller.ScrollToEnd();
        };
    }

    // The docked Claude button's square, the size of the icon buttons in the page's row at the top (ThreadActions).
    const double DockedClaudeSize = 26;

    // The gap from the button's bottom to the box's rounded edge: the send button's own gap inside the box (its frame's
    // 3 DIP padding), so the two buttons stand on one line.
    const double DockedClaudeGap = 3;

    // The comment box with the task's Claude button on its left (task 625405cc), or the box alone when the task has none
    // (a note, a closed task, or no StartClaude wired). The manager's page puts its own Claude button here the same way
    // (task d04cf9f0, ManagerBoxArea): the choice comes in, so both pages draw one button by one rule. Left, as a
    // messenger's extra button sits left of the box, so it is never mistaken for send, which is inside the box at its
    // right end.
    //
    // The button stays level with the box's last line, as send does: the box grows upward with its text (up to a third of
    // the page), and the button follows its bottom edge, 3 DIP above it like send. In a one-line box that centres it. It
    // keys on the box's rounded field, the first part of CommentBox's stack, and not on the whole stack, whose pictures
    // waiting to go with the comment sit under the field and would push the button below it.
    UIElement WithClaudeButton(ClaudeChoice? choice, UIElement box)
    {
        if (choice is not { } claude) return box;
        // The page's icon button (GlyphButton), so it looks like the row's buttons it came from; its tooltip starts with its
        // words, as theirs do.
        var button = GlyphButton(claude.Icon, claude.Words, claude.Press, $"{claude.Words}: {claude.Tip}", size: DockedClaudeSize,
            ignoredWhen: ClickIgnored);
        button.VerticalAlignment = VerticalAlignment.Top;
        var grid = new Grid { Children = { button, box } };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(box, 1);
        void Level(double fieldHeight, double above) =>
            button.Margin = new Thickness(0, Math.Max(0, above + fieldHeight - DockedClaudeGap - DockedClaudeSize), 6, 0);
        if (box is StackPanel { Children.Count: > 0 } stack && stack.Children[0] is FrameworkElement field)
        {
            // Before the first layout: a one-line field (the send button's 24 DIP, its frame's 3 DIP padding and 1 DIP edge).
            Level(24 + 2 * 3 + 2, stack.Margin.Top);
            field.SizeChanged += (_, e) => Level(e.NewSize.Height, stack.Margin.Top);
        }
        return grid;
    }
}
