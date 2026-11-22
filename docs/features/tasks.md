---
title: Tasks
summary: A short task list at the bottom of the bar, for the things you want to remember while you work.
order: 7
clip: task-due
---

The bottom of the bar carries a short task list, for the things you want to remember while you work.

## How to use it

At the top of the list is a text box that says where a note would go, "Add to Deskbar…" (or "Add…" on a desktop you have not named). Under it are the first three open tasks, one per line, with the workspace each belongs to as a small chip in that workspace's colour (or "none"). Tasks that wait on you come first, then finished ones, then the newest.

When more than three are open, a small "+2 more" under the last line counts the ones out of sight. It turns orange and reads "+2 waiting" when the hidden ones are waiting on you, so nothing that needs you stays hidden without a sign. Click it to open the list out: up to eight lines show, and the mouse wheel scrolls through the rest. "Less" at the end folds the list back to three.

Click in the box to write a note. Under it, always, are chips for where the note goes: the workspace you are on (ticked, and following you while the box is empty), **none**, and **other...**, which lists your other workspaces. On a narrow bar they wrap onto a second line. Clicking a chip starts a note just as clicking in the box does. Press Enter to add the note and Esc to clear the box. Either way the keyboard goes back to the window you were typing in, as long as that window is on the desktop you are looking at, because Deskbar never switches desktops to hand it back. If you click into another app while typing, what you typed stays in the box, with its chip, until you come back to it.

A note has a tick box at the left of its line. Ticking it closes the note at once, but its line stays where it is for six seconds, struck through, with **Undo** where the tick box was. Undo brings the note back, and otherwise the line then leaves. A double click, or a click in the half second after the list changes, is ignored rather than landing on the next note. Past the six seconds, `task list --closed` finds a closed note and `task reopen <id>` brings it back.

Click anywhere else on a line to go to its workspace. A note on no workspace has nowhere to go, so the click does nothing. A task that is done shows a close button at the right of its line instead of the tick box, since a done task waits for you to look at it and close it. It has the same six seconds of Undo.

A task is in one of five statuses: backlog (a plain note), in progress, waiting on you, done or closed. Closed tasks leave the list.

## Tasks from older versions

Earlier versions had a stopwatch that parked a workspace for a few minutes. That is gone. Tasks from those versions come back as notes, with their timers dropped, and the old file is kept beside the new one as `tasks.json.v1`. That copy appears the first time the tasks change (you add, tick or close one, or a script does), not at start. Until then `tasks.json` is still the old file.

If `tasks.json` cannot be read at all, the list says so on its first line and keeps the unreadable file aside as a `.bak`. The cross at the end of that line dismisses the message.

## Settings

Untick **Task list** under the strip items in Manage's settings to hide the list. Tasks are kept in `tasks.json`, beside `state.json` in `%APPDATA%\Deskbar`.

## From a script

```powershell
$ts = "$env:APPDATA\Deskbar\deskbar.cmd"
& $ts task add "Wait for CI"                # a note on the caller's own workspace
& $ts task add "Call the bank" --workspace none
& $ts task list                             # JSON: the open tasks in the bar's order
& $ts task list --closed                    # the closed ones, the latest closed first
& $ts task reopen 3f2a                      # bring a closed task back
& $ts task close 3f2a                       # an id, or a unique prefix of four or more characters
```

The full set:

- `task list [--closed]`
- `task add <title> [--workspace <name>|none]`
- `task close <id>` and `task reopen <id>`
- `task status <id> <backlog|in-progress|waiting|done>`
- `task rename <id> <title>`
- `task move <id> <workspace>|none`
- `task comment <id> <markdown>`
- `task delete <id>`

## With Claude sessions

A script or a Claude Code session can add a note to its own workspace with `task add`, for example to remember something while it waits for CI. See [Claude Code sessions](claude-sessions.md).

## Related

- [Driving it from scripts](scripts.md) lists every other command.
- [Docking and taskbar pieces](docking.md) covers the rest of the bottom strip.
