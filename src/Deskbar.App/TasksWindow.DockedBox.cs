using System.Windows;
using System.Windows.Controls;

namespace Deskbar.App;

// THE COMMENT BOX STAYS PUT (the owner, from live use, 2026-11-27: the box was the last thing inside the page's scroller, so it
// scrolled away with the chat). On a task's page, in the panel and in a task window, the comment box is docked at the bottom
// of the page, OUTSIDE the scroller: only the chat above it scrolls, and the page's top row stays where it is. One docked area
// holds what waits at the bottom: the permission card (when one shows) directly above the box, and the box last, with its
// hint line and the pictures waiting to go with it, which are parts of the box and move with it.
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
public partial class TasksWindow
{
    /// <summary>The Tag of the page's docked bottom area, for the tests.</summary>
    internal const string DockedAreaTag = "page-docked-bottom";

    /// <summary>The Tag of the frame the docked comment box stands in, for the tests.</summary>
    internal const string DockedBoxTag = "page-docked-box";

    // The box Thread built for the page's own task, waiting for DrawInboxPage to dock it.
    UIElement? dockedBox;
    Guid dockedBoxFor;

    // Thread's one hook: the box Thread just built goes to the page's bottom on a page's own thread (true: keep it out of the
    // thread), else (false) Thread adds it to the thread as before.
    bool KeepBoxForDock(UIElement box, Guid id)
    {
        if (inboxPage != id) return false;
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
                Child = box,
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
}
