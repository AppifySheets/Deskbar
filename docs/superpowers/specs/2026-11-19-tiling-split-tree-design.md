# Tiling: a split tree instead of columns of rows

Status: design, 2026-11-19. Builds on `2026-11-08-tiling-design.md`, `2026-11-08-tile-resize-design.md` and
`2026-11-19-tiling-predictable-placement-design.md`, which stay the record of everything this does not change.

## Why

Petre, testing tiling live with three windows on one screen, two stacked on the left and one on the right:

> when there are three windows, one on top of another and another on the right, the one on the right will not
> become a side of either of the left windows, making the other larger. it needs to be fitted with either window,
> so that it gives way to the other windows to grow

Dropping the right window (C) on the right quarter of the top-left tile (A) should give A and C side by side in the
top half and the bottom-left tile (B) the full width below. Dropping it beside B should do the mirror image.

That layout cannot be stored today. `TileSizes` is columns of rows: `Columns[c]` is a column's share of the width
and `Rows[c][r]` a row's share of that column's height. Every layout is a set of full-height columns. A drop on a
tile's left or right quarter (`TileArrange.Drop`, `Zone.Left`/`Zone.Right`) always calls `InsertColumn`, so C is
lifted out of its column and put back as a full-height column beside A's column: nothing visibly changes.

## Goals

- A drop on any edge quarter of any tile puts the dragged window beside THAT tile only, at any depth of nesting.
  The tile gives up half of its own size; no other tile changes size.
- A window that leaves a split hands its space to its neighbours in that split, so they grow into it ("gives way to
  the other windows to grow"). A split left with one child disappears and that child takes the whole area.
- A full-height column or full-width row can still be made beside anything, by dropping on a band along the
  screen's edge (Petre picked this over two depths inside the quarter, and over losing the gesture).
- Everything tiling does today keeps working on the new model: split dragging, kept sizes, openings, keep-space,
  arrival splitting the last-used tile, minimize and return, cross-screen drags, swap in the middle, "Even out
  tiles", the pipe's `tile`/`untile`/`even`/`keep-space`, the preview overlay.
- A layout stored by today's build loads and lays out pixel for pixel as it does now.

## Non-goals

- No new hotkeys or menu items. The gesture is the drag, as it is today.
- No tabbed or stacked (monocle) tiles.
- No change to which windows tile, the gate, the desktop rules or the put-back backstop in `Tiler.cs`.

## Anti-goals

- A drop or a close that moves a tile the user did not touch, beyond the neighbours that share the split
  (they grow or shrink, they do not change place).
- An existing user's stored layout changing on upgrade.
- A tree that grows useless levels (a split with one child, or a split inside a split of the same direction).
  Those make split dragging behave differently from what the screen shows.

## Approaches

1. Split tree (picked). A screen's layout is a tree: each inner node splits its area across (side by side) or down
   (stacked) into children with fractions; each leaf is one window or the opening. Any drop at any depth is
   representable, and closing hands space to the split's other children. Cost: the model under `TileSizes`,
   `GridLayout`, `SplitMove`, `TileArrange`, `TileArrival` and the Tiler's resize and minimize paths is rewritten,
   with a migration for stored sizes.
2. Rows or columns. Keep a two-level grid that can be either columns of rows or rows of columns. Fixes the
   three-window case Petre hit, cheaply, but a drop beside a stacked tile still fails once two stacked columns
   exist. Rejected by Petre for the tree.
3. Leave the model and refuse the drop. Not a fix.

## The model

A screen's layout is a `TileTree` (name left to the builder; it replaces `TileSizes` as what is stored per screen,
in `Workspace.TileSizes`/`ScreenTileSizes`):

    Node  = Leaf | Split
    Leaf  = one cell; the opening is a leaf marked empty (at most one per screen, as today)
    Split = Direction (Across: children side by side, left to right; Down: stacked, top to bottom)
            + children, each with a fraction of the split's span; fractions sum to 1

