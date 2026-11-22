# Locked tiles, and new windows looking at the other screens

Date: 2026-11-22. A small change to tiling's arrival rules, on top of
`2026-11-19-tiling-predictable-placement-design.md` ("Change 2", a new window splits the tile you last used).

## Why

Petre wants a window in a tiled workspace to stay put: "i want to pin the window so that it's not moving". Of the
seven ways a tile moves today, the one this answers is a new window halving it. And when a window opens, Deskbar
should "check if there's space on another screen too" before halving anything.

## Goals

- A tile can be locked. A new window never splits a locked tile.
- A new window goes to another screen that has room before it halves a tile on this one. "Room" means an empty
  screen or a kept opening (Petre picked this over balancing by tile size).

## Non-goals

- A full lock. A locked tile still grows when a neighbour in its split leaves, still follows a dragged separation,
  still takes part in "Even out tiles" and "Reset to grid", still swaps when dragged, and still moves when a screen
  or work area changes. Each of those can be added later if live use asks for it.
- Floating a window out of the grid.
- A badge or other mark on a locked window's icon in the bar. The pin on the window itself (below) is the sign.
- Other buttons on the window. The pin is the only one for now; the overlay that carries it can take more later.
- Balancing windows across screens by tile size. That would send windows to screens Petre is not looking at, which
  the predictable placement design names as a failure.

## Anti-goals

- A window dropped on a screen, or restored from minimized, landing on another screen. Both stay on their screen,
  as they do today (Petre: "let's respect which monitor i put it to").
- A window landing where it cannot be seen: on top of a maximized window, or behind one.

## Approaches considered (for "space on another screen")

- A, picked: only an empty screen or a kept opening counts as room. Predictable, and a window only goes to
  another screen when this one would otherwise lose half a tile to it.
- B: A plus balancing, sending the window to whichever screen has the largest tile. Rejected, see non-goals.

## Behaviour

Arrival order for one new window (only the steps marked new change):

1. A minimized tile coming back goes to its old place (unchanged).
2. A window dropped on a screen or restored from minimized stays on that screen and only goes through steps 3,
   5 and 6 there (unchanged).
3. A kept opening: the last-used tile's screen, then the window's own screen (unchanged), then any other screen
   of the workspace (new).
4. An empty screen (new): the window's own screen if it is empty, then any other empty screen, in screen number
   order. A screen counts as empty when no tile of this workspace is on it and no maximized window of this
   workspace is set aside on it (one would cover the new tile, or it would cover the maximized window).
5. Split the tile last used, unless it is locked or fails the quarter floor (locked is new).
6. Split the largest unlocked tile on the target screen. If every tile there is locked, split the largest unlocked
   tile on any other screen (new). If every tile everywhere is locked, the lock is ignored for this one window,
   because a window has to go somewhere.

Steps 3 and 4 do not apply to the second and later windows of one evaluate any differently from the first: each
arrival sees the board as the earlier ones left it.

## The lock

- Toggled from the window icon's right-click menu in the bar: a checkable "Don't split this tile", shown only while
  that window is a tile of the workspace being looked at (on the Tiler's board). It is the last item before the
  separator that starts the closing group.
- Remote twin on the control pipe, `lock-tile <hwnd> on|off` (the shape of `remember-workspace <hwnd> on|off`),
  listed in `ControlUsage`, because every menu command has one.
- My call: the Tiler holds the locked set, keyed by window handle, in memory only. It is forgotten when the window
  closes and when Deskbar restarts. Handles get reused after a reboot, so writing them to `state.json` could lock
  an unrelated window later; keying on process and start time would fix that, but costs more than the feature is
  worth until live use says restarts losing the lock is a problem.
- `TileArrival.Choose` already takes a `skip` set for restoring maximized tiles; locked tiles join that set, and
  its "every tile skipped, ignore the skip" rule gives step 6's last sentence.

## The pin on the window

Petre: "can you have extra buttons on the windows themselves, maybe on top of the window, so i don't have to go
to the bar?", then "can you also draw the pin on the window?". From four mockups he picked A: a pin beside the
minimize button, in the title bar.

- What it is: a small Deskbar window laid over the tile's title bar, never anything inside the app. Adding a real
  button to another app's title bar means injecting code into its process, which fails on elevated windows, trips
  antivirus and would not pass Store certification.
- Look: a padlock glyph in a rounded square about the size of a caption button's icon (24 DIP). Locked: a closed
  padlock on a filled square, always shown. Unlocked: an open padlock in a faint outline, shown only while the mouse
  is over that tile's title bar; elsewhere an unlocked tile shows nothing. Clicking the mark toggles the lock, the
  same as the bar's menu item, and does not activate the mark's own window. Petre first picked a pushpin; the probe
  found VS Code's own "always on top" pushpin sits exactly where the mark goes, so he switched to the padlock (B in
  the glyph mockup, 2026-11-22). "Pin" below means this mark.
