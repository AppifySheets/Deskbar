---
title: Tasks
summary: A short task list at the bottom of the bar, for the things you want to remember while you work.
order: 7
clip: tasks-bar
# Clips to film: tasks-bar (the bar's list with its toggles and a drag), tasks-window (the List view), tasks-matrix
# (the Matrix, with a drag between cells). task-due showed the stopwatch build and is retired.
---

The bottom of the bar carries a short task list, for the things you want to remember while you work.

## How to use it

At the top of the list is a text box that says where a note would go, "Add to Deskbar…" (or "Add…" on a desktop you have not named). Under it are the first three open tasks, one per line, with the workspace each belongs to as a small chip in that workspace's colour (or "none"). Tasks that wait on you come first, then the rest in the order of [importance and urgency](#important-and-urgent), each group in the order you drag them into. On a bar docked to a screen edge, the list uses all the height the workspace rows leave free, so it shows as many tasks as fit, never fewer than three.

When more than three are open, a small "+2 more" under the last line counts the ones out of sight. It turns orange and reads "+2 waiting" when the hidden ones are waiting on you, so nothing that needs you stays hidden without a sign. With nothing out of sight it reads **All tasks**. Either way, a click opens the [tasks window](#the-tasks-window), where every task is, open and closed.

Click in the box to write a note. Under it, always, are chips for where the note goes: the workspace you are on (ticked, and following you while the box is empty), **none**, and **other...**, which lists your other workspaces. Only the chosen chip is filled and outlined in blue; the others are dimmed. The box names where the note will go ("Add to Deskbar..." or, with none chosen, "Add..."). The workspaces in **other...** look the way they do on the bar: each one is its chip in its own colour, in the bar's order, with grouped workspaces under their group's name. On a narrow bar they wrap onto a second line. Clicking a chip starts a note just as clicking in the box does. Press Enter to add the note and Esc to clear the box. Either way the keyboard goes back to the window you were typing in, as long as that window is on the desktop you are looking at, because Deskbar never switches desktops to hand it back. If you click into another app while typing, what you typed stays in the box, with its chip, until you come back to it.

A note has a tick box at the left of its line. Ticking it closes the note at once, but its line stays where it is for six seconds, struck through, with **Undo** where the tick box was. Undo brings the note back, and otherwise the line then leaves. A double click, or a click in the half second after the list changes, is ignored rather than landing on the next note. Past the six seconds, `task list --closed` finds a closed note and `task reopen <id>` brings it back.

The workspace chip at the end of a line is a button. Click it to move the task: a menu lists **none** and every workspace in its colour, with the task's own ticked. The title shortens to make room, so the chip is never cut off.

Click anywhere else on a line to go to its workspace. A note on no workspace has nowhere to go, so the click does nothing. The task you clicked last stays highlighted, with a lighter background and a thin blue edge on its left, on the bar and in the tasks window alike, until you pick another or the task is closed. A Claude task that is done shows a close button at the right of its line, since it waits for you to look at it and close it; a note keeps its tick box whatever its status. The close button has the same six seconds of Undo, and Undo puts the task back exactly as it was: its status, its question if it was waiting on you, its place and the selection.

A task is in one of five statuses: backlog (a plain note), in progress, waiting on you, done or closed. Closed tasks leave the list.

## Important and urgent

Every task can be marked important, urgent, both or neither. Each line has two small toggles for it, an exclamation mark for important and a clock for urgent, just left of the age. Off, they stay out of sight until you point at the line or select it; on, they show in colour, red for important and amber for urgent. Hover one to see its name.

The two flags sort the list into the four quadrants of Stephen Covey's time management matrix, from *The 7 Habits of Highly Effective People*:

- I: urgent and important
- II: important, not urgent
- III: urgent, not important
- IV: neither

Tasks waiting on you stay on top whatever their flags, with the latest question first. Below them come quadrants I, II, III and IV, with a little space between groups. A new task goes to the top of its quadrant (a new task has no flags, so that is quadrant IV). Setting or clearing a flag moves the task to the top of the quadrant it lands in. A task that is done keeps its place; a closed task leaves, and one you reopen comes back at the top of its quadrant. Undo after closing puts the task back where it was. A status line from a session never moves a line.

Within a quadrant the order is yours. Drag a line up or down and let go: it goes just above the line you let go over if you are on its top half, just below it if you are on its bottom half. Let go among another quadrant's lines and the task takes that quadrant's flags. A press that does not move is still a click. Tasks waiting on you cannot be dragged and nothing can be dropped above them; once you answer, the task drops into its quadrant at its place. The list does not redraw while you hold the mouse button down, so nothing moves under the pointer.

## The tasks window

"+2 more" (or **All tasks**) under the list opens the tasks window: a window of its own with every task. It has a title bar, you can resize it, and it stays open when you click into another app, so you can keep it beside your work. It is in Alt+Tab and on the taskbar. Close it with its X. It opens again where you left it, at the same size and on the same screen, even after a restart. If that screen is gone, it opens next to the bar. Clicking "+2 more" while the window is open brings it forward, onto the desktop you are on.

The window opens on the list itself, not on a form. Open tasks are grouped as on the bar: **Waiting on you** first, then the four quadrants under Covey's own words, "I Urgent and important", "II Important, not urgent", "III Urgent, not important" and "IV Not urgent, not important". The chips along the top filter by status and show how many tasks each one holds: Open, Waiting on you, Done, In progress, Backlog and **Closed**. A status chip keeps the quadrants and shows only that status in each. Closed tasks only show under their own chip, the most recently closed first, each with **Reopen**. **Workspace: all** narrows the list to one workspace, or to tasks on none.

Each line has the same two toggles as on the bar, and drags the same way: within its group to reorder, into another quadrant to take its flags.

**List** and **Matrix** at the top right switch between this list and Covey's matrix itself: a 2 by 2 grid with urgent and not urgent across the top and important and not important down the side, so quadrant I sits top left and quadrant II top right. Each cell lists its quadrant's open tasks in their order, with the same lines, and scrolls on its own when it is long. Tasks waiting on you sit at the top of their own quadrant's cell, in orange. Drag a line within a cell to reorder it, or into another cell, an empty one included, to give it that cell's flags. Quadrant II, important but not urgent, is drawn a little brighter, since it is the one Covey says to put first. The window remembers which view you left it on.

Each line shows the task's title, then its question or status line under it, then its sessions (a square for each, blue while it is live, with "2 sessions, 1 live" or "1 session, ended"), its workspace and its comment count. A Claude task has an orange edge and a **CL** tag. A note has a tick box that closes it, with the same six seconds of Undo as on the bar.

- Click a line to go to its workspace. The window stays open behind you. The line stays highlighted, as on the bar, and when the window opens it scrolls to that task.
- Click the comment glyph (with the count beside it when there are comments) to open the task's comments. Each comment shows who wrote it, **Claude** or **you**, and when, and its text is formatted: bold, italics, code, code blocks, links and lists. Only web links can be clicked, and they open in your default browser. Type in the box under the comments and press Enter to add one as **you**. Shift+Enter starts a new line.
- **...** at the end of a line sets the status by hand (Backlog, In progress, Waiting on you or Done), closes or reopens the task, renames it in place, moves it to another workspace or deletes it. Delete needs a second click, on **Delete for good**, because it cannot be undone.
- **+ Add** opens a box for a note. Enter adds it on the workspace its chip shows, which you can change. Esc closes the box.

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
& $ts task flag 3f2a important on           # or urgent; off clears it
& $ts task order 3f2a top                   # or end, or a place in its quadrant counting from 1
& $ts tasks-window matrix                   # or list
```

`task list` gives each task's `important` and `urgent` flags, its `quadrant` ("I" to "IV") and its `order`, its place in that quadrant counting from 1 (null while it waits on you, since it is pinned on top until you answer).

The full set:

- `task list [--closed]`
- `task add <title> [--workspace <name>|none]`
- `task close <id>` and `task reopen <id>`
- `task status <id> <backlog|in-progress|waiting|done>`
- `task rename <id> <title>`
- `task move <id> <workspace>|none`
- `task comment <id> <markdown>`
- `task delete <id>`
- `task flag <id> important|urgent on|off`, the toggles' twin; the task moves to the top of the quadrant it lands in
- `task order <id> top|end|<n>`, the drag's twin, within the task's own quadrant; a task waiting on you is refused
- `tasks-window show` and `tasks-window hide`, to open or bring forward the tasks window, or close it
- `tasks-window list` and `tasks-window matrix`, to switch the window's view (remembered)

## With Claude sessions

A script or a Claude Code session can add a note to its own workspace with `task add`, for example to remember something while it waits for CI. See [Claude Code sessions](claude-sessions.md).

## Related

- [Driving it from scripts](scripts.md) lists every other command.
- [Docking and taskbar pieces](docking.md) covers the rest of the bottom strip.
