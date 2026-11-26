using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Deskbar.App;

// THE ANSWER BUTTONS UNDER A QUESTION (task asks with options, AnswerOptions in Core). When the question a task waits on
// came with answers (TaskRowView.QuestionOptions, at most four), the page draws them as buttons right under the question,
// wherever the question shows: in the chat, as its question comment (the comment Asks marks as the live one), or in the
// amber-edged bubble when the chat does not show it (TasksWindow.InboxPage.cs, DetailShown). One drawing for the panel's
// page and a task window's, both pages only: a List line's thread keeps the question as text.
//
// A CLICK posts that answer as the user's comment (TasksBoard.AnswerWithOption), passing the very list the button was
// drawn from, so a click on buttons a newer question has replaced is refused instead of answering the wrong question; the
// refusal is one line under the buttons. The page's guard after a redraw applies (ClickIgnored), so a click meant for
// something the redraw moved does not land on an answer.
//
// THE LOOK: a rounded, edged button per answer, its text in full and wrapped inside the button, never cut (an answer is at
// most AnswerOptions' limit, 40 characters, which fits on one line of a narrow panel); the buttons wrap onto more rows
// when the panel is narrow. Ink on the raised fill, well above 4.5 to 1, and the buttons say what they are by their words.
// THE KEYBOARD: Tab reaches each button, Enter or Space presses the focused one.
public partial class TasksWindow
{
    internal const string AnswerButtonsTag = "answer-buttons";

    // What a click met when it was refused, by task, with the question and options on show WHEN it was refused (a click on
    // buttons a newer question replaced is refused, and the line stands under the new question's buttons, which is where
    // the owner looks). It is shown only under that question and those options: when either changes again it goes, and a
    // new question starts clean (tester finding L3).
    readonly Dictionary<Guid, (string Key, string Note)> answerNote = [];

    // The question and its options as the buttons drawn for them know them.
    static string AnswerKey(TaskRowView row) => row.Detail + "\u0002" + string.Join('\u0001', row.QuestionOptions);

    // The refusal that belongs to what the task asks now, or none.
    string AnswerNoteFor(TaskRowView? row) =>
        row is not null && answerNote.TryGetValue(row.Id, out var shown) && shown.Key == AnswerKey(row) ? shown.Note : "";

    // Part of the page's redraw signature: the question, its options and the note that goes with them.
    string AnswersSignature(TasksView view, Guid id) =>
        AllRows(view).FirstOrDefault(r => r.Id == id) is { } row ? "|ans:" + AnswerKey(row) + "|" + AnswerNoteFor(row) : "|ans:";

    /// <summary>The buttons for the page's task's question, or null when it offers none or this is not its page.</summary>
    FrameworkElement? AnswerButtons(Guid taskId)
    {
        if (inboxPage != taskId || latest is not { } view || AllRows(view).FirstOrDefault(r => r.Id == taskId) is not { } row) return null;
        var options = row.QuestionOptions;
        if (options.Count == 0) return null;
        var stack = new StackPanel { Tag = AnswerButtonsTag, Margin = new Thickness(0, 8, 0, 0) };
        var buttons = new WrapPanel();
        AutomationProperties.SetName(buttons, "Answers");
        for (var i = 0; i < options.Count; i++)
        {
            var index = i;
            var words = Text(options[i], 12.5, InboxLook.Ink, FontWeights.SemiBold);
            words.TextWrapping = TextWrapping.Wrap;
            words.TextTrimming = TextTrimming.None;
            words.MaxWidth = 320;
            var button = InboxButton(words, options[i], () => AnswerWith(taskId, index, options), new Thickness(12, 5, 12, 6),
                InboxLook.Raised, 12, InboxLook.StrongLine, ClickIgnored);
            button.Margin = new Thickness(0, 0, 6, 6);
            button.Focusable = true;
            button.IsTabStop = true;
            button.ToolTip = "Answer with this. It goes to Claude as your comment.";
            buttons.Children.Add(button);
        }
        stack.Children.Add(buttons);
        if (AnswerNoteFor(row) is { Length: > 0 } note)
        {
            var line = Text(note, 12, InboxLook.Ink2, margin: new Thickness(2, 0, 2, 0));
            line.TextWrapping = TextWrapping.Wrap;
            stack.Children.Add(line);
        }
        return stack;
    }

    /// <summary>For the tests: a click on answer <paramref name="index"/> of the buttons the page draws for the task now.</summary>
    internal void ClickAnswer(Guid taskId, int index)
    {
        if (RowFor(taskId) is { } row) AnswerWith(taskId, index, row.QuestionOptions);
    }

    void AnswerWith(Guid taskId, int index, IReadOnlyList<string> drawn)
    {
        if (Actions?.AnswerWithOption is not { } answer) return;
        var result = answer(taskId, index, drawn);
        // The refusal belongs to the question on show now, which is not the drawn one when the click was a stale one.
        if (result.IsFailure) answerNote[taskId] = (RowFor(taskId) is { } now ? AnswerKey(now) : "", result.Error);
        else answerNote.Remove(taskId);
        if (latest is not null) Draw(latest);
    }
}
