using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Reactive.Linq;
using CSharpFunctionalExtensions;
using Deskbar.Core.Goals;
using Deskbar.Core.Tasks;
using Deskbar.Windows.Monitoring;

namespace Deskbar.App;

// THE TASKS WINDOW (docs/superpowers/specs/2026-11-22-tasks-board-design.md, "The window" and "Comments"; mockup
// .superpowers/brainstorm/876-1795339550/content/tasks-window.html). Display only, like Quick find's window: it draws
// the TasksView its controller (TasksBoard) hands it and passes every click back through TasksWindowActions.
//
//   THE DOCKED PANEL (docs/superpowers/specs/2026-11-23-tasks-panel-design.md, "The panel window"; plan
//   2026-11-23-tasks-panel, Task 6). It was a page of its own, a regular window with a title bar, a place remembered
//   across restarts (AppState.TasksWindow, now ignored), a maximize and a Matrix that grew it; all of that went when it
//   became a full-height panel docked inside the bar. TasksPanel places it, reserves its strip and shows or hides it;
//   this file draws the board and the width grip.
//   Open / Dismiss   Open readies a fresh view (no add box, no edit, threads folded) and Dismiss ends one (menus
//                    closed, edits dropped); TasksBoard calls them around TasksPanel's Show and Hide, which do the
//                    showing. Closing the window (Alt+F4) never closes it: it asks the controller to close the panel
//                    (ClosePanel), until the app exits (CloseForGood), since a closed WPF window can never be shown
//                    again and the next "All tasks" would throw. It opens on the board: nothing typed into has the
//                    keyboard (the first task window opened onto an entry form, and Petre did not like it).
//   The grip         a 6 DIP band on the inner edge, the one facing the windows (SetInnerEdge): a drag previews the
//                    new width on the window itself and hands it to the controller only at the drag's end
//                    (ResizePanel), which saves it and re-docks the panel, as the bar's reservation follows its own
//                    width drag only when it ends. TasksWindow.Grip.cs.
//   Present          draws a view. A view that would draw what is already drawn draws nothing, and a view that
//                    arrives while drawing would pull something out from under Petre waits (MustWait): one of the
//                    window's menus open (the bar's #77 lesson), a rename under way, or a press in flight on the window
//                    (the bar's #48 lost click). Each of those ends by drawing the latest view (Flush). A half-typed
//                    comment does not hold a redraw; its text, keyboard and caret are carried through it instead.
//   Clicks           a line and its buttons take the bar's click guard (ClickIgnored): no second press of a double
//                    click, and nothing in the half second after the lines were redrawn.
//   The search box   (tasks panel plan, Task 7; spec 2026-11-23-tasks-panel-design.md, "The new filters") the row under
//                    the toolbar's title line, made once in the XAML and never rebuilt, so a redraw while Petre types
//                    cannot reset it: Draw clears and rebuilds the rows and the chips, never the box, and nothing writes
//                    the view's Search back into it. Its text, caret and keyboard therefore survive every redraw without
//                    the carrying-over a comment box needs. Each keystroke goes to the controller (Search), which narrows
//                    the board and hands back a new view. Ctrl+F anywhere in the panel focuses it (Press); Esc in it
//                    empties it and gives the keyboard back to the board (Escape, FocusBoard). Emptied on Open, since a
//                    fresh open resets every filter; not saved anywhere.
//   Claude switch    three orange-edged parts with the status chips, "Claude N", "working N", "idle N" (task 10b49554,
//                    option B of the mockup; it was the one "Claude working N" chip). One part is on at most: every task
//                    with a live session, only those with a session working now, only the idle ones. A click on the part
//                    that is on turns the switch off. The part that is on is filled with ClaudeOnFill, never the blue
//                    selected edge, which would lose the orange. Each N is what that part would show under every other
//                    filter (TasksView.ClaudeCount, ClaudeWorkingCount, ClaudeIdleCount).
//   Selected chips   a status chip, a ticked checkbox or the List / Matrix / Stats part in force is FILLED (PickedFill)
//                    as well as edged in blue (Picked; task 10b49554, Petre: "change selected color so that the button
//                    gets the color, not only the ring").
//   Idle part        the Claude working block's idle tasks (TaskGroupKind.ClaudeIdle, task 10b49554) come after its
//                    working ones under a dashed divider captioned "idle" (IdleDivider), their lines at IdleOpacity; a
//                    ClaudeIdle group with no working one above it has an ordinary "CLAUDE IDLE" caption instead.
//   The width        (tasks panel plan, Task 8; spec "Lines at panel width" and "The Matrix") below 850 DIP the lines are
//                    slim (the view's Slim: squares for sessions, no status chip, assignee, age or Reopen; "Lines at panel width"
//                    below Line), and the List / Matrix switch shows only from 780 (MatrixAllowed, PaintViewSwitch in
//                    TasksWindow.Matrix.cs). Both come with the view, made again at the grip's drag end, never during it.
//                    A session Claude is busy in wears a 2 DIP halo on its square, on either line (SessionSquare).
//
// Top to bottom: the toolbar in the approved mockup's three rows (the title line with "Tasks" and the end buttons,
// List / Matrix, "+ Add" and ×; then the search row; then the filter chips: the status filters with their counts,
// "Claude working", the workspace and assignee filters and the goal filter while goals exist, wrapping as needed), the
// add box while "+ Add" has it open, a line saying why the last action failed, then the groups (Waiting on you, then
// Covey's quadrants I to IV, captioned; Task 12). Each line: Claude's orange edge (no CL badge since Ruling 42: the
// edge is the mark) or a note's tick box (Undo in its place for the six seconds after a close), the title over the question or
// status line, the important and urgent toggles (quiet until hover or selection, as on the bar), the sessions (a
// square each, blue while live), the age, the workspace chip, the comment glyph with the count beside it, which opens
// the thread, a close button on a done Claude task or Reopen on a closed one, and "⋯" for the rest (status, close or
// reopen, rename, move, delete). A click anywhere else on the line goes to the task, and a press dragged up or down
// reorders it (TasksWindow.Drag.cs). The Matrix draws the same lines, compact (TasksWindow.Matrix.cs).
public partial class TasksWindow : Window
{
    // UI Automation sees this window and nothing inside it, so WPF stops walking its element tree after every
    // layout pass (QuietAutomation.cs has the measurement: 1.45 s freezes on a busy machine).
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new QuietWindowPeer(this);

    // The mockup's palette. Frozen, as every static WPF brush must be (CLAUDE.md, Traps).
    static readonly Brush Ink = Frozen(0xFF, 0xE6, 0xE6, 0xE6);
    static readonly Brush DimInk = Frozen(0xFF, 0xA9, 0xAD, 0xB8);
    static readonly Brush HeaderInk = Frozen(0xFF, 0x8B, 0x8F, 0x99);
    static readonly Brush Orange = Frozen(0xFF, FloatingBar.AskingOrange.R, FloatingBar.AskingOrange.G, FloatingBar.AskingOrange.B);
    static readonly Brush AskTint = Frozen(0xFF, 0x3A, 0x2C, 0x1C);
    // A toolbar chip that is ON is filled, not only edged (task 10b49554, Petre: "change selected color so that the button
    // gets the color, not only the ring"): the blue of the selected edge (PickedEdge) darkened enough that the light ink
    // stays readable on it (about 5.5:1), and for the Claude switch the orange's own dark shade (about 5:1).
    static readonly Brush PickedFill = Frozen(0xFF, 0x2E, 0x5B, 0xAA);
    static readonly Brush ClaudeOnFill = Frozen(0xFF, 0x8A, 0x54, 0x10);
    // A task line's tile (TasksWindow.Line): a shade lighter than the panel (#1E1F24), so each task is its own block.
    static readonly Brush TileFill = Frozen(0xFF, 0x24, 0x26, 0x2C);
    // The space between two tiles, and between a tile and the panel's sides, in DIP.
    const double TileGap = 3;
    const double TileInset = 6;
    static readonly Brush BadgeInk = Frozen(0xFF, 0x1E, 0x1F, 0x24);
    static readonly Brush ButtonFill = Frozen(0xFF, 0x2A, 0x2C, 0x33);
    static readonly Brush PickedEdge = Frozen(0xFF, 0x4F, 0x8C, 0xFF);
    static readonly Brush TickEdge = Frozen(0xFF, 0x77, 0x77, 0x77);
    static readonly Brush ThreadFill = Frozen(0xFF, 0x18, 0x19, 0x1D);
    static readonly Brush ThreadEdge = Frozen(0xFF, 0x33, 0x35, 0x3C);
    static readonly Brush ClaudeInk = Frozen(0xFF, 0xD9, 0xB9, 0x8A);
    static readonly Brush LiveSession = Frozen(0xFF, 0x3B, 0x82, 0xF6);
    static readonly Brush EndedSession = Frozen(0xFF, 0x4B, 0x4F, 0x5A);
    static readonly Brush NoneFill = Frozen(0xFF, 0x33, 0x33, 0x33);
    static readonly Brush NoneInk = Frozen(0xFF, 0x99, 0x99, 0x99);

    // THE FLOATING WINDOW'S PLACEMENT LIVED HERE (Gap, PlaceSettles, the dark title bar, PreferredWidth and Height, Place,
    // PlaceNear, PlaceAt, CurrentPlace, NoteNormal, MaximizeIfRemembered). Removed with the floating window by the tasks
    // panel plan (docs/superpowers/plans/2026-11-23-tasks-panel.md, Task 6): the panel's place is TasksPanel's.

    // At most this many session squares on a line; the words beside them give the count.
    const int SessionSquares = 3;

    // The four statuses Petre sets by hand from the menu; Closed is Close's (TaskBoard.SetStatus refuses it).
    static readonly IReadOnlyList<TaskStatus> SettableStatuses = [TaskStatus.Backlog, TaskStatus.InProgress, TaskStatus.WaitingOnYou, TaskStatus.Done];

    enum KeyOutcome { Ignored, NewLine, Posted }

    // What was drawn, and what is waiting to be: the latest view, the signature of the one drawn (so a view that
    // changes nothing draws nothing), and whether a view is held back by MustWait.
    TasksView? latest;
    string drawn = "";
    bool held;
    bool drawing;

    // The menu on show, if any, and the line being renamed in place with its box.
    ContextMenu? openMenu;
    Guid? renaming;
    TextBox? renameBox;
    // Ruling 58: the editor's second box (the description, under the title's), and the comment being rewritten in place.
    TextBox? descriptionBox;
    // RULING 62: the tasks whose settled comments are unfolded in their threads. A view state of the window alone, kept while
    // the window is open (cleared when it opens, with the threads), and part of the redraw signature.
    readonly HashSet<Guid> settledOpen = [];
    // RULING 63: the tasks whose earlier comments (all but the latest three) are unfolded, and the long comments shown in full.
    readonly HashSet<Guid> earlierOpen = [];
    readonly HashSet<Guid> longOpen = [];
    Guid? editingComment;
    TextBox? commentEditor;

    // Per line, rebuilt with the rows: the "⋯" button a menu hangs from, the thread's comment box and its authors.
    // The drafts outlive a redraw, so a comment half typed survives a change arriving from a session.
    readonly Dictionary<Guid, Button> menuButtons = [];
    readonly Dictionary<Guid, TextBox> commentBoxes = [];
    // Each comment box's "/" list of Claude Code skills (task 29bdd158, SkillCompletion), by task: kept so the window's
    // Esc can close an open list before it does anything else (Escape), and for the tests. Cleared with the boxes.
    readonly Dictionary<Guid, SkillCompletion> commentSkills = [];
    readonly Dictionary<Guid, IReadOnlyList<string>> authors = [];
    readonly Dictionary<Guid, IReadOnlyList<Guid>> shownComments = [];
    readonly Dictionary<Guid, bool> closing = [];
    readonly Dictionary<Guid, string> drafts = [];

    readonly IDisposable ageWatch;

    // The workspace chosen in the add box, or none chosen yet (the view's AddTarget then).
    TaskPlaceChoice? addChosen;

    // The add box's "/" list of Claude Code skills (task 3a872301, SkillCompletion); kept so it lives with the window.
    readonly SkillCompletion addSkills;

    // Set only when the app is going away; see QuickFindWindow.closingForGood.
    bool closingForGood;

