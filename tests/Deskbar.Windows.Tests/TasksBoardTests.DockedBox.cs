using System.IO;
using System.Windows;
using System.Windows.Controls;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core.Tasks;

namespace Deskbar.Windows.Tests;

// THE COMMENT BOX STAYS PUT (TasksWindow.DockedBox.cs; the owner, from live use, 2026-11-27): on a task's page it is docked at
// the bottom, outside the page's scroller, with the permission card directly above it; the List's thread keeps its box
// inside the thread. The send button sits centred in a one-line box, with the same gap on its right, top and bottom.
public partial class TasksBoardTests
{
    static Guid PageTaskWithLongChat(Board t, int paragraphs = 60)
    {
        var id = t.Tasks.Create("Review the diff", t.Gepha, Session("s1")).Value.Id;
        t.Tasks.Describe(id, string.Join("\n\n", Enumerable.Range(1, paragraphs).Select(i => $"Step {i} of a long brief, long enough to take a line of its own.")));
        t.Controller.Show();
        ChooseInbox(t);
        t.Window.UpdateLayout();
        t.Controller.ShowTask(id);
        t.Window.UpdateLayout();
        return id;
    }

    static Point PlaceIn(FrameworkElement what, UIElement relativeTo) => what.TranslatePoint(new Point(0, 0), relativeTo);

    [Fact]
    public void The_comment_box_is_docked_outside_the_pages_scroller() => StaThread.Run(() =>
    {
        using var t = Build(width: 460);
        var id = PageTaskWithLongChat(t);
        var page = t.Window.InboxPageContent!;
        var box = t.Window.CommentBoxOf(id)!;
        var scroller = t.Window.InboxPageScroller!;

        Assert.DoesNotContain(box, Inside(scroller));
        Assert.Contains(box, Inside(page));
        var frame = Inside(page).OfType<Border>().Single(b => Equals(b.Tag, TasksWindow.DockedBoxTag));
        Assert.Contains(box, Inside(frame));
        Assert.True(scroller.ScrollableHeight > 0);
        // At the page's bottom edge, under the whole scroller.
        var scrollerBottom = PlaceIn(scroller, (UIElement)page).Y + scroller.ActualHeight;
        Assert.True(PlaceIn(frame, (UIElement)page).Y >= scrollerBottom - 1);
        Assert.True(((FrameworkElement)page).ActualHeight - (PlaceIn(frame, (UIElement)page).Y + frame.ActualHeight) < 4);
    });

    [Fact]
    public void Scrolling_the_chat_does_not_move_the_box_or_the_top_row() => StaThread.Run(() =>
    {
        using var t = Build(width: 460);
        var id = PageTaskWithLongChat(t);
        var page = (UIElement)t.Window.InboxPageContent!;
        var box = t.Window.CommentBoxOf(id)!;
        var scroller = t.Window.InboxPageScroller!;
        var title = Inside(page).OfType<TextBlock>().First(b => Equals(b.Tag, "task-title"));
        var (boxAt, titleAt) = (PlaceIn(box, page), PlaceIn(title, page));

        scroller.ScrollToVerticalOffset(0);
        t.Window.UpdateLayout();
        Assert.Equal(0, scroller.VerticalOffset, 1);
        Assert.Equal(boxAt.Y, PlaceIn(box, page).Y, 1);
        Assert.Equal(titleAt.Y, PlaceIn(title, page).Y, 1);

        scroller.ScrollToVerticalOffset(scroller.ScrollableHeight / 2);
        t.Window.UpdateLayout();
        Assert.Equal(boxAt.Y, PlaceIn(box, page).Y, 1);
        Assert.Equal(titleAt.Y, PlaceIn(title, page).Y, 1);
    });

    [Fact]
    public void The_page_still_opens_at_its_end_with_the_box_docked() => StaThread.Run(() =>
    {
        using var t = Build(width: 460);
        PageTaskWithLongChat(t);

        var (offset, extent) = t.Window.InboxPageScroll;

        Assert.True(extent > 0);
        Assert.Equal(extent, offset, 1);
    });

    [Fact]
    public void A_draft_and_the_pending_pictures_survive_a_redraw_with_the_box_docked() => StaThread.Run(() =>
    {
        var root = ScratchRoot();
        try
        {
            using var t = Build(width: 460, stateDir: root);
            var id = PageTaskWithLongChat(t, 10);
            var first = t.Window.CommentBoxOf(id)!;
            first.Text = "half a thought";
            first.CaretIndex = 4;
            Assert.True(t.Window.PasteImages(id, BitmapData()));
            Assert.Single(t.Window.PendingImages(id));

            // A session's comment redraws the whole page.
            Assert.True(t.Tasks.Comment(id, "s1", "A word from the session").IsSuccess);

            var again = t.Window.CommentBoxOf(id)!;
            Assert.NotSame(first, again);
            Assert.Equal("half a thought", again.Text);
            Assert.Equal("half a thought", t.Window.CommentDraft(id));
            Assert.Single(t.Window.PendingImages(id));
            var frame = Inside(t.Window.InboxPageContent!).OfType<Border>().Single(b => Equals(b.Tag, TasksWindow.DockedBoxTag));
            Assert.Contains(again, Inside(frame));
            Assert.DoesNotContain(again, Inside(t.Window.InboxPageScroller!));
            // The preview of the waiting picture is in the box's frame, with its x.
            Assert.NotEmpty(Inside(frame).OfType<System.Windows.Controls.Image>());
        }
        finally { Directory.Delete(root, true); }
    });

