using CSharpFunctionalExtensions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// A TASK LINE'S RIGHT-CLICK MENU AND ITS IN-PLACE EDITOR ON THE BAR (Ruling 44; Petre: "task rightclick should let me
// edit it first, first option"). Design: docs/superpowers/specs/2026-11-22-tasks-board-design.md, "The bar".
//
// Right-clicking a task line opens its menu, Edit first: Edit, then ONE Claude item (task b6f068b1), "Open Claude" on a
// Claude task (task 7f61af2e, the double-click's go without the click that opens the task) or "Start Claude on this" on a
// task no session has joined (Task 9, when the App wires it), then the line's other actions (the two flags, Move to workspace…, Close). Held open the way the bar's other menus are (HoldFadeWhileOpen): the bar neither fades nor
// repaints under it.
//
// Edit opens an editor in the line itself, over the title: a box with the whole title (a title may hold line breaks;
// the line shows its first line). Enter saves through TaskService.Rename, the window's editor and `task rename`'s one
// path; Shift+Enter adds a line break; Esc, a click into another app or the box losing the keyboard cancels. While it is
// open:
//   * no paint runs under it (TaskListMustWait), so a change arriving from a session never takes the text away; the
//     list is painted once it closes;
//   * the bar holds full strength (HoldsFullStrength), as it does while a note is typed;
//   * the bar has the keyboard (Activate, then the box), and when the editor closes the keyboard goes back to the
//     window Petre was typing in when he opened it, as the add box does (GiveTaskKeyboardBack's rule: only a window
//     that still exists, is not Deskbar's own and is on the desktop in front).
public partial class FloatingBar
{
    // The task being edited, its box, and where the keyboard goes back to when the editor closes.
    Guid? editingTask;
    TextBox? taskEditBox;
    nint taskEditReturnTo;

    /// <summary>Whether a task line's editor is open (holds the bar at full strength and holds paints back).</summary>
    internal bool TaskEditing => editingTask is not null;

    /// <summary>For the tests: the open editor, if any.</summary>
    internal TextBox? TaskEditor => taskEditBox;

    /// <summary>For the tests: the last line menu opened by a right-click.</summary>
    internal ContextMenu? TaskLineMenu { get; private set; }

    /// <summary>
    /// "Start Claude on this" (Task 9): the App's ClaudeTaskActions.Offer, which asks first on a task with a running
    /// session, starts in the task's one folder or answers the pick list it opened. Unset, the item is not shown.
    /// </summary>
    internal Func<Guid, ContextMenu?>? StartClaude { get; init; }

    /// <summary>"Open in window" (task dca4ebeb): the App's TasksBoard.OpenWindow. Unset, the item is not shown.</summary>
    internal Action<Guid>? OpenTaskWindow { get; init; }

    /// <summary>
    /// "Open parent" on a subtask's line (task 54b39c4c): the App's TasksBoard.OpenParent, given the parent's id, which
    /// shows the parent in the tasks panel. Unset, the item is not shown.
    /// </summary>
    internal Action<Guid>? OpenParentTask { get; init; }

    // The title, or on the line being edited, the editor in its place.
    UIElement TitleOrEditor(TaskLine line) => editingTask == line.Id && !line.Closing ? TaskEditBox(line) : TextOf(line);

