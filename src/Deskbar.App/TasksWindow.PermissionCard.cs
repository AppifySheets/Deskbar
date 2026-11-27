using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// THE PERMISSION CARD (Allow / Deny from a task's page; the picked mockup's .perm, notes\mockups\2026-11-26-tasks-redesign-
// round2). While a session of the page's task waits on a permission question (PermissionDesk), the page shows one card
// per question (any session since task 6306d261: a terminal or VS Code one too, not only a background one), the oldest first, DOCKED AT THE PAGE'S BOTTOM (DrawInboxPage's one-line hook), so it is in view
// whatever the thread's scroll. The same drawing serves the panel's page and a task window's page (the window draws
// through a TasksWindow of its own, TasksWindow.InboxWindow.cs, with the same actions).
//
// WHAT IT SAYS is PermissionDisplay's text, which hides nothing: the tool and the folder, the main field (the command, the
// edited file) in a monospaced box that WRAPS and is never cut, every other field under it, and Claude's own description
// apart, labelled as Claude's words. Hidden and direction characters show as marks (⟨U+202E⟩). A part longer than
// PermissionDisplay.FoldLines lines folds by whole lines behind "Show all (N more lines)". The mockup's box scrolled
// sideways (`white-space: nowrap`), which hid the tail of a long command off the right edge; that is the one thing of the
// mockup this does not copy.
//
// THE BUTTONS: Allow, "Allow and stop asking for edits" (only for an edit tool, with what it does said under it), Deny,
// and "Answer in the terminal" (the hook prints nothing at once, and the terminal's own dialog, drawn the whole time,
// decides). Only the user's own action in this window writes an answer (no pipe command does it). THE KEYBOARD, as the
// PM ruled after the review's L1: a button focused with Tab and pressed with Enter is allowed; no single key allows. Space
// does nothing on the buttons, and no key pressed elsewhere reaches them. (The plan first said "Enter never allows";
// that was wrong as built, and the ruling keeps the build.) A click in the first ArmTime after a card first appears is
// ignored, so a click meant for whatever was under the pointer cannot land on Allow; the page's own guard after a redraw
// applies too.
public partial class TasksWindow
{
    /// <summary>How long a new card's buttons ignore clicks.</summary>
    internal static readonly TimeSpan ArmTime = TimeSpan.FromMilliseconds(600);

    internal const string PermissionCardTag = "permission-card";
    internal const string PermissionPartTag = "permission-part";
    internal const string AllowText = "Allow";
    internal const string AllowEditsText = "Allow and stop asking for edits";
    internal const string DenyText = "Deny";
    internal const string AnswerInTerminalText = "Answer in the terminal";
    internal const string ShowAllPartPrefix = "Show all";

    // The tools whose question is a file edit: only for those does "stop asking for edits" mean what it says.
    static readonly string[] EditTools = ["Edit", "MultiEdit", "Write", "NotebookEdit"];

    static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Courier New");

    // When each card was first drawn (by slot), for ArmTime; the parts unfolded by "Show all" (slot and label); and the
    // one line said after a click that came too late, by task.
    readonly Dictionary<string, DateTime> permissionShownAt = [];
    readonly HashSet<string> permissionUnfolded = [];
    readonly Dictionary<Guid, string> permissionNote = [];

    IReadOnlyList<PermissionDesk.Pending> PermissionsOfTask(Guid id) => Actions?.PermissionsOf?.Invoke(id) ?? [];

    // Part of the page's redraw signature: the questions on show, what is unfolded and the note.
    string PermissionSignature(Guid id) =>
        $"|perm:{string.Join(',', PermissionsOfTask(id).Select(p => p.Slot))}|{string.Join(',', permissionUnfolded)}|{permissionNote.GetValueOrDefault(id)}";

