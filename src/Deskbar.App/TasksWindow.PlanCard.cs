using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// THE PLAN CARD (task 8dbbaa0b, a subtask of c047d0a0, Petre: "model this in the task so i don't have to open claude
// code"). A session in plan mode asks for its plan's approval with Claude Code's ExitPlanMode dialog. That dialog reaches
// the permission desk through the same hook as a tool permission and as the multiple-choice dialog (PermissionDesk,
// PlanAsk), and this card stands where their cards stand: docked at the bottom of the task's page, in the panel and in a
// task window alike (TasksWindow.PermissionCard draws one of the three per question).
//
// WHAT IT SHOWS: the plan in full, as the markdown Claude wrote, read like a chat message (the cards' own scroll holds a
// long one); hidden and direction characters show as marks, as on the permission card, since this is what the user is
// asked to approve. Under it a box for a note.
//
// THE BUTTONS are the terminal dialog's three answers, each measured to work from the hook (PlanAsk's header):
//   Approve                    the session leaves plan mode and asks before each edit, as usual;
//   Approve and accept edits   the same, and the session edits files without asking from now on;
//   Keep planning              enabled once the note box has words: Claude reads the note and plans again, and its next
//                              plan comes back on a new card;
// and "Answer in the terminal", which hands the dialog back (the hook prints nothing). The terminal's own dialog is up the
// whole time; whichever is answered first wins. The buttons keep the permission card's rules: the arming delay after the
// card appears, Tab and Enter but never Space.
//
// THE NOTE LIVES where the dialog card's Other text lives (dialogOther, by slot, under NoteKey), so a redraw of the page
// keeps it, the box gets the keyboard back after one (dialogOtherBoxes), and it goes with the card (ForgetDialog).
public partial class TasksWindow
{
    internal const string PlanCardTag = "plan-card";
    internal const string PlanBodyTag = "plan-body";
    internal const string PlanNoteTag = "plan-note";
    internal const string ApprovePlanText = "Approve";
    internal const string ApprovePlanEditsText = "Approve and accept edits";
    internal const string KeepPlanningText = "Keep planning";

    // The note's key among a slot's kept texts; no question of a dialog can be the empty text, so it cannot collide.
    const string NoteKey = "";

    FrameworkElement PlanCard(Guid task, PermissionDesk.Pending question, PlanAsk plan)
    {
        if (!permissionShownAt.ContainsKey(question.Slot)) permissionShownAt[question.Slot] = ClickClock();
        dialogTask[question.Slot] = task;
        var kept = dialogOther.TryGetValue(question.Slot, out var o) ? o : dialogOther[question.Slot] = [];
        var card = new StackPanel();
        card.Children.Add(Row(InboxMarks.Icon("ask", 14, InboxLook.Amber, InboxLook.Surface),
            Text("CLAUDE'S PLAN, FOR YOUR APPROVAL", 11, InboxLook.Amber, FontWeights.SemiBold)));

        // Line ends and tabs are ordinary in a plan (a code block's indent), so they are made plain before the marks:
        // PermissionDisplay.Visible would otherwise draw each as a mark through the text.
        var words = plan.Plan.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\t", "    ");
        var document = MarkdownBlocks.Render(PermissionDisplay.Visible(words), LinkOpener(task));
        InboxLook.Reading(document, InboxLook.ChatText);
        document.Foreground = InboxLook.ReadInk;
        card.Children.Add(new RichTextBox
        {
            Tag = PlanBodyTag,
            Document = document,
            IsReadOnly = true,
            IsDocumentEnabled = true,
            Template = (ControlTemplate)FindResource("BareRichTextBox"),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = InboxLook.Ink,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 7, 0, 0),
            Cursor = Cursors.Arrow,
        });
        if (question.Request.Folder.Length > 0)
            card.Children.Add(Text($"In {PermissionDisplay.Visible(question.Request.Folder)}", 11.5, InboxLook.Ink3,
                margin: new Thickness(0, 4, 0, 0)).Wrapped());

