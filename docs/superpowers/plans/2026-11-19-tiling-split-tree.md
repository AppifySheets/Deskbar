# Tiling split tree Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the tiling grid's columns-of-rows model with a split tree, so a window dropped on an edge of any
tile goes beside that tile only, and add screen-edge bands for full-height columns and full-width rows.

**Architecture:** A new pure `TileLayout` (a tree of `TileBranch`/`TileLeaf` nodes plus an optional opening leaf)
replaces `TileSizes` everywhere the Tiler reasons about a screen's layout. Leaves are addressed by LEAF INDEX
(depth-first order, the opening counted), and window slots map to leaves exactly as `TileArrange.CellOf` maps them to
cells today. Tasks 1 to 4 add the new pure code and storage beside the old (everything keeps compiling and passing),
task 5 switches the Tiler over, task 6 adds the band drop, first fill and minimize memory, task 7 does the App, the
pipe, docs and deletes the dead grid code.

**Tech Stack:** C# / .NET 10, WPF (App only), xUnit, CSharpFunctionalExtensions (`Maybe`, `Result`),
System.Text.Json (reflection, `JsonPersistenceStore.Options`), the repo's `ValueList<T>`.

**Spec:** `docs/superpowers/specs/2026-11-19-tiling-split-tree-design.md`

## Global Constraints

- All work in the worktree `E:\repos\TaskSpaces\.claude\worktrees\tile-drop-beside`, branch `fix/tile-drop-beside-stacked`.
- Repo C# style: `private` implied, `var`, expression bodies (`=>`) for one-liners, no braces on single-statement
  ifs, immutable records, readonly collections (`ValueList`, `IReadOnlyList`), lambdas over loops, `Maybe`/`Result`
  for expected failures. Long reasoning comments, as the surrounding tiling code has: say WHY and quote the spec.
- Pure classes stay pure (no Win32, no `WorkspaceManager`) and are tested without Windows.
- Commit messages: plain sentences, no em dashes, and the last line is exactly `*opus5.5*`. No `Co-Authored-By`
  trailer, no "Generated with" line.
- No real people's names anywhere (code, tests, commits).
- Routine test command: `dotnet test Deskbar.sln --filter "Category!=Integration"`. Never run Integration tests.
- Before the final commit of task 7: `dotnet build Deskbar.sln -c Release -warnaserror` must pass.
- Do not edit `version.json` or any version.
- Values pinned by the spec: screen-edge band depth `0.06` of the inner area's width (left/right) or height
  (top/bottom); tile edge zones stay a quarter (`TileArrange.Edge = 0.25`); `SplitMove.MinShare = 0.1`;
  `TileArrange.MinOpening = 0.1`; `TileArrival` floor `0.25`.
- Pipe commands: `even` = equal splits, shape kept; `reset-grid` = back to the automatic grid. Menu labels:
  "Even out tiles", "Reset to grid".

## Review Focus

1. A stored layout from today's build (old `TileSizes` entry) must lay out pixel-identical after upgrade, gaps 0, 7
   and 8 included. Pinned in task 1 (`Migrated_grids_lay_out_pixel_identical`) and task 4 (load migration test).
2. Petre's exact case, end to end through the Tiler: three windows `Across[Down[A,B],C]`, C dropped on A's right
   quarter gives `Down[Across[A,C],B]`, B full width. Pinned in task 3 (pure) and task 5 (Tiler).
3. Restart with a hand-made shape: windows keep their places (no B/C swap). Pinned in task 6 (`FirstFill` overlap).
4. Normal form never breaks: no single-child branch, no same-axis nesting, shares sum to 1 after every operation.
   Pinned in task 2 by an invariant check run on every edit test's result.
5. An older build reading the new state.json does not crash: the old `TileSizes` property is written empty. Pinned
   in task 4 (`Saved_state_keeps_the_old_property_empty`).

---

### Task 1: TileLayout model, normal form, layout arithmetic, JSON

