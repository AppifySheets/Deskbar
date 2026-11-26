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
    // 24 DIP. It shipped at 32, the mockup Petre picked ("B", spec "The look"), and on his screens it was too tall:
    // "make the fonts smaller, maybe make the strip with less padding". 24 holds the 20 DIP icon with its 1 DIP of
    // breathing room and the 2 DIP underline. Turned into the screen's pixels at the screen's own scale, so 36 px at 150%.
    public const double HeightDip = 24;

    // The bar's own flat surface (BarSurface.Solid), so a strip reads as part of Deskbar. Flat only while there is no
    // frost: since Deskbar task d52ca36e (Petre, of the bar's top: "i want to make this like the rest of the bar; and the
    // top bar too") a strip stands on its screen's wallpaper, frosted, under the band's veil, like the bar (ShowFrost).
    // And the tag's grey for an unnamed desktop, which has no workspace colour. Frozen, as every static brush must be
    // (CLAUDE.md, traps): an unfrozen one takes the affinity of the first thread that touches it and throws on the next.
    // BarSurface's are.
    static readonly Brush StripBackground = BarSurface.Solid;
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

    // The window's content, which carries the strip's top layer: the flat base without frost, the band's veil over one.
    // Never transparent, so the free space right of the last tab stays hit-testable (see the constructor).
    readonly Border root;

    // THE STRIP'S FROST (Deskbar task d52ca36e). A strip is an app bar along its screen's top edge, so, like the docked
    // bar, the only thing ever behind it is the wallpaper: the bar cuts that strip of the picture, frosts it
    // (FloatingBar.StripFrost.cs) and hands it here. The frost is the WINDOW's background and the veil (BarSurface.FrostVeil,
    // the rows band's half) is the root's, so the strip reads exactly as the bar's rows do. The window is not layered, so a
    // see-through background would show black: there is no 85% fallback here, only the frost or the flat base.
    //
    // What the frost on show was made from, so the bar's twice-a-second check can tell nothing changed (MakeFrost compares
    // it). Null while the strip is flat.
    internal FloatingBar.FrostSource? FrostSource { get; private set; }

    // The last reason this strip went without frost, so the trace says it once and not on every check.
    internal string? FrostFallback { get; set; }

    // What the last frost that failed was cut from (FloatingBar.StripFrost.cs), so a check where nothing changed skips
    // decoding the picture again. Null after a frost, after the setting went off, and when Windows named no picture.
    internal FloatingBar.FrostSource? FrostFailedFor { get; set; }

    // Puts a frost on the strip (its source and its brush, frozen), or takes it off (nulls).
    internal void ShowFrost(FloatingBar.FrostSource? source, Brush? frost)
    {
        FrostSource = frost is null ? null : source;
        Background = frost ?? StripBackground;
        root.Background = frost is null ? StripBackground : BarSurface.FrostVeil;
    }

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

    // The window covering this strip's whole screen right now (a full-screen Remote Desktop, a video, a browser in F11),
    // or 0 when nothing does. In the app it is TopLevelWindows.CoveringScreen for this strip's own screen rectangle:
    // the windows on that screen, top first, the first of them deciding (FullScreenCover). Faked in the tests, whose
    // made-up screens are on no real monitor.
    internal Func<nint> Covering { get; init; }

    // The covering window the strip is standing directly behind, or 0 while it is not. For the trace, which says it
    // once per change, and for Describe.
    nint steppedBelow;

    // What the last dock asked for and what it got, for Describe.
    AppBar.Rect? asked;
    AppBar.Rect? granted;

    // The band the shell granted at the last dock, physical pixels, or none when no dock has been granted any room (it
    // was refused because the screen could not be read, or the shell answered with an empty rectangle). For the strip
    // set, which moves windows off the band once the strip is up (ScreenStrips.ClearUnder) and must use the band the
    // shell really gave rather than recompute a height from a DPI.
    internal Maybe<AppBar.Rect> Granted => granted is { } g && g.R > g.L && g.B > g.T ? g : Maybe<AppBar.Rect>.None;

    // When the strip last docked and when the guard last took it back, and how often it has, for the guard's two
    // clocks and for Describe.
    DateTime lastDocked = DateTime.MinValue;
    DateTime lastRepair = DateTime.MinValue;

    // Repairs and refusals are counted APART, on purpose. A repair is a re-dock the guard made that the shell honoured: the
    // shell had let go of the strip and now holds it again. A refusal is a dock the strip itself declined (its screen
    // could not be read) or the shell answered with no room. Counted as repairs, every refused guard attempt read in
    // Describe as "the shell dropped the strip again" when the strip had never been held to drop; the two numbers say
    // different things, and the trace needs both.
    int repairs;
    int refusals;

    // Where the strip's trace lines go: ClickTrace in the app (the one switch, %APPDATA%\Deskbar\trace.on, and the log
    // %TEMP%\deskbar-trace.log, CLAUDE.md "Instrumentation"), a list in the tests, which cannot read the log.
    internal Action<string> Trace { get; init; } = ClickTrace.Write;

    // Whether tracing is on. Asked BEFORE a line is built: the 1 s beat reaches TraceGeometry for every strip, and
    // Describe is a native monitor query plus a long string, so with tracing off (nearly always) building the line only
    // for ClickTrace.Write to throw it away would cost that every second. The tests set it true to read what is traced.
    internal Func<bool> Tracing { get; init; } = () => ClickTrace.On;

    // How often Describe has run, so a test can show that tracing off never builds a line.
    internal int DescribeCalls { get; private set; }

    // One event line, built only when tracing is on.
    void Say(Func<string> line)
    {
        if (Tracing()) Trace(line());
    }

    // The last geometry line written, so TraceGeometry writes only when the line changes.
    string lastGeometry = "";

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
            // The tab titles' size (FloatingBar.StripTab), semibold so the workspace's name still reads as the strip's
            // label. It went 12, 10 and 9.5 ("smaller font" twice, then "too small now"), and settled with the titles at 12.
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
        root = new Border { Background = StripBackground, Child = Row };
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
        Covering = () => TopLevelWindows.CoveringScreen(Screen.Left, Screen.Top, Screen.Right, Screen.Bottom) ?? 0;
    }

    // Fills the strip: the name tag on `colour` (the neutral grey when there is none, an unnamed desktop), then the
    // tabs, each StripPlan.TabWidth wide for the room left after the tag. Replaces whatever was there, so the bar can
    // hand over a fresh set on every rebuild, and an empty list leaves the tag alone on the strip, which is how a
    // screen with no windows of the current workspace keeps its strip (and its work area) through a switch.
    //
    // The room comes from the SCREEN's width at the screen's scale, not from the window's ActualWidth: the strip is
    // filled before it is first shown, and a window not yet laid out has no width to read.
    //
    // `jumps` are the pinned workspaces' items (FloatingBar.WorkspaceJumps.cs), placed AFTER the tabs: Petre, "put it
    // at the end of the list of windows". They keep their own width and the tabs share what is left, so tag, tabs and
    // items together still fit the screen and the strip stays one line. Optional, so every caller that hands over
    // tabs alone is unchanged.
    public void SetTabs(string name, Brush? colour, IReadOnlyList<FrameworkElement> tabs, IReadOnlyList<FrameworkElement>? jumps = null)
    {
        nameText.Text = name;
        NameTag.Background = colour ?? NeutralTag;
        Row.Children.RemoveRange(1, Row.Children.Count - 1);

        var items = jumps ?? [];
        NameTag.Measure(new Size(double.PositiveInfinity, HeightDip));
        items.ToList().ForEach(item => item.Measure(new Size(double.PositiveInfinity, HeightDip)));
        var width = StripPlan.TabWidth(WidthDip() - NameTag.DesiredSize.Width - items.Sum(item => item.DesiredSize.Width), tabs.Count);
        tabs.ToList().ForEach(tab =>
        {
            tab.Width = width;
            Row.Children.Add(tab);
        });
        items.ToList().ForEach(item => Row.Children.Add(item));
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
                return Refuse($"The strip's screen {Format(new AppBar.Rect(Screen.Left, Screen.Top, Screen.Right, Screen.Bottom))} cannot be read right now.");

            var screen = new AppBar.Rect(Screen.Left, Screen.Top, Screen.Right, Screen.Bottom);
            var thicknessPx = (int)Math.Round(HeightDip * scale);
            asked = new AppBar.Rect(screen.L, screen.T, screen.R, screen.T + thicknessPx);
            var got = DockWith(AppBar.Edge.Top, screen, thicknessPx);
            granted = got;

            // Nothing to place a window in. Left docked, so the guard asks again on its own clock and Undock still gives
            // back whatever the shell may hold.
            if (got.R <= got.L || got.B <= got.T)
                return Refuse($"The shell granted no room for the strip on {Format(screen)}.");

            PlaceAt(hwnd, got, scale);
            if (!IsVisible) Show();
            Say(() => $"strip on {Screen.Left},{Screen.Top}: docked, asked {Format(asked.Value)}, granted {Format(got)}");
            return Result.Success();
        }
        finally
        {
            docking = false;
        }
    }

    // A dock that did not happen: counted (see `refusals`), traced with its reason so a strip that never appeared can be
    // told from one that was never made, and handed back as the failure. This is the one place a refusal is traced, so
    // every caller (the strip set's first dock, the guard, the shell's notice) is covered without each tracing it.
    Result Refuse(string reason)
    {
        refusals++;
        Say(() => $"strip on {Screen.Left},{Screen.Top}: dock refused: {reason}");
        return Result.Failure(reason);
    }

    // Gives the strip back to the shell (ABM_REMOVE), then hides the window: in that order, so a window is never left
    // gone with its band still reserved. The window stays alive and can be docked again; the strip set closes it when
    // its screen has left.
    public void Undock()
    {
        appBar?.Remove();
        docked = false;
        fullScreenOpen = false;
        steppedBelow = 0;
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
        // A fresh registration, as the bar's guard makes: the old one may be one the shell no longer honours.
        appBar?.Remove();
        // Dock's failure is not thrown away: Dock has traced it as a refusal already, and it is counted as one. Only a
        // dock that was honoured is a repair.
        //
        // A refused one HIDES the window (final review, third finding). The registration was given up on the line above,
        // so after a refusal no band is reserved, and a window still shown there sits topmost over the top of the screen,
        // on the title bars of the maximized windows the shell has just let back up, until the next try RetryEvery later:
        // the spec's third anti-goal. Hidden, the screen is simply without its strip for that while; the next dock that
        // works shows it again (Dock shows a window that is not up), and the strip set pins a strip once it is shown.
        // Hiding does not undock: `docked` stays set, so this guard keeps trying.
        if (Dock().IsSuccess)
        {
            repairs++;
            Say(() => $"strip on {Screen.Left},{Screen.Top}: taken back by the guard ({repairs} so far)");
        }
        else
            Hide();
    }

    // The strip's Describe line, written to the trace when it differs from the last one written. Called on the guard's 1 s
    // beat (ScreenStrips.Tick), so an unchanged strip says it once and a change (a re-dock, a work area that moved, a
    // full-screen window) is one new line. Like the bar's geometry traces (CLAUDE.md, "Instrumentation").
    public void TraceGeometry()
    {
        if (!Tracing()) return;
        var line = Describe();
        if (line == lastGeometry) return;
        lastGeometry = line;
        Trace(line);
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
    //
    // A window covering the strip's screen comes first: the strip stands directly behind it (StepBelowCover), whatever
    // the shell has or has not said.
    public void ReclaimTopmost()
    {
        if (!IsVisible) return;
        // Not over one of Deskbar's own menus, which has no owner window to keep it above the strip (OwnMenus).
        if (OwnMenus.AnyOpen) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;
        if (StepBelowCover(hwnd) || fullScreenOpen) return;
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        // Then any of Deskbar's own tooltips back over the strip, which has just covered any that reach onto it: a
        // tooltip's popup has no owner window (task d4695a97, OwnToolTips).
        OwnToolTips.Lift();
    }

    // The shell's full-screen notice (AppBar's onFullScreen): out of the topmost band while a full-screen window is up,
    // so the strip does not sit over a video or a game, and back in when it closes. As the docked bar does
    // (FloatingBar.OnFullScreenApp): it steps aside, it is not hidden, moved or undocked, so the work area stays put.
    // Behind the covering window when there is one on this screen, for the reason StepBelowCover gives.
    public void StepAside(bool fullScreen)
    {
        fullScreenOpen = fullScreen;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0 || StepBelowCover(hwnd)) return;
        NativeMethods.SetWindowPos(hwnd, fullScreen ? HwndNoTopmost : NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    // STANDING BEHIND A FULL-SCREEN WINDOW. Petre, on task b488e2e9 ("Maximized window captions sit under the top
    // bar"), with a screenshot of a Remote Desktop window maximized on a screen with a strip: the strip lay over the
    // top 36 px of it, across the caption of the remote app inside. Remote Desktop's maximize button takes it FULL
    // SCREEN when the session fits the screen, a window with exactly the screen's rectangle that ignores the work area,
    // so the band the strip reserves does not keep it clear, as it keeps ordinary maximized windows clear.
    //
    // Two things kept the strip on top of it. The first is that the shell's full-screen notice alone decided, and on
    // that machine no step aside was ever traced while the window was up. The strip now looks for itself, on the 1 s
    // beat and on every foreground change, the way the floating bar does (FloatingBar.StepAsideIfCovered): the first
    // window in z-order on the strip's screen, if it covers the whole screen (FullScreenCover has the measured rules).
    // The second would have kept it on top even with the notice: leaving the topmost band (HWND_NOTOPMOST) puts a
    // window at the TOP of the ordinary band, above every ordinary window, and a full-screen Remote Desktop or a browser
    // in F11 is an ordinary window. Measured for the floating bar on build 26300 and written there: only putting the
    // window directly behind the covering one puts it where it belongs. A covering window that is itself topmost (a
    // video player) needs only the first call.
    //
    // True while the strip is behind a covering window, so the caller does not put it back on top.
    bool StepBelowCover(nint hwnd)
    {
        var cover = Covering();
        if (cover == 0)
        {
            if (steppedBelow != 0) Say(() => $"strip on {Screen.Left},{Screen.Top}: back on top, nothing covers its screen");
            steppedBelow = 0;
            return false;
        }

        NativeMethods.SetWindowPos(hwnd, HwndNoTopmost, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        if (!TopLevelWindows.IsTopmost(cover))
            NativeMethods.SetWindowPos(hwnd, cover, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

        if (steppedBelow != cover)
            Say(() => $"strip on {Screen.Left},{Screen.Top}: stepped below {cover:X} class={TopLevelWindows.ClassOf(cover)}, which covers its screen");
        steppedBelow = cover;
        return true;
    }

    // One line for the debug dump and, later, the trace: the screen, what was asked and granted, the screen's work area
    // as Windows reports it now, whether the shell registration stands, and how often the guard has taken it back.
    public string Describe()
    {
        DescribeCalls++;
        return DescribeLine();
    }

    string DescribeLine() =>
        $"strip on {Format(new AppBar.Rect(Screen.Left, Screen.Top, Screen.Right, Screen.Bottom))}: "
        + $"asked {(asked is { } a ? Format(a) : "nothing")}, granted {(granted is { } g ? Format(g) : "nothing")}, "
        + $"work {(OwnMonitor() is { } own ? DebugDump.Format(own.Work) : "unreadable")}, "
        + $"docked {(docked ? "yes" : "no")}, registered {(appBar?.Registered == true ? "yes" : "no")}, "
        + $"{(fullScreenOpen ? "stepped aside for a full-screen window, " : "")}"
        + $"{(steppedBelow != 0 ? $"behind {steppedBelow:X}, which covers the screen, " : "")}"
        + $"taken back {repairs} time(s){(repairs > 0 ? $", last {lastRepair.ToLocalTime():HH:mm:ss}" : "")}, refused {refusals} time(s)";

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
