using System.Reactive.Subjects;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CSharpFunctionalExtensions;
using Deskbar.App;
using Deskbar.Core.Domain;
using Deskbar.Core.Tasks;

namespace Deskbar.Windows.Tests;

// The tasks panel (TasksPanel) behind a tasks board under test, with every seam faked the way TasksPanelTests fakes
// them, so no board test ever registers a real app bar (that reserves a strip of the real screen and moves every
// maximized window on it; plan 2026-11-23-tasks-panel, "Global constraints").
//
// The bar is a plain Subject standing in for FloatingBar.Strip, and Docked() is the bar's first docked notice, answered
// at once (the panel answers on its dispatcher, so it is pumped here). After that a board's Show docks the panel
// synchronously: the fake grants exactly what was asked, on a made-up screen far off every real monitor, so the window
// is shown, topmost and never seen.
//
// THE SCALE IS THE WINDOW'S OWN. The fake screen is in physical pixels, and the panel converts the board's width in
// DIPs to pixels with ScaleOf. Answering with the window's own DPI (read once its handle exists) makes the window come
// out exactly that many DIPs wide wherever the suite runs, so a test that reads a line's layout sees the width it asked
// for, as the floating window's tests did with its XAML width.
internal sealed class TasksPanelRig
{
    // 4000 px wide so half of it (the panel's ceiling, TasksPanelLayout.Clamp) is well over any width a test asks for at
    // any scale; 700 px high so the window stays inside the size Windows allows a top-level window on a small CI desktop
    // (ScreenStripTests has the story).
    internal static readonly WindowRect Screen = new(-30000, -30000, -26000, -29300);

    public readonly Subject<BarStrip> Bar = new();
    public readonly List<string> Calls = [];
    public readonly TasksPanel Panel;
    readonly Window window;

    public TasksPanelRig(Window window)
    {
        this.window = window;
        // Made now, so GetDpi below answers the window's own scale rather than the system's.
        new WindowInteropHelper(window).EnsureHandle();
        Panel = new TasksPanel(window, Bar)
        {
            DockWith = (edge, screen, px) =>
            {
                Calls.Add($"DockWith {edge} {px}");
                return edge == PanelEdge.Left
                    ? new WindowRect(screen.Left, screen.Top, screen.Left + px, screen.Bottom)
                    : new WindowRect(screen.Right - px, screen.Top, screen.Right, screen.Bottom);
            },
            Undock = () => Calls.Add("Undock"),
            ScaleOf = _ => VisualTreeHelper.GetDpi(window).DpiScaleX,
            // The shell holding the strip: the guard never repairs in a board test.
            WorkAreaOf = screen => new WindowRect(screen.Left + 1000, screen.Top, screen.Right - 1000, screen.Bottom),
            Tracing = () => false,
            // Nothing covers the made-up screen full screen; asked of the real windows, the answer would depend on the
            // machine running the suite.
            CoveringWindow = _ => 0,
        };
    }

    // The bar docks on `edge` ("right" or "left") of the fake screen and announces it; answered before this returns.
    // `screenDips` makes the fake screen that many DIPs wide at the window's scale instead of 4000 px, so the panel's
    // clamp to half the screen bites (Task 8 fix round 1: the lines follow the width the panel docks at). Called again,
    // it is the bar moving to another screen: the panel registers afresh there.
    public TasksPanelRig Docked(string edge = "right", double? screenDips = null)
    {
        var screen = screenDips is { } dips
            ? Screen with { Right = Screen.Left + (int)Math.Round(dips * VisualTreeHelper.GetDpi(window).DpiScaleX) }
            : Screen;
        Bar.OnNext(new BarStrip(edge, screen, Maybe<WindowRect>.None, edge == "left" ? screen.Left + 150 : screen.Right - 150, Gone: false));
        Pump();
        return this;
    }

    // Runs whatever the panel queued on its dispatcher (TasksPanelTests' Rig.Pump has the reasoning).
    public void Pump() => window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
}
