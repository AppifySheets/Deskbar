using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// THE BAR'S TASK LINES IN THE INBOX LOOK (Deskbar task 740a2231, slice 2; the plan is the workspace's
// notes\tasks-inbox-bar-plan.md; the look is the picked mockup's bar strip, notes\mockups\2026-11-26-tasks-redesign-round2).
// While the tasks panel shows its Inbox (TasksPanelState.Inbox, the same switch), the bar draws its task lines the way the
// panel's Inbox does, and the old way otherwise. ONLY THE DRAWING CHANGES: what the bar lists, in what order, how many,
// the Waiting on you fold, the click guard, the press and drag, the menu, the click and double-click, the tooltip and the
// Undo all stay FloatingBar.TaskList.cs's. A line's Button is built there as always; only its content is this file's
// (TaskLineButton's one-line hook), and it keeps the content's role: the Border the selection and the glow paint.
//
// A line, top to bottom in its text column, with the state mark at its left:
//   the caption   on the first line of the Waiting on you block only: "NEEDS YOU" and how many lines the block holds,
//                 folded ones included. Inside that line rather than a line of its own, because the lines' panel holds
//                 only line buttons and the slot count, the fold's place and "+N more" all read it. A press on the
//                 caption is not a click on the task;
//   the title     up to two rows, ending in "…"; semibold on a Claude task, normal on a plain note; bold when important,
//                 italic when urgent (Ruling 59); "↳" before a subtask and "· N subtasks" after a parent;
//                 NO SECOND LINE (the owner, 2026-11-27, after using it: it added nothing and the bar has little room).
//                 A question or a status line is in the line's tooltip, in full, with the title;
//   the chip      the workspace's full name with a dot in its colour, under the title at every width; a click opens the
//                 move menu as today's chip does. A task on no workspace has no chip.
// The mark is the panel's (InboxMarks): amber only for a line that asks or went quiet. No fill behind a line but the
// bar's hover and the selection. The text is the bar's own sizes (Text), so the text-size setting keeps working.
//
// COLOURS ON THE BAR. The bar keeps its own surface (BarSurface, #1B1C21), lighter than the panel's, and its hover
// washes a line with 12% white. The third grey of the Inbox look reads at only 3.8 to 1 on that hover, so the bar's
// lines use the first two inks and amber alone (every pair at 4.5 to 1 or better, FloatingBarTaskListTests).
public partial class FloatingBar
{
    // The switch: the tasks panel's Inbox view, as it saves it.
    bool InboxBarOn => manager.State.TasksPanel?.Inbox ?? false;

    // The selected line's fill in the Inbox look (the mockup's .bi.on); the old look keeps its own.
    static readonly Brush InboxBarSelectedFill = Frozen(0xFF, 0x27, 0x2C, 0x36);

    // What the selection and the glow paint on a line's content, in the look on show (PaintTaskSelection, GlowTaskLine).
    Brush SelectedLineFill => InboxBarOn ? InboxBarSelectedFill : TaskSelectedFill;

    internal const string InboxBarLineTag = "inbox-bar-line";
    internal const string InboxBarCaptionTag = "inbox-bar-caption";
    internal const string InboxBarMarkTag = "inbox-bar-mark";

    // What a line's height follows in the Inbox look, beyond today's key (HeightKeyOf): the look itself, and the caption.
    string InboxBarHeightKey(TaskLine line) => InboxBarOn ? $"|inbox|{CaptionCount(line)}" : "";

    // The line's state mark, from its task as the panel reads it (InboxTabs.MarkOf).
    InboxMark BarMarkOf(TaskLine line) =>
        taskService?.Board.Tasks.FirstOrDefault(t => t.Id == line.Id) is { } task
            ? InboxTabs.MarkOf(task, InboxMarks.StoppedOnLimit, hollow: _ => line.Delivery.Shipping, asksPermission: AsksPermission)
            : line.Claudes ? InboxMark.Idle : InboxMark.Note;

