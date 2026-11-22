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
//                   keyboard (BeginTyping). Under it, while it has the keyboard or holds text, the chips choosing
//                   where the note goes: the current workspace (ticked to begin with), "none" and "other…", which
//                   lists the other workspaces; they wrap onto a second line on a narrow bar rather than clip. Enter
//                   adds a Backlog note through TaskService.AddNote and Esc clears the box; either way the keyboard
//                   goes back to the window Petre was typing in. Clicking away leaves the text where it is.
//   the lines       the first three of the pile (Waiting on you, then Done, then the rest; TaskBoard.Pile). A
//                   Claude line (a task with a session) has an orange edge and tint, a CLAUDE tag, the VS Code icon
//                   of its session's window when one is found, then the task's title in a tan ink and, after a dot,
//                   the question while it waits on Petre, else its status line, with "no live session" when every
//                   session has ended. Petre's notes have a plain tick box that closes them. A Done Claude task has
//                   a close button. Every line ends with its workspace's chip in that row's own colour, or "none".
//                   A line just closed stays in its place for six seconds, struck through, with "Undo" where its
//                   tick or close button was (Petre: "when i click on done, there should be undo"; Ruling 10).
//   "+N more"       a small line under the last line when more open tasks are out of sight. When some of the hidden
//                   ones wait on Petre it says so in the asking orange ("+2 waiting"), so no ask is out of sight
//                   without a sign (spec, "The bar's order"). Until the task window exists (a later pull request sets
//                   OpenTasksWindow) it expands the list in place and reads "Less" at the end of it. Under the lines
//                   rather than in a header, so it does not move under the pointer when the list opens.
//
// CLICKS. A line goes to its task through GoToTask, which the App points at the workspace switch (a later pull
// request sends a Claude task to its session instead); a note on no workspace has nowhere to go, so its click does
// nothing. The tick box and the close button call TaskService.Close at once (the store stays the truth; only Petre
// closes, and these are his clicks), and "Undo" calls Reopen.
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

    // What "+N" opens once the task window exists. Null until then, and the button expands the list in place.
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
        || arrangeDrag is not null || arrangeDropQueued || draggingWindow || openMenu?.IsOpen == true;

    // For the tests: whether the list is on screen, what "+N more" says ("" while it is hidden), whether the box has
    // the keyboard, where a note added now would go, what the empty box says, and the lines on show.
    internal bool TaskListShown => TaskListLine.Visibility == Visibility.Visible;
    internal string TaskListMoreText => taskMoreButton is { Visibility: Visibility.Visible } ? taskMoreLabel?.Text ?? "" : "";
    internal bool TaskListAdding => taskTyping;
    internal Maybe<Guid> TaskAddTarget => taskAddTarget;
    internal string TaskAddPlaceholder => taskAddPlaceholder?.Text ?? "";
    internal IReadOnlyList<TaskLine> TaskListLines => shownTaskLines;

    // Three lines, the number the spec gives ("the three most recent tasks").
    const int TaskListSlots = 3;

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

    sealed record PendingClose(TaskLine Line, int Index, TaskStatus Was, IDisposable Timer);

    // The click guard's two facts (TaskClickIgnored): the click count of the last press on one of the list's guarded
    // buttons, and when the lines were last redrawn.
    int taskPressClicks;
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
            t.IsClaudes ? SessionOf(t).Bind(s => sessionWindow(s.Folder)) : Maybe<nint>.None)).ToArray();

    // What follows the title: the question while the task waits on Petre, else its status line; and for a Claude task
    // whose sessions have all ended, "no live session" after that (spec, "A task whose sessions have all ended stays
    // open and says 'no live session'").
    static string DetailOf(TaskItem task) =>
        string.Join(TaskDetailSeparator, new[]
        {
            task.Status == TaskStatus.WaitingOnYou && task.Question.HasValue ? task.Question.Value : task.StatusLine,
            task.IsClaudes && !task.HasLiveSession ? NoLiveSession : "",
        }.Where(part => part.Length > 0));

    // The session whose window the line shows: a live one before an ended one, the latest joined first. The spec's
    // "the one waiting on Petre" cannot be told apart yet (nothing records which session asked), and while a task
    // waits it is its live session that asked.
    static Maybe<TaskSession> SessionOf(TaskItem task) =>
        task.Sessions.OrderByDescending(s => s.IsLive).ThenByDescending(s => s.Joined).TryFirst();

    // The words of each status, as the spec spells them.
    static string StatusName(TaskStatus status) => status switch
    {
        TaskStatus.Backlog => "Backlog",
        TaskStatus.InProgress => "In progress",
        TaskStatus.WaitingOnYou => "Waiting on you",
        TaskStatus.Done => "Done",
        TaskStatus.Closed => "Closed",
        _ => status.ToString(),
    };

    // The line under the list: how many open tasks are out of sight, naming how many of them wait on Petre; "Less" at
    // the end of the list expanded in place.
    static string MoreText(int hidden, int waiting, bool expanded) =>
        expanded ? "Less"
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
        // them cut off). Shown only while the box has the keyboard or holds text (PaintAddRow).
        taskChipHere = TaskChip("", () => ChooseTaskTarget(taskAddHere));
        taskChipNone = TaskChip("none", () => ChooseTaskTarget(Maybe<Guid>.None));
        taskChipOther = TaskChip(OtherChipText, OpenOtherWorkspaces);
        taskAddOn = new TextBlock { Text = "on", Foreground = TaskDimInk, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 1, 4, 1) };
        taskAddChips = new WrapPanel
        {
            Tag = "task-add-chips",
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 3, 0, 0),
            Children = { taskAddOn, taskChipHere.Button, taskChipNone.Button, taskChipOther.Button },
        };
        var addRow = new StackPanel { Margin = new Thickness(0, 0, 0, 3), Children = { box, taskAddChips } };

        taskLinesPanel = new StackPanel();
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

        TaskListLine.Child = new TakesNoWidth { Child = new StackPanel { Children = { taskProblemHost, addRow, taskLinesScroller, taskMoreButton } } };
        ApplyTaskListTextSize();
        PaintAddRow();
        Closed += (_, _) =>
        {
            taskChanges?.Dispose();
            // Every closed line's timer goes with the bar; the tasks are closed already, nothing is left to do.
            pendingCloses.ForEach(pending => pending.Timer.Dispose());
            pendingCloses.Clear();
        };
    }

    // A one-shot dispatcher timer for the undo window (TaskUndoTimer's default).
    static IDisposable DispatcherUndoTimer(TimeSpan after, Action then)
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
    internal void PaintTaskList()
    {
        // The rebuild paints the list itself once it has finished (Rebuild, Relayout).
        if (rebuilding || taskLinesPanel is null) return;

        // Not under a press or a drag anywhere on the bar (see TaskListMustWait). The rebuild that serves the wait ends
        // by painting the list, so nothing is lost, only put off until the press or the drag is over.
        if (TaskListMustWait)
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

        var open = Lines(taskService.Board, WorkspaceName, id => workspaceAccents.GetValueOrDefault(id), SessionWindow);
        // A closed line whose task is open again (reopened from the pipe, say) is not pending any more: the task is back
        // in the pile under its own line.
        pendingCloses.Where(p => open.Any(l => l.Id == p.Line.Id)).ToList().ForEach(p => ForgetPendingClose(p, repaint: false));
        if (open.Count <= TaskListSlots) taskListExpanded = false;
        var all = WithPendingCloses(open, pendingCloses);
        var problem = taskService.LoadProblem;
        var shape = string.Join("\n", all.Select(ShapeOf).Prepend(
            $"{taskListExpanded}|{OpenTasksWindow is null}|{TextOffset}|{problem.GetValueOrDefault("")}"));
        if (shape == lastTaskListShape) return;
        lastTaskListShape = shape;

        shownTaskLines = taskListExpanded ? all : all.Take(TaskListSlots).ToArray();
        taskLinesPanel.Children.Clear();
        if (taskProblemHost is not null)
        {
            taskProblemHost.Child = problem.HasValue ? LoadProblemRow(problem.Value) : null;
            taskProblemHost.Visibility = problem.HasValue ? Visibility.Visible : Visibility.Collapsed;
        }
        shownTaskLines.ToList().ForEach(line => taskLinesPanel.Children.Add(TaskLineButton(line)));
        // The lines just moved, so the click guard distrusts a click for the next half second (TaskClickIgnored).
        taskLinesChangedAt = TaskClickClock();
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
        $"{(line.Colour as SolidColorBrush)?.Color}|{line.Window.Match(w => w.ToString(), () => "")}|{line.Closing}";

    // "+N more" counts the open tasks out of sight; a closed line in its undo window is not an open task, so it is
    // neither counted nor allowed to hide one from the count.
    void PaintTaskMore(IReadOnlyList<TaskLine> open)
    {
        if (taskMoreButton is null || taskMoreLabel is null) return;
        var onShow = shownTaskLines.Select(l => l.Id).ToHashSet();
        var hidden = open.Where(l => !onShow.Contains(l.Id)).ToArray();
        var waiting = hidden.Count(l => l.Status == TaskStatus.WaitingOnYou);
        taskMoreButton.Visibility = hidden.Length > 0 || taskListExpanded ? Visibility.Visible : Visibility.Collapsed;
        taskMoreLabel.Text = MoreText(hidden.Length, waiting, taskListExpanded);
        taskMoreLabel.Foreground = waiting > 0 && !taskListExpanded ? TaskOrange : TaskDimInk;
        taskMoreButton.ToolTip = taskListExpanded ? "Show the first three" : $"{hidden.Length} more {(hidden.Length == 1 ? "task" : "tasks")}";
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
        var button = new Button
        {
            Tag = line,
            Content = edge,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 1, 0, 1),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Template = TaskLineTemplate,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Focusable = false,
            Cursor = Cursors.Hand,
            ToolTip = line.Closing ? "Closed. Undo brings it back." : TipOf(line),
        };
        AutomationProperties.SetName(button, line.Title);
        // The click guard, as on the tick and the close button (TaskClickIgnored).
        button.PreviewMouseLeftButtonDown += (_, e) => taskPressClicks = e.ClickCount;
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
    // A Done Claude task just closed has "Undo" in the close button's place.
    IEnumerable<UIElement> RightOf(TaskLine line) =>
        new UIElement?[]
        {
            WorkspaceChip(line),
            line.Status == TaskStatus.Done && line.Claudes
                ? line.Closing
                    ? UndoButton(line)
                    : TaskButton("Close task", new TextBlock { Text = "✕", Foreground = TaskInk, FontSize = Text(8.5), Margin = new Thickness(5, 0, 5, 0) },
                        () => CloseWithUndo(line), "Done: close this task", ignoredWhen: TaskClickIgnored)
                : null,
        }.OfType<UIElement>();

    // "Undo" on a line just closed: the asking orange's opposite, the plain bright ink, in a small pill.
    Button UndoButton(TaskLine line) =>
        TaskButton("Undo", new Border
        {
            Background = TaskButtonFill,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 0, 6, 1),
            Child = new TextBlock { Text = "Undo", Foreground = TaskInk, FontSize = Text(7.5) },
        }, () => UndoClose(line.Id), "Bring the task back", ignoredWhen: DoubleClickIgnored);

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
    Border WorkspaceChip(TaskLine line) => new()
    {
        Tag = "task-workspace",
        Background = line.WorkspaceName is null ? TaskNoneFill : ChipFill(line.Colour),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(7, 0, 7, 1),
        Margin = new Thickness(4, 0, 0, 0),
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
            $"{StatusName(line.Status)}: {line.Title}",
            line.Detail,
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
        var pending = new PendingClose(line with { Closing = true }, Math.Max(index, 0), line.Status, Disposable.Empty);
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

    // Undo: the task comes back as it was. A note, and any task that was not Done, through Reopen (Backlog, or In
    // progress while a session is live on it). A Done task through SetStatus Done, the board's way back to Done for a
    // closed task, since Reopen would make it In progress or Backlog; becoming Done stamps its place in the Done group
    // as now (TaskBoard.BecameDone), which puts it at the top of that group.
    void UndoClose(Guid id)
    {
        if (taskService is null || pendingCloses.FirstOrDefault(p => p.Line.Id == id) is not { } pending) return;
        ForgetPendingClose(pending, repaint: false);
        var reopened = pending.Was == TaskStatus.Done ? taskService.SetStatus(id, TaskStatus.Done) : taskService.Reopen(id);
        // Reopen pulses Changed, which repaints; a refusal (the task is gone) still needs the line taken away.
        reopened.TapError(_ => PaintTaskList());
    }

    // "+N more": the task window once it exists, else the list expanded in place, and folded back on the next click.
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

    // The placeholder and the chips as the box stands: the placeholder while it is empty, the chips while it has the
    // keyboard or holds text.
    void PaintAddRow()
    {
        if (taskAddBox is null || taskAddPlaceholder is null || taskAddChips is null) return;
        var empty = taskAddBox.Text.Length == 0;
        taskAddPlaceholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        taskAddChips.Visibility = taskTyping || !empty ? Visibility.Visible : Visibility.Collapsed;
    }

    // On every paint: the placeholder names the workspace in front, so it follows a desktop switch; and while nobody is
    // typing, the "here" chip is that workspace too. An empty box's chip follows it; a box holding text keeps the
    // workspace it was given, which then shows as the "other…" chip if it is no longer the one in front.
    void FollowCurrentWorkspace()
    {
        if (taskAddPlaceholder is null || taskAddBox is null) return;
        var current = manager.CurrentWorkspaceId;
        taskAddPlaceholder.Text = PlaceholderFor(current.Bind(WorkspaceName));
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

    // A chip, or a workspace picked from "other…"'s menu. The chips also show while the box merely holds text, after a
    // click away (EndTyping keeps the text), so a chip can be the first thing pressed on the way back. That press must
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

    // "other…": every workspace but the current one, as a menu under the chip. Held open the way the bar's other menus
    // are (HoldFadeWhileOpen), so the bar neither fades nor rebuilds out from under it.
    void OpenOtherWorkspaces()
    {
        if (taskChipOther is null) return;
        var menu = new ContextMenu { PlacementTarget = taskChipOther.Button, Placement = PlacementMode.Bottom };
        manager.State.Workspaces
            .Where(w => !taskAddHere.HasValue || w.Id != taskAddHere.Value)
            .ToList()
            .ForEach(w =>
            {
                // A TextBlock, not a string, so an underscore in a name is not read as an access key.
                var item = new MenuItem { Header = new TextBlock { Text = w.Name } };
                item.Click += (_, _) => ChooseTaskTarget(w.Id);
                menu.Items.Add(item);
            });
        if (menu.Items.Count == 0) return;
        HoldFadeWhileOpen(menu);
        menu.IsOpen = true;
    }

    // The chips as the choice stands: the current workspace's (hidden on no workspace), "none", and "other…", which
    // takes the name of the workspace chosen from its menu. The ticked one has the blue edge.
    void PaintTaskChips()
    {
        if (taskChipHere is null || taskChipNone is null || taskChipOther is null) return;
        var here = taskAddHere;
        var other = taskAddTarget.HasValue && !taskAddTarget.Equals(here) ? taskAddTarget : Maybe<Guid>.None;

        taskChipHere.Button.Visibility = here.HasValue ? Visibility.Visible : Visibility.Collapsed;
        if (here.HasValue) PaintChip(taskChipHere, WorkspaceName(here.Value).GetValueOrDefault("this workspace"), ChipFill(workspaceAccents.GetValueOrDefault(here.Value)));
        PaintChip(taskChipOther, other.HasValue ? WorkspaceName(other.Value).GetValueOrDefault(OtherChipText) : OtherChipText,
            other.HasValue ? ChipFill(workspaceAccents.GetValueOrDefault(other.Value)) : TaskButtonFill);

        taskChipHere.Face.BorderBrush = here.HasValue && taskAddTarget.Equals(here) ? TaskPickedEdge : Brushes.Transparent;
        taskChipNone.Face.BorderBrush = taskAddTarget.HasNoValue ? TaskPickedEdge : Brushes.Transparent;
        taskChipOther.Face.BorderBrush = other.HasValue ? TaskPickedEdge : Brushes.Transparent;
    }

    static void PaintChip(TaskChipParts chip, string text, Brush fill)
    {
        chip.Label.Text = text;
        chip.Face.Background = fill;
        AutomationProperties.SetName(chip.Button, text);
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
    sealed class TakesNoWidth : Decorator
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
            if (double.IsNaN(arrangedWidth) || Math.Abs(arrangeSize.Width - arrangedWidth) > 0.01)
            {
                arrangedWidth = arrangeSize.Width;
                Child.Measure(new Size(arrangedWidth, double.PositiveInfinity));
            }
            Child.Arrange(new Rect(arrangeSize));
            return arrangeSize;
        }
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
    Maybe<nint> Window, bool Closing = false);
