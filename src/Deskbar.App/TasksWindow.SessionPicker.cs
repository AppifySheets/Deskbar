using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// THE PICKER ROW UNDER A TASK'S COMMENT BOX (the picked mockup's pickComposer and pickMenu, notes\mockups\2026-11-26-tasks-
// redesign-round2): the model and effort the task's Claude session runs on, and its permission mode, each a quiet button
// whose menu changes it. One drawing for the panel's page and a task window's (which draws through a TasksWindow of its
// own, TasksWindow.InboxWindow.cs, with the same actions); DrawInboxPage's body puts it under the thread, whose last part
// is the comment box. The permission cards are docked at the page's bottom, outside the scrolling body, so the two never
// take the same room.
//
// WHAT IT SAYS is SessionPicker.RowOf's (Core): the buttons' words ("Sonnet · medium", "Edit by itself"); while a pick
// waits for a pause or a switch runs, that in words beside them, with Cancel where there is something to cancel; for a
// session in a window of its own (VS Code, a terminal), disabled buttons whose tooltip says why, never hidden. A task
// with no session has the row too: its pick is what the first start passes.
//
// THE MENUS: the choices, the current one ticked by the menu's own tick mark (never by colour), each permission mode with
// its one-line explanation, and the note that a change applies at Claude's next pause. "Other model…" opens a box in the
// row for a model's full name, checked by SessionChoices before anything is picked. A pick goes to ClaudeSessions.Pick
// (the one way a switch begins); what it met that needs saying (kept for the next start, a refusal) is one line under
// the row.
//
// THE KEYBOARD: the buttons take focus by Tab, and Enter or Space opens the menu, which the arrows and Enter work as any
// menu. The other-model box takes Enter to pick and Esc to close.
public partial class TasksWindow
{
    internal const string PickerRowTag = "session-picker";
    internal const string PickerModelName = "Model and effort";
    internal const string PickerModeName = "Permission mode";
    internal const string PickerCancelText = "Cancel";
    internal const string OtherModelText = "Other model…";
    internal const string OtherModelBoxTag = "picker-other-model";
    internal const string ModelDefaultText = "Model default";
    internal const string ModeDefaultText = "Claude Code's setting";
    internal const string PickerTickTag = "picker-tick";

    // What a pick met, one line per task; whether the other-model box is open, and what was typed in it.
    readonly Dictionary<Guid, string> pickerNote = [];
    readonly HashSet<Guid> otherModelOpen = [];
    readonly Dictionary<Guid, string> otherModelDraft = [];

    SessionPickRow? PickRowOf(Guid id) => Actions?.SessionPickOf?.Invoke(id);

    // Part of the page's redraw signature: the row's words and state, the note and the box.
    string PickerSignature(Guid id) =>
        $"|pick:{PickRowOf(id)?.Signature}|{pickerNote.GetValueOrDefault(id)}|{otherModelOpen.Contains(id)}";

    /// <summary>The row under the comment box, or null for a closed task or a copy without Claude (the page body's hook).</summary>
    FrameworkElement? SessionPickerRow(TaskRowView row)
    {
        if (row.Closing || row.Status == TaskStatus.Closed || PickRowOf(row.Id) is not { } pick) return null;
        var stack = new StackPanel { Tag = PickerRowTag, Margin = new Thickness(2, 6, 0, 2) };
        var buttons = new WrapPanel();
        Button? modelButton = null, modeButton = null;
        modelButton = PickerButton("spark", pick.ModelWords, PickerModelName, pick, () => OpenModelMenu(row.Id, modelButton!));
        modeButton = PickerButton("bolt", pick.ModeWords, PickerModeName, pick, () => OpenModeMenu(row.Id, modeButton!));
        buttons.Children.Add(modelButton);
        buttons.Children.Add(modeButton);
        if (pick.PhaseWords.Length > 0)
        {
            var words = Text(pick.PhaseWords, 12, InboxLook.Ink2, margin: new Thickness(2, 0, 8, 4));
            words.TextWrapping = TextWrapping.Wrap;
            AutomationProperties.SetName(words, "Picker state");
            buttons.Children.Add(words);
        }
        if (pick.CanCancel)
        {
            var cancel = InboxButton(Text(PickerCancelText, 12, InboxLook.Ink, FontWeights.SemiBold), PickerCancelText, () => CancelPick(row.Id),
                new Thickness(10, 3, 10, 4), InboxLook.Raised, 10, InboxLook.StrongLine, ClickIgnored);
            cancel.Focusable = true;
            cancel.IsTabStop = true;
            cancel.Margin = new Thickness(0, 0, 6, 4);
            cancel.ToolTip = pick.Phase == PickPhase.Confirming
                ? "Go back to the earlier session, which still runs, and stop the new one"
                : "Keep the session as it is";
            buttons.Children.Add(cancel);
        }
        stack.Children.Add(buttons);
        if (otherModelOpen.Contains(row.Id) && pick.Pickable) stack.Children.Add(OtherModelBox(row.Id, pick));
        if (pickerNote.GetValueOrDefault(row.Id) is { Length: > 0 } note)
        {
            var line = Text(note, 12, InboxLook.Ink2, margin: new Thickness(2, 2, 2, 0));
            line.TextWrapping = TextWrapping.Wrap;
            stack.Children.Add(line);
        }
        return stack;
    }