    // The caption's count on the Waiting on you block's first line, or null on every other line. The block is the bar's
    // own (the questions and the Done tasks, TaskBoard.Pinned); its size is taken before the fold, so the lines folded
    // into "+N more waiting on you" count too.
    int? CaptionCount(TaskLine line) =>
        line.GroupKey == "waiting" && shownTaskLines.FirstOrDefault(l => l.GroupKey == "waiting")?.Id == line.Id
            ? unfoldedTaskLines.Count(l => l.GroupKey == "waiting")
            : null;

    double InboxBarMarkSize => Math.Round(Text(9.5) * 1.5);

    // A line's content in the Inbox look (see the header). The Border is what the selection and the glow paint.
    Border InboxLineFrame(TaskLine line)
    {
        var mark = BarMarkOf(line);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(InboxBarMarkSize) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        if (CaptionCount(line) is { } count)
        {
            var caption = InboxBarCaption(count);
            Grid.SetColumnSpan(caption, 2);
            grid.Children.Add(caption);
        }

        // The mark at the first title row's height; Undo in its place while the line is closing.
        UIElement lead = line.Closing ? UndoButton(line) : InboxMarks.Icon(InboxMarks.IconOf(mark), InboxBarMarkSize, InboxMarks.InkOf(mark), BarSurface.Solid,
            turning: mark == InboxMark.Working);
        if (lead is FrameworkElement leadElement)
        {
            leadElement.VerticalAlignment = VerticalAlignment.Top;
            leadElement.HorizontalAlignment = HorizontalAlignment.Center;
            leadElement.Margin = new Thickness(0, Math.Max(0, (TaskTextLineHeight - InboxBarMarkSize) / 2), 0, 0);
            if (!line.Closing)
            {
                leadElement.Tag = InboxBarMarkTag;
                AutomationProperties.SetName(leadElement, InboxMarks.WordsOf(mark));
            }
        }
        Grid.SetRow(lead, 1);
        grid.Children.Add(lead);

        var text = new StackPanel { Margin = new Thickness(7, 0, 0, 0) };
        text.Children.Add(editingTask == line.Id && !line.Closing ? TitleOrEditor(line) : InboxBarTitle(line));
        if (line.WorkspaceName is not null) text.Children.Add(InboxBarChip(line));
        Grid.SetRow(text, 1);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        // A closed line in its undo window is dimmed, all but its Undo, as today's line is.
        if (line.Closing) text.Opacity = 0.45;

        var selected = selection.IsSelected(line.Id);
        return new Border
        {
            Tag = InboxBarLineTag,
            // No edge of its own: the selection's brush is set on it as on today's frame, and stays invisible.
            BorderThickness = new Thickness(0),
            BorderBrush = selected ? TaskPickedEdge : Brushes.Transparent,
            Background = selected ? InboxBarSelectedFill : Brushes.Transparent,
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(5, 4, 6, 5),
            Child = grid,
            // A permission question waiting on this line's session is said in the tooltip, above the bar's own text for
            // the line (TipOf, the Button's tooltip, which shows whenever this one is null).
            ToolTip = PermissionWords(line.Id) is { } asked && !line.Closing ? $"{asked}\n{TipOf(line)}" : null,
        };
    }

    // The title, two rows at most, in the bar's text size (TextOf's line height and cut).
    TextBlock InboxBarTitle(TaskLine line)
    {
        var lineHeight = TaskTextLineHeight;
        var title = new TextBlock
        {
            Tag = "task-text",
            FontSize = Text(9.5),
            Foreground = InboxLook.Ink,
            FontWeight = line.Important ? FontWeights.Bold : line.Claudes ? FontWeights.SemiBold : FontWeights.Normal,
            FontStyle = line.Urgent ? FontStyles.Italic : FontStyles.Normal,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            LineHeight = lineHeight,
            MaxHeight = 2 * lineHeight,
            TextDecorations = line.Closing ? TextDecorations.Strikethrough : null,
        };
        if (line.ParentTitle is not null)
            title.Inlines.Add(new Run(TaskViews.Breadcrumb(line.ParentTitle, full: false)) { Foreground = InboxLook.Ink2, FontWeight = FontWeights.Normal, FontStyle = FontStyles.Normal });
        title.Inlines.Add(new Run(FirstLine(line.Title)));
        if (line.SubtaskCount > 0)
            title.Inlines.Add(new Run(TaskViews.SubtaskCountSeparator + TaskViews.SubtaskCountText(line.SubtaskCount))
                { Foreground = InboxLook.Ink2, FontWeight = FontWeights.Normal, FontStyle = FontStyles.Normal });
        return title;
    }