**Files:**
- Create: `src/Deskbar.Core/Tiling/TileLayout.cs`
- Modify: `src/Deskbar.Core/Tiling/GridLayout.cs` (add `TileLayout` overloads; keep the `TileSizes` ones until task 7)
- Test: `tests/Deskbar.Core.Tests/Tiling/TileLayoutTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public enum Axis { Across, Down }   // Across: children side by side, left to right; Down: stacked, top to bottom

  [JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
  [JsonDerivedType(typeof(TileLeaf), "leaf")]
  [JsonDerivedType(typeof(TileBranch), "split")]
  public abstract record TileNode;
  public sealed record TileLeaf : TileNode { public static TileLeaf Instance { get; } = new(); }
  public sealed record TileBranch(Axis Axis, ValueList<TileChild> Children) : TileNode;
  public sealed record TileChild(TileNode Node, double Share);

  public sealed record TileLayout(TileNode Root)
  {
      public int? Opening { get; init; }                 // leaf index of the empty leaf, null for none
      public int Count { get; }                           // number of leaves, the opening counted
      public static TileLayout Automatic(int count);      // GridLayout.Shape(count) as a tree, even shares
      public static TileLayout FromGrid(TileSizes grid);  // migration: Across of Down columns, normal form, Opening kept
      public bool HasOpeningFor(int windows);             // Opening set and Count == windows + 1
      public bool IsEven { get; }                          // every branch's shares are 1/n (1e-9)
      public TileLayout Evened();                          // same structure, every branch even, Opening kept
      public bool SameShapeAs(TileLayout other);           // structure equal, shares ignored
      public (double Width, double Height) ShareOf(int leaf); // share of the inner area on each axis
      public static TileLayout Normal(TileNode root, int? opening); // the only constructor path for edited trees
  }
  // GridLayout additions:
  public static IReadOnlyList<WindowRect> Arrange(WindowRect area, int count, int gap, TileLayout layout);
  public static Maybe<WindowRect> OpeningOf(WindowRect area, int count, int gap, TileLayout layout);
  public static IReadOnlyList<WindowRect> CellsOf(WindowRect area, int gap, TileLayout layout); // every leaf, opening included
  ```
  `Arrange` returns `layout.Count - 1` rects (opening left out) when `layout.HasOpeningFor(count)`, otherwise
  `Count` rects; callers pass a layout whose `Count` matches (task 5 guarantees it).

- [ ] **Step 1: Write the failing tests** in `TileLayoutTests.cs`:
  - `Automatic_matches_the_grid_shape`: for counts 1..9, `GridLayout.Arrange(area, n, gap, TileLayout.Automatic(n))`
    equals `GridLayout.Arrange(area, n, gap)` for `area = new(0,0,1000,600)` and `(-1920, 0, 0, 1040)`, gaps 0, 7, 8.
  - `Migrated_grids_lay_out_pixel_identical`: for shapes `[1]`, `[2]`, `[1,1]`, `[1,2]`, `[2,1]`, `[2,2]`,
    `[1,2,2]`, `[3,3,3]` with uneven sizes (e.g. columns `[0.3,0.7]`, rows `[0.25,0.75]`, built by hand per shape),
    `Arrange(area, n, gap, TileLayout.FromGrid(sizes))` equals `Arrange(area, n, gap, sizes)`, gaps 0, 7, 8;
    and with an `Opening`, `OpeningOf` agrees too.
  - `Normal_form_collapses_single_children_and_merges_same_axis`: `Normal(Across[ Across[L 0.5, L 0.5] 0.5, L 0.5 ])`
    is `Across[L .25, L .25, L .5]`; `Normal(Down[ Across[L,L] 1.0 ])` is `Across[L .5, L .5]`.
  - `Petre_layout_cells`: `Down[Across[A .5, C .5] .5, B .5]` on `(0,0,1000,600)` gap 0 gives
    `(0,0,500,300)`, `(500,0,1000,300)`, `(0,300,1000,600)` in that order.
  - `ShareOf_multiplies_along_the_path`: in `Down[Across[L .5, L .5] .5, L .5]` leaf 1 is `(0.5, 0.5)`, leaf 2 `(1.0, 0.5)`.
  - `Evened_keeps_structure`, `SameShapeAs_ignores_shares`.
  - `Json_round_trips`: serialize/deserialize a `TileLayout` with `JsonPersistenceStore`'s options shape
    (`new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } }`); equal after.
- [ ] **Step 2: Run** `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~TileLayoutTests"`. Expected: build FAIL (types missing).
- [ ] **Step 3: Implement** `TileLayout.cs` and the `GridLayout` overloads. Layout is recursive: the root gets
  `InnerArea(area, gap)`; a branch divides its rect's width (Across) or height (Down) with the existing private
  `Edges(span, shares)` (make it `internal` or move it; keep its arithmetic byte for byte); a leaf's rect is inset with
  `HalfGaps` on every edge that is not on the inner area's border (`rect.Left > inner.Left` and so on). Equality:
  `ValueList` gives `TileBranch` value equality; `TileLayout` equality is `Root` and `Opening`. Leaf order is
  depth-first, children in order. `Automatic(n)`: `Across` of columns per `GridLayout.Shape(n)`, a one-row column is a
  leaf, then `Normal` (one column becomes a `Down` root, one window a lone leaf).
