using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;
using Deskbar.Core.Geometry;
using Deskbar.Windows.Monitoring;

namespace Deskbar.App;

// ONE SCREEN'S STRIP: the current workspace's window list along the top edge of one screen, 32 DIP high and the full
// width of that screen, registered with the shell as an app bar so maximized windows, Snap and new windows start
// below it. Petre: "let's add a feature for a new taskbar-like bar at the top of each monitor, for its tasks". The
// design is docs/superpowers/specs/2026-11-19-screen-strips-design.md ("The look", "Constraints", "The parts"); the
// measurements it rests on are docs/superpowers/notes/2026-11-19-screen-strips-probes.md.
//
// What this window does and what it does not. It docks itself to its screen's top edge and keeps that strip (the
// 1 s guard below, and a re-dock whenever the shell says something on an edge moved). It hosts a name tag and
// whatever tabs it is handed, sized by StripPlan. It reports a window icon dropped on it and a right-click on its
// own surface. It does NOT decide which windows it shows: the bar builds every tab with the same code that builds a
// window button on the bar, so a tab cannot drift from a button (the spec's third anti-goal), and hands them over.
// Nor does it decide which screens get a strip: that is the strip set, reconciled on the settled display layout.
// Nor does it hide itself from the window list or pin itself to every desktop: the strip set does that around
// creation, the way App does it for the bar.
//
// A SCREEN IS ITS RECTANGLE. The strip is tied to one MonitorBounds, in physical pixels, for its whole life (a screen
// whose rectangle changed is a different screen, and gets a new strip; StripPlan.Reconcile). It never asks Windows
// which screen its window is on: MONITOR_DEFAULTTONEAREST answers with ANOTHER screen while the strip's own one has
// dropped out for a moment on unlock, and the docked bar's guard once re-registered on that answer and moved the bar
// to the primary (FloatingBar.DockGuard.cs, 2026-11-01). Everything here reads its own screen or nothing.
public sealed class ScreenStrip : Window
{
    // 32 DIP: the mockup Petre picked ("B", spec "The look"). Turned into the screen's pixels at the screen's own scale,
    // so 48 px at 150%.
    public const double HeightDip = 32;

    // The bar's own background, so a strip reads as part of Deskbar; and the tag's grey for an unnamed desktop, which
    // has no workspace colour. Frozen, as every static brush must be (CLAUDE.md, traps): an unfrozen one takes the
    // affinity of the first thread that touches it and throws on the next.
    static readonly Brush StripBackground = Frozen(Color.FromArgb(0xFF, 0x14, 0x15, 0x18));
    static readonly Brush NeutralTag = Frozen(Color.FromArgb(0xFF, 0x3A, 0x3F, 0x49));

    // MonitorFromPoint's "no monitor" flag: an empty answer instead of the nearest screen. See OwnMonitor.
    const uint MonitorDefaultToNull = 0;

    // SetWindowPos' "out of the topmost band". FloatingBar has its own; they are one value.
    static readonly nint HwndNoTopmost = -2;

    // The screen this strip belongs to, physical pixels, as the strip set was given it.
    public MonitorBounds Screen { get; }

    // The one line the strip draws: the name tag first, then the tabs. Internal for the tests, which read what is in it.
    internal StackPanel Row { get; }

    // The workspace's name on its colour, at the left end of the strip.
    internal Border NameTag { get; }

    readonly TextBlock nameText;

    // How the strip docks: the shell's app bar in the app, a recording fake in the tests, so no test ever reserves a
    // strip of a real screen (that moves every maximized window on it). Arguments and answer as AppBar.Dock's.
    internal Func<AppBar.Edge, AppBar.Rect, int, AppBar.Rect> DockWith { get; init; }

    // The screen's scale (1.5 at 150%). In the app it is read from the screen itself with GetDpiForMonitor, for the
    // reason NativeMethods gives (a window's own DPI can be stale just after a show). Faked in the tests, so the
    // pixel arithmetic does not depend on the scale of the machine running them.
    //
    // No value when the screen cannot be read (it dropped out for a moment, as a waking screen does on unlock). There is
    // deliberately no stand-in: the window's own scale is the only other one at hand, and after Windows has moved the
    // window off the vanished screen it is ANOTHER screen's. A strip docked with it on a mixed-scale desk registered at
    // the wrong thickness (32 px on a 150% screen), and the guard can never see that: the work area starts below a strip
    // too thin just as it does below the right one. So Dock refuses instead (review of task 6, fix round 1).
    internal Func<Maybe<double>> Scale { get; init; }

