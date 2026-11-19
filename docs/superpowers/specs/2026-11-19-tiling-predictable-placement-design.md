# Tiling: predictable placement on open and close

Date: 2026-11-19. Builds on `2026-11-08-tiling-design.md` and `2026-11-08-tile-resize-design.md`, and on the
drop zones and openings that came after them (`TileArrange`).

## Why

Petre, using tiling day to day: "when opening new windows, it's sometimes confusing where they appear. also, on
closing, i sometimes want not to move other windows."

Both come from one cause. With nothing dragged, a screen's grid shape is worked out from its window count
(`GridLayout.Shape`), and slots fill column by column. So any change in the count can reshape the whole screen:

```
before (2x2)        close D, today       close D, after change 1
+---+---+           +---+---+            +---+---+
| A | C |           |   | B |            | A |   |
+---+---+    ->     | A +---+     vs     +---+ C |
| B | D |           |   | C |            | B |   |
+---+---+           +---+---+            +---+---+
```

B never moved, yet it changes column. Opening a window does the same in reverse, and the new window lands at the
end of whichever screen the app chose to open on, which has nothing to do with where the user was working.

Other tiling window managers were compared (i3, Sway, bspwm, Hyprland, Pop!_OS, yabai, dwm, xmonad, niri,
FancyZones). The ones people find predictable share two rules: a new window lands next to the window you were
using, and a closing window gives its room back to its neighbours only, or to nobody. The "reshape from the count"
family (dwm, xmonad) is the one Deskbar matches today, and is the one this changes away from.

Petre picked all three changes below.

## Goals

1. Closing a tile never moves a tile in another column, and never changes the order of the tiles that stay.
2. A new window lands next to the tile you were last using, and takes room only from that tile.
3. Per workspace, a closing tile can leave its space empty so that nothing else moves at all, and the next window
   goes into that space.

## Non-goals

- A binary-tree layout (bspwm style) where only the one sibling grows back. It is the most predictable model, but
  the sizes are stored as columns of rows (`TileSizes`), and drags, split moves, drop zones and openings are built
  on that. Changes 1 and 2 get most of the benefit on the current model.
- Changing what happens on minimize and restore. A minimized tile still gives its slot up and comes back to its
  place (`Tiler.Minimize.cs`).
