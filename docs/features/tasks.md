---
title: Tasks
summary: The stopwatch in the bar's bottom strip parks a workspace for 1, 5, 10 or 30 minutes and tells you when it is due.
order: 7
clip: task-due
---

The stopwatch in the bar's bottom strip, beside the language code, parks a workspace for later and tells you when the time is up.

## How to use it

Click the stopwatch and pick 1, 5, 10 or 30 minutes to park the workspace you are on. The task takes the title of the workspace's latest open task, or the title of the window you were typing in when that window is on this workspace, or else the workspace's name. Parking the same title again reuses the task instead of adding a row.

While a timer runs, the stopwatch shows the soonest one counting down ("4:32") next to a swatch in its workspace's colour, and that workspace's row gets a small blue pie that empties as the time runs out. When a timer runs out, the stopwatch reads "due" in orange and the row blinks.

Below the four lengths, the stopwatch's menu lists your open tasks. Each has its own submenu: **Done**, **Clear timer**, or a new length.

Coming back ends the timer. Switch to the workspace and stay there for 2 seconds, and every timer that was running there when you arrived ends while the task stays open. Flicking past a desktop on the way somewhere else does not count, nothing restarts a timer by itself, and a timer you set during those 2 seconds is left running.

If a timer runs out while the bar is out of sight (minimized, hidden, stepped aside, or under a full-screen app), a small card appears in the bottom right corner of the main screen and follows you across desktops. It never takes the focus. Click it to go to the task's workspace. For a task with no workspace, the card opens the stopwatch menu instead. The card closes once the task is no longer due. At most five cards show at a time, and the rest stay due in the bar.

## Settings

Untick **Tasks** under **Bottom strip** in Manage's settings to hide the stopwatch. Tasks are kept in `tasks.json`, beside `state.json` in `%APPDATA%\Deskbar`.

## From a script

```powershell
$ts = "$env:APPDATA\Deskbar\deskbar.cmd"
& $ts task add "Wait for CI" --in 10m       # parks the caller's own workspace
& $ts task add "Call the bank" --workspace none
& $ts task list                             # JSON: open tasks, today's done ones, and asks
& $ts task done 3f2a                        # an id, or a unique prefix of four or more characters
```

The full set:

- `task list`
- `task add <title> [--workspace <name>|none] [--in 1m|5m|10m|30m]`
- `task timer <id> (--in ... | --clear)`
- `task done`, `task rename` and `task delete`

## With Claude sessions

A Claude Code session can park its own workspace with `task add`, for example while it waits for CI. A due task's row blinks the way it does when a Claude session asks for you. See [Claude Code sessions](claude-sessions.md).

## Related

- [Driving it from scripts](scripts.md) lists every other command.
- [Docking and taskbar pieces](docking.md) covers the rest of the bottom strip.