- Where: just left of the window's caption buttons. The caption buttons' bounds come from
  `DwmGetWindowAttribute(DWMWA_CAPTION_BUTTON_BOUNDS)` where Windows answers; a window with a title bar it draws itself
  (VS Code, Chromium) may answer differently, so a probe on VS Code, Edge or Chrome, Explorer and Windows Terminal
  comes first and settles the placement and any fallback. Known cost, accepted by Petre: on VS Code and Chromium the
  pin covers whatever the app put beside minimize (layout buttons, tab search). The probe
  (`docs/superpowers/notes/2026-11-22-tile-pin-probes.md`) settled it: Edge, Chrome, Explorer and Terminal report
  their buttons; VS Code reports a zero-width rectangle, so a window whose bounds have height but no width falls back
  to three 46 DIP buttons at the right edge of its visible frame (`DWMWA_EXTENDED_FRAME_BOUNDS`).
- Which windows: only tiles of the tiled workspace on the desktop being looked at (the Tiler's board). Not floating
  windows, not maximized or minimized ones, and no pin on any other desktop. The pins go away when the desktop
  changes or tiling stops, and are made again on arrival.
- Following the window: NOT by making the overlay owned by the tile. An owner or parent relationship across two
  processes ties the two threads' input together, so a hung app (VS Code busy, Chrome stuck) could freeze Deskbar's
  bar with it. Instead the overlay is an ordinary unowned Deskbar window (tool window, never activated, not in
  Alt+Tab or the taskbar), moved on the Tiler's own LOCATIONCHANGE for tiled windows, and put directly above its tile
  in z-order (`SetWindowPos` after the window above the tile) whenever the foreground window or the z-order changes.
  Tiles do not overlap one another, so the only things that can cover a tile are floating windows and dialogs, and
  the pin goes under them with the tile. The probe checks this holds when a tile is activated, covered and
  uncovered.
- Hover: my call, a cursor poll (`GetCursorPos`, around 100 ms) that runs only while a tiled workspace is the one
  being looked at, rather than a low-level mouse hook, which costs every mouse event on the machine.
- Deskbar's own windows: the overlays opt out of the bar, placement memory and the roster (`WindowMonitor.Ignore`),
  as the bar does.

## Delivery

Two pull requests, my call. The first is the lock, the new arrival rules, the bar's menu item and the pipe command:
small, testable against fakes, and usable on its own. The second is the pin on the window, which starts with the
probe and is the riskier half. Nothing in the first waits on the second.

## Trace

Every arrival decision already writes a `tile:` line. The new ones do too: the window took another screen's
opening, took an empty screen, or skipped a locked tile (naming it).

## Left to the builder

- Where the menu item's code lives (`FloatingBar.Tiling.cs` is the natural place), and how the bar reaches the
  Tiler's lock (the bar already asks it `InTile`).
- Tests in `TilerTests.Arrival.cs` and `TileArrivalTests.cs` for each new step, plus the pipe command's test.
