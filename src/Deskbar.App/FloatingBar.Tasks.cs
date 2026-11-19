using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using CSharpFunctionalExtensions;
using Deskbar.Core.Persistence;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// TASKS IN THE BAR: the stopwatch in the bottom strip and the pie in a workspace row's caption.
// Design: docs/superpowers/specs/2026-11-19-tasks-in-the-bar-design.md, sections "The bar" and "When the strip runs out
// of room". The model and its rules live in Deskbar.Core.Tasks; this file only draws them.
//
//   the stopwatch   in the strip, just inward of the language code (LayOutStrip). Three faces:
//                     at rest     the glyph only, StripButtonWidth wide like the strip's other icon buttons
//                     running     the glyph, a 9 DIP swatch of the soonest timer's workspace colour (the row's own
//                                 rail colour, so the two read as one), and its time left, "4:32"
//                     due         the glyph and the word "due" in orange, pulsing with the shared AskingPulse brush
//                   Any due task wins over every running one; among running timers the soonest end is shown. A free
//                   task (no workspace) has no colour, so no swatch.
//   the pie         an 11 DIP pie in the caption of a workspace row with a running timer (WorkspaceGroup.Timer, the
//                   soonest on that workspace), blue #4F8CFF on 13% white, emptying clockwise from twelve o'clock as
//                   the time runs out. Blue because nothing wants Petre yet; a DUE timer is the row's orange blink
//                   instead (WantsAttention), so a due workspace has no pie.
//
// REPAINTED ON THE CLOCK'S TICK, NEVER REBUILT. The strip's clock already ticks every second (clockTimer); the
// stopwatch and every live pie are repainted on that tick, in place. The pies are kept in a list that each rebuild
// empties (ForgetTaskPies) and refills as it builds the captions, the same way the captions' time marks are kept
// (FloatingBar.WorkspaceTime.cs). Nothing here ever asks for a rebuild: the bar's rebuild is re-entrancy guarded,
// costs tens of milliseconds, and a once-a-second rebuild would also restart every glide and hover.
//
// THE CLICK. Until the task window exists (a later pull request), the stopwatch opens a small menu (TasksMenu): park
// the current workspace for 1, 5, 10 or 30 minutes, then the live tasks with their time left, each a click away from
// done. App can hand over another action through OpenTasks, which is how the window will take the click over.
public partial class FloatingBar
{
    // The task service, handed over by App. Setting it shows the stopwatch (while the gate allows) and repaints it
    // straight away, and again on every change to the board, so a park from the menu or from `deskbar.cmd task` shows
    // at once instead of at the next tick. Left unset, as every bar test but the tasks ones leaves it, the stopwatch
    // stays collapsed and takes no room in the strip.
    internal TaskService? Tasks
    {
        get => taskService;
        set
        {
            taskChanges?.Dispose();
            taskService = value;
            taskChanges = value?.Changed.Subscribe(_ => PaintStopwatch());
            ShowStopwatch();
            PaintStopwatch();
            FitClock();
        }
    }

    TaskService? taskService;
    IDisposable? taskChanges;

    // What a click on the stopwatch does when App has something better than the menu (the task window, later).
    internal Action? OpenTasks { get; set; }

    // Whether this copy may use tasks: Deskbar Pro, or any copy while tasks are free (TasksGate.Free, for now, which
    // makes this always true). Entitlement's cached verdict in the app, the same pattern as tiling's IsPro; a seam for
    // the tests, which cannot fake a Store licence, and nothing else. When tasks are gated, the stopwatch is hidden
    // for the free tier and nothing else here needs to change.
    internal Func<bool> TasksAllowed { get; init; } = () => TasksGate.Allowed(Entitlement.Current.IsPro);

    // A window's title, for the title a park from the menu takes when the workspace has no live task yet. A seam for
    // the tests, which must not read whatever the person running the suite has open.
    internal Func<nint, string> WindowTitleOf { get; init; } = ReadWindowTitle;

    // The stopwatch, for the tests.
    internal Button? StripTasks => stripTasks;