    public TasksWindow()
    {
        InitializeComponent();
        // Its own checklist icon (TasksIcon). The panel is no longer listed on the bar (the App opts its handle out, as
        // it does the bar's own), but Windows still asks a window for its icon in a debug dump or a trace.
        Icon = TasksIcon.Image;
        // Esc undoes the innermost thing; F2 edits the selected line's title in place (Ruling 38); Ctrl+F goes to the
        // search box (Task 7). Through Press, so the tests can hand it the modifiers a synthetic key event cannot carry.
        PreviewKeyDown += (_, e) => e.Handled = Press(e.Key, Keyboard.Modifiers);
        // Never in Alt+Tab: WS_EX_TOOLWINDOW, for the reason the bar gives in its own OnSourceInitialized
        // (ShowInTaskbar false alone keeps a window off the taskbar but not out of Alt+Tab). The panel is part of the
        // bar, which is not in Alt+Tab either (spec "The panel window").
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE,
                NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE) | (nint)NativeMethods.WS_EX_TOOLWINDOW);
        };
        var context = new System.Windows.Threading.DispatcherSynchronizationContext(Dispatcher);
        // The ages' minute (RefreshAges), only while the window is open, on the window's own thread.
        ageWatch = Observable.Interval(TimeSpan.FromMinutes(1)).ObserveOn(context).Subscribe(_ =>
        {
            if (IsOpen) RefreshAges();
        });
        // A press on the window holds a redraw (MustWait); its release, after the click it made has been handled (the
        // handler sees handled events too, and runs once the button under it has raised its Click), draws what waited.
        AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler((_, _) => Flush()), handledEventsToo: true);
        AddBox.TextChanged += (_, _) => AddPlaceholder.Visibility = AddBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        // The "/" list of skills (task 3a872301, SkillCompletion), as on the bar's box: attached BEFORE PressInAdd, so its
        // Enter, Tab and Esc pick or close while it is open. It lists the skills of the workspace on the add chip, and
        // only while the panel can hand a note to Claude (AddNoteAndStartClaude wired). Actions is read at the keystroke.
        addSkills = new SkillCompletion(AddBox,
            () => Actions?.WorkspaceSkills is { } skills && (addChosen ?? latest?.AddTarget) is { } target
                ? skills(target.Workspace, Maybe<Guid>.None)
                : Task.FromResult<IReadOnlyList<SkillChoice>>([]),
            () => Actions?.WorkspaceSkills is not null && Actions?.AddNoteAndStartClaude is not null,
            new SkillCompletion.Look(Frozen(0xFF, 0x15, 0x16, 0x1A), PickedEdge, Ink, DimInk, SelectedFill));
        AddBox.PreviewKeyDown += (_, e) => e.Handled = PressInAdd(e.Key, Keyboard.Modifiers, e.IsRepeat) != KeyOutcome.Ignored;
        // The panel resized (or the add row laid out for the first time): the chips go beside the box or under it
        // again, whichever leaves room to type (FitAddEnd, task f212d886). Only a width change can change that answer.
        AddLine.SizeChanged += (_, e) => { if (e.WidthChanged) FitAddEnd(); };
        // Ctrl+V and drops of pictures on the add box (TasksWindow.Images.cs, "the add box").
        TakeAddPictures();
        // The search box: every keystroke goes to the controller at once, which narrows and redraws (no debounce, see
        // TasksBoard.Search for why). The box is never written by a redraw, only read, so typing keeps its text, caret
        // and keyboard through every redraw ("the search box" below).
        BuildSearchClear();
        SearchBox.TextChanged += (_, _) =>
        {
            // The placeholder while empty, the clear button (×) while not: one or the other, never both.
            SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            SearchClear.Visibility = SearchBox.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            Actions?.Search(SearchBox.Text);
        };
        WireDrag();
        WireGrip();
        BuildToolbarEnd();
        BuildColumnHeader();
    }

    // The controller's side, set once by TasksBoard's constructor.
    internal TasksWindowActions? Actions { get; set; }

    // For the tests only: never activates itself (ActivateHere). Where it is shown is the panel's, which the tests dock
    // on a made-up screen off every real monitor (TasksPanelRig).
    internal bool Quiet { get; init; }

    // The user's open: from Open to Dismiss. TasksBoard keeps it in step with TasksPanel.IsShown.
    public bool IsOpen { get; private set; }

    // For the controller and the tests: what is drawn, in order.
    internal int Rebuilds { get; private set; }
    internal int Presents { get; private set; }
    internal IReadOnlyList<string> Headers { get; private set; } = [];
    internal IReadOnlyList<Guid> Keys { get; private set; } = [];
    internal IReadOnlyList<string> FilterLabels { get; private set; } = [];
    internal string WorkspaceFilterLabel { get; private set; } = "";
    // The goal filter's words, "" while it is not shown (ruling G1: no goals, no goal filter).
    internal string GoalFilterLabel { get; private set; } = "";
    internal string AssigneeFilterLabel { get; private set; } = "";
    // The Claude switch's three parts' words ("Claude 3", "working 1", "idle 2") and which part is on (task 10b49554).
    internal IReadOnlyList<string> ClaudeLabels { get; private set; } = [];
    internal ClaudeSessionFilter ClaudeFilterOn { get; private set; }
    // Whether the "Current workspace" checkbox is ticked, as last drawn (task ed1a01fc), for the tests.
    internal bool CurrentWorkspaceOn { get; private set; }
    // Whether the "Subtasks" checkbox is ticked, as last drawn (task 173ef268), for the tests.
    internal bool SubtasksOn { get; private set; } = true;
    internal bool AddBoxShown => AddRow.Visibility == Visibility.Visible;
    internal string? Problem => ProblemText.Visibility == Visibility.Visible ? ProblemText.Text : null;
    internal bool ThreadShown(Guid id) => authors.ContainsKey(id);
    internal IReadOnlyList<string> CommentAuthors(Guid id) => authors.GetValueOrDefault(id) ?? [];
    /// <summary>For the tests: the ids of the comments drawn in a task's thread, in order (settled ones folded away are not).</summary>
    internal IReadOnlyList<Guid> ShownComments(Guid id) => shownComments.GetValueOrDefault(id) ?? [];
    internal bool IsClosing(Guid id) => closing.GetValueOrDefault(id);
    internal string CommentDraft(Guid id) => commentBoxes.TryGetValue(id, out var box) ? box.Text : drafts.GetValueOrDefault(id, "");
    /// <summary>For the tests: stands in for "a comment box has the keyboard", which a window that never activates cannot have.</summary>
    internal Func<bool>? EscapeIgnoresThreadWhile { get; set; }
    internal TextBox? CommentBoxOf(Guid id) => commentBoxes.GetValueOrDefault(id);

    // For the tests: a line's comment box's "/" list of skills (task 29bdd158).
    internal SkillCompletion? CommentSkillsOf(Guid id) => commentSkills.GetValueOrDefault(id);

    // THE CLICK GUARD, the bar's (#48, Ruling 10; FloatingBar.TaskClickIgnored), fix round 1, Important 1. A line
    // closed here stays six seconds and then leaves, and a tick or Reopen takes its line away, so the next line slides
    // under the pointer: the second press of a double click closed or reopened a second task, and a click aimed at a
    // line that just moved hit its neighbour. A click on a line or one of its buttons is dropped when its press was the
    // second of a double click or when it lands within half a second of a redraw of the lines. Undo drops only the
    // double click: it appears where the tick was on purpose, so a deliberate click on it, however soon, is meant.
    // ClickClock is a seam for the tests, which put a click right after a redraw or half a second later.
    internal Func<DateTime> ClickClock { get; init; } = () => DateTime.UtcNow;

    // IMAGES IN TASKS (docs/superpowers/specs/2026-11-24-task-images-design.md). StateDir is the folder pasted pictures are
    // saved under (task-images\<task id>\) and read from; OpenImage is what a click on a thumbnail does with the picture's
    // absolute path: PictureViewer enlarges it in a lightbox over the screen under the cursor (task e5d85e5e), and its
    // "Open in viewer" button still reaches OpenInViewer below. Both are seams so the tests use a scratch folder and
    // record the click instead of opening a viewer.
    internal string StateDir { get; init; } = AppPaths.NewStateDir;

    internal Action<string> OpenImage { get; init; } = PictureViewer.Show;

    // A refusal (no viewer registered for PNG) is traced, never thrown: a click in the panel must not take the app down.
    internal static void OpenInViewer(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ClickTrace.Write($"tasks window: could not open {path}: {e.Message}");
        }
    }

    static readonly TimeSpan RepaintGrace = TimeSpan.FromMilliseconds(500);
    string linesPlaces = "";
    int pressClicks;
    DateTime linesChangedAt = DateTime.MinValue;

    bool ClickIgnored() => DoubleClickIgnored() || ClickClock() - linesChangedAt < RepaintGrace;

    // Reading it consumes the press's count, as the bar's does.
    bool DoubleClickIgnored()
    {
        var doubled = pressClicks > 1;
        pressClicks = 0;
        return doubled;
    }

    // Alt+F4 on the panel closes the panel, through the controller, so its strip goes back to the shell and the saved
    // state says closed, exactly as the toolbar's × does. The WPF window itself is closed only by CloseForGood.
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!closingForGood)
        {
            e.Cancel = true;
            if (Actions is { } actions) actions.ClosePanel();
            else Dismiss();
        }
        base.OnClosing(e);
    }

    public void CloseForGood()
    {
        closingForGood = true;
        StopTranscript();
        ageWatch.Dispose();
        Close();
    }

    /// <summary>
    /// A fresh view, before the panel shows the window: no add box, nothing being edited or renamed, threads folded, the
    /// list at its top. It does not show the window: TasksPanel does, once the bar's strip stands. The keyboard goes to
    /// the toolbar, never to a text box (FocusBoard, after the controller brought the window forward).
    /// </summary>
    public void Open()
    {
        HideAdd();
        settledOpen.Clear();
        earlierOpen.Clear();
        longOpen.Clear();
        renaming = null;
        renameBox = null;
        descriptionBox = null;
        EndEditorPictures();
        editingComment = null;
        commentEditor = null;
        // The search starts over with every other filter (TasksBoard.OpenFresh has already reset its own). Emptied
        // before IsOpen is set, so the TextChanged it raises redraws nothing (the controller's search is "" already).
        SearchBox.Text = "";
        IsOpen = true;
        // Shown WITHOUT activation (fix round 1, Critical 1): the controller brings it forward afterwards (raise, then
        // activate; TasksBoard.Arrive). The XAML says so too; set again here because a test may flip it.
        ShowActivated = false;
        Scroller.ScrollToTop();
        // NO Toolbar.Focus() HERE (2026-11-23, "tasks should become a top window, now it's behind somewhere"). A WPF
        // Focus() on an element of a window that is not active activates that window (measured on the hidden desktop),
        // and the seam's SetForegroundWindow on what already was the foreground window then raised nothing. The
        // controller hands the keyboard over once the window has arrived (FocusBoard, from TasksBoard.Arrived).
    }

    /// <summary>
    /// The keyboard to the toolbar, never to a text box (the window opens on the board). Only once the controller has
    /// brought the window forward and activated it: focusing an inactive window activates it.
    /// </summary>
    internal void FocusBoard() => Toolbar.Focus();

    /// <summary>
    /// The window activating itself, only when the controller has no seam to bring it (and counted for the tests).
    /// </summary>
    internal void ActivateHere()
    {
        SelfActivations++;
        if (!Quiet) Activate();
    }

    internal int SelfActivations { get; private set; }

    /// <summary>
    /// The view ends (the × , Alt+F4 or `tasks-window hide`, through TasksBoard.Hide): a menu on show closes and an edit
    /// under way is dropped. It does not hide the window: TasksPanel does, after giving its strip back.
    /// </summary>
    public void Dismiss()
    {
        if (!IsOpen) return;
        IsOpen = false;
        if (openMenu is { IsOpen: true } menu) menu.IsOpen = false;
        renaming = null;
        renameBox = null;
        descriptionBox = null;
        EndEditorPictures();
        editingComment = null;
        commentEditor = null;
    }

    public void ShowProblem(string? text)
    {
        ProblemText.Text = text ?? "";
        ProblemText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Draws the view, unless it would draw nothing new or must wait (see the header).</summary>
    internal void Present(TasksView view)
    {
        Presents++;
        latest = view;
        // A view arriving in the middle of a draw (a pulse pumped by something the draw did) is held, and drawn by the
        // Flush at the end of that draw (fix round 1).
        if (MustWait || drawing)
        {
            held = true;
            return;
        }
        held = false;
        Draw(view);
    }

    // What holds a redraw back (see the header). Not a comment box with text in it (fix round 1, Important 2): every
    // button here is unfocusable, so the box keeps the keyboard through any click, and a redraw waiting for it never
    // came; the filters, the arrows and the ticks all seemed dead. The draft, the keyboard and the caret survive a
    // redraw instead (Draw).
    bool MustWait =>
        openMenu?.IsOpen == true
        || renaming is not null
        || editingComment is not null
        // A line armed, dragged, or its drop still queued (TasksWindow.Drag.cs): no redraw under it (#48).
        || DragInFlight
        // A text's height grip being dragged (task 2088e1ed, TasksWindow.BodyGrip.cs): a redraw would replace the grip.
        || bodyGripDragging
        || (Mouse.LeftButton == MouseButtonState.Pressed && IsMouseOver);

    // What waited is drawn now that nothing holds it. Not from inside a draw: clearing the rows takes the keyboard
    // from a box being replaced, and its LostKeyboardFocus lands here (CLAUDE.md, "Re-entrancy in the bar's rebuild",
    // the same shape of trap).
    void Flush()
    {
        if (held && !drawing && latest is not null) Present(latest);
    }

    // --- drawing -----------------------------------------------------------------------------------------------------

    void Draw(TasksView view)
    {
        var signature = Signature(view);
        // Where a new note would go (AddTarget) is not part of the signature (whole-PR review, I-1): it changes with every
        // workspace switch, a line's click included, and redrawing every row for it reset the Matrix's scrolls. Only the
        // add box's chip shows it, painted here in place.
        if (signature == drawn || drawing)
        {
            if (!drawing) PaintAddPlace();
            return;
        }
        drawn = signature;
        Rebuilds++;
        drawing = true;
        // The link picker (TasksWindow.Links.cs) is rebuilt with the rows; whether its box had the keyboard is noted first.
        ForgetLinkBox();
        // ...and the subtask box in a thread (TasksWindow.SubtaskAdd.cs), the same way.
        ForgetSubtaskBox();
        try
        {
            DrawRows(view);
        }
        finally
        {
            drawing = false;
        }
        // An edit of a comment that is no longer drawn (its thread closed, the task gone) ends, or a redraw would wait for ever.
        if (editingComment is not null && commentEditor is null) editingComment = null;
        // A comment box that had the keyboard gets it back in its new form, its text (from `drafts`) and its caret where
        // they were, so typing carries on through a redraw and a second comment can follow the first without a click.
        // Likewise the rename box.
        if (keyboardWasIn != Guid.Empty && commentBoxes.TryGetValue(keyboardWasIn, out var again))
        {
            again.Focus();
            again.CaretIndex = Math.Min(caretWas, again.Text.Length);
        }
        renameBox?.Focus();
        commentEditor?.Focus();
        RefocusLink();
        RefocusSubtaskBox();
        RefocusDialogOther();
        // A view that arrived during the draw is drawn now.
        Flush();
    }

    // Which comment box had the keyboard when the last draw began, and where its caret was.
    Guid keyboardWasIn;
    int caretWas;

    void DrawRows(TasksView view)
    {
        commentEditor = null; // set again by CommentOf when its comment is drawn
        keyboardWasIn = commentBoxes.FirstOrDefault(pair => pair.Value.IsKeyboardFocusWithin).Key;
        caretWas = keyboardWasIn != Guid.Empty ? commentBoxes[keyboardWasIn].CaretIndex : 0;
        RememberDialogFocus();
        // When the lines on show change (which tasks, their order, which threads are open, which are closing), a line
        // may now be under the pointer that was not, so for half a second a click is not trusted (the click guard,
        // ClickIgnored). A redraw that only rewrites what lines say (a session's status line) moves nothing and does
        // not start the half second, or frequent status pulses would eat clicks (review minor).
        var shownRows = view.Matrix ? view.Cells.SelectMany(c => c.Rows).ToArray() : view.Groups.SelectMany(g => g.Rows).ToArray();
        // Which comments a thread shows, and what is unfolded in it, are part of where things sit (review of the assignee batch,
        // M4): an earlier-comments line unfolding or a long comment opening moves every line below, so it starts the half second
        // too.
        // The manager line (task 61e4d143) showing or going moves every line under it, so it is part of the key too.
        var managerShown = view.Stats is null && view.ManagerAsks is not null;
        var placesKey = string.Join('|', shownRows.Select(r => $"{r.Id}{r.Expanded}{r.Closing}{ThreadLayoutKey(r)}").Prepend($"{view.Matrix}{managerShown}"));
        if (placesKey != linesPlaces)
        {
            linesPlaces = placesKey;
            linesChangedAt = ClickClock();
        }
        DrawFilters(view);
        // The manager's question above the List and the Matrix, never over the Stats view (TasksWindow.Manager.cs).
        DrawManagerLine(managerShown ? view.ManagerAsks : null);
        // The List's rows are not cleared here: DrawList keeps the lines that did not change (TasksWindow.LineReuse.cs).
        // The registries below are, and a kept line writes its entries back.
        // Where each open thread is scrolled to, read before any line is rebuilt, and which threads open now (task
        // ee9bd3fc). It empties the capped threads' registry too, which a kept line writes back like the others.
        BeforeThreadsRedraw(shownRows);
        menuButtons.Clear();
        lineButtons.Clear();
        dragLines.Clear();
        dragEntries.Clear();
        commentBoxes.Clear();
        commentSkills.Clear();
        ages.Clear();
        slimTips.Clear();
        selectionFrames.Clear();
        authors.Clear();
        shownComments.Clear();
        closing.Clear();
        idleLines.Clear();
        // THE STATS VIEW (task 521d2b07, TasksWindow.Stats.cs) takes the place of both the List and the Matrix, and of the
        // search row and the filter chips, which do not apply to it (it counts every task). Drawn, and nothing else.
        statsShown = view.Stats is not null;
        StatsScroller.Visibility = statsShown ? Visibility.Visible : Visibility.Collapsed;
        SearchRow.Visibility = statsShown ? Visibility.Collapsed : Visibility.Visible;
        Filters.Visibility = statsShown ? Visibility.Collapsed : Visibility.Visible;
        // The Inbox's header and footer in the toolbar's place, or the toolbar back (TasksWindow.Inbox.cs, task 740a2231).
        PaintInboxChrome(view);
        if (view.Stats is { } stats)
        {
            LeaveList();
            matrixShown = false;
            Scroller.Visibility = Visibility.Collapsed;
            MatrixGrid.Visibility = Visibility.Collapsed;
            MatrixGrid.Children.Clear();
            DrawStats(stats);
            Headers = [];
            Keys = [];
            EmptyText.Visibility = Visibility.Collapsed;
            // No lines, so no column header over them (task 3df2d28f).
            ColumnHeader.Visibility = Visibility.Collapsed;
            return;
        }
        StatsPanel.Children.Clear();
        // The List or the Matrix (TasksWindow.Matrix.cs), whichever the switch says; the other is emptied and hidden.
        matrixShown = view.Matrix;
        Scroller.Visibility = view.Matrix ? Visibility.Collapsed : Visibility.Visible;
        MatrixGrid.Visibility = view.Matrix ? Visibility.Visible : Visibility.Collapsed;
        MatrixGrid.Children.Clear();
        // Slim lines on a narrow panel (Task 8, see "Lines at panel width" below Line). The Matrix's compact lines are
        // their own, and the Matrix never shows on a panel that narrow anyway. The squares' column is as wide as the line
        // with the most squares needs, the same on every line so the chip after it sits at one x.
        slimLines = view.Slim && !view.Matrix;
        // The subtasks drawn as lines in an open parent's thread (task 249a1e8a) share these columns, so they count too:
        // a closed subtask's Reopen, a folded Claude subtask's squares or a subtask with ten comments would otherwise be
        // clipped by columns sized for the top-level lines alone (review of this change).
        var linedRows = shownRows.Concat(ThreadLines(shownRows)).ToArray();
        slimSquaresWidth = SquaresColumn(linedRows.Select(r => r.SessionCount).DefaultIfEmpty(0).Max());
        // The button columns as narrow as the lines on show need, and the text columns' widths (TasksWindow.Columns.cs).
        // The Open Claude column (task 7f61af2e) is there while any line shown has the button, zero on a board without
        // one, so a list of notes gives its title the room, as the squares' column does.
        SizeColumns(view, linedRows);
        fullGrids.Clear();
        fullGrids.Add(ColumnHeader);
        // The Inbox draws the same rows its own way (TasksWindow.Inbox.cs, task 740a2231). LeaveList first: it empties Rows
        // and lets go of the List's kept blocks, which the Inbox does not use.
        if (view.Inbox is not null)
        {
            LeaveList();
            DrawInbox(view);
            return;
        }
        if (view.Matrix)
        {
            LeaveList();
            DrawMatrix(view);
        }
        else DrawList(view);
        AfterThreadsRedraw(shownRows);
        Headers = view.Matrix ? view.Cells.Select(c => c.Header).ToArray() : view.Groups.Select(g => g.Header).ToArray();
        Keys = shownRows.Select(r => r.Id).ToArray();
        EmptyText.Text = view.EmptyText;
        // The Matrix always draws its four cells, empty or not, so it needs no empty line.
        EmptyText.Visibility = Keys.Count == 0 && !view.Matrix ? Visibility.Visible : Visibility.Collapsed;
        // The column header (task 3df2d28f) heads full lines only: not the Matrix, not slim lines, not an empty list.
        PaintColumnHeader(!view.Matrix && !slimLines && Keys.Count > 0);
        PaintActiveCell();
        PaintAddPlace();
    }

    // Everything the view shows, as text, so two views can be compared without drawing either.
    string Signature(TasksView view) =>
        string.Join('\u0001',
            string.Join('|', view.Filters.Select(f => $"{f.Label}{f.On}{f.Asks}")),
            view.WorkspaceFilterLabel,
            $"{view.Where}",
            // The goal filter (goals plan, Task 6): whether it shows, its label and the filter in force, which is what the
            // toolbar draws. NOT the goals the two goal menus list (review M1): both menus are built from `latest` when
            // they open, so they are current without a redraw, and a redraw for every goal edit rebuilt every row and
            // reset the Matrix's scrolls. A row's own goal is not drawn either, so it is not here.
            $"{view.ShowsGoals}{view.GoalFilterLabel}{view.GoalFilter}",
            // The "Assigned" filter (Ruling 57): its label is what the toolbar draws.
            $"{view.AssigneeFilterLabel}",
            // The Claude switch (Task 7's chip, three parts since task 10b49554): which part is on, and each part's count.
            // NOT the search text: the box is not drawn from the view, and the lines it narrows are below, so a keystroke
            // that changes no line redraws nothing.
            $"{view.ClaudeFilter}{view.ClaudeCount}/{view.ClaudeWorkingCount}/{view.ClaudeIdleCount}",
            // The "Subtasks" checkbox (task 173ef268): a tick on a list with no subtasks changes no line, and the box must
            // still redraw ticked.
            $"{view.ShowsSubtasks}",
            // The manager's question (task 61e4d143): asked, replaced or cleared redraws its line. Its time too, so the same
            // text asked again counts its age from the new ask; the age itself is rewritten once a minute (RefreshAges).
            // An answer in place of a question (task 2c5de076) redraws it too, even with the same text.
            $"{view.ManagerAsks?.Question}{view.ManagerAsks?.AskedAt}{view.ManagerAsks?.Answered}",
            string.Join('|', view.Places.Select(p => $"{p.IsGroup}{p.Workspace}{p.Name}{(p.Colour as SolidColorBrush)?.Color}")),
            view.EmptyText,
            $"{renaming}{editingComment}{linking}{subtaskUnder}{string.Join(",", settledOpen.Order())}|{string.Join(",", earlierOpen.Order())}|{string.Join(",", longOpen.Order())}",
            // The heights texts were dragged to (task 2088e1ed), so a redraw after a drag builds them at that height.
            BodyHeightsSignature,
            // The two width flags (Task 8): which line is drawn and whether the view switch shows. Not the width itself, so
            // a grip drag that crosses neither threshold redraws nothing.
            $"{view.Matrix}{view.MatrixAllowed}{view.Slim}",
            // The Stats view (task 521d2b07): its numbers and every task it lists, so a status changing redraws it.
            StatsSignature(view.Stats),
            // A List row's WorkingCount is in its line below (Task 8): a new agents list that only starts or stops Claude
            // working changes the squares' halos and nothing else, and must still redraw the line. Not in a Matrix cell's:
            // a compact line draws no sessions, so it would only rebuild the Matrix for nothing (review minor 2).
            string.Join('\u0002', view.Cells.Select(c => $"{c.Header}:" + string.Join('\u0003', c.Rows.Select(r =>
                $"{r.Id}|{r.Title}|{r.Detail}|{r.Description}|{r.Status}|{r.Claudes}|{r.Assignee.Id}|{PlaceSignature(r.Place)}|{r.CommentCount}|{r.Expanded}|{r.Closing}|{r.Important}|{r.Urgent}|{r.ParentTitle}|{r.ParentId}|{r.SubtaskCount}|{r.ClaudeWorking}|{r.Unfinished}|" +
                // The ring or the disc and its tooltip line (task 8470d176): GitHub's answer changes nothing else on a line.
                $"{r.Delivery.Stage}|{r.Delivery.Note}|{r.Problem}|" +
                SubtasksSignature(r) + "|" +
                string.Join('\u0004', r.Comments.Select(c => $"{c.Id}{c.Author}{c.When}{c.Markdown}{c.Settled}")))))),
            string.Join('\u0002', view.Groups.Select(g => $"{g.Header}:" + string.Join('\u0003', g.Rows.Select(ListRowSignature)))),
            // The Inbox's tabs, its page and its own state (TasksWindow.Inbox.cs, task 740a2231); "" in every other view.
            InboxSignature(view));

    // One List row as the signature reads it. A subtask open in place in its parent's thread (task 0eb943d8) carries its
    // own row, read the same way inside braces, so a comment or a session on it redraws the parent's thread.
    static string ListRowSignature(TaskRowView r) =>
        $"{r.Id}|{r.Title}|{r.Detail}|{r.Description}|{r.Status}|{r.Claudes}|{r.Assignee.Id}|{r.Sessions}|{r.SessionCount}|{r.LiveCount}|{PlaceSignature(r.Place)}|{r.CommentCount}|" +
        $"{r.Expanded}|{r.Closing}|{r.Important}|{r.Urgent}|{r.WorkingCount}|{r.ParentTitle}|{r.ParentId}|{r.SubtaskCount}|{r.ClaudeWorking}|{r.Unfinished}|{r.Nested}|{r.DeployBuilt}|" +
        // Which Claude button the line has (task 6a57f59b, OpensSession): it changes when the first agents list arrives or
        // a session's worktree goes, with nothing else on a closed line changing, so without it the orange icon would stay.
        $"{r.OpensSession}|" +
        // The ring or the disc, the chip's word and the tooltip line (task 8470d176): GitHub's answer changes nothing else.
        $"{r.Delivery.Stage}|{r.Delivery.Note}|{r.Problem}|" +
        SubtasksSignature(r) + "|" +
        string.Join('\u0004', r.SessionList.Select(s => $"{s.Id}{s.Joined}{s.State}")) + "|" +
        // Task links: the thread's link lines, and the status words of a closed duplicate.
        string.Join('\u0004', r.Links.Select(l => $"{l.Kind}{l.Id}{l.Title}{l.Status}")) + $"|{r.DuplicateOfTitle}|" +
        string.Join('\u0004', r.Comments.Select(c => $"{c.Id}{c.Author}{c.When}{c.Markdown}{c.Settled}"));

    // A row's subtask entries, and the whole row of each one open in place (task 0eb943d8). The Matrix's cells read it too:
    // a subtask folded there is in no cell, so opening it in its parent's thread changed nothing the cells' signature
    // read, and the draw never came (review).
    // The open subtasks a page shows at its top (task 2106492b) come first, so a subtask renamed, moved on or closed redraws it.
    static string SubtasksSignature(TaskRowView r) =>
        string.Join('\u0004', r.OpenSubtasks.Select(c => $"{c.Id}{c.Title}{c.Status}")) + "\u0005" +
        string.Join('\u0004', r.Subtasks.Select(c => $"{c.Id}{c.Title}{c.Status}{c.StatusLine}{c.HasLine}{c.Closing}{c.TickTip}{c.Quadrant}" + (c.Row is { } open ? "{" + ListRowSignature(open) + "}" : "")));

    static string PlaceSignature(TaskPlaceChoice place) => $"{place.Workspace}{place.Name}{(place.Colour as SolidColorBrush)?.Color}";

    // THE GAP BETWEEN WRAPPED ROWS OF CHIPS (the Task 7 review's deferred minor, fixed in Task 8): every chip has this
    // bottom margin, so on a narrow panel, where the chips wrap onto two or three rows, the rows stand 4 DIP apart as in
    // the approved mockup instead of touching. The Filters panel's -4 bottom margin (TasksWindow.xaml) gives the last
    // row's back, so the toolbar is no taller for it.
    const double ChipRowGap = 4;

    // The status chips and the workspace filter, redrawn with the rows.
    void DrawFilters(TasksView view)
    {
        Filters.Children.Clear();
        view.Filters.ToList().ForEach(chip =>
        {
            var button = Flat(new TextBlock { Text = chip.Label, Foreground = chip.Asks ? Orange : Ink, FontSize = 11 }, $"Filter: {chip.Label}",
                () => Actions?.Filter(chip.Filter), new Thickness(8, 2, 8, 2));
            Picked(button, chip.On);
            button.Margin = new Thickness(0, 0, 6, ChipRowGap);
            Filters.Children.Add(button);
        });
        // The Claude switch (Task 7's chip, three parts since task 10b49554; see the header), with the status chips. The
        // three parts sit together in one panel so the WrapPanel never splits them across rows on a narrow panel.
        var claude = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 6, ChipRowGap) };
        var parts = new (ClaudeSessionFilter Part, string Label, string Name, string Tip)[]
        {
            (ClaudeSessionFilter.Any, $"Claude {view.ClaudeCount}", "Filter: Claude",
                "Only the tasks Claude has a session open on, working or idle. Kept when the panel opens again"),
            (ClaudeSessionFilter.Working, $"working {view.ClaudeWorkingCount}", "Filter: Claude working",
                "Only the tasks Claude is working on right now. Kept when the panel opens again"),
            (ClaudeSessionFilter.Idle, $"idle {view.ClaudeIdleCount}", "Filter: Claude idle",
                "Only the tasks whose Claude session is open but idle: its turn is over and it asked nothing. Kept when the panel opens again"),
        };
        parts.ToList().ForEach(p =>
        {
            var on = view.ClaudeFilter == p.Part;
            // A click on the part that is on turns the switch off; on another part, moves it there.
            var button = Flat(new TextBlock { Text = p.Label, Foreground = Ink, FontSize = 11 }, p.Name,
                () => Actions?.FilterClaude(on ? ClaudeSessionFilter.Off : p.Part), new Thickness(8, 2, 8, 2));
            // The orange edge ALWAYS (it is the Claude switch, on or off) and the orange fill on the part that is on.
            button.BorderBrush = Orange;
            button.Background = on ? ClaudeOnFill : ButtonFill;
            button.Margin = new Thickness(0, 0, p.Part == ClaudeSessionFilter.Idle ? 0 : 2, 0);
            button.ToolTip = p.Tip;
            // Read as a checkbox's state by a screen reader and the tests, as "Current workspace" is.
            AutomationProperties.SetItemStatus(button, on ? "checked" : "unchecked");
            claude.Children.Add(button);
        });
        Filters.Children.Add(claude);
        ClaudeLabels = parts.Select(p => p.Label).ToArray();
        ClaudeFilterOn = view.ClaudeFilter;
        // The menu opens from Flat's own press, never from a Click handler added afterwards: Flat's handler marks the
        // click handled, and WPF does not call a later += handler on a handled event, so such a menu never opened
        // (2026-11-23, "dropdown doesn't work"; TasksBoardTests.The_add_rows_workspace_chip_opens_its_menu_and_a_pick_lands).
        // "Current workspace", a checkbox beside the workspace filter (task ed1a01fc; Petre, after the first cut put it in
        // the filter's menu: "let's just do a checkbox, it would be easier to tick/untick"). Ticked, the list follows the
        // workspace in front (TaskWorkspaceFilter.Current); unticked, it shows every workspace. Picking a workspace in the
        // menu beside it unticks it, since the filter then names a workspace of its own. It is drawn as a toolbar chip with
        // the icon font's box, not a WPF CheckBox: the toolbar is always the dark page, and under ThemeMode="System" a
        // light Windows theme would draw a stock CheckBox in light colours (the workspace menu's lesson, ruling g).
        // "Subtasks", a checkbox drawn the same way (task 173ef268; Petre's screenshot of a parent with eight subtasks
        // filling the panel: "checkbox at the top to show nested tasks or not"). Ticked, subtasks are drawn under their
        // parent as before; unticked, they fold into the parent's line, whose "· 8 subtasks" already says how many there
        // are, and its thread still lists them. A subtask waiting on you stays (TaskViews.FoldSubtasks). It sits before
        // "Current workspace" so that one stays next to the workspace menu it belongs with. Remembered across opens.
        if (Actions?.ShowSubtasks is { } showSubtasks)
        {
            var on = view.ShowsSubtasks;
            var subtasks = Flat(CheckContent(on, "Subtasks"), "Subtasks", () => showSubtasks(!on), new Thickness(6, 2, 8, 2));
            Picked(subtasks, on);
            subtasks.Margin = new Thickness(0, 0, 6, ChipRowGap);
            subtasks.ToolTip = on
                ? "Subtasks are drawn under their parent, here and on the bar. Untick to fold them into the parent's line, which counts them"
                : "Subtasks are folded into their parent's line, here and on the bar, which counts them. Tick to draw them under it";
            AutomationProperties.SetItemStatus(subtasks, on ? "checked" : "unchecked");
            Filters.Children.Add(subtasks);
        }
        SubtasksOn = view.ShowsSubtasks;
        var following = view.Where.FollowsCurrent;
        var current = Flat(CheckContent(following, "Current workspace"), "Current workspace",
            () => Actions?.FilterWorkspace(following ? TaskWorkspaceFilter.All : TaskWorkspaceFilter.Current), new Thickness(6, 2, 8, 2));
        Picked(current, following);
        current.Margin = new Thickness(0, 0, 6, ChipRowGap);
        current.ToolTip = "Only the tasks of the workspace you are on, following you when you switch desktops";
        // A screen reader hears it as a checkbox's state, the same word the tests read.
        AutomationProperties.SetItemStatus(current, following ? "checked" : "unchecked");
        // Not in the Inbox's Filter sheet: its header's "This workspace | Everywhere" is this switch (task 740a2231).
        if (view.Inbox is null) Filters.Children.Add(current);
        CurrentWorkspaceOn = following;
        Button? workspace = null;
        workspace = Flat(new TextBlock { Text = $"{view.WorkspaceFilterLabel} ▾", Foreground = Ink, FontSize = 11 }, "Workspace filter",
            () => OpenMenu(WorkspaceFilterMenu(view, workspace!)), new Thickness(8, 2, 8, 2));
        workspace.Margin = new Thickness(0, 0, 0, ChipRowGap);
        Filters.Children.Add(workspace);
        // The assignee filter (Ruling 57): me, Claude or all. Its menu opens from Flat's own press, like the others.
        Button? assignee = null;
        assignee = Flat(new TextBlock { Text = $"{view.AssigneeFilterLabel} ▾", Foreground = Ink, FontSize = 11 }, "Assignee filter",
            () => OpenAssigneeFilterMenu(), new Thickness(8, 2, 8, 2));
        assignee.Margin = new Thickness(6, 0, 0, ChipRowGap);
        assigneeFilterButton = assignee;
        Filters.Children.Add(assignee);
        AssigneeFilterLabel = view.AssigneeFilterLabel;
        // The goal filter, beside the workspace one, only while goals exist (ruling G1).
        goalFilterButton = null;
        if (view.ShowsGoals)
        {
            // Its menu opens from Flat's own press, as the workspace filter's does (see above): a Click handler added
            // after Flat's was never called, because Flat's own handler marks the click handled.
            var goal = Flat(new TextBlock { Text = $"{view.GoalFilterLabel} ▾", Foreground = Ink, FontSize = 11 }, "Goal filter",
                () => OpenGoalFilterMenu(), new Thickness(8, 2, 8, 2));
            goal.Margin = new Thickness(6, 0, 0, ChipRowGap);
            goalFilterButton = goal;
            Filters.Children.Add(goal);
        }
        GoalFilterLabel = view.GoalFilterLabel;
        FilterLabels = view.Filters.Select(f => f.Label).ToArray();
        // The view switch: which view is on, and whether it shows at all at this width (TasksWindow.Matrix.cs).
        PaintViewSwitch(view);
        WorkspaceFilterLabel = view.WorkspaceFilterLabel;
    }

    // The List / Matrix switch (Task 12, Ruling 28): two chips, the one on show outlined (DrawFilters).
    Button? listSwitch;
    Button? matrixSwitch;
    Button? statsSwitch;

    // The view switch, "+ Add" and the panel's ×, made once. The × is the panel's close (spec "The panel window": its
    // toolbar gains a close button at the right end, after "+ Add"), since a panel has no title bar to close it from.
    void BuildToolbarEnd()
    {
        listSwitch = Flat(new TextBlock { Text = "List", Foreground = Ink, FontSize = 11 }, "List", () => Actions?.ShowMatrix(false), new Thickness(8, 2, 8, 2));
        listSwitch.ToolTip = "The tasks grouped, one group under another";
        matrixSwitch = Flat(new TextBlock { Text = "Matrix", Foreground = Ink, FontSize = 11 }, "Matrix", () => Actions?.ShowMatrix(true), new Thickness(8, 2, 8, 2));
        matrixSwitch.ToolTip = "Covey's matrix: urgent and not urgent across, important and not important down";
        matrixSwitch.Margin = new Thickness(4, 0, 0, 0);
        // The Stats switch (task 521d2b07, TasksWindow.Stats.cs): always there, whatever the width, unlike List and Matrix.
        statsSwitch = Flat(new TextBlock { Text = "Stats", Foreground = Ink, FontSize = 11 }, "Stats", () => Actions?.ShowStats?.Invoke(), new Thickness(8, 2, 8, 2));
        statsSwitch.ToolTip = "Added, finished and open tasks per day, each task a link";
        statsSwitch.Margin = new Thickness(4, 0, 10, 0);
        // No Inbox chip here: the Inbox is reached from the panel's foot, drawn in every view (TasksWindow.Inbox.cs,
        // PaintInboxChrome), so this end is main's List, Matrix, Stats, "+ Add" and ×.
        ToolbarEnd.Children.Add(listSwitch);
        ToolbarEnd.Children.Add(matrixSwitch);
        ToolbarEnd.Children.Add(statsSwitch);
        var add = Flat(new TextBlock { Text = "+ Add", Foreground = Ink, FontSize = 11 }, "Add", ShowAdd, new Thickness(10, 2, 10, 2));
        add.ToolTip = "Write a note";
        ToolbarEnd.Children.Add(add);
        var close = Flat(new TextBlock { Text = "×", Foreground = Ink, FontSize = 13 }, "Close the panel", () => Actions?.ClosePanel(), new Thickness(7, 0, 7, 1));
        close.Background = Brushes.Transparent;
        close.Margin = new Thickness(6, 0, 0, 0);
        close.ToolTip = "Close the tasks panel";
        ToolbarEnd.Children.Add(close);
    }

    // A group's caption. The Claude working block's idle part right under its working part (task 10b49554, see the
    // header) is one block split in two, so it gets the divider instead of a caption of its own.
    UIElement Header(TaskGroupView group, TaskGroupKind? above) =>
        group.Kind == TaskGroupKind.ClaudeIdle && above == TaskGroupKind.ClaudeWorking
            ? IdleDivider()
            : new TextBlock
            {
                Tag = "task-group",
                Text = group.Header.ToUpperInvariant(),
                FontSize = 10,
                Foreground = group.Kind == TaskGroupKind.WaitingOnYou ? Orange : HeaderInk,
                Margin = new Thickness(12, 6, 12, 1),
            };

    // How much an idle line of the Claude working block is dimmed (task 10b49554): still readable, plainly quieter than the
    // working lines above it, as the approved mockup draws them.
    const double IdleOpacity = 0.72;

    // The lines drawn at IdleOpacity in the last draw, so a drag's end puts the dimming back (TasksWindow.Drag.cs).
    readonly HashSet<Guid> idleLines = [];

    // The dashed hairline with "idle" in italics at its left end, between the Claude working block's working and idle
    // lines (task 10b49554, option B of the mockup).
    static UIElement IdleDivider()
    {
        var grid = new Grid { Tag = "task-group", Margin = new Thickness(12, 5, 12, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var words = new TextBlock { Text = "idle", FontSize = 10, FontStyle = FontStyles.Italic, Foreground = HeaderInk, Margin = new Thickness(0, 0, 6, 0) };
        var dashes = new System.Windows.Shapes.Rectangle
        {
            Height = 1,
            Stroke = HeaderInk,
            StrokeThickness = 1,
            StrokeDashArray = [3, 3],
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
        };
        Grid.SetColumn(dashes, 1);
        grid.Children.Add(words);
        grid.Children.Add(dashes);
        return grid;
    }

    // A toolbar chip on or off (task 10b49554, see the header): on is the blue edge AND the blue fill.
    static void Picked(Button button, bool on)
    {
        button.BorderBrush = on ? PickedEdge : Brushes.Transparent;
        button.Background = on ? PickedFill : ButtonFill;
    }

    // One line: a button across the window, so a click anywhere on it goes to the task; the small buttons inside it
    // mark their Click handled, so the line does not also act on them.
    //
    // `inThreadOf`: the parent whose open thread this line is drawn in (task 249a1e8a; Petre: "subtasks should get all
    // buttons like the parents do; they are regular tasks, upgrade them"). A subtask listed in its parent's thread used to
    // be a bare "↳ title · status" entry (SubtaskEntry) with a tick and nothing else, so its toggles, comments, Open Claude
    // and menu were out of reach. It is now this same line, built the same way, with three differences, each so it stays
    // where it is drawn (task 0eb943d8, Petre: "let subtasks stay where they are, expand their current place"):
    //   the click   opens or folds its thread in place under it (Actions.ToggleThread) rather than picking it, since a
    //               picked task never folds (TaskViews.FoldSubtasks) and would jump out of the thread onto a line of its own;
    //   the drag    is the thread entry's (dragEntries, task f64d06ae): out of the thread frees it, inside it does nothing,
    //               and it is no drop target, as a nested line is not;
    //   the margin  sits inside the thread's frame, which already gives the inset.
    Button Line(TaskRowView row, Guid? inThreadOf = null)
    {
        // ONE COLUMN LAYOUT FOR EVERY LINE (Petre's screenshot: each line sized a column to its own content, so the chip,
        // the comments and "…" sat at a different x on every line, and a long title pushed the chip off the window).
        // Every column but the title's has one width for the whole draw, the same on every line of every group, so each
        // sits at the same x, flush to the right edge; the title takes the star column. On a full line the five text
        // columns are the header's, which Petre resizes, and the buttons sit together at the right end
        // (TasksWindow.Columns.cs, task 3df2d28f); a part's column comes from At, since the Matrix orders them its own way.
        var grid = new Grid();
        var columns = compactLines ? CompactColumns : slimLines ? SlimColumns() : FullColumns();
        columns.ToList().ForEach(width => grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width }));
        // Only the full line has the session words, the assignee and the Reopen button. The age is on the slim line too.
        var full = !compactLines && !slimLines;
        // A full line's grid follows the header's dividers while they are dragged (ApplyColumns).
        if (full) fullGrids.Add(grid);
        // The title's floor and the menu strip's cap (task 59b8f613, ConstrainFullColumns).
        if (full) ConstrainFullColumns(grid);
        // WHERE THE CONTROLS GO (tasks 65ab598d and 50667e10): on a full or a Matrix line, the line's own grid, in the
        // columns beside the title; on a slim line, a strip of their own on a row of its own under the title, starting at
        // the title's left edge (SlimControls and ControlsUnderTitle, below). A part's column comes from At either way (the
        // List's order, TasksWindow.Columns.cs); the strip starts at FirstControlColumn.
        var tucked = slimLines;
        var controls = tucked ? SlimControls(columns) : grid;
        void Control(int column, UIElement? part) => Cell(controls, tucked ? column - FirstControlColumn : column, part);
        // A subtask's line in its parent's thread names its tick after the subtask ("Close <title>"), as its entry's tick
        // was named, so it is told apart from the parent line's plain "Close".
        var left = LeftOf(row, inThreadOf is null ? null : FloatingBar.FirstLine(row.Title));
        // On a slim line the tick stays beside the title's first line while a long title wraps under it.
        if (tucked && left is FrameworkElement tick) tick.VerticalAlignment = VerticalAlignment.Top;
        Cell(grid, At(Part.Tick), left);
        // TWO ROWS: THE TITLE WITH EVERY CONTROL BESIDE IT, THEN WHAT IS UNDER THE TITLE ACROSS THE LINE (task d560f3eb,
        // "misaligned text", Petre's screenshot of a waiting task whose question stood in a column a few letters wide, two
        // or three letters to a line, with the tick, the toggles and the chip floating half way down it). The question,
        // the status line and the description used to sit under the title in the title's own column, which is what the
        // fixed columns leave over: on a slim line 286 DIP of them, so a 360 DIP panel left that column 29 DIP and a
        // question in it stood 629 DIP tall; 129 DIP at the default 460 (measured, TasksBoardTests
        // A_question_spans_the_line_under_its_title). Now the first row holds the title and the controls, centred on it as
        // before, and the second row holds what is under the title, from the title's left edge to the line's right end,
        // under the toggles, the age and the chip, which have nothing below them. The title itself stays in its column.
        // A slim line has a third row between the two: its controls' strip (ControlsUnderTitle), so what is under the
        // title comes after the strip, the question last.
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (tucked) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var text = TextOf(row);
        Cell(grid, At(Part.Title), text.Title);
        if (text.Below is { } below)
        {
            Grid.SetRow(below, tucked ? 2 : 1);
            Grid.SetColumnSpan(below, grid.ColumnDefinitions.Count - 1);
            Cell(grid, At(Part.Title), below);
        }
        if (tucked) ControlsUnderTitle(grid, text.Title, controls);
        var flags = FlagsOf(row);
        Control(At(Part.Flags), flags);
        // A Matrix line (compact) has no column for the sessions, the age or the close button, so they are not made at
        // all (whole-PR review, minor 5): no element in a zero-width column, and no age for the minute's tick to rewrite.
        // The status in words (Petre, 2026-11-23: "i can't currently see statuses"; TaskStatusMarks.Chip), in its own
        // column so it sits at the same x on every line. Only on a full line: none in a Matrix line (no room there; its
        // Done tick still shows) and none on a slim one (the tick still shows, and the status is named in its tooltip,
        // SlimTip), and none on a line in its undo window (it is closed).
        Control(At(Part.Status), full && !row.Closing ? TaskStatusMarks.Chip(row.Status, row.Delivery.Stage,
            DeployWait.Waits(row.Status, row.Delivery.Shipping, row.QuestionOptions.Count, row.AwaitsDeploy)) : null);
        // A slim line has the sessions as squares alone, and no assignee or Reopen (Task 8): those are in its tooltip
        // and its "⋯" menu (SlimTip, OpenMenuFor).
        Control(At(Part.Sessions), compactLines ? null : slimLines ? SquaresOf(row) : SessionsOf(row));
        // Who the task is on (Ruling 57): "Me" or "Claude", dim; none in a Matrix line (no column there).
        Control(At(Part.Assignee), full ? AssigneeOf(row) : null);
        // THE AGE ON EVERY LIST LINE, THE SLIM ONE INCLUDED (Petre, 2026-11-24, of the bar's "26m": "move the time from
        // here to the proper tasks window"). The bar's lines lost theirs that day, so the panel at its usual slim width
        // must show it, not only its tooltip. None in a Matrix line (no column there).
        Control(At(Part.Age), compactLines ? null : AgeOf(row));
        Control(At(Part.Place), PlaceChip(row));
        var comments = CommentsButton(row);
        Control(At(Part.Comments), comments);
        // The action column: Reopen on a closed task (full line only), else Open Claude on a Claude task (task 7f61af2e,
        // OpenClaudeButton), on the full and the slim line. The two never meet: a closed task has no Open Claude. A task no
        // session has joined has Start Claude there instead (task 59b8f613, StartClaudeButton), the menu's other half.
        var openClaude = compactLines ? null : OpenClaudeButton(row) ?? StartClaudeButton(row);
        // Reevaluate (task 3dd07539) right after Open Claude, in the same column: Petre's pick, "a refresh icon right after
        // the orange Claude icon, on every Claude task" (TasksWindow.LineButtons.cs, ReevaluateButton).
        // On a full line only: a slim line has it as the first slot of its strip, right after this column (SizeColumns).
        var reevaluate = full ? ReevaluateButton(row) : null;
        UIElement? claudeButtons = reevaluate is null || openClaude is null ? openClaude
            : new StackPanel { Orientation = Orientation.Horizontal, Children = { openClaude, reevaluate } };
        Control(At(Part.Action), (full ? ActionOf(row) : null) ?? claudeButtons);
        var more = Small(new TextBlock { Text = "", FontFamily = IconFont, FontSize = 12, Foreground = Ink }, "More",
            () => OpenMenuFor(row.Id), "Edit, status, move, delete");
        menuButtons[row.Id] = more;
        // On a List line, "⋯" ends a strip of the menu's actions as icon buttons, as many as the line has room for (task
        // 59b8f613, TasksWindow.LineButtons.cs). A Matrix line has no room: "⋯" alone, as before.
        var lineActions = compactLines ? null : LineButtons(row, more);
        Control(At(Part.More), (UIElement?)lineActions ?? more);
        // A CLOSED SUBTASK IN ITS PARENT'S THREAD fades the same way (task a78ce57a, ClosedInThread): the line keeps its
        // tick column, which shows the ticked box (TickOf), and everything else goes to 0.45.
        var faded = row.Closing || ClosedInThread(row);
        if (faded)
            grid.Children.OfType<UIElement>().Where(part => Grid.GetColumn(part) != At(Part.Tick)).ToList().ForEach(part => part.Opacity = 0.45);
        // The comments and "…" buttons are quiet until the pointer is on their line, like the bar's quiet controls, and so
        // are the menu's actions beside "⋯". A line in its undo window keeps its own dimming (above): its quiet buttons are
        // not brightened by a hover or a selection (review minor).
        var quiet = faded ? Array.Empty<UIElement>()
            : new UIElement?[] { comments, more, openClaude, reevaluate }.OfType<UIElement>().Concat(flags?.Children.OfType<UIElement>() ?? [])
                .Concat(lineActions?.Children.OfType<Button>().Where(button => !ReferenceEquals(button, more)) ?? []).ToArray();
        Quieten(quiet, loud: false);

        // EVERY LINE IS A TILE (Petre's pick, option E of the task-separation mockup, 2026-11-25; the task was "better task
        // separation with lines or something", from a screenshot where two-line Claude tasks with status lines ran into
        // each other). Three layers, outermost first:
        //   the Button   the tile: its Background is the tile's fill (TileFill, or AskTint while the task waits on Petre),
        //                its template rounds the corners and washes it lighter under the pointer (LineButton in the XAML),
        //                and its Margin leaves TileGap between one tile and the next;
        //   the frame    the selection (ruling 21, as on the bar): 2 DIP on the left, clear unless this is the task Petre
        //                picked, then the text box's blue over a translucent fill that lightens the tile. Always there, so
        //                a pick moves nothing; a change of selection repaints it in place (PaintFrame), not the lines;
        //   the edge     the 3 DIP orange edge of a task Claude works on, now one rounded piece per tile, so a run of
        //                Claude tasks no longer reads as one long stripe.
        // The waiting tint moved from the edge to the tile, so it fills the whole tile, rounded corners included.
        var edge = new Border
        {
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = row.Claudes ? Orange : Brushes.Transparent,
            CornerRadius = new CornerRadius(3, 0, 0, 3),
            // RULING 64 (Petre: "same here, make more compact", after the bar's line gap went from 8 to 3 DIP) set 1.5 DIP above
            // and below the text. A tile needs a little air inside its fill or the text touches its edges, so 3 DIP now,
            // with the 3 DIP between tiles (TileGap) doing the separating. 4 DIP before the tick, as before.
            Padding = new Thickness(4, 3, 8, 3),
            Child = grid,
        };
        var frame = new Border { BorderThickness = new Thickness(2, 0, 0, 0), CornerRadius = new CornerRadius(4), Child = edge };
        selectionFrames[row.Id] = (frame, quiet);
        PaintFrame(row.Id);
        var line = new Button
        {
            Tag = row.Id,
            Content = frame,
            Template = (ControlTemplate)FindResource("LineButton"),
            // The tile's fill: the waiting tint while the task waits on Petre, else the plain tile.
            Background = row.Status == TaskStatus.WaitingOnYou && !row.Closing ? AskTint : TileFill,
            // The gap above each tile, and an inset from the panel's sides so the rounded corners show. A Matrix line
            // (compact) sits in a cell that already has its own inset, so it keeps the full width of the cell.
            // A subtask's line in its parent's thread (inThreadOf) is inside the thread's frame, which has its own padding.
            Margin = inThreadOf is not null ? new Thickness(0, 0, 0, TileGap)
                : new Thickness(compactLines ? 0 : TileInset, TileGap, compactLines ? 0 : TileInset, 0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            // EXPLICIT: the app runs under ThemeMode="System" (App.xaml), whose Fluent Button style sets HorizontalAlignment to
            // Left. A line left to the default shrank to its own content, so every line had its own width and the columns
            // followed each line's own right edge (Petre's zigzag, 2.5.238). Tests build the window without that theme unless
            // they ask for it, which is why they passed.
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Focusable = false,
            Cursor = Cursors.Hand,
            // A slim line's tooltip also carries the age and the assignee its line has no room for (SlimTip).
            ToolTip = slimLines ? SlimTip(row, row.Closing ? LineTip(row) : row.Title) : LineTip(row),
        };
        // A subtask's line in its parent's thread keeps the name its entry had there, "Subtask <title>", so a screen reader
        // still hears what it is and the tests still find it apart from a line of its own.
        AutomationProperties.SetName(line, inThreadOf is null ? row.Title : $"Subtask {FloatingBar.FirstLine(row.Title)}");
        line.MouseEnter += (_, _) => Quieten(quiet, loud: true);
        line.MouseLeave += (_, _) => Quieten(quiet, loud: Picked(row.Id));
        // The click guard, as on the line's buttons (ClickIgnored); and the press that may become a drag of the line
        // (TasksWindow.Drag.cs), armed only, so a press released in place stays this Click.
        line.PreviewMouseLeftButtonDown += (_, e) =>
        {
            // A press on the grip under the open line's description or question resizes that text (task 2088e1ed,
            // TasksWindow.BodyGrip.cs): the grip takes it, and the line neither picks, folds nor arms a drag.
            if (IsOnBodyGrip(e.OriginalSource)) return;
            // A PRESS ON A LINK IN THE OPEN LINE'S DESCRIPTION IS THE LINK'S (task e117cb92; Petre, of a WEB-657 link
            // under a task's title: "link in the description doesn't get clicked; it closes the issue panel"). The
            // description box takes no hits, so a press anywhere on it is a press on the line (DescriptionBlock says why),
            // and a press on its link folded the line instead of opening the address. So the line asks first whether the
            // press is over one of the description's links (DescriptionLinkAt) and, if it is, raises that link's Click,
            // which opens it as a question's or a comment's link opens (in the task's workspace, LinkOpener). Handled, so
            // the line neither picks, folds nor arms a drag. A second press of a double-click on a link is swallowed:
            // the first one already opened it, and a double-click must not also take Petre to the task's workspace.
            // The repaint guard holds here as on every other click (RepaintGrace, read directly because ClickIgnored also
            // spends pressClicks): a line that slid under the pointer in the last half second, as the one below a closed
            // line does, must not open an address Petre never aimed at. A line in its undo window opens nothing either,
            // as its own Click picks nothing: Undo is the one thing on it to press.
            if (DescriptionLinkAt(line, e.GetPosition) is { } descriptionLink)
            {
                e.Handled = true;
                if (e.ClickCount == 1 && !row.Closing && ClickClock() - linesChangedAt >= RepaintGrace)
                    descriptionLink.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent, descriptionLink));
                return;
            }
            // RULING 54 (Petre: "clicking a task should open that task; double clicking takes me to the workspace and
            // window"): a double-click anywhere on the line goes there, as the bar's does. Handled, so the line neither
            // clicks again nor arms a drag; its first click already picked and expanded the task (the Click below). A
            // press on one of the line's own buttons (tick, toggles, chip, comments, "...") is that button's, and one
            // in the rename editor is the editor's. This replaced the double-click on a title that edited it (Ruling 38):
            // editing is F2, or Edit in the menu. With it went the title click's half-second wait for a second click:
            // every click acts at once now.
            if (e.ClickCount == 2 && !row.Closing && row.Status != TaskStatus.Closed && renaming != row.Id && IsTheLinesOwn(line, e.OriginalSource))
            {
                e.Handled = true;
                Actions?.Go(row.Id);
                return;
            }
            pressClicks = e.ClickCount;
            DragPress(row.Id, DragMouse.Position(e, DragSurface), fromThread: inThreadOf is not null);
        };
        // A subtask drawn under its parent is dragged (out, to free it) but is no drop target (review of #467, finding 2).
        // One drawn in its parent's thread (inThreadOf) drags as the thread's entry did (task f64d06ae): kept with the
        // parent whose thread it is in, and armed only while it is open and not in its undo window.
        if (inThreadOf is { } threadParent)
        {
            if (row.Status != TaskStatus.Closed && !row.Closing)
                dragEntries[row.Id] = (line, threadParent, row.Quadrant, TaskDropTarget.PartOf(row.Status, row.Delivery.Shipping));
        }
        else
            dragLines[row.Id] = (row.Quadrant, row.Status.IsPinned() && !row.Closing, row.Closing || row.Status == TaskStatus.Closed, row.Nested,
                row.Closing ? TaskDropTarget.Part.None : TaskDropTarget.PartOf(row.Status, row.Delivery.Shipping));
        // A right-click anywhere on the line opens its menu at the pointer, Edit first (Ruling 44, OpenMenuFor).
        line.PreviewMouseRightButtonUp += (_, e) =>
        {
            if (renaming == row.Id) return; // a right-click in the editor is the editor's own
            e.Handled = true;
            OpenMenuFor(row.Id, atPointer: true);
        };
        line.Click += (_, _) =>
        {
            // A line just closed picks nothing; Undo is the one thing on it to press. A line being renamed neither.
            if (ClickIgnored() || row.Closing || renaming == row.Id) return;
            // A single click picks the task and opens its thread; it goes nowhere (Ruling 54). A subtask's line in its
            // parent's thread opens or folds its thread in place instead (see inThreadOf above).
            // On an Inbox page a subtask opens a page of its own (TasksWindow.InboxPage.cs, task 740a2231).
            if (inThreadOf is not null) { if (!OpenSubtaskPage(row.Id)) Actions?.ToggleThread(row.Id); }
            else Actions?.Pick(row.Id);
        };
        return line;
    }

    // Whether a press landed on the line itself rather than on a button inside it or on a text box (the rename editor).
    static bool IsTheLinesOwn(Button line, object? source)
    {
        for (var node = source as DependencyObject; node is not null; node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, line)) return true;
            if (node is Button or System.Windows.Controls.Primitives.TextBoxBase) return false;
        }
        return false;
    }


    // The line's columns: since task 3df2d28f they are TasksWindow.Columns.cs's (FullColumns, SlimColumns, the order in
    // ListOrder). Before it a full line had fixed columns sized for the widest thing each could ever hold, 560 DIP in all:
    // the toggles after the title (56), status chip (72), sessions (116, for "2 sessions, 1 live"), assignee (46), age (44),
    // workspace chip (88), comments (40), action (52) and "…" (24). Now the five text columns start at about 300 DIP in
    // all and Petre resizes them from the header, and the buttons sit together at the right end, as narrow as their
    // glyphs. The age stays left-aligned in its own column so its x never follows the text length. The full line is
    // drawn only from 850 DIP (TasksPanelLayout.SlimBelow; narrower, the slim line below). It was drawn from 710, the
    // width at which its columns merely fitted beside a 100 DIP title, until Petre's "wider bar renders worse than
    // narrow" (task 39d58e5b): the title then wrapped into five lines where the slim line read it in one. From 850 the
    // title never has less than TasksPanelLayout.FullTitleReadable, 320 DIP: the menu buttons at the line's end give way
    // first (FullColumns), and a window test measures it.

    // LINES AT PANEL WIDTH (spec docs/superpowers/specs/2026-11-23-tasks-panel-design.md, "Lines at panel width"; Petre's
    // pick, "slim line when narrow"; plan 2026-11-23-tasks-panel, Task 8). Below TasksPanelLayout.SlimBelow (850 DIP; the
    // view's Slim) a line is slim: tick, title (a Done task's green tick still in front of it), the sessions as squares
    // alone (no words), the age (since 2026-11-24, when it left the bar), the workspace chip, then the buttons: the
    // toggles and the snooze moon, the comment count, Open Claude and "⋯". The status chip and the assignee move into the
    // line's tooltip, with the age again (SlimTip, "Done · 5h · Claude"); Close and Reopen were already in "⋯"
    // (OpenMenuFor), so the Reopen button goes without anything becoming unreachable. Same column indexes as the full
    // line, the dropped ones zero wide and left empty, and no header over it. The switch happens only when the width
    // changes at the grip's drag end, never during a drag (TasksBoard.ResizePanel), and then every line redraws once.
    bool slimLines;
    GridLength slimSquaresWidth = new(0);

    // THE SLIM LINE'S TITLE HAS THE WHOLE LINE, AND THE CONTROLS HAVE A ROW OF THEIR OWN UNDER IT, FROM THE LEFT (tasks
    // 65ab598d and 50667e10). The slim line's fixed columns come to about 300 DIP, so with the title in a column of its
    // own beside them a 360 DIP panel left the title about 15 DIP and it broke one letter per line (65ab598d). The title
    // therefore spans from its column to the line's right end and wraps at that width. Its controls (squares, age,
    // workspace chip, toggles and moon, comments, Open Claude, "⋯") first sat at the right end of the title's last line,
    // dropping under it when the text ran into them; Petre, on a screenshot of that (task 50667e10): "give buttons and
    // status and all its own line and align on the left". So they are now one strip, always on the row under the title,
    // starting at the title's left edge. The strip keeps the slim columns' fixed widths, so each control still sits at the
    // same x on every line, and every slim line has the same shape whatever its title's length. What is under the title
    // (the question, the status line, the description) comes after the strip.
    //
    // The first control column of a line (the toggles'); the tick and the title come before it.
    const int FirstControlColumn = 2;

    // The strip of a slim line's controls: the slim columns from the toggles on, left-aligned in the row under the title.
    // All fixed, so it cannot shrink: at the narrowest panel, with the scroll bar and a line of five sessions, it is 297
    // DIP in a 303 DIP slot since the button columns grew to their true widths (task 735abbf5, measured by
    // At_the_narrowest_panel_the_controls_strip_fits_its_line, which fails if it outgrows its line).
    //
    // The last column, "⋯" with the menu's actions before it (task 59b8f613, FitStrip), takes the rest of the line: the
    // strip stretches across the line and that column is a star, so the actions get exactly the room left over and show
    // as many as fit. Every column before it is fixed, so nothing else moves.
    static Grid SlimControls(IReadOnlyList<GridLength> columns)
    {
        var strip = new Grid { Tag = ControlsTag, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(2, 2, 0, 0) };
        columns.Skip(FirstControlColumn).SkipLast(1).Append(new GridLength(1, GridUnitType.Star)).ToList()
            .ForEach(width => strip.ColumnDefinitions.Add(new ColumnDefinition { Width = width }));
        return strip;
    }

    // Tagged for tests: the strip of a slim line's controls.
    internal const string ControlsTag = "task-controls";

    // Puts the title across the whole slim line and the controls' strip in the row under it, at the title's left edge
    // (row 1 of the line; what is under the title goes to row 2, Line). While the line is being renamed there is no title
    // block, only the editor, which then gets the whole line and its own Save and Cancel; the strip is collapsed until the
    // rename ends and the line is drawn again.
    static void ControlsUnderTitle(Grid line, UIElement title, Grid strip)
    {
        var titleColumn = Grid.GetColumn(title);
        Grid.SetColumnSpan(title, line.ColumnDefinitions.Count - titleColumn);
        Grid.SetRow(strip, 1);
        Grid.SetColumn(strip, titleColumn);
        Grid.SetColumnSpan(strip, line.ColumnDefinitions.Count - titleColumn);
        line.Children.Add(strip);
        if (TitleBlockIn(title) is null) strip.Visibility = Visibility.Collapsed;
    }

    // The title's own text block inside what TextOf made: the block itself, or inside the Done tick's row.
    static TextBlock? TitleBlockIn(UIElement part) => part switch
    {
        TextBlock block when Equals(block.Tag, TitleTag) => block,
        Panel panel => panel.Children.OfType<UIElement>().Select(TitleBlockIn).FirstOrDefault(block => block is not null),
        _ => null,
    };

    // THE OPEN CLAUDE BUTTON (task 7f61af2e; Petre, on the menu item: "add a button on the task in the tasklist for it
    // too"). A line's single click opens the task (picks it and expands its thread, Ruling 54) and its double-click goes
    // to the session only after that; this button goes at once, the line menu's "Open Claude" and the double-click's go
    // (Actions.Go: the task's workspace, then its session's window, resuming an ended session first). Its own Click is
    // handled as every small button's is, so the line does not also pick the task and open the thread, and a press on it
    // is not the line's double-click (IsTheLinesOwn stops at a Button). On a Claude task's line, full or slim, in the
    // action column (Reopen's, which a closed task alone uses, and a closed task has no button); none in a Matrix line,
    // whose menu has the item. Quiet like the comments and "⋯": half strength until the line is hovered or selected.
    // Its picture is the Claude spark in orange, the mark Start Claude has in plain ink in this same column (task
    // 5e02e99f, ClaudeMark in TasksWindow.LineButtons.cs): one picture, the colour says whether there is a session.
    Button? OpenClaudeButton(TaskRowView row) =>
        OpensClaude(row)
            ? Small(ClaudeMark(hasSession: true),
                OpenClaudeText, () => Actions?.Go(row.Id), "Open Claude: go to its session without opening the task")
            : null;

    // Whether a line has the Open Claude button: a Claude task that is open, not in its undo window, and whose session the
    // click can open (TaskRowView.OpensSession, task 6a57f59b). A task whose session cannot come back (its worktree was
    // removed, or its transcript is gone) has Start Claude on this in that column instead (StartsClaude), as its thread
    // and its menu do: an orange icon whose click opened nothing is what Petre reported as "doesn't open claude".
    static bool OpensClaude(TaskRowView row) => row.Claudes && row.OpensSession && !row.Closing && row.Status != TaskStatus.Closed;

    // The slim line's age column (2026-11-24, the age moved off the bar), wide enough that the text never trims. The
    // widest age TaskAge.Format writes is one in hours and minutes, "23h59m": "m" is its widest letter. Measured from
    // Segoe UI's advance widths at the age's 10.5 size it is 37.6 DIP ("27d23h" is 34.8), plus the 6 DIP left margin
    // AgeOf gives it, 43.6. The column was 42, sized for "27d23h" alone, so an age such as "12h59m" read "12h5…"
    // (task f564a7cd, Petre's screenshot); 46 leaves a little over 2 DIP for rounding.
    const double SlimAgeColumn = 46;

    // A slim line's squares: 8 DIP each, 3 DIP apart. The column fits the line with the most squares and no more than
    // SlimSquares of them (47 DIP, inside the plan's 48), with 3 DIP to spare before the chip; zero on a board of notes.
    const double SlimSquare = 8;
    const double SquareGap = 3;
    const int SlimSquares = 4;
    const double SquaresColumnMost = 48;

    static GridLength SquaresColumn(int widest) =>
        widest == 0 ? new(0) : new(Math.Min(SquaresColumnMost, Math.Min(widest, SlimSquares) * (SlimSquare + SquareGap) + SquareGap));

    // The sessions of a slim line: one square per session up to SlimSquares, the live ones first (the working ones first
    // of those), then the ended ones. No words: the counts are in the thread.
    UIElement? SquaresOf(TaskRowView row)
    {
        if (row.SessionCount == 0) return null;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Enumerable.Range(0, Math.Min(SlimSquares, row.SessionCount)).ToList()
            .ForEach(i => panel.Children.Add(SessionSquare(SlimSquare, MarkOf(row, i))));
        return panel;
    }

    enum SessionMark { Ended, Live, Working }

    // Square i of a line: the first WorkingCount are working, the rest of the first LiveCount live, the others ended.
    static SessionMark MarkOf(TaskRowView row, int i) =>
        i < Math.Min(row.WorkingCount, row.LiveCount) ? SessionMark.Working
        : i < row.LiveCount ? SessionMark.Live
        : SessionMark.Ended;

    // ONE SESSION'S SQUARE, on the full line (10 DIP) and the slim one (8): the dim fill once ended, the live blue while
    // live, and the live blue inside a 2 DIP halo while Claude is busy in it (Task 8: the row's WorkingCount, from the
    // latest `claude agents --json`). The halo is a translucent ring drawn OUTSIDE the square's footprint (negative
    // margins), so a working square takes exactly the room a plain one does and nothing on the line moves when Claude
    // starts or stops. Tagged with its mark for the tests.
    static Border SessionSquare(double size, SessionMark mark)
    {
        var square = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(2),
            Background = mark == SessionMark.Ended ? EndedSession : LiveSession,
        };
        if (mark != SessionMark.Working)
        {
            square.Tag = mark == SessionMark.Live ? "session-live" : "session-ended";
            square.Margin = new Thickness(0, 0, SquareGap, 0);
            return square;
        }
        return new Border
        {
            Tag = "session-working",
            Width = size + 2 * HaloWidth,
            Height = size + 2 * HaloWidth,
            Padding = new Thickness(HaloWidth),
            CornerRadius = new CornerRadius(2 + HaloWidth),
            Background = Halo,
            Margin = new Thickness(-HaloWidth, -HaloWidth, SquareGap - HaloWidth, -HaloWidth),
            Child = square,
            ToolTip = "Claude is working in this session",
        };
    }

    const double HaloWidth = 2;
    static readonly Brush Halo = Frozen(0x66, 0x3B, 0x82, 0xF6);

    // What a line's tooltip says: its status and title, or that it was just closed.
    // A Done task's change not running here yet, or running, adds a line saying so (task 8470d176, TaskDelivery.Note).
    static string LineTip(TaskRowView row) =>
        row.Closing ? "Closed. Undo brings it back." : $"{StatusWords(row)}: {row.Title}{DeliveryLine(row)}";

    // The tooltip's line about where a Done task's change stands against this Deskbar, with its line break, or nothing.
    static string DeliveryLine(TaskRowView row) => row.Delivery.Note.Length > 0 ? $"\n{row.Delivery.Note}" : "";

    // A line's status in words: "Duplicate of <title>" for a closed duplicate (task links), the status's name otherwise.
    static string StatusWords(TaskRowView row) =>
        row.DuplicateOfTitle is { } original ? $"{TaskViews.DuplicateName} of {original}" : TaskViews.StatusName(row.Status);

    // A slim line's tooltip: its title (or that it was just closed), then what the line has no columns for: the status the
    // full line shows in its chip, the age and the assignee ("Done · 5h · Claude"). The title alone heads it, without the
    // full line's "Done: " in front, since the status follows on the next line. A TextBlock rather than a string so the
    // minute's tick (RefreshAges) rewrites its age in place, as it does the full line's age column, and an open tooltip
    // never shows an age frozen at the last redraw.
    readonly Dictionary<Guid, (TextBlock Tip, string Head, string Status, DateTimeOffset Created, string Assignee)> slimTips = [];

    // A line with more sessions than it has squares for (SlimSquares) also says them in words, "6 sessions, 2 live", so
    // the ones past the fourth leave a trace (review minor 3).
    TextBlock SlimTip(TaskRowView row, string head)
    {
        var tip = new TextBlock { Tag = "task-line-tip" };
        slimTips[row.Id] = (tip, (row.SessionCount > SlimSquares ? $"{head}\n{row.Sessions}" : head) + (row.Closing ? "" : DeliveryLine(row)), StatusWords(row),
            row.Created, row.Assignee.Name);
        WriteSlimTip(slimTips[row.Id], Now());
        return tip;
    }

    static void WriteSlimTip((TextBlock Tip, string Head, string Status, DateTimeOffset Created, string Assignee) tip, DateTimeOffset now) =>
        tip.Tip.Text = $"{tip.Head}\n{tip.Status} · {TaskAge.Format(now - tip.Created)} · {tip.Assignee}";

    // THE TWO TOGGLES (Task 12), the bar's (FloatingBar.FlagToggle): important, Segoe Fluent Icons' "Important" U+E8C9,
    // and urgent, its "Recent" clock U+E823 (Segoe MDL2 Assets carries both on Windows 10). On, the glyph wears its
    // colour; off it is dim and quiet: invisible until the line is hovered or selected, or dimmed on a slim line, whose
    // controls have a row of their own (Quieten); still clickable either way.
    // Automation status "on" or "off", tooltip the flag's name. None on a closed task or a line in its undo window.
    StackPanel? FlagsOf(TaskRowView row)
    {
        if (row.Closing || row.Status == TaskStatus.Closed) return null;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        new[] { (TaskFlag.Important, "Important", "\uE8C9", row.Important, ImportantInk), (TaskFlag.Urgent, "Urgent", "\uE823", row.Urgent, UrgentInk) }
            .ToList().ForEach(flag =>
            {
                var toggle = Small(new TextBlock { Text = flag.Item3, FontFamily = IconFont, FontSize = 12, Foreground = flag.Item4 ? flag.Item5 : DimInk },
                    flag.Item2, () => Actions?.SetFlag(row.Id, flag.Item1, !flag.Item4), flag.Item2);
                toggle.Padding = new Thickness(2, 0, 2, 1);
                toggle.Tag = flag.Item4 ? FlagOnTag : FlagOffTag;
                AutomationProperties.SetItemStatus(toggle, flag.Item4 ? "on" : "off");
                panel.Children.Add(toggle);
            });
        if (SnoozeButton(row) is { } snooze) panel.Children.Add(snooze);
        if (RepeatMark(row) is { } mark) panel.Children.Add(mark);
        return panel;
    }

    // THE REPEAT MARK (task 0b78269e, Task 9; Petre picked candidate A of the mockup): Segoe Fluent's RepeatAll, U+E8EE,
    // in the dim ink, after the snooze moon. It is a mark, not a button: a plain glyph whose tooltip says the schedule, the
    // next run and the model ("Repeats every hour, 09:00-19:00 · next run Thu 09:00 · sonnet"; TaskRepeats.MarkTip, shared
    // with the bar). Always shown (RepeatMarkTag, which Quieten leaves at full strength): it says what the task IS, where
    // the toggles only answer a hover. Margin 3 and 1 DIP below make it a toggle's 18 DIP wide (ToggleWidth) so it
    // fits the flags column. None on a Matrix line, which has no room (the same as the moon), nor on a plain task.
    TextBlock? RepeatMark(TaskRowView row) =>
        compactLines || row.RepeatLabel is not { } label ? null
            : new TextBlock
            {
                Text = "\uE8EE", FontFamily = IconFont, FontSize = 12, Foreground = DimInk, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(3, 0, 3, 1), Tag = RepeatMarkTag,
                ToolTip = TaskRepeats.MarkTip(label, row.NextRun, row.RepeatModel ?? TaskRepeat.DefaultModel, Now()),
            };

    internal const string RepeatMarkTag = "task-repeat-mark";

    // THE SNOOZE BUTTON (task 6da8d7eb, Petre: "task should have a snooze button, which will hide it from view for a while";
    // and on the task, "only in the large tasks view", so the bar's lines have none). Third after the two toggles and
    // quiet like an off toggle: invisible until the line is hovered or selected (dimmed on a slim line). Segoe's "QuietHours" moon, U+E708, in
    // both icon fonts (checked). A press opens the lengths (SnoozeMenu); on a snoozed line, which only the Snoozed filter
    // shows, the moon is lit and the menu offers "Wake now" first. A line waiting on Petre has one too (Petre, 2026-11-25:
    // "i may snooze something that's waiting"; TaskBoard.Snooze says what happens then). None in a Matrix line, whose
    // flags column has room for two glyphs only; "Snooze" in the line's "⋯" menu reaches it there.
    Button? SnoozeButton(TaskRowView row)
    {
        if (Actions?.Snooze is null || compactLines) return null;
        var snoozed = row.SnoozedUntil is not null;
        Button? button = null;
        button = Small(new TextBlock { Text = "", FontFamily = IconFont, FontSize = 12, Foreground = snoozed ? SnoozeInk : DimInk },
            "Snooze", () => OpenSnoozeMenu(row.Id, button), snoozed ? "Snoozed: wake it, or snooze it for longer" : "Snooze: hide it for a while");
        button.Padding = new Thickness(2, 0, 2, 1);
        button.Tag = snoozed ? FlagOnTag : FlagOffTag;
        AutomationProperties.SetItemStatus(button, snoozed ? "on" : "off");
        return button;
    }

    static readonly Brush SnoozeInk = Frozen(0xFF, 0x9F, 0xA8, 0xFF);

    /// <summary>
    /// The snooze lengths under <paramref name="anchor"/> (the moon), or at the pointer: "Wake now" first on a snoozed task,
    /// then TaskSnooze's four, each with the time it ends where that is a clock time. Internal, answering the menu, for the tests.
    /// </summary>
    internal ContextMenu OpenSnoozeMenu(Guid id, UIElement? anchor)
    {
        var menu = anchor is null
            ? OwnMenus.AtPointer(this) // this window as its target, for its screen's scaling
            : new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        SnoozeItems(id).ForEach(item => menu.Items.Add(item));
        OpenMenu(menu);
        return menu;
    }

    // The items both the moon's menu and the "⋯" menu's "Snooze" submenu list. Labels are worked out now, on the local
    // clock, so "Tomorrow, Thu 09:00" names the right day.
    List<object> SnoozeItems(Guid id)
    {
        var row = RowFor(id);
        var now = Now().ToLocalTime();
        var items = new List<object>();
        if (row?.SnoozedUntil is { } until)
        {
            items.Add(Item("Wake now", () => Actions?.Wake?.Invoke(id)));
            // When it comes back, as a dim line that does nothing.
            var when = Item($"Snoozed {TaskSnooze.UntilText(until.ToLocalTime(), now)}");
            when.IsEnabled = false;
            items.Add(when);
            items.Add(new Separator());
        }
        TaskSnooze.Choices.ToList().ForEach(choice =>
            items.Add(Item(TaskSnooze.Label(choice, now, TimeZoneInfo.Local.GetUtcOffset), () => Actions?.Snooze?.Invoke(id, choice))));
        return items;
    }

    const string FlagOnTag = "task-flag-on";
    const string FlagOffTag = "task-flag-off";
    static readonly Brush ImportantInk = Frozen(0xFF, 0xFF, 0x6B, 0x6B);
    static readonly Brush UrgentInk = Frozen(0xFF, 0xFF, 0xD5, 0x4F);

    // THE AGE ON EACH LINE (ruling 26, as on the bar): TaskAge's compact form, dim, right-aligned before the chip, the
    // full date and time in its tooltip. Rewritten in place once a minute while the window is open (RefreshAges), never
    // by a redraw, and not while a redraw would wait (MustWait). Now is the clock, a seam for the tests.
    internal Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    readonly Dictionary<Guid, (TextBlock Text, DateTimeOffset Created)> ages = [];

    /// <summary>Rewrites every age on show for the time now; nothing else changes.</summary>
    internal void RefreshAges()
    {
        if (MustWait) return;
        var now = Now();
        WriteAges(now);
        RefreshManagerAge(now);
    }

    // Every line's age and slim tooltip for `now`. Also after each List draw, for the lines it kept (TasksWindow.LineReuse.cs):
    // a kept line's age was written when it was built, which may be hours ago when the panel was closed in between.
    void WriteAges(DateTimeOffset now)
    {
        ages.Values.ToList().ForEach(age =>
        {
            age.Text.Text = TaskAge.Format(now - age.Created);
            age.Text.ToolTip = AgeTip(now, age.Created);
        });
        // A slim line's age is in its tooltip (Task 8).
        slimTips.Values.ToList().ForEach(tip => WriteSlimTip(tip, now));
        RefreshInboxTimes(now);
    }

    internal string AgeText(Guid id) => ages.TryGetValue(id, out var age) ? age.Text.Text : "";

    // The age's tooltip: "Added 2 hours 3 minutes ago", then the date and time, so the compact "2h3m" cannot be taken for
    // a timer or a due time. The bar's age had this tooltip (Ruling 46: Petre "misread them for what they were"); it came
    // here with the age on 2026-11-24.
    internal static string AgeTip(DateTimeOffset now, DateTimeOffset created) =>
        $"{TaskAge.Words(now - created)}\n{created.ToLocalTime():dddd d MMMM yyyy, HH:mm}";

    TextBlock AgeOf(TaskRowView row)
    {
        var now = Now();
        var text = new TextBlock
        {
            Tag = "task-age",
            Text = TaskAge.Format(now - row.Created),
            FontSize = 10.5,
            Foreground = HeaderInk,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = AgeTip(now, row.Created),
        };
        ages[row.Id] = (text, row.Created);
        return text;
    }

    // The icon font of Windows 11, with Windows 10's as the fallback.
    static FontFamily IconFont => FloatingBar.IconFont;

    // THE SELECTED TASK (ruling 21): which line is drawn as picked, the frame of each line on show, and their quiet
    // buttons, which a selected line shows at full strength as hover does.
    Maybe<Guid> selectedId = Maybe<Guid>.None;
    readonly Dictionary<Guid, (Border Frame, IReadOnlyList<UIElement> Quiet)> selectionFrames = [];
    static readonly Brush SelectedFill = Frozen(0x38, 0xFF, 0xFF, 0xFF);

    // Whether a task is the one picked, as the selection says (IsSelected, for the tests, reads what is drawn).
    bool Picked(Guid id) => selectedId.HasValue && selectedId.Value == id;

    /// <summary>Draws the selection as it now stands, in place.</summary>
    internal void PaintSelection(Maybe<Guid> selected)
    {
        selectedId = selected;
        selectionFrames.Keys.ToList().ForEach(PaintFrame);
        PaintInboxSelection();
        // In the Matrix the selected task's cell becomes the active one (TasksWindow.AddFlags.cs).
        if (selected.HasValue) FollowSelectionToCell(selected.Value);
    }

    void PaintFrame(Guid id)
    {
        if (!selectionFrames.TryGetValue(id, out var parts)) return;
        var selected = Picked(id);
        parts.Frame.BorderBrush = selected ? PickedEdge : Brushes.Transparent;
        parts.Frame.Background = selected ? SelectedFill : Brushes.Transparent;
        Quieten(parts.Quiet, loud: selected);
    }

    /// <summary>Brings the selected task's line into view, as the window opens.</summary>
    internal void ScrollToSelected()
    {
        // The Inbox opens an explicitly shown task's page in its tab (TasksWindow.InboxPage.cs, task 740a2231).
        if (InboxFollowsSelection(open: true)) return;
        if (selectedId.HasNoValue || !selectionFrames.TryGetValue(selectedId.Value, out var parts)) return;
        UpdateLayout();
        // The line's place in the list, a third of the way down the view; the scroll itself runs in the next layout
        // pass, which is run here so the window opens already there. In the Matrix it is the line's own cell that
        // scrolls (whole-PR review, minor 5: measuring against the List's rows threw there).
        var (scroller, panel) = matrixShown && cells.Values.FirstOrDefault(c => parts.Frame.IsDescendantOf(c.Lines)) is { Lines: not null } cell
            ? ((ScrollViewer)cell.Lines.Parent, (Visual)cell.Lines)
            : (Scroller, Rows);
        if (!parts.Frame.IsDescendantOf(panel)) return;
        var top = parts.Frame.TransformToAncestor(panel).Transform(new Point(0, 0)).Y;
        scroller.ScrollToVerticalOffset(Math.Max(0, top - scroller.ViewportHeight / 3));
        UpdateLayout();
        // A line inside a capped thread comes out of the thread's own scroll, and an open thread shows whole with its
        // line (task ee9bd3fc, TasksWindow.ThreadCap.cs).
        ShowSelectedThread(selectedId.Value, parts.Frame);
        UpdateLayout();
    }

    /// <summary>
    /// The glow of a task just added from the panel's add box (TaskGlow), on its line when that line is on show and is the
    /// selected one. The next paint of the selection (PaintFrame) puts the plain selected fill back.
    /// </summary>
    internal void Glow(Guid id)
    {
        if (InboxGlow(id)) return; // the Inbox shows it in its tab (TasksWindow.InboxLine.cs, task 740a2231)
        if (Picked(id) && selectionFrames.TryGetValue(id, out var parts)) parts.Frame.Background = TaskGlow.Fading(SelectedFill);
    }

    // For the tests: whether a line on show is glowing.
    internal bool IsGlowing(Guid id) => selectionFrames.TryGetValue(id, out var parts) && TaskGlow.IsGlow(parts.Frame.Background);

    /// <summary>
    /// Brings the selected task's line into view as little as it takes, unlike ScrollToSelected's third of the way down:
    /// for a selection that comes while the panel is open (a Claude task's terminal brought to the front, task 4846fde1),
    /// a line already in view stays exactly where it is. In the Matrix the line's own cell scrolls. A line the filters
    /// or the search hide is not drawn, so nothing moves.
    /// </summary>
    internal void RevealSelected()
    {
        // The Inbox brings the line into view in the tab on show, never switching it (task 740a2231).
        if (InboxFollowsSelection(open: false)) return;
        if (selectedId.HasNoValue || !selectionFrames.TryGetValue(selectedId.Value, out var parts)) return;
        parts.Frame.BringIntoView();
    }

    // For the tests.
    internal bool IsSelected(Guid id) =>
        selectionFrames.TryGetValue(id, out var parts) && parts.Frame.BorderBrush is SolidColorBrush { Color.A: > 0 };

    internal double ScrollOffset => Scroller.VerticalOffset;

    // Quiet parts at half strength until hover or selection; an on flag toggle always at full strength; an off one
    // quieter still, and how quiet at rest depends on the line:
    //   - full and Matrix lines: invisible until hover or selection (as on the bar). The toggles sit beside the title
    //     there, and a column of dim glyphs down every line was noise;
    //   - slim lines: shown at rest as strongly as the other quiet buttons (OffFlagAtRestOnSlim), full under the pointer
    //     or on the picked line. A slim line's controls have a row of their own under the
    //     title (ControlsUnderTitle), and the toggles' 54 DIP are kept in it whether they show or not, so hiding them
    //     left a hole between the workspace chip and the comments with nothing to say three buttons were in it.
    //     Petre, on a screenshot of that hole (task f564a7cd): "lots of horizontal space for buttons, still hidden".
    // Not static: it reads slimLines, the form of the lines on show, which every caller's line belongs to.
    void Quieten(IEnumerable<UIElement> parts, bool loud) => parts.ToList().ForEach(part => part.Opacity =
        (part as FrameworkElement)?.Tag switch
        {
            FlagOnTag or RepeatMarkTag => 1,
            FlagOffTag => slimLines ? (loud ? 1 : OffFlagAtRestOnSlim) : loud ? 0.55 : 0,
            _ => loud ? 1 : 0.5,
        });

    // An off toggle on a slim line nobody points at: as light on the tile as the comment, Open Claude and "⋯" buttons
    // beside it, and full strength under the pointer, so hover still answers. The toggles are drawn in the dim ink
    // (A9ADB8) where those buttons use the bright one (E6E6E6) at 0.5, so 0.75 of the dim ink lands at about the same
    // lightness, roughly 4:1 against the tile. It was 0.3 (task f564a7cd), about 1.7:1, and Petre read the three as
    // missing: "snooze and others not showing?" (task ec731be5, option A of the mockup).
    const double OffFlagAtRestOnSlim = 0.75;

    static void Cell(Grid grid, int column, UIElement? part)
    {
        if (part is null) return;
        Grid.SetColumn(part, column);
        grid.Children.Add(part);
    }

    // The tick box of a note or a Claude task (Ruling 40, and the Done follow-up: all lines look alike), or Undo on a line
    // just closed. A Claude line has no badge here (Ruling 42, Petre: "don't say
    // claude"): the orange edge at the line's start is its mark.
    UIElement LeftOf(TaskRowView row, string? title = null) =>
        // A parent with open subtasks is refused by the board (task 842e1ba4); the tip says so before the click, and the
        // click's refusal shows on the problem line (TasksBoard.Close reports it).
        TickOf(row.Id, row.Status, row.Closing, TaskViews.TickTip(row.Claudes, row.SubtaskCount, row.ClaudeWorking, row.Unfinished), title, inThread: row.Nested);

    // A CLOSED SUBTASK MUST LOOK CLOSED IN ITS PARENT'S THREAD (task a78ce57a: "i can't really tell the
    // subtask is closed"). Inside a thread a closed subtask lost only its tick box and read almost like an open sibling.
    // It now has what a line in its undo window has (the title struck through, the rest of the line at 0.45), except
    // that its tick column shows a ticked box where the undo window shows Undo. Only a subtask drawn in a thread
    // (row.Nested): a top-level closed line, as under the Closed filter where every line is closed, stays as it was.
    // A line still in its undo window is not this: it has its Undo sign and its own look (row.Closing).
    static bool ClosedInThread(TaskRowView row) => row.Nested && row.Status == TaskStatus.Closed && !row.Closing;

    // The tick box itself, or Undo while the task is in its undo window, or nothing once it is closed: a line's (LeftOf)
    // and a subtask entry's in its parent's thread (SubtaskEntry, task 0eb943d8: "how do i close subtasks?").
    // `title`, on a subtask entry's tick, tells it from its parent line's for screen readers and the tests.
    // `inThread`: the tick is a subtask's inside its parent's thread, where a closed one shows the ticked box
    // (ClosedBox, task a78ce57a) instead of nothing.
    UIElement TickOf(Guid id, TaskStatus status, bool closing, string tip, string? title = null, bool inThread = false) =>
        closing
            // A sign, not a word (Petre: "undo sign instead of text"), the bar's own (FloatingBar.UndoSign).
            ? Small(FloatingBar.UndoSign(12, Ink), title is null ? "Undo" : $"Undo closing {title}", () => Actions?.Undo(id), "Undo", DoubleClickIgnored)
            : status == TaskStatus.Closed
                    ? inThread ? ClosedBox(title) : new TextBlock()
                    : Small(new Border
                    {
                        Width = 11,
                        Height = 11,
                        BorderBrush = TickEdge,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(3),
                    }, title is null ? "Close" : $"Close {title}", () => Actions?.Close(id), tip);

    // THE TICKED BOX of a closed subtask in its parent's thread (task a78ce57a): the open tick box's own size, edge and
    // corners (TickOf) with a check mark drawn inside, so the shape alone says "closed" (no colour
    // is the signal). Not a button: reopening stays in the line's menu. It says "Closed" to the pointer and, by name, to
    // the tests. The transparent fill makes its whole square hit-testable, so the tooltip shows over the inside as well.
    static UIElement ClosedBox(string? title)
    {
        var box = new Border
        {
            Width = 11,
            Height = 11,
            BorderBrush = TickEdge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Background = Brushes.Transparent,
            ToolTip = "Closed",
            // The Border's inside is 9 by 9 DIP: the check runs from the left, down to the foot, and up to the right.
            Child = new System.Windows.Shapes.Polyline
            {
                Points = [new Point(1.5, 4.5), new Point(3.5, 6.5), new Point(7.5, 2)],
                Stroke = Ink,
                StrokeThickness = 1.4,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            },
        };
        AutomationProperties.SetName(box, title is null ? "Closed" : $"Closed {title}");
        return box;
    }

    // The title (on a line being renamed, the box that renames it), and apart from it what goes under it: the description,
    // its pictures and the detail (the question or the status line). Line puts the two in rows of their own, so what is
    // under the title spans the line rather than the title's column (task d560f3eb, see Line).
    (UIElement Title, UIElement? Below) TextOf(TaskRowView row)
    {
        var panel = new StackPanel { Margin = new Thickness(2, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        var below = new StackPanel { Margin = new Thickness(2, 0, 4, 0) };
        // The caps around an open line's description and question (Capped), for the "Show all in window" link under them.
        var caps = new List<HeightCap>();
        // THE REFUSAL FIRST (task 41f08dba; Petre: "show error on the task, not at the top of the bar"): why the last action
        // on this task did not happen ("Close its open subtask first"), orange like the panel's problem line it replaces
        // for an action on a task, right under the title so it reads as this line's answer. It goes once the next action
        // works (TasksBoard.Report).
        if (row.Problem is { Length: > 0 } problem) below.Children.Add(ProblemOf(problem));
        if (renaming == row.Id)
        {
            renameBox = RenameBox(row);
            descriptionBox = DescriptionBox(row);
            panel.Children.Add(renameBox);
            panel.Children.Add(descriptionBox);
            panel.Children.Add(EditorRow());
        }
        else
        {
            // THE WHOLE TITLE (Ruling 38; Petre, of the Matrix's "…": "let tasks be multiline and editable"). It wraps in
            // its column instead of ending in an ellipsis, and keeps any line break it holds, so a long title makes its
            // line taller and every other column keeps its place. Select the line and press F2, or choose Edit in its
            // menu, to edit it in place (RenameBox).
            var title = new TextBlock
            {
                Tag = TitleTag,
                Text = row.Title,
                // RULING 59 (Petre: an important task bold, an urgent one italic): the flags show in the title itself, here and on
                // the bar, in the List and the Matrix alike (they share this line). Titles without a flag are plain, so the bold
                // and the slant mean something; the toggles stay as they were.
                FontWeight = row.Important ? FontWeights.Bold : FontWeights.Normal,
                FontStyle = row.Urgent ? FontStyles.Italic : FontStyles.Normal,
                FontSize = 12,
                Foreground = row.Claudes ? ClaudeInk : Ink,
                TextWrapping = TextWrapping.Wrap,
                // Struck through in the undo window and, since task a78ce57a, on a closed subtask in its parent's thread. A
                // decoration on the TextBlock covers the runs the subtask branch below puts in it (breadcrumb, title, count).
                TextDecorations = row.Closing || ClosedInThread(row) ? TextDecorations.Strikethrough : null,
            };
            // SUBTASKS (Petre's pick A): the parent's name in front of a subtask's title on a full line, "↳" alone on a slim
            // one, and a parent's open subtasks' count after its title, both in the caption grey so the title stays the
            // news. Runs in the title's own block, so they wrap with it. The bold and slant are the title's alone.
            if (row.ParentTitle is not null || row.SubtaskCount > 0)
            {
                title.Inlines.Clear();
                if (row.ParentTitle is not null)
                    title.Inlines.Add(new Run(TaskViews.Breadcrumb(row.ParentTitle, full: !slimLines && !compactLines && !row.Nested))
                        { Foreground = HeaderInk, FontWeight = FontWeights.Normal, FontStyle = FontStyles.Normal });
                title.Inlines.Add(new Run(row.Title));
                if (row.SubtaskCount > 0)
                    title.Inlines.Add(new Run(TaskViews.SubtaskCountSeparator + TaskViews.SubtaskCountText(row.SubtaskCount))
                        { Foreground = HeaderInk, FontWeight = FontWeights.Normal, FontStyle = FontStyles.Normal });
            }
            // A DONE TASK'S TICK in front of its title, the bar's own (TaskStatusMarks.DoneTick; Petre, 2026-11-23, "build
            // both"). Docked left of the title so the title still wraps in what is left; at the first line's height (top).
            // Not on a line in its undo window, which is closed.
            //
            // A SUBTASK'S TICK GOES AFTER ITS ARROW (task 9e85caee, Petre: "what's up with research being nested further
            // than the other tasks?"). Docked left, the tick sat in front of the "↳" the title starts with, so a done
            // subtask's arrow stood a tick's width right of its siblings' and read as one more level of nesting. On a
            // subtask the tick is an inline right after the breadcrumb run instead ("↳ ✓ title", or "↳ Parent › ✓ title"
            // on a full line), the way the bar draws it, so every sibling's arrow starts at the same x.
            //
            // HOLLOW UNTIL IT RUNS (task 8470d176): a Done task whose change is not in this Deskbar yet gets the ring, which
            // fills into the disc once the running build holds it (row.Delivery, TaskStatusMarks.DoneTick).
            // MERGED OR RUNNING BEFORE DONE (task 1afbd44f): a task waiting on a redeploy, or still in progress after its
            // merge, gets the same ring or disc (TaskStatusMarks.ShowsTick).
            var doneTick = TaskStatusMarks.ShowsTick(row.Status, row.Delivery) && !row.Closing;
            if (doneTick && row.ParentTitle is not null)
            {
                var tick = TaskStatusMarks.DoneTick(12, row.Delivery.Shipping, row.Delivery.Unchecked);
                tick.Margin = new Thickness(0, 0, 5, 0);
                // Inlines[0] is the breadcrumb run (the subtask branch above always adds it first), so the tick is second.
                title.Inlines.InsertAfter(title.Inlines.FirstInline, new InlineUIContainer(tick) { BaselineAlignment = BaselineAlignment.Center });
                panel.Children.Add(title);
            }
            else if (doneTick)
            {
                var tick = TaskStatusMarks.DoneTick(12, row.Delivery.Shipping, row.Delivery.Unchecked);
                tick.Margin = new Thickness(0, 2, 5, 0);
                tick.VerticalAlignment = VerticalAlignment.Top;
                var titleRow = new DockPanel();
                DockPanel.SetDock(tick, Dock.Left);
                titleRow.Children.Add(tick);
                titleRow.Children.Add(title);
                panel.Children.Add(titleRow);
            }
            else
                panel.Children.Add(title);
            // THE DESCRIPTION (Ruling 58): Petre's markdown under the title, a block of its own. Not hit-testable and not
            // focusable, so a click on it is a click on the line (a RichTextBox would swallow it).
            // IN FULL ONLY WHEN THE LINE IS OPEN (Petre, 2026-11-23, of a long brief that filled the window: "i want that
            // after i go to details"). A closed line shows the description's first line, plain and dim, cut to one line,
            // so a line with a description still says so and stays one line tall; a click on the line opens it, and then
            // the whole description shows, rendered, above the thread.
            // Images in tasks: an open line shows the description's text, then its pictures as a row of thumbnails
            // (TaskImageRow), and no block at all for a description that is only pictures.
            if (row.Description.Length > 0 && !row.Expanded) below.Children.Add(DescriptionPreview(row));
            if (row.Description.Length > 0 && row.Expanded)
            {
                var parts = TaskImages.Split(row.Description);
                if (parts.Text.Length > 0) below.Children.Add(Capped(DescriptionBlock(row), caps, DescriptionKey(row.Id)));
                if (TaskImageRow.Of(StateDir, parts.Images, OpenImage) is { } pictures) below.Children.Add(pictures);
            }
        }
        // THE DETAIL: the question while the task waits on Petre, else its status line. Cut to one line on a closed line,
        // wrapped on an open one, as the description is: a session's question was readable nowhere in full (Petre,
        // 2026-11-24, of a question cut at "Add an "O…" with its thread open: "where's the question?"). Tagged for tests.
        // THE QUESTION GOES TO THE CHAT WHILE IT IS OPEN (task 968c2216, Petre: "when asking a question, let that also be
        // present in the chat. when i open chat, hide the question from the main bar, show it when i collapse it"). The
        // ask writes the question into the thread too (TaskBoard.Ask), so an open line whose thread shows that comment
        // leaves the question off and keeps the rest of its detail ("no live session"); folding the line brings it back.
        var detail = DetailShown(row);
        if (detail.Length > 0) below.Children.Add(row.Expanded ? Capped(DetailText(row, detail), caps, DetailKey(row.Id)) : DetailText(row, detail));
        // "Show all in window" under what was cut (task e91f8aa5). Only while something is: a short question and
        // description show whole, as before, and need no link.
        if (caps.Count > 0 && Actions?.OpenWindow is { } openWindow) below.Children.Add(ShowAllLink(row.Id, caps, openWindow));
        return (panel, below.Children.Count > 0 ? below : null);
    }

    // AN OPEN LINE'S TEXT STOPS AT A FEW LINES (task e91f8aa5; Petre, of a waiting task whose nine-line Georgian question
    // filled the panel: "limit how much can fit here. expanding should open a detailview"). The open line showed its
    // question and its description in full since #466, because nothing else showed them; the task window (task dca4ebeb)
    // now does, so the line keeps to OpenLineShown lines of each and "Show all in window" opens the window for the rest.
    // Without a window to open (no Actions.OpenWindow) nothing is capped, or the rest could be read nowhere.
    //
    // THE CUT CAN BE DRAGGED (task 2088e1ed, TasksWindow.BodyGrip.cs): a grip under the cap moves it, from one line to the
    // whole text, and the height it is left at (bodyHeights, under `key`) is where the cap starts on every later redraw.
    // The grip shows while the cap cuts something, or once a height was dragged (so a text dragged whole can shrink again).
    UIElement Capped(FrameworkElement text, List<HeightCap> caps, string key)
    {
        if (Actions?.OpenWindow is null) return text;
        // Lines of the text's own size in the window's font (the text inherits it, but only once it is in the tree), so
        // the cut falls between two lines rather than through one. Both texts are 11 DIP today.
        var size = text is TextBlock block ? block.FontSize : 11.0;
        var line = Math.Ceiling(FontFamily.LineSpacing * size);
        // A TextBlock's lines are all one height (measured, Georgian and emoji included), so lines times that height is a
        // line's edge. The description's paragraphs are spaced, so its cut moves up to the top of the line it would split.
        var cap = new HeightCap
        {
            Cap = bodyHeights.TryGetValue(key, out var dragged) ? dragged : Math.Ceiling(OpenLineShown * FontFamily.LineSpacing * size),
            Child = text, Tag = CappedTag,
            EdgeOf = text is RichTextBox ? LineEdgeAbove : null,
        };
        caps.Add(cap);
        // Dragged to the text's foot, the cap is "whole" (infinite), so text that grows later still shows whole.
        double Fitted(double height) => height >= cap.FullHeight - 0.5 ? double.PositiveInfinity : height;
        var grip = BodyGrip(text is RichTextBox ? "Resize description" : "Resize question",
            shown: () => cap.ActualHeight, setting: () => cap.Cap, least: () => line, whole: () => cap.FullHeight,
            preview: height => cap.Cap = Fitted(height), keep: height => KeepCappedHeight(key, height, cap.FullHeight));
        // Collapsed until a layout pass says the cap cut something, as "Show all in window" is (the width, and so the
        // wrapping, is known only then). Never collapsed mid-drag, which would take the mouse from it.
        grip.Visibility = Visibility.Collapsed;
        grip.LayoutUpdated += (_, _) =>
        {
            var wanted = cap.Overflowing || bodyHeights.ContainsKey(key) || grip.IsMouseCaptured ? Visibility.Visible : Visibility.Collapsed;
            if (grip.Visibility != wanted) grip.Visibility = wanted;
        };
        return new StackPanel { Children = { cap, grip } };
    }

    // The top of the description's line that a cut at `cap` would go through, or the cap when it falls between lines (in
    // a paragraph's spacing, or past the text). The point is 1 DIP in from the left, where every line starts.
    static double LineEdgeAbove(UIElement child, double cap)
    {
        if (child is not RichTextBox box) return cap;
        var at = box.GetPositionFromPoint(new Point(1, cap), snapToText: true);
        if (at is null) return cap;
        var line = at.GetCharacterRect(LogicalDirection.Forward);
        return line.Top < cap - 0.5 && line.Bottom > cap + 0.5 ? Math.Floor(line.Top) : cap;
    }

    // How many lines of an open line's question, and of its description, show before "Show all in window".
    internal const int OpenLineShown = 4;

    // The cap around an open line's question or description, so a test can find it and ask whether it cut anything.
    internal const string CappedTag = "task-capped";

    // The link under a cut question or description: dim, small, like a comment's "Show more", and it opens the task's
    // window (TasksBoard.OpenWindow) rather than unfolding in place, since unfolding in place is what filled the panel.
    // Collapsed until a layout pass finds a cap that cut something: the width, and so the wrapping, is known only then.
    Button ShowAllLink(Guid id, IReadOnlyList<HeightCap> caps, Action<Guid> openWindow)
    {
        var link = Small(new TextBlock { Text = ShowAllText, Foreground = HeaderInk, FontSize = 10.5 }, ShowAllText,
            () => openWindow(id), "Read the whole task in a window of its own");
        link.Tag = ShowAllTag;
        link.HorizontalAlignment = HorizontalAlignment.Left;
        link.Margin = new Thickness(0, 1, 0, 0);
        link.Visibility = Visibility.Collapsed;
        link.LayoutUpdated += (_, _) =>
        {
            var wanted = caps.Any(cap => cap.Overflowing) ? Visibility.Visible : Visibility.Collapsed;
            if (link.Visibility != wanted) link.Visibility = wanted;
        };
        return link;
    }

    // What a click on a link in one task's text does (task 26f15d70): the controller's OpenLink, which goes to the task's
    // workspace before opening it. Null without a controller, so MarkdownBlocks opens it on the desktop in front.
    Action<Uri>? LinkOpener(Guid taskId) => Actions?.OpenLink is { } open ? uri => open(taskId, uri) : null;

    internal const string ShowAllText = "Show all in window";
    internal const string ShowAllTag = "task-show-all";

    // THE DETAIL'S TEXT, its links clickable (task 08cdb052, Petre: "when asking for action, prs should have links, or
    // artifacts; make that clickable"). A session asking for a merge names the pull request as a markdown link,
    // "Merge [Deskbar #490](https://github.com/...): CI is green", or writes the address bare, and both are drawn as a
    // link in the comments' link blue (MarkdownBlocks.InlinesOf), on a closed line as on an open one, since the closed
    // line is where the question is first seen. The rest of the inline markdown (bold, code) reads as it does in a
    // comment, and a line break stays a line break, as it was with the plain text.
    //
    // A press on a link is the link's: a Hyperlink takes the mouse on its press and marks it handled, so the line under
    // it (a Button) never presses and does not pick the task, and the click opens the address in the default browser.
    // A press anywhere else on the text still falls through to the line as before. Web addresses and local files become
    // links (the MarkdownBlocks header): a session's text never runs a program from a click, a file link included.
    // The detail a line shows (task 968c2216): without the question while the line is open and its thread shows the
    // question's comment, which can be folded behind "N earlier comments" and then is not on show, so the line keeps it.
    string DetailShown(TaskRowView row) =>
        row is { Expanded: true, DetailInThread: { } inThread } && ThreadComments(row).Shown.Any(c => c.AsksNow) ? inThread : row.Detail;

    TextBlock DetailText(TaskRowView row, string detail)
    {
        // Orange while it carries the question; what is left once the question moved to the chat is plain detail.
        var asks = row.Status == TaskStatus.WaitingOnYou && detail == row.Detail;
        var text = new TextBlock
        {
            Tag = DetailTag,
            FontSize = 11,
            Foreground = asks ? Orange : DimInk,
            FontWeight = asks ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = row.Expanded ? TextTrimming.None : TextTrimming.CharacterEllipsis,
            TextWrapping = row.Expanded ? TextWrapping.Wrap : TextWrapping.NoWrap,
        };
        // Plain text stays the Text it always was: a TextBlock filled with inlines reports an empty Text (measured: the
        // window tests reading a status line through it saw ""), so inlines are used only when there is markdown to draw.
        var inlines = MarkdownBlocks.InlinesOf(detail, LinkOpener(row.Id));
        if (inlines.All(inline => inline is Run { Tag: null } run && run.Text.Length > 0)) text.Text = detail;
        else inlines.ToList().ForEach(text.Inlines.Add);
        return text;
    }

    // THE DESCRIPTION UNDER A TITLE (Ruling 58): Petre's markdown, rendered as a comment's is (MarkdownBlocks), dim, a
    // block of its own under the title. A read-only box that takes no hits and no focus, so a click on it falls through to
    // the line (selecting it): a box that took hits would keep every press for its own text selection, and a click on
    // the description would stop picking the task. Its links still open on a click (task e117cb92): the line's press
    // asks DescriptionLinkAt whether the pointer is over one before it does anything else.
    RichTextBox DescriptionBlock(TaskRowView row)
    {
        // The text alone: its pictures are the row under it (TaskImageRow), never a path shown as text.
        var document = MarkdownBlocks.Render(TaskImages.Split(row.Description).Text, LinkOpener(row.Id));
        document.FontSize = 11;
        document.Foreground = DimInk;
        return new RichTextBox
        {
            Tag = DescriptionTag,
            Document = document,
            IsReadOnly = true,
            IsHitTestVisible = false,
            Focusable = false,
            Template = (ControlTemplate)FindResource("BareRichTextBox"),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = DimInk,
            Padding = new Thickness(0),
            // Directly under the title, no gap (Ruling 64).
            Margin = new Thickness(0),
        };
    }

    const string DescriptionTag = "task-description";

    // THE DESCRIPTION LINK UNDER THE POINTER (task e117cb92), or none. The description box takes no hits, so WPF's own hit
    // test never reaches its Hyperlinks; this does the hit test by hand. Every description box inside the line that is
    // showing, and where the point (pointIn gives it in an element's coordinates, the press's GetPosition in the window)
    // lies inside both the box and the part of it its HeightCap lets show (an open line cuts a long description at four
    // lines, and a press below the cut is on whatever is drawn there, not on the hidden text). Then each link in the box's
    // document is asked whether one of its characters covers the point (Covers). Internal for the tests, which pass a
    // point of their own: a raised MouseButtonEventArgs reads the real mouse's position.
    internal static Hyperlink? DescriptionLinkAt(DependencyObject line, Func<IInputElement, Point> pointIn) =>
        LogicalDescendants(line).OfType<RichTextBox>()
            .Where(box => Equals(box.Tag, DescriptionTag) && box.IsVisible)
            .Select(box => (Box: box, At: pointIn(box)))
            .Where(hit => Shows(hit.Box, hit.At))
            .SelectMany(hit => LogicalDescendants(hit.Box.Document).OfType<Hyperlink>().Where(link => Covers(link, hit.At)))
            .FirstOrDefault();

    // The line's content and everything under it, by the logical tree: a description box sits in a HeightCap (a Decorator)
    // in a StackPanel in the line's frame, and a FlowDocument's blocks and inlines are its logical children, so one walk
    // reaches both the boxes and their links.
    static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject node) =>
        LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>().SelectMany(child => new[] { child }.Concat(LogicalDescendants(child)));

    // Whether a point in the box's coordinates is on the part of the box that is drawn: inside the box, and inside the
    // HeightCap around it when the open line cut it (Capped puts the box straight in the cap, which clips at its bounds).
    static bool Shows(RichTextBox box, Point at) =>
        new Rect(box.RenderSize).Contains(at)
        && (box.Parent is not HeightCap cap || new Rect(cap.RenderSize).Contains(box.TranslatePoint(at, cap)));

    // Whether one of the link's characters covers the point. A character's box runs from the left edge of the position in
    // front of it to the right edge of the position after it, a line high; a link that wraps has characters on two lines,
    // each with its own box, so the gap at the end of its first line is not part of it. GetCharacterRect is in the box's
    // coordinates, the same ones the point is in (LineEdgeAbove relies on the same).
    static bool Covers(Hyperlink link, Point at)
    {
        var positions = Positions(link).ToList();
        return positions.Zip(positions.Skip(1), CharacterBox).Any(box => box.Contains(at));
    }

    // Every insertion position from the link's start to its end, which are the edges of its characters.
    static IEnumerable<TextPointer> Positions(TextElement element)
    {
        for (var at = element.ContentStart; at is not null && at.CompareTo(element.ContentEnd) < 0; at = at.GetNextInsertionPosition(LogicalDirection.Forward))
            yield return at;
        yield return element.ContentEnd;
    }

    // The box of the character between two neighbouring positions. Empty when the two edges are on different lines, which
    // only a position at a wrap can give; the character is then measured from the other side by its neighbours.
    static Rect CharacterBox(TextPointer from, TextPointer to)
    {
        var left = from.GetCharacterRect(LogicalDirection.Forward);
        var right = to.GetCharacterRect(LogicalDirection.Backward);
        return left.IsEmpty || right.IsEmpty || Math.Abs(left.Top - right.Top) > 0.5
            ? Rect.Empty
            : new Rect(new Point(Math.Min(left.Left, right.Left), left.Top), new Point(Math.Max(left.Left, right.Left), Math.Max(left.Bottom, right.Bottom)));
    }

    // The line's detail (question or status line), so a test can find it and read how it is cut.
    internal const string DetailTag = "task-detail";

    // A line's refusal (TaskRowView.Problem), so a test can find it on the line.
    internal const string ProblemTag = "task-problem";

    // The refusal under a line's title (task 41f08dba): the panel's problem line's orange (#FFA726, ProblemText in the
    // XAML) at the line's detail size, wrapped, never cut, since it is short and is the one thing to read on the line.
    static TextBlock ProblemOf(string problem) => new()
    {
        Tag = ProblemTag,
        Text = problem,
        FontSize = 11,
        Foreground = ProblemInk,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 1, 0, 1),
    };

    static readonly Brush ProblemInk = Frozen(0xFF, 0xFF, 0xA7, 0x26);

    // THE DESCRIPTION'S PREVIEW on a closed line: its first line without markdown markers (TaskViews.PlainFirstLine, the
    // same text the bar shows for it), dim like the description, one line ending in an ellipsis. A TextBlock takes no
    // focus and passes its clicks to the line, so a click on it opens the line like a click anywhere else on it.
    TextBlock DescriptionPreview(TaskRowView row) => new()
    {
        Tag = DescriptionPreviewTag,
        Text = TaskViews.PlainFirstLine(row.Description),
        FontSize = 11,
        Foreground = DimInk,
        TextWrapping = TextWrapping.NoWrap,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    const string DescriptionPreviewTag = "task-description-preview";


    // The assignee column (Ruling 57): "Me" or "Claude" (a colleague by name, later), dim like the age. Shown on every line,
    // closed ones too: it says who had the task. Changed only in the "Assign to" menu.
    UIElement AssigneeOf(TaskRowView row) => new TextBlock
    {
        Tag = AssigneeTag,
        Text = row.Assignee.Name,
        FontSize = 10,
        Foreground = row.Assignee.IsClaude ? ClaudeInk : DimInk,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
        ToolTip = $"Assigned to {row.Assignee.Name}",
    };

    const string AssigneeTag = "task-assignee";

    // The sessions, a square each (blue while live, haloed while Claude works in it; SessionSquare), then the words:
    // "2 sessions, 1 live". Empty for a note. A DockPanel rather than a horizontal StackPanel (task 3df2d28f): the column
    // is now as narrow as Petre drags it, and a StackPanel gives its text all the width it wants, so the words were cut
    // mid-letter by the column's edge; docked after the squares, they get what is left and end in an ellipsis, whole in
    // their tooltip.
    UIElement? SessionsOf(TaskRowView row)
    {
        if (row.Sessions.Length == 0) return null;
        var panel = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };
        Enumerable.Range(0, Math.Min(SessionSquares, row.SessionCount)).ToList().ForEach(i =>
        {
            var square = SessionSquare(10, MarkOf(row, i));
            DockPanel.SetDock(square, Dock.Left);
            panel.Children.Add(square);
        });
        panel.Children.Add(new TextBlock
        {
            Text = row.Sessions, FontSize = 10, Foreground = DimInk, Margin = new Thickness(2, 0, 4, 0),
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = row.Sessions,
        });
        return panel;
    }

    /// <summary>The automation name of a line's workspace chip while it moves the task, for the tests.</summary>
    internal const string PlaceChipName = "Move to workspace";

    // A LINE'S WORKSPACE CHIP MOVES THE TASK (task 3757a3e1, Petre: "move it to another workspace should be offered by
    // clicking the workspace chip"). The bar's line chip has done this since it was drawn (FloatingBar.WorkspaceChipButton);
    // the panel's was a plain label, so the move lived only on the line's Move to button and in "⋯", away from the
    // workspace it changes. Now the chip is a button with no face of its own around the chip (transparent, no padding, no
    // edge), so it looks exactly as before and dims a little under the pointer like every flat button; a click opens the
    // same compact workspace menu as the Move to button (OpenMoveMenu), anchored under the chip, the task's own workspace
    // outlined. A line in its undo window keeps the plain chip, as its other buttons go (`live` in LineButtons). The click
    // guard (ClickIgnored) and the handled click come from Flat, so the click never also lands on the line.
    UIElement PlaceChip(TaskRowView row)
    {
        var chip = Chip(row.Place);
        if (row.Closing) return chip;
        // The chip's gap to the next column moves to the button, so the hover area is the chip and nothing more.
        chip.Margin = new Thickness(0);
        // WPF shows the tooltip of the innermost element under the pointer, and the chip covers the whole button, so the
        // chip's own tip (the bare name) would hide the button's. The button's tip carries the full name too.
        chip.ToolTip = null;
        Button? button = null;
        button = Flat(chip, PlaceChipName, () => OpenMoveMenu(row.Id, row.Place.Workspace, button), new Thickness(0), ClickIgnored);
        button.Background = Brushes.Transparent;
        button.BorderThickness = new Thickness(0);
        button.HorizontalAlignment = HorizontalAlignment.Left;
        button.Margin = new Thickness(0, 0, 6, 0);
        button.ToolTip = $"{row.Place.Name}: move it to another workspace";
        return button;
    }

    // A workspace chip in its row's colour, faded so the name reads on it, or a grey "none".
    static Border Chip(TaskPlaceChoice place) => new()
    {
        Background = place.Workspace.HasValue ? Faded(place.Colour) : NoneFill,
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(7, 1, 7, 1),
        Margin = new Thickness(0, 0, 6, 0),
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = place.Name,
            FontSize = 10,
            Foreground = place.Workspace.HasValue ? Ink : NoneInk,
            TextTrimming = TextTrimming.CharacterEllipsis,
        },
        // The whole name, for a chip its column has trimmed (task 3df2d28f: the column is as narrow as Petre drags it).
        ToolTip = place.Name,
    };

    // A comment glyph, with the count beside it when there are comments (Petre asked what the old "▸" arrow meant). A
    // line with no comments shows the glyph alone, so the first comment has a way in. Tooltip "Comments", the same
    // whether the thread is open or not.
    Button CommentsButton(TaskRowView row)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = "", FontFamily = IconFont, FontSize = 12, Foreground = Ink, VerticalAlignment = VerticalAlignment.Center });
        if (row.CommentCount > 0)
            content.Children.Add(new TextBlock { Text = $"{row.CommentCount}", FontSize = 11, Foreground = Ink, Margin = new Thickness(3, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return Small(content, "Comments", () => Actions?.ToggleThread(row.Id), "Comments");
    }

    // Reopen on a closed task. A done Claude task has no close button any more (Ruling 52): its tick box closes it, as on a note.
    UIElement? ActionOf(TaskRowView row) =>
        row.Closing ? null
        : row.Status == TaskStatus.Closed ? Small("Reopen", "Reopen", () => Actions?.Reopen(row.Id), "Bring the task back")
        : null;

    // --- the thread --------------------------------------------------------------------------------------------------

    // Whether a thread is drawn as its task's page (task 5ee182cf), in the panel or a task window. The view being drawn must
    // be the Inbox's too, not only inboxPage: a switch from a page to the List keys the click guard (DrawRows' placesKey,
    // from ThreadLayoutKey) before PaintInboxChrome clears inboxPage, and with the page's rule there the real move from page
    // to List started no guard while the next unrelated redraw did, eating a click (review of this change). `latest` is
    // already the view being drawn when Draw runs, and a task window's page view has its Inbox set.
    bool OnItsPage(Guid id) => inboxPage == id && latest?.Inbox is not null;

    // The comments a thread draws (settled ones folded away, then only the latest three unless the earlier ones are
    // unfolded) and how many were left out as earlier. The one place the rule lives: Thread draws from it and the click
    // guard's key (ThreadLayoutKey) reads it.
    //
    // A TASK'S PAGE KEEPS ITS WHOLE CHAT (task 5ee182cf; Petre, of a page whose chat had shrunk to its last few bubbles
    // under an empty page: "chat shouldn't collapse on me in the inbox task view", "i mean old chat messages hiding,
    // settling"). The page is a chat read top to bottom with a scroller of its own, so neither fold belongs there: a
    // session's `task answered` settles every comment it wrote since it asked, and those bubbles vanished in front of him;
    // past three comments the older ones folded, and the session's steps and messages older than the oldest comment shown
    // folded with them (ClaudeChatStream.Build's earlierCommentsFolded), so one more comment could take most of the chat
    // away. So on its page (in the panel or in its own window, both set inboxPage) a task's thread shows every comment in
    // its place, a settled one dimmed as history (CommentOf), and reports none as earlier. The List's thread, and a subtask's
    // thread drawn inside its parent's page, keep both folds: a thread under a line has no page of room to spend.
    (IReadOnlyList<TaskCommentView> Shown, int Earlier) ThreadComments(TaskRowView row)
    {
        if (OnItsPage(row.Id)) return (row.Comments, 0);
        var candidates = row.Comments.Where(c => !c.Settled || settledOpen.Contains(row.Id)).ToList();
        var earlier = Math.Max(0, candidates.Count - LatestShown);
        return (earlier > 0 && !earlierOpen.Contains(row.Id) ? candidates.Skip(earlier).ToList() : candidates, earlier);
    }

    // What an open thread's layout depends on, as text: its session lines (Task 9: a session joining adds a line above the
    // comments and moves every one below), the comments it shows and which of them are opened in full.
    string ThreadLayoutKey(TaskRowView row) =>
        !row.Expanded ? ""
        : string.Join(',', row.SessionList.Select(s => s.Id)) + "/"
          // Whether there is a session to open decides which Claude button is in the row at the thread's top (task
          // 5e02e99f: Open Claude, or "Start Claude on this" once the session has expired). The two differ in width, so
          // the swap moves "Add subtask" beside them and must start the guard, or a click meant for it lands elsewhere. (On a
          // page the button is beside the docked box, task 625405cc, so there the guard is only a harmless extra.)
          + (row.OpensSession ? "opens" : "") + "/"
          // A subtask open in place (task 0eb943d8) puts its whole thread inside this one.
          + string.Join(',', row.Subtasks.Select(c => $"{c.Id}" + (c.Row is { } open ? "[" + ThreadLayoutKey(open) + "]" : ""))) + "/"
          // Task links: the link lines and an open picker sit above the comments, so each moves them.
          + string.Join(',', row.Links.Select(l => $"{l.Kind}{l.Id}")) + (linking is { } l && l.Task == row.Id ? $"picker{l.Duplicate}" : "")
          // The subtask box (task 0eb943d8) opens under the row of buttons, and Add subtask leaves the row meanwhile.
          + (subtaskUnder == row.Id ? "subtask-box" : "") + "/"
          // Not a height a comment was dragged to (task 2088e1ed): the lines moved under the pointer while it dragged, so
          // nothing slides under it at the release, and the click guard would only eat the next click.
          + string.Join(',', ThreadComments(row).Shown.Select(c => $"{c.Id}{(longOpen.Contains(c.Id) ? "+" : "")}"))
          + $"{settledOpen.Contains(row.Id)}{earlierOpen.Contains(row.Id)}";

    // The row of buttons (ThreadActions, task 1c17b7d6: Open in window, Open Claude or "Start Claude on this", Add subtask),
    // the task's sessions with their Open buttons (Task 9), the subtasks and links, then the comments as a chat (task
    // 39bc9c4d, ChatThread), then a box for Petre's next one. RULING 62: the settled comments are folded away
    // behind one dim line above the comments, "N settled comments", which unfolds them in place (and reads "Hide N
    // settled comments" while they show). They are kept, so the history stays.
    //
    // IN PLACE (task 0eb943d8, Petre: "let subtasks stay where they are, expand their current place"): a subtask listed in
    // its parent's open thread opens there, its own thread drawn right under its entry (`inParent`), indented in a frame
    // of its own. That thread leaves out "Subtask of" (the parent is the thread around it). Since task 249a1e8a the entry
    // is a full line and its thread keeps the whole row of buttons, Add subtask included, which adds a sibling.
    //
    // CAPPED UNDER ITS LINE (task ee9bd3fc, TasksWindow.ThreadCap.cs): a top-level thread is given the line it is drawn
    // under (`under`), and is then no taller than the list's view less that line, its head and comment box fixed and
    // the rest scrolling between them. A subtask's thread in place has no `under` and stays one plain stack.
    UIElement Thread(TaskRowView row, bool inParent = false, FrameworkElement? under = null)
    {
        // The head: what stays at the top of a capped thread (the parent entry, the row of buttons, the subtask box).
        var head = new StackPanel();
        // The body: what scrolls in a capped thread (the session lines, subtasks, links, the folds and the comments).
        var panel = new StackPanel();
        // A SUBTASK'S PARENT FIRST (task 54b39c4c; Petre: "subtask should show its parent and have a way to open the parent
        // easily"). A nested line shows only "↳", trusting the parent to be right above, but with a few siblings and an
        // open thread the parent is off the top of the list. So an open subtask names its parent at the head of its
        // thread, and the name is a button that opens the parent (TasksBoard.OpenParent).
        if (!inParent && row.ParentId is { } parentId && row.ParentTitle is { } parentTitle && Actions?.OpenParent is { } openParent)
            head.Children.Add(ParentEntry(parentId, parentTitle, openParent));
        // A SUBTASK OPENED IN PLACE SHOWS ITS DESCRIPTION (task b2ff3fb7; Petre, of a subtask whose two screenshots were
        // nowhere to be seen once it sat under another task: "subtask may have lost its image after being moved into
        // another task"). A task's description and its pictures are drawn by its LINE (TextOf), under the title. This
        // thread drew them itself while a subtask in its parent's thread had only a bare entry; since task 249a1e8a that
        // subtask has a full line right above its thread (Line with inThreadOf), which draws them, so nothing here does.
        //
        // THE THREAD'S ACTIONS ARE ONE ROW OF BUTTONS AT ITS TOP (task 1c17b7d6; Petre, on a screenshot of an open thread
        // that stacked "Open in window", the session line's "Open" and "Add subtask" on three lines: "open a window, open
        // claude and add subtasks to be buttons at the top"). ThreadActions builds the row; the subtask box, when "Add
        // subtask" was pressed, opens right under it, so it still appears where Petre clicked (task 0eb943d8).
        // A subtask's thread in its parent's has the row in full too (task 249a1e8a, "all buttons like the parents do"):
        // its Add subtask adds a sibling, as its line's menu always has, since subtasks are one level deep.
        var addsSubtask = Actions?.SubtaskParent is not null && row.Status != TaskStatus.Closed && !row.Closing;
        var (actions, hasOpenClaude) = ThreadActions(row, addsSubtask);
        if (actions.Children.Count > 0) head.Children.Add(actions);
        // The buttons the line right above already shows leave the row (task 59b8f613, FollowLine). A subtask's thread in
        // its parent's has its own line right above it as well now (task 249a1e8a), so it follows that line too.
        if (lineButtons.TryGetValue(row.Id, out var lineStrip)) FollowLine(actions, row, lineStrip);
        if (addsSubtask && subtaskUnder == row.Id) head.Children.Add(SubtaskBox(row.Id));
        // The session lines stay as the record of who worked on the task. With Open Claude in the row above and the task
        // having ONE session in all, that line's own Open would be the same click twice, so it is left off. With two or
        // more, each line keeps its Open, since Open Claude goes to one of them only (TaskViews.SessionToGoTo: live first,
        // then the newest), and that one may be an expired session with nothing to open (review of this change).
        var onlySession = hasOpenClaude && row.SessionList.Count == 1;
        row.SessionList.ToList().ForEach(session => panel.Children.Add(SessionLine(row.Id, session, withOpen: !onlySession)));
        // SUBTASKS IN THE THREAD (follow-up: Petre's screenshot of an open parent that said "2 subtasks" and showed none).
        // Each is a full line (task 249a1e8a, Line with inThreadOf) whose click opens or folds that subtask's own thread
        // right under it, in place (task 0eb943d8). One with a line of its own elsewhere in the view keeps the short
        // entry, which goes to that line: drawing the line twice would draw its thread twice too.
        // Gathered in a stack of their own, which a capped thread puts in its head (below), the rest in the body as before.
        var subtasks = new StackPanel();
        row.Subtasks.ToList().ForEach(sub =>
        {
            if (sub is { HasLine: false, Row: { } subRow })
            {
                closing[subRow.Id] = subRow.Closing;
                subtasks.Children.Add(Line(subRow, inThreadOf: row.Id));
                if (subRow.Expanded) subtasks.Children.Add(Thread(subRow, inParent: true));
            }
            else subtasks.Children.Add(SubtaskEntry(sub, row.Id));
        });
        if (subtasks.Children.Count > 0 && under is null) panel.Children.Add(subtasks);
        // TASK LINKS (docs/superpowers/specs/2026-11-25-task-links-design.md): what the task duplicates, its duplicates and
        // its related tasks, each a button that shows that task, then the link picker while "Duplicate of…" or "Related
        // to…" has it open on this task.
        row.Links.ToList().ForEach(link => panel.Children.Add(LinkEntry(row.Id, link)));
        if (row.Links.Count > 0) ((FrameworkElement)panel.Children[^1]).Margin = new Thickness(0, 0, 0, 8);
        if (linking is { } picking && picking.Task == row.Id) panel.Children.Add(LinkPicker(row.Id, picking.Duplicate));
        var settledCount = row.Comments.Count(c => c.Settled);
        var unfolded = settledOpen.Contains(row.Id);
        var (shown, earlier) = ThreadComments(row);
        var open = earlierOpen.Contains(row.Id);
        // RULING 63: more than three comments show only the latest three, under a dim "N earlier comments" line that
        // unfolds the rest (settled comments, folded above, do not count toward the three).
        // The two folds sit side by side, centred over the conversation, as a chat shows "earlier messages" (task 39bc9c4d).
        var folds = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        // Not on the task's page, where the settled comments stay in their places (ThreadComments, task 5ee182cf).
        if (settledCount > 0 && !OnItsPage(row.Id)) folds.Children.Add(SettledFold(row.Id, settledCount, unfolded));
        if (earlier > 0) folds.Children.Add(EarlierFold(row.Id, earlier, open));
        if (folds.Children.Count > 0) panel.Children.Add(folds);
        // The comments as a chat (task 39bc9c4d, ChatThread): bubbles on two sides, Deskbar's lines as events, a day line
        // above each day's first comment. It replaced the hairline between comments (task aa42794f).
        ChatStreamFor(row, shown, earlier > 0 && !open).ToList().ForEach(comment => panel.Children.Add(comment));
        var box = CommentBox(row.Id);
        // On a task's page the box is kept aside for DrawInboxPage to dock (TasksWindow.DockedBox.cs); elsewhere it stays in the thread.
        var docked = KeepBoxForDock(box, row.Id);
        authors[row.Id] = shown.Select(c => c.Author).ToArray();
        shownComments[row.Id] = shown.Select(c => c.Id).ToArray();
        var frame = new Border
        {
            // So a test can tell a thread's frame from the other Borders around it (task ee9bd3fc).
            Tag = ThreadTag,
            Background = ThreadFill,
            BorderBrush = ThreadEdge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8),
            // 3 DIP above (TileGap), so the thread stands apart from its task's tile as the tiles do from each other. A
            // subtask's thread in place sits under its entry, indented a little inside the parent's frame.
            //
            // THE SAME SIDES AS THE TILES (task dee002f7, Petre: "task details are indented needlessly", on a screenshot of
            // a narrow panel). The thread used to start 43 DIP in, under the title rather than the tile, and end 10 DIP
            // short of the right side. On a narrow panel that cost a fifth of the width, and the row of buttons and the
            // session line ran off the right edge. The frame and its fill already say which task it belongs to, so it now
            // takes the tile's own inset (TileInset) on both sides and lines up with the tile above it.
            Margin = inParent ? new Thickness(12, 0, 0, 8) : new Thickness(TileInset, TileGap, TileInset, 6),
        };
        // Capped under its line (TasksWindow.ThreadCap.cs), or the head, the body and the box in one plain stack, as a
        // thread always was before the cap.
        // A CAPPED THREAD KEEPS ITS SUBTASKS IN SIGHT (task 452487b4). Its body scrolls and opens at the latest comment,
        // so subtasks drawn in the body sat above that, out of sight in a long conversation: Petre's parent with ten
        // comments refused its tick with "Close its open subtask first" and he asked "where is it?". So in a capped
        // thread they sit in the head, under the row of buttons, in a shelf of their own no taller than half the thread
        // (SubtaskShelf), so a subtask opened in place there cannot crowd out the comments and the box.
        if (subtasks.Children.Count > 0 && under is not null) head.Children.Add(SubtaskShelf(subtasks, frame));
        frame.Child = under is not null
            ? CappedThread(row.Id, frame, under, head, panel, box, shown.Select(c => (Guid?)c.Id).LastOrDefault())
            : docked ? new StackPanel { Children = { head, panel } } : new StackPanel { Children = { head, panel, box } };
        return frame;
    }

    // The row of buttons at the top of an open thread (task 1c17b7d6), left to right, each only where it applies:
    //   Open in window        the task in a window of its own (task dca4ebeb): the thread is where a long task stops fitting;
    //   Open Claude           a Claude task's session, the line's own Open Claude button and its double-click (Actions.Go,
    //                         task 7f61af2e), here so the thread has it too; tagged so the session line can leave its Open
    //                         off. Only while there is a session it can open (TaskRowView.OpensSession, the line's own
    //                         rule since task 6a57f59b): running, or ended, which it resumes. On a task's page it (or
    //                         Start Claude, its other face) sits beside the docked comment box instead (task 625405cc);
    //   Start Claude on this  only where Open Claude is not: a task no session has joined, or one whose session has
    //                         expired, so nothing is left to open;
    //   Reevaluate            beside Open Claude (task 3dd07539, "move it as a button at the top"): the session checks
    //                         where the task stands and fixes its question, status and status line. Its twin is the
    //                         line's icon beside Open Claude, so like Open Claude it shows here only when the line can't;
    //   Add subtask          the twin of the menu's "Add subtask…" (task 0eb943d8). While its box is open (Thread puts the
    //                         box right under this row) the button is left out, so the box is the one place to type.
    // ONE CLAUDE BUTTON, NEVER BOTH (task 5e02e99f; Petre, on a screenshot of a done task's thread whose session had ended
    // and whose row read Open in window, Open Claude, Start Claude on this, Add subtask: "open claude and start claude on
    // this, seems excessive"). Until then Start Claude came back beside Open Claude as soon as no session was running
    // (task 9b6275bc), for a fresh session. But Open Claude resumes the ended session, which is what is wanted from a
    // task Claude already worked on, so the second button was a second way to the same place. This is the rule the line
    // menus already follow (task b6f068b1). A fresh session beside an ended one still comes from the task's own window
    // (Open in window, its Sessions column) and from `deskbar task start`.
    // A WrapPanel, so on a narrow panel the buttons wrap onto a second line instead of clipping. Tagged for the tests.
    //
    // ON AN INBOX PAGE THEY ARE ICON BUTTONS (task 57191c84; Petre, on a screenshot of a page whose row read "Open in
    // window  Open Claude  Reevaluate  Add subtask" in words: "these should be buttons, as with the list"). The Inbox
    // list's line has these same actions as square icon buttons (LineButtonsOf: GlyphButton), and the page showed them as
    // the List look's flat words, which read as links. So while a page's thread is built (threadOnPage, set by PageBody)
    // each button is the list's own GlyphButton with the list's own picture, the words kept as its name and in its
    // tooltip. Add subtask, which the Inbox line does not have, takes the plus. Under a List line the row is words, as
    // before.
    (WrapPanel Row, bool HasOpenClaude) ThreadActions(TaskRowView row, bool addsSubtask)
    {
        var icons = threadOnPage;
        // One button of the row: the Inbox's icon button on a page, the List's words elsewhere. The name is the same in
        // both, since FollowLine and the tests find a button by it. An icon's tooltip starts with the words it replaced
        // (`tipSaysIt`: Reevaluate's tip already does).
        Button Make(string icon, string words, Action press, string tip, bool tipSaysIt = false) =>
            icons
                ? GlyphButton(icon, words, press, tipSaysIt ? tip : $"{words}: {tip}", size: 26, ignoredWhen: ClickIgnored)
                : Small(words, words, press, tip);
        var buttons = new List<Button>();
        if (Actions?.OpenWindow is { } openWindow)
            buttons.Add(Make("open", OpenInWindowText, () => openWindow(row.Id),
                "Read and edit the whole task in a window of its own"));
        var openClaude = Actions is not null && OpensClaude(row);
        // The one Claude button (ClaudeAction), here unless the task's page docks it beside the comment box at the bottom
        // (task 625405cc, DockBottom). It still counts as the thread's Open Claude there (the returned HasOpenClaude), so
        // the one session line keeps leaving its own Open off: the same click is a button away at the bottom.
        if (!BoxDocks(row.Id) && ClaudeAction(row) is { } claude)
            buttons.Add(Make(claude.Icon, claude.Words, claude.Press, claude.Tip));
        // Reevaluate (task 3dd07539), right after the Claude button, its neighbour: the task's session checks where the
        // task stands and fixes it. Only beside Open Claude, where there is a session to reach (an expired one could not
        // take the request). The line has it as an icon beside Open Claude, so FollowLine hides this one as it hides Open
        // Claude; it shows under a Matrix line (no action column) and while the line is being edited. On a page it stays in
        // this row while the Claude button has moved down beside the comment box (task 625405cc).
        if (openClaude && Reevaluates(row))
            buttons.Add(Make("refresh", ReevaluateText, () => Actions?.Reevaluate?.Invoke(row.Id), ReevaluateTip, tipSaysIt: true));
        if (addsSubtask && subtaskUnder != row.Id)
            buttons.Add(Make("plus", ThreadAddSubtaskText, () => BeginSubtaskAdd(row.Id), "Add subtask under this task", tipSaysIt: true));
        var actions = new WrapPanel { Tag = ThreadActionsTag, Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        buttons.ForEach(button =>
        {
            // Icon buttons are 26 DIP squares with their own air around the picture, so they stand closer than words do.
            button.Margin = new Thickness(0, 0, icons ? 2 : 6, 4);
            actions.Children.Add(button);
        });
        return (actions, openClaude);
    }

    // A Claude button's picture, words (its automation name, which the tests and FollowLine find it by), press and tooltip.
    sealed record ClaudeChoice(string Icon, string Words, Action Press, string Tip);

    // THE THREAD'S ONE CLAUDE BUTTON (task 5e02e99f, never both): Open Claude while the task has a session it can open
    // (OpensClaude: running, or ended, which it resumes), else "Start Claude on this" on a task that is still open, else
    // none. A running session always has Open Claude, so Start never shows beside one. ThreadActions puts it in the row at
    // the thread's top; on a task's page DockBottom puts it beside the docked comment box instead (task 625405cc).
    ClaudeChoice? ClaudeAction(TaskRowView row) =>
        Actions is null ? null
        : OpensClaude(row) ? new ClaudeChoice("terminal", OpenClaudeText, () => Actions?.Go(row.Id),
            "Go to this task's Claude session, resuming it first if it has ended")
        : Actions.StartClaude is { } start && row.Status != TaskStatus.Closed && !row.Closing
            ? new ClaudeChoice("play", StartClaudeText, () => start(row.Id),
                "Start a background Claude session on this task, with the task as its first message")
        : null;

    // Whether the thread being built is an Inbox page's (PageBody sets it around its Thread call): its row of buttons is
    // then icon buttons (ThreadActions). A subtask's thread drawn in place inside it takes the same look.
    bool threadOnPage;

    /// <summary>Tagged for tests: the row of buttons at the top of an open thread (task 1c17b7d6).</summary>
    internal const string ThreadActionsTag = "thread-actions";

    // The thread row's Add subtask, its words and its name (the line's button is the menu's "Add subtask…").
    const string ThreadAddSubtaskText = "Add subtask";

    // "N settled comments" (Ruling 62), dim, a button of its own: unfolds the thread's settled comments, then folds them again.
    UIElement SettledFold(Guid task, int count, bool unfolded)
    {
        var words = count == 1 ? "1 settled comment" : $"{count} settled comments";
        var fold = Small(new TextBlock { Text = unfolded ? $"Hide {words}" : words, Foreground = HeaderInk, FontSize = 10.5 },
            "Settled comments", () => ToggleSettled(task), "Settled comments are kept, folded away");
        return AsFoldPill(fold);
    }

    // How many comments a thread shows before the rest fold behind "N earlier comments" (Ruling 63).
    const int LatestShown = 3;

    // "3 earlier comments", dim, a button: unfolds the older comments, then folds them again.
    UIElement EarlierFold(Guid task, int count, bool unfolded)
    {
        var words = count == 1 ? "1 earlier comment" : $"{count} earlier comments";
        var fold = Small(new TextBlock { Text = unfolded ? $"Hide {words}" : words, Foreground = HeaderInk, FontSize = 10.5 },
            "Earlier comments", () =>
            {
                if (!earlierOpen.Remove(task)) earlierOpen.Add(task);
                if (latest is not null) Draw(latest);
            }, "Only the latest three comments show");
        return AsFoldPill(fold);
    }

    // A fold as a small filled pill, centred over the conversation beside its twin (task 39bc9c4d).
    static Button AsFoldPill(Button fold)
    {
        fold.Background = TileFill;
        fold.Padding = new Thickness(9, 1, 9, 2);
        fold.Margin = new Thickness(3, 0, 3, 6);
        return fold;
    }

    // A comment shows WHOLE unless it is super long (task f0168cdd; Petre: "give comments more space, two times more, if
    // they need it. actually show the entire comment, don't collapse it, unless it's super long"). Ruling 63 folded every
    // comment past about three lines behind "Show more"; now only one past SuperLongLines folds, and folded it shows
    // twice the old three lines (FoldedLines). A line is counted by its text, a hundred characters to a line: the width
    // is not known when the thread is built, and a test needs the same answer. The laid-out height decides once there is
    // one (CommentOf's LayoutUpdated).
    // BLANK LINES DO NOT COUNT (review of task f0168cdd): a session writes a blank line between paragraphs, and counting
    // them made ten short paragraphs "super long" by the count but not once laid out. Such a comment was built folded and
    // opened a pass later, growing by a hundred DIP or more after the panel had arranged its scroll, which could pull the
    // scroll position up. Erring low is the safe side: a comment built whole that turns out super long only shrinks.
    internal static bool IsLong(string markdown) =>
        markdown.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Trim().Length > 0)
            .Sum(line => (int)Math.Ceiling(line.Length / 100.0)) > SuperLongLines;

    // One line of 11.5-point text, with a little to spare for a paragraph's spacing (Ruling 63 had 54 DIP for three). It
    // is also the least of a comment its grip can leave showing (task 2088e1ed).
    const double OneCommentLine = 18;

    // How many lines a comment may run to and still show whole (task f0168cdd). Past this it is "super long": a thread
    // with one of those would be that comment alone on most of a panel's height, so it folds.
    const int SuperLongLines = 15;

    // What a super long comment shows folded: six lines, twice Ruling 63's three ("two times more", task f0168cdd). A
    // preview rather than the fifteen it could have shown whole, so a long report still leaves the thread under it in
    // sight; "Show more" or the grip opens the rest.
    const double FoldedLines = 6 * OneCommentLine;

    // The laid-out height past which a comment counts as super long.
    const double SuperLongHeight = SuperLongLines * OneCommentLine;


    void ToggleSettled(Guid task)
    {
        if (!settledOpen.Remove(task)) settledOpen.Add(task);
        if (latest is not null) Draw(latest);
    }

    /// <summary>The words of the menu item and the thread's button (Task 9).</summary>
    internal const string StartClaudeText = "Start Claude on this";

    /// <summary>The menus' and the thread's "Open in window" (task dca4ebeb), the panel's and the bar's alike.</summary>
    internal const string OpenInWindowText = "Open in window";

    /// <summary>The menus' "Mark deployed" (task 8470d176, phase 2), the panel's and the bar's alike.</summary>
    internal const string MarkDeployedText = "Mark deployed";
    /// <summary>
    /// A Claude task's menu item, on the bar and in this window, that goes to its session without opening the task first
    /// (task 7f61af2e): what a double-click on the line does, minus the click that opens it.
    /// </summary>
    internal const string OpenClaudeText = "Open Claude";

    /// <summary>The words in front of the parent's title at the head of a subtask's thread, and the button's name.</summary>
    internal const string SubtaskOfText = "Subtask of";

    /// <summary>A subtask line's menu item that shows its parent (task 54b39c4c), the panel's and the bar's alike.</summary>
    internal const string OpenParentText = "Open parent";

    // The head of a subtask's thread (task 54b39c4c): "↑ Subtask of <parent title>", the arrow and the words in the
    // caption grey, the title in ink so it reads as the thing to press. The whole line is a button that shows the parent
    // (picked, its thread open, scrolled to), the mirror of a parent's SubtaskEntry below.
    UIElement ParentEntry(Guid parentId, string parentTitle, Action<Guid> openParent)
    {
        var text = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        text.Inlines.Add(new Run($"↑ {SubtaskOfText} ") { Foreground = HeaderInk });
        text.Inlines.Add(new Run(parentTitle) { Foreground = Ink });
        var entry = Flat(text, $"Parent {parentTitle}", () => openParent(parentId), new Thickness(4, 1, 4, 1));
        entry.Background = Brushes.Transparent;
        entry.HorizontalAlignment = HorizontalAlignment.Left;
        entry.Margin = new Thickness(0, 0, 0, 6);
        entry.ToolTip = "Show the parent task";
        return entry;
    }

    // One subtask in its parent's thread: "↳ title · status · status line", dim when it is done or closed. The whole line
    // is a button that opens the subtask's own thread right under it, in place, and folds it again (task 0eb943d8,
    // Petre: "let subtasks stay where they are, expand their current place"). It used to pick the subtask and show it
    // (TasksBoard.ShowTask), which unfolded it onto a line of its own below the parent's whole thread, two
    // threads open one above the other. The selection is left alone, since picking the subtask is what unfolds it.
    UIElement SubtaskEntry(SubtaskView sub, Guid parent)
    {
        var text = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var finished = sub.Status is TaskStatus.Done or TaskStatus.Closed;
        text.Inlines.Add(new Run("↳ ") { Foreground = HeaderInk });
        text.Inlines.Add(new Run(sub.Title) { Foreground = finished ? HeaderInk : Ink });
        text.Inlines.Add(new Run($" · {TaskViews.StatusName(sub.Status)}") { Foreground = HeaderInk });
        if (sub.StatusLine.Length > 0) text.Inlines.Add(new Run($" · {sub.StatusLine}") { Foreground = HeaderInk });
        // A subtask with a line of its own elsewhere in this view (a waiting one in the pinned block, a closed one, any in
        // the Matrix) opens on that line, as before: shown, picked and scrolled to. Opening it here as well would draw
        // its thread twice (review of task 0eb943d8).
        var entry = Flat(text, $"Subtask {sub.Title}", () =>
        {
            if (sub.HasLine) Actions?.ShowTask?.Invoke(sub.Id);
            else Actions?.ToggleThread(sub.Id);
        }, new Thickness(4, 1, 4, 1));
        entry.Background = Brushes.Transparent;
        entry.HorizontalAlignment = HorizontalAlignment.Left;
        entry.ToolTip = sub.HasLine ? "Show this subtask" : sub.Row is not { Expanded: true } ? "Open this subtask here" : "Fold this subtask";
        // DRAGGED OUT OF THE THREAD (task f64d06ae, TasksWindow.Drag.cs's dragEntries): a press on the entry arms a drag as
        // a press on a line does, so an open subtask can be dragged out of its parent, or onto another task. A press
        // that stays a click is still the entry's Click above. A closed one, or one in its undo window, arms nothing.
        if (sub.Status != TaskStatus.Closed && !sub.Closing)
        {
            // A thread entry carries its row only once unfolded; without one it counts as a full disc (task a1debf34).
            dragEntries[sub.Id] = (entry, parent, sub.Quadrant, TaskDropTarget.PartOf(sub.Status, sub.Row?.Delivery.Shipping ?? false));
            entry.PreviewMouseLeftButtonDown += (_, e) => DragPress(sub.Id, DragMouse.Position(e, DragSurface), fromThread: true);
        }
        // ITS TICK IN FRONT (task 0eb943d8, Petre: "how do i close subtasks?"): a subtask folded into its parent's thread
        // has no line, so no tick and no menu, and opened in place it still had none. The entry now starts with the same
        // tick a line has (TickOf): it closes the subtask with Undo in its place for the undo window, by the board's own
        // rules (a working Claude offers to stop first). Once it is closed, the ticked box (task a78ce57a, ClosedBox), in the
        // same slot, so it still starts at the same x as its open siblings' and a closed one reads as closed.
        // The tick sits in a slot of fixed width, so a closed subtask's entry (no tick) starts at the same x as its open
        // siblings'. A DockPanel, not a horizontal StackPanel: the entry fills what is left, so its text still trims
        // with "…" at the thread's edge rather than being clipped mid-word (review).
        var slot = new Border { Width = 22, VerticalAlignment = VerticalAlignment.Center, Child = TickOf(sub.Id, sub.Status, sub.Closing, sub.TickTip, sub.Title, inThread: true) };
        DockPanel.SetDock(slot, Dock.Left);
        return new DockPanel
        {
            Margin = new Thickness(0, 0, 0, 4),
            Children = { slot, entry },
        };
    }

    // One session of the thread (Task 9, the spec's "Task history"): its short id, when it joined and where it stands,
    // and Open, which does what going to that session does. An expired one has nothing left to open, so no button. Nor
    // has a line whose Open would repeat the Open Claude at the thread's top (withOpen false, task 1c17b7d6; see Thread).
    UIElement SessionLine(Guid taskId, SessionView session, bool withOpen = true)
    {
        var shortId = session.Id[..Math.Min(8, session.Id.Length)];
        var line = new DockPanel { Tag = "session-line", Margin = new Thickness(0, 0, 0, 4), LastChildFill = true };
        if (withOpen && session.State != SessionState.Expired && Actions?.OpenSession is { } open)
        {
            var button = Small("Open", $"Open session {shortId}", () => open(taskId, session.Id),
                "Go to this session's window, resuming it first if it has ended");
            DockPanel.SetDock(button, Dock.Right);
            line.Children.Add(button);
        }
        line.Children.Add(new TextBlock
        {
            Text = $"Session {shortId} · joined {When(session.Joined)} · {StateWords(session.State)}",
            FontSize = 11,
            Foreground = session.State is SessionState.Working or SessionState.Idle ? Ink : HeaderInk,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return line;
    }

    // Today as "11:42", another day as "22 Nov 11:42", the way a comment's time reads.
    string When(DateTimeOffset at) =>
        at.ToLocalTime() is var local && local.Date == Now().ToLocalTime().Date ? local.ToString("HH:mm") : local.ToString("d MMM HH:mm");

    static string StateWords(SessionState state) => state switch
    {
        SessionState.Working => "working",
        SessionState.Idle => "idle",
        SessionState.Ended => "ended",
        _ => "expired",
    };

    // `taskId` is the task the comment is on, the one whose workspace a link in it opens in (LinkOpener): the subtask's
    // own for a subtask's thread drawn inside its parent's.
    // One comment of a thread, drawn as a chat (task 39bc9c4d, ChatThread): Petre's a bubble on the right, a session's or
    // the manager's a bubble on the left, each with its author and time under it, Deskbar's own lines a small centred
    // event. Edit and Settle sit by the time and show on hover. While one of his comments is being edited it is the
    // editor box at the thread's full width instead, under its author and time, as before.
    UIElement CommentOf(TaskCommentView comment, Guid taskId)
    {
        // RULING 58 ("i should only be able to edit my own comments, not claude's"): his own comments, and only those, have
        // an Edit button beside the time; the board refuses a session's comment too (TaskBoard.EditComment).
        var editing = editingComment == comment.Id && comment.Editable;
        if (editing)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new TextBlock { Text = comment.Author, FontWeight = FontWeights.Bold, FontSize = 11, Foreground = comment.ByClaude ? ClaudeInk : Ink });
            header.Children.Add(new TextBlock { Text = $" · {comment.When}", FontSize = 11, Foreground = HeaderInk });
            return new StackPanel { Margin = new Thickness(0, 0, 0, 8), Children = { header, CommentEditorBox(comment) } };
        }
        var actions = new List<Button>();
        if (comment.Editable)
            actions.Add(Small(new TextBlock { Text = "Edit", Foreground = DimInk, FontSize = 10 }, "Edit comment", () => BeginEditComment(comment.Id), "Edit your comment"));
        // RULING 62: any comment can be settled (a question that has its answer) or brought back, from its own button; the
        // comment stays either way.
        // Not on the manager's page (task 6c4c067f): its bubbles are the user's messages to the manager, no task's comments.
        if (taskId != Deskbar.Core.Tasks.ManagerSession.PageId)
        actions.Add(Small(new TextBlock { Text = comment.Settled ? "Unsettle" : "Settle", Foreground = DimInk, FontSize = 10 },
            comment.Settled ? "Unsettle comment" : "Settle comment",
            () => Actions?.SettleComment(comment.Id, !comment.Settled),
            // On its task's page a settled comment stays in place, dimmed (ThreadComments, task 5ee182cf): the hints say so.
            comment.Settled ? OnItsPage(taskId) ? "Show it undimmed again" : "Show it with the thread again"
            : OnItsPage(taskId) ? "Dim this comment as settled, keeping it in place" : "Fold this comment away, keeping it"));
        // A question Deskbar asked is a bubble like a session's, not an event line (task 968c2216).
        var isEvent = comment.Who == CommentAuthor.Deskbar && !comment.Asks;
        // Images in tasks: the body renders the comment's text alone; its pictures are a row of their own after it
        // (TaskImageRow), outside the clipped box, so folding to six lines cuts text, never a picture.
        var split = TaskImages.Split(comment.Markdown);
        // A one-line Deskbar event carries its time at its end ("Resumed. · 09:08"); a longer one has it on a line below.
        var timeInBody = isEvent && split.Images.Count == 0 && ChatThread.TimeFitsInline(split.Text);
        var document = MarkdownBlocks.Render(timeInBody ? $"{split.Text.Trim()} · {comment.Time}" : split.Text, LinkOpener(taskId));
        if (FullBubbles) InboxLook.Reading(document, isEvent ? InboxLook.ChatSmall : InboxLook.ChatText);
        else document.FontSize = isEvent ? 10.5 : 11.5;
        // The question waiting on Petre (task 968c2216) reads as it did on the line: orange, semibold.
        document.Foreground = isEvent ? HeaderInk : comment.AsksNow ? Orange : FullBubbles ? InboxLook.ReadInk : Ink;
        if (comment.AsksNow) document.FontWeight = FontWeights.SemiBold;
        if (isEvent) document.TextAlignment = TextAlignment.Center;
        var body = new RichTextBox
        {
            Document = document,
            IsReadOnly = true,
            // Hyperlinks inside a read-only box are clickable only with this set.
            IsDocumentEnabled = true,
            Template = (ControlTemplate)FindResource("BareRichTextBox"),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Ink,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 1, 0, 0),
            Focusable = true,
            Cursor = Cursors.Arrow,
        };
        // RULING 63, as task f0168cdd changed it: a comment shows whole unless it is super long (past SuperLongLines),
        // and a super long one shows FoldedLines, clipped, and ends in "Show more", which opens it in place; "Show less"
        // folds it back. The state is the window's (longOpen), so a redraw keeps it. Whether it is super long is judged
        // by the character count at first (the width is not known while the thread is built), and by the laid-out height
        // once it has been (review of the batch): a comment that fits at the window's width shows whole with no button,
        // and one that wraps past the limit folds and gains it.
        // A HEIGHT DRAGGED (task 2088e1ed, TasksWindow.BodyGrip.cs) replaces the six lines: the grip under a long comment
        // shows more or less of it, and the height it was left at is where the comment starts on every later redraw.
        // A comment dragged to its foot is whole (infinity), and reads as one Show more opened.
        var isLong = IsLong(split.Text);
        // On the Inbox page every bubble shows in full (FullBubble): no clip, no grip, only the forty-line guard.
        var dragged = FullBubbles ? null : BodyHeight(CommentKey(comment.Id));
        var full = FullBubbles || longOpen.Contains(comment.Id) || dragged is double.PositiveInfinity;
        if (!full)
        {
            // Not yet laid out: the character count says whether it folds. One that is not super long has no ceiling.
            body.MaxHeight = dragged ?? (isLong ? FoldedLines : double.PositiveInfinity);
            body.ClipToBounds = true;
        }
        // A comment of pictures alone has no text box to show.
        if (split.Text.Trim().Length == 0) body.Visibility = Visibility.Collapsed;
        // As wide as its text, so a short comment gets a short bubble (ChatThread.FitWidth); an event is centred.
        ChatThread.FitWidth(body);
        body.HorizontalAlignment = isEvent ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        // The grip under the text (task 2088e1ed): from one line to the whole comment. Dragged to its foot, the comment is
        // opened as Show more opens it (KeepCommentHeight).
        var grip = BodyGrip("Resize comment",
            shown: () => body.ActualHeight, setting: () => body.MaxHeight, least: () => OneCommentLine, whole: () => body.ExtentHeight,
            preview: height => body.MaxHeight = height >= body.ExtentHeight - 0.5 ? double.PositiveInfinity : height,
            keep: height => KeepCommentHeight(comment.Id, height, body.ExtentHeight));
        var parts = new StackPanel { Children = { body, grip } };
        var more = Small(new TextBlock { Text = full ? "Show less" : "Show more", Foreground = HeaderInk, FontSize = 10.5 },
            full ? "Show less" : "Show more", () =>
            {
                // By what shows, not by longOpen alone: a comment dragged whole says "Show less" without being in it.
                if (full) longOpen.Remove(comment.Id);
                else longOpen.Add(comment.Id);
                // Either button says how much shows by itself: a dragged height goes (Show less is six lines again).
                ForgetCommentHeight(comment.Id);
                if (latest is not null) Draw(latest);
            }, full ? "Fold the comment back to six lines" : "Show the whole comment");
        // Collapsed means it takes no room and is not in the tree for a reader, but it is there for the layout to switch on.
        // The grip shows with the button: only a comment long enough to be cut has anything to resize.
        more.Visibility = full || isLong || dragged is not null ? Visibility.Visible : Visibility.Collapsed;
        grip.Visibility = more.Visibility;
        parts.Children.Add(more);
        if (FullBubbles) FullInPage(parts, body, grip, more, "comment:" + comment.Id, document.FontSize);
        if (TaskImageRow.Of(StateDir, split.Images, OpenImage) is { } pictures) parts.Children.Add(pictures);
        if (!full)
            body.LayoutUpdated += (_, _) =>
            {
                if (body.ActualWidth <= 0 || body.Visibility != Visibility.Visible) return;
                // Whole up to SuperLongHeight, folded past it (task f0168cdd). ExtentHeight is the whole text's height at
                // the box's width whatever its MaxHeight, so setting the ceiling here settles in one more pass. A dragged
                // height is Petre's and stays, and a grip being dragged sets the ceiling itself (its preview).
                var superLong = body.ExtentHeight > SuperLongHeight + 0.5;
                var ceiling = superLong ? FoldedLines : double.PositiveInfinity;
                if (dragged is null && !grip.IsMouseCaptured && !body.MaxHeight.Equals(ceiling)) body.MaxHeight = ceiling;
                // A comment dragged to a height keeps both however short it gets (a wider panel, an edit), or one dragged
                // to a line could stay cut with nothing left to undo it (review of task 2088e1ed).
                var wanted = superLong || dragged is not null ? Visibility.Visible : Visibility.Collapsed;
                if (more.Visibility != wanted) more.Visibility = wanted;
                // Never collapsed mid-drag, which would take the mouse from it.
                if (grip.Visibility != wanted && !grip.IsMouseCaptured) grip.Visibility = wanted;
            };
        if (isEvent) more.HorizontalAlignment = HorizontalAlignment.Center;
        if (comment.AsksNow && AnswerButtons(taskId) is { } answers) parts.Children.Add(answers);
        var placed = isEvent ? ChatThread.Event(comment, parts, timeInBody, actions) : ChatThread.Message(comment, parts, actions);
        // A settled comment, when unfolded or on its task's page (task 5ee182cf), is dim: it is history.
        placed.Opacity = comment.Settled ? 0.55 : 1;
        return placed;
    }

    // --- editing one of his comments in place (Ruling 58) -------------------------------------------------------------

    // The box a comment is rewritten in: its markdown, wrapping, Enter saves (EditComment, TaskBoard.EditComment) and
    // Shift+Enter starts a new line; Esc, or a click away, keeps the comment as it was. A redraw waits while it is open
    // (MustWait), and it keeps the keyboard through one (Draw).
    TextBox CommentEditorBox(TaskCommentView comment)
    {
        var box = new TextBox
        {
            Text = comment.Markdown,
            Template = (ControlTemplate)FindResource("BareTextBox"),
            Background = Frozen(0xFF, 0x15, 0x16, 0x1A),
            Foreground = Ink,
            CaretBrush = Brushes.White,
            BorderBrush = PickedEdge,
            BorderThickness = new Thickness(1),
            FontSize = 12,
            Padding = new Thickness(3, 1, 3, 1),
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            MaxHeight = 160,
            // No tooltip: it popped up over the text whenever the pointer rested on the box (task bc27a308).
        };
        AutomationProperties.SetName(box, "Edit your comment");
        commentEditor = box;
        box.Loaded += (_, _) =>
        {
            box.SelectAll();
            box.Focus();
        };
        box.PreviewKeyDown += (_, e) =>
        {
            if (!EditorSaves(e.Key, Keyboard.Modifiers)) return;
            e.Handled = true;
            SaveCommentEditor();
        };
        box.LostKeyboardFocus += (_, _) =>
        {
            if (commentEditor == box && !drawing) CancelCommentEdit();
        };
        return box;
    }

    /// <summary>
    /// Opens one of his comments for editing in its open thread. A session's or Deskbar's comment, or one not on show,
    /// opens nothing (the board would refuse it anyway). Internal for the tests; the "Edit" beside a comment's time calls it.
    /// </summary>
    internal void BeginEditComment(Guid commentId)
    {
        var shown = AllRows(latest)
            .SelectMany(r => r.Comments).FirstOrDefault(c => c.Id == commentId);
        if (shown is null || !shown.Editable) return;
        editingComment = commentId;
        if (latest is not null) Draw(latest);
    }

    /// <summary>For the tests: the open comment editor's box, if any.</summary>
    internal TextBox? CommentEditor => commentEditor;

    /// <summary>For the tests: Enter in the open comment editor, the save its key press makes.</summary>
    internal void SaveCommentEditor()
    {
        if (commentEditor is null || editingComment is not { } id) return;
        if (Actions?.EditComment(id, commentEditor.Text) != false) EndCommentEdit();
    }

    /// <summary>Esc or a click away: the comment stays as it was.</summary>
    internal void CancelCommentEdit() => EndCommentEdit();

    void EndCommentEdit()
    {
        editingComment = null;
        commentEditor = null;
        if (latest is not null) Present(latest);
    }


    // Petre's next comment: Enter adds it, Shift+Enter starts a new line (markdown lists need them). What is typed is
    // kept in `drafts`, so a redraw while he types (from a session's change) loses nothing.
    //
    // Petre's screenshot: the placeholder "Comment as you, in markdow…" was cut off and the box was squeezed beside its
    // button inside the indented frame. So the box spans the frame's full width with a short placeholder and the send
    // button sits inside the box's right end (task 39bc9c4d). Nothing here has a width of its own to clip at the
    // window's minimum width: the box wraps.
    //
    // NO "MARKDOWN WORKS" HINT (task bc27a308, "it appears and is annoying"): the box used to carry the hint (markdown
    // works, which keys do what) as a tooltip, which popped up over the text whenever the pointer rested on the box,
    // and as a dim line under it while it had the keyboard. Both are gone; the placeholder and the send button's own
    // tooltip ("Post the comment (Enter)") are all the box says about itself.
    internal const string CommentPlaceholder = "Add a comment…";

    UIElement CommentBox(Guid id)
    {
        var box = new TextBox
        {
            Text = drafts.GetValueOrDefault(id, ""),
            Template = (ControlTemplate)FindResource("BareTextBox"),
            Background = Brushes.Transparent,
            Foreground = Ink,
            CaretBrush = Brushes.White,
            BorderThickness = new Thickness(0),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            // A PASTE KEEPS ALL ITS LINES (task 0596fca2, "pasting long text should increase the comment space"): with
            // AcceptsReturn off WPF cuts a paste at its first line break, so a pasted form of many lines arrived as its
            // first line and the box had nothing to grow for. With it on, the pasted lines stay and the box grows with
            // them up to MaxHeight (about seven lines), then scrolls inside, as the subtask and add boxes do. Plain Enter
            // still posts: PressInComment, on PreviewKeyDown, takes it before the box can break the line.
            AcceptsReturn = true,
            MaxHeight = 120,
        };
        // The manager's page (task 6c4c067f) uses this same box under ManagerSession.PageId: its words say who it reaches.
        var toManager = id == Deskbar.Core.Tasks.ManagerSession.PageId;
        AutomationProperties.SetName(box, toManager ? "Message the manager" : "New comment");
        var placeholder = new TextBlock
        {
            Tag = "comment-placeholder",
            Text = toManager ? ManagerPlaceholder : CommentPlaceholder,
            FontSize = 12,
            Foreground = HeaderInk,
            IsHitTestVisible = false,
            Margin = new Thickness(2, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed,
        };
        box.TextChanged += (_, _) =>
        {
            drafts[id] = box.Text;
            placeholder.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        // THE "/" LIST OF SKILLS (task 29bdd158, "add /skills support to comments"), as the add box has (task 3a872301):
        // "/" at the start of the comment lists the Claude Code skills of the folder the comment reaches (the task's
        // session's, TasksWindowActions.TaskSkills); Tab or Enter puts "/skill " in the box for its arguments, and the
        // next Enter posts it. CommentWaker then asks the session to run that skill. Attached BEFORE PressInComment, so
        // while the list is open its Enter picks instead of posting.
        commentSkills[id] = new SkillCompletion(box,
            () => Actions?.TaskSkills is { } skills ? skills(id) : Task.FromResult<IReadOnlyList<SkillChoice>>([]),
            // Not in the manager's box: the manager runs only Deskbar's own commands, so it has no skills to list.
            () => Actions?.TaskSkills is not null && !toManager,
            new SkillCompletion.Look(Frozen(0xFF, 0x15, 0x16, 0x1A), PickedEdge, Ink, DimInk, SelectedFill),
            SkillCompletion.CommentAsking, SkillCompletion.CommentNone);
        box.PreviewKeyDown += (_, e) => e.Handled = PressInComment(id, e.Key, Keyboard.Modifiers) != KeyOutcome.Ignored;
        // Ctrl+V and drops of pictures (TasksWindow.Images.cs). After the handler above, which sets Handled for every key.
        // Not in the manager's box: the manager cannot read files, so a picture would reach it as a path it cannot open.
        if (!toManager) TakePictures(box, id);
        // The keyboard leaving the box frees a redraw that waited for it.
        box.LostKeyboardFocus += (_, _) => Flush();
        commentBoxes[id] = box;

        // THE BOX AS A CHAT'S (task 39bc9c4d): a rounded box with a round send button inside its right end, in place of the
        // square box with a "Comment" button under it. The button keeps its name, "Post comment", for automation and the
        // tests. It sits at the bottom, so it stays by the last line as the box grows.
        var post = Flat(SendArrow(), toManager ? "Send to the manager" : "Post comment", () => Post(id), new Thickness(0));
        post.Background = Brushes.Transparent;
        post.ToolTip = toManager ? "Send to the manager (Enter)" : "Post the comment (Enter)";
        post.VerticalAlignment = VerticalAlignment.Bottom;
        post.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(post, 1);
        box.VerticalAlignment = VerticalAlignment.Center;
        placeholder.VerticalAlignment = VerticalAlignment.Center;
        var inside = new Grid { Children = { box, placeholder, post } };
        inside.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inside.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        return new StackPanel
        {
            Margin = new Thickness(0, 4, 0, 0),
            Children =
            {
                new Border
                {
                    CornerRadius = new CornerRadius(15),
                    Background = Frozen(0xFF, 0x15, 0x16, 0x1A),
                    BorderBrush = ThreadEdge,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(12, 3, 3, 3),
                    Child = inside,
                },
                // The pictures waiting to go with the comment, each with its x (TasksWindow.Images.cs).
                PendingRow(id),
            },
        };
    }

    // The send button's face: an up arrow on a round blue disc, 22 DIP across.
    static UIElement SendArrow() => new Border
    {
        Width = 22,
        Height = 22,
        CornerRadius = new CornerRadius(11),
        Background = PickedFill,
        Child = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 5.5,10 L 5.5,1.5 M 2,5 L 5.5,1.5 L 9,5"),
            Stroke = Brushes.White,
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Width = 11,
            Height = 11.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };

    // A key in a comment box, as one method the tests drive without a real key event.
    KeyOutcome PressInComment(Guid id, Key key, ModifierKeys modifiers)
    {
        if (key != Key.Enter && key != Key.Return) return KeyOutcome.Ignored;
        if (!modifiers.HasFlag(ModifierKeys.Shift)) return Post(id) ? KeyOutcome.Posted : KeyOutcome.NewLine;
        if (commentBoxes.TryGetValue(id, out var box))
        {
            var at = box.CaretIndex;
            box.Text = box.Text.Insert(at, "\n");
            box.CaretIndex = at + 1;
        }
        return KeyOutcome.NewLine;
    }

    // Posts the box's text as Petre's comment, with its pending pictures written and their lines after the text (images
    // in tasks: a comment of pictures alone is fine, one of neither is nothing). The box is emptied first, so the redraw the
    // new comment causes is not held back by text that is already posted; a refusal puts the text back, keeps the
    // pictures pending and deletes the files just written.
    bool Post(Guid id)
    {
        var text = CommentDraft(id);
        if (string.IsNullOrWhiteSpace(text) && PendingImages(id).Count == 0 || Actions is null) return false;
        // A dialog waits on the user (TasksWindow.DialogCard): the typed words go to it as the answer instead of into the
        // thread, where the session, blocked on the dialog, would read them only after it is answered (task c047d0a0, a
        // case measured by task c49adbe0: a reply sat ten hours in the chat). Only words, no pictures; when the dialog
        // has ended, the words post as a comment as ever.
        if (PendingImages(id).Count == 0 && text.Trim().Length > 0 && AnsweredDialogWith(id, text))
        {
            drafts.Remove(id);
            if (commentBoxes.TryGetValue(id, out var answered)) answered.Text = "";
            return true;
        }
        if (CommentWithPictures(id, text.Trim().Length == 0 ? "" : text) is not { } comment) return false;
        drafts.Remove(id);
        if (commentBoxes.TryGetValue(id, out var box)) box.Text = "";
        // The manager's box (task 6c4c067f) sends to the manager rather than posting a comment on a task.
        if (id == Deskbar.Core.Tasks.ManagerSession.PageId ? SendToManager(comment.Markdown) : Actions.Comment(id, comment.Markdown))
        {
            Posted(id);
            return true;
        }
        drafts[id] = text;
        if (commentBoxes.TryGetValue(id, out var still)) still.Text = text;
        ForgetWritten(comment.Written);
        return false;
    }

    // For the tests: types into a line's comment box and presses a key (none: only types).
    internal bool TypeComment(Guid id, string text, Key? key, ModifierKeys modifiers = ModifierKeys.None)
    {
        if (!commentBoxes.TryGetValue(id, out var box)) return false;
        box.Text = text;
        return key is { } pressed && PressInComment(id, pressed, modifiers) == KeyOutcome.Posted;
    }

    // --- rename --------------------------------------------------------------------------------------------------------

    // The title's TextBlock, so a double-click on it can be told from one elsewhere on the line.
    const string TitleTag = "task-title";

    // THE IN-PLACE EDITOR (Ruling 38, grown by Ruling 58): Edit opens the title AND the description together, two boxes one
    // above the other in the line. Each wraps and takes line breaks (the description is markdown). Enter in either saves
    // both through Edit (TaskService.Rename and TaskService.Describe, `task rename` and `task describe`'s twins),
    // Shift+Enter adds a line break, Esc or a click away from both cancels; Tab moves between them, and the keyboard
    // moving from one box to the other is not "a click away" (EditorKeepsFocus). While it is open a redraw waits
    // (MustWait: `renaming`), so a change arriving from a session never takes the text from under the keyboard; it is drawn
    // when the editor closes. A Claude task's status line is not here: it is the session's.
    TextBox RenameBox(TaskRowView row)
    {
        var box = EditorBox(row, row.Title, "Rename task");
        box.Loaded += (_, _) =>
        {
            box.SelectAll();
            box.Focus();
        };
        return box;
    }

    // The description's box: the same look, taller, empty for a task with none. Images in tasks: it holds the text alone;
    // the description's pictures are the row under it (EditorRow), so a path is never edited by hand, and Ctrl+V or a drop
    // of a picture adds one there (TasksWindow.Images.cs).
    TextBox DescriptionBox(TaskRowView row)
    {
        var box = EditorBox(row, TaskImages.Split(row.Description).Text, "Task description");
        box.MinHeight = 38;
        box.Margin = new Thickness(0, 3, 0, 0);
        TakeEditorPictures(box);
        return box;
    }

    TextBox EditorBox(TaskRowView row, string text, string name)
    {
        var box = new TextBox
        {
            Text = text,
            Template = (ControlTemplate)FindResource("BareTextBox"),
            Background = Frozen(0xFF, 0x15, 0x16, 0x1A),
            Foreground = Ink,
            CaretBrush = Brushes.White,
            BorderBrush = PickedEdge,
            BorderThickness = new Thickness(1),
            FontSize = 12,
            Padding = new Thickness(3, 1, 3, 1),
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
        };
        AutomationProperties.SetName(box, name);
        box.PreviewKeyDown += (_, e) =>
        {
            // Shift+Enter is left to the box, which takes it as a line break (AcceptsReturn).
            if (!EditorSaves(e.Key, Keyboard.Modifiers)) return;
            e.Handled = true;
            SaveEditor();
        };
        // A click elsewhere abandons the edit; only Enter keeps it. The keyboard going to the editor's other box does not.
        box.LostKeyboardFocus += (_, e) =>
        {
            if (renameBox is not null && !drawing && !EditorKeepsFocus(e.NewFocus)) EndRename();
        };
        return box;
    }

    // Whether the keyboard is moving to one of the editor's own boxes (title and description), or out of the window
    // altogether. Leaving the window says "to nowhere" (null): that is Win+Shift+S for a screenshot, or a press on a file in
    // Explorer to drag it in, and both are on the way to putting a picture in the description (images in tasks; the final
    // review of that branch), so the editor stays open for it. A click on anything else in the panel still cancels, and Esc
    // does. While it stays open the panel's redraws wait, as they do for any open editor (MustWait).
    bool EditorKeepsFocus(IInputElement? next) =>
        next is null || ReferenceEquals(next, renameBox) || ReferenceEquals(next, descriptionBox);

    internal void BeginRename(Guid id)
    {
        renaming = id;
        // Images in tasks: the description's pictures are the editor's kept ones, and nothing is pasted yet.
        StartEditorPictures(RowFor(id)?.Description ?? "");
        if (latest is not null) Draw(latest);
    }

    // Enter in the editor: saved through Edit, and the editor closes once it is taken (a refused title, an empty one,
    // keeps it open with the problem line saying why; the description is not touched then). Images in tasks: the pictures
    // pasted into the description are written first and their lines joined after its text with the kept ones; pictures
    // that cannot be written keep the editor open, and a refused edit deletes them again.
    void SaveEdit(Guid id, string title, string description)
    {
        if (DescriptionWithPictures(id, description) is not { } withPictures) return;
        if (Actions?.Edit(id, title, withPictures.Description) != false) EndRename();
        else ForgetWritten(withPictures.Written);
    }

    // F2: the selected line's title, when the line is on show, open and nothing is being edited already.
    bool EditSelected()
    {
        // An Inbox page edits its own task, whatever is selected (TasksWindow.InboxPage.cs, task 740a2231).
        if (InboxEditPage()) return true;
        if (renaming is not null || selectedId.HasNoValue) return false;
        var id = selectedId.Value;
        // Not from a box being typed in (the add box, the search box, the subtask box in a thread): F2 there is the box's.
        // Any line on show, a subtask's in its parent's open thread included (task 452487b4: an open thread takes in the
        // subtasks nested under it, so a selected subtask can have no top-level line).
        // In the Inbox only a line drawn in the tab on show (Keys): RowFor reads every row of the view, the other tabs' too,
        // and F2 must not open an editor on a line that is not on screen (task 740a2231, the tester's M1).
        if (RowFor(id) is null || latest?.Inbox is not null && !Keys.Contains(id)
            || closing.GetValueOrDefault(id) || AddBoxShown && AddBox.IsKeyboardFocusWithin || SearchBox.IsKeyboardFocusWithin
            || subtaskBox?.IsKeyboardFocusWithin == true) return false;
        BeginRename(id);
        return true;
    }

    /// <summary>For the tests: the open editor, if any (the title's box).</summary>
    internal TextBox? Editor => renameBox;

    /// <summary>For the tests: the open editor's description box, if any.</summary>
    internal TextBox? DescriptionEditor => descriptionBox;

    /// <summary>
    /// Whether a key in the editor saves: Enter without Shift. Shift+Enter is left to the box, which takes it as a line
    /// break (AcceptsReturn).
    /// </summary>
    internal static bool EditorSaves(Key key, ModifierKeys modifiers) =>
        key is Key.Enter or Key.Return && !modifiers.HasFlag(ModifierKeys.Shift);

    /// <summary>For the tests: Enter in the open editor, the save its key press makes.</summary>
    internal void SaveEditor()
    {
        if (renameBox is not null && renaming is { } id) SaveEdit(id, renameBox.Text, descriptionBox?.Text ?? "");
    }

    void EndRename()
    {
        renaming = null;
        renameBox = null;
        descriptionBox = null;
        EndEditorPictures();
        if (latest is not null) Present(latest);
    }

    // --- menus ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A line's "⋯" menu: status, close or reopen, rename, move, delete. Internal so the tests can open one and check a
    /// redraw waits for it.
    /// </summary>
    //
    // EDIT FIRST (Ruling 44; Petre: "task rightclick should let me edit it first, first option"). The same menu opens
    // from "⋯" and from a right-click anywhere on the line (`atPointer`: at the pointer instead of under "⋯"), and its
    // first item is Edit, the in-place editor (BeginRename). Edit took the place of the old "Rename…", which did the same.
    // EVERY ROW DRAWN AS A LINE, BY ID (task 249a1e8a): the List's lines, the Matrix's, and the subtasks drawn as lines in
    // a parent's open thread, which no group or cell lists. The menu, Edit, the snooze and goal menus, Assign to, the link
    // picker, the subtask box and a comment's Edit look a task up here, so each works on a subtask's line in a thread as it
    // does on any other line. A subtask with a line of its own carries no row in its parent's thread (SubtaskView.Row), so
    // no task is found twice.
    // Only the half on show: the view carries the List's groups and the Matrix's cells alike, and the two can disagree on
    // whether a subtask folds into its parent (one in the Claude working group keeps its own line in the List), so
    // reading both could find a subtask in a thread the List never draws (review of this change).
    static IEnumerable<TaskRowView> AllRows(TasksView? view) =>
        view is null ? [] : DrawnRows(view).SelectMany(r => ThreadLines([r]).Prepend(r));

    static IEnumerable<TaskRowView> DrawnRows(TasksView view) =>
        view.Matrix ? view.Cells.SelectMany(c => c.Rows) : view.Groups.SelectMany(g => g.Rows);

    // The subtasks the given lines' open threads draw as lines of their own (Thread: no line elsewhere, a row to draw).
    static IEnumerable<TaskRowView> ThreadLines(IEnumerable<TaskRowView> lines) =>
        lines.SelectMany(r => r.Subtasks.Where(s => !s.HasLine).Select(s => s.Row).OfType<TaskRowView>());

    TaskRowView? RowFor(Guid id) => AllRows(latest).FirstOrDefault(r => r.Id == id);

    // Whether the task is drawn as a subtask's line inside its parent's open thread (task 249a1e8a, Line's inThreadOf).
    bool InParentThread(Guid id) => latest is { } view && ThreadLines(DrawnRows(view)).Any(sub => sub.Id == id);

    internal ContextMenu OpenMenuFor(Guid id, bool atPointer = false)
    {
        var row = RowFor(id);
        var menu = atPointer
            ? OwnMenus.AtPointer(this) // this window as its target, for its screen's scaling
            : new ContextMenu { PlacementTarget = menuButtons.GetValueOrDefault(id), Placement = PlacementMode.Bottom };
        if (row is null) return menu;

        // A closed task's title is not edited: it is history, and Reopen brings it back first.
        if (row.Status != TaskStatus.Closed && !row.Closing)
        {
            // Every top-level item wears an icon (TaskMenuIcons, task e3f94ead), the line's own picture where it has one.
            menu.Items.Add(Item("Edit", () => BeginRename(id)).WithIcon(TaskMenuIcons.Edit));
            // OPEN IN WINDOW (task dca4ebeb): the task in a window of its own, for a long one (TasksBoard.OpenWindow). Right
            // after Edit, its sibling: both are about reading and writing the task itself. Not a double-click on the line,
            // which goes to the task's workspace (Ruling 54).
            if (Actions?.OpenWindow is { } openWindow)
                menu.Items.Add(Item(OpenInWindowText, () => Dispatcher.BeginInvoke(() => openWindow(id), System.Windows.Threading.DispatcherPriority.Input))
                    .WithIcon(TaskMenuIcons.OpenInWindow));
            // OPEN CLAUDE (task 7f61af2e; Petre: "make it possible to open claude without opening the task first"): the
            // double-click's go (Actions.Go: the task's workspace, then its session's window) without the single click
            // that picks the task and opens its thread first (Ruling 54). Only on a Claude task, which has a session to
            // open; a note's go is a workspace switch. Run once this menu has closed, as the items below are, so the
            // window it brings forward does not come up under an open menu.
            // Only while the click can open the session (OpensClaude, task 6a57f59b); otherwise Start Claude on this below.
            if (OpensClaude(row))
                menu.Items.Add(Item(OpenClaudeText, () => Dispatcher.BeginInvoke(() => Actions?.Go(id), System.Windows.Threading.DispatcherPriority.Input))
                    .WithClaude(hasSession: true));
            // Task 9: handing the task to Claude, right after Edit (the spec, "Where the clicks are"). Run once this menu
            // has closed: it may ask a question or open a pick list of folders at the pointer.
            // ONE CLAUDE ITEM PER MENU (task b6f068b1; Petre: "open claude and start claude , no need for both"): only on
            // a task no session has joined. A Claude task has Open Claude above, which resumes an ended session. Its
            // thread follows the same rule (ThreadActions, task 5e02e99f), and so does the bar's menu
            // (FloatingBar.OpenTaskLineMenu).
            else if (Actions?.StartClaude is { } start)
                menu.Items.Add(Item(StartClaudeText, () => Dispatcher.BeginInvoke(() => start(id), System.Windows.Threading.DispatcherPriority.Input))
                    .WithClaude(hasSession: false));
            // Subtasks (Petre's pick A): the add box with this line's task as the parent (a sibling on a subtask's line),
            // once this menu has closed so the box can take the keyboard.
            if (Actions?.SubtaskParent is not null)
                menu.Items.Add(Item(FloatingBar.AddSubtaskText, () => Dispatcher.BeginInvoke(() => BeginSubtaskAdd(id), System.Windows.Threading.DispatcherPriority.Input))
                    .WithIcon(TaskMenuIcons.AddSubtask));
        }
        // OPEN PARENT (task 54b39c4c): on a subtask's line, the parent shown, picked and opened, as the thread's "Subtask
        // of" does, without opening this line's thread first. Right after "Add subtask…", its family neighbour. On a
        // closed subtask too, since the parent is still worth reaching from it. Run once this menu has closed.
        if (row.ParentId is { } parentId && Actions?.OpenParent is { } openParent)
            menu.Items.Add(Item(OpenParentText, () => Dispatcher.BeginInvoke(() => openParent(parentId), System.Windows.Threading.DispatcherPriority.Input))
                .WithIcon(TaskMenuIcons.OpenParent));
        // COPY LINK (task 5016d533; Petre: "create a link to the task and include it in github pr / issues / elsewhere so
        // that when i click on it, it opens the task in deskbar"): the markdown link to the task (TaskLinkClipboard), for
        // a pull request, an issue or a message. On every line, a closed one too: a link to history still opens it.
        menu.Items.Add(Item(TaskLinkClipboard.CopyLinkText, () => TaskLinkClipboard.Copy(id)).WithIcon(TaskMenuIcons.CopyLink));
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());

        var status = Item("Status").WithIcon(TaskMenuIcons.Status);
        SettableStatuses.ToList().ForEach(s =>
        {
            var item = Item(TaskViews.StatusName(s), () => Actions?.SetStatus(id, s));
            item.IsChecked = row.Status == s && !row.Closing;
            status.Items.Add(item);
        });
        menu.Items.Add(status);
        menu.Items.Add(row.Status == TaskStatus.Closed && !row.Closing
            ? Item("Reopen", () => Actions?.Reopen(id)).WithIcon(TaskMenuIcons.Reopen)
            : Item("Close", () => Actions?.Close(id)).WithIcon(TaskMenuIcons.Close));
        // MARK DEPLOYED (task 8470d176, phase 2; RunningChanges' header). A Done task whose pull request in another
        // repository is merged keeps its hollow ring until a deploy is reported, and Deskbar cannot see that app's
        // servers. When the session could not check (or the deploy came later), Petre can: the ring fills with
        // "Marked deployed by hand" as the evidence. Next to Close, its neighbour in finishing a task. Only while the
        // item would change something (row.CanMarkDeployed); `task deployed <id> <note>` is its remote twin.
        if (row.CanMarkDeployed && !row.Closing && Actions?.MarkDeployed is { } markDeployed)
            menu.Items.Add(Item(MarkDeployedText, () => markDeployed(id)).WithIcon(TaskMenuIcons.MarkDeployed));
        // Snooze (task 6da8d7eb): the moon's lengths as a submenu, for every open line, waiting ones included, so a Matrix
        // line, which has no moon, reaches it too.
        if (Actions?.Snooze is not null && row.Status != TaskStatus.Closed && !row.Closing)
        {
            var snooze = Item(row.SnoozedUntil is null ? "Snooze" : "Snoozed").WithIcon(TaskMenuIcons.Snooze);
            SnoozeItems(id).ForEach(item => snooze.Items.Add(item));
            menu.Items.Add(snooze);
        }
        // Repeat (task 0b78269e, RepeatMenu): the five schedules, Custom… and, while it repeats, Off. Open lines only.
        if (Actions?.Repeat is { } repeat && row.Status != TaskStatus.Closed && !row.Closing)
        {
            var repeats = Item(RepeatMenu.Header(row.RepeatText)).WithIcon(TaskMenuIcons.Repeat);
            // Run once this menu has closed, as "Start Claude on this" does: the folder pick opens a menu of its own, and one
            // opened from inside a click of this menu closes with it (review fix, task 0b78269e).
            RepeatMenu.Items(id, row.RepeatText,
                (task, words) => Dispatcher.BeginInvoke(() => repeat(task, words), System.Windows.Threading.DispatcherPriority.Input),
                AskRepeat).ForEach(item => repeats.Items.Add(item));
            menu.Items.Add(repeats);
        }
        // The task list's one workspace menu (WorkspaceChipMenu), the move the bar's line chip makes. Its own menu,
        // opened under the line's "⋯" once this one has closed: the compact chip menu is a context menu of its own, not
        // a submenu.
        // Who the task is on (Ruling 57), here and nowhere on the bar: Me or Claude, the task's own ticked.
        var assign = Item(AssignToText).WithIcon(TaskMenuIcons.AssignTo);
        AssignItems(id).ForEach(item => assign.Items.Add(item));
        menu.Items.Add(assign);
        menu.Items.Add(Item(MoveToText, () => Dispatcher.BeginInvoke(() => OpenMoveMenu(id, row.Place.Workspace),
            System.Windows.Threading.DispatcherPriority.Input)).WithIcon(TaskMenuIcons.MoveTo));
        // TASK LINKS (Petre's pick: a menu item with a search picker; TasksWindow.Links.cs). Each opens the picker in the
        // task's thread once this menu has closed, so its box can take the keyboard. "Duplicate of…" on any line not
        // waiting out its undo, closed ones too (a closed task can still turn out to be a duplicate); a task already a
        // duplicate is reopened, not re-pointed, so it has none.
        if (Actions?.LinkCandidates is not null && !row.Closing)
        {
            if (row.DuplicateOfTitle is null)
                menu.Items.Add(Item(DuplicateOfText, () => Dispatcher.BeginInvoke(() => BeginLink(id, duplicate: true), System.Windows.Threading.DispatcherPriority.Input))
                    .WithIcon(TaskMenuIcons.DuplicateOf));
            menu.Items.Add(Item(RelatedToText, () => Dispatcher.BeginInvoke(() => BeginLink(id, duplicate: false), System.Windows.Threading.DispatcherPriority.Input))
                .WithIcon(TaskMenuIcons.RelatedTo));
        }
        // The goal the task counts toward (goals plan, Task 6): a plain submenu, only while goals exist (ruling G1).
        if (latest?.ShowsGoals == true)
        {
            var goal = Item("Goal").WithIcon(TaskMenuIcons.Goal);
            GoalItems(id, row.Goal).ForEach(item => goal.Items.Add(item));
            menu.Items.Add(goal);
        }
        menu.Items.Add(new Separator());
        // Delete cannot be undone, so it takes a second, deliberate step: a submenu item rather than a dialog, which
        // would be one more window to answer for a menu choice already made.
        var delete = Item(DeleteText).WithIcon(TaskMenuIcons.Delete);
        delete.Items.Add(Item("Delete for good", () => Actions?.Delete(id)));
        menu.Items.Add(delete);
        ShapeWindowMenu(menu, row);
        OpenMenu(menu);
        return menu;
    }

    // A line's "Move to…": the compact workspace menu under its "⋯" (or under the line's own Move to button, `anchor`,
    // task 59b8f613), the task's own workspace outlined. Internal, and answering the menu, for the tests.
    internal ContextMenu OpenMoveMenu(Guid id, Maybe<Guid> current, UIElement? anchor = null)
    {
        var menu = WorkspaceChipMenu.Build(latest?.Places ?? [], current, workspace => Actions?.MoveTo(id, workspace), anchor ?? menuButtons.GetValueOrDefault(id));
        OpenMenu(menu);
        return menu;
    }

    // "Workspace: all ▾": "All workspaces" first, then the compact workspace menu, the filter in force outlined. With
    // every workspace shown, or the one in front followed (the "Current workspace" checkbox beside it, task ed1a01fc), no
    // chip is: an id no workspace has stands for "none of these". Under the checkbox the workspace in front is in the menu
    // too, but outlining it would say the filter is pinned to it, and picking it does pin it (and unticks the box).
    ContextMenu WorkspaceFilterMenu(TasksView view, Button anchor) =>
        WorkspaceChipMenu.Build(view.Places, view.Where.Any || view.Where.FollowsCurrent ? Maybe.From(Guid.Empty) : view.Where.Workspace,
            workspace => Actions?.FilterWorkspace(TaskWorkspaceFilter.On(workspace)), anchor,
            extra: [new WorkspaceChipMenu.Extra("All workspaces", view.Where.Any, () => Actions?.FilterWorkspace(TaskWorkspaceFilter.All))]);

    // --- goals (goals plan, Task 6) -------------------------------------------------------------------------------------

    // The assignee filter's button: the anchor its menu opens under (Ruling 57).
    Button? assigneeFilterButton;

    /// <summary>
    /// "Assigned: all ▾": Me, Claude, All, the filter in force checked. Internal, and answering the menu, for the tests.
    /// </summary>
    internal ContextMenu OpenAssigneeFilterMenu()
    {
        var shown = latest?.AssigneeFilter ?? TaskAssigneeFilter.All;
        var menu = new ContextMenu { PlacementTarget = assigneeFilterButton, Placement = PlacementMode.Bottom };
        new[] { ("Me", TaskAssigneeFilter.Me), ("Claude", TaskAssigneeFilter.Claude), ("All", TaskAssigneeFilter.All) }.ToList().ForEach(choice =>
        {
            var item = Item(choice.Item1, () => Actions?.FilterAssignee(choice.Item2));
            item.IsChecked = shown == choice.Item2;
            menu.Items.Add(item);
        });
        OpenMenu(menu);
        return menu;
    }

    // The goal filter's button, while it is shown: the anchor its menu opens under.
    Button? goalFilterButton;

    // The "Goal" choices for one task: "None", then this month's and this week's open goals (the view's Goals), the
    // task's own goal checked, each with "week" or "month" dim beside it. Plain TextBlock headers and no colours: goals
    // have none. A task linked to a goal the list leaves out (another period's, or one done since) gets that goal at the
    // end, ticked, so the menu never hides what the task counts toward (review M6); it takes no click, since it is the
    // link already made. With nothing to offer but "None", a disabled line says why.
    List<MenuItem> GoalItems(Guid id, GoalChoice? own)
    {
        var none = Item("None", () => Actions?.SetGoal(id, Maybe<Guid>.None));
        none.IsChecked = own is null;
        var offered = latest?.Goals ?? [];
        var goals = offered.Select(choice => GoalItem(choice, own, () => Actions?.SetGoal(id, choice.Goal)))
            .Concat(own is not null && offered.All(c => c.Goal != own.Goal) ? [GoalItem(own, own, null)] : Array.Empty<MenuItem>())
            .ToList();
        return goals.Count > 0
            ? goals.Prepend(none).ToList()
            : [none, new MenuItem { Header = new TextBlock { Text = "No goals this week or month" }, IsEnabled = false }];
    }

    static MenuItem GoalItem(GoalChoice choice, GoalChoice? own, Action? click)
    {
        var item = Item(choice.Title, click);
        item.InputGestureText = choice.Period;
        item.IsChecked = own is not null && choice.Goal == own.Goal;
        return item;
    }

    /// <summary>
    /// A line's goal choices as a menu of their own under its "⋯", the same items as the More menu's "Goal" submenu.
    /// Internal, and answering the menu, for the tests, which check it and that a redraw waits for it.
    /// </summary>
    internal ContextMenu OpenGoalMenu(Guid id)
    {
        var row = RowFor(id);
        var menu = new ContextMenu { PlacementTarget = menuButtons.GetValueOrDefault(id), Placement = PlacementMode.Bottom };
        GoalItems(id, row?.Goal).ForEach(item => menu.Items.Add(item));
        OpenMenu(menu);
        return menu;
    }

    /// <summary>
    /// "Goal: all ▾": "All goals", "No goal", then this month's and this week's goals (every status) and the goal in
    /// force, the filter in force checked. Internal, and answering the menu, for the tests.
    /// </summary>
    internal ContextMenu OpenGoalFilterMenu()
    {
        var shown = latest?.GoalFilter ?? TaskGoalFilter.All;
        var menu = new ContextMenu { PlacementTarget = goalFilterButton, Placement = PlacementMode.Bottom };
        var all = Item("All goals", () => Actions?.FilterGoal(TaskGoalFilter.All));
        all.IsChecked = shown.Any;
        var noGoal = Item("No goal", () => Actions?.FilterGoal(TaskGoalFilter.NoGoal));
        noGoal.IsChecked = shown == TaskGoalFilter.NoGoal;
        menu.Items.Add(all);
        menu.Items.Add(noGoal);
        (latest?.GoalFilterChoices ?? []).ToList().ForEach(choice =>
        {
            var item = Item(choice.Title, () => Actions?.FilterGoal(new TaskGoalFilter(false, choice.Goal)));
            item.InputGestureText = choice.Period;
            item.IsChecked = !shown.Any && shown.Goal == choice.Goal;
            menu.Items.Add(item);
        });
        OpenMenu(menu);
        return menu;
    }

    // For the tests: the menu open now, if any.
    internal ContextMenu? OpenedMenu => openMenu;

    /// <summary>
    /// The menu a tick (or the More menu's Close) opens on a task Claude is still working on (task 37cd7081), at the
    /// pointer: a line saying so, which takes no click, and "Stop Claude and close" (TasksWindowActions.StopClaudeAndClose).
    /// Opened by TasksBoard.Close; internal, and answering the menu, for the tests.
    /// </summary>
    internal ContextMenu OpenStopClaudeMenu(Guid id)
    {
        var menu = OwnMenus.AtPointer(this); // this window as its target, for its screen's scaling
        menu.Items.Add(new MenuItem { Header = new TextBlock { Text = "Claude is still working on this task." }, IsEnabled = false });
        // Run once this menu has closed, so the redraw the close causes is not held back by it (MustWait).
        menu.Items.Add(Item(TaskViews.StopClaudeAndClose, () => Dispatcher.BeginInvoke(() => Actions?.StopClaudeAndClose?.Invoke(id),
            System.Windows.Threading.DispatcherPriority.Input)));
        OpenMenu(menu);
        return menu;
    }

    /// <summary>
    /// The menu a tick (or the More menu's Close) opens on a task that is not finished (task 579f2e4c: it still asks
    /// something or has a live session, and its work is not merged), at the pointer: <paramref name="header"/>, which
    /// takes no click and says what the close would drop (TaskViews.UnfinishedHeader), and "Close anyway"
    /// (TasksWindowActions.CloseAnyway). Esc or a click elsewhere leaves the task open, which is the point: one tick no
    /// longer closes such a task. Opened by TasksBoard.Close; internal, and answering the menu, for the tests.
    /// </summary>
    internal ContextMenu OpenUnfinishedMenu(Guid id, string header)
    {
        var menu = OwnMenus.AtPointer(this); // this window as its target, for its screen's scaling
        menu.Items.Add(new MenuItem { Header = new TextBlock { Text = header }, IsEnabled = false });
        // Run once this menu has closed, so the redraw the close causes is not held back by it (MustWait).
        menu.Items.Add(Item(TaskViews.CloseAnyway, () => Dispatcher.BeginInvoke(() => Actions?.CloseAnyway?.Invoke(id),
            System.Windows.Threading.DispatcherPriority.Input)));
        OpenMenu(menu);
        return menu;
    }

    // Every menu of the window opens here, so a redraw waits for it (MustWait) and runs when it closes.
    void OpenMenu(ContextMenu menu)
    {
        menu.Closed += (_, _) =>
        {
            if (openMenu == menu) openMenu = null;
            Flush();
        };
        openMenu = menu;
        menu.IsOpen = true;
    }

    /// <summary>
    /// Asks for Custom…'s words (task 0b78269e): a PromptDialog taking what `task repeat` takes, `--model` included.
    /// A seam for the tests, which never show a dialog.
    /// </summary>
    internal Func<string, Maybe<string>> AskRepeat
    {
        get => askRepeat ?? (current => PromptDialog.Ask("Repeat", "Schedule, e.g. every 2h, daily 09:00, weekly fri 17:30 (add --model <m> to change the model):", current, owner: this));
        set => askRepeat = value;
    }

    Func<string, Maybe<string>>? askRepeat;

    // A TextBlock header rather than a string, so an underscore in a workspace's name is not read as an access key.
    static MenuItem Item(string text, Action? click = null)
    {
        var item = new MenuItem { Header = new TextBlock { Text = text } };
        if (click is not null) item.Click += (_, _) => click();
        return item;
    }

    // --- the add box -------------------------------------------------------------------------------------------------

    void ShowAdd()
    {
        addChosen = null;
        AddRow.Visibility = Visibility.Visible;
        PaintAddPlace();
        AddBox.Focus();
    }

    void HideAdd()
    {
        AddBox.Text = "";
        addChosen = null;
        ResetAddFlags();
        ClearAddPictures();
        AddRow.Visibility = Visibility.Collapsed;
    }

    // --- the add box's place and chips --------------------------------------------------------------------------------
    //
    // A subtask is no longer added here: "Add subtask" opens a box in the task's own thread (TasksWindow.SubtaskAdd.cs,
    // task 0eb943d8, Petre: "a new textbox appears at the top of the bar; make it right there, at the add subtask
    // button"). The add box at the top adds plain notes only.

    const string NotePlaceholder = "A note: Enter asks where, then adds it; Esc closes the box";

    internal string AddPlaceholderText => AddPlaceholder.Text;

    // The toggles and chips, then where they go: beside the box or under it (FitAddEnd).
    void PaintAddPlace()
    {
        PaintAddChips();
        FitAddEnd();
    }

    // ROOM TO TYPE (task f212d886, "adding a subtask is impossible", Petre's screenshot). The toggles, the parent chip
    // (up to 220 DIP of title) and the workspace chip used to be docked right of the box on one line, always. The box
    // is the DockPanel's fill, so it got only what they left, and on a narrow panel that was nothing: measured 0 DIP
    // at a 420 DIP panel and 125 DIP at 560, with a long parent title. The caret had nowhere to go, so a subtask could
    // not be typed at all. Now the chips stay on the box's line only while the box keeps MinAddBoxWidth beside them;
    // otherwise they take a line of their own under the box, left-aligned with it, and wrap there if even that line is
    // too narrow. The decision reads the chips' one-line width (measured unconstrained) against the line's width, and
    // neither changes with where the chips are docked, so it cannot flip back and forth.
    const double MinAddBoxWidth = 200;

    void FitAddEnd()
    {
        var room = AddLine.ActualWidth;
        // Not laid out yet (the row was just shown for the first time): AddLine's SizeChanged calls this again once it
        // has a width.
        if (room <= 0) return;
        AddEnd.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        // DesiredSize includes AddEnd's margin, which the previous call set (-4 DIP under the box, 0 beside it). Left in,
        // it made the answer depend on the last answer, and in a 4 DIP band of widths the chips flipped between beside
        // and under on every redraw (review of this fix). The chips' own width is the margin taken back out.
        var chips = AddEnd.DesiredSize.Width - AddEnd.Margin.Left - AddEnd.Margin.Right;
        var below = room - chips < MinAddBoxWidth;
        DockPanel.SetDock(AddEnd, below ? Dock.Bottom : Dock.Right);
        AddEnd.HorizontalAlignment = below ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        // Under the box: a little air above, and the first toggle's 4 DIP left margin taken back so the line starts
        // at the box's left edge.
        AddEnd.Margin = below ? new Thickness(-4, 6, 0, 0) : new Thickness(0);
    }

    // The chip saying where the note goes: the one picked from its menu, else the view's (the filtered workspace, or
    // the workspace in front).
    void PaintAddChips()
    {
        AddEnd.Children.Clear();
        AddPlaceholder.Text = NotePlaceholder;
        // The important and urgent toggles first (Ruling 37, TasksWindow.AddFlags.cs), then the workspace chip.
        AddFlagToggles().ToList().ForEach(toggle => AddEnd.Children.Add(toggle));
        // Then Start Claude (task 3c40dec9), while the controller can start one: AddAndStartClaudeButton.
        // The camera (task 494d34ee) before it, beside the toggles as Petre asked: AddFromWindowButton.
        if (AddFromWindowButton() is { } fromWindow) AddEnd.Children.Add(fromWindow);
        if (AddAndStartClaudeButton() is { } start) AddEnd.Children.Add(start);
        var target = addChosen ?? latest?.AddTarget;
        if (target is null) return;
        // DROPDOWN THAT DID NOT OPEN (2026-11-23, Petre: "dropdown doesn't work"). The menu used to open from a second
        // Click handler added after Flat's, and Flat's own handler marks the click handled, so WPF never called the
        // second one: a handler added with += is skipped for an event already handled, even on the same element. The
        // menu now opens from Flat's press itself.
        // The chip is the workspace's name and the arrow, no "on" before it, as on the bar's add row (task 47398c0f,
        // Petre: "drop on; make it compact").
        addChip = Flat(new TextBlock { Text = $"{target.Name} ▾", Foreground = Ink, FontSize = 11 }, "Add to workspace",
            () => OpenAddPlaceMenu(target, then: null), new Thickness(8, 2, 8, 2));
        addChip.Margin = new Thickness(8, 0, 0, 0);
        AddEnd.Children.Add(addChip);
    }

    // The add box's workspace chip as last painted: where its menu opens, from its own press or from Enter.
    Button? addChip;

    // The add chip's menu (WorkspaceChipMenu), opened on `target`. A pick becomes the chip's choice (addChosen) and then
    // runs `then`, Enter's add (task 6184e8bf), or with none just gives the box the keyboard back. A menu closed without
    // a pick (Esc, a click away) gives the box the keyboard back too, its text untouched. Closed comes late, from the
    // popup's destroy timer (up to 150 ms with the menu fade on), so after a pick it finds `picked` set; and it leaves
    // the keyboard alone while a newer menu is open (Esc then a quick Enter), whose type-ahead would otherwise type
    // into the note.
    ContextMenu OpenAddPlaceMenu(TaskPlaceChoice target, Action? then)
    {
        var places = latest?.Places ?? [];
        var picked = false;
        var menu = WorkspaceChipMenu.Build(places, target.Workspace,
            workspace =>
            {
                picked = true;
                AddPickMenu = null;
                addChosen = places.Where(p => !p.IsGroup && p.Workspace.Equals(workspace))
                    .Select(p => new TaskPlaceChoice(p.Workspace, p.Name, p.Colour))
                    .FirstOrDefault();
                PaintAddPlace();
                if (then is null) AddBox.Focus();
                else then();
            }, addChip);
        menu.Closed += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (AddPickMenu == menu) AddPickMenu = null;
            if (!picked && AddBoxShown && AddPickMenu is null && openMenu is null) AddBox.Focus();
        }, System.Windows.Threading.DispatcherPriority.Input);
        OpenMenu(menu);
        return menu;
    }

    /// <summary>The workspace menu Enter opened and nothing has been picked from yet, for the tests.</summary>
    internal ContextMenu? AddPickMenu { get; private set; }

    KeyOutcome PressInAdd(Key key, ModifierKeys modifiers, bool isRepeat)
    {
        // Shift+Enter is a line break, which the box makes itself (AcceptsReturn), as in the comment boxes.
        if (key is not (Key.Enter or Key.Return) || modifiers.HasFlag(ModifierKeys.Shift)) return KeyOutcome.Ignored;
        // A held Enter repeats; only the first press counts, so it cannot also pick from the menu it opened.
        if (isRepeat) return KeyOutcome.NewLine;
        // Ctrl+Enter is the Start Claude button's key (task 3c40dec9).
        var startClaude = modifiers.HasFlag(ModifierKeys.Control);
        // THE WORKSPACE ASKED AT ENTER (task 6184e8bf; Petre: "when i press enter, open a workspace selection which i
        // can select with the keyboard so that i don't accidentally create a task in the wrong workspace"). With
        // something to add, Enter opens the chip's menu with the keyboard on where the note would go now; a second
        // Enter takes it, the arrows or a name's first letters move to another, and the pick adds the note there (with
        // Claude started when this press was Ctrl+Enter). Esc adds nothing and leaves the text. The bar's add box does
        // the same (FloatingBar.AskTaskTarget). An empty box is refused as before, with no menu.
        if ((AddBox.Text.Trim().Length > 0 || AddBoxImages.Count > 0) && (addChosen ?? latest?.AddTarget) is { } target)
        {
            AddPickMenu = OpenAddPlaceMenu(target, () =>
            {
                addPickAdded = AddFromBox(startClaude);
                if (!addPickAdded && AddBoxShown) AddBox.Focus();
            });
            return KeyOutcome.Posted;
        }
        return AddFromBox(startClaude) ? KeyOutcome.Posted : KeyOutcome.NewLine;
    }

    // Whether the last pick from Enter's menu added its note, for the tests (TypeNote).
    bool addPickAdded;

    // The box's note added, where the chip says with the flags the toggles say, and the box closed; whether it was added.
    // With `startClaude` (the Start Claude button or Ctrl+Enter) the note is then handed to Claude through the
    // controller's AddNoteAndStartClaude; with none wired, it is added as Enter adds it. A `shot` (the camera, task
    // 494d34ee) is one more picture after the pending ones, with the line naming its window under the typed lines.
    bool AddFromBox(bool startClaude, WindowShot.Shot? shot = null)
    {
        var target = addChosen ?? latest?.AddTarget;
        // The first line is the title and the lines after it the description (TaskNote.Split, task 98420dbc). Pasted
        // pictures (TasksWindow.Images.cs, "the add box") follow that text in the description, and with no words typed
        // the task is called "Picture".
        var (title, body) = TaskNote.Split(AddBox.Text, AddBoxImages.Count + (shot is null ? 0 : 1));
        var describe = AddDescription(body, shot);
        var add = startClaude && Actions?.AddNoteAndStartClaude is { } andStart ? andStart : Actions?.AddNote;
        var added = add is not null && target is not null && add(title, target.Workspace, AddQuadrant, describe);
        if (!added) return false;
        HideAdd();
        return true;
    }

    // ADD AND START CLAUDE (task 3c40dec9; Petre: "when adding a task, have a button to start immediately with claude").
    // After the flag toggles, before the workspace chip: adds the box's note as Enter does, then runs "Start Claude on
    // this" on it (TasksBoard.AddNoteAndStartClaude), which starts in the workspace's one folder or opens the folder pick
    // list. Ctrl+Enter in the box is the same. The glyph is the line's own Start Claude button's, Segoe's "Play" (U+E768),
    // so the two read as one action. Unfocusable (Flat), so a refused add leaves the keyboard in the box. None while the
    // controller cannot start Claude, as the line's button.
    internal const string AddAndStartClaudeText = "Add and start Claude";

    Button? AddAndStartClaudeButton()
    {
        if (Actions?.AddNoteAndStartClaude is null) return null;
        var button = Flat(new TextBlock { Text = "", FontFamily = IconFont, FontSize = 12, Foreground = DimInk },
            AddAndStartClaudeText, () =>
            {
                if (!AddFromBox(startClaude: true) && AddBoxShown) AddBox.Focus();
            }, new Thickness(5, 2, 5, 2));
        button.Background = Brushes.Transparent;
        button.ToolTip = "Add and start Claude (Ctrl+Enter): adds the task and starts a Claude session on it";
        button.Margin = new Thickness(4, 0, 0, 0);
        return button;
    }

    // A TASK FROM THE WINDOW ON SCREEN (task 494d34ee; Petre: "add a task from the window open on the screen as a separate
    // button near the urgent/important buttons, to start immediately in the current workspace"). The camera after the
    // flag toggles: a picture of the window Petre was in before he clicked Deskbar (the controller's ShootWindow, which
    // asks the bar's foreground hook, WindowShot.cs has the rules), added with the box's words, if any, as the play button
    // adds a note and then Claude started on it. No window to take is said on the problem line and nothing is added. None
    // while the controller cannot shoot or start Claude. Segoe's "Camera" (U+E722), as the bar's camera.
    internal const string AddFromWindowText = "Add from this window and start Claude";

    Button? AddFromWindowButton()
    {
        if (Actions?.ShootWindow is null || Actions.AddNoteAndStartClaude is null) return null;
        var button = Flat(new TextBlock { Text = "\uE722", FontFamily = IconFont, FontSize = 12, Foreground = DimInk },
            AddFromWindowText, AddFromWindow, new Thickness(5, 2, 5, 2));
        button.Background = Brushes.Transparent;
        button.ToolTip = "Add from this window: takes a picture of the window you were in, adds it as a task and starts Claude on it";
        button.Margin = new Thickness(4, 0, 0, 0);
        return button;
    }

    // The press: the picture, then the add that starts Claude. Internal for the tests.
    internal void AddFromWindow()
    {
        if (Actions?.ShootWindow is not { } shoot) return;
        shoot().Match(
            shot =>
            {
                ShowProblem(null);
                if (!AddFromBox(startClaude: true, shot) && AddBoxShown) AddBox.Focus();
            },
            ShowProblem);
    }

    // For the tests: types into the add box and presses a key.
    // Enter asks for the workspace (task 6184e8bf), so this presses Enter a second time as Petre would: the menu's
    // pick of the chip it opened on, and the answer is whether that added the note. Tests that need the menu itself
    // press with PressNote.
    internal bool TypeNote(string text, Key key, ModifierKeys modifiers = ModifierKeys.None)
    {
        addPickAdded = false;
        if (!PressNote(text, key, modifiers)) return false;
        if (AddPickMenu is not { } menu) return true;
        WorkspaceChipMenu.StartChip(menu).Tap(chip => chip.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)));
        return addPickAdded;
    }

    // For the tests: types into the add box and presses a key, and no more; whether the press was taken as an add,
    // which with a note in the box means Enter's workspace menu is open (AddPickMenu).
    internal bool PressNote(string text, Key key, ModifierKeys modifiers = ModifierKeys.None, bool isRepeat = false)
    {
        AddBox.Text = text;
        return PressInAdd(key, modifiers, isRepeat) == KeyOutcome.Posted;
    }

    /// <summary>
    /// A key pressed anywhere in the panel (its PreviewKeyDown), answering whether it was taken: Esc (Escape), F2 (edit the
    /// selected line) and Ctrl+F (the search box). Internal so the tests can press Ctrl+F, whose modifier a synthetic key
    /// event cannot carry.
    /// </summary>
    internal bool Press(Key key, ModifierKeys modifiers) =>
        (key == Key.Escape && Escape())
        || (key == Key.F2 && EditSelected())
        || (key == Key.F && modifiers == ModifierKeys.Control && FocusSearch());

    // Ctrl+F (Task 7): the keyboard to the search box, its text selected so typing replaces it, as a browser's find does.
    // Not while a rename or a comment edit is open: those hold every redraw (MustWait), so a search typed then would seem
    // dead until the edit ended, and the edit's box takes the keyboard back on the next draw anyway. A TextBox does
    // nothing of its own with Ctrl+F, so leaving it unhandled there costs nothing.
    bool FocusSearch()
    {
        if (renaming is not null || editingComment is not null) return false;
        ShowInboxSearch();
        SearchBox.Focus();
        SearchBox.SelectAll();
        return true;
    }

    // Esc undoes the innermost thing: in the search box, the search (Task 7: emptied, and the keyboard back to the board);
    // else a rename, else the add box. It does not close the panel: the × and Alt+F4 do.
    bool Escape()
    {
        if (SearchBox.IsKeyboardFocusWithin) EndSearch();
        else if (renaming is not null) EndRename();
        else if (editingComment is not null) EndCommentEdit();
        // A comment box's skill list closes before anything else does (task 29bdd158): the window's Esc runs ahead of the
        // box's, and with the add box also shown it would otherwise hide that box and leave the list open.
        else if (commentSkills.Values.Any(skills => skills.Dismiss())) { }
        // The subtask box in a thread (TasksWindow.SubtaskAdd.cs) closes first: before the add box, and before the thread
        // it sits in, which the next Esc folds.
        else if (subtaskUnder is not null) EndSubtaskAdd();
        // The add box's skill list closes before the box does (task 3a872301): the window's Esc runs ahead of the box's.
        else if (addSkills.Dismiss()) { }
        else if (AddBoxShown) HideAdd();
        // An Inbox page: back, to its parent's page or the list (TasksWindow.InboxPage.cs, task 740a2231).
        else if (InboxBack()) { }
        // RULING 63: Esc folds the selected line's open thread, the last thing it undoes.
        // Not while a comment box has the keyboard: there Esc belongs to the box (review, M5).
        else if (selectedId.HasValue && authors.ContainsKey(selectedId.Value) && !(EscapeIgnoresThreadWhile?.Invoke() ?? commentBoxes.Values.Any(box => box.IsKeyboardFocusWithin)))
            Actions?.ToggleThread(selectedId.Value);
        else return false;
        return true;
    }

    // Esc in the search box: emptied (its TextChanged hands "" to the controller, which shows everything again), then the
    // keyboard back on the board, as when the panel opens (FocusBoard). The window has the keyboard already, so focusing
    // the toolbar activates nothing.
    void EndSearch()
    {
        ClearSearch();
        FocusBoard();
    }

    /// <summary>
    /// The search box emptied, the keyboard left where it is: Esc's half of EndSearch, and the controller's when a task
    /// clicked on the bar is hidden by the search (TasksBoard.RevealPastSearch). Its TextChanged tells the controller.
    /// </summary>
    internal void ClearSearch() => SearchBox.Text = "";

    /// <summary>
    /// The clear button (×) at the right end of the search box, shown only while the box has text (its TextChanged above).
    /// Internal so the tests can click it.
    /// </summary>
    internal Button SearchClear { get; private set; } = null!;

    // The search box's clear button, the toolbar's × in small (same flat button, transparent until hovered). A click
    // empties the box, whose TextChanged tells the controller and hides the button again, and leaves the keyboard IN the
    // box, as a browser's search field does: the usual reason to clear a search is to type another. Esc stays the way to
    // clear and hand the keyboard back to the board (EndSearch). Not Focusable (Flat), so the press itself never takes
    // the keyboard out of the box.
    void BuildSearchClear()
    {
        SearchClear = Flat(new TextBlock { Text = "×", Foreground = Ink, FontSize = 13 }, "Clear the search", () =>
        {
            ClearSearch();
            SearchBox.Focus();
        }, new Thickness(5, 0, 5, 1));
        SearchClear.Background = Brushes.Transparent;
        SearchClear.Margin = new Thickness(4, -2, -3, -2);
        SearchClear.ToolTip = "Clear the search";
        SearchClear.Visibility = Visibility.Collapsed;
        Grid.SetColumn(SearchClear, 1);
        SearchField.Children.Add(SearchClear);
    }

    // --- small parts -------------------------------------------------------------------------------------------------

    // A checkbox's look inside a flat chip: the icon font's empty box (U+E739 "Checkbox") or ticked box (U+E73A
    // "CheckboxComposite"), both in Segoe Fluent Icons and Segoe MDL2 Assets, then the words. Ticked, the box is the
    // picked chips' blue.
    static UIElement CheckContent(bool on, string text)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(new TextBlock
        {
            Text = on ? "" : "", FontFamily = IconFont, FontSize = 12, Foreground = on ? PickedEdge : DimInk,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0),
        });
        line.Children.Add(new TextBlock { Text = text, Foreground = Ink, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        return line;
    }

    // A flat button with a name for automation and the tests. Unfocusable, so a click never takes the keyboard from a
    // box Petre is typing in. `ignoredWhen` is the click guard it takes (ClickIgnored on a line's buttons,
    // DoubleClickIgnored on Undo, none on the toolbar and the comment box's button, under which nothing moves); its press
    // count is noted on the press, and the click is still marked handled when the guard drops it, so a dropped click
    // never turns into a click on the line around it.
    Button Flat(UIElement content, string name, Action press, Thickness padding, Func<bool>? ignoredWhen = null)
    {
        var button = new Button
        {
            Content = content,
            Template = (ControlTemplate)FindResource("FlatButton"),
            Background = ButtonFill,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Padding = padding,
            Focusable = false,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(button, name);
        if (ignoredWhen is not null) button.PreviewMouseLeftButtonDown += (_, e) => pressClicks = e.ClickCount;
        button.Click += (_, e) =>
        {
            // Inside a line, a small button's click must not also go to the line.
            e.Handled = true;
            if (ignoredWhen?.Invoke() == true) return;
            press();
        };
        return button;
    }

    // A line's small button, guarded by the click guard (ClickIgnored) unless told otherwise (Undo).
    Button Small(string text, string name, Action press, string tip, Func<bool>? ignoredWhen = null) =>
        Small(new TextBlock { Text = text, Foreground = Ink, FontSize = 11 }, name, press, tip, ignoredWhen);

    Button Small(UIElement content, string name, Action press, string tip, Func<bool>? ignoredWhen = null)
    {
        var button = Flat(content, name, press, new Thickness(4, 0, 4, 1), ignoredWhen ?? ClickIgnored);
        button.Background = Brushes.Transparent;
        button.ToolTip = tip;
        button.HorizontalAlignment = HorizontalAlignment.Left;
        return button;
    }

    static Brush Faded(Brush? accent) =>
        accent is SolidColorBrush { Color: var colour } ? Frozen(0x66, colour.R, colour.G, colour.B) : ButtonFill;

    static Brush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