Normal form, enforced by every operation (anti-goal above):

- A split has at least two children. A split left with one child is replaced by that child, which takes the
  split's whole area.
- A split's child is never a split of the same direction; it is merged into its parent, its fractions scaled by
  its own share. Three columns are one Across split of three, exactly as today's `Columns` list.

Slot order is the leaves in depth-first order, children left to right or top to bottom. For a tree that is an
Across split of Down splits (today's grid), that is column by column, top to bottom, which is today's slot order,
so the board (`TileBoard`), the order, the opening's slot and every caller that maps a window to a slot keep their
meaning.

Layout (replacing `GridLayout.Cells`): shrink the area by the outer gap (`GridLayout.InnerArea`), then divide each
split's span by its fractions with today's `Edges` arithmetic (even fractions use floor(span / n) per entry,
uneven ones round the running total, the remainder lands in the last entry), then inset each leaf on its inner
edges with `HalfGaps`. An inner edge is any edge that is not on the inner area's border. For a migrated
columns-of-rows tree this is the same arithmetic in the same order, so the result is pixel-identical; a test
asserts it for a spread of today's shapes and sizes.

Nothing stored means the automatic layout, as today: `GridLayout.Shape(count)` turned into a tree (Across of Down
columns, single-row columns collapsing to a leaf, one column collapsing to a Down split). A tree that equals the
automatic one with even fractions and no opening is plain and is not stored (`TileArrange.IsPlain`).

Each leaf has a share of the screen on each axis (the product of the fractions of the splits of that direction on
its path). `TileArrival`'s floor and "largest tile" rules read those shares where they read `Columns[c]` and
`Rows[c][r]` today.

## Drops

A drop is read in this order.

1. Screen-edge bands. A band along each edge of the screen's inner area, 6% of the inner width (left and right
   bands) or height (top and bottom bands) deep, wins over any tile under it. The dragged window becomes a
   full-height column (left, right) or full-width row (top, bottom) on that side of the whole screen: when the
   root already splits in that direction it is a new first or last child, otherwise a new root split of the old
   root and the window. It takes half of the share of the child it lands next to (half the screen when the root
   was split the other way or was a leaf), matching today's "the tile dropped next to gives up half". In a corner,
   the nearer edge wins, as `ZoneOf` does for tiles. 6% is a starting number for Petre to try live; the builder
   keeps it in one named constant.
2. Tile zones, unchanged in geometry (`TileArrange.ZoneOf`: quarter-deep edges, nearest edge wins, middle swaps).
   An edge drop splits the target leaf in that direction:
   - when the target's parent already splits in that direction, the window becomes the target's sibling on that
     side, and the target gives up half of its fraction to it;
   - otherwise the target leaf is replaced by a new split of the target and the window, 50/50.
   Both give the same picture: the target is halved and nothing else moves. This is today's top/bottom behaviour
   unchanged, and it is the left/right fix.
3. Middle: swap, as today (`SettledMove`).

The dragged window is lifted out of its own place first, as today, and the target is read in the tree that is
left. Lifting removes its leaf; its fraction goes to the other children of its split in proportion; normal form
then collapses and merges. That is what makes B "give way and grow" in Petre's case:

    before                   lift C                   drop C on A's right
    Across[Down[A, B], C]    Down[A, B]               Down[Across[A, C], B]
    +----+----+              +---------+              +----+----+
    | A  |    |              |    A    |              | A  | C  |
    +----+ C  |              +---------+              +----+----+
    | B  |    |              |    B    |              |    B    |
    +----+----+              +---------+              +---------+

A window dragged in from another screen has nothing to lift here; its own screen removes its leaf as any leaver's.
A drop into the opening is unchanged in intent (`IntoOpening`): the window leaves its leaf and fills the opening.
The preview overlay keeps drawing the outcome's resulting rectangle, so it shows bands and nested splits without
its own rules.

