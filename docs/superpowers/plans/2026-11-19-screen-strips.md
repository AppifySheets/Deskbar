# Screen strips implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A third place for the current workspace's titled window list: a strip along the top of every
screen, listing that screen's windows.

**Architecture:** The bar keeps building every window button. A thin `ScreenStrip` window per screen
owns a top-edge app bar registration and hosts the tabs the bar's rebuild hands it. `App` owns the set
of strips and reconciles it against the connected screens with a pure function from `Deskbar.Core`.

**Tech stack:** C# / .NET 10, WPF, Win32 app bars (`SHAppBarMessage`), CSharpFunctionalExtensions, Rx,
xUnit.

**Spec:** `docs/superpowers/specs/2026-11-19-screen-strips-design.md`. Read it before any task; this
plan does not repeat its reasoning.

## Global constraints

- Branch `feat/screen-strips`. Never push to main, never merge. No git worktrees.
- Read the repo's `CLAUDE.md` first: C# style (expressions over statement bodies, `var`, no `private`
  keyword, immutable, `Result` for expected failures, Rx over events, lambdas over loops), and long
  comments that carry the reason and Petre's words. Every new member gets such a comment.
- The strip is 32 DIP high, on `#FF141518`, square corners. Tabs are 150 DIP wide at most.
- App bars speak physical pixels. Convert DIPs with the strip's own screen's DPI.
- Static brushes and geometries are frozen.
- No test registers a real app bar, presses real keys, or moves real windows. Routine test command:
  `dotnet test Deskbar.sln --filter "Category!=Integration"`.
- Before the PR: `dotnet build Deskbar.sln -c Release -warnaserror`.
- A Debug build is run with `DESKBAR_APPDATA` set to a scratch folder only if the installed copy is not
  the Debug build Petre is already running; check which copy runs before starting another. He uses the
  app all day: stop, build and start in one command, never leave it down.
- No version edits. The PR carries a `## Release notes` section for users.
- Commits end with one italic line naming the model, nothing else.
- No real people's names anywhere, screenshots and commit messages included.

## Review focus

1. A screen that drops out for a moment on unlock: the strip must wait, and must never be docked onto
   the screen `MONITOR_DEFAULTTONEAREST` answers with. Test in task 5 (`StripHeld` top, other screen
   gives no verdict) and task 4 (transient empty layout changes nothing).
2. Rebuild re-entrancy: tabs handed to a strip while a rebuild is already running must not double.
   Test in task 7.
3. The bar hidden or minimized: tabs still refresh, strips stay docked and topmost. Test in task 7.
4. A workspace switch with a screen that has no windows: the strip keeps its height and shows only the
   name tag. Test in task 6.
5. An old `state.json` with `"WindowListAtBottom": true` and no new key still reads as the bottom
   list. Test in task 1.

---

### Task 0: Three measurements

No product code. Throwaway probes in the scratchpad, results written to
`docs/superpowers/notes/2026-11-19-screen-strips-probes.md` (what was run, the numbers, the conclusion).
PowerShell probes call `SetProcessDpiAwarenessContext(-4)` first and declare P/Invokes
`CharSet.Unicode`. Use a throwaway window of your own for every registration, never the running bar's.

- [ ] **Step 1: Top-edge app bars.** On each connected screen: `ABM_NEW`, `ABM_QUERYPOS`,
  `ABM_SETPOS` with `uEdge = 1` on a borderless layered window; record asked and granted rectangles and
  the work area before, during and after `ABM_REMOVE`. Repeat with a second throwaway window registered
  on the left edge of the same screen, in both registration orders, and after re-running `SETPOS` on
  each. Record which order gives "top strip the full width, left bar below it". Record whether a
  restored window whose title bar is under the new strip is moved by the shell.
- [ ] **Step 2: A bar-built button in another window.** On a scratch branch that is thrown away, host
  one `IconButton` result in a second WPF window, with `trace.on`. Record for each of: click, click on
  the active window, right-click menu, hover card position, candidate ring, drag out and drop on a bar
  row, a press whose release is orphaned. Name the bar fields each broken one depends on.
- [ ] **Step 3: A screen dropping out.** With throwaway top bars registered on every screen: lock and
  unlock, and (with Petre, at a moment he picks) unplug a screen. Record the `DisplaySettingsChanged`
  sequence, `GetMonitorInfo` for each bar's window through it, and where the shell leaves each bar.
