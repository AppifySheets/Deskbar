# Resizable Tiles Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Dragging the separation between two tiles (a tiled window's edge, by hand) moves the split instead of snapping back; the sizes stick per workspace and screen, and "Even out tiles" resets them.

**Architecture:** Three pure pieces in `Deskbar.Core/Tiling`, unit-tested: `TileSizes` (column and row fractions, reshaped when the grid changes shape) with a sized `GridLayout.Arrange`; `SettledResize` (tells a resize by hand from a move); `SplitMove` (moves a split with its 10% clamp). The persisted sizes live on `Workspace`, set through `WorkspaceManager`. The surface learns to report whether a settle was by hand and each screen's monitor bounds. The `Tiler` only wires them, in a new partial file, because `Tiler.cs` is already 646 lines.

**Tech Stack:** C# / .NET 10, WPF, System.Reactive, CSharpFunctionalExtensions, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-08-tile-resize-design.md` (read it, and the tiling design it builds on: `docs/superpowers/specs/2026-11-08-tiling-design.md`).

## Global Constraints

- Petre's C# rules: no `private` keyword, expression bodies, `var`, readonly collections, immutable records, setters in the order none > private init > private set > init > set, lambdas over loops, `Result`/`Result<T>`/`Maybe<T>` for expected failures.
- Ample comments that carry the reasoning. Quote Petre where a decision is his ("i want to be able to resize tiles by dragging the separation", "keep my sizes", "places").
- Physical pixels; `WindowRect` Right and Bottom are exclusive.
- Sizes belong to places, not windows: column fractions per screen and row fractions per column, indexed by position (column 0 leftmost, row 0 top). Fractions in each list sum to 1.
- Reshape rules: a leaving window takes its own row's fraction (the rest share it in proportion); a joining window adds a row at the bottom with an equal share (1/n, the rest scale in proportion); when columns drop, the rightmost goes and the rest share it in proportion; when columns rise, the new column is added on the right with 1/n and the rest scale in proportion; a row count that drops with no known leaver drops the bottom rows.
- Resize test: one or two adjacent edges moved, the edges opposite them within 2 px of where they were, AND a mouse button down during the change. Anything else is a move and keeps the existing rules.
- Clamp: no column or row narrower than 10% of the work area's inner span. No other clamp; a window that will not shrink is accepted where it lands.
- An edge on the outside of the grid has no split; dragging it snaps back (existing put-back, counted by the backstop).
- Sizes are kept per workspace keyed by the screen's monitor bounds (`"L,T,R,B"`), survive restarts and screens dropping out, and are removed only by "Even out tiles", `untile`, or deleting the workspace.
- Experimental and Pro through the existing gate; nothing here runs while the experiment is off.
- No live following during a drag: the neighbours move when the drag settles.
- Before the PR: `dotnet build Deskbar.sln -c Release -warnaserror` and `dotnet test Deskbar.sln --filter "Category!=Integration"` green. Release notes say it is part of the experimental tiling.

## Review Focus

1. A corner drag moves a vertical and a horizontal split in one settle, and the grid stays gap-exact. (Task 2, `A_corner_drag_moves_both_splits`; Task 5, `A_corner_resize_moves_both_splits_and_retiles`)
2. An app growing itself (no button down) never moves a split. (Task 5, `An_app_resizing_itself_snaps_back_and_moves_no_split`)
3. A window closing from the middle of a resized column keeps the other rows' proportions. (Task 1, `A_leaving_row_gives_its_share_to_the_rest_in_proportion`; Task 5, `Closing_a_middle_window_keeps_the_other_rows_proportions`)
4. A screen that drops out and comes back with the same bounds gets its sizes back. (Task 5, `A_screen_that_returns_with_the_same_bounds_gets_its_sizes_back`)
5. Turning tiling off and on, or "Even out tiles", gives equal tiles. (Task 3, `Untiling_forgets_the_sizes`, `Evening_out_clears_the_sizes`)

---

### Task 1: Sizes and the sized grid

**Files:**
- Create: `src/Deskbar.Core/Tiling/TileSizes.cs`
- Modify: `src/Deskbar.Core/Tiling/GridLayout.cs`
- Test: `tests/Deskbar.Core.Tests/Tiling/TileSizesTests.cs`, `tests/Deskbar.Core.Tests/Tiling/GridLayoutTests.cs` (add)