    /// <summary>The cards of the page's task, docked at its bottom, or null when nothing waits (DrawInboxPage's hook).</summary>
    FrameworkElement? PermissionCards(TaskRowView row)
    {
        var questions = PermissionsOfTask(row.Id);
        var note = permissionNote.GetValueOrDefault(row.Id);
        if (questions.Count == 0 && note is null) return null;
        var stack = new StackPanel { Margin = new Thickness(10, 6, 10, 8) };
        if (note is not null)
            stack.Children.Add(Text(note, 12, InboxLook.Ink2, margin: new Thickness(2, 0, 2, 6)).Wrapped());
        foreach (var question in questions) stack.Children.Add(PermissionCard(row.Id, question));
        return new Border
        {
            BorderBrush = InboxLook.Hairline,
            BorderThickness = new Thickness(0, 1, 0, 0),
            // A long command unfolded must not push the whole page off: the cards scroll inside 70% of the page at most, room for a
            // folded card with its buttons on a short panel.
            Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = Math.Max(240, (InboxPage.ActualHeight > 0 ? InboxPage.ActualHeight : 600) * 0.7),
                Focusable = false,
                Content = stack,
            },
        };
    }

    FrameworkElement PermissionCard(Guid task, PermissionDesk.Pending question)
    {
        if (!permissionShownAt.ContainsKey(question.Slot)) permissionShownAt[question.Slot] = ClickClock();
        var text = PermissionDisplay.Of(question.Request);
        var card = new StackPanel();

        // The caption: the shield, as the line's mark, and the words, so it is never told by colour alone.
        card.Children.Add(Row(InboxMarks.Icon("shield", 14, InboxLook.Amber, InboxLook.Surface),
            Text("CLAUDE ASKS PERMISSION", 11, InboxLook.Amber, FontWeights.SemiBold)));
        var shell = text.Parts.FirstOrDefault(p => p.Main) is { Label: "command" };
        card.Children.Add(Text(shell ? $"It wants to run this ({text.Tool}):" : $"It wants to use {text.Tool}:", 12.5, InboxLook.Ink2,
            margin: new Thickness(0, 6, 0, 0)).Wrapped());
        foreach (var part in text.Parts) card.Children.Add(PermissionPart(question.Slot, part));
        if (text.ClaudeSays is { Length: > 0 } says)
            card.Children.Add(Text($"Claude says: {says}", 12, InboxLook.Ink3, margin: new Thickness(0, 4, 0, 0)).Wrapped());
        if (text.Folder.Length > 0)
            card.Children.Add(Text($"In {text.Folder}", 11.5, InboxLook.Ink3, margin: new Thickness(0, 4, 0, 0)).Wrapped());

        var buttons = new WrapPanel { Margin = new Thickness(0, 9, 0, 0) };
        var edit = EditTools.Contains(question.Request.Tool, StringComparer.Ordinal);
        buttons.Children.Add(PermissionButton(task, question, PermissionDecision.Allow, AllowText, primary: true));
        if (edit) buttons.Children.Add(PermissionButton(task, question, PermissionDecision.AllowEdits, AllowEditsText));
        buttons.Children.Add(PermissionButton(task, question, PermissionDecision.Deny, DenyText));
        buttons.Children.Add(PermissionButton(task, question, PermissionDecision.Pass, AnswerInTerminalText));
        card.Children.Add(buttons);
        if (edit)
            card.Children.Add(Text("\"Stop asking for edits\" lets this session edit files without asking from now on, also after a restart.",
                11.5, InboxLook.Ink3, margin: new Thickness(0, 6, 0, 0)).Wrapped());

        var frame = new Border
        {
            Tag = PermissionCardTag,
            Background = InboxLook.Surface,
            BorderBrush = InboxLook.AskEdge,
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 10, 12, 11),
            Margin = new Thickness(0, 0, 0, 8),
            Child = card,
        };
        AutomationProperties.SetName(frame, $"Claude asks permission to use {text.Tool}");
        return frame;
    }

    // One field: its label (the main one has none), its text in full and wrapped, folded by whole lines when long.
    FrameworkElement PermissionPart(string slot, PermissionPart part)
    {
        var key = $"{slot}/{part.Label}";
        var fold = PermissionDisplay.Fold(part.Text);
        var open = permissionUnfolded.Contains(key);
        var shown = open ? fold.Shown.Concat(fold.Rest) : fold.Shown;
        var box = new StackPanel();
        if (!part.Main) box.Children.Add(Text(part.Label, 11, InboxLook.Ink3, margin: new Thickness(0, 5, 0, 1)));
        box.Children.Add(new Border
        {
            Background = InboxLook.Background,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(9, 7, 9, 7),
            Margin = new Thickness(0, part.Main ? 6 : 0, 0, 2),
            Child = new TextBlock
            {
                Tag = PermissionPartTag,
                Text = string.Join("\n", shown),
                FontFamily = Mono,
                FontSize = part.Main ? 12.5 : 11.5,
                Foreground = InboxLook.Ink,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.None,
            },
        });
        if (fold.Rest.Count > 0)
        {
            var words = open ? "Show less" : $"{ShowAllPartPrefix} ({fold.Rest.Count} more lines)";
            var toggle = InboxButton(Text(words, 11.5, InboxLook.Blue), words, () =>
            {
                if (!permissionUnfolded.Remove(key)) permissionUnfolded.Add(key);
                if (latest is not null) Draw(latest);
            }, new Thickness(2, 1, 2, 1), Brushes.Transparent, 6);
            toggle.HorizontalAlignment = HorizontalAlignment.Left;
            box.Children.Add(toggle);
        }
        return box;
    }

    Button PermissionButton(Guid task, PermissionDesk.Pending question, PermissionDecision decision, string words, bool primary = false)
    {
        var face = Text(words, 12.5, primary ? InboxLook.Background : InboxLook.Ink, FontWeights.SemiBold);
        var button = InboxButton(face, words, () => Answer(task, question.Slot, decision), new Thickness(12, 5, 12, 6),
            primary ? InboxLook.Ink : InboxLook.Raised, 12, primary ? null : InboxLook.StrongLine,
            ignoredWhen: () => ClickIgnored() || ClickClock() - permissionShownAt.GetValueOrDefault(question.Slot) < ArmTime);
        button.Margin = new Thickness(0, 0, 6, 4);
        // Tab reaches the buttons and Enter presses the focused one; Space, the other key a Button takes, does nothing here.
        button.Focusable = true;
        button.IsTabStop = true;
        button.PreviewKeyDown += (_, e) => { if (e.Key == Key.Space) e.Handled = true; };
        button.PreviewKeyUp += (_, e) => { if (e.Key == Key.Space) e.Handled = true; };
        return button;
    }

    // The click: the desk writes the answer, or says the question has ended (then the terminal is the place to answer).
    void Answer(Guid task, string slot, PermissionDecision decision)
    {
        if (Actions?.AnswerPermission is not { } answer) return;
        var result = answer(slot, decision);
        permissionShownAt.Remove(slot);
        permissionUnfolded.RemoveWhere(k => k.StartsWith(slot + "/", StringComparison.Ordinal));
        if (result.IsFailure) permissionNote[task] = result.Error;
        else permissionNote.Remove(task);
        if (latest is not null) Draw(latest);
    }
}

file static class PermissionTextBlocks
{
    // Wrapped in full, as every text of the card is.
    public static TextBlock Wrapped(this TextBlock block)
    {
        block.TextWrapping = TextWrapping.Wrap;
        block.TextTrimming = TextTrimming.None;
        return block;
    }
}