- [ ] **Step 4: Checkpoint.** If step 1 cannot produce "strip across the top, docked bar below", or
  step 2 finds a behaviour that cannot be forwarded to the bar, stop and report to Petre with the
  numbers. Otherwise amend tasks 5, 8 and 9 below where the measurements pin a value they leave open
  (registration order, which members to forward, whether to nudge restored windows) and commit the
  notes: `docs: measurements for the screen strips`.

### Task 1: The setting has three values

**Files:**
- Create: `src/Deskbar.Core/Persistence/WindowListPlace.cs`
- Modify: `src/Deskbar.Core/Persistence/AppState.cs:178-196`, `src/Deskbar.Core/WorkspaceManager.cs:4085-4091`, both call sites in `src/Deskbar.App/FloatingBar.xaml.cs` (6895, 7033)
- Test: `tests/Deskbar.Core.Tests/WindowListPlaceTests.cs`, `tests/Deskbar.Core.Tests/JsonPersistenceStoreTests.cs`

**Interfaces:**
- Produces: `enum WindowListPlace { Row, Bottom, Screens }`;
  `AppState.WindowListOnScreens` (stored `bool`, default false);
  `[JsonIgnore] AppState.WindowListPlace` (`Screens` when `WindowListOnScreens`, else `Bottom` when
  `WindowListAtBottom`, else `Row`); `AppState.ExpandCurrentWorkspace` becomes
  `WindowListPlace == WindowListPlace.Row`;
  `Result WorkspaceManager.SetWindowListPlace(WindowListPlace place)`, a no-op when unchanged, writing
  both bools (`WindowListAtBottom` is left as it was when moving to `Screens`, so turning strips off
  from another build's point of view lands where the user was). `SetExpandCurrentWorkspace` is removed.

- [ ] **Step 1: Failing tests.**
  `Place_reads_row_by_default`, `Place_reads_bottom_from_an_old_file` (JSON holding only
  `"WindowListAtBottom": true` deserializes to `Bottom`), `Place_reads_screens_when_both_are_set`,
  `Setting_the_same_place_does_not_persist` (store's write count unchanged),
  `Setting_screens_keeps_the_bottom_choice` (from `Bottom` to `Screens` leaves `WindowListAtBottom`
  true), `Expand_is_true_only_for_row`.
- [ ] **Step 2: Run, expect compile failure on `WindowListPlace`.**
- [ ] **Step 3: Implement.** The two menu call sites keep compiling by calling
  `SetWindowListPlace(expanding ? WindowListPlace.Bottom : WindowListPlace.Row)` until task 3 replaces
  them.
- [ ] **Step 4: Run the Core tests, expect pass.**
- [ ] **Step 5: Commit** `feat: the window list setting can name a third place, the screens`.

### Task 2: The pipe command

**Files:**
- Modify: `src/Deskbar.Core/Control/ControlUsage.cs` (beside `window-lines`, line 94), `src/Deskbar.Core/Control/RemoteControl.cs:122`, `src/Deskbar.Core/Control/RemoteControl.Workspaces.cs` (beside `WindowLines`)
- Test: the existing control tests under `tests/Deskbar.Core.Tests` that cover `window-lines`; add beside them

**Interfaces:**
- Consumes: `SetWindowListPlace`.
- Produces: `window-list row|bottom|screens`.

- [ ] **Step 1: Failing tests.** `Window_list_screens_sets_the_place`, `Window_list_row_sets_the_place`,
  `Window_list_with_an_unknown_word_fails_with_its_usage_line`, and the existing "every dispatched
  command is in ControlUsage" test must pass with the new command.
- [ ] **Step 2: Run, expect fail.**
- [ ] **Step 3: Implement**, following `WindowLines(mode)`.
- [ ] **Step 4: Run, expect pass.**
- [ ] **Step 5: Commit** `feat: window-list on the control pipe`.

### Task 3: The menu

**Files:**
- Modify: `src/Deskbar.App/FloatingBar.xaml.cs` (6892-6898, 7033-7036, `AddSplitItem` at 7078)
- Test: `tests/Deskbar.Windows.Tests/FloatingBarWindowListMenuTests.cs`

**Interfaces:**
- Produces: `void AddWindowListItems(ContextMenu menu)` on `FloatingBar`, used by both menus: a parent
  item "Window list" (glyph `☰`) with three checkable children, exact headers "In the workspace's
  row", "At the bottom of the bar", "At the top of each screen", the current one checked.

- [ ] **Step 1: Failing tests.** `Both_menus_offer_the_three_places`, `The_current_place_is_checked`,
  `Choosing_a_place_sets_it`, `The_collapsed_click_item_is_offered_only_for_row`.
- [ ] **Step 2: Run, expect fail.**
- [ ] **Step 3: Implement.** `AddSplitItem`'s guard reads `WindowListPlace == Row` through
  `ExpandCurrentWorkspace` as now.
- [ ] **Step 4: Run, expect pass.**
- [ ] **Step 5: Commit** `feat: the window list item is a choice of three places`.

### Task 4: Which strips should exist, and how wide a tab is

**Files:**
- Create: `src/Deskbar.Core/Geometry/StripPlan.cs`
- Test: `tests/Deskbar.Core.Tests/StripPlanTests.cs`

**Interfaces:**
- Produces:
  `sealed record StripChanges(IReadOnlyList<MonitorBounds> Add, IReadOnlyList<MonitorBounds> Remove)`;
  `static StripChanges StripPlan.Reconcile(bool wanted, IReadOnlyList<MonitorBounds> screens, IReadOnlyList<MonitorBounds> existing)`;
  `static double StripPlan.TabWidth(double room, int tabs)` with constants `TabMax = 150`,
  `TabIconOnly = 30`; `static bool StripPlan.ShowsTitle(double tabWidth)` (true above `TabIconOnly + 12`).

- [ ] **Step 1: Failing tests.**
  `Not_wanted_removes_every_strip`; `Wanted_adds_one_per_screen`;
  `A_screen_that_left_loses_its_strip`; `A_screen_that_changed_size_is_a_remove_and_an_add`;
  `An_empty_screen_list_changes_nothing` (the transient no-screen moment keeps existing strips);
  `Nothing_changes_when_screens_match`;
  `TabWidth`: room 1000, 3 tabs gives 150; room 300, 3 tabs gives 100; room 300, 20 tabs gives 30;
  0 tabs gives 150.
- [ ] **Step 2: Run, expect fail.**
- [ ] **Step 3: Implement.** A screen's identity is its `MonitorBounds` record value.
- [ ] **Step 4: Run, expect pass.**
- [ ] **Step 5: Commit** `feat: the rule for which screens get a strip and how wide a tab is`.

### Task 5: The top edge, in `AppBar` and the guard

**Files:**
- Modify: `src/Deskbar.App/AppBar.cs:27,68-103`, `src/Deskbar.App/FloatingBar.DockGuard.cs:78-92`
- Test: `tests/Deskbar.Windows.Tests/DockGuardTests.cs`

**Interfaces:**
- Produces: `AppBar.Edge.Top = 1`; `Rect AppBar.Dock(Edge edge, Rect screen, int thicknessPx)` (for
  `Top`: asks for `screen.L, screen.T, screen.R, screen.T + thicknessPx`, and after `QUERYPOS`
  re-derives `B = T + thicknessPx`); `StripHeld("top", bar, work)` is `work.Top >= bar.Bottom`. The
  three-argument `StripHeld(edge, bar, monitor, work)` keeps returning null when the monitor does not
  contain the bar's centre.

- [ ] **Step 1: Failing tests** in `DockGuardTests`: `Top_strip_is_held_when_the_work_area_starts_below_it`,
  `Top_strip_is_not_held_when_the_work_area_reaches_under_it`,
  `Top_strip_on_another_screen_gives_no_verdict`.
- [ ] **Step 2: Run, expect fail.**
- [ ] **Step 3: Implement.** Existing left and right callers pass their width as `thicknessPx`.
- [ ] **Step 4: Run the Windows tests, expect pass.**
- [ ] **Step 5: Commit** `feat: an app bar can take the top edge`.

### Task 6: The strip window

**Files:**
- Create: `src/Deskbar.App/ScreenStrip.cs` (code-only window, no XAML)
- Test: `tests/Deskbar.Windows.Tests/ScreenStripTests.cs`

**Interfaces:**
- Consumes: `AppBar` with `Edge.Top`; `StripPlan.TabWidth`, `ShowsTitle`; `StripHeld`.
- Produces, on `sealed class ScreenStrip : Window`:
  - `ScreenStrip(MonitorBounds screen)`; `MonitorBounds Screen { get; }`; `const double HeightDip = 32`.
  - `void SetTabs(string name, Brush? colour, IReadOnlyList<FrameworkElement> tabs)`: the name tag
    (neutral grey `#FF3A3F49` when `colour` is null), then the tabs, each given
    `StripPlan.TabWidth(room, tabs.Count)`. Replaces what was there; safe to call with an empty list.
  - `Result Dock()`, `void Undock()` (sends `ABM_REMOVE`, then hides), `void KeepStrip(IReadOnlyList<MonitorBounds> connected)`
    (the 1 s check: same `SettleFor` 3 s and `RetryEvery` 10 s as the docked bar. Measured in task 0:
    it never asks Windows which screen it is on. It re-docks when its window is not inside its own
    `Screen`, or `StripHeld("top", …)` is false against its own screen's work area, and only while
    `Screen` is in `connected`; otherwise it does nothing),
  - Re-docks on `ABN_POSCHANGED` like the bar (an unchanged grant sends no further notification, task 0).
    `void ReclaimTopmost()`, `void StepAside(bool fullScreen)`.
  - `Action<WindowHandle>? Dropped { get; init; }`, raised for a window icon dropped anywhere on the
    strip (the payload format is `WindowDragSource`'s).
  - `Action? MenuRequested { get; init; }`, raised by a right-click on the name tag or free space.
  - `string Describe()`: screen, asked and granted rectangles, work area, registered, repairs; one line.
  - Window: borderless, `ShowInTaskbar` false, tool window (`WS_EX_TOOLWINDOW`, as the bar sets it at
    `FloatingBar.xaml.cs:429-446`), topmost, `Background #FF141518`.
  - An internal seam `Func<AppBar.Edge, AppBar.Rect, int, AppBar.Rect> DockWith { get; init; }` so
    tests never call the shell.

- [ ] **Step 1: Failing tests** (STA, like the other window tests):
  `An_empty_strip_shows_only_the_name_tag_at_32_dip`; `Tabs_are_150_wide_when_they_fit`;
  `Tabs_narrow_equally_when_they_do_not`; `SetTabs_twice_does_not_accumulate`;
  `A_null_colour_gives_the_neutral_tag`; `Right_click_on_the_tag_raises_MenuRequested`;
  `Dock_asks_for_the_top_of_its_own_screen_in_pixels` (through `DockWith`, at a faked 150% scale the
  thickness asked is 48); `KeepStrip_does_nothing_within_three_seconds_of_a_dock`;
  `A_full_screen_window_on_its_screen_takes_the_strip_out_of_topmost_and_its_closing_puts_it_back`
  (the `AppBar`'s `onFullScreen` callback calls `StepAside`).
- [ ] **Step 2: Run, expect fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run, expect pass.**
- [ ] **Step 5: Commit** `feat: the strip window for one screen's top edge`.

### Task 7: The tab, and the bar handing tabs over

**Files:**
- Create: `src/Deskbar.App/FloatingBar.Strips.cs` (a new partial, so the 8,000-line file grows by call sites only)
- Modify: `src/Deskbar.App/FloatingBar.xaml.cs` (`RebuildCore` near 2286-2303 and 2551, the `if (IsVisible) Rebuild()` gates, the places task 0 step 2 named)
- Test: `tests/Deskbar.Windows.Tests/FloatingBarStripsTests.cs`

**Interfaces:**
- Consumes: `ScreenStrip.SetTabs`, `Dropped`, `MenuRequested`; `WindowListPlace`.
- Produces, on `FloatingBar`:
  - `Func<IReadOnlyList<ScreenStrip>> Strips { get; init; } = () => [];`
  - `FrameworkElement StripTab(string groupLabel, WindowRow row, Brush accent)`: `IconButton(...)` with
    group key `CurrentListGroupKey`, then the tab dressing: icon left, title with `TitleActive` /
    `TitleResting` / `TitleBehind` by the rule in `Titled`, pin badge for a pinned window, a 2 DIP
    underline (`#FF3A3F49` at rest, `accent` and fill `#FF1F232B` when `row.IsActive`), title hidden
    when `!StripPlan.ShowsTitle(width)`.
  - `void FillStrips(...)`: called inside `RebuildCore`'s guarded section when the place is `Screens`.
    For each strip, the rows of the current workspace (or current unnamed desktop) whose monitor is
    that strip's screen, pinned rows included, in the order the titled list uses; then `SetTabs` with
    the workspace's name and colour. With the place elsewhere it does nothing.
  - `void ReclaimStripTopmost()`.
  - The forwarding task 0 found necessary (notes, section 2): `internal void NoteStripPress()` and
    `internal void NoteStripRelease()`, the press-state half of the bar's preview handlers only
    (`pressSeen`, `pressConsumedByChild`, `pressedRowKey`, `orphanRelease`, `dragJustFinished`), never
    `dragStart` or `resizing`; each strip calls them from its own `PreviewMouseLeftButtonDown` and
    `PreviewMouseLeftButtonUp`. `ShowHoverCard` places the card against the anchor, `Placement =
    Bottom`, when the anchor is not inside the bar (`!IsAncestorOf(anchor)`), and the hover watch treats
    a pointer over any strip like a pointer over the bar.
  - Tests added for these: `A_strip_press_then_release_is_not_an_orphan`,
    `A_hover_card_for_a_tab_opens_below_the_tab_without_throwing`.
  - Drop on a strip: named workspace calls `manager.AssignWindow(handle, workspaceId, screenIndex)`;
    unnamed desktop calls `manager.MoveWindowToMonitor(handle, monitorNumber)`.
- Behaviour changes in `RebuildCore`: with the place `Screens`, no row is expanded and `CurrentList`
  gets nothing. Rebuild triggers gated on `IsVisible` also run when the place is `Screens`; in that
  case a hidden bar skips building its own rows and only fills the strips.

- [ ] **Step 1: Failing tests.** `Screens_place_builds_no_list_and_expands_no_row`;
  `Each_strip_gets_its_own_screens_windows`; `A_screen_with_no_windows_gets_an_empty_tab_list_and_the_name`;
  `The_active_window_tab_carries_the_accent_underline`; `A_pinned_window_is_on_its_screens_strip_with_the_badge`;
  `A_minimized_bar_still_fills_the_strips`; `A_reentrant_rebuild_does_not_double_the_tabs`;
  `Clicking_a_tab_toggles_the_window_through_the_manager`;
  `A_drop_on_a_strip_assigns_to_the_current_workspace_on_that_screen`;
  `A_drop_on_a_strip_on_an_unnamed_desktop_moves_to_that_monitor`;
  `Other_places_fill_nothing`.
- [ ] **Step 2: Run, expect fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run, expect pass, and the whole Windows suite stays green.**
- [ ] **Step 5: Commit** `feat: the bar fills each screen's strip with that screen's windows`.

### Task 8: The set of strips, owned by App

**Files:**
- Create: `src/Deskbar.App/ScreenStrips.cs`
- Modify: `src/Deskbar.App/App.xaml.cs` (bar construction at 1430, the foreground subscription at 1497, the 1 s timer at 1530-1533, exit and restart paths, the debug dump)
- Test: `tests/Deskbar.Windows.Tests/ScreenStripsTests.cs`

**Interfaces:**
- Consumes: `StripPlan.Reconcile`, `ScreenStrip`, `FloatingBar.Strips`.
- Produces, on `sealed class ScreenStrips`:
  - `ScreenStrips(Func<bool> wanted, Func<IReadOnlyList<MonitorBounds>> screens, Func<MonitorBounds, ScreenStrip> create, Action<nint> ignore, Action<nint> pinToAllDesktops)`
  - `IReadOnlyList<ScreenStrip> Current { get; }`
  - `void Reconcile()`: applies `StripPlan.Reconcile`; a new strip gets `ignore` on its handle before
    its first show, `Dock()`, then `pinToAllDesktops` after the show; a removed one gets `Undock()`
    then `Close()`.
  - `void Tick()`: `KeepStrip()` and `ReclaimTopmost()` on each. `void RemoveAll()`.
  - `IEnumerable<string> Describe()`.
- Wiring in `App`: `Strips = () => strips.Current` on the bar; `Reconcile()` at start after the bar is
  built, when the manager's state changes the place, and from the bar's `SettleLayout` (expose
  `IObservable<Unit> LayoutSettled` on the bar, raised at the end of `SettleLayout`); after every
  reconcile that changed something, ask the bar to rebuild; `Tick()` on the 1 s timer and
  `ReclaimTopmost` on foreground change, neither gated on the bar's visibility; `RemoveAll()` before
  Exit and Restart close windows; the debug dump appends `Describe()`.

- [ ] **Step 1: Failing tests**, with a fake `create` returning strips whose `DockWith` is faked:
  `Turning_the_setting_on_creates_one_strip_per_screen`; `Turning_it_off_undocks_then_closes_each`;
  `A_new_strip_is_ignored_before_it_is_shown_and_pinned_after`;
  `A_settle_with_no_screens_keeps_the_strips`; `A_screen_that_left_takes_its_strip_with_it`;
  `RemoveAll_undocks_every_strip`.
- [ ] **Step 2: Run, expect fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run, expect pass.**
- [ ] **Step 5: Commit** `feat: strips follow the screens and the setting`.

### Task 9: The docked bar under the strip

**Files:**
- Modify: `src/Deskbar.App/FloatingBar.xaml.cs` (`DockTo` at 743 and its seam half), `src/Deskbar.App/ScreenStrips.cs`
- Test: `tests/Deskbar.Windows.Tests/FloatingBarDockedEdgeTests.cs`

**Interfaces (amended by task 0):** the shell already puts a side bar below a top bar in either order,
and the running bar already follows (measured, notes section 1), so no registration order is coded.
What is left:
- Produces: `static WindowRect? StripPlan.Clear(WindowRect window, MonitorBounds screen, int stripPx)`
  in `Deskbar.Core` (task 4's file): for a restored window whose top is above `screen.Top + stripPx`
  and whose centre is on that screen, the same rectangle moved down to start at `screen.Top + stripPx`;
  otherwise null. `ScreenStrips.Reconcile` applies it, once per created strip, to every restored,
  not-minimized window on that screen through `IScreenLayout.MoveTo(window, rect, mayChangeShowState: false)`.
  The window list and rects come in through a constructor argument so the tests fake them.

- [ ] **Step 1: Failing tests.** In `StripPlanTests`: `Clear_moves_a_window_under_the_strip_down`,
  `Clear_leaves_a_window_below_the_strip`, `Clear_leaves_a_window_on_another_screen`. In
  `ScreenStripsTests`: `A_new_strip_moves_restored_windows_under_it_once`,
  `A_strip_that_already_existed_moves_nothing`.
- [ ] **Step 2: Run, expect fail.**
- [ ] **Step 3: Implement.** Also check by reading `DockTo` and its seam half that each keeps the top
  and bottom its QUERYPOS returns (the left-screen half did, measured); fix and test whichever does not.
- [ ] **Step 4: Run, expect pass.**
- [ ] **Step 5: Commit** `feat: the docked bar sits under the strip`.

### Task 10: Traces, the manual script, the live check and the PR

**Files:**
- Modify: `src/Deskbar.App/ScreenStrip.cs` (trace), `docs/superpowers/notes/manual-test-script.md`, `CLAUDE.md` (one decision entry and the instrumentation list), `README.md` if it lists the window list's places

- [ ] **Step 1: Trace.** Each strip writes its `Describe()` line through `ClickTrace.Write` only when
  the line changes, plus one line per registration, repair and removal. Test:
  `An_unchanged_strip_writes_its_geometry_once` in `ScreenStripTests`.
- [ ] **Step 2: Manual script.** A section "Screen strips": turn on from the menu and from the pipe;
  dock the bar left, right and across the seam; switch workspaces and watch a maximized window's top
  edge not move; minimize the bar; lock and unlock; full-screen video on one screen; unplug a screen;
  RDP in; Exit and confirm every work area is back.
- [ ] **Step 3: Full check.** `dotnet test Deskbar.sln --filter "Category!=Integration"` green, then
  `dotnet build Deskbar.sln -c Release -warnaserror` clean.
- [ ] **Step 4: Live.** Swap the running Debug build in one stop-build-start command, turn the strips
  on, and hand it to Petre with the trace on. Tab width, paddings and the active fill are tuned here
  with him.
- [ ] **Step 5: PR.** Push the branch, open the PR against main with a `## Release notes` section
  ("The window list can now sit along the top of each screen: right-click the bar, Window list, At
  the top of each screen."), arm the base watch from `CLAUDE.md`, and say the watch ends with the
  session. Do not merge.