**Interfaces:**
- Produces:
  - `public sealed record TileSizes(IReadOnlyList<double> Columns, IReadOnlyList<IReadOnlyList<double>> Rows)` with `static TileSizes Equal(IReadOnlyList<int> shape)`, `bool IsEqual` (every list even, within 1e-9), and `TileSizes Fit(IReadOnlyList<int> shape, Maybe<(int Column, int Row)> leaving)` applying the reshape rules. `shape[c]` is the row count of column c. Record equality must compare list contents (override `Equals`/`GetHashCode`), because tests and the Tiler compare sizes.
  - `GridLayout.Shape(int count) : IReadOnlyList<int>` (the column sizes the existing `Arrange` already computes; refactor `Arrange` to use it).
  - `GridLayout.Arrange(WindowRect area, int count, int gap, TileSizes sizes) : IReadOnlyList<WindowRect>`; the existing overload equals this one with `TileSizes.Equal(Shape(count))`. Integer remainders still go to the last column and last row.
  - `GridLayout.CellOf(int slot, IReadOnlyList<int> shape) : (int Column, int Row)`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Equal_sizes_for_one_then_two_rows() =>
    Assert.Equal(new TileSizes([0.5, 0.5], [[1.0], [0.5, 0.5]]), TileSizes.Equal([1, 2]));

[Fact] public void A_joining_row_takes_an_equal_share() =>          // [0.7,0.3] with one row each, a 3rd window joins column 1
    Assert.Equal(new TileSizes([0.7, 0.3], [[1.0], [0.5, 0.5]]),
        new TileSizes([0.7, 0.3], [[1.0], [1.0]]).Fit([1, 2], Maybe<(int, int)>.None));

[Fact] public void A_leaving_row_gives_its_share_to_the_rest_in_proportion() =>
    AssertClose(new TileSizes([1.0], [[0.2 / 0.7, 0.5 / 0.7]]),
        new TileSizes([1.0], [[0.2, 0.3, 0.5]]).Fit([2], (0, 1)));

[Fact] public void A_new_column_takes_one_nth_and_appears_on_the_right() =>   // 4 windows [2,2] become 5 [1,2,2]
    AssertClose([0.7 * 2 / 3, 0.3 * 2 / 3, 1.0 / 3],
        new TileSizes([0.7, 0.3], [[0.5, 0.5], [0.5, 0.5]]).Fit([1, 2, 2], Maybe<(int, int)>.None).Columns);

[Fact] public void A_column_that_goes_away_is_the_rightmost() =>
    AssertClose([0.4 / 0.6, 0.2 / 0.6],
        new TileSizes([0.4, 0.2, 0.4], [[1.0], [1.0], [1.0]]).Fit([1, 1], Maybe<(int, int)>.None).Columns);

[Fact] public void Rows_that_drop_with_no_known_leaver_drop_from_the_bottom() =>
    AssertClose([1.0], new TileSizes([1.0], [[0.25, 0.75]]).Fit([1], Maybe<(int, int)>.None).Rows[0]);

// GridLayoutTests additions
[Fact] public void Sized_columns_follow_their_fractions() =>
    Assert.Equal([new WindowRect(0, 0, 700, 600), new WindowRect(700, 0, 1000, 600)],
        GridLayout.Arrange(new(0, 0, 1000, 600), 2, 0, new TileSizes([0.7, 0.3], [[1.0], [1.0]])));

[Fact] public void Sized_rows_follow_their_fractions() =>
    Assert.Equal([new WindowRect(500, 0, 1000, 150), new WindowRect(500, 150, 1000, 600)],
        GridLayout.Arrange(new(0, 0, 1000, 600), 3, 0, new TileSizes([0.5, 0.5], [[1.0], [0.25, 0.75]])).Skip(1));

[Fact] public void Equal_sizes_give_the_same_grid_as_before() =>
    Assert.Equal(GridLayout.Arrange(new(0, 0, 1000, 600), 5, 8),
        GridLayout.Arrange(new(0, 0, 1000, 600), 5, 8, TileSizes.Equal(GridLayout.Shape(5))));

[Fact] public void Cell_of_a_slot() => Assert.Equal((2, 1), GridLayout.CellOf(4, [1, 2, 2]));
```

`AssertClose` compares within 1e-9.

- [ ] **Step 2: Run them and see them fail.** `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~TileSizes|FullyQualifiedName~GridLayoutTests"`. Expected: FAIL, types missing.
- [ ] **Step 3: Implement.** Pure code; gaps behave exactly as in the equal grid (outer edges the full gap, inner edges split it).
- [ ] **Step 4: Run them and see them pass,** then `dotnet test tests/Deskbar.Core.Tests`.
- [ ] **Step 5: Commit** `feat: tile sizes, and a grid that follows them`.