- [ ] **Step 4: Run** the same command. Expected: PASS. Then the whole routine command: PASS.
- [ ] **Step 5: Commit** `feat: a split tree layout for tiling, beside the grid` (body says it lays out pixel-identical
  to the grid for migrated sizes), last line `*opus5.5*`.

### Task 2: Tree edits: remove, insert beside, insert at the screen edge

**Files:**
- Modify: `src/Deskbar.Core/Tiling/TileLayout.cs`
- Test: `tests/Deskbar.Core.Tests/Tiling/TileLayoutEditTests.cs`

**Interfaces:**
- Consumes: task 1's types.
- Produces (on `TileLayout`; `Side` is `TileArrange.Zone` minus `Middle`, passed as `TileArrange.Zone`):
  ```csharp
  public TileLayout Remove(int leaf);  // share to its siblings in proportion; one child left replaces the branch; Opening fixed up
  public (TileLayout Layout, int Leaf) Insert(int target, TileArrange.Zone side, double share = 0.5);
  public (TileLayout Layout, int Leaf) InsertAtEdge(TileArrange.Zone side);
  ```
  Implement the leaf insert on top of an internal "split this NODE on this side" helper; task 3's `Returned` needs
  the same operation on a whole subtree.
  `Insert`: when the target leaf's parent splits along the side's axis (Left/Right = Across, Top/Bottom = Down), the
  new leaf becomes its sibling on that side and takes `share` of the target's share (the target keeps the rest);
  otherwise the target leaf becomes a branch of that axis holding target `1 - share` and new `share`, in side order.
  A lone-leaf root is the second case. `InsertAtEdge`: when the root splits along the side's axis, the new leaf is
  the first (Left/Top) or last (Right/Bottom) child and takes half of the outer child's share; otherwise the root
  becomes a branch of that axis holding the old root and the new leaf, 0.5 each. Both return the new leaf's index.
  Opening bookkeeping: an insert at index `i` moves an opening at `>= i` up one; removing leaf `i` clears an opening
  at `i` and moves one above `i` down one. Every result goes through `Normal`.

- [ ] **Step 1: Write the failing tests.** A helper `AssertNormal(TileLayout)` (no branch with fewer than two
  children, no child branch with its parent's axis, every branch's shares sum to 1 within 1e-9) runs on every result.
  - `Lifting_the_right_column_leaves_the_stack`: `Across[Down[A,B] .5, C .5].Remove(2)` is `Down[L .5, L .5]`.
  - `Petre_case_dropping_beside_the_top_tile`: from `Down[L .5, L .5]`, `Insert(0, Right)` gives
    `Down[Across[L .5, L .5] .5, L .5]`, new leaf 1.
  - `Beside_the_bottom_tile_is_the_mirror`: `Insert(1, Right)` gives `Down[L .5, Across[L .5, L .5] .5]`, leaf 2.
  - `Inserting_along_the_parent_axis_halves_only_the_target`: `Across[L .25, L .75].Insert(1, Left)` is
    `Across[L .25, L .375, L .375]`, leaf 1.
  - `A_lone_leaf_splits`: `new TileLayout(TileLeaf.Instance).Insert(0, Bottom)` is `Down[L .5, L .5]`, leaf 1.
  - `Removing_shares_in_proportion`: `Across[L .2, L .3, L .5].Remove(0)` is `Across[L .375, L .625]`.
  - `Removing_collapses_and_merges`: `Across[L .5, Down[L, Across[L,L]] .5]` removing leaf 1 gives an `Across` of
    three after merge, shares `.5, .25, .25`.
  - `Edge_band_on_a_matching_root_halves_the_outer_child`: `Across[L .5, L .5].InsertAtEdge(Right)` is
    `Across[L .5, L .25, L .25]`, leaf 2.
  - `Edge_band_on_a_crossing_root_wraps_it`: `Down[L,L].InsertAtEdge(Right)` is `Across[Down[L,L] .5, L .5]`, leaf 2;
    `InsertAtEdge(Left)` gives leaf 0 and the old leaves become 1 and 2.
  - `Opening_moves_with_inserts_and_removes` and `Removing_the_opening_clears_it`.
