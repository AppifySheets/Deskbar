# The tasks panel: the tasks window docked beside the bar

Date: 2026-11-23. Task 8f70d8ea. Mockup: [Deskbar Tasks Panel](https://claude.ai/artifact/LCdRn4XfGmfk7WSKRCTjde)
(approved by Petre, "all good").

## Why

Petre works from the task list all day, and more and more of those tasks are Claude sessions running in the
background ([Deskbar #443](https://github.com/AppifySheets/Deskbar/pull/443)). He wants to see every task Claude
is working on and jump into any of them. The bar shows only his own tasks and only a few lines. The tasks window
shows everything, but it is a floating window that lands on whichever workspace it was opened on, sits over his
work and "keeps jumping into workspaces". His words:

> it should come up next to the bar and push other windows away from deskbar; for full height, rather than the
> separate window that keeps jumping into workspaces-tasks should be opened and should stick with the deskbar in
> all sessions

> it effectively becomes an extension of deskbar

> all tasks window should have a filter, by workspaces, by titles, etc.

## Goals

- The tasks window becomes a panel docked beside the bar: same content, no title bar, full height of the screen,
  registered with the shell as an app bar, so maximized windows, Snap and new windows keep clear of it.
- It is on every virtual desktop, like the bar. Switching workspaces never moves it or leaves it behind.
- It stays with the bar: same screen, same edge, just inside the bar. If the bar moves, the panel follows.
- It is opened and closed from the bar's "All tasks" / "+N more" and from its own close button, and whether it is
  open survives restarts of Deskbar.
- A search box filters tasks by text, and a "Claude working" filter keeps only tasks with a live Claude session.

## Non-goals

- No floating mode. The floating tasks window goes away; the panel replaces it (Petre: "rather than the separate
  window").
- No change to what a line's clicks do, the thread, Add, drag to reorder, or the existing status, workspace, goal
  and assignee filters. They move into the panel as they are. What a line SHOWS changes only below 620 DIP, and
  the Matrix only shows from 780 DIP (both below, Petre's picks).
- No change to the bar's own task list.

## Anti-goals

- The bar must never end up inside the panel (the panel against the screen edge, the bar between it and the
  windows). Petre sees the bar as the anchor; the panel is its extension.
- The panel must never leave a reserved empty band on a screen after it closes or after Deskbar exits.
- Opening the panel must not take Petre to another desktop (the window's old "Activate follows it there" trap,
  TasksBoard.Open's comment). A window pinned to every desktop does not have this problem, which is part of why
  pinning is right.

## Approaches considered

1. **The tasks window becomes a second app bar on the bar's edge (chosen by Petre).** The window keeps its
   content and code; it loses its title bar, is pinned to every desktop, and registers its own app bar after the
   bar's, so the shell stacks it just inside the bar. Cost: two app bars to keep in order (see "Keeping the bar
   outside").
2. **The bar grows a column.** One window, one app bar that gets wider. Rejected: the panel would inherit the bar's
   idle fade (BarIdleOpacity, 0.8 on Petre's machine) and frost, which are wrong for something read and typed into,
   and every layout path of an 8,300-line FloatingBar (seam dock, minimize, scale) would have to know a second
   column.
3. **An overlay that slides out over windows.** Rejected: Petre asked for windows to be pushed away.

## Measured facts this rests on

- Petre's bar is docked in every layout saved in state.json (`DockBar: true`; `DockedEdge` "left" or "right" in
  every `BarPlacements` entry; "right" today). `DockAcrossScreens` is false.
- AppBar.cs's measurements: QUERYPOS stacks a new app bar beside one already on the edge; a killed process's
  strip is given back by Explorer within a second; the shell works in physical pixels.
- The shell orders app bars on one edge by when they registered: the earlier one sits nearer the edge. This is
  the basis for "the bar registers first". It is to be confirmed in the probe below before code relies on it.

## The design

### The panel window

TasksWindow keeps its XAML content (toolbar, add row, problem line, list or Matrix) and its code-behind. What
changes:

- `WindowStyle="None"`, `ResizeMode="NoResize"`, `ShowInTaskbar="False"`, no Alt+Tab entry (WS_EX_TOOLWINDOW),
  topmost like the bar. The window's `MinWidth` (the XAML says 560; the code's real floor is
  `TasksWindow.Matrix.cs`'s `ListMinWidth = 620`) goes, along with `GrowForMatrix` and its give-back logic. The
  panel's own minimum is 360 DIP.
- Its toolbar gains a close button (×) at the right end, after "+ Add".
- A drag grip on its inner edge (the edge facing the windows, away from the bar) sets the width. As with the bar,
  the reservation follows when the drag ends, not during it.
- It is pinned to every virtual desktop after its first show, the way the bar and the detached stats window are
  (App.xaml.cs, `desktops.Pin`). Once pinned, TasksBoard's bring-forward no longer moves it between desktops; the
  "Arrive" seam reduces to raise and activate.
- It is kept out of tiling. `TasksBoard.KeptFromTiling` currently lets the tasks window tile ("maybe even tile
  it"); a docked panel is not a tile, so that exception goes and the panel is an own window like the bar.
- The toolbar's chips already wrap (WrapPanel), so the toolbar fits at 360 DIP as it is.

### Lines at panel width (Petre's pick: "slim line when narrow")

Today's line (`TasksWindow.xaml.cs`, `LineColumns`) has 470 DIP of fixed columns: tick 22, toggles 38, sessions
116 ("2 sessions, 1 live"), assignee 46, age 44, chip 88, comments 40, action 52 (Close or Reopen), "⋯" 24. That
is wider than the panel's 460 default, so:

- **Below 620 DIP** the panel draws a slim line: tick, title (the star column, trimming with an ellipsis), the two
  toggles, the sessions as squares only (one per session, blue while live, glowing while busy, no words), the
  workspace chip, the comment count, "⋯". The age and the assignee move into the line's tooltip; Close and Reopen
  are reached through "⋯", which already offers them. The fixed columns come to roughly 240 DIP, so the title
  keeps about 200 DIP at 460 and about 100 at 360 with the scroll bar showing. Exact widths are the builder's.
- **At 620 DIP and wider** the line is today's, unchanged.
- The switch happens on resize, at the drag's end like the reservation, and redraws every line once.

### The Matrix (Petre's pick: "only when wide")

The Matrix needs `MatrixMinWidth = 780` DIP. In the panel it never widens anything:

- The List/Matrix switch shows in the toolbar only while the panel is 780 DIP or wider.
- Narrower, the panel shows the List, which already groups tasks under the quadrants I to IV.
- `AppState.TasksMatrix` keeps Petre's choice. A panel narrowed below 780 while on the Matrix shows the List but
  leaves the setting as it is, so widening it again brings the Matrix back, and the panel reopens on start in
  whichever view the setting and its width allow.

### Where it docks

- The panel docks on the bar's edge of the bar's screen, full height as the shell grants it, `Width` = the
  remembered width (default 460 DIP), converted to physical pixels with that monitor's DPI as DockTo does.
- Bar docked left or right: the panel is a second `AppBar` on the same edge, registered after the bar's.
- Bar across the seam (DockAcrossScreens): the panel docks on the right-hand screen's LEFT edge, inside the bar's
  right half. Builder's call how to express it; the rule is "the right-hand screen, inside the bar".
- Bar floating (DockBar off): the panel still docks, on the edge of the bar's screen nearest the bar, as its only
  app bar on that edge. It cannot sit "beside" a floating bar, and pushing windows away is the point. My call:
  Petre never floats it today, and this keeps the panel useful if he does.
- Bar minimized: the panel closes and gives its strip back, and reopens when the bar is restored if it was open.
- Full-screen app: the panel steps out of the topmost band and back, through the same AppBar `onFullScreen`
  callback the bar uses.

### Keeping the bar outside

The shell places a newer registration inside an older one. So every time the bar's app bar registers again, the
panel's must register again after it. Three paths re-register the bar today, and each must be followed by the
panel removing and re-docking its own app bar:

1. `FloatingBar.KeepDockedStrip` (the 1 s guard) calls `appBar.Remove()` then `DockTo`.
2. TaskbarCreated (Explorer restarted): `AppBar.Hook` clears `Registered`, and the next dock registers anew. The
   panel must not answer TaskbarCreated by itself; it waits for the bar's re-dock and docks after it.
3. Docking after undock, minimize/restore, a display change, or a switch between edge and seam.

Proposed shape: FloatingBar raises one notification (an Rx `IObservable<Unit>` or an event the App wires) after
each successful dock or re-dock of its own app bar, with the bar's screen and edge. The panel's controller answers
with `Remove` then `Dock`. A plain position change notice (ABN_POSCHANGED, `onPositionChanged`) does not
re-register and only re-asks QUERYPOS/SETPOS, as the bar does.

The panel also needs a guard like `KeepDockedStrip`: once a second, if its screen's work area reaches under the
panel's strip, it re-registers, with the same SettleFor and RetryEvery clocks, and traced.

Probe before code (on Petre's screen is not allowed: it shrinks his work area): in Windows Sandbox or a VM, or
with Petre's agreement for a minute, register A then B on the right edge, then remove and re-register A, and
record which ends up nearer the edge. If the shell keeps A outside even after it re-registers, path 1 and 3 are
harmless and only the TaskbarCreated order matters; the design above stays correct either way.

### Opening and closing

- "+N more" / "All tasks" on the bar toggles the panel (it opens the window today). Its "on" look while the panel
  is open is the builder's call; the mockup shows it highlighted.
- The × in the toolbar closes it. `tasks-window show|hide` on the pipe opens and closes it as now.
- Closing removes the app bar (ABM_REMOVE) and hides the window; it never closes the WPF window (CloseForGood's
  note still holds).
- `AppState.TasksWindow` (a floating rectangle) is replaced by a panel state: whether it is open and its width in
  DIP. On start, a panel left open opens again once the bar has docked. An old `TasksWindow` value is ignored.
- Opening keeps today's rule: the board, not a text box, has the keyboard; filters reset on a fresh open.

### The new filters

- **Search.** A text box under the title line, full width. It narrows the list and the Matrix as Petre types,
  case-insensitive, matching any word he types (all words must match) against the title, the status line and the
  open question. Ctrl+F in the panel focuses it; Esc clears it and gives the keyboard back to the board. It is
  applied with the other filters before grouping and counting, so the chips' counts agree with what is shown
  (the same rule TaskGoalFilter follows). It is not persisted.
- **Claude working.** A chip, orange-edged, with a count: only tasks with at least one LIVE session (the session
  squares the line already draws in blue). It combines with the other filters. It is reset on a fresh open like
  the others.
- The existing status, workspace, goal and assignee filters stay as they are. The panel shows every assignee by
  default, as the window does today (the bar keeps showing only Petre's).

### Instrumentation

Behind the existing trace switch (`trace.on`): one line per panel dock, re-dock, removal and guard repair, with the
granted rectangle and the bar's, written when it changes, like the screen strips' lines. Two app bars in a fixed
order is exactly the kind of state that needs a measurement when it goes wrong.

## Left to the builder

- How the panel's controller is split between TasksBoard, TasksWindow and a new class; how the notification from
  the bar is wired. One constraint on it: it fires only when the bar's app bar REGISTERED (ABM_NEW), not on a
  plain re-ask after a position change, or the bar's position-changed re-dock and the panel's remove-and-redock
  would answer each other forever.
- What Esc does outside the search box (nothing new is required).
- The panel's state record's name and shape.
- The "on" look of the bar's All tasks line.
- Tests: the pure parts (search matching, filter combination and counts, the order rule "panel re-docks after any
  bar registration") in Deskbar.Core.Tests; the window parts on the hidden desktop
  (`pwsh scripts/test-hidden.ps1`), never on Petre's desktop, and no test that registers a real app bar on his
  screens.

## Docs

README's tasks section and the manual test script get the panel, the search box and the Claude working filter.
The README screencast of the tasks window needs re-filming.
