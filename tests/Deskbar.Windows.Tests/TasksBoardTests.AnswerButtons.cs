using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CSharpFunctionalExtensions;
using Deskbar.App;

namespace Deskbar.Windows.Tests;

// The answer buttons under a task's question on its Inbox page. They post through the board's own AnswerWithOption.
public partial class TasksBoardTests
{
    static readonly string[] Answers = ["Yes, merge it", "No, wait for CI", "Rebase first, then merge it", "Close the pull request now"];

    // `asker`: the session that asks, so the question goes into the chat; none, and it shows in the amber-edged bubble.
    static (Board Board, Guid Task) AnswerPage(Maybe<string> asker, IReadOnlyList<string>? options = null, double width = Wide)
    {
        var t = Build(width: width);
        var id = t.Tasks.Create("Ship the fix", t.Gepha, Session("s1")).Value.Id;
        Assert.True(t.Tasks.Ask(id, "OK to merge?", asker, options ?? Answers).IsSuccess);
        t.Controller.Show();
        ChooseInbox(t);
        Click(LineOf(t.Window, id));
        return (t, id);
    }

    static StackPanel AnswerRow(Board t) =>
        Inside(t.Window.InboxPageContent!).OfType<StackPanel>().Single(p => Equals(p.Tag, TasksWindow.AnswerButtonsTag));

    [Fact]
    public void The_answers_show_under_the_question_in_the_chat_and_a_click_posts_one() => StaThread.Run(() =>
    {
        var (t, id) = AnswerPage("s1");
        using var _ = t;

        var row = AnswerRow(t);
        Assert.Equal(Answers, Inside(row).OfType<Button>().Select(b => AutomationProperties.GetName(b)));
        Assert.All(Inside(row).OfType<Button>(), b =>
        {
            Assert.True(b.Focusable);
            Assert.True(b.IsTabStop);
        });
        // Under the question's own text, in its comment.
        var question = Inside(t.Window.InboxPageContent!).OfType<RichTextBox>()
            .First(b => new System.Windows.Documents.TextRange(b.Document.ContentStart, b.Document.ContentEnd).Text.Contains("OK to merge?"));
        Assert.Same(LogicalTreeHelper.GetParent(question), LogicalTreeHelper.GetParent(row));

        Click(ButtonNamed(row, Answers[1]));

        Assert.Contains(t.Tasks.Board.Tasks.Single(x => x.Id == id).Comments, c => c.Markdown == Answers[1] && c.SessionId.HasNoValue);
    });

    // A question the chat does not show (no question comment): the buttons go in its amber-edged bubble.
    [Fact]
    public void The_answers_show_in_the_question_bubble_when_the_chat_does_not_carry_it() => StaThread.Run(() =>
    {
        var (t, _) = AnswerPage(Maybe<string>.None);
        using var __ = t;

        var bubble = Inside(t.Window.InboxPageContent!).OfType<Border>().Single(b => Equals(b.Tag, TasksWindow.QuestionBubbleTag));
        Assert.Contains(AnswerRow(t), Inside(bubble));
    });

    // Buttons drawn for an older question are refused, and the page says so; nothing is posted.
    [Fact]
    public void A_click_on_the_answers_of_a_replaced_question_is_refused() => StaThread.Run(() =>
    {
        var (t, id) = AnswerPage("s1");
        using var _ = t;
        var stale = ButtonNamed(AnswerRow(t), Answers[0]);
        // A newer question with other answers replaces the one the button was drawn for.
        Assert.True(t.Tasks.Ask(id, "Which branch?", Maybe<string>.None, ["main", "release"]).IsSuccess);
        var comments = t.Tasks.Board.Tasks.Single(x => x.Id == id).Comments.Count;

        Click(stale);

        Assert.Equal(comments, t.Tasks.Board.Tasks.Single(x => x.Id == id).Comments.Count);
        Assert.Equal(["main", "release"], Inside(AnswerRow(t)).OfType<Button>().Select(b => AutomationProperties.GetName(b)));
        Assert.True(Inside(AnswerRow(t)).OfType<TextBlock>().Count() > 2);
    });

    // Four answers of forty characters on a narrow panel: all there, wrapped onto rows, none cut.
    [Fact]
    public void Long_answers_wrap_on_a_narrow_panel_and_are_never_cut() => StaThread.Run(() =>
    {
        string[] long40 = [new string('a', 40), new string('b', 40), new string('c', 40), new string('d', 40)];
        var (t, _) = AnswerPage("s1", long40, width: 380);
        using var __ = t;
        t.Window.UpdateLayout();

        var row = AnswerRow(t);
        Assert.Equal(long40, Inside(row).OfType<Button>().Select(b => AutomationProperties.GetName(b)));
        Assert.All(Inside(row).OfType<Button>().Select(b => Inside(b).OfType<TextBlock>().Single()), words =>
        {
            Assert.Equal(TextWrapping.Wrap, words.TextWrapping);
            Assert.Equal(TextTrimming.None, words.TextTrimming);
        });
        Assert.IsType<WrapPanel>(Inside(row).OfType<WrapPanel>().First());
    });
}
