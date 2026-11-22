---
title: Arranging the bar
summary: Arrange workspaces lets you reorder and group the bar's rows by dragging them.
order: 8
clip: arrange
---

**Arrange workspaces** turns the bar into a mode where you reorder and group its rows by dragging them.

## How to use it

Choose **Arrange workspaces** on any row's menu, a group header's menu or the bar's own menu. It does not work while the bar is minimized, so restore the bar first. End the mode with `Esc`, with **Done** on the strip at the bottom of the bar ("Drag rows to arrange · Esc"), or with **Done arranging** on the menu.

While the mode is on, a grip sits before every row and group header name. Clicks on rows do not switch workspaces, and the rows' icons are dimmed and do not drag windows. Everything that is not a row works as usual: Start, search, the pinned apps, the footer, the back and minimize buttons, the right-click menus and the window tabs on the screens. You can still resize the bar from its edge and move it by anything that is not a row. `Ctrl`+drag on a row does not move it.

The row under the pointer decides where a dragged row goes:

- Its top quarter drops before it and its bottom quarter after it. If that row is in a group, the dragged row goes inside the group, shown as a short line inside the group's bracket. Between top-level rows, the line is full width.
- The middle drops onto it. Onto a row in a group, the dragged row joins that group below it. Onto a row in no group, a prompt asks for a group name, prefilled with that row's name, and makes a group of the two at that row's place. Cancel changes nothing.
- On a group header, the top quarter drops above the whole group and the rest joins the group at the top.
- A folded group is joined through its line.
- Below the last row means the end of the bar.

A row that leaves a group of two dissolves it. Dragging a group header moves the whole group among the top-level rows. Pinned workspaces stay at the top and move only among themselves, and they cannot be grouped.

`Esc` during a drag cancels the drag, and a second `Esc` ends the mode.

## Settings

Manage has no setting for arranging. The mode lasts until you end it.

## From a script

`deskbar.cmd arrange on` and `arrange off` start and end the mode. Neither works while the bar is minimized. The pipe does not bring the bar to the front, so `Esc` works only once the bar has focus.

Rows and groups can also be moved one at a time:

```powershell
$ts = "$env:APPDATA\Deskbar\deskbar.cmd"
& $ts reorder Review end                  # to the bottom of its group
& $ts group-leave Review                  # out of the group
```

`reorder <ws>` and `group-reorder <group>` take `up`, `down`, `top`, `end` or a row number.

## Related

- [The bar](the-bar.md) covers the rows themselves.
- [Strips and pinned workspaces](strips.md) covers pinning a workspace.
- The README on [groups](../../README.md#groups).
