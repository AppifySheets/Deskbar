using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// TASK LINKS IN THE TASKS PANEL (docs/superpowers/specs/2026-11-25-task-links-design.md, "The tasks panel"; Petre, 2026-11-25:
// "introduce a duplicate status in tasks", "also introduce related to", then "Menu item with a search picker").
//
// Two pieces, both inside an open task's thread, above its comments:
//
//   * THE LINK LINES. "Duplicate of: title · status", "Duplicate: title · status" (one per task closed as a duplicate of
//     this one) and "Related: title · status". The title is a button that shows that task (TasksBoard.ShowTask, which
//     switches to the Closed filter for a closed one). A related line has a cross that removes the link; "Duplicate of"
//     has none, since the link goes when the task is reopened (Reopen on the menu), and a duplicate's line in its
//     original's thread has none either: undoing it is reopening that task, which is that task's business.
//
//   * THE PICKER, opened by the line menu's "Duplicate of…" or "Related to…" (BeginLink). A box with a placeholder and,
//     under it, the first few tasks whose titles hold every typed word (TaskBoard.LinkCandidates, open ones first). Typing
//     rebuilds only the list under the box, never the panel, so the box keeps the keyboard. Enter picks the first task,
//     a click picks any, Esc closes the picker. A pick that the board refuses (a duplicate of a task with open subtasks,
//     Claude still working) leaves the picker open and the problem line says why (TasksBoard.Report).
//
// The picker survives a redraw the way the comment box does: the words typed are kept in `linkTyped`, and a box that had
// the keyboard gets it back with its caret at the end (Draw calls RefocusLink).
public partial class TasksWindow
{
    // The task the picker is open on and which kind of link it makes, or null while it is closed.
    (Guid Task, bool Duplicate)? linking;

    // The words typed into the picker, kept across redraws.
    string linkTyped = "";

    // The picker's box as last drawn, and whether it should take the keyboard after the next draw (it was just opened, or
    // it had the keyboard when the draw began).
    TextBox? linkBox;
    bool focusLinkBox;

    /// <summary>The menu items' words (task links).</summary>
    internal const string DuplicateOfText = "Duplicate of…";

    internal const string RelatedToText = "Related to…";

    /// <summary>For the tests: the open picker's box, if any.</summary>
    internal TextBox? LinkBox => linkBox;

    /// <summary>
    /// Opens the picker on a task's thread: "Duplicate of…" (<paramref name="duplicate"/>) or "Related to…". The thread
    /// opens first when it is closed (ShowTask), and the box takes the keyboard once drawn.
    /// </summary>
    internal void BeginLink(Guid id, bool duplicate)
    {
        linking = (id, duplicate);
        linkTyped = "";
        focusLinkBox = true;
        var row = latest?.Groups.SelectMany(g => g.Rows).FirstOrDefault(r => r.Id == id);
        if (row is { Expanded: false } && Actions?.ShowTask is { } show) show(id);
        else if (latest is not null) Draw(latest);
    }

    /// <summary>Closes the picker (Esc, a pick that worked) and redraws without it.</summary>
    internal void EndLink()
    {
        linking = null;
        linkTyped = "";
        linkBox = null;
        focusLinkBox = false;
        if (latest is not null) Present(latest);
    }

    // Called by Draw before the rows are rebuilt: whether the old box had the keyboard decides whether the new one takes it.
    void ForgetLinkBox()
    {
        focusLinkBox |= linkBox?.IsKeyboardFocusWithin == true;
        linkBox = null;
    }

    // Called by Draw after the rows are rebuilt.
    void RefocusLink()
    {
        if (linkBox is null || !focusLinkBox) return;
        focusLinkBox = false;
        linkBox.Focus();
        linkBox.CaretIndex = linkBox.Text.Length;
    }

