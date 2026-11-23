# Dragging half a line at a crossing: design

Date: 2026-11-23. Status: short design, waiting on Petre's approval. Builds on the split tree
(`docs/superpowers/specs/2026-11-19-tiling-split-tree-design.md`) and the resize design
(`docs/superpowers/specs/2026-11-08-tile-resize-design.md`). Everything not mentioned here stays as they say.

## Why

Petre, looking at four tiles in a 2x2 (A B on top, C D below): "all windows should let me resize only the other
one being resized, not the entire row", and "the split could be between a and c, which keeps b and d as they are",
in both directions.

Today a dragged edge moves the whole split it belongs to (`SplitMove.Apply`, `TileLayout.SplitBeside`). A 2x2 is
stored as two columns, `Across[Down[A, C], Down[B, D]]`, so the A|B line and the C|D line are one split: dragging
A's right edge also changes C and D. A/C happens to be local already, but only because of how the tree is grouped;
once a block is grouped as rows, the other direction behaves the same way.

## Behaviour

- Where a line is crossed straight through by a line of the other direction (a "+"), the line is in pieces. A drag
  moves only the piece along the dragged tile, between the crossings either side of it. In the 2x2: dragging A|B
  changes A and B only; dragging A/C changes A and C only. B and D, or C and D, keep their size and position.
- Both directions, every time, whichever way the tree happens to be grouped.
- A "T" (one line ending against another) is not a crossing. In A and C side by side over a full-width B, A's bottom
  edge still moves the whole line, as today: at a T there is no way to move half a line without tiles overlapping.
  So after a half-line drag turns a "+" into two T's, the line that now ends against the moved piece moves whole.
  Petre was told this and agreed.
- A corner drag is unchanged: it moves both lines it touches, whole. Moving a crossing point moves every tile that
  meets it.
- Two lines count as one crossing when the two sides' boundaries are within 2 px of each other on screen (the same
  tolerance `SettledResize` uses for an edge that held).
- The live preview while dragging (`Tiler.Drag.LiveResize`) and the drop (`Tiler.Resize.MovedASplit`) use the same
  rule through the same function, so the preview and the result never disagree.
- The new shape is the stored layout. "Even out tiles" keeps the shape, as today; "reset to grid" goes back to the
  automatic columns.
- A locked tile still follows a dragged separation (`Tiler.Lock.cs`), as today.

## Approaches

1. Regroup the split tree at a crossing when a drag starts, then move one split. Picked by Petre. Everything built
   on the tree keeps working: stored sizes, drops, openings, minimize and rejoin, the lock.
2. Replace the tree with free-form rectangles and shared-edge constraints. It would allow "pinwheel" layouts and so
   keep a few more drags local after lines go offset, but drops, openings, rejoin, the lock and the stored layouts
   are all written against the tree. Rejected: most of tiling rewritten for a small gain.

## How (approach 1)

The dragged edge belongs to split S of axis X (found as today by `SplitBeside`), between its children P and Q.
Perpendicular boundaries of P and Q are the boundaries of P's own top-level split of the other axis Y, and the same
for Q. A leaf, or a child split along X, has none.

1. Lay out P and Q (`GridLayout.ChildRects`) and list each one's Y boundaries in pixels. Boundaries that match within
   the tolerance are the crossings. No crossings: today's behaviour, unchanged.
2. The dragged tile's span along Y lies between two neighbouring crossings (or an end of S). Cut P and Q at those
   crossings into bands. A crossing is always one of P's own child boundaries and one of Q's, so a band is a run of
   consecutive children (a single child, or a Y split of that run with its shares rescaled).
3. Replace P and Q in S by one child, `Y[ X[P band, Q band] for each band ]`, whose share is P's plus Q's. In each
   band the X split shares are P's and Q's in proportion. Pass the result through `TileLayout.Normal`.
4. Move the boundary of the band holding the dragged tile with today's arithmetic (`SplitMove.Split`, the 10% minimum
   applies within that band).

Pairwise only: S's other children are untouched. With three aligned columns, the next drag regroups the next pair by
the same rule, so a block of any size behaves as one grid of pieces without regrouping it all at once.

The regroup must not move any tile by itself. The dragged tile's side sets a crossing's position; the other side
may shift by up to the 2 px tolerance, which is accepted.

Regrouping changes the depth-first leaf order (`Across[Down[A, C], Down[B, D]]` lists A C B D;
the regrouped tree, which normal form turns into `Down[Across[A, B], Across[C, D]]`, lists A B C D), and the board's window order on a
screen is its leaf order. So the move returns the leaf permutation with the new layout, and both callers apply it:
`TileBoard.Reordered` for the screen, and the opening's leaf index. `MovedASplit` passes the dragged tile's NEW leaf
index to `TileArrange.Open` for the corner case. The comment there ("the split changes shares only, never the
tree's structure") and the header of `SplitMove` ("on screen they are one line, so they are one split") change with
it.

## Tests

In `Deskbar.Core.Tests`, on `SplitMove` and the Tiler:

- 2x2 grouped as columns, then as rows: A|B and A/C each move only their two tiles, B/D or C/D frames unchanged
  to the pixel.
- Three rows on both sides of a column line, all aligned: a drag from the middle row moves only the middle piece.
- A T (A and C over a full-width B): the whole line moves, as today.
- Boundaries 3 px apart: not a crossing, whole line moves; 2 px apart: a crossing.
- A corner drag at a crossing: both whole lines move, as today.
- The board order and the opening follow the permutation; the dragged window stays in its tile.
- Live resize and the drop produce the same frames.

Manual steps go into `docs/superpowers/notes/manual-test-script.md`: tile four windows, drag A|B, then A/C, then
the now-offset line, then a corner.

## Left to the builder

Naming, where the regroup lives (`SplitMove` or `TileLayout`), the exact shape of the permutation result, and the
trace line (`tile: ... resized by its Right edge, split at a crossing, sizes ...`).
