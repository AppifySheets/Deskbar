# Pinned Workspaces Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A pinned workspace gets one item on every screen strip and in the bar's header that takes you into it, and, while you are in it, takes you back to where you came from.

**Architecture:** A `Pinned` flag on `Workspace`, set through `WorkspaceManager.SetWorkspacePinned` (menu and control pipe). A pure Core function, `WorkspaceJumps.Of`, decides each item's label and target from the workspaces, the current workspace and `ByRecentUse().Back`. The bar builds one button per jump in a new partial file and hands copies to every strip through a new optional `SetTabs` argument, inside the existing rebuild.

**Tech Stack:** C# / .NET 10, WPF, xUnit, CSharpFunctionalExtensions.

**Spec:** `docs/superpowers/specs/2026-11-20-pinned-workspaces-design.md`

## Global Constraints

- `Workspace.Pinned` is a `bool` init property, default false, no migration (as `Folded` and `Tiled`).
- The back target is `WorkspaceManager.ByRecentUse().Back`, the same one the bar's ↩ button and a Win+Ctrl+Tab tap use. No second MRU.
- Menu item: one flipping item, "Pin to strip" / "Unpin from strip". Remote twins: `pin-workspace <workspace>`, `unpin-workspace <workspace>`, listed in `ControlUsage`.
- Nothing OS-pins a window. No new hotkey.
- Clicking an item calls `manager.Switch(target)`, the bar row's own path.
- Strip items are built inside the bar's rebuild, never by a rebuild of their own (re-entrancy guard, CLAUDE.md).
- A fresh element per strip: a WPF element has one parent.
- Static brushes frozen. Code comments carry the reasoning, with Petre's words where they set a rule.
- Before the PR: `dotnet build Deskbar.sln -c Release -warnaserror` and `dotnet test Deskbar.sln --filter "Category!=Integration"`.

## Review Focus

- The pinned workspace is the only workspace there is, or you have visited nothing else since start: `Back` is None. The item shows the workspace's own name and clicking does nothing (Task 2 test `Inside_with_nothing_to_go_back_to_names_itself_and_goes_nowhere`).
- You are on an unnamed desktop: no workspace is current, every item reads its own name and goes in (Task 2 test `On_an_unnamed_desktop_every_item_goes_in`).
- A pinned workspace is deleted: its item vanishes on the next rebuild, because it is no longer in `State.Workspaces` (Task 2 test `Only_pinned_workspaces_get_an_item_in_list_order`).
- Two strips: each gets its own element instances, and a re-entrant rebuild does not double the items (Task 3 tests `Every_strip_gets_the_pinned_items_after_the_name_tag`, `A_reentrant_rebuild_does_not_double_the_pinned_items`).
- The items eat the tabs' width: `SetTabs` subtracts their measured width from the room before `StripPlan.TabWidth` (Task 3 test `Pinned_items_take_their_width_out_of_the_tabs_room`).

---

### Task 1: The flag, the manager call, the menu's remote twin