---

### Task 2: Telling a resize from a move, and moving a split

**Files:**
- Create: `src/Deskbar.Core/Tiling/SettledResize.cs`, `src/Deskbar.Core/Tiling/SplitMove.cs`
- Test: `tests/Deskbar.Core.Tests/Tiling/SettledResizeTests.cs`, `tests/Deskbar.Core.Tests/Tiling/SplitMoveTests.cs`

**Interfaces:**
- Produces:
  - `[Flags] public enum Edges { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }`
  - `SettledResize.Classify(WindowRect accepted, WindowRect frame, bool byHand) : Edges` returns the moved edges for a resize and `Edges.None` for a move (or anything that is not a resize).
  - `SplitMove.Apply(TileSizes sizes, IReadOnlyList<int> shape, WindowRect area, int gap, (int Column, int Row) cell, Edges moved, WindowRect frame) : Maybe<TileSizes>`; `None` when no moved edge faces a split (outer edges only), so the caller snaps back. Moved outer edges alongside an inner one are ignored.

- [ ] **Step 1: Write the failing tests**

```csharp
static readonly WindowRect Accepted = new(0, 0, 500, 600);
[Fact] public void A_right_edge_drag_by_hand_is_a_resize() => Assert.Equal(Edges.Right, SettledResize.Classify(Accepted, new(0, 0, 700, 600), byHand: true));
[Fact] public void The_same_change_without_a_button_is_not() => Assert.Equal(Edges.None, SettledResize.Classify(Accepted, new(0, 0, 700, 600), byHand: false));
[Fact] public void A_shift_is_a_move() => Assert.Equal(Edges.None, SettledResize.Classify(Accepted, new(100, 0, 600, 600), byHand: true));
[Fact] public void A_corner_drag_moves_both_splits() => Assert.Equal(Edges.Right | Edges.Bottom, SettledResize.Classify(Accepted, new(0, 0, 700, 400), byHand: true));
[Fact] public void Two_pixels_of_drift_on_the_opposite_edge_still_count() => Assert.Equal(Edges.Right, SettledResize.Classify(Accepted, new(2, 1, 700, 599), byHand: true));
[Fact] public void Both_sides_of_an_axis_moving_is_a_move() => Assert.Equal(Edges.None, SettledResize.Classify(Accepted, new(-50, 0, 700, 600), byHand: true));

// SplitMove: area 0,0,1000,600, gap 0, two columns [0.5,0.5] of one row each
[Fact] public void Dragging_the_left_columns_right_edge_moves_the_split() =>
    AssertClose([0.7, 0.3], Apply(cell: (0, 0), Edges.Right, frame: new(0, 0, 700, 600)).Value.Columns);
[Fact] public void Dragging_the_right_columns_left_edge_moves_the_same_split() =>
    AssertClose([0.6, 0.4], Apply(cell: (1, 0), Edges.Left, frame: new(600, 0, 1000, 600)).Value.Columns);
[Fact] public void The_split_stops_at_ten_percent() =>
    AssertClose([0.9, 0.1], Apply(cell: (0, 0), Edges.Right, frame: new(0, 0, 990, 600)).Value.Columns);
[Fact] public void An_outer_edge_has_no_split() => Assert.True(Apply(cell: (0, 0), Edges.Left, frame: new(50, 0, 500, 600)).HasNoValue);
[Fact] public void A_row_split_moves_within_its_column() =>     // column 1 has two rows; row 0's bottom released at 200
    AssertClose([1.0 / 3, 2.0 / 3], ApplyRows(cell: (1, 0), Edges.Bottom, frame: new(500, 0, 1000, 200)).Value.Rows[1]);
[Fact] public void With_a_gap_the_gap_stays_exact() =>   // gap 8: tiles end at the split minus 4 and start at the split plus 4
    Assert.Equal(8, GapBetweenColumnsAfter(Apply(cell: (0, 0), Edges.Right, frame: new(8, 8, 696, 592), gap: 8).Value));
```

- [ ] **Step 2: Run them and see them fail.** `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~SettledResize|FullyQualifiedName~SplitMove"`.
- [ ] **Step 3: Implement.** The split's position is the moved edge plus half the gap toward the neighbour, as a fraction of the inner span (the area less the gap on both sides); clamp both sides to at least 0.1 of the inner span. A corner moves the column split and that column's row split.
- [ ] **Step 4: Run them and see them pass,** then the Core suite.
- [ ] **Step 5: Commit** `feat: tell a resize by hand from a move, and move a split`.

---

### Task 3: Stored sizes, resetting them, and `even` on the pipe

