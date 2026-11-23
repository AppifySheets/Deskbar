# Recent windows of a chosen app split last Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A new window splits a big-enough tile used longest ago, never one of the three most recently used windows of an app marked "split last" (VS Code) unless nothing else is left.

**Architecture:** `TileArrival.Choose` changes rule: it now takes each window's age (its rank in the Tiler's activation order) instead of one "last used" window, keeps the tiles at least half as big as the largest one, and splits the oldest of those. It also takes a second, softer skip set (`avoid`, the protected tiles), tried before the hard one. The Tiler computes the protected set from a new per-app list in state, mirroring `RememberedApps`, and its "another screen" redirect now fires when the target screen has no free tile. Pipe twin and icon-menu item copy "Remember <app>'s workspace".

**Tech Stack:** C# / .NET 10, xUnit, CSharpFunctionalExtensions.

**Spec:** `docs/superpowers/specs/2026-11-23-split-recent-app-windows-last-design.md`

## Global Constraints

- Three protected windows per app (`const int ProtectedPerApp = 3`), counted on the board of the workspace being tiled.
- Big enough means area at least half the largest candidate's area (`const double BigEnough = 0.5`), compared as layout shares (width share times height share) within one screen, in pixels across screens.
- Age: rank in `Tiler.used`, 0 for the most recently activated. A window never activated is older than every activated one.
- Tier order for a split: free tiles, then protected tiles (oldest first), then maximized or locked tiles (the existing "everything skipped" fallback).
- Aimed arrivals (dropped on a screen, restored from minimized) never leave their screen; the tiers apply within it.
- The setting is `AppState.SplitLastApps`, a list of process names, compared case-insensitively.
- Menu text: "Split <ProcessName> windows last". Pipe: `split-last <hwnd> on|off`.
- C# style per CLAUDE.md. Release build with `-warnaserror` before the PR. `Deskbar.Windows.Tests` only through `pwsh scripts/test-hidden.ps1`.

## Review Focus

1. **Two windows arriving in one evaluate.** The second must see the first in its new place and pick by the same rule. It must never split the first one just because it was never activated (age = never). A test in Task 2 opens two windows at once.
2. **An app with fewer than three tiles.** All of its tiles are protected. With every tile on every screen protected, the oldest protected one is split. Task 2 tests it.
3. **A protected window that closes.** The next most recent window of that app becomes protected, because the set is computed at arrival from the board. Task 2 tests it.
4. **Setting turned on for an elevated or Deskbar's own window.** It is refused, as `RememberWorkspace` refuses. Task 3 tests it.
5. **Every free tile fails the quarter floor.** It falls back to the largest free tile, never a protected one. Task 1 tests it.

---

### Task 1: The split rule (Core, pure)

