using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// THE LINE MENU'S ACTIONS ON THE LINE, AS MANY AS FIT (task 59b8f613; Petre, on a screenshot of a line's "⋯" menu: "move
// those context menu buttons to the task itself if there's space for them", then "a", option A of the mockup at
// https://claude.ai/artifact/XnnhzhfmVkzjYCTBCeKheh). A slim line's controls strip used to end at "⋯" with about 130 DIP of
// tile left empty after it at the default 460 DIP panel. The menu's actions now fill that room as icon buttons, in a
// fixed order, between Open Claude and "⋯":
//
//   Edit (E70F)  Open in window (E737)  Add subtask (E710)  Assign to (E77B)  Related to (E71B)  Move to (E8DE)  Delete (E74D)
//
// The order is the order they are given up in. Measured on a Claude task's line with a comment count: at 360 DIP (the
// narrowest panel) Edit alone, at 460 (the default) six, and all seven a little wider. FitStrip (below) does the fitting from the width it is given, so a line never decides it from numbers
// that might be stale. "⋯" stays last, right after the last button shown, and keeps every item: the menu is still the
// full list, for the buttons left off, for the Matrix (whose line has no room and gets none) and for the right-click.
// Left in the menu only: Status and Close (the tick), Snooze (the moon), Duplicate of, Goal and Open parent.
//
// Each action keeps its slot on every line, an empty slot where it does not apply (a closed task cannot be edited), so a
// button sits at the same x on every line, as every other column does. A full line (710 DIP and wider) has the room for
// all seven, a fixed column beside the others at the right end, which the title gives up.
//
// The buttons are quiet like the comments and "⋯" (half strength until the line is hovered or picked, Quieten) and click
// like them (Small: the click guard, and the click kept from the line). A button whose action is a menu (Assign to, Move
// to, Delete) opens it under the button; Delete's menu holds "Delete for good", the same second step the "⋯" menu has.
//
// THE THREAD'S ROW STEPS ASIDE FOR THEM (task 1c17b7d6, related; Petre there: "i mean at the top, next to important,
// snooze, etc."). An open task's thread starts with a row of buttons (ThreadActions: Open in window, Open Claude or
// Start Claude on this, Add subtask). Each of those that the line right above shows is left out of the row, and the row
// goes once all of them are, so the same button is not drawn twice one under the other. On a narrow panel, where the
// line has no room for Open in window or Add subtask, those stay in the row (FollowLine).
public partial class TasksWindow
{
    // The slots, in the order they are given up in. The thread's row (FollowLine) reads two of them.
    const int EditSlot = 0;
    const int OpenInWindowSlot = 1;
    const int AddSubtaskSlot = 2;
    const int LineButtonSlots = 7;

    /// <summary>The words of the menu item and the line's button, which open the assignee's choices.</summary>
    internal const string AssignToText = "Assign to";

    /// <summary>The words of the menu item and the line's button, which open the workspace menu.</summary>
    internal const string MoveToText = "Move to…";

    /// <summary>The line's Delete button and the menu item, whose own menu holds "Delete for good".</summary>
    internal const string DeleteText = "Delete";

    /// <summary>Tagged for the tests: a line's strip of menu actions with "⋯" last (FitStrip).</summary>
    internal const string LineButtonsTag = "task-line-buttons";

    // Per line, rebuilt with the rows: its strip of buttons, which the thread's row under it follows.
    readonly Dictionary<Guid, FitStrip> lineButtons = [];

