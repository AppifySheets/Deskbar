# Bar edit mode: implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An "Arrange workspaces" mode on the floating bar in which workspace rows and group headers are dragged and dropped: between rows moves, onto a row groups.

**Architecture:** Every rule about what a drop means lives in Core as a pure function (`RowDrop.Plan`) that the bar calls on every pointer move to draw the preview and once on release to act; `WorkspaceManager.ApplyDrop` turns the planned action into ONE persisted state change. The bar owns only the mode flag, hit testing, the visuals and the name prompt. The control pipe gets `arrange on|off`.

**Tech Stack:** C# / .NET 10, WPF, xUnit, CSharpFunctionalExtensions.

**Spec:** `docs/superpowers/specs/2026-11-20-bar-edit-mode-design.md`, section "Pull request 2: the bar edit mode". Visual picks, approved by Petre on 2026-11-20 against the mockup https://claude.ai/artifact/2fvo8SPCfWh5DctS6egBVY: **1A** (a grip before every row and group header name, no outlines) and **3A** ("group with" target: the white `DropHighlight` wash plus an `ActiveBorder` ring).

## Global constraints

- Work in the worktree `.claude/worktrees/bar-edit-mode` on branch `bar-edit-mode`, off `origin/main`.
- C# style per the user's rules: expression bodies, `var`, no `private` keyword, lambdas over loops, immutable records, `Result` for expected failures. Comments carry the reasoning, in the codebase's voice; no em dashes.
- Menu wording: `Arrange workspaces` (glyph `⇅`), flipping to `Done arranging` while the mode is on. Strip text: `Drag rows to arrange · Esc`, button `Done`. Hint under a "group with" target: `Group with {name}…`. Prompt: `PromptDialog.Ask("New group", "Name:", {target's name}, owner: this)`.
- Pipe: `arrange on|off`, usage text `Arrange workspaces (drag rows on the bar) / Done arranging`, JSON reply `{ arranging = true|false }`.
- Colours reuse the bar's existing brushes: insertion line `InsertMarkerBrush`, 2 DIP; "group with" target `DropHighlight` background plus a 1 DIP `ActiveBorder` ring; grip `⋮⋮` in `DimForeground`; icons at 45% opacity while the mode is on; the dragged row leaves a dashed 1 DIP gap at 25% white and follows the pointer as a copy at 72% opacity.
- The mode is not persisted. Each drop is one `Persist`. A refused or cancelled drop changes nothing.
- Commits end with the line `*opus5.5*` and nothing else as attribution. The repo stores LF; committed blobs carry no carriage returns.
- Before the PR: `dotnet build Deskbar.sln -c Release -warnaserror` and `dotnet test Deskbar.sln --filter "Category!=Integration"`.

## Review focus

1. Dragging a row whose workspace is current, or the only member of a group of two: the old group dissolves as `LeaveGroup` does, and the bar does not switch workspace on release.
2. A pinned row (ungrouped, drawn at the top) dragged among unpinned rows, or an unpinned row onto a pinned one: no preview and no change, because a pinned row moves only among pinned rows and can never be grouped.
3. A rebuild arriving mid-drag (a window opens): the drag survives, because the bar defers rebuilds while an arrange drag is in progress, and the deferred rebuild runs after the drop.
4. The name prompt cancelled, or answered with a name another group has: nothing changes, and a taken name shows the usual refusal message.
5. Esc while the mode is on and the pointer is elsewhere: the mode ends when the bar is the active window; entering the mode activates the bar so Esc reaches it.

Each has a test in the task that owns the code (Tasks 1 and 2 for 1, 2 and 4; Task 4 for 3; Task 3 for 5).

---

### Task 1: Drop rules in Core

**Files:**
- Create: `src/Deskbar.Core/Arrange/RowDrop.cs`
- Test: `tests/Deskbar.Core.Tests/Arrange/RowDropTests.cs`

**Interfaces:**
- Produces, in namespace `Deskbar.Core.Arrange`:

```csharp
public abstract record RowRef { public sealed record Workspace(Guid Id) : RowRef; public sealed record Group(Guid Id) : RowRef; }
public enum DropZone { Before, After, Onto, End }
// What a drop would do, or Nothing. Beside is always a top-level row or a member, After says which side.
public abstract record DropAction
{
    public sealed record Nothing : DropAction;
    public sealed record Place(Guid Workspace, RowRef Beside, bool After) : DropAction;      // moves; joins Beside's group if Beside is a member, leaves its own if Beside is top-level
    public sealed record GroupWith(Guid Target, Guid Dragged, string SuggestedName) : DropAction;
    public sealed record PlaceGroup(Guid Group, RowRef Beside, bool After) : DropAction;    // Beside is top-level
}
// What the bar draws while the pointer is there.
public abstract record DropShow
{
    public sealed record None : DropShow;
    public sealed record Line(RowRef Beside, bool After, bool InsideGroup) : DropShow;      // InsideGroup: short line in the bracket
    public sealed record Highlight(RowRef Target, string? Hint) : DropShow;                 // Hint "Group with {name}…" for GroupWith, null for a join
}
public static class RowDrop
{
    public static (DropAction Action, DropShow Show) Plan(AppState state, RowRef dragged, RowRef? target, DropZone zone);
}
```

- [ ] **Step 1: Write the failing tests.** One state for most tests: `Mail`, `Code` ungrouped; group `Work` = [`Tickets`, `Reviews`]; `Docs`, `Personal` ungrouped; plus a variant with `Mail` pinned. Tests, each asserting both Action and Show:
  - `Top_quarter_of_an_ungrouped_row_places_before_it_ungrouped` (Personal, Workspace(Code), Before) → `Place(Personal, Workspace(Code), false)`, `Line(Workspace(Code), false, false)`.
  - `Bottom_quarter_of_a_groups_last_member_joins_at_the_bottom` (Personal, Workspace(Reviews), After) → `Place(…, Workspace(Reviews), true)`, `Line(…, InsideGroup: true)`.
  - `Top_quarter_of_the_row_after_a_group_places_ungrouped_below_the_group` (Personal, Workspace(Docs), Before) → `Line(…, InsideGroup: false)`.
  - `Onto_an_ungrouped_row_groups_with_it` (Personal, Workspace(Docs), Onto) → `GroupWith(Docs, Personal, "Docs")`, `Highlight(Workspace(Docs), "Group with Docs…")`.
  - `Onto_a_grouped_row_joins_below_it` (Personal, Workspace(Tickets), Onto) → `Place(…, Workspace(Tickets), true)`, `Highlight(Workspace(Tickets), null)`.
  - `Top_quarter_of_a_group_header_places_above_the_whole_group` (Personal, Group(Work), Before) → `Place(…, Group(Work), false)`, full `Line`.
  - `The_rest_of_a_group_header_joins_at_the_top` (Personal, Group(Work), Onto and After) → `Place(…, Workspace(Tickets), false)`, `Line(Workspace(Tickets), false, true)`.
  - `Empty_space_below_the_last_row_places_at_the_end` (Tickets, null, End) → `Place(Tickets, Workspace(Personal), true)`, Tickets ungrouped, full `Line`.
  - `A_member_reorders_inside_its_own_group` (Reviews, Workspace(Tickets), Before) → `Place`, `Line(…, true)`.
  - `Onto_itself_or_a_row_of_its_own_group_does_nothing` (Tickets onto Reviews; Docs onto Docs; Docs Before Docs) → `Nothing`, `None`.
  - `A_header_moves_among_top_level_rows_only` (Group(Work), Workspace(Tickets) or Group(Work), any zone) → `Nothing`; (Group(Work), Workspace(Docs), After) → `PlaceGroup(Work, Workspace(Docs), true)`, full `Line`.
  - `A_pinned_row_moves_only_among_pinned_rows` and `Nothing_groups_with_a_pinned_row` (pinned variant).
  - `A_group_with_a_taken_name_is_still_planned` (planning does not check names; Task 2 refuses on apply).
- [ ] **Step 2: Run them and see them fail.** `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~RowDropTests"` → compile failure, `RowDrop` not defined.
- [ ] **Step 3: Implement `RowDrop.Plan`** in `RowDrop.cs` from `state.Blocks()`, `state.GroupOf`, `state.MembersOf` and `Workspace.Pinned`. A drop that would leave the order exactly as it is returns `Nothing`/`None` (compare the resulting order the same way Task 2 will build it, or keep a small helper both use).
- [ ] **Step 4: Run the tests and see them pass**, then the full non-Integration suite.
- [ ] **Step 5: Commit** `feat: the rules for what dropping a row on the bar does`

### Task 2: Apply a drop

**Files:**
- Modify: `src/Deskbar.Core/WorkspaceManager.cs` (new section next to the group membership methods; reuse `WithoutMember`, `GroupNameTaken`, `MovedTo`, `AfterLastMemberOf`)
- Test: `tests/Deskbar.Core.Tests/Arrange/ApplyDropTests.cs`

