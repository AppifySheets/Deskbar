# Recent windows of a chosen app split last Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A new window still splits the tile last used, except when that tile is one of the three most recently used windows of an app marked "split last" (VS Code). Those are split only when nothing else is left.

**Architecture:** `TileArrival.Choose` gains a softer skip set, `avoid` (the protected tiles), tried before the hard `skip` (maximized, locked). It also gains an `age` function that orders the avoided tier, oldest first. The Tiler computes the protected set from a new per-app list in state (`SplitLastApps`, shaped like `RememberedApps`), and its "go to another screen" redirect now fires when the target screen has no free tile. The pipe twin and the icon-menu item copy "Remember <app>'s workspace".

**Tech Stack:** C# / .NET 10, xUnit, CSharpFunctionalExtensions.

**Spec:** `docs/superpowers/specs/2026-11-23-split-recent-app-windows-last-design.md`

## Global Constraints

- Three protected windows per app (`const int ProtectedPerApp = 3`), counted on the board of the workspace being tiled.
- Age: rank in `Tiler.used`, 0 for the most recently activated; a window never activated gets `int.MaxValue`, which makes it the oldest.
- The order of tiers for a split:
  1. the tile last used, if it is free and passes the floor;
  2. the largest free tile;
  3. protected tiles, the oldest one that passes the floor, else the largest;
  4. maximized or locked tiles (the existing "everything skipped" fallback).
- Aimed arrivals (dropped on a screen, restored from minimized) never leave their screen.
- The setting is `AppState.SplitLastApps`, process names compared case-insensitively. The menu text is "Split <ProcessName> windows last". The pipe command is `split-last <hwnd> on|off`.
- C# style per CLAUDE.md. Release build with `-warnaserror` before the PR. `Deskbar.Windows.Tests` only through `pwsh scripts/test-hidden.ps1`, filter `Category!=Integration&Category!=Screen`.

## Review Focus

1. **Two windows arriving in one evaluate.** The second gets no last-used tile (existing rule) and must not take a protected tile while a free one exists. A test in Task 2.
2. **An app with fewer than three tiles.** All of its tiles are protected. With only protected tiles anywhere, the oldest protected one is split. A test in Task 2.
3. **A protected window that closes.** The next most recent window of that app becomes protected, because the set is computed at each arrival. A test in Task 2.
4. **The setting turned on for an elevated window or Deskbar's own.** It is refused, as `RememberWorkspace` refuses. A test in Task 3.
5. **Every free tile fails the quarter floor.** The largest free tile is still split, never a protected one. A test in Task 1.

---

### Task 1: The rule (Core, pure)

**Files:**
- Modify: `src/Deskbar.Core/Tiling/TileArrival.cs`
- Modify: `src/Deskbar.Core/Tiling/Tiler.Arrival.cs` (only the call in `Split`, passing an empty `avoid` and `_ => int.MaxValue`, so the build stays green)
- Test: `tests/Deskbar.Core.Tests/Tiling/TileArrivalLayoutTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public static Maybe<TileSplit> Choose(
      IReadOnlyList<WindowHandle> order, TileLayout layout, IReadOnlyList<WindowRect> tiles, Maybe<WindowHandle> lastUsed,
      IReadOnlySet<WindowHandle> skip, IReadOnlySet<WindowHandle>? avoid = null, Func<WindowHandle, int>? age = null);
  ```
  - The free tiles are those in neither `skip` nor `avoid`. When at least one exists, the existing rule runs over them: the last used if it is among them and passes the floor, else the largest.
  - When none is free, the avoided ones not in `skip`: the one with the largest `age` that passes the floor, else the largest.
  - When those are empty too, every tile, as now. The side rule is unchanged.

- [ ] **Step 1: Write the failing tests.** Extend the file's `Choose` helper with optional `avoid` and `ages` (by slot), leaving every existing case unchanged, and add:
  - `An_avoided_last_used_tile_hands_the_split_to_the_largest_free_one`: 2 tiles, W1 last used and avoided, so W2.
  - `Avoided_tiles_are_split_only_when_no_tile_is_free`: every tile avoided, so the tile with the largest age (ages W1=0, W2=1, so W2).
  - `Among_avoided_tiles_the_oldest_that_passes_the_floor_is_split`: 3 avoided tiles, the oldest one too small, so the next oldest.
  - `Skipped_tiles_still_come_after_avoided_ones`: W1 avoided, W2 skipped, so W1.
  - `When_every_free_tile_fails_the_floor_the_largest_free_one_is_split_not_an_avoided_one` (Review Focus 5).
- [ ] **Step 2:** `dotnet test tests/Deskbar.Core.Tests --filter FullyQualifiedName~TileArrivalLayoutTests`. Expected: build error.
- [ ] **Step 3: Implement**, updating `Choose`'s header comment with the tiers and the spec's quotes, and fix the one call site.
- [ ] **Step 4:** Same command, then the whole Core suite. Expected: PASS.
- [ ] **Step 5: Commit** `feat: a tile can be avoided by a new window, split only when nothing else is free`.

### Task 2: The Tiler protects the three latest windows of chosen apps

