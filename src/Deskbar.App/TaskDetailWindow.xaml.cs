using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using CSharpFunctionalExtensions;
using Deskbar.Core.Persistence;
using Deskbar.Core.Tasks;
using Deskbar.Windows.Monitoring;

namespace Deskbar.App;

// A TASK IN A WINDOW OF ITS OWN (task dca4ebeb; design docs/superpowers/specs/2026-11-25-task-window-design.md). The
// XAML's header comment says what it looks like and why it exists; this file draws it and passes every click back.
//
// WHO DRIVES IT. TasksBoard makes one per task (OpenWindow), hands it the panel's own TasksWindowActions, and calls
// Present with the task as it is now on every redraw of the board. So a click here is the same TasksBoard method a click
// in the panel is, and obeys the same rules. The window never looks a task up itself.
//
// WHEN IT REDRAWS. Present draws only what changed since the last draw (a signature of everything shown), and waits while
// one of its menus is open or one of Petre's comments is being edited in place, then draws the latest view. The comment
// box and the description editor are built once by the XAML and never redrawn, so a session posting while Petre writes
// does not take his words away; the description shown beneath the editor still follows the task.
//
// EDITING (Petre's pick: "edit in the window"). Edit, F2 or Ctrl+E turn the title into a box and the description into a
// large markdown box with a Preview tab; Ctrl+S or Ctrl+Enter save through TasksWindowActions.Edit, Esc cancels.
// Pictures pasted or dropped into the description wait as thumbnails, each with its x, and the ones already in the
// description are kept unless taken out, as in the panel's editor (TasksWindow.Images.cs, which has the rules).
//
// ITS PLACE (Petre: "remember size and position"). One place shared by every task window, AppState.TaskWindow, as
// WINDOWPLACEMENT keeps it (device pixels, maximized or not). Applied as the window's handle is made (OnSourceInitialized,
// the moment WPF lets a placement stand), offset a little for each other task window already open (`cascade`), and saved
// whenever a move or resize ends, the window is maximized or restored, or it closes.
public partial class TaskDetailWindow : Window
{
    // UI Automation sees this window and nothing inside it, so WPF stops walking its element tree after every
    // layout pass (QuietAutomation.cs has the measurement: 1.45 s freezes on a busy machine).
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new QuietWindowPeer(this);

    // The panel's colours (TasksWindow), so a task reads the same in both.
    static readonly Brush Ink = Frozen(0xFF, 0xE6, 0xE6, 0xE6);
    static readonly Brush DimInk = Frozen(0xFF, 0xA9, 0xAD, 0xB8);
    static readonly Brush HeaderInk = Frozen(0xFF, 0x8B, 0x8F, 0x99);
    static readonly Brush Orange = Frozen(0xFF, FloatingBar.AskingOrange.R, FloatingBar.AskingOrange.G, FloatingBar.AskingOrange.B);
    static readonly Brush AskTint = Frozen(0xFF, 0x3A, 0x2C, 0x1C);
    static readonly Brush DetailFill = Frozen(0xFF, 0x24, 0x26, 0x2C);
    static readonly Brush ButtonFill = Frozen(0xFF, 0x2A, 0x2C, 0x33);
    static readonly Brush PrimaryFill = Frozen(0xFF, 0x4F, 0x8C, 0xFF);
    static readonly Brush TileFill = Frozen(0xFF, 0x24, 0x26, 0x2C);
    static readonly Brush ThreadEdge = Frozen(0xFF, 0x33, 0x35, 0x3C);
    static readonly Brush ClaudeInk = Frozen(0xFF, 0xD9, 0xB9, 0x8A);
    static readonly Brush LiveSession = Frozen(0xFF, 0x3B, 0x82, 0xF6);
    static readonly Brush EndedSession = Frozen(0xFF, 0x4B, 0x4F, 0x5A);
    static readonly Brush ImportantInk = Frozen(0xFF, 0xFF, 0x6B, 0x6B);
    static readonly Brush UrgentInk = Frozen(0xFF, 0xFF, 0xD5, 0x4F);
    static readonly Brush StatusFill = Frozen(0x22, 0x4F, 0x8C, 0xFF);
    static readonly Brush StatusInk = Frozen(0xFF, 0xBF, 0xD3, 0xFF);

    // The statuses Petre sets by hand from the "⋯" menu; Closed is Close's, as in the panel.
    static readonly IReadOnlyList<TaskStatus> SettableStatuses = [TaskStatus.Backlog, TaskStatus.InProgress, TaskStatus.WaitingOnYou, TaskStatus.Done];

    /// <summary>From this width (DIP) the sessions, subtasks and details stand in a column of their own on the right.</summary>
    internal const double SideBySideFrom = 1100;

    /// <summary>How far (device pixels) each further task window open at once is moved down and right from the saved place.</summary>
    internal const int CascadeStep = 28;

    // What was drawn last, and the newest view not drawn yet while a menu or a comment edit holds the redraw.
    TaskDetailView? latest;
    TaskDetailView? waiting;
    string drawn = "";
    bool menuOpen;
    bool closingForGood;
    // The side column's place: beside the thread (true) or under it.
    bool sideBySide = true;

    // The description editor: open or not, showing the markdown or its preview, and its pictures (kept and new).
    bool editing;
    bool previewing;
    readonly List<string> editorKept = [];
    readonly List<TaskImageFiles.Pending> editorNew = [];

    // Pictures waiting in the comment box, and the comment being edited in place (one of Petre's own), with its box.
    readonly List<TaskImageFiles.Pending> commentPictures = [];
    Guid? editingComment;
    TextBox? commentEditor;

    // Whether the thread shows its settled comments.
    bool settledOpen;

    // The placement to apply when the handle is made (Open), and the cascade offset for it.
    TaskWindowState? placeToApply;
    int cascadeBy;

