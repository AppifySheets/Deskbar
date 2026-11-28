using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Deskbar.App;
using Deskbar.Core.Tasks;

namespace Deskbar.Windows.Tests;

// The permission card in a task's own window (Allow / Deny from a task's page): the window draws the panel's page through
// a TasksWindow of its own, with the same actions, so the card is there too, and its click writes the answer.
public partial class TaskDetailWindowInboxTests
{
    [Fact]
    public void A_task_window_shows_the_card_and_its_click_answers() => StaThread.Run(() =>
    {
        using var t = Build();
        var box = Path.Combine(Path.GetTempPath(), "deskbar-perm-window-" + Guid.NewGuid().ToString("N"));
        try
        {
            var desk = new PermissionDesk(box, () => Start.AddHours(1));
            t.Board.Permissions = desk;
            var id = t.Tasks.Create("Ship the fix", t.Sparrow, Session("s1")).Value.Id;
            var (window, page) = Open(t, id);
            Directory.CreateDirectory(Path.Combine(box, "req-1-1"));
            Assert.True(desk.Register("req-1-1", new PermissionRequest("s1", "Bash", @"C:\repos\TaskSpaces", "default",
                [new PermissionField("command", "git push fork feature/x")])).IsSuccess);

            var card = Inside(window).OfType<System.Windows.Controls.Border>().Single(b => b.Tag as string == TasksWindow.PermissionCardTag);
            Assert.Contains(Inside(card).OfType<TextBlock>(), b => b.Text == "git push fork feature/x");
            Click(card, TasksWindow.DenyText);

            Assert.EndsWith(" deny\n", File.ReadAllText(Path.Combine(box, "req-1-1", PermissionDesk.AnswerFile)));
            Assert.DoesNotContain(Inside(window).OfType<System.Windows.Controls.Border>(), b => b.Tag as string == TasksWindow.PermissionCardTag);
            Assert.NotNull(page);
        }
        finally
        {
            if (Directory.Exists(box)) Directory.Delete(box, true);
        }
    });

    // --- the multiple-choice dialog's card (AskUserQuestion, task c047d0a0) --------------------------------------------

    // Two questions: a single pick of colour, and a multi-select of sizes.
    const string DialogInput =
        """{"questions":[{"question":"Pick a colour","header":"Colour","multiSelect":false,"options":[{"label":"Red","description":"warm"},{"label":"Blue","description":"cool"}]},{"question":"Which sizes?","header":"Size","multiSelect":true,"options":[{"label":"S","description":"small"},{"label":"M","description":"medium"},{"label":"L","description":"large"}]}]}""";

    // A task window with a dialog on show for the task's session, on a scratch permission box.
    static void WithDialog(Action<Rig, PermissionDesk, string, Guid, TaskDetailWindow, TasksWindow> body, Func<DateTime>? pageClock = null)
    {
        using var t = Build(pageClock: pageClock);
        var box = Path.Combine(Path.GetTempPath(), "deskbar-dialog-window-" + Guid.NewGuid().ToString("N"));
        try
        {
            var desk = new PermissionDesk(box, () => Start.AddHours(1));
            t.Board.Permissions = desk;
            var id = t.Tasks.Create("Ship the fix", t.Sparrow, Session("s1")).Value.Id;
            var (window, page) = Open(t, id);
            Directory.CreateDirectory(Path.Combine(box, "req-1-1"));
            Assert.True(desk.Register("req-1-1", new PermissionRequest("s1", "AskUserQuestion", @"C:\repos\TaskSpaces", "default",
                [new PermissionField("questions", "...")], Input: DialogInput)).IsSuccess);
            body(t, desk, box, id, window, page);
        }
        finally
        {
            if (Directory.Exists(box)) Directory.Delete(box, true);
        }
    }

    static bool IsDialogCard(System.Windows.Controls.Border b) => b.Tag as string == TasksWindow.DialogCardTag;

    static IEnumerable<System.Windows.Controls.Border> Cards(object window, string tag) =>
        Inside(window).OfType<System.Windows.Controls.Border>().Where(b => b.Tag as string == tag);