    // The time the guard reads, UTC. Faked in the tests to step over the settle time without waiting for it.
    internal Func<DateTime> Clock { get; init; } = () => DateTime.UtcNow;

    // A window icon was dropped somewhere on this strip: the bar decides what that means (the current workspace on
    // this screen, or this monitor on an unnamed desktop; spec "What a tab does").
    public Action<WindowHandle>? Dropped { get; init; }

    // A right-click on the name tag or on the strip's free space: the bar opens its own menu, which holds the window
    // list choice, so the way back to the other two places is on the strip itself (spec "What a tab does").
    public Action? MenuRequested { get; init; }

    // Created on the first dock and kept, like the bar's: the hook on this window's message loop goes in once.
    AppBar? appBar;

    // Docked and not undocked since. The guard and the re-dock on the shell's notices only act while it is set.
    bool docked;

    // A dock in progress. COM and the shell's SendMessage pump messages, so a notice can arrive in the middle of a
    // dock and ask for another one; the bar's DockTo is guarded the same way.
    bool docking;

    // The shell said a full-screen window is up on this screen (ABN_FULLSCREENAPP): out of the topmost band until it
    // says the window closed, and the 1 s re-assert leaves it there.
    bool fullScreenOpen;

    // What the last dock asked for and what it got, for Describe.
    AppBar.Rect? asked;
    AppBar.Rect? granted;

    // When the strip last docked and when the guard last took it back, and how often it has, for the guard's two
    // clocks and for Describe.
    DateTime lastDocked = DateTime.MinValue;
    DateTime lastRepair = DateTime.MinValue;
    int repairs;

