using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// THE ADD BOX AND ITS ROW IN THE INBOX LOOK (Deskbar task 740a2231; the owner: the box and the row under it did not fit
// the new design). Only the drawing changes, and only while the tasks panel shows its Inbox: typing, pictures, Enter,
// Ctrl+Enter, Esc, the placeholder's words, the chip's menu, the toggles' meaning and the play button's work are all
// FloatingBar.TaskList.cs's, and the old look is put back exactly when the Inbox goes.
//
// What the row under the box holds, today and in this look (the panel button and Manager went to the list's foot,
// FloatingBar.InboxFooter.cs):
//   the workspace chip      where a note typed now goes; its press opens the workspace menu (OpenAddTargetMenu).
//                           Here: an outline, a dot in the workspace's colour, the full name, a small down arrow, as the
//                           chip on an Inbox line. "none" has no dot and its word in the second ink;
//   the parent chip         "↳ <parent> ×", only while "Add subtask…" adds under a task; its press drops the parent.
//                           Kept, restyled the same quiet way;
//   Important ("!")         and Urgent (the clock): the flags of the note about to be added. Their two shapes stay.
//                           Off: the glyph in the second ink on nothing; on: a light filled pill with the glyph dark on
//                           it, so on and off differ by fill, not by colour alone. Their tooltips say the state;
//   Add and start Claude    the play glyph, at the row's right end, in a quiet round button. Shown when the App wired
//                           StartClaude, as today.
// The row stays always shown, as it is today (Ruling 12: a row that came and went with the keyboard made the list jump).
//
// HOW IT STAYS THE BAR'S OWN. The controls are the ones BuildTaskList made, and the bar's own paints keep setting what
// they set (the chip's name, the toggles' "on" or "off"). In this look each control's face is swapped for one drawn here,
// and the face follows what the bar set: the chip's name and target (PaintTaskChips' hook), the toggles' automation
// status (a value-changed listener on it, which PaintAddFlags sets on every paint). Leaving the Inbox puts every original
// face, template and brush back.
public partial class FloatingBar
{
    bool inboxAddShown;
    bool inboxAddWired;
    object? oldChipContent;
    object? oldParentContent;
    object? oldImportantContent;
    object? oldUrgentContent;
    object? oldStartContent;
    Border? inboxChipFace;
    System.Windows.Shapes.Ellipse? inboxChipDot;
    TextBlock? inboxChipName;
    TextBlock? inboxChipArrow;
    Border? inboxParentFace;
    TextBlock? inboxParentLabel;
    (Border Pill, TextBlock Glyph)? inboxImportant;
    (Border Pill, TextBlock Glyph)? inboxUrgent;
    Border? inboxStartFace;
    TextBlock? inboxStartGlyph;

    internal const string InboxAddChipTag = "inbox-add-chip";
    internal const string InboxAddToggleTag = "inbox-add-toggle";

    // The add box's rounded face (the mockup's .b-add): a full radius, the field's own fill and edge from the box's
    // properties, so the keyboard's blue edge is a property change. Sealed for the tests' threads.
    static readonly ControlTemplate InboxAddBoxTemplate = Sealed((ControlTemplate)XamlReader.Parse(
        """
        <ControlTemplate TargetType="TextBox" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                  BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="11"
                  Padding="{TemplateBinding Padding}" SnapsToDevicePixels="True">
            <ScrollViewer x:Name="PART_ContentHost" Focusable="False" VerticalAlignment="Center"
                          HorizontalScrollBarVisibility="Hidden" VerticalScrollBarVisibility="Hidden" />
          </Border>
        </ControlTemplate>
        """));

    internal static readonly Brush InboxFieldFill = Frozen(0xFF, 0x0F, 0x12, 0x18);
    internal static readonly Brush InboxFieldEdge = Frozen(0xFF, 0x2E, 0x36, 0x44);
    internal static readonly Brush InboxFieldFocusEdge = Frozen(0xFF, 0x8A, 0xB4, 0xFF);
    internal static readonly Brush InboxToggleOnFill = Frozen(0xFF, 0xEE, 0xF1, 0xF6);
    internal static readonly Brush InboxToggleOnInk = Frozen(0xFF, 0x1B, 0x1C, 0x21);