    // A line's strip: the seven slots, each its button or an empty slot where the action does not apply, then "⋯".
    // Matrix lines have none (Line calls this for List lines only).
    FitStrip LineButtons(TaskRowView row, Button more)
    {
        // A closed task is not edited and has no subtasks added (the menu's rule); a line in its undo window is closed in
        // all but name, and its buttons would act on a task about to go.
        var open = row.Status != TaskStatus.Closed && !row.Closing;
        var live = !row.Closing;
        var slots = new[]
        {
            IconButton(open, "", "Edit", "Edit the title and the description (F2)", _ => BeginRename(row.Id)),
            IconButton(open && Actions?.OpenWindow is not null, "", OpenInWindowText, "Read and edit the whole task in a window of its own",
                _ => Actions?.OpenWindow?.Invoke(row.Id)),
            // A press while this line's box or picker is already open leaves it alone: starting it again would clear what
            // was typed and the pictures pasted into it (review of this change). The thread's own Add subtask leaves its
            // row while the box is open for the same reason.
            IconButton(open && Actions?.SubtaskParent is not null, "", FloatingBar.AddSubtaskText, "Add a subtask under this task",
                _ =>
                {
                    if (subtaskUnder != row.Id) BeginSubtaskAdd(row.Id);
                }),
            IconButton(live, "", AssignToText, $"Assigned to {row.Assignee.Name}: change who it is on", button => OpenAssignMenu(row.Id, button)),
            IconButton(live && Actions?.LinkCandidates is not null, "", RelatedToText, "Link a related task", _ =>
            {
                if (linking is not { Duplicate: false } picking || picking.Task != row.Id) BeginLink(row.Id, duplicate: false);
            }),
            IconButton(live, "", MoveToText, "Move it to another workspace", button => OpenMoveMenu(row.Id, row.Place.Workspace, button)),
            IconButton(live, "", DeleteText, "Delete it, after one more click", button => OpenDeleteMenu(row.Id, button)),
        };
        var strip = new FitStrip(ButtonColumn) { Tag = LineButtonsTag, VerticalAlignment = VerticalAlignment.Center };
        // An empty element holds a slot the action does not apply to, so the next button stays at its own x.
        slots.Select(button => (UIElement?)button ?? new FrameworkElement()).ToList().ForEach(slot => strip.Children.Add(slot));
        strip.Children.Add(more);
        lineButtons[row.Id] = strip;
        return strip;
    }

