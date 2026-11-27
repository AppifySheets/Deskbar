using System.Windows.Automation;
using System.Windows.Controls;
using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.Windows.Tests;

// A TASK'S PAGE KEEPS ITS WHOLE CHAT (task 5ee182cf; Petre: "chat shouldn't collapse on me in the inbox task view", "i mean
// old chat messages hiding, settling"). On the Inbox page neither fold applies: settled comments stay in their places and
// older comments are not cut to the latest three. The List's open thread keeps both folds.
public partial class TasksBoardTests
{
    // Six comments, the first two settled, as a session's `task answered` leaves them.
    static Guid TaskWithSettledChat(Board t)
    {
        var id = t.Tasks.Create("Feature mail", t.Gepha, Session("s1")).Value.Id;
        Enumerable.Range(1, 6).ToList().ForEach(i => Assert.True(t.Tasks.Comment(id, Maybe<string>.None, $"Comment {i}", wakes: false).IsSuccess));
        t.Tasks.Board.Tasks.Single(x => x.Id == id).Comments.Take(2).ToList()
            .ForEach(c => Assert.True(t.Tasks.SettleComment(c.Id, true).IsSuccess));
        return id;
    }

    static bool ShowsFold(Board t, string name) =>
        Inside(t.Window).OfType<Button>().Any(b => AutomationProperties.GetName(b) == name && b.IsVisible);

    [Fact]
    public void A_tasks_page_shows_every_comment_settled_ones_included() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = TaskWithSettledChat(t);
        t.Controller.Show();
        ChooseInbox(t);

        t.Controller.ShowTask(id);

        Assert.Equal(id, t.Window.InboxPageShown);
        Assert.Equal(t.Tasks.Board.Tasks.Single(x => x.Id == id).Comments.Select(c => c.Id), t.Window.ShownComments(id));
        Assert.False(ShowsFold(t, "Settled comments"));
        Assert.False(ShowsFold(t, "Earlier comments"));
    });

    [Fact]
    public void A_comment_settled_while_the_page_is_open_stays_in_the_chat() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = TaskWithSettledChat(t);
        t.Controller.Show();
        ChooseInbox(t);
        t.Controller.ShowTask(id);

        var last = t.Tasks.Board.Tasks.Single(x => x.Id == id).Comments[^1].Id;
        Assert.True(t.Tasks.SettleComment(last, true).IsSuccess);

        Assert.Equal(6, t.Window.ShownComments(id).Count);
        Assert.Contains(last, t.Window.ShownComments(id));
        // Redrawn with it settled: the bubble holding its words is dimmed in place, not folded away.
        static string TextOf(RichTextBox b) => new System.Windows.Documents.TextRange(b.Document.ContentStart, b.Document.ContentEnd).Text;
        var dimmed = Inside(t.Window).OfType<System.Windows.UIElement>()
            .Where(e => e.Opacity < 1 && Inside(e).OfType<RichTextBox>().Any(b => TextOf(b).Contains("Comment 6")))
            .ToList();
        Assert.NotEmpty(dimmed);
        Assert.Equal(0.55, dimmed[0].Opacity, 2);
    });

    [Fact]
    public void The_lists_open_thread_still_folds_settled_and_earlier_comments() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = TaskWithSettledChat(t);
        t.Controller.Show();

        t.Controller.ShowTask(id);

        Assert.Null(t.Window.InboxShown);
        Assert.Equal(3, t.Window.ShownComments(id).Count);
        Assert.True(ShowsFold(t, "Settled comments"));
        Assert.True(ShowsFold(t, "Earlier comments"));
    });
}