    // The stopwatch's text as painted: "" at rest, "4:32" while running, "due". For the tests.
    internal string StopwatchText => stopwatchCountdown?.Text ?? "";

    // Whether the countdown text is on screen, which FitClock decides. For the tests.
    internal bool CountdownShown => stopwatchCountdown is { Visibility: Visibility.Visible };

    // The pie's blue, and the faint disc it sits on: 13% white (0x21 of 0xFF), so an empty pie still reads as a pie.
    static readonly Brush PieBlue = Frozen(0xFF, 0x4F, 0x8C, 0xFF);
    static readonly Brush PieTrack = Frozen(0x21, 0xFF, 0xFF, 0xFF);
    const double PieSize = 11;
    const double SwatchSize = 9;

    // A stopwatch on a 16 unit square, drawn as a path like the strip's other glyphs (see the glyphs section of
    // FloatingBar.Shell.cs for why paths rather than a Segoe Fluent Icons codepoint): the dial, the crown and its
    // stem at the top, one hand at twelve, and the side button that sets a stopwatch apart from a clock. It cannot
    // be a clock glyph: the strip already shows Windows' clock (spec, "The bar").
    const string StopwatchShape =
        "M2.8,9.4 A5.2,5.2 0 1 1 13.2,9.4 A5.2,5.2 0 1 1 2.8,9.4 Z M6.4,1.8 L9.6,1.8 M8,1.8 L8,4.2 M8,9.4 L8,6.6 M12,4.9 L13.2,3.7";

    Path? stopwatchGlyph;
    Border? stopwatchSwatch;
    TextBlock? stopwatchCountdown;

    // What the stopwatch last showed, so a tick that changes nothing touches nothing (and does not re-fit the strip).
    (string Text, bool Due, Brush? Swatch)? lastStopwatchPaint;

    // The live pies of the rows on screen, each with the timer it draws; and each workspace row's rail colour, for the
    // stopwatch's swatch. Both belong to the rows of one rebuild, so ForgetTaskPies empties them with those rows.
    readonly List<(TaskTimer Timer, Path Slice)> taskPies = [];
    readonly Dictionary<Guid, Brush?> workspaceAccents = [];

    void ForgetTaskPies()
    {
        taskPies.Clear();
        workspaceAccents.Clear();
    }

    void NoteWorkspaceAccent(Guid workspace, Brush? accent) => workspaceAccents[workspace] = accent;

