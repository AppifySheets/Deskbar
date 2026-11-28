using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Reactive.Disposables;
using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// THE TASK LIST AT THE BOTTOM OF THE BAR. Design: docs/superpowers/specs/2026-11-22-tasks-board-design.md, sections
// "The bar", "Statuses" and "Calls made after the builder check" ("What a Claude line shows", "The bar's order").
// Mockups: .superpowers/brainstorm/876-1795339550/content/claude-marked.html (option A, chosen: one pile, Claude's
// lines marked) and tasklist-bottom.html (the add box). The model and its rules live in Deskbar.Core.Tasks; this file
// only draws TaskBoard.Pile() and hands Petre's clicks back to TaskService.
//
// WHAT IT SHOWS, top to bottom, inside the TaskListLine border at the top of the footer. There is no header: no
// "Tasks" label and no "+ Add" button (Petre, at the live check: "let there be a textbox without +add", then "don't
// say Tasks"; Controller Rulings 9 and 11).
//
//   a failed load   "Tasks could not be read. ..." first, the store's own reason in its tooltip and a ✕ that
//                   dismisses it (TaskService.DismissLoadProblem). An empty list must not pass for "nothing to do"
//                   when the truth is "your tasks could not be read".
//   the text box    always there, with a placeholder naming where a note would go ("Add to Deskbar…", or
//                   "Add…" on an unnamed desktop) that follows a desktop switch. A click in it takes the
//                   keyboard (BeginTyping). Under it, always, ONE chip choosing where the note goes
//                   (Ruling 45; the "on" before it went, task 47398c0f): the current workspace to begin with, or "none", with a small arrow; its press opens
//                   the compact workspace menu (none first) and a pick changes the chip. The row wraps onto a second
//                   line on a narrow bar rather than clip. A chip pressed while the box is idle
//                   starts typing as a click in the box does. Enter
//                   adds a Backlog note through TaskService.AddNote and Esc clears the box; either way the keyboard
//                   goes back to the window Petre was typing in. Clicking away leaves the text where it is.
//                   Shift+Enter starts a new line and long text wraps, the box growing up to TaskAddMaxLines; the
//                   first line is the title and the rest the description (TaskNote, task 98420dbc).
//   the lines       every line of the pile, in a viewer as tall as three lines on a floating bar and as many as fit
//                   on a docked one; the rest scroll under the wheel (THE LIST SCROLLS, below CapTaskLines) (Waiting
//                   on you, then Covey's quadrants I to IV, each in Petre's own order, dragged up and down; TaskBoard.Pile,
//                   THE FLAGS below, FloatingBar.TaskDrag.cs). A
//                   Claude line (a task with a session) has an orange edge and tint (the edge is the mark: Ruling 42,
//                   "don't say claude", took the CLAUDE tag off), the VS Code icon of its session's window when one is
//                   found, then the task's title in a tan ink and, after a dot,
//                   the question while it waits on Petre, else its status line, with "no live session" when every
//                   session has ended. No tick box (task acd76fbe): a line is closed from its right-click menu
//                   or in the tasks panel. Every line ends its first row with its workspace's chip in that row's own
//                   colour, or "none", and its second row runs on under the chip to the line's end (TaskLineFace).
//                   A line just closed stays in its place for six seconds, struck through, with "Undo" at its
//                   start (Petre: "when i click on done, there should be undo"; Ruling 10).
//   the flags       (Task 12, Rulings 25, 28 and 59.) Important and urgent name the task's Covey quadrant, and on the
//                   bar they show in the title alone: bold for important, italic for urgent. No sign and no toggle
//                   on the line (Petre, 2026-11-24: "important is bold; urgent is italic; no need for extra signs";
//                   the exclamation mark and the clock, and the quiet toggles over the title's end, were taken off).
//                   The line's right-click menu sets them (FloatingBar.TaskEdit.cs), as the tasks panel's toggles do,
//                   and the task goes to the top of the quadrant it lands in. The groups (the Waiting on you block, then each quadrant) are told apart by a
//                   little space above a group's first line, and nothing names them: the bar stays compact (the
//                   window's List view has the captions).
//   the group line  "+N more waiting on you": the Waiting on you block keeps three lines (one on a floating bar,
//                   whose view is three lines tall) and the rest fold into this
//                   one, so the important tasks stay in sight under it; a click opens the tasks panel (FoldWaiting,
//                   WaitingGroupButton; task 39f4cc8a).
//   "+N more"      a small line under the last line when more open tasks are out of sight, scrolled above the view or
//                   still below it, recounted as the list scrolls. When some of the hidden
//                   ones wait on Petre it says so in the asking orange ("+2 waiting"), so no ask is out of sight
//                   without a sign (spec, "The bar's order"). It toggles the docked tasks panel (ToggleTasksPanel,
//                   set by the App; TasksBoard.Toggle) and looks on while the panel is shown (TasksPanelShown), and
//                   with the panel there it is always shown, reading "All tasks" when nothing is hidden, since the
//                   panel is where closed tasks and comment threads are. Without a panel (the bar's tests) it expands
//                   the list in place and reads "Less" at the end of it. Under the lines rather than in a header, so
//                   it does not move under the pointer when the list opens.
//
// CLICKS. A line goes to its task through GoToTask, which the App points at the workspace switch and then the task's
// Claude session (ClaudeTaskActions.Go, Task 9), the same path the tasks window's lines take; a note
// on no workspace has nowhere to go, so its click does nothing. A single click opens the task in the tasks panel
// (OnTaskLineOpen). The menu's Close calls TaskService.Close at once (the store stays the truth; only Petre
// closes, and this is his click), and "Undo" calls TaskService.UndoClose, which brings the task back exactly as it
// was (Ruling 31).
//
// WHEN IT IS DRAWN. The lines are rebuilt when TaskService.Changed pulses and after every rebuild of the bar (the
// workspace names and row colours the lines borrow are refreshed by the rebuild), and only when what they would show
// has changed: the "shape" (ids, statuses, titles, details, the chips, the icons, the text size, folded or not)
// is compared with the last one drawn, so a pulse that changes nothing on screen touches nothing. Never during the
// bar's own rebuild (CLAUDE.md, "Re-entrancy in the bar's rebuild": COM calls pump messages, so a pulse can arrive
// in the middle of one); the rebuild paints the list once it has finished. And never while Rebuild itself would
// wait: a button down anywhere on the bar or a screen strip, a drag, an open menu (TaskListMustWait). A paint that
// changes the line count changes the bar's height, which on a bottom-anchored bar moves the rows under a press in
// flight (the bar's #48 lost click); the wait ends the way a rebuild's does, and that rebuild paints the list.
//
// WIDTH. The bar sizes itself to its content, so a long task title would widen the whole bar. The list therefore
// claims no width of its own (TakesNoWidth) and takes whatever the rows give it, trimming every line to fit.
//
// GATES. Shown only with a task service, while TasksGate allows tasks (always, for now) and while the "Task list"
// strip item (StripItem.Tasks, Manage's Settings tab) is switched on.
public partial class FloatingBar
{
    // The task service, handed over by App, the same instance the control pipe edits, so the two always agree. Left
    // unset, as every bar test but the task list's leaves it, the list stays collapsed.
    internal TaskService? Tasks
    {
        get => taskService;
        init
        {
            taskChanges?.Dispose();
            taskService = value;
            taskChanges = value?.Changed.Subscribe(_ => PaintTaskList());
            lastTaskListShape = null;
            PaintTaskList();
        }
    }

    TaskService? taskService;
    IDisposable? taskChanges;

    // THE TASK'S GOAL (goals plan, Task 7; docs/superpowers/specs/2026-11-22-goals-planner-design.md, "Linking a task to
    // a goal elsewhere"): the bar gains NO chip and no control for goals (the anti-goal "a busier bar"); a line's tooltip
    // names its goal as its last line, "Goal: <title>". App answers the title through GoalService.GoalOf (none for no
    // link and for a link to a goal that no longer exists) and hands GoalService.Changed in, so a goal renamed from the
    // pipe repaints the lines. The title is part of a line's shape (a repaint) but not of the places key, so a rename
    // moves no line and does not restart the click guard (TaskClickIgnored). Left unset, no line names a goal.
    internal Func<TaskItem, Maybe<string>> GoalTitle { get; init; } = _ => Maybe<string>.None;

    internal IObservable<System.Reactive.Unit>? GoalChanges
    {
        init
        {
            goalChanges?.Dispose();
            goalChanges = value?.Subscribe(_ => PaintTaskList());
            // GoalTitle may have been set after Tasks in the same initializer, which painted without it.
            PaintTaskList();
        }
    }

    IDisposable? goalChanges;

    // What a double-click on a line does with the task's id. App sets it to GoToTask (its workspace, then its Claude
    // session's window); a refusal is shown.
    internal Func<Guid, Result> GoToTask { get; init; } = _ => Result.Success();

    // THE SELECTED TASK (Petre: "highlight selected task"; ruling 21): the one his last click on a line picked, here or
    // in the tasks window, which the App hands the same object (TaskSelection). Its line has a lighter fill than hover
    // and a 2 DIP edge on its left in the blue the text box uses, so it reads as chosen, not hovered. Every line carries
    // that edge, clear unless selected, so selecting changes neither a line's height nor the bar's width; and a change
    // of selection repaints the highlight in place (PaintTaskSelection), not the lines, so nothing moves and the click
    // guard's half second does not start again.
    internal TaskSelection Selection
    {
        get => selection;
        init
        {
            selectionChanges?.Dispose();
            selection = value;
            selectionChanges = value.Changed.Subscribe(_ => PaintTaskSelection());
        }
    }

    TaskSelection selection = new();
    IDisposable? selectionChanges;

    // The selected line's fill (lighter than the hover's #1FFFFFFF) and its edge (TaskPickedEdge, the text box's blue).
    static readonly Brush TaskSelectedFill = Frozen(0x38, 0xFF, 0xFF, 0xFF);

    // The highlight as the selection stands, on the lines drawn, in place; then the selected line scrolled into sight
    // when the list had it out of view (BringSelectedIntoSight).
    void PaintTaskSelection()
    {
        // A task selected from elsewhere (its Claude terminal brought to the front, #477) whose line is folded into the
        // Waiting on you group line has no line to light: the paint draws it in its own place (FoldWaiting's `keep`),
        // and the highlight below then finds it.
        // And the other way round (review finding 2): a line drawn only because it was selected folds as soon as the
        // selection leaves it, in answer to that click, not at some later paint that would move the lines on its own.
        if ((selection.Current is { HasValue: true } picked && waitingFold.Folded.Any(l => l.Id == picked.Value)) || waitingFold.KeptSelected)
            PaintTaskList();
        (taskLinesPanel?.Children.OfType<Button>() ?? []).Where(b => b.Tag is TaskLine).ToList().ForEach(button =>
        {
            if (button.Content is not Border frame) return;
            var selected = selection.IsSelected(((TaskLine)button.Tag).Id);
            frame.BorderBrush = selected ? TaskPickedEdge : Brushes.Transparent;
            frame.Background = selected ? SelectedLineFill : Brushes.Transparent; // the look on show (FloatingBar.InboxLine.cs)
        });
        BringSelectedIntoSight();
    }

    // For the tests: whether a line on show is drawn as selected.
    internal bool TaskLineSelected(Guid id) =>
        (taskLinesPanel?.Children.OfType<Button>() ?? []).Any(b => b.Tag is TaskLine line && line.Id == id
            && b.Content is Border { BorderBrush: SolidColorBrush { Color.A: > 0 } });

    // What "+N more" / "All tasks" does: toggles the docked tasks panel (TasksBoard.Toggle, set by the App; plan
    // 2026-11-23-tasks-panel, Task 6: it used to open the floating tasks window). While it is set, the button is always
    // there, reading "All tasks" when nothing is out of sight, because the panel is the one place the closed tasks can be
    // seen (Petre: "can i see task list in deskbar? all of them, new and old"). Left null (the bar's own tests, or a copy
    // with no panel) the button expands the list in place, as before the window existed.
    internal Action? ToggleTasksPanel { get; init; }

    // What the Manager button does: opens today's manager session (ManagerActions.Open, wired by the App, which also says
    // any failure to the user). The manager is the one Claude session that coordinates the task board (spec
    // 2026-11-25-manager-session-design). Left null (the bar's own tests, or a copy where it is not wired) the button is
    // not shown at all, so there is never a button that does nothing.
    internal Action? OpenManager { get; init; }

    // Whether the tasks panel is shown, from the App (TasksPanel.Shown, relayed because the panel is built after the
    // bar). While it says true the "All tasks" button looks on (PaintTaskMoreLook), so the line that opened the panel
    // reads as the one that closes it (spec "Opening and closing": its "on" look is the builder's call; the mockup
    // shows it highlighted). Subscribed here, in the init: the bar lives as long as the app, and the stream completes
    // when the panel shuts down.
    internal IObservable<bool>? TasksPanelShown
    {
        get => tasksPanelShown;
        init
        {
            tasksPanelShown = value;
            value?.Subscribe(on =>
            {
                tasksPanelOn = on;
                PaintTaskMoreLook();
            });
        }
    }

    IObservable<bool>? tasksPanelShown;
    bool tasksPanelOn;

    // Whether this copy may use tasks: Deskbar Pro, or any copy while tasks are free (TasksGate.Free, so always, for
    // now). The same seam the stopwatch had, for the tests, which cannot fake a Store licence.
    internal Func<bool> TasksAllowed { get; init; } = () => TasksGate.Allowed(Entitlement.Current.IsPro);

    // The VS Code window a Claude session works in, from the session's folder: the App answers with the rule
    // RemoteControl.CallerOf uses (EditorFolder.Best over the editor windows' titles). A seam so the tests need no
    // real windows. No window, no icon.
    internal Func<string, Maybe<nint>> SessionWindow { get; init; } = _ => Maybe<nint>.None;

    // Whether a window still exists, before the keyboard is handed back to it (GiveTaskKeyboardBack). A seam for the
    // tests, whose windows are made-up handles; HandForegroundTo and ForegroundWindow, its two neighbours, are the
    // language switcher's seams (FloatingBar.Language.cs).
    internal Func<nint, bool> WindowAlive { get; init; } = Windows.Monitoring.NativeMethods.IsWindow;

    // Whether a window is on the virtual desktop in front (a window pinned to every desktop is). The App answers from
    // the desktop service; left unset the answer is no, so a bar without one never hands the keyboard to a window that
    // might sit on another desktop, which would switch desktops under Petre (GiveTaskKeyboardBack).
    internal Func<nint, bool> OnCurrentDesktop { get; init; } = _ => false;

    // The clock the click guard reads (TaskClickIgnored). A seam so the tests can put a click right after a repaint,
    // or half a second later, without waiting.
    internal Func<DateTime> TaskClickClock { get; init; } = () => DateTime.UtcNow;

    // NO AGE ON THE BAR'S LINES (reverses Ruling 46; Petre, 2026-11-24, of the bar's "26m": "move the
    // time from here to the proper tasks window"). The age lives in the tasks panel now, on its slim lines too
    // (TasksWindow.Line, column 6), and the bar's line keeps the created date in its tooltip (TipOf). TaskNow stays: it
    // is the clock the bar's pasted pictures are named against (FloatingBar.TaskPictures.cs), and a seam for the tests.
    internal Func<DateTimeOffset> TaskNow { get; init; } = () => DateTimeOffset.UtcNow;

    // How a closed line's six seconds are timed: run `then` once after `after`, unless the returned handle is disposed
    // first (Undo, or the bar closing). A dispatcher timer in the app, so `then` runs on the UI thread; a seam so the
    // tests move time by hand instead of waiting.
    internal Func<TimeSpan, Action, IDisposable> TaskUndoTimer { get; init; } = DispatcherUndoTimer;

    // Whether a paint must wait: the conditions Rebuild waits under, all of them (FloatingBar.xaml.cs, Rebuild). A
    // button down anywhere on the bar or on a screen strip, not only on the list: a paint that adds or drops a line
    // changes the bar's height, a bar anchored at the bottom then moves its Top, and the rows shift under a press in
    // flight, so the release lands on another element (#48's lost click). And while a row or a window is being
    // dragged, or an arrange drop is queued, or one of the bar's menus is open. Each of those ends by serving
    // rebuildRequested (the release, the drop, the cancel, the drag's end, the menu's close), and that rebuild ends
    // by painting the list.
    bool TaskListMustWait =>
        (Mouse.LeftButton == MouseButtonState.Pressed && (IsMouseOver || PointerOnStrip()))
        || arrangeDrag is not null || arrangeDropQueued || draggingWindow || openMenu?.IsOpen == true
        // A task line armed, dragged, or its drop still queued (FloatingBar.TaskDrag.cs).
        || TaskDragInFlight
        // A line's title being edited in place (FloatingBar.TaskEdit.cs): no paint takes the text from under the keyboard.
        || editingTask is not null;

    // For the tests: whether the list is on screen, what "+N more" says ("" while it is hidden), whether the box has
    // the keyboard, where a note added now would go, what the empty box says, and the lines on show.
    internal bool TaskListShown => TaskListLine.Visibility == Visibility.Visible;
    internal string TaskListMoreText => taskMoreButton is { Visibility: Visibility.Visible } ? taskMoreFull : "";
    internal string TaskListMoreShown => taskMoreButton is { Visibility: Visibility.Visible } ? taskMoreLabel?.Text ?? "" : "";
    // Whether "All tasks" looks on, as the tasks panel is shown (TasksPanelShown).
    internal bool TaskListMoreOn => taskMoreButton is not null && AutomationProperties.GetItemStatus(taskMoreButton) == "on";
    internal bool TaskListAdding => taskTyping;
    internal Maybe<Guid> TaskAddTarget => taskAddTarget;
    internal string TaskAddPlaceholder => taskAddPlaceholder?.Text ?? "";
    internal IReadOnlyList<TaskLine> TaskListLines => shownTaskLines;
    // The lines in sight in the viewer right now, the rest being scrolled above or below it (THE LIST SCROLLS).
    internal IReadOnlyList<TaskLine> TaskLinesInSight => LinesInSight();
    // The viewer, for the tests that scroll it.
    internal ScrollViewer? TaskLinesScroller => taskLinesScroller;

    // Three lines, the number the spec gives ("the three most recent tasks"): always on a floating bar, and the least
    // on a docked one.
    const int TaskListSlots = 3;

    // THE DOCKED BAR'S LIST FILLS THE FREE HEIGHT (Petre: "tasks list can grow all the way up to the available vertical
    // space when docked", then, of his docked bar with the whole screen empty above three lines and "+3 more": "use
    // entire height when available for tasks"; ruling 23). When the bar's height is not its content's (docked to an
    // edge, or across the seam: SizeToContent is Manual), the list shows as many lines as fit in the height the rows
    // leave free, at least three; the rest stay behind "+N more". A floating bar keeps three.
    //
    // The rows keep priority, and nothing measures the list against itself (a layout loop). A pass after layout
    // (FitTaskListToFreeHeight, deferred to the dispatcher's Loaded priority) works out the ROOM the lines have: the
    // height the window gives the content, less what the rows need and what the footer needs apart from the lines
    // (LineRoom). That room does not depend on how many lines are on show, so the count is a function of it and of the
    // lines' own heights: as many lines, from the top of the pile, as their heights fit in the room, at least three.
    //
    // THE LINES' HEIGHTS ARE EACH LINE'S OWN (review C-1 of 187cf4a). The first version took one line's height from the
    // first line and multiplied: with notes on top (17 DIP at text offset -2) and Claude lines under them carrying a
    // session window icon (20 DIP), the count flipped 7, 6, 7, 6 for ever, and every flip queued another pass above
    // Input priority, so the UI thread froze (measured: 340 layouts in 1.5 s, no input handled). Now every line drawn
    // has its measured height remembered by what it shows (taskLineHeights); a line not drawn yet counts as the tallest
    // remembered one, so the list never grows into an overflow on a guess. A pass can only teach the memory heights it
    // did not have, so after at most one more pass the answer stops changing: the pass settles.
    //
    // A pass that would change nothing changes nothing; one that waits for a press, a drag or a menu (TaskListMustWait)
    // is left to the paint that follows.
    int taskListSlots = TaskListSlots;
    bool taskFitQueued;

    // Each line's height as last drawn, with its top and bottom margins, by HeightKeyOf; only the lines the list can
    // show are kept.
    readonly Dictionary<string, double> taskLineHeights = [];

    // Every line PaintTaskList would draw, in order (the open lines with the closed ones still in their undo window).
    IReadOnlyList<TaskLine> allTaskLines = [];

    internal int TaskListSlotCount => taskListSlots;

    // How many lines the list shows: the docked count while the bar's height is set by hand, three while it follows its
    // content. Read from SizeToContent rather than trusted to taskListSlots, so the first layout after an undock already
    // draws three (review I-1).
    int TaskSlotsNow => SizeToContent == SizeToContent.Manual ? taskListSlots : TaskListSlots;