    // The answers the desk handed over: the code is in answer.txt ("<code> dialog"), and the line is fetched with it as
    // the script does (PermissionDesk.Fetch); no file holds the line.
    static JsonNode DialogAnswersWritten(PermissionDesk desk, string box) =>
        JsonNode.Parse(desk.Fetch("req-1-1", File.ReadAllText(Path.Combine(box, "req-1-1", PermissionDesk.AnswerFile)).Split(' ')[0]).Value)!
            ["hookSpecificOutput"]!["decision"]!["updatedInput"]!["answers"]!;

    [Fact]
    public void A_pending_dialog_draws_the_dialog_card_and_not_the_permission_card() => StaThread.Run(() => WithDialog((t, desk, box, id, window, page) =>
    {
        var card = Cards(window, TasksWindow.DialogCardTag).Single();
        Assert.Empty(Cards(window, TasksWindow.PermissionCardTag));
        Assert.Contains(Inside(card).OfType<TextBlock>(), b => b.Text == "Pick a colour");
        Assert.Contains(Inside(card).OfType<TextBlock>(), b => b.Text == "Which sizes?");
        // Five options, one Other box per question.
        Assert.Equal(["Red", "Blue", "S", "M", "L"],
            Inside(card).OfType<Button>().Where(b => b.Tag as string == TasksWindow.DialogOptionTag).Select(b => AutomationProperties.GetName(b)));
        Assert.Equal(2, Inside(card).OfType<TextBox>().Count(b => b.Tag as string == TasksWindow.DialogOtherTag));
        Assert.True(HasButtonNamed(card, TasksWindow.SendAnswersText));
        Assert.True(HasButtonNamed(card, TasksWindow.AnswerInTerminalText));
        Assert.NotNull(page);
    }));

    // Send stays off until every question has an answer; picks then Send hand the labels to the desk, which writes the
    // line the hook prints, and the card goes.
    [Fact]
    public void Send_waits_for_every_question_and_then_answers_with_the_picked_labels() => StaThread.Run(() => WithDialog((t, desk, box, id, window, page) =>
    {
        var card = Cards(window, TasksWindow.DialogCardTag).Single();
        Assert.False(ButtonNamed(card, TasksWindow.SendAnswersText).IsEnabled);

        Click(card, "Red");
        Assert.False(ButtonNamed(card, TasksWindow.SendAnswersText).IsEnabled);

        // A multi-select takes several picks, joined in the options' order.
        Click(card, "M");
        Click(card, "S");
        var send = ButtonNamed(card, TasksWindow.SendAnswersText);
        Assert.True(send.IsEnabled);
        // A picked option is named as picked.
        Assert.True(HasButtonNamed(card, "Red, picked"));

        Click(card, TasksWindow.SendAnswersText);

        var answers = DialogAnswersWritten(desk, box);
        Assert.Equal("Red", (string)answers["Pick a colour"]!);
        Assert.Equal("S, M", (string)answers["Which sizes?"]!);
        Assert.EndsWith(" dialog\n", File.ReadAllText(Path.Combine(box, "req-1-1", PermissionDesk.AnswerFile)));
        Assert.Empty(Cards(window, TasksWindow.DialogCardTag));
        Assert.Empty(desk.All);
    }));

    // A typed Other answer stands in for a pick of a single-choice question.
    [Fact]
    public void An_other_answer_counts_as_the_questions_answer() => StaThread.Run(() => WithDialog((t, desk, box, id, window, page) =>
    {
        var card = Cards(window, TasksWindow.DialogCardTag).Single();
        var others = Inside(card).OfType<TextBox>().Where(b => b.Tag as string == TasksWindow.DialogOtherTag).ToList();
        others[0].Text = "ფერი";
        Click(card, "L");

        Click(card, TasksWindow.SendAnswersText);

        var answers = DialogAnswersWritten(desk, box);
        Assert.Equal("ფერი", (string)answers["Pick a colour"]!);
        Assert.Equal("L", (string)answers["Which sizes?"]!);
    }));

