# Tiling: design

Date: 2026-11-08. Status: draft, waiting on Petre's approval.

## What this is and why

Petre wants a tiling window manager inside Deskbar: on a workspace where he turns it on, windows sit
side by side in a grid with a narrow gap, and the grid rearranges itself as windows open and close.
He used komorebi before and named it as the reference.

Deskbar builds its own instead of running komorebi or another tiler next to it, for three reasons
that came up while deciding:

- komorebi's licence allows personal use only. Use at work needs a paid subscription per person, so
  Deskbar Pro (sold in the Store) could neither ship it nor depend on it.
- komorebi, GlazeWM and Whim all bring their own workspaces and switch between them by hiding or
  cloaking windows. Deskbar is built on Windows' virtual desktops and never hides windows (a crash
  can orphan a hidden window). Two programs deciding where windows belong would fight.
- FancyZones in PowerToys coexists with virtual desktops but only offers zones you drag into: a new
  window does not make the others rearrange, which is the behaviour Petre asked for.

Do not copy komorebi's code. Its licence bars commercial use. The ideas are fine to take; the grid
arithmetic below is described from its behaviour so it can be written fresh.

## Goals

- A workspace can be tiled. Tiling is off by default for every workspace.
- While a workspace is tiled, its windows fill each screen's work area as a grid, with a narrow gap
  between windows and at the screen edges, and corners stay rounded.
- The grid keeps itself tidy without being asked: a new window, a closed window, a minimize or a
  restore re-tiles that screen at once.
- A maximized window covers the others; the tiles stay put underneath, and when it is restored it
  drops back into its slot.

## Non-goals

- A global hotkey. Tiling is turned on from a menu. Deskbar's rule is that each global hotkey earns
  its place (quick find's and the switcher's did); a hotkey can come later if Petre reaches for one.
- Layouts other than the grid. komorebi's spiral (BSP), columns, rows and main-plus-stack were
  shown to Petre; he chose the grid first. The code should not make a second layout hard to add,
  but none is built now.
