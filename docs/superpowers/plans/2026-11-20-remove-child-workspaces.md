# Remove child workspaces: implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every group becomes a plain named group: no anchor, no window borrowing, no nesting commands, and existing anchored groups migrate without losing a workspace, name, order or colour.

**Architecture:** `Group.AnchorWorkspaceId` survives only as a legacy field that `AppState.Migrated()` reads once and clears. Everything that read it (borrowing in `WorkspaceManager`, the anchor branches of moves and deletes, the bar's anchored drawing, Manage's nest controls, `add-child`) is deleted. `RepairInheritedPins` stays so a machine that crashed mid-borrow still gets its windows back.

**Tech Stack:** C# / .NET 10, WPF, xUnit, CSharpFunctionalExtensions.

**Spec:** `docs/superpowers/specs/2026-11-20-bar-edit-mode-design.md` (the "Pull request 1" part). The edit mode (pull request 2) gets its own plan once this one is merged.

## Global constraints

- Work in this worktree, on a new branch `remove-child-workspaces` off `origin/main`; the design commit comes along with it.
- C# style per the user's rules: expression bodies, `var`, no `private` keyword, lambdas over loops, immutable records, `Result` for expected failures.
- Comments carry the reasoning, as the rest of the code does; a deleted feature's comments go with it, and comments that mention borrowing or anchors elsewhere are rewritten, not left stale.
- No version file edits; the PR body has a `## Release notes` section (text below).
- Commits end with the line `*opus5.5*` and nothing else as attribution.
- Before the PR: `dotnet build Deskbar.sln -c Release -warnaserror` and `dotnet test Deskbar.sln --filter "Category!=Integration"`, both green.

## Review focus

1. A state.json where a child sits ABOVE its parent in `Workspaces`: after load the bar draws the same rows in the same order, and the group shows the same colour it did.
2. A state.json from before groups (`ParentId` only, no `Groups`): loads as a named group, parent first.
3. A state.json with `InheritedPins` left by a crash: windows are unpinned and moved home on start, and the list is empty afterwards.
4. A group whose stored anchor id names a workspace that no longer exists: loads as an ordinary group, nothing throws.
5. A group named exactly like one of its members ("Sparrow" with member "Sparrow"): the switcher and quick find show "Sparrow", never "Sparrow › Sparrow".

Each line has its test in the task that owns the code (Tasks 1, 1, 2, 1 and 3).

---

### Task 1: Migrate anchored groups to named groups

**Files:**
- Modify: `src/Deskbar.Core/Domain/Group.cs` (record shape and the header comment)
- Modify: `src/Deskbar.Core/Persistence/AppState.cs:420-506` (`MembersOf`, `ColourSlotOf`, `Migrated`, new `DrawnBlocks`)
- Modify: `src/Deskbar.Core/WorkspaceManager.cs` (`Blocks` callers use `State.DrawnBlocks()`; the static `Blocks` goes)
- Test: `tests/Deskbar.Core.Tests/GroupMigrationTests.cs`

**Interfaces:**
- Produces: `public sealed record Group(Guid Id, string Name)` with `public Guid? AnchorWorkspaceId { get; init; }` marked legacy (read only by `Migrated`, written as null). `public IReadOnlyList<IReadOnlyList<Workspace>> AppState.DrawnBlocks()`, the walk `WorkspaceManager.Blocks` does today, moved verbatim with its comment.
- Consumes: `WorkspacePalette.For(string? color, int index)`.

- [ ] **Step 1: Write the failing tests** in `GroupMigrationTests`. Replace `A_parent_becomes_one_anchored_group` and add:

```csharp
[Fact] public void A_parent_becomes_one_named_group_with_the_parent_first()
// Nested() migrated: one group, AnchorWorkspaceId null, Name "Sparrow",
// MembersOf(group) == [parent, first, second]

[Fact] public void An_anchored_group_loses_its_anchor_and_keeps_name_members_and_colour()
// state with Group(g, "Work") { AnchorWorkspaceId = a.Id, Color = "#123456" } and members a, b:
// migrated group has AnchorWorkspaceId null, Name "Work", Color "#123456", members [a, b]

[Fact] public void A_child_listed_above_its_parent_keeps_the_drawn_order()
// Workspaces [loner, child, parent] with parent anchoring child's group:
// migrated Workspaces order == [loner, parent, child]

[Fact] public void A_group_whose_slot_moves_keeps_the_colour_it_was_showing()
// Workspaces [child, other, parent], group anchored on parent, Color null (old slot = 2):
// migrated group.Color == WorkspacePalette.For(null, 2)

[Fact] public void A_group_whose_slot_stays_still_follows_its_position()
// Workspaces [parent, child], anchored on parent, Color null: migrated group.Color is null

[Fact] public void An_anchor_that_no_longer_exists_is_simply_dropped()
// Group anchored on Guid.NewGuid() with members a, b: migrated group has no anchor, members [a, b]

[Fact] public void Migrating_twice_changes_nothing()
// state.Migrated().Migrated() equals state.Migrated() on Groups and Workspaces
```

Add one round-trip test in `JsonPersistenceStoreTests`: a state.json text containing `"Groups":[{"Id":"…","Name":"Work","AnchorWorkspaceId":"<a>"}]` loads through `JsonPersistenceStore` and, after `Migrated()`, has no anchor and `a` first.

- [ ] **Step 2: Run them and see them fail**

Run: `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~GroupMigrationTests|FullyQualifiedName~JsonPersistenceStoreTests"`
Expected: the new tests fail (anchors are kept, order unchanged).

- [ ] **Step 3: Implement**

- `Group`: drop `IsAnchored` only after Task 2 removes its callers; in this task move `AnchorWorkspaceId` from the positional list to a legacy init property with a comment saying why it is still there. Fix every `new Group(id, name, anchor)` call site to the initializer form so the solution builds.
- `AppState.Migrated()` becomes two steps, each a no-op when it has nothing to do: `GroupsFromParentIds()` (today's body, which now produces groups carrying the legacy anchor) then `WithoutAnchors()`.
- `WithoutAnchors()`: if no group has an anchor, return `this`. Otherwise, per anchored group, note `oldSlot` = the anchor's list index (today's `ColourSlotOf`). Build the new `Workspaces` by walking `DrawnBlocks()` with each anchored group's anchor moved to the front of its block, then flattening. Set `Color = WorkspacePalette.For(null, oldSlot)` only when `Color` is null and the group's first member's new index differs from `oldSlot`. Clear `AnchorWorkspaceId` on every group.
- `MembersOf` keeps anchor-first in this task (Task 2 removes it), so `DrawnBlocks` already returns anchor-first blocks; the explicit move to the front is for clarity and survives Task 2.

- [ ] **Step 4: Run the tests and see them pass**

Run: the Step 2 command, then `dotnet test Deskbar.sln --filter "Category!=Integration"`
Expected: all green.

- [ ] **Step 5: Commit** `feat: anchored groups load as named groups, in the order and colour they were drawn`

### Task 2: Delete borrowing and nesting from Core

**Files:**
- Modify: `src/Deskbar.Core/WorkspaceManager.cs`: `WorkspaceOf` (205-210), the arrival path (`ApplyInheritance` call near 1140 and the bounce comment and trace text), the borrowing block (1198-1282: `borrowed`, `ApplyInheritance`, `ReleaseBorrowed`, `PersistBorrowed`, `ParentDesktopOf`), `RepairInheritedPins` (keep, rewrite its comment as legacy), `MoveAmongPeers` (1616-1630), `NestWorkspace`, `JoinedAnchoredGroup`, `AddChildWorkspace` (1815-1910), `WithoutMember` (1950-1958), `MoveIntoGroup`'s anchor refusal (~2008), the two `borrowed.Remove` taps (~2616, ~3364), the borrowed filters in `WindowsByWorkspace` (~4220-4250)
- Modify: `src/Deskbar.Core/Persistence/AppState.cs`: delete `LendsWindowsTo`, `IsAnchor`; `MembersOf` is plain list order; `ColourSlotOf` is the first member's index; `InheritedPins` comment marked legacy
- Modify: `src/Deskbar.Core/Domain/Group.cs`: delete `IsAnchored`; header comment describes one kind of group
- Modify: `src/Deskbar.Core/Domain/DeleteWorkspacePrompt.cs:54-57` (anchor case goes)
- Modify: `src/Deskbar.Core/Control/RemoteControl.cs:89`, `RemoteControl.Workspaces.cs:63-66`, `ControlUsage.cs:59` (`add-child` goes)
- Modify: tiler comments that say "borrowed" (`Tiler.cs:21,677`, `Tiler.Arrival.cs:101`, `Tiler.Drag.cs:259`)
- Test: `NestedWorkspaceTests.cs`, `GroupMembershipTests.cs`, `GroupColourTests.cs`, `DeleteWorkspaceTests.cs`, `DeleteWorkspacePromptTests.cs`, `LostDesktopTests.cs`, `RemoteControlMenuTests.cs`, `MoveToMonitorTests.cs` (comment), `Tiling/TilerTests.cs`