- [ ] **Step 2: Run** `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~TileLayoutEditTests"`. Expected: FAIL.
- [ ] **Step 3: Implement** the three methods.
- [ ] **Step 4: Run** it, then the routine command. Expected: PASS.
- [ ] **Step 5: Commit** `feat: split tree edits for drops, leavers and screen-edge bands`, `*opus5.5*`.

### Task 3: TileArrange, SplitMove and TileArrival on the tree

All new methods are OVERLOADS beside today's `TileSizes` ones (same names, `TileLayout` and leaf-index parameters),
so the Tiler keeps compiling until task 5. Port each existing test file's cases to new files against the overloads;
where this design changes behaviour on purpose, the new test asserts the new rule and its name says so.

**Files:**
- Modify: `src/Deskbar.Core/Tiling/TileArrange.cs`, `SplitMove.cs`, `TileArrival.cs`
- Test: `tests/Deskbar.Core.Tests/Tiling/TileArrangeLayoutTests.cs`, `SplitMoveLayoutTests.cs`, `TileArrivalLayoutTests.cs`

**Interfaces:**
- Consumes: tasks 1 and 2.
- Produces (static, in `TileArrange` unless noted):
  ```csharp
  public static int LeafOf(TileLayout layout, int slot, int count);       // slot >= Opening ? slot + 1 : slot when HasOpeningFor(count), else slot
  public static bool IsCustom(TileLayout layout);                          // !SameShapeAs(Automatic(window count))
  public static bool IsPlain(TileLayout layout);                           // IsEven && !IsCustom && Opening is null
  public static TileLayout Refit(TileLayout stored, int count, Maybe<int> leavingLeaf);
  public static (TileLayout Start, Maybe<int> Leaving) FitStart(Maybe<TileLayout> stored, int count, Maybe<int> leaf, int oldCount);
  public static (IReadOnlyList<WindowHandle> Order, TileLayout Layout) Drop(IReadOnlyList<WindowHandle> order, TileLayout layout, WindowHandle dragged, WindowHandle target, Zone zone);
  public static (IReadOnlyList<WindowHandle> Order, TileLayout Layout) DropAtEdge(IReadOnlyList<WindowHandle> order, TileLayout layout, WindowHandle dragged, Zone side);
  public static Maybe<Zone> ScreenBand(WindowRect inner, int x, int y);   // const Band = 0.06; nearer edge wins in a corner
  public static Maybe<TileLayout> Open(TileLayout layout, int count, WindowRect area, int gap, int leaf, Edges moved, WindowRect frame);
  public static bool Closes(TileLayout layout, int count, WindowRect area, int gap, int leaf, Edges moved, WindowRect frame);
  public static TileLayout KeptOpen(TileLayout layout, int leaf);
  public static TileLayout Closed(TileLayout layout);
  public static (IReadOnlyList<WindowHandle> Order, TileLayout Layout) Filled(IReadOnlyList<WindowHandle> order, TileLayout layout, WindowHandle newcomer);
  public static (IReadOnlyList<WindowHandle> Order, TileLayout Layout) IntoOpening(IReadOnlyList<WindowHandle> order, TileLayout layout, WindowHandle window);
  public sealed record ReturnSpot(IReadOnlyList<WindowHandle> Siblings, Zone Side, double Share);
  public static Maybe<ReturnSpot> SpotOf(IReadOnlyList<WindowHandle> order, TileLayout layout, WindowHandle window);
  public static Maybe<(IReadOnlyList<WindowHandle> Order, TileLayout Layout)> Returned(IReadOnlyList<WindowHandle> order, TileLayout layout, WindowHandle window, ReturnSpot spot);
  public enum TileTidy { None, EvenOut, ResetGrid }
  public static TileTidy TidyStep(IEnumerable<TileLayout> stored);         // any !IsEven: EvenOut; else any stored: ResetGrid; else None
  // SplitMove:
  public static Maybe<TileLayout> Apply(TileLayout layout, WindowRect area, int gap, int leaf, Edges moved, WindowRect frame);
  // TileArrival:
  public static Maybe<TileSplit> Choose(IReadOnlyList<WindowHandle> order, TileLayout layout, IReadOnlyList<WindowRect> tiles, Maybe<WindowHandle> lastUsed, IReadOnlySet<WindowHandle> skip);
  ```
  Rules, where the spec leaves a choice:
  - `Drop`: closes an opening first (as today), lifts `dragged` (`Remove` its leaf, if on this screen), then
    `Insert(target leaf, zone)`; the order is the leaves' windows in leaf order (insert `dragged` at its new leaf's
    slot). Middle, `dragged == target`, a count mismatch, or a target not on the screen: unchanged, as today.
  - `DropAtEdge`: same lift, then `InsertAtEdge(side)`.
  - `Refit`: opening rules as today (an opening for `count` is kept; one leaver beside an opening removes only its
    leaf; otherwise close first). `Count == count` returns it; one more leaf and a known leaver removes that leaf;
    more leaves than windows removes the LAST non-opening leaves one by one; fewer inserts `Bottom` of the last leaf,
    once per missing window.
  - `FitStart`: as today with leaves; nothing stored and a leaver means `Automatic(oldCount)`.
  - `Open`: an edge is outer when no ancestor branch of its axis has a sibling on that side. The empty leaf is
    placed as `Insert(leaf, side)` would place it, with a share making it as deep as the pull in pixels (of the parent
    branch's span when it becomes a sibling, of the leaf's span when it splits the leaf). Not when an opening exists
    or the count mismatches; pull `< MinOpening` of the inner span, or a tile with `< 2 * MinOpening` of the inner
    span on that axis, gives None. Deepest pull wins at a corner.
  - `Closes`: the opening is the moved leaf's adjacent sibling, on the moved side, in a branch of that edge's axis,
    and the edge let go within a quarter of the hole's depth from its far side.
  - `SplitMove.Apply`: per moved edge, walk up to the nearest branch of that edge's axis where the leaf's subtree
    has a neighbour on that side; move the boundary between those two children to the released edge plus `far`
    (right/bottom) or minus `near` (left/top), as a fraction of THAT branch's rect (computed with task 1's
    arithmetic); clamp so neither child goes under `MinShare` of that branch. None when no moved edge has a split.
  - `SpotOf`: None for a lone leaf. The sibling child is the one before the window's child in its parent branch,
    else the one after; `Side` is where the window sits relative to it (Right/Bottom when the sibling is before);
    `Siblings` are the windows of that child's subtree; `Share` is the window's share over the window's plus the
    sibling's (pairwise, so a re-split gives it the same proportion of the pair it had). The opening is never a sibling.
  - `Returned`: find the largest subtree (most leaves; a tie goes to the first in leaf order) whose windows are all
    in `spot.Siblings` (at least one, no opening in it); split THAT node on
    `spot.Side` with `spot.Share` (as a sibling when its parent has the side's axis, otherwise by wrapping it). None
    when no sibling is on the screen (the caller then arrives it as new). Opening bookkeeping as in task 2.
  - `TileArrival.Choose`: shares from `layout.ShareOf(LeafOf(...))`; floor and largest-tile rules as today; side is
    Right when the tile's rect is wider than tall, otherwise Bottom (the "alone in its column" condition is gone).

- [ ] **Step 1: Write the failing tests.** Port every case of `TileArrangeTests`, `SplitMoveTests` and
  `TileArrivalTests` that still applies; add at least:
  - `Drop_beside_the_top_of_a_stack_shares_its_row` (Review Focus 2): order `[A,B,C]`, layout
    `Across[Down[L,L] .5, L .5]`, `Drop(C, A, Right)` gives order `[A,C,B]` and `Down[Across[L .5,L .5] .5, L .5]`.
  - `Drop_beside_the_bottom_of_a_stack_shares_its_row`: `Drop(C, B, Right)` gives `[A,B,C]`, `Down[L .5, Across[L .5,L .5] .5]`.
  - `Drop_beside_a_full_height_tile_makes_a_column` (unchanged behaviour): `Across[L,L]`, `Drop(B, A, Left)` …
  - `DropAtEdge_right_makes_a_full_height_column`: `Down[L,L,L]` order `[A,B,C]`, `DropAtEdge(C, Right)` gives
    `Across[Down[L,L] .5, L .5]`, order `[A,B,C]`.
  - `ScreenBand_is_six_percent_deep`: inner `(0,0,1000,600)`: `(59,300)` Left, `(61,300)` None, `(500,35)` Top,
    `(30,20)` Top (nearer edge), `(500,300)` None.
  - `Refit_without_a_leaver_drops_the_last_leaves` and `Refit_adds_below_the_last_leaf` (changed on purpose; name says so).
  - `SplitMove_bottom_edge_of_a_top_pair_moves_the_shared_split`: in `Down[Across[A,C],B]`, A's bottom dragged to
    y 400 of 600 (gap 0) gives top share 2/3 and leaves the `Across` shares alone.
  - `SplitMove_inside_a_nested_branch_uses_that_branch_span`: A's right edge dragged in `Down[Across[A,C],B]`.
  - `Open_beside_a_stacked_tile_splits_only_that_tile` (changed on purpose): right column `Down[L,L]` at the screen's
    right edge, top tile's right edge pulled 200 px in on a 1000 px inner width gives that leaf a sibling opening,
    bottom tile untouched.
  - `Closes_when_pulled_back_over_its_sibling_opening`.
  - `Returned_brings_a_full_height_column_back_full_height`: spot of C in `Across[Down[A,B] .5, C .5]` is
    siblings `[A,B]`, side Right, share 0.5. `Returned` on order `[A,B]`, layout `Down[L,L]` gives order `[A,B,C]`
    and `Across[Down[L,L] .5, L .5]`.
  - `Returned_goes_beside_the_first_remaining_sibling_when_the_stack_was_split`: same spot, screen now `[A,B,D]`
    in `Down[L,L,L]`. No subtree holds only siblings except the leaves A and B; take the first in leaf order (A):
    order `[A,C,B,D]`, layout `Down[Across[L .5,L .5], L, L]` (the Down shares as they were).
  - `Returned_is_none_without_siblings`.
  - `Arrival_splits_a_wide_stacked_tile_to_the_right` (changed on purpose).
  - `TidyStep` cases: uneven → EvenOut; even custom shape → ResetGrid; nothing stored → None.
- [ ] **Step 2: Run** `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~LayoutTests"`. Expected: FAIL.
- [ ] **Step 3: Implement** the overloads.
- [ ] **Step 4: Run** it, then the routine command. Expected: PASS.
- [ ] **Step 5: Commit** `feat: drops, splits, openings and arrivals on the split tree`, `*opus5.5*`.

### Task 4: Storing layouts, migration, Even out and Reset to grid in the manager

**Files:**
- Modify: `src/Deskbar.Core/Domain/Workspace.cs` (new `ScreenTileLayout`, `TileLayouts`), `src/Deskbar.Core/Persistence/JsonPersistenceStore.cs`
  (migration after load), `src/Deskbar.Core/WorkspaceManager.cs` (around 1731-1775)
- Test: `tests/Deskbar.Core.Tests/Tiling/TileLayoutSettingTests.cs`, plus a persistence test beside the existing
  `JsonPersistenceStore` tests (find them with `grep -rl JsonPersistenceStore tests`)

**Interfaces:**
- Consumes: tasks 1 and 3 (`TileLayout.FromGrid`, `IsPlain`, `Evened`).
- Produces:
  ```csharp
  public sealed record ScreenTileLayout(string Screen, TileLayout Layout);
  // Workspace:
  public ValueList<ScreenTileLayout> TileLayouts { get; init; } = [];
  // WorkspaceManager:
  public Result SetTileLayout(Guid id, string screen, TileLayout layout); // plain removes the entry, as SetTileSizes does
  public Result EvenOutTiles(Guid id);   // every stored layout Evened(); a plain result's entry goes
  public Result ResetTileGrid(Guid id);  // TileLayouts = [] (today's EvenOutTiles behaviour)
  ```
  `SetWorkspaceTiled(false)` clears `TileLayouts` as it clears `TileSizes` today. `SetTileSizes` stays until task 7.
  Migration: right after `Deserialize<AppState>`, each workspace with `TileSizes` non-empty and `TileLayouts` empty
  gets `TileLayouts` from `TileLayout.FromGrid` per entry, then `TileSizes = []`. Every save writes `TileSizes` empty.

- [ ] **Step 1: Write the failing tests:** `Old_sizes_load_as_layouts` (a state.json text with an old `TileSizes`
  entry loads as the `FromGrid` layout and an empty `TileSizes`); `Saved_state_keeps_the_old_property_empty`
  (Review Focus 5); `SetTileLayout_removes_a_plain_layout`; `EvenOutTiles_keeps_the_shape`;
  `EvenOutTiles_drops_an_even_automatic_layout`; `ResetTileGrid_forgets_every_layout`; `Untiling_forgets_layouts`.
- [ ] **Step 2: Run** `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~TileLayoutSettingTests|FullyQualifiedName~Persistence"`. Expected: FAIL.
- [ ] **Step 3: Implement.** The existing `EvenOutTiles` body becomes `ResetTileGrid`; the new `EvenOutTiles`
  evens. The pipe's `even` keeps calling `EvenOutTiles` (task 7 adds `reset-grid`).
- [ ] **Step 4: Run** it, then the routine command. Expected: PASS (existing tests that asserted Even out forgets
  everything move to `ResetTileGrid`; the commit body says so).
- [ ] **Step 5: Commit** `feat: store split tree layouts and migrate grid sizes`, `*opus5.5*`.

### Task 5: The Tiler on the tree

Switch every Tiler partial from `TileSizes` and `(int Column, int Row)` to `TileLayout` and leaf indexes, with
today's behaviour except where the spec changes it. No new gestures here (bands and first fill are task 6).

**Files:**
- Modify: `src/Deskbar.Core/Tiling/Tiler.cs` (`StateKey.Sizes` becomes `ValueList<ScreenTileLayout>`, `SlotsOf`,
  the opening paths near 545 and 797), `Tiler.Resize.cs` (`Leaver.Cell` → `int Leaf`, `SizesOn` → `LayoutOn`,
  `StoreSizes` → `StoreLayout` via `SetTileLayout`, `MovedASplit`, `Show` prints the tree), `Tiler.Drag.cs`
  (`ShownSizes` → `ShownLayout`, `StoredOn`, `CellOn` → `LeafOn`, `Rearranged`), `Tiler.Arrival.cs`,
  `Tiler.Minimize.cs` (exact-restore path only; `Away.Sizes` becomes `Maybe<TileLayout>`; the changed-screen path
  calls the old column logic through `TileLayout` temporarily: replace it with "arrive as new" and leave a
  `// task 6` note), `SettledMove.cs` (`Snapped`: pick the neighbour tile geometrically from `slots`, the nearest
  tile whose rect lies wholly left/right of this one and spans its vertical middle, else nearest by vertical
  distance; no `shape` parameter)
- Test: `tests/Deskbar.Core.Tests/Tiling/TilerTests.cs`, `TilerResizeTests.cs`, `TileSizesSettingTests.cs` (port)

**Interfaces:**
- Consumes: tasks 1 to 4.
- Produces: nothing new for other tasks beyond `ShownLayout(Workspace, TileScreen, int) : TileLayout`,
  `StoredOn(Guid, string) : Maybe<TileLayout>`, `StoreLayout(Workspace, string, TileLayout) : Result`, used by task 6.

- [ ] **Step 1: Port the tests** to the new types (they construct `TileLayout` where they built `TileSizes`; the
  `FakeManager`/state builders set `TileLayouts`). Add `Tiler_drop_beside_the_top_of_a_stack` (Review Focus 2):
  three windows, C dropped by hand at a point in A's right quarter, the surface receives
  A `(0,0,500,300)`, C `(500,0,1000,300)`, B `(0,300,1000,600)` (gap 0) and the stored layout is
  `Down[Across[L,L],L]`. Tests whose expectation changes on purpose are renamed to say the new rule.
- [ ] **Step 2: Run** `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~Tiling"`. Expected: FAIL (does not build).
- [ ] **Step 3: Switch the Tiler.** Trace lines keep their wording with "leaf N" for "column C, row R".
- [ ] **Step 4: Run** it, then the routine command. Expected: PASS.
- [ ] **Step 5: Commit** `feat: the tiler lays out and edits the split tree`, `*opus5.5*`.

### Task 6: Screen-edge bands, first fill by position, minimize memory

**Files:**
- Modify: `src/Deskbar.Core/Tiling/SettledMove.cs` (`MoveOutcome.AtEdge(TileArrange.Zone Side)`; `DecideAt` gets
  `Maybe<WindowRect> innerHere` for the screen under the point and returns `AtEdge` when `ScreenBand` hits, after
  the opening check and before tile hits; on another screen it uses that screen's inner area),
  `Tiler.Drag.cs` (apply `AtEdge` with `DropAtEdge`, store first, re-tile; its preview is the dragged window's
  rect in the resulting layout, as `BesidePreview` does), `TileBoard.cs` (`FirstFill` overload),
  `Tiler.cs` (first fill passes the stored cells), `Tiler.Minimize.cs` (`Away` gains `Maybe<ReturnSpot> Spot`
  from `TileArrange.SpotOf` at minimize; the changed-screen path uses `Returned`, falling back to the arrival path)
- Test: `tests/Deskbar.Core.Tests/Tiling/TileBoardTests.cs`, `TilerTests.cs`, a `SettledMove` test file if one
  exists (`grep -rl SettledMove tests`), else `TilerTests.cs`

**Interfaces:**
- Consumes: task 3 (`ScreenBand`, `DropAtEdge`, `SpotOf`, `Returned`), task 5 (`ShownLayout`, `StoredOn`, `StoreLayout`).
- Produces:
  ```csharp
  public static TileBoard FirstFill(IEnumerable<(WindowHandle Window, int Screen, WindowRect Frame)> windows,
      Func<int, int, Maybe<IReadOnlyList<WindowRect>>> cells); // (screen, count) -> stored layout's window rects when its count matches
  ```
  Greedy by overlap area: repeatedly take the (window, cell) pair with the largest intersection, ties broken by
  today's Left-then-Top order; windows with no overlapping cell take the remaining cells in Left-then-Top order. The
  old one-argument overload stays and means "no cells".

- [ ] **Step 1: Write the failing tests:** `FirstFill_keeps_a_hand_made_shape` (Review Focus 3: frames sitting in
  `Down[Across[A,C],B]` cells give order `[A,C,B]`); `FirstFill_without_cells_sorts_as_before`;
  `DecideAt_in_the_right_band_is_AtEdge`; `DecideAt_opening_wins_over_a_band`; `Tiler_band_drop_makes_a_full_height_column`
  (three stacked windows, C dropped at x = 98% of the width, ends `Across[Down[A,B],C]`);
  `Minimized_column_comes_back_full_height_when_the_screen_changed` (C minimized from `Across[Down[A,B],C]`, a new
  D arrives, C restored: C beside the subtree holding A and B, full height); `Minimize_restore_on_an_unchanged_screen_is_exact`
  (kept from today, re-asserted on Petre's layout).
- [ ] **Step 2: Run** `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~Tiling"`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** it, then the routine command. Expected: PASS.
- [ ] **Step 5: Commit** `feat: screen-edge band drops, first fill by position, minimized tiles return beside their neighbours`, `*opus5.5*`.

### Task 7: Menu, pipe, docs, and the grid code goes

**Files:**
- Modify: `src/Deskbar.App/FloatingBar.Tiling.cs` (row menu and `FillTileMenu`: one item from
  `TileArrange.TidyStep(workspace.TileLayouts.Select(l => l.Layout))`: `EvenOut` shows "Even out tiles" calling
  `EvenOutTiles`, `ResetGrid` shows "Reset to grid" calling `ResetTileGrid`, `None` shows nothing; same glyph `⊟`;
  update the comments that call Even out "the one way back"), `src/Deskbar.Core/Control/ControlUsage.cs` (`even`
  text "Even out tiles: equal splits, your shape kept"; new `reset-grid <workspace>` "Back to the automatic grid"),
  `src/Deskbar.Core/Control/RemoteControl*.cs` (dispatch `reset-grid`), `README.md` and
  `docs/superpowers/notes/manual-test-script.md` (tiling sections: beside a tile, bands, Even out then Reset to grid).
- Delete: the `TileSizes` overloads in `GridLayout`, `TileArrange`, `SplitMove`, `TileArrival`; `TileSizes.Fit`
  and anything only it used; `WorkspaceManager.SetTileSizes`; the old test files `TileArrangeTests`,
  `SplitMoveTests`, `TileArrivalTests`, `TileSizesTests`, `TileSizesSettingTests` whose cases task 3 to 5 ported.
  `TileSizes` itself stays as the legacy JSON shape `FromGrid` reads, with a comment saying only migration uses it.
- Test: `tests/Deskbar.Windows.Tests/FloatingBarTilingMenuTests.cs`, `FloatingBarTileButtonTests.cs`,
  `tests/Deskbar.Core.Tests/Control/RemoteControlTileTests.cs`

- [ ] **Step 1: Write the failing tests:** `Menu_offers_even_out_while_a_split_is_uneven`,
  `Menu_offers_reset_to_grid_once_even_with_a_custom_shape`, `Menu_offers_neither_with_nothing_stored`, the same
  three for the tile button's menu, `Pipe_reset_grid_forgets_layouts`, `Pipe_even_keeps_the_shape`, and the existing
  `ControlUsage` completeness test covering `reset-grid`.
- [ ] **Step 2: Run** `dotnet test Deskbar.sln --filter "Category!=Integration&(FullyQualifiedName~Tiling|FullyQualifiedName~RemoteControl)"`. Expected: FAIL.
- [ ] **Step 3: Implement**, then delete the dead code and old tests.
- [ ] **Step 4: Run** the routine command (PASS), then `dotnet build Deskbar.sln -c Release -warnaserror` (0 warnings, 0 errors).
- [ ] **Step 5: Commit** `feat: even out keeps your shape, reset to grid forgets it; the grid model goes`, `*opus5.5*`.