    // Queues the pass after the layout now under way.
    void QueueTaskListFit()
    {
        if (taskFitQueued) return;
        taskFitQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            taskFitQueued = false;
            FitTaskListToFreeHeight();
        });
    }

    /// <summary>The pass itself (see above), internal so the tests can run it after their own layout.</summary>
    internal void FitTaskListToFreeHeight()
    {
        var slots = SizeToContent == SizeToContent.Manual ? LinesThatFit().GetValueOrDefault(taskListSlots) : TaskListSlots;
        if (slots == taskListSlots || TaskListMustWait) return;
        taskListSlots = slots;
        PaintTaskList();
    }

    // How many lines fit in the room (see above), or none when nothing can be measured yet (no layout, no line drawn):
    // the count then stays as it is. While the list is expanded in place every line is on show and the count is moot.
    Maybe<int> LinesThatFit()
    {
        if (taskListExpanded || LineRoom() is not { } room) return Maybe<int>.None;
        RememberLineHeights();
        var known = allTaskLines.Select(line => taskLineHeights.TryGetValue(HeightKeyOf(line), out var h) ? h : (double?)null).ToList();
        // The Waiting on you group line (FoldWaiting) takes a place among the lines, at its own height once laid out.
        if (waitingFold.Any) known.Insert(Math.Min(waitingFold.At, known.Count), waitingGroupHeight);
        var tallest = known.OfType<double>().DefaultIfEmpty(0).Max();
        if (tallest <= 0) return Maybe<int>.None;
        // Each line's height, a line never drawn counting as the tallest; then how many of the running totals fit. A
        // hair of tolerance, so layout rounding (a line at 17.0000001) cannot drop a line that fits exactly.
        var totals = known.Select(h => h ?? tallest).Aggregate(new List<double>(), (sums, h) =>
        {
            sums.Add(sums.LastOrDefault() + h);
            return sums;
        });
        return Math.Max(TaskListSlots, totals.Count(total => total <= room + 0.01));
    }

    // The room the lines have: the height the window gives the content, less what the rows need and what everything
    // else in the content needs, the footer included, plus what the lines on show take of the footer now (so the room
    // is the same whatever their count). Read from the window, not from the rows' area: once the content is taller
    // than the window, WPF arranges the area at its own desired height and clips it (measured: four rows added and the
    // area still read "free 0"), so its ActualHeight never shows the overflow. Everything here is in content units
    // (the bar scales its content).
    double? LineRoom()
    {
        if (BarContent.Children.OfType<FrameworkElement>().LastOrDefault() is not Panel rowsArea || taskLinesPanel is null) return null;
        if (PresentationSource.FromVisual(BarContent) is null) return null;
        var toBar = BarContent.TransformToAncestor(this);
        var top = toBar.Transform(new Point(0, 0)).Y;
        var unit = toBar.Transform(new Point(0, 1)).Y - top;
        if (unit <= 0) return null;
        // The content's height: the window's below BarContent's top, less the root's padding at the foot.
        var available = (ActualHeight - top) / unit - RootPadding - BarContent.Margin.Bottom;
        var others = BarContent.Children.OfType<UIElement>().Where(child => child != rowsArea).Sum(child => child.DesiredSize.Height);
        var rowsNeed = rowsArea.Children.OfType<UIElement>().Sum(child => child.DesiredSize.Height) + rowsArea.Margin.Top + rowsArea.Margin.Bottom;
        // What the lines take of the footer is the viewer's height, not the lines' own: every line is drawn now and the
        // viewer shows only the ones that fit (THE LIST SCROLLS), so the lines' sum would count the scrolled ones too.
        var linesNow = taskLinesScroller?.DesiredSize.Height ?? 0;
        return available - others - rowsNeed + linesNow;
    }

    // Remembers the height of every line on show, and forgets the lines the list no longer has.
    void RememberLineHeights()
    {
        (taskLinesPanel?.Children.OfType<Button>() ?? []).Where(b => b.Tag is TaskLine && b.ActualHeight > 0).ToList()
            .ForEach(b => taskLineHeights[HeightKeyOf((TaskLine)b.Tag)] = b.ActualHeight + b.Margin.Top + b.Margin.Bottom);
        var current = allTaskLines.Select(HeightKeyOf).ToHashSet();
        taskLineHeights.Keys.Where(key => !current.Contains(key)).ToList().ForEach(key => taskLineHeights.Remove(key));
        // The group line's height (one line of text, so its words never change it; the text size does, and the next
        // layout reads it again).
        (taskLinesPanel?.Children.OfType<Button>() ?? []).Where(b => Equals(b.Tag, WaitingGroupTag) && b.ActualHeight > 0).Take(1).ToList()
            .ForEach(b => waitingGroupHeight = b.ActualHeight + b.Margin.Top + b.Margin.Bottom);
    }

    // The Waiting on you group line's height as last laid out, or none before it ever was (LinesThatFit then counts it
    // as the tallest line).
    double? waitingGroupHeight;

    // What a line's height follows: everything it shows, and the text size.
    // Since Ruling 41 a line's text can wrap to two lines, so the width it is laid out at is part of the key as well:
    // the same line is one line on a wide list and two on a narrow one.
    string HeightKeyOf(TaskLine line) => $"{TextOffset}|{Math.Round(taskLinesPanel?.ActualWidth ?? 0)}|{ShapeOf(line)}{InboxBarHeightKey(line)}";

    // Expanded in place, at most this many lines show and the rest scroll (PR A review, Important 3): dozens of
    // migrated notes must not push the workspace rows off a docked bar.
    const int ExpandedSlots = 8;

    // A click this soon after a repaint that changed the lines is not trusted (TaskClickIgnored).
    static readonly TimeSpan RepaintGrace = TimeSpan.FromMilliseconds(500);

    // How long a closed line stays, struck through with its "Undo" (Ruling 10).
    internal static readonly TimeSpan UndoWindow = TimeSpan.FromSeconds(6);
    const string NoLiveSession = "no live session";
    const string TaskDetailSeparator = " · ";

    // The colours, from the chosen mockup and the bar's own palette: the asking orange for anything Claude's or
    // waiting on Petre, the mockup's light and dim inks, its tan for a Claude task's title, and quick find's blue
    // for the field and the chip that is ticked. All frozen (CLAUDE.md: static WPF brushes must be).
    static readonly Brush TaskOrange = Frozen(0xFF, 0xFF, 0xA7, 0x26);
    static readonly Brush TaskClaudeTint = Frozen(0x26, 0xFF, 0xA7, 0x26);
    static readonly Brush TaskInk = Frozen(0xFF, 0xE6, 0xE6, 0xE6);
    static readonly Brush TaskDimInk = Frozen(0xFF, 0xA9, 0xAD, 0xB8);
    // The subtask arrow and a parent's count (Petre's pick A): fainter than the dim ink, so the title and the status line
    // stay the news on a line. The tasks panel's caption grey.
    static readonly Brush TaskFaintInk = Frozen(0xFF, 0x8B, 0x8F, 0x99);
    static readonly Brush TaskTitleInk = Frozen(0xFF, 0xD9, 0xB9, 0x8A);
    static readonly Brush TaskBadgeInk = Frozen(0xFF, 0x1E, 0x1F, 0x24);
    static readonly Brush TaskButtonFill = Frozen(0xFF, 0x2A, 0x2C, 0x33);
    static readonly Brush TaskTickEdge = Frozen(0xFF, 0x77, 0x77, 0x77);
    static readonly Brush TaskNoneFill = Frozen(0xFF, 0x33, 0x33, 0x33);
    static readonly Brush TaskNoneInk = Frozen(0xFF, 0x99, 0x99, 0x99);
    static readonly Brush TaskFieldFill = Frozen(0xFF, 0x15, 0x16, 0x1A);
    static readonly Brush TaskPickedEdge = Frozen(0xFF, 0x4F, 0x8C, 0xFF);

    // The flags' colours (Task 12): important a soft red, urgent an amber, both clear of the asking orange that means
    // Claude's or waiting. Only the add box's two toggles wear them now (AddFlagToggle): a line shows its flags in its
    // title's weight and slant.
    static readonly Brush TaskImportantInk = Frozen(0xFF, 0xFF, 0x6B, 0x6B);
    static readonly Brush TaskUrgentInk = Frozen(0xFF, 0xFF, 0xD5, 0x4F);

    // The persistent parts, built once in BuildTaskList.
    Border? taskProblemHost;
    TextBlock? taskMoreLabel;
    Button? taskMoreButton;
    Button? taskManagerButton;
    TextBox? taskAddBox;
    // The add box's "/" list of Claude Code skills (task 3a872301, SkillCompletion).
    SkillCompletion? taskAddSkills;
    TextBlock? taskAddPlaceholder;
    WrapPanel? taskAddChips;
    TaskChipParts? taskAddChip;
    StackPanel? taskLinesPanel;
    ScrollViewer? taskLinesScroller;

    // The list's state: folded to three or expanded, what was drawn last, the lines on show, and the add box's choice.
    bool taskListExpanded;
    string? lastTaskListShape;
    IReadOnlyList<TaskLine> shownTaskLines = [];
    Maybe<Guid> taskAddHere = Maybe<Guid>.None;
    Maybe<Guid> taskAddTarget = Maybe<Guid>.None;

    // WHETHER THE TARGET WAS CHOSEN BY HAND (task 701c71a8). Set when Petre picks a workspace (or "none") from the chip's
    // menu (ChooseTaskTarget); cleared when the box empties after Enter or Esc (EndTyping). While it is unset the target
    // is only the default, the workspace in front, and it keeps following that workspace even with text in the box: he
    // clicked into the box on Rustdesk, pasted a note, clicked contabo on the bar and pressed Enter there, and the note
    // (and the Claude session started on it) went to Rustdesk, because a box holding text used to keep whatever
    // workspace it opened on. A choice made by hand still stays put across a desktop switch, as Ruling 9 intends.
    bool taskAddTargetPicked;

    // Whether the box has the keyboard: from a click in it (BeginTyping) until Enter, Esc or the bar losing the
    // foreground (EndTyping). The bar stays at full strength while it is set, not merely while the box holds text. And
    // the window to hand the keyboard back to after Enter or Esc.
    bool taskTyping;
    nint taskReturnTo;

    // The lines just closed, each staying in its place for the undo window: the line as it was drawn (marked Closing),
    // its index among the lines on show when it was closed, its status before (what Undo restores), and its timer.
    readonly List<PendingClose> pendingCloses = [];

    // Undo brings back exactly what the close took (Ruling 31): the status (Was), the question of a task that waited on
    // Petre, and the selection when the line was the one selected.
    sealed record PendingClose(TaskLine Line, int Index, TaskStatus Was, IDisposable Timer)
    {
        public Maybe<string> Question { get; init; } = Maybe<string>.None;

        public bool Selected { get; init; }
    }

    // The click guard's two facts (TaskClickIgnored): the click count of the last press on one of the list's guarded
    // buttons, and when the lines were last redrawn.
    int taskPressClicks;

    // The task the last single click on a line landed on, for the double-click guard (TaskLineButton).
    Guid? lastClickedTask;
    string taskLinePlaces = "";
    DateTime taskLinesChangedAt = DateTime.MinValue;

    // A workspace chip's fill: its row's rail colour, faded so the name stays readable on it. One brush per colour,
    // per bar, frozen.
    readonly Dictionary<Color, Brush> taskChipFills = [];

    sealed record TaskChipParts(Button Button, Border Face, TextBlock Label, TextBlock Arrow);

    // --- the lines, as data -------------------------------------------------------------------------------------

    // RULING 48 (Petre: "when i have responded to a claude session that wanted my attention, i think it's okay if it gets
    // removed from the tasklist"; and, precisely: "not when i have activated the window, but when i have responded to it").
    // The BAR lists a Claude task only while it needs him: Waiting on you, or Done (waiting for him to close it). A Claude
    // task in progress or in the backlog leaves the bar's list and stays in the tasks window, which shows everything.
    // What takes a waiting task off is the session settling its question with `task answered` after his reply (#435; a
    // prompt alone no longer does, TaskBoard.Answered,
    // which moves Waiting on you back to In progress); arriving at the workspace, activating the session's window or
    // clicking the line never changes a status, so none of them takes it off. When the session asks again the task is
    // Waiting on you again and comes back at the top of the pile. His own notes are unaffected. "+N more" and the docked
    // fit pass count only what the bar lists, since both read this list.
    //
    // RULING 57 REPLACES THE STATUS TEST with the assignee: the bar lists the tasks assigned to Petre. The rules that move
    // the assignee (TaskBoard) make a Claude task his exactly while it is waiting on him or done, so for Claude's tasks this
    // is the same list as before; and a note he handed to Claude in the tasks window leaves the bar, which the status
    // test could not say. Assigning is only ever done in the tasks window, never from the bar.
    internal static bool NeedsYou(TaskLine line) => line.Assignee.IsMe;

    /// <summary>
    /// Every open task as the bar draws it, in the pile's order (TaskBoard.Pile). <paramref name="workspaceName"/> and
    /// <paramref name="colour"/> answer for the workspace chip; <paramref name="sessionWindow"/> finds a Claude task's
    /// VS Code window from its session's folder. <paramref name="delivery"/> says whether a Done task's change runs here yet
    /// (TaskService.DeliveryOf; task 8470d176), none meaning the full tick. Pure apart from those, so the rules are read in
    /// one place.
    /// </summary>
    internal static IReadOnlyList<TaskLine> Lines(TaskBoard board, Func<Guid, Maybe<string>> workspaceName,
        Func<Guid, Brush?> colour, Func<string, Maybe<nint>> sessionWindow, Func<TaskItem, Maybe<string>>? goalTitle = null,
        Func<TaskItem, TaskDelivery>? delivery = null, Func<TaskItem, bool>? opensSession = null) =>
        // A snoozed task is off the bar until it comes back (task 6da8d7eb, TaskItem.SnoozedUntil); the tasks panel's
        // "Snoozed" filter is where it can be found meanwhile.
        // The Done tasks show the full discs first, then the hollow rings (task a1debf34, TaskBoard.Pinned).
        board.Pile(delivery is null ? null : t => delivery(t).Shipping).Where(t => !t.IsSnoozed).Select(t => new TaskLine(
            t.Id,
            t.Title,
            DetailOf(t, withDescription: true, plain: true),
            t.Status,
            t.IsClaudes,
            t.WorkspaceId.Bind(workspaceName).Match(name => (string?)name, () => null),
            t.WorkspaceId.HasValue ? colour(t.WorkspaceId.Value) : null,
            t.IsClaudes ? TaskViews.SessionToGoTo(t).Bind(s => sessionWindow(s.Folder)) : Maybe<nint>.None)
        {
            Created = t.Created,
            Assignee = t.Assignee,
            Important = t.Important,
            Urgent = t.Urgent,
            // The goal it counts toward, for the tooltip only (see GoalTitle).
            GoalTitle = (goalTitle?.Invoke(t) ?? Maybe<string>.None).Match(title => (string?)title, () => null),
            // Subtasks (Petre's pick A): the parent's title for the arrow's tooltip, and a parent's open subtasks' count.
            ParentTitle = board.ParentOf(t).Map(p => (string?)FirstLine(p.Title)).GetValueOrDefault(),
            ParentId = board.ParentOf(t).Map(p => (Guid?)p.Id).GetValueOrDefault(),
            SubtaskCount = board.OpenSubtaskCount(t.Id),
            // Whether a Done task's change runs in this Deskbar yet: the ring, or the disc (task 8470d176).
            Delivery = delivery?.Invoke(t) ?? TaskDelivery.None,
            // Whether the line menu's Open Claude can open anything (task 6a57f59b, ClaudeTaskActions.CanOpen). Without
            // the App's answer (the tests) a Claude task's session counts as openable, as before.
            OpensSession = t.IsClaudes && (opensSession?.Invoke(t) ?? true),
            // The repeat mark's facts (task 0b78269e, Task 9): the schedule in words, when it next runs and its model.
            RepeatLabel = t.Repeat.Map(r => r.Schedule.Label).GetValueOrDefault(),
            NextRun = t.NextRun.Map(next => (DateTimeOffset?)next).GetValueOrDefault(),
            RepeatModel = t.Repeat.Map(r => r.Model).GetValueOrDefault(),
        }).ToArray();

    // SUBTASKS UNDER THEIR PARENT (follow-up to the subtasks spec; Petre's screenshot of "↳ test1" four lines above its
    // parent). After the bar's own filter, so a parent that is not on the bar (Claude's) leaves its subtasks where they
    // were. A moved line is drawn in its parent's group (ShownIn), so an important subtask under a plain parent starts no
    // group of its own, and a plain subtask of a pinned task (waiting on Petre or Done) is drawn under it in the pinned
    // block (task f41d7857). A subtask that is itself pinned stays in the pinned block unless its parent is there too:
    // the panel's rule, TaskViews.NestsUnder, which carries the reasons.
    internal static IReadOnlyList<TaskLine> UnderParents(IReadOnlyList<TaskLine> lines)
    {
        Maybe<Guid> ParentOf(TaskLine l) => l.ParentId is { } p ? p : Maybe<Guid>.None;
        // GroupBy: a duplicate id from a hand-edited file must not throw here (review of #467, finding 5).
        var groupOf = lines.GroupBy(l => l.Id).ToDictionary(g => g.Key, g => g.First().GroupKey);
        // A pinned subtask (waiting on Petre or Done) moves only under a parent that is pinned too; any other subtask
        // moves under its parent, into the pinned block when the parent is there (task f41d7857; TaskViews.NestsUnder,
        // the panel's rule; the bar has no Closed group to keep apart: a line closing in its undo window keeps its group).
        bool Movable(TaskLine l) =>
            !l.Status.IsPinned() || (l.ParentId is { } p && groupOf.TryGetValue(p, out var g) && g == "waiting");
        var nested = SubtaskOrder.Nested(lines, l => l.Id, ParentOf, Movable);
        return SubtaskOrder.UnderParents(lines, l => l.Id, ParentOf, Movable)
            .Select(l => nested.Contains(l.Id) ? l with { ShownIn = groupOf[l.ParentId!.Value] } : l)
            .ToArray();
    }

    // Whether subtasks are drawn under their parent: the tasks panel's "Subtasks" checkbox (AppState.TasksPanel.HideSubtasks,
    // task 173ef268). An absent panel state is the default, subtasks shown.
    bool Subtasks => !(manager.State.TasksPanel?.HideSubtasks ?? false);

    // THE "SUBTASKS" CHECKBOX UNTICKED (task 173ef268): every line UnderParents drew under its parent (ShownIn set) goes,
    // and the parent's "· N subtasks" says they are there. The panel's rule, TaskViews.FoldSubtasks: a subtask whose
    // parent is not on the bar (a Claude task the bar leaves out) was never nested, so it stays, and a subtask waiting on
    // Petre stays, because a question must stay findable. Ticked, the lines are as they were.
    // TASK 4c01747c overrules the first half of that for the bar: with the box unticked Petre expects no subtask lines on
    // the bar at all. His screenshots: eight subtasks of a SNOOZED parent, then "↳ test" under a parent assigned to
    // Claude, both on the bar because their parent never is. So every subtask with an open parent (`underOpenParent`,
    // TaskViews.UnderOpenParent) folds here too, nested or not, except a pinned one (a question, or Done work waiting to
    // be closed), which UnderParents would have left in the pinned lines under a parent that is not. A subtask whose
    // parent is closed (or gone) keeps its line: nothing would show it otherwise. The tasks panel lists every parent.
    // The bar's selected task (`keep`) never folds, the panel's rule from task b0526e18: a subtask added from the bar
    // under a parent the bar does not list is selected and glows (ShowAddedTask, task ee75f046), and folded at once it
    // left no line and no count going up, so the add looked like it did nothing. It folds once another task is picked.
    internal static IReadOnlyList<TaskLine> FoldSubtasks(IReadOnlyList<TaskLine> lines, bool shown, IReadOnlySet<Guid>? underOpenParent = null,
        Maybe<Guid> keep = default) =>
        shown ? lines
        : lines.Where(l => (l.ShownIn is null && !((underOpenParent?.Contains(l.Id) ?? false) && !l.Status.IsPinned()))
            || l.Status == TaskStatus.WaitingOnYou || (keep.HasValue && keep.Value == l.Id)).ToArray();

    // Marks the first line of each group after the first (TaskLine.StartsGroup), over the lines as they are drawn, the
    // closed ones in their undo window included, so the space sits where the eye sees a group begin.
    internal static IReadOnlyList<TaskLine> WithGroupStarts(IReadOnlyList<TaskLine> lines) =>
        lines.Select((line, i) => line with { StartsGroup = i > 0 && lines[i - 1].GroupKey != line.GroupKey }).ToArray();

    // THE WAITING BLOCK FOLDS AFTER THREE LINES (task 39f4cc8a; Petre, with a screenshot of a docked bar whose Waiting on
    // you block of questions and Done tasks filled the whole list: "group tasks waiting on me in the main bar after 3;
    // clicking the group or the button should open the tasks bar", and "so that i can see what tasks i have that are
    // important"). The first WaitingShown lines of the pinned block (TaskLine.GroupKey "waiting": the questions, then
    // the Done tasks, with the subtasks drawn under them) keep their lines; the rest become ONE line, "+N more waiting
    // on you" (WaitingGroupButton), drawn where the block ends, so quadrant I starts right under it and the important
    // tasks stay in sight. The group line opens the tasks panel, where the whole block is (OpenTasksFromGroup).
    //
    // What never folds, and why:
    //   - a line in its undo window (Closing): it must stay where it was for its six seconds, or the lines would move
    //     under the pointer (Ruling 10). A closed line among the first three still takes one of the three places, so
    //     nothing slides up until its undo window ends.
    //   - the selected task (`keep`), the rule FoldSubtasks has for the same reason (task b0526e18): a Claude terminal
    //     brought to the front selects its task (#477), and a folded line could not show that selection. It is drawn
    //     in its own place in the block, above the group line.
    // A group line that would stand for a single task saves no room and hides that task's title, so the block folds
    // only when at least two lines would go (FoldsFrom). The lines are counted as drawn, so a subtask drawn under a
    // waiting parent takes a place like any line.
    //
    // ON A FLOATING BAR ONE LINE, NOT THREE. Its viewer is three lines tall (TaskListSlots), so three waiting lines
    // would fill it and push both the group line and quadrant I below the view, where the fold changes nothing Petre
    // sees (review of this change, finding 1). There the block keeps one line, so the view reads: the newest
    // question, the group line, the first important task. A docked bar keeps three, as asked; its slot count comes
    // from the free height, and the kept count does not follow it, since the fit pass counts the lines the fold
    // leaves and a kept count that moved with the slots could flip between two answers on every pass.
    internal const int WaitingShown = 3;

    internal const int FloatingWaitingShown = 1;

    // How many waiting lines this bar keeps (see above).
    int WaitingKept => SizeToContent == SizeToContent.Manual ? WaitingShown : FloatingWaitingShown;

    // The fewest lines worth a group line: one line folded into "+1 more waiting on you" would hide a title for nothing.
    const int FoldsFrom = 2;

    /// <summary>
    /// The lines the bar draws after folding the Waiting on you block (<see cref="FoldWaiting"/>): <paramref
    /// name="Lines"/> in order without the folded ones, the <paramref name="Folded"/> lines in the group line, and
    /// <paramref name="At"/>, the index in <paramref name="Lines"/> before which the group line is drawn (-1: no group
    /// line, nothing folded).
    /// </summary>
    internal sealed record WaitingFold(IReadOnlyList<TaskLine> Lines, IReadOnlyList<TaskLine> Folded, int At)
    {
        public static WaitingFold None(IReadOnlyList<TaskLine> lines) => new(lines, [], -1);

        public bool Any => Folded.Count > 0;

        // Whether a line past the kept ones was drawn only because it is selected (or a selected subtask is drawn under
        // it): once the selection moves on, the fold must be taken again at once (PaintTaskSelection), not at some
        // later paint that would then move the lines below it with nothing done by Petre.
        public bool KeptSelected { get; init; }

        // How many of the folded lines are questions (Waiting on you), the rest being Done tasks: the group line turns
        // the asking orange when there is one, as "+N waiting" does, so no question is out of sight without a sign.
        public int Questions => Folded.Count(l => l.Status == TaskStatus.WaitingOnYou);
    }

    /// <summary>Folds the Waiting on you block after <paramref name="shown"/> lines (see the comment above).</summary>
    internal static WaitingFold FoldWaiting(IReadOnlyList<TaskLine> lines, int shown, Maybe<Guid> keep = default)
    {
        // The selected line stays, and so does every task it is drawn under (review finding 3): a subtask drawn under a
        // folded parent would sit, arrow and all, under whichever line preceded it, and read as that one's subtask (the
        // drag code would treat it so too). So its parent, and the parent's parent, keep their lines with it.
        var byId = lines.GroupBy(l => l.Id).ToDictionary(g => g.Key, g => g.First());
        IEnumerable<Guid> WithParents(Guid id) =>
            byId.TryGetValue(id, out var line) && line.ShownIn is not null && line.ParentId is { } parent && parent != id
                ? WithParents(parent).Prepend(id)
                : [id];
        var kept = keep.HasValue ? WithParents(keep.Value).Take(lines.Count).ToHashSet() : [];
        var tail = lines.Where(l => l.GroupKey == "waiting").Skip(shown).ToArray();
        var folded = tail.Where(l => !l.Closing && !kept.Contains(l.Id)).ToArray();
        // Set whether or not anything folds: a selected line can be the one that keeps the fold below two lines, and the
        // fold must still be taken once the selection leaves it.
        var keptSelected = tail.Any(l => kept.Contains(l.Id));
        if (folded.Length < FoldsFrom) return WaitingFold.None(lines) with { KeptSelected = keptSelected };
        var gone = folded.Select(l => l.Id).ToHashSet();
        var drawn = lines.Where(l => !gone.Contains(l.Id)).ToArray();
        // Right after the leading run of waiting lines: the block sits at the top of the pile, so this is where quadrant
        // I (or whatever group follows) begins. The leading run, not the last waiting line anywhere: a closing line put
        // back at its old place (WithPendingCloses) can fall past the block's end once the block has shrunk, and the
        // group line must not follow it in among the quadrant lines (review finding 4).
        return new WaitingFold(drawn, folded, drawn.TakeWhile(l => l.GroupKey == "waiting").Count()) { KeptSelected = keptSelected };
    }

    // The group line's words: how many more tasks wait on Petre. One phrase whatever the mix of questions and Done
    // tasks, since both are "waiting on you" (the block's own name in the tasks panel); the ink says whether a
    // question is among them, the tooltip lists them. A plain subtask drawn under a waiting parent folds with the
    // block but does not wait on him, so when the group holds some it says so the way "+N more, K waiting" does,
    // rather than count them as waiting (review of this change).
    internal static string WaitingGroupText(WaitingFold fold)
    {
        var waiting = fold.Folded.Count(l => l.Status.IsPinned());
        return waiting == fold.Folded.Count ? $"+{waiting} more waiting on you"
            : waiting == 0 ? $"+{fold.Folded.Count} more"
            : $"+{fold.Folded.Count} more, {waiting} waiting on you";
    }

    // What follows the title: the question while the task waits on Petre, else its status line, else (`withDescription`,
    // the bar's own lines) the first line of his description (Ruling 58: it shows where a Claude task shows its status
    // line, and a status line or a question keeps the place); and for a Claude task whose sessions have all ended, "no
    // live session" after that (spec, "A task whose sessions have all ended stays open and says 'no live session'"). The
    // tasks window's lines say the same but draw the whole description as a block of their own, so they leave it out here
    // (TasksBoard.Row).
    //
    // `plain` (the bar, task 08cdb052): a session's question or status line may carry markdown, above all the link to the
    // pull request it asks to have merged. The tasks window keeps it, and draws the link as one to click
    // (TasksWindow.DetailText); the bar's line is a single click target that picks its task, so there it is the link's
    // words alone (TaskViews.PlainInline), never "[Deskbar #490](https://github.com/...)" written out.
    //
    // `withQuestion` false (the tasks window's open line whose thread shows the question, task 968c2216): the question's
    // place is left empty rather than given to the status line, so opening the chat only takes the question away.
    internal static string DetailOf(TaskItem task, bool withDescription = false, bool plain = false, bool withQuestion = true) =>
        string.Join(TaskDetailSeparator, new[]
        {
            task.Status == TaskStatus.WaitingOnYou && task.Question.HasValue ? (withQuestion ? Shown(task.Question.Value, plain) : "")
                : task.StatusLine.Length > 0 ? Shown(task.StatusLine, plain)
                : withDescription ? TaskViews.PlainFirstLine(task.Description) : "",
            task.IsClaudes && !task.HasLiveSession ? NoLiveSession : "",
        }.Where(part => part.Length > 0));

    static string Shown(string text, bool plain) => plain ? TaskViews.PlainInline(text) : text;

    // The line under the list: how many open tasks are out of sight, naming how many of them wait on Petre; "Less" at
    // the end of the list expanded in place; "All tasks" when nothing is hidden and the button opens the window.
    static string MoreText(int hidden, int waiting, bool expanded) =>
        expanded ? "Less"
        : hidden == 0 ? "All tasks"
        : waiting == 0 ? $"+{hidden} more"
        : waiting == hidden ? $"+{hidden} waiting"
        : $"+{hidden} more, {waiting} waiting";

    // What the empty box says: where a note typed now would go.
    internal static string PlaceholderFor(Maybe<string> workspace) =>
        workspace.HasValue ? $"Add to {workspace.Value}…" : "Add…";

    // THE FAILED-LOAD LINE, which must be true about the file (kept from the abandoned task window, commit 78ff230,
    // TaskPanel.LoadProblemLine). JsonTaskStore.Load moves an unreadable tasks.json (and its previous copy) aside as
    // `tasks.json.<stamp>.bak` and says so in its reason ("kept tasks.json as ..."); when a move fails, a lock on the
    // file, the reason says "could not keep <path> aside" instead, and the file stays where the next save may write
    // over it once the lock clears. So the line promises a .bak only when the reason says one was kept and none
    // failed, and otherwise tells Petre to save the file himself. The reason, which names the files, is the tooltip.
    internal static string LoadProblemLine(string reason) =>
        reason.Contains("kept ", StringComparison.Ordinal) && !reason.Contains("could not keep", StringComparison.Ordinal)
            ? "Tasks could not be read. The old file is kept beside tasks.json as a .bak."
            : "Tasks could not be read. tasks.json could not be moved aside, so copy it somewhere safe before changing anything.";

    // --- building --------------------------------------------------------------------------------------------------

    // Built once, from the constructor, like the stats line and the Pro reminder. Everything that stays is made here;
    // the lines are made by PaintTaskList.
    void BuildTaskList()
    {
        // The failed-load line's place, first in the list (LoadProblemRow fills it).
        taskProblemHost = new Border { Visibility = Visibility.Collapsed };

        // The box. Its Enter and Esc are read in Preview, ahead of anything the box itself does with them. WPF has no
        // placeholder of its own, so the placeholder is a text laid over the box, shown while the box is empty and
        // never in the way of a click.
        taskAddBox = new TextBox
        {
            Tag = "task-add-box",
            // The bar's own template, never the theme's (TaskAddBoxTemplate says why: Fluent turned it white).
            Template = TaskAddBoxTemplate,
            Background = TaskFieldFill,
            Foreground = TaskInk,
            CaretBrush = TaskInk,
            BorderBrush = TaskPickedEdge,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 2, 4, 2),
            // A NOTE OF SEVERAL LINES (task 98420dbc, TaskNote has the rest). Long text wraps and the box grows a line at
            // a time instead of scrolling sideways out of sight; Shift+Enter breaks the line (OnTaskAddKey leaves it to
            // the box, which takes it because AcceptsReturn is on; that is also what keeps the line breaks of pasted
            // text, which a single-line box drops). Past TaskAddMaxLines the box stops growing and scrolls inside, so a
            // long paste cannot push the task lines off the bar.
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            MaxLines = TaskAddMaxLines,
            ToolTip = "Enter asks which workspace, then adds the task; Ctrl+Enter does the same and starts Claude on it; Shift+Enter starts a new line, Esc clears the box. The first line is the title, the rest the description. Ctrl+V or a drop adds a picture. Type / to pick one of the workspace's Claude Code skills",
        };
        AutomationProperties.SetName(taskAddBox, "New task");
        // The "/" list of skills (task 3a872301). Attached BEFORE OnTaskAddKey, so while the list is open its Enter, Tab
        // and Esc pick or close instead of adding the task or clearing the box. It lists the skills of the workspace on
        // the chip (taskAddTarget), under the subtask's parent when there is one, and only while a task can be handed to
        // Claude at all (StartClaude wired, as for the Add and start Claude button). The properties are read at the
        // keystroke, since they are set after this list is built.
        taskAddSkills = new SkillCompletion(taskAddBox,
            () => WorkspaceSkills is { } skills ? skills(taskAddTarget, taskAddParent) : Task.FromResult<IReadOnlyList<SkillChoice>>([]),
            () => WorkspaceSkills is not null && StartClaude is not null,
            new SkillCompletion.Look(TaskFieldFill, TaskPickedEdge, TaskInk, TaskDimInk, TaskSelectedFill));
        taskAddBox.PreviewKeyDown += OnTaskAddKey;
        taskAddBox.PreviewMouseLeftButtonDown += (_, _) =>
        {
            if (!taskTyping) BeginTyping();
        };
        taskAddBox.TextChanged += (_, _) => PaintAddRow();
        // The box is in the footer, so a line more or less changes the room the docked bar's task lines have: they are
        // counted again, as when the pictures row comes or goes (PaintTaskAddPictures).
        taskAddBox.SizeChanged += (_, e) =>
        {
            if (e.HeightChanged) QueueTaskListFit();
        };
        taskAddPlaceholder = new TextBlock
        {
            Tag = "task-add-placeholder",
            Foreground = TaskDimInk,
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            // The box's border, padding and the caret's own two DIP, so the placeholder sits where typing starts.
            Margin = new Thickness(7, 0, 6, 0),
        };
        var box = new Grid { Children = { taskAddBox, taskAddPlaceholder } };

        // The chips, wrapping onto a second line on a narrow bar instead of clipping (Petre's 320 px screenshot had
        // them cut off). Always shown (Ruling 12, Petre: "present always"): a row that came and went with the keyboard
        // made the whole list jump up and down under it.
        // RULING 45: ONE chip, the chosen target, whose press opens the workspace menu (OpenAddTargetMenu). The menu
        // opens from the chip button's own press (TaskButton's Click), never from a second Click handler: that marks the
        // click handled, so a later handler never runs (the tasks window's dropdown, 2026-11-23).
        taskAddChip = TaskChip("", () => OpenAddTargetMenu());
        // NO "on" BEFORE THE CHIP, AND A COMPACT ROW (task 47398c0f; Petre: "drop on; make it compact"). His screenshot
        // had this row wrap its last button, the play glyph of Add and start Claude, onto a second line of its own. The
        // word "on" said nothing the chip and its arrow do not, so it went, and the gaps between the chip, the toggles,
        // the play glyph, "All tasks" and "Manager" are 2 DIP a side instead of 3 to 6 (the glyph margins of
        // AddFlagToggle and AddAndStartClaudeButton, TaskChip's and ParentChipButton's right margin, the two right-hand
        // buttons' padding and left margin). The row still wraps, rather than clips, on a bar too narrow for it.
        // The important and urgent toggles of a task line, for the note about to be added (Ruling 37), at the end of the
        // chip row: in the wrap, so on a narrow bar they wrap with the chips instead of widening the bar (the whole list
        // sits in TakesNoWidth), and the docked fit pass measures the row as it is.
        taskAddImportant = AddFlagToggle(TaskFlag.Important);
        taskAddUrgent = AddFlagToggle(TaskFlag.Urgent);
        // ADD AND START CLAUDE (task 3c40dec9; Petre: "when adding a task, have a button to start immediately with
        // claude"), after the two flags: AddAndStartClaudeButton says what it does.
        taskAddStartClaude = AddAndStartClaudeButton();
        // A TASK FROM THE WINDOW ON SCREEN (task 494d34ee): the camera right after the two toggles, before the play glyph,
        // since Petre asked for it "near the urgent/important buttons" (FloatingBar.TaskFromWindow.cs).
        taskAddFromWindow = AddFromWindowButton();
        taskAddChips = new WrapPanel
        {
            Tag = "task-add-chips",
            Margin = new Thickness(0, 3, 0, 0),
            Children = { taskAddChip.Button, ParentChipButton(), taskAddImportant.Button, taskAddUrgent.Button, taskAddFromWindow, taskAddStartClaude },
        };
        PaintAddFlags();
        // RULING 50 (Petre: "move all tasks to the top to save vertical space, next to workspace selection"). "All tasks" /
        // "+N more" / "+N waiting" / "Less" used to be a line of its own under the last task. It sits in the chip row now,
        // right-aligned after the chip and the flag toggles, so the bottom line is gone and the docked fit pass (which
        // measures what the footer needs) hands that line's height to the task lines. The row is a Grid: the wrapping
        // chip row takes what is left and the button its own width, so the button never wraps. On a narrow list its text
        // shortens ("All", "+3") with the full words in the tooltip (ApplyMoreText), so nothing widens the bar (the whole
        // list sits in TakesNoWidth). Like every task button it acts on its own press (TaskButton's Click, with the click
        // guard), never a second Click handler (#48).
        taskMoreLabel = new TextBlock
        {
            Foreground = TaskDimInk,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        taskMoreButton = TaskButton("More tasks", taskMoreLabel, OnTaskMoreClick, ignoredWhen: TaskClickIgnored);
        taskMoreButton.HorizontalAlignment = HorizontalAlignment.Right;
        taskMoreButton.VerticalAlignment = VerticalAlignment.Top;
        taskMoreButton.Padding = new Thickness(4, 1, 4, 1);
        taskMoreButton.Margin = new Thickness(2, 4, 0, 0);
        taskMoreButton.Visibility = Visibility.Collapsed;
        // The Manager button sits after "All tasks" in a third Auto column, styled like it (dim ink, same padding and margin),
        // and is collapsed when the App wired no OpenManager. It takes the same click guard as "All tasks", since the list
        // can slide under the pointer too. ApplyMoreText counts its width, so "All tasks" shortens rather than clipping.
        // The dot before "Manager" (event wakes, docs/superpowers/specs/2026-11-25-manager-event-wakes-design.md): Deskbar
        // woke the manager and the user has not opened it since (ManagerRecord.Unread, PaintManagerDot). Orange like "+N
        // waiting", with a dark rim like the attention dot, so it reads on any bar colour. Collapsed, it takes no width.
        taskManagerDot = new System.Windows.Shapes.Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = TaskOrange,
            Stroke = ManagerDotRim,
            StrokeThickness = 0.75,
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        taskManagerButton = TaskButton("Manager",
            new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                Children =
                {
                    taskManagerDot,
                    new TextBlock { Text = "Manager", Foreground = TaskDimInk, VerticalAlignment = VerticalAlignment.Center },
                },
            },
            () => OpenManager?.Invoke(), ManagerTip, TaskClickIgnored);
        taskManagerButton.HorizontalAlignment = HorizontalAlignment.Right;
        taskManagerButton.VerticalAlignment = VerticalAlignment.Top;
        taskManagerButton.Padding = new Thickness(4, 1, 4, 1);
        taskManagerButton.Margin = new Thickness(2, 4, 0, 0);
        taskManagerButton.Visibility = OpenManager is null ? Visibility.Collapsed : Visibility.Visible;
        var chipRow = new Grid
        {
            Tag = "task-add-row",
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
            },
            Children = { taskAddChips, taskMoreButton, taskManagerButton },
        };
        Grid.SetColumn(taskMoreButton, 1);
        Grid.SetColumn(taskManagerButton, 2);
        // The pictures pasted or dropped on the box wait between it and the chips (FloatingBar.TaskPictures.cs).
        var addRow = new StackPanel { Margin = new Thickness(0, 0, 0, 3), Children = { box, TaskAddPicturesRow(taskAddBox), chipRow } };
        addRow.SizeChanged += (_, _) => ApplyMoreText();
        taskAddChips.SizeChanged += (_, _) => ApplyMoreText();

        taskLinesPanel = new StackPanel();
        WireTaskDrag(taskLinesPanel);
        // Every line is drawn in here and the viewer is only as tall as the lines that fit (CapTaskLines), so the rest
        // scroll (THE LIST SCROLLS). The bar draws no scroll bar of its own anywhere, so none here either: the wheel
        // scrolls it, and "+N more" says how much is out of sight.
        taskLinesScroller = new ScrollViewer
        {
            Tag = "task-lines",
            Content = taskLinesPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };
        // A scroll, or the view or the list changing size, changes which lines are out of sight: "+N more" is recounted.
        // Only the count and its words change, never a line, so nothing moves under the pointer.
        taskLinesScroller.ScrollChanged += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, taskLinesScroller)) PaintTaskMore(openTaskLines);
        };
        // The lines' heights follow the width they are laid out at (a title can wrap to two lines, Ruling 41), and the
        // first paint measures them before any width is known: once laid out, the viewer's height is taken again from
        // the lines as they really are.
        taskLinesPanel.SizeChanged += (_, _) => RecapTaskLinesFromLayout();
        // The manager's question (task 61e4d143, FloatingBar.ManagerLine.cs) sits between the add row and the lines, outside
        // the scrolling viewer, so it is always the first line in sight and never scrolls away: its priority.
        taskManagerHost = new Border { Tag = "manager-line-host", Visibility = Visibility.Collapsed };
        var host = new TakesNoWidth(VisibleWidthOf) { Child = new StackPanel { Children = { taskProblemHost, addRow, taskManagerHost, taskLinesScroller, InboxFooterRow() } } };
        TaskListLine.Child = host;
        // A RIGHT-CLICK IN THE LIST NEVER OPENS THE BAR'S MENU (Petre's screenshot: right-clicking a task line opened New
        // workspace, Window list, Arrange, Dock, Manage, Exit). The bar's menu is the Root's ContextMenu, and a
        // ContextMenuOpening that nothing below it handles bubbles up to it. A task line has its own menu (Edit first;
        // FloatingBar.TaskEdit.cs) and the text boxes their own (cut, copy, paste), and both handle the event first. For
        // the rest of the list (the chip row, the toggles, "All tasks", the gaps) this stops it here: nothing opens.
        TaskListLine.ContextMenuOpening += (_, e) => e.Handled = true;
        // The width the list is laid out at follows the bar's window (VisibleWidthOf), and a footer laid out wider than
        // the window keeps the host's own slot unchanged when the window narrows, so nothing would arrange it again:
        // every change of the bar's size asks it to (measured: the lines stayed at the width of the bar before).
        SizeChanged += (_, _) =>
        {
            host.InvalidateArrange();
            // A docked bar's height changed: the free height for the list did too (ruling 23).
            if (SizeToContent == SizeToContent.Manual || taskListSlots != TaskListSlots) QueueTaskListFit();
        };
        // The bar's own selection until the App hands it the shared one (Selection's init replaces this subscription).
        // With the Subtasks box unticked the selected task never folds (FoldSubtasks' `keep`, task 4c01747c), so a change
        // of selection can add or take away a line: the list is painted again first, which costs a comparison when the
        // lines come out the same (PaintTaskList's shape check).
        selectionChanges ??= selection.Changed.Subscribe(_ =>
        {
            if (!Subtasks) PaintTaskList();
            PaintTaskSelection();
        });
        ApplyTaskListTextSize();
        PaintAddRow();
        Closed += (_, _) =>
        {
            taskChanges?.Dispose();
            goalChanges?.Dispose();
            selectionChanges?.Dispose();
            // Every closed line's timer goes with the bar; the tasks are closed already, nothing is left to do.
            pendingCloses.ForEach(pending => pending.Timer.Dispose());
            pendingCloses.Clear();
        };
    }

    // A one-shot dispatcher timer for the undo window (TaskUndoTimer's default, and the tasks window's).
    internal static IDisposable DispatcherUndoTimer(TimeSpan after, Action then)
    {
        var timer = new DispatcherTimer { Interval = after };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            then();
        };
        timer.Start();
        return Disposable.Create(timer.Stop);
    }

    // A small button in the bar's style (the strip's template, so it hovers like the bar's other small controls).
    // Marks the press consumed, as every button on the bar does, so the bar's own release does not read it as a click
    // of its own; and marks the Click handled, because a line is itself a button and the tick box and the close button
    // sit inside it: Click bubbles, and the line must not also act on them. Handled even when the guard drops the
    // click, so a dropped tick never turns into a click on its line.
    //
    // `ignoredWhen`: the rule that drops a click. The tick, the close button and "+N more" take TaskClickIgnored; Undo
    // takes only the double click (DoubleClickIgnored), since it appears under the pointer on purpose; the chips and
    // the failed-load line's dismiss take none, since nothing slides under them.
    Button TaskButton(string name, UIElement content, Action press, string? tip = null, Func<bool>? ignoredWhen = null)
    {
        var button = new Button
        {
            Content = content,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Template = ShellButtonTemplate,
            Focusable = false,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(button, name);
        if (tip is not null) button.ToolTip = tip;
        if (ignoredWhen is not null) button.PreviewMouseLeftButtonDown += (_, e) => taskPressClicks = e.ClickCount;
        button.Click += (_, e) =>
        {
            e.Handled = true;
            MarkPressConsumed();
            if (ignoredWhen?.Invoke() == true) return;
            press();
        };
        return button;
    }

    // THE CLICK GUARD (PR A review, Important 2 and Minor 5). Ticking a note takes its line away and the next note's
    // tick slides under the pointer, so the second click of a double click closed that one too; and the list grows
    // upward from the footer, so "+N" moves under the pointer the same way. A click is dropped when its press was the
    // second of a double click (ClickCount above 1) or when it lands within half a second of a repaint that redrew the
    // lines: either way it was aimed at something that has just moved. Reading the guard consumes the press's count.
    bool TaskClickIgnored() => DoubleClickIgnored() || TaskClickClock() - taskLinesChangedAt < RepaintGrace;

    // Undo's rule: only the second press of a double click is dropped. Undo appears where the tick or the close button
    // was, so that a quick second click lands on it and not on the next note; a deliberate click on it, however soon,
    // is meant. A double click on the tick, though, must not close and undo in one gesture.
    bool DoubleClickIgnored()
    {
        var doubled = taskPressClicks > 1;
        taskPressClicks = 0;
        return doubled;
    }

    // A chip of the add box. Its edge is always 1 DIP, coloured only when the chip is the one ticked, so ticking
    // another chip never moves anything (CLAUDE.md: present and recoloured, not conditional thickness).
    TaskChipParts TaskChip(string text, Action press)
    {
        // Trimmed under a ceiling (ApplyTaskListTextSize), so a long workspace name cannot push "other…" out of the row.
        var label = new TextBlock { Text = text, Foreground = TaskInk, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        // The small arrow says the chip opens a menu; its own text, so a long name trims and the arrow stays.
        var arrow = new TextBlock { Text = "▾", Foreground = TaskDimInk, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        var face = new Border
        {
            Tag = "task-chip",
            Background = TaskButtonFill,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(7, 0, 7, 1),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { label, arrow } },
        };
        var button = TaskButton("Add to workspace", face, press, "Choose the workspace for the note");
        button.Margin = new Thickness(0, 1, 2, 1);
        return new TaskChipParts(button, face, label, arrow);
    }

    // The widest a workspace chip's name may be, in a line or in the add box: about eight characters of chip text,
    // following the text size. Past it the name is trimmed, so it never squeezes a title away.
    double ChipTextCeiling => Text(7.5) * 7;

    // The text built once and kept takes the bar's text size here (ApplyTextSize); the lines take it as they are made.
    void ApplyTaskListTextSize()
    {
        Sized(taskMoreLabel, 8);
        Sized(taskAddPlaceholder, 9.5);
        if (taskAddChip is not null)
        {
            Sized(taskAddChip.Label, 7.5);
            Sized(taskAddChip.Arrow, 7.5);
            taskAddChip.Label.MaxWidth = ChipTextCeiling;
        }
        if (taskAddBox is not null) taskAddBox.FontSize = Text(9.5);
    }

    // --- painting --------------------------------------------------------------------------------------------------

    // The list's one way onto the screen: from the service's pulse, after every rebuild of the bar, and after a click
    // that changes what it shows. Cheap when nothing changed: the lines are compared, not redrawn (see the header).
    // `evenUnderPress` is the undock's (FoldTaskListForFloating): the bar is changing shape anyway, so the wait for a
    // press to end protects nothing there.
    internal void PaintTaskList(bool evenUnderPress = false)
    {
        // The rebuild paints the list itself once it has finished (Rebuild, Relayout).
        if (rebuilding || taskLinesPanel is null) return;

        // Not under a press or a drag anywhere on the bar (see TaskListMustWait). The rebuild that serves the wait ends
        // by painting the list, so nothing is lost, only put off until the press or the drag is over.
        if (!evenUnderPress && TaskListMustWait)
        {
            rebuildRequested = true;
            return;
        }

        var shown = taskService is not null && TasksAllowed() && !manager.State.HiddenStripItems.Contains(StripItem.Tasks);
        TaskListLine.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (!shown || taskService is null)
        {
            if (taskTyping) EndTyping(clear: false);
            return;
        }

        FollowCurrentWorkspace();
        // A task closed or deleted is not selected any more (ruling 21).
        selection.KeepOnly(taskService.Board);
        // On a bar whose height is not its content's, the pass after layout sees whether the free height takes more
        // lines or fewer (ruling 23). Queued on every paint, the rebuild's included, since rows added or removed change
        // the free height without changing the list.
        if (SizeToContent == SizeToContent.Manual || taskListSlots != TaskListSlots) QueueTaskListFit();

        // RULING 48: the bar lists a Claude task only while it needs Petre (NeedsYou). The window shows everything.
        // The bar's lines in the pile's order; UnderParents draws subtasks under their parent. WithPendingCloses does it
        // again with the lines in their undo window among them (review of #467, finding 4).
        var flat = Lines(taskService.Board, WorkspaceName, id => workspaceAccents.GetValueOrDefault(id), SessionWindow, GoalTitle,
                taskService.DeliveryOf,
                // Asked only of the lines the bar keeps (NeedsYou): the others are dropped below, so their folders and
                // transcripts are not looked up on every paint (task 6a57f59b review).
                OpensClaudeSession is { } opens ? t => t.Assignee.IsMe && opens(t) : null)
            .Where(NeedsYou).ToArray();
        // The tasks panel's "Subtasks" checkbox applies here too (task 173ef268; Petre, with a screenshot of the bar's
        // Warehouse parent and its subtasks under it: "same checkbox"). One setting, saved by the panel, so the bar and
        // the panel never disagree; a state pulse rebuilds the bar, and the rebuild repaints this list.
        // Every subtask with an open parent folds, its parent on the bar or not (task 4c01747c: a snoozed parent, or one
        // assigned to Claude, is never on the bar, and Petre still expects its subtasks gone; see FoldSubtasks).
        var underOpenParent = TaskViews.UnderOpenParent(taskService.Board);
        var open = FoldSubtasks(UnderParents(flat), Subtasks, underOpenParent, selection.Current);
        // A closed line whose task is open again (reopened from the pipe, say) is not pending any more: the task is back
        // in the pile under its own line.
        pendingCloses.Where(p => open.Any(l => l.Id == p.Line.Id)).ToList().ForEach(p => ForgetPendingClose(p, repaint: false));
        var slots = TaskSlotsNow;
        // Expanded in place only while something would be out of sight: more lines than slots, or a Waiting on you block
        // that folds (the group line's click expands the list too, OpenTasksFromGroup; review finding 5).
        if (open.Count <= slots && !FoldWaiting(open, WaitingKept, selection.Current).Any) taskListExpanded = false;
        var unfolded = FoldSubtasks(WithPendingCloses(flat, pendingCloses), Subtasks, underOpenParent, selection.Current);
        // The Waiting on you block folds after three lines (task 39f4cc8a, FoldWaiting), except while the list is
        // expanded in place (no tasks panel behind the bar: the group line's click expands it, and then every line shows).
        var fold = taskListExpanded ? WaitingFold.None(unfolded) : FoldWaiting(unfolded, WaitingKept, selection.Current);
        var all = WithGroupStarts(fold.Lines);
        allTaskLines = all;
        var problem = taskService.LoadProblem;
        // The manager's question is part of the shape (task 61e4d143), so asking, replacing or clearing it repaints.
        var managerAsk = ManagerAsks?.Invoke();
        // The group line is part of the shape too: where it sits, whether a selected line holds the fold open, and each
        // folded line's status (the ink, the tooltip's "Done: ") and title (the tooltip).
        var shape = string.Join("\n", all.Select(ShapeOf).Prepend(
            $"{InboxBarOn}|{taskListExpanded}|{ToggleTasksPanel is null}|{TextOffset}|{problem.GetValueOrDefault("")}|{slots}|{editingTask}|{managerAsk}|{PermissionShape()}|" +
            $"{fold.At}|{fold.KeptSelected}|{string.Join(',', fold.Folded.Select(l => $"{l.Id}:{l.Status}:{l.Title}"))}"));
        if (shape == lastTaskListShape) return;
        lastTaskListShape = shape;

        // Every line is drawn; the viewer shows `slots` of them (or eight expanded in place) and the rest scroll.
        shownTaskLines = all;
        // The lines before the fold, which a closing line's place is counted in (CloseWithUndo), so it goes back to the
        // same place among them however the fold has changed meanwhile.
        unfoldedTaskLines = unfolded;
        waitingFold = fold;
        openTaskLines = open;
        taskLinesCap = taskListExpanded ? ExpandedSlots : slots;
        taskLinesPanel.Children.Clear();
        if (taskProblemHost is not null)
        {
            taskProblemHost.Child = problem.HasValue ? LoadProblemRow(problem.Value) : null;
            taskProblemHost.Visibility = problem.HasValue ? Visibility.Visible : Visibility.Collapsed;
        }
        PaintManagerLine(managerAsk);
        shownTaskLines.ToList().ForEach(line => taskLinesPanel.Children.Add(TaskLineButton(line)));
        // The folded Waiting on you lines' one line, where the block ends (FoldWaiting).
        if (fold.Any) taskLinesPanel.Children.Insert(fold.At, WaitingGroupButton(fold));
        // When the lines on show changed places (which tasks, in which order, which are closing), the click guard
        // distrusts a click for the next half second (TaskClickIgnored). A paint that only rewrote what lines say (a
        // session's status line) moved nothing and does not start it, or frequent status pulses would eat clicks. The
        // text size and the failed-load line move every line too (a bigger font, a line above them), so they are part
        // of the key (review minor 3 of 187cf4a).
        // The manager line showing or going moves every task line down or up, so it is part of the key too.
        // The group line coming, going or moving moves the lines below it, so its place is part of the key as well.
        var placesKey = string.Join('|', shownTaskLines.Select(l => $"{l.Id}{l.Closing}").Prepend($"{TextOffset}|{problem.HasValue}|{managerAsk is not null}|{fold.At}"));
        if (placesKey != taskLinePlaces)
        {
            taskLinePlaces = placesKey;
            taskLinesChangedAt = TaskClickClock();
        }
        CapTaskLines();
        PaintTaskMore(open);
        QueueTaskMoreRecount();
    }

    // The open lines of the last paint, for "+N more" recounted on a scroll (ScrollChanged), between paints.
    IReadOnlyList<TaskLine> openTaskLines = [];

    // The last paint's lines before the Waiting on you fold, and the fold itself (FoldWaiting, task 39f4cc8a).
    IReadOnlyList<TaskLine> unfoldedTaskLines = [];
    WaitingFold waitingFold = WaitingFold.None([]);

    // How many lines the viewer is as tall as: the slots (three floating, the free height's worth docked), or eight
    // while the list is expanded in place.
    int taskLinesCap = TaskListSlots;
    bool taskMoreRecountQueued;

    // The lines just drawn have no layout yet, so the paint counts "+N more" as if the view were at its top; a list
    // scrolled down and repainted (a status line changed) keeps its place, the same extent and offset, and then raises
    // no ScrollChanged to correct the count. So the count is taken again once the layout has run.
    void QueueTaskMoreRecount()
    {
        if (taskMoreRecountQueued) return;
        taskMoreRecountQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            taskMoreRecountQueued = false;
            PaintTaskMore(openTaskLines);
        });
    }

    // The open lines with every closed line still in its undo window put back where it was, in the order they were
    // closed at, so no line moves up under the pointer while one lingers (Ruling 10). Each index is the line's place
    // among the lines on show when it was closed, the earlier lines (pending ones included) already in front of it.
    //
    // SUBTASKS (review of #467, finding 4): the lines are nested with the closing ones among them, so a parent ticked off
    // keeps its subtasks under its struck-through line instead of letting them fall back to their own places, which would
    // move lines under the pointer. Each closing line then goes back to its place together with the subtasks drawn
    // under it, as one block.
    static IReadOnlyList<TaskLine> WithPendingCloses(IReadOnlyList<TaskLine> flat, IReadOnlyList<PendingClose> pending) =>
        pending.OrderBy(p => p.Index).Aggregate(UnderParents(flat.Concat(pending.Select(p => p.Line)).ToArray()).ToList(), (lines, p) =>
        {
            var block = lines.Where(l => l.Id == p.Line.Id || (l.ShownIn is not null && l.ParentId == p.Line.Id)).ToList();
            lines.RemoveAll(block.Contains);
            lines.InsertRange(Math.Min(p.Index, lines.Count), block);
            return lines;
        });

    // THE LIST SCROLLS (Petre, with a screenshot of his docked bar ending in "+16 more": "when there are more tasks than
    // can fit in the bar, it should let me scroll"). Every line of the pile is drawn into the viewer, and the viewer's
    // ceiling is the height of its first taskLinesCap lines: three on a floating bar, as many as the free height takes
    // on a docked one (the fit pass above decides that count, so the rows keep their priority as before), eight while
    // expanded in place. The lines past it scroll under the wheel, and "+N more" counts the lines out of sight as the
    // view moves (PaintTaskMore over LinesInSight). Before this the lines past the count were simply not drawn and the
    // only way to them was the tasks panel.
    //
    // The ceiling is set here, at the paint, from the lines just built and measured at the width the list has now, so
    // the very layout that follows already has it (an undock lays out three lines from the start, review I-1); once the
    // lines are laid out, RecapTaskLinesFromLayout takes it again from their real heights. A list that fits has no
    // ceiling and sits at its top.
    void CapTaskLines()
    {
        if (taskLinesScroller is null || taskLinesPanel is null) return;
        // Counted over everything drawn, the group line included (FoldWaiting): it takes a line's place in the viewer.
        if (taskLinesPanel.Children.Count <= taskLinesCap)
        {
            taskLinesScroller.MaxHeight = double.PositiveInfinity;
            taskLinesScroller.ScrollToTop();
            return;
        }
        var width = taskLinesPanel.ActualWidth > 0 ? taskLinesPanel.ActualWidth : double.PositiveInfinity;
        var counted = taskLinesPanel.Children.OfType<FrameworkElement>().Take(taskLinesCap).ToList();
        counted.ForEach(row => row.Measure(new Size(width, double.PositiveInfinity)));
        taskLinesScroller.MaxHeight = counted.Sum(row => row.DesiredSize.Height);
    }

    // The ceiling again from the lines as laid out (see CapTaskLines), when it differs by more than layout rounding.
    // The lines' heights do not depend on the ceiling (the scroll bar is hidden and takes no width), so this settles
    // after one more layout.
    void RecapTaskLinesFromLayout()
    {
        if (taskLinesScroller is null || taskLinesPanel is null || double.IsPositiveInfinity(taskLinesScroller.MaxHeight)) return;
        var counted = taskLinesPanel.Children.OfType<FrameworkElement>().Take(taskLinesCap).ToList();
        if (counted.Any(row => row.ActualHeight <= 0)) return;
        var ceiling = counted.Sum(row => row.ActualHeight + row.Margin.Top + row.Margin.Bottom);
        if (Math.Abs(ceiling - taskLinesScroller.MaxHeight) > 0.5) taskLinesScroller.MaxHeight = ceiling;
    }

    // The lines in sight: every line while nothing scrolls; before the lines are laid out, the first taskLinesCap (the
    // view at its top); else each line at least half inside the view.
    IReadOnlyList<TaskLine> LinesInSight() =>
        ButtonsInSight().Where(b => b.Tag is TaskLine).Select(b => (TaskLine)b.Tag).ToArray();

    // Whether the Waiting on you group line (FoldWaiting) is in sight, by the same measure as a task line.
    bool WaitingGroupInSight() => ButtonsInSight().Any(b => Equals(b.Tag, WaitingGroupTag));

    // Every button drawn in the viewer that is in sight, task lines and the group line alike, so the group line takes
    // its place in "the first taskLinesCap" as it does in the viewer's ceiling (CapTaskLines).
    IReadOnlyList<Button> ButtonsInSight()
    {
        if (taskLinesScroller is null || taskLinesPanel is null) return [];
        var buttons = taskLinesPanel.Children.OfType<Button>().ToList();
        if (double.IsPositiveInfinity(taskLinesScroller.MaxHeight)) return buttons;
        if (taskLinesScroller.ViewportHeight <= 0 || buttons.Any(b => b.ActualHeight <= 0 || !b.IsDescendantOf(taskLinesPanel)))
            return buttons.Take(taskLinesCap).ToArray();
        var top = taskLinesScroller.VerticalOffset;
        var bottom = top + taskLinesScroller.ViewportHeight;
        return buttons.Where(b =>
        {
            var y = b.TranslatePoint(new Point(0, 0), taskLinesPanel).Y;
            return Math.Min(y + b.ActualHeight, bottom) - Math.Max(y, top) >= b.ActualHeight / 2;
        }).ToArray();
    }

    // THE SELECTED LINE IS BROUGHT INTO SIGHT when the selection changes and its line is scrolled away (a Claude terminal
    // brought to the front selects its task, #477, and that line may sit below the view). Only when it is out of sight,
    // so a click on a line in view never scrolls anything. The scroll moves the lines, so the click guard distrusts a
    // click for its half second, as it does after a repaint (TaskClickIgnored).
    void BringSelectedIntoSight()
    {
        if (selection.Current.HasValue) ScrollTaskLineIntoView(selection.Current.Value);
    }

    /// <summary>
    /// Scrolls the viewer so the task's line is in sight, by the least distance (to the view's top edge when the line
    /// is above it, to its bottom edge when below). Nothing when the line is in sight already, when it is not drawn
    /// (the bar does not list the task), or under a press, a drag, a menu or an edit (TaskListMustWait: the lines must
    /// not slide under any of them). A line drawn by a paint that has not been laid out yet (a note just added, then
    /// selected) is scrolled to once the layout has run.
    /// </summary>
    internal void ScrollTaskLineIntoView(Guid id)
    {
        if (taskLinesScroller is null || taskLinesPanel is null || TaskListMustWait) return;
        var button = taskLinesPanel.Children.OfType<Button>().FirstOrDefault(b => b.Tag is TaskLine line && line.Id == id);
        if (button is null) return;
        if (button.ActualHeight <= 0 || taskLinesScroller.ViewportHeight <= 0)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (button.ActualHeight > 0 && taskLinesScroller.ViewportHeight > 0) ScrollTaskLineIntoView(id);
            });
            return;
        }
        if (LinesInSight().Any(l => l.Id == id)) return;
        var y = button.TranslatePoint(new Point(0, 0), taskLinesPanel).Y;
        var offset = y < taskLinesScroller.VerticalOffset ? y : y + button.ActualHeight - taskLinesScroller.ViewportHeight;
        taskLinesScroller.ScrollToVerticalOffset(Math.Max(0, offset));
        // The scroll moves the lines under the pointer, so the click guard distrusts a click for its half second.
        taskLinesChangedAt = TaskClickClock();
    }

    // Everything a line shows, as text, so two paints can be compared without drawing anything.
    static string ShapeOf(TaskLine line) =>
        $"{line.Id}|{line.Status}|{line.Title}|{line.Detail}|{line.Claudes}|{line.OpensSession}|{line.WorkspaceName}|" +
        $"{(line.Colour as SolidColorBrush)?.Color}|{line.Window.Match(w => w.ToString(), () => "")}|{line.Closing}|" +
        $"{line.Important}|{line.Urgent}|{line.StartsGroup}|{line.GoalTitle}|{line.Assignee.Id}|" +
        // Subtasks: the parent's title (the arrow's tooltip), the count, and where the line is drawn. A count can change
        // with no line of the bar changing (a subtask assigned to Claude is not on the bar), so it must be in the shape.
        $"{line.ParentTitle}|{line.SubtaskCount}|{line.ShownIn}|" +
        // The ring or the disc, and the hover line saying which (task 8470d176): GitHub's answer can change with nothing
        // else on the line changing.
        $"{line.Delivery.Stage}|{line.Delivery.Note}";

    // "+N more" counts the open tasks out of sight; a closed line in its undo window is not an open task, so it is
    // neither counted nor allowed to hide one from the count.
    void PaintTaskMore(IReadOnlyList<TaskLine> open)
    {
        if (taskMoreButton is null || taskMoreLabel is null) return;
        // Out of sight now means scrolled above the view or still below it (THE LIST SCROLLS). The lines folded into the
        // Waiting on you group line (FoldWaiting) are counted by that line while it is in sight, so they are not counted
        // here again; with the group line scrolled away they are out of sight like any line, and "+N waiting" says so.
        var onShow = LinesInSight().Select(l => l.Id)
            .Concat(WaitingGroupInSight() ? waitingFold.Folded.Select(l => l.Id) : []).ToHashSet();
        var hidden = open.Where(l => !onShow.Contains(l.Id)).ToArray();
        var waiting = hidden.Count(l => l.Status == TaskStatus.WaitingOnYou);
        // The Manager button follows the wiring on every paint: the list is built in the constructor, before the init
        // property is set, so its visibility cannot be decided at build time.
        if (taskManagerButton is not null) taskManagerButton.Visibility = OpenManager is null || InboxBarOn ? Visibility.Collapsed : Visibility.Visible; // in the Inbox look, the list's foot (FloatingBar.InboxFooter.cs)
        PaintManagerDot();
        taskMoreButton.Visibility = !InboxBarOn && (hidden.Length > 0 || taskListExpanded || ToggleTasksPanel is not null) ? Visibility.Visible : Visibility.Collapsed;
        taskMoreFull = MoreText(hidden.Length, waiting, taskListExpanded);
        taskMoreShort = taskListExpanded ? "Less" : hidden.Length == 0 ? "All" : $"+{hidden.Length}";
        ApplyMoreText();
        taskMoreWaiting = waiting > 0 && !taskListExpanded;
        // The tooltip carries the whole sentence whichever text shows, "+3 more, 1 waiting" cut to "+3" included.
        taskMoreTip = taskListExpanded ? "Show the first three"
            : ToggleTasksPanel is not null ? $"{(hidden.Length == 0 ? "" : $"{taskMoreFull}. ")}Every task, open and closed, with its comments"
            : $"{hidden.Length} more {(hidden.Length == 1 ? "task" : "tasks")}";
        PaintInboxFooter(hidden.Length, waiting); // the Inbox look's foot (FloatingBar.InboxFooter.cs)
        PaintTaskMoreLook();
    }

    // What PaintTaskMore worked out that the "on" look also needs: whether the words are orange (something hidden waits
    // on Petre) and the tooltip while the panel is not shown.
    bool taskMoreWaiting;

    // The Manager button's unread dot (event wakes): built with the button, painted by PaintManagerDot.
    System.Windows.Shapes.Ellipse? taskManagerDot;

    const string ManagerTip = "Open the manager: one Claude session that coordinates your tasks";

    // The dot's rim: frozen, as every static brush must be (a brush with thread affinity throws on another thread).
    static readonly Brush ManagerDotRim = Frozen(0xB0, 0x20, 0x20, 0x20);

    /// <summary>
    /// Whether Deskbar woke the manager since the user last opened it (ManagerRecord.Unread), read on every paint and on
    /// every state change (the wake and the click that clears it both save the state). Null shows no dot.
    /// </summary>
    internal Func<bool>? ManagerUnread { get; init; }

    // The dot shown while the manager has something unread, and the tooltip saying so.
    internal void PaintManagerDot()
    {
        PaintInboxFooterLook(); // its dot too (FloatingBar.InboxFooter.cs)
        if (taskManagerDot is null || taskManagerButton is null) return;
        var unread = ManagerUnread?.Invoke() == true;
        taskManagerDot.Visibility = unread ? Visibility.Visible : Visibility.Collapsed;
        taskManagerButton.ToolTip = unread ? "The manager has news since you last opened it. " + ManagerTip : ManagerTip;
    }
    string taskMoreTip = "";

    // THE "ON" LOOK of "All tasks" while the tasks panel is shown (TasksPanelShown): the picked line's light fill
    // (TaskSelectedFill) behind it and its words in full ink, unless they are orange for a task waiting on Petre, which
    // stays the louder signal; the tooltip then says what a click does now. Repainted on every list paint and on every
    // change of the panel, never rebuilding a line. Its automation status says "on" or "off", as the window's toggles do.
    void PaintTaskMoreLook()
    {
        if (taskMoreButton is null || taskMoreLabel is null) return;
        var on = tasksPanelOn && ToggleTasksPanel is not null;
        taskMoreButton.Background = on ? TaskSelectedFill : Brushes.Transparent;
        taskMoreLabel.Foreground = taskMoreWaiting ? TaskOrange : on ? TaskInk : TaskDimInk;
        taskMoreButton.ToolTip = on ? "Hide the tasks panel" : taskMoreTip;
        AutomationProperties.SetItemStatus(taskMoreButton, on ? "on" : "off");
        PaintInboxFooterLook(); // the Inbox look's foot (FloatingBar.InboxFooter.cs)
    }

    // The button's words in full and cut short (Ruling 50): the label shows the short one when the list is too narrow for
    // the full one beside the chip and the toggles, rather than wrapping to a line of its own or widening the bar.
    string taskMoreFull = "";
    string taskMoreShort = "";


    // Run on every paint of the button and whenever the add row's width changes, text only: it changes no line.
    // THE FULL WORDS SHOW ONLY WHEN THEY FIT (the first version cut at a fixed 200 DIP, which a large text size outgrew:
    // at TextOffset +3.5 the button clipped). The room is the row's width less what the chip and the toggles take on one
    // line (their measured widths); the need is the full words at the label's own font size, plus the button's padding
    // and margin. So it follows the text size, the chip's name and the bar's width.
    void ApplyMoreText()
    {
        if (taskMoreLabel is null || taskMoreButton is null) return;
        var row = (taskMoreButton.Parent as FrameworkElement)?.ActualWidth ?? 0;
        var chips = taskAddChips?.Children.OfType<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).Sum(c => c.DesiredSize.Width) ?? 0;
        // The Manager button takes its own width off the row too (measured, as the chips are), or "All tasks" would keep the
        // room the Manager button now fills and clip.
        var manager = taskManagerButton is { Visibility: Visibility.Visible } m ? m.DesiredSize.Width : 0;
        var need = FullMoreWidth() + manager + taskMoreButton.Padding.Left + taskMoreButton.Padding.Right + taskMoreButton.Margin.Left;
        taskMoreLabel.Text = row > 0 && chips > 0 && row - chips < need ? taskMoreShort : taskMoreFull;
    }

    // The full words' width at the label's font and size.
    double FullMoreWidth() =>
        taskMoreLabel is null ? 0 : new FormattedText(taskMoreFull, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(taskMoreLabel.FontFamily, taskMoreLabel.FontStyle, taskMoreLabel.FontWeight, taskMoreLabel.FontStretch),
            taskMoreLabel.FontSize, Brushes.White, VisualTreeHelper.GetDpi(taskMoreLabel).PixelsPerDip).Width;

    // The repeat mark for the Right slot (task 0b78269e, Task 9): Segoe Fluent's RepeatAll, U+E8EE, in the dim ink, with the
    // same tooltip as the tasks panel's (TaskRepeats.MarkTip). Empty for a task that does not repeat.
    IReadOnlyList<UIElement> RepeatMarkOf(TaskLine line) =>
        line.RepeatLabel is not { } label ? []
            : [new TextBlock
            {
                Text = "\uE8EE", FontFamily = IconFont, FontSize = 12, Foreground = TaskDimInk, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0), Tag = "task-repeat-mark",
                ToolTip = TaskRepeats.MarkTip(label, line.NextRun, line.RepeatModel ?? TaskRepeat.DefaultModel, DateTimeOffset.Now),
            }];

    // One task's line: a button the width of the list, so a click anywhere on it goes to the task.
    Button TaskLineButton(TaskLine line)
    {
        // Laid out by TaskLineFace (see its header): the title takes every DIP the tick box and the chip leave, and the
        // chip turns into a dot when the title would keep less than its share of the line.
        //
        // NOTHING RIGHT OF THE TITLE, AND NO OVERLAY (Petre, 2026-11-24: "important is bold; urgent is italic; no need
        // for extra signs, move the time from here to the proper tasks window"). The flag glyphs that were on, the quiet
        // toggles laid over the title's end, and the age are all gone from the bar's line: the title's weight and slant
        // already say the flags (Ruling 59, TextOf), the line's right-click menu still sets them (Important, Urgent:
        // FloatingBar.TaskEdit.cs), and the tasks panel has both the toggles and the age. TaskLineFace keeps its right
        // parts and overlay slots for any later use; the bar hands it none, with ONE exception: the repeat mark of a
        // recurring task (task 0b78269e, Task 9; Petre picked it from a mockup). It is a fact about the task, not a
        // control or a flag, and the title's weight cannot say it. It takes the Right slot: a glyph's width, beside the
        // title's first row, so the chip and the title keep their rules. A plain task hands the slot nothing, as before.
        var face = new TaskLineFace(LeftOf(line).ToList(), TitleOrEditor(line), RepeatMarkOf(line), WorkspaceChipButton(line),
            WorkspaceDot(line), null);
        // A closed line in its undo window is dimmed, all but its Undo, which is the one thing on it to press.
        if (line.Closing)
            face.Children.OfType<UIElement>().Where(part => part is not Button).ToList().ForEach(part => part.Opacity = 0.45);

        // Every line has the same 3 DIP edge, orange on Claude's and clear on Petre's notes, so the two kinds line up
        // and nothing changes width when a note becomes Claude's.
        var edge = new Border
        {
            Tag = "task-line",
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = line.Claudes ? TaskOrange : Brushes.Transparent,
            Background = line.Claudes ? TaskClaudeTint : Brushes.Transparent,
            CornerRadius = new CornerRadius(4),
            // 1 DIP above and below the text (was 3; Petre 2026-11-23: "no need for vertical spacing this big"). Enough
            // for the Claude tint and the selection fill to clear the text, and no more.
            Padding = new Thickness(5, 1, 5, 1),
            Child = face,
        };
        // The selection's frame (see Selection): always 2 DIP on the left, clear unless this line is selected.
        var selected = selection.IsSelected(line.Id);
        var frame = new Border
        {
            Tag = "task-selection",
            BorderThickness = new Thickness(2, 0, 0, 0),
            BorderBrush = selected ? TaskPickedEdge : Brushes.Transparent,
            Background = selected ? TaskSelectedFill : Brushes.Transparent,
            CornerRadius = new CornerRadius(4),
            Child = edge,
        };
        var button = new Button
        {
            Tag = line,
            Content = InboxBarOn ? InboxLineFrame(line) : frame, // the Inbox look (FloatingBar.InboxLine.cs, task 740a2231)
            Padding = new Thickness(0),
            // A group's first line has a little space above it (see the header: the groups are not named on the bar).
            // Otherwise one DIP between lines, below each (was 1 above and 1 below), so two tinted lines still part.
            Margin = new Thickness(0, line.StartsGroup ? GroupGap : 0, 0, 1),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Template = TaskLineTemplate,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch, // explicit: Fluent's Button style says Left (see TasksWindow.Line)
            Focusable = false,
            Cursor = Cursors.Hand,
            ToolTip = line.Closing ? "Closed. Undo brings it back." : TipOf(line),
        };
        AutomationProperties.SetName(button, line.Title);
        // The click guard, as on the tick and the close button (TaskClickIgnored); and the press that may become a drag
        // of the line (FloatingBar.TaskDrag.cs), armed only, so a press released in place stays this Click.
        button.PreviewMouseLeftButtonDown += (_, e) =>
        {
            // A press inside the line's own editor is the editor's: selecting text must not drag the line (review, I-1).
            if (editingTask == line.Id) return;
            taskPressClicks = e.ClickCount;
            if (taskLinesPanel is not null) TaskDragPress(line.Id, ArrangeMouseInput.Position(e, taskLinesPanel));
        };
        // Right-click: the line's menu, Edit first (FloatingBar.TaskEdit.cs).
        WireTaskLineMenu(button, line);
        button.Click += (_, _) =>
        {
            MarkPressConsumed();
            // RULING 54 (Petre: "clicking a task should open that task; double clicking takes me to the workspace and
            // window"): a single click opens the task in the tasks window and goes nowhere; the double-click goes, which
            // is what a single click did before. A closed line does neither: its task is closed, and Undo is the one
            // thing on it to press. A line being edited neither: the click is in its editor. Reading the press's click
            // count consumes it (DoubleClickIgnored's way). The repaint grace (TaskClickIgnored) still drops a single
            // click aimed at a line that has just moved (#48), but never the second click of a double-click, which comes
            // within the half second by definition and was aimed at the line the first one landed on.
            var doubled = taskPressClicks > 1;
            taskPressClicks = 0;
            if (line.Closing || editingTask == line.Id) return;
            // A DOUBLE-CLICK GOES ONLY TO THE LINE ITS FIRST CLICK LANDED ON (#48's class: a reshuffle between the two
            // presses puts another task under the pointer, and the second press would send him to it). The id the last
            // single click landed on is remembered whether or not it acted (the repaint grace may have dropped it);
            // a second press on any other line is ignored.
            if (doubled)
            {
                if (lastClickedTask == line.Id)
                {
                    lastClickedTask = null;
                    OnTaskLineGo(line);
                }
                return;
            }
            lastClickedTask = line.Id;
            if (TaskClickClock() - taskLinesChangedAt >= RepaintGrace) OnTaskLineOpen(line);
        };
        return button;
    }

    // The group line's tag, which tells it from a task line (whose tag is its TaskLine) wherever the drawn buttons are read.
    internal const string WaitingGroupTag = "task-waiting-group";

    // THE WAITING ON YOU GROUP LINE (task 39f4cc8a, FoldWaiting): "+N more waiting on you", drawn like a task line (the
    // same template, so it lights up under the pointer the same way, and the same 3 DIP edge, clear here) so the block
    // reads as one block ending in it. Its words are in the asking orange when a question is among the folded tasks,
    // as "+N waiting" is (no question out of sight without a sign), else in the dim ink. The tooltip names every folded
    // task, so a glance tells what is behind it without opening anything. A click opens the tasks panel, where the whole
    // block is (OpenTasksFromGroup); no right-click menu and no drag, since it is not a task.
    Button WaitingGroupButton(WaitingFold fold)
    {
        var text = new TextBlock
        {
            Tag = "task-waiting-group-text",
            Text = WaitingGroupText(fold),
            FontSize = Text(9.5),
            TextTrimming = TextTrimming.CharacterEllipsis,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            LineHeight = TaskTextLineHeight,
            Foreground = fold.Questions > 0 ? TaskOrange : TaskDimInk,
            Margin = new Thickness(4, 0, 4, 0),
        };
        var edge = new Border
        {
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = Brushes.Transparent,
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 1, 5, 1),
            Child = text,
        };
        // The selection frame's 2 DIP, clear (the group line is never selected), so its words line up with the lines'.
        var frame = new Border
        {
            BorderThickness = new Thickness(2, 0, 0, 0),
            BorderBrush = Brushes.Transparent,
            CornerRadius = new CornerRadius(4),
            Child = edge,
        };
        var titles = fold.Folded.Select(l => $"{(l.Status == TaskStatus.Done ? "Done: " : "")}{FirstLine(l.Title)}");
        var button = new Button
        {
            Tag = WaitingGroupTag,
            Content = InboxBarOn ? InboxGroupFrame(fold) : frame, // the Inbox look (FloatingBar.InboxLine.cs)
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 0, 1),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Template = TaskLineTemplate,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Focusable = false,
            Cursor = Cursors.Hand,
            ToolTip = string.Join("\n", titles.Prepend(ToggleTasksPanel is not null
                ? "Open the tasks panel to see them all:" : "Show them all:")),
        };
        AutomationProperties.SetName(button, "More waiting tasks");
        button.Click += (_, _) =>
        {
            MarkPressConsumed();
            // The click guard, as on a task line: a click landing within half a second of the lines moving was aimed at
            // whatever sat here before (#48).
            if (TaskClickClock() - taskLinesChangedAt < RepaintGrace) return;
            OpenTasksFromGroup();
        };
        return button;
    }

    // The group line's click: the tasks panel opened, never closed (Petre: "clicking the group ... should open the tasks
    // bar"), so a click while it is shown leaves it there; the "+N more" button keeps toggling it. With no panel behind
    // the bar (its tests), the list expands in place as "+N more" does, which unfolds the block (PaintTaskList).
    void OpenTasksFromGroup()
    {
        if (ToggleTasksPanel is not null && tasksPanelOn) return;
        OnTaskMoreClick();
    }

    // Left of the text: nothing, or "Undo" on a line just closed. No CLAUDE tag since Ruling 42 (Petre: "don't say
    // claude"): the orange edge at the line's start is the mark. No VS Code icon since Ruling 47 (it was the same on every
    // Claude line and told him nothing).
    //
    // NO TICK BOX (task acd76fbe, Petre: "remove the checkbox and utilize the second row fully; clicking a task will open
    // it in the tasks list"). The box was on every line since Ruling 40; it took 13 DIP of every line and a misplaced
    // click on it closed a task. A task is closed from its line's right-click menu (Close, with the same six seconds of
    // Undo, FloatingBar.TaskEdit.cs) or in the tasks panel, which a single click on the line opens on that task
    // (OnTaskLineOpen). The title now starts at the line's left edge.
    IEnumerable<UIElement> LeftOf(TaskLine line) =>
        line.Closing ? [UndoButton(line)] : [];

    // The workspace as a dot in its colour, for a line too narrow for the chip (TaskLineFace): its name in the tooltip,
    // and the chip's click (the move menu) on a line that is not closing.
    UIElement WorkspaceDot(TaskLine line)
    {
        var name = line.WorkspaceName ?? "none";
        var dot = new System.Windows.Shapes.Ellipse
        {
            Tag = "task-workspace-dot",
            Width = 8,
            Height = 8,
            Fill = line.WorkspaceName is null ? TaskNoneFill : line.Colour ?? TaskButtonFill,
            Margin = new Thickness(5, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = $"On {name}",
        };
        if (line.Closing) return dot;
        Button? button = null;
        button = TaskButton("Workspace dot", dot, () => OpenTaskMoveMenu(line.Id, button), $"On {name}: move to another workspace",
            ignoredWhen: TaskClickIgnored);
        return button;
    }

    // The space above a group's first line, in DIP: enough to see a break, little enough to keep the bar compact.
    const double GroupGap = 6;

    // Undo on a line just closed: a sign, not a word (Petre: "undo sign instead of text"), the plain half-circle back arrow
    // U+21B6 (Ruling 51; the Segoe Fluent glyph E7A7 it replaced reads as a hook), in the plain bright ink, about the tick
    // box's size and in its place.
    // Its automation name and tooltip say "Undo".
    Button UndoButton(TaskLine line) =>
        TaskButton("Undo", UndoSign(Text(8.5), TaskInk), () => UndoClose(line.Id), "Undo", ignoredWhen: DoubleClickIgnored);

    internal static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    // The undo arrow's own font: Segoe UI Symbol has the glyph, Segoe UI is the fallback (Ruling 51).
    static readonly FontFamily UndoFont = new("Segoe UI Symbol, Segoe UI");

    internal static TextBlock UndoSign(double size, Brush ink) => new()
    {
        Tag = "undo-sign",
        Text = "↶",
        FontFamily = UndoFont,
        FontSize = size,
        Foreground = ink,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(1, 0, 1, 0),
    };

    // The title, then a dot and the detail. On Claude's lines the title is the tan of the mockup's session title and
    // the detail is the bright text, since the detail (the question, the status line) is the news; on a note the
    // title is the news. A question waiting on Petre is orange either way.
    //
    // TWO LINES AT MOST (Ruling 41, which overrides Ruling 38's "the bar stays one line"; Petre: "give long tasks two
    // lines, when needed"). The text wraps onto a second line when it does not fit, and an ellipsis ends the second when
    // even two are not enough; a short one stays one line. The line height is fixed (BlockLineHeight), so two lines are
    // exactly 2 x TaskTextLineHeight and MaxHeight cuts at the second line's foot. The chip stays in the first line's row
    // (TaskLineFace.FirstRow), and the second line runs on under it to the line's end (TaskLineFace, TitleRows; task
    // acd76fbe). The tooltip keeps the whole text.
    TextBlock TextOf(TaskLine line)
    {
        var lineHeight = TaskTextLineHeight;
        var text = new TextBlock
        {
            Tag = "task-text",
            FontSize = Text(9.5),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            LineHeight = lineHeight,
            MaxHeight = 2 * lineHeight,
            VerticalAlignment = VerticalAlignment.Top,
            // No space on the left with nothing before the title (the tick box is gone, task acd76fbe); Undo keeps it.
            Margin = new Thickness(line.Closing ? 4 : 0, 0, 4, 0),
        };
        // A closed line in its undo window is struck through.
        if (line.Closing) text.TextDecorations = TextDecorations.Strikethrough;
        var detailInk = line.Status == TaskStatus.WaitingOnYou ? TaskOrange : line.Claudes ? TaskInk : TaskDimInk;
        // Only the title's first line (Ruling 38: titles may hold line breaks, written in the tasks window's editor); the
        // tooltip has the whole title.
        // SUBTASKS (Petre's pick A): a subtask's line starts with "↳" alone, since the bar has no room for the parent's
        // name (the tooltip has it); a parent's title is followed by its open subtasks' count. Both in the faint ink.
        if (line.ParentTitle is not null)
            text.Inlines.Add(new Run(TaskViews.Breadcrumb(line.ParentTitle, full: false)) { Foreground = TaskFaintInk });
        // A DONE TASK'S TICK (Petre, 2026-11-23, "build both"; TaskStatusMarks): a green disc in front of the title, so a
        // finished Claude task, waiting for Petre to close it, no longer reads like work in progress. Sized to the text,
        // centred on it, inline so it wraps with the title. Not on a line in its undo window (it is closed, struck through).
        // AFTER THE SUBTASK ARROW, never before it (task 9e85caee, Petre: "what's up with research being nested further
        // than the other tasks?"): in front of the arrow it pushed a done subtask's "↳" a tick's width to the right of its
        // siblings', which reads as one more level of nesting. The arrow says where the line sits; the tick is the title's.
        // HOLLOW UNTIL IT RUNS (task 8470d176): a Done task whose change is not in this Deskbar yet gets the ring
        // (TaskStatusMarks.DoneTick), which fills into the disc once the running build holds it.
        // MERGED OR RUNNING BEFORE DONE (task 1afbd44f): a task waiting on a redeploy, or still in progress after its merge,
        // gets the same ring or disc (TaskStatusMarks.ShowsTick).
        if (TaskStatusMarks.ShowsTick(line.Status, line.Delivery) && !line.Closing)
        {
            var tick = TaskStatusMarks.DoneTick(Math.Round(Text(9.5)), line.Delivery.Shipping, line.Delivery.Unchecked);
            tick.Margin = new Thickness(0, 0, 4, 0);
            text.Inlines.Add(new InlineUIContainer(tick) { BaselineAlignment = BaselineAlignment.Center });
        }
        // RULING 59: important is bold and urgent is italic, in the title only (not the detail after it).
        text.Inlines.Add(new Run(FirstLine(line.Title))
        {
            Foreground = line.Claudes ? TaskTitleInk : TaskInk,
            FontWeight = line.Important ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = line.Urgent ? FontStyles.Italic : FontStyles.Normal,
        });
        if (line.SubtaskCount > 0)
            text.Inlines.Add(new Run(TaskViews.SubtaskCountSeparator + TaskViews.SubtaskCountText(line.SubtaskCount)) { Foreground = TaskFaintInk });
        if (line.Detail.Length == 0) return text;
        text.Inlines.Add(new Run(TaskDetailSeparator) { Foreground = line.Claudes ? TaskTitleInk : TaskDimInk });
        text.Inlines.Add(new Run(line.Detail) { Foreground = detailInk });
        return text;
    }

    // One line of a task's text: its font's own spacing at the text size, rounded up to a whole DIP.
    double TaskTextLineHeight => Math.Ceiling(Text(9.5) * TaskTextFont.LineSpacing);

    static readonly FontFamily TaskTextFont = SystemFonts.MessageFontFamily;

    /// <summary>A title's first line: the bar shows one line of a title that holds line breaks.</summary>
    internal static string FirstLine(string title) => title.Split('\n')[0].TrimEnd('\r');

    // The workspace's chip in its row's colour (the colour the rebuild noted for that row), or a grey "none".
    // THE CHIP MOVES THE TASK (Petre, at the live check: "make workspace changeable"). A button like the tick, with the
    // tick's click guard, and its click marked handled (TaskButton), so it never also goes to the workspace; it opens the
    // workspace menu (OpenTaskMoveMenu). A line in its undo window has a plain chip: its task is closed.
    //
    // The chip is docked before the title fills what is left (TaskLineButton), and its name is trimmed under a ceiling,
    // so the title trims first and the chip stays whole (Petre's screenshot had "Deskba" cut off at the line's end;
    // A_long_title_trims_and_leaves_the_chip_whole pins it at the narrowest bar and the largest text size).
    UIElement WorkspaceChipButton(TaskLine line)
    {
        var chip = WorkspaceChip(line);
        if (line.Closing) return chip;
        Button? button = null;
        button = TaskButton("Move to workspace", chip, () => OpenTaskMoveMenu(line.Id, button), "Move to another workspace",
            ignoredWhen: TaskClickIgnored);
        button.Margin = new Thickness(4, 0, 0, 0);
        return button;
    }

    // The task's workspace menu: every workspace as its chip, in the bar's order and grouped as on the bar, with none
    // first and the task's own ticked (WorkspaceChipMenu); a pick moves the task through TaskService.MoveTo, the same
    // move the tasks window and `task move` make. Held open the way the bar's menus are (HoldFadeWhileOpen): the bar
    // neither fades nor rebuilds under it (#77), and the paint the move causes waits for it to close
    // (TaskListMustWait). Internal, and answering the menu, for the tests.
    internal ContextMenu? OpenTaskMoveMenu(Guid id, UIElement? anchor = null)
    {
        if (taskService?.Board.Tasks.FirstOrDefault(t => t.Id == id) is not { } task) return null;
        var menu = WorkspaceChipMenu.Build(WorkspaceChipMenu.Entries(manager.State, withNone: true), task.WorkspaceId,
            workspace => taskService?.MoveTo(id, workspace).TapError(err => ClickTrace.Write($"task list: not moved: {err}")),
            anchor, ChipMenuWidth);
        HoldFadeWhileOpen(menu);
        TaskMoveMenu = menu;
        menu.IsOpen = true;
        return menu;
    }

    // The workspace menus are about 260 DIP wide, or the bar's own width when it is narrower (WorkspaceChipMenu).
    double ChipMenuWidth => ActualWidth > 0 ? Math.Min(WorkspaceChipMenu.PreferredWidth, ActualWidth) : WorkspaceChipMenu.PreferredWidth;

    // The last workspace menu a chip opened, for the tests.
    internal ContextMenu? TaskMoveMenu { get; private set; }

    Border WorkspaceChip(TaskLine line) => new()
    {
        Tag = "task-workspace",
        Background = line.WorkspaceName is null ? TaskNoneFill : ChipFill(line.Colour),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(7, 0, 7, 1),
        Margin = line.Closing ? new Thickness(4, 0, 0, 0) : new Thickness(0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = line.WorkspaceName ?? "none",
            FontSize = Text(7.5),
            Foreground = line.WorkspaceName is null ? TaskNoneInk : TaskInk,
            MaxWidth = ChipTextCeiling,
            TextTrimming = TextTrimming.CharacterEllipsis,
        },
    };

    Brush ChipFill(Brush? accent)
    {
        if (accent is not SolidColorBrush { Color: var colour }) return TaskButtonFill;
        if (taskChipFills.TryGetValue(colour, out var known)) return known;
        var fill = Frozen(0x66, colour.R, colour.G, colour.B);
        taskChipFills[colour] = fill;
        return fill;
    }

    static string TipOf(TaskLine line) =>
        string.Join("\n", new[]
        {
            $"{TaskViews.StatusName(line.Status)}: {line.Title}",
            // Where a Done task's change stands against this Deskbar (task 8470d176): "Merged in Deskbar #573, not in
            // this Deskbar (2.6.23) yet; ..." or "Running in this Deskbar (2.6.23).", nothing when it cannot be told.
            line.Delivery.Note,
            line.Detail,
            line.Quadrant == Quadrant.IV ? "" : $"{line.Quadrant.Numeral()}: {line.Quadrant.Words()}",
            line.WorkspaceName is { } name ? $"On {name}" : "On no workspace",
            // When it was made: the bar's line shows no age since 2026-11-24 (the tasks panel does), so this is where it is.
            line.Created == default ? "" : $"Created {line.Created.ToLocalTime():dddd d MMMM yyyy, HH:mm}",
            line.GoalTitle is { } goal ? $"Goal: {goal}" : "",
            line.ParentTitle is { } parent ? $"Subtask of {parent}" : "",
            line.SubtaskCount > 0 ? $"Has {TaskViews.SubtaskCountText(line.SubtaskCount)}" : "",
        }.Where(part => part.Length > 0));

    // The failed-load line: the words, the store's reason as the tooltip, and a ✕ that dismisses it.
    UIElement LoadProblemRow(string reason)
    {
        var dismiss = TaskButton("Dismiss", new TextBlock { Text = "✕", Foreground = TaskInk, FontSize = Text(8.5), Margin = new Thickness(5, 0, 5, 0) }, () =>
        {
            taskService?.DismissLoadProblem();
            PaintTaskList();
        }, "Dismiss");
        DockPanel.SetDock(dismiss, Dock.Right);
        return new DockPanel
        {
            LastChildFill = true,
            Margin = new Thickness(3, 1, 0, 2),
            Children =
            {
                dismiss,
                new TextBlock
                {
                    Tag = "task-load-problem",
                    Text = LoadProblemLine(reason),
                    ToolTip = reason,
                    Foreground = TaskOrange,
                    FontSize = Text(9),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
    }

    // --- clicks ----------------------------------------------------------------------------------------------------

    // A DOUBLE-CLICK on a line goes to its task (Ruling 54). A note on no workspace has nowhere to go; a Claude task goes
    // even on no workspace, to its session, which the App decides (ClaudeTaskActions.Go).
    void OnTaskLineGo(TaskLine line)
    {
        // The click picks the task, in both views, whether or not it has anywhere to go.
        selection.Select(line.Id);
        var goes = taskService?.Board.Tasks.TryFirst(t => t.Id == line.Id).Where(t => t.IsClaudes || t.WorkspaceId.HasValue).HasValue ?? false;
        if (!goes) return;
        // Shown the way a row's failed switch is (Report), not only traced: a click that goes nowhere must say why.
        Report(GoToTask(line.Id));
    }

    // A SINGLE CLICK on a line opens that task in the tasks window (Ruling 54): picked, scrolled into view and expanded
    // to its comment thread (OpenTaskInWindow, the App's TasksBoard.ShowTask). It goes nowhere. Without the seam (a bar
    // with no window) it only picks the task.
    void OnTaskLineOpen(TaskLine line)
    {
        selection.Select(line.Id);
        OpenTaskInWindow?.Invoke(line.Id);
    }

    /// <summary>A line's single click: the tasks window on that task (App: TasksBoard.ShowTask). Unset: nothing opens.</summary>
    internal Action<Guid>? OpenTaskInWindow { get; init; }

    // THE UNDO WINDOW (Petre: "when i click on done, there should be undo"; Ruling 10). The task is closed at once
    // through Close, so the store is the truth whatever happens next; its line stays in its place for six seconds,
    // struck through, with "Undo" where the tick or the close button was, and then leaves and the list settles.
    // The line is pending before Close pulses Changed, so the repaint that pulse causes already draws it struck
    // through rather than dropping it for a moment. A refused Close leaves nothing pending and says why (Report): the
    // task was closed meanwhile, or it is a parent whose subtasks are not all closed yet (task 842e1ba4), which
    // without the message would look like a tick that does nothing. Several lines can be pending at once, each with
    // its own timer.
    //
    // CLAUDE STILL AT IT (task 37cd7081). A task Claude is still working on (open, neither Done nor waiting on Petre, a live session; task 3cdd0a52) does not close
    // on a tick: closing ends no session, and the session would be left running with nowhere to report. The tick, and
    // the line menu's Close, open the stop menu instead (OpenStopClaudeMenu), whose one item closes with `stopClaude`:
    // through StopClaudeAndClose (the App's ClaudeTaskActions.StopAndClose, which closes and then stops the sessions),
    // with the same undo window as any close.
    //
    // NOT FINISHED (task 579f2e4c, TaskService.Unfinished). A task that still asks something or has a live session, and
    // whose work is not merged, does not close on a tick either: Petre ticked off a waiting task with a question open and
    // only Undo saved it. The tick opens the "Close anyway" menu (OpenUnfinishedMenu), whose one item closes with
    // `anyway`, with the same undo window.
    void CloseWithUndo(TaskLine line, bool stopClaude = false, bool anyway = false)
    {
        if (taskService is null) return;
        // OPEN SUBTASKS FIRST (task 452487b4): nothing gets past them, not "Stop Claude and close" and not "Close anyway",
        // so a parent with an open subtask is refused at once, naming the subtask, rather than offering a menu whose
        // every item is then refused. The board's own Close checks them first too.
        if (taskService.Board.OpenSubtaskCount(line.Id) > 0)
        {
            Report(taskService.Close(line.Id));
            return;
        }
        if (!stopClaude && ClaudeStillWorking(line.Id))
        {
            OpenStopClaudeMenu(line);
            return;
        }
        if (!stopClaude && !anyway && taskService.Board.Tasks.FirstOrDefault(t => t.Id == line.Id) is { } task
            && taskService.Unfinished(task))
        {
            OpenUnfinishedMenu(line, TaskViews.UnfinishedHeader(task));
            return;
        }
        // The place among the lines BEFORE the Waiting on you fold (unfoldedTaskLines), the list WithPendingCloses puts
        // it back into: counted among the lines drawn, a line below a group line of six would land six places too high.
        var index = unfoldedTaskLines.ToList().FindIndex(l => l.Id == line.Id);
        var pending = new PendingClose(line with { Closing = true }, Math.Max(index, 0), line.Status, Disposable.Empty)
        {
            Question = taskService.Board.Tasks.TryFirst(t => t.Id == line.Id).Bind(t => t.Question),
            Selected = selection.IsSelected(line.Id),
        };
        pending = pending with { Timer = TaskUndoTimer(UndoWindow, () => ForgetPendingClose(pendingCloses.FirstOrDefault(p => p.Line.Id == line.Id), repaint: true)) };
        pendingCloses.Add(pending);
        var closed = !stopClaude ? taskService.Close(line.Id, anyway: anyway)
            : StopClaudeAndClose is { } stop ? stop(line.Id)
            // No App behind the bar (the tests): the close alone, nothing to stop.
            : taskService.Close(line.Id, stoppingClaude: true);
        Report(closed.TapError(_ => ForgetPendingClose(pending, repaint: true)));
    }

    // Whether ticking the line off would leave Claude working on its task (TaskBoard.ClaudeStillWorking).
    bool ClaudeStillWorking(Guid id) =>
        taskService?.Board.Tasks.FirstOrDefault(t => t.Id == id) is { } task && TaskBoard.ClaudeStillWorking(task);

    /// <summary>
    /// "Stop Claude and close" for a task Claude is still working on (task 37cd7081): the App's
    /// ClaudeTaskActions.StopAndClose, which closes the task past the refusal and stops its background sessions. Unset (the
    /// tests), the menu's item closes the task the same way and stops nothing.
    /// </summary>
    internal Func<Guid, Result>? StopClaudeAndClose { get; init; }

    /// <summary>For the tests: the last stop menu a tick opened.</summary>
    internal ContextMenu? StopClaudeMenu { get; private set; }

    /// <summary>
    /// The menu a tick opens on a task Claude is still working on, at the pointer: a line saying so, which takes no click,
    /// and "Stop Claude and close". Held open the way the bar's menus are (HoldFadeWhileOpen). Internal, for the tests.
    /// </summary>
    internal ContextMenu OpenStopClaudeMenu(TaskLine line)
    {
        var menu = OwnMenus.AtPointer(this); // the bar as its target, for its screen's scaling
        menu.Items.Add(new MenuItem { Header = new TextBlock { Text = "Claude is still working on this task." }, IsEnabled = false });
        // Run once this menu has closed, so the line's undo paint is not held back by the menu still being open.
        menu.Items.Add(TaskMenuItem(TaskViews.StopClaudeAndClose, () => Dispatcher.BeginInvoke(() => CloseWithUndo(line, stopClaude: true),
            System.Windows.Threading.DispatcherPriority.Input)));
        HoldFadeWhileOpen(menu);
        StopClaudeMenu = menu;
        menu.IsOpen = true;
        return menu;
    }

    /// <summary>For the tests: the last "Close anyway" menu a tick opened (task 579f2e4c).</summary>
    internal ContextMenu? UnfinishedMenu { get; private set; }

    /// <summary>
    /// The menu a tick opens on a task that is not finished (task 579f2e4c), at the pointer: <paramref name="header"/>
    /// (TaskViews.UnfinishedHeader), which takes no click and says what the close would drop, and "Close anyway". Esc or
    /// a click elsewhere leaves the task open. Held open the way the bar's menus are (HoldFadeWhileOpen). Internal, for
    /// the tests.
    /// </summary>
    internal ContextMenu OpenUnfinishedMenu(TaskLine line, string header)
    {
        var menu = OwnMenus.AtPointer(this); // the bar as its target, for its screen's scaling
        menu.Items.Add(new MenuItem { Header = new TextBlock { Text = header }, IsEnabled = false });
        // Run once this menu has closed, so the line's undo paint is not held back by the menu still being open.
        menu.Items.Add(TaskMenuItem(TaskViews.CloseAnyway, () => Dispatcher.BeginInvoke(() => CloseWithUndo(line, anyway: true),
            System.Windows.Threading.DispatcherPriority.Input)));
        HoldFadeWhileOpen(menu);
        UnfinishedMenu = menu;
        menu.IsOpen = true;
        return menu;
    }

    // The undo window is over (or Undo was pressed, or the task came back another way): the line leaves.
    void ForgetPendingClose(PendingClose? pending, bool repaint)
    {
        if (pending is null || !pendingCloses.Remove(pending)) return;
        pending.Timer.Dispose();
        if (repaint) PaintTaskList();
    }

    /// <summary>
    /// Letting go of the strip (FloatingBar.LetGoOfStrip): the list goes back to three lines and is drawn so BEFORE the
    /// bar's first layout as a floating bar, so that layout, and the position the undock saves from it, see the bar's
    /// floating height and not the docked list's (review I-1 of 187cf4a).
    /// </summary>
    internal void FoldTaskListForFloating()
    {
        taskListSlots = TaskListSlots;
        lastTaskListShape = null;
        PaintTaskList(evenUnderPress: true);
    }

    // Undo: the task comes back exactly as it was (Ruling 31), through TaskBoard.UndoClose: the status it had before the
    // close, its question if it waited on Petre, its place (a reopening would put it on top, but the line never left the
    // screen during its six seconds), and the selection if it was the line selected.
    void UndoClose(Guid id)
    {
        if (taskService is null || pendingCloses.FirstOrDefault(p => p.Line.Id == id) is not { } pending) return;
        ForgetPendingClose(pending, repaint: false);
        var reopened = taskService.UndoClose(id, pending.Was, pending.Question);
        // The line Petre had picked is picked again: the close took the selection away (ruling 21), the undo gives it back.
        if (reopened.IsSuccess && pending.Selected) selection.Select(id);
        // Reopen pulses Changed, which repaints; a refusal (the task is gone) still needs the line taken away.
        reopened.TapError(_ => PaintTaskList());
    }

    // "+N more": the tasks panel shown or hidden when the App gave one, else the list expanded in place, and folded back
    // on the next click.
    void OnTaskMoreClick()
    {
        if (ToggleTasksPanel is { } toggle)
        {
            toggle();
            return;
        }
        taskListExpanded = !taskListExpanded;
        PaintTaskList();
    }

    // A CLICK IN THE BOX takes the keyboard for the bar the way quick find's box does (QuickFindWindow.Open: Activate,
    // then focus the box), since a key goes to the foreground window and the bar may not be it when the click lands.
    // The chips show, the current workspace ticked unless the box still holds text from before, which keeps the chip
    // it was given.
    //
    // First it notes where Petre was typing, to hand the keyboard back there after Enter or Esc (GiveTaskKeyboardBack):
    // the window in front at the click, when it is one you type in; failing that (the click has usually made the bar
    // the window in front already), the last such window the foreground hook saw (languageTarget), but only while it
    // is on the desktop in front. A window on another desktop is never chosen: giving it the foreground would switch
    // desktops under Petre (PR A review, Minor 8).
    void BeginTyping()
    {
        if (taskService is null || taskAddBox is null) return;
        var front = ForegroundWindow();
        FollowForeground();
        taskReturnTo = IsTypingWindow(front, isFlyout: ShellKeys.Recognise(front) is not null || (front != 0 && front == trayOverflow))
            ? front
            : languageTarget != 0 && OnCurrentDesktop(languageTarget) ? languageTarget : 0;
        taskAddHere = manager.CurrentWorkspaceId;
        // A subtask box keeps its parent's workspace, empty or not (final review, finding 2): the subtask lands with its parent.
        // A workspace picked by hand stays too, but only for a note still in the box; any other target is the workspace
        // in front, text in the box or not (task 701c71a8). ChooseTaskTarget sets its pick after this call, so it lands.
        ForgetPickOnEmptyBox();
        if (TaskAddTargetFollowsFront) taskAddTarget = taskAddHere;
        taskTyping = true;
        PaintTaskChips();
        PaintAddRow();
        UpdateFade();
        Activate();
        taskAddBox.Focus();
    }

    // The box lets go of the keyboard. After Enter or Esc (`clear`) it empties, the chip goes back to the current
    // workspace and the keyboard goes back to where Petre was typing. After a click elsewhere (the bar lost the
    // foreground) the text stays as it is, with its chip, for whenever he comes back to it (Ruling 9: the box itself
    // is the draft); nothing is handed back, since the keyboard is already where he took it. `handBack` false keeps the
    // keyboard (and the window it goes back to, taskReturnTo) for later: the add that starts Claude hands it back only
    // once "Start Claude on this" has had its say (StartAddedTask).
    void EndTyping(bool clear, bool handBack = true)
    {
        if (taskAddBox is null) return;
        taskTyping = false;
        if (clear)
        {
            taskAddBox.Text = "";
            taskAddTarget = taskAddHere;
            // The next note starts from the workspace in front again, not from this one's pick (task 701c71a8).
            taskAddTargetPicked = false;
            // The toggles are off again after an add (and after Esc): each note says its own flags (Ruling 37).
            taskAddFlags = (false, false);
            // A subtask's parent is for that one add (subtasks spec): the next note is a plain one again.
            taskAddParent = Maybe<Guid>.None;
            // The pasted pictures go with the text (FloatingBar.TaskPictures.cs).
            ClearTaskAddPictures();
            PaintAddFlags();
            PaintTaskChips();
            if (handBack) GiveTaskKeyboardBack();
        }
        else
        {
            taskReturnTo = 0;
            // A click elsewhere with nothing in the box drops a pick made for it, so the idle box follows the workspace
            // in front again instead of filing a note typed hours later on another desktop to the old pick (review of
            // task 701c71a8, finding 1).
            ForgetPickOnEmptyBox();
        }
        PaintAddRow();
        UpdateFade();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (taskTyping) EndTyping(clear: false);
        // A click into another app abandons an edit in progress; the keyboard is already where he took it.
        if (editingTask is not null) CancelTaskEdit(handBack: false);
    }

    // The placeholder as the box stands: shown while the box is empty. (The chips under it are always shown.)
    void PaintAddRow()
    {
        if (taskAddBox is null || taskAddPlaceholder is null) return;
        taskAddPlaceholder.Visibility = taskAddBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // On every paint the "here" chip is the workspace in front, and the chosen chip follows it (the placeholder with it,
    // so both follow a desktop switch) unless the chip was picked by hand or the box is adding a subtask: those keep
    // their workspace, text in the box or not. Before task 701c71a8 a box holding text, or one being typed in, kept
    // whatever workspace it opened on, so a note begun on one desktop and sent after switching to another went to the
    // first. The placeholder always names the chosen chip's workspace (PaintPlaceholder).
    void FollowCurrentWorkspace()
    {
        if (taskAddPlaceholder is null || taskAddBox is null) return;
        var current = manager.CurrentWorkspaceId;
        taskAddHere = current;
        if (TaskAddTargetFollowsFront) taskAddTarget = current;
        PaintTaskChips();
    }

    // Whether the add box's target is only the default and so follows the workspace in front: nothing was picked from
    // the chip's menu for this note, and it is not a subtask (a subtask lands with its parent; final review, finding 2).
    bool TaskAddTargetFollowsFront => !taskAddTargetPicked && taskAddParent.HasNoValue;

    // A pick belongs to the note it was made for. With no text, no pasted picture and no subtask parent there is no such
    // note any more, so the pick is dropped and the target goes back to following the workspace in front.
    void ForgetPickOnEmptyBox()
    {
        if (taskAddBox?.Text.Length is null or 0 && taskAddImages.Count == 0 && taskAddParent.HasNoValue) taskAddTargetPicked = false;
    }

    // The window noted when the box opened gets the foreground back, the way the language switcher hands it back
    // (HandForegroundTo), so Petre's next keystrokes land where he was typing rather than in a bar with nothing to
    // type into. Only a window that still exists, is not one of Deskbar's own (the bar included) and is still on the
    // desktop in front: Petre may have switched desktops while the box was open, and handing the keyboard to a window
    // left behind would switch him back.
    void GiveTaskKeyboardBack()
    {
        var back = taskReturnTo;
        taskReturnTo = 0;
        if (back == 0 || IsOwnWindow(back) || !WindowAlive(back) || !OnCurrentDesktop(back)) return;
        keyboardHandedBack = (back, TaskClickClock());
        HandForegroundTo(back);
    }

    // THE KEYBOARD GOING BACK IS NOT A PICK (task ee75f046). The App selects the task of a Claude terminal that comes to
    // the front (task 4846fde1). After an add, the window the keyboard goes back to is very often that terminal: Petre
    // types a note in the bar while talking to a session, presses Enter, and the terminal's foreground event arrived a
    // quarter of a second after the note was selected and selected the terminal's task instead. Measured on his bar with
    // the add's trace: "highlighted b6cefb60 ... selection=b6cefb60", then "settled ... selection=ee75f046" 230 ms
    // later, his session's own task. Live: "it kind of highlights it and then immediately loses focus". The bar moved
    // that window to the front, not Petre, so its foreground event must not pick a task.
    //
    // The bar notes the window and when it handed the keyboard back; the App asks KeyboardHandedBackTo once for each
    // foreground change before selecting a terminal's task. The note is spent by the first foreground event for that
    // window, and it lapses after HandBackWindow even if none comes (the window was already in front), so a terminal
    // Petre brings forward himself later still selects its task.
    (nint Window, DateTime At) keyboardHandedBack;

    static readonly TimeSpan HandBackWindow = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Whether <paramref name="hwnd"/> coming to the front is the bar handing the keyboard back after its add box
    /// (see above), in which case it picks no task. Answers yes once per hand-back, within <see cref="HandBackWindow"/>.
    /// </summary>
    internal bool KeyboardHandedBackTo(nint hwnd)
    {
        var (window, at) = keyboardHandedBack;
        if (hwnd == 0 || window != hwnd) return false;
        keyboardHandedBack = default;
        return TaskClickClock() - at < HandBackWindow;
    }

    // How many lines the add box grows to before it scrolls inside instead (task 98420dbc): enough for a short paragraph,
    // few enough that the docked bar keeps room for its task lines.
    const int TaskAddMaxLines = 6;

    void OnTaskAddKey(object sender, KeyEventArgs e)
    {
        // Shift+Enter is a line break, left to the box (AcceptsReturn); the tasks panel's comment and description boxes
        // read the two keys the same way.
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            // A held Enter repeats; only the first press counts, so holding it a moment too long cannot also pick from
            // the workspace menu that press opens (AskTaskTarget).
            if (e.IsRepeat) return;
            // Ctrl+Enter is the Start Claude button's key (task 3c40dec9): the note is added and handed to Claude.
            var startClaude = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            // With something to add, Enter first asks where it goes (task 6184e8bf); with nothing, the add is refused
            // exactly as before and no menu opens for an empty box.
            if (HasTaskNote) AskTaskTarget(startClaude);
            else CommitTaskNote(startClaude);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            // Subtasks: the first Esc only drops the parent, as the placeholder says, so the text becomes a plain note.
            if (taskAddParent.HasValue) DropTaskAddParent();
            else EndTyping(clear: true);
        }
    }

    // THE WORKSPACE ASKED AT ENTER (task 6184e8bf; Petre: "when adding a task, when i press enter, open a workspace
    // selection which i can select with the keyboard so that i don't accidentally create a task in the wrong
    // workspace"). The chip beside the box always said where a note would go, but nobody reads a chip while typing, and
    // a note sent from the wrong desktop landed on the wrong workspace without a word. So Enter (and Ctrl+Enter) no
    // longer adds at once: it opens the add chip's own workspace menu (WorkspaceChipMenu) under the chip, with the
    // keyboard on the workspace the note would go to now, the one in front unless a chip was picked or the note is a
    // subtask. A second Enter takes it, so the old habit is Enter, Enter; the arrows, Tab or the first letters of a name
    // (the menu's type-ahead) move to another, and Enter or a click picks that one. Either way the pick is made as a
    // chip press makes it (ChooseTaskTarget) and then the note is added (CommitTaskNote), Claude started on it when the
    // first press was Ctrl+Enter. Esc, or a click away, closes the menu and adds nothing: the text stays in the box,
    // with the keyboard, for another Enter or an edit.
    void AskTaskTarget(bool startClaude)
    {
        if (taskAddBox is null || taskAddChip is null) return;
        // The menu opens on the workspace the note would go to at this moment (CommitTaskNote's own rule), even if no
        // repaint has caught a desktop switch made since the box opened.
        if (TaskAddTargetFollowsFront) taskAddTarget = manager.CurrentWorkspaceId;
        PaintTaskChips();
        var picked = false;
        var menu = WorkspaceChipMenu.Build(WorkspaceChipMenu.Entries(manager.State, withNone: true), taskAddTarget, target =>
            {
                picked = true;
                TaskAddPickMenu = null;
                ChooseTaskTarget(target);
                CommitTaskNote(startClaude);
            },
            taskAddChip.Button, ChipMenuWidth);
        // Closed comes late: the popup hides at once but raises Closed from its destroy timer (up to 150 ms with the
        // menu fade on, measured in WPF's Popup.HideWindow), so for a pick it runs after the note was added. Only a menu
        // closed without one (Esc, a click away) puts the keyboard back in the box, and only while no newer menu is
        // open: Esc then a quick Enter opens a second menu before the first one's Closed, and taking the keyboard from
        // it would send its type-ahead letters into the note.
        menu.Closed += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (TaskAddPickMenu == menu) TaskAddPickMenu = null;
            if (!picked && taskTyping && TaskAddPickMenu is null) taskAddBox?.Focus();
        }, DispatcherPriority.Input);
        HoldFadeWhileOpen(menu);
        TaskAddPickMenu = menu;
        menu.IsOpen = true;
    }

    // Whether the box holds a note to add: words, or a pasted picture (a picture alone makes a "Picture" task).
    bool HasTaskNote => taskAddBox?.Text.Trim().Length > 0 || taskAddImages.Count > 0;

    /// <summary>The workspace menu Enter opened and nothing has been picked from yet, for the tests.</summary>
    internal ContextMenu? TaskAddPickMenu { get; private set; }

    // Enter: a Backlog note where the chips say. A title of nothing is refused by the board and the box keeps the
    // keyboard. The first line is the title and any lines after it the description (TaskNote.Split, task 98420dbc).
    // Pasted pictures go in the description too, after that text, and with nothing typed the task is called "Picture"
    // (FloatingBar.TaskPictures.cs). The note added is then highlighted (ShowAddedTask). With `startClaude` (the Start
    // Claude button or Ctrl+Enter, task 3c40dec9) the note is then handed to Claude (StartAddedTask). A `shot` (the
    // camera button, task 494d34ee) is one more picture after the pasted ones, with the line naming its window under the
    // typed words; it counts as a picture for the title, so a shot with nothing typed is a "Picture" task.
    void CommitTaskNote(bool startClaude = false, WindowShot.Shot? shot = null)
    {
        if (taskService is null || taskAddBox is null) return;
        var (title, body) = TaskNote.Split(taskAddBox.Text, taskAddImages.Count + (shot is null ? 0 : 1));
        var quadrant = QuadrantOf.Flags(taskAddFlags.Important, taskAddFlags.Urgent);
        // The workspace is settled at Enter, not when the box opened (task 701c71a8): unless Petre picked one by hand,
        // the note goes to the workspace in front now, even if no repaint has caught a desktop switch made since.
        if (TaskAddTargetFollowsFront) taskAddTarget = manager.CurrentWorkspaceId;
        (TaskAddDescription(body, shot) is { } describe
                ? taskService.AddNote(title, taskAddTarget, quadrant, taskAddParent, describe)
                : taskService.AddNote(title, taskAddTarget, quadrant, taskAddParent))
            .Tap(task =>
            {
                var starts = startClaude && AddedTaskStarter is not null;
                EndTyping(clear: true, handBack: !starts);
                // A note handed to Claude is not selected or scrolled to (task 7b17a573, Petre: "don't even take me away
                // from my current thread"): the selection is shared with the tasks panel, and moving it would scroll the
                // panel off the thread he has open. The "Claude started" card says where it went instead.
                if (starts) StartAddedTask(task.Id);
                else ShowAddedTask(task.Id);
            })
            .TapError(err => ClickTrace.Write($"task list: note not added: {err}"));
    }

    // ADD AND START CLAUDE (task 3c40dec9; Petre: "when adding a task, have a button to start immediately with claude").
    // Before this, a note meant for Claude took three steps: Enter, find its line, right-click and "Start Claude on this".
    // The button after the flag toggles (and Ctrl+Enter in the box) does all three: it adds the note exactly as Enter
    // does, then runs the line menu's "Start Claude on this" on it (StartClaude, the App's ClaudeTaskActions.Offer). So
    // everything that item does holds here too: it starts in the workspace's one folder, or opens the folder pick list
    // at the pointer, and a refusal (Claude sessions off, Claude Code missing) is told the same way. With nothing typed
    // and no picture, the board refuses the note as Enter's add is refused, and nothing starts. Shown only while the App
    // wired StartClaude, as the menu item is (PaintTaskChips, since the init property is set after this list is built).
    // The glyph is the tasks panel line's Start Claude button, Segoe's "Play" (U+E768), so the two read as one action.
    Button? taskAddStartClaude;

    internal const string AddAndStartClaudeText = "Add and start Claude";

    Button AddAndStartClaudeButton()
    {
        var glyph = new TextBlock
        {
            Text = "",
            FontFamily = IconFont,
            FontSize = Text(8.5),
            Foreground = TaskDimInk,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 1, 2, 1),
        };
        // Pressed on an idle box (a draft left in it), it takes the keyboard first, as a chip or a flag toggle does, so
        // the add hands it back to the window noted now (BeginTyping) and not to one noted long ago.
        // On an idle, empty box (a stray press, or the second press of a double-click after the first added the note)
        // there is nothing to add, so the press does nothing rather than take the keyboard for an empty box.
        var button = TaskButton(AddAndStartClaudeText, glyph, () =>
            {
                if (!taskTyping && taskAddBox?.Text.Length is null or 0 && taskAddImages.Count == 0) return;
                if (!taskTyping) BeginTyping();
                CommitTaskNote(startClaude: true);
            },
            "Add and start Claude (Ctrl+Enter): adds the task and starts a Claude session on it");
        button.Visibility = Visibility.Collapsed;
        return button;
    }

    // "Start Claude on this" on the note just added, run once the add's own work has settled (the paint, the selection),
    // as the line menu's item runs once its menu has closed. The keyboard is still the bar's (CommitTaskNote kept it):
    // handed back before this, the folder pick list would open while another app had the foreground, so its arrows and
    // Esc went to that app, and a refusal's message box could open behind it (review of task 3c40dec9). So a pick list,
    // when it opens, holds the bar still the way the bar's own menus do, and the Claude window it leads to takes the
    // front; otherwise (one folder started, or a refusal told) the keyboard goes back to where Petre was typing now.
    //
    // Since task 7b17a573 the start runs in the background (StartClaudeInBackground, the App's
    // ClaudeTaskActions.OfferInBackground): no Claude window opens, so with one folder the keyboard goes straight back to
    // where Petre was, and a "Claude started" card in the screen's corner (ClaudeStartedNotice) says so.
    void StartAddedTask(Guid id)
    {
        if (AddedTaskStarter is not { } start) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (start(id) is { } pick)
            {
                HoldFadeWhileOpen(pick);
                // In the background no Claude window comes to the front after the pick (review of task 7b17a573), so
                // the keyboard would stay in the bar's idle box and the next keystrokes meant for the terminal would land
                // there. Once the list has closed and whatever its item opened has had its turn (Background priority),
                // the keyboard goes back, but only while the bar itself still has it: "Choose a folder…" or a refusal's
                // message box in front keeps it.
                if (StartClaudeInBackground is not null)
                    pick.Closed += (_, _) => Dispatcher.BeginInvoke(() =>
                    {
                        if (ForegroundWindow() == new System.Windows.Interop.WindowInteropHelper(this).Handle) GiveTaskKeyboardBack();
                    }, DispatcherPriority.Background);
            }
            else GiveTaskKeyboardBack();
        }, DispatcherPriority.Input);
    }

    // For the tests: the add row's Start Claude button.
    internal Button? TaskAddStartClaudeButton => taskAddStartClaude;

    // THE NOTE JUST ADDED IS HIGHLIGHTED (task ee75f046; TaskGlow has the mockup and Petre's pick). A note lands at the top
    // of its quadrant, often below the lines on show, and before this nothing said where it went. It becomes the
    // selected task, in the bar and in the tasks panel alike (the one TaskSelection), and its line glows. The add's own
    // pulse has already drawn the line by now (TaskService.Changed runs PaintTaskList inline), so the selection's paint
    // finds it. Selecting is also what brings the line into sight when it lands out of view: a change of selection
    // scrolls the list to the selected line (the bar's task scrolling, task ea017420). The glow goes on after the
    // selection's paint, which would otherwise put the plain selected fill back over it.
    void ShowAddedTask(Guid id)
    {
        TraceAddedTask(id, "added");
        selection.Select(id);
        GlowTaskLine(id);
        TraceAddedTask(id, "highlighted");
        // Once everything the add queued has run (the docked bar's fit pass, the scroll's retry, layout), the same
        // again: what Petre is left looking at.
        if (ClickTrace.On) Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => TraceAddedTask(id, "settled"));
    }

    // THE ADDED NOTE'S TRACE. Petre, live on v2.5.412, adding from the bar: "doesn't work, test tasks were not
    // highlighted", while the panel's add box highlighted its task; the bar's tests, floating and docked, pass. One line
    // per step of ShowAddedTask says which part failed on his bar: whether the note's line is drawn at all, drawn as
    // selected, glowing and in sight, what the selection holds, and the gates that can put a paint off (a rebuild in
    // progress, TaskListMustWait). Only while tracing is on (ClickTrace).
    void TraceAddedTask(Guid id, string step)
    {
        if (!ClickTrace.On) return;
        var drawn = (taskLinesPanel?.Children.OfType<Button>() ?? []).Any(b => b.Tag is TaskLine line && line.Id == id);
        var current = selection.Current.Match(c => c.ToString()[..8], () => "none");
        ClickTrace.Write($"task list: {step} {id.ToString()[..8]} drawn={drawn} selected={TaskLineSelected(id)} " +
                         $"glowing={TaskLineGlowing(id)} inSight={LinesInSight().Any(l => l.Id == id)} selection={current} " +
                         $"rebuilding={rebuilding} mustWait={TaskListMustWait} lines={taskLinesPanel?.Children.Count ?? 0} " +
                         $"offset={taskLinesScroller?.VerticalOffset ?? -1:0} viewport={taskLinesScroller?.ViewportHeight ?? -1:0}");
    }

    // The glow on the selected line `id`, when it is on show (TaskGlow.Fading).
    void GlowTaskLine(Guid id) =>
        (taskLinesPanel?.Children.OfType<Button>() ?? [])
            .Where(b => b.Tag is TaskLine line && line.Id == id && selection.IsSelected(id))
            .Select(b => b.Content).OfType<Border>().ToList()
            .ForEach(frame => frame.Background = TaskGlow.Fading(SelectedLineFill));

    // For the tests: whether a line on show is glowing.
    internal bool TaskLineGlowing(Guid id) =>
        (taskLinesPanel?.Children.OfType<Button>() ?? []).Any(b => b.Tag is TaskLine line && line.Id == id
            && b.Content is Border frame && TaskGlow.IsGlow(frame.Background));

    // A chip, or a workspace picked from "other…"'s menu. The chips are always shown, so a chip can be the first thing
    // pressed, on an idle box or on the way back to text left in it (EndTyping keeps the text). That press must
    // take the keyboard exactly as a click in the box does (BeginTyping): otherwise the bar is focused but fades, has
    // nowhere to hand the keyboard back to after Enter or Esc, and does not notice losing the foreground again.
    // BeginTyping keeps the chip a box with text was given, so the choice made here is set after it.
    void ChooseTaskTarget(Maybe<Guid> target)
    {
        if (!taskTyping) BeginTyping();
        taskAddTarget = target;
        // A pick by hand stays put across a desktop switch until the note is added or dropped (task 701c71a8).
        taskAddTargetPicked = true;
        PaintTaskChips();
        taskAddBox?.Focus();
    }

    // FLAGS WHEN ADDING (Ruling 37; Petre: "i should be able to say urgent or important or both when adding"). The note
    // about to be added has the two flags a task line has, as toggles at the end of the chip row: neither, either or
    // both, picked before Enter, and the note lands at the top of the quadrant they give. Like a chip, a toggle pressed
    // on an idle box takes the keyboard first (BeginTyping). Off again after Enter or Esc (EndTyping).
    (bool Important, bool Urgent) taskAddFlags;
    TaskAddToggle? taskAddImportant;
    TaskAddToggle? taskAddUrgent;

    sealed record TaskAddToggle(Button Button, TextBlock Glyph, TaskFlag Flag);

    TaskAddToggle AddFlagToggle(TaskFlag flag)
    {
        var name = flag == TaskFlag.Important ? "Add as important" : "Add as urgent";
        var glyph = new TextBlock
        {
            Text = flag == TaskFlag.Important ? "\uE8C9" : "\uE823",
            FontFamily = IconFont,
            FontSize = Text(8.5),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 1, 2, 1),
        };
        var button = TaskButton(name, glyph, () => ToggleAddFlag(flag), name);
        return new TaskAddToggle(button, glyph, flag);
    }

    void ToggleAddFlag(TaskFlag flag)
    {
        if (!taskTyping) BeginTyping();
        taskAddFlags = flag == TaskFlag.Important ? taskAddFlags with { Important = !taskAddFlags.Important } : taskAddFlags with { Urgent = !taskAddFlags.Urgent };
        PaintAddFlags();
        taskAddBox?.Focus();
    }

    // On: the glyph in its flag's colour; off: the dim ink of an off toggle. Automation status "on" or "off".
    void PaintAddFlags() =>
        new[] { taskAddImportant, taskAddUrgent }.OfType<TaskAddToggle>().ToList().ForEach(toggle =>
        {
            var on = toggle.Flag == TaskFlag.Important ? taskAddFlags.Important : taskAddFlags.Urgent;
            toggle.Glyph.Foreground = on ? (toggle.Flag == TaskFlag.Important ? TaskImportantInk : TaskUrgentInk) : TaskDimInk;
            AutomationProperties.SetItemStatus(toggle.Button, on ? "on" : "off");
        });

    // The chip's menu (Ruling 45): "none" first, then every workspace as its chip in its colour, grouped as on the bar
    // (WorkspaceChipMenu), the chosen target ticked; a pick goes through ChooseTaskTarget and changes the chip. Held open
    // the way the bar's other menus are (HoldFadeWhileOpen), so the bar neither fades nor rebuilds out from under it
    // (#48: no repaint during an open menu). Internal, and answering the menu, for the tests, which pick from it.
    internal ContextMenu? OpenAddTargetMenu()
    {
        if (taskAddChip is null) return null;
        var menu = WorkspaceChipMenu.Build(WorkspaceChipMenu.Entries(manager.State, withNone: true), taskAddTarget, ChooseTaskTarget,
            taskAddChip.Button, ChipMenuWidth);
        HoldFadeWhileOpen(menu);
        TaskAddMenu = menu;
        menu.IsOpen = true;
        return menu;
    }

    // The last menu the add chip opened, for the tests.
    internal ContextMenu? TaskAddMenu { get; private set; }

    // The one chip as the choice stands: the chosen workspace's name in its row's colour, or "none" in the neutral
    // fill. It is always the chosen thing, so it is always filled and bright, with its own fill as its 1 DIP edge, so a
    // pick moves nothing. (Before Ruling 45 three chips took turns at looking chosen; there is nothing to choose between
    // now.) Then the placeholder follows it.
    void PaintTaskChips()
    {
        if (taskAddChip is null) return;
        var target = taskAddTarget;
        var accent = target.HasValue ? workspaceAccents.GetValueOrDefault(target.Value) : null;
        var fill = accent is null ? TaskButtonFill : ChipFill(accent);
        taskAddChip.Label.Text = target.HasValue ? WorkspaceName(target.Value).GetValueOrDefault("this workspace") : "none";
        taskAddChip.Label.Foreground = TaskInk;
        taskAddChip.Face.Background = fill;
        taskAddChip.Face.BorderBrush = fill;
        // The Start Claude button shows once the App has wired StartClaude (AddAndStartClaudeButton).
        if (taskAddStartClaude is not null)
            taskAddStartClaude.Visibility = StartClaude is null ? Visibility.Collapsed : Visibility.Visible;
        // The camera too: it adds and starts Claude in one press, so it needs what the play button needs (task 494d34ee).
        if (taskAddFromWindow is not null)
            taskAddFromWindow.Visibility = StartClaude is null ? Visibility.Collapsed : Visibility.Visible;
        PaintParentChip();
        PaintPlaceholder();
        PaintInboxAdd(); // the Inbox look's add row (FloatingBar.InboxAdd.cs)
    }

    // The empty box says where a note typed now would go: the chosen chip's workspace ("Add to Sparrow…"), or "Add…" for none. Not the workspace in front: after picking "none" the box must not
    // still promise "Add to Deskbar…".
    void PaintPlaceholder()
    {
        if (taskAddPlaceholder is not null)
            taskAddPlaceholder.Text = taskAddParent.HasValue ? SubtaskPlaceholder : PlaceholderFor(taskAddTarget.Bind(WorkspaceName));
    }

    // --- adding a subtask (docs/superpowers/specs/2026-11-24-subtasks-design.md, "Making subtasks", Petre's pick A) ---
    //
    // "Add subtask…" on a line's menu opens this same box with the parent as a chip ("↳ <parent title> ×") after the
    // workspace chip. The workspace chip moves to the parent's workspace (still changeable), so a subtask added from
    // another desktop lands with its parent. Enter asks where (the parent's workspace first) and adds it under the parent; Esc or the chip's press drops the parent and
    // leaves the text as a plain note; after an add or a clearing Esc the box is a plain note box again (EndTyping).

    internal const string SubtaskPlaceholder = "A subtask: Enter asks where, then adds it; Esc drops the parent";

    // The task the next add goes under, or none for a plain note.
    Maybe<Guid> taskAddParent = Maybe<Guid>.None;
    Button? taskAddParentChip;
    TextBlock? taskAddParentLabel;

    /// <summary>
    /// "Add subtask…" on the line <paramref name="lineId"/>: the box takes the keyboard with that line's task as the
    /// parent, or the task's own parent when the line is a subtask (one level, so the new one is a sibling;
    /// TaskBoard.SubtaskParentFor). Internal for the tests.
    /// </summary>
    internal void BeginSubtaskAdd(Guid lineId)
    {
        if (taskService is null || taskAddBox is null) return;
        var parent = taskService.Board.SubtaskParentFor(lineId);
        if (parent.HasNoValue) return;
        if (!taskTyping) BeginTyping();
        taskAddParent = parent.Value.Id;
        taskAddTarget = parent.Value.WorkspaceId;
        PaintTaskChips();
        taskAddBox.Focus();
    }

    void DropTaskAddParent()
    {
        taskAddParent = Maybe<Guid>.None;
        // The note stays on the parent's workspace the chip shows, as though picked by hand, rather than jumping to the
        // workspace in front at the next paint or at Enter (task 701c71a8).
        taskAddTargetPicked = true;
        PaintTaskChips();
        taskAddBox?.Focus();
    }

    // The parent chip: the workspace chip's look with a tan "↳" title and a "×", collapsed while there is no parent.
    // Its own tag, so it is never taken for the workspace chip.
    Button ParentChipButton()
    {
        taskAddParentLabel = new TextBlock { Foreground = TaskTitleInk, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var cross = new TextBlock { Text = "×", Foreground = TaskDimInk, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) };
        var face = new Border
        {
            Tag = "task-add-parent",
            Background = TaskButtonFill,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(7, 0, 7, 1),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { taskAddParentLabel, cross } },
        };
        taskAddParentChip = TaskButton("Subtask of", face, DropTaskAddParent, "A subtask of this task; press to add a plain note instead");
        taskAddParentChip.Margin = new Thickness(0, 1, 2, 1);
        taskAddParentChip.Visibility = Visibility.Collapsed;
        return taskAddParentChip;
    }

    // The parent chip as the box stands: the parent's title, cut as a chip's name is, or collapsed for none.
    void PaintParentChip()
    {
        if (taskAddParentChip is null || taskAddParentLabel is null) return;
        var parent = taskAddParent.Bind(id => taskService?.Board.Tasks.TryFirst(t => t.Id == id) ?? Maybe<TaskItem>.None);
        taskAddParentChip.Visibility = parent.HasValue ? Visibility.Visible : Visibility.Collapsed;
        taskAddParentLabel.Text = parent.Map(p => $"↳ {FirstLine(p.Title)}").GetValueOrDefault("");
        taskAddParentLabel.MaxWidth = 2 * ChipTextCeiling;
    }

    // THE NEW-TASK BOX'S TEMPLATE (task "White looks bad", 2026-11-24). Left to the app's theme (ThemeMode="System",
    // App.xaml), the box ran WPF's Fluent TextBox template, which paints its own fill for each state from the theme's
    // resources and ignores the Background set here: under the light resources a focused box went pure white, with the
    // bar's light-grey ink on it barely readable (Petre's screenshot), and under either theme it drew a clear button of
    // its own inside the box. Measured off-screen in The_add_box_stays_dark_under_either_theme_and_focused: focused,
    // (255,255,255) under Light and (21,21,21) under Dark, one button each. So the box draws only what the bar sets:
    // its field fill, its blue edge, its padding, and the text host WPF needs by name (PART_ContentHost). Nothing in it
    // follows the theme, so it is the same dark field on any machine. The tasks window's BareTextBox (TasksWindow.xaml)
    // is the same fix for its own boxes.
    static readonly ControlTemplate TaskAddBoxTemplate = Sealed((ControlTemplate)XamlReader.Parse(
        """
        <ControlTemplate TargetType="TextBox" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                  BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="3"
                  Padding="{TemplateBinding Padding}" SnapsToDevicePixels="True">
            <ScrollViewer x:Name="PART_ContentHost" Focusable="False" VerticalAlignment="Center"
                          HorizontalScrollBarVisibility="Hidden" VerticalScrollBarVisibility="Hidden" />
          </Border>
        </ControlTemplate>
        """));

    // A line's template: the strip buttons' hover, with the content stretched across the line so the text has the
    // width to trim against (BareStretchButton's reason, with a hover added).
    static readonly ControlTemplate TaskLineTemplate = Sealed((ControlTemplate)XamlReader.Parse(
        """
        <ControlTemplate TargetType="Button" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Border x:Name="Chrome" Background="{TemplateBinding Background}" CornerRadius="4"
                  Padding="{TemplateBinding Padding}">
            <ContentPresenter HorizontalAlignment="Stretch" VerticalAlignment="Center" />
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter TargetName="Chrome" Property="Background" Value="#1FFFFFFF" />
            </Trigger>
            <Trigger Property="IsPressed" Value="True">
              <Setter TargetName="Chrome" Property="Background" Value="#33FFFFFF" />
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """));

    // Holds the task list without letting it widen the bar. The bar sizes itself to its content (SizeToContent), so
    // a task title measured at its full length would make the whole bar that wide; this reports no width of its own,
    // only its height, and lays its content out at whatever width the rest of the bar gave it, where every line trims
    // to fit.
    //
    // IT MUST MEASURE AT ONE WIDTH (PR A review, Critical 1). The first version measured the content at the width it
    // was offered and again at the arranged width on every arrange. On a bar whose width SizeToContent still owns (a
    // floating bar never resized, a new install's) the offered width is infinite, so the content's size flipped
    // between "untrimmed" and "trimmed" on every pass: each arrange's measure changed the child's DesiredSize, that
    // invalidated this element's measure, the next measure flipped it back, and layout never settled (a probe did 154
    // arranges in one UpdateLayout and gave up). So the arranged width is remembered and every later measure uses it;
    // only a first measure, with nothing arranged yet, takes the offered width. An arrange at a NEW width (the bar
    // resized) measures once at it and remembers it, and the measure that follows asks for the same width, a no-op.
    // The height is always measured unbounded: the list is as tall as its lines (the expanded list caps itself).
    //
    // AND AT THE WIDTH THE BAR SHOWS, NOT THE WIDTH IT IS GIVEN (Petre: the chip row read "on [Deskbar] [none] [oth",
    // and the lines' chips were not at the edge). Measured from his trace: "stats geometry: footer=226 ... pins=222" on
    // a bar 140 to 197 DIP wide. His footer is laid out wider than the bar, because its line of pinned apps asks for
    // 222 DIP, and WPF arranges an element no narrower than it asked for and clips the rest. This element, asking for
    // no width, was arranged at that footer width, so the chip row never wrapped at the edge the bar shows and a line's
    // chip, docked at the line's right end, sat past it. So the width the content is laid out at is capped by
    // `visibleWidth`: what is left of the bar's window from this element's left edge, in this element's own units.
    sealed class TakesNoWidth(Func<UIElement, double> visibleWidth) : Decorator
    {
        double arrangedWidth = double.NaN;

        protected override Size MeasureOverride(Size constraint)
        {
            if (Child is null) return new Size(0, 0);
            Child.Measure(new Size(double.IsNaN(arrangedWidth) ? constraint.Width : arrangedWidth, double.PositiveInfinity));
            return new Size(0, Child.DesiredSize.Height);
        }

        protected override Size ArrangeOverride(Size arrangeSize)
        {
            if (Child is null) return arrangeSize;
            var width = Math.Min(arrangeSize.Width, visibleWidth(this));
            if (double.IsNaN(arrangedWidth) || Math.Abs(width - arrangedWidth) > 0.01)
            {
                arrangedWidth = width;
                Child.Measure(new Size(arrangedWidth, double.PositiveInfinity));
            }
            Child.Arrange(new Rect(0, 0, width, arrangeSize.Height));
            return arrangeSize;
        }
    }

    // How much of the bar's window there is to the right of `part`'s left edge, in `part`'s own units (the bar scales
    // its content, so a bar DIP is not a list DIP): the window's width less the root's padding (RootPadding, in content
    // units, so scaled like the part), less where the part starts. Unbounded while the part is not on screen yet, which
    // leaves the first layout to the width it is given, as before.
    double VisibleWidthOf(UIElement part)
    {
        if (PresentationSource.FromVisual(part) is null || !part.IsDescendantOf(this) || ActualWidth <= 0) return double.PositiveInfinity;
        var toBar = part.TransformToAncestor(this);
        var left = toBar.Transform(new Point(0, 0)).X;
        var unit = toBar.Transform(new Point(1, 0)).X - left;
        return unit <= 0 ? double.PositiveInfinity : Math.Max(0, (ActualWidth - left) / unit - RootPadding);
    }
}