    [Fact]
    public void The_draft_survives_the_page_closing_and_opening_again_and_enter_still_posts() => StaThread.Run(() =>
    {
        using var t = Build(width: 460);
        var id = PageTaskWithLongChat(t, 5);
        t.Window.CommentBoxOf(id)!.Text = "kept across a close";

        Click(ButtonNamed(t.Window.InboxPageContent!, TasksWindow.InboxPageBackText));
        t.Controller.ShowTask(id);

        Assert.Equal("kept across a close", t.Window.CommentBoxOf(id)!.Text);
        Assert.True(t.Window.TypeComment(id, "posted from the docked box", System.Windows.Input.Key.Enter));
        Assert.Equal("posted from the docked box", t.Tasks.Board.Tasks.Single(x => x.Id == id).Comments.Last().Markdown);
        Assert.Equal("", t.Window.CommentBoxOf(id)!.Text);
    });

    [Fact]
    public void The_lists_thread_keeps_its_box_inside_the_thread() => StaThread.Run(() =>
    {
        using var t = Build(width: 460);
        var id = t.Tasks.Create("Review the diff", t.Gepha, Session("s1")).Value.Id;
        t.Controller.Show();
        Click(LineOf(t.Window, id));
        t.Window.UpdateLayout();

        Assert.Null(t.Window.InboxPageContent);
        var box = t.Window.CommentBoxOf(id)!;
        Assert.Contains(box, Inside(t.Window.Rows));
        Assert.DoesNotContain(Inside(t.Window).OfType<Border>(), b => Equals(b.Tag, TasksWindow.DockedBoxTag));
    });

    [Fact]
    public void The_permission_card_sits_directly_above_the_docked_box() => StaThread.Run(() =>
    {
        using var r = PermissionPage();
        var page = (UIElement)r.Board.Window.InboxPageContent!;
        var area = Inside(page).OfType<StackPanel>().Single(p => Equals(p.Tag, TasksWindow.DockedAreaTag));

        var card = Assert.Single(Cards(r));
        var frame = Inside(page).OfType<Border>().Single(b => Equals(b.Tag, TasksWindow.DockedBoxTag));
        Assert.Equal([card.GetType(), frame.GetType()], area.Children.Cast<UIElement>().Select(c => c.GetType()));
        Assert.Same(frame, area.Children[^1]);
        Assert.Contains(card, Inside(area.Children[0]));
        Assert.DoesNotContain(frame, Inside(r.Board.Window.InboxPageScroller!));
        r.Board.Window.UpdateLayout();
        Assert.True(PlaceIn(card, page).Y + card.ActualHeight <= PlaceIn(frame, page).Y + 1);
    });

    [Fact]
    public void A_growing_box_stops_at_a_third_of_the_page_and_the_chat_keeps_its_end() => StaThread.Run(() =>
    {
        using var t = Build(width: 460);
        var id = PageTaskWithLongChat(t);
        var box = t.Window.CommentBoxOf(id)!;
        var oneLine = box.ActualHeight;

        box.Text = string.Join("\n", Enumerable.Range(1, 5).Select(i => $"line {i}"));
        t.Window.UpdateLayout();
        var five = box.ActualHeight;
        var (offset, extent) = t.Window.InboxPageScroll;
        Assert.True(five > oneLine + 20);
        Assert.Equal(extent, offset, 1.5);

        box.Text = string.Join("\n", Enumerable.Range(1, 80).Select(i => $"line {i}"));
        t.Window.UpdateLayout();
        var page = (FrameworkElement)t.Window.InboxPageContent!;
        Assert.True(box.ActualHeight <= page.ActualHeight / 3 + 1, $"{box.ActualHeight} of {page.ActualHeight}");
        Assert.True(box.ActualHeight > page.ActualHeight / 3 - 40);
        Assert.True(t.Window.InboxPageScroller!.ViewportHeight > page.ActualHeight / 3);
    });

    // The send button: centred in a one-line box, equal gaps right, top and bottom; at the bottom right of a taller one.
    static (double Top, double Right, double Bottom) SendGaps(TasksWindow window, Guid id)
    {
        var page = (UIElement)window.InboxPageContent!;
        var send = ButtonNamed(page, "Post comment");
        var frame = Inside(page).OfType<Border>().First(b => b.CornerRadius.TopLeft == 15 && Inside(b).Contains(send));
        var at = PlaceIn(send, frame);
        // The frame's own 1 DIP edge is not room.
        return (at.Y - 1, frame.ActualWidth - 1 - (at.X + send.ActualWidth), frame.ActualHeight - 1 - (at.Y + send.ActualHeight));
    }

    [Fact]
    public void The_send_button_is_centred_in_a_one_line_box_with_equal_gaps() => StaThread.Run(() =>
    {
        using var t = Build(width: 460);
        var id = PageTaskWithLongChat(t, 5);
        t.Window.UpdateLayout();

        var (top, right, bottom) = SendGaps(t.Window, id);

        Assert.Equal(top, bottom, 1);
        Assert.Equal(top, right, 1.5);
        Assert.InRange(top, 1, 6);
    });

    [Fact]
    public void In_a_taller_box_the_send_button_stays_at_the_bottom_right_with_the_same_gap() => StaThread.Run(() =>
    {
        using var t = Build(width: 460);
        var id = PageTaskWithLongChat(t, 5);
        var (top1, right1, bottom1) = SendGaps(t.Window, id);
        t.Window.CommentBoxOf(id)!.Text = string.Join("\n", Enumerable.Range(1, 4).Select(i => $"line {i}"));
        t.Window.UpdateLayout();

        var (top, right, bottom) = SendGaps(t.Window, id);

        Assert.Equal(bottom1, bottom, 1);
        Assert.Equal(right1, right, 1);
        Assert.True(top > bottom + 10, $"top {top} bottom {bottom}");
    });
}
