using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// THE LINE MENU'S ACTIONS ON THE LINE, AS MANY AS FIT (task 59b8f613; Petre, on a screenshot of a line's "⋯" menu: "move
// those context menu buttons to the task itself if there's space for them", then "a", option A of the mockup at
// https://claude.ai/artifact/XnnhzhfmVkzjYCTBCeKheh). A slim line's controls strip used to end at "⋯" with about 130 DIP of
// tile left empty after it at the default 460 DIP panel. The menu's actions now fill that room as icon buttons, in a
// fixed order, between Open Claude and "⋯":
//
//   Edit (E70F)  Open in window (E737)  Add subtask (E710)  Assign to (E77B)  Related to (E71B)  Move to (E8DE)  Delete (E74D)
//   Reevaluate (E72C, task 3dd07539: only on a Claude task waiting on Petre, see ReevaluateSlot)
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
    const int ReevaluateSlot = 7;
    const int LineButtonSlots = 8;

    // REEVALUATE ON THE LINE (task 3dd07539). The eighth slot, LAST, because it applies to few lines: a task waiting on
    // Petre (a question or Done) that a Claude session has worked on (Reevaluates). Anywhere earlier, its empty slot would
    // leave a gap between two buttons on nearly every line. Last, a full line shows it, and a narrower one leaves it to the
    // open thread's row (ThreadActions, which follows the line, FollowLine) and to the "⋯" menu, which has it on every open
    // Claude task.
    /// <summary>The words of the menu item, the line's button and the thread row's button (TaskService.Reevaluate).</summary>
    internal const string ReevaluateText = "Reevaluate";

    internal const string ReevaluateTip =
        "Ask Claude to check where this task really stands and fix it: settle a question already answered, mark finished work done, rewrite an old status line";

    // Whether a line and its thread offer Reevaluate as a button: a task in Waiting on you (a question or Done, the two
    // statuses that wait on Petre) that a Claude session has worked on. That is where a stale question or status costs
    // him: the task sits on top of his board asking for something already done. The menu offers it more widely (OpenMenuFor).
    bool Reevaluates(TaskRowView row) =>
        row.Claudes && row.Status.IsPinned() && !row.Closing && Actions?.Reevaluate is not null;

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
            // Segoe's "Refresh", U+E72C, in both icon fonts.
            IconButton(Reevaluates(row), "", ReevaluateText, ReevaluateTip, _ => Actions?.Reevaluate?.Invoke(row.Id)),
        };
        var strip = new FitStrip(ButtonColumn) { Tag = LineButtonsTag, VerticalAlignment = VerticalAlignment.Center };
        // An empty element holds a slot the action does not apply to, so the next button stays at its own x.
        slots.Select(button => (UIElement?)button ?? FitStrip.Gap()).ToList().ForEach(slot => strip.Children.Add(slot));
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
    // Open Claude (OpenClaudeButton). The "⋯" menu has the same pair, one or the other (task b6f068b1). It wears the
    // same mark as Open Claude, in the plain ink (ClaudeMark below).
    Button? StartClaudeButton(TaskRowView row) =>
        StartsClaude(row) && Actions?.StartClaude is { } start
            ? Small(ClaudeMark(hasSession: false),
                StartClaudeText, () => start(row.Id), "Start Claude on this: a background session with the task as its first message")
            : null;

    // ONE MARK FOR BOTH CLAUDE BUTTONS, TOLD APART BY COLOUR (task 5e02e99f; Petre, on the line's Open Claude icon, an
    // arrow leaving a box, and the play triangle a task without a session had in the same column: "this and another icon,
    // both opening claude", then "keep them on the same spot, give them claude icon and differentiate by color",
    // "whether it has a claude session or not"). The two were different pictures for what he reads as one thing, and the
    // arrow also looked like "Open in window" two slots along. So the column has one picture, a spark of twelve rays:
    //   * orange (ClaudeSessionInk) on a task that has a session: the button is Open Claude and goes to it;
    //   * the plain ink every other icon button has, on a task with none: the button is Start Claude on this.
    // The name and the tooltip still say which of the two it is. The spark is drawn here, a stroked path, because the
    // icon fonts have no such glyph; it is a spark of our own, not Anthropic's artwork.
    internal const string ClaudeMarkTag = "claude-mark";

    /// <summary>The mark's colour on a task that has a Claude session (Open Claude). Internal for the tests.</summary>
    internal static readonly Brush ClaudeSessionInk = Frozen(0xFF, 0xD9, 0x77, 0x57);

    /// <summary>The mark's colour on a task with no session yet (Start Claude on this): the other icon buttons' ink.</summary>
    internal static Brush ClaudeNoSessionInk => Ink;

    // Twelve rays about the centre of a 12 DIP box, every 30 degrees, long and short in turn and a little uneven, so it
    // reads as a hand-drawn spark and not as a gear or a loading wheel. Frozen: one geometry for every line.
    static readonly Geometry ClaudeSpark = Spark();

    static Geometry Spark()
    {
        double[] reach = [5.6, 4.3, 5.2, 4.6, 5.6, 4.2, 5.3, 4.5, 5.6, 4.3, 5.1, 4.6];
        const double centre = 6, hub = 1.5;
        var spark = new StreamGeometry();
        using (var draw = spark.Open())
            reach.Select((length, i) => (length, angle: (i * 30 - 90) * Math.PI / 180)).ToList().ForEach(ray =>
            {
                draw.BeginFigure(new Point(centre + hub * Math.Cos(ray.angle), centre + hub * Math.Sin(ray.angle)), isFilled: false, isClosed: false);
                draw.LineTo(new Point(centre + ray.length * Math.Cos(ray.angle), centre + ray.length * Math.Sin(ray.angle)), isStroked: true, isSmoothJoin: false);
            });
        spark.Freeze();
        return spark;
    }

    System.Windows.Shapes.Path ClaudeMark(bool hasSession) => new()
    {
        Tag = ClaudeMarkTag,
        Data = ClaudeSpark,
        Width = 12,
        Height = 12,
        Stroke = hasSession ? ClaudeSessionInk : ClaudeNoSessionInk,
        StrokeThickness = 1.3,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        SnapsToDevicePixels = false,
        VerticalAlignment = VerticalAlignment.Center,
    };

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
        var assignee = RowFor(id)?.Assignee;
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
            ReevaluateText => fitted > ReevaluateSlot && strip.Children[ReevaluateSlot] is Button,
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
    // A gap at the END of the ones that fit is not shown (task 3dd07539): the last child moves up to right after the last
    // real one. Gaps between real ones stay, so a button keeps its x on every line; only a trailing gap would push the
    // last child one slot further than on the line above for nothing (the review of task 3dd07539: Reevaluate, the
    // eighth slot, is a gap on nearly every line, and every full line had a blank slot before "⋯").
    int SlotsIn(double width)
    {
        var fit = double.IsInfinity(width) ? Children.Count - 1 : Math.Clamp((int)Math.Floor((width + 1) / slot) - 1, 0, Math.Max(0, Children.Count - 1));
        return Enumerable.Range(0, fit).LastOrDefault(i => !IsGap(Children[i]), -1) + 1;
    }

    /// <summary>An empty slot: holds the place of an action that does not apply to this line, so the next keeps its x.</summary>
    public static FrameworkElement Gap() => new() { Tag = GapTag };

    const string GapTag = "fit-strip-gap";

    static bool IsGap(UIElement child) => child is FrameworkElement { Tag: GapTag };

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
