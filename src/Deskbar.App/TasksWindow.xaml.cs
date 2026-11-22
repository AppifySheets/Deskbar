using System.Runtime.InteropServices;
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
using Deskbar.Core.Persistence;
using Deskbar.Core.Tasks;
using Deskbar.Windows.Monitoring;

namespace Deskbar.App;

// THE TASKS WINDOW (docs/superpowers/specs/2026-11-22-tasks-board-design.md, "The window" and "Comments"; mockup
// .superpowers/brainstorm/876-1795339550/content/tasks-window.html). Display only, like Quick find's window: it draws
// the TasksView its controller (TasksBoard) hands it and passes every click back through TasksWindowActions.
//
//   A PAGE OF ITS OWN (Petre: "can i have a separate page for all tasks?"): a regular window with a title bar,
//   resizable, in Alt+Tab and on the taskbar, which stays open when he clicks into another app.
//   Open / Dismiss   Open shows it where he last left it (AppState.TasksWindow: the size, the place and so the
//                    screen, across restarts), or beside the bar the first time and whenever no screen holds that
//                    place any more (PlaceNear). Opening a window that is already open brings it forward instead and
//                    keeps its view (TasksBoard.Open). Closing it (the title bar's X) only hides it until the app
//                    exits (CloseForGood): a closed WPF window can never be shown again and the next "+N more" would
//                    throw. Every close, and every move or resize a second after it settles, hands the place to the
//                    controller to remember. It opens on the board: nothing typed into has the keyboard (the first
//                    task window opened onto an entry form, and Petre did not like it).
//   Present          draws a view. A view that would draw what is already drawn draws nothing, and a view that
//                    arrives while drawing would pull something out from under Petre waits (MustWait): one of the
//                    window's menus open (the bar's #77 lesson), a rename under way, or a press in flight on the window
//                    (the bar's #48 lost click). Each of those ends by drawing the latest view (Flush). A half-typed
//                    comment does not hold a redraw; its text, keyboard and caret are carried through it instead.
//   Clicks           a line and its buttons take the bar's click guard (ClickIgnored): no second press of a double
//                    click, and nothing in the half second after the lines were redrawn.
//
// Top to bottom: the toolbar (status filters with their counts, the workspace filter, the goal filter while goals
// exist, "+ Add"), the
// add box while "+ Add" has it open, a line saying why the last action failed, then the groups (Waiting on you, then
// Covey's quadrants I to IV, captioned; Task 12). Each line: Claude's
// CL badge or a note's tick box (Undo in its place for the six seconds after a close), the title over the question or
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

    // The gap kept between the bar and the window, and the window's size when the screen allows it (the mockup's
    // 720 wide; tall enough for a dozen lines and an open thread).
    const double Gap = 4;

    // How long a move or a resize must have settled before the place is remembered, so a drag writes state.json once
    // rather than on every pixel.
    static readonly TimeSpan PlaceSettles = TimeSpan.FromSeconds(1);

    // DWMWA_USE_IMMERSIVE_DARK_MODE: a dark title bar over the dark page.
    const uint DarkTitleBar = 20;

    // MONITOR_DEFAULTTONULL: no monitor at all for a point on none, rather than the nearest one.
    const uint MonitorDefaultToNull = 0;

    const double PreferredWidth = 720;
    const double PreferredHeight = 560;

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

    // Per line, rebuilt with the rows: the "⋯" button a menu hangs from, the thread's comment box and its authors.
    // The drafts outlive a redraw, so a comment half typed survives a change arriving from a session.
    readonly Dictionary<Guid, Button> menuButtons = [];
    readonly Dictionary<Guid, TextBox> commentBoxes = [];
    readonly Dictionary<Guid, IReadOnlyList<string>> authors = [];
    readonly Dictionary<Guid, bool> closing = [];
    readonly Dictionary<Guid, string> drafts = [];

    // Where it first opens: the bar's rectangle in device pixels (empty: centred on the screen of the window that was
    // in front). And the window's last rectangle in its normal state, in device pixels, which is what is remembered (a
    // maximized window remembers the rectangle it returns to, and that it was maximized).
    Rect near = Rect.Empty;
    nint fallbackAnchor;
    Rect lastNormal = Rect.Empty;
    readonly IDisposable placeWatch;
    readonly IDisposable ageWatch;

    // The workspace chosen in the add box, or none chosen yet (the view's AddTarget then).
    TaskPlaceChoice? addChosen;

    // Set only when the app is going away; see QuickFindWindow.closingForGood.
    bool closingForGood;

    public TasksWindow()
    {
        InitializeComponent();
        PreviewKeyDown += (_, e) => e.Handled = e.Key == Key.Escape && Escape();
        SourceInitialized += (_, _) =>
        {
            var on = 1;
            _ = NativeMethods.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DarkTitleBar, ref on, sizeof(int));
        };
        // Its place is remembered once a move or a resize has settled (PlaceSettles), and on every close (Dismiss). The
        // rectangle in the normal state is noted on every change, since a maximized window's own rectangle is the
        // screen's, not the one it returns to.
        var context = new System.Windows.Threading.DispatcherSynchronizationContext(Dispatcher);
        placeWatch = Observable.Merge(
                Observable.FromEventPattern(this, nameof(LocationChanged)).Select(_ => System.Reactive.Unit.Default),
                Observable.FromEventPattern(this, nameof(SizeChanged)).Select(_ => System.Reactive.Unit.Default),
                Observable.FromEventPattern(this, nameof(StateChanged)).Select(_ => System.Reactive.Unit.Default))
            .Do(_ => NoteNormal())
            .Throttle(PlaceSettles)
            .ObserveOn(context)
            .Subscribe(_ =>
            {
                if (IsOpen && !Quiet) Actions?.Remember(CurrentPlace());
            });
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
        WireDrag();
        BuildToolbarEnd();
    }

    // The controller's side, set once by TasksBoard's constructor.
    internal TasksWindowActions? Actions { get; set; }

    // For the tests only: shown without taking the focus and parked off the virtual screen, as the bar under test is.
    internal bool Quiet { get; init; }

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
    internal bool AddBoxShown => AddRow.Visibility == Visibility.Visible;
    internal string? Problem => ProblemText.Visibility == Visibility.Visible ? ProblemText.Text : null;
    internal bool ThreadShown(Guid id) => authors.ContainsKey(id);
    internal IReadOnlyList<string> CommentAuthors(Guid id) => authors.GetValueOrDefault(id) ?? [];
    internal bool IsClosing(Guid id) => closing.GetValueOrDefault(id);
    internal string CommentDraft(Guid id) => commentBoxes.TryGetValue(id, out var box) ? box.Text : drafts.GetValueOrDefault(id, "");
    internal TextBox? CommentBoxOf(Guid id) => commentBoxes.GetValueOrDefault(id);

    // THE CLICK GUARD, the bar's (#48, Ruling 10; FloatingBar.TaskClickIgnored), fix round 1, Important 1. A line
    // closed here stays six seconds and then leaves, and a tick or Reopen takes its line away, so the next line slides
    // under the pointer: the second press of a double click closed or reopened a second task, and a click aimed at a
    // line that just moved hit its neighbour. A click on a line or one of its buttons is dropped when its press was the
    // second of a double click or when it lands within half a second of a redraw of the lines. Undo drops only the
    // double click: it appears where the tick was on purpose, so a deliberate click on it, however soon, is meant.
    // ClickClock is a seam for the tests, which put a click right after a redraw or half a second later.
    internal Func<DateTime> ClickClock { get; init; } = () => DateTime.UtcNow;

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

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!closingForGood)
        {
            e.Cancel = true;
            Dismiss();
        }
        base.OnClosing(e);
    }

    public void CloseForGood()
    {
        closingForGood = true;
        placeWatch.Dispose();
        ageWatch.Dispose();
        Close();
    }

    /// <summary>
    /// Shown where it was last left (<paramref name="remembered"/>, when a screen still holds it), else clear of
    /// <paramref name="bar"/> (the bar's rectangle on screen, device pixels; empty to centre it on the screen of the
    /// window in front). The keyboard goes to the toolbar, never to a text box: the window opens on the board.
    /// </summary>
    public void Open(Rect bar, Maybe<TasksWindowState> remembered)
    {
        fallbackAnchor = NativeMethods.GetForegroundWindow();
        near = bar;
        HideAdd();
        renaming = null;
        renameBox = null;
        IsOpen = true;
        // Shown WITHOUT activation (fix round 1, Critical 1): the controller brings it to Petre afterwards, moving it to
        // the desktop in front before anything activates it (TasksBoard.Open, Arrive).
        ShowActivated = false;
        // Placed before Show, so it does not open somewhere else first. A maximized window is shown at its normal place
        // and maximized only once it has arrived (MaximizeIfRemembered, after the move to the desktop in front): showing
        // maximized activates the window whatever ShowActivated says (review I-2).
        WindowState = WindowState.Normal;
        // The List's minimum while it is placed, so a remembered List width is not held at the Matrix's; the Matrix
        // grows it once it is on screen (FitWidthToView, below).
        listNormal = null;
        MinWidth = ListMinWidth;
        var placed = remembered.HasValue && PlaceAt(remembered.Value);
        if (!placed) Place();
        maximizeOnArrival = placed && remembered.Value.Maximized;
        Show();
        // Show applies the window's Width and Height at the DPI the window was created with. PlaceAt set them to the
        // remembered pixels at that DPI, so on the same screen this changes nothing; on a screen of another DPI the
        // window can show one frame at the scaled size before this puts the remembered device rectangle back.
        if (placed) PlaceDevice(remembered.Value);
        NoteNormal();
        FitWidthToView(matrixShown);
        Scroller.ScrollToTop();
        // NO Toolbar.Focus() HERE (2026-11-23, "tasks should become a top window, now it's behind somewhere"). A WPF
        // Focus() on an element of a window that is not active activates that window (measured on the hidden desktop),
        // so this used to make the window the foreground one before the controller had moved it to the desktop in
        // front, and the seam's SetForegroundWindow on what already was the foreground window then raised nothing. The
        // controller hands the keyboard over once the window has arrived (FocusBoard, from TasksBoard.Arrived).
    }

    /// <summary>
    /// The keyboard to the toolbar, never to a text box (the window opens on the board). Only once the controller has
    /// brought the window to the desktop in front and activated it: focusing an inactive window activates it.
    /// </summary>
    internal void FocusBoard() => Toolbar.Focus();

    // Whether the window, opened at its remembered normal place, is to be maximized once it has arrived.
    bool maximizeOnArrival;

    /// <summary>
    /// Maximizes the window when it was left maximized, once the controller has brought it to the desktop in front
    /// (review I-2: SW_SHOWMAXIMIZED activates, so it must not run before the move).
    /// </summary>
    internal void MaximizeIfRemembered()
    {
        if (!maximizeOnArrival) return;
        maximizeOnArrival = false;
        WindowState = WindowState.Maximized;
    }

    /// <summary>
    /// The window activating itself, only when the controller has no seam to bring it (and counted for the tests):
    /// restored first if it was minimized, since nothing else will.
    /// </summary>
    internal void ActivateHere()
    {
        SelfActivations++;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (!Quiet) Activate();
    }

    internal int SelfActivations { get; private set; }

    /// <summary>Hides it (the title bar's X, or `tasks-window hide`), remembering where it was.</summary>
    public void Dismiss()
    {
        if (!IsOpen) return;
        IsOpen = false;
        if (openMenu is { IsOpen: true } menu) menu.IsOpen = false;
        renaming = null;
        renameBox = null;
        NoteNormal();
        Actions?.Remember(CurrentPlace());
        Hide();
    }

    /// <summary>Where the window is: its normal rectangle in device pixels, and whether it is maximized.</summary>
    // While the Matrix has grown the window, the List's rectangle (ListRect, TasksWindow.Matrix.cs).
    internal TasksWindowState CurrentPlace() => PlaceOf(ListRect());

    TasksWindowState PlaceOf(Rect place) =>
        new((int)place.Left, (int)place.Top, (int)place.Width, (int)place.Height, WindowState == WindowState.Maximized);

    // The window's rectangle as Windows has it, noted only in the normal state: a maximized window's rectangle is the
    // screen's and a minimized one's is nonsense (CLAUDE.md, Traps), and neither is where it returns to.
    void NoteNormal()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0 || WindowState != WindowState.Normal || !NativeMethods.GetWindowRect(handle, out var rect)) return;
        if (rect.Right - rect.Left > 0 && rect.Bottom - rect.Top > 0)
            lastNormal = new Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    // The remembered place, when a screen still holds its middle, put on the window in DEVICE PIXELS (fix round 1,
    // Important 3). The first version divided by the target screen's DPI and assigned Left/Top, which WPF then turned
    // back into pixels at the DPI of the screen the window was created on, so on mixed DPI it landed in the wrong place
    // and size. SetWindowPos on the (still hidden) handle takes the stored pixels as they are. False when no screen
    // holds the place any more (a monitor unplugged since); the caller then places it beside the bar.
    bool PlaceAt(TasksWindowState place)
    {
        if (place.Width <= 0 || place.Height <= 0) return false;
        var middle = new NativeMethods.POINT { X = place.Left + place.Width / 2, Y = place.Top + place.Height / 2 };
        if (NativeMethods.MonitorFromPoint(middle, MonitorDefaultToNull) == 0) return false;
        PlaceDevice(place);
        // The same size as WPF's own Width and Height, at the DPI the hidden window has now, so Show does not apply the
        // XAML size for a frame (review minor: the post-Show placement could flash one frame).
        var dpi = VisualTreeHelper.GetDpi(this);
        Width = place.Width / dpi.DpiScaleX;
        Height = place.Height / dpi.DpiScaleY;
        return true;
    }

    void PlaceDevice(TasksWindowState place) =>
        NativeMethods.SetWindowPos(new WindowInteropHelper(this).EnsureHandle(), 0, place.Left, place.Top, place.Width, place.Height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

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
        // A comment box that had the keyboard gets it back in its new form, its text (from `drafts`) and its caret where
        // they were, so typing carries on through a redraw and a second comment can follow the first without a click.
        // Likewise the rename box.
        if (keyboardWasIn != Guid.Empty && commentBoxes.TryGetValue(keyboardWasIn, out var again))
        {
            again.Focus();
            again.CaretIndex = Math.Min(caretWas, again.Text.Length);
        }
        renameBox?.Focus();
        // A view that arrived during the draw is drawn now.
        Flush();
    }

    // Which comment box had the keyboard when the last draw began, and where its caret was.
    Guid keyboardWasIn;
    int caretWas;

    void DrawRows(TasksView view)
    {
        keyboardWasIn = commentBoxes.FirstOrDefault(pair => pair.Value.IsKeyboardFocusWithin).Key;
        caretWas = keyboardWasIn != Guid.Empty ? commentBoxes[keyboardWasIn].CaretIndex : 0;
        // When the lines on show change (which tasks, their order, which threads are open, which are closing), a line
        // may now be under the pointer that was not, so for half a second a click is not trusted (the click guard,
        // ClickIgnored). A redraw that only rewrites what lines say (a session's status line) moves nothing and does
        // not start the half second, or frequent status pulses would eat clicks (review minor).
        var shownRows = view.Matrix ? view.Cells.SelectMany(c => c.Rows).ToArray() : view.Groups.SelectMany(g => g.Rows).ToArray();
        var placesKey = string.Join('|', shownRows.Select(r => $"{r.Id}{r.Expanded}{r.Closing}").Prepend($"{view.Matrix}"));
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
        selectionFrames.Clear();
        authors.Clear();
        closing.Clear();
        // The List or the Matrix (TasksWindow.Matrix.cs), whichever the switch says; the other is emptied and hidden.
        matrixShown = view.Matrix;
        FitWidthToView(view.Matrix);
        Scroller.Visibility = view.Matrix ? Visibility.Collapsed : Visibility.Visible;
        MatrixGrid.Visibility = view.Matrix ? Visibility.Visible : Visibility.Collapsed;
        MatrixGrid.Children.Clear();
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
            string.Join('|', view.Places.Select(p => $"{p.IsGroup}{p.Workspace}{p.Name}{(p.Colour as SolidColorBrush)?.Color}")),
            view.EmptyText,
            $"{renaming}",
            $"{view.Matrix}",
            string.Join('\u0002', view.Cells.Select(c => $"{c.Header}:" + string.Join('\u0003', c.Rows.Select(r =>
                $"{r.Id}|{r.Title}|{r.Detail}|{r.Status}|{r.Claudes}|{PlaceSignature(r.Place)}|{r.CommentCount}|{r.Expanded}|{r.Closing}|{r.Important}|{r.Urgent}|" +
                string.Join('\u0004', r.Comments.Select(c => $"{c.Id}{c.Author}{c.When}{c.Markdown}")))))),
            string.Join('\u0002', view.Groups.Select(g => $"{g.Header}:" + string.Join('\u0003', g.Rows.Select(r =>
                $"{r.Id}|{r.Title}|{r.Detail}|{r.Status}|{r.Claudes}|{r.Sessions}|{r.SessionCount}|{r.LiveCount}|{PlaceSignature(r.Place)}|{r.CommentCount}|" +
                $"{r.Expanded}|{r.Closing}|{r.Important}|{r.Urgent}|" + string.Join('\u0004', r.Comments.Select(c => $"{c.Id}{c.Author}{c.When}{c.Markdown}")))))));

    static string PlaceSignature(TaskPlaceChoice place) => $"{place.Workspace}{place.Name}{(place.Colour as SolidColorBrush)?.Color}";

    // The status chips and the workspace filter, redrawn with the rows.
    void DrawFilters(TasksView view)
    {
        Filters.Children.Clear();
        view.Filters.ToList().ForEach(chip =>
        {
            var button = Flat(new TextBlock { Text = chip.Label, Foreground = chip.Asks ? Orange : Ink, FontSize = 11 }, $"Filter: {chip.Label}",
                () => Actions?.Filter(chip.Filter), new Thickness(8, 2, 8, 2));
            button.BorderBrush = chip.On ? PickedEdge : Brushes.Transparent;
            button.Margin = new Thickness(0, 0, 6, 0);
            Filters.Children.Add(button);
        });
        var workspace = Flat(new TextBlock { Text = $"{view.WorkspaceFilterLabel} ▾", Foreground = Ink, FontSize = 11 }, "Workspace filter",
            () => { }, new Thickness(8, 2, 8, 2));
        workspace.Click += (_, _) => OpenMenu(WorkspaceFilterMenu(view, workspace));
        Filters.Children.Add(workspace);
        // The goal filter, beside the workspace one, only while goals exist (ruling G1).
        goalFilterButton = null;
        if (view.ShowsGoals)
        {
            var goal = Flat(new TextBlock { Text = $"{view.GoalFilterLabel} ▾", Foreground = Ink, FontSize = 11 }, "Goal filter",
                () => { }, new Thickness(8, 2, 8, 2));
            goal.Click += (_, _) => OpenGoalFilterMenu();
            goal.Margin = new Thickness(6, 0, 0, 0);
            goalFilterButton = goal;
            Filters.Children.Add(goal);
        }
        GoalFilterLabel = view.GoalFilterLabel;
        FilterLabels = view.Filters.Select(f => f.Label).ToArray();
        // The view switch shows which view is on, outlined like a filter chip.
        if (listSwitch is not null) listSwitch.BorderBrush = view.Matrix ? Brushes.Transparent : PickedEdge;
        if (matrixSwitch is not null) matrixSwitch.BorderBrush = view.Matrix ? PickedEdge : Brushes.Transparent;
        WorkspaceFilterLabel = view.WorkspaceFilterLabel;
    }

    // The List / Matrix switch (Task 12, Ruling 28): two chips, the one on show outlined (DrawFilters).
    Button? listSwitch;
    Button? matrixSwitch;

    // The view switch and "+ Add", made once. The title bar's X closes the window, so the toolbar has no close button
    // of its own.
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
    }

    UIElement Header(TaskGroupView group) => new TextBlock
    {
        Tag = "task-group",
        Text = group.Header.ToUpperInvariant(),
        FontSize = 10,
        Foreground = group.Kind == TaskGroupKind.WaitingOnYou ? Orange : HeaderInk,
        Margin = new Thickness(12, 10, 12, 3),
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
        (compactLines ? CompactColumns : LineColumns).ToList().ForEach(width => grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width }));
        Cell(grid, 0, LeftOf(row));
        Cell(grid, 1, TextOf(row));
        var flags = FlagsOf(row);
        Cell(grid, 2, flags);
        // A Matrix line (compact) has no column for the sessions, the age or the close button, so they are not made at
        // all (whole-PR review, minor 5): no element in a zero-width column, and no age for the minute's tick to rewrite.
        Cell(grid, 3, compactLines ? null : SessionsOf(row));
        Cell(grid, 4, compactLines ? null : AgeOf(row));
        Cell(grid, 5, Chip(row.Place));
        var comments = CommentsButton(row);
        Cell(grid, 6, comments);
        Cell(grid, 7, compactLines ? null : ActionOf(row));
        var more = Small(new TextBlock { Text = "", FontFamily = IconFont, FontSize = 12, Foreground = Ink }, "More",
            () => OpenMenuFor(row.Id), "Status, move, rename, delete");
        menuButtons[row.Id] = more;
        Cell(grid, 8, more);
        if (row.Closing)
            grid.Children.OfType<UIElement>().Where(part => Grid.GetColumn(part) != 0).ToList().ForEach(part => part.Opacity = 0.45);
        // The comments and "…" buttons are quiet until the pointer is on their line, like the bar's quiet controls.
        // A line in its undo window keeps its own dimming (above): its quiet buttons are not brightened by a hover or a
        // selection (review minor).
        var quiet = row.Closing ? Array.Empty<UIElement>()
            : new UIElement[] { comments, more }.Concat(flags?.Children.OfType<UIElement>() ?? []).ToArray();
        Quieten(quiet, loud: false);

        // The selection's frame (ruling 21, as on the bar): 2 DIP on the left, clear unless this is the task Petre
        // picked, then the text box's blue over a fill lighter than hover. Always there, so a pick moves nothing; a
        // change of selection repaints it in place (PaintSelection), not the lines.
        var edge = new Border
        {
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = row.Claudes ? Orange : Brushes.Transparent,
            Background = row.Status == TaskStatus.WaitingOnYou && !row.Closing ? AskTint : Brushes.Transparent,
            Padding = new Thickness(7, 5, 8, 5),
            Child = grid,
        };
        var frame = new Border { BorderThickness = new Thickness(2, 0, 0, 0), Child = edge };
        selectionFrames[row.Id] = (frame, quiet);
        PaintFrame(row.Id);
        var line = new Button
        {
            Tag = row.Id,
            Content = frame,
            Template = (ControlTemplate)FindResource("LineButton"),
            Background = Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Focusable = false,
            Cursor = Cursors.Hand,
            ToolTip = row.Closing ? "Closed. Undo brings it back." : $"{TaskViews.StatusName(row.Status)}: {row.Title}",
        };
        AutomationProperties.SetName(line, row.Title);
        line.MouseEnter += (_, _) => Quieten(quiet, loud: true);
        line.MouseLeave += (_, _) => Quieten(quiet, loud: Picked(row.Id));
        // The click guard, as on the line's buttons (ClickIgnored); and the press that may become a drag of the line
        // (TasksWindow.Drag.cs), armed only, so a press released in place stays this Click.
        line.PreviewMouseLeftButtonDown += (_, e) =>
        {
            pressClicks = e.ClickCount;
            DragPress(row.Id, DragMouse.Position(e, DragSurface));
        };
        dragLines[row.Id] = (row.Quadrant, row.Status == TaskStatus.WaitingOnYou && !row.Closing, row.Closing || row.Status == TaskStatus.Closed);
        line.Click += (_, _) =>
        {
            // A line just closed goes nowhere; Undo is the one thing on it to press. A line being renamed neither.
            if (ClickIgnored() || row.Closing || renaming == row.Id) return;
            Actions?.Go(row.Id);
        };
        return line;
    }

    // The line's columns: tick, title (the star, trimming first), the two flag toggles, sessions, age, workspace chip,
    // comments, action (close or Reopen), "…". Fixed widths, shared by every line, so each column sits at the same x on
    // every line. The sessions column is narrow (its words trim; it is empty on a note) and the window's minimum width
    // is 560 DIP, so the title keeps a usable width there even with the list's scroll bar showing (review I-3).
    static readonly IReadOnlyList<GridLength> LineColumns =
        [new(22), new(1, GridUnitType.Star), new(38), new(64), new(44), new(88), new(40), new(52), new(24)];

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
        return panel;
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
        ages.Values.ToList().ForEach(age => age.Text.Text = TaskAge.Format(now - age.Created));
    }

    internal string AgeText(Guid id) => ages.TryGetValue(id, out var age) ? age.Text.Text : "";

    TextBlock AgeOf(TaskRowView row)
    {
        var text = new TextBlock
        {
            Tag = "task-age",
            Text = TaskAge.Format(Now() - row.Created),
            FontSize = 10.5,
            Foreground = HeaderInk,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 6, 0),
            ToolTip = row.Created.ToLocalTime().ToString("dddd d MMMM yyyy, HH:mm"),
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

    // Claude's badge, a note's tick box, or Undo on a line just closed.
    UIElement LeftOf(TaskRowView row) =>
        row.Closing
            // A sign, not a word (Petre: "undo sign instead of text"), the bar's own (FloatingBar.UndoSign).
            ? Small(FloatingBar.UndoSign(12, Ink), "Undo", () => Actions?.Undo(row.Id), "Undo", DoubleClickIgnored)
            : row.Claudes
                ? new Border
                {
                    Background = Orange,
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(3, 0, 3, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = "CL", FontSize = 9, FontWeight = FontWeights.Bold, Foreground = BadgeInk },
                    ToolTip = "Claude's task",
                }
                : row.Status == TaskStatus.Closed
                    ? new TextBlock()
                    : Small(new Border
                    {
                        Width = 11,
                        Height = 11,
                        BorderBrush = TickEdge,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(3),
                    }, "Close", () => Actions?.Close(row.Id), "Tick off: closes the note");

    // The title over its detail; on a line being renamed, the box that renames it.
    UIElement TextOf(TaskRowView row)
    {
        var panel = new StackPanel { Margin = new Thickness(4, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        if (renaming == row.Id)
        {
            renameBox = RenameBox(row);
            panel.Children.Add(renameBox);
        }
        else
            panel.Children.Add(new TextBlock
            {
                Text = row.Title,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Foreground = row.Claudes ? ClaudeInk : Ink,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextDecorations = row.Closing ? TextDecorations.Strikethrough : null,
            });
        if (row.Detail.Length > 0)
            panel.Children.Add(new TextBlock
            {
                Text = row.Detail,
                FontSize = 11,
                Foreground = row.Status == TaskStatus.WaitingOnYou ? Orange : DimInk,
                FontWeight = row.Status == TaskStatus.WaitingOnYou ? FontWeights.SemiBold : FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        return panel;
    }

    // The sessions, a square each (blue while live), then the words: "2 sessions, 1 live". Empty for a note.
    UIElement? SessionsOf(TaskRowView row)
    {
        if (row.Sessions.Length == 0) return null;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Enumerable.Range(0, Math.Min(SessionSquares, row.SessionCount)).ToList().ForEach(i => panel.Children.Add(new Border
        {
            Width = 10,
            Height = 10,
            CornerRadius = new CornerRadius(2),
            Background = i < row.LiveCount ? LiveSession : EndedSession,
            Margin = new Thickness(0, 0, 3, 0),
        }));
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

    // A done Claude task's close button (it waits for Petre to look and close it), or Reopen on a closed task.
    UIElement? ActionOf(TaskRowView row) =>
        row.Closing ? null
        : row.Status == TaskStatus.Closed ? Small("Reopen", "Reopen", () => Actions?.Reopen(row.Id), "Bring the task back")
        : row.Status == TaskStatus.Done && row.Claudes ? Small("✕", "Close", () => Actions?.Close(row.Id), "Done: close this task")
        : null;

    // --- the thread --------------------------------------------------------------------------------------------------

    // The comments, each with its author and time over its markdown, then a box for Petre's next one.
    UIElement Thread(TaskRowView row)
    {
        var panel = new StackPanel();
        row.Comments.ToList().ForEach(comment => panel.Children.Add(CommentOf(comment)));
        panel.Children.Add(CommentBox(row.Id));
        authors[row.Id] = row.Comments.Select(c => c.Author).ToArray();
        return new Border
        {
            Background = ThreadFill,
            BorderBrush = ThreadEdge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8),
            Margin = new Thickness(43, 0, 10, 6),
            Child = panel,
        };
    }

    UIElement CommentOf(TaskCommentView comment)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock { Tag = "comment-author", Text = comment.Author, FontWeight = FontWeights.Bold, FontSize = 11, Foreground = comment.ByClaude ? ClaudeInk : Ink });
        header.Children.Add(new TextBlock { Text = $" · {comment.When}", FontSize = 11, Foreground = HeaderInk });
        var document = MarkdownBlocks.Render(comment.Markdown);
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
        return new StackPanel { Margin = new Thickness(0, 0, 0, 8), Children = { header, body } };
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
    internal const string CommentHint = "Markdown works. Enter adds, Shift+Enter starts a new line.";

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

    // Posts the box's text as Petre's comment. The box is emptied first, so the redraw the new comment causes is not
    // held back by text that is already posted; a refusal puts the text back.
    bool Post(Guid id)
    {
        var text = CommentDraft(id);
        if (string.IsNullOrWhiteSpace(text) || Actions is null) return false;
        drafts.Remove(id);
        if (commentBoxes.TryGetValue(id, out var box)) box.Text = "";
        if (Actions.Comment(id, text)) return true;
        drafts[id] = text;
        if (commentBoxes.TryGetValue(id, out var still)) still.Text = text;
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

    TextBox RenameBox(TaskRowView row)
    {
        var box = new TextBox
        {
            Text = row.Title,
            Template = (ControlTemplate)FindResource("BareTextBox"),
            Background = Frozen(0xFF, 0x15, 0x16, 0x1A),
            Foreground = Ink,
            CaretBrush = Brushes.White,
            BorderBrush = PickedEdge,
            BorderThickness = new Thickness(1),
            FontSize = 12,
            Padding = new Thickness(3, 1, 3, 1),
        };
        AutomationProperties.SetName(box, "Rename task");
        box.Loaded += (_, _) => box.SelectAll();
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Return)) return;
            e.Handled = true;
            if (Actions?.Rename(row.Id, box.Text) != false) EndRename();
        };
        // A click elsewhere abandons the rename; only Enter keeps it.
        box.LostKeyboardFocus += (_, _) =>
        {
            if (renameBox == box && !drawing) EndRename();
        };
        return box;
    }

    internal void BeginRename(Guid id)
    {
        renaming = id;
        if (latest is not null) Draw(latest);
    }

    void EndRename()
    {
        renaming = null;
        renameBox = null;
        if (latest is not null) Present(latest);
    }

    // --- menus ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A line's "⋯" menu: status, close or reopen, rename, move, delete. Internal so the tests can open one and check a
    /// redraw waits for it.
    /// </summary>
    internal ContextMenu OpenMenuFor(Guid id)
    {
        var row = latest?.Groups.SelectMany(g => g.Rows).FirstOrDefault(r => r.Id == id);
        var menu = new ContextMenu { PlacementTarget = menuButtons.GetValueOrDefault(id), Placement = PlacementMode.Bottom };
        if (row is null) return menu;

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
        menu.Items.Add(Item("Rename…", () => BeginRename(id)));
        // The task list's one workspace menu (WorkspaceChipMenu), the move the bar's line chip makes. Its own menu,
        // opened under the line's "⋯" once this one has closed: the compact chip menu is a context menu of its own, not
        // a submenu.
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
    // every workspace shown, no workspace chip is: an id no workspace has stands for "none of these".
    ContextMenu WorkspaceFilterMenu(TasksView view, Button anchor) =>
        WorkspaceChipMenu.Build(view.Places, view.Where.Any ? Maybe.From(Guid.Empty) : view.Where.Workspace,
            workspace => Actions?.FilterWorkspace(TaskWorkspaceFilter.On(workspace)), anchor,
            extra: [new WorkspaceChipMenu.Extra("All workspaces", view.Where.Any, () => Actions?.FilterWorkspace(TaskWorkspaceFilter.All))]);

    // --- goals (goals plan, Task 6) -------------------------------------------------------------------------------------

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
        AddRow.Visibility = Visibility.Collapsed;
    }

    // The chip saying where the note goes: the one picked from its menu, else the view's (the filtered workspace, or
    // the workspace in front).
    void PaintAddPlace()
    {
        AddEnd.Children.Clear();
        var target = addChosen ?? latest?.AddTarget;
        if (target is null) return;
        var chip = Flat(new TextBlock { Text = $"on {target.Name} ▾", Foreground = Ink, FontSize = 11 }, "Add to workspace", () => { }, new Thickness(8, 2, 8, 2));
        chip.Margin = new Thickness(8, 0, 0, 0);
        chip.Click += (_, _) =>
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
        };
        AddEnd.Children.Add(chip);
    }

    KeyOutcome PressInAdd(Key key)
    {
        if (key is not (Key.Enter or Key.Return)) return KeyOutcome.Ignored;
        var target = addChosen ?? latest?.AddTarget;
        if (Actions is null || target is null || !Actions.AddNote(AddBox.Text, target.Workspace)) return KeyOutcome.NewLine;
        HideAdd();
        return KeyOutcome.Posted;
    }

    // For the tests: types into the add box and presses a key.
    internal bool TypeNote(string text, Key key)
    {
        AddBox.Text = text;
        return PressInAdd(key) == KeyOutcome.Posted;
    }

    // Esc undoes the innermost thing: a rename, else the add box. It does not close the page: a page of its own closes
    // from its title bar, like any other window.
    bool Escape()
    {
        if (renaming is not null) EndRename();
        else if (AddBoxShown) HideAdd();
        else return false;
        return true;
    }

    // --- small parts -------------------------------------------------------------------------------------------------

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

    // --- placement ---------------------------------------------------------------------------------------------------

    // On the bar's screen, in that screen's own DPI (Quick find's reason: a window-scoped DPI query can be stale right
    // after Show), at the preferred size or as much of it as the work area has, clear of the bar (PlaceNear). Opened
    // without a bar, centred across the screen of the window that was in front, an eighth of the way down.
    void Place()
    {
        if (Quiet)
        {
            Left = Top = -32000;
            return;
        }
        var monitor = near.IsEmpty
            ? NativeMethods.MonitorFromWindow(fallbackAnchor != 0 ? fallbackAnchor : new WindowInteropHelper(this).Handle, NativeMethods.MONITOR_DEFAULTTONEAREST)
            : NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = (int)(near.Left + near.Width / 2), Y = (int)(near.Top + near.Height / 2) }, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return;
        NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out var dpiY);
        var (scaleX, scaleY) = (dpiX / 96.0, dpiY / 96.0);
        var work = new Rect(info.rcWork.Left / scaleX, info.rcWork.Top / scaleY,
            (info.rcWork.Right - info.rcWork.Left) / scaleX, (info.rcWork.Bottom - info.rcWork.Top) / scaleY);
        Width = Math.Max(320, Math.Min(PreferredWidth, work.Width - 2 * Gap));
        Height = Math.Max(240, Math.Min(PreferredHeight, work.Height - 2 * Gap));

        if (near.IsEmpty)
        {
            Left = work.Left + Math.Max(0, (work.Width - Width) / 2);
            Top = work.Top + Math.Max(0, Math.Min(work.Height / 8, work.Height - Height));
            return;
        }
        var bar = new Rect(near.Left / scaleX, near.Top / scaleY, near.Width / scaleX, near.Height / scaleY);
        var at = PlaceNear(bar, work, new Size(Width, Height));
        Left = at.X;
        Top = at.Y;
    }

    /// <summary>
    /// The arithmetic of Place, in DIPs, apart so the tests can check it without a screen: the window's top left for a
    /// bar covering <paramref name="bar"/> on a screen whose work area is <paramref name="work"/>. Adapted from the
    /// abandoned task window's PlaceUnder (commit 183d85e), whose reasons hold here: the bar re-asserts itself at the
    /// top of the topmost band every second (FloatingBar.ReclaimTopmost), so any part of this window over the bar is
    /// covered within a second; and a docked bar is an app bar, outside the work area. Tried in this order, each kept
    /// inside the work area:
    ///   below the bar    left edges lined up (the list and its "+N more" are at the bar's foot)
    ///   above the bar    a bar near the bottom of the screen, or docked along it
    ///   beside the bar   a bar docked down the left or right edge: on its left when that fits, else on its right, top
    ///                    edges lined up
    ///   anywhere         a screen too small for any of those: as high as the work area allows above the bar, the one
    ///                    case where an overlap cannot be helped
    /// </summary>
    internal static Point PlaceNear(Rect bar, Rect work, Size size)
    {
        var (width, height) = (size.Width, size.Height);
        var (minLeft, maxLeft) = (work.Left, Math.Max(work.Left, work.Right - width));
        var (minTop, maxTop) = (work.Top, Math.Max(work.Top, work.Bottom - height));
        var alignedLeft = Math.Clamp(bar.Left, minLeft, maxLeft);

        var below = Math.Max(bar.Bottom + Gap, work.Top);
        if (below + height <= work.Bottom) return new Point(alignedLeft, below);

        var above = Math.Min(bar.Top - Gap, work.Bottom) - height;
        if (above >= work.Top) return new Point(alignedLeft, above);

        var besideTop = Math.Clamp(bar.Top, minTop, maxTop);
        var leftOf = Math.Min(bar.Left - Gap, work.Right) - width;
        if (leftOf >= work.Left) return new Point(leftOf, besideTop);
        var rightOf = Math.Max(bar.Right + Gap, work.Left);
        if (rightOf + width <= work.Right) return new Point(rightOf, besideTop);

        return new Point(alignedLeft, Math.Clamp(above, minTop, maxTop));
    }
}