    public ScreenStrip(MonitorBounds screen)
    {
        Screen = screen;

        // Borderless, never on the taskbar, never activated by being shown, topmost like the taskbar. The tool-window
        // half of "never in Alt+Tab" is set on the handle, see OnSourceInitialized.
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.Manual;
        Background = StripBackground;
        Height = HeightDip;
        Title = "Deskbar strip";

        nameText = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        // Capped, so a long workspace name cannot take the room the tabs need; the ellipsis shows it was cut.
        NameTag = new Border { Background = NeutralTag, Padding = new Thickness(10, 0, 10, 0), MaxWidth = 200, Child = nameText };

        // One line, always: past the icon-only width the last tabs are clipped rather than wrapped (spec "The look").
        Row = new StackPanel { Orientation = Orientation.Horizontal, Height = HeightDip, ClipToBounds = true };
        Row.Children.Add(NameTag);

        // The root carries the background, so the free space to the right of the last tab is hit-testable and a
        // right-click there reaches OnRightButtonUp.
        var root = new Border { Background = StripBackground, Child = Row };
        // MouseUp, not MouseRightButtonUp: the button-specific events are DIRECT events, raised on each element as the
        // bubbling MouseUp passes it, so the bubbling one is the event that really travels from a tag or a tab up here.
        root.MouseUp += OnRightButtonUp;
        Content = root;

        // A window icon dragged here from the bar or from another strip: WindowDragSource's payload.
        AllowDrop = true;
        DragOver += (_, e) =>
        {
            e.Effects = Accepts(e.Data) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
        Drop += (_, e) =>
        {
            TakeDrop(e.Data);
            e.Handled = true;
        };

        // The real implementations of the two seams; a test's object initializer replaces them after this runs.
        DockWith = (edge, rect, thicknessPx) => (appBar ?? throw new InvalidOperationException("The strip has no app bar yet.")).Dock(edge, rect, thicknessPx);
        Scale = ScaleOfScreen;
    }

    // Fills the strip: the name tag on `colour` (the neutral grey when there is none, an unnamed desktop), then the
    // tabs, each StripPlan.TabWidth wide for the room left after the tag. Replaces whatever was there, so the bar can
    // hand over a fresh set on every rebuild, and an empty list leaves the tag alone on the strip, which is how a
    // screen with no windows of the current workspace keeps its strip (and its work area) through a switch.
    //
    // The room comes from the SCREEN's width at the screen's scale, not from the window's ActualWidth: the strip is
    // filled before it is first shown, and a window not yet laid out has no width to read.
    public void SetTabs(string name, Brush? colour, IReadOnlyList<FrameworkElement> tabs)
    {
        nameText.Text = name;
        NameTag.Background = colour ?? NeutralTag;
        Row.Children.RemoveRange(1, Row.Children.Count - 1);

        NameTag.Measure(new Size(double.PositiveInfinity, HeightDip));
        var width = StripPlan.TabWidth(WidthDip() - NameTag.DesiredSize.Width, tabs.Count);
        tabs.ToList().ForEach(tab =>
        {
            tab.Width = width;
            Row.Children.Add(tab);
        });
    }

    // The screen's width in its own DIPs. Only for sizing tabs, so while the screen cannot be read the window's own scale
    // is good enough here: a tab a little too wide or narrow until the next rebuild reserves nothing, unlike a dock.
    double WidthDip() => (Screen.Right - Screen.Left) / Scale().GetValueOrDefault(VisualTreeHelper.GetDpi(this).DpiScaleX);

    // Reserves the top of this strip's screen, HeightDip thick in that screen's pixels, and puts the window exactly in
    // what the shell granted, then shows it if it is not up yet. Also the way to RE-dock: on the shell's notices, and
    // from the guard. A strip with nothing else on its top edge gets exactly what it asked for (measured, probe notes
    // section 1); a taskbar on the top edge would push it down, which is why the grant is what is placed.
    public Result Dock()
    {
        if (docking) return Result.Success();
        docking = true;
        try
        {
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            appBar ??= new AppBar(hwnd, onPositionChanged: OnShellMovedStrips, onFullScreen: StepAside);

            // The guard gives the shell SettleFor from here before it judges the work area.
            lastDocked = Clock();
            docked = true;

            // Its own screen cannot be read: wait, never dock at a guessed scale (see Scale). Left docked, so the guard
            // docks again on its own clock once the screen is back, and Undock still gives back whatever a dock before
            // this one reserved. This is also what keeps the shell's re-dock notice (OnShellMovedStrips) harmless while a
            // screen is dropping out, which is exactly the display change that sends one.
            if (Scale().TryGetValue(out var scale) is false)
                return Result.Failure($"The strip's screen {Format(new AppBar.Rect(Screen.Left, Screen.Top, Screen.Right, Screen.Bottom))} cannot be read right now.");

            var screen = new AppBar.Rect(Screen.Left, Screen.Top, Screen.Right, Screen.Bottom);
            var thicknessPx = (int)Math.Round(HeightDip * scale);
            asked = new AppBar.Rect(screen.L, screen.T, screen.R, screen.T + thicknessPx);
            var got = DockWith(AppBar.Edge.Top, screen, thicknessPx);
            granted = got;

            // Nothing to place a window in. Left docked, so the guard asks again on its own clock and Undock still gives
            // back whatever the shell may hold.
            if (got.R <= got.L || got.B <= got.T)
                return Result.Failure($"The shell granted no room for the strip on {Format(screen)}.");

            PlaceAt(hwnd, got, scale);
            if (!IsVisible) Show();
            return Result.Success();
        }
        finally
        {
            docking = false;
        }
    }

    // Gives the strip back to the shell (ABM_REMOVE), then hides the window: in that order, so a window is never left
    // gone with its band still reserved. The window stays alive and can be docked again; the strip set closes it when
    // its screen has left.
    public void Undock()
    {
        appBar?.Remove();
        docked = false;
        fullScreenOpen = false;
        Hide();
    }

    // THE 1 S GUARD, the docked bar's (FloatingBar.KeepDockedStrip) for one screen's top edge: the shell can stop
    // holding a strip without saying so, and Explorer restarting forgets every app bar. The same two clocks: never
    // within SettleFor of a dock (the shell may not have applied it yet), and at most once every RetryEvery, for a
    // shell that will not honour an app bar at all.
    //
    // It takes the strip back when the window is no longer inside its own screen, or when its own screen's work area
    // reaches under it (StripHeld, "top"). Only while its screen is among the `connected` ones: a screen that left, or
    // one that has dropped out for a moment while it wakes, is the reconcile's business on the settled layout, never a
    // reason to dock somewhere else (probe notes, section 3). And when its own screen cannot be read, no verdict:
    // it waits, as the bar's guard does.
    public void KeepStrip(IReadOnlyList<MonitorBounds> connected)
    {
        if (!docked || docking || !connected.Contains(Screen)) return;
        var now = Clock();
        if (now - lastDocked < FloatingBar.SettleFor || now - lastRepair < FloatingBar.RetryEvery) return;
        if (Held() is not false) return;

        lastRepair = now;
        repairs++;
        // A fresh registration, as the bar's guard makes: the old one may be one the shell no longer honours.
        appBar?.Remove();
        _ = Dock();
    }

    // Whether the strip is where it should be: false when the window has left its screen or the work area reaches under
    // it, null when that cannot be told (no window, or its own screen cannot be read right now).
    bool? Held()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0 || !NativeMethods.GetWindowRect(hwnd, out var bar)) return null;
        if (!Inside(bar)) return false;
        return OwnMonitor() is { } own ? FloatingBar.StripHeld("top", bar, own.Work) : null;
    }

    bool Inside(NativeMethods.RECT r) => r.Left >= Screen.Left && r.Top >= Screen.Top && r.Right <= Screen.Right && r.Bottom <= Screen.Bottom;

    // The shell said something on an edge moved (another app bar, the taskbar, a resolution change), or Explorer
    // restarted: ask again. Queued, because the notice arrives inside the shell's own message. It does not loop: a
    // SETPOS that grants the rectangle already held sends no further notice (measured, probe notes section 1). Nor does
    // it dock while the strip's screen cannot be read: Dock refuses then. Internal so a test can send the notice without
    // the shell, which only sends it to a registered app bar.
    internal void OnShellMovedStrips() =>
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (docked) _ = Dock();
        }));

    // Topmost is a shared band (CLAUDE.md): the taskbar and the Start menu climb over anything in it when activated,
    // so the strips join the bar's re-assertion on foreground change and on the 1 s timer. Not while a full-screen
    // window is up on this screen, which the shell asked the strip to keep clear of. SWP_NOACTIVATE is the load-bearing
    // flag, as for the bar: without it this would take the focus from whatever was just clicked.
    public void ReclaimTopmost()
    {
        if (!IsVisible || fullScreenOpen) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    // The shell's full-screen notice (AppBar's onFullScreen): out of the topmost band while a full-screen window is up,
    // so the strip does not sit over a video or a game, and back in when it closes. As the docked bar does
    // (FloatingBar.OnFullScreenApp): it steps aside, it is not hidden, moved or undocked, so the work area stays put.
    public void StepAside(bool fullScreen)
    {
        fullScreenOpen = fullScreen;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;
        NativeMethods.SetWindowPos(hwnd, fullScreen ? HwndNoTopmost : NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    // One line for the debug dump and, later, the trace: the screen, what was asked and granted, the screen's work area
    // as Windows reports it now, whether the shell registration stands, and how often the guard has taken it back.
    public string Describe() =>
        $"strip on {Format(new AppBar.Rect(Screen.Left, Screen.Top, Screen.Right, Screen.Bottom))}: "
        + $"asked {(asked is { } a ? Format(a) : "nothing")}, granted {(granted is { } g ? Format(g) : "nothing")}, "
        + $"work {(OwnMonitor() is { } own ? DebugDump.Format(own.Work) : "unreadable")}, "
        + $"docked {(docked ? "yes" : "no")}, registered {(appBar?.Registered == true ? "yes" : "no")}, "
        + $"{(fullScreenOpen ? "stepped aside for a full-screen window, " : "")}"
        + $"taken back {repairs} time(s){(repairs > 0 ? $", last {lastRepair.ToLocalTime():HH:mm:ss}" : "")}";

    static string Format(AppBar.Rect r) => $"{r.L},{r.T} to {r.R},{r.B} ({r.R - r.L}x{r.B - r.T})";

    // Moves the window, in physical pixels, exactly onto the granted rectangle. First onto the screen's scale, the way
    // the bar's dock does it (ScaleHop has the measurement): a window still at another screen's scale is rescaled by
    // Windows only when it lands on the new screen, and that rescale resizes it. So it hops there first, lets Windows
    // rescale it, and only then gets its final rectangle. Pixels, never Left/Top/Width/Height: those are DIPs at the
    // WINDOW's scale, the unit that is wrong exactly while the scales differ.
    void PlaceAt(nint hwnd, AppBar.Rect rect, double scale)
    {
        if (NativeMethods.GetWindowRect(hwnd, out var now)
            && ScaleHop.Target(rect.L / scale, rect.T / scale, now.Right - now.Left, now.Bottom - now.Top,
                (int)NativeMethods.GetDpiForWindow(hwnd),
                new MiddleDrag.Screen(Screen.Left, Screen.Top, Screen.Right, Screen.Bottom, (int)Math.Round(scale * 96))) is { } hop)
            NativeMethods.SetWindowPos(hwnd, 0, hop.Left, hop.Top, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        NativeMethods.SetWindowPos(hwnd, 0, rect.L, rect.T, rect.R - rect.L, rect.B - rect.T,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    // This strip's own screen and its work area, or null when Windows has no screen with exactly this rectangle right
    // now. Found from the screen's centre with "no monitor" as the fallback, and then the rectangle compared: the
    // nearest-screen answer is the one that once moved the docked bar to the primary (see the top of this file).
    (nint Handle, NativeMethods.RECT Work)? OwnMonitor()
    {
        var monitor = NativeMethods.MonitorFromPoint(
            new NativeMethods.POINT { X = Screen.Left + (Screen.Right - Screen.Left) / 2, Y = Screen.Top + (Screen.Bottom - Screen.Top) / 2 },
            MonitorDefaultToNull);
        if (monitor == 0) return null;
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        return NativeMethods.GetMonitorInfo(monitor, ref info)
               && (info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom) == (Screen.Left, Screen.Top, Screen.Right, Screen.Bottom)
            ? (monitor, info.rcWork)
            : null;
    }

    // The screen's own scale, from the screen itself, or no value when it cannot be read (it dropped out for a moment).
    // Nothing stands in for it, for the reason Scale gives.
    Maybe<double> ScaleOfScreen() =>
        OwnMonitor() is { } own && NativeMethods.GetDpiForMonitor(own.Handle, NativeMethods.MDT_EFFECTIVE_DPI, out var dpi, out _) == 0 && dpi > 0
            ? dpi / 96.0
            : Maybe<double>.None;

    // A drag carrying a window icon, in WindowDragSource's format. Anything else (a file, some text) is refused.
    internal static bool Accepts(IDataObject data) => data.GetDataPresent(DraggedWindow.DragFormat);

    // The drop itself, split from the event so a test can hand it a payload: DragEventArgs cannot be constructed.
    internal void TakeDrop(IDataObject data)
    {
        if (data.GetData(DraggedWindow.DragFormat) is DraggedWindow dragged) Dropped?.Invoke(dragged.Handle);
    }

    // A right-click on the tag or on the free space asks for the bar's menu. One on a tab is the tab's own: the tab
    // carries its window's menu (a ContextMenu, opened after this event if nothing handles it), so it is left alone and
    // not marked handled. A tab is any child of the row other than the tag, found by walking up from what was clicked.
    void OnRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Right || e.Handled || Ancestry(e.OriginalSource as DependencyObject).Any(d => d != NameTag && ParentOf(d) == Row)) return;
        MenuRequested?.Invoke();
        e.Handled = true;
    }

    // From an element up to the root. Visual parents where there are any, logical ones for what has none (a Run inside
    // a TextBlock is a ContentElement, not a Visual, and VisualTreeHelper throws for it).
    static IEnumerable<DependencyObject> Ancestry(DependencyObject? at) =>
        at is null ? [] : Ancestry(ParentOf(at)).Prepend(at);

    static DependencyObject? ParentOf(DependencyObject d) =>
        d is Visual or Visual3D ? VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    // Never in Alt+Tab: WS_EX_TOOLWINDOW, for the reason the bar gives in its own OnSourceInitialized (ShowInTaskbar
    // false alone leaves an owned window listed through its invisible owner). Set as soon as the handle exists, which
    // is before the first show: the strip set asks for the handle first, to hand it to WindowMonitor.Ignore.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE,
            NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE) | (nint)NativeMethods.WS_EX_TOOLWINDOW);
    }

    // A strip closed without Undock (the app going away by a path that forgot it) still gives its band back. AppBar.Remove
    // does nothing when nothing is claimed, so the usual Undock-then-Close costs nothing extra here.
    protected override void OnClosed(EventArgs e)
    {
        appBar?.Remove();
        docked = false;
        base.OnClosed(e);
    }

    static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