**Files:**
- Modify: `src/Deskbar.Core/Persistence/AppState.cs` (add `SplitLastApps`, with a comment shaped like `RememberedApps`')
- Modify: `src/Deskbar.Core/WorkspaceManager.cs` (add `ProcessNameOf`, `SplitsLast`, `SetSplitLast`)
- Create: `src/Deskbar.Core/Tiling/Tiler.SplitLast.cs` (`AgeOf`, `ProtectedAmong`)
- Modify: `src/Deskbar.Core/Tiling/Tiler.Arrival.cs` (`Split` passes `avoid` and `age`; `SplitSomewhere`'s blocked test and `UnlockedElsewhere`, renamed `FreeElsewhere`, treat protected tiles as not free; `Why` names a protected tile passed over; the header comment gets a paragraph)
- Test: `tests/Deskbar.Core.Tests/Tiling/TilerTests.SplitLast.cs` (new)

**Interfaces:**
- Consumes: `TileArrival.Choose` (Task 1).
- Produces:
  ```csharp
  // WorkspaceManager
  public Maybe<string> ProcessNameOf(WindowHandle window);
  public bool SplitsLast(string processName);
  public Result SetSplitLast(WindowHandle window, bool on); // refuses our own windows and unknown ones, like RememberWorkspace
  // Tiler (private)
  int AgeOf(WindowHandle window);                              // index in `used`, int.MaxValue if absent
  IReadOnlySet<WindowHandle> ProtectedAmong(TileBoard board);  // per app in SplitLastApps: that app's board windows with an age, by age, first 3
  ```
- The trace says `split 0x.. (the largest free tile, the tile last used (0x..) is protected)`, and `screen N has no free tile, it goes to screen M` for the redirect.

- [ ] **Step 1: Write the failing tests**, with an `Info` variant that takes a process name (`"Code"`, `"msedge"`), turning the setting on through `manager.SetSplitLast`:
  - `A_window_opened_from_a_recent_code_window_splits_another_tile`: screen 1 holds C1 (Code) and E (msedge). Use E, then C1. Open W: E is split, not C1.
  - `A_window_opened_from_a_browser_still_splits_the_browser`: the same layout, used C1 then E. Open W: E is split (the rule is unchanged).
  - `The_fourth_code_window_back_is_not_protected`: C1 to C4 on one screen, used C4, C3, C2, C1 in that order, C1 last. Open W: C4 is the largest free tile, so it is split. Pick a layout where C4 is the largest, or assert on the free set.
  - `With_only_protected_tiles_the_oldest_of_them_is_split` (Review Focus 2).
  - `A_protected_window_closing_protects_the_next_one` (Review Focus 3).
  - `Two_windows_opening_together_leave_protected_tiles_alone` (Review Focus 1).
  - `Protected_tiles_on_the_target_screen_send_the_window_to_a_free_tile_on_another_screen`.
  - `A_window_dropped_on_a_screen_of_protected_tiles_stays_there`: aimed, so it splits the oldest protected tile there.
  - `The_setting_survives_a_restart`: `SplitLastApps` is in the stored state.
  - `An_app_without_the_setting_is_not_protected`.
- [ ] **Step 2:** `dotnet test tests/Deskbar.Core.Tests --filter FullyQualifiedName~TilerTests`. Expected: build errors, then the new tests fail.
- [ ] **Step 3: Implement.**
- [ ] **Step 4:** `dotnet test tests/Deskbar.Core.Tests --filter "Category!=Integration"`. Expected: all pass, the existing arrival, lock and other-screens tests unchanged.
- [ ] **Step 5: Commit** `feat: the three latest windows of a split-last app are split only when nothing else is free`.

### Task 3: The menu item, the pipe twin and the docs

**Files:**
- Modify: `src/Deskbar.Core/Control/RemoteControl.cs` (dispatch `("split-last", [var hwnd, var onOff])`), `RemoteControl.Windows.cs` (handler shaped like `RememberWorkspace`), `ControlUsage.cs` (`new("split-last", "<hwnd> on|off", "Split <app> windows last: a new window halves its three latest only when nothing else is free")`)
- Modify: `src/Deskbar.App/FloatingBar.xaml.cs` (a checkable "Split {ProcessName} windows last", glyph "⊟", just before "Don't split this tile". It is shown when the window is not ours; Click calls `manager.SetSplitLast(handle, item.IsChecked)` through `Report`)
- Modify: `README.md`, `docs/features/tiling.md`, `docs/superpowers/notes/manual-test-script.md` (S1 to S6)
- Test: `tests/Deskbar.Core.Tests/Control/RemoteControlSplitLastTests.cs`, `tests/Deskbar.Windows.Tests/FloatingBarSplitLastMenuTests.cs`

- [ ] **Step 1: Write the failing tests.**
  - Pipe: `on` persists the app, `off` removes it, `maybe` prints the usage line, an unknown hwnd answers "Window no longer exists.", and our own window is refused (Review Focus 4).
  - Menu: the item is there and unticked, ticked once the app is listed, and a click sets it.
- [ ] **Step 2:** Run both and watch them fail (the Windows one through `test-hidden.ps1`).
- [ ] **Step 3: Implement.** Docs:
  - README tiling paragraph: one sentence on "Split VS Code windows last".
  - tiling.md: the rule and the VS Code example from the spec.
  - Manual steps S1 to S6: tick the item, open from VS Code, open from a browser, the fourth VS Code window, only VS Code on screen 1, the pipe.
- [ ] **Step 4:** `dotnet build Deskbar.sln -c Release -warnaserror`, then `pwsh scripts/test-hidden.ps1`. Expected: 0 warnings, all green (one rerun allowed for the known `TileSurfaceTests` and `ControlPipeTests` flakes, named in the PR).
- [ ] **Step 5: Commit** `feat: split <app> windows last, from the icon menu and the pipe`, then the docs commit.
