# Dragging half a line at a crossing: implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A dragged tile edge that meets a straight "+" crossing moves only the piece of the line along the dragged tile, in both directions.

**Architecture:** `SplitMove.Apply` first regroups the two children either side of the dragged boundary into bands at the crossings around the dragged tile, then moves one split with today's arithmetic. It returns the new layout plus the leaf permutation the regroup caused, and the Tiler reorders the screen's windows to match before storing and placing.

**Tech Stack:** C# / .NET 10, xUnit, CSharpFunctionalExtensions. All logic in `Deskbar.Core`, no Windows code.

**Spec:** `docs/superpowers/specs/2026-11-23-tile-local-split-design.md`

## Global Constraints

- Two boundaries are one crossing when they are within 2 px of each other on screen (`const int CrossingTolerance = 2`).
- No regroup when BOTH axes have an inner boundary (a corner drag): both whole lines move, as today.
- A "T" (one side has no boundary of the other axis at that place) is not a crossing: the whole split moves, as today.
- The 10% minimum (`SplitMove.MinShare`) applies within the band's split.
- The regroup itself moves no tile by more than the 2 px tolerance; the dragged tile's side sets each crossing's position.
- Every edited tree goes through `TileLayout.Normal`.
- C# style: expression bodies, `var`, readonly collections, lambdas over loops, ample comments with the reasoning.
- Build before the PR: `dotnet build Deskbar.sln -c Release -warnaserror`. Tests: `dotnet test tests/Deskbar.Core.Tests --filter "Category!=Integration"` (Core only; do not run `Deskbar.Windows.Tests` on Petre's desktop).

## Review Focus

1. The board's window order after a regroup: if it is not reordered, windows trade places on release. Pinned by Task 2's "B and D stay put" test.
2. The live preview and the drop disagreeing (live places by the old order). Pinned by Task 2's live test.
3. An opening inside the regrouped pair: its index must follow the permutation. Pinned by Task 1's opening test.
4. A crossing deeper than the top level of P or Q (P's band child is itself an Across split): normal form must merge it into the band's split. Pinned by Task 1's nested test.
5. After a half-line drag, the now-offset line must move whole without throwing. Pinned by Task 2's offset test.

---

### Task 1: Regroup at a crossing in SplitMove

**Files:**
- Modify: `src/Deskbar.Core/Tiling/SplitMove.cs` (header comment, `Apply`, new private helpers)
- Modify: `src/Deskbar.Core/Tiling/Tiler.Drag.cs:72` and `src/Deskbar.Core/Tiling/Tiler.Resize.cs:365` only as far as needed to compile (`.Layout`); real wiring is Task 2
- Modify: `tests/Deskbar.Core.Tests/Tiling/SplitMoveLayoutTests.cs` (helpers map `.Layout`), `tests/Deskbar.Core.Tests/Tiling/TileArrangeLayoutTests.cs` (same, where it calls `SplitMove.Apply`)
- Create: `tests/Deskbar.Core.Tests/Tiling/SplitMoveCrossingTests.cs`

**Interfaces:**
- Produces: `public sealed record SplitMoved(TileLayout Layout, IReadOnlyList<int> Order)` in `SplitMove.cs`. `Order[newLeaf] == oldLeaf`; identity when nothing was regrouped. `Layout.Opening` is already remapped.
- Produces: `public static Maybe<SplitMoved> SplitMove.Apply(TileLayout layout, WindowRect area, int gap, int leaf, Edges moved, WindowRect frame)` (same parameters, new return type).
- Produces: `public IReadOnlyList<WindowHandle> SplitMoved.Windows(IReadOnlyList<WindowHandle> windows, TileLayout before)`: the screen's windows (in slot order for `before`) in slot order for `Layout`. Slots map to leaves through `TileArrange.LeafOf`, which skips the opening.
- Produces: `public int SplitMoved.LeafOf(int oldLeaf) => Order.IndexOf(oldLeaf)` (the dragged tile's new leaf).

- [ ] **Step 1: Write the failing tests** in `SplitMoveCrossingTests.cs`, using `Trees` (`Tree`, `Across`, `Down`, `C`, `L`, `Text`). Area `new WindowRect(0, 0, 1000, 600)`, gap 0. `Grid2x2Columns = Tree(Across(C(Down(C(L,.5),C(L,.5)),.5), C(Down(C(L,.5),C(L,.5)),.5)))` (leaves A=0, C=1, B=2, D=3); `Grid2x2Rows = Tree(Down(C(Across(C(L,.5),C(L,.5)),.5), C(Across(C(L,.5),C(L,.5)),.5)))` (A=0, B=1, C=2, D=3).

```csharp
[Fact] public void Dragging_A_B_in_columns_moves_only_the_top_piece()
{
    var moved = SplitMove.Apply(Grid2x2Columns, Area, 0, 0, Edges.Right, new(0, 0, 700, 300)).Value;
    Assert.Equal("D[A[L 0.7, L 0.3] 0.5, A[L 0.5, L 0.5] 0.5]", Text(moved.Layout.Root));
    Assert.Equal([0, 2, 1, 3], moved.Order);
}

[Fact] public void Dragging_A_C_in_rows_moves_only_the_left_piece()
{
    var moved = SplitMove.Apply(Grid2x2Rows, Area, 0, 0, Edges.Bottom, new(0, 0, 500, 400)).Value;
    Assert.Equal("A[D[L 0.666667, L 0.333333] 0.5, D[L 0.5, L 0.5] 0.5]", Text(moved.Layout.Root));
    Assert.Equal([0, 2, 1, 3], moved.Order);
}

// C and D (old leaves 1 and 3) are laid out in exactly the same pixels before and after.
[Fact] public void The_other_pieces_tiles_do_not_move_a_pixel()
{
    var before = GridLayout.Arrange(Area, 4, 8, Grid2x2Columns);
    var moved = SplitMove.Apply(Grid2x2Columns, Area, 8, 0, Edges.Right, new(8, 8, 696, 296)).Value;
    var after = GridLayout.Arrange(Area, 4, 8, moved.Layout);
    Assert.Equal(before[1], after[moved.LeafOf(1)]);
    Assert.Equal(before[3], after[moved.LeafOf(3)]);
}

// Three aligned rows each side: a drag from the middle row moves only the middle piece.
[Fact] public void A_middle_row_drag_cuts_three_bands()  // Across[Down thirds, Down thirds], leaf 1, Right to 700, frame (0,200,700,400)
// Text: "D[A[L 0.5, L 0.5] 0.333333, A[L 0.7, L 0.3] 0.333333, A[L 0.5, L 0.5] 0.333333]", Order [0, 3, 1, 4, 2, 5]

// Four aligned rows, drag the top one: the rows below stay grouped as a run.
[Fact] public void Rows_past_the_next_crossing_stay_one_band()  // Across[Down quarters, Down quarters], leaf 0, Right to 700, frame (0,0,700,150)
// Text: "D[A[L 0.7, L 0.3] 0.25, A[D[L 0.333333, L 0.333333, L 0.333333] 0.5, D[L 0.333333, L 0.333333, L 0.333333] 0.5] 0.75]"
// Order [0, 4, 1, 2, 3, 5, 6, 7]

[Fact] public void A_T_join_moves_the_whole_line()  // Tree(Down(C(Across(C(L,.5),C(L,.5)),.5), C(L,.5))), leaf 0, Bottom to 400
// Root shares [2/3, 1/3], Order [0, 1, 2]

[Fact] public void Boundaries_three_pixels_apart_are_not_a_crossing()  // Across[Down[.5,.5], Down[303.0/600, 297.0/600]], leaf 0, Right to 700
// Root shares [.7, .3], structure unchanged, Order identity
[Fact] public void Boundaries_two_pixels_apart_are_a_crossing()  // same with 302.0/600, 298.0/600: root is Down, Order [0, 2, 1, 3]

[Fact] public void A_corner_at_a_crossing_moves_both_whole_lines()  // Grid2x2Columns, leaf 0, Right|Bottom, frame (0,0,700,400)
// Root Across [.7, .3], left Down [2/3, 1/3], right Down [.5, .5], Order identity

[Fact] public void The_opening_follows_the_regroup()  // Grid2x2Columns with Opening = 1, leaf 0, Right to 700
// moved.Layout.Opening == 2

// P's top-left band is itself an Across: Normal merges it, so the band is one Across of three.
[Fact] public void A_band_that_is_already_split_across_is_merged()  // Tree(Across(C(Down(C(Across(C(L,.5),C(L,.5)),.5),C(L,.5)),.5), C(Down(C(L,.5),C(L,.5)),.5))), leaf 1 (top-left's right half), Right to 700, frame (250,0,700,300)
// Text: "D[A[L 0.25, L 0.45, L 0.3] 0.5, A[L 0.5, L 0.5] 0.5]", Order [0, 1, 3, 2, 4]

[Fact] public void Windows_follow_the_order()
{
    var moved = SplitMove.Apply(Grid2x2Columns, Area, 0, 0, Edges.Right, new(0, 0, 700, 300)).Value;
    Assert.Equal([W(1), W(3), W(2), W(4)], moved.Windows([W(1), W(2), W(3), W(4)], Grid2x2Columns));
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~SplitMoveCrossingTests"`
Expected: build error, `SplitMoved` does not exist.

- [ ] **Step 3: Implement in `SplitMove.cs`**

`Apply` becomes: find the axis to move as today. If exactly one axis has an inner boundary, try `Regrouped(layout, area, gap, leaf, side)`; on Some, run today's boundary arithmetic on the regrouped layout at the dragged tile's new leaf; on None, run it on the original with the identity order. Two inner boundaries: today's code, identity order.

`static Maybe<(TileLayout Layout, IReadOnlyList<int> Order)> Regrouped(TileLayout layout, WindowRect inner, int leaf, TileArrange.Zone side)`:

```
path = layout.PathTo(leaf); i = SplitBeside(path, side); S = path[i].Branch (axis X); k = boundary index
P = S.Children[k], Q = S.Children[k+1]; Y = the other axis
rects of P and Q = GridLayout.ChildRects(S, rects[i])[k], [k+1]
boundariesOf(node, rect) = node is TileBranch b && b.Axis == Y
    ? ChildRects(b, rect) start positions of children 1..n-1 along Y, each with its child index
    : []
crossings = pairs (pIndex, qIndex) whose positions differ by <= CrossingTolerance
none -> None
the dragged tile is in P (path[i].Child == k) or Q; its child index jp in that side's Y split (or the whole side when
  it has no Y split: then there is no crossing anyway)
lo = the nearest crossing at or above the tile's child, hi = the nearest below; bands = up to three runs of children
  per side: [start, lo), [lo, hi), [hi, end), empty runs dropped
band node = one child alone, or a Y split of the run with shares rescaled to sum 1; band share = the dragged side's
  run share sum
R = Y[ X[(Pband, sP/(sP+sQ)), (Qband, sQ/(sP+sQ))] per band, band share ], replacing P and Q in S with share sP+sQ
Order: leaves outside S's P..Q range unchanged; inside, per band, P band's leaves then Q band's leaves
Opening: Order.IndexOf(old opening)
return Normal(new root, opening) with Order
```

`Normal` keeps depth-first order when it flattens, so the order computed before it still holds. Update the file header: "on screen they are one line, so they are one split" holds at a T; at a crossing the line is in pieces (spec reference).

Callers in `Tiler.Drag.cs` and `Tiler.Resize.cs`: use `.Layout` for now so the build passes.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~SplitMove"`
Expected: all pass, the existing `SplitMoveLayoutTests` included.

- [ ] **Step 5: Commit**

```bash
git add src/Deskbar.Core/Tiling tests/Deskbar.Core.Tests/Tiling
git commit -m "feat: a dragged tile edge at a crossing moves only its own piece of the line"
```

### Task 2: The Tiler reorders the screen on a regroup

**Files:**
- Modify: `src/Deskbar.Core/Tiling/Tiler.Resize.cs` (`MovedASplit`, around line 365, and its comment "never the tree's structure")
- Modify: `src/Deskbar.Core/Tiling/Tiler.Drag.cs` (`LiveResize`, line 67)
- Test: `tests/Deskbar.Core.Tests/Tiling/TilerResizeTests.cs`

**Interfaces:**
- Consumes: `SplitMoved`, `SplitMoved.Windows`, `SplitMoved.LeafOf` from Task 1; `TileBoard.Reordered(int screen, IReadOnlyList<WindowHandle> order)`; `Keep(workspace, board)`.

- [ ] **Step 1: Write the failing tests** in `TilerResizeTests.cs`. Helper `StartFour()`: W1..W4 on Alpha, screen 1, `Start()`. The automatic layout is two columns: W1 top-left (8,8,956,516), W2 bottom-left (8,524,956,1032), W3 top-right (964,8,1912,516), W4 bottom-right (964,524,1912,1032).

```csharp
// Petre: "the split could be between a and c, which keeps b and d as they are".
[Fact]
public void Dragging_the_top_separation_at_a_crossing_leaves_the_bottom_row_alone()
{
    StartFour();
    ResizedByHand(W(1), new WindowRect(8, 8, 1337, 516));
    Assert.Equal(new WindowRect(1345, 8, 1912, 516), surface.Frames[W(3)]);
    Assert.Equal(new WindowRect(8, 524, 956, 1032), surface.Frames[W(2)]);
    Assert.Equal(new WindowRect(964, 524, 1912, 1032), surface.Frames[W(4)]);
    Assert.Equal([W(1), W(3), W(2), W(4)], tiler.Board.On(1));
    Assert.Equal(GridLayout.Arrange(Area, 4, Gap, Stored(AreaKey)), FramesOf([W(1), W(3), W(2), W(4)]));
}

[Fact]
public void Live_resize_at_a_crossing_moves_only_the_neighbour_in_the_piece()
// Settling W1, frame (8,8,1337,516), MovingSubject W1: surface.Placed == [(W3, (1345,8,1912,516))]; nothing stored;
// release with ResizedByHand at the same frame: no further Place.

[Fact]
public void After_a_half_line_drag_the_offset_line_moves_whole()
// After the first test's drag, ResizedByHand(W1, (8,8,1337,700)): all four heights change (the Down root's split),
// W2 and W4 both start at y 708; no exception; board order unchanged [W1, W3, W2, W4].
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~TilerResizeTests"`
Expected: the first two fail (W2 and W4 move, or windows swap); the third may pass or fail.

- [ ] **Step 3: Wire it**

`MovedASplit`: on Some, compute `order = split.Windows(windows, shown)`; when it differs from `windows`, `Keep(workspace, board.Reordered(screenNow, order))` BEFORE `StoreLayout`, so no evaluate between the two sees the new layout with the old order. Pass `split.LeafOf(leaf)` to `TileArrange.Open`. Trace line: `... resized by its {moved} edge at a crossing, sizes ...` when the order changed. Rewrite the comment that says the split never changes the tree's structure.

`LiveResize`: place with `SlotsOf(screen, next.Windows(windows, shown), next.Layout)`. It stores nothing and leaves the board alone; the drop recomputes from the stored layout and the release frame, so the two agree.

- [ ] **Step 4: Run all Core tests**

Run: `dotnet test tests/Deskbar.Core.Tests --filter "Category!=Integration"`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/Deskbar.Core/Tiling tests/Deskbar.Core.Tests/Tiling
git commit -m "feat: the tiler keeps windows in their tiles when a drag regroups the layout"
```

### Task 3: Docs, Release build, PR

**Files:**
- Modify: `README.md:292` ("Drag a separation between two tiles to resize them.")
- Modify: `docs/features/tiling.md:19`
- Modify: `docs/superpowers/notes/manual-test-script.md` (new step T7 after T6)

- [ ] **Step 1:** README and `docs/features/tiling.md`: add one sentence after "Drag a separation...": where lines cross, a drag moves only the piece beside the tile you drag, so the tiles on the other side stay as they are.
- [ ] **Step 2:** Manual step T7: tile four windows; drag A|B (C|D stays); drag A/C on a fresh 2x2 after Reset to grid (B/D stays); drag the now-offset line (all four follow); drag a corner at a "+" (all four follow); the live preview matches each release.
- [ ] **Step 3:** `dotnet build Deskbar.sln -c Release -warnaserror` passes; Core tests pass.
- [ ] **Step 4:** Commit, push the branch, open the PR with a `## Release notes` section for users, ending with the model-name line; watch CI to green and arm the base watch.