    // One icon button of the strip, or none where its action does not apply. The press gets the button, the anchor of
    // the menu it may open.
    Button? IconButton(bool applies, string glyph, string name, string tip, Action<Button> press)
    {
        if (!applies) return null;
        Button? button = null;
        button = Small(new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 12, Foreground = Ink, VerticalAlignment = VerticalAlignment.Center },
            name, () => press(button!), tip);
        return button;
    }

    // START CLAUDE ON THE LINE: the action column's button on a task no Claude session has joined, where a Claude task has
    // Open Claude (OpenClaudeButton). The "⋯" menu has the same pair, one or the other (task b6f068b1). Segoe's "Play",
    // U+E768, in both icon fonts.
    Button? StartClaudeButton(TaskRowView row) =>
        StartsClaude(row) && Actions?.StartClaude is { } start
            ? Small(new TextBlock { Text = "", FontFamily = IconFont, FontSize = 12, Foreground = Ink, VerticalAlignment = VerticalAlignment.Center },
                StartClaudeText, () => start(row.Id), "Start Claude on this: a background session with the task as its first message")
            : null;

    // Whether a line has the Start Claude button: an open task no session has joined, while Deskbar can start one.
    bool StartsClaude(TaskRowView row) =>
        !row.Claudes && !row.Closing && row.Status != TaskStatus.Closed && Actions?.StartClaude is not null;

    // The assignee's choices under the line's button, as the "⋯" menu's "Assign to" submenu lists them.
    // Internal, answering the menu, for the tests.
    internal ContextMenu OpenAssignMenu(Guid id, UIElement anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        AssignItems(id).ForEach(item => menu.Items.Add(item));
        OpenMenu(menu);
        return menu;
    }

    // Me and Claude, the task's own ticked (Ruling 57). The "⋯" menu's submenu and the line's button share them.
    List<MenuItem> AssignItems(Guid id)
    {
        var assignee = latest?.Groups.SelectMany(g => g.Rows).FirstOrDefault(r => r.Id == id)?.Assignee;
        return new[] { Assignee.Me, Assignee.Claude }.Select(who =>
        {
            var item = Item(who.Name, () => Actions?.Assign(id, who));
            item.IsChecked = assignee == who;
            return item;
        }).ToList();
    }

    // "Delete for good" under the line's Delete button: deleting cannot be undone, so the button takes the same second,
    // deliberate step as the "⋯" menu's Delete submenu. Internal, answering the menu, for the tests.
    internal ContextMenu OpenDeleteMenu(Guid id, UIElement anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        menu.Items.Add(Item("Delete for good", () => Actions?.Delete(id)));
        OpenMenu(menu);
        return menu;
    }

    // The thread's row of buttons follows the line above it: each button whose twin the line shows is collapsed, and the
    // row too once none is left (see the top of this file). Called while the thread is built, after its line, and again
    // each time the line's strip fits a different number of buttons (a resize). The line always has its Claude button,
    // Open Claude on a Claude task (the thread's Open Claude needs one) and Start Claude on a task with no session; the
    // thread's Start Claude on a Claude task whose session expired has no twin on the line and stays. While the line is
    // being edited its controls are put away (ControlsUnderTitle collapses a slim line's strip), so the Claude button then
    // stays in the row (review of this change); Open in window and Add subtask follow the strip, which fits nothing then.
    void FollowLine(WrapPanel actions, TaskRowView row, FitStrip strip)
    {
        var buttons = actions.Children.OfType<Button>().ToList();
        var editing = renaming == row.Id;
        bool OnLine(Button button, int fitted) => AutomationProperties.GetName(button) switch
        {
            OpenInWindowText => fitted > OpenInWindowSlot && strip.Children[OpenInWindowSlot] is Button,
            ThreadAddSubtaskText => fitted > AddSubtaskSlot && strip.Children[AddSubtaskSlot] is Button,
            OpenClaudeText => !editing,
            StartClaudeText => !editing && StartsClaude(row),
            _ => false,
        };
        strip.Fitted.Subscribe(fitted =>
        {
            buttons.ForEach(button => button.Visibility = OnLine(button, fitted) ? Visibility.Collapsed : Visibility.Visible);
            actions.Visibility = buttons.Any(button => button.Visibility == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    // For the tests: how many of a line's menu buttons its strip shows now (null for a line with no strip).
    internal int? LineButtonsShown(Guid id) => lineButtons.TryGetValue(id, out var strip) ? strip.Shown : null;
}

/// <summary>
/// A row of equal slots that shows as many of its children as its width allows, in order, and always its last child
/// right after the shown ones (task 59b8f613: a task line's menu actions, then "⋯"). The children it has no room for are
/// arranged past its right edge and clipped away, so they are neither drawn nor hit. <see cref="Fitted"/> reports how
/// many of the leading children show, again whenever that changes.
/// </summary>
//
// Measured with the width it is given: in a star column (the slim line's strip) that is the room the line has left; in a
// fixed column (the full line's) it is enough for all. With no limit (infinite width), all show.
internal sealed class FitStrip : Panel
{
    readonly double slot;
    readonly BehaviorSubject<int> fitted = new(0);

    public FitStrip(double slotWidth)
    {
        slot = slotWidth;
        ClipToBounds = true;
    }

    /// <summary>How many of the leading children show now, and each change after.</summary>
    public IObservable<int> Fitted => fitted.DistinctUntilChanged();

    /// <summary>How many of the leading children show now.</summary>
    public int Shown => fitted.Value;

    // The leading children that fit beside the last one in this width. A DIP of slack, so layout rounding at a scaling
    // other than 100% (a device pixel off a column capped at exactly the slots' width) does not drop the last button.
    int SlotsIn(double width) =>
        double.IsInfinity(width) ? Children.Count - 1 : Math.Clamp((int)Math.Floor((width + 1) / slot) - 1, 0, Math.Max(0, Children.Count - 1));

    protected override Size MeasureOverride(Size available)
    {
        var height = Children.OfType<UIElement>().Select(child =>
        {
            child.Measure(new Size(slot, available.Height));
            return child.DesiredSize.Height;
        }).DefaultIfEmpty(0).Max();
        return new Size((SlotsIn(available.Width) + 1) * slot, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var shown = SlotsIn(finalSize.Width);
        var last = Children.Count - 1;
        // Shown: one slot each from the left. The last child: the slot after them. The rest: past the edge, clipped.
        Children.OfType<UIElement>().Select((child, i) => (child, i)).ToList().ForEach(c => c.child.Arrange(new Rect(
            c.i == last ? shown * slot : c.i < shown ? c.i * slot : finalSize.Width + (c.i + 1) * slot, 0, slot, finalSize.Height)));
        if (shown != fitted.Value) fitted.OnNext(shown);
        return finalSize;
    }
}