- Changing drops, swaps, keyboard snaps or split moves.
- A preselect marker (bspwm's shaded "next window goes here" box). Possible later; not in this work.

## Anti-goals

- A window jumping to a screen the user cannot see, or away from where they are working, with no clear reason.
- Holes that pile up with no obvious way to close them.
- Any new behaviour that runs on a timer. Petre tests live and a delay that sometimes fires reads as a bug.

## Approaches considered

- **Three layered changes on the current columns-of-rows model** (chosen). Sticky shapes, split the last-used
  tile, optional kept space. Each reuses code that already exists (`TileArrange.RemoveCell`, `TileArrange.Drop`,
  openings), and each can be tested in the pure classes.
- **Binary tree, bspwm style.** Rejected for now, see non-goals: it replaces the storage every other tiling
  feature reads.
- **Keep the count-based grid, add only the kept space.** Rejected: it fixes "close moves others" only when the
  option is on, and does nothing for where new windows land.

## Change 1: the shape stays when a window leaves

A window that leaves a screen's grid (it closed, was hidden, went to another workspace, or was minimized) takes
only its own cell out, the way a custom shape already does (`TileArrange.Refit`, the `RemoveCell` path):

- If its column has other rows, they share its height in proportion. Nothing outside that column changes.
- If it was alone in its column, the column goes and the remaining columns share its width in proportion. Their
  order and their rows do not change.

Today this happens only when the stored sizes are a custom shape. It has to happen for the automatic shape too,
including a screen with nothing stored at all (which `SizesOn` treats as `TileSizes.Equal(GridLayout.Shape(count))`).
In that case the starting point is the equal grid for the count before the leave, and the leaver's cell is
removed from it.

The result is a custom shape and is stored like any other (it is not plain, so `SetTileSizes` keeps it). "Even out
tiles" stays the way back to the automatic grid; it will now show in the menu after the first close on a screen.
That is expected.

This holds for every way a window leaves a screen, including a tile dragged to another screen and a tile dropped
on one of the bar's rows: the screen it left keeps its shape either way. "Changing drops" in the non-goals means
the drop rules (where the dragged window goes), not the screen it left. (Builder check, my call: one rule for every
leave is simpler than passing the reason down to `SizesOn`, and it is the goal stated plainly.)

Only one leaver is known per change (`NoteLeavers` keeps the first). When several windows leave at once, the first
removes its own cell and the rest fall back to today's rule. That is acceptable: it happens on screen changes and
on arrival at a workspace, not under the user's hand.

## Change 2: a new window splits the tile you were last using

### Which tile

The Tiler keeps every window in the order it was last activated (the `Activated` window event, which
`OnWindowEvent` already sees). The target for a new window is the most recently activated window that is on the
current workspace's board, is not the new window itself and is not maximized. Activations are recorded for every
window and filtered when the target is picked, because a new window usually activates itself BEFORE it joins the
board: once it has joined, it is the last-used tile like any other, and the next window splits it (until the
floor below sends that one to the largest tile).

Activations Windows hands out by itself do not count. When a tile closes or is minimized, Windows activates
whatever is next in z-order, which can be a tile on another screen the user never touched. So an `Activated`
event that arrives with no user input since a tile last left the grid is ignored (the surface exposes the last
input time, `GetLastInputInfo`; no timer is involved). Activating a window that is not a tile (floating, pinned,
borrowed) leaves the target as it was. (Builder check, my calls.)

If there is no such tile (nothing activated yet this session, or every activated tile has gone), the new window
goes to its own screen and the fallback below picks the tile it splits.

### How it splits

The target tile gives up half of its own room, and no other tile changes:

- The target is wider than it is tall **and** alone in its column: a new column to its right. The column's width
  is halved. (`TileArrange.Drop` with `Zone.Right`, which halves the column.)
- Otherwise: a new row directly below it in its column. The target's row height is halved.
  (`TileArrange.Drop` with `Zone.Bottom`.)

The second rule covers the case of a wide tile in a column of several rows. Splitting it sideways would mean
halving the whole column and narrowing its neighbours too, which breaks "takes room only from that tile". Stacking
below keeps the promise.

The new window goes onto the target's screen, even when the app opened it on another one.

### Order of precedence for a window that arrives

1. A minimized tile coming back goes to its old place (unchanged, `ReturnedToTheirPlaces`).
2. An opening: the window goes into it (`IntoOpenings`). The target tile's screen is looked at first, then the
   screen the window opened on. A kept space from change 3 is an opening, so the next window fills it. A split
   never throws an opening away: `TileArrange.Drop` closes a screen's opening before it rearranges, and reaching
   step 3 means the screen being split has none. (Builder check, my call.)
3. Otherwise, it splits the last-used tile (this change).
4. Otherwise, the fallback split below. Nothing appends to the end of a screen any more: on a sticky shape that
   piles every joiner into the bottom of the last column, and on a screen with nothing stored it rebuilds the
   whole grid, which is what this work sets out to stop.

### When the last-used tile is too small, and the fallback

The builder check found that nothing stops one tile being halved over and over: a new window becomes the last-used
tile, a tile in a column of several rows can only stack below, so four windows opened in a row on one 1920x1080
tile give a 960-wide column of rows 540, 270 and 135 high. Petre left the rule to me ("you decide"); the
alternative, Hyprland's "always split the last-used tile, however small", was weighed and dropped because the
spiral it makes is its own kind of unpredictable.

The rule: a split must leave both halves at least a quarter of the screen's work area wide and a quarter of it
high. When splitting the last-used tile would go below that, the new window splits the LARGEST tile on that
screen instead (by area; on a tie, the one latest in slot order), with the same right-or-below rule. The same
"largest tile" rule is the fallback when there is no last-used tile, and is what the second and later windows of
a multi-window arrival do. When even the largest tile is below the floor, the window still splits it: the screen
is full, and the zero-size guard in `RetileScreen` remains the hard stop.

### Which arrivals split

- A window that appeared on the workspace, or became tileable there (the `Evaluate` path).
- A window brought to the workspace from another one (`OnWindowMoved`, a bar drop on a row or the pipe's `move`).

Not:

- A window dragged by hand onto another screen or tile. The user pointed at a place; the drop rules decide it.
- Windows re-homed because their screen went away.
- The first fill when tiling starts on a workspace (`TileBoard.FirstFill`), which orders by position.

When several windows arrive in one evaluate (arriving at a workspace after windows opened there while away), the
first splits the target (or fills the opening) and each of the rest splits the largest tile on its screen, as in
the fallback above.

## Change 3: keep the space when a window closes (per workspace)

A per-workspace switch, off by default. When it is on and a tile **closes** (its window `Disappeared`, or was
`Hidden`, which is how many apps close to the tray), its cell is not removed: it becomes the screen's opening
(`TileSizes.Opening`). No other tile moves. The next window to arrive on that screen goes into it (precedence
step 2 above).

- Minimizing is not a close. It keeps today's behaviour (change 1 applies).
- Moving a window to another workspace is not a close either. Change 1 applies.
- A screen has at most one opening. If a second tile closes while the screen already has one, the second close
  removes its cell as in change 1, and the first opening stays. `TileArrange.Refit` already has this case (the
  `count + 2` branch).
- The last tile on a screen closing leaves nothing to keep.
- The space goes away as openings already do: pull a neighbour's edge back over it (`TileArrange.Closes`), drag a
  tile into it, or "Even out tiles". No timer closes it.

### Where the switch lives

- The workspace row's menu, directly under "Tile windows" (and "Even out tiles" when that shows): a checkable
  "Keep the space when a window closes", shown only while the workspace is tiled.
- The header tile button's right-click menu, the same item, for the workspace you are on.
- Stored on `Workspace` as an init bool defaulting to false, so older `state.json` files load with it off. Not
  cleared by untiling, so turning tiling back on keeps the choice.
- A control-pipe twin, as every menu command has one (`ControlUsage`): `keep-space <workspace> on|off`.

## Constraints

- The Tiler acts only where `TilingGate.Acts` says so and only on the desktop being looked at. Nothing here
  changes that.
- Every board change goes through `Keep`, and leaver cells are noted there (`NoteLeavers`). Change 1 and change 3
  both depend on that cell being right.
- Stored sizes are written through `StoreSizes` so the Tiler's own store does not cause a full evaluate.
- The pure classes (`TileArrange`, `TileSizes`, `GridLayout`, `TileBoard`) hold the arithmetic and carry the
  tests. The Tiler decides when.
- Trace lines: one per decision, naming which rule placed the window (opening, split of which tile and which
  side, fallback to the end, space kept). The trace is how live reports get settled.

## Left to the builder

- Where the most-recently-activated list lives (a field on the Tiler, per workspace) and how it is pruned when
  windows leave.
- How change 1 is expressed: a change in `TileArrange.Refit`, in `SizesOn`'s nothing-stored branch, or both.
- How a minimized tile that comes back to a screen that changed meanwhile places its cell. Today it goes back to
  its old index in the order and the sizes refit "as for any join". With shapes now sticky, the cell should go
  where the index puts it, not to the bottom of the last column. Pick the simplest rule that keeps the other
  tiles where they are.
- The exact menu wording, within the meaning above.
- Tests: pure-class tests for each rule, plus `TilerTests` cases for precedence, the activation list, the
  close-versus-minimize distinction and the one-opening limit.