    // The look on show, both ways. Cheap when nothing changed; called from the list's paints (PaintInboxFooterLook) and
    // from PaintTaskChips.
    void PaintInboxAdd()
    {
        if (taskAddBox is null || taskAddPlaceholder is null || taskAddChip is null || taskAddImportant is null || taskAddUrgent is null
            || taskAddStartClaude is null || taskAddParentChip is null) return;
        WireInboxAdd();
        var on = InboxBarOn;
        if (on != inboxAddShown)
        {
            inboxAddShown = on;
            if (on)
            {
                oldChipContent = taskAddChip.Button.Content;
                oldParentContent = taskAddParentChip.Content;
                oldImportantContent = taskAddImportant.Button.Content;
                oldUrgentContent = taskAddUrgent.Button.Content;
                oldStartContent = taskAddStartClaude.Content;
                taskAddChip.Button.Content = InboxChipFace();
                taskAddParentChip.Content = InboxParentFace();
                taskAddImportant.Button.Content = InboxToggleFace(taskAddImportant, ref inboxImportant);
                taskAddUrgent.Button.Content = InboxToggleFace(taskAddUrgent, ref inboxUrgent);
                taskAddStartClaude.Content = InboxStartFace();
                // The play button at the row's right end: out of the wrapping chips, into the row's own right-hand
                // column (the one the panel button used, which the Inbox look leaves empty).
                if (taskAddChips?.Parent is Grid addRow && taskAddStartClaude.Parent is Panel chips)
                {
                    chips.Children.Remove(taskAddStartClaude);
                    Grid.SetColumn(taskAddStartClaude, 2);
                    addRow.Children.Add(taskAddStartClaude);
                }
                taskAddBox.Template = InboxAddBoxTemplate;
                taskAddBox.Padding = new Thickness(9, 3, 9, 3);
                taskAddPlaceholder.Margin = new Thickness(12, 0, 10, 0);
            }
            else
            {
                taskAddChip.Button.Content = oldChipContent;
                taskAddParentChip.Content = oldParentContent;
                taskAddImportant.Button.Content = oldImportantContent;
                taskAddUrgent.Button.Content = oldUrgentContent;
                taskAddStartClaude.Content = oldStartContent;
                // Back to its place, last of the chips, as BuildTaskList put it.
                if (taskAddStartClaude.Parent is Grid addRow && taskAddChips is not null)
                {
                    addRow.Children.Remove(taskAddStartClaude);
                    Grid.SetColumn(taskAddStartClaude, 0);
                    taskAddChips.Children.Add(taskAddStartClaude);
                }
                // The tooltips the bar gave them (TaskChip, AddFlagToggle).
                taskAddChip.Button.ToolTip = "Choose the workspace for the note";
                taskAddImportant.Button.ToolTip = "Add as important";
                taskAddUrgent.Button.ToolTip = "Add as urgent";
                taskAddBox.Template = TaskAddBoxTemplate;
                taskAddBox.Padding = new Thickness(4, 2, 4, 2);
                taskAddPlaceholder.Margin = new Thickness(7, 0, 6, 0);
            }
        }
        // The brushes and texts, every time: the bar's own paints set the old look's, and a text size may have changed.
        taskAddBox.Background = on ? InboxFieldFill : TaskFieldFill;
        taskAddBox.BorderBrush = !on ? TaskPickedEdge : taskAddBox.IsKeyboardFocusWithin ? InboxFieldFocusEdge : InboxFieldEdge;
        taskAddPlaceholder.Foreground = on ? InboxLook.Ink2 : TaskDimInk;
        if (!on) return;
        PaintInboxChip();
        PaintInboxToggle(taskAddImportant, inboxImportant);
        PaintInboxToggle(taskAddUrgent, inboxUrgent);
        if (inboxParentLabel is not null && taskAddParentLabel is not null)
        {
            inboxParentLabel.Text = $"{taskAddParentLabel.Text}  ×";
            inboxParentLabel.FontSize = Text(8.5);
            inboxParentLabel.MaxWidth = 2 * ChipTextCeiling + 20;
        }
        if (inboxStartGlyph is not null) inboxStartGlyph.FontSize = Text(8.5);
    }

    // Once: the box's edge follows the keyboard, and each toggle's face follows the "on" or "off" PaintAddFlags sets.
    void WireInboxAdd()
    {
        if (inboxAddWired || taskAddBox is null || taskAddImportant is null || taskAddUrgent is null) return;
        inboxAddWired = true;
        taskAddBox.IsKeyboardFocusWithinChanged += (_, _) => PaintInboxAdd();
        var status = DependencyPropertyDescriptor.FromProperty(AutomationProperties.ItemStatusProperty, typeof(Button));
        status.AddValueChanged(taskAddImportant.Button, (_, _) => PaintInboxToggle(taskAddImportant, inboxImportant));
        status.AddValueChanged(taskAddUrgent.Button, (_, _) => PaintInboxToggle(taskAddUrgent, inboxUrgent));
    }

