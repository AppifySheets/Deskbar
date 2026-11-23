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

- Big enough, then oldest (picked): tiles at least half as big as the largest count, and of those the one used
  longest ago is split.
- A score, size times time since last use. Dropped: it balances finely, but nobody can predict it by looking.

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
4. Changed: split a tile that is neither protected nor locked, chosen by SIZE, then AGE. Petre: "one that's not been
   used most recently", and on size, "it should" count; he picked "big enough, then oldest" over a size-times-age
   score, which could not be predicted by looking.
   - The candidates are those tiles on the target screen (the screen of the tile last used, else the window's own
     screen) whose split passes the quarter floor.
   - Big enough: a candidate whose area is at least half the area of the largest candidate.
   - Of the big-enough ones, the tile used longest ago is split (the Tiler's activation order, `used`; a tile never
     activated counts as older than any that was). Ties by area, larger first.
   - No candidate on the target screen: the same rule over every other screen's tiles together.
   This replaces "split the tile last used" for a window free to move. The new window no longer lands beside the
   window it was opened from; that is the price of not being interrupted.
5. New: split a protected tile, the least recently used of them first, so the window in use now is split last.
6. Split a locked tile (unchanged: the lock is ignored only when nothing else is left).

Example: a full-height browser, a half-height Explorer and a quarter-size Terminal on one screen, none protected. The
browser is the largest, the Explorer is at least half its size and the Terminal is not; of the browser and the
Explorer, whichever was used longer ago is split.

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
