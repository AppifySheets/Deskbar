using System.Reactive;
using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Abstractions;

// One screen as the tiling grid sees it: its number (the same number ITileSurface.ScreenOf answers for a
// window), its WORK area in physical pixels (the taskbar and a docked bar already taken out, spec "Which
// windows tile"), and its DPI, which scales the gap so it looks the same on every screen.
//
// Bounds is the whole monitor (MONITORINFO.rcMonitor), from the same lookup as the work area, so it always
// contains it. It is what tile sizes are kept by (Petre: "keep my sizes", "places"): the screen NUMBER can
// change when a screen is unplugged and Windows renumbers, and the work area changes whenever the taskbar or
// the bar docks, but where the monitor sits on the desktop changes only when the user rearranges screens.
public sealed record TileScreen(int Number, WindowRect WorkArea, int Dpi, WindowRect Bounds);

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

    // Is this window still moving? True while it has a pending quiet timer: it moved within the last quiet
    // interval, or it is waiting for the mouse buttons to come up. The Tiler does not place a window that is
    // settling, because that window is under the user's pointer (a title-bar drag, an AltSnap drag) or in the
    // middle of a move of its own, and a Place would make it jump. Its own Settled follows and puts it right.
    bool IsSettling(WindowHandle window);

    // Where the window visibly is. None for a window that has gone between the question and the answer.
    Maybe<WindowRect> VisibleFrameOf(WindowHandle window);

    // Puts the window's VISIBLE frame on `visibleFrame`, compensating for its invisible borders, with
    // SWP_NOZORDER | SWP_NOACTIVATE so a re-tile never raises a tile over a maximized window or takes focus.
    // A failure is ordinary (the window closed, or it is elevated) and the Tiler carries on with the rest.
    Result Place(WindowHandle window, WindowRect visibleFrame);

    // Un-maximizes the window (ShowWindowAsync SW_RESTORE). Asynchronous: the window is still maximized when this
    // returns, and its Settled follows once it has restored. Only ever asked of a window on the desktop being
    // looked at, because un-maximizing one on another desktop makes Windows follow it there (spec, anti-goals).
    void Restore(WindowHandle window);

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

    // Was a mouse button down at ANY location change of the change that last settled for this window? The
    // half of the resize test that the geometry cannot answer: a window whose edges moved with no button down
    // was resized by its app, a keyboard snap or Deskbar itself, not dragged by the user (Petre: "i want to be
    // able to resize tiles by dragging the separation"). Asked of the end of the change it would always be
    // false, because a drag ends with the button released; so it is remembered from the moves themselves.
    // Either button counts, so AltSnap's right-drag resize is by hand too.
    //
    // Valid while the Tiler handles that window's Settled (which is raised synchronously), and afterwards until
    // the window's next change starts, which resets it. False for a window that has never settled.
    bool ByHand(WindowHandle window);

    // The monitor set or a work area changed: a screen plugged in or out, the taskbar moved, the bar docked.
    IObservable<Unit> ScreensChanged { get; }

    // --- while a tile is being dragged (Tiler.Drag.cs) ---------------------------------------------------------
    // Petre: "can you resize, or show me how it would end up on the screen, before i let it go?", then from a
    // mockup, "Live resize + move preview": the tiles beside a dragged separation follow it as it moves, and a
    // tile being moved shows where it will land.

    // A watched window moved while a mouse button was down: the drag is still going on. Throttled to about 30 a
    // second per window, because a drag raises a location change for every pixel and each one may place windows.
    // The last one may be dropped by the throttle; the window's Settled always follows and decides the end.
    IObservable<WindowHandle> Moving { get; }

    // Puts `windows` directly behind `front` in the z-order, without activating or moving any of them: the tiles of
    // a workspace come up together when one of them is activated (Tiler.Drag.cs). Never takes focus from `front`.
    void RaiseBehind(WindowHandle front, IReadOnlyList<WindowHandle> windows);

    // Where the mouse pointer is, in physical pixels like every rectangle here. None if Windows will not say.
    Maybe<(int X, int Y)> Pointer();

    // Shows translucent rectangles over the screen where a dragged tile, and the tile it would swap with, will
    // land. Never activated, never hit by a click, never on the bar. An empty list hides them.
    void Preview(IReadOnlyList<TilePreview> tiles);
}

// One rectangle of a drag's preview. `Dragged` is the window under the pointer's own landing place (drawn filled);
// otherwise it is the tile it would swap with, going to the dragged window's old slot (drawn as an outline).
public sealed record TilePreview(WindowRect Frame, bool Dragged);
