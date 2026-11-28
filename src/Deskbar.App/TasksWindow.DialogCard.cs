using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// THE DIALOG CARD (task c047d0a0, Petre: "model this in the task so i don't have to open claude code"; the picked mockup
// https://claude.ai/artifact/5ked6RkY6E1LVmdpHomKba). Claude Code's multiple-choice dialog, AskUserQuestion, reaches the
// permission desk through the same hook as a tool permission (PermissionDesk, DialogAsk), and this card stands where the
// Allow / Deny card stands: docked at the bottom of the task's page, in the panel and in a task window alike
// (TasksWindow.PermissionCard draws one or the other per question).
//
// WHAT IT SHOWS: every question of the dialog at once, one under the other (the terminal shows them as tabs; on a page
// they fit and nothing is hidden), each with its header as a chip, its text, and its options as rows: the label, a
// "Recommended" tag where Claude wrote "(Recommended)" after it, the description under it. Round marks for one pick, square
// ones where the question allows several. The picked option's preview, when Claude gave one, opens under it in a
// monospaced box. Last, an "Other" box for an answer of the user's own words, which is sent as the answer itself.
//
// THE BUTTONS: "Send answers", enabled once every question has an answer, and "Answer in the terminal", which hands the
// dialog back (the hook prints nothing). The terminal's own dialog is up the whole time; whichever is answered first wins,
// as with the permission card. The same arming delay as the permission card guards the buttons and the options, so a
// click meant for what was under the pointer when the card appeared does not land on it.
//
// WHERE THE PICKS LIVE: in this window, by slot (dialogPicks, dialogOther), so a redraw of the page keeps them. A pick or a
// keystroke repaints the card's own rows in place rather than redrawing the page, which would take the Other box's focus.
public partial class TasksWindow
{
    internal const string DialogCardTag = "dialog-card";
    internal const string DialogOptionTag = "dialog-option";
    internal const string DialogOtherTag = "dialog-other";
    internal const string DialogPreviewTag = "dialog-preview";
    internal const string SendAnswersText = "Send answers";
    const string RecommendedSuffix = "(Recommended)";

    // The picks by slot, then by question text; the Other box's text by slot, then by question text.
    readonly Dictionary<string, Dictionary<string, List<string>>> dialogPicks = [];
    readonly Dictionary<string, Dictionary<string, string>> dialogOther = [];

    // The task each card's slot belongs to, so a task's page can drop what it kept for cards no longer on show.
    readonly Dictionary<string, Guid> dialogTask = [];

    // THE OTHER BOX KEEPS THE KEYBOARD THROUGH A REDRAW (the branch's review, finding 2): every draw builds the card anew,
    // so the box that had the keyboard ("slot\nquestion") and its caret are noted as the draw begins (DrawRows) and the new
    // box gets them back once it ends, as the comment box does.
    readonly Dictionary<string, TextBox> dialogOtherBoxes = [];
    (string Key, int Caret)? dialogFocusWas;

    void RememberDialogFocus() =>
        dialogFocusWas = dialogOtherBoxes.FirstOrDefault(b => b.Value.IsKeyboardFocusWithin) is { Value: not null } had
            ? (had.Key, had.Value.CaretIndex)
            : null;

    void RefocusDialogOther()
    {
        if (dialogFocusWas is not { } was || !dialogOtherBoxes.TryGetValue(was.Key, out var box) || !box.IsVisible) return;
        box.Focus();
        box.CaretIndex = Math.Min(was.Caret, box.Text.Length);
    }

    // What a task's page kept for dialog cards that are no longer on show (answered in the terminal, handed back,
    // expired) goes when the page draws its cards again (the branch's review, finding 5).
    void ForgetEndedDialogs(Guid task, IReadOnlyList<PermissionDesk.Pending> onShow) =>
        dialogTask.Where(d => d.Value == task && onShow.All(p => p.Slot != d.Key)).Select(d => d.Key).ToList().ForEach(ForgetDialog);

