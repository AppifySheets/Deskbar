# Recent windows of a chosen app are split last

Date: 2026-11-23. A change to tiling's arrival rules, on top of
`2026-11-19-tiling-predictable-placement-design.md` ("Change 2", a new window splits the tile you last used) and
`2026-11-22-tile-lock-and-other-screens-design.md` (locked tiles, openings and empty screens on any screen).

## Why

Petre, trying the padlock from [Deskbar #434](https://github.com/AppifySheets/Deskbar/pull/434): "i don't like it. i
think what i need is that the vscode windows are not affected, or at least not the current one when oepning a new
browser or other window", "so i don't get interrupted", "do it so that vscode windows are the last to be affected",
then "maybe the last few vscode windows that were active, last 3, let's say".

Today a new window splits the tile you last used. That is usually the VS Code window you were just working in (you
clicked a link in it), so the window you are working in is the one that shrinks. #434 was closed unmerged.

## Goals

- A chosen app's three most recently used windows are the last tiles a new window splits. Older windows of that app
  are split like any other.
- Chosen per app, once, from the bar; kept across restarts.

## Non-goals

- A per-window lock or anything drawn on the windows (#434, dropped). The lock from #403 stays as it is.
- A configurable count. Three, until live use asks for another number.
- Changing what happens to a window dropped on a screen or restored from minimized (they keep their screen).

## Anti-goals

- The window being worked in shrinks when something opens while other tiles could have given the room.
- A new window going where it cannot be seen (unchanged from the lock design).

## Approaches considered

For which tile a window splits:

- Middle way (picked): keep "split the tile last used" (predictable placement, 2026-11-19: a new window lands beside
  the one you were using), except when that tile is protected; then the largest other free tile.
- Big enough, then oldest: tiles at least half as big as the largest count, and of those the one used longest ago is
  split. Petre first asked for it ("one that's not been used most recently", and size "should" count), then, asked
  why the last-used rule existed ("why did we want last window split again?"), chose the middle way: the oldest rule
  brings back the "where did my window go" confusion that the 2026-11-19 change fixed.
- A score, size times time since last use. Dropped: nobody can predict it by looking.

For protecting VS Code:

- A, picked: a per-app setting, "Split VS Code windows last", protecting that app's three most recently used tiles.
  Petre narrowed "all VS Code windows" to "the last 3 that were active".
- B: never split the tile you are in, whatever the app. Dropped: it still halves the other VS Code windows he is
  switching between.
- C: VS Code hard-wired. Dropped: Deskbar is sold, and a setting costs one menu item.

## Behaviour

Arrival order for one new window, from the lock design, with the new tier marked:

1. A minimized tile coming back goes to its old place (unchanged).
2. A window dropped on a screen or restored from minimized stays on that screen (unchanged), and the tiers below
   apply within that screen only.
3. A kept opening, then an empty screen (unchanged).
4. Unchanged rule, new exception: split the tile last used when it passes the quarter floor and is neither protected
   nor locked. A browser opened from a browser still lands beside it.
5. Changed: when the tile last used is protected (or locked, or too small), split the LARGEST free tile on the target
   screen (free: not protected, not locked, not maximized). With no free tile there, the largest free tile on another
   screen (the lock design's redirect, now also for protected tiles). Aimed windows stay on their screen.
6. New: split a protected tile, the least recently used of them first, so the window in use now is split last.
7. Split a locked tile (unchanged: the lock is ignored only when nothing else is left).

Example: you are in VS Code (protected) on screen 1, beside an Explorer; you open a browser from VS Code. Today it
halves VS Code; now it halves the Explorer. With only VS Code windows on screen 1 and a Terminal on screen 2, it halves
the Terminal on screen 2.

"Protected" means: a tile of an app whose setting is on, and among that app's tiles on the board, one of the three
activated most recently (the Tiler's existing activation order, `used`, which already drops activations Windows hands
out by itself). Several apps can have the setting on; each protects its own three.

## The setting

- On the bar's window icon menu, a checkable "Split <app> windows last" (the app's name as the menu shows it
  elsewhere), shown for any window while its workspace is tiled. It sits beside "Don't split this tile".
- Saved in `state.json` as a list of process names (`ProcessName`, e.g. `Code`), compared case-insensitively. Process
  names, not handles, so it survives restarts and cannot attach to an unrelated window.
- Remote twin, `split-last <app> on|off` with the process name, listed in `ControlUsage`.

## Trace

Each arrival already writes a `tile:` line. When protection changes the choice it says so, naming the tiles passed over
("passed over 0x... (Code, used 1st)").

## Left to the builder

- Where the protected set is computed (beside `LockedAmong` in the Tiler is the natural place) and how the tiers feed
  `TileArrival.Choose`'s skip set.
- The README and `docs/features/tiling.md` lines, and manual test steps.