**Files:**
- Modify: `src/Deskbar.Core/Domain/Workspace.cs`, `src/Deskbar.Core/WorkspaceManager.cs` (beside `SetWorkspaceTiled`), `src/Deskbar.Core/Control/ControlUsage.cs`, `src/Deskbar.Core/Control/RemoteControl.cs`, `src/Deskbar.Core/Control/RemoteControl.Workspaces.cs`
- Test: `tests/Deskbar.Core.Tests/Tiling/TileSizesSettingTests.cs`, `tests/Deskbar.Core.Tests/Control/RemoteControlEvenTests.cs`

**Interfaces:**
- Consumes: `TileSizes` (Task 1).
- Produces:
  - `public sealed record ScreenTileSizes(string Screen, TileSizes Sizes);` and `Workspace.TileSizes : IReadOnlyList<ScreenTileSizes> { get; init; } = []` (a state.json without it loads empty).
  - `WorkspaceManager.SetTileSizes(Guid id, string screen, TileSizes sizes) : Result` (replaces that screen's entry; equal sizes remove it), `WorkspaceManager.EvenOutTiles(Guid id) : Result` (clears the list), and `SetWorkspaceTiled(id, false)` also clears it.
  - Pipe: `new("even", "<workspace>", "Even out tiles: equal columns and rows again")`, dispatched next to `tile`/`untile`, refused with the same two messages and in the same order as `tile`; on a workspace with no stored sizes (tiled or not) it succeeds and changes nothing.

- [ ] **Step 1: Write the failing tests**
`Sizes_persist_per_screen` (round trip through `FakeStore`), `A_state_file_without_sizes_loads_empty` (real JSON through `JsonPersistenceStore`), `Setting_equal_sizes_removes_the_entry`, `Evening_out_clears_the_sizes`, `Untiling_forgets_the_sizes`, `Turning_the_experiment_off_keeps_the_sizes`; pipe: `Even_clears_the_sizes_in_pro`, `Even_refuses_while_the_experiment_is_off`, `Even_refuses_in_the_free_tier`, `Even_on_a_workspace_with_no_sizes_succeeds`, plus the existing every-verb-in-`ControlUsage` test still passing.
- [ ] **Step 2: Run them and see them fail.** `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~TileSizesSetting|FullyQualifiedName~RemoteControlEven"`.
- [ ] **Step 3: Implement,** following `SetWorkspaceTiled` and `SetRowTiled`.
- [ ] **Step 4: Run them and see them pass,** then the Core suite.
- [ ] **Step 5: Commit** `feat: tile sizes are stored per workspace and screen, and even resets them`.

---

### Task 4: The surface reports a settle by hand and each screen's bounds

**Files:**
- Modify: `src/Deskbar.Core/Abstractions/ITileSurface.cs`, `src/Deskbar.Windows/Tiling/TileSurface.cs`, `tests/Deskbar.Core.Tests/Fakes.cs` (`FakeTileSurface`)
- Test: `tests/Deskbar.Windows.Tests/Tiling/TileSurfaceTests.cs` (add)

**Interfaces:**
- Produces:
  - `TileScreen` gains a last positional parameter `WindowRect Bounds` (the monitor rectangle, `rcMonitor`). Every existing construction of `TileScreen` is updated; the fake defaults it to the work area.
  - `ITileSurface.ByHand(WindowHandle window) : bool`: whether a mouse button was down at any location change of the change that last settled for this window. It is valid while `Settled` for that window is being handled, and resets when the window's next change starts.
  - `FakeTileSurface` gains a settable per-window `ByHand` answer.

- [ ] **Step 1: Write the failing tests**
`A_change_with_a_button_down_settles_by_hand` (the test's `ButtonsDown` seam is true during one nudge, false at the quiet tick: `ByHand` is true when `Settled` fires), `A_change_without_a_button_does_not` (false throughout), `Screens_carry_their_monitor_bounds` (each `TileScreen.Bounds` contains its `WorkArea`).
- [ ] **Step 2: Run them and see them fail.** `dotnet test tests/Deskbar.Windows.Tests --filter "FullyQualifiedName~TileSurface"`.
- [ ] **Step 3: Implement.** Record "button seen" per window in the location callback's restart path (after its existing filter, so the callback stays cheap); keep it through the quiet tick and the `Settled` emission; clear it when a new change starts for that window.
- [ ] **Step 4: Run them and see them pass,** then `dotnet test Deskbar.sln --filter "Category!=Integration"` (the `TileScreen` change touches both test projects).
- [ ] **Step 5: Commit** `feat: the tile surface says when a window was resized by hand, and where each screen is`.

---

### Task 5: The Tiler moves splits and keeps sizes

**Files:**
- Create: `src/Deskbar.Core/Tiling/Tiler.Resize.cs` (the `Tiler` becomes `partial`)
- Modify: `src/Deskbar.Core/Tiling/Tiler.cs` (only the seams named below)
- Test: `tests/Deskbar.Core.Tests/Tiling/TilerResizeTests.cs`

**Interfaces:**
- Consumes: Tasks 1 to 4.
- Produces: no new public members.

Wiring, in `Tiler.Resize.cs`, called from three seams in `Tiler.cs`:
1. `SlotsOf` asks for the screen's sizes: the workspace's `ScreenTileSizes` whose `Screen` equals the screen's bounds key (`"L,T,R,B"`), fitted to `GridLayout.Shape(count)` with the screen's pending leaver, if any; equal when there is none. If the fitted sizes differ from the stored ones, store them with `SetTileSizes` (the `StateKey` early return already keeps that pulse from re-evaluating).
2. Where a window leaves a board on a screen (closing, minimizing, moving away, leaving by `WindowMoved`), remember its cell, from `GridLayout.CellOf` and the board order before it left, as that screen's pending leaver; it is used by the next fit of that screen and then cleared.
3. In `OnSettled`, before `SettledMove.Decide`, for a window with an accepted frame: `SettledResize.Classify(given.Accepted, frame, surface.ByHand(window))`. When it returns edges, `SplitMove.Apply` with the window's cell; `None` means put it back (`PutBack`); a value is stored with `SetTileSizes` and that screen is re-tiled. When it returns `Edges.None`, the existing move rules run unchanged.

- [ ] **Step 1: Write the failing tests** (built like `TilerTests`, with `FakeTileSurface.ByHand`)
`A_hand_resize_of_a_separation_moves_the_split_and_retiles` (2 windows: the left one's right edge settles at 0.7 by hand; both placed at the new split; sizes stored), `A_corner_resize_moves_both_splits_and_retiles`, `An_app_resizing_itself_snaps_back_and_moves_no_split` (ByHand false), `An_outer_edge_resize_snaps_back`, `A_new_window_keeps_the_column_widths` (third window joins after a split move: column fractions unchanged), `Closing_a_middle_window_keeps_the_other_rows_proportions`, `A_screen_that_returns_with_the_same_bounds_gets_its_sizes_back` (ScreensChanged drops the screen, then brings it back), `Evening_out_retiles_to_equal`, `Nothing_moves_a_split_while_the_experiment_is_off`, `A_drop_pending_window_does_not_move_a_split`.
- [ ] **Step 2: Run them and see them fail.** `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~TilerResize"`.
- [ ] **Step 3: Implement.** Keep every existing `TilerTests` test green; the equal-sizes path must place exactly as before.
- [ ] **Step 4: Run them and see them pass,** then the Core suite.
- [ ] **Step 5: Commit** `feat: dragging a separation moves the split, and the sizes stick`.

---

### Task 6: "Even out tiles" on the row menu

**Files:**
- Modify: `src/Deskbar.App/FloatingBar.Tiling.cs`, `README.md` (append "and drag a separation to resize" to the tiling line)
- Test: `tests/Deskbar.Windows.Tests/FloatingBarTilingMenuTests.cs` (add)

**Interfaces:**
- Consumes: `WorkspaceManager.EvenOutTiles` (Task 3).

- [ ] **Step 1: Write the failing tests**
`Even_out_is_offered_once_a_split_has_moved` (experiment on, Pro, tiled, a non-equal entry), `Even_out_is_not_offered_with_equal_tiles`, `Even_out_is_not_offered_while_the_experiment_is_off`, `Even_out_clears_the_sizes_when_clicked`.
- [ ] **Step 2: Run them and see them fail.** `dotnet test tests/Deskbar.Windows.Tests --filter "FullyQualifiedName~FloatingBarTilingMenu"`.
- [ ] **Step 3: Implement.** The item sits directly under "Tile windows", reads state at click time, and reports through `Report` like its neighbours.
- [ ] **Step 4:** `dotnet build Deskbar.sln -c Release -warnaserror` and `dotnet test Deskbar.sln --filter "Category!=Integration"`.
- [ ] **Step 5: Commit** `feat: even out tiles from the row menu`. No live check and no PR here: the controller runs the live check with Petre (it needs his Store copy exited, as last time) and opens the PR, stacked on Deskbar #337, with release notes.