    // Right-click on a line: its menu at the pointer (see the header). Wired by TaskLineButton.
    void WireTaskLineMenu(Button button, TaskLine line) =>
        button.PreviewMouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            OpenTaskLineMenu(line.Id);
        };

    /// <summary>
    /// A line's menu, Edit first, then its one Claude item (Open Claude, or "Start Claude on this" when wired), its flags, Move to workspace… and Close. Internal, and answering the menu, for
    /// the tests. Nothing for a line in its undo window, whose task is closed.
    /// </summary>
    internal ContextMenu? OpenTaskLineMenu(Guid id)
    {
        var line = shownTaskLines.FirstOrDefault(l => l.Id == id);
        if (taskService is null || line is null || line.Closing) return null;
        // The bar as its target, for the bar's screen scaling (OwnMenus.AtPointer; task 4b6cc9a9).
        var menu = OwnMenus.AtPointer(this);
        menu.Items.Add(TaskMenuItem("Edit", () => BeginTaskEdit(id)));
        // OPEN IN WINDOW (task dca4ebeb): the task in a window of its own (the App's TasksBoard.OpenWindow), right after
        // Edit as in the panel's menu. Run once this menu has closed, so the new window is not activated under it.
        if (OpenTaskWindow is { } openWindow)
            menu.Items.Add(TaskMenuItem(TasksWindow.OpenInWindowText, () => Dispatcher.BeginInvoke(() => openWindow(id),
                System.Windows.Threading.DispatcherPriority.Input)));
        // OPEN CLAUDE (task 7f61af2e; Petre: "make it possible to open claude without opening the task first"). A line's
        // single click opens the task in the tasks panel (Ruling 54), so the double-click that reaches the session opened
        // the panel on its first press, and the panel's strip moved every window on the screen on the way. This item is
        // the double-click's go (OnTaskLineGo: its workspace, then its session's window through GoToTask) with no click
        // on the line, so nothing opens first. Only on a Claude task: a note has no session, and its go is the workspace
        // switch a row of the bar already gives. Run once this menu has closed, so the window it brings forward is not
        // brought forward under an open menu.
        if (line.Claudes)
            menu.Items.Add(TaskMenuItem(TasksWindow.OpenClaudeText, () => Dispatcher.BeginInvoke(() => OnTaskLineGo(line),
                System.Windows.Threading.DispatcherPriority.Input)));
        // Task 9: handing the task to Claude, right after Edit (the spec, "Where the clicks are"). Run once this menu has
        // closed, as "Move to workspace…" is: it may ask first, and may open a pick list of folders at the pointer, which
        // holds the bar still the way the bar's own menus do.
        // ONE CLAUDE ITEM PER MENU (task b6f068b1; Petre, with a screenshot of both: "open claude and start claude , no
        // need for both"). A task that has a session gets Open Claude above and not this one: Open goes to the session
        // and resumes it when it has ended, which is what he wants from a task Claude already has. A second session on
        // such a task comes from the "Start Claude on this" button in the task's own window (Open in window), which shows
        // once none of its sessions is running (task 9b6275bc), and from `deskbar task start` at any time. The thread's
        // row of buttons has one Claude button too (task 5e02e99f, TasksWindow.ThreadActions).
        else if (StartClaude is { } start)
            menu.Items.Add(TaskMenuItem(TasksWindow.StartClaudeText, () => Dispatcher.BeginInvoke(() =>
            {
                if (start(id) is { } pick) HoldFadeWhileOpen(pick);
            }, System.Windows.Threading.DispatcherPriority.Input)));
        // Subtasks (spec "Making subtasks", Petre's pick A): after "Start Claude on this", the add box with this line's task
        // as the parent, a sibling on a subtask's line. Run once this menu has closed, so the box can take the keyboard.
        menu.Items.Add(TaskMenuItem(AddSubtaskText, () => Dispatcher.BeginInvoke(() => BeginSubtaskAdd(id),
            System.Windows.Threading.DispatcherPriority.Input)));
        // REEVALUATE (task 3dd07539), after "Add subtask…" as in the panel's menu (TasksWindow.OpenMenuFor), on a Claude
        // task: its session checks where the task stands and fixes its question, status and status line. A refusal (a
        // closed task) is reported as the bar's other refusals are.
        if (line.Claudes)
            menu.Items.Add(TaskMenuItem(TasksWindow.ReevaluateText, () => Report(taskService.Reevaluate(id))));
        // OPEN PARENT (task 54b39c4c; Petre: "subtask should show its parent and have a way to open the parent easily"):
        // a subtask's line on the bar shows only "↳", so this item reaches the parent, shown in the tasks panel with its
        // thread open, as the panel's own "Open parent" does. Run once this menu has closed, as the items above are.
        if (line.ParentId is { } parentId && OpenParentTask is { } openParent)
            menu.Items.Add(TaskMenuItem(TasksWindow.OpenParentText, () => Dispatcher.BeginInvoke(() => openParent(parentId),
                System.Windows.Threading.DispatcherPriority.Input)));
        // COPY LINK (task 5016d533): the markdown link that opens this task in Deskbar from a pull request, an issue or
        // anywhere else (TaskLinkClipboard), as the panel's line menu has it.
        menu.Items.Add(TaskMenuItem(TaskLinkClipboard.CopyLinkText, () => TaskLinkClipboard.Copy(id)));
        menu.Items.Add(new Separator());
        menu.Items.Add(TaskMenuItem("Important", () => taskService?.SetFlag(id, TaskFlag.Important, !line.Important), line.Important));
        menu.Items.Add(TaskMenuItem("Urgent", () => taskService?.SetFlag(id, TaskFlag.Urgent, !line.Urgent), line.Urgent));
        // The workspace menu is a menu of its own, opened once this one has closed (as the window's "Move to…" does).
        menu.Items.Add(TaskMenuItem("Move to workspace…", () => Dispatcher.BeginInvoke(() => OpenTaskMoveMenu(id, LineButtonOf(id)),
            System.Windows.Threading.DispatcherPriority.Input)));
        menu.Items.Add(new Separator());
        menu.Items.Add(TaskMenuItem("Close", () => CloseWithUndo(line)));
        // MARK DEPLOYED (task 8470d176, phase 2), as on the tasks panel's line menu (TasksWindow.OpenMenuFor): a Done task
        // whose pull request in another repository is merged but not reported running gets its ring filled by hand, with
        // TaskService.MarkedByHand as the evidence. Only while it would change something (TaskService.CanMarkDeployed).
        if (taskService.Board.Tasks.FirstOrDefault(t => t.Id == id) is { } task && taskService.CanMarkDeployed(task))
            menu.Items.Add(TaskMenuItem(TasksWindow.MarkDeployedText, () => Report(taskService.MarkDeployed(id, TaskService.MarkedByHand))));
        HoldFadeWhileOpen(menu);
        TaskLineMenu = menu;
        menu.IsOpen = true;
        return menu;
    }

    internal const string AddSubtaskText = "Add subtask…";

    // A TextBlock header, so an underscore in a word is not read as an access key.
    static MenuItem TaskMenuItem(string text, Action click, bool? isChecked = null)
    {
        var item = new MenuItem { Header = new TextBlock { Text = text } };
        if (isChecked is { } on) item.IsChecked = on;
        item.Click += (_, _) => click();
        return item;
    }

    /// <summary>
    /// Opens the editor on a line (Edit). The bar takes the keyboard, noting first where Petre was typing so it can be
    /// handed back.
    /// </summary>
    internal void BeginTaskEdit(Guid id)
    {
        if (taskService is null || editingTask is not null || shownTaskLines.All(l => l.Id != id)) return;
        var front = ForegroundWindow();
        taskEditReturnTo = IsTypingWindow(front, isFlyout: false) ? front
            : languageTarget != 0 && OnCurrentDesktop(languageTarget) ? languageTarget : 0;
        editingTask = id;
        // Drawn now, whatever would hold a paint back: the editor itself is what holds them from here on.
        PaintTaskList(evenUnderPress: true);
        UpdateFade();
        Activate();
        taskEditBox?.Focus();
    }

    /// <summary>For the tests, and Enter: the editor's text saved through Rename, and the editor closed.</summary>
    internal void SaveTaskEdit()
    {
        if (editingTask is not { } id || taskEditBox is null) return;
        // A refused rename (an empty title) keeps the editor open with what was typed, as the tasks window's editor does
        // (review, Minor 3): Esc is the way out without saving.
        if (taskService?.Rename(id, taskEditBox.Text) is { IsFailure: true } refused)
        {
            ClickTrace.Write($"task list: not renamed: {refused.Error}");
            return;
        }
        EndTaskEdit(handBack: true);
    }

    /// <summary>Closes the editor without saving (Esc, a click away). The keyboard goes back only after Esc.</summary>
    internal void CancelTaskEdit(bool handBack = true) => EndTaskEdit(handBack);

    void EndTaskEdit(bool handBack)
    {
        if (editingTask is null) return;
        editingTask = null;
        taskEditBox = null;
        var back = taskEditReturnTo;
        taskEditReturnTo = 0;
        // What the editor held back is painted now, and the rebuild it deferred is served (Rebuild), and the docked fit
        // pass queued again: it declines to run while the editor is open (review, Minor 5).
        PaintTaskList();
        FlushDeferredRebuild();
        QueueTaskListFit();
        UpdateFade();
        if (handBack && back != 0 && !IsOwnWindow(back) && WindowAlive(back) && OnCurrentDesktop(back)) HandForegroundTo(back);
    }

    // The editor: the whole title, wrapping, taking line breaks (Shift+Enter), in the bar's dark box with the blue edge.
    TextBox TaskEditBox(TaskLine line)
    {
        var box = new TextBox
        {
            Tag = "task-edit-box",
            Text = line.Title,
            FontSize = Text(9.5),
            Foreground = TaskInk,
            Background = Frozen(0xFF, 0x15, 0x16, 0x1A),
            CaretBrush = Brushes.White,
            BorderBrush = TaskPickedEdge,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2, 0, 2, 0),
            Margin = new Thickness(4, 0, 4, 0),
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(box, "Edit task");
        box.Loaded += (_, _) =>
        {
            box.SelectAll();
            box.Focus();
        };
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CancelTaskEdit();
            }
            else if (TasksWindow.EditorSaves(e.Key, Keyboard.Modifiers))
            {
                e.Handled = true;
                SaveTaskEdit();
            }
        };
        // A click elsewhere abandons the edit; only Enter keeps it. Not a loss of focus caused by the paint that
        // builds this very box.
        box.LostKeyboardFocus += (_, _) =>
        {
            if (ReferenceEquals(taskEditBox, box) && box.IsLoaded) CancelTaskEdit(handBack: false);
        };
        taskEditBox = box;
        return box;
    }
}
