# Resizable tiles: design

Date: 2026-11-08. Status: draft, waiting on Petre's approval. Builds on the tiling design
(`docs/superpowers/specs/2026-11-08-tiling-design.md`) and on its implementation in
[Deskbar #337](https://github.com/AppifySheets/Deskbar/pull/337). Read that design first; everything
here changes or adds to it, and anything not mentioned stays as it says.

## What this is and why

Petre, after the live check: "i want to be able to resize tiles by dragging the separation". Today a
tiled window resized by its edge snaps back into its slot, because the tiling design kept grid tiles
equal. He tried it by hand and saw exactly that: "i could drag, it didn't stay in dragged position".

The separation between two tiles is only a gap, but Windows gives every window an invisible resize
border about 7 px wide that reaches into it. That is why Petre already sees the resize cursor there.
So dragging the separation is dragging the edge of the window beside it, and no Deskbar-owned handle
is needed (one was considered and dropped: it would fight the windows' own borders for the click).

## Goals

- Dragging a separation moves the split to where it is released. The windows on both sides of that
  separation take the new sizes; nothing snaps back.
  - A vertical separation between two columns changes those two columns' widths. Every window in
    both columns follows.
  - A horizontal separation inside a column changes the heights of the two rows it divides, in that
    column only.
  - A corner drag moves both splits it touches.
- Every way of resizing counts: the separation, the visible edge, a corner, and AltSnap's
  right-drag resize.
- The sizes stick (Petre: "keep my sizes"). They are kept per workspace and per screen, survive a
  restart, and survive windows opening and closing (see "Keeping sizes when the grid changes").
- "Even out tiles" on the workspace row menu resets that workspace to equal tiles. Petre agreed to
  this when asked how he would get equal tiles back.

## Non-goals

- A Deskbar-drawn handle in the gap, or any visible splitter.
- Live following while dragging: the neighbours take their new size when the drag ends, not during
  it (see "My calls").
- Resizing the outer edges of the grid. An edge on the screen side of the grid has no split behind it;
  dragging it snaps back, as today.
- Sizes for layouts other than the grid.
- A new hotkey or a double-click gesture. Double-clicking a window's top or bottom edge already means
  "stretch to full height" in Windows, and the separation is an edge.

## Anti-goals

- A resize that leaves windows overlapping, or a gap wider or narrower than the setting.
- A split that keeps moving after the drag ended, or Deskbar and an app fighting over a size.
- Changing anything for a workspace that is not tiled, or while the experiment is off.

## Behaviour

### Telling a resize from a move

When a tiled window settles after a change Deskbar did not make, the Tiler compares its visible frame
with the frame it last accepted for that window.

- If every edge moved by the same amount, or both edges of one axis moved together, it is a move,
  and the existing rule applies (swap by centre, back to slot, or join another screen).
- If one or two adjacent edges moved and the edges opposite them stayed put (within 2 px, physical),
  and a mouse button was down at some point during the change, it is a resize. The surface reports
  that (`ITileSurface` gains it; `Settled` alone cannot tell). A size change an app makes on its own,
  with no button down, is not a resize: it snaps back as today, under the put-back backstop, so
  splits never drift without Petre touching them. Each moved edge that faces a neighbouring tile moves a split. Each moved edge on
  the outside of the grid snaps back.
- Anything else, for example a keyboard snap that changes the size and the position, is a move, as
  today.

### Moving a split

- A vertical split between columns i and i+1 moves to the x where the resized window's edge was
  released, less half the gap, so the gap stays exactly the gap setting.
- A horizontal split between rows j and j+1 of one column works the same on y.
- The new split is clamped so no column or row gets narrower than 10% of the screen's work area. A
  window that will not shrink to its new slot is accepted where it lands, exactly as the tiling design
  already accepts one at any re-tile; nothing is placed twice to measure it.
- The Tiler then re-tiles that screen with the new proportions: one placement per affected window.

### Keeping sizes when the grid changes

Sizes belong to places on the screen, not to windows (Petre chose this: "places"). The left column
stays wide whatever happens; the window in it may change when windows open, close or swap, and Petre
swaps the one he wants back by dragging. Sizes do not follow a window around the grid.

Sizes are stored as proportions: one list of column fractions per screen, and one list of row
fractions per column, both indexed by position (column 0 is the leftmost, row 0 the top).

Which index changes when the grid changes shape:

- A window that leaves a column takes its own row's fraction with it; the rows above and below it
  share that space in proportion.
- A window that joins a column adds a row at the bottom of it, as the grid already places it.
- When the number of columns drops, the rightmost column's fraction is the one removed (the grid
  fills columns left to right, so the last column is the one that empties), and the others share it
  in proportion. Its row fractions go with it.
- When the number of columns rises, the new column is added on the right.

- A window joins or leaves without changing the number of columns: column widths stay as they are.
  The column that gains or loses a row gives the new row an equal share, scaling its other rows down
  in proportion, or hands the leaving row's share to the rest in proportion.
- The number of columns changes (for example 4 windows become 5, which makes 2 columns into 3): the
  new column takes an equal share, 1/n, and the existing columns scale down in proportion. A column
  that goes away gives its share back to the others in proportion. Row fractions belong to a column's
  index, and a new column starts with equal rows.
- A screen that drops out (unplugged, asleep, a dock reconnecting, a resolution change at sign-in)
  keeps its stored sizes. They are keyed by the screen's bounds and used again when a screen with the
  same bounds comes back. `ScreensChanged` never deletes stored sizes. They are removed only by
  "Even out tiles", `untile`, or deleting the workspace.

### Resetting

- "Even out tiles" on the workspace row menu, directly under "Tile windows". It is shown only while
  that workspace is tiled and has at least one moved split, and it resets every screen of that
  workspace to equal. It obeys the same gate as "Tile windows": experiment on, workspace tiled, Pro.
- A pipe twin, `even <workspace>`, listed in `ControlUsage` beside `tile`/`untile`. It refuses with
  the same messages as `tile` when the experiment is off or the copy is not Pro, and it succeeds and
  does nothing on a workspace whose tiles are already equal.
- `untile` forgets the workspace's sizes, so tiling it again starts from equal tiles.

### Persistence

The proportions are stored on the workspace in `state.json`, next to `Tiled`, keyed by screen.
The screen key is the screen's position and size (its bounds), not its number, so that a numbering
change after plugging in a screen does not hand one screen's sizes to another. A `state.json` without
the new key loads with equal tiles and needs no migration.

## Constraints

- The pure parts go in `Deskbar.Core/Tiling` with unit tests, as before: telling a resize from a
  move, moving a split with its clamps, and carrying fractions across a change in shape.
  `GridLayout.Arrange` gains an overload that takes the fractions; the existing equal overload stays
  and is what an equal layout uses.
- `Tiler.cs` is already 646 lines. The split logic does not go into it; the Tiler calls a separate
  pure class and keeps only the wiring.
- Everything in the tiling design's "Constraints and known traps" still applies: physical pixels,
  visible frames, the accepted-frame guard, the put-back backstop (a resize that keeps snapping back
  from an app counts toward it), and nothing acting during a pending bar drop or while a window is
  still settling.
- Experimental and Pro, through the existing gate. Nothing here runs while the experiment is off.

## My calls (for Petre to check)

- Live following is left out: the neighbours resize when the drag ends. Following live would mean
  re-tiling on every location change during the drag, which the tiling design avoids on purpose (the
  hook only reports a window once it has settled). It can come later if the end-of-drag version
  feels laggy.
- The 10% minimum for a column or row, and no other clamp.
- Only a resize with a mouse button down moves a split.
- A screen that drops out keeps its sizes for when it returns.
- New columns take an equal share and the rest scale in proportion, rather than some other rule.
- A leaving window takes its own row's share; a column that goes away is always the rightmost; a new
  column always appears on the right.
- `untile` forgets the sizes.
- The screen key is the screen's bounds, not its number.
- "Even out tiles" only appears once a split has moved.

## Left to the builder

- Names of the new types, and where the fractions live in the model.
- The trace lines (one per moved split and one per reset, written only when something changes).
