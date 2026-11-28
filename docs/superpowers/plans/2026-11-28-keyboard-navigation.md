# Keyboard-only navigation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

[Deskbar task b61f79de](https://deskbar.app/task#b61f79de-1d04-4d99-ab58-2d1ddba4190c)

**Goal:** Work the tasks panel from the keyboard alone, and show which keys exist in a keys card.

**Architecture:** One key router in a new partial, `TasksWindow.Keys.cs`, called from the existing `Press(key, modifiers)`
(so the tests drive it as they drive F2). It moves the board's existing selection (which survives redraws), opens the
existing menus, and calls the existing actions; no button becomes focusable. The pure parts (the next line, the key list
per view) live in Core and are unit tested there. The keys card is a `Border` drawn over the board in the window's own
XAML.

**Tech Stack:** C# / WPF (.NET 10), xUnit, CSharpFunctionalExtensions.

**Spec:** `docs/superpowers/specs/2026-11-28-keyboard-navigation-design.md`

## Global Constraints

- No key in this plan fires while a `TextBoxBase` has the keyboard (`Keyboard.FocusedElement is TextBoxBase`), except Esc,
  F2 and Ctrl+F, which keep exactly today's behaviour.
- No new global hotkey; nothing changes outside the tasks panel and the task window.
- Buttons stay `Focusable = false` (the `Flat` comment: "a click never takes the keyboard from a box Petre is typing in").
- Letters act only with no modifiers; `?` is Shift+OemQuestion; F1 is the same as `?`.
- Code comments explain the why at length, as the rest of the file does. Project C# rules: `=>` for one-liners, `var`,
  readonly collections, `Maybe`/`Result` from CSharpFunctionalExtensions.
- Windows tests run only through `scripts/test-hidden.ps1`, never `dotnet test` on Petre's desktop.

## Review Focus

1. A text box has the keyboard (comment box, search, add box, rename): Up/Down/letters/Enter must reach the box untouched.
   Test in Task 2 (`Keys_do_nothing_while_a_text_box_has_the_keyboard`).
2. A redraw lands between two presses (Claude sessions redraw every few seconds): the second Down must move on from the
   line selected, not restart. Test in Task 2 (`Down_after_a_redraw_moves_on_from_the_selected_line`).
3. The selected task is not drawn (filtered out, another tab): the first Down picks the first line, Up the last. Core test
   in Task 1.
4. An empty view (no lines, Stats): arrows, Enter and letters do nothing and return false (the key is not swallowed).
   Test in Task 2 and Task 3.
5. Shift+F10 reaches `Press` as `Key.System` with `SystemKey = F10`: the window hook must unwrap it, as
   `TaskDetailWindow` already does. Test in Task 3 drives `Press(Key.F10, Shift)` and the hook change is reviewed.

---

### Task 1: Core: the next line and the key list

**Files:**
- Create: `src/Deskbar.Core/Tasks/PanelKeys.cs`
- Test: `tests/Deskbar.Core.Tests/Tasks/PanelKeysTests.cs`

**Interfaces:**
- Produces:
  - `public enum PanelPlace { InboxList, InboxPage, List, Matrix, Stats }`
  - `public sealed record PanelKey(string Keys, string Does);`
  - `public static class PanelKeys`
    - `public static Maybe<Guid> Step(IReadOnlyList<Guid> shown, Maybe<Guid> selected, int by)`: `by` is -1/+1 for
      Up/Down, `int.MinValue`/`int.MaxValue` for Home/End. Clamps at the ends (no wrap). Selected not in `shown`: +1 and
      End give the first/last as their own sense says, i.e. Down/Home give the first, Up/End give the last. Empty `shown`:
      None.
    - `public static IReadOnlyList<PanelKey> For(PanelPlace place)`: the keys card's rows for that place, in the
      order of the spec's table, only the rows that act there. Exact copy below.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Down_moves_to_the_next_line_and_stops_at_the_last()
// shown [a,b,c]: Step(shown, a, +1) == b; Step(shown, c, +1) == c
[Fact] public void Up_moves_to_the_previous_line_and_stops_at_the_first()
// Step(shown, b, -1) == a; Step(shown, a, -1) == a
[Fact] public void Home_and_End_go_to_the_ends()
// Step(shown, b, int.MinValue) == a; Step(shown, b, int.MaxValue) == c
[Fact] public void With_nothing_shown_selected_Down_picks_the_first_and_Up_the_last()
// Step(shown, None, +1) == a; Step(shown, None, -1) == c; Step(shown, x /*not shown*/, +1) == a
[Fact] public void An_empty_view_steps_nowhere()
// Step([], a, +1) == None
[Fact] public void The_card_lists_paging_keys_only_where_they_act()
// For(InboxList) contains Keys "Enter" with Does "Open the task's page"; For(Stats) has no "Up / Down" row;
// every place's list ends with "? or F1" / "These keys"
```

- [ ] **Step 2: Run, verify they fail**

Run: `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~PanelKeysTests"`
Expected: FAIL, `PanelKeys` does not exist.

- [ ] **Step 3: Implement `PanelKeys` in `src/Deskbar.Core/Tasks/PanelKeys.cs`**

Card copy (Keys / Does), filtered per place as marked:

| Keys | Does | Places |
|------|------|--------|
| Up / Down | Select the line above or below | InboxList, List, Matrix |
| Up / Down, Page Up / Page Down | Scroll the page | InboxPage |
| Home / End | First or last line | InboxList, List, Matrix |
| Enter | Open the task's page | InboxList |
| Enter | Open or fold the thread | List, Matrix |
| Esc | Back, fold, or close a box | all |
| Left / Right | The tab before or after | InboxList |
| Ctrl+1 to Ctrl+4 | Inbox, List, Matrix, Stats | all |
| Menu key or Shift+F10 | The task's ⋯ menu | InboxList, InboxPage, List, Matrix |
| C | Write a comment | InboxPage, List, Matrix |
| P | Open its pull request | InboxList, InboxPage, List, Matrix |
| W | Move it to another workspace | InboxList, InboxPage, List, Matrix |
| R | Recheck its state | InboxList, InboxPage, List, Matrix |
| N | Add a task | all |
| F2 | Rename the task | InboxList, InboxPage, List, Matrix |
| Ctrl+F | Search | all |
| ? or F1 | These keys | all |

- [ ] **Step 4: Run, verify they pass** (same command, PASS)

- [ ] **Step 5: Commit** `feat: the panel's key list and next-line step`

### Task 2: Arrows, Home/End and Enter move and open

**Files:**
- Create: `src/Deskbar.App/TasksWindow.Keys.cs`
- Modify: `src/Deskbar.App/TasksWindow.xaml.cs` (`Press` ~3673; the `PreviewKeyDown` hook ~179; the constructor for
  `Activated`)
- Modify: `src/Deskbar.App/TasksBoard.cs` (`TasksWindowActions` init block ~380, new `Select` member; `Pick` ~855)
- Test: `tests/Deskbar.Windows.Tests/TasksBoardTests.Keys.cs`

**Interfaces:**
- Consumes: `PanelKeys.Step`, `PanelPlace` (Task 1); existing `Keys`, `selectedId`, `inboxPage`, `OpenInboxPage(Guid)`,
  `Actions.Pick`, `RevealSelected()`, `pageScroller`.
- Produces:
  - `TasksWindowActions.Select : Action<Guid>?` (init), wired in `TasksBoard` to a new `public void Select(Guid id)`:
    `selection.Select(id); Refresh();` with no thread toggle (unlike `Pick`), then `window.RevealSelected()`.
  - In `TasksWindow.Keys.cs`: `bool Navigate(Key key, ModifierKeys modifiers)`, `PanelPlace PlaceShown` (InboxPage when
    `inboxPage` is set, else from `latest`), `Maybe<Guid> KeyTask` (`inboxPage` if set, else `selectedId`),
    `static bool TypingIn(IInputElement? focused) => focused is TextBoxBase`.
  - `Press` becomes `... || Navigate(key, modifiers)` after the existing three; the hook passes
    `e.Key == Key.System ? e.SystemKey : e.Key`.

- [ ] **Step 1: Write the failing tests** (rig: `Build()`, `InboxTasks(t)`, `ChooseInbox(t)`, `Click(...)`, as in
  `TasksBoardTests.Inbox.cs`)

```csharp
[Fact] public void Down_and_Up_walk_the_inbox_lines_in_the_drawn_order()
// All tab; nothing selected; Press(Down) -> selected == Keys[0]; Press(Down) -> Keys[1]; Press(Up) -> Keys[0];
// Press(End) -> Keys[^1]; Press(Home) -> Keys[0]; every Press returns true; no page opened (InboxPageShown == null)
[Fact] public void Enter_opens_the_selected_lines_page_and_Esc_comes_back()
// select Keys[1] by Down twice; Press(Enter) -> InboxPageShown == Keys[1]; Press(Escape) -> InboxPageShown == null
[Fact] public void In_the_List_Enter_opens_and_folds_the_selected_thread()
// List view; Down; Press(Enter) -> thread open (the window's open-thread state for that id); Press(Enter) -> folded
[Fact] public void Down_after_a_redraw_moves_on_from_the_selected_line()
// Down (Keys[0]); force a redraw (t.Controller.Refresh()); Down -> Keys[1]
[Fact] public void Keys_do_nothing_while_a_text_box_has_the_keyboard()
// open a page, focus its comment box (TypeComment rig or commentBoxes via the "New comment" automation name);
// Press(Down), Press(Enter), Press(C) all return false and the selection is unchanged
[Fact] public void Arrows_do_nothing_in_an_empty_view()
// no tasks; Press(Down) returns false
```

- [ ] **Step 2: Run, verify they fail**

Run: `pwsh scripts/test-hidden.ps1 -Filter "FullyQualifiedName~TasksBoardTests.Keys"` (check the script's parameter
name in its header first). Expected: FAIL (Press returns false).

- [ ] **Step 3: Implement**

- `Navigate`: return false when `TypingIn(Keyboard.FocusedElement)`. Up/Down/Home/End: on an Inbox page scroll
  `pageScroller` (LineUp/LineDown/ScrollToHome/ScrollToEnd; PageUp/PageDown too); elsewhere
  `PanelKeys.Step(Keys, selectedId, by)` then `Actions.Select`. Enter: InboxList -> `OpenInboxPage(id)`; List/Matrix
  -> `Actions.Pick(id)`. Return false when nothing acted, so the key is not swallowed.
- `Activated += ...`: when nothing in the window has the keyboard, `FocusBoard()`, so a panel brought up by a click on a
  (non-focusable) line still gets the keys. Do not call it from `Open` (its comment says why).

- [ ] **Step 4: Run, verify they pass**, plus the whole `TasksBoardTests` class (no regressions in F2/Esc tests).

- [ ] **Step 5: Commit** `feat: arrows, Home, End and Enter in the tasks panel`

### Task 3: Tabs, views, the menu key and the letters

**Files:**
- Modify: `src/Deskbar.App/TasksWindow.Keys.cs`
- Test: `tests/Deskbar.Windows.Tests/TasksBoardTests.Keys.cs`

**Interfaces:**
- Consumes: `ChooseInboxTab(InboxTab)`, `inboxTab`, `InboxShown?.Tabbed`, `Actions.ShowInbox`, `Actions.ShowMatrix`,
  `Actions.ShowStats`, `latest.MatrixAllowed`, `OpenMenuFor(Guid)`, `menuButtons`, `inboxLines`,
  `OpenMoveMenu(Guid, Maybe<Guid>, UIElement?)`, `OpenPullRequest(Guid, PullRequestLink)`, `RowFor(Guid)`,
  `Reevaluates(TaskRowView)`, `Actions.Reevaluate`, `commentBoxes`, `ShowAdd()`, `OpenMenu(ContextMenu)`.
- Produces: no new members beyond private helpers.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Left_and_Right_walk_the_inbox_tabs()
// NeedsYou; Press(Right) -> InboxTabShown == Claude; Right, Right -> All; Right -> All (stops); Left -> Notes
[Fact] public void Ctrl_digits_switch_the_views()
// Press(D2, Control) -> List shown; D1 -> Inbox shown; D4 -> Stats shown (Keys empty)
[Fact] public void The_menu_key_and_Shift_F10_open_the_selected_tasks_menu()
// select a line; Press(Apps) -> an open ContextMenu whose items include the Edit item; close it;
// Press(F10, Shift) -> open again
[Fact] public void C_puts_the_keyboard_in_the_pages_comment_box()
// open a page; Press(C) -> the comment box IsKeyboardFocusWithin
[Fact] public void P_opens_the_one_pull_request_and_offers_a_menu_for_several()
// task with one PR: Press(P) -> Actions.OpenLink called with its URL (rig a recording OpenLink);
// two PRs: Press(P) -> an open ContextMenu with both labels
[Fact] public void W_opens_the_move_menu_and_N_the_add_box()
// Press(W) -> open ContextMenu containing the workspace names; Esc it; Press(N) -> AddBoxShown
[Fact] public void Letters_with_a_modifier_or_with_nothing_selected_do_nothing()
// nothing selected: Press(W) false; Press(C, Control) false
```

- [ ] **Step 2: Run, verify they fail** (same script, FAIL)

- [ ] **Step 3: Implement** in `Navigate`:
  - Left/Right: only InboxList with `Tabbed`; order `InboxTab.NeedsYou, Claude, Notes, All`, clamped.
  - Ctrl+D1..D4 (and NumPad1..4): `ShowInbox(true)`, `ShowMatrix(false)`, `ShowMatrix(true)` only when
    `MatrixAllowed`, `ShowStats()`.
  - Apps, or F10 with Shift: `OpenMenuFor(id)`; when `menuButtons` has no button for it (a List line whose "⋯" is
    hidden), set the returned menu's `PlacementTarget` to the line (`inboxLines[id].Line` or the selection frame) before
    it opens; read `OpenMenuFor` and give it an optional `UIElement? anchor` parameter rather than moving it afterwards.
  - C: `commentBoxes.TryGetValue(id)` -> `Focus()`; none -> false.
  - P: `RowFor(id).PullRequests`: one -> `OpenPullRequest`; several -> a ContextMenu, one item per PR label, opened
    through `OpenMenu`.
  - W: `OpenMoveMenu(id, row.Place.Workspace, anchor)` unless `row.Closing`.
  - R: `Reevaluates(row)` -> `Actions.Reevaluate(id)`.
  - N: `ShowAdd()`.

- [ ] **Step 4: Run, verify they pass**, plus all of `TasksBoardTests`.

- [ ] **Step 5: Commit** `feat: tabs, views, the task menu and letter keys in the tasks panel`

### Task 4: The keys card and its hint; docs

**Files:**
- Modify: `src/Deskbar.App/TasksWindow.xaml` (a `Border x:Name="KeysCard"` over the board's content grid, collapsed)
- Create: `src/Deskbar.App/TasksWindow.KeysCard.cs`
- Modify: `src/Deskbar.App/TasksWindow.Keys.cs` (`?`/F1, Esc closes the card first)
- Modify: `src/Deskbar.App/TasksWindow.Inbox.cs` (`InboxFooterOf`: a quiet "? keys" button at the right)
- Modify: `README.md` (the tasks section: one paragraph on the keys), `docs/superpowers/notes/manual-test-script.md`
  (steps KB1-KB6)
- Test: `tests/Deskbar.Windows.Tests/TasksBoardTests.Keys.cs`

**Interfaces:**
- Consumes: `PanelKeys.For`, `PlaceShown`.
- Produces: `internal bool KeysCardShown`, `void ToggleKeysCard()`, `internal const string KeysHintText = "? keys"`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Question_mark_and_F1_open_the_keys_card_for_the_view_shown_and_Esc_closes_it()
// Inbox list: Press(OemQuestion, Shift) -> KeysCardShown; the card's texts include "Open the task's page";
// Press(Escape) -> !KeysCardShown and the inbox page state unchanged; Press(F1) -> shown; Press(F1) -> hidden
[Fact] public void The_card_follows_the_place_it_was_opened_in()
// open a page, Press(F1): card has "Scroll the page", not "Select the line above or below"
[Fact] public void The_footer_hint_opens_the_card()
// Click(ButtonNamed(footer, KeysHintText)) -> KeysCardShown
```

- [ ] **Step 2: Run, verify they fail**

- [ ] **Step 3: Implement**: the card is a dark rounded `Border` (the panel's existing surface and ink brushes), a title
  "Keys", then a two-column `Grid` from `PanelKeys.For(PlaceShown)`, key column in the panel's mono or semibold face.
  It closes on Esc (handled first in `Escape()`), on `?`/F1 again, on a click outside it, and on a view change. A
  redraw does not close it. The footer hint is a `Flat` button with dim ink, tooltip "Keyboard keys".

- [ ] **Step 4: Run, verify they pass**; then build `dotnet build Deskbar.sln -c Release -warnaserror`.

- [ ] **Step 5: Commit** `feat: a keys card for the tasks panel, and docs`
