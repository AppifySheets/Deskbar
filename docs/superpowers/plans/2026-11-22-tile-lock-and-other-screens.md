# Locked tiles and other screens on arrival: implementation plan (PR 1 of 2)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A tile can be locked so no new window ever splits it, and a new window takes an opening or an empty screen anywhere on the workspace before it halves a tile.

**Architecture:** The lock is an in-memory set of window handles held by the Tiler; locked tiles join the `skip` set `TileArrival.Choose` already takes for restoring maximized tiles. The new arrival steps live in `Tiler.Arrival.cs` (`Arrive`, `Split`). The bar's window icon menu and the control pipe both call one Tiler method.

**Tech Stack:** C# / .NET 10, WPF, xUnit, CSharpFunctionalExtensions, System.Reactive.

**Spec:** `docs/superpowers/specs/2026-11-22-tile-lock-and-other-screens-design.md`. This plan covers everything in it except "The pin on the window", which is PR 2.

## Global Constraints

- All work in the worktree `E:\repos\TaskSpaces\.claude\worktrees\tile-lock`, branch `feat/tile-lock`.
- Repo C# style: `private` implied, expression bodies, `var`, lambdas over loops, readonly collections, immutable state (swap an `IReadOnlySet`, do not mutate a shared one), `Result`/`Maybe` from CSharpFunctionalExtensions for expected failures.
- Every change carries long "why" comments in the style of the surrounding tiling code; every arrival decision writes one `tile: <workspace>: ...` trace line.
- Menu label, exactly: `Don't split this tile`. Pipe command, exactly: `lock-tile <hwnd> on|off`.
- Refusal text when locking a window that is not a tile: `That window is not in a tile.`
- A window dropped on a screen (`manager.TakeAimedScreen`) or restored from minimized (`unminimized`) never leaves its screen: no other-screen opening, no empty screen, no cross-screen fallback.
- Routine test command: `dotnet test Deskbar.sln --filter "Category!=Integration"`. Never run Integration tests.
- Before the PR: `dotnet build Deskbar.sln -c Release -warnaserror` must be clean.
- Commit messages end with the single italic line `*opus5.5*`; no Co-Authored-By trailer.

## Review Focus

1. Two windows arriving in one evaluate with one empty screen: the first takes the empty screen, the second must see it occupied and split a tile instead of also landing there alone. Test in Task 2.
2. A screen whose only window of this workspace is maximized (set aside, off the board) is not empty: a new window must not land under or over it. Test in Task 2.
3. A locked window that closes and whose handle Windows later reuses must not come back locked. Test in Task 1.
4. A window dropped on a screen whose tiles are all locked splits a locked tile there rather than jumping to another screen. Test in Task 1.
5. The existing icon menu test lists every item; the new item, hidden for a window that is not a tile, must not change what a user sees there. Test in Task 4.

---

### Task 1: The lock in the Tiler