    // The chip under the title: the full name and a dot in the workspace's colour, in a quiet pill; the move menu on a
    // click, as today's chip (OpenTaskMoveMenu). A line in its undo window has a plain chip: its task is closed.
    UIElement InboxBarChip(TaskLine line)
    {
        var name = line.WorkspaceName ?? "";
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 6, Height = 6, Fill = line.Colour ?? InboxLook.Ink2, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center,
        });
        content.Children.Add(new TextBlock { Text = name, FontSize = Text(8.5), Foreground = InboxLook.Ink2, TextTrimming = TextTrimming.CharacterEllipsis });
        var pill = new Border
        {
            Tag = "task-workspace",
            Background = InboxLook.Surface,
            BorderBrush = InboxLook.StrongLine,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6, 0, 7, 1),
            Child = content,
            ToolTip = $"On {name}",
        };
        if (line.Closing) return new Border { Child = pill, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 3, 0, 0) };
        Button? button = null;
        button = TaskButton("Move to workspace", pill, () => OpenTaskMoveMenu(line.Id, button), $"On {name}: move to another workspace",
            ignoredWhen: TaskClickIgnored);
        button.HorizontalAlignment = HorizontalAlignment.Left;
        button.Margin = new Thickness(0, 3, 0, 0);
        return button;
    }

    // "NEEDS YOU" and the count, as the panel's section captions read. A press on it is the caption's own: it is inside
    // the first task's line, and must not open that task or start its drag.
    FrameworkElement InboxBarCaption(int count)
    {
        var caption = new StackPanel
        {
            Tag = InboxBarCaptionTag,
            Orientation = Orientation.Horizontal,
            Background = Brushes.Transparent,
            Margin = new Thickness(0, 0, 0, 4),
            ToolTip = count == 1 ? "1 task needs you" : $"{count} tasks need you",
        };
        caption.Children.Add(new TextBlock { Text = "NEEDS YOU", FontSize = Text(7.5), FontWeight = FontWeights.SemiBold, Foreground = InboxLook.Ink2 });
        caption.Children.Add(new TextBlock { Text = $"{count}", FontSize = Text(7.5), FontWeight = FontWeights.SemiBold, Foreground = InboxLook.Ink, Margin = new Thickness(6, 0, 0, 0) });
        AutomationProperties.SetName(caption, $"Needs you {count}");
        caption.MouseLeftButtonDown += (_, e) => e.Handled = true;
        caption.MouseLeftButtonUp += (_, e) => e.Handled = true;
        return caption;
    }

    // "+N more waiting on you" in the Inbox look: the words in the second ink, never amber; the question mark in amber in
    // the mark's place when a folded task asks something. The same Button, click and tooltip (WaitingGroupButton).
    Border InboxGroupFrame(WaitingFold fold)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(InboxBarMarkSize) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (fold.Questions > 0)
        {
            var ask = InboxMarks.Icon("ask", InboxBarMarkSize, InboxLook.Amber, BarSurface.Solid);
            ask.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(ask);
        }
        var words = new TextBlock
        {
            Tag = "task-waiting-group-text",
            Text = WaitingGroupText(fold),
            FontSize = Text(9.5),
            Foreground = InboxLook.Ink2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(7, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(words, 1);
        grid.Children.Add(words);
        return new Border
        {
            BorderThickness = new Thickness(0),
            BorderBrush = Brushes.Transparent,
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(5, 3, 6, 3),
            Child = grid,
        };
    }

    /// <summary>For the tests: whether the bar draws its lines in the Inbox look.</summary>
    internal bool TaskLinesInInboxLook => InboxBarOn;
}