**Files:**
- Modify: `src/Deskbar.Core/Domain/Workspace.cs` (new property after `Tiled`)
- Modify: `src/Deskbar.Core/WorkspaceManager.cs` (next to `SetWorkspaceFolded`, ~line 1724)
- Modify: `src/Deskbar.Core/Control/RemoteControl.cs` (dispatch next to `fold`/`unfold` ~line 91; `pinned = workspace.Pinned` in the workspace description next to `folded` ~line 193)
- Modify: `src/Deskbar.Core/Control/RemoteControl.Workspaces.cs` (next to `SetRowFolded`)
- Modify: `src/Deskbar.Core/Control/ControlUsage.cs` (two entries after `unfold`; add "pinned" to the `workspaces` line's field list)
- Test: `tests/Deskbar.Core.Tests/PinnedWorkspaceTests.cs` (new, built like `FoldTests`), `tests/Deskbar.Core.Tests/RemoteControlMenuTests.cs`

**Interfaces:**
- Produces: `bool Workspace.Pinned { get; init; }`; `Result WorkspaceManager.SetWorkspacePinned(Guid id, bool pinned)`; control commands `pin-workspace`, `unpin-workspace` replying with the workspace description, which now carries `pinned`.

- [ ] **Step 1: Write the failing tests**

In `PinnedWorkspaceTests` (setup copied from `FoldTests.Started`, two workspaces are enough):
- `A_workspace_starts_unpinned`: no workspace in `manager.State.Workspaces` has `Pinned`.
- `Pinning_persists_and_pulses`: `SetWorkspacePinned(a, true)` succeeds; `State` and `store.Stored` both have `Pinned == true` for `a`; `SaveCount` up by 1; one `StateChanged` pulse.
- `Unpinning_clears_it`: pin then unpin, `Pinned == false`.
- `Pinning_an_unknown_workspace_fails`: `SetWorkspacePinned(Guid.NewGuid(), true).IsFailure`.

In `RemoteControlMenuTests`, `Pin_and_unpin_a_workspace`, shaped like `Fold_and_unfold_a_row`: `Run(manager, "pin-workspace", "Main")` is Ok and its JSON has `pinned == true`; `unpin-workspace` brings it back to false.

- [ ] **Step 2: Run them, expect compile failure** (`Pinned` and `SetWorkspacePinned` do not exist)

Run: `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~PinnedWorkspaceTests|FullyQualifiedName~RemoteControlMenuTests"`

- [ ] **Step 3: Implement**

`Workspace.Pinned` with a comment naming the spec and Petre's "i need to jump in and out of the messengers". `SetWorkspacePinned` is `SetWorkspaceFolded` with `Pinned` in place of `Folded`. `SetRowPinned(string workspace, bool pinned)` in `RemoteControl.Workspaces.cs` is `SetRowFolded`'s twin. The ControlUsage test that fails on a dispatched command missing from the list must pass.

- [ ] **Step 4: Run the Core tests, expect PASS**

Run: `dotnet test tests/Deskbar.Core.Tests`

- [ ] **Step 5: Commit** `feat: a workspace can be pinned, from the pipe as well`

### Task 2: What each item says and where it goes

**Files:**
- Create: `src/Deskbar.Core/Domain/WorkspaceJumps.cs`
- Test: `tests/Deskbar.Core.Tests/WorkspaceJumpsTests.cs`

**Interfaces:**
- Consumes: `Workspace.Pinned` (Task 1), `RecentWorkspaces.Back` (existing, `Maybe<Workspace>`).
- Produces:
  ```csharp
  // One item: the pinned workspace it belongs to, what it reads, where a click goes (None: nowhere), and
  // whether it is the flipped "back" form.
  public sealed record WorkspaceJump(Guid PinnedId, string Label, Maybe<Guid> Target, bool IsBack);

  public static class WorkspaceJumps
  {
      public const string BackGlyph = "↩"; // the bar's own back button's glyph (FloatingBar.xaml, BackButton)
      public static IReadOnlyList<WorkspaceJump> Of(IReadOnlyList<Workspace> workspaces, Maybe<Guid> current, Maybe<Workspace> back);
  }
  ```

Rules: one jump per workspace with `Pinned`, in list order. Not current: `Label = Name`, `Target = Id`, `IsBack = false`. Current with a back: `Label = $"{BackGlyph} {back.Name}"`, `Target = back.Id`, `IsBack = true`. Current without one: `Label = Name`, `Target = None`, `IsBack = false`.

- [ ] **Step 1: Write the failing tests**
  - `Only_pinned_workspaces_get_an_item_in_list_order`: workspaces A (pinned), B, C (pinned); `Of(ws, None, None)` gives PinnedIds `[A, C]`.
  - `Outside_an_item_goes_in`: current B, back A; the jump for C is `("C", Target C, IsBack false)`.
  - `Inside_the_item_flips_to_back`: current C (Messengers), back B (Deskbar); the jump for C has `Label == "↩ Deskbar"`, `Target == B`, `IsBack == true`.
  - `Inside_with_nothing_to_go_back_to_names_itself_and_goes_nowhere`: current C, back None; `Label == "C"`, `Target.HasNoValue`.
  - `On_an_unnamed_desktop_every_item_goes_in`: current None, back A; every jump targets its own pinned id.
- [ ] **Step 2: Run, expect compile failure**

Run: `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~WorkspaceJumpsTests"`
- [ ] **Step 3: Implement `WorkspaceJumps.Of`** as one expression over `workspaces.Where(w => w.Pinned)`.
- [ ] **Step 4: Run, expect PASS**
- [ ] **Step 5: Commit** `feat: work out what a pinned workspace's item reads and where it goes`

### Task 3: The items on the strips and in the bar's header

Before any code: a mockup (memory "Mockup before visual changes"). Show Petre the current strip next to the strip with the item in both states (outside: "Messengers" in its colour; inside: "↩ Deskbar"), plus the header line, and recommend one. Build what he picks. The values below are the starting proposal.

**Files:**
- Create: `src/Deskbar.App/FloatingBar.WorkspaceJumps.cs`
- Modify: `src/Deskbar.App/ScreenStrip.cs` (`SetTabs`, ~line 216)
- Modify: `src/Deskbar.App/FloatingBar.Strips.cs` (`FillStrips` passes the jumps)
- Modify: `src/Deskbar.App/FloatingBar.Shell.cs` (`BuildShellStrip` adds `jumpLine` as the last child of `ShellStart`)
- Modify: `src/Deskbar.App/FloatingBar.xaml.cs` (row menu: the pin item after "Fold row", ~line 6931; the full rebuild fills `jumpLine`)
- Test: `tests/Deskbar.Windows.Tests/FloatingBarWorkspaceJumpsTests.cs` (new, harness copied from `FloatingBarStripsTests`), `tests/Deskbar.Windows.Tests/ScreenStripTests.cs`

**Interfaces:**
- Consumes: `WorkspaceJumps.Of`, `WorkspaceJump` (Task 2); `SetWorkspacePinned` (Task 1); `manager.ByRecentUse().Back`; `WorkspaceAccent(Overview, WorkspaceGroup)`.
- Produces:
  - `public void ScreenStrip.SetTabs(string name, Brush? colour, IReadOnlyList<FrameworkElement> tabs, IReadOnlyList<FrameworkElement>? jumps = null)`: jumps go right after `NameTag`, before the tabs; the tabs' room is the screen width minus the tag's and every jump's desired width.
  - In `FloatingBar`: `internal const string WorkspaceJumpTag = "workspace-jump"` on every item; `IReadOnlyList<WorkspaceJump> JumpsFor(Overview overview)`; `FrameworkElement JumpButton(WorkspaceJump jump, Overview overview)`, a new element on every call; `StackPanel jumpLine`, refilled on each full rebuild and from `FillStripsAlone`.

Look: a button the strip's height, 10 DIP side padding like `NameTag`, the label in white. The background is the colour of the workspace the click goes to (`WorkspaceAccent` of the target, or the strip's neutral grey with no target). Tooltip: "Go to Messengers" / "Back to Deskbar". In the header, the same buttons at the header's glyph height.

