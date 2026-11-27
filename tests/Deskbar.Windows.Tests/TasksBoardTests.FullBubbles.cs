using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core.Tasks;

namespace Deskbar.Windows.Tests;

// EVERY BUBBLE ON A TASK'S PAGE IN FULL (FullBubble; the owner's first live look, 2026-11-27): a long comment or a long
// message from the session is drawn at its natural height, with no clip to three lines, no inner scrollbar and no resize
// grip; only a bubble past forty lines is cut, with "Show all", which survives a redraw. The List's thread keeps its clip.
public partial class TasksBoardTests
{
    // `lines` short paragraphs: a few more than three lines, and fewer than forty, or many more than forty.
    static string Paragraphs(int lines) => string.Join("\n\n", Enumerable.Range(1, lines).Select(i => $"Paragraph {i} of a made-up message."));

    static string TextOf(RichTextBox box) => new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text;

    static RichTextBox BoxWith(object scope, string text) =>
        Inside(scope).OfType<RichTextBox>().First(b => TextOf(b).Contains(text));

    static ScrollViewer InnerScroller(RichTextBox box)
    {
        box.ApplyTemplate();
        return (ScrollViewer)box.Template.FindName("PART_ContentHost", box);
    }

    static bool HasNamed(object scope, string name) => Inside(scope).OfType<FrameworkElement>().Any(e => AutomationProperties.GetName(e) == name);

    // Natural height: a text under the forty-line guard is laid out at its own height (no three-line cap), nothing scrolls inside
    // the box, and no resize grip, Show more or Show all is on the bubble.
    static void AssertWhole(object page, RichTextBox box, double size = InboxLook.ChatText)
    {
        Assert.Equal(FullBubble.GuardHeight(size), box.MaxHeight);
        Assert.True(box.ExtentHeight < box.MaxHeight, $"extent {box.ExtentHeight} under the guard {box.MaxHeight}");
        var inner = InnerScroller(box);
        Assert.Equal(ScrollBarVisibility.Disabled, inner.VerticalScrollBarVisibility);
        Assert.Equal(0, inner.ScrollableHeight, 1);
        Assert.Equal(box.ExtentHeight, box.ActualHeight, 1);
        Assert.False(HasNamed(page, "Resize comment"));
        Assert.False(HasNamed(page, "Show more"));
    }

    static Guid SessionTaskWithComments(Board t, params (string Author, string Text)[] comments)
    {
        var id = t.Tasks.Create("Review the diff", t.Gepha, Session("s1")).Value.Id;
        foreach (var (author, text) in comments) t.Tasks.Comment(id, author.Length == 0 ? Maybe<string>.None : author, text);
        return id;
    }

    [Fact]
    public void A_long_comment_on_the_page_is_drawn_whole_with_no_inner_scroll_and_no_grip() => StaThread.Run(() =>
    {
        using var t = Build(width: 460);
        var id = SessionTaskWithComments(t, ("s1", Paragraphs(16)), ("", Paragraphs(12) + " yours"));
        t.Controller.Show();
        ChooseInbox(t);
        t.Controller.ShowTask(id);
        t.Window.UpdateLayout();

        var page = t.Window.InboxPageContent!;
        AssertWhole(page, BoxWith(page, "Paragraph 16 of a made-up"));
        AssertWhole(page, BoxWith(page, "yours"));
        Assert.True(BoxWith(page, "Paragraph 16 of a made-up").ActualHeight > 54 * 4);
    });

    [Fact]
    public void The_list_thread_still_clips_a_long_comment_to_six_lines() => StaThread.Run(() =>
    {
        using var t = Build(width: 460);
        var id = SessionTaskWithComments(t, ("s1", Paragraphs(16)));
        t.Controller.Show();
        Click(LineOf(t.Window, id));
        t.Window.UpdateLayout();

        Assert.Null(t.Window.InboxPageContent);
        var box = BoxWith(t.Window, "Paragraph 16 of a made-up");
        // The List's fold, TasksWindow.FoldedLines: six lines of 18 DIP since task f0168cdd (it was Ruling 63's three, 54).
        Assert.Equal(6 * 18, box.MaxHeight);
        Assert.True(box.ClipToBounds);
        Assert.True(HasNamed(t.Window, "Show more"));
    });

    [Fact]
    public void A_comment_is_13_and_a_half_on_the_Inbox_page_and_11_and_a_half_on_the_List() => StaThread.Run(() =>
    {
        using var t = Build(width: 460);
        var id = SessionTaskWithComments(t, ("s1", "A short made-up comment."));
        t.Controller.Show();
        Click(LineOf(t.Window, id));
        t.Window.UpdateLayout();
        Assert.Null(t.Window.InboxPageContent);
        Assert.Equal(11.5, BoxWith(t.Window, "A short made-up").Document.FontSize);

        using var inbox = Build(width: 460);
        var inboxId = SessionTaskWithComments(inbox, ("s1", "A short made-up comment."));
        inbox.Controller.Show();
        ChooseInbox(inbox);
        inbox.Controller.ShowTask(inboxId);
        inbox.Window.UpdateLayout();
        Assert.Equal(InboxLook.ChatText, BoxWith(inbox.Window.InboxPageContent!, "A short made-up").Document.FontSize);
    });

