using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core.Tasks;

namespace Deskbar.Windows.Tests;

// THE INBOX PAGE KEEPS ITS PLACE WHILE ITS CONTENT IS SHORT (the owner, build 10: the chat "scrolls up by itself"; the trace
// on his PC showed redraws whose extent was 700 DIP, then 2400 DIP short of what he had been reading, with the offset
// clamped to what fitted). The page's chat is a stream that the transcript poll fills; when the poll's source goes missing
// for a moment (the transcript path not found, another session chosen) the stream empties, the scroller clamps its offset,
// and the content comes back only with a later poll or redraw. The page must be where the owner left it afterwards.
public partial class TasksBoardTests
{
    sealed class SourceSwitch { public bool Gone; }

    // A task with a session, a transcript of 30 messages (the fake), and comments with pictures, on a page. The source of the
    // transcript can be switched off (Gone) and on again, as it is on the owner's PC when its path is not found for a moment.
    static (Board T, Guid Id, SourceSwitch Source, string Root) PictureChat()
    {
        var root = ScratchRoot();
        var fake = new FakeTranscript();
        for (var i = 0; i < 30; i++)
            fake.Add(ClaudeSaid($"Message {i}: " + string.Join(" ", Enumerable.Repeat("words that wrap over the line", 6)), 60 + i * 2));
        var source = new SourceSwitch();
        var t = Build(width: 460, stateDir: root, transcriptOf: s => source.Gone ? Maybe<string>.None : TranscriptPath(s));
        t.Window.TranscriptInline = true;
        t.Window.TranscriptTimer = t.Timers.Start;
        t.Window.TranscriptReader = () => fake.Read;
        var id = SessionTask(t);
        for (var i = 0; i < 3; i++)
        {
            var picture = TaskImageFiles.Save(root, id, [TaskImageFiles.FromBitmap(Picture(900, 600)).Value], Start.AddSeconds(i)).Value.Single();
            t.Tasks.Comment(id, Maybe<string>.None, TaskImages.Join($"Comment {i} " + string.Join(" ", Enumerable.Repeat("more words", 8)), [picture]));
        }
        t.Controller.Show();
        ChooseInbox(t);
        t.Window.UpdateLayout();
        t.Controller.ShowTask(id);
        t.Window.UpdateLayout();
        SettleDispatcher(t);
        return (t, id, source, root);
    }

    static void SettleDispatcher(Board t)
    {
        for (var i = 0; i < 3; i++)
        {
            t.Window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
            Thread.Sleep(20);
        }
        t.Window.UpdateLayout();
    }

    // The poll's tick, then the layout it causes, settled.
    static void PollNow(Board t)
    {
        t.Timers.FireAll();
        t.Window.UpdateLayout();
        SettleDispatcher(t);
    }

    static void Redraw(Board t, Guid id, string line)
    {
        t.Tasks.SetStatusLine(id, line);
        t.Window.UpdateLayout();
        SettleDispatcher(t);
    }

    static double ScrollMiddle(Board t)
    {
        var s = t.Window.InboxPageScroller!;
        var middle = Math.Round(s.ScrollableHeight / 2);
        s.ScrollToVerticalOffset(middle);
        t.Window.UpdateLayout();
        SettleDispatcher(t);
        Assert.Equal(middle, s.VerticalOffset, 1);
        return middle;
    }

    static string Where(Board t)
    {
        var s = t.Window.InboxPageScroller!;
        return $"offset {s.VerticalOffset:0}, extent {s.ExtentHeight:0}, viewport {s.ViewportHeight:0}";
    }

