# Bar edit mode, and the end of child workspaces

Date: 2026-11-20. Two pull requests, in this order: child workspaces go first, then the edit mode is
built on the simpler model.

## Why

Petre wants to arrange workspaces on the bar by hand: "a mode for drag and dropping workspaces", "when
activated, i should drag and drop bars", "i should also be able to group them when i drop it onto an
existing bar". Today a row moves only through its right-click menu (Move up, Move down, Move to top,
Move to end), one step per click, and joining or leaving a group is two more menu trips.

It has to be a mode. Outside it a press on a row switches workspace and a press on an icon drags a
window; a row that tried to be both a click target and a drag handle lost clicks for weeks (#48, and
the long comment above `FloatingBar.OnPreviewMouseLeftButtonDown`). Inside the mode a press on a row
means "pick up this row", so nothing is guessed from movement.

Child workspaces go because Petre never found them useful ("let's also eliminate child workspaces, i
have never found them to be useful"), and because they are the most dangerous code the app has: a
child borrows its parent's windows by pinning other programs' windows to every desktop and moving them
home afterwards (`WorkspaceManager`, "a nested workspace borrows its parent's windows (#42)"), with a
persisted list of borrowed windows and a crash repair for it. Removing them first also removes every
exception the drop rules would otherwise need for an anchor row.

## Goals

- A mode on the bar in which workspace rows and group headers are dragged and dropped.
- Dropping between rows moves the row there; dropping onto a row groups the two.
- No child workspaces anywhere: not in the model, the bar, the menus, Manage or the control pipe.
- Nobody loses a workspace, a group, a name or a colour in the change.

## Non-goals

- Dragging rows outside the edit mode. A plain press on a row stays a workspace switch.
- Nesting groups inside groups. Groups stay one level.
- Merging two groups by dropping one onto the other. A group header only moves.
- Moving windows in the edit mode. Icons are inert while it is on.
- Rows that are not workspaces (an unnamed desktop's row, Unplaced) cannot be dragged and are not drop
  targets; they are not in `AppState.Workspaces`.

## Anti-goals

- A drop that does something the user cannot see coming. Every drop target shows what the drop will do
  before the button is released.
- A drop that leaves the bar half done: each drop is ONE state change and one persist, so a refused
  drop changes nothing.
- Windows moving between desktops as a side effect of arranging rows.

## Approaches considered

For the mode itself:

1. **A mode on the bar (chosen).** The rows you already look at become draggable. Nothing new to learn
   about where things are.
2. A drag list in Manage. Easier to build (a WPF list with reorder), but it is a second picture of the
   bar, and Petre asked for the bar.
3. Drag without a mode, with a modifier. Ctrl+drag already moves the bar, and another modifier on top
   of that is a gesture nobody would find.

For dropping onto a row that is not in a group, two kinds of group existed:

1. **A named group with no parent (chosen).** Pure membership, no window borrowing. What "New group…"
   makes today.
2. Nest under the target. Rejected first because a drop is easy to aim slightly wrong and nesting changes
   which windows a row shows, then made moot by removing child workspaces altogether.

For naming that new group: a prompt (chosen, Petre: "it should ask me for the group title, right?")
over an automatic name. The drag is over by the time the prompt opens, so it interrupts nothing.

## Pull request 1: remove child workspaces

### What a user sees

- Every existing anchored group (a parent with children) becomes a plain named group. It keeps its
  name, members and colour. The former parent becomes the group's first member. The group now has a
  header row carrying its name, the way every anchorless group does, so the bar shows one more line
  per former parent.
- A child row no longer shows its parent's windows.
- "Add child…" leaves the row menu, "Nest under" and "Un-nest" leave Manage, `add-child` leaves the
  control pipe.

### Migration

On load, in the same place `AppState.Migrated` runs:

- Every group with an `AnchorWorkspaceId` loses it. Its stored `Name` already exists (it started as the
  parent's name), so nothing is invented.
- The bar must not reshuffle on update. `MembersOf` put the anchor first whatever the list order, and
  `NestWorkspace` never reordered the list, so the anchor is often NOT first among its members in
  `Workspaces`. The migration therefore rewrites `Workspaces` into the order the bar draws (the same
  flattening `MovedTo` does after any move), anchor first in its group.
- That can change the group's colour, which comes from a list position when it has none of its own
  (`AppState.ColourSlotOf`, the anchor's index today, the first member's after). So a former anchored
  group with no `Color` gets the colour it was showing written into `Group.Color`. Ungrouped rows
  whose list index moves can change colour, as they do after any move today. (My call; this only
  affects other users: Petre's state.json has four groups, all anchorless, and no inherited pins.)
- The pre-groups migration (`ParentId` to groups) produces anchorless groups from now on, with the
  parent first.
- `InheritedPins` and `RepairInheritedPins` STAY, for one reason: a machine that crashed while a child
  was borrowing has windows pinned to every desktop, and nothing else would ever send them home. The
  start-up repair already runs; after it, the list is always empty. Mark both as legacy, to be deleted
  in a later release.

### What is deleted

Builder to find the full set; the known parts are:

- `Group.AnchorWorkspaceId`, `Group.IsAnchored`, `AppState.IsAnchor`, `AppState.LendsWindowsTo`, the
  anchor-first rule in `AppState.MembersOf`.
- The borrowing in `WorkspaceManager`: the `borrowed` dictionary, the inherit step on arrival,
  `ReleaseBorrowed`, `PersistBorrowed`, `ParentDesktopOf`, and the borrowed-window answer in the
  "which desktop is this window on" lookup. The bounce cooldown also guards focus restore; keep that
  part.
- `WorkspaceManager.NestWorkspace`, `AddChildWorkspace`, and every "anchor moves the whole group" and
  "refused for an anchor" branch (`MoveAmongPeers`, `MoveIntoGroup`, `CreateGroup`, deletes).
- `DeleteWorkspacePrompt`'s anchor case.
- Anchor and borrow references in the tiler, quick find, workspace time and the bar's drawing of a
  nested row. (`FrontsElsewhere` uses "anchor" for a row's reference window, a different thing: leave
  it. `Overview` and `OverviewBuilder` mention nesting only in comments.) Also the bar's drawing of a
  nested row (the spine drawn for a child, `nested`,
  `NestedIndent` where it only served children). Groups keep their bracket and header.
- The row menu's "Add child…", Manage's nest controls, `add-child` in `ControlUsage` and
  `RemoteControl`.
- README sections on nesting, the `add-child` row in the control table, the row-menu screenshot's alt
  text, and the CLAUDE.md line "A nested workspace really gets its parent's windows".

`Workspace.ParentId` stays as a read-only legacy field, for the reason its comment already gives.

### A former parent's name, twice

A former anchored group is usually named after its parent, so the bar shows a header "Sparrow" over a
row "Sparrow", and the switcher and quick find, which leave the anchor's group prefix out on purpose
(`LaneColour.GroupNameOf`, #93), would read "Sparrow › Sparrow". My call: the prefix is left out
whenever the group's name equals the workspace's name, which keeps today's switcher text exactly. The
header above a same-named row stays; renaming the group from its header fixes it, and the migration
does not invent a name.

### Release notes

"Nested workspaces are now ordinary groups. A workspace you had nested under another keeps its group,
name and colour, and the parent becomes the group's first row; a nested workspace no longer shows its
parent's windows. Add child is gone from the row menu."

## Pull request 2: the bar edit mode

### Turning it on and off

- An "Arrange workspaces" item in the bar's right-click menu, added in `AddBarItems` and also on the
  group header's own menu, which does not call `AddBarItems` today. While the mode is on the item
  reads "Done arranging".
- Esc ends it, and so does a Done button the bar shows while the mode is on.
- A control pipe twin, `arrange on|off`, added to `ControlUsage` (every menu command has one).
- The mode is not persisted. A restart starts with it off.

### What changes on the bar while it is on

- Every workspace row and every group header shows a grip at its left edge and a move cursor.
- A click on a row does nothing: no switch, no fold. Icons do not drag windows and do not activate.
- The right-click menu still opens, so Rename and colour stay reachable.
- The bar does not rebuild while a drag is in progress. What stops a rebuild during a press today is
  the deferral while the left button is down over the bar (`FloatingBar.xaml.cs`, the guard around
  line 1991); a capture-based drag has to keep that guard true, including when the pointer leaves the
  bar during capture. The deferred rebuild runs after the drop.

Builder shows Petre a mockup of the grip, the insertion line and the drop-onto highlight, current
against proposed, before writing the visual code (his standing rule for visual changes).

```
 ⋮⋮ Mail        [icons]                 <- grip, row draggable
 ─────────────────────────────          <- insertion line: drop here moves the row
 ⋮⋮ Code        [icons]
┏━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┓        <- highlight: drop here groups with Docs
┃⋮⋮ Docs        [icons]        ┃
┗━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┛
 ⋮⋮ Work (group header)
 │ ⋮⋮ Tickets   [icons]
 │ ⋮⋮ Reviews   [icons]
                     [ Done ]
```

### What a drop does

A dragged workspace row, by where it is released:

| Released on | Result |
|---|---|
| The gap between two top-level rows | Moves there, ungrouped. Leaves its group if it was in one. |
| The gap between two members of a group | Joins that group at that place. |
| A row that is in a group | Joins that row's group, below that row. |
| A row that is not in a group | Asks for a group name, prefilled with the target's name. OK: a new group of the target then the dragged row, at the target's place. Cancel: nothing changes. |
| A group header | Joins that group at the top (see the zones below). |
| Its own place, or a row of its own group | Nothing. |

Which of those the pointer means is decided by the ROW under it, never by a shared gap, and the
insertion line shows the answer before release (Petre agreed, 2026-11-20):

- A row's top quarter means "before this row" and its bottom quarter "after this row", at that row's
  level; its middle half means "onto this row".
- So the bottom quarter of a group's last member joins the group at the bottom, and the top quarter of
  the next top-level row places the dragged row ungrouped above it. The line is drawn short, inside the
  group's bracket, for a join, and full width for a top-level place.
- If a group is the last thing on the bar, the empty space below the last row means "ungrouped, at the
  end".
- A group header's top quarter means "above the whole group"; the rest of the header joins the group
  at the top.
- A folded group is a target only through its header, and does not unfold during a drag.

```
 ⋮⋮ Work (header)
 │ ⋮⋮ Tickets
 │ ⋮⋮ Reviews
 │ ───────────────     <- bottom of Reviews: joins Work
 ──────────────────    <- top of Mail: ungrouped, above Mail
 ⋮⋮ Mail
```

A dragged group header moves the whole group, members in order, to a gap between top-level rows. It
cannot be dropped inside a group or onto a row.

A group that is left with one member dissolves, as `LeaveGroup` does today.

### Core operation

One method on `WorkspaceManager` per kind of drop target, each a single `Persist`, all of them pure
list arithmetic tested in `Deskbar.Core.Tests` the way `MoveAmongPeers` is. The bar only works out the
target from the pointer and calls it. The "new group" case takes the name as a parameter; the prompt
lives in the bar. Existing group rules apply (a group name that is taken is refused with the usual
message).

### Left to the builder

- How the drag is implemented. My call: mouse capture inside the bar, not `DoDragDrop`, because a row
  never leaves the bar and the OLE loop is what swallowed clicks in #48. Confirm against the code.
- The menu wording, the grip glyph and the Done button's look, within the mockup Petre approves.
- Whether the control pipe gains a `group-with <workspace> <target> <name>` command for the new-group
  drop, or `group-create` plus `group-join` is enough.
- Click trace lines for press, drag start and drop in the mode (`ClickTrace`), so a lost drop can be
  read from the log like a lost click.
- The manual test script gets a section for the mode.

## Still open

"let there be a desk" came with the first request and has not been explained. If "desk" is meant to be
the mode's name, the menu item and the control command take it.
