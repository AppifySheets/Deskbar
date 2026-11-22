using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
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
// WHAT IT SHOWS, top to bottom, inside the TaskListLine border at the top of the footer:
//
//   the header      "Tasks", the "+N" button when more than three tasks are open, and "+ Add" at the right.
//                   "+N" counts the hidden tasks; when some of them wait on Petre it says so, in the asking orange
//                   ("+2 waiting"), so no ask is out of sight without a sign (spec, "The bar's order"). Until the
//                   task window exists (a later pull request sets OpenTasksWindow) the button expands the list in
//                   place, and a second click folds it back to three.
//   the add box     shown by "+ Add": a text box, then the chips choosing where the note goes: the current
//                   workspace (ticked to begin with), "none" and "other…", which lists the other workspaces. Enter
//                   adds a Backlog note through TaskService.AddNote, Esc drops what was typed, and either way the
//                   keyboard goes back to the window Petre was typing in. A click into another window closes the box
//                   but keeps what was typed as a draft for the next "+ Add" (Controller Ruling 5). Built once and
//                   kept, so the "other…" menu always has its chip to hang from.
//   a failed load   "Tasks could not be read. ..." as the first line, the store's own reason in its tooltip and a ✕
//                   that dismisses it (TaskService.DismissLoadProblem). An empty list must not pass for "nothing to
//                   do" when the truth is "your tasks could not be read".
//   the lines       the first three of the pile (Waiting on you, then Done, then the rest; TaskBoard.Pile). A
//                   Claude line (a task with a session) has an orange edge and tint, a CLAUDE tag, the VS Code icon
//                   of its session's window when one is found, then the task's title in a tan ink and, after a dot,
//                   the question while it waits on Petre, else its status line, with "no live session" when every
//                   session has ended. Petre's notes have a plain tick box that closes them. A Done Claude task has
//                   a close button. Every line ends with its workspace's chip in that row's own colour, or "none".
//
// CLICKS. A line goes to its task through GoToTask, which the App points at the workspace switch (a later pull
// request sends a Claude task to its session instead); a note on no workspace has nowhere to go, so its click does
// nothing. The tick box and the close button call TaskService.Close: only Petre closes, and these are his clicks.
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

    // For the tests: whether the list is on screen, what "+N" says ("" while it is hidden), whether the add box is
    // open, where a note added now would go, and the lines on show.
    internal bool TaskListShown => TaskListLine.Visibility == Visibility.Visible;
    internal string TaskListMoreText => taskMoreButton is { Visibility: Visibility.Visible } ? taskMoreLabel?.Text ?? "" : "";
    internal bool TaskListAdding => taskAddPanel is { Visibility: Visibility.Visible };
    internal Maybe<Guid> TaskAddTarget => taskAddTarget;
    internal IReadOnlyList<TaskLine> TaskListLines => shownTaskLines;

    // Three lines, the number the spec gives ("the three most recent tasks").
    const int TaskListSlots = 3;
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
    TextBlock? taskHeaderLabel;
    TextBlock? taskMoreLabel;
    Button? taskMoreButton;
    TextBlock? taskAddLabel;
    StackPanel? taskAddPanel;
    TextBox? taskAddBox;
    TextBlock? taskAddOn;
    TextBlock? taskAddHint;
    TaskChipParts? taskChipHere;
    TaskChipParts? taskChipNone;
    TaskChipParts? taskChipOther;
    StackPanel? taskLinesPanel;

    // The list's state: folded to three or expanded, what was drawn last, the lines on show, and the add box's choice.
    bool taskListExpanded;
    string? lastTaskListShape;
    IReadOnlyList<TaskLine> shownTaskLines = [];
    Maybe<Guid> taskAddHere = Maybe<Guid>.None;
    Maybe<Guid> taskAddTarget = Maybe<Guid>.None;

    // A note put aside when the bar lost the keyboard mid-typing (PutTaskDraftAside): its text stays in the box and its
    // chip in taskAddTarget, for the next "+ Add". And the window to hand the keyboard back to after Enter or Esc.
    bool taskDraftKept;
    nint taskReturnTo;

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

    // "+N" over the hidden lines, naming how many of them wait on Petre; "Less" once expanded in place.
    static string MoreText(int hidden, int waiting, bool expanded) =>
        expanded ? "Less"
        : waiting == 0 ? $"+{hidden}"
        : waiting == hidden ? $"+{hidden} waiting"
        : $"+{hidden}, {waiting} waiting";

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
        taskHeaderLabel = new TextBlock { Text = "Tasks", Foreground = TaskDimInk, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 6, 0) };
        taskMoreLabel = new TextBlock { Foreground = TaskInk };
        taskMoreButton = TaskButton("More tasks", Pill(taskMoreLabel), OnTaskMoreClick);
        taskMoreButton.Visibility = Visibility.Collapsed;
        taskAddLabel = new TextBlock { Text = "+ Add", Foreground = TaskInk };
        var add = TaskButton("Add a task", Pill(taskAddLabel), StartAddingTask, "Add a note");
        DockPanel.SetDock(add, Dock.Right);
        var header = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 2), Children = { add, taskHeaderLabel, taskMoreButton } };

        // The add box. Its Enter and Esc are read in Preview, ahead of anything the box itself does with them.
        taskAddBox = new TextBox
        {
            Tag = "task-add-box",
            Background = TaskFieldFill,
            Foreground = TaskInk,
            CaretBrush = TaskInk,
            BorderBrush = TaskPickedEdge,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 2, 4, 2),
            ToolTip = "Enter to add, Esc to drop",
        };
        AutomationProperties.SetName(taskAddBox, "New task");
        taskAddBox.PreviewKeyDown += OnTaskAddKey;

        taskChipHere = TaskChip("", () => ChooseTaskTarget(taskAddHere));
        taskChipNone = TaskChip("none", () => ChooseTaskTarget(Maybe<Guid>.None));
        taskChipOther = TaskChip(OtherChipText, OpenOtherWorkspaces);
        taskAddOn = new TextBlock { Text = "on", Foreground = TaskDimInk, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 4, 0) };
        taskAddHint = new TextBlock
        {
            Text = "Enter to add, Esc to drop",
            Foreground = TaskDimInk,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(6, 0, 2, 0),
        };
        var chips = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 3, 0, 0) };
        new UIElement[] { taskAddOn, taskChipHere.Button, taskChipNone.Button, taskChipOther.Button }.ToList().ForEach(part =>
        {
            DockPanel.SetDock(part, Dock.Left);
            chips.Children.Add(part);
        });
        chips.Children.Add(taskAddHint);
        taskAddPanel = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 2, 0, 3), Children = { taskAddBox, chips } };

        taskLinesPanel = new StackPanel();
        TaskListLine.Child = new TakesNoWidth { Child = new StackPanel { Children = { header, taskAddPanel, taskLinesPanel } } };
        ApplyTaskListTextSize();
        Closed += (_, _) => taskChanges?.Dispose();
    }

    // A small button in the bar's style (the strip's template, so it hovers like the bar's other small controls).
    // Marks the press consumed, as every button on the bar does, so the bar's own release does not read it as a click
    // of its own; and marks the Click handled, because a line is itself a button and the tick box and the close button
    // sit inside it: Click bubbles, and the line must not also act on them.
    Button TaskButton(string name, UIElement content, Action press, string? tip = null)
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
        button.Click += (_, e) =>
        {
            e.Handled = true;
            MarkPressConsumed();
            press();
        };
        return button;
    }

    // The header's grey pill around a label ("+2", "+ Add").
    static Border Pill(TextBlock label) =>
        new() { Background = TaskButtonFill, CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 1, 8, 1), Child = label };

    // A chip of the add box. Its edge is always 1 DIP, coloured only when the chip is the one ticked, so ticking
    // another chip never moves anything (CLAUDE.md: present and recoloured, not conditional thickness).
    TaskChipParts TaskChip(string text, Action press)
    {
        var label = new TextBlock { Text = text, Foreground = TaskInk, VerticalAlignment = VerticalAlignment.Center };
        var face = new Border
        {
            Background = TaskButtonFill,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(7, 0, 7, 1),
            Child = label,
        };
        var button = TaskButton(text, face, press);
        button.Margin = new Thickness(0, 0, 4, 0);
        return new TaskChipParts(button, face, label);
    }

    // The text built once and kept takes the bar's text size here (ApplyTextSize); the lines take it as they are made.
    void ApplyTaskListTextSize()
    {
        Sized(taskHeaderLabel, 8.5);
        Sized(taskMoreLabel, 8.5);
        Sized(taskAddLabel, 8.5);
        Sized(taskAddOn, 7.5);
        Sized(taskAddHint, 7.5);
        new[] { taskChipHere, taskChipNone, taskChipOther }.ToList().ForEach(chip => Sized(chip?.Label, 7.5));
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
            if (TaskListAdding) PutTaskDraftAside();
            return;
        }

        var lines = Lines(taskService.Board, WorkspaceName, id => workspaceAccents.GetValueOrDefault(id), SessionWindow);
        if (lines.Count <= TaskListSlots) taskListExpanded = false;
        var problem = taskService.LoadProblem;
        var shape = string.Join("\n", lines.Select(ShapeOf).Prepend(
            $"{taskListExpanded}|{OpenTasksWindow is null}|{TextOffset}|{problem.GetValueOrDefault("")}"));
        if (shape == lastTaskListShape) return;
        lastTaskListShape = shape;

        shownTaskLines = taskListExpanded ? lines : lines.Take(TaskListSlots).ToArray();
        taskLinesPanel.Children.Clear();
        if (problem.HasValue) taskLinesPanel.Children.Add(LoadProblemRow(problem.Value));
        shownTaskLines.ToList().ForEach(line => taskLinesPanel.Children.Add(TaskLineButton(line)));
        PaintTaskMore(lines);
    }

    // Everything a line shows, as text, so two paints can be compared without drawing anything.
    static string ShapeOf(TaskLine line) =>
        $"{line.Id}|{line.Status}|{line.Title}|{line.Detail}|{line.Claudes}|{line.WorkspaceName}|" +
        $"{(line.Colour as SolidColorBrush)?.Color}|{line.Window.Match(w => w.ToString(), () => "")}";

    void PaintTaskMore(IReadOnlyList<TaskLine> lines)
    {
        if (taskMoreButton is null || taskMoreLabel is null) return;
        var hidden = lines.Skip(TaskListSlots).ToArray();
        var waiting = hidden.Count(l => l.Status == TaskStatus.WaitingOnYou);
        taskMoreButton.Visibility = hidden.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        taskMoreLabel.Text = MoreText(hidden.Length, waiting, taskListExpanded);
        taskMoreLabel.Foreground = waiting > 0 && !taskListExpanded ? TaskOrange : TaskInk;
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
            ToolTip = TipOf(line),
        };
        AutomationProperties.SetName(button, line.Title);
        button.Click += (_, _) =>
        {
            MarkPressConsumed();
            OnTaskLineClick(line);
        };
        return button;
    }

    // Left of the text: the CLAUDE tag and the session's window icon on Claude's lines, the tick box on a note.
    IEnumerable<UIElement> LeftOf(TaskLine line) =>
        line.Claudes
            ? new UIElement?[] { ClaudeTag(), line.Window.HasValue ? WindowIconOf(line.Window.Value) : null }.OfType<UIElement>()
            : [TaskButton("Tick off", new Border
            {
                Width = 11,
                Height = 11,
                BorderBrush = TaskTickEdge,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Background = Brushes.Transparent,
                Margin = new Thickness(1, 0, 1, 0),
            }, () => taskService?.Close(line.Id), "Tick off: closes the note")];

    // Right of the text, from the edge in: the workspace chip, then, on a Done Claude task, the close button, since a
    // Done task waits for Petre to look and close it (spec, "A Done Claude task shows a close button on its line"). The
    // button is the Done mark: a "Done" word beside it was tried and, on a bar of the default width, took the room the
    // title needed. The line's tooltip names the status.
    IEnumerable<UIElement> RightOf(TaskLine line) =>
        new UIElement?[]
        {
            WorkspaceChip(line),
            line.Status == TaskStatus.Done && line.Claudes
                ? TaskButton("Close task", new TextBlock { Text = "✕", Foreground = TaskInk, FontSize = Text(8.5), Margin = new Thickness(5, 0, 5, 0) },
                    () => taskService?.Close(line.Id), "Done: close this task")
                : null,
        }.OfType<UIElement>();

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
        GoToTask(line.Id).TapError(err => ClickTrace.Write($"task list: going to {line.Id} failed: {err}"));
    }

    // "+N": the task window once it exists, else the list expanded in place, and folded back on the next click.
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

    // "+ Add": the box opens with the current workspace's chip ticked, or with the draft a click elsewhere put aside,
    // its text and its chip as they were (see PutTaskDraftAside). The bar takes the keyboard the way quick find's box
    // does (QuickFindWindow.Open: Activate, then focus the box), since a key goes to the foreground window and the bar
    // may not be it when the click arrives through a strip or after a menu.
    //
    // First it notes where Petre was typing, to hand the keyboard back there after Enter or Esc (GiveTaskKeyboardBack).
    // The language switcher's rule finds that window (FollowForeground): the window in front when it is one you type
    // in, else the last such window the foreground hook saw, since the click on the bar has usually made the bar the
    // window in front already.
    void StartAddingTask()
    {
        if (taskService is null || taskAddPanel is null || taskAddBox is null) return;
        FollowForeground();
        taskReturnTo = languageTarget;
        taskAddHere = manager.CurrentWorkspaceId;
        if (!taskDraftKept) taskAddTarget = taskAddHere;
        taskDraftKept = false;
        PaintTaskChips();
        taskAddPanel.Visibility = Visibility.Visible;
        UpdateFade();
        Activate();
        taskAddBox.Focus();
        taskAddBox.CaretIndex = taskAddBox.Text.Length;
    }

    // Enter or Esc: the box closes, empty, and the keyboard goes back to where Petre was typing.
    void StopAddingTask()
    {
        if (taskAddPanel is null || taskAddBox is null) return;
        taskAddBox.Text = "";
        taskDraftKept = false;
        taskAddPanel.Visibility = Visibility.Collapsed;
        UpdateFade();
        GiveTaskKeyboardBack();
    }

    // THE BAR LOST THE KEYBOARD WHILE A NOTE WAS BEING TYPED (Controller Ruling 5): Petre clicked into another window.
    // The box closes, so the bar is free to fade again (HoldsFullStrength) and no box sits open that Esc can no longer
    // reach, but what he typed is KEPT as a draft with its chip, and the next "+ Add" brings it back. Nothing is handed
    // back: the keyboard is already where he took it.
    void PutTaskDraftAside()
    {
        if (taskAddPanel is null || !TaskListAdding) return;
        taskDraftKept = true;
        taskReturnTo = 0;
        taskAddPanel.Visibility = Visibility.Collapsed;
        UpdateFade();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        PutTaskDraftAside();
    }

    // The window noted when the box opened gets the foreground back, the way the language switcher hands it back
    // (HandForegroundTo), so Petre's next keystrokes land where he was typing rather than in a bar with nothing to
    // type into. Only a window that still exists and is not one of Deskbar's own (the bar included).
    void GiveTaskKeyboardBack()
    {
        var back = taskReturnTo;
        taskReturnTo = 0;
        if (back != 0 && !IsOwnWindow(back) && WindowAlive(back)) HandForegroundTo(back);
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
            StopAddingTask();
        }
    }

    // Enter: a Backlog note where the chips say. A title of nothing is refused by the board and the box stays open.
    void CommitTaskNote()
    {
        if (taskService is null || taskAddBox is null) return;
        taskService.AddNote(taskAddBox.Text, taskAddTarget)
            .Tap(_ => StopAddingTask())
            .TapError(err => ClickTrace.Write($"task list: note not added: {err}"));
    }

    void ChooseTaskTarget(Maybe<Guid> target)
    {
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
    // to fit. Measured again at that width on arrange, so the trimming is worked out for the width it really has.
    sealed class TakesNoWidth : Decorator
    {
        protected override Size MeasureOverride(Size constraint)
        {
            if (Child is null) return new Size(0, 0);
            Child.Measure(constraint);
            return new Size(0, Child.DesiredSize.Height);
        }

        protected override Size ArrangeOverride(Size arrangeSize)
        {
            if (Child is null) return arrangeSize;
            Child.Measure(new Size(arrangeSize.Width, double.PositiveInfinity));
            Child.Arrange(new Rect(arrangeSize));
            return arrangeSize;
        }
    }
}

/// <summary>
/// One task's line as the bar draws it: the task's id, title and detail (the question while it waits on Petre, else
/// its status line, plus "no live session" when its sessions have all ended), its status, whether it is Claude's (it
/// has a session), its workspace's name and row colour (none on no workspace), and the VS Code window of its session
/// when one is found. Made by <see cref="FloatingBar.Lines"/>.
/// </summary>
internal sealed record TaskLine(Guid Id, string Title, string Detail, TaskStatus Status, bool Claudes, string? WorkspaceName, Brush? Colour, Maybe<nint> Window);
