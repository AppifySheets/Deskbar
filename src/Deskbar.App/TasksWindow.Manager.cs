using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// THE MANAGER LINE IN THE TASKS PANEL (task 61e4d143; Core's ManagerQuestion has the rules, ManagerLook the colours). While
// the manager has a question open for Petre, the panel shows it as one violet tile above the list (and above the Matrix),
// outside their scrolling, so it is always the first thing on the panel: "Manager asks", the "Manager" chip and the age on
// the first row, the whole question wrapped under them. It is drawn like a task's tile (the LineButton template, the 3 DIP
// stripe, the 6 DIP inset) so it reads as part of the list, but it is no task: no tick, no toggles, no menu, no thread.
// A click opens the manager (TasksWindowActions.OpenManager). The Stats view hides it, as it hides the list.
public partial class TasksWindow
{
    // The age on the manager line, rewritten with the other ages (RefreshAges); none while no line shows.
    (TextBlock Text, DateTimeOffset AskedAt, bool Answered)? managerAge;

    void DrawManagerLine(ManagerAsk? ask)
    {
        managerAge = null;
        ManagerLineHost.Child = ask is null ? null : ManagerLine(ask);
        ManagerLineHost.Visibility = ask is null ? Visibility.Collapsed : Visibility.Visible;
    }

    Button ManagerLine(ManagerAsk ask)
    {
        var now = Now();
        var title = new TextBlock { FontSize = 12, Foreground = ManagerLook.TitleInk, Margin = new Thickness(2, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        title.Inlines.Add(new Run("Manager") { FontWeight = FontWeights.Bold });
        title.Inlines.Add(new Run(" " + ask.Verb));
        var chip = new Border
        {
            Tag = "manager-chip",
            Background = ManagerLook.ChipFill,
            BorderBrush = ManagerLook.Stripe,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6, 0, 6, 1),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Child = new TextBlock { Text = "Manager", FontSize = 10, Foreground = ManagerLook.QuestionInk },
        };
        var age = new TextBlock
        {
            Tag = "manager-age",
            Text = TaskAge.Format(now - ask.AskedAt),
            FontSize = 10.5,
            Foreground = HeaderInk,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = AskedTip(now, ask.AskedAt, ask.Answered),
        };
        managerAge = (age, ask.AskedAt, ask.Answered);
        // The whole question, wrapped: it is the point of the line, and there is only ever one.
        var question = new TextBlock
        {
            Tag = "manager-question",
            Text = ask.Question,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = ManagerLook.QuestionInk,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 0, 4, 0),
        };
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
            },
            RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition { Height = GridLength.Auto } },
            Children = { title, chip, age, question },
        };
        Grid.SetColumn(chip, 1);
        Grid.SetColumn(age, 2);
        Grid.SetRow(question, 1);
        Grid.SetColumnSpan(question, 3);
        var edge = new Border
        {
            Tag = "manager-line",
            BorderBrush = ManagerLook.Stripe,
            BorderThickness = new Thickness(3, 0, 0, 0),
            CornerRadius = new CornerRadius(3, 0, 0, 3),
            // A task tile's edge padding (Line) plus the 2 DIP its selection frame takes, so the texts line up.
            Padding = new Thickness(6, 3, 8, 3),
            Child = grid,
        };
        var button = new Button
        {
            Tag = "manager-line-button",
            Template = (ControlTemplate)FindResource("LineButton"),
            Background = ManagerLook.PanelFill,
            Content = edge,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            // The tiles' inset and gap, and the list's first caption follows with its own 6 DIP above it.
            Margin = new Thickness(6, 3, 6, 0),
            Focusable = false,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = ask.Tip,
        };
        AutomationProperties.SetName(button, $"Manager {ask.Verb}: {ask.Question}");
        // The panel's click guard (ClickIgnored): a redraw that just moved the lines distrusts a click for half a second.
        button.Click += (_, e) =>
        {
            e.Handled = true;
            if (ClickIgnored()) return;
            Actions?.OpenManager?.Invoke();
        };
        return button;
    }

    // The age's tooltip, as a task line's (AgeTip) but "Asked" (or "Answered", task 2c5de076) rather than "Added".
    static string AskedTip(DateTimeOffset now, DateTimeOffset asked, bool answered = false) =>
        // TaskAge.Words says "Added 2 hours ago"; the manager asked or answered, so its first word is swapped.
        $"{(answered ? "Answered" : "Asked")}{TaskAge.Words(now - asked)["Added".Length..]}\n{asked.ToLocalTime():dddd d MMMM yyyy, HH:mm}";

    // RefreshAges' share for the manager line.
    void RefreshManagerAge(DateTimeOffset now)
    {
        if (managerAge is not { } shown) return;
        shown.Text.Text = TaskAge.Format(now - shown.AskedAt);
        shown.Text.ToolTip = AskedTip(now, shown.AskedAt, shown.Answered);
    }
}
