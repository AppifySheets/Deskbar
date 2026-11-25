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
//   Claude working   a chip with the status chips, "Claude working N", orange-edged (Orange) on or off, filled with
//                    AskTint while on (the approved mockup; never the blue selected edge). N is what it would show
//                    under every other filter (TasksView.ClaudeWorkingCount).
//   The width        (tasks panel plan, Task 8; spec "Lines at panel width" and "The Matrix") below 710 DIP the lines are
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
    // The mockup's palette. Frozen, as every static WPF brush must be (CLAUDE.md, Traps).
    static readonly Brush Ink = Frozen(0xFF, 0xE6, 0xE6, 0xE6);
    static readonly Brush DimInk = Frozen(0xFF, 0xA9, 0xAD, 0xB8);
    static readonly Brush HeaderInk = Frozen(0xFF, 0x8B, 0x8F, 0x99);
    static readonly Brush Orange = Frozen(0xFF, FloatingBar.AskingOrange.R, FloatingBar.AskingOrange.G, FloatingBar.AskingOrange.B);
    static readonly Brush AskTint = Frozen(0xFF, 0x3A, 0x2C, 0x1C);
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
    readonly Dictionary<Guid, IReadOnlyList<string>> authors = [];
    readonly Dictionary<Guid, IReadOnlyList<Guid>> shownComments = [];
    readonly Dictionary<Guid, bool> closing = [];
    readonly Dictionary<Guid, string> drafts = [];

    readonly IDisposable ageWatch;

    // The workspace chosen in the add box, or none chosen yet (the view's AddTarget then).
    TaskPlaceChoice? addChosen;

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
        AddBox.PreviewKeyDown += (_, e) => e.Handled = PressInAdd(e.Key) != KeyOutcome.Ignored;
        // Ctrl+V and drops of pictures on the add box (TasksWindow.Images.cs, "the add box").
        TakeAddPictures();
        // The search box: every keystroke goes to the controller at once, which narrows and redraws (no debounce, see
        // TasksBoard.Search for why). The box is never written by a redraw, only read, so typing keeps its text, caret
        // and keyboard through every redraw ("the search box" below).
        SearchBox.TextChanged += (_, _) =>
        {
            SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Actions?.Search(SearchBox.Text);
        };
        WireDrag();
        WireGrip();
        BuildToolbarEnd();
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
    // The "Claude working" chip's words ("Claude working 2") and whether it is on (Task 7).
    internal string ClaudeWorkingLabel { get; private set; } = "";
    internal bool ClaudeWorkingOn { get; private set; }
    // Whether the "Current workspace" checkbox is ticked, as last drawn (task ed1a01fc), for the tests.
    internal bool CurrentWorkspaceOn { get; private set; }
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
    // absolute path: the default image viewer, through the shell. Both are seams so the tests use a scratch folder and
    // record the click instead of opening a viewer.
    internal string StateDir { get; init; } = AppPaths.NewStateDir;

    internal Action<string> OpenImage { get; init; } = OpenInViewer;

    // A refusal (no viewer registered for PNG) is traced, never thrown: a click in the panel must not take the app down.
    static void OpenInViewer(string path)
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
        // When the lines on show change (which tasks, their order, which threads are open, which are closing), a line
        // may now be under the pointer that was not, so for half a second a click is not trusted (the click guard,
        // ClickIgnored). A redraw that only rewrites what lines say (a session's status line) moves nothing and does
        // not start the half second, or frequent status pulses would eat clicks (review minor).
        var shownRows = view.Matrix ? view.Cells.SelectMany(c => c.Rows).ToArray() : view.Groups.SelectMany(g => g.Rows).ToArray();
        // Which comments a thread shows, and what is unfolded in it, are part of where things sit (review of the assignee batch,
        // M4): an earlier-comments line unfolding or a long comment opening moves every line below, so it starts the half second
        // too.
        var placesKey = string.Join('|', shownRows.Select(r => $"{r.Id}{r.Expanded}{r.Closing}{ThreadLayoutKey(r)}").Prepend($"{view.Matrix}"));
        if (placesKey != linesPlaces)
        {
            linesPlaces = placesKey;
            linesChangedAt = ClickClock();
        }
        DrawFilters(view);
        Rows.Children.Clear();
        menuButtons.Clear();
        dragLines.Clear();
        commentBoxes.Clear();
        ages.Clear();
        slimTips.Clear();
        selectionFrames.Clear();
        authors.Clear();
        shownComments.Clear();
        closing.Clear();
        // The List or the Matrix (TasksWindow.Matrix.cs), whichever the switch says; the other is emptied and hidden.
        matrixShown = view.Matrix;
        Scroller.Visibility = view.Matrix ? Visibility.Collapsed : Visibility.Visible;
        MatrixGrid.Visibility = view.Matrix ? Visibility.Visible : Visibility.Collapsed;
        MatrixGrid.Children.Clear();
        // Slim lines on a narrow panel (Task 8, see "Lines at panel width" below Line). The Matrix's compact lines are
        // their own, and the Matrix never shows on a panel that narrow anyway. The squares' column is as wide as the line
        // with the most squares needs, the same on every line so the chip after it sits at one x.
        slimLines = view.Slim && !view.Matrix;
        slimSquaresWidth = SquaresColumn(shownRows.Select(r => r.SessionCount).DefaultIfEmpty(0).Max());
        if (view.Matrix) DrawMatrix(view);
        else
            view.Groups.ToList().ForEach(group =>
            {
                Rows.Children.Add(Header(group));
                group.Rows.ToList().ForEach(row =>
                {
                    closing[row.Id] = row.Closing;
                    Rows.Children.Add(Line(row, view));
                    if (row.Expanded) Rows.Children.Add(Thread(row));
                });
            });
        Headers = view.Matrix ? view.Cells.Select(c => c.Header).ToArray() : view.Groups.Select(g => g.Header).ToArray();
        Keys = shownRows.Select(r => r.Id).ToArray();
        EmptyText.Text = view.EmptyText;
        // The Matrix always draws its four cells, empty or not, so it needs no empty line.
        EmptyText.Visibility = Keys.Count == 0 && !view.Matrix ? Visibility.Visible : Visibility.Collapsed;
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
            // The "Claude working" chip (Task 7): on or off, and its count. NOT the search text: the box is not drawn from
            // the view, and the lines it narrows are below, so a keystroke that changes no line redraws nothing.
            $"{view.ClaudeWorking}{view.ClaudeWorkingCount}",
            string.Join('|', view.Places.Select(p => $"{p.IsGroup}{p.Workspace}{p.Name}{(p.Colour as SolidColorBrush)?.Color}")),
            view.EmptyText,
            $"{renaming}{editingComment}{string.Join(",", settledOpen.Order())}|{string.Join(",", earlierOpen.Order())}|{string.Join(",", longOpen.Order())}",
            // The two width flags (Task 8): which line is drawn and whether the view switch shows. Not the width itself, so
            // a grip drag that crosses neither threshold redraws nothing.
            $"{view.Matrix}{view.MatrixAllowed}{view.Slim}",
            // A List row's WorkingCount is in its line below (Task 8): a new agents list that only starts or stops Claude
            // working changes the squares' halos and nothing else, and must still redraw the line. Not in a Matrix cell's:
            // a compact line draws no sessions, so it would only rebuild the Matrix for nothing (review minor 2).
            string.Join('\u0002', view.Cells.Select(c => $"{c.Header}:" + string.Join('\u0003', c.Rows.Select(r =>
                $"{r.Id}|{r.Title}|{r.Detail}|{r.Description}|{r.Status}|{r.Claudes}|{r.Assignee.Id}|{PlaceSignature(r.Place)}|{r.CommentCount}|{r.Expanded}|{r.Closing}|{r.Important}|{r.Urgent}|{r.ParentTitle}|{r.SubtaskCount}|{r.ClaudeWorking}|" +
                string.Join('\u0004', r.Comments.Select(c => $"{c.Id}{c.Author}{c.When}{c.Markdown}{c.Settled}")))))),
            string.Join('\u0002', view.Groups.Select(g => $"{g.Header}:" + string.Join('\u0003', g.Rows.Select(r =>
                $"{r.Id}|{r.Title}|{r.Detail}|{r.Description}|{r.Status}|{r.Claudes}|{r.Assignee.Id}|{r.Sessions}|{r.SessionCount}|{r.LiveCount}|{PlaceSignature(r.Place)}|{r.CommentCount}|" +
                $"{r.Expanded}|{r.Closing}|{r.Important}|{r.Urgent}|{r.WorkingCount}|{r.ParentTitle}|{r.SubtaskCount}|{r.ClaudeWorking}|{r.Nested}|" +
                string.Join('\u0004', r.Subtasks.Select(c => $"{c.Id}{c.Title}{c.Status}{c.StatusLine}")) + "|" + string.Join('\u0004', r.SessionList.Select(s => $"{s.Id}{s.Joined}{s.State}")) + "|" +
                string.Join('\u0004', r.Comments.Select(c => $"{c.Id}{c.Author}{c.When}{c.Markdown}{c.Settled}")))))));

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
            button.BorderBrush = chip.On ? PickedEdge : Brushes.Transparent;
            button.Margin = new Thickness(0, 0, 6, ChipRowGap);
            Filters.Children.Add(button);
        });
        // "Claude working N" (Task 7, see the header), with the status chips; the WrapPanel lets it wrap on a narrow panel.
        // A click flips it. As the approved mockup draws it: the orange edge ALWAYS (it is the Claude chip, on or off), and
        // while on the AskTint fill under it, never the other chips' blue selected edge, which would lose the orange.
        var working = Flat(new TextBlock { Text = $"Claude working {view.ClaudeWorkingCount}", Foreground = Ink, FontSize = 11 },
            "Filter: Claude working", () => Actions?.FilterClaudeWorking(!view.ClaudeWorking), new Thickness(8, 2, 8, 2));
        working.BorderBrush = Orange;
        working.Background = view.ClaudeWorking ? AskTint : ButtonFill;
        working.Margin = new Thickness(0, 0, 6, ChipRowGap);
        working.ToolTip = "Only the tasks Claude has a live session on";
        Filters.Children.Add(working);
        ClaudeWorkingLabel = $"Claude working {view.ClaudeWorkingCount}";
        ClaudeWorkingOn = view.ClaudeWorking;
        // The menu opens from Flat's own press, never from a Click handler added afterwards: Flat's handler marks the
        // click handled, and WPF does not call a later += handler on a handled event, so such a menu never opened
        // (2026-11-23, "dropdown doesn't work"; TasksBoardTests.The_add_rows_workspace_chip_opens_its_menu_and_a_pick_lands).
        // "Current workspace", a checkbox beside the workspace filter (task ed1a01fc; Petre, after the first cut put it in
        // the filter's menu: "let's just do a checkbox, it would be easier to tick/untick"). Ticked, the list follows the
        // workspace in front (TaskWorkspaceFilter.Current); unticked, it shows every workspace. Picking a workspace in the
        // menu beside it unticks it, since the filter then names a workspace of its own. It is drawn as a toolbar chip with
        // the icon font's box, not a WPF CheckBox: the toolbar is always the dark page, and under ThemeMode="System" a
        // light Windows theme would draw a stock CheckBox in light colours (the workspace menu's lesson, ruling g).
        var following = view.Where.FollowsCurrent;
        var current = Flat(CheckContent(following, "Current workspace"), "Current workspace",
            () => Actions?.FilterWorkspace(following ? TaskWorkspaceFilter.All : TaskWorkspaceFilter.Current), new Thickness(6, 2, 8, 2));
        current.BorderBrush = following ? PickedEdge : Brushes.Transparent;
        current.Margin = new Thickness(0, 0, 6, ChipRowGap);
        current.ToolTip = "Only the tasks of the workspace you are on, following you when you switch desktops";
        // A screen reader hears it as a checkbox's state, the same word the tests read.
        AutomationProperties.SetItemStatus(current, following ? "checked" : "unchecked");
        Filters.Children.Add(current);
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

    // The view switch, "+ Add" and the panel's ×, made once. The × is the panel's close (spec "The panel window": its
    // toolbar gains a close button at the right end, after "+ Add"), since a panel has no title bar to close it from.
    void BuildToolbarEnd()
    {
        listSwitch = Flat(new TextBlock { Text = "List", Foreground = Ink, FontSize = 11 }, "List", () => Actions?.ShowMatrix(false), new Thickness(8, 2, 8, 2));
        listSwitch.ToolTip = "The tasks grouped, one group under another";
        matrixSwitch = Flat(new TextBlock { Text = "Matrix", Foreground = Ink, FontSize = 11 }, "Matrix", () => Actions?.ShowMatrix(true), new Thickness(8, 2, 8, 2));
        matrixSwitch.ToolTip = "Covey's matrix: urgent and not urgent across, important and not important down";
        matrixSwitch.Margin = new Thickness(4, 0, 10, 0);
        ToolbarEnd.Children.Add(listSwitch);
        ToolbarEnd.Children.Add(matrixSwitch);
        var add = Flat(new TextBlock { Text = "+ Add", Foreground = Ink, FontSize = 11 }, "Add", ShowAdd, new Thickness(10, 2, 10, 2));
        add.ToolTip = "Write a note";
        ToolbarEnd.Children.Add(add);
        var close = Flat(new TextBlock { Text = "×", Foreground = Ink, FontSize = 13 }, "Close the panel", () => Actions?.ClosePanel(), new Thickness(7, 0, 7, 1));
        close.Background = Brushes.Transparent;
        close.Margin = new Thickness(6, 0, 0, 0);
        close.ToolTip = "Close the tasks panel";
        ToolbarEnd.Children.Add(close);
    }

    UIElement Header(TaskGroupView group) => new TextBlock
    {
        Tag = "task-group",
        Text = group.Header.ToUpperInvariant(),
        FontSize = 10,
        Foreground = group.Kind == TaskGroupKind.WaitingOnYou ? Orange : HeaderInk,
        Margin = new Thickness(12, 6, 12, 1),
    };

    // One line: a button across the window, so a click anywhere on it goes to the task; the small buttons inside it
    // mark their Click handled, so the line does not also act on them.
    Button Line(TaskRowView row, TasksView view)
    {
        // ONE COLUMN LAYOUT FOR EVERY LINE (Petre's screenshot: each line sized a column to its own content, so the chip,
        // the comments and "…" sat at a different x on every line, and a long title pushed the chip off the window).
        // Every column but the title's is a fixed width, the same on every line of every group, so each sits at the
        // same x, flush to the right edge; the title takes the star column and trims first.
        var grid = new Grid();
        (compactLines ? CompactColumns : slimLines ? SlimColumns(slimSquaresWidth) : LineColumns).ToList()
            .ForEach(width => grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width }));
        // Only the full line has the session words, the assignee and the Reopen button. The age is on the slim line too.
        var full = !compactLines && !slimLines;
        Cell(grid, 0, LeftOf(row));
        // TWO ROWS: THE TITLE WITH EVERY CONTROL BESIDE IT, THEN WHAT IS UNDER THE TITLE ACROSS THE LINE (task d560f3eb,
        // "misaligned text", Petre's screenshot of a waiting task whose question stood in a column a few letters wide, two
        // or three letters to a line, with the tick, the toggles and the chip floating half way down it). The question,
        // the status line and the description used to sit under the title in the title's own column, which is what the
        // fixed columns leave over: on a slim line 286 DIP of them, so a 360 DIP panel left that column 29 DIP and a
        // question in it stood 629 DIP tall; 129 DIP at the default 460 (measured, TasksBoardTests
        // A_question_spans_the_line_under_its_title). Now the first row holds the title and the controls, centred on it as
        // before, and the second row holds what is under the title, from the title's left edge to the line's right end,
        // under the toggles, the age and the chip, which have nothing below them. The title itself stays in its column.
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var text = TextOf(row);
        Cell(grid, 1, text.Title);
        if (text.Below is { } below)
        {
            Grid.SetRow(below, 1);
            Grid.SetColumnSpan(below, grid.ColumnDefinitions.Count - 1);
            Cell(grid, 1, below);
        }
        var flags = FlagsOf(row);
        Cell(grid, 2, flags);
        // A Matrix line (compact) has no column for the sessions, the age or the close button, so they are not made at
        // all (whole-PR review, minor 5): no element in a zero-width column, and no age for the minute's tick to rewrite.
        // The status in words (Petre, 2026-11-23: "i can't currently see statuses"; TaskStatusMarks.Chip), in its own
        // column so it sits at the same x on every line. Only on a full line: none in a Matrix line (no room there; its
        // Done tick still shows) and none on a slim one (the tick still shows, and the status is named in its tooltip,
        // SlimTip), and none on a line in its undo window (it is closed).
        Cell(grid, 3, full && !row.Closing ? TaskStatusMarks.Chip(row.Status) : null);
        // A slim line has the sessions as squares alone, and no assignee or Reopen (Task 8): those are in its tooltip
        // and its "⋯" menu (SlimTip, OpenMenuFor).
        Cell(grid, 4, compactLines ? null : slimLines ? SquaresOf(row) : SessionsOf(row));
        // Who the task is on (Ruling 57): "Me" or "Claude", dim; none in a Matrix line (no column there).
        Cell(grid, 5, full ? AssigneeOf(row) : null);
        // THE AGE ON EVERY LIST LINE, THE SLIM ONE INCLUDED (Petre, 2026-11-24, of the bar's "26m": "move the time from
        // here to the proper tasks window"). The bar's lines lost theirs that day, so the panel at its usual slim width
        // must show it, not only its tooltip. None in a Matrix line (no column there).
        Cell(grid, 6, compactLines ? null : AgeOf(row));
        Cell(grid, 7, Chip(row.Place));
        var comments = CommentsButton(row);
        Cell(grid, 8, comments);
        Cell(grid, 9, full ? ActionOf(row) : null);
        var more = Small(new TextBlock { Text = "", FontFamily = IconFont, FontSize = 12, Foreground = Ink }, "More",
            () => OpenMenuFor(row.Id), "Edit, status, move, delete");
        menuButtons[row.Id] = more;
        Cell(grid, 10, more);
        if (row.Closing)
            grid.Children.OfType<UIElement>().Where(part => Grid.GetColumn(part) != 0).ToList().ForEach(part => part.Opacity = 0.45);
        // The comments and "…" buttons are quiet until the pointer is on their line, like the bar's quiet controls.
        // A line in its undo window keeps its own dimming (above): its quiet buttons are not brightened by a hover or a
        // selection (review minor).
        var quiet = row.Closing ? Array.Empty<UIElement>()
            : new UIElement[] { comments, more }.Concat(flags?.Children.OfType<UIElement>() ?? []).ToArray();
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
            Margin = new Thickness(compactLines ? 0 : TileInset, TileGap, compactLines ? 0 : TileInset, 0),
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
        AutomationProperties.SetName(line, row.Title);
        line.MouseEnter += (_, _) => Quieten(quiet, loud: true);
        line.MouseLeave += (_, _) => Quieten(quiet, loud: Picked(row.Id));
        // The click guard, as on the line's buttons (ClickIgnored); and the press that may become a drag of the line
        // (TasksWindow.Drag.cs), armed only, so a press released in place stays this Click.
        line.PreviewMouseLeftButtonDown += (_, e) =>
        {
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
            DragPress(row.Id, DragMouse.Position(e, DragSurface));
        };
        // A subtask drawn under its parent is dragged (out, to free it) but is no drop target (review of #467, finding 2).
        dragLines[row.Id] = (row.Quadrant, row.Status == TaskStatus.WaitingOnYou && !row.Closing, row.Closing || row.Status == TaskStatus.Closed, row.Nested);
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
            // A single click picks the task and opens its thread; it goes nowhere (Ruling 54).
            Actions?.Pick(row.Id);
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


    // The line's columns: tick, title (the star, trimming first), the two flag toggles, status chip (72 DIP, "In progress"
    // fits), sessions, assignee (46 DIP, "Claude" fits), age, workspace chip,
    // comments, action (close or Reopen), "…". Fixed widths, shared by every line, so each column sits at the same x on
    // every line. The sessions column is wide enough for "2 sessions, 1 live" (116 DIP, so the words never spill into the
    // age column; it is empty on a note), the age is left-aligned in its own so its x never follows the text length, and this
    // line is drawn only from 710 DIP (TasksPanelLayout.SlimBelow: the old floating window's minimum, 620 until the status
    // chip column took 72, 692 until the snooze moon took 18; narrower, the slim line below), so the title keeps a usable
    // width even with the list's scroll bar showing (review I-3). The panel itself goes down to 360 DIP.
    //
    // The toggles' column is 56 DIP since the snooze moon joined the two flags (task 6da8d7eb): three 16 DIP glyph buttons
    // and a little air, the same on the slim line.
    static readonly IReadOnlyList<GridLength> LineColumns =
        [new(22), new(1, GridUnitType.Star), new(FlagsColumn), new(72), new(116), new(46), new(44), new(88), new(40), new(52), new(24)];

    // LINES AT PANEL WIDTH (spec docs/superpowers/specs/2026-11-23-tasks-panel-design.md, "Lines at panel width"; Petre's
    // pick, "slim line when narrow"; plan 2026-11-23-tasks-panel, Task 8). The full line's fixed columns come to 560 DIP
    // (542 before the snooze moon widened the toggles by 18, 470 before the 72 DIP status chip column), more than the
    // panel's default 460, so below TasksPanelLayout.SlimBelow (710 DIP; the view's Slim) a line is slim: tick, title (a
    // Done task's green tick still in front of it), the toggles and the snooze moon, the sessions as squares alone (no
    // words), the age (since 2026-11-24, when it left the bar), the workspace chip, the comment count and "⋯". The status
    // chip and the assignee move into the line's tooltip, with the age again (SlimTip, "Done · 5h · Claude"); Close and
    // Reopen were already in "⋯"
    // (OpenMenuFor), so the Reopen button goes without anything becoming unreachable. Same column indexes as the full
    // line, the dropped ones zero wide and left empty. The switch happens only when the width changes at the grip's drag
    // end, never during a drag (TasksBoard.ResizePanel), and then every line redraws once.
    bool slimLines;
    GridLength slimSquaresWidth = new(0);

    // The toggles' column on a full and a slim line: important, urgent and the snooze moon (see LineColumns).
    const double FlagsColumn = 56;

    static IReadOnlyList<GridLength> SlimColumns(GridLength squares) =>
        [new(22), new(1, GridUnitType.Star), new(FlagsColumn), new(0), squares, new(0), new(SlimAgeColumn), new(88), new(40), new(0), new(24)];

    // The slim line's age column (2026-11-24, the age moved off the bar): "27d23h", the widest TaskAge.Format writes, at
    // the age's 10.5 size is about 34 DIP, plus the 6 DIP left margin AgeOf gives it, so the text never trims.
    const double SlimAgeColumn = 42;

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
    static string LineTip(TaskRowView row) =>
        row.Closing ? "Closed. Undo brings it back." : $"{TaskViews.StatusName(row.Status)}: {row.Title}";

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
        slimTips[row.Id] = (tip, row.SessionCount > SlimSquares ? $"{head}\n{row.Sessions}" : head, TaskViews.StatusName(row.Status),
            row.Created, row.Assignee.Name);
        WriteSlimTip(slimTips[row.Id], Now());
        return tip;
    }

    static void WriteSlimTip((TextBlock Tip, string Head, string Status, DateTimeOffset Created, string Assignee) tip, DateTimeOffset now) =>
        tip.Tip.Text = $"{tip.Head}\n{tip.Status} · {TaskAge.Format(now - tip.Created)} · {tip.Assignee}";

    // THE TWO TOGGLES (Task 12), the bar's (FloatingBar.FlagToggle): important, Segoe Fluent Icons' "Important" U+E8C9,
    // and urgent, its "Recent" clock U+E823 (Segoe MDL2 Assets carries both on Windows 10). On, the glyph wears its
    // colour; off it is dim and quiet: invisible until the line is hovered or selected (Quieten), still clickable.
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
        return panel;
    }

    // THE SNOOZE BUTTON (task 6da8d7eb, Petre: "task should have a snooze button, which will hide it from view for a while";
    // and on the task, "only in the large tasks view", so the bar's lines have none). Third after the two toggles and
    // quiet like an off toggle: invisible until the line is hovered or selected. Segoe's "QuietHours" moon, U+E708, in
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
            ? new ContextMenu { Placement = PlacementMode.MousePoint }
            : new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        SnoozeItems(id).ForEach(item => menu.Items.Add(item));
        OpenMenu(menu);
        return menu;
    }

    // The items both the moon's menu and the "⋯" menu's "Snooze" submenu list. Labels are worked out now, on the local
    // clock, so "Tomorrow, Thu 09:00" names the right day.
    List<object> SnoozeItems(Guid id)
    {
        var row = latest?.Groups.SelectMany(g => g.Rows).FirstOrDefault(r => r.Id == id);
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
        ages.Values.ToList().ForEach(age =>
        {
            age.Text.Text = TaskAge.Format(now - age.Created);
            age.Text.ToolTip = AgeTip(now, age.Created);
        });
        // A slim line's age is in its tooltip (Task 8).
        slimTips.Values.ToList().ForEach(tip => WriteSlimTip(tip, now));
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
    }

    /// <summary>
    /// The glow of a task just added from the panel's add box (TaskGlow), on its line when that line is on show and is the
    /// selected one. The next paint of the selection (PaintFrame) puts the plain selected fill back.
    /// </summary>
    internal void Glow(Guid id)
    {
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
        if (selectedId.HasNoValue || !selectionFrames.TryGetValue(selectedId.Value, out var parts)) return;
        parts.Frame.BringIntoView();
    }

    // For the tests.
    internal bool IsSelected(Guid id) =>
        selectionFrames.TryGetValue(id, out var parts) && parts.Frame.BorderBrush is SolidColorBrush { Color.A: > 0 };

    internal double ScrollOffset => Scroller.VerticalOffset;

    // Quiet parts at half strength until hover or selection; an off flag toggle quieter still, invisible until then
    // (as on the bar), an on one always at full strength.
    static void Quieten(IEnumerable<UIElement> parts, bool loud) => parts.ToList().ForEach(part => part.Opacity =
        (part as FrameworkElement)?.Tag switch
        {
            FlagOnTag => 1,
            FlagOffTag => loud ? 0.55 : 0,
            _ => loud ? 1 : 0.5,
        });

    static void Cell(Grid grid, int column, UIElement? part)
    {
        if (part is null) return;
        Grid.SetColumn(part, column);
        grid.Children.Add(part);
    }

    // The tick box of a note or a Claude task (Ruling 40, and the Done follow-up: all lines look alike), or Undo on a line
    // just closed. A Claude line has no badge here (Ruling 42, Petre: "don't say
    // claude"): the orange edge at the line's start is its mark.
    UIElement LeftOf(TaskRowView row) =>
        row.Closing
            // A sign, not a word (Petre: "undo sign instead of text"), the bar's own (FloatingBar.UndoSign).
            ? Small(FloatingBar.UndoSign(12, Ink), "Undo", () => Actions?.Undo(row.Id), "Undo", DoubleClickIgnored)
            : row.Status == TaskStatus.Closed
                    ? new TextBlock()
                    : Small(new Border
                    {
                        Width = 11,
                        Height = 11,
                        BorderBrush = TickEdge,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(3),
                    // A parent with open subtasks is refused by the board (task 842e1ba4); the tip says so before the
                    // click, and the click's refusal shows on the problem line (TasksBoard.Close reports it).
                    }, "Close", () => Actions?.Close(row.Id), TaskViews.TickTip(row.Claudes, row.SubtaskCount, row.ClaudeWorking));

    // The title (on a line being renamed, the box that renames it), and apart from it what goes under it: the description,
    // its pictures and the detail (the question or the status line). Line puts the two in rows of their own, so what is
    // under the title spans the line rather than the title's column (task d560f3eb, see Line).
    (UIElement Title, UIElement? Below) TextOf(TaskRowView row)
    {
        var panel = new StackPanel { Margin = new Thickness(2, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        var below = new StackPanel { Margin = new Thickness(2, 0, 4, 0) };
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
                TextDecorations = row.Closing ? TextDecorations.Strikethrough : null,
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
            if (row.Status == TaskStatus.Done && !row.Closing)
            {
                var tick = TaskStatusMarks.DoneTick(12);
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
                if (parts.Text.Length > 0) below.Children.Add(DescriptionBlock(row));
                if (TaskImageRow.Of(StateDir, parts.Images, OpenImage) is { } pictures) below.Children.Add(pictures);
            }
        }
        // THE DETAIL: the question while the task waits on Petre, else its status line. Cut to one line on a closed line,
        // IN FULL ON AN OPEN ONE, as the description is: a session's question was readable nowhere in full (Petre,
        // 2026-11-24, of a question cut at "Add an "O…" with its thread open: "where's the question?"). Tagged for tests.
        if (row.Detail.Length > 0) below.Children.Add(DetailText(row));
        return (panel, below.Children.Count > 0 ? below : null);
    }

    // THE DETAIL'S TEXT, its links clickable (task 08cdb052, Petre: "when asking for action, prs should have links, or
    // artifacts; make that clickable"). A session asking for a merge names the pull request as a markdown link,
    // "Merge [Deskbar #490](https://github.com/...): CI is green", or writes the address bare, and both are drawn as a
    // link in the comments' link blue (MarkdownBlocks.InlinesOf), on a closed line as on an open one, since the closed
    // line is where the question is first seen. The rest of the inline markdown (bold, code) reads as it does in a
    // comment, and a line break stays a line break, as it was with the plain text.
    //
    // A press on a link is the link's: a Hyperlink takes the mouse on its press and marks it handled, so the line under
    // it (a Button) never presses and does not pick the task, and the click opens the address in the default browser.
    // A press anywhere else on the text still falls through to the line as before. Only web addresses become links (the
    // MarkdownBlocks header): a session's text never runs a program from a click.
    TextBlock DetailText(TaskRowView row)
    {
        var text = new TextBlock
        {
            Tag = DetailTag,
            FontSize = 11,
            Foreground = row.Status == TaskStatus.WaitingOnYou ? Orange : DimInk,
            FontWeight = row.Status == TaskStatus.WaitingOnYou ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = row.Expanded ? TextTrimming.None : TextTrimming.CharacterEllipsis,
            TextWrapping = row.Expanded ? TextWrapping.Wrap : TextWrapping.NoWrap,
        };
        // Plain text stays the Text it always was: a TextBlock filled with inlines reports an empty Text (measured: the
        // window tests reading a status line through it saw ""), so inlines are used only when there is markdown to draw.
        var inlines = MarkdownBlocks.InlinesOf(row.Detail);
        if (inlines.All(inline => inline is Run { Tag: null } run && run.Text.Length > 0)) text.Text = row.Detail;
        else inlines.ToList().ForEach(text.Inlines.Add);
        return text;
    }

    // THE DESCRIPTION UNDER A TITLE (Ruling 58): Petre's markdown, rendered as a comment's is (MarkdownBlocks), dim, a
    // block of its own under the title. A read-only box that takes no hits and no focus, so a click on it falls through to
    // the line (selecting it) and a link in it is not clickable here; the full text is a click away in Edit.
    RichTextBox DescriptionBlock(TaskRowView row)
    {
        // The text alone: its pictures are the row under it (TaskImageRow), never a path shown as text.
        var document = MarkdownBlocks.Render(TaskImages.Split(row.Description).Text);
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

    // The line's detail (question or status line), so a test can find it and read how it is cut.
    internal const string DetailTag = "task-detail";

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
    // "2 sessions, 1 live". Empty for a note.
    UIElement? SessionsOf(TaskRowView row)
    {
        if (row.Sessions.Length == 0) return null;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Enumerable.Range(0, Math.Min(SessionSquares, row.SessionCount)).ToList()
            .ForEach(i => panel.Children.Add(SessionSquare(10, MarkOf(row, i))));
        panel.Children.Add(new TextBlock { Text = row.Sessions, FontSize = 10, Foreground = DimInk, Margin = new Thickness(2, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        return panel;
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

    // The comments a thread draws (settled ones folded away, then only the latest three unless the earlier ones are
    // unfolded) and how many were left out as earlier. The one place the rule lives: Thread draws from it and the click
    // guard's key (ThreadLayoutKey) reads it.
    (IReadOnlyList<TaskCommentView> Shown, int Earlier) ThreadComments(TaskRowView row)
    {
        var candidates = row.Comments.Where(c => !c.Settled || settledOpen.Contains(row.Id)).ToList();
        var earlier = Math.Max(0, candidates.Count - LatestShown);
        return (earlier > 0 && !earlierOpen.Contains(row.Id) ? candidates.Skip(earlier).ToList() : candidates, earlier);
    }

    // What an open thread's layout depends on, as text: its session lines (Task 9: a session joining adds a line above the
    // comments and moves every one below), the comments it shows and which of them are opened in full.
    string ThreadLayoutKey(TaskRowView row) =>
        !row.Expanded ? ""
        : string.Join(',', row.SessionList.Select(s => s.Id)) + "/" + string.Join(',', row.Subtasks.Select(c => c.Id)) + "/"
          + string.Join(',', ThreadComments(row).Shown.Select(c => $"{c.Id}{(longOpen.Contains(c.Id) ? "+" : "")}"))
          + $"{settledOpen.Contains(row.Id)}{earlierOpen.Contains(row.Id)}";

    // The task's sessions with their Open buttons and "Start Claude on this" (Task 9), then the comments, each with its
    // author and time over its markdown, then a box for Petre's next one. RULING 62: the settled comments are folded away
    // behind one dim line above the comments, "N settled comments", which unfolds them in place (and reads "Hide N
    // settled comments" while they show). They are kept, so the history stays.
    UIElement Thread(TaskRowView row)
    {
        var panel = new StackPanel();
        row.SessionList.ToList().ForEach(session => panel.Children.Add(SessionLine(row.Id, session)));
        if (Actions?.StartClaude is { } start && row.Status != TaskStatus.Closed && !row.Closing)
        {
            var button = Small(StartClaudeText, StartClaudeText, () => start(row.Id),
                "Start a background Claude session on this task, with the task as its first message");
            button.Margin = new Thickness(0, 0, 0, 8);
            panel.Children.Add(button);
        }
        // SUBTASKS IN THE THREAD (follow-up: Petre's screenshot of an open parent that said "2 subtasks" and showed none).
        // Each is a button that shows that subtask (picked, its thread open, scrolled to); then "Add subtask", the twin of
        // the menu's "Add subtask…".
        row.Subtasks.ToList().ForEach(sub => panel.Children.Add(SubtaskEntry(sub)));
        if (Actions?.SubtaskParent is not null && row.Status != TaskStatus.Closed && !row.Closing)
        {
            var add = Small("Add subtask", "Add subtask", () => BeginSubtaskAdd(row.Id), "Add a subtask under this task");
            add.Margin = new Thickness(0, 0, 0, 8);
            panel.Children.Add(add);
        }
        var settledCount = row.Comments.Count(c => c.Settled);
        var unfolded = settledOpen.Contains(row.Id);
        var (shown, earlier) = ThreadComments(row);
        var open = earlierOpen.Contains(row.Id);
        // RULING 63: more than three comments show only the latest three, under a dim "N earlier comments" line that
        // unfolds the rest (settled comments, folded above, do not count toward the three).
        if (settledCount > 0) panel.Children.Add(SettledFold(row.Id, settledCount, unfolded));
        if (earlier > 0) panel.Children.Add(EarlierFold(row.Id, earlier, open));
        shown.ToList().ForEach(comment => panel.Children.Add(CommentOf(comment)));
        panel.Children.Add(CommentBox(row.Id));
        authors[row.Id] = shown.Select(c => c.Author).ToArray();
        shownComments[row.Id] = shown.Select(c => c.Id).ToArray();
        return new Border
        {
            Background = ThreadFill,
            BorderBrush = ThreadEdge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8),
            // 3 DIP above (TileGap), so the thread stands apart from its task's tile as the tiles do from each other.
            Margin = new Thickness(43, TileGap, 10, 6),
            Child = panel,
        };
    }

    // "N settled comments" (Ruling 62), dim, a button of its own: unfolds the thread's settled comments, then folds them again.
    UIElement SettledFold(Guid task, int count, bool unfolded)
    {
        var words = count == 1 ? "1 settled comment" : $"{count} settled comments";
        var fold = Small(new TextBlock { Text = unfolded ? $"Hide {words}" : words, Foreground = HeaderInk, FontSize = 10.5 },
            "Settled comments", () => ToggleSettled(task), "Settled comments are kept, folded away");
        fold.Margin = new Thickness(0, 0, 0, 6);
        return fold;
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
        fold.Margin = new Thickness(0, 0, 0, 6);
        return fold;
    }

    // About three lines of a comment show; a longer one ends in "Show more" (Ruling 63). A line is counted by its text, a
    // hundred characters to a line: the width is not known when the thread is built, and a test needs the same answer.
    internal static bool IsLong(string markdown) =>
        markdown.Split('\n').Sum(line => Math.Max(1, (int)Math.Ceiling(line.TrimEnd('\r').Length / 100.0))) > 3;

    // Three lines of 11.5-point text, with a little to spare for a paragraph's spacing.
    const double ThreeLines = 54;


    void ToggleSettled(Guid task)
    {
        if (!settledOpen.Remove(task)) settledOpen.Add(task);
        if (latest is not null) Draw(latest);
    }

    /// <summary>The words of the menu item and the thread's button (Task 9).</summary>
    internal const string StartClaudeText = "Start Claude on this";

    // One subtask in its parent's thread: "↳ title · status · status line", dim when it is done or closed. The whole line
    // is a button that shows the subtask (TasksBoard.ShowTask).
    UIElement SubtaskEntry(SubtaskView sub)
    {
        var text = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var finished = sub.Status is TaskStatus.Done or TaskStatus.Closed;
        text.Inlines.Add(new Run("↳ ") { Foreground = HeaderInk });
        text.Inlines.Add(new Run(sub.Title) { Foreground = finished ? HeaderInk : Ink });
        text.Inlines.Add(new Run($" · {TaskViews.StatusName(sub.Status)}") { Foreground = HeaderInk });
        if (sub.StatusLine.Length > 0) text.Inlines.Add(new Run($" · {sub.StatusLine}") { Foreground = HeaderInk });
        var entry = Flat(text, $"Subtask {sub.Title}", () => Actions?.ShowTask?.Invoke(sub.Id), new Thickness(4, 1, 4, 1));
        entry.Background = Brushes.Transparent;
        entry.HorizontalAlignment = HorizontalAlignment.Left;
        entry.Margin = new Thickness(0, 0, 0, 4);
        entry.ToolTip = "Show this subtask";
        return entry;
    }

    // One session of the thread (Task 9, the spec's "Task history"): its short id, when it joined and where it stands,
    // and Open, which does what going to that session does. An expired one has nothing left to open, so no button.
    UIElement SessionLine(Guid taskId, SessionView session)
    {
        var shortId = session.Id[..Math.Min(8, session.Id.Length)];
        var line = new DockPanel { Tag = "session-line", Margin = new Thickness(0, 0, 0, 4), LastChildFill = true };
        if (session.State != SessionState.Expired && Actions?.OpenSession is { } open)
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

    UIElement CommentOf(TaskCommentView comment)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock { Tag = "comment-author", Text = comment.Author, FontWeight = FontWeights.Bold, FontSize = 11, Foreground = comment.ByClaude ? ClaudeInk : Ink });
        header.Children.Add(new TextBlock { Text = $" · {comment.When}", FontSize = 11, Foreground = HeaderInk });
        // RULING 58 ("i should only be able to edit my own comments, not claude's"): his own comments, and only those, have
        // an Edit button beside the time; the board refuses a session's comment too (TaskBoard.EditComment).
        var editing = editingComment == comment.Id && comment.Editable;
        if (comment.Editable && !editing)
        {
            var edit = Small("Edit", "Edit comment", () => BeginEditComment(comment.Id), "Edit your comment");
            edit.Margin = new Thickness(8, 0, 0, 0);
            header.Children.Add(edit);
        }
        // RULING 62: any comment can be settled (a question that has its answer) or brought back, from its own button; the
        // comment stays either way.
        var settle = Small(comment.Settled ? "Unsettle" : "Settle", comment.Settled ? "Unsettle comment" : "Settle comment",
            () => Actions?.SettleComment(comment.Id, !comment.Settled), comment.Settled ? "Show it with the thread again" : "Fold this comment away, keeping it");
        settle.Margin = new Thickness(4, 0, 0, 0);
        if (!editing) header.Children.Add(settle);
        if (editing) return new StackPanel { Margin = new Thickness(0, 0, 0, 8), Children = { header, CommentEditorBox(comment) } };
        // Images in tasks: the body renders the comment's text alone; its pictures are a row of their own after it
        // (TaskImageRow), outside the clipped box, so folding to three lines cuts text, never a picture.
        var split = TaskImages.Split(comment.Markdown);
        var document = MarkdownBlocks.Render(split.Text);
        document.FontSize = 11.5;
        document.Foreground = Ink;
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
        // RULING 63: a long comment shows about three lines, clipped, and ends in "Show more", which opens it in place;
        // "Show less" folds it back. The state is the window's (longOpen), so a redraw keeps it. Whether it is long is
        // judged by the character count at first (the width is not known while the thread is built), and by the laid-out
        // height once it has been (review of the batch): a paragraph that fits three lines at the window's width loses its
        // button, and one that wraps to more gains it.
        var isLong = IsLong(split.Text);
        var full = longOpen.Contains(comment.Id);
        if (!full)
        {
            body.MaxHeight = ThreeLines;
            body.ClipToBounds = true;
        }
        // A comment of pictures alone has no text box to show.
        if (split.Text.Trim().Length == 0) body.Visibility = Visibility.Collapsed;
        var parts = new StackPanel { Margin = new Thickness(0, 0, 0, 8), Opacity = comment.Settled ? 0.55 : 1, Children = { header, body } };
        var more = Small(new TextBlock { Text = full ? "Show less" : "Show more", Foreground = HeaderInk, FontSize = 10.5 },
            full ? "Show less" : "Show more", () =>
            {
                if (!longOpen.Remove(comment.Id)) longOpen.Add(comment.Id);
                if (latest is not null) Draw(latest);
            }, full ? "Fold the comment back to three lines" : "Show the whole comment");
        // Collapsed means it takes no room and is not in the tree for a reader, but it is there for the layout to switch on.
        more.Visibility = full || isLong ? Visibility.Visible : Visibility.Collapsed;
        parts.Children.Add(more);
        if (TaskImageRow.Of(StateDir, split.Images, OpenImage) is { } pictures) parts.Children.Add(pictures);
        if (!full)
            body.LayoutUpdated += (_, _) =>
            {
                if (body.ActualWidth <= 0 || body.Visibility != Visibility.Visible) return;
                var wanted = body.ExtentHeight > ThreeLines + 0.5 ? Visibility.Visible : Visibility.Collapsed;
                if (more.Visibility != wanted) more.Visibility = wanted;
            };
        // A settled comment, when unfolded, is dim: it is history.
        return parts;
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
            ToolTip = "Markdown works. Enter saves, Shift+Enter starts a new line, Esc keeps the comment as it was.",
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
        var shown = (latest?.Groups.SelectMany(g => g.Rows) ?? []).Concat(latest?.Cells.SelectMany(c => c.Rows) ?? [])
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
    // button inside the indented frame. So the box spans the frame's full width with a short placeholder; the hint
    // (markdown works, which keys do what) is its tooltip and a dim line under it while it has the keyboard; the
    // "Comment" button sits at the end of that line. Nothing here has a width of its own to clip at the window's
    // minimum width: the hint trims, the box wraps.
    internal const string CommentPlaceholder = "Add a comment…";
    internal const string CommentHint = "Markdown works. Paste or drop images. Enter adds, Shift+Enter starts a new line.";

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
            AcceptsReturn = false,
            MaxHeight = 120,
            ToolTip = CommentHint,
        };
        AutomationProperties.SetName(box, "New comment");
        var placeholder = new TextBlock
        {
            Tag = "comment-placeholder",
            Text = CommentPlaceholder,
            FontSize = 12,
            Foreground = HeaderInk,
            IsHitTestVisible = false,
            Margin = new Thickness(2, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed,
        };
        var hint = new TextBlock
        {
            Tag = "comment-hint",
            Text = CommentHint,
            FontSize = 10.5,
            Foreground = HeaderInk,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            // Hidden rather than collapsed, so the line under the box keeps its height and nothing moves as the keyboard
            // comes and goes.
            Visibility = Visibility.Hidden,
        };
        box.TextChanged += (_, _) =>
        {
            drafts[id] = box.Text;
            placeholder.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        box.PreviewKeyDown += (_, e) => e.Handled = PressInComment(id, e.Key, Keyboard.Modifiers) != KeyOutcome.Ignored;
        // Ctrl+V and drops of pictures (TasksWindow.Images.cs). After the handler above, which sets Handled for every key.
        TakePictures(box, id);
        box.GotKeyboardFocus += (_, _) => hint.Visibility = Visibility.Visible;
        // The keyboard leaving the box hides the hint, and frees a redraw that waited for it.
        box.LostKeyboardFocus += (_, _) =>
        {
            hint.Visibility = Visibility.Hidden;
            Flush();
        };
        commentBoxes[id] = box;

        var post = Flat(new TextBlock { Text = "Comment", Foreground = Ink, FontSize = 11 }, "Post comment", () => Post(id), new Thickness(10, 1, 10, 1));
        post.Margin = new Thickness(6, 0, 0, 0);
        DockPanel.SetDock(post, Dock.Right);
        return new StackPanel
        {
            Children =
            {
                new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Background = Frozen(0xFF, 0x15, 0x16, 0x1A),
                    BorderBrush = ThreadEdge,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(6, 3, 6, 3),
                    Child = new Grid { Children = { box, placeholder } },
                },
                // The pictures waiting to go with the comment, each with its x (TasksWindow.Images.cs).
                PendingRow(id),
                new DockPanel { Margin = new Thickness(0, 3, 0, 0), Children = { post, hint } },
            },
        };
    }

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
        if (CommentWithPictures(id, text.Trim().Length == 0 ? "" : text) is not { } comment) return false;
        drafts.Remove(id);
        if (commentBoxes.TryGetValue(id, out var box)) box.Text = "";
        if (Actions.Comment(id, comment.Markdown))
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
        box.ToolTip = "A description, in markdown. Paste or drop images. Enter saves, Shift+Enter starts a new line.";
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
        StartEditorPictures(latest?.Groups.SelectMany(g => g.Rows).FirstOrDefault(r => r.Id == id)?.Description ?? "");
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
        if (renaming is not null || selectedId.HasNoValue) return false;
        var id = selectedId.Value;
        // Not from a box being typed in (the add box, the search box): F2 there is the box's.
        if (!Keys.Contains(id) || closing.GetValueOrDefault(id) || AddBoxShown && AddBox.IsKeyboardFocusWithin || SearchBox.IsKeyboardFocusWithin) return false;
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
    internal ContextMenu OpenMenuFor(Guid id, bool atPointer = false)
    {
        var row = latest?.Groups.SelectMany(g => g.Rows).FirstOrDefault(r => r.Id == id);
        var menu = atPointer
            ? new ContextMenu { Placement = PlacementMode.MousePoint }
            : new ContextMenu { PlacementTarget = menuButtons.GetValueOrDefault(id), Placement = PlacementMode.Bottom };
        if (row is null) return menu;

        // A closed task's title is not edited: it is history, and Reopen brings it back first.
        if (row.Status != TaskStatus.Closed && !row.Closing)
        {
            menu.Items.Add(Item("Edit", () => BeginRename(id)));
            // Task 9: handing the task to Claude, right after Edit (the spec, "Where the clicks are"). Run once this menu
            // has closed: it may ask a question or open a pick list of folders at the pointer.
            if (Actions?.StartClaude is { } start)
                menu.Items.Add(Item(StartClaudeText, () => Dispatcher.BeginInvoke(() => start(id), System.Windows.Threading.DispatcherPriority.Input)));
            // Subtasks (Petre's pick A): the add box with this line's task as the parent (a sibling on a subtask's line),
            // once this menu has closed so the box can take the keyboard.
            if (Actions?.SubtaskParent is not null)
                menu.Items.Add(Item(FloatingBar.AddSubtaskText, () => Dispatcher.BeginInvoke(() => BeginSubtaskAdd(id), System.Windows.Threading.DispatcherPriority.Input)));
            menu.Items.Add(new Separator());
        }

        var status = Item("Status");
        SettableStatuses.ToList().ForEach(s =>
        {
            var item = Item(TaskViews.StatusName(s), () => Actions?.SetStatus(id, s));
            item.IsChecked = row.Status == s && !row.Closing;
            status.Items.Add(item);
        });
        menu.Items.Add(status);
        menu.Items.Add(row.Status == TaskStatus.Closed && !row.Closing
            ? Item("Reopen", () => Actions?.Reopen(id))
            : Item("Close", () => Actions?.Close(id)));
        // Snooze (task 6da8d7eb): the moon's lengths as a submenu, for every open line, waiting ones included, so a Matrix
        // line, which has no moon, reaches it too.
        if (Actions?.Snooze is not null && row.Status != TaskStatus.Closed && !row.Closing)
        {
            var snooze = Item(row.SnoozedUntil is null ? "Snooze" : "Snoozed");
            SnoozeItems(id).ForEach(item => snooze.Items.Add(item));
            menu.Items.Add(snooze);
        }
        // The task list's one workspace menu (WorkspaceChipMenu), the move the bar's line chip makes. Its own menu,
        // opened under the line's "⋯" once this one has closed: the compact chip menu is a context menu of its own, not
        // a submenu.
        // Who the task is on (Ruling 57), here and nowhere on the bar: Me or Claude, the task's own ticked.
        var assign = Item("Assign to");
        new[] { Assignee.Me, Assignee.Claude }.ToList().ForEach(who =>
        {
            var item = Item(who.Name, () => Actions?.Assign(id, who));
            item.IsChecked = row.Assignee == who;
            assign.Items.Add(item);
        });
        menu.Items.Add(assign);
        menu.Items.Add(Item("Move to…", () => Dispatcher.BeginInvoke(() => OpenMoveMenu(id, row.Place.Workspace),
            System.Windows.Threading.DispatcherPriority.Input)));
        // The goal the task counts toward (goals plan, Task 6): a plain submenu, only while goals exist (ruling G1).
        if (latest?.ShowsGoals == true)
        {
            var goal = Item("Goal");
            GoalItems(id, row.Goal).ForEach(item => goal.Items.Add(item));
            menu.Items.Add(goal);
        }
        menu.Items.Add(new Separator());
        // Delete cannot be undone, so it takes a second, deliberate step: a submenu item rather than a dialog, which
        // would be one more window to answer for a menu choice already made.
        var delete = Item("Delete");
        delete.Items.Add(Item("Delete for good", () => Actions?.Delete(id)));
        menu.Items.Add(delete);
        OpenMenu(menu);
        return menu;
    }

    // A line's "Move to…": the compact workspace menu under its "⋯", the task's own workspace outlined. Internal, and
    // answering the menu, for the tests.
    internal ContextMenu OpenMoveMenu(Guid id, Maybe<Guid> current)
    {
        var menu = WorkspaceChipMenu.Build(latest?.Places ?? [], current, workspace => Actions?.MoveTo(id, workspace), menuButtons.GetValueOrDefault(id));
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
        var row = latest?.Groups.SelectMany(g => g.Rows).FirstOrDefault(r => r.Id == id);
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
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        menu.Items.Add(new MenuItem { Header = new TextBlock { Text = "Claude is still working on this task." }, IsEnabled = false });
        // Run once this menu has closed, so the redraw the close causes is not held back by it (MustWait).
        menu.Items.Add(Item(TaskViews.StopClaudeAndClose, () => Dispatcher.BeginInvoke(() => Actions?.StopClaudeAndClose?.Invoke(id),
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
        addParent = null;
        ResetAddFlags();
        ClearAddPictures();
        AddRow.Visibility = Visibility.Collapsed;
    }

    // --- adding a subtask (docs/superpowers/specs/2026-11-24-subtasks-design.md, "Making subtasks", Petre's pick A) ---
    //
    // "Add subtask…" opens the add box with the parent as a chip ("↳ <parent title> ×") before the workspace chip, and the
    // workspace chip set to the parent's workspace (still changeable), so the subtask lands with its parent. Enter adds it
    // under the parent; Esc or the chip's press drops the parent and leaves the text as a plain note.

    const string NotePlaceholder = "A note: Enter adds it, Esc closes the box";

    // The parent the box adds under, or null for a plain note.
    SubtaskParentView? addParent;

    internal string AddPlaceholderText => AddPlaceholder.Text;

    /// <summary>"Add subtask…" on the line <paramref name="lineId"/>. Internal for the tests.</summary>
    internal void BeginSubtaskAdd(Guid lineId)
    {
        if (Actions?.SubtaskParent?.Invoke(lineId) is not { } parent) return;
        ShowAdd();
        addParent = parent;
        addChosen = (latest?.Places ?? []).Where(p => !p.IsGroup && p.Workspace.Equals(parent.Workspace))
            .Select(p => new TaskPlaceChoice(p.Workspace, p.Name, p.Colour))
            .FirstOrDefault();
        PaintAddPlace();
        AddBox.Focus();
    }

    void DropAddParent()
    {
        addParent = null;
        PaintAddPlace();
        AddBox.Focus();
    }

    // The chip saying where the note goes: the one picked from its menu, else the view's (the filtered workspace, or
    // the workspace in front).
    void PaintAddPlace()
    {
        AddEnd.Children.Clear();
        AddPlaceholder.Text = addParent is null ? NotePlaceholder : FloatingBar.SubtaskPlaceholder;
        // The important and urgent toggles first (Ruling 37, TasksWindow.AddFlags.cs), then the parent chip when adding a
        // subtask, then the workspace chip.
        AddFlagToggles().ToList().ForEach(toggle => AddEnd.Children.Add(toggle));
        if (addParent is { } parent)
        {
            var label = new TextBlock { Text = $"↳ {FloatingBar.FirstLine(parent.Title)} ×", Foreground = ClaudeInk, FontSize = 11, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis };
            var parentChip = Flat(label, "Subtask of", DropAddParent, new Thickness(8, 2, 8, 2));
            parentChip.ToolTip = "A subtask of this task; press to add a plain note instead";
            parentChip.Margin = new Thickness(8, 0, 0, 0);
            AddEnd.Children.Add(parentChip);
        }
        var target = addChosen ?? latest?.AddTarget;
        if (target is null) return;
        // DROPDOWN THAT DID NOT OPEN (2026-11-23, Petre: "dropdown doesn't work"). The menu used to open from a second
        // Click handler added after Flat's, and Flat's own handler marks the click handled, so WPF never called the
        // second one: a handler added with += is skipped for an event already handled, even on the same element. The
        // menu now opens from Flat's press itself.
        Button? chip = null;
        chip = Flat(new TextBlock { Text = $"on {target.Name} ▾", Foreground = Ink, FontSize = 11 }, "Add to workspace", () =>
        {
            var places = latest?.Places ?? [];
            OpenMenu(WorkspaceChipMenu.Build(places, target.Workspace,
                workspace =>
                {
                    addChosen = places.Where(p => !p.IsGroup && p.Workspace.Equals(workspace))
                        .Select(p => new TaskPlaceChoice(p.Workspace, p.Name, p.Colour))
                        .FirstOrDefault();
                    PaintAddPlace();
                    AddBox.Focus();
                }, chip));
        }, new Thickness(8, 2, 8, 2));
        chip.Margin = new Thickness(8, 0, 0, 0);
        AddEnd.Children.Add(chip);
    }

    KeyOutcome PressInAdd(Key key)
    {
        if (key is not (Key.Enter or Key.Return)) return KeyOutcome.Ignored;
        var target = addChosen ?? latest?.AddTarget;
        // Pasted pictures (TasksWindow.Images.cs, "the add box"): they become the description, and with no words typed the
        // task is called "Picture".
        var title = TaskImages.TitleFor(AddBox.Text, AddBoxImages.Count);
        var describe = AddDescription();
        var added = addParent is { } parent
            ? Actions?.AddSubtask is { } addSubtask && target is not null && addSubtask(title, target.Workspace, AddQuadrant, parent.Id, describe)
            : Actions is not null && target is not null && Actions.AddNote(title, target.Workspace, AddQuadrant, describe);
        if (!added) return KeyOutcome.NewLine;
        HideAdd();
        return KeyOutcome.Posted;
    }

    // For the tests: types into the add box and presses a key.
    internal bool TypeNote(string text, Key key)
    {
        AddBox.Text = text;
        return PressInAdd(key) == KeyOutcome.Posted;
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
        // Subtasks: in a subtask box the first Esc drops the parent (the placeholder says so); the next closes the box.
        else if (AddBoxShown && addParent is not null) DropAddParent();
        else if (AddBoxShown) HideAdd();
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