/// <summary>
/// One task's line as the bar draws it: the task's id, title and detail (the question while it waits on Petre, else
/// its status line, plus "no live session" when its sessions have all ended), its status, whether it is Claude's (it
/// has a session), its workspace's name and row colour (none on no workspace), and the VS Code window of its session
/// when one is found. Made by <see cref="FloatingBar.Lines"/>. <see cref="Closing"/> marks a line whose task was just
/// closed and which stays, struck through with its Undo, for the undo window (FloatingBar.CloseWithUndo).
/// </summary>
internal sealed record TaskLine(Guid Id, string Title, string Detail, TaskStatus Status, bool Claudes, string? WorkspaceName, Brush? Colour,
    Maybe<nint> Window, bool Closing = false)
{
    /// <summary>When the task appeared, for the line's tooltip ("Created ..."). Not part of the line's shape: it never
    /// changes once the task exists.</summary>
    public DateTimeOffset Created { get; init; }

    /// <summary>Who the task is on (Ruling 57). The bar lists only the lines assigned to Petre (NeedsYou).</summary>
    public Assignee Assignee { get; init; } = Assignee.Me;

    /// <summary>The task's two flags (Task 12), drawn in the title: bold for important, italic for urgent (Ruling 59).</summary>
    public bool Important { get; init; }

    /// <summary>
    /// Whether the line menu's Open Claude can open anything (task 6a57f59b): a Claude task whose session to go to runs or
    /// can be resumed. False, the menu offers Start Claude on this instead, as the tasks panel's line and thread do.
    /// </summary>
    public bool OpensSession { get; init; }

    public bool Urgent { get; init; }

    /// <summary>The task's repeat in words ("every day at 09:00"), or null: the line's repeat mark (task 0b78269e, Task 9).</summary>
    public string? RepeatLabel { get; init; }

    /// <summary>When the next run is due, or null: in the repeat mark's tooltip.</summary>
    public DateTimeOffset? NextRun { get; init; }

    /// <summary>The model the repeat's runs use, or null: in the repeat mark's tooltip.</summary>
    public string? RepeatModel { get; init; }

    /// <summary>
    /// Whether a Done task's change runs in this Deskbar yet (task 8470d176, RunningChanges): Shipping draws the hollow
    /// ring instead of the disc, and the Note goes in the tooltip. None for anything it cannot tell.
    /// </summary>
    public TaskDelivery Delivery { get; init; } = TaskDelivery.None;

    public Quadrant Quadrant => QuadrantOf.Flags(Important, Urgent);

    /// <summary>
    /// Which group the line is in: the pinned Waiting on you block (its questions and its Done tasks, TaskBoard.Pinned),
    /// or its quadrant. Lines of one group sit together (TaskBoard.Pile), so a change of key between neighbours is where a
    /// group starts.
    /// </summary>
    public string GroupKey => ShownIn ?? (Status.IsPinned() ? "waiting" : Quadrant.Numeral());

    /// <summary>
    /// The group a subtask drawn under its parent belongs to (its parent's GroupKey), or null for a line in its own place
    /// (FloatingBar.UnderParents).
    /// </summary>
    public string? ShownIn { get; init; }

    /// <summary>The parent task's id when this is a subtask whose parent is on the board, or null.</summary>
    public Guid? ParentId { get; init; }

    /// <summary>Whether this line is the first of its group, below another group (FloatingBar.WithGroupStarts).</summary>
    public bool StartsGroup { get; init; }

    /// <summary>
    /// The title of the goal the task counts toward, or null (goals plan, Task 7). Shown only in the tooltip, never on
    /// the line; part of the line's shape so a rename repaints it, not of the places key, so it moves nothing.
    /// </summary>
    public string? GoalTitle { get; init; }

    /// <summary>
    /// Subtasks (Petre's pick A): the parent's title when the task is a subtask, or null. The line shows "↳" and the
    /// tooltip names the parent. Part of the line's shape, so renaming the parent repaints its subtasks.
    /// </summary>
    public string? ParentTitle { get; init; }

    /// <summary>How many open subtasks the task has, shown after its title ("· 2 subtasks"); 0 for none.</summary>
    public int SubtaskCount { get; init; }
}