    // Built once, from BuildShellStrip, like the strip's other buttons. Collapsed until Tasks is set (ShowStopwatch).
    Button StopwatchButton()
    {
        stopwatchGlyph = new Path
        {
            Data = Geometry.Parse(StopwatchShape),
            Stroke = ShellGlyphBrush,
            StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        var glyph = new Viewbox
        {
            Width = StripGlyphSize,
            Height = StripGlyphSize,
            Child = new Canvas { Width = 16, Height = 16, Children = { stopwatchGlyph } },
        };
        stopwatchSwatch = new Border
        {
            Width = SwatchSize,
            Height = SwatchSize,
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        stopwatchCountdown = new TextBlock
        {
            // The language code's size and weight, its neighbour on the strip.
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        var face = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Children = { glyph, stopwatchSwatch, stopwatchCountdown },
        };
        var button = BareShellButton("Tasks", face, StripButtonWidth, StripButtonHeight);
        // The countdown and "due" take the button's Foreground (TextBlock inherits it); the glyph's stroke is set
        // beside it in PaintStopwatch, since a Path does not inherit a foreground.
        button.Foreground = ClockForeground;
        button.Visibility = Visibility.Collapsed;
        // Worked out when the tooltip opens rather than written every second: it names the task and its workspace.
        button.ToolTipOpening += (_, _) => button.ToolTip = StopwatchTip();
        button.Click += (_, _) =>
        {
            // The strip sits inside the bar, whose own mouse-up must not treat this release as a click of its own.
            MarkPressConsumed();
            (OpenTasks ?? OpenTasksMenu)();
        };
        clockTimer.Tick += (_, _) => PaintStopwatch();
        Closed += (_, _) => taskChanges?.Dispose();
        return button;
    }

    // The stopwatch's one owner of Visibility: shown when there is a task service, the gate allows tasks, and the
    // strip item is not switched off in Settings (which also leaves it out of the strip, see LayOutStrip). FitClock
    // never hides the button itself, only its countdown text.
    void ShowStopwatch()
    {
        if (stripTasks is null) return;
        var shown = Tasks is not null && TasksAllowed() && !stripHidden.Contains(StripItem.Tasks);
        stripTasks.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
    }

    // FitClock's half of the countdown: shown when it fits and there is something to show.
    void ShowCountdown(bool fits)
    {
        if (stopwatchCountdown is null) return;
        stopwatchCountdown.Visibility = fits && stopwatchCountdown.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // What the stopwatch shows: the task it is about, if any, and its text.
    readonly record struct StopwatchFace(Maybe<TaskItem> Task, string Text, bool Due);

    static StopwatchFace FaceOf(TaskService tasks)
    {
        var now = tasks.Now;
        var timed = tasks.Board.Tasks
            .Select(t => (Task: t, State: t.StateAt(now)))
            .Where(x => x.State is TaskState.Due or TaskState.Running)
            .ToArray();
        var due = timed.Where(x => x.State == TaskState.Due).OrderBy(x => x.Task.Timer.Value.Ends).Select(x => x.Task).TryFirst();
        var running = timed.Where(x => x.State == TaskState.Running).OrderBy(x => x.Task.Timer.Value.Ends).Select(x => x.Task).TryFirst();
        return due.HasValue ? new StopwatchFace(due, "due", true)
            : running.HasValue ? new StopwatchFace(running, Countdown(running.Value.Timer.Value.Ends - now), false)
            : new StopwatchFace(Maybe<TaskItem>.None, "", false);
    }

    // "4:32", "12:05": minutes not zero-padded, seconds always two digits, and total minutes over an hour (which the
    // 30 minute presets cannot reach). Rounded UP to the second, so a running timer never reads "0:00" before it is
    // due, and the second it turns due is the second the text says "due".
    internal static string Countdown(TimeSpan left)
    {
        var seconds = Math.Max(0, (int)Math.Ceiling(left.TotalSeconds));
        return $"{seconds / 60}:{seconds % 60:00}";
    }

    // Called on every tick of the strip's clock, after each change to the board, and by the tests: the stopwatch's
    // face, then every live pie. Touches the stopwatch only when its face changed, and re-fits the strip then,
    // because the countdown's width is part of what FitClock shares out.
    internal void PaintStopwatch()
    {
        PaintPies();
        if (stripTasks is null || stopwatchGlyph is null || stopwatchSwatch is null || stopwatchCountdown is null) return;
        var face = Tasks is { } tasks ? FaceOf(tasks) : new StopwatchFace(Maybe<TaskItem>.None, "", false);
        // The swatch is the row's own rail colour (NoteWorkspaceAccent), so none for a free task, or for a workspace
        // that opted out of a colour.
        var swatch = face.Task.Bind(t => t.WorkspaceId).Map(id => workspaceAccents.GetValueOrDefault(id)).GetValueOrDefault();
        var paint = (face.Text, face.Due, swatch);
        if (lastStopwatchPaint != paint)
        {
            lastStopwatchPaint = paint;
            stopwatchCountdown.Text = face.Text;
            stripTasks.Foreground = face.Due ? AskingPulse : ClockForeground;
            stopwatchGlyph.Stroke = face.Due ? AskingPulse : ShellGlyphBrush;
            stopwatchSwatch.Background = swatch;
            stopwatchSwatch.Visibility = swatch is null ? Visibility.Collapsed : Visibility.Visible;
            // At rest the strip's icon width; with a swatch or text, as wide as they need.
            var bare = face.Text.Length == 0 && swatch is null;
            stripTasks.Width = bare ? StripButtonWidth : double.NaN;
            stripTasks.Padding = bare ? new Thickness(0) : new Thickness(5, 0, 5, 0);
            // Shown here; FitClock takes it away again if the strip has no room for it.
            ShowCountdown(true);
            FitClock();
        }
        // The pulse runs while anything is due, a free task included (see SyncAsking).
        RunAsking(overviewAsks || AnyTaskDue);
    }

    // Whether any task's timer has run out, for the shared pulse.
    bool AnyTaskDue => Tasks is { } tasks && tasks.Board.Tasks.Any(t => t.StateAt(tasks.Now) == TaskState.Due);

    string StopwatchTip()
    {
        if (Tasks is not { } tasks) return "Tasks";
        var face = FaceOf(tasks);
        var where = face.Task.Bind(t => t.WorkspaceId).Bind(WorkspaceName).Map(name => $" on {name}").GetValueOrDefault("");
        return face.Task.HasNoValue ? "Tasks: park this workspace"
            : face.Due ? $"{face.Task.Value.Title}{where} is due"
            : $"{face.Task.Value.Title}{where}: {face.Text} left";
    }

    Maybe<string> WorkspaceName(Guid id) => manager.State.Workspaces.TryFirst(w => w.Id == id).Map(w => w.Name);

    // --- the pie ------------------------------------------------------------------------------------------------

    // One caption's pie, registered for the tick. Tagged so a test (and a reader of the visual tree) can find it.
    FrameworkElement TaskPie(TaskTimer timer)
    {
        var slice = new Path { Fill = PieBlue };
        var pie = new Grid
        {
            Width = PieSize,
            Height = PieSize,
            Tag = "task-pie",
            Margin = new Thickness(3, 0, 1, 0),
            VerticalAlignment = VerticalAlignment.Center,
            // Part of the caption's click target, which must keep switching to the workspace.
            IsHitTestVisible = false,
            Children = { new Ellipse { Fill = PieTrack }, slice },
        };
        taskPies.Add((timer, slice));
        slice.Data = PieGeometry(Remaining(timer, TasksNow));
        return pie;
    }

    // The service's clock when there is one, so the tests' moved clock drives the pies too.
    DateTimeOffset TasksNow => Tasks?.Now ?? DateTimeOffset.UtcNow;

    void PaintPies()
    {
        if (taskPies.Count == 0) return;
        var now = TasksNow;
        taskPies.ForEach(p => p.Slice.Data = PieGeometry(Remaining(p.Timer, now)));
    }

    // The share of the timer still to run, 1 when it has just started and 0 once it is due.
    static double Remaining(TaskTimer timer, DateTimeOffset now) =>
        timer.Length <= TimeSpan.Zero ? 0 : Math.Clamp((timer.Ends - now) / timer.Length, 0, 1);

    // The slice still to run, from twelve o'clock clockwise, on the pie's own square. A whole disc and nothing are
    // their own cases: an arc from a point back to itself draws nothing at all.
    internal static Geometry PieGeometry(double remaining)
    {
        const double r = PieSize / 2;
        if (remaining >= 0.9999) return FrozenGeometry(new EllipseGeometry(new Point(r, r), r, r));
        if (remaining <= 0) return Geometry.Empty;
        var angle = remaining * 2 * Math.PI;
        var end = new Point(r + r * Math.Sin(angle), r - r * Math.Cos(angle));
        var figure = new PathFigure { StartPoint = new Point(r, r), IsClosed = true, IsFilled = true };
        figure.Segments.Add(new LineSegment(new Point(r, 0), isStroked: false));
        figure.Segments.Add(new ArcSegment(end, new Size(r, r), 0, isLargeArc: remaining > 0.5, SweepDirection.Clockwise, isStroked: false));
        return FrozenGeometry(new PathGeometry([figure]));
    }

    static Geometry FrozenGeometry(Geometry geometry)
    {
        geometry.Freeze();
        return geometry;
    }

    // --- the menu, until the task window exists ----------------------------------------------------------------

    void OpenTasksMenu()
    {
        if (stripTasks is null) return;
        var menu = TasksMenu();
        menu.PlacementTarget = stripTasks;
        menu.Placement = PlacementMode.Top;
        menu.IsOpen = true;
    }

    // The most live tasks the menu lists. More than this is the task window's job.
    const int MenuTasks = 8;

    // Built fresh on every click, so it shows the board and the times as they are now.
    //
    //   Park <workspace>: <title>     a header, then the four lengths. The workspace is the one Petre is on
    //                                 (manager.CurrentWorkspaceId). The title is the workspace's most recently changed
    //                                 live task's, so parking the same thread again lands on the same task; else the
    //                                 window he was last typing in (languageTarget, the language switcher's memory of
    //                                 it, which a click on the bar does not move); else the workspace's own name.
    //                                 On a desktop nobody named there is no workspace to park, so the header says how
    //                                 to get one and the lengths are disabled.
    //   <title> · 4:32       Done     each live task, in the order the lists use (due first, then the soonest
    //                                 running, then open by newest), with its time left; a click marks it done.
    internal ContextMenu TasksMenu()
    {
        var menu = new ContextMenu();
        HoldFadeWhileOpen(menu);
        if (Tasks is not { } tasks) return menu;

        var workspace = manager.CurrentWorkspaceId;
        var name = workspace.Bind(WorkspaceName);
        var title = workspace.Bind(id => tasks.Board.LatestLiveOn(id)).Map(t => t.Title)
            .Or(() => Maybe.From(WindowTitleOf(languageTarget)).Where(t => !string.IsNullOrWhiteSpace(t)))
            .Or(name)
            .GetValueOrDefault("");
        menu.Items.Add(new MenuItem
        {
            Header = name.HasValue
                ? MenuText(string.Equals(title, name.Value, StringComparison.Ordinal) ? $"Park {name.Value}" : $"Park {name.Value}: {Shorten(title)}")
                : "Name this desktop to park it",
            IsEnabled = false,
        });
        TaskPresets.Lengths.ToList().ForEach(length =>
        {
            var item = new MenuItem { Header = TaskPresets.Label(length), Icon = MenuGlyph("◔"), IsEnabled = workspace.HasValue };
            item.Click += (_, _) => Report(Plain(tasks.Park(title, workspace, length)));
            menu.Items.Add(item);
        });

        var now = tasks.Now;
        var live = tasks.Entries([], DateOnly.FromDateTime(DateTime.Now))
            .Where(e => e.Kind == TaskEntryKind.Task && e.State != TaskState.Done && e.Task.HasValue)
            .Select(e => (Task: e.Task.Value, e.State))
            .Take(MenuTasks)
            .ToList();
        if (live.Count > 0) menu.Items.Add(new Separator());
        live.ForEach(x =>
        {
            var left = x.State switch
            {
                TaskState.Due => " · due",
                TaskState.Running => $" · {Countdown(x.Task.Timer.Value.Ends - now)}",
                _ => "",
            };
            var item = new MenuItem
            {
                Header = MenuText($"{Shorten(x.Task.Title)}{left}"),
                InputGestureText = "Done",
                Icon = MenuGlyph("✓"),
                ToolTip = "Mark this task done",
            };
            item.Click += (_, _) => Report(tasks.Done(x.Task.Id));
            menu.Items.Add(item);
        });
        return menu;
    }

    // Window titles run long ("Inbox - Outlook - Microsoft 365"); the menu shows the start of one.
    static string Shorten(string text) => text.Length <= 48 ? text : text[..47] + "…";

    // A menu header turns "_" into an access key and swallows it; doubled, it is drawn as itself.
    static string MenuText(string text) => text.Replace("_", "__");

    static Result Plain<T>(Result<T> result) => result.IsSuccess ? Result.Success() : Result.Failure(result.Error);

    static string ReadWindowTitle(nint hwnd)
    {
        if (hwnd == 0) return "";
        var buffer = new StringBuilder(512);
        return Windows.Monitoring.NativeMethods.GetWindowText(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : "";
    }
}