**Interfaces:**
- Consumes: Task 1's `DropAction`, `RowRef`.
- Produces: `public Result ApplyDrop(DropAction action, Func<string, Maybe<string>> askGroupName)` on `WorkspaceManager`. `askGroupName` is called only for `GroupWith`, with the suggested name; `None` cancels with `Result.Success()` and no change.

- [ ] **Step 1: Write the failing tests** (count persists with the existing fake store's save counter, or `StateChanged` pulses):
  - `Placing_beside_a_top_level_row_leaves_the_old_group_in_one_persist` (Tickets placed after Docs: Tickets ungrouped, stored order puts it right after Docs, Work keeps Reviews only, so Work dissolves; exactly one persist).
  - `Placing_beside_a_member_joins_that_group_at_that_place`.
  - `Placing_a_group_moves_its_members_together_in_order`.
  - `Grouping_two_rows_makes_a_group_at_the_targets_place_target_first` (GroupWith(Docs, Personal) with name "Docs": new group named "Docs", members [Docs, Personal], placed where Docs was, colour = Docs's own `Color` as `CreateGroup` does).
  - `A_cancelled_name_changes_nothing` (askGroupName returns None: success, zero persists).
  - `A_taken_group_name_is_refused_and_changes_nothing` (failure message `A group named 'Work' already exists.`).
  - `A_dragged_member_of_another_group_leaves_it_when_grouping` (Tickets grouped with Docs: Work dissolves, new group [Docs, Tickets]).
  - `Nothing_is_a_no_op`.
  - `Placing_a_pinned_row_keeps_the_unpinned_rows_stored_places` (mirrors `ReorderedOnItsSide`).
- [ ] **Step 2: Run and see them fail.** `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~ApplyDropTests"`.
- [ ] **Step 3: Implement `ApplyDrop`** as a switch over the action, each arm building the new `AppState` from the current one and ending in one `Persist` (or `MovedTo`). Stored order after a placement: the dragged workspace removed and reinserted directly before/after `Beside` (for `Group(id)` beside, before its first or after its last member); `GroupId` set to `Beside`'s group or cleared; then `WithoutMember`'s dissolve rule for the group it left.
- [ ] **Step 4: Run the tests and the full suite.**
- [ ] **Step 5: Commit** `feat: a drop on the bar is one change to the workspace list`

### Task 3: The mode on the bar, and its pipe twin

**Files:**
- Create: `src/Deskbar.App/FloatingBar.Arrange.cs` (partial class: the mode flag, `SetArranging`, menu item, Esc, the Done strip, grips)
- Modify: `src/Deskbar.App/FloatingBar.xaml.cs`: `AddBarItems` (7177) and `GroupMenu` (3415) add the menu item; the caption builders for workspace rows and group headers (`GroupRow` caption, `GroupHeader` 3353, `FoldedGroupLine` 3477) prepend the grip while arranging; icons dim and refuse to start a window drag while arranging (`WindowDragSource` and the icon press path); `OnPreviewMouseLeftButtonDown` (1072) and `OnPreviewMouseLeftButtonUp` (1257) return early with `e.Handled = true` while arranging, so no row switches and no fold toggles; the mode change triggers a rebuild.
- Modify: `src/Deskbar.Core/Control/RemoteControl.cs` (constructor gains `Func<bool, Result>? setArranging = null`; dispatch arm `("arrange", [var state])`), `RemoteControl.Workspaces.cs` (`Arrange(string state)` beside `Bar`), `ControlUsage.cs` (the line), `src/Deskbar.App/App.xaml.cs` (~1318, pass `setArranging: on => floatingBar is null ? Result.Failure("The bar is not running.") : floatingBar.SetArranging(on)`).
- Test: `tests/Deskbar.Windows.Tests/FloatingBarArrangeTests.cs` (use `FloatingBarStripsTests.StripHarness` or its pattern), `tests/Deskbar.Core.Tests/RemoteControlMenuTests.cs`.

**Interfaces:**
- Produces: `internal bool Arranging { get; }` and `internal Result SetArranging(bool on)` on `FloatingBar`; `internal void EndArrangingOnEscape()` used by the key handler.

- [ ] **Step 1: Write the failing tests:**
  - Windows: `Arranging_puts_a_grip_on_every_row_and_header`, `A_click_on_a_row_does_not_switch_while_arranging`, `The_menu_item_flips_to_done_arranging`, `Esc_ends_the_mode`, `Done_ends_the_mode`, `Icons_are_dimmed_while_arranging`.
  - Core: `Arrange_on_and_off_over_the_pipe` (with a fake `setArranging` recording calls; replies `{ "arranging": true }` / `false`), `Arrange_refuses_when_the_bar_cannot_be_reached` (null func), and `"arrange"` added to `Help_lists_every_command`.
- [ ] **Step 2: Run them and see them fail.**
- [ ] **Step 3: Implement.** Entering the mode calls `Activate()` on the bar so Esc reaches it (Review focus 5); the key handler is `PreviewKeyDown` on the bar for `Key.Escape`. The strip with `Done` sits below the rows inside `Root`, only while arranging. A rebuild while arranging draws grips; leaving the mode rebuilds without them.
- [ ] **Step 4: Run the focused tests and the full suite; Release build.**
- [ ] **Step 5: Commit** `feat: an arrange mode on the bar, and arrange on|off on the pipe`

### Task 4: Dragging rows

**Files:**
- Modify: `src/Deskbar.App/FloatingBar.Arrange.cs` (drag state, hit testing, preview, release)
- Modify: `src/Deskbar.App/FloatingBar.xaml.cs`: register arrange targets while building (in `WorkspaceRow` 2393 for each workspace row, `GroupHeader`/`FoldedGroupLine` for each group, and the `FamilyBox` element of each group so a line can sit at the box's edge); `OnPreviewMouseMove` (1732) routes to the arrange drag while one is active; the rebuild deferral (~1991) also defers while `arrangeDrag is not null`.
- Modify: `src/Deskbar.App/ClickTrace.cs` usage: trace `arrange press`, `arrange drag-start`, `arrange drop <action>`, `arrange cancel`.
- Test: `tests/Deskbar.Windows.Tests/FloatingBarArrangeTests.cs`

**Interfaces:**
- Consumes: `RowDrop.Plan`, `WorkspaceManager.ApplyDrop`, Task 3's `Arranging`.
- Produces: `internal (DropAction Action, DropShow Show) ArrangePreviewAt(RowRef dragged, Point inRoot)` (the hit test plus `RowDrop.Plan`, used by the drag and by tests) and `internal Result ArrangeDropAt(RowRef dragged, Point inRoot)`.

- [ ] **Step 1: Write the failing tests:**
  - `The_zone_comes_from_the_row_under_the_pointer` (points at 10%, 50% and 90% of a row's height give Before, Onto and After; on a group header the top quarter is Before and the rest Onto; below the last row is End).
  - `A_preview_line_inside_a_group_is_short` and `A_group_with_target_is_highlighted_with_its_hint`.
  - `Dropping_onto_an_ungrouped_row_asks_for_a_name_prefilled_with_its_name` (make the prompt injectable: `internal Func<string, Maybe<string>> AskGroupName` defaulting to `PromptDialog.Ask("New group", "Name:", name, owner: this)`).
  - `A_rebuild_during_an_arrange_drag_is_deferred_until_the_drop`.
  - `Escape_during_a_drag_cancels_it_and_changes_nothing`.
- [ ] **Step 2: Run them and see them fail.**
- [ ] **Step 3: Implement.** Mouse capture on the bar (`CaptureMouse`) from the press on a grip-bearing row or header, a drag start after the system drag distance, never `DoDragDrop`: the row never leaves the bar, and the OLE loop is what swallowed clicks in #48. Zones: top quarter Before, bottom quarter After, middle half Onto; a group header: top quarter Before, rest Onto; a header drag uses halves (Before/After). The preview follows the constraints block's visuals; the dragged row's own slot shows the dashed gap. Release: `ArrangeDropAt`, then release capture and serve the deferred rebuild. Esc or losing capture cancels.
- [ ] **Step 4: Run the focused tests and the full suite; Release build.**
- [ ] **Step 5: Commit** `feat: drag rows on the bar to arrange and group them`

### Task 5: Docs, then the pull request

**Files:**
- Modify: `README.md` (a short "Arranging the bar" section beside the groups section; the `arrange` row in the control table)
- Modify: `docs/superpowers/notes/manual-test-script.md` (a section walking through: enter the mode from the menu, grips appear, click does nothing, drag between rows, into a group's bottom, onto an ungrouped row with the prompt and with Cancel, onto a grouped row, a header drag, Esc mid-drag, Done)

- [ ] **Step 1: Edit the docs.**
- [ ] **Step 2: Commit** `docs: arranging the bar in the README and the manual test script`
- [ ] **Step 3 (controller): push, open the PR with a `## Release notes` section written for users, and watch its base.**
