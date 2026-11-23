# Tasks Panel Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The tasks window becomes a full-height panel docked just inside the bar, on every virtual desktop, with a
search box and a "Claude working" filter, slim lines below 620 DIP and the Matrix only from 780 DIP.

**Architecture:** The pure decisions (search, Claude-working narrowing, the panel's width rules, where it docks) live
in Deskbar.Core with unit tests. A new `TasksPanel` class in Deskbar.App owns the panel's `AppBar` and its order
behind the bar, driven by a notification FloatingBar raises each time its own app bar registers. TasksWindow keeps
its content and loses its title bar; TasksBoard becomes the panel's controller for open, close and the new filters.

**Tech Stack:** C# / .NET 10, WPF, Win32 app bars (`SHAppBarMessage`), Rx, CSharpFunctionalExtensions, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-23-tasks-panel-design.md`

## Global Constraints

- The bar is always the outer app bar; the panel is always registered AFTER the bar's app bar, every time the bar's registers.
- Panel width: default 460 DIP, minimum 360 DIP; slim lines below 620 DIP; Matrix switch shown only at 780 DIP or wider.
- No floating mode: the panel replaces the floating tasks window. `AppState.TasksWindow` is ignored from now on.
- The panel is pinned to every virtual desktop, topmost, WS_EX_TOOLWINDOW, not in Alt+Tab or the taskbar, never tiled.
- Search: case-insensitive, every typed word must match, across title, status line and question; not persisted.
- "Claude working": only tasks with at least one live session (`TaskItem.HasLiveSession`).
- Filters (status, workspace, goal, assignee, search, Claude working) all reset on a fresh open, as today.
- Never register a real app bar in a test, and never run Deskbar.Windows.Tests on Petre's desktop: `pwsh scripts/test-hidden.ps1`.
- Core tests: `dotnet test tests/Deskbar.Core.Tests --filter "Category!=Integration"`.
- Before the PR: `dotnet build Deskbar.sln -c Release -warnaserror`.
- C# style per Petre's CLAUDE.md: expression-bodied members, `var`, immutable records, readonly collections, Result/Maybe for expected failures, Rx over events, long comments carrying the why.
- Trace lines only behind `ClickTrace.On`, written when they change.
- Commits end with the single line `*opus5.5*`; no other attribution.

## Review Focus

1. Explorer restarts while the panel is open: both re-register, and the bar must still end up outside. Task 5 pins it with a fake dock seam.
2. Deskbar exits while the panel is open: its strip must be given back (ABM_REMOVE on shutdown) and its "open" remembered. Task 5 and Task 6 pin it.
3. A width out of range in state.json (hand-edited, or a screen narrower than 720 DIP): clamped to [360, half the screen's width], never wider than half. Task 2 pins it.
4. Search text with mixed case, extra spaces, or only spaces: matched word by word; blank means no search. Task 1 pins it.
5. The bar minimized and restored with the panel open: the panel gives its strip back and returns. Task 5 pins it.

---

### Task 0: Probe the shell's order on one edge (controller, not a subagent)

Not on Petre's screens. In Windows Sandbox if it is enabled (`Get-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM`), otherwise skip and note it.

- [ ] **Step 1:** A throwaway PowerShell probe registers app bar A then B on the right edge, prints both granted rects, removes and re-registers A, prints again.
- [ ] **Step 2:** Record the result in `docs/superpowers/notes/2026-11-23-tasks-panel-probes.md`. Either answer keeps the design; it only tells whether path 1 and 3 of the spec's "Keeping the bar outside" are needed or only belt and braces. If Sandbox is unavailable, the note says so and the design's rule (always re-dock the panel after the bar registers) stands.

### Task 1: Search and Claude-working narrowing (Core)

**Files:**
- Create: `src/Deskbar.Core/Tasks/TaskSearch.cs`
- Test: `tests/Deskbar.Core.Tests/Tasks/TaskSearchTests.cs`

**Interfaces:**
- Produces: `public sealed record TaskSearch(string Text)` with `static readonly TaskSearch None`, `bool IsBlank`, `bool Admits(TaskItem task)`, `TaskBoard Narrow(TaskBoard board)`; and `public static class ClaudeWorking` with `TaskBoard Narrow(TaskBoard board, bool on)`. Both shaped like `TaskGoalFilter.Narrow` (a blank search or `on: false` returns the same board instance).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Every_word_must_match_somewhere_in_title_status_line_or_question()
// task: Title "Store submission for 2.6", StatusLine "waiting on certification", Question "Which price tier?"
// "store tier" admits, "STORE   Certification" admits, "store video" does not.
[Fact] public void A_blank_search_admits_everything_and_narrows_nothing()
// new TaskSearch("   ").IsBlank; Narrow(board) is the same instance (Assert.Same).
[Fact] public void A_task_with_no_question_is_matched_on_title_and_status_line()
[Fact] public void Claude_working_keeps_only_tasks_with_a_live_session()
// one task with an ended session, one with a live one, one note: on => only the live one; off => Assert.Same(board, result).
```

- [ ] **Step 2:** Run `dotnet test tests/Deskbar.Core.Tests --filter FullyQualifiedName~TaskSearchTests`; expect a compile failure.
- [ ] **Step 3:** Implement. Words split on whitespace, compared with `StringComparison.OrdinalIgnoreCase` and `Contains`.
- [ ] **Step 4:** Run the same command; expect PASS.
- [ ] **Step 5:** Commit `feat: search and Claude-working narrowing for the tasks board`.

### Task 2: The panel's state and width rules (Core)

**Files:**
- Modify: `src/Deskbar.Core/Persistence/AppState.cs` (beside `TasksWindowState`, line ~784, and the `TasksWindow` property, line ~502)
- Modify: `src/Deskbar.Core/WorkspaceManager.cs` (beside `SaveTasksWindow`, line ~4377)
- Create: `src/Deskbar.Core/Tasks/TasksPanelLayout.cs`
- Test: `tests/Deskbar.Core.Tests/Tasks/TasksPanelLayoutTests.cs`, and a round trip in `tests/Deskbar.Core.Tests/JsonPersistenceStoreTests.cs`

**Interfaces:**
- Produces: `public sealed record TasksPanelState(bool Open, double Width)`; `AppState.TasksPanel { get; init; }` (nullable; null reads as closed at 460); `WorkspaceManager.SaveTasksPanel(TasksPanelState state) : Result`.
- Produces: `public static class TasksPanelLayout` with `const double DefaultWidth = 460, MinWidth = 360, SlimBelow = 620, MatrixFrom = 780`; `double Clamp(double width, double screenWidthDips)` (to [MinWidth, max(MinWidth, screenWidthDips / 2)]); `bool Slim(double width)`; `bool MatrixAllowed(double width)`.
- `AppState.TasksWindow` and `SaveTasksWindow` stay readable for old files but nothing writes them after Task 6; mark both obsolete in a comment, not `[Obsolete]` (warnaserror).

- [ ] **Step 1: Write the failing tests**

```csharp
[Theory]
[InlineData(100, 2560, 360)] [InlineData(460, 2560, 460)] [InlineData(5000, 2560, 1280)] [InlineData(460, 600, 360)]
public void The_width_is_kept_between_the_minimum_and_half_the_screen(double width, double screen, double expected)
[Theory] [InlineData(619.9, true)] [InlineData(620, false)] public void Lines_are_slim_below_620(double w, bool slim)
[Theory] [InlineData(779.9, false)] [InlineData(780, true)] public void The_Matrix_is_offered_from_780(double w, bool ok)
[Fact] public void The_panel_state_survives_a_save_and_load()   // in JsonPersistenceStoreTests: Open true, Width 512.5
[Fact] public void A_file_without_a_panel_state_reads_as_closed_at_the_default_width()
```

- [ ] **Step 2:** Run the Core tests filtered to these names; expect compile failure.
- [ ] **Step 3:** Implement. `SaveTasksPanel` follows `SaveTasksWindow`'s shape (state replaced, persisted, StateChanged pulsed).
- [ ] **Step 4:** Run; expect PASS. Then the whole Core suite; expect PASS.
- [ ] **Step 5:** Commit `feat: the tasks panel's remembered state and width rules`.

### Task 3: Where the panel docks (Core)

**Files:**
- Create: `src/Deskbar.Core/Tasks/TasksPanelPlacement.cs`
- Test: `tests/Deskbar.Core.Tests/Tasks/TasksPanelPlacementTests.cs`

**Interfaces:**
- Produces: `public enum PanelEdge { Left, Right }`; `public sealed record ScreenBox(int Left, int Top, int Right, int Bottom)` (physical pixels; reuse an existing Core rectangle record if one fits, and say so in the report); `public sealed record PanelPlacement(PanelEdge Edge, ScreenBox Screen)`; `public static PanelPlacement TasksPanelPlacement.For(string? barEdge, ScreenBox barScreen, Maybe<ScreenBox> seamRightScreen, int barMiddleX)`.
- Rules (spec, "Where it docks"): `"left"` → Left of barScreen; `"right"` → Right of barScreen; `"seam"` → Left of seamRightScreen (barScreen when it has no value); null (bar floating) → the edge of barScreen nearer barMiddleX, Right on a tie.

- [ ] **Step 1: Write the failing tests:** one `[Fact]` per rule above, plus `A_floating_bar_exactly_in_the_middle_docks_the_panel_right`.
- [ ] **Step 2:** Run; expect compile failure.
- [ ] **Step 3:** Implement as one switch expression.
- [ ] **Step 4:** Run; expect PASS.
- [ ] **Step 5:** Commit `feat: where the tasks panel docks beside the bar`.

### Task 4: The bar tells when its strip registered or went

**Files:**
- Modify: `src/Deskbar.App/AppBar.cs` (Dock, lines ~74 on)
- Modify: `src/Deskbar.App/FloatingBar.xaml.cs` (DockTo ~line 760, DockAcrossSeam ~line 845, LetGoOfStrip ~line 735, minimize path)
- Modify: `src/Deskbar.App/FloatingBar.DockGuard.cs` (KeepDockedStrip)

**Interfaces:**
- Produces: `AppBar.RegisteredOnLastDock : bool` (true when that Dock sent ABM_NEW and it succeeded).
- Produces: `internal sealed record BarStrip(string? Edge, nint Monitor, int MiddleX)` and `FloatingBar.Strip : IObservable<BarStrip>`. It emits after every DockTo or DockAcrossSeam whose app bar `RegisteredOnLastDock` (Edge "left", "right" or "seam"), after any dock whose edge or monitor differs from the last one emitted (a display change can move the bar to another screen without a new registration), and with `Edge: null` when the strip is let go (LetGoOfStrip, minimize). A plain re-dock after a position change, same edge and monitor, no ABM_NEW, emits NOTHING; that is what stops the bar's and the panel's re-docks answering each other.
- Also: `FloatingBar.Minimized` stays as is; the panel reads minimize through `Strip` emitting `Edge: null`.

- [ ] **Step 1:** Add `RegisteredOnLastDock` to AppBar, set in Dock from the ABM_NEW answer, false on every Dock that did not send it. Comment why (the panel's order).
- [ ] **Step 2:** Add the `Subject<BarStrip>` to FloatingBar, emit at the three points, complete it on close.
- [ ] **Step 3:** Build: `dotnet build Deskbar.sln`; expect success. No unit test can register a real app bar (Global Constraints); the emission points are reviewed by reading and exercised by Task 5's fakes and the manual script (Task 9).
- [ ] **Step 4:** Commit `feat: the bar announces when its docked strip registers or goes`.

### Task 5: TasksPanel, the panel's app bar and its order

**Files:**
- Create: `src/Deskbar.App/TasksPanel.cs`
- Test: `tests/Deskbar.Windows.Tests/TasksPanelTests.cs`

**Interfaces:**
- Consumes: `BarStrip`, `FloatingBar.Strip` (Task 4); `TasksPanelPlacement.For`, `PanelPlacement` (Task 3); `TasksPanelLayout.Clamp` (Task 2).
- Produces: `internal sealed class TasksPanel : IDisposable` with
  - seams, as `ScreenStrip` has them: `internal Func<PanelEdge, ScreenBox, int, ScreenBox> DockWith { get; init; }` (thickness in physical pixels, returns the granted rect), `internal Action Undock { get; init; }`, `internal Func<nint, double> ScaleOf { get; init; }` (monitor → DPI scale), `internal Func<ScreenBox, ScreenBox> WorkAreaOf { get; init; }`, `internal Func<DateTime> Clock { get; init; }`;
  - `TasksPanel(Window window, IObservable<BarStrip> barStrip, Func<BarStrip, PanelPlacement> placementOf)`;
  - `void Show(double widthDips)`, `void Hide()`, `bool IsShown`, `void SetWidth(double widthDips)` (re-docks once), `void Keep()` (the 1 s guard), `void Shutdown()` (removes the strip);
  - `IObservable<bool> Shown` (for the bar's "on" look and persistence).
- Behaviour:
  - Show: places with the last `BarStrip`'s placement, calls `Undock` then `DockWith`, moves the window into the granted rect (DIPs via `ScaleOf`), shows it topmost.
  - A `BarStrip` with an edge while shown: `Undock`, then `DockWith` again (after the bar, always). One with `Edge: null` while shown: `Undock`, hide the window, remember it was shown, and show again on the next `BarStrip` with an edge.
  - Its own position-changed callback (ABN_POSCHANGED) re-asks `DockWith` without `Undock`.
  - Keep(): with `FloatingBar.SettleFor` and `RetryEvery`, if `WorkAreaOf(screen)` reaches under the granted rect, `Undock` and `DockWith`; traced.
  - Full screen: the AppBar's onFullScreen drops and restores topmost, as the bar's `OnFullScreenApp`.
  - Trace lines behind `ClickTrace.On`: dock, re-dock (with the reason: bar registered, position changed, guard), removal, granted rect and the bar's edge, written when they change.
  - The production seams wrap an `AppBar` created on the window's handle; the panel's window is pinned to every desktop by the App (Task 6), not here.

- [ ] **Step 1: Write the failing tests** (fake seams record every call in order):

```csharp
[Fact] public void Showing_docks_on_the_bar_edge_with_the_width_in_physical_pixels()   // scale 1.5, 460 DIP => DockWith(Right, screen, 690)
[Fact] public void When_the_bar_registers_again_the_panel_undocks_and_docks_after_it() // emit BarStrip("right") => calls: Undock, DockWith
[Fact] public void After_an_Explorer_restart_the_panel_docks_only_after_the_bar()       // review focus 1: no DockWith until a BarStrip arrives
[Fact] public void A_bar_strip_that_goes_takes_the_panel_strip_with_it_and_brings_it_back() // review focus 5
[Fact] public void Hiding_gives_the_strip_back()
[Fact] public void Shutdown_gives_the_strip_back_while_shown()                          // review focus 2
[Fact] public void The_guard_waits_out_the_settle_time_and_then_docks_again_when_the_work_area_runs_under_the_panel()
[Fact] public void A_width_below_the_minimum_docks_at_the_minimum()
```

- [ ] **Step 2:** Run `pwsh scripts/test-hidden.ps1 -Arguments 'tests/Deskbar.Windows.Tests --filter FullyQualifiedName~TasksPanelTests'`; expect compile failure.
- [ ] **Step 3:** Implement `TasksPanel`.
- [ ] **Step 4:** Run the same command; expect PASS.
- [ ] **Step 5:** Commit `feat: the tasks panel docks beside the bar and keeps behind it`.

### Task 6: The window becomes the panel, and the App wires it

**Files:**
- Modify: `src/Deskbar.App/TasksWindow.xaml` (window attributes, toolbar close button)
- Modify: `src/Deskbar.App/TasksWindow.xaml.cs` (Open/Dismiss/CurrentPlace/MaximizeIfRemembered, the width grip)
- Modify: `src/Deskbar.App/TasksWindow.Matrix.cs` (remove `GrowForMatrix`, `listNormal`, `grownNormal`, `ListMinWidth`'s use as MinWidth)
- Modify: `src/Deskbar.App/TasksBoard.cs` (Open becomes Toggle/Show/Hide, `Arrive` reduced to raise and activate, `KeptFromTiling`, `TasksWindowActions.Remember` replaced)
- Modify: `src/Deskbar.App/App.xaml.cs` (construction ~line 2123, `OpenTasksWindow` ~line 1852, `setTasksWindow` ~line 1550, pin after first show, start-up reopen, the 1 s timer calls `TasksPanel.Keep`, shutdown)
- Modify: `src/Deskbar.App/FloatingBar.TaskList.cs` (the "All tasks" / "+N more" line toggles, and looks "on" while the panel is shown)
- Test: `tests/Deskbar.Windows.Tests/TasksBoardTests.cs`, `tests/Deskbar.Windows.Tests/FloatingBarTaskListTests.cs`

**Interfaces:**
- Consumes: `TasksPanel` (Task 5), `TasksPanelState`, `SaveTasksPanel`, `TasksPanelLayout` (Task 2).
- Produces: `TasksBoard.Toggle()`, `TasksBoard.Show()`, `TasksBoard.Hide()`, `TasksBoard.IsOpen`; `TasksWindowActions.Close` stays the task close, and a new `TasksWindowActions.ClosePanel : Action` and `ResizePanel : Action<double>` (width in DIP, at the grip's drag end); `FloatingBar.TasksPanelShown : IObservable<bool>` set by the App (`init`).
- Window: `WindowStyle="None"`, `ResizeMode="NoResize"`, `ShowInTaskbar="False"`, `Topmost="True"`, WS_EX_TOOLWINDOW in `SourceInitialized`, no `MinWidth`. The grip is a 6 DIP `Thumb` on the inner edge (left edge for a Right dock, right edge for a Left dock), a live preview during the drag, `ResizePanel` on `DragCompleted`.
- Persist: every Show/Hide and every resize saves `TasksPanelState(Open, Width)`. At start the App shows the panel if `TasksPanel?.Open == true`, once the bar's first `BarStrip` with an edge arrives. Exit saves `Open` as it was before shutdown hides it.
- `TasksBoard.KeptFromTiling(hwnd, _)` becomes `FloatingBar.IsOwnWindow(hwnd)`: the panel is an own window.
- The pipe's `tasks-window show|hide` maps to Show/Hide; `ControlUsage` text says "the tasks panel".

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Toggle_shows_a_hidden_panel_and_hides_a_shown_one()
[Fact] public void Showing_and_hiding_remember_whether_the_panel_is_open()   // manager.State.TasksPanel.Open
[Fact] public void A_fresh_show_resets_every_filter()                         // incl. search and Claude working (Task 7 adds those; assert the four existing here)
[Fact] public void The_panel_is_kept_out_of_tiling()                          // KeptFromTiling(panelHwnd, panelHwnd) is true
[Fact] public void The_all_tasks_line_looks_on_while_the_panel_is_shown()    // FloatingBarTaskListTests, through TasksPanelShown
```

- [ ] **Step 2:** Run them on the hidden desktop; expect failures.
- [ ] **Step 3:** Implement. Remove the code that only served the floating window (place memory, maximize, GrowForMatrix) and leave a comment naming this plan where it was.
- [ ] **Step 4:** Run the whole `pwsh scripts/test-hidden.ps1` and the Core suite; expect PASS, with old TasksWindow-place tests deleted or rewritten (name them in the report).
- [ ] **Step 5:** Commit `feat: the tasks window is a panel beside the bar`.

### Task 7: Search box and the Claude working chip

**Files:**
- Modify: `src/Deskbar.App/TasksWindow.xaml` (a search row under the toolbar's title line)
- Modify: `src/Deskbar.App/TasksWindow.xaml.cs` (the box, Ctrl+F, Esc, the chip)
- Modify: `src/Deskbar.App/TasksBoard.cs` (View narrows by search and Claude working BEFORE grouping and counting, next to `goalFilter.Narrow`; `TasksView` gains `Search : string`, `ClaudeWorking : bool`, `ClaudeWorkingCount : int`; actions `Search : Action<string>`, `FilterClaudeWorking : Action<bool>`)
- Test: `tests/Deskbar.Windows.Tests/TasksBoardTests.cs`

**Interfaces:**
- Consumes: `TaskSearch`, `ClaudeWorking` (Task 1); `TasksBoard.Show` (Task 6).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Searching_narrows_the_groups_and_the_chip_counts_alike()
[Fact] public void Claude_working_keeps_only_tasks_with_a_live_session_and_counts_them()
[Fact] public void Search_and_Claude_working_combine_with_the_status_and_workspace_filters()
[Fact] public void A_fresh_show_clears_the_search_and_Claude_working()
[Fact] public void Typing_into_the_search_box_keeps_its_text_and_caret_through_a_redraw()   // as the comment box does today
```

- [ ] **Step 2:** Run on the hidden desktop; expect failures.
- [ ] **Step 3:** Implement. The search row is a bordered `TextBox` with the BareTextBox template and the placeholder "Search titles, status lines and questions", Ctrl+F focuses it, Esc in it clears it and focuses the board (`FocusBoard`). The chip reads `Claude working N`, orange-edged (the window's `Orange` brush), selected look when on. A redraw while typing never resets the box.
- [ ] **Step 4:** Run; expect PASS.
- [ ] **Step 5:** Commit `feat: search and a Claude working filter in the tasks panel`.

### Task 8: Slim lines below 620, Matrix from 780, busy squares

**Files:**
- Modify: `src/Deskbar.App/TasksWindow.xaml.cs` (`LineColumns`, the line builder ~line 652, tooltip)
- Modify: `src/Deskbar.App/TasksWindow.Matrix.cs` (the switch's visibility)
- Modify: `src/Deskbar.App/TasksBoard.cs` (`TaskRowView.WorkingCount : int`, from a new constructor parameter `Func<TaskItem, int>? workingOf`)
- Modify: `src/Deskbar.App/App.xaml.cs` (workingOf: the task's live sessions listed busy in the latest `ClaudeSessions.Live` list)
- Test: `tests/Deskbar.Windows.Tests/TasksBoardTests.cs` (or a new `TasksWindowWidthTests.cs`)

**Interfaces:**
- Consumes: `TasksPanelLayout.Slim`, `MatrixAllowed` (Task 2); `ResizePanel` (Task 6).
- Slim columns: tick 22, title star, toggles 38, sessions as squares (one 8 DIP square per session, 3 DIP apart, the column sized to the widest line's count, at most 48), chip 88, comments 40, "⋯" 24. The tooltip on a slim line carries the age and the assignee. Squares: dim when ended, blue when live, blue with a 2 DIP halo when working (`WorkingCount`).
- The Matrix: below 780 the switch is hidden and the List shows; `AppState.TasksMatrix` is not changed by the width.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void A_panel_narrower_than_620_draws_slim_lines()          // no "sessions" words, no age, no action button in the line's visual tree
[Fact] public void A_panel_620_or_wider_draws_todays_lines()
[Fact] public void The_Matrix_switch_shows_only_from_780_and_the_setting_survives_a_narrow_panel()
[Fact] public void A_session_that_is_working_draws_a_halo()
[Fact] public void A_slim_line_keeps_close_and_reopen_in_its_menu()
```

- [ ] **Step 2:** Run on the hidden desktop; expect failures.
- [ ] **Step 3:** Implement. The layout switches on `ResizePanel` (drag end), redrawing every line once.
- [ ] **Step 4:** Run the whole hidden suite and the Core suite; expect PASS.
- [ ] **Step 5:** Commit `feat: slim task lines and a width-gated Matrix in the panel`.

### Task 9: Docs and the release build

**Files:**
- Modify: `README.md` (the tasks section), `docs/superpowers/notes/manual-test-script.md`, `CLAUDE.md` (one bullet under "Decisions already made": the tasks panel and its order behind the bar)

- [ ] **Step 1:** README: the panel, opening and closing, the width, search, Claude working; name the tasks-window screencast as needing re-filming.
- [ ] **Step 2:** Manual test script: open/close from the bar and the ×, resize across 620 and 780, switch workspaces, restart Explorer (`taskkill /f /im explorer.exe & start explorer`) and check the bar is outside, minimize and restore the bar, restart Deskbar with the panel open, a full-screen video.
- [ ] **Step 3:** `dotnet build Deskbar.sln -c Release -warnaserror`; expect 0 warnings, 0 errors.
- [ ] **Step 4:** Commit `docs: the tasks panel in the README, the manual script and the briefing`.