    FrameworkElement DialogCard(Guid task, PermissionDesk.Pending question, DialogAsk dialog)
    {
        if (!permissionShownAt.ContainsKey(question.Slot)) permissionShownAt[question.Slot] = ClickClock();
        dialogTask[question.Slot] = task;
        var picks = dialogPicks.TryGetValue(question.Slot, out var p) ? p : dialogPicks[question.Slot] = [];
        var other = dialogOther.TryGetValue(question.Slot, out var o) ? o : dialogOther[question.Slot] = [];
        var card = new StackPanel();
        card.Children.Add(Row(InboxMarks.Icon("ask", 14, InboxLook.Amber, InboxLook.Surface),
            Text("CLAUDE ASKS YOU", 11, InboxLook.Amber, FontWeights.SemiBold)));

        // The Send button is made first so every pick and keystroke below can enable it.
        var send = InboxButton(Text(SendAnswersText, 12.5, InboxLook.Background, FontWeights.SemiBold), SendAnswersText,
            () => SendDialog(task, question.Slot, dialog), new Thickness(12, 5, 12, 6), InboxLook.Ink, 12,
            ignoredWhen: () => ClickIgnored() || Arming(question.Slot));
        void Ready() => send.IsEnabled = Answers(dialog, picks, other).Count == dialog.Questions.Count;

        foreach (var item in dialog.Questions) card.Children.Add(DialogQuestionBlock(question.Slot, item, picks, other, Ready));

        var buttons = new WrapPanel { Margin = new Thickness(0, 9, 0, 0) };
        send.Margin = new Thickness(0, 0, 6, 4);
        buttons.Children.Add(send);
        buttons.Children.Add(PermissionButton(task, question, TerminalChoice));
        card.Children.Add(buttons);
        card.Children.Add(Text("Or write a reply in the comment box: it answers every question you have not picked.",
            11.5, InboxLook.Ink3, margin: new Thickness(0, 4, 0, 0)).Wrapped());
        Ready();

        var frame = new Border
        {
            Tag = DialogCardTag,
            Background = InboxLook.Surface,
            BorderBrush = InboxLook.AskEdge,
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 10, 12, 11),
            Margin = new Thickness(0, 0, 0, 8),
            Child = card,
        };
        AutomationProperties.SetName(frame, "Claude asks you");
        return frame;
    }

    bool Arming(string slot) => ClickClock() - permissionShownAt.GetValueOrDefault(slot) < ArmTime;

    // One question: the chip and the text, the option rows, the Other box.
    FrameworkElement DialogQuestionBlock(string slot, DialogAsk.Item item, Dictionary<string, List<string>> picks,
        Dictionary<string, string> other, Action ready)
    {
        var block = new StackPanel { Margin = new Thickness(0, 9, 0, 0) };
        var head = new WrapPanel();
        if (item.Header.Length > 0)
            head.Children.Add(new Border
            {
                Background = InboxLook.Raised,
                BorderBrush = InboxLook.StrongLine,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(7, 0, 7, 1),
                Margin = new Thickness(0, 1, 8, 2),
                VerticalAlignment = VerticalAlignment.Top,
                Child = Text(item.Header, 11, InboxLook.Ink2),
            });
        block.Children.Add(head);
        block.Children.Add(Text(item.Question, 13, InboxLook.Ink, margin: new Thickness(0, 2, 0, 6)).Wrapped());

        var rows = new StackPanel();
        block.Children.Add(rows);
        var otherBox = new TextBox
        {
            Tag = DialogOtherTag,
            Text = other.GetValueOrDefault(item.Question, ""),
            FontSize = 12.5,
            Foreground = InboxLook.Ink,
            Background = InboxLook.Raised,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 5, 8, 5),
            Margin = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = false,
        };
        AutomationProperties.SetName(otherBox, $"Other answer to {Label(item)}");
        dialogOtherBoxes[$"{slot}\n{item.Question}"] = otherBox;
        var hint = Text("Other: type your own answer", 12.5, InboxLook.Ink3, margin: new Thickness(10, 0, 0, 0));
        hint.IsHitTestVisible = false;
        var otherArea = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        otherArea.Children.Add(otherBox);
        otherArea.Children.Add(hint);

        // Repaints this question's rows from the picks: what is picked, and the preview under the picked option.
        void Paint()
        {
            rows.Children.Clear();
            var mine = picks.GetValueOrDefault(item.Question, []);
            foreach (var option in item.Options) rows.Children.Add(OptionRow(slot, item, option, mine.Contains(option.Label), Pick));
            hint.Visibility = otherBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            ready();
        }

        // A click on an option: one pick replaces the other (and empties the Other box, whose words would otherwise be
        // the answer); several picks toggle.
        void Pick(string label)
        {
            var mine = picks.TryGetValue(item.Question, out var list) ? list : picks[item.Question] = [];
            if (item.MultiSelect)
            {
                if (!mine.Remove(label)) mine.Add(label);
            }
            else
            {
                mine.Clear();
                mine.Add(label);
                otherBox.Text = "";
            }
            Paint();
        }

        otherBox.TextChanged += (_, _) =>
        {
            other[item.Question] = otherBox.Text;
            // Typing an answer of one's own in a one-pick question takes the place of a picked option.
            if (!item.MultiSelect && otherBox.Text.Trim().Length > 0 && picks.TryGetValue(item.Question, out var list) && list.Count > 0)
                list.Clear();
            Paint();
        };
        block.Children.Add(otherArea);
        Paint();
        return block;
    }

    // One option: its mark, its label (with the Recommended tag) and description, and its preview when picked.
    FrameworkElement OptionRow(string slot, DialogAsk.Item item, DialogAsk.Option option, bool picked, Action<string> pick)
    {
        var (label, recommended) = option.Label.EndsWith(RecommendedSuffix, StringComparison.Ordinal)
            ? (option.Label[..^RecommendedSuffix.Length].TrimEnd(), true)
            : (option.Label, false);
        var words = new StackPanel();
        var title = new WrapPanel();
        title.Children.Add(Text(label, 12.5, InboxLook.Ink, FontWeights.SemiBold));
        if (recommended)
            title.Children.Add(new Border
            {
                BorderBrush = InboxLook.Green,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(6, 0, 6, 0),
                Margin = new Thickness(6, 1, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = Text("Recommended", 10.5, InboxLook.Green),
            });
        words.Children.Add(title);
        if (option.Description.Length > 0) words.Children.Add(Text(option.Description, 12, InboxLook.Ink3).Wrapped());
        if (picked && option.Preview.HasValue)
            words.Children.Add(new Border
            {
                Background = InboxLook.Background,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(9, 7, 9, 7),
                Margin = new Thickness(0, 5, 0, 1),
                Child = new TextBlock
                {
                    Tag = DialogPreviewTag,
                    Text = option.Preview.Value,
                    FontFamily = Mono,
                    FontSize = 11.5,
                    Foreground = InboxLook.Ink,
                    TextWrapping = TextWrapping.Wrap,
                },
            });

        var mark = new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(item.MultiSelect ? 3 : 7),
            BorderThickness = new Thickness(1.5),
            BorderBrush = picked ? InboxLook.Blue : InboxLook.Ink3,
            Margin = new Thickness(0, 2, 9, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = picked
                ? new Border
                {
                    Width = 7, Height = 7, CornerRadius = new CornerRadius(item.MultiSelect ? 1.5 : 3.5),
                    Background = InboxLook.Blue, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                }
                : null,
        };
        var line = new DockPanel();
        DockPanel.SetDock(mark, Dock.Left);
        line.Children.Add(mark);
        line.Children.Add(words);

        var button = InboxButton(line, option.Label, () => pick(option.Label), new Thickness(10, 7, 10, 7),
            picked ? InboxLook.RaisedHot : InboxLook.Raised, 10, picked ? InboxLook.Blue : null,
            ignoredWhen: () => ClickIgnored() || Arming(slot));
        button.Tag = DialogOptionTag;
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.Margin = new Thickness(0, 0, 0, 4);
        // Tab reaches each option and Enter picks it; Space does nothing, as on the permission card's buttons.
        button.Focusable = true;
        button.IsTabStop = true;
        button.PreviewKeyDown += (_, e) => { if (e.Key == Key.Space) e.Handled = true; };
        AutomationProperties.SetName(button, $"{label}{(picked ? ", picked" : "")}");
        return button;
    }

    static string Label(DialogAsk.Item item) => item.Header.Length > 0 ? item.Header : item.Question;

    // Every question's answer, for those that have one: the Other box's words in a one-pick question, else the picks (in
    // the options' order) followed by the Other box's words, joined as Claude Code joins several picks.
    static IReadOnlyDictionary<string, string> Answers(DialogAsk dialog, Dictionary<string, List<string>> picks, Dictionary<string, string> other) =>
        dialog.Questions
            .Select(q =>
            {
                var typed = other.GetValueOrDefault(q.Question, "").Trim();
                var picked = q.Options.Select(o => o.Label).Where(l => picks.GetValueOrDefault(q.Question, []).Contains(l));
                var parts = q.MultiSelect ? picked.Append(typed) : typed.Length > 0 ? new[] { typed } : picked;
                return (q.Question, Answer: string.Join(DialogAsk.PickSeparator, parts.Where(s => s.Length > 0)));
            })
            .Where(a => a.Answer.Length > 0)
            .ToDictionary(a => a.Question, a => a.Answer, StringComparer.Ordinal);

    // Send: the desk writes the answers for the hook, or says why not (a question unanswered, the question ended).
    void SendDialog(Guid task, string slot, DialogAsk dialog)
    {
        if (Actions?.AnswerDialog is not { } answer) return;
        var result = answer(slot, Answers(dialog, dialogPicks.GetValueOrDefault(slot, []), dialogOther.GetValueOrDefault(slot, [])));
        // A failure keeps the picks: an unanswered question is still the user's to fill in, and a card that ended goes
        // with the page's next draw anyway.
        if (result.IsSuccess) ForgetDialog(slot);
        if (result.IsFailure) permissionNote[task] = result.Error;
        else permissionNote.Remove(task);
        if (latest is not null) Draw(latest);
    }

    // The comment box's words as the answer to the task's open dialog, if it has one: every question picked on the card
    // keeps its pick, every other one takes the words (Claude reads them per question, so a one-line reply reaches it
    // whichever question it meant). False when no dialog waits or the desk refused (the dialog ended): the caller then
    // posts the words as a comment.
    //
    // Only from the box of the task's PAGE, where the card is drawn above it, and only for a card that has been on show
    // for ArmTime (the branch's review, finding 3): a comment box in the List's thread shows no card, so its words are a
    // comment; and words typed before the card appeared were not written as an answer to it.
    bool AnsweredDialogWith(Guid task, string words)
    {
        if (Actions?.AnswerDialog is not { } answer || inboxPage != task) return false;
        var waiting = PermissionsOfTask(task)
            .Select(p => (Pending: p, Dialog: DialogAsk.Of(p.Request)))
            .FirstOrDefault(d => d.Dialog.HasValue && permissionShownAt.ContainsKey(d.Pending.Slot) && !Arming(d.Pending.Slot));
        if (waiting.Pending is null) return false;
        var (pending, dialog) = (waiting.Pending, waiting.Dialog.Value);
        var picked = Answers(dialog, dialogPicks.GetValueOrDefault(pending.Slot, []), dialogOther.GetValueOrDefault(pending.Slot, []));
        var answers = dialog.Questions.ToDictionary(q => q.Question, q => picked.GetValueOrDefault(q.Question, words.Trim()), StringComparer.Ordinal);
        if (answer(pending.Slot, answers).IsFailure) return false;
        ForgetDialog(pending.Slot);
        permissionNote.Remove(task);
        if (latest is not null) Draw(latest);
        return true;
    }

    void ForgetDialog(string slot)
    {
        dialogPicks.Remove(slot);
        dialogOther.Remove(slot);
        dialogTask.Remove(slot);
        dialogOtherBoxes.Keys.Where(k => k.StartsWith(slot + "\n", StringComparison.Ordinal)).ToList().ForEach(k => dialogOtherBoxes.Remove(k));
        permissionShownAt.Remove(slot);
    }
}