    // The comment box answers an open dialog instead of posting a comment: a question picked on the card keeps its pick,
    // the others take the words.
    [Fact]
    public void The_comment_box_answers_an_open_dialog_instead_of_posting() => StaThread.Run(() => WithDialog((t, desk, box, id, window, page) =>
    {
        var card = Cards(window, TasksWindow.DialogCardTag).Single();
        Click(card, "Blue");

        Assert.True(page.TypeComment(id, "M and L", Key.Enter));

        var answers = DialogAnswersWritten(desk, box);
        Assert.Equal("Blue", (string)answers["Pick a colour"]!);
        Assert.Equal("M and L", (string)answers["Which sizes?"]!);
        Assert.DoesNotContain(t.Tasks.Board.Tasks.Single(x => x.Id == id).Comments, c => c.Markdown == "M and L");
        Assert.Empty(Cards(window, TasksWindow.DialogCardTag));
        Assert.Empty(desk.All);
    }));

    // Nothing picked: the words go to every question.
    [Fact]
    public void The_comment_box_answers_every_question_when_nothing_is_picked() => StaThread.Run(() => WithDialog((t, desk, box, id, window, page) =>
    {
        Assert.True(page.TypeComment(id, "Whatever you think", Key.Enter));

        var answers = DialogAnswersWritten(desk, box);
        Assert.Equal("Whatever you think", (string)answers["Pick a colour"]!);
        Assert.Equal("Whatever you think", (string)answers["Which sizes?"]!);
    }));

    // A card only just drawn answers nothing from the comment box (the arming delay a click gets too: a reply typed
    // while the card appears is a comment, not an answer), and answers once it has been on show past ArmTime. The page
    // clock is frozen here so "just drawn" and "a moment later" are exact.
    [Fact]
    public void The_comment_box_does_not_answer_a_dialog_before_the_arming_time() => StaThread.Run(() =>
    {
        var now = Start.UtcDateTime;
        WithDialog((t, desk, box, id, window, page) =>
        {
            Assert.True(page.TypeComment(id, "too early", Key.Enter));

            // Posted as a comment; the dialog is still waiting and nothing was handed over.
            Assert.Contains(t.Tasks.Board.Tasks.Single(x => x.Id == id).Comments, c => c.Markdown == "too early");
            Assert.Single(desk.All);
            Assert.False(File.Exists(Path.Combine(box, "req-1-1", PermissionDesk.AnswerFile)));

            now += TasksWindow.ArmTime + TimeSpan.FromMilliseconds(100);
            Assert.True(page.TypeComment(id, "now it counts", Key.Enter));

            Assert.Equal("now it counts", (string)DialogAnswersWritten(desk, box)["Pick a colour"]!);
            Assert.DoesNotContain(t.Tasks.Board.Tasks.Single(x => x.Id == id).Comments, c => c.Markdown == "now it counts");
        }, pageClock: () => now);
    });

    // A redraw of the page (here, a new comment on the task) rebuilds the card, and the Other box keeps what was typed
    // in it, and the keyboard when it had it (RememberDialogFocus / RefocusDialogOther).
    [Fact]
    public void A_redraw_keeps_the_other_boxs_text_and_focus() => StaThread.Run(() => WithDialog((t, desk, box, id, window, page) =>
    {
        var other = Inside(window).OfType<TextBox>().First(b => b.Tag as string == TasksWindow.DialogOtherTag);
        other.Text = "typed so far";
        other.Focus();
        var hadFocus = other.IsKeyboardFocusWithin;
        other.CaretIndex = 5;

        Assert.True(t.Tasks.Comment(id, CSharpFunctionalExtensions.Maybe<string>.None, "a redraw").IsSuccess);

        var after = Inside(window).OfType<TextBox>().First(b => b.Tag as string == TasksWindow.DialogOtherTag);
        Assert.NotSame(other, after);
        Assert.Equal("typed so far", after.Text);
        // The hidden desktop may not give the window the keyboard at all; when the box had it, the new one must too.
        if (hadFocus)
        {
            Assert.True(after.IsKeyboardFocusWithin);
            Assert.Equal(5, after.CaretIndex);
        }
    }));

    // With no dialog waiting the same box posts a comment as ever.
    [Fact]
    public void Without_a_dialog_the_comment_box_posts_a_comment() => StaThread.Run(() =>
    {
        using var t = Build();
        var id = t.Tasks.Create("Ship the fix", t.Sparrow, Session("s1")).Value.Id;
        var (_, page) = Open(t, id);

        Assert.True(page.TypeComment(id, "A plain word", Key.Enter));

        Assert.Contains(t.Tasks.Board.Tasks.Single(x => x.Id == id).Comments, c => c.Markdown == "A plain word");
    });
}