- Resizing tiles. Grid tiles stay equal, as in komorebi's grid.
- Keyboard focus movement, window stacks or tabs, per-app rule files, its own workspace system.
- Alt-dragging (hold a modifier and drag anywhere in a window to move or resize it). Petre uses
  [AltSnap](https://github.com/RamonUnch/AltSnap) for that (GPL-3.0, free at work too). Building it
  into Deskbar would need a low-level mouse hook, which Deskbar has turned down twice
  (`IInputActivity`, `WorkspaceSwitchGesture`), plus AltSnap's years of edge cases: the Alt-up menu
  bar, VS Code's Alt+Click, mixed-DPI screens, games. Do not copy AltSnap's code; the GPL would come
  with it. Tiling must work with AltSnap instead (see "By hand").

## Anti-goals (it fails even if it works)

- Hiding or cloaking any window.
- Touching windows on a desktop Petre is not standing on in a way that pulls him there. Un-maximizing
  a window on another desktop makes Windows follow it there (see `IScreenLayout.MoveTo`'s
  `mayChangeShowState`).
- A tiled workspace that fights the user: a window that keeps jumping while being dragged, or a
  re-tile loop between Deskbar and an app that insists on its own size.
- Tiling a workspace nobody asked to tile.
- Tiling working in the free tier, or a lapsed trial leaving windows in a broken half-tiled state.

## Behaviour, as Petre decided it

### Turning it on

- An item on the workspace row's menu in the bar (`FloatingBar.WorkspaceMenu`): "Tile windows",
  ticked while on. It is there only while the experiment is on (see below). Clicking it again turns tiling off.
- A control-pipe twin, so it obeys the rule that every menu command has one and `ControlUsage` lists
  it: `tile <workspace>` and `untile <workspace>`, in the "Workspace row menu" section, mirroring
  `fold`/`unfold`.
- My call: the setting lives on the workspace (an init property on `Workspace`, like `Folded`) so it
  survives restarts in `state.json` with no migration; a file without the key loads untiled.
- My call: turning tiling off leaves windows exactly where they are. Nothing remembers or restores
  the pre-tiling positions.

### Experimental, and off by default

Petre: "let's make this an experimental feature and disabled by default, until i have tested it
well". So tiling ships behind a switch that nobody has turned on.

- One switch: a "Tiling (experimental)" checkbox on Manage, stored as `AppState.ExperimentalTiling`,
  false by default and false for every existing `state.json`. Not an environment variable, because
  the Store copy never sees one (CLAUDE.md, `DESKBAR_APPDATA` trap), and Petre tests on the Store copy.
- While it is off: nothing tiles, the LOCATIONCHANGE hook is never installed, the row menu has no
  tiling item at all (no Pro wording either: an untested feature is not advertised), and `tile` on
  the pipe refuses with a message saying to turn it on in Manage. `untile` always works.
- Turning it off stops tiling and leaves every window where it is. Each workspace's `Tiled` flag is
  kept, so turning it back on resumes where it was.
- Tiling acts only when all three hold: the experiment is on, the workspace is tiled, and the copy
  is Pro.
- Graduating it later (default on, or the checkbox removed) is its own small PR, once Petre says so.

### Deskbar Pro

Tiling is a Pro feature (Petre's decision), the first thing gated on `Entitlement.Current.IsPro`.
Pro is the Store package; the GitHub exe is the free tier (`ProEntitlement.Decide`).

- My call: in the free tier the menu item is still there, worded "Tile windows (Deskbar Pro)".
  Clicking it shows a short message saying tiling comes with Deskbar Pro, with a link to the
  [Store listing](https://apps.microsoft.com/detail/9PJX26NGFPNF). A hidden feature sells nothing,
  and a disabled item cannot explain itself.
- My call: on the pipe, `tile` in the free tier changes nothing and fails with a one-line message
  saying tiling needs Deskbar Pro, with a non-zero exit code. `untile` always works, so a free copy
  can always clear the flag.
- My call: when Pro lapses (a trial ends, a refund), the tiled flag stays in `state.json` but
  nothing is tiled any more. Windows stay where they are. If Pro returns, tiling resumes. The
  verdict is re-read on the update timer, so this happens within minutes, not at restart.
- The gate is checked where tiling decides to act, through one question in Core that takes the
  verdict as input, so it is unit-tested like `ProEntitlement`.

### The grid (komorebi's grid, read from its source)

- Columns = the square root of the window count, rounded up: 1 window gets 1 column, 2 to 4 get 2,
  5 to 9 get 3, and so on.
- Columns fill left to right. Each column takes `floor(remaining windows / remaining columns)`
  windows, so earlier columns hold fewer and their windows are taller. Three windows: one
  full-height window in the left column, two stacked on the right. Five: 1, 2, 2.
- Columns are equal width; windows within a column are equal height. Integer remainders go to the
  last column and the last row, so the grid covers the area exactly.
- Gap: one setting, used both between windows and between windows and the screen edge. Default
  8 pixels at 100% scaling. My call: it scales with each monitor's DPI so it looks the same on every
  screen, and it is set on Manage.

```
3 windows            5 windows
+-----+-----+        +----+----+----+
|     |  2  |        |    | 2  | 4  |
|  1  +-----+        | 1  +----+----+
|     |  3  |        |    | 3  | 5  |
+-----+-----+        +----+----+----+
```

### Which windows tile, and in what order

- Each screen tiles separately, inside its work area, so the taskbar and a docked Deskbar
  (`AppBar`) are respected. My call: the floating, undocked bar is treated like any other topmost
  window and does not shrink the area.
- A window belongs to the screen `MonitorFromWindow` says it is on.
- Windows that never tile, and float above the grid: dialogs and owned windows, tool windows,
  windows that cannot be resized (no thick frame), Deskbar's own windows, and anything Deskbar
  cannot move (an elevated window refuses `SetWindowPos` from a non-elevated process). Decided by
  the window's own styles and by trying, not by a rule file.
- Only windows that belong to the workspace tile (Petre agreed). A window that is only visiting
  floats on top of the grid: an anchor's windows pinned onto a group member while you stand in it
  (`WorkspaceManager`'s `borrowed`), and any window pinned to every desktop. The test is "lives
  here", the same one `WorkspaceOf` answers, not "is on this desktop right now". Otherwise the grid
  would move windows that belong elsewhere, and two tiled members of one group would reshuffle the
  same anchor windows on every switch between them.
- There is no per-window "keep this out of the grid" control in this version (Petre agreed). The
  floating rules above decide. A "Float this window" item can come after live use shows what the
  rules miss.
- Minimized windows leave the grid; the rest close the gap. Restored, a window rejoins at the end.
- Order: the slot order is the order windows joined the grid, a new window going to the end. My
  call for the first fill when tiling is turned on: the current left-to-right, top-to-bottom order
  of the windows' positions, so the first tidy-up moves things as little as possible.

### Keeping it tidy

- Re-tile a screen when one of its tiled windows appears, disappears, minimizes, restores,
  maximizes, un-maximizes, or moves to or from it.
- `WindowMonitor` covers only part of that today. It raises Appeared, Disappeared, Hidden and
  MinimizeChanged, but `Moved` comes only from `EVENT_SYSTEM_MOVESIZEEND` (a mouse drag or resize),
  and there is no maximize event at all. Keyboard snaps (Win+Arrow), Win+Shift+Arrow to another
  screen, an app resizing itself and un-maximizing all go unseen.
- My call: add an `EVENT_OBJECT_LOCATIONCHANGE` hook, and keep it cheap. It is installed only while
  the desktop being looked at is tiled and removed when it is not. The callback drops everything
  that is not `OBJID_WINDOW` on a window currently in a grid, before any other work, because this
  event fires constantly for every window on the machine and the callback runs on the UI thread (the
  existing hook comments in `WindowMonitor` explain why that matters). Changes are debounced per
  screen (around 150 ms, measured and tuned live) and compared with the slot the window should be in.
  The 5 s sweep is not fast enough: a window un-maximized into a blank space for five seconds looks
  broken.
- A keyboard snap or an app's own resize is treated like a mouse drop. If the window is still on the
  same screen it goes back into its slot; if it is now on another screen it joins that grid.
- Deskbar's own `SetWindowPos` calls do raise LOCATIONCHANGE, so the self-move guard below is needed:
  a change that leaves a window in its assigned slot is not a reason to re-tile.
- Only the desktop being looked at is tiled. When Petre arrives at a tiled workspace, it re-tiles
  once, which picks up anything that changed while he was away.
- A maximized window is skipped by the grid and left maximized; its slot is kept for it, so the
  tiles beneath do not reflow. When it is restored, it goes back into its slot.

### By hand

One rule covers every way a window can be moved: a title-bar drag, an AltSnap Alt-drag, a keyboard
snap, or another tool. When a tiled window settles after a move that Deskbar did not make:

- If the centre of its visible frame is over another tile on the same screen, the two swap slots.
- If it is on the same screen but not over another tile (its own slot, a gap, an edge), it goes back
  into its slot.
- If it is now on another screen, it joins that screen's grid at the end, and both screens re-tile.
- Resizing a tiled window, by an edge or by AltSnap's right-drag, also sends it back to its slot.

Detection: a title-bar drag is framed by `MoveStarted` and `Moved` (`EVENT_SYSTEM_MOVESIZEEND`), so
it settles at the end of the drag. AltSnap moves windows with `SetWindowPos` and raises neither, so
its drags arrive only as LOCATIONCHANGE; "settled" there means no further change for the debounce
interval. Do not act while the left or right mouse button is still down (`GetAsyncKeyState`), or a
slow AltSnap drag that pauses would be swapped or snapped back mid-drag. Using the window's centre
rather than the cursor is what makes this work for moves that have no cursor (keyboard snaps).

### Rounded corners

Windows 11 rounds most top-level windows already. For a tiled window that has opted out, ask DWM for
rounding (`DWMWA_WINDOW_CORNER_PREFERENCE`, `DWMWCP_ROUND`). Only windows Deskbar tiles are touched.
My call: the preference is left in place when tiling turns off, since resetting it to "default" may
not be what the window had and nothing visible depends on it.

## Constraints and known traps

- Positions are physical pixels, as everywhere else in Deskbar (`ScreenLayout`, `MonitorMove`).
- Windows draw invisible resize borders, so `GetWindowRect` is larger than what you see. Set each
  window so its visible frame (`DWMWA_EXTENDED_FRAME_BOUNDS`) lands on the slot, or the gaps come out
  uneven. Measure the difference per window; it is not a constant.
- Some apps enforce a minimum size larger than their slot. Accept what the window ends up as, do not
  retry in a loop, and record it in the trace.
- `ShowWindowAsync` is asynchronous and a minimized window's rectangle is nonsense (CLAUDE.md
  traps, `ScreenLayout.MoveTo`). Reuse `IScreenLayout.MoveTo` or its rules rather than a second
  mover.
- Deskbar's own moves raise LOCATIONCHANGE. They must not trigger another re-tile: comparing the
  window's visible frame against the slot it was just given is the guard, with a short per-window
  quiet period as a backstop.
- A re-tile keeps z-order: tiles are placed with `SWP_NOZORDER | SWP_NOACTIVATE`, so they never rise
  over a maximized window or take focus.
- A drop on one of the bar's workspace rows is the bar's gesture (move to that workspace) and wins
  over a tile swap.
- Re-tile every screen of the current tiled desktop when the work area or the monitor set changes
  (taskbar moved, Deskbar docked or undocked, a screen plugged in).
- The grid arithmetic and the slot bookkeeping (join, leave, swap, which screen) go in
  `Deskbar.Core` as pure code with unit tests. Only window styles, DWM calls and moving live in
  `Deskbar.Windows`.
- Trace: tiling decisions write to the existing trace log behind `trace.on`, writing only when the
  layout changes, like the other traces.

## Left to the builder

- Naming of types and where the tiling coordinator lives (next to `WorkspaceManager` or beside it).
- The exact menu wording, glyph and position in the workspace row menu.
- How the swap target is found (tile rectangles in Core, cursor position from the drag).
- How long the self-move guard lasts.
