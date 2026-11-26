using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;

namespace Deskbar.App;

// THE FOOT OF THE BAR'S TASK LIST IN THE INBOX LOOK (Deskbar task 740a2231; the owner, of the small "+1" / "All" button at
// the right end of the add row: he did not like where it was, and its name changed under him). In the Inbox look the row
// under the add box holds only what belongs to adding (the workspace chip, the flags, the play button), and the two
// buttons that used to share it move to a row of their own at the END of the list, as the approved mockup has it (`.b-ft`
// in notes\mockups\2026-11-26-tasks-redesign-round2): "All tasks" on the left, "Manager" on the right, two equal quiet
// buttons. The old look keeps its add row exactly as it was.
//
// WHAT THE OLD BUTTON SAID (PaintTaskMore, MoreText, ApplyMoreText), and what this one says instead:
//   nothing out of sight, panel wired     "All tasks", cut to "All" on a narrow bar   ->  "All tasks"
//   N lines out of sight                  "+N more", "+N waiting" or "+N more, W waiting", orange while some wait,
//                                         cut to "+N"                                  ->  "All tasks · N more"
//   the panel shown                       the same words on the picked fill; a click closes the panel
//                                                                                      ->  "Hide tasks"
//   no panel wired (expanded in place)    "+N more" / "Less"                           ->  "All tasks · N more" / "Fewer tasks"
// The count means what it always meant: the open lines out of sight in the list's viewer, scrolled away or below it
// (PaintTaskMore, LinesInSight), the lines folded into the "+N more waiting" line counted only while that line is out of
// sight too. A click does what it always did (OnTaskMoreClick: the panel shown or hidden, or the list expanded in place),
// and so does a right-click (nothing: the list's ContextMenuOpening is handled). The button shows when the old one would
// have; the Manager button when the App wired OpenManager, with the dot while Deskbar woke the manager and the user has
// not opened it since.
//
// OUTSIDE THE LINES' PANEL. The row is the last child of the list's own column, after the lines' viewer, never inside
// the panel the lines are drawn in: the slot count, the fold's place, the scroll and "+N more" all read that panel. The
// docked bar's fit (LineRoom) counts everything of the list's column but the viewer as what the list cannot give to
// lines, so it leaves the row its height. The manager's violet question line keeps its place above the lines.
public partial class FloatingBar
{
    Border? inboxFooter;
    Grid? inboxFooterButtons;
    Button? inboxAllTasks;
    Button? inboxManager;
    TextBlock? inboxAllTasksWords;
    System.Windows.Shapes.Ellipse? inboxManagerDot;
    TextBlock? inboxManagerWords;
    FrameworkElement? inboxManagerMark;

    // What the last PaintTaskMore found: how many open lines are out of sight, and how many of those wait on the user.
    int inboxFooterHidden;
    int inboxFooterWaiting;

    internal const string InboxFooterTag = "task-list-footer";
    internal const string AllTasksName = "All tasks";

    // The footer's buttons: a rounded quiet fill that lightens under the pointer, their words centred (the mockup's
    // `.b-ft button`). Sealed, so the bar's threads in the tests may all use it.
    static readonly ControlTemplate InboxFooterButtonTemplate = Sealed((ControlTemplate)XamlReader.Parse(
        """
        <ControlTemplate TargetType="Button" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Border x:Name="Chrome" Background="{TemplateBinding Background}" CornerRadius="12" Padding="{TemplateBinding Padding}">
            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" />
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter TargetName="Chrome" Property="Background" Value="#FF2C323D" />
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """));

    static readonly Brush InboxFooterFill = Frozen(0xFF, 0x20, 0x24, 0x2C);
    static readonly Brush InboxFooterOnFill = Frozen(0xFF, 0x27, 0x2C, 0x36);
    static readonly Brush InboxFooterHairline = Frozen(0xFF, 0x2B, 0x2B, 0x30);