    // The owner's own input on the scroller, as WPF sends it.
    static void OwnerWheels(Board t) => t.Window.InboxPageScroller!.RaiseEvent(
        new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });

    [Fact]
    public void A_page_scrolled_to_the_middle_is_there_again_after_its_chat_went_missing_for_a_redraw() => StaThread.Run(() =>
    {
        var (t, id, source, root) = PictureChat();
        using var _t = t;
        try
        {
            var middle = ScrollMiddle(t);
            var full = t.Window.InboxPageScroller!.ExtentHeight;

            source.Gone = true;
            PollNow(t);
            Redraw(t, id, "while the chat is missing");
            Assert.True(t.Window.InboxPageScroller!.ScrollableHeight < middle, $"the chat was not missing: {Where(t)}");

            source.Gone = false;
            PollNow(t);
            Redraw(t, id, "back");

            var s = t.Window.InboxPageScroller!;
            Assert.True(Math.Abs(s.ExtentHeight - full) < 40, $"the chat did not come back: full {full:0}, now {Where(t)}");
            Assert.True(Math.Abs(s.VerticalOffset - middle) < 3, $"wanted {middle:0}, got {Where(t)}");
        }
        finally { System.IO.Directory.Delete(root, true); }
    });

    [Fact]
    public void A_page_at_its_end_is_at_its_end_again_after_its_chat_went_missing_for_a_redraw() => StaThread.Run(() =>
    {
        var (t, id, source, root) = PictureChat();
        using var _t = t;
        try
        {
            var s0 = t.Window.InboxPageScroller!;
            s0.ScrollToEnd();
            t.Window.UpdateLayout();
            SettleDispatcher(t);
            Assert.True(s0.VerticalOffset >= s0.ScrollableHeight - 1 && s0.ScrollableHeight > 100, $"not at its end: {Where(t)}");

            source.Gone = true;
            PollNow(t);
            Redraw(t, id, "while the chat is missing");
            source.Gone = false;
            PollNow(t);
            Redraw(t, id, "back");

            var s = t.Window.InboxPageScroller!;
            Assert.True(s.ScrollableHeight > 100, $"the chat did not come back: {Where(t)}");
            Assert.True(s.VerticalOffset >= s.ScrollableHeight - 3, $"wanted the end, got {Where(t)}");
        }
        finally { System.IO.Directory.Delete(root, true); }
    });

    // The redraw that comes while the page is already short reads its offset from the clamped scroller (the trace's
    // "keep=0 ... extent=3013"): the place must come from before the chat went.
    [Fact]
    public void Two_redraws_while_the_chat_is_missing_still_bring_the_page_back_to_where_it_was() => StaThread.Run(() =>
    {
        var (t, id, source, root) = PictureChat();
        using var _t = t;
        try
        {
            var middle = ScrollMiddle(t);

            source.Gone = true;
            PollNow(t);
            Redraw(t, id, "one");
            Redraw(t, id, "two");
            source.Gone = false;
            PollNow(t);

            var s = t.Window.InboxPageScroller!;
            Assert.True(s.ScrollableHeight > middle, $"the chat did not come back: {Where(t)}");
            Assert.True(Math.Abs(s.VerticalOffset - middle) < 3, $"wanted {middle:0}, got {Where(t)}");
        }
        finally { System.IO.Directory.Delete(root, true); }
    });

    // The restore never fights him: once he has scrolled himself, a chat that goes and comes back leaves him where he is.
    [Fact]
    public void After_the_owner_scrolls_himself_the_page_is_not_pulled_back() => StaThread.Run(() =>
    {
        var (t, id, source, root) = PictureChat();
        using var _t = t;
        try
        {
            ScrollMiddle(t);
            source.Gone = true;
            PollNow(t);
            Redraw(t, id, "while the chat is missing");

            OwnerWheels(t);
            var s = t.Window.InboxPageScroller!;
            s.ScrollToVerticalOffset(0);
            t.Window.UpdateLayout();

            source.Gone = false;
            PollNow(t);
            Redraw(t, id, "back");

            Assert.True(t.Window.InboxPageScroller!.VerticalOffset < 3, $"he was at the top, now {Where(t)}");
        }
        finally { System.IO.Directory.Delete(root, true); }
    });
}