    // A quiet button: an icon and the words, a hairline edge, no fill. Disabled, its words stay readable (Ink2) and its
    // tooltip still shows, saying why.
    Button PickerButton(string icon, string words, string name, SessionPickRow pick, Action press)
    {
        var ink = pick.Pickable ? InboxLook.Ink : InboxLook.Ink2;
        var face = Row(InboxMarks.Icon(icon, 13, ink), Text(words, 12, ink));
        var button = InboxButton(face, name, press, new Thickness(9, 3, 10, 4), Brushes.Transparent, 10, InboxLook.StrongLine, ClickIgnored);
        return Finished(button, pick);
    }

    Button Finished(Button button, SessionPickRow pick)
    {
        button.Margin = new Thickness(0, 0, 6, 4);
        button.Focusable = true;
        button.IsTabStop = true;
        button.IsEnabled = pick.Pickable;
        button.ToolTip = pick.ReadOnlyWhy.GetValueOrDefault(pick.Phase == PickPhase.Switching ? SettingsSwitch.UnderWay : pick.Tip);
        ToolTipService.SetShowOnDisabled(button, true);
        return button;
    }

    // --- the menus ---------------------------------------------------------------------------------------------------

    /// <summary>The model and effort menu under its button. Internal, answering the menu, for the tests.</summary>
    internal ContextMenu OpenModelMenu(Guid id, UIElement anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        if (PickRowOf(id) is not { } pick) return menu;
        var shown = pick.Shown;
        menu.Items.Add(Heading("Model"));
        foreach (var model in SessionChoices.Models)
            menu.Items.Add(Choice(model.Label, shown.Model.Map(m => m == model.Value).GetValueOrDefault(false), () => PickSettings(id, shown with { Model = model.Value })));
        var other = shown.Model.Map(m => SessionChoices.Models.All(c => c.Value != m)).GetValueOrDefault(false);
        menu.Items.Add(Choice(other ? $"{OtherModelText} ({shown.Model.Value})" : OtherModelText, other, () => OpenOtherModel(id)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Heading("Effort"));
        menu.Items.Add(Choice(ModelDefaultText, shown.Effort.HasNoValue, () => PickSettings(id, shown with { Effort = Maybe<string>.None })));
        foreach (var effort in SessionChoices.Efforts)
            menu.Items.Add(Choice(effort.Label, shown.Effort.Map(e => e == effort.Value).GetValueOrDefault(false), () => PickSettings(id, shown with { Effort = effort.Value })));
        menu.Items.Add(new Separator());
        menu.Items.Add(Heading(SessionPicker.NextPauseTip));
        OpenMenu(menu);
        return menu;
    }

    /// <summary>The permission mode menu under its button, each mode with what it lets Claude do. Internal, for the tests.</summary>
    internal ContextMenu OpenModeMenu(Guid id, UIElement anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        if (PickRowOf(id) is not { } pick) return menu;
        var shown = pick.Shown;
        menu.Items.Add(Heading("What Claude may do without asking"));
        foreach (var mode in SessionChoices.Modes)
            menu.Items.Add(Choice(mode.Label, shown.Mode.Map(m => m == mode.Value).GetValueOrDefault(false), () => PickSettings(id, shown with { Mode = mode.Value }), mode.Hint));
        menu.Items.Add(Choice(ModeDefaultText, shown.Mode.HasNoValue, () => PickSettings(id, shown with { Mode = Maybe<string>.None }),
            "whatever Claude Code's own settings say"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Heading(SessionPicker.NextPauseTip));
        OpenMenu(menu);
        return menu;
    }

    // A choice: its words (and, for a mode, its explanation on a second line), ticked by the menu's tick mark when current.
    static MenuItem Choice(string words, bool current, Action click, string hint = "")
    {
        // The tick is drawn in the header itself, in the menu's own ink: the theme's menu does not always draw the check
        // of IsChecked (it did not in the Fluent look, seen in a rendered picture), and it must never be colour alone.
        var lines = new StackPanel();
        lines.Children.Add(new TextBlock { Text = words, FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal });
        if (hint.Length > 0) lines.Children.Add(new TextBlock { Text = hint, FontSize = 11, Opacity = 0.85 });
        var tick = new TextBlock { Tag = PickerTickTag, Text = current ? "✓" : "", Width = 18, FontWeight = FontWeights.SemiBold };
        Grid.SetColumn(lines, 1);
        var header = new Grid { Children = { tick, lines } };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var item = new MenuItem { Header = header, IsChecked = current };
        AutomationProperties.SetName(item, words);
        item.Click += (_, _) => click();
        return item;
    }

    // A heading or a note in a menu: words only, nothing to click and not a stop for the arrows.
    static MenuItem Heading(string words) =>
        new() { Header = new TextBlock { Text = words, FontSize = 11, Margin = new Thickness(18, 0, 0, 0) }, IsHitTestVisible = false, Focusable = false, StaysOpenOnClick = true };

    // --- a pick, Cancel and the other-model box ----------------------------------------------------------------------

    void PickSettings(Guid id, SessionSettings settings)
    {
        if (Actions?.PickSession is not { } pick) return;
        otherModelOpen.Remove(id);
        pickerNote.Remove(id);
        var picked = pick(id, settings);
        if (picked.IsCompleted) Picked(id, picked.Result);
        else picked.ContinueWith(done => Dispatcher.BeginInvoke(new Action(() => Picked(id, done.Result))), TaskScheduler.Default);
        if (latest is not null) Draw(latest);
    }

    // What the pick met, said under the row when it needs saying.
    void Picked(Guid id, Result<SwitchStep> result)
    {
        var note = result.IsFailure ? result.Error
            : result.Value is SwitchStep.NextStart && PickRowOf(id)?.Tip != SessionPicker.FirstStartTip
                ? "The session can no longer be resumed. Your pick is used by the next session started on this task."
            : null;
        if (note is null) pickerNote.Remove(id);
        else pickerNote[id] = note;
        if (latest is not null) Draw(latest);
    }

    void CancelPick(Guid id)
    {
        if (Actions?.CancelPick is not { } cancel) return;
        var result = cancel(id);
        if (result.IsFailure) pickerNote[id] = result.Error;
        else pickerNote.Remove(id);
        if (latest is not null) Draw(latest);
    }

    void OpenOtherModel(Guid id)
    {
        otherModelOpen.Add(id);
        otherModelFocus = id;
        if (latest is not null) Draw(latest);
    }

    // Set when the box is opened, so the box takes the keyboard once, not on every redraw.
    Guid? otherModelFocus;

    FrameworkElement OtherModelBox(Guid id, SessionPickRow pick)
    {
        var box = new TextBox
        {
            Tag = OtherModelBoxTag,
            Text = otherModelDraft.GetValueOrDefault(id, ""),
            FontSize = 12,
            Foreground = InboxLook.Ink,
            Background = InboxLook.Background,
            CaretBrush = InboxLook.Ink,
            BorderBrush = InboxLook.StrongLine,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 3, 6, 3),
            MinWidth = 220,
            ToolTip = "A model's full name, such as claude-opus-5-5. Enter picks it, Esc closes.",
        };
        AutomationProperties.SetName(box, "Other model");
        box.TextChanged += (_, _) => otherModelDraft[id] = box.Text;
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                otherModelOpen.Remove(id);
                if (latest is not null) Draw(latest);
            }
            else if (e.Key == Key.Enter)
            {
                e.Handled = true;
                var model = SessionChoices.Model(box.Text);
                if (model.IsFailure)
                {
                    pickerNote[id] = model.Error;
                    if (latest is not null) Draw(latest);
                    return;
                }
                otherModelDraft.Remove(id);
                PickSettings(id, pick.Shown with { Model = model.Value });
            }
        };
        if (otherModelFocus == id)
        {
            otherModelFocus = null;
            box.Loaded += (_, _) => box.Focus();
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 4) };
        row.Children.Add(Text("Model:", 12, InboxLook.Ink2, margin: new Thickness(0, 0, 6, 0)));
        row.Children.Add(box);
        return row;
    }
}