    // The chip: an outline, the dot, the full name, the arrow.
    Border InboxChipFace()
    {
        inboxChipDot = new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
        inboxChipName = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        inboxChipArrow = new TextBlock { Text = "▾", Foreground = InboxLook.Ink2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) };
        inboxChipFace = new Border
        {
            Tag = InboxAddChipTag,
            Background = InboxLook.Surface,
            BorderBrush = InboxLook.StrongLine,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(7, 1, 7, 2),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { inboxChipDot, inboxChipName, inboxChipArrow } },
        };
        return inboxChipFace;
    }

    void PaintInboxChip()
    {
        if (taskAddChip is null || inboxChipName is null || inboxChipDot is null || inboxChipArrow is null) return;
        var target = taskAddTarget;
        var accent = target.HasValue ? workspaceAccents.GetValueOrDefault(target.Value) : null;
        // The full name, as the bar's own label holds it; trimmed only when the bar is narrower than it.
        inboxChipName.Text = taskAddChip.Label.Text;
        inboxChipName.Foreground = target.HasValue ? InboxLook.Ink : InboxLook.Ink2;
        inboxChipName.FontSize = Text(8.5);
        inboxChipName.MaxWidth = Math.Max(60, (taskAddChips?.ActualWidth ?? 0) * 0.6);
        inboxChipArrow.FontSize = Text(7.5);
        inboxChipDot.Fill = accent ?? InboxLook.Ink2;
        inboxChipDot.Visibility = target.HasValue ? Visibility.Visible : Visibility.Collapsed;
        taskAddChip.Button.ToolTip = target.HasValue ? $"Adds to {taskAddChip.Label.Text}: choose another workspace" : "Adds to no workspace: choose one";
    }

    Border InboxParentFace()
    {
        inboxParentLabel = new TextBlock { Foreground = InboxLook.Ink2, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        inboxParentFace = new Border
        {
            Background = InboxLook.Surface,
            BorderBrush = InboxLook.StrongLine,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(7, 1, 7, 2),
            Child = inboxParentLabel,
        };
        return inboxParentFace;
    }

    // A toggle's face: its glyph (the same shape as today's) in a pill that is filled while on.
    Border InboxToggleFace(TaskAddToggle toggle, ref (Border Pill, TextBlock Glyph)? face)
    {
        var glyph = new TextBlock { Text = toggle.Glyph.Text, FontFamily = IconFont, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var pill = new Border { Tag = InboxAddToggleTag, CornerRadius = new CornerRadius(9), Padding = new Thickness(6, 2, 6, 2), Child = glyph, Margin = new Thickness(2, 0, 0, 0) };
        face = (pill, glyph);
        return pill;
    }

    void PaintInboxToggle(TaskAddToggle toggle, (Border Pill, TextBlock Glyph)? face)
    {
        if (!inboxAddShown || face is not { } f) return;
        var on = AutomationProperties.GetItemStatus(toggle.Button) == "on";
        f.Pill.Background = on ? InboxToggleOnFill : Brushes.Transparent;
        f.Glyph.Foreground = on ? InboxToggleOnInk : InboxLook.Ink2;
        f.Glyph.FontSize = Text(8.5);
        var name = toggle.Flag == TaskFlag.Important ? "Important" : "Urgent";
        toggle.Button.ToolTip = $"{name}: {(on ? "on" : "off")}. The note is added as {(on ? "" : "not ")}{name.ToLowerInvariant()}";
    }

    // The play button's face: its glyph in a quiet round button of the raised fill.
    Border InboxStartFace()
    {
        inboxStartGlyph = new TextBlock { Text = "", FontFamily = IconFont, Foreground = InboxLook.Ink2, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        inboxStartFace = new Border { Background = InboxLook.Raised, CornerRadius = new CornerRadius(10), Padding = new Thickness(6, 3, 5, 3), Child = inboxStartGlyph, Margin = new Thickness(4, 0, 0, 0) };
        return inboxStartFace;
    }

    /// <summary>For the tests: whether the add row is drawn in the Inbox look.</summary>
    internal bool AddRowInInboxLook => inboxAddShown;
}
