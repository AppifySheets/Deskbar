using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Deskbar.App;

namespace Deskbar.Windows.Tests;

// The task thread drawn as a chat (task 39bc9c4d, ChatThread): the day lines, the grouping of a run of one author's
// comments, how wide a bubble's text is measured, and how a comment's day is named. No window is shown.
public class ChatThreadTests
{
    static TaskCommentView Comment(CommentAuthor who, string day) =>
        new(Guid.NewGuid(), who == CommentAuthor.You ? "you" : who.ToString(), who == CommentAuthor.Claude, $"{day} 10:00", "text")
        {
            Who = who,
            Day = day,
            Time = "10:00",
        };

    [Fact]
    public void A_day_line_heads_each_day_and_a_run_by_one_author_is_named_once() => StaThread.Run(() =>
    {
        var comments = new[]
        {
            Comment(CommentAuthor.Claude, "25 Nov"),
            Comment(CommentAuthor.Claude, "25 Nov"),
            Comment(CommentAuthor.You, "25 Nov"),
            Comment(CommentAuthor.Claude, "Today"),
        };
        var named = new List<bool>();
        var drawn = ChatThread.WithDays(comments, (_, isNamed) =>
        {
            named.Add(isNamed);
            return new Border();
        });

        // Two day lines (25 Nov, Today) around the four comments.
        var days = drawn.OfType<TextBlock>().Where(t => Equals(t.Tag, ChatThread.DayTag)).Select(t => t.Text).ToArray();
        Assert.Equal(["25 Nov", "Today"], days);
        Assert.Equal(6, drawn.Count);
        Assert.Same(drawn[0], drawn.OfType<TextBlock>().First());
        // The second of Claude's two comments carries no name; a new author, or a new day, names it again.
        Assert.Equal([true, false, true, true], named);
    });

    [Fact]
    public void A_short_paragraph_measures_snug_and_a_list_keeps_the_full_width() => StaThread.Run(() =>
    {
        var shortDocument = new FlowDocument(new Paragraph(new Run("Resumed."))) { FontSize = 11.5 };
        var width = ChatThread.NaturalWidth(shortDocument);
        Assert.InRange(width, 20, 120);

        var longer = ChatThread.NaturalWidth(new FlowDocument(new Paragraph(new Run("along with guidance and expected costs"))) { FontSize = 11.5 });
        Assert.True(longer > width);

        var list = new FlowDocument(new List(new ListItem(new Paragraph(new Run("one")))));
        Assert.True(double.IsPositiveInfinity(ChatThread.NaturalWidth(list)));
    });

    [Fact]
    public void A_day_is_named_today_yesterday_or_by_its_date()
    {
        var today = new DateTime(2026, 11, 26);
        Assert.Equal("Today", TasksBoard.DayOf(today, today));
        Assert.Equal("Yesterday", TasksBoard.DayOf(today.AddDays(-1), today));
        // In the machine's own language, as the comment times always were; written out here the same way.
        Assert.Equal(new DateTime(2026, 11, 20).ToString("d MMM"), TasksBoard.DayOf(new DateTime(2026, 11, 20), today));
        Assert.Equal(new DateTime(2025, 11, 20).ToString("d MMM yyyy"), TasksBoard.DayOf(new DateTime(2025, 11, 20), today));
    }

    [Fact]
    public void Inline_code_is_measured_in_its_own_wider_font() => StaThread.Run(() =>
    {
        static FlowDocument Of(Inline inline) => new(new Paragraph(inline)) { FontSize = 11.5 };
        var plain = ChatThread.NaturalWidth(Of(new Run("TasksWindow.xaml.cs")));
        var code = ChatThread.NaturalWidth(Of(new Run("TasksWindow.xaml.cs") { FontFamily = new System.Windows.Media.FontFamily("Consolas") }));
        Assert.True(code > plain);
        // A line break ends a line: two short lines measure as one of them, not as both side by side.
        var twoLines = ChatThread.NaturalWidth(new FlowDocument(new Paragraph { Inlines = { new Run("abc"), new LineBreak(), new Run("abc") } }) { FontSize = 11.5 });
        Assert.Equal(ChatThread.NaturalWidth(Of(new Run("abc"))), twoLines, 3);
    });

    [Fact]
    public void A_one_line_event_carries_its_time_and_a_longer_one_does_not()
    {
        Assert.True(ChatThread.TimeFitsInline("Resumed."));
        Assert.False(ChatThread.TimeFitsInline("Set aside while Claude works on your reply:\n> the question"));
        Assert.False(ChatThread.TimeFitsInline(""));
    }
}
