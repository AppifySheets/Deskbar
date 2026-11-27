using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Deskbar.App;

// THE MANAGER LINE ON THE BAR (task 61e4d143; Core's ManagerQuestion has the rules, ManagerLook the colours). While the
// manager has a question open for the user, the bar's task list shows it as one line above every task line, built like
// a task line (the 3 DIP stripe, the tint, the 9.5 text wrapping to two lines) but violet, and with a ring where a task
// line has its tick box: the manager is not a task, so there is nothing to tick off. It sits outside the scrolling
// viewer (taskManagerHost, between the add row and the lines), so it is always in sight.
//
// A click opens the manager (OpenManager, the Manager button's own click), with the list's click guard, since the line
// can slide under the pointer like any other. Nothing else: no drag, no menu, no double-click. Opening the manager does
// not clear the question; the user's reply to it does (ManagerQuestion.AfterPrompt).
public partial class FloatingBar
{
    /// <summary>
    /// The manager's open question, or null while it has none (ManagerRecord.Question). Read on every paint of the list;
    /// asking, replacing or clearing it saves the state, whose change repaints the list. Null shows no line.
    /// </summary>
    internal Func<ManagerAsk?>? ManagerAsks { get; init; }

    // The line's place in the list (built with the list, FloatingBar.TaskList.cs).
    Border? taskManagerHost;

    // Fills the host with the line for `ask`, or empties and collapses it. Without an OpenManager there is no manager to
    // open, so no line either.
    void PaintManagerLine(ManagerAsk? ask)
    {
        if (taskManagerHost is null) return;
        var shows = ask is not null && OpenManager is not null;
        taskManagerHost.Child = shows ? ManagerLine(ask!) : null;
        taskManagerHost.Visibility = shows ? Visibility.Visible : Visibility.Collapsed;
    }

    Button ManagerLine(ManagerAsk ask)
    {
        var lineHeight = TaskTextLineHeight;
        var text = new TextBlock
        {
            Tag = "manager-line-text",
            FontSize = Text(9.5),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            LineHeight = lineHeight,
            MaxHeight = 2 * lineHeight,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(4, 0, 4, 0),
            Foreground = ManagerLook.TitleInk,
        };
        text.Inlines.Add(new Run("Manager") { FontWeight = FontWeights.SemiBold });
        // An answer says so (task 2c5de076); a question keeps the line as it was, "Manager" and the question.
        if (ask.Answered) text.Inlines.Add(new Run(" answered") { FontWeight = FontWeights.SemiBold });
        text.Inlines.Add(new Run(TaskDetailSeparator) { Foreground = ManagerLook.QuestionInk });
        // One line of the question: the line wraps to two at most and the tooltip carries all of it.
        text.Inlines.Add(new Run(ask.Question.ReplaceLineEndings(" ")) { Foreground = ManagerLook.QuestionInk });

        var face = new DockPanel
        {
            LastChildFill = true,
            Children = { ManagerRing(lineHeight), text },
        };
        DockPanel.SetDock(face.Children[0], Dock.Left);
        var edge = new Border
        {
            Tag = "manager-line",
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = ManagerLook.Stripe,
            Background = ManagerLook.BarTint,
            CornerRadius = new CornerRadius(4),
            // As a task line's edge (TaskLineButton), plus the 2 DIP its selection frame takes on the left, so the
            // manager line's text starts where the task lines' text does.
            Padding = new Thickness(5, 1, 5, 1),
            Margin = new Thickness(2, 0, 0, 0),
            Child = face,
        };
        var button = TaskButton($"Manager {ask.Verb}", edge, () => OpenManager?.Invoke(), $"{ask.Tip}\n\n{ask.Question}", TaskClickIgnored);
        button.Template = TaskLineTemplate;
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.VerticalAlignment = VerticalAlignment.Top;
        // The 1 DIP under it that parts two task lines, and the group gap above the first task line follows it anyway.
        button.Margin = new Thickness(0, 0, 0, 1);
        AutomationProperties.SetName(button, $"Manager {ask.Verb}: {ask.Question}");
        return button;
    }

    // The ring where a task line has its tick box: a 11 DIP circle with a dot in it, in the stripe's violet, centred on
    // the first text line as the tick box is.
    static FrameworkElement ManagerRing(double lineHeight) => new Canvas
    {
        Tag = "manager-ring",
        Width = 11,
        Height = 11,
        Margin = new Thickness(1, Math.Max(0, (lineHeight - 11) / 2), 1, 0),
        VerticalAlignment = VerticalAlignment.Top,
        IsHitTestVisible = false,
        Children =
        {
            new Ellipse { Width = 9, Height = 9, Stroke = ManagerLook.Stripe, StrokeThickness = 1.4, Margin = new Thickness(1) },
            new Ellipse { Width = 3.4, Height = 3.4, Fill = ManagerLook.Stripe, Margin = new Thickness(3.8) },
        },
    };
}