    [Fact]
    public void A_monster_comment_is_cut_at_the_guard_and_show_all_opens_it_for_good_through_a_redraw() => StaThread.Run(() =>
    {
        using var t = Build(width: 460);
        var id = SessionTaskWithComments(t, ("s1", Paragraphs(70)));
        t.Controller.Show();
        ChooseInbox(t);
        t.Controller.ShowTask(id);
        t.Window.UpdateLayout();
        var page = t.Window.InboxPageContent!;
        var box = BoxWith(page, "Paragraph 70 of a made-up");

        Assert.False(double.IsPositiveInfinity(box.MaxHeight));
        Assert.Equal(FullBubble.GuardHeight(InboxLook.ChatText), box.MaxHeight);
        var showAll = ButtonNamed(page, FullBubble.ShowAllName);
        Assert.Equal(Visibility.Visible, showAll.Visibility);
        Assert.Equal(ScrollBarVisibility.Disabled, InnerScroller(box).VerticalScrollBarVisibility);
        Assert.False(HasNamed(page, "Resize comment"));

        Click(showAll);
        Assert.True(double.IsPositiveInfinity(box.MaxHeight));
        Assert.Equal(Visibility.Collapsed, showAll.Visibility);

        // A redraw builds every comment again: it must not fold this one.
        Assert.True(t.Window.Actions!.Comment(id, "a later word"));
        t.Window.UpdateLayout();
        var again = t.Window.InboxPageContent!;
        var rebuilt = BoxWith(again, "Paragraph 70 of a made-up");
        Assert.NotSame(box, rebuilt);
        Assert.True(double.IsPositiveInfinity(rebuilt.MaxHeight));
        Assert.DoesNotContain(Inside(again).OfType<Button>(), b => AutomationProperties.GetName(b) == FullBubble.ShowAllName && b.Visibility == Visibility.Visible);
    });

    [Fact]
    public void The_description_on_the_page_is_whole_and_a_monster_one_has_the_guard() => StaThread.Run(() =>
    {
        using var t = Build(width: 460);
        var id = SessionTaskWithComments(t);
        t.Tasks.Describe(id, Paragraphs(14));
        t.Controller.Show();
        ChooseInbox(t);
        t.Controller.ShowTask(id);
        t.Window.UpdateLayout();
        AssertWhole(t.Window.InboxPageContent!, BoxWith(t.Window.InboxPageContent!, "Paragraph 14 of a made-up"), 13);

        t.Tasks.Describe(id, Paragraphs(80));
        t.Window.UpdateLayout();
        var page = t.Window.InboxPageContent!;
        var box = BoxWith(page, "Paragraph 80 of a made-up");
        Assert.Equal(FullBubble.GuardHeight(13), box.MaxHeight);
        Click(ButtonNamed(page, FullBubble.ShowAllName));
        Assert.True(double.IsPositiveInfinity(box.MaxHeight));
    });

    [Fact]
    public void A_long_message_from_the_session_is_whole_and_a_monster_one_has_the_guard_that_survives_a_rebuild() => StaThread.Run(() =>
    {
        var fake = new FakeTranscript();
        fake.Add(new ChatMessage("long", ChatRole.Claude, Paragraphs(16), 0, false, Moment(1)));
        fake.Add(new ChatMessage("monster", ChatRole.Claude, Paragraphs(75), 0, false, Moment(2)));
        using var t = BuildWithTranscript(fake);
        var id = SessionTask(t);
        t.Controller.Show();
        ChooseInbox(t);
        t.Controller.ShowTask(id);
        t.Window.UpdateLayout();
        var page = t.Window.InboxPageContent!;

        AssertWhole(page, BoxWith(page, "Paragraph 16 of a made-up"));
        var monster = BoxWith(page, "Paragraph 75 of a made-up");
        Assert.Equal(FullBubble.GuardHeight(InboxLook.ChatText), monster.MaxHeight);
        // Only the monster has a visible "Show all"; the long message is under the guard, so its button is collapsed.
        Click(Inside(page).OfType<Button>().Single(b => AutomationProperties.GetName(b) == FullBubble.ShowAllName && b.Visibility == Visibility.Visible));
        Assert.True(double.IsPositiveInfinity(monster.MaxHeight));

        // A comment redraws the page; the stream moves into it and the opened message stays open.
        Assert.True(t.Window.Actions!.Comment(id, "a word"));
        t.Window.UpdateLayout();
        Assert.True(double.IsPositiveInfinity(BoxWith(t.Window.InboxPageContent!, "Paragraph 75 of a made-up").MaxHeight));
    });
}