## Dragging a separation

`SplitMove` moves the split a dragged edge belongs to: walk up from the tile's leaf to the nearest split of that
edge's direction (Across for left/right edges, Down for top/bottom) in which the tile's subtree is not at the end
on that side; the boundary between that child and the next is the one that moves, to the released edge plus or
minus the half gap, as today, and only those two children change (`MinShare` as today). A corner moves one split
per axis. An edge with no such split is an outer edge: the opening rules below.

In Petre's layout, dragging A's bottom edge moves the boundary between the top pair and B, so C's bottom moves
with it: they are one split, and the screen shows them as one line.

## Openings, keep-space, closing

- Pulling an outer edge in (`TileArrange.Open`) makes an empty leaf on that side of the tile with the pull's
  depth, placed exactly as a drop of a window on that side of the tile would be (sibling in a parent of that
  direction, otherwise a new split of the leaf). Only the pulled tile gives up room. Today a left/right opening
  is a whole new column and every tile in the column narrows; that changes, and it is the builder's call made
  here, because it follows "only the tile you touch changes". `MinOpening` and the one-opening rule stay.
- `Closes`: a tile pulled back over the opening next to it in the same split closes it, by the same quarter rule.
- Keep-space on close (`TileArrange.KeptOpen`): the closing window's leaf stays, marked empty. Nothing else
  changes.
- `Closed`: the opening leaf is removed like any leaver's, and its neighbours grow.
- A window that leaves (closed with keep-space off, moved away, minimized) removes its leaf (above).

## Arrival and minimize

- Arrival (`TileArrival`): the same rules (last-used tile when both halves keep a quarter of the screen on each
  axis, otherwise the largest tile, maximized tiles skipped). The side becomes Right when the tile is wider than
  tall, otherwise Bottom. The "alone in its column" condition goes: it existed because Right made a full-height
  column, and Right now only splits that tile. Builder's call, made here; Petre sees it live.
- Filling an opening on arrival (`TileArrange.Filled`) is unchanged: the newcomer takes the empty leaf.
- Minimize and return, screen unchanged: today's exact restore in `Tiler.Minimize.cs` stays. When the screen still
  holds the same other windows, the old order and the old tree come back as they were. Minimizing C in
  `Across[Down[A, B], C]` and restoring it gives the same layout back, never C at half height.
- Minimize and return, screen changed (`TileArrange.Returned`): when a tile is minimized the Tiler remembers, per
  window, the windows of its SIBLING SUBTREE in its parent split (the child next to it on one side, before it when
  it has one, otherwise after), the side it was on relative to them, the split's direction, and the window's own
  fraction in that parent split. On return, the largest subtree of the current tree whose leaves are all among
  those remembered windows (and holds at least one) is split on the remembered side, the window taking the
  remembered fraction of that subtree's span on that axis (clamped to `MinShare`). So a window that was a
  full-height column beside a stack comes back full height when the stack is still there as a stack, and beside
  the remaining tile when only one of them is left. When none of the remembered windows is on the screen, the
  window arrives as a new window does. This replaces today's "old column still there" rule and keeps its intent:
  the window comes back beside what it was next to, and only that changes size. Builder's call, made here.
- A count change with no known leaver (`TileArrange.Refit` without a leaver, which `ShownSizes` runs on every
  preview, arrival and live resize, plus restarts, `JoinScreen` and re-homed windows): fewer windows than leaves
  removes the LAST leaves in slot order, one by one, each with the normal leaver rule; more windows than leaves
  splits the last leaf in slot order at the bottom, once per extra window. This mirrors today's "drop the bottom
  rows, rightmost column" and "a joiner goes to the bottom of the last column". As today, a refit is for laying
  out only and never rewrites the stored entry. Nothing stored still means the automatic layout for the count.
  `TileSizesTests`' `Fit` tests and the "two leavers at once" tests are ported to these rules; where a ported
  expectation differs from today's, the commit says so. The pixel-identical promise covers a stored entry whose
  count matches the screen's windows. Builder's call, made here.