    // The row, built once with the list (BuildTaskList puts it after the lines' viewer); collapsed until a paint in the
    // Inbox look shows it.
    UIElement InboxFooterRow()
    {
        inboxAllTasksWords = new TextBlock { FontSize = Text(9), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        inboxAllTasks = FooterButton(AllTasksName, inboxAllTasksWords, OnTaskMoreClick);
        inboxManagerDot = new System.Windows.Shapes.Ellipse
        {
            Width = 6, Height = 6, Fill = InboxLook.Amber, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        var managerWords = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                inboxManagerDot,
                (inboxManagerMark = InboxMarks.Icon("spark", Math.Round(Text(9)), InboxLook.Violet)),
                (inboxManagerWords = new TextBlock { Text = "Manager", FontSize = Text(9), FontWeight = FontWeights.SemiBold, Foreground = InboxLook.Ink, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }),
            },
        };
        inboxManager = FooterButton("Manager", managerWords, () => OpenManager?.Invoke());
        Grid.SetColumn(inboxManager, 1);
        var row = inboxFooterButtons = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition() },
            Children = { inboxAllTasks, inboxManager },
        };
        inboxFooter = new Border
        {
            Tag = InboxFooterTag,
            BorderBrush = InboxFooterHairline,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 6, 0, 0),
            Margin = new Thickness(0, 4, 0, 0),
            // The buttons join the row only while the Inbox look shows (PaintInboxFooterLook): in the old look the list has
            // no second "Manager" or panel button, so nothing that finds a button by its name finds two.
            Child = null,
            Visibility = Visibility.Collapsed,
        };
        return inboxFooter;
    }

    Button FooterButton(string name, UIElement content, Action press)
    {
        var button = TaskButton(name, content, press, ignoredWhen: TaskClickIgnored);
        button.Template = InboxFooterButtonTemplate;
        button.Background = InboxFooterFill;
        button.Padding = new Thickness(8, 4, 8, 4);
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.Margin = new Thickness(0, 0, 0, 0);
        return button;
    }

    // PaintTaskMore's last word: the out-of-sight count it took, then the row as it should be now.
    void PaintInboxFooter(int hidden, int waiting)
    {
        inboxFooterHidden = hidden;
        inboxFooterWaiting = waiting;
        PaintInboxFooterLook();
    }

    // The row in the look on show: words, tooltips, which buttons show, and the panel's "on" state. Also called whenever
    // the panel opens or closes (PaintTaskMoreLook) and the manager's dot changes (PaintManagerDot).
    void PaintInboxFooterLook()
    {
        if (inboxFooter is null || inboxAllTasks is null || inboxManager is null || inboxAllTasksWords is null || inboxManagerDot is null) return;
        var allShown = inboxFooterHidden > 0 || taskListExpanded || ToggleTasksPanel is not null;
        var managerShown = OpenManager is not null;
        inboxFooter.Visibility = InboxBarOn && (allShown || managerShown) ? Visibility.Visible : Visibility.Collapsed;
        inboxFooter.Child = InboxBarOn ? inboxFooterButtons : null;
        // The add box and its row follow the same switch (FloatingBar.InboxAdd.cs); this paint runs on every list paint.
        PaintInboxAdd();
        inboxAllTasks.Visibility = allShown ? Visibility.Visible : Visibility.Collapsed;
        inboxManager.Visibility = managerShown ? Visibility.Visible : Visibility.Collapsed;
        // One button alone takes the whole row.
        Grid.SetColumnSpan(inboxAllTasks, managerShown ? 1 : 2);
        Grid.SetColumn(inboxManager, allShown ? 1 : 0);
        Grid.SetColumnSpan(inboxManager, allShown ? 1 : 2);
        inboxManager.Margin = new Thickness(allShown ? 3 : 0, 0, 0, 0);
        inboxAllTasks.Margin = new Thickness(0, 0, managerShown ? 3 : 0, 0);

        // The bar's text size, which can change after the row was built (ApplyTaskListTextSize repaints the list).
        inboxAllTasksWords.FontSize = Text(9);
        if (inboxManagerWords is not null) inboxManagerWords.FontSize = Text(9);
        if (inboxManagerMark is not null) inboxManagerMark.Width = inboxManagerMark.Height = Math.Round(Text(9));

        var on = tasksPanelOn && ToggleTasksPanel is not null;
        var (words, more, tip) = AllTasksWords(on);
        inboxAllTasksWords.Inlines.Clear();
        inboxAllTasksWords.Inlines.Add(new Run(words) { Foreground = InboxLook.Ink, FontWeight = FontWeights.SemiBold });
        if (more.Length > 0) inboxAllTasksWords.Inlines.Add(new Run(more) { Foreground = InboxLook.Ink2 });
        inboxAllTasks.ToolTip = tip;
        inboxAllTasks.Background = on ? InboxFooterOnFill : InboxFooterFill;
        AutomationProperties.SetItemStatus(inboxAllTasks, on ? "on" : "off");

        var unread = ManagerUnread?.Invoke() == true;
        inboxManagerDot.Visibility = unread ? Visibility.Visible : Visibility.Collapsed;
        inboxManager.ToolTip = unread ? "The manager has news since you last opened it. " + ManagerTip : ManagerTip;
    }

    // The panel button's words, its quieter count, and its one-sentence tooltip (see the header).
    (string Words, string More, string Tip) AllTasksWords(bool panelShown)
    {
        var hidden = inboxFooterHidden;
        var more = hidden > 0 ? $" · {hidden} more" : "";
        var outOfSight = hidden == 0 ? ""
            : inboxFooterWaiting > 0 ? $"; {hidden} more {(hidden == 1 ? "is" : "are")} out of sight here, {inboxFooterWaiting} of them waiting on you"
            : $"; {hidden} more {(hidden == 1 ? "is" : "are")} out of sight here";
        return panelShown ? ("Hide tasks", "", "Close the tasks panel.")
            : ToggleTasksPanel is not null ? (AllTasksName, more, $"Open the tasks panel with every task, open and closed{outOfSight}.")
            : taskListExpanded ? ("Fewer tasks", "", "Show only the first lines again.")
            : (AllTasksName, more, $"Show every task in this list{outOfSight}.");
    }

    /// <summary>For the tests: the footer's panel button's words as shown ("All tasks · 3 more"), "" while it does not show.</summary>
    internal string InboxFooterWords =>
        inboxFooter is { Visibility: Visibility.Visible } && inboxAllTasks is { Visibility: Visibility.Visible } && inboxAllTasksWords is not null
            ? string.Concat(inboxAllTasksWords.Inlines.OfType<Run>().Select(r => r.Text))
            : "";

    /// <summary>For the tests: whether the footer row shows.</summary>
    internal bool InboxFooterShown => inboxFooter is { Visibility: Visibility.Visible };
}