**Interfaces:**
- Consumes: Task 1's `AppState.DrawnBlocks()`.
- Produces: no anchor API anywhere. `WorkspaceOf(window)` = None for a pinned window, else the workspace on `desktops.DesktopOf(window)`. `MoveAmongPeers` treats every group member as movable inside its box (`pinned` is gone).

- [ ] **Step 1: Rework the tests first**
  - `NestedWorkspaceTests.cs`: delete the nesting, borrowing and add-child tests (43-290). Move the insert and move tests (295-510) into a new `GroupOrderTests.cs`, building their groups with `CreateGroup` + `MoveIntoGroup` instead of `NestWorkspace`, and drop assertions that read `LendsWindowsTo`. Delete `Moving_the_anchor_moves_the_whole_group` and `Up_on_the_first_child_does_nothing_instead_of_swapping_with_the_parent`; add `Up_on_the_first_member_does_nothing` (first member of a group, `MoveWorkspace(id, -1)` succeeds and the order is unchanged).
  - Add `A_borrow_left_by_a_crash_is_still_repaired_at_startup` to `GroupOrderTests` or a small `InheritedPinRepairTests.cs`: stored `InheritedPins = [new(w, home)]`, `desktops.PinnedWindows` holds `w`; after `Start()`, `w` is unpinned, on `home`, and `State.InheritedPins` is empty. (Port of the existing `A_borrow_left_by_a_crash_is_repaired_at_startup`.)
  - `GroupMembershipTests`: delete the four anchored tests (100, 130, 179, 226) and the `IsAnchored`/`LendsWindowsTo` asserts in 33-55; rebuild `Ungrouping_keeps_desktops_names_and_positions` on `CreateGroup`.
  - `GroupColourTests`: delete 50, 102 and 148; add `A_group_takes_its_colour_from_its_first_members_position`.
  - `DeleteWorkspaceTests`: rebuild 139 on a named group (`A_member_of_a_group_of_two_can_go_and_the_other_stands_alone`); delete 261.
  - `DeleteWorkspacePromptTests`: delete `Deleting_the_anchor_says_the_others_stop_borrowing`.
  - `LostDesktopTests` 486 and 506: rebuild on named groups, assert the group outcome only.
  - `RemoteControlMenuTests`: delete `Add_child_nests_a_new_workspace_under_its_parent`; remove `"add-child"` from `Help_lists_every_command`.
  - `TilerTests.Borrowed_and_pinned_windows_float` renamed `Pinned_windows_float`.

- [ ] **Step 2: Run the reworked tests against the old code.** This is a deletion, so the point is that no test still calls `NestWorkspace`, `AddChildWorkspace`, `LendsWindowsTo`, `IsAnchor` or `IsAnchored`, and that the ported tests pass before the code goes.

Run: `dotnet test tests/Deskbar.Core.Tests --filter "Category!=Integration"`, then `grep -rnE "NestWorkspace|AddChildWorkspace|LendsWindowsTo|IsAnchor" tests`
Expected: green, and the grep prints nothing.

- [ ] **Step 3: Delete the Core code** listed under Files. Rewrite the bounce comment and its trace to name focus restore only ("Focus restore suspended for 2s"). `WindowsByWorkspace` keeps its pinned filter and `desktopOf` asks `DesktopOfRemembering` for every window.

- [ ] **Step 4: Run all tests**

Run: `dotnet test Deskbar.sln --filter "Category!=Integration"`
Expected: Core green; `Deskbar.App` does not build yet (Task 3), so run `dotnet test tests/Deskbar.Core.Tests --filter "Category!=Integration"` if the solution run stops on the App.