**Files:**
- Create: `src/Deskbar.Core/Tiling/Tiler.Lock.cs`
- Modify: `src/Deskbar.Core/Tiling/Tiler.Arrival.cs` (`Split`, `Arrive`, `Why`), `src/Deskbar.Core/Tiling/Tiler.cs` (the Disappeared branch that calls `ForgetUsed`, around line 363; the class header's rule list gains one line)
- Test: `tests/Deskbar.Core.Tests/Tiling/TilerTests.Lock.cs` (a new part of the `TilerTests` partial class, using the harness in `TilerTests.cs` and the helpers `Uses`, `AssertArranged` from `TilerTests.Arrival.cs`)

**Interfaces:**
- Produces, on `Tiler`:
  - `public Maybe<bool> TileLockOf(WindowHandle window)`: None when the window is not on the current `Board`, otherwise whether it is locked.
  - `public Result SetTileLocked(WindowHandle window, bool locked)`: locking a window not on `Board` fails with `That window is not in a tile.`; unlocking always succeeds. Writes a trace line `tile: <workspace>: <hwnd> locked` / `unlocked`. Moves nothing.
  - private `IReadOnlySet<WindowHandle> locked`, swapped, never mutated in place.

- [ ] **Step 1: Write the failing tests** in `TilerTests.Lock.cs`:

```csharp
[Fact] public void A_locked_tile_last_used_is_not_split()
{   // W1 | W2, W1 used and locked: W3 splits W2 (the largest unlocked), below it.
    new[] { 1, 2 }.ToList().ForEach(n => Exists(W(n), alpha));
    Start(); Uses(W(1));
    Assert.True(tiler.SetTileLocked(W(1), true).IsSuccess);
    Opens(W(3), alpha);
    AssertArranged([W(1), W(2), W(3)], Columns([0.5, 0.5], [1.0], [0.5, 0.5]));
}

[Fact] public void When_every_tile_on_the_screen_is_locked_the_window_splits_an_unlocked_tile_on_another_screen()
{
    surface.ScreenList.Add(FakeTileSurface.Screen(2, Area2));
    Exists(W(1), alpha, screen: 1); Exists(W(2), alpha, screen: 2);
    Start(); Uses(W(1));
    Assert.True(tiler.SetTileLocked(W(1), true).IsSuccess);
    Opens(W(3), alpha, screen: 1);
    AssertArranged([W(1)], TileLayout.Automatic(1));
    AssertArranged([W(2), W(3)], Columns([0.5, 0.5], [1.0], [1.0]), Area2, screen: 2);
}

[Fact] public void When_every_tile_everywhere_is_locked_the_lock_is_ignored_for_that_window()
{
    Exists(W(1), alpha); Start(); Uses(W(1));
    Assert.True(tiler.SetTileLocked(W(1), true).IsSuccess);
    Opens(W(2), alpha);
    AssertArranged([W(1), W(2)], Columns([0.5, 0.5], [1.0], [1.0]));
}

[Fact] public void A_window_dropped_on_a_screen_of_locked_tiles_stays_there()
{   // Review focus 4. Screen 1: W1 locked; screen 2: W2 unlocked. W3 dropped on screen 1 splits W1.
    surface.ScreenList.Add(FakeTileSurface.Screen(2, Area2));
    Exists(W(1), alpha, screen: 1); Exists(W(2), alpha, screen: 2); Exists(W(3), beta, screen: 2);
    Start(); Uses(W(1));
    Assert.True(tiler.SetTileLocked(W(1), true).IsSuccess);
    manager.AssignWindow(W(3), alpha.Id, monitor: 1);
    AssertArranged([W(1), W(3)], Columns([0.5, 0.5], [1.0], [1.0]));
    AssertArranged([W(2)], TileLayout.Automatic(1), Area2, screen: 2);
}

[Fact] public void Locking_a_window_that_is_not_a_tile_is_refused()
{
    Exists(W(1), alpha); Start();
    var result = tiler.SetTileLocked(W(9), true);
    Assert.True(result.IsFailure);
    Assert.Equal("That window is not in a tile.", result.Error);
    Assert.True(tiler.TileLockOf(W(9)).HasNoValue);
}

[Fact] public void A_closed_window_is_forgotten_so_a_reused_handle_comes_back_unlocked()
{   // Review focus 3.
    Exists(W(1), alpha); Start();
    Assert.True(tiler.SetTileLocked(W(1), true).IsSuccess);
    Assert.Equal(Maybe.From(true), tiler.TileLockOf(W(1)));
    Closes(W(1));
    Opens(W(1), alpha);
    Assert.Equal(Maybe.From(false), tiler.TileLockOf(W(1)));
}
```

- [ ] **Step 2: Run them and see them fail**

Run: `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~TilerTests&Category!=Integration"`
Expected: build error, `TileLockOf` and `SetTileLocked` do not exist.

- [ ] **Step 3: Implement**
  - `Tiler.Lock.cs`: the field, `TileLockOf`, `SetTileLocked`, and `void ForgetLock(WindowHandle window)`. Neither public method goes through `Run` (no re-tile follows; the set is swapped atomically, so a call pumped in mid-evaluate is safe). Header comment: what the lock covers (only being split, spec "Non-goals"), why handles are not persisted (spec "The lock").
  - `Tiler.cs`: call `ForgetLock` next to `ForgetUsed` on Disappeared.
  - `Tiler.Arrival.cs` `Split`: the skip set becomes the screen's maximized tiles plus its locked tiles.
  - `Tiler.Arrival.cs` `Arrive`: before calling `Split` on the target screen, when the arrival is NOT aimed and every tile on the target screen is locked (and there is at least one), pick another screen: of the other screens with an unlocked tile, the one whose largest unlocked tile has the largest pixel area (its slot from `SlotsOf`), ties to the lower screen number. Split there with `lastUsed` None. If no other screen has one, split on the target screen as today (Choose then ignores the skip). `Arrive` needs to know whether the arrival was aimed: pass `aimed[window].HasValue` from `Arrived` as a new `bool aimed` parameter.
  - `Why`: when the last-used tile was skipped because it is locked, say `the tile last used (<hwnd>) is locked`; the cross-screen case traces `every tile on screen <n> is locked, split <hwnd> on screen <m>`.

- [ ] **Step 4: Run the tests and see them pass**

Run: `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~TilerTests&Category!=Integration"`
Expected: all pass, the existing arrival tests included.

- [ ] **Step 5: Commit**

```bash
git add src/Deskbar.Core/Tiling tests/Deskbar.Core.Tests/Tiling/TilerTests.Lock.cs
git commit -m "feat: a locked tile is never split by a new window"
```

---

### Task 2: A new window looks at the other screens first

**Files:**
- Modify: `src/Deskbar.Core/Tiling/Tiler.Arrival.cs` (`Arrive`, its header comment's numbered steps, and the file header)
- Test: `tests/Deskbar.Core.Tests/Tiling/TilerTests.OtherScreens.cs` (new `TilerTests` part)

**Interfaces:**
- Consumes: `bool aimed` on `Arrive` from Task 1; `aside` (the set-aside maximized windows, `Tiler.cs`).
- Produces: nothing public.

The new order inside `Arrive` for an arrival that is not aimed (spec "Behaviour", steps 3 to 6): opening on the target screen, opening on its own screen, opening on each other reported screen in number order, then an empty screen (own screen first, then the others in number order), then the split as Task 1 left it. An aimed arrival keeps today's order on its own screen only. A screen is empty when `board.On(n)` is empty and no window in `aside` is on it (`surface.ScreenOf`). Taking an empty screen is `board.Join(n, window)` and traces `took empty screen <n>`; another screen's opening traces `took the opening at slot <s> on screen <n>, another screen`.

- [ ] **Step 1: Write the failing tests** in `TilerTests.OtherScreens.cs`:

```csharp
[Fact] public void A_new_window_takes_an_empty_screen_before_splitting_a_tile()
{
    surface.ScreenList.Add(FakeTileSurface.Screen(2, Area2));
    Exists(W(1), alpha, screen: 1); Start(); Uses(W(1));
    Opens(W(2), alpha, screen: 1);
    AssertArranged([W(1)], TileLayout.Automatic(1));
    AssertArranged([W(2)], TileLayout.Automatic(1), Area2, screen: 2);
}

[Fact] public void Its_own_empty_screen_comes_before_another_empty_screen()
{   // Screens 2 and 3 empty, the window opened on 3: it stays on 3.
    surface.ScreenList.Add(FakeTileSurface.Screen(2, Area2));
    surface.ScreenList.Add(FakeTileSurface.Screen(3, Area3));
    Exists(W(1), alpha, screen: 1); Start(); Uses(W(1));
    Opens(W(2), alpha, screen: 3);
    Assert.Equal([W(2)], tiler.Board.On(3));
    Assert.Empty(tiler.Board.On(2));
}

[Fact] public void Of_several_other_empty_screens_the_lowest_numbered_is_taken()
{
    surface.ScreenList.Add(FakeTileSurface.Screen(2, Area2));
    surface.ScreenList.Add(FakeTileSurface.Screen(3, Area3));
    Exists(W(1), alpha, screen: 1); Start(); Uses(W(1));
    Opens(W(2), alpha, screen: 1);
    Assert.Equal([W(2)], tiler.Board.On(2));
}

[Fact] public void A_screen_showing_a_maximized_window_of_the_workspace_is_not_empty()
{   // Review focus 2.
    surface.ScreenList.Add(FakeTileSurface.Screen(2, Area2));
    Exists(W(1), alpha, screen: 1); Exists(W(2), alpha, screen: 2);
    surface.Maximized.Add(W(2));
    Start(); Uses(W(1));
    Opens(W(3), alpha, screen: 1);
    AssertArranged([W(1), W(3)], Columns([0.5, 0.5], [1.0], [1.0]));
    Assert.Empty(tiler.Board.On(2));
}

[Fact] public void An_opening_on_another_screen_is_filled_before_a_tile_is_split()
{   // Screen 2: W2 | W3 with keep-space; W3 closes and leaves an opening. W4 opens on screen 1 and fills it.
    surface.ScreenList.Add(FakeTileSurface.Screen(2, Area2));
    Exists(W(1), alpha, screen: 1);
    new[] { 2, 3 }.ToList().ForEach(n => Exists(W(n), alpha, screen: 2));
    Start();
    Assert.True(manager.SetTileKeepsSpace(alpha.Id, true).IsSuccess);
    Closes(W(3)); Uses(W(1));
    Opens(W(4), alpha, screen: 1);
    AssertArranged([W(1)], TileLayout.Automatic(1));
    Assert.Equal([W(2), W(4)], tiler.Board.On(2));
}

[Fact] public void A_window_dropped_on_a_screen_does_not_go_to_an_empty_one()
{
    surface.ScreenList.Add(FakeTileSurface.Screen(2, Area2));
    Exists(W(1), alpha, screen: 1); Exists(W(3), beta, screen: 1);
    Start(); Uses(W(1));
    manager.AssignWindow(W(3), alpha.Id, monitor: 1);
    AssertArranged([W(1), W(3)], Columns([0.5, 0.5], [1.0], [1.0]));
    Assert.Empty(tiler.Board.On(2));
}

// A window restored from minimized with no remembered place keeps the screen Windows restored it to, even with
// another screen empty. Set it up exactly as the existing restored-from-minimized test in TilerTests.Arrival.cs
// (the one quoting "i had a chrome window in the deskbar workspace minimized in the left screen"), with screen 2
// empty instead of holding a tile, and assert the restored window is on screen 1 and screen 2 is still empty.
[Fact] public void A_window_restored_from_minimized_does_not_go_to_an_empty_screen() { /* as described above */ }

[Fact] public void Two_windows_in_one_evaluate_do_not_both_take_one_empty_screen()
{   // Review focus 1. Both open while the user is away, so they arrive in one evaluate on the way back (the
    // batching TilerTests.Arrival.cs uses for "a leave and an arrival in one evaluate"). W2 takes the empty screen 2;
    // W3 then finds no empty screen and splits the largest tile of its own screen, W1.
    surface.ScreenList.Add(FakeTileSurface.Screen(2, Area2));
    Exists(W(1), alpha, screen: 1); Start(); Uses(W(1));
    SwitchTo(beta);
    Opens(W(2), alpha, screen: 1);
    Opens(W(3), alpha, screen: 1);
    SwitchTo(alpha);
    AssertArranged([W(1), W(3)], Columns([0.5, 0.5], [1.0], [1.0]));
    AssertArranged([W(2)], TileLayout.Automatic(1), Area2, screen: 2);
}
```

`Area3` is a new `static readonly WindowRect Area3 = new(3840, 0, 5760, 1040);` beside `Area2` in `TilerTests.cs`.

- [ ] **Step 2: Run them and see them fail**

Run: `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~TilerTests&Category!=Integration"`
Expected: the new tests fail (the windows split on screen 1).

- [ ] **Step 3: Implement the new order in `Arrive`** and rewrite the numbered steps in the `Tiler.Arrival.cs` header to the spec's list.

- [ ] **Step 4: Run the whole core suite**

Run: `dotnet test tests/Deskbar.Core.Tests --filter "Category!=Integration"`
Expected: the new tests pass. Any older test that now fails because an empty second screen takes the window is checked against the spec: if the new rule explains the change, update its expectation and say why in its comment; if not, it is a bug in Step 3. List every updated test in the commit message.

- [ ] **Step 5: Commit**

```bash
git add src/Deskbar.Core/Tiling tests/Deskbar.Core.Tests/Tiling
git commit -m "feat: a new tile takes an opening or an empty screen anywhere before splitting one"
```

---

### Task 3: `lock-tile` on the control pipe

**Files:**
- Modify: `src/Deskbar.Core/Control/RemoteControl.cs` (constructor parameter, header comment, dispatch arm), `src/Deskbar.Core/Control/RemoteControl.Windows.cs` (the handler beside `RememberWorkspace`), `src/Deskbar.Core/Control/ControlUsage.cs` (one line beside `remember-workspace`), `src/Deskbar.App/App.xaml.cs` (the `new Core.Control.RemoteControl(...)` call around line 1419)
- Test: `tests/Deskbar.Core.Tests/Control/RemoteControlTileLockTests.cs`

**Interfaces:**
- Consumes: `Tiler.SetTileLocked(WindowHandle, bool) : Result` (Task 1).
- Produces: `RemoteControl(..., Func<WindowHandle, bool, Result>? setTileLocked = null)` as the last constructor parameter. Null refuses with `Tiling is not running.`

- [ ] **Step 1: Write the failing tests**: `lock-tile <hwnd> on` calls the delegate with `(handle, true)` and exits `ControlReply.Ok`; `off` with `false`; a failing delegate's error is the reply's output and the exit code is not Ok; no delegate refuses with `Tiling is not running.`; `lock-tile <hwnd> maybe` fails with `ControlUsage.LineFor("lock-tile")`; `ControlUsage.Text` contains `lock-tile`. Use a window known to a `WorkspaceManager` built as in `RemoteControlKeepSpaceTests`, with the hwnd written the way `windows` prints it.

- [ ] **Step 2: Run and see them fail**: `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~RemoteControlTileLockTests"`, expected build error.

- [ ] **Step 3: Implement**: `("lock-tile", [var hwnd, var onOff]) => LockTile(hwnd, onOff)`, the handler shaped like `RememberWorkspace` through `OnWindow`; usage line `new("lock-tile", "<hwnd> on|off", "Don't split this tile: a new window never takes half of it")`; App passes `setTileLocked: (handle, on) => tiler?.SetTileLocked(handle, on) ?? Result.Failure("Tiling is not running.")`.

- [ ] **Step 4: Run** `dotnet test tests/Deskbar.Core.Tests --filter "Category!=Integration"`, expected all pass, including the test that fails when a dispatched command is missing from `ControlUsage`.

- [ ] **Step 5: Commit** `feat: lock-tile on the control pipe`.

---

### Task 4: "Don't split this tile" on the window icon menu

**Files:**
- Modify: `src/Deskbar.App/FloatingBar.Tiling.cs` (the two seams and a `MenuItem TileLockItem(WindowHandle window)` builder), `src/Deskbar.App/FloatingBar.xaml.cs` (`IconMenu`: add the item last before the closing group's separator, refresh it in the existing `menu.Opened` handler), `src/Deskbar.App/App.xaml.cs` (wire the seams where `IsTiled` is wired, around line 1669)
- Test: `tests/Deskbar.Windows.Tests/FloatingBarTileLockMenuTests.cs`; modify `tests/Deskbar.Windows.Tests/FloatingBarRebuildTests.cs` (`Icon_right_click_menu_offers_naming_then_closing` and the frame-host test assert on VISIBLE items)

**Interfaces:**
- Consumes: `Tiler.TileLockOf`, `Tiler.SetTileLocked` (Task 1).
- Produces on `FloatingBar`:
  - `internal Func<WindowHandle, Maybe<bool>> TileLockOf { get; init; } = _ => Maybe<bool>.None;`
  - `internal Func<WindowHandle, bool, Result> SetTileLocked { get; init; } = (_, _) => Result.Failure("Tiling is not running.");`

The menu is filled once and kept (`FloatingBar.LazyMenus.cs`), so the item is always added; its `Visibility` (Collapsed when `TileLockOf` is None) and `IsChecked` are set when it is built and again on `menu.Opened`. `IsCheckable = true`, header `Don't split this tile`, glyph `MenuGlyph("⊞")` (the tiling items' glyph). Click reads the state at click time and calls `Report(SetTileLocked(handle, !locked))`, using Click and not Checked/Unchecked for the reason `KeepSpaceItem` gives.

- [ ] **Step 1: Write the failing tests** (`StaThread.Run`, `Harness.Build()`, a bar built like `FloatingBarTilingMenuTests.ShowBar` with the seams set):
  - with `TileLockOf = _ => Maybe.From(false)` the icon menu has a visible, checkable, unticked `Don't split this tile`;
  - with `Maybe.From(true)` it is ticked;
  - with the default seam the item is Collapsed (Review focus 5);
  - clicking it calls `SetTileLocked` with the icon's handle and `true`.

- [ ] **Step 2: Run and see them fail**: `dotnet test tests/Deskbar.Windows.Tests --filter "FullyQualifiedName~FloatingBarTileLockMenuTests"`.

- [ ] **Step 3: Implement**, then change the two `FloatingBarRebuildTests` menu assertions to `.Where(item => item.Visibility == Visibility.Visible)` with a comment saying why (the tile item exists on every icon, hidden unless the window is a tile).

- [ ] **Step 4: Run** `dotnet test Deskbar.sln --filter "Category!=Integration"`, expected all green.

- [ ] **Step 5: Commit** `feat: Don't split this tile on the window icon menu`.

---

### Task 5: Docs, release build and the pull request

**Files:**
- Modify: `docs/superpowers/notes/manual-test-script.md` (a tiling step for each new behaviour: lock via the menu, lock via `deskbar.cmd lock-tile`, empty screen taken, opening on another screen taken, a drop staying on its screen), `README.md` (one sentence in the tiling section on locking and on new windows using an empty screen first, if that section describes arrival).

- [ ] **Step 1:** Write the doc changes and commit them: `docs: locked tiles and other screens in the manual test script`.
- [ ] **Step 2:** `dotnet build Deskbar.sln -c Release -warnaserror`, expected `0 Warning(s)  0 Error(s)`.
- [ ] **Step 3:** `dotnet test Deskbar.sln --filter "Category!=Integration"`, expected all pass; note the count.
- [ ] **Step 4:** Push `feat/tile-lock` and open the PR with a `## Release notes` section written for users, links to the spec and plan, ending with `*opus5.5*`.
- [ ] **Step 5:** Watch the PR's CI run to green (`gh run watch`), and arm the base-watch Monitor from CLAUDE.md; run `refresh-pr` when main moves.