    // One linked task in a thread: the kind in dim words, the title as a button that shows it, its status, and for a
    // related one a cross that removes the link.
    UIElement LinkEntry(Guid task, TaskLinkView link)
    {
        var text = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        text.Inlines.Add(new Run(LinkLabel(link.Kind) + ": ") { Foreground = HeaderInk });
        text.Inlines.Add(new Run(link.Title) { Foreground = link.Finished ? HeaderInk : Ink });
        text.Inlines.Add(new Run($" · {link.Status}") { Foreground = HeaderInk });
        var entry = Flat(text, $"{LinkLabel(link.Kind)} {link.Title}", () => Actions?.ShowTask?.Invoke(link.Id), new Thickness(4, 1, 4, 1));
        entry.Background = Brushes.Transparent;
        entry.HorizontalAlignment = HorizontalAlignment.Left;
        entry.ToolTip = "Show this task";
        var line = new DockPanel { Margin = new Thickness(0, 0, 0, 4), LastChildFill = false };
        line.Children.Add(entry);
        if (link.Kind == TaskLinkKind.Related && Actions?.Unrelate is { } unrelate)
        {
            var cross = Small("✕", $"Remove the link to {link.Title}", () => unrelate(task, link.Id), "Remove this link");
            cross.Margin = new Thickness(4, 0, 0, 0);
            line.Children.Add(cross);
        }
        return line;
    }

    internal static string LinkLabel(TaskLinkKind kind) => kind switch
    {
        TaskLinkKind.DuplicateOf => $"{TaskViews.DuplicateName} of",
        TaskLinkKind.Duplicate => TaskViews.DuplicateName,
        _ => "Related",
    };

    // The picker: its box over the list of matching tasks.
    UIElement LinkPicker(Guid task, bool duplicate)
    {
        var box = new TextBox
        {
            Text = linkTyped,
            Template = (ControlTemplate)FindResource("BareTextBox"),
            Background = Brushes.Transparent,
            Foreground = Ink,
            CaretBrush = Brushes.White,
            BorderThickness = new Thickness(0),
            FontSize = 12,
            AcceptsReturn = false,
        };
        AutomationProperties.SetName(box, duplicate ? "Duplicate of which task" : "Related to which task");
        var placeholder = new TextBlock
        {
            Text = duplicate ? "Duplicate of: type part of a title" : "Related to: type part of a title",
            FontSize = 12,
            Foreground = HeaderInk,
            IsHitTestVisible = false,
            Margin = new Thickness(2, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed,
        };
        var choices = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        IReadOnlyList<TaskLinkView> shown = [];
        void Fill()
        {
            shown = Actions?.LinkCandidates?.Invoke(task, box.Text, duplicate) ?? [];
            choices.Children.Clear();
            shown.ToList().ForEach(choice => choices.Children.Add(LinkChoice(task, duplicate, choice)));
            if (shown.Count == 0)
                choices.Children.Add(new TextBlock { Text = "No task matches", FontSize = 11, Foreground = HeaderInk, Margin = new Thickness(4, 1, 4, 1) });
        }
        box.TextChanged += (_, _) =>
        {
            linkTyped = box.Text;
            placeholder.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Fill();
        };
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                EndLink();
            }
            else if (e.Key is Key.Enter or Key.Return)
            {
                e.Handled = true;
                if (shown.Count > 0) PickLink(task, duplicate, shown[0].Id);
            }
        };
        linkBox = box;
        Fill();
        return new StackPanel
        {
            Margin = new Thickness(0, 0, 0, 8),
            Children =
            {
                new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Background = Frozen(0xFF, 0x15, 0x16, 0x1A),
                    BorderBrush = ThreadEdge,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(6, 3, 6, 3),
                    Child = new Grid { Children = { box, placeholder } },
                },
                choices,
            },
        };
    }

    // One task the picker offers: its title and status; a click picks it.
    UIElement LinkChoice(Guid task, bool duplicate, TaskLinkView choice)
    {
        var text = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        text.Inlines.Add(new Run(choice.Title) { Foreground = choice.Finished ? HeaderInk : Ink });
        text.Inlines.Add(new Run($" · {choice.Status}") { Foreground = HeaderInk });
        var button = Flat(text, $"Link to {choice.Title}", () => PickLink(task, duplicate, choice.Id), new Thickness(4, 1, 4, 1));
        button.Background = Brushes.Transparent;
        button.HorizontalAlignment = HorizontalAlignment.Left;
        button.Margin = new Thickness(0, 0, 0, 2);
        return button;
    }

    /// <summary>
    /// A pick: the duplicate or the related link through the controller. A refusal keeps the picker open, its reason on the
    /// problem line; one that worked closes it. Internal for the tests.
    /// </summary>
    internal void PickLink(Guid task, bool duplicate, Guid other)
    {
        var worked = duplicate ? Actions?.MarkDuplicate?.Invoke(task, other) : Actions?.Relate?.Invoke(task, other);
        if (worked == true) EndLink();
    }
}
