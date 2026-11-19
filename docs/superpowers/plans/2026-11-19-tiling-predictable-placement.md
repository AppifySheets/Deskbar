# Tiling: predictable placement on open and close, implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A closing tile never moves tiles in other columns, a new window splits the tile you last used, and a per-workspace switch can keep a closed tile's space empty for the next window.

**Architecture:** The arithmetic goes in pure classes (`TileArrange`, a new `TileArrival`) with their own tests. The Tiler decides when: `SizesOn` removes only the leaver's cell, `Evaluate` and `OnWindowMoved` send arrivals through one `Arrived` step (opening, then split, then fallback split), and the leaver bookkeeping learns whether the window closed. The setting is a `Workspace` flag with a manager setter, a pipe twin and two menu items.

**Tech Stack:** C# / .NET 10, WPF, xUnit, CSharpFunctionalExtensions, System.Reactive.

**Spec:** `docs/superpowers/specs/2026-11-19-tiling-predictable-placement-design.md`

**Worktree:** `E:\repos\TaskSpaces-tiling-placement`, branch `feat/tiling-predictable-placement`. Every path below is relative to it. Petre asked for a worktree here, overriding the repo's "no worktrees" rule for this work.

## Global Constraints

- Petre's C# rules: `var`, expression bodies for one-liners, lambdas over loops, readonly collections, immutable records, `private` implied, `Result`/`Maybe` from CSharpFunctionalExtensions for expected failures.
- Long intent comments on every new member, as the surrounding tiling code has; quote Petre where a rule comes from him.
- Floor: both halves of a split at least a quarter (0.25) of the screen's width share and a quarter of its height share. Compare fractions from `TileSizes`, never pixels: pixels lose the gaps and a 2x2 tile would fail its own quarter.
- Largest tile: by `Columns[c] * Rows[c][r]`; on a tie, the one latest in slot order.
- Side: `Right` when the tile is wider than tall (pixels from its slot) AND alone in its column, otherwise `Bottom`.
- Every board change still goes through `Keep`; every size the Tiler stores goes through `StoreSizes`.
- Trace one line per arrival decision and per kept space, starting `tile:` like the others.
- Commit messages end with a blank line and the line `*opus5.5*`, nothing else. No Co-Authored-By trailer.
- Routine test command: `dotnet test Deskbar.sln --filter "Category!=Integration"`. Never run the integration tests.

## Review Focus

1. A new window that opens on screen 2 while the last-used tile is on screen 1: it lands in the split on screen 1. Test in Task 3.
2. Windows hands focus to another tile after a close, with the `Activated` event arriving either BEFORE or AFTER the `Disappeared`: either way that tile is not the target. Two tests in Task 3.
3. A window that opens maximized on a tiled workspace still gets un-maximized into its new split, not the end slot. Test in Task 3.
4. "Keep the space" with the last tile on a screen closing: no opening is stored for an empty screen, nothing throws. Test in Task 5.
5. A tile that hides to the tray with "keep the space" on and then shows again: it fills the opening it left. Test in Task 5.

---

### Task 1: A leaving window takes only its own cell

**Files:**
- Modify: `src/Deskbar.Core/Tiling/TileArrange.cs` (`Refit`, lines 61-81)
- Modify: `src/Deskbar.Core/Tiling/Tiler.Resize.cs` (`SizesOn`, lines 102-127)
- Test: `tests/Deskbar.Core.Tests/Tiling/TileArrangeTests.cs`, `tests/Deskbar.Core.Tests/Tiling/TilerTests.cs`

**Interfaces:**
- Produces: `TileArrange.Refit` removes the leaver's cell on ANY shape when exactly one window left and its cell is known. `SizesOn` does the same for a screen with nothing stored, starting from `TileSizes.Equal(GridLayout.Shape(count + 1))`.

- [ ] **Step 1: Write the failing pure tests** in `TileArrangeTests.cs`