    public TaskDetailWindow()
    {
        InitializeComponent();
        CommentBox.TextChanged += (_, _) => CommentPlaceholder.Visibility = CommentBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        CommentBox.PreviewKeyDown += (_, e) =>
        {
            // Ctrl+Enter posts; a plain Enter is a new line, since a comment written here is often long.
            if (e.Key is not (Key.Enter or Key.Return) || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
            e.Handled = true;
            Post();
        };
        PicturePaste.Take(CommentBox, PasteIntoComment);
        PicturePaste.Take(DescriptionBox, PasteIntoDescription);
        CommentEnd.Children.Add(Flat("Comment", "Post comment", () => Post(), primary: true));
        EditEnd.Children.Add(Flat("Cancel", "Cancel editing", EndEdit));
        var save = Flat("Save", "Save the task", () => SaveEdit(), primary: true);
        save.Margin = new Thickness(6, 0, 0, 0);
        EditEnd.Children.Add(save);
        PreviewKeyDown += (_, e) => e.Handled = Press(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers);
        SizeChanged += (_, _) => PlaceSide();
        StateChanged += (_, _) => SavePlace();
    }

    /// <summary>Every click goes back through these, the panel's own (TasksBoard hands them in).</summary>
    internal TasksWindowActions? Actions { get; set; }

    /// <summary>Saves the one place every task window shares (AppState.TaskWindow); set by TasksBoard.</summary>
    internal Action<TaskWindowState>? PlaceChanged { get; set; }

    /// <summary>The window is closing, by Petre or because its task was deleted: TasksBoard lets go of it.</summary>
    internal Action? Leaving { get; set; }

    // Pictures are saved under, and read from, the app's state folder; a click on one enlarges it in a lightbox
    // (PictureViewer, task e5d85e5e). Seams, as in the panel, so the tests use a scratch folder and record the click.
    internal string StateDir { get; init; } = AppPaths.NewStateDir;

    internal Action<string> OpenImage { get; init; } = PictureViewer.Show;

    internal Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>The task this window shows (Guid.Empty before the first Present).</summary>
    internal Guid TaskId => latest?.Row.Id ?? Guid.Empty;

    // A click on a link in this task's text (task 26f15d70): the controller's OpenLink, which goes to the task's workspace
    // before opening it. The task's id is read at the click, not when the link was drawn. Without a controller (a test
    // that builds the window alone), MarkdownBlocks' own default: the desktop in front.
    void OpenLink(Uri uri)
    {
        if (Actions?.OpenLink is { } open) open(TaskId, uri);
        else MarkdownBlocks.Open(uri);
    }

    /// <summary>For the tests: whether the description editor is open.</summary>
    internal bool IsEditing => editing;

    /// <summary>The window's handle, made if it is not yet.</summary>
    internal nint EnsureHandle() => new WindowInteropHelper(this).EnsureHandle();

    // A TILE, NOT A FLOATING WINDOW (task 4c746a42, Petre: "detail window should not hover on top, but be docked as
    // regular windows are"). The tiler keeps every window of this process out of the grid (TasksBoard.KeptFromTiling),
    // which is right for the bar, Manage, the dialogs and the docked panel, but left a task window floating over the
    // tiles of a tiled workspace. A task window is an ordinary window (title bar, taskbar, Alt+Tab), so it tiles like
    // one. The tiler asks by handle, so each task window's handle is listed here from the moment it exists until it is
    // gone. An immutable set swapped whole, so a reader on any thread sees one consistent set without a lock.
    static System.Collections.Immutable.ImmutableHashSet<nint> openHandles = [];

    /// <summary>Whether the handle is a task window's (TasksBoard.KeptFromTiling lets these tile).</summary>
    internal static bool IsTaskWindow(nint hwnd) => openHandles.Contains(hwnd);

    // This window's own entry in openHandles, kept so OnClosed removes exactly what OnSourceInitialized added.
    nint listedHandle;

    // --- showing ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Shows the window for the first time: at the shared saved place, moved `cascade` steps down and right when other
    /// task windows are already open, or at the default size centred on the screen when nothing was saved yet.
    /// </summary>
    internal void Open(TaskWindowState? place, int cascade)
    {
        placeToApply = place;
        cascadeBy = cascade;
        // Centred on the screen only the first time, with nothing saved; a saved place is applied as the handle is made.
        WindowStartupLocation = place is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.Manual;
        Show();
    }

    // WPF lets a placement stand only once the handle exists and before the window is first shown, which is here.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        // Listed as a task window before it is first shown, so the tiler's first look at it already lets it tile; let go
        // of once the handle is destroyed (OnClosed), so a recycled handle is never mistaken for one.
        listedHandle = hwnd;
        System.Collections.Immutable.ImmutableInterlocked.Update(ref openHandles, set => set.Add(hwnd));
        // The title bar in the dark, to match the window (DWMWA_USE_IMMERSIVE_DARK_MODE, Windows 11 and late Windows 10).
        var dark = 1;
        _ = NativeMethods.DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref dark, sizeof(int));
        HwndSource.FromHwnd(hwnd)?.AddHook(Hook);
        if (placeToApply is not { } place) return;
        var offset = cascadeBy * CascadeStep;
        var placement = new NativeMethods.WINDOWPLACEMENT
        {
            length = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>(),
            // Normal first, and maximized after, so a maximized window still remembers the rectangle it restores to. A
            // cascaded one opens restored: two maximized windows exactly over each other hide the first.
            showCmd = ShowNormal,
            rcNormalPosition = new NativeMethods.RECT
            {
                Left = place.Left + offset, Top = place.Top + offset, Right = place.Right + offset, Bottom = place.Bottom + offset,
            },
        };
        // SetWindowPlacement also pulls a rectangle no screen holds any more (a screen since unplugged) back onto one.
        NativeMethods.SetWindowPlacement(hwnd, ref placement);
        if (place.Maximized && cascadeBy == 0) WindowState = WindowState.Maximized;
    }

    const uint DwmUseImmersiveDarkMode = 20;
    const int ShowNormal = 1;
    const int ShowMaximized = 3;
    const int WmExitSizeMove = 0x0232;

    // A move or resize by the title bar or an edge ends with WM_EXITSIZEMOVE: the place is saved then, once, rather than
    // on every step of the drag.
    nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmExitSizeMove) SavePlace();
        return 0;
    }

    // The window's place as WINDOWPLACEMENT keeps it, saved as the one place every task window opens at.
    void SavePlace()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0 || PlaceChanged is null) return;
        var placement = new NativeMethods.WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>() };
        if (!NativeMethods.GetWindowPlacement(hwnd, ref placement)) return;
        var rect = placement.rcNormalPosition;
        if (rect.Right <= rect.Left || rect.Bottom <= rect.Top) return;
        PlaceChanged(new TaskWindowState(rect.Left, rect.Top, rect.Right, rect.Bottom, placement.showCmd == ShowMaximized));
    }

    /// <summary>Activates the window where it is (no seam: the tests, or a copy with no activator).</summary>
    internal void ActivateHere()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Why the last action failed, until the next one works; null clears it.</summary>
    internal void ShowProblem(string? text)
    {
        ProblemText.Text = text ?? "";
        ProblemText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Closing with the editor open keeps nothing, as Esc would; the place is saved as it is.
        SavePlace();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        // No longer a task window: Windows may hand this handle to another window now. The handle as it was listed,
        // since the interop helper's may already read zero while the window is being destroyed.
        var listed = listedHandle;
        System.Collections.Immutable.ImmutableInterlocked.Update(ref openHandles, set => set.Remove(listed));
        Leaving?.Invoke();
    }

    /// <summary>Closes the window: its task was deleted, or the app is going.</summary>
    internal void CloseForGood()
    {
        if (closingForGood) return;
        closingForGood = true;
        Close();
    }

    // --- drawing -----------------------------------------------------------------------------------------------------

    /// <summary>Draws the task as it is now, or nothing when what it shows has not changed. Waits while a menu is open
    /// or a comment is being edited in place, then draws the newest view.</summary>
    internal void Present(TaskDetailView view)
    {
        if (menuOpen || editingComment is not null)
        {
            waiting = view;
            return;
        }
        waiting = null;
        latest = view;
        var signature = Signature(view);
        if (signature == drawn) return;
        drawn = signature;
        Draw(view);
    }

    // A held redraw, once nothing holds it any more.
    void Flush()
    {
        if (waiting is { } view) Present(view);
    }

    // Everything the window shows, as text, compared and never parsed. The age is in whole minutes, so it moves once a
    // minute at most and only while something else redraws.
    static string Signature(TaskDetailView view)
    {
        var r = view.Row;
        return string.Join('\u001f',
            r.Id, r.Title, r.Detail, r.Status, r.Claudes, r.Place.Name, r.Place.Colour?.ToString() ?? "", r.Closing, r.Assignee.Id,
            r.Description, r.Important, r.Urgent, r.SnoozedUntil?.ToString("O") ?? "", r.RepeatText ?? "", r.NextRun?.ToString("O") ?? "", r.Goal?.Title ?? "", r.ParentTitle ?? "",
            // The parent's id too (task 54b39c4c): the Parent button opens it, so a move to a namesake parent redraws.
            r.ParentId?.ToString() ?? "",
            // Whether a Done task's change runs in this Deskbar yet (task 8470d176): the status chip's word and tooltip.
            r.Delivery.Stage, r.Delivery.Note,
            // The pull requests section (task e7aa49b1): a new one redraws.
            string.Join('\u001e', r.PullRequests.Select(p => p.Key)),
            r.ClaudeWorking, r.WorkingCount,
            string.Join('\u001e', r.Comments.Select(c => $"{c.Id}|{c.Author}|{c.When}|{c.Settled}|{c.Editable}|{c.Markdown}")),
            string.Join('\u001e', r.SessionList.Select(s => $"{s.Id}|{s.State}|{s.Joined:O}")),
            string.Join('\u001e', r.Subtasks.Select(s => $"{s.Id}|{s.Title}|{s.Status}|{s.StatusLine}")),
            string.Join('\u001e', view.Places.Select(p => p.ToString())),
            (int)(view.Now - r.Created).TotalMinutes);
    }

    void Draw(TaskDetailView view)
    {
        var row = view.Row;
        Title = $"{FloatingBar.FirstLine(row.Title)} · Deskbar";
        TitleText.Text = row.Title;
        TitleText.Foreground = row.Claudes ? ClaudeInk : Ink;
        TitleText.TextDecorations = row.Closing ? TextDecorations.Strikethrough : null;
        DrawHeaderEnd(row);
        DrawMeta(view);
        DrawDetail(row);
        DrawDescription(row);
        DrawThread(row);
        DrawSide(view);
    }

    // Edit (not on a closed task: Reopen brings it back first, as in the panel) and the "⋯" menu.
    void DrawHeaderEnd(TaskRowView row)
    {
        HeaderEnd.Children.Clear();
        if (!editing && row.Status != TaskStatus.Closed && !row.Closing)
            HeaderEnd.Children.Add(Flat("Edit", "Edit the task", BeginEdit, tip: "Edit the title and description (F2)"));
        Button? more = null;
        more = Flat("⋯", "More", () => OpenMenu(more), tip: "Status, assign, move, delete");
        more.Margin = new Thickness(6, 0, 0, 0);
        HeaderEnd.Children.Add(more);
    }

    // The chips under the title: status (with Undo while a close can still be undone), workspace in its lane colour,
    // assignee, the two flags as toggles, snoozed, age.
    void DrawMeta(TaskDetailView view)
    {
        var row = view.Row;
        Meta.Children.Clear();
        // A Done task whose change is not in this Deskbar yet says "Merged" (or "PR open"), as the panel's chip does, and
        // its tooltip says where the change stands (task 8470d176, TaskDelivery).
        var status = Chip(row.Closing ? "Closed" : DeliveryWord(row), StatusFill, StatusInk);
        if (!row.Closing && row.Delivery.Note.Length > 0) status.ToolTip = row.Delivery.Note;
        Meta.Children.Add(status);
        if (row.Closing && Actions is { } undoing)
            Meta.Children.Add(MetaButton(Flat("Undo", "Undo", () => undoing.Undo(row.Id), tip: "Undo the close")));
        Meta.Children.Add(PlaceChip(row));
        Meta.Children.Add(Chip(row.Assignee.Name, ButtonFill, row.Assignee == Assignee.Claude ? ClaudeInk : DimInk));
        Meta.Children.Add(FlagChip(row, TaskFlag.Important, "Important", row.Important, ImportantInk));
        Meta.Children.Add(FlagChip(row, TaskFlag.Urgent, "Urgent", row.Urgent, UrgentInk));
        if (row.SnoozedUntil is { } until)
            Meta.Children.Add(Chip($"Snoozed {TaskSnooze.UntilText(until.ToLocalTime(), view.Now.ToLocalTime())}", ButtonFill, DimInk));
        Meta.Children.Add(Chip(TaskAge.Words(view.Now - row.Created), ButtonFill, HeaderInk));
    }

    /// <summary>The automation name of the workspace chip while it moves the task, for the tests.</summary>
    internal const string PlaceChipName = "Move to workspace";

    // The workspace chip moves the task (task 3757a3e1, as the panel's and the bar's line chips do): the same chip, now a
    // flat button in the workspace's colour that opens the compact workspace menu under itself (OpenMoveMenu, the menu
    // "⋯ > Move to…" opens). While a close can still be undone the chip stays a plain label, as the panel's does.
    UIElement PlaceChip(TaskRowView row)
    {
        var fill = row.Place.Colour ?? ButtonFill;
        var ink = row.Place.Colour is null ? DimInk : Ink;
        if (row.Closing) return Chip(row.Place.Name, fill, ink);
        var text = new TextBlock { Text = row.Place.Name, FontSize = 11.5, Foreground = ink };
        Button? button = null;
        button = Flat(text, PlaceChipName, () => OpenMoveMenu(button), new Thickness(8, 3, 8, 3),
            tip: $"{row.Place.Name}: move it to another workspace");
        button.Background = fill;
        return MetaButton(button);
    }

    // A flag as a chip that toggles it: lit in its colour while on, dim while off.
    UIElement FlagChip(TaskRowView row, TaskFlag flag, string name, bool on, Brush lit)
    {
        var text = new TextBlock { Text = name, FontSize = 11.5, Foreground = on ? lit : HeaderInk, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal };
        var button = Flat(text, name, () => Actions?.SetFlag(row.Id, flag, !on), new Thickness(8, 3, 8, 3),
            tip: on ? $"Clear {name.ToLowerInvariant()}" : $"Mark {name.ToLowerInvariant()}");
        return MetaButton(button);
    }

    static UIElement MetaButton(Button button)
    {
        button.Margin = new Thickness(0, 0, 6, 6);
        return button;
    }

    // The status chip's word: "Merged" or "PR open" for a Done task whose change is not running here yet (task 8470d176),
    // the status's name otherwise ("Done" while Deskbar is still asking, TaskStatusMarks.ShippingWord).
    static string DeliveryWord(TaskRowView row) =>
        row.Status != TaskStatus.Done || !row.Delivery.Shipping ? TaskViews.StatusName(row.Status)
        : TaskStatusMarks.ShippingWord(row.Delivery.Stage);

    static Border Chip(string text, Brush fill, Brush ink) => new()
    {
        Background = fill,
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(8, 3, 8, 3),
        Margin = new Thickness(0, 0, 6, 6),
        Child = new TextBlock { Text = text, FontSize = 11.5, Foreground = ink },
    };

    // The question while the task waits on Petre (orange, on the waiting tint), else its status line: in full, with its
    // links clickable (MarkdownBlocks.InlinesOf, as the panel's open line draws it).
    //
    // THE QUESTION IS IN THE CHAT (task 968c2216, Petre: "when i open chat, hide the question from the main bar"): this
    // window's thread is always open and shows every unsettled comment, so once the ask wrote the question into it
    // (TaskCommentView.AsksNow) the box leaves the question to the chat and keeps only the rest of the detail, plain.
    void DrawDetail(TaskRowView row)
    {
        var inChat = row.DetailInThread is not null && row.Comments.Any(c => c.AsksNow);
        var detail = inChat ? row.DetailInThread! : row.Detail;
        DetailBox.Visibility = detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var waits = row.Status == TaskStatus.WaitingOnYou && !row.Closing && !inChat;
        DetailBox.Background = waits ? AskTint : DetailFill;
        DetailText.Foreground = waits ? Orange : DimInk;
        DetailText.FontWeight = waits ? FontWeights.SemiBold : FontWeights.Normal;
        DetailText.Inlines.Clear();
        MarkdownBlocks.InlinesOf(detail, OpenLink).ToList().ForEach(DetailText.Inlines.Add);
    }

    // The description: its text rendered, links clickable, then its pictures; a dim line when there is none.
    void DrawDescription(TaskRowView row)
    {
        DescriptionHost.Children.Clear();
        var parts = TaskImages.Split(row.Description);
        if (parts.Text.Trim().Length == 0 && parts.Images.Count == 0)
            DescriptionHost.Children.Add(new TextBlock
            {
                Tag = DescriptionTag,
                Text = row.Status == TaskStatus.Closed ? "No description." : "No description yet. Edit adds one.",
                Foreground = HeaderInk,
                FontSize = 12.5,
            });
        if (parts.Text.Trim().Length > 0) DescriptionHost.Children.Add(Rendered(parts.Text, 13, DescriptionTag));
        if (TaskImageRow.Of(StateDir, parts.Images, OpenImage) is { } pictures) DescriptionHost.Children.Add(pictures);
    }

    /// <summary>The Tag of the rendered description, for the tests.</summary>
    internal const string DescriptionTag = "task-window-description";

    /// <summary>The Tag of one comment in the thread, for the tests.</summary>
    internal const string CommentTag = "task-window-comment";

    // A read-only rendered markdown block whose links are clickable, with nothing drawn around it.
    RichTextBox Rendered(string markdown, double size, string? tag = null)
    {
        var document = MarkdownBlocks.Render(markdown, OpenLink);
        document.FontSize = size;
        document.Foreground = Ink;
        var box = new RichTextBox
        {
            Tag = tag,
            Document = document,
            IsReadOnly = true,
            // Hyperlinks inside a read-only box are clickable only with this set.
            IsDocumentEnabled = true,
            Template = (ControlTemplate)FindResource("BareRichTextBox"),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Ink,
            Padding = new Thickness(0),
            Cursor = Cursors.Arrow,
        };
        return box;
    }

    // The thread: every comment, none cut short and none folded behind "N earlier"; settled ones stay folded behind
    // their own line, as in the panel (Ruling 62), since settling is how Petre says a comment no longer matters.
    void DrawThread(TaskRowView row)
    {
        ThreadHost.Children.Clear();
        var shown = row.Comments.Where(c => !c.Settled).ToArray();
        var settled = row.Comments.Count(c => c.Settled);
        ThreadHost.Children.Add(new TextBlock
        {
            Text = shown.Length == 0 ? "No comments yet" : shown.Length == 1 ? "1 comment" : $"{shown.Length} comments",
            Foreground = HeaderInk,
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 10),
        });
        if (settled > 0)
        {
            var words = settled == 1 ? "1 settled comment" : $"{settled} settled comments";
            var fold = Flat(new TextBlock { Text = settledOpen ? $"Hide {words}" : words, Foreground = HeaderInk, FontSize = 11 },
                "Settled comments", () =>
                {
                    settledOpen = !settledOpen;
                    if (latest is { } view) Draw(view);
                }, new Thickness(8, 2, 8, 2), tip: "Settled comments are kept, folded away");
            fold.HorizontalAlignment = HorizontalAlignment.Left;
            fold.Margin = new Thickness(0, 0, 0, 10);
            ThreadHost.Children.Add(fold);
        }
        // The comments as a chat (task 39bc9c4d, ChatThread), as in the panel: bubbles on two sides, Deskbar's lines as
        // events, a day line above each day's first comment. It replaced the hairline between comments (task aa42794f).
        ChatThread.WithDays(row.Comments.Where(c => settledOpen || !c.Settled).ToArray(), CommentOf).ToList()
            .ForEach(comment => ThreadHost.Children.Add(comment));
    }

    // One comment: a bubble (Petre's on the right, a session's or the manager's on the left) or, for Deskbar's own lines,
    // a centred event, with Edit and Settle by its time on hover (ChatThread). Being edited, it is the editor at the
    // thread's full width under its author and time, as before.
    UIElement CommentOf(TaskCommentView comment)
    {
        var split = TaskImages.Split(comment.Markdown);
        if (editingComment == comment.Id)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new TextBlock { Text = comment.Author, FontWeight = FontWeights.Bold, FontSize = 11.5, Foreground = comment.ByClaude ? ClaudeInk : Ink, VerticalAlignment = VerticalAlignment.Center });
            header.Children.Add(new TextBlock { Text = $" · {comment.When}", FontSize = 11.5, Foreground = HeaderInk, VerticalAlignment = VerticalAlignment.Center });
            return new StackPanel { Tag = CommentTag, Margin = new Thickness(0, 0, 0, 14), Children = { header, CommentEditor(comment) } };
        }
        // Only his own comments are edited (Ruling 58); any can be settled or brought back (Ruling 62).
        var actions = new List<Button>();
        if (comment.Editable)
            actions.Add(Flat(SmallText("Edit"), "Edit comment", () => BeginEditComment(comment), new Thickness(5, 0, 5, 1), tip: "Edit your comment"));
        actions.Add(Flat(SmallText(comment.Settled ? "Unsettle" : "Settle"), comment.Settled ? "Unsettle comment" : "Settle comment",
            () => Actions?.SettleComment(comment.Id, !comment.Settled), new Thickness(5, 0, 5, 1),
            tip: comment.Settled ? "Show it with the thread again" : "Fold this comment away, keeping it"));
        actions.ForEach(button => button.Background = Brushes.Transparent);
        // A question Deskbar asked is a bubble like a session's, not an event line (task 968c2216).
        var isEvent = comment.Who == CommentAuthor.Deskbar && !comment.Asks;
        // A one-line Deskbar event carries its time at its end ("Resumed. · 09:08"); a longer one has it on a line below.
        var timeInBody = isEvent && split.Images.Count == 0 && ChatThread.TimeFitsInline(split.Text);
        var parts = new StackPanel();
        if (split.Text.Trim().Length > 0)
        {
            var body = Rendered(timeInBody ? $"{split.Text.Trim()} · {comment.Time}" : split.Text, isEvent ? 11 : 12.5);
            if (isEvent)
            {
                body.Document.Foreground = HeaderInk;
                body.Document.TextAlignment = TextAlignment.Center;
            }
            // The question waiting on Petre reads as it did on the line: orange, semibold (task 968c2216).
            if (comment.AsksNow)
            {
                body.Document.Foreground = Orange;
                body.Document.FontWeight = FontWeights.SemiBold;
            }
            // As wide as its text, so a short comment gets a short bubble; an event is centred.
            ChatThread.FitWidth(body);
            body.HorizontalAlignment = isEvent ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            parts.Children.Add(body);
        }
        if (TaskImageRow.Of(StateDir, split.Images, OpenImage) is { } pictures) parts.Children.Add(pictures);
        var placed = isEvent ? ChatThread.Event(comment, parts, timeInBody, actions) : ChatThread.Message(comment, parts, actions);
        placed.Tag = CommentTag;
        // A settled comment, when unfolded, is dim: it is history.
        placed.Opacity = comment.Settled ? 0.55 : 1;
        return placed;
    }

    static TextBlock SmallText(string text) => new() { Text = text, Foreground = DimInk, FontSize = 10.5 };

    // --- the side column ---------------------------------------------------------------------------------------------

    // Sessions (each with Open), "Start Claude on this", subtasks (each opening its own window), and the details.
    void DrawSide(TaskDetailView view)
    {
        var row = view.Row;
        SideWide.Children.Clear();
        SideNarrow.Children.Clear();
        var side = sideBySide ? SideWide : SideNarrow;

        side.Children.Add(Section("Sessions"));
        if (row.SessionList.Count == 0)
            side.Children.Add(new TextBlock { Text = "No Claude session yet.", Foreground = HeaderInk, FontSize = 11.5, Margin = new Thickness(0, 0, 0, 6) });
        row.SessionList.ToList().ForEach(session => side.Children.Add(SessionLine(row.Id, session)));
        // Not while a session of the task is running (task 9b6275bc, TaskRowView.HasOpenSession): Open on its line is the
        // way in. The "⋯" menu's item follows the same rule.
        if (Actions?.StartClaude is { } start && row.Status != TaskStatus.Closed && !row.Closing && !row.HasOpenSession)
        {
            var button = Flat(TasksWindow.StartClaudeText, TasksWindow.StartClaudeText, () => start(row.Id),
                tip: "Start a background Claude session on this task, with the task as its first message");
            button.HorizontalAlignment = HorizontalAlignment.Left;
            button.Margin = new Thickness(0, 4, 0, 16);
            side.Children.Add(button);
        }
        else side.Children.Add(new Border { Height = 12 });

        // A SUBTASK'S PARENT (task 54b39c4c; Petre: "subtask should show its parent and have a way to open the parent
        // easily"): a section of its own above the subtasks, a button that opens the parent in a window of its own, as a
        // subtask's button below does. It took the place of the plain "Subtask of" pair the Details grid had, which named
        // the parent and went nowhere.
        if (row.ParentId is { } parentId && row.ParentTitle is { } parentTitle)
        {
            side.Children.Add(Section("Parent"));
            side.Children.Add(ParentEntry(parentId, parentTitle));
            side.Children.Add(new Border { Height = 12 });
        }

        if (row.Subtasks.Count > 0)
        {
            side.Children.Add(Section("Subtasks"));
            row.Subtasks.ToList().ForEach(sub => side.Children.Add(SubtaskEntry(sub)));
            side.Children.Add(new Border { Height = 12 });
        }

        // THE TASK'S PULL REQUESTS (task e7aa49b1): the ones its sessions opened, each a link that opens it in the browser,
        // so the task found by a pull request's number in the search shows the pull request itself too. Drawn as one
        // markdown line per pull request, through the same renderer the thread uses, so the link looks and opens as the
        // thread's links do.
        if (row.PullRequests.Count > 0)
        {
            side.Children.Add(Section("Pull requests"));
            row.PullRequests.ToList().ForEach(p => side.Children.Add(Rendered($"[{p.Label}]({p.Url})", 12.5)));
            side.Children.Add(new Border { Height = 12 });
        }

        side.Children.Add(Section("Details"));
        var details = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        details.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var pairs = new List<(string Label, string Value)>
        {
            ("Quadrant", row.Quadrant.ToString()),
            ("Workspace", row.Place.Name),
            ("Assigned", row.Assignee.Name),
        };
        if (row.Goal is { } goal) pairs.Add(("Goal", $"{goal.Title} ({goal.Period})"));
        pairs.Add(("Added", row.Created.ToLocalTime().ToString("d MMM yyyy HH:mm")));
        pairs.Add(("Id", row.Id.ToString()[..8]));
        pairs.Select((pair, i) => (pair, i)).ToList().ForEach(x =>
        {
            details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = x.pair.Label, Foreground = HeaderInk, FontSize = 11.5, Margin = new Thickness(0, 0, 10, 4) };
            var value = new TextBox
            {
                // A read-only box, so the id (and anything else) can be selected and copied.
                Text = x.pair.Value, IsReadOnly = true, Foreground = DimInk, FontSize = 11.5, Background = Brushes.Transparent,
                BorderThickness = new Thickness(0), Padding = new Thickness(0), TextWrapping = TextWrapping.Wrap,
                Template = (ControlTemplate)FindResource("BareTextBox"), Margin = new Thickness(0, 0, 0, 4),
            };
            Grid.SetRow(label, x.i);
            Grid.SetRow(value, x.i);
            Grid.SetColumn(value, 1);
            details.Children.Add(label);
            details.Children.Add(value);
        });
        side.Children.Add(details);
    }

    static TextBlock Section(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        Foreground = HeaderInk,
        FontSize = 10.5,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 0, 0, 8),
    };

    // One session: a square (blue while live), its short id, where it stands and when it joined, and Open.
    UIElement SessionLine(Guid taskId, SessionView session)
    {
        var line = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var live = session.State is SessionState.Working or SessionState.Idle;
        if (Actions?.OpenSession is { } open && session.State != SessionState.Expired)
        {
            var button = Flat("Open", "Open session", () => open(taskId, session.Id), new Thickness(8, 2, 8, 2), tip: "Go to this session's window");
            DockPanel.SetDock(button, Dock.Right);
            line.Children.Add(button);
        }
        var square = new Border
        {
            Width = 9, Height = 9, CornerRadius = new CornerRadius(2), Background = live ? LiveSession : EndedSession,
            Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(square, Dock.Left);
        line.Children.Add(square);
        var state = session.State switch
        {
            SessionState.Working => "working",
            SessionState.Idle => "idle",
            SessionState.Ended => "ended",
            _ => "expired",
        };
        line.Children.Add(new TextBlock
        {
            Text = $"{session.Id[..Math.Min(8, session.Id.Length)]} · {state} · {session.Joined.ToLocalTime():d MMM HH:mm}",
            Foreground = live ? Ink : DimInk,
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        return line;
    }

    // The parent of a subtask (task 54b39c4c): "↑ <parent title>", a button that opens the parent in a window of its own,
    // the mirror of SubtaskEntry below. Named "Parent <title>", as the tasks panel's thread link is.
    UIElement ParentEntry(Guid parentId, string parentTitle)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        text.Inlines.Add(new Run("↑ ") { Foreground = HeaderInk });
        text.Inlines.Add(new Run(parentTitle) { Foreground = Ink });
        var button = Flat(text, $"Parent {parentTitle}", () => Actions?.OpenWindow?.Invoke(parentId), new Thickness(8, 4, 8, 4), tip: "Open the parent task in its own window");
        button.Background = TileFill;
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Left;
        button.Margin = new Thickness(0, 0, 0, 3);
        return button;
    }

    // A subtask: its title and status, a button that opens the subtask in a window of its own.
    UIElement SubtaskEntry(SubtaskView sub)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        text.Inlines.Add(new Run("↳ ") { Foreground = HeaderInk });
        text.Inlines.Add(new Run(sub.Title) { Foreground = sub.Status == TaskStatus.Closed ? HeaderInk : Ink });
        text.Inlines.Add(new Run($" · {TaskViews.StatusName(sub.Status)}") { Foreground = HeaderInk });
        var button = Flat(text, sub.Title, () => Actions?.OpenWindow?.Invoke(sub.Id), new Thickness(8, 4, 8, 4), tip: "Open this subtask in its own window");
        button.Background = TileFill;
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Left;
        button.Margin = new Thickness(0, 0, 0, 3);
        return button;
    }

    // The side column stands beside the thread from SideBySideFrom DIP wide, and under it below that.
    void PlaceSide()
    {
        var wide = ActualWidth >= SideBySideFrom;
        if (wide == sideBySide && SideWideBorder.Visibility == (wide ? Visibility.Visible : Visibility.Collapsed)) return;
        sideBySide = wide;
        SideColumn.Width = wide ? new GridLength(300) : new GridLength(0);
        SideWideBorder.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        SideNarrow.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
        if (latest is { } view) DrawSide(view);
    }

    // --- the "⋯" menu ------------------------------------------------------------------------------------------------

    /// <summary>The header's "⋯" menu; internal, and answering the menu, for the tests.</summary>
    internal ContextMenu OpenMenu(UIElement? anchor = null)
    {
        // With no anchor, at the pointer with this window as its target, for its screen's scaling (OwnMenus.AtPointer).
        var menu = anchor is null ? OwnMenus.AtPointer(this) : new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        if (latest?.Row is not { } row || Actions is not { } actions) return menu;
        var open = row.Status != TaskStatus.Closed && !row.Closing;
        if (open)
        {
            menu.Items.Add(Item("Edit", BeginEdit));
            if (actions.StartClaude is { } start && !row.HasOpenSession) menu.Items.Add(Item(TasksWindow.StartClaudeText, () => Later(() => start(row.Id))));
            menu.Items.Add(new Separator());
        }
        var status = Item("Status");
        SettableStatuses.ToList().ForEach(s =>
        {
            var item = Item(TaskViews.StatusName(s), () => actions.SetStatus(row.Id, s));
            item.IsChecked = row.Status == s && !row.Closing;
            status.Items.Add(item);
        });
        menu.Items.Add(status);
        menu.Items.Add(row.Status == TaskStatus.Closed && !row.Closing
            ? Item("Reopen", () => actions.Reopen(row.Id))
            : Item("Close", () => actions.Close(row.Id)));
        var assign = Item("Assign to");
        new[] { Assignee.Me, Assignee.Claude }.ToList().ForEach(who =>
        {
            var item = Item(who.Name, () => actions.Assign(row.Id, who));
            item.IsChecked = row.Assignee == who;
            assign.Items.Add(item);
        });
        menu.Items.Add(assign);
        // Repeat (task 0b78269e, RepeatMenu), as the panel's menu has it; open tasks only.
        if (open && actions.Repeat is { } repeat)
        {
            var repeats = Item(RepeatMenu.Header(row.RepeatText));
            RepeatMenu.Items(row.Id, row.RepeatText, repeat, AskRepeat).ForEach(item => repeats.Items.Add(item));
            menu.Items.Add(repeats);
        }
        // The task list's one workspace menu (WorkspaceChipMenu), opened under "⋯" once this menu has closed.
        menu.Items.Add(Item("Move to…", () => Later(() => OpenMoveMenu(anchor))));
        menu.Items.Add(new Separator());
        // Where else the task can be seen: its workspace and session (as a double-click on its line goes), the panel.
        if (open) menu.Items.Add(Item("Go to its workspace", () => actions.Go(row.Id)));
        if (actions.ShowTask is { } showTask) menu.Items.Add(Item("Show in the tasks panel", () => showTask(row.Id)));
        menu.Items.Add(new Separator());
        // Delete cannot be undone, so it takes a second step, as in the panel. The window closes with the task.
        var delete = Item("Delete");
        delete.Items.Add(Item("Delete for good", () => actions.Delete(row.Id)));
        menu.Items.Add(delete);
        Hold(menu);
        menu.IsOpen = true;
        return menu;
    }

    ContextMenu? OpenMoveMenu(UIElement? anchor)
    {
        if (latest is not { } view) return null;
        var row = view.Row;
        var menu = WorkspaceChipMenu.Build(view.Places, row.Place.Workspace, workspace => Actions?.MoveTo(row.Id, workspace), anchor);
        Hold(menu);
        MoveMenu = menu;
        menu.IsOpen = true;
        return menu;
    }

    /// <summary>The workspace menu opened last (by the workspace chip or "⋯ > Move to…"), for the tests.</summary>
    internal ContextMenu? MoveMenu { get; private set; }

    // A menu holds the redraws while it is open (they would rebuild what it points at), then lets the newest through.
    void Hold(ContextMenu menu)
    {
        menuOpen = true;
        menu.Closed += (_, _) =>
        {
            menuOpen = false;
            Flush();
        };
    }

    // Run once the menu that called it has closed: it may open a menu or a question of its own.
    void Later(Action action) => Dispatcher.BeginInvoke(action, System.Windows.Threading.DispatcherPriority.Input);

    /// <summary>Asks for Custom…'s words (task 0b78269e), as TasksWindow.AskRepeat does; a seam for the tests.</summary>
    internal Func<string, Maybe<string>> AskRepeat
    {
        get => askRepeat ?? (current => PromptDialog.Ask("Repeat", "Schedule, e.g. every 2h, daily 09:00, weekly fri 17:30 (add --model <m> to change the model):", current, owner: this));
        set => askRepeat = value;
    }

    Func<string, Maybe<string>>? askRepeat;

    // A TextBlock header, so an underscore in a word is not read as an access key.
    static MenuItem Item(string text, Action? click = null)
    {
        var item = new MenuItem { Header = new TextBlock { Text = text } };
        if (click is not null) item.Click += (_, _) => click();
        return item;
    }

    // --- keys --------------------------------------------------------------------------------------------------------

    /// <summary>A key in the window, as one method the tests drive without a real key event; whether it was used.</summary>
    internal bool Press(Key key, ModifierKeys modifiers)
    {
        var ctrl = modifiers.HasFlag(ModifierKeys.Control);
        if (editing)
            return key switch
            {
                Key.Escape => Done(EndEdit),
                Key.S when ctrl => Done(() => SaveEdit()),
                Key.Enter or Key.Return when ctrl => Done(() => SaveEdit()),
                _ => false,
            };
        if (editingComment is not null)
            return key switch
            {
                Key.Escape => Done(EndEditComment),
                Key.Enter or Key.Return when ctrl => Done(() => SaveComment()),
                _ => false,
            };
        return key switch
        {
            Key.F2 => Done(BeginEdit),
            Key.E when ctrl => Done(BeginEdit),
            _ => false,
        };
    }

    static bool Done(Action action)
    {
        action();
        return true;
    }

    // --- the description editor --------------------------------------------------------------------------------------

    /// <summary>Opens the editor on the task's title and description (Edit, F2, Ctrl+E). Not on a closed task.</summary>
    internal void BeginEdit()
    {
        if (editing || latest?.Row is not { } row || row.Status == TaskStatus.Closed || row.Closing) return;
        editing = true;
        var parts = TaskImages.Split(row.Description);
        TitleBox.Text = row.Title;
        DescriptionBox.Text = parts.Text;
        editorKept.Clear();
        editorKept.AddRange(parts.Images);
        editorNew.Clear();
        RefreshEditorPictures();
        ShowMarkdown(true);
        TitleText.Visibility = Visibility.Collapsed;
        TitleField.Visibility = Visibility.Visible;
        ReadPanel.Visibility = Visibility.Collapsed;
        EditPanel.Visibility = Visibility.Visible;
        DrawHeaderEnd(row);
        // The keyboard goes to the description: a long task is edited for its brief far more often than for its title.
        DescriptionBox.Focus();
        DescriptionBox.CaretIndex = DescriptionBox.Text.Length;
    }

    /// <summary>Closes the editor and keeps nothing (Cancel, Esc).</summary>
    internal void EndEdit()
    {
        if (!editing) return;
        editing = false;
        editorKept.Clear();
        editorNew.Clear();
        TitleText.Visibility = Visibility.Visible;
        TitleField.Visibility = Visibility.Collapsed;
        ReadPanel.Visibility = Visibility.Visible;
        EditPanel.Visibility = Visibility.Collapsed;
        if (latest?.Row is { } row) DrawHeaderEnd(row);
        Header.Focus();
    }

    /// <summary>
    /// Saves the editor (Save, Ctrl+S, Ctrl+Enter): the new pictures are written, then the title and the description (its
    /// text, then every kept and new picture) go through TasksWindowActions.Edit. A refusal keeps the editor open and
    /// deletes the pictures it just wrote; the reason is on the problem line. Whether it saved.
    /// </summary>
    internal bool SaveEdit()
    {
        if (!editing || latest?.Row is not { } row || Actions is null) return false;
        var saved = TaskImageFiles.Save(StateDir, row.Id, editorNew, Now());
        if (saved.IsFailure)
        {
            ShowProblem($"The pictures could not be saved: {saved.Error}");
            return false;
        }
        var description = TaskImages.Join(DescriptionBox.Text.TrimEnd(), [.. editorKept, .. saved.Value]);
        if (!Actions.Edit(row.Id, TitleBox.Text.Trim(), description))
        {
            ForgetWritten(saved.Value);
            return false;
        }
        EndEdit();
        return true;
    }

    // The editor's two tabs: the markdown box, or what it will look like.
    void ShowMarkdown(bool markdown)
    {
        previewing = !markdown;
        EditTabs.Children.Clear();
        EditTabs.Children.Add(Tab("Markdown", markdown, () => ShowMarkdown(true)));
        EditTabs.Children.Add(Tab("Preview", !markdown, () => ShowMarkdown(false)));
        DescriptionField.Visibility = markdown ? Visibility.Visible : Visibility.Collapsed;
        PreviewField.Visibility = markdown ? Visibility.Collapsed : Visibility.Visible;
        if (!markdown)
        {
            var document = MarkdownBlocks.Render(DescriptionBox.Text, OpenLink);
            document.FontSize = 13;
            document.Foreground = Ink;
            PreviewBox.Document = document;
        }
        else if (editing) DescriptionBox.Focus();
    }

    /// <summary>For the tests: whether the editor shows its preview.</summary>
    internal bool IsPreviewing => previewing;

    Button Tab(string text, bool on, Action press)
    {
        var button = Flat(new TextBlock { Text = text, FontSize = 12, Foreground = on ? Ink : HeaderInk }, text, press, new Thickness(10, 4, 10, 4));
        button.Background = on ? Frozen(0xFF, 0x15, 0x16, 0x1A) : Brushes.Transparent;
        button.Margin = new Thickness(0, 0, 2, 0);
        return button;
    }

    /// <summary>A paste or drop on the description box: true when it held pictures, which wait under the box.</summary>
    internal bool PasteIntoDescription(IDataObject data) => PicturePaste.Took(TaskImageFiles.FromData(data), taken =>
    {
        editorNew.AddRange(taken);
        RefreshEditorPictures();
    });

    void RefreshEditorPictures()
    {
        var kept = editorKept.Select(relative => TaskImageFiles.Thumbnail(StateDir, relative, 96) is { HasValue: true } shown ? shown.Value : null).ToArray();
        Fill(EditorPictures, [.. kept, .. editorNew.Select(p => (ImageSource?)p.Thumbnail)], i =>
        {
            if (i < editorKept.Count) editorKept.RemoveAt(i);
            else editorNew.RemoveAt(i - editorKept.Count);
            RefreshEditorPictures();
        });
    }

    // --- the comment box ---------------------------------------------------------------------------------------------

    /// <summary>A paste or drop on the comment box: true when it held pictures, which wait under the box.</summary>
    internal bool PasteIntoComment(IDataObject data) => PicturePaste.Took(TaskImageFiles.FromData(data), taken =>
    {
        commentPictures.AddRange(taken);
        RefreshCommentPictures();
    });

    void RefreshCommentPictures() =>
        Fill(CommentPictures, commentPictures.Select(p => (ImageSource?)p.Thumbnail).ToArray(), i =>
        {
            commentPictures.RemoveAt(i);
            RefreshCommentPictures();
        });

    /// <summary>For the tests: what is typed in the comment box.</summary>
    internal string CommentDraft
    {
        get => CommentBox.Text;
        set => CommentBox.Text = value;
    }

    /// <summary>
    /// Posts the comment box as Petre's comment (Comment, Ctrl+Enter), its pictures written and their lines after the
    /// text. Emptied only once it posted: a refusal keeps the words and the pictures and deletes the files just written.
    /// Whether it posted.
    /// </summary>
    internal bool Post()
    {
        if (latest?.Row is not { } row || Actions is null) return false;
        var text = CommentBox.Text;
        if (string.IsNullOrWhiteSpace(text) && commentPictures.Count == 0) return false;
        var saved = TaskImageFiles.Save(StateDir, row.Id, commentPictures, Now());
        if (saved.IsFailure)
        {
            ShowProblem($"The pictures could not be saved: {saved.Error}");
            return false;
        }
        if (!Actions.Comment(row.Id, TaskImages.Join(text.Trim().Length == 0 ? "" : text.TrimEnd(), saved.Value)))
        {
            ForgetWritten(saved.Value);
            return false;
        }
        CommentBox.Text = "";
        commentPictures.Clear();
        RefreshCommentPictures();
        return true;
    }

    // --- editing a comment in place ------------------------------------------------------------------------------------

    void BeginEditComment(TaskCommentView comment)
    {
        editingComment = comment.Id;
        if (latest is { } view) Draw(view);
        commentEditor?.Focus();
    }

    // A box with the comment's text (its pictures stay as they are), Save and Cancel.
    UIElement CommentEditor(TaskCommentView comment)
    {
        var split = TaskImages.Split(comment.Markdown);
        commentEditor = new TextBox
        {
            Text = split.Text, FontSize = 12.5, Foreground = Ink, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            CaretBrush = Brushes.White, Padding = new Thickness(0), TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MinHeight = 40,
            Template = (ControlTemplate)FindResource("BareTextBox"),
        };
        AutomationProperties.SetName(commentEditor, "Edit comment");
        commentEditor.Tag = split.Images;
        var field = new Border { Style = (Style)FindResource("FieldEdge"), Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 4, 0, 0), Child = commentEditor };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(Flat("Cancel", "Cancel comment edit", EndEditComment));
        var save = Flat("Save", "Save comment", () => SaveComment(), primary: true);
        save.Margin = new Thickness(6, 0, 0, 0);
        buttons.Children.Add(save);
        return new StackPanel { Children = { field, buttons } };
    }

    // Saves the comment being edited, its pictures kept; whether it saved. A refusal keeps the box open.
    bool SaveComment()
    {
        if (editingComment is not { } id || commentEditor is null || Actions is null) return false;
        var images = commentEditor.Tag as IReadOnlyList<string> ?? [];
        if (!Actions.EditComment(id, TaskImages.Join(commentEditor.Text.TrimEnd(), images))) return false;
        EndEditComment();
        return true;
    }

    void EndEditComment()
    {
        editingComment = null;
        commentEditor = null;
        drawn = "";
        if (waiting is not null) Flush();
        else if (latest is { } view) Present(view);
    }

    // --- small parts -------------------------------------------------------------------------------------------------

    // Pictures written for a post or an edit that was then refused: deleted again, as the panel does.
    void ForgetWritten(IReadOnlyList<string> written) =>
        written.Select(relative => TaskImages.Resolve(relative, StateDir)).Where(path => path.HasValue).ToList()
            .ForEach(path => Result.Try(() => File.Delete(path.Value)));

    // A row of thumbnails, each with an x calling `remove` with its place; hidden when empty.
    void Fill(Panel row, IReadOnlyList<ImageSource?> thumbnails, Action<int> remove)
    {
        row.Children.Clear();
        thumbnails.Select((thumbnail, i) => Thumb(thumbnail, () => remove(i))).ToList().ForEach(item => row.Children.Add(item));
        row.Visibility = thumbnails.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    FrameworkElement Thumb(ImageSource? thumbnail, Action remove)
    {
        var cross = Flat(new TextBlock { Text = "✕", Foreground = Ink, FontSize = 9 }, "Remove picture", remove, new Thickness(3, 0, 3, 1), tip: "Take this picture out");
        cross.HorizontalAlignment = HorizontalAlignment.Right;
        cross.VerticalAlignment = VerticalAlignment.Top;
        cross.Background = Frozen(0xCC, 0x15, 0x16, 0x1A);
        UIElement picture = thumbnail is null
            ? new Border { Width = 64, Height = 64, Background = ButtonFill }
            : new Image { Source = thumbnail, Height = 64, Stretch = Stretch.Uniform };
        return new Border
        {
            Margin = new Thickness(0, 0, 6, 4),
            BorderBrush = ThreadEdge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Child = new Grid { Children = { picture, cross } },
        };
    }

    Button Flat(string text, string name, Action press, Thickness padding, string? tip = null) =>
        Flat(new TextBlock { Text = text, FontSize = 11.5, Foreground = Ink }, name, press, padding, tip: tip);

    Button Flat(string text, string name, Action press, bool primary = false, string? tip = null) =>
        Flat(new TextBlock { Text = text, FontSize = 12, Foreground = primary ? Brushes.White : Ink }, name, press, new Thickness(10, 4, 10, 4), primary, tip);

    // A flat button with a name for automation and the tests. Unfocusable, so a click never takes the keyboard from a box
    // Petre is typing in.
    Button Flat(UIElement content, string name, Action press, Thickness padding, bool primary = false, string? tip = null)
    {
        var button = new Button
        {
            Content = content,
            Template = (ControlTemplate)FindResource("FlatButton"),
            Background = primary ? PrimaryFill : ButtonFill,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Padding = padding,
            Focusable = false,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = tip,
        };
        AutomationProperties.SetName(button, name);
        button.Click += (_, e) =>
        {
            e.Handled = true;
            press();
        };
        return button;
    }

    static Brush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
