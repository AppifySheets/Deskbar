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
//                   keyboard (BeginTyping). Under it, always, the chips choosing where the note goes: the current
//                   workspace (ticked to begin with), "none" and "other…", which lists the other workspaces; they
//                   wrap onto a second line on a narrow bar rather than clip. A chip pressed while the box is idle
//                   starts typing as a click in the box does. Enter
//                   adds a Backlog note through TaskService.AddNote and Esc clears the box; either way the keyboard
//                   goes back to the window Petre was typing in. Clicking away leaves the text where it is.
//   the lines       the first lines of the pile, three on a floating bar and as many as fit on a docked one (Waiting
//                   on you, then Covey's quadrants I to IV, each in Petre's own order, dragged up and down; TaskBoard.Pile,
//                   THE FLAGS below, FloatingBar.TaskDrag.cs). A
//                   Claude line (a task with a session) has an orange edge and tint, a CLAUDE tag, the VS Code icon
//                   of its session's window when one is found, then the task's title in a tan ink and, after a dot,
//                   the question while it waits on Petre, else its status line, with "no live session" when every
//                   session has ended. Petre's notes have a plain tick box that closes them. A Done Claude task has
//                   a close button. Every line ends with its workspace's chip in that row's own colour, or "none".
//                   A line just closed stays in its place for six seconds, struck through, with "Undo" where its
//                   tick or close button was (Petre: "when i click on done, there should be undo"; Ruling 10).
//   the flags       (Task 12, Rulings 25 and 28.) Two small toggles on every open line, important (an exclamation
//                   mark) and urgent (a clock), which name the task's Covey quadrant. Off they are quiet: invisible,
//                   still there to click, shown dimly while the line is hovered or selected. On they are drawn in
//                   their colour. A click sets the flag through TaskService.SetFlag and the task goes to the top of the
//                   quadrant it lands in. The groups (the Waiting on you block, then each quadrant) are told apart by a
//                   little space above a group's first line, and nothing names them: the bar stays compact (the
//                   window's List view has the captions).
//   "+N more"       a small line under the last line when more open tasks are out of sight. When some of the hidden
//                   ones wait on Petre it says so in the asking orange ("+2 waiting"), so no ask is out of sight
//                   without a sign (spec, "The bar's order"). It opens the tasks window (OpenTasksWindow, set by the
//                   App; TasksBoard), and with the window there it is always shown, reading "All tasks" when nothing
//                   is hidden, since the window is where closed tasks and comment threads are. Without a window (the
//                   bar's tests) it expands the list in place and reads "Less" at the end of it. Under the lines
//                   rather than in a header, so it does not move under the pointer when the list opens.
//
// CLICKS. A line goes to its task through GoToTask, which the App points at the workspace switch, the same path the
// tasks window's lines take (a later change sends a Claude task to its session instead; controller ruling 13); a note
// on no workspace has nowhere to go, so its click does nothing. The tick box and the close button call TaskService.Close at once (the store stays the truth; only Petre
// closes, and these are his clicks), and "Undo" calls TaskService.UndoClose, which brings the task back exactly as it
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

    // What a click on a line does with the task's id. App sets it to the workspace switch; a refusal is traced.
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

    // The highlight as the selection stands, on the lines on show, in place.
    void PaintTaskSelection() =>
        (taskLinesPanel?.Children.OfType<Button>() ?? []).Where(b => b.Tag is TaskLine).ToList().ForEach(button =>
        {
            if (button.Content is not Border frame) return;
            var selected = selection.IsSelected(((TaskLine)button.Tag).Id);
            frame.BorderBrush = selected ? TaskPickedEdge : Brushes.Transparent;
            frame.Background = selected ? TaskSelectedFill : Brushes.Transparent;
            ShowQuietFlags(button, selected || button.IsMouseOver);
        });

    // For the tests: whether a line on show is drawn as selected.
    internal bool TaskLineSelected(Guid id) =>
        (taskLinesPanel?.Children.OfType<Button>() ?? []).Any(b => b.Tag is TaskLine line && line.Id == id
            && b.Content is Border { BorderBrush: SolidColorBrush { Color.A: > 0 } });

    // What "+N more" opens: the tasks window (TasksBoard.Open, set by the App: it opens the window, or brings an open
    // one to the desktop in front). While it is set, the line under the
    // list is always there, reading "All tasks" when nothing is out of sight, because the window is the one place the
    // closed tasks can be seen (Petre: "can i see task list in deskbar? all of them, new and old"). Left null (the
    // bar's own tests, or a copy with no window) the button expands the list in place, as before the window existed.
    internal Action? OpenTasksWindow { get; init; }

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

    // THE AGE ON EACH LINE (Petre: "tasks should say how long ago they appeared - 2h3m"; ruling 26). A dim text just
    // before the workspace chip, TaskAge's compact form, the full date and time in its tooltip. Once a minute only
    // those texts are rewritten (RefreshTaskAges), never the lines: a repaint would change the list's shape and could
    // slide a line under the pointer (#48). Not while a paint would wait either (TaskListMustWait: a press, a drag, a
    // menu). TaskNow is the clock they are told against; TaskAgeTimer starts the minute's tick. Both seams for the tests.
    internal Func<DateTimeOffset> TaskNow { get; init; } = () => DateTimeOffset.UtcNow;
    internal Func<TimeSpan, Action, IDisposable> TaskAgeTimer { get; init; } = RepeatingTimer;

    static readonly TimeSpan AgeTick = TimeSpan.FromMinutes(1);

    // The age texts on show, by task, with when each task appeared.
    readonly Dictionary<Guid, (TextBlock Text, DateTimeOffset Created)> taskAges = [];
    IDisposable? taskAgeTick;

    /// <summary>Rewrites every age on show for the time now; nothing else on the lines changes.</summary>
    internal void RefreshTaskAges()
    {
        if (TaskListMustWait) return;
        var now = TaskNow();
        taskAges.Values.ToList().ForEach(age => age.Text.Text = TaskAge.Format(now - age.Created));
    }

    // A dispatcher timer that runs `then` every `every` until disposed.
    static IDisposable RepeatingTimer(TimeSpan every, Action then)
    {
        var timer = new DispatcherTimer { Interval = every };
        timer.Tick += (_, _) => then();
        timer.Start();
        return Disposable.Create(timer.Stop);
    }

    // The age text of a line: dim, trimmed after the title has given up its room, never pushing the chip.
    TextBlock AgeOf(TaskLine line)
    {
        var text = new TextBlock
        {
            Tag = "task-age",
            Text = TaskAge.Format(TaskNow() - line.Created),
            FontSize = Text(7.5),
            Foreground = TaskDimInk,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(4, 0, 0, 0),
            ToolTip = line.Created.ToLocalTime().ToString("dddd d MMMM yyyy, HH:mm"),
        };
        taskAges[line.Id] = (text, line.Created);
        return text;
    }

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
        || TaskDragInFlight;

    // For the tests: whether the list is on screen, what "+N more" says ("" while it is hidden), whether the box has
    // the keyboard, where a note added now would go, what the empty box says, and the lines on show.
    internal bool TaskListShown => TaskListLine.Visibility == Visibility.Visible;
    internal string TaskListMoreText => taskMoreButton is { Visibility: Visibility.Visible } ? taskMoreLabel?.Text ?? "" : "";
    internal bool TaskListAdding => taskTyping;
    internal Maybe<Guid> TaskAddTarget => taskAddTarget;
    internal string TaskAddPlaceholder => taskAddPlaceholder?.Text ?? "";
    internal IReadOnlyList<TaskLine> TaskListLines => shownTaskLines;

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
        var known = allTaskLines.Select(line => taskLineHeights.TryGetValue(HeightKeyOf(line), out var h) ? h : (double?)null).ToArray();
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
        var linesNow = taskLinesPanel.Children.OfType<FrameworkElement>().Sum(line => line.DesiredSize.Height);
        return available - others - rowsNeed + linesNow;
    }

    // Remembers the height of every line on show, and forgets the lines the list no longer has.
    void RememberLineHeights()
    {
        (taskLinesPanel?.Children.OfType<Button>() ?? []).Where(b => b.Tag is TaskLine && b.ActualHeight > 0).ToList()
            .ForEach(b => taskLineHeights[HeightKeyOf((TaskLine)b.Tag)] = b.ActualHeight + b.Margin.Top + b.Margin.Bottom);
        var current = allTaskLines.Select(HeightKeyOf).ToHashSet();
        taskLineHeights.Keys.Where(key => !current.Contains(key)).ToList().ForEach(key => taskLineHeights.Remove(key));
    }

    // What a line's height follows: everything it shows, and the text size.
    string HeightKeyOf(TaskLine line) => $"{TextOffset}|{ShapeOf(line)}";

    // Expanded in place, at most this many lines show and the rest scroll (PR A review, Important 3): dozens of
    // migrated notes must not push the workspace rows off a docked bar.
    const int ExpandedSlots = 8;

    // A click this soon after a repaint that changed the lines is not trusted (TaskClickIgnored).
    static readonly TimeSpan RepaintGrace = TimeSpan.FromMilliseconds(500);

    // How long a closed line stays, struck through with its "Undo" (Ruling 10).
    internal static readonly TimeSpan UndoWindow = TimeSpan.FromSeconds(6);
    const string NoLiveSession = "no live session";
    const string TaskDetailSeparator = " · ";
    const string OtherChipText = "other…";

    // The colours, from the chosen mockup and the bar's own palette: the asking orange for anything Claude's or
    // waiting on Petre, the mockup's light and dim inks, its tan for a Claude task's title, and quick find's blue
    // for the field and the chip that is ticked. All frozen (CLAUDE.md: static WPF brushes must be).
    static readonly Brush TaskOrange = Frozen(0xFF, 0xFF, 0xA7, 0x26);
    static readonly Brush TaskClaudeTint = Frozen(0x26, 0xFF, 0xA7, 0x26);
    static readonly Brush TaskInk = Frozen(0xFF, 0xE6, 0xE6, 0xE6);
    static readonly Brush TaskDimInk = Frozen(0xFF, 0xA9, 0xAD, 0xB8);
    static readonly Brush TaskTitleInk = Frozen(0xFF, 0xD9, 0xB9, 0x8A);
    static readonly Brush TaskBadgeInk = Frozen(0xFF, 0x1E, 0x1F, 0x24);
    static readonly Brush TaskButtonFill = Frozen(0xFF, 0x2A, 0x2C, 0x33);
    static readonly Brush TaskTickEdge = Frozen(0xFF, 0x77, 0x77, 0x77);
    static readonly Brush TaskNoneFill = Frozen(0xFF, 0x33, 0x33, 0x33);
    static readonly Brush TaskNoneInk = Frozen(0xFF, 0x99, 0x99, 0x99);
    static readonly Brush TaskFieldFill = Frozen(0xFF, 0x15, 0x16, 0x1A);
    static readonly Brush TaskPickedEdge = Frozen(0xFF, 0x4F, 0x8C, 0xFF);

    // The flags' colours (Task 12): important a soft red, urgent an amber, both clear of the asking orange that means
    // Claude's or waiting; off, the dim ink. Internal for the tests.
    static readonly Brush TaskImportantInk = Frozen(0xFF, 0xFF, 0x6B, 0x6B);
    static readonly Brush TaskUrgentInk = Frozen(0xFF, 0xFF, 0xD5, 0x4F);
    internal static readonly Brush TaskFlagOffInk = Frozen(0xFF, 0xA9, 0xAD, 0xB8);

    // The persistent parts, built once in BuildTaskList.
    Border? taskProblemHost;
    TextBlock? taskMoreLabel;
    Button? taskMoreButton;
    TextBox? taskAddBox;
    TextBlock? taskAddPlaceholder;
    WrapPanel? taskAddChips;
    TextBlock? taskAddOn;
    TaskChipParts? taskChipHere;
    TaskChipParts? taskChipNone;
    TaskChipParts? taskChipOther;
    StackPanel? taskLinesPanel;
    ScrollViewer? taskLinesScroller;

    // The list's state: folded to three or expanded, what was drawn last, the lines on show, and the add box's choice.
    bool taskListExpanded;
    string? lastTaskListShape;
    IReadOnlyList<TaskLine> shownTaskLines = [];
    Maybe<Guid> taskAddHere = Maybe<Guid>.None;
    Maybe<Guid> taskAddTarget = Maybe<Guid>.None;

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
    string taskLinePlaces = "";
    DateTime taskLinesChangedAt = DateTime.MinValue;

    // A workspace chip's fill: its row's rail colour, faded so the name stays readable on it. One brush per colour,
    // per bar, frozen.
    readonly Dictionary<Color, Brush> taskChipFills = [];

    sealed record TaskChipParts(Button Button, Border Face, TextBlock Label);

    // --- the lines, as data -------------------------------------------------------------------------------------

    /// <summary>
    /// Every open task as the bar draws it, in the pile's order (TaskBoard.Pile). <paramref name="workspaceName"/> and
    /// <paramref name="colour"/> answer for the workspace chip; <paramref name="sessionWindow"/> finds a Claude task's
    /// VS Code window from its session's folder. Pure apart from those three, so the rules are read in one place.
    /// </summary>
    internal static IReadOnlyList<TaskLine> Lines(TaskBoard board, Func<Guid, Maybe<string>> workspaceName,
        Func<Guid, Brush?> colour, Func<string, Maybe<nint>> sessionWindow) =>
        board.Pile().Select(t => new TaskLine(
            t.Id,
            t.Title,
            DetailOf(t),
            t.Status,
            t.IsClaudes,
            t.WorkspaceId.Bind(workspaceName).Match(name => (string?)name, () => null),
            t.WorkspaceId.HasValue ? colour(t.WorkspaceId.Value) : null,
            t.IsClaudes ? TaskViews.SessionToGoTo(t).Bind(s => sessionWindow(s.Folder)) : Maybe<nint>.None)
        {
            Created = t.Created,
            Important = t.Important,
            Urgent = t.Urgent,
        }).ToArray();

    // Marks the first line of each group after the first (TaskLine.StartsGroup), over the lines as they are drawn, the
    // closed ones in their undo window included, so the space sits where the eye sees a group begin.
    internal static IReadOnlyList<TaskLine> WithGroupStarts(IReadOnlyList<TaskLine> lines) =>
        lines.Select((line, i) => line with { StartsGroup = i > 0 && lines[i - 1].GroupKey != line.GroupKey }).ToArray();

    // What follows the title: the question while the task waits on Petre, else its status line; and for a Claude task
    // whose sessions have all ended, "no live session" after that (spec, "A task whose sessions have all ended stays
    // open and says 'no live session'"). The tasks window's lines say the same (TasksBoard.Row).
    internal static string DetailOf(TaskItem task) =>
        string.Join(TaskDetailSeparator, new[]
        {
            task.Status == TaskStatus.WaitingOnYou && task.Question.HasValue ? task.Question.Value : task.StatusLine,
            task.IsClaudes && !task.HasLiveSession ? NoLiveSession : "",
        }.Where(part => part.Length > 0));

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
            Background = TaskFieldFill,
            Foreground = TaskInk,
            CaretBrush = TaskInk,
            BorderBrush = TaskPickedEdge,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 2, 4, 2),
            ToolTip = "Enter adds the task, Esc clears the box",
        };
        AutomationProperties.SetName(taskAddBox, "New task");
        taskAddBox.PreviewKeyDown += OnTaskAddKey;
        taskAddBox.PreviewMouseLeftButtonDown += (_, _) =>
        {
            if (!taskTyping) BeginTyping();
        };
        taskAddBox.TextChanged += (_, _) => PaintAddRow();
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
        taskChipHere = TaskChip("", () => ChooseTaskTarget(taskAddHere));
        taskChipNone = TaskChip("none", () => ChooseTaskTarget(Maybe<Guid>.None));
        taskChipOther = TaskChip(OtherChipText, () => OpenOtherWorkspaces());
        taskAddOn = new TextBlock { Text = "on", Foreground = TaskDimInk, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 1, 4, 1) };
        taskAddChips = new WrapPanel
        {
            Tag = "task-add-chips",
            Margin = new Thickness(0, 3, 0, 0),
            Children = { taskAddOn, taskChipHere.Button, taskChipNone.Button, taskChipOther.Button },
        };
        var addRow = new StackPanel { Margin = new Thickness(0, 0, 0, 3), Children = { box, taskAddChips } };

        taskLinesPanel = new StackPanel();
        WireTaskDrag(taskLinesPanel);
        // The lines scroll inside this once the expanded list is capped (CapTaskLines). The bar draws no scroll bar of
        // its own anywhere, so none here either: the wheel scrolls it. Folded to three, it has no ceiling at all.
        taskLinesScroller = new ScrollViewer
        {
            Tag = "task-lines",
            Content = taskLinesPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };

        // "+N more" / "Less": a small line under the last line, at the left, trimmed rather than clipped.
        taskMoreLabel = new TextBlock { Foreground = TaskDimInk, TextTrimming = TextTrimming.CharacterEllipsis };
        taskMoreButton = TaskButton("More tasks", taskMoreLabel, OnTaskMoreClick, ignoredWhen: TaskClickIgnored);
        taskMoreButton.HorizontalAlignment = HorizontalAlignment.Left;
        taskMoreButton.Padding = new Thickness(6, 1, 6, 1);
        taskMoreButton.Margin = new Thickness(0, 1, 0, 0);
        taskMoreButton.Visibility = Visibility.Collapsed;

        var host = new TakesNoWidth(VisibleWidthOf) { Child = new StackPanel { Children = { taskProblemHost, addRow, taskLinesScroller, taskMoreButton } } };
        TaskListLine.Child = host;
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
        selectionChanges ??= selection.Changed.Subscribe(_ => PaintTaskSelection());
        ApplyTaskListTextSize();
        PaintAddRow();
        Closed += (_, _) =>
        {
            taskChanges?.Dispose();
            taskAgeTick?.Dispose();
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
        var face = new Border
        {
            Tag = "task-chip",
            Background = TaskButtonFill,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(7, 0, 7, 1),
            Child = label,
        };
        var button = TaskButton(text, face, press);
        button.Margin = new Thickness(0, 1, 4, 1);
        return new TaskChipParts(button, face, label);
    }

    // The widest a workspace chip's name may be, in a line or in the add box: about eight characters of chip text,
    // following the text size. Past it the name is trimmed, so it never squeezes a title away.
    double ChipTextCeiling => Text(7.5) * 7;

    // The text built once and kept takes the bar's text size here (ApplyTextSize); the lines take it as they are made.
    void ApplyTaskListTextSize()
    {
        Sized(taskMoreLabel, 8);
        Sized(taskAddOn, 7.5);
        Sized(taskAddPlaceholder, 9.5);
        new[] { taskChipHere, taskChipNone, taskChipOther }.ToList().ForEach(chip =>
        {
            Sized(chip?.Label, 7.5);
            if (chip is not null) chip.Label.MaxWidth = ChipTextCeiling;
        });
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

        var open = Lines(taskService.Board, WorkspaceName, id => workspaceAccents.GetValueOrDefault(id), SessionWindow);
        // A closed line whose task is open again (reopened from the pipe, say) is not pending any more: the task is back
        // in the pile under its own line.
        pendingCloses.Where(p => open.Any(l => l.Id == p.Line.Id)).ToList().ForEach(p => ForgetPendingClose(p, repaint: false));
        var slots = TaskSlotsNow;
        if (open.Count <= slots) taskListExpanded = false;
        var all = WithGroupStarts(WithPendingCloses(open, pendingCloses));
        allTaskLines = all;
        var problem = taskService.LoadProblem;
        var shape = string.Join("\n", all.Select(ShapeOf).Prepend(
            $"{taskListExpanded}|{OpenTasksWindow is null}|{TextOffset}|{problem.GetValueOrDefault("")}|{slots}"));
        if (shape == lastTaskListShape) return;
        lastTaskListShape = shape;

        shownTaskLines = taskListExpanded ? all : all.Take(slots).ToArray();
        taskLinesPanel.Children.Clear();
        taskAges.Clear();
        // The minute's tick of the ages starts with the first lines drawn and lives as long as the bar.
        taskAgeTick ??= TaskAgeTimer(AgeTick, RefreshTaskAges);
        if (taskProblemHost is not null)
        {
            taskProblemHost.Child = problem.HasValue ? LoadProblemRow(problem.Value) : null;
            taskProblemHost.Visibility = problem.HasValue ? Visibility.Visible : Visibility.Collapsed;
        }
        shownTaskLines.ToList().ForEach(line => taskLinesPanel.Children.Add(TaskLineButton(line)));
        // When the lines on show changed places (which tasks, in which order, which are closing), the click guard
        // distrusts a click for the next half second (TaskClickIgnored). A paint that only rewrote what lines say (a
        // session's status line) moved nothing and does not start it, or frequent status pulses would eat clicks. The
        // text size and the failed-load line move every line too (a bigger font, a line above them), so they are part
        // of the key (review minor 3 of 187cf4a).
        var placesKey = string.Join('|', shownTaskLines.Select(l => $"{l.Id}{l.Closing}").Prepend($"{TextOffset}|{problem.HasValue}"));
        if (placesKey != taskLinePlaces)
        {
            taskLinePlaces = placesKey;
            taskLinesChangedAt = TaskClickClock();
        }
        CapTaskLines();
        PaintTaskMore(open);
    }

    // The open lines with every closed line still in its undo window put back where it was, in the order they were
    // closed at, so no line moves up under the pointer while one lingers (Ruling 10). Each index is the line's place
    // among the lines on show when it was closed, the earlier lines (pending ones included) already in front of it.
    static IReadOnlyList<TaskLine> WithPendingCloses(IReadOnlyList<TaskLine> open, IReadOnlyList<PendingClose> pending) =>
        pending.OrderBy(p => p.Index).Aggregate(open.ToList(), (lines, p) =>
        {
            lines.Insert(Math.Min(p.Index, lines.Count), p.Line);
            return lines;
        });

    // The expanded list's ceiling: the height of its first eight lines, so the ninth and later scroll inside the viewer. Measured from the lines just built, at their own
    // height, since a line's height follows the text size. Folded, there is no ceiling and the viewer is at its top.
    void CapTaskLines()
    {
        if (taskLinesScroller is null || taskLinesPanel is null) return;
        if (!taskListExpanded || shownTaskLines.Count <= ExpandedSlots)
        {
            taskLinesScroller.MaxHeight = double.PositiveInfinity;
            taskLinesScroller.ScrollToTop();
            return;
        }
        var counted = taskLinesPanel.Children.OfType<UIElement>().Take(ExpandedSlots).ToList();
        counted.ForEach(row => row.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)));
        taskLinesScroller.MaxHeight = counted.Sum(row => row.DesiredSize.Height);
    }

    // Everything a line shows, as text, so two paints can be compared without drawing anything.
    static string ShapeOf(TaskLine line) =>
        $"{line.Id}|{line.Status}|{line.Title}|{line.Detail}|{line.Claudes}|{line.WorkspaceName}|" +
        $"{(line.Colour as SolidColorBrush)?.Color}|{line.Window.Match(w => w.ToString(), () => "")}|{line.Closing}|" +
        $"{line.Important}|{line.Urgent}|{line.StartsGroup}";

    // "+N more" counts the open tasks out of sight; a closed line in its undo window is not an open task, so it is
    // neither counted nor allowed to hide one from the count.
    void PaintTaskMore(IReadOnlyList<TaskLine> open)
    {
        if (taskMoreButton is null || taskMoreLabel is null) return;
        var onShow = shownTaskLines.Select(l => l.Id).ToHashSet();
        var hidden = open.Where(l => !onShow.Contains(l.Id)).ToArray();
        var waiting = hidden.Count(l => l.Status == TaskStatus.WaitingOnYou);
        taskMoreButton.Visibility = hidden.Length > 0 || taskListExpanded || OpenTasksWindow is not null ? Visibility.Visible : Visibility.Collapsed;
        taskMoreLabel.Text = MoreText(hidden.Length, waiting, taskListExpanded);
        taskMoreLabel.Foreground = waiting > 0 && !taskListExpanded ? TaskOrange : TaskDimInk;
        taskMoreButton.ToolTip = taskListExpanded ? "Show the first three"
            : OpenTasksWindow is not null ? "Every task, open and closed, with its comments"
            : $"{hidden.Length} more {(hidden.Length == 1 ? "task" : "tasks")}";
    }

    // One task's line: a button the width of the list, so a click anywhere on it goes to the task.
    Button TaskLineButton(TaskLine line)
    {
        var face = new DockPanel { LastChildFill = true };
        LeftOf(line).ToList().ForEach(part =>
        {
            DockPanel.SetDock(part, Dock.Left);
            face.Children.Add(part);
        });
        RightOf(line).ToList().ForEach(part =>
        {
            DockPanel.SetDock(part, Dock.Right);
            face.Children.Add(part);
        });
        face.Children.Add(TextOf(line));
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
            Padding = new Thickness(5, 3, 5, 3),
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
            Content = frame,
            Padding = new Thickness(0),
            // A group's first line has a little space above it (see the header: the groups are not named on the bar).
            Margin = new Thickness(0, line.StartsGroup ? GroupGap : 1, 0, 1),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Template = TaskLineTemplate,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Focusable = false,
            Cursor = Cursors.Hand,
            ToolTip = line.Closing ? "Closed. Undo brings it back." : TipOf(line),
        };
        AutomationProperties.SetName(button, line.Title);
        // The quiet toggles show while the pointer is on the line, and stay while it is selected.
        ShowQuietFlags(button, selected);
        button.MouseEnter += (_, _) => ShowQuietFlags(button, true);
        button.MouseLeave += (_, _) => ShowQuietFlags(button, selection.IsSelected(line.Id));
        // The click guard, as on the tick and the close button (TaskClickIgnored); and the press that may become a drag
        // of the line (FloatingBar.TaskDrag.cs), armed only, so a press released in place stays this Click.
        button.PreviewMouseLeftButtonDown += (_, e) =>
        {
            taskPressClicks = e.ClickCount;
            if (taskLinesPanel is not null) TaskDragPress(line.Id, ArrangeMouseInput.Position(e, taskLinesPanel));
        };
        button.Click += (_, _) =>
        {
            MarkPressConsumed();
            // A closed line goes nowhere: its task is closed, and Undo is the one thing on it to press.
            if (TaskClickIgnored() || line.Closing) return;
            OnTaskLineClick(line);
        };
        return button;
    }

    // Left of the text: the CLAUDE tag and the session's window icon on Claude's lines, the tick box on a note, and on a
    // note just closed, "Undo" in the tick box's place.
    IEnumerable<UIElement> LeftOf(TaskLine line) =>
        line.Claudes
            ? new UIElement?[] { ClaudeTag(), line.Window.HasValue ? WindowIconOf(line.Window.Value) : null }.OfType<UIElement>()
            : line.Closing
                ? [UndoButton(line)]
                : [TaskButton("Tick off", new Border
            {
                Width = 11,
                Height = 11,
                BorderBrush = TaskTickEdge,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Background = Brushes.Transparent,
                Margin = new Thickness(1, 0, 1, 0),
            }, () => CloseWithUndo(line), "Tick off: closes the note", ignoredWhen: TaskClickIgnored)];

    // Right of the text, from the edge in: the workspace chip, then, on a Done Claude task, the close button, since a
    // Done task waits for Petre to look and close it (spec, "A Done Claude task shows a close button on its line"). The
    // button is the Done mark: a "Done" word beside it was tried and, on a bar of the default width, took the room the
    // title needed. The line's tooltip names the status.
    // A Done Claude task just closed has "Undo" in the close button's place. Just inside the chip, the task's age
    // (AgeOf): docked before the title fills the rest, so the title trims first, then the age, never the chip.
    IEnumerable<UIElement> RightOf(TaskLine line) =>
        new UIElement?[]
        {
            WorkspaceChipButton(line),
            AgeOf(line),
            line.Status == TaskStatus.Done && line.Claudes
                ? line.Closing
                    ? UndoButton(line)
                    : TaskButton("Close task", new TextBlock { Text = "✕", Foreground = TaskInk, FontSize = Text(8.5), Margin = new Thickness(5, 0, 5, 0) },
                        () => CloseWithUndo(line), "Done: close this task", ignoredWhen: TaskClickIgnored)
                : null,
            line.Closing ? null : FlagToggle(line, TaskFlag.Urgent),
            line.Closing ? null : FlagToggle(line, TaskFlag.Important),
        }.OfType<UIElement>();

    // THE TWO TOGGLES (Task 12; see the header). Glyphs by code point in the icon font (IconFont: Segoe Fluent Icons,
    // Segoe MDL2 Assets on Windows 10, both checked to carry them): U+E8C9 "Important", an exclamation mark, and
    // U+E823 "Recent", a clock face, for urgent. Same size on or off, so a toggle never moves anything; off, the
    // button is transparent (Opacity 0, still hit-testable) until the line is hovered or selected (ShowQuietFlags).
    // Its automation status says "on" or "off", its tooltip names the flag. A closed line in its undo window has none.
    Button FlagToggle(TaskLine line, TaskFlag flag)
    {
        var on = flag == TaskFlag.Important ? line.Important : line.Urgent;
        var name = flag == TaskFlag.Important ? "Important" : "Urgent";
        var glyph = new TextBlock
        {
            Text = flag == TaskFlag.Important ? "\uE8C9" : "\uE823",
            FontFamily = IconFont,
            FontSize = Text(8.5),
            Foreground = on ? (flag == TaskFlag.Important ? TaskImportantInk : TaskUrgentInk) : TaskFlagOffInk,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 2, 0),
        };
        var button = TaskButton(name, glyph,
            () => taskService?.SetFlag(line.Id, flag, !on).TapError(err => ClickTrace.Write($"task list: flag not set: {err}")),
            name, ignoredWhen: TaskClickIgnored);
        button.Tag = on ? FlagOnTag : FlagOffTag;
        AutomationProperties.SetItemStatus(button, on ? "on" : "off");
        if (!on) button.Opacity = 0;
        return button;
    }

    const string FlagOnTag = "task-flag-on";
    const string FlagOffTag = "task-flag-off";

    // The space above a group's first line, in DIP: enough to see a break, little enough to keep the bar compact.
    const double GroupGap = 6;

    // How strongly an off toggle shows while its line is hovered or selected.
    const double QuietFlagOpacity = 0.55;

    // Shows or hides a line's off toggles; the on ones are always at full strength.
    static void ShowQuietFlags(Button line, bool show) =>
        LogicalDescendants(line).OfType<Button>().Where(b => Equals(b.Tag, FlagOffTag)).ToList()
            .ForEach(toggle => toggle.Opacity = show ? QuietFlagOpacity : 0);

    // Every element under `node` in the logical tree, itself included.
    static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject node) =>
        LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>().SelectMany(LogicalDescendants).Prepend(node);

    // Undo on a line just closed: a sign, not a word (Petre: "undo sign instead of text"), Segoe Fluent Icons' Undo
    // (U+E7A7, Segoe MDL2 Assets on Windows 10), in the plain bright ink, about the tick box's size and in its place.
    // Its automation name and tooltip say "Undo".
    Button UndoButton(TaskLine line) =>
        TaskButton("Undo", UndoSign(Text(8.5), TaskInk), () => UndoClose(line.Id), "Undo", ignoredWhen: DoubleClickIgnored);

    internal static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    internal static TextBlock UndoSign(double size, Brush ink) => new()
    {
        Tag = "undo-sign",
        Text = "",
        FontFamily = IconFont,
        FontSize = size,
        Foreground = ink,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(1, 0, 1, 0),
    };

    // The CLAUDE tag of the mockup: dark capitals on the asking orange.
    Border ClaudeTag() => new()
    {
        Tag = "task-claude-tag",
        Background = TaskOrange,
        CornerRadius = new CornerRadius(3),
        Padding = new Thickness(4, 0, 4, 0),
        Margin = new Thickness(0, 0, 5, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = "CLAUDE", FontSize = Text(6.5), FontWeight = FontWeights.Bold, Foreground = TaskBadgeInk },
    };

    // The session's VS Code window's own icon, from the same cache the rows draw from.
    UIElement WindowIconOf(nint hwnd)
    {
        var handle = new WindowHandle(hwnd);
        var path = manager.KnownWindows.FirstOrDefault(w => w.Handle == handle)?.ProcessPath;
        return new Image
        {
            Tag = "task-window-icon",
            Source = IconCache.For(handle, path),
            Width = 14,
            Height = 14,
            Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    // The title, then a dot and the detail. On Claude's lines the title is the tan of the mockup's session title and
    // the detail is the bright text, since the detail (the question, the status line) is the news; on a note the
    // title is the news. A question waiting on Petre is orange either way.
    TextBlock TextOf(TaskLine line)
    {
        var text = new TextBlock
        {
            Tag = "task-text",
            FontSize = Text(9.5),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 4, 0),
        };
        // A closed line in its undo window is struck through.
        if (line.Closing) text.TextDecorations = TextDecorations.Strikethrough;
        var detailInk = line.Status == TaskStatus.WaitingOnYou ? TaskOrange : line.Claudes ? TaskInk : TaskDimInk;
        text.Inlines.Add(new Run(line.Title) { Foreground = line.Claudes ? TaskTitleInk : TaskInk });
        if (line.Detail.Length == 0) return text;
        text.Inlines.Add(new Run(TaskDetailSeparator) { Foreground = line.Claudes ? TaskTitleInk : TaskDimInk });
        text.Inlines.Add(new Run(line.Detail) { Foreground = detailInk });
        return text;
    }

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
            line.Detail,
            line.Quadrant == Quadrant.IV ? "" : $"{line.Quadrant.Numeral()}: {line.Quadrant.Words()}",
            line.WorkspaceName is { } name ? $"On {name}" : "On no workspace",
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

    // A line goes to its task. A note on no workspace has nowhere to go; a Claude task goes even on no workspace,
    // since a later pull request takes it to its session, which the App decides.
    void OnTaskLineClick(TaskLine line)
    {
        // The click picks the task, in both views, whether or not it has anywhere to go.
        selection.Select(line.Id);
        var goes = taskService?.Board.Tasks.TryFirst(t => t.Id == line.Id).Where(t => t.IsClaudes || t.WorkspaceId.HasValue).HasValue ?? false;
        if (!goes) return;
        // Shown the way a row's failed switch is (Report), not only traced: a click that goes nowhere must say why.
        Report(GoToTask(line.Id));
    }

    // THE UNDO WINDOW (Petre: "when i click on done, there should be undo"; Ruling 10). The task is closed at once
    // through Close, so the store is the truth whatever happens next; its line stays in its place for six seconds,
    // struck through, with "Undo" where the tick or the close button was, and then leaves and the list settles.
    // The line is pending before Close pulses Changed, so the repaint that pulse causes already draws it struck
    // through rather than dropping it for a moment. A refused Close (the task was closed meanwhile) leaves nothing
    // pending. Several lines can be pending at once, each with its own timer.
    void CloseWithUndo(TaskLine line)
    {
        if (taskService is null) return;
        var index = shownTaskLines.ToList().FindIndex(l => l.Id == line.Id);
        var pending = new PendingClose(line with { Closing = true }, Math.Max(index, 0), line.Status, Disposable.Empty)
        {
            Question = taskService.Board.Tasks.TryFirst(t => t.Id == line.Id).Bind(t => t.Question),
            Selected = selection.IsSelected(line.Id),
        };
        pending = pending with { Timer = TaskUndoTimer(UndoWindow, () => ForgetPendingClose(pendingCloses.FirstOrDefault(p => p.Line.Id == line.Id), repaint: true)) };
        pendingCloses.Add(pending);
        taskService.Close(line.Id).TapError(_ => ForgetPendingClose(pending, repaint: true));
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

    // "+N more": the tasks window when the App gave one, else the list expanded in place, and folded back on the next
    // click.
    void OnTaskMoreClick()
    {
        if (OpenTasksWindow is { } open)
        {
            open();
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
        if (taskAddBox.Text.Length == 0) taskAddTarget = taskAddHere;
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
    // is the draft); nothing is handed back, since the keyboard is already where he took it.
    void EndTyping(bool clear)
    {
        if (taskAddBox is null) return;
        taskTyping = false;
        if (clear)
        {
            taskAddBox.Text = "";
            taskAddTarget = taskAddHere;
            PaintTaskChips();
            GiveTaskKeyboardBack();
        }
        else taskReturnTo = 0;
        PaintAddRow();
        UpdateFade();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (taskTyping) EndTyping(clear: false);
    }

    // The placeholder as the box stands: shown while the box is empty. (The chips under it are always shown.)
    void PaintAddRow()
    {
        if (taskAddBox is null || taskAddPlaceholder is null) return;
        taskAddPlaceholder.Visibility = taskAddBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // On every paint, while nobody is typing, the "here" chip is the workspace in front. An empty box's chip follows it
    // (and the placeholder with it, so it follows a desktop switch); a box holding text keeps the workspace it was
    // given, which then shows as the "other…" chip if it is no longer the one in front. The placeholder always names
    // the chosen chip's workspace (PaintPlaceholder).
    void FollowCurrentWorkspace()
    {
        if (taskAddPlaceholder is null || taskAddBox is null) return;
        var current = manager.CurrentWorkspaceId;
        PaintPlaceholder();
        if (taskTyping) return;
        taskAddHere = current;
        if (taskAddBox.Text.Length == 0) taskAddTarget = current;
        PaintTaskChips();
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
        if (back != 0 && !IsOwnWindow(back) && WindowAlive(back) && OnCurrentDesktop(back)) HandForegroundTo(back);
    }

    void OnTaskAddKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitTaskNote();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            EndTyping(clear: true);
        }
    }

    // Enter: a Backlog note where the chips say. A title of nothing is refused by the board and the box keeps the
    // keyboard.
    void CommitTaskNote()
    {
        if (taskService is null || taskAddBox is null) return;
        taskService.AddNote(taskAddBox.Text, taskAddTarget)
            .Tap(_ => EndTyping(clear: true))
            .TapError(err => ClickTrace.Write($"task list: note not added: {err}"));
    }

    // A chip, or a workspace picked from "other…"'s menu. The chips are always shown, so a chip can be the first thing
    // pressed, on an idle box or on the way back to text left in it (EndTyping keeps the text). That press must
    // take the keyboard exactly as a click in the box does (BeginTyping): otherwise the bar is focused but fades, has
    // nowhere to hand the keyboard back to after Enter or Esc, and does not notice losing the foreground again.
    // BeginTyping keeps the chip a box with text was given, so the choice made here is set after it.
    void ChooseTaskTarget(Maybe<Guid> target)
    {
        if (!taskTyping) BeginTyping();
        taskAddTarget = target;
        PaintTaskChips();
        taskAddBox?.Focus();
    }

    // "other…": every workspace but the current one, as a menu under the chip, each drawn as its chip in its colour and
    // grouped as on the bar (WorkspaceChipMenu), the chosen one ticked. Held open the way the bar's other menus are
    // (HoldFadeWhileOpen), so the bar neither fades nor rebuilds out from under it. Internal, and answering the menu,
    // for the tests, which pick from it.
    internal ContextMenu? OpenOtherWorkspaces()
    {
        if (taskChipOther is null) return null;
        var entries = WorkspaceChipMenu.Entries(manager.State, withNone: false, w => !taskAddHere.HasValue || w.Id != taskAddHere.Value);
        if (entries.Count == 0) return null;
        var menu = WorkspaceChipMenu.Build(entries, taskAddTarget, ChooseTaskTarget, taskChipOther.Button, ChipMenuWidth);
        HoldFadeWhileOpen(menu);
        menu.IsOpen = true;
        return menu;
    }

    // The chips as the choice stands: the current workspace's (hidden on no workspace), "none", and "other…", which
    // takes the name of the workspace chosen from its menu.
    //
    // EXACTLY ONE CHIP READS AS CHOSEN (Petre's live check: he picked "none", its edge went blue, but the "Deskbar" chip
    // stayed filled in its purple and still looked chosen). Chosen: filled (a workspace chip in its own colour, "none"
    // and "other…" in the neutral chip fill) with the blue edge and the bright ink. Not chosen: no fill and dim ink,
    // with only a thin edge, in the workspace's colour for a workspace chip and the neutral fill for the other two, so a
    // chip never looks chosen merely by being coloured. The edge is 1 DIP either way, so choosing moves nothing.
    void PaintTaskChips()
    {
        if (taskChipHere is null || taskChipNone is null || taskChipOther is null) return;
        var here = taskAddHere;
        var other = taskAddTarget.HasValue && !taskAddTarget.Equals(here) ? taskAddTarget : Maybe<Guid>.None;

        taskChipHere.Button.Visibility = here.HasValue ? Visibility.Visible : Visibility.Collapsed;
        if (here.HasValue)
            PaintChip(taskChipHere, WorkspaceName(here.Value).GetValueOrDefault("this workspace"), workspaceAccents.GetValueOrDefault(here.Value),
                chosen: taskAddTarget.Equals(here));
        PaintChip(taskChipNone, "none", null, chosen: taskAddTarget.HasNoValue);
        PaintChip(taskChipOther, other.HasValue ? WorkspaceName(other.Value).GetValueOrDefault(OtherChipText) : OtherChipText,
            other.HasValue ? workspaceAccents.GetValueOrDefault(other.Value) : null, chosen: other.HasValue);
        PaintPlaceholder();
    }

    // `accent` is the workspace's rail colour, or null for "none" and an unpicked "other…" (and a workspace without a
    // colour), which take the neutral chip fill.
    void PaintChip(TaskChipParts chip, string text, Brush? accent, bool chosen)
    {
        var fill = accent is null ? TaskButtonFill : ChipFill(accent);
        chip.Label.Text = text;
        chip.Label.Foreground = chosen ? TaskInk : TaskDimInk;
        chip.Face.Background = chosen ? fill : Brushes.Transparent;
        chip.Face.BorderBrush = chosen ? TaskPickedEdge : fill;
        AutomationProperties.SetName(chip.Button, text);
    }

    // The empty box says where a note typed now would go: the chosen chip's workspace ("Add to Sparrow…", also for one
    // picked through "other…"), or "Add…" for none. Not the workspace in front: after picking "none" the box must not
    // still promise "Add to Deskbar…".
    void PaintPlaceholder()
    {
        if (taskAddPlaceholder is not null) taskAddPlaceholder.Text = PlaceholderFor(taskAddTarget.Bind(WorkspaceName));
    }

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
    /// <summary>When the task appeared, for the age the line shows (TaskAge). Not part of the line's shape: the age is
    /// rewritten in place once a minute, never by a repaint.</summary>
    public DateTimeOffset Created { get; init; }

    /// <summary>The task's two flags (Task 12), drawn as the line's toggles.</summary>
    public bool Important { get; init; }

    public bool Urgent { get; init; }

    public Quadrant Quadrant => QuadrantOf.Flags(Important, Urgent);

    /// <summary>
    /// Which group the line is in: the pinned Waiting on you block, or its quadrant. Lines of one group sit together
    /// (TaskBoard.Pile), so a change of key between neighbours is where a group starts.
    /// </summary>
    public string GroupKey => Status == TaskStatus.WaitingOnYou ? "waiting" : Quadrant.Numeral();

    /// <summary>Whether this line is the first of its group, below another group (FloatingBar.WithGroupStarts).</summary>
    public bool StartsGroup { get; init; }
}
