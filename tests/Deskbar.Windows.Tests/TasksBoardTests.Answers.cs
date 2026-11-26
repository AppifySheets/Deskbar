using System.Collections.Immutable;
using System.Windows.Controls;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core.Tasks;

namespace Deskbar.Windows.Tests;

// The answer options of a question (the inbox's answer buttons, Core half in AnswerOptionsTests): the row view carries
// them next to the question, and TasksBoard.AnswerWithOption posts one as the user's comment through the road a typed
// comment takes. No drawing here: the buttons are a later change, and these tests call the operation as a click will.
public partial class TasksBoardTests
{
    static readonly string[] MergeOrNot = ["Merge it", "Not yet"];

    // A task whose session s1 asked "Merge it?" offering MergeOrNot.
    static Guid AskedWithOptions(Board t, IReadOnlyList<string>? options = null)
    {
        var id = t.Tasks.Create("Tasks redesign", t.Gepha, Session("s1")).Value.Id;
        Assert.True(t.Tasks.Ask(id, "Merge the pull request?", options: options ?? MergeOrNot).IsSuccess);
        return id;
    }

    static TaskItem Of(Board t, Guid id) => t.Tasks.Board.Tasks.Single(x => x.Id == id);

    [Fact]
    public void A_row_carries_the_options_of_the_question_and_none_without_one() => StaThread.Run(() =>
    {
        using var t = Build();
        var asked = AskedWithOptions(t);
        var plain = t.Tasks.Create("Other work", t.Gepha, Session("s2")).Value.Id;
        t.Tasks.Ask(plain, "Which colour?");

        Assert.Equal(MergeOrNot, t.Controller.Row(Of(t, asked)).QuestionOptions);
        Assert.Empty(t.Controller.Row(Of(t, plain)).QuestionOptions);

        t.Tasks.Answered("s1");
        Assert.Empty(t.Controller.Row(Of(t, asked)).QuestionOptions);
    });

    [Fact]
    public void Answering_with_an_option_posts_its_exact_words_as_the_users_comment_and_wakes_like_a_typed_one() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = AskedWithOptions(t);
        var heard = new List<(Guid TaskId, TaskComment Comment)>();
        using var subscription = t.Tasks.Commented.Subscribe(heard.Add);