        var noteBox = new TextBox
        {
            Tag = PlanNoteTag,
            Text = kept.GetValueOrDefault(NoteKey, ""),
            FontSize = 12.5,
            Foreground = InboxLook.Ink,
            Background = InboxLook.Raised,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 5, 8, 5),
            TextWrapping = TextWrapping.Wrap,
            // Shift+Enter is not needed for a note of a few sentences, and Enter must not press a button from the box.
            AcceptsReturn = false,
            MaxLength = PlanAsk.NoteLength,
        };
        AutomationProperties.SetName(noteBox, "What should change in the plan");
        dialogOtherBoxes[$"{question.Slot}\n{NoteKey}"] = noteBox;
        var hint = Text("To keep planning: what should change?", 12.5, InboxLook.Ink3, margin: new Thickness(10, 0, 0, 0));
        hint.IsHitTestVisible = false;
        hint.VerticalAlignment = VerticalAlignment.Center;
        var noteArea = new Grid { Margin = new Thickness(0, 9, 0, 0) };
        noteArea.Children.Add(noteBox);
        noteArea.Children.Add(hint);
        card.Children.Add(noteArea);

        var buttons = new WrapPanel { Margin = new Thickness(0, 9, 0, 0) };
        var keep = PlanButton(task, question, PlanDecision.KeepPlanning, KeepPlanningText);
        buttons.Children.Add(PlanButton(task, question, PlanDecision.Approve, ApprovePlanText, primary: true));
        buttons.Children.Add(PlanButton(task, question, PlanDecision.ApproveEdits, ApprovePlanEditsText));
        buttons.Children.Add(keep);
        buttons.Children.Add(PermissionButton(task, question, TerminalChoice));
        card.Children.Add(buttons);
        card.Children.Add(Text("\"Accept edits\" lets this session edit files without asking from now on, also after a restart.",
            11.5, InboxLook.Ink3, margin: new Thickness(0, 6, 0, 0)).Wrapped());

        // Keep planning needs the note (PlanAsk.AnswerLine); the hint shows while the box is empty.
        void Paint()
        {
            hint.Visibility = noteBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            keep.IsEnabled = noteBox.Text.Trim().Length > 0;
        }
        noteBox.TextChanged += (_, _) =>
        {
            kept[NoteKey] = noteBox.Text;
            Paint();
        };
        Paint();

        var frame = new Border
        {
            Tag = PlanCardTag,
            Background = InboxLook.Surface,
            BorderBrush = InboxLook.AskEdge,
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 10, 12, 11),
            Margin = new Thickness(0, 0, 0, 8),
            Child = card,
        };
        AutomationProperties.SetName(frame, "Claude's plan, for your approval");
        return frame;
    }

    Button PlanButton(Guid task, PermissionDesk.Pending question, PlanDecision decision, string words, bool primary = false)
    {
        var face = Text(words, 12.5, primary ? InboxLook.Background : InboxLook.Ink, FontWeights.SemiBold);
        var button = InboxButton(face, words, () => AnswerPlan(task, question.Slot, decision), new Thickness(12, 5, 12, 6),
            primary ? InboxLook.Ink : InboxLook.Raised, 12, primary ? null : InboxLook.StrongLine,
            ignoredWhen: () => ClickIgnored() || Arming(question.Slot));
        button.Margin = new Thickness(0, 0, 6, 4);
        // Tab reaches the buttons and Enter presses the focused one; Space does nothing, as on the permission card.
        button.Focusable = true;
        button.IsTabStop = true;
        button.PreviewKeyDown += (_, e) => { if (e.Key == Key.Space) e.Handled = true; };
        button.PreviewKeyUp += (_, e) => { if (e.Key == Key.Space) e.Handled = true; };
        return button;
    }

    // The click: the desk hands the answer to the hook, or says why not (no note yet, the plan's question ended).
    void AnswerPlan(Guid task, string slot, PlanDecision decision)
    {
        if (Actions?.AnswerPlan is not { } answer) return;
        var result = answer(slot, decision, dialogOther.GetValueOrDefault(slot, []).GetValueOrDefault(NoteKey, ""));
        // A failure keeps the note: a missing note is the user's to write, and a card that ended goes with the next draw.
        if (result.IsSuccess) ForgetDialog(slot);
        if (result.IsFailure) permissionNote[task] = result.Error;
        else permissionNote.Remove(task);
        if (latest is not null) Draw(latest);
    }
}