- First fill (`TileBoard.FirstFill`, on every start and every time tiling is switched on): when the screen has a
  stored tree whose leaf count equals its window count, each window is matched to the leaf whose rectangle its
  current frame overlaps most (greedily, largest overlap first), so a hand-made layout survives a restart with
  every window where it was. Today's sort by Left then Top is column-major and would swap B and C in
  `Down[Across[A, C], B]` on every restart. Without a stored tree, or with a count mismatch, today's sort stays.
  Builder's call, made here.

## Even out tiles

One menu item, two steps (Petre's pick, over "even only" and over today's behaviour):

- While some split on the workspace is uneven, the item reads "Even out tiles". It keeps every tree's structure,
  makes every split's children equal, and stores that. A tree that then equals the automatic layout is plain and
  its entry goes, as today.
- Once every split is even but some screen still has a hand-made shape (a stored tree that is not the automatic
  one), the same item reads "Reset to grid" and clears the stored entries, which is today's Even out: back to the
  automatic grid.
- With nothing stored it is not offered, as today.

The same in the row menu and the header button's right-click menu (`FloatingBar.Tiling.cs`, whose "the one way back
to equal columns and rows" comment changes with it). The pipe gets the two as separate commands, `even` (equal
splits, shape kept) and `reset-grid` (back to the automatic grid), each listed in `ControlUsage`; the pipe has no
menu to read the step from, so it names the step.

## Storing and migration

The trees go in a NEW property on `Workspace` (one entry per screen, keyed by the screen as `ScreenTileSizes` is),
not into `ScreenTileSizes.Sizes`. `TileSizes` is a positional record, so an older build handed a tree there would
deserialize null `Columns` and `Rows` and throw on the first `Shape`, and `StateVersionGate` only refuses a newer
major version, which this is not.

- On load, a workspace with old-shape entries (`TileSizes`: `Columns`, `Rows`, `Opening`) and no trees gets each
  one converted to the equivalent tree: an Across split of the columns, each column a Down split of its rows,
  normal form applied, the opening slot mapped to its leaf.
- Every save writes the trees and leaves the old property empty.
- An older build reading the file then finds no old-shape sizes and lays every screen out on the automatic grid;
  its next save drops the trees. Accepted: a downgrade loses hand-made shapes, nothing else.

## Constraints

- Pure Core classes stay pure and testable without Windows, as `GridLayout`, `TileSizes`, `TileArrange`,
  `SplitMove` and `TileArrival` are now. The Tiler's wiring changes only where it handles cells (`(int Column, int
  Row)` becomes a slot or a leaf path).
- Functional, immutable style (`ValueList`, records, `Maybe`/`Result`), and the long reasoning comments this code
  carries, per the repo's rules.
- The existing tiling tests are the spec of today's behaviour. Every test whose behaviour this design keeps keeps
  passing unchanged in meaning (it may be rewritten against the new types). Tests whose behaviour this design
  changes on purpose are rewritten to the new rule, and the commit says which: left/right drops beside a stacked
  tile, left/right openings in a stacked column, arrival's side rule, minimize return on a changed screen, the
  no-leaver refit, first fill with a stored tree, and "Even out tiles".
- Release notes for users in the PR (it changes `src/`).

## Left to the builder

- Type names, whether the tree is one recursive record or a flat list with parent links, and how a leaf is
  addressed (slot index or path).
- Whether `TileSizes` is replaced or renamed; the JSON property names of the new shape.
- How `SettledMove` reports a band drop (a new `MoveOutcome` or a `Beside` with a screen target).
- The exact neighbour chosen for minimize-return when the sibling subtree holds several leaves, within the rule
  above.
- Splitting the work into more than one pull request, if the model change and the drop change land more safely
  apart (the model with pixel-identical layout first, then the new drops).