        var result = t.Controller.AnswerWithOption(id, 1, MergeOrNot);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error : "");
        // The same announcement a typed comment makes, which is what the CommentWaker wakes the session on.
        var announced = Assert.Single(heard);
        Assert.Equal(id, announced.TaskId);
        Assert.Equal("Not yet", announced.Comment.Markdown);
        Assert.True(announced.Comment.SessionId.HasNoValue);
        var comment = Of(t, id).Comments.Last();
        Assert.Equal(announced.Comment.Id, comment.Id);
        Assert.False(comment.BySession);
        Assert.False(comment.Asks);
        // The thread opens to show it, as it does for a typed comment.
        Assert.True(t.Controller.Row(Of(t, id)).Expanded);
    });

    [Fact]
    public void An_option_answer_is_a_reply_that_sets_the_question_aside_like_typed_words() => StaThread.Run(() =>
    {
        using var t = Build();
        var typed = AskedWithOptions(t);
        var clicked = t.Tasks.Create("Second task", t.Gepha, Session("s2")).Value.Id;
        t.Tasks.Ask(clicked, "Merge the pull request?", options: MergeOrNot);
        var working = new HashSet<string> { "s1", "s2" };

        Assert.True(t.Controller.Comment(typed, "Merge it").IsSuccess);
        Assert.True(t.Controller.AnswerWithOption(clicked, 0, MergeOrNot).IsSuccess);

        // RepliedTasks sees the two replies alike: both questions are set aside while their sessions work.
        var found = RepliedTasks.SetAside(t.Tasks.Board, working, ImmutableDictionary<string, DateTimeOffset>.Empty)
            .Select(f => f.TaskId).ToList();
        Assert.Contains(typed, found);
        Assert.Contains(clicked, found);
        Assert.Equal(Of(t, typed).Comments.Last().Markdown, Of(t, clicked).Comments.Last().Markdown);
    });

    [Fact]
    public void The_options_stay_with_the_question_after_a_click_until_it_is_set_aside_or_settled() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = AskedWithOptions(t);

        Assert.True(t.Controller.AnswerWithOption(id, 0, MergeOrNot).IsSuccess);

        // The question is the session's to settle (task answered), so until it does the options are still the question's.
        Assert.Equal(MergeOrNot, Of(t, id).QuestionOptions);
        Assert.Equal(TaskStatus.WaitingOnYou, Of(t, id).Status);
    });

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void An_index_out_of_range_posts_nothing_and_says_so_on_the_line(int index) => StaThread.Run(() =>
    {
        using var t = Build();
        var id = AskedWithOptions(t);
        t.Controller.Show();
        var before = Of(t, id).Comments.Count;

        var result = t.Controller.AnswerWithOption(id, index, MergeOrNot);

        Assert.True(result.IsFailure);
        Assert.Equal(before, Of(t, id).Comments.Count);
        Assert.Contains("no answer number", LineProblemOf(t.Window, id));
    });

    [Fact]
    public void Options_that_changed_since_the_row_was_drawn_post_nothing() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = AskedWithOptions(t);
        t.Controller.Show();
        // The session asks again with other words before the click.
        Assert.True(t.Tasks.Ask(id, "Merge the pull request?", options: ["Merge it", "Wait for review"]).IsSuccess);
        var before = Of(t, id).Comments.Count;

        var result = t.Controller.AnswerWithOption(id, 0, MergeOrNot);

        Assert.True(result.IsFailure);
        Assert.Equal(TaskBoard.OptionsChanged, result.Error);
        Assert.Equal(before, Of(t, id).Comments.Count);
        Assert.Equal(TaskBoard.OptionsChanged, LineProblemOf(t.Window, id));
    });

    [Fact]
    public void Options_of_a_question_already_answered_post_nothing() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = AskedWithOptions(t);
        t.Tasks.Answered("s1");
        var before = Of(t, id).Comments.Count;

        var result = t.Controller.AnswerWithOption(id, 0, MergeOrNot);

        Assert.True(result.IsFailure);
        Assert.Equal(before, Of(t, id).Comments.Count);
    });

    // L3: a refusal stands under the buttons of the question on show when it was refused, and goes when that question or its
    // answers change again.
    static string[] AnswerTexts(Board t) =>
        Inside(Inside(t.Window.InboxPageContent!).OfType<StackPanel>().Single(p => Equals(p.Tag, TasksWindow.AnswerButtonsTag))).OfType<TextBlock>().Select(x => x.Text).ToArray();

    [Fact]
    public void A_refusal_goes_when_the_question_or_its_answers_change_again() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = AskedWithOptions(t);
        t.Controller.Show();
        ChooseInbox(t);
        t.Controller.ShowTask(id);
        var stale = ButtonNamed(t.Window.InboxPageContent!, "Merge it");
        Assert.True(t.Tasks.Ask(id, "Merge the pull request?", options: ["Merge it", "Wait for review"]).IsSuccess);

        // A click on the buttons the new question replaced is refused, and the line stands under the new buttons.
        Click(stale);
        Assert.Contains(AnswerTexts(t), text => text.Contains(TaskBoard.OptionsChanged));

        // The question changes again: a fresh start, no refusal carried over.
        Assert.True(t.Tasks.Ask(id, "Merge the pull request?", options: ["Merge it", "Close it"]).IsSuccess);
        Assert.DoesNotContain(AnswerTexts(t), text => text.Contains(TaskBoard.OptionsChanged));
        Assert.True(HasButtonNamed(t.Window.InboxPageContent!, "Close it"));
    });

    [Fact]
    public void A_refusal_for_the_buttons_on_show_stands_until_the_question_changes() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = AskedWithOptions(t);
        t.Controller.Show();
        ChooseInbox(t);
        t.Controller.ShowTask(id);

        // A click that is refused for the buttons the page draws now (an answer number they do not have).
        t.Window.ClickAnswer(id, 7);
        Assert.Contains(AnswerTexts(t), text => text.Contains("no answer number"));

        // Another question, with other answers: a fresh start.
        Assert.True(t.Tasks.Ask(id, "Another question?", options: ["Yes", "No"]).IsSuccess);
        Assert.DoesNotContain(AnswerTexts(t), text => text.Contains("no answer number"));
    });}