- [ ] **Step 5: Commit** `feat: child workspaces are gone from Core: no borrowing, no nesting, no add-child`

### Task 3: Remove child workspaces from the app

**Files:**
- Modify: `src/Deskbar.App/FloatingBar.xaml.cs`: 2412-2416 (`isAnchor` goes; `nested = group is not null`), 2468-2502 (comment about the anchor; `boxed = members.Count + 1 > 1`; the header is inserted for every group), 3322 (`IsAnchor` condition goes), 6908-6914 ("Add child…" goes; `WorkspaceMenu`'s `nested` parameter goes if nothing else reads it)
- Modify: `src/Deskbar.App/LaneColour.cs:19-21`
- Modify: `src/Deskbar.App/ManageWindow.xaml:113-119` and `ManageWindow.xaml.cs` (`ReloadNestParents`, `OnNestWorkspace`, `OnUnnestWorkspace` and their calls at 128 and 308)
- Modify: `src/Deskbar.App/QuickFindSources.cs:30-38` (the borrowed recheck goes; `DistinctBy` stays)
- Modify: `src/Deskbar.App/App.xaml.cs:187` and any other App comment that says borrowed
- Test: new `tests/Deskbar.Windows.Tests/LaneColourTests.cs`

**Interfaces:**
- Consumes: Task 2's Core without anchors.
- Produces: `LaneColour.GroupNameOf(state, workspace)` returns null when the workspace is in no group or when `group.Name` equals `workspace.Name` (`StringComparison.OrdinalIgnoreCase`), else the group's name.

- [ ] **Step 1: Write the failing tests** in `LaneColourTests`:

```csharp
[Fact] public void A_member_named_like_its_group_shows_its_bare_name()
// group "Sparrow", member "Sparrow": FullName == "Sparrow"
[Fact] public void A_member_shows_its_group_first()
// group "Sparrow", member "dice-rolls": FullName == "Sparrow › dice-rolls"
[Fact] public void An_ungrouped_workspace_shows_its_bare_name()
```

- [ ] **Step 2: Run them and see the first one fail**

Run: `dotnet test tests/Deskbar.Windows.Tests --filter "FullyQualifiedName~LaneColourTests"`

- [ ] **Step 3: Implement** the App changes under Files. The bar's comments that explain anchored drawing are rewritten for one kind of group.

- [ ] **Step 4: Build Release and run everything**

Run: `dotnet build Deskbar.sln -c Release -warnaserror` then `dotnet test Deskbar.sln --filter "Category!=Integration"`
Expected: no warnings, all green.

- [ ] **Step 5: Live check.** Stop the running Deskbar, start the Debug build (`src/Deskbar.App/bin/Debug/net10.0-windows10.0.19041.0/Deskbar.exe`) against a scratch copy of Petre's state.json (`DESKBAR_APPDATA`), confirm groups draw as before and the row menu has no "Add child…". Then put Petre's own build back.

- [ ] **Step 6: Commit** `feat: the bar, Manage and quick find have no child workspaces`

### Task 4: Docs, then the pull request

**Files:**
- Modify: `README.md` (281 and the row-menu alt text at 287: no "Add child"; 325-327: the nested-workspace bullet goes; 614: Manage's nesting goes; 690: `add-child` leaves the control table)
- Modify: `CLAUDE.md` (the "Workspaces can be grouped" decision: drop "A nested workspace really gets its parent's windows", add that child workspaces were removed on 2026-11-20 and why)

- [ ] **Step 1: Edit the docs.** Grep `README.md` and `docs/superpowers/notes/manual-test-script.md` for `nest`, `child`, `add-child`, `borrow` and fix every user-facing hit.
- [ ] **Step 2: Commit** `docs: child workspaces are gone from the README and the briefing`
- [ ] **Step 3: Push and open the PR** `Remove child workspaces` with this release note:

```
## Release notes

Nested workspaces are now ordinary groups. A workspace you had nested under another keeps its group,
name and colour, and the parent becomes the group's first row; a nested workspace no longer shows its
parent's windows. Add child is gone from the row menu.
```

- [ ] **Step 4: Watch the PR's base** with the Monitor loop from CLAUDE.md and refresh it when main moves.