Row menu: the existing `Add(glyph, text, action)` shape. The glyph is plain Unicode, not MDL2 (the `MenuGlyph` comment's ruling); the mockup settles which. Text: `pinned ? "Unpin from strip" : "Pin to strip"`, action `Report(manager.SetWorkspacePinned(workspaceId, !pinned))`.

- [ ] **Step 1: Write the failing tests**
  - In `ScreenStripTests`, `Pinned_items_take_their_width_out_of_the_tabs_room`: a strip given two 100 DIP wide jumps and N tabs gives each tab `StripPlan.TabWidth(WidthDip - tag - 200, N)`, and `Row.Children` is `[NameTag, jump, jump, tab...]`.
  - In `FloatingBarWorkspaceJumpsTests`:
    - `Every_strip_gets_the_pinned_items_after_the_name_tag`: two strips, Messengers pinned, current Deskbar; each strip's `Row.Children[1]` is tagged `WorkspaceJumpTag` and reads "Messengers"; the two strips' elements are different instances.
    - `Inside_the_pinned_workspace_the_item_reads_back`: current Messengers, Deskbar visited before it; the item reads "↩ Deskbar".
    - `Clicking_the_item_switches_through_the_manager`: raising Click on it switches the fake desktops to the target's desktop, as `Clicking_a_tab_toggles_the_window_through_the_manager` checks a tab.
    - `The_header_line_carries_the_items`: `jumpLine` has one child per pinned workspace, and none with nothing pinned.
    - `A_reentrant_rebuild_does_not_double_the_pinned_items`: shaped like the strips' re-entrancy test.
    - `The_row_menu_pins_and_unpins`: the row menu has "Pin to strip"; invoking it sets `Pinned`; the rebuilt menu then says "Unpin from strip".
- [ ] **Step 2: Run, expect failures**

Run: `dotnet test tests/Deskbar.Windows.Tests --filter "FullyQualifiedName~WorkspaceJumps|FullyQualifiedName~ScreenStripTests"`
- [ ] **Step 3: Implement** `SetTabs`'s extra argument, `FloatingBar.WorkspaceJumps.cs` (with a header comment: Petre's words, the spec, why one item flips), `FillStrips` building `JumpButton`s per strip, `jumpLine`, and the menu item.
- [ ] **Step 4: Run the whole routine suite and the Release build, expect PASS and no warnings**

Run: `dotnet build Deskbar.sln -c Release -warnaserror` then `dotnet test Deskbar.sln --filter "Category!=Integration"`
- [ ] **Step 5: Commit** `feat: pinned workspaces on the strips and the bar, flipping to back inside`

### Task 4: Live check, docs, PR

**Files:**
- Modify: `README.md` (one paragraph where the strips are described), `docs/superpowers/notes/manual-test-script.md` (a pinned workspace section)

- [ ] **Step 1:** Stop the running Debug instance, build, start `src/Deskbar.App/bin/Debug/net10.0-windows10.0.19041.0/Deskbar.exe` in the background (memory: restart immediately, never leave it down). Pin a workspace with `deskbar.cmd pin-workspace <name>` against it, and check `deskbar.cmd workspaces` shows `"pinned": true`. Ask Petre to try the loop: into Messengers from the strip, then back.
- [ ] **Step 2:** Docs, then commit `docs: pinned workspaces in the README and the manual test script`.
- [ ] **Step 3:** Push the branch, open the PR with a `## Release notes` section written for users and the model-name line at the end, then arm the base watch from CLAUDE.md.