```csharp
[Fact]
public void A_leaver_on_the_automatic_shape_takes_only_its_own_cell()
{
    var refit = TileArrange.Refit(TileSizes.Equal([2, 2]), 3, Maybe.From((Column: 1, Row: 1)));
    Assert.Equal([2, 1], refit.Shape);
    Assert.Equal(new TileSizes([0.5, 0.5], [[0.5, 0.5], [1.0]]), refit);
}

[Fact]
public void A_leaver_alone_in_its_column_takes_the_column_and_the_rest_keep_their_rows()
{
    var refit = TileArrange.Refit(TileSizes.Equal([1, 2]), 2, Maybe.From((Column: 0, Row: 0)));
    Assert.Equal(new TileSizes([1.0], [[0.5, 0.5]]), refit);
}
```

- [ ] **Step 2: Run them, expect FAIL** (today the automatic shape refits to `GridLayout.Shape(3)` = `[1, 2]`)

Run: `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~TileArrangeTests"`

- [ ] **Step 3: In `Refit`, move the `have == count + 1 && leaving.HasValue && Holds(...)` → `RemoveCell` branch above the `!IsCustom(stored)` branch.** Update the method comment: the shape survives a leave on every shape now, and why (Petre's "on closing, i sometimes want not to move other windows").

- [ ] **Step 4: Write the failing Tiler test** in `TilerTests.cs`

```csharp
// Petre: "on closing, i sometimes want not to move other windows". In a 2x2, closing the bottom-right tile used to
// rebuild the grid as 1+2 and move the bottom-left tile into the right column.
[Fact]
public void Closing_a_tile_keeps_the_other_tiles_in_their_columns()
{
    Enumerable.Range(1, 4).ToList().ForEach(n => Exists(W(n), alpha));
    Start();
    Closes(W(4));
    Assert.Equal([W(1), W(2), W(3)], tiler.Board.On(1));
    var expected = GridLayout.Arrange(Area, 3, Gap, new TileSizes([0.5, 0.5], [[0.5, 0.5], [1.0]]));
    Assert.Equal(expected, new[] { W(1), W(2), W(3) }.Select(w => surface.Frames[w]).ToList());
}
```

- [ ] **Step 5: Run it, expect FAIL.**

- [ ] **Step 6: In `SizesOn`, when nothing is stored and a leaver is pending, fit `TileArrange.Refit(TileSizes.Equal(GridLayout.Shape(count + 1)), count, leaving)` and store it unless plain**, with the same trace line as the stored branch. With no leaver, nothing stored still means `TileSizes.Equal(shape)` as today.

- [ ] **Step 7: Run the whole Core suite.** Existing tests whose expectation encodes the old count-based rebuild after a leave will fail. Rewrite each to the new rule (only the leaver's cell goes) with a one-line comment naming this change. Any other failure is a bug in this task.

Run: `dotnet test tests/Deskbar.Core.Tests --filter "Category!=Integration"`. Expected: all pass.

- [ ] **Step 8: Commit** `feat: a closing tile takes only its own cell, the others keep their columns`

---

### Task 2: Which tile a new window splits (pure)

**Files:**
- Create: `src/Deskbar.Core/Tiling/TileArrival.cs`
- Test: `tests/Deskbar.Core.Tests/Tiling/TileArrivalTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record TileSplit(WindowHandle Target, TileArrange.Zone Side);`
  - `public static Maybe<TileSplit> TileArrival.Choose(IReadOnlyList<WindowHandle> order, TileSizes sizes, IReadOnlyList<WindowRect> tiles, Maybe<WindowHandle> lastUsed)`. `tiles` is aligned with `order` (the slots `SlotsOf` lays out). None only when `order` is empty. `Side` is only ever `Right` or `Bottom`.

- [ ] **Step 1: Write the failing tests.** Use 1920x1040 tiles built with `GridLayout.Arrange(new WindowRect(0, 0, 1920, 1040), n, 0, sizes)`.

```csharp
[Fact] public void One_wide_tile_is_split_to_its_right()
    // order [1], Equal([1]) → Choose(..., Maybe.From(W(1))) == TileSplit(W(1), Zone.Right)
[Fact] public void A_tall_tile_is_split_below()
    // order [1,2], Equal([1,1]), lastUsed W(1) → TileSplit(W(1), Zone.Bottom)
[Fact] public void A_wide_tile_in_a_column_of_several_rows_is_split_below()
    // order [1,2], sizes ([1.0], [[0.5,0.5]]) (two wide rows), lastUsed W(2) → TileSplit(W(2), Zone.Bottom)
[Fact] public void A_last_used_tile_below_the_floor_hands_the_split_to_the_largest()
    // order [1,2,3], sizes ([0.5,0.5], [[1.0],[0.75,0.25]]), lastUsed W(3) (its row 0.25 halves to 0.125)
    // → TileSplit(W(1), Zone.Bottom)
[Fact] public void With_no_last_used_tile_the_largest_is_split()
    // order [1,2,3], sizes ([0.5,0.5], [[1.0],[0.5,0.5]]), None → TileSplit(W(1), Zone.Bottom)
[Fact] public void A_tie_goes_to_the_tile_latest_in_slot_order()
    // order [1,2], Equal([1,1]), None → TileSplit(W(2), Zone.Bottom)
[Fact] public void When_every_tile_is_below_the_floor_the_largest_is_still_split()
    // order [1..6], sizes ([0.5,0.5], [[0.3,0.3,0.4],[0.3,0.3,0.4]]) (no row halves to 0.25), None
    // → W3 and W6 tie at 0.2, the latest wins → TileSplit(W(6), Zone.Bottom)
[Fact] public void A_last_used_window_not_on_this_screen_is_ignored()
    // order [1,2], Equal([1,1]), lastUsed W(9) → TileSplit(W(2), Zone.Bottom)
[Fact] public void Nothing_to_split_on_an_empty_screen()
    // order [], Equal([]), Maybe.From(W(1)) → Maybe.None
```

Write the exact `Assert.Equal(Maybe.From(new TileSplit(...)), result)` for each, with the values in the comments.

- [ ] **Step 2: Run, expect FAIL** (type not defined).

Run: `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~TileArrivalTests"`

- [ ] **Step 3: Implement `TileArrival.Choose`** with the Global Constraints' floor, largest and side rules. Cells come from `TileArrange.CellOf(sizes, slot, order.Count)`. A tile passes the floor for `Right` when `Columns[c] / 2 >= 0.25 - 1e-9` and `Rows[c][r] >= 0.25 - 1e-9`, and for `Bottom` when `Columns[c] >= 0.25 - 1e-9` and `Rows[c][r] / 2 >= 0.25 - 1e-9`. The last-used tile is chosen when it is in `order` and passes the floor for its side; otherwise the largest. Header comment: the spec's "Change 2" and the reason fractions are used, not pixels.

- [ ] **Step 4: Run, expect PASS.**

- [ ] **Step 5: Commit** `feat: the rule for which tile a new window splits`

---

### Task 3: A new window splits the tile you last used

**Files:**
- Create: `src/Deskbar.Core/Tiling/Tiler.Arrival.cs` (partial Tiler)
- Modify: `src/Deskbar.Core/Tiling/Tiler.cs` (`OnWindowEvent`, `Evaluate`, `OnWindowMoved`)
- Modify: `src/Deskbar.Core/Tiling/Tiler.Resize.cs` (`NoteLeavers` calls `NoteLeft`)
- Modify: `src/Deskbar.Core/Abstractions/ITileSurface.cs`, `src/Deskbar.Windows/Tiling/TileSurface.cs`, `src/Deskbar.Windows/Monitoring/NativeMethods.cs`
- Modify: `tests/Deskbar.Core.Tests/Fakes.cs` (`FakeTileSurface`)
- Modify: `tests/Deskbar.Core.Tests/Tiling/TilerTests.cs` (make the class `partial`)
- Test: `tests/Deskbar.Core.Tests/Tiling/TilerTests.Arrival.cs` (new partial of `TilerTests`, reusing its harness)

**Interfaces:**
- Consumes: `TileArrival.Choose`, `TileSplit` (Task 2); `TileArrange.Drop`, `TileArrange.Filled`.
- Produces:
  - `uint ITileSurface.LastInputTick()`: `GetLastInputInfo`'s `dwTime`. The fake exposes `public uint InputTick { get; set; }`.
  - In `Tiler.Arrival.cs`: `void NoteUsed(WindowHandle window)`, `void NoteLeft()`, `Maybe<WindowHandle> LastUsed(TileBoard board, WindowHandle except)`, `TileBoard Arrived(Workspace workspace, TileBoard board, IReadOnlyList<WindowHandle> arrivals)`.

- [ ] **Step 1: Add `LastInputTick` to `ITileSurface`** with a comment saying what it is for (telling a user's activation from one Windows hands out after a close). Implement it in `TileSurface` through a `GetLastInputInfo` P/Invoke and a `LASTINPUTINFO` struct in `NativeMethods`. Add `InputTick` to `FakeTileSurface`. Build: `dotnet build Deskbar.sln`.

- [ ] **Step 2: Make `TilerTests` partial and add the helpers to `TilerTests.Arrival.cs`:**

```csharp
// The user activates a window: input happened first, so the tick moves on.
void Uses(WindowHandle h)
{
    surface.InputTick++;
    monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Activated, Info(h)));
}

// Windows activates a window by itself (after a close or minimize): no input since.
void HandedFocus(WindowHandle h) =>
    monitor.Subject.OnNext(new WindowEvent(WindowEventKind.Activated, Info(h)));
```

- [ ] **Step 3: Write the failing Tiler tests.** For each, assert the board order and the frames against `GridLayout.Arrange(Area, n, Gap, <expected sizes>)`.

```csharp
[Fact] public void A_new_window_splits_the_tile_last_used()
    // W1,W2 tiled (two columns). Uses(W(1)). Opens(W(3)). Board [W1,W3,W2]; sizes ([0.5,0.5], [[0.5,0.5],[1.0]]).
[Fact] public void A_new_window_lands_on_the_screen_of_the_tile_last_used()
    // Two screens (add Area2 as screen 2 to surface.ScreenList), W1 on 1, W2 on 2. Uses(W(1)). Opens(W(3), alpha, screen: 2).
    // Board.On(1) == [W1, W3]; Board.On(2) == [W2]. (Review Focus 1)
[Fact] public void The_newcomer_is_the_next_target_once_it_has_joined()
    // W1 alone. Uses(W(1)). Opens(W(2)) → right of W1. Uses(W(2)). Opens(W(3)) → below W2: Board [W1,W2,W3], shape [1,2].
[Fact] public void Focus_Windows_hands_out_after_a_close_does_not_count_when_it_comes_after()
    // W1,W2,W3 tiled as [W1 | W2/W3]. Uses(W(2)). Uses(W(3)). surface.InputTick++ (the click on X). Closes(W(3)).
    // HandedFocus(W(1)). Opens(W(4)). The target is W2, the last tile the user chose: order [W1,W2,W4], W4 below W2.
    // Had the handed focus counted, W4 would be below W1: [W1,W4,W2]. (Review Focus 2)
[Fact] public void Focus_Windows_hands_out_after_a_close_does_not_count_when_it_comes_before()
    // As above, but HandedFocus(W(1)) between the InputTick++ and Closes(W(3)). Same outcome. (Review Focus 2)
[Fact] public void Activating_a_floating_window_keeps_the_target()
    // W1,W2 tiled. Uses(W(1)). Uses(W(9)) where W9 exists but surface.CannotTile contains it. Opens(W(3)) → splits W1.
[Fact] public void A_window_opening_maximized_is_restored_into_its_split()
    // W1 alone, Uses(W(1)). Exists(W(2)); surface.Maximized.Add(W(2)); Appeared. Assert surface.Restored contains W2
    // and Board [W1,W2]; after Settles(W(2), <its slot>) its frame is the right half. (Review Focus 3)
[Fact] public void A_new_window_goes_into_an_opening_before_any_split()
    // Store sizes with an opening on screen 1 (manager.SetTileSizes) for W1,W2; Uses(W(1)); Opens(W(3)) → it fills
    // the opening (order per TileArrange.Filled), and W1's tile is unchanged.
[Fact] public void Several_arrivals_at_once_split_the_target_then_the_largest()
    // W1 on Alpha; switch to Beta; W2,W3 open on Alpha (Exists only); Uses(W(1)) beforehand; SwitchTo(alpha).
    // W2 splits W1 (Right), W3 splits the largest (tie of two halves → latest in order, W2 → Bottom).
[Fact] public void A_window_brought_from_another_workspace_splits_the_tile_last_used()
    // W1,W2 on Alpha; Uses(W(1)); manager moves W3 from Beta to Alpha (as WindowMovedTests do) → Board [W1,W3,W2].
```

- [ ] **Step 4: Run, expect FAIL.**

Run: `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~TilerTests"`

- [ ] **Step 5: Implement `Tiler.Arrival.cs`.**
  - Activation list: `List<(WindowHandle Window, uint Tick)>`, most recent first, at most 32 entries. `NoteUsed` ignores an activation whose `LastInputTick()` equals the tick saved by the last `NoteLeft`; otherwise it moves the window to the front. `NoteLeft` saves `LastInputTick()` and drops every entry carrying that same tick, which covers the activation that arrived before the leave. A `Disappeared` window is removed from the list.
  - `LastUsed(board, except)`: the first listed window on `board`, not `except`, not maximized.
  - `Arrived`: the first arrival is offered `LastUsed`, the rest `None`. Per arrival: the target screen is the last-used tile's screen, or the window's own screen. If the target screen's stored sizes have an opening for its count without the window, fill it there (`TileArrange.Filled`, store, `board.Into`). Otherwise do the same for its own screen's opening. Otherwise call `TileArrival.Choose` on the target screen's order without the window, with its `ShownSizes` and `SlotsOf`. On a split, call `TileArrange.Drop(order, sizes, window, split.Target, split.Side)`, `StoreSizes`, then `board.Into(screen, newOrder)`. On None (empty screen), `board.Join(screen, window)`. Trace each decision: which rule, which tile, which side.
- [ ] **Step 6: Wire it in.**
  - `OnWindowEvent`: call `NoteUsed` on `Activated` before `RaiseTogether`, and remove `Disappeared` windows from the list.
  - `NoteLeavers`: call `NoteLeft()` when any screen lost a window.
  - `Evaluate`: take the arrivals (on the reconciled board, not on `before`, not in `away` as it was before `ReturnedToTheirPlaces`, in board order) and pass the board through `Arrived` before `Keep`. Do this only when `before` is not null, so `FirstFill` is untouched.
  - `OnWindowMoved`: the join path becomes `Keep(workspace, Arrived(workspace, board.Join(to, window), [window]))`, then `Retile` the screens the window left and landed on.

- [ ] **Step 7: Run the whole Core suite.** Tests that open windows one by one and expect the count-based order and grid (from four windows up, the order changes: a fourth window now goes below the largest tile instead of rebuilding the grid as 2x2) fail. Rewrite each to the new rule with a one-line comment. Any other failure is a bug in this task.

Run: `dotnet test tests/Deskbar.Core.Tests --filter "Category!=Integration"`. Expected: all pass.

- [ ] **Step 8: Commit** `feat: a new window splits the tile you last used, on that tile's screen`

---

### Task 4: The "keep the space" setting

**Files:**
- Modify: `src/Deskbar.Core/Domain/Workspace.cs`, `src/Deskbar.Core/WorkspaceManager.cs`
- Modify: `src/Deskbar.Core/Control/RemoteControl.cs`, `src/Deskbar.Core/Control/RemoteControl.Workspaces.cs`, `src/Deskbar.Core/Control/ControlUsage.cs`
- Test: `tests/Deskbar.Core.Tests/Tiling/TiledSettingTests.cs`, `tests/Deskbar.Core.Tests/Control/RemoteControlKeepSpaceTests.cs` (new, shaped like `RemoteControlEvenTests.cs`)

**Interfaces:**
- Produces: `public bool Workspace.TileKeepsSpace { get; init; }` (default false); `public Result WorkspaceManager.SetTileKeepsSpace(Guid id, bool keep)`; pipe command `keep-space <workspace> on|off`.

- [ ] **Step 1: Write the failing tests.**

```csharp
// TiledSettingTests
[Fact] public void Keep_space_is_off_by_default_and_persists_when_turned_on()
    // SetTileKeepsSpace(id, true) succeeds; store.Stored workspace has TileKeepsSpace == true.
[Fact] public void Untiling_does_not_clear_keep_space()
    // SetTileKeepsSpace(true), SetWorkspaceTiled(false) → TileKeepsSpace still true.
[Fact] public void Keep_space_on_a_missing_workspace_fails()

// RemoteControlKeepSpaceTests
[Fact] public void Keep_space_on_turns_it_on()            // reply ok, flag true
[Fact] public void Keep_space_off_turns_it_off()
[Fact] public void Keep_space_is_refused_while_the_experiment_is_off()
    // "Tiling is experimental and turned off. Turn it on in Manage."
[Fact] public void Keep_space_needs_Pro()                // "Tiling needs Deskbar Pro."
[Fact] public void Keep_space_with_another_word_is_a_usage_error()   // `keep-space Alpha maybe` fails as an unknown command does
```

- [ ] **Step 2: Run, expect FAIL.**

- [ ] **Step 3: Implement.** The flag gets a comment saying why it is init-only and false by default (older state.json loads with it off) and that untiling leaves it alone. The pipe handler refuses in the same order and with the same messages as `EvenOutRow`. Add `new("keep-space", "<workspace> on|off", "Keep the space when a tiled window closes, for the next window")` to `ControlUsage` after `even`. The dispatch-coverage test already fails if a command is missing.

- [ ] **Step 4: Run, expect PASS.** `dotnet test tests/Deskbar.Core.Tests --filter "Category!=Integration"`

- [ ] **Step 5: Commit** `feat: a per-workspace switch to keep a closed tile's space, with its pipe twin`

---

### Task 5: A closing tile leaves its space when the switch is on

**Files:**
- Modify: `src/Deskbar.Core/Tiling/TileArrange.cs` (new `KeptOpen`)
- Modify: `src/Deskbar.Core/Tiling/Tiler.cs` (`OnWindowEvent`), `src/Deskbar.Core/Tiling/Tiler.Resize.cs` (`leavers`, `NoteLeavers`, `SizesOn`)
- Test: `tests/Deskbar.Core.Tests/Tiling/TileArrangeTests.cs`, `tests/Deskbar.Core.Tests/Tiling/TilerTests.Arrival.cs`

**Interfaces:**
- Consumes: `Workspace.TileKeepsSpace` (Task 4).
- Produces: `public static TileSizes TileArrange.KeptOpen(TileSizes sizes, (int Column, int Row) cell)`, which returns `sizes with { Opening = <slot of cell> }`. Leaver entries become `(int Column, int Row) Cell, bool Closed`.

- [ ] **Step 1: Write the failing tests.**

```csharp
// TileArrangeTests
[Fact] public void Keeping_a_cell_open_makes_it_the_opening()
    // KeptOpen(TileSizes.Equal([2,2]), (1,1)) == TileSizes.Equal([2,2]) with { Opening = 3 }; HasOpeningFor(3) is true.

// TilerTests.Arrival.cs, all on a 2x2 of W1..W4 with manager.SetTileKeepsSpace(alpha.Id, true)
[Fact] public void A_closing_tile_leaves_its_space_and_nothing_else_moves()
    // Frames of W1..W3 before Closes(W(4)) equal the frames after; stored sizes have Opening == 3.
[Fact] public void The_next_window_fills_the_kept_space()
    // Then Opens(W(5)) → W5's frame is W4's old frame; the stored opening is gone.
[Fact] public void A_tile_hidden_to_the_tray_keeps_its_space_and_fills_it_when_it_shows_again()
    // Hidden event for W(4) → opening; Appeared for W(4) → W4 back in its old frame. (Review Focus 5)
[Fact] public void Minimizing_does_not_keep_the_space()
    // Minimizes(W(4), true) → the Task 1 rule (shape [2,1]), no opening.
[Fact] public void A_second_close_while_a_space_is_kept_closes_up_as_usual()
    // Closes(W(4)) then Closes(W(1)) → opening still at W4's place; W1's cell removed (column 0 is W2 alone).
[Fact] public void The_last_tile_on_a_screen_closing_keeps_nothing()
    // One tile, Closes it → no stored sizes for the screen, no trace line with "threw". (Review Focus 4)
[Fact] public void With_the_switch_off_a_close_closes_up()
    // Same as Task 1's test, switch off: no opening.
```

- [ ] **Step 2: Run, expect FAIL.**

- [ ] **Step 3: Implement.**
  - `OnWindowEvent`: on `Disappeared` or `Hidden` of a window on the board while the acting workspace has `TileKeepsSpace`, add it to a `closing` set before `Evaluate`.
  - `NoteLeavers`: the leaver entry records whether its window is in `closing`, then clears the set.
  - `SizesOn`: for a closed leaver, start from the stored sizes or `TileSizes.Equal(GridLayout.Shape(count + 1))`. When that start has no opening and exactly `count + 1` cells, use `TileArrange.KeptOpen(start, cell)`, store it and trace `space kept`. Otherwise use the Task 1 path; `Refit`'s `count + 2` branch already handles a second close.
  - The kept space takes priority over the plain-sizes rule: a kept opening is never plain, so `SetTileSizes` keeps it.

- [ ] **Step 4: Run the whole Core suite, expect PASS.**

- [ ] **Step 5: Commit** `feat: with keep-the-space on, a closing tile leaves its space for the next window`

---

### Task 6: The switch in the row menu and the header button menu

**Files:**
- Modify: `src/Deskbar.App/FloatingBar.Tiling.cs` (`AddTilingItems`, `FillTileMenu`)
- Test: `tests/Deskbar.Windows.Tests/FloatingBarTilingMenuTests.cs`, `tests/Deskbar.Windows.Tests/FloatingBarTileButtonTests.cs`

**Interfaces:**
- Consumes: `Workspace.TileKeepsSpace`, `WorkspaceManager.SetTileKeepsSpace` (Task 4).
- Produces: a checkable menu item with the header `"Keep the space when a window closes"`, shown only on a tiled workspace with Pro, after "Tile windows" and "Even out tiles".

- [ ] **Step 1: Write the failing tests.**

```csharp
// FloatingBarTilingMenuTests
[Fact] public void Keep_space_is_offered_on_a_tiled_workspace_after_the_tiling_items()
    // headers.IndexOf("Keep the space when a window closes") == last tiling item index + 1
[Fact] public void Keep_space_is_not_offered_on_an_untiled_workspace()
[Fact] public void Keep_space_is_ticked_when_on_and_clicking_flips_it()

// FloatingBarTileButtonTests: the existing "with nothing to even out the menu stays empty" test now expects the
// menu to hold only the keep-space item on a tiled workspace; rename it to say so.
[Fact] public void Right_click_offers_keep_space_on_a_tiled_workspace()
```

- [ ] **Step 2: Run, expect FAIL.** `dotnet test tests/Deskbar.Windows.Tests --filter "FullyQualifiedName~FloatingBarTil"`

- [ ] **Step 3: Implement both items.** Read the flag at click time, as `TiledNow` does, and use `Click`, not `Checked`, for the reason the existing comment gives.

- [ ] **Step 4: Run, expect PASS.**

- [ ] **Step 5: Commit** `feat: keep the space, in the row menu and the tile button's menu`

---

### Task 7: Verify, try it live, open the PR

- [ ] **Step 1:** `dotnet build Deskbar.sln -c Release -warnaserror`. Expected: 0 warnings, 0 errors.
- [ ] **Step 2:** `dotnet test Deskbar.sln --filter "Category!=Integration"`. Expected: all pass.
- [ ] **Step 3: Live check with the trace on** (`%APPDATA%\Deskbar\trace.on`). Swap the running Deskbar for the worktree's Debug build in one stop-build-start command, then:
  - On a tiled workspace with four tiles, close one and check the `tile:` lines and the screen: nothing changes column.
  - Click a tile and open a new window: it splits that tile. Open three more: once the floor is hit, the largest tile is split.
  - Close a tile with the X and open a window: confirm from the trace that the handed focus was ignored (`Activated` before or after the leave).
  - Turn "Keep the space" on, close a tile, open a window: it fills the space.
  - Measure whether moving a window to another desktop outside Deskbar (Win+Tab drag) raises `Hidden`. If it does, a kept space opens for it. Report that to Petre and do not patch it on the spot.
  Then swap the installed build back in.
- [ ] **Step 4:** Push the branch, open the PR against main with a `## Release notes` section written for users (three bullets, one per change) and the italic `*opus5.5*` line at the end. Arm the PR base watch from CLAUDE.md.
