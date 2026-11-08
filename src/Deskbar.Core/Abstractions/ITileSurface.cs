using System.Reactive;
using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Abstractions;

// One screen as the tiling grid sees it: its number (the same number ITileSurface.ScreenOf answers for a
// window), its WORK area in physical pixels (the taskbar and a docked bar already taken out, spec "Which
// windows tile"), and its DPI, which scales the gap so it looks the same on every screen.
public sealed record TileScreen(int Number, WindowRect WorkArea, int Dpi);

// Everything the Tiler needs from Windows, and nothing it does not. The Tiler (Deskbar.Core.Tiling) decides
// WHEN and WHERE windows go; this answers what the windows are like and does the moving. Kept apart for the
// same reason as IScreenLayout: the decisions are the part that can be wrong in ways nobody notices for a
// week, and they can only be tested against a fake.
//
// All rectangles are VISIBLE frames (DWMWA_EXTENDED_FRAME_BOUNDS), not GetWindowRect: windows draw invisible
// resize borders, so the two differ by a few pixels per side, and by a different amount per window (spec,
// "Constraints and known traps"). The implementation converts on the way in and out; the Tiler never sees
// the invisible part.
public interface ITileSurface
{
    // The screens there are right now, with their work areas.
    IReadOnlyList<TileScreen> Screens();

    // The screen a window is on, as MonitorFromWindow says (spec: "A window belongs to the screen
    // MonitorFromWindow says it is on").
    int ScreenOf(WindowHandle window);

    // May this window take part in the grid at all? False for dialogs and owned windows, tool windows,
    // windows without a thick frame, Deskbar's own windows, and windows Deskbar cannot move (an elevated
    // window refuses SetWindowPos from a non-elevated process). Decided by the window's own styles, never
    // by a rule file.
    bool CanTile(WindowHandle window);

    bool IsMaximized(WindowHandle window);
    bool IsMinimized(WindowHandle window);

    // Where the window visibly is. None for a window that has gone between the question and the answer.
    Maybe<WindowRect> VisibleFrameOf(WindowHandle window);

    // Puts the window's VISIBLE frame on `visibleFrame`, compensating for its invisible borders, with
    // SWP_NOZORDER | SWP_NOACTIVATE so a re-tile never raises a tile over a maximized window or takes focus.
    // A failure is ordinary (the window closed, or it is elevated) and the Tiler carries on with the rest.
    Result Place(WindowHandle window, WindowRect visibleFrame);

    // Asks DWM to round the window's corners (DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND). Called once
    // per window as it joins a grid; never undone (spec, "Rounded corners").
    void RoundCorners(WindowHandle window);

    // The windows whose moves matter: the LOCATIONCHANGE hook is narrowed to these. An EMPTY set removes the
    // hook entirely, which is what keeps it off while the experiment is off or the desktop being looked at
    // is not tiled (spec, "Keeping it tidy").
    void Watch(IReadOnlySet<WindowHandle> windows);

    // A watched window stopped moving (no further change for the debounce interval) with both mouse
    // buttons up. Covers every way a window moves: a title-bar drag, an AltSnap drag, a keyboard snap,
    // maximize and un-maximize, an app resizing itself, and Deskbar's own Place.
    IObservable<WindowHandle> Settled { get; }

    // The monitor set or a work area changed: a screen plugged in or out, the taskbar moved, the bar docked.
    IObservable<Unit> ScreensChanged { get; }
}