**Files:**
- Modify: `src/Deskbar.Core/Tiling/TileArrival.cs`
- Test: `tests/Deskbar.Core.Tests/Tiling/TileArrivalLayoutTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public static Maybe<TileSplit> Choose(
      IReadOnlyList<WindowHandle> order, TileLayout layout, IReadOnlyList<WindowRect> tiles,
      Func<WindowHandle, int> age, IReadOnlySet<WindowHandle> avoid, IReadOnlySet<WindowHandle> skip);
  ```
  `age(w)`: 0 is the most recent, `int.MaxValue` means never activated. The candidates are the tiles in neither `avoid` nor `skip`. If none, the tiles not in `skip`. If none, every tile (the existing rule). Among the candidates that pass the floor, the big-enough ones are those whose area is at least 0.5 of the largest. Of those, the largest `age` wins; ties go to the larger area, then the later slot. If no candidate passes the floor, the largest candidate (today's fallback). The side rule is unchanged.

- [ ] **Step 1: Rewrite the test file's `Choose` helper** to take `int[] ages` (by slot, `int.MaxValue` for never) plus `avoid` and `skip`. Re-derive each existing case's expectation under the new rule. Where a case was about "the last-used tile is split", pass ages that make that tile the oldest, and add a comment saying so. Then add:
  - `The_oldest_big_enough_tile_is_split`: 3 tiles in `Columns([0.5,0.5],[1.0],[0.5,0.5])`, so W1 is full height and W2 and W3 half height each. Ages W1=0, W2=2, W3=1: all are big enough (0.25 ≥ 0.5 × 0.5), so W2, the oldest, is split.
  - `A_tile_under_half_the_largest_is_passed_over_however_old`: W1 at 0.5×1.0 and W2 at 0.5×0.2 (below half). W2 is older, and W1 is split.
  - `A_tile_never_used_is_older_than_any_used_one`.
  - `Avoided_tiles_are_split_only_when_nothing_else_is_left`: W1 avoided and oldest, W2 free, so W2. All avoided, so the oldest avoided one.
  - `Skipped_tiles_still_come_after_avoided_ones`: W1 avoided, W2 skipped, so W1.
  - `When_every_free_tile_fails_the_floor_the_largest_free_one_is_split_not_an_avoided_one` (Review Focus 5).
- [ ] **Step 2:** `dotnet test tests/Deskbar.Core.Tests --filter FullyQualifiedName~TileArrivalLayoutTests`. Expected: build error.
- [ ] **Step 3: Implement `Choose`.** Rewrite its header comment to say the rule and Petre's words from the spec.
- [ ] **Step 4:** Same command, expected PASS. The Tiler doesn't compile yet; Task 2 fixes the call site, so commit only after Task 2's Step 3. If the build has to stay green per commit, do Tasks 1 and 2 as one commit.

### Task 2: The Tiler picks by age and protects recent windows of chosen apps

**Files:**
- Modify: `src/Deskbar.Core/Persistence/AppState.cs` (add `SplitLastApps`, with a comment shaped like `RememberedApps`')
- Modify: `src/Deskbar.Core/WorkspaceManager.cs` (add `ProcessNameOf`, `SplitsLast`, `SetSplitLast`; Task 3 uses the last two)
- Modify: `src/Deskbar.Core/Tiling/Tiler.Arrival.cs` (`Split`, `SplitSomewhere`, `UnlockedElsewhere` renamed `FreeElsewhere`, `Why`, header comment)
- Create: `src/Deskbar.Core/Tiling/Tiler.SplitLast.cs` (`AgeOf`, `ProtectedAmong`)
- Test: `tests/Deskbar.Core.Tests/Tiling/TilerTests.SplitLast.cs` (new), and update expectations in `TilerTests.Arrival.cs`, `TilerTests.Lock.cs` and `TilerTests.OtherScreens.cs`

**Interfaces:**
- Consumes: `TileArrival.Choose` (Task 1).
- Produces:
  ```csharp
  // WorkspaceManager
  public Maybe<string> ProcessNameOf(WindowHandle window);
  public bool SplitsLast(string processName);
  public Result SetSplitLast(WindowHandle window, bool on); // fails like RememberWorkspace for our own and unknown windows
  // Tiler (private)
  int AgeOf(WindowHandle window);                          // index in `used`, int.MaxValue if absent
  IReadOnlySet<WindowHandle> ProtectedAmong(TileBoard board); // per app in SplitLastApps: its board windows by AgeOf, first 3 with an age below int.MaxValue
  ```
- In `Split`: `skip` = maximized plus locked, as now. `avoid` = `ProtectedAmong(board)` minus `skip`. Pass `AgeOf` in place of `lastUsed`. `lastUsed` is still used to choose the TARGET SCREEN in `Arrive`, unchanged.
- `SplitSomewhere`: the target is blocked when it has no free tile (none outside `skip` and `avoid`) and has at least one tile, or when it has only an aside maximized window. Then `FreeElsewhere` picks, over every other screen, the free tile that is big enough (pixel area at least half the largest free one there) and oldest, and the window splits on that tile's screen. With no free tile elsewhere, it splits on the target, where `Choose` falls through its tiers. Aimed windows are never redirected.
- `Why` reports "the oldest big-enough tile" and, when protection or locks passed tiles over, names them: `passed over 0x.. (protected), 0x.. (locked)`.

- [ ] **Step 1: Write the new tests** in `TilerTests.SplitLast.cs`, with an `Info` variant that takes a process name (`"Code"`, `"msedge"`), and the setting turned on through `manager.SetSplitLast`:
  - `A_new_window_splits_the_tile_used_longest_ago_not_the_one_in_use`: W1 | W2, use W2 then W1, open W3; W2 is split.
  - `The_three_most_recent_code_windows_are_passed_over`: Code tiles C1, C2, C3 and an msedge tile E on one screen, in a layout where every tile is big enough. Activated oldest first: C1, C2, C3, E. Open W, and E is split although it is the most recent: all three Code windows are protected.
  - `The_fourth_code_window_back_is_not_protected`: as above, plus a Code tile C4 activated before C1. Open W, and C4 is split: it is the oldest, and not among the three latest Code windows.
  - `With_only_protected_tiles_the_oldest_of_them_is_split` (Review Focus 2): two Code tiles, setting on, so C-older is split.
  - `A_protected_window_closing_protects_the_next_one` (Review Focus 3).
  - `Two_windows_opening_together_do_not_split_each_other` (Review Focus 1): two windows in one evaluate. The second splits an existing old tile, not the first newcomer.
  - `Protected_tiles_on_the_target_screen_send_the_window_to_a_free_tile_on_another_screen`.
  - `A_window_dropped_on_a_screen_of_protected_tiles_stays_there`: aimed, so it splits the oldest protected tile there.
  - `The_setting_survives_a_restart`: `SplitLastApps` is in the stored state.
- [ ] **Step 2:** `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~TilerTests|FullyQualifiedName~TileArrival"`. Expected: build errors, then failures.
- [ ] **Step 3: Implement.** Then go through every failing existing test in `TilerTests.Arrival.cs`, `TilerTests.Lock.cs` and `TilerTests.OtherScreens.cs`. Where only the expected tile changed because the rule changed (it used to split the last-used tile), update the expectation and add a comment naming the new reason. Where a test's PURPOSE was the last-used rule itself (for example "a window opened from a tile splits that tile"), rename it to state the new rule rather than deleting it. Ledger each changed test.
- [ ] **Step 4:** `dotnet test tests/Deskbar.Core.Tests --filter "Category!=Integration"`. Expected: all pass.
- [ ] **Step 5: Commit** `feat: a new window splits a big-enough tile used longest ago, recent windows of a split-last app last`.

### Task 3: The menu item, the pipe twin and the docs

**Files:**
- Modify: `src/Deskbar.Core/Control/RemoteControl.cs` (dispatch `("split-last", [var hwnd, var onOff])`), `RemoteControl.Windows.cs` (handler shaped like `RememberWorkspace`), `ControlUsage.cs` (`new("split-last", "<hwnd> on|off", "Split <app> windows last: a new window halves them only when nothing else is left")`)
- Modify: `src/Deskbar.App/FloatingBar.xaml.cs` (a checkable "Split {ProcessName} windows last" with glyph "⊟", next to "Don't split this tile", shown when `manager.ProcessNameOf` answers and the window is not ours; Click calls `manager.SetSplitLast(handle, item.IsChecked)`)
- Modify: `README.md`, `docs/features/tiling.md`, `docs/superpowers/notes/manual-test-script.md` (S1 to S6)
- Test: `tests/Deskbar.Core.Tests/Control/RemoteControlSplitLastTests.cs`, `tests/Deskbar.Windows.Tests/FloatingBarSplitLastMenuTests.cs`

- [ ] **Step 1: Write the failing tests.**
  - Pipe: `split-last 0x.. on` persists, `off` removes it, `maybe` prints the usage line, an unknown hwnd gives "Window no longer exists.", and an own window is refused (Review Focus 4). Also the existing test that every dispatched command is in `ControlUsage` still passes.
  - Menu: the item is present and unticked for a tile, ticked once the app is listed, and a click sets it.
- [ ] **Step 2:** Run both filters and watch them fail (the Windows one through `test-hidden.ps1`).
- [ ] **Step 3: Implement.** Docs:
  - README tiling paragraph: one sentence saying a new window halves the biggest tile you haven't used for a while, and that "Split VS Code windows last" keeps your three latest VS Code windows whole.
  - tiling.md: the rule with the browser, Explorer and Terminal example.
  - Manual steps S1 to S6: menu tick, opening from VS Code, the fourth VS Code window, all protected, a second screen, the pipe.
- [ ] **Step 4:** `dotnet build Deskbar.sln -c Release -warnaserror`, then `pwsh scripts/test-hidden.ps1`. Expected: 0 warnings, all green (a rerun allowed for the known `TileSurfaceTests` and `ControlPipeTests` flakes, named in the PR).
- [ ] **Step 5: Commit** `feat: split <app> windows last, from the icon menu and the pipe`, then the docs commit.
