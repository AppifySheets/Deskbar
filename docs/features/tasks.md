---
title: Tasks
summary: A short task list at the bottom of the bar, for the things you want to remember while you work.
order: 7
clip: tasks-bar
# Clips to film: tasks-bar (the bar's list with its toggles and a drag), tasks-window (the List view), tasks-matrix
# (the Matrix, with a drag between cells). task-due showed the stopwatch build and is retired. Film tasks-window again
# with a goal in place, since its toolbar then shows the goal filter, and with a screenshot pasted into a comment
# (Win+Shift+S, Ctrl+V, Enter), which shows the thumbnail in the thread.
---

The bottom of the bar carries a short task list, for the things you want to remember while you work.

## How to use it

At the top of the list is a text box that says where a note would go, "Add to Deskbar…" (or "Add…" on a desktop you have not named). Under it are the first three open tasks, one per line, with how long ago it was added (dim, compact: "now", "16m", "7h55m", "3d3h") and the workspace each belongs to as a small chip in that workspace's colour (or "none"). Point at the age and its tooltip says it in words, "Added 2 hours 3 minutes ago", with the date, so it cannot be taken for a timer or a due time. The title gets all the room the line has left, and a title too long for one line takes a second, ending in … if even two are not enough (the tooltip has it whole); on a narrow bar, where the chip would leave the title too little, the chip shrinks to a dot in the workspace's colour, with its name in the tooltip. Tasks that wait on you come first, then the rest in the order of [importance and urgency](#important-and-urgent), each group in the order you drag them into. On a bar docked to a screen edge, the list uses all the height the workspace rows leave free, so it shows as many tasks as fit, never fewer than three.

When more than three are open, a small "+2 more" at the right end of the row under the box, after the workspace chip and the two toggles, counts the ones out of sight. On a narrow bar it shortens to "+2" (and "All tasks" to "All"), with the whole words in its tooltip, rather than wrapping. It turns orange and reads "+2 waiting" when the hidden ones are waiting on you, so nothing that needs you stays hidden without a sign. With nothing out of sight it reads **All tasks**. Either way, a click opens the [tasks panel](#the-tasks-window), where every task is, open and closed.

Click in the box to write a note. Under it, always, is "on" and one chip, the workspace the note goes to, with a small arrow, like "on Deskbar ▾" in the tasks panel. It starts as the workspace you are on (and follows you while the box is empty), or you can make it **none**. Click it and a menu opens with **none** first and then every workspace, each one its chip in its own colour, in the bar's order, with grouped workspaces under their group's name; the one chosen is ticked. Pick one and the chip changes to it. The box names where the note will go ("Add to Deskbar..." or, with none chosen, "Add..."). On a narrow bar the row wraps onto a second line. Clicking the chip starts a note just as clicking in the box does. Beside the chip are the two toggles a task line has, important and urgent: turn on neither, either or both before you press Enter, and the note goes to the top of the quadrant they give. They are off again after each note. Press Enter to add the note and Esc to clear the box. Either way the keyboard goes back to the window you were typing in, as long as that window is on the desktop you are looking at, because Deskbar never switches desktops to hand it back. If you click into another app while typing, what you typed stays in the box, with its chip, until you come back to it.

A note has a tick box at the left of its line. Ticking it closes the note at once, but its line stays where it is for six seconds, struck through, with **Undo** where the tick box was. Undo brings the note back, and otherwise the line then leaves. A double click, or a click in the half second after the list changes, is ignored rather than landing on the next note. Past the six seconds, `task list --closed` finds a closed note and `task reopen <id>` brings it back.

Point at a line for its tooltip: its status and title, its question or status line, its quadrant, its workspace and, when it belongs to one, its [goal](goals.md).

The workspace chip at the end of a line is a button. Click it to move the task: a menu lists **none** and every workspace in its colour, with the task's own ticked. The title shortens to make room, so the chip is never cut off; on a narrow bar, where the chip would leave the title too little, the chip becomes a dot in the workspace's colour, and clicking the dot opens the same menu.

Right-click a line for its menu: **Edit** first, which opens the title for editing right in the line (Enter saves, Shift+Enter adds a line break, Esc cancels), then **Start Claude on this**, which hands the task to a new background Claude session (see [Claude Code sessions](claude-sessions.md#going-to-a-session-and-starting-one)), the two flags, **Move to workspace...** and **Close**. Click anywhere else on a line to open that task in the [tasks panel](#the-tasks-window): the window comes forward and takes the foreground, with the task selected, scrolled into view and expanded to its comments, and you stay where you are. Double-click the line to go to its workspace and, for a Claude task, to its session: its terminal or VS Code window comes to the front, opening on the session or resuming it first when needed. A note on no workspace has nowhere to go, so the double-click does nothing, and a double-click goes only to the line its first click landed on. The task you clicked last stays highlighted, with a lighter background and a thin blue edge on its left, on the bar and in the tasks panel alike, until you pick another or the task is closed. A Claude task has the same tick box a note has, whatever its status, so you can settle it yourself whenever you like: ticking it closes it. A task that is done waits for you to look at it and tick it. The tick has the same six seconds of Undo, and Undo puts the task back exactly as it was: its status, its question if it was waiting on you, its place and the selection. A Claude task that was still in progress (ticked in the tasks panel, where it shows) comes back to the tasks panel, not to the bar, since the bar lists a Claude task only while it needs you.

A task is in one of five statuses: backlog (a plain note), in progress, waiting on you, done or closed. Closed tasks leave the list. A task that is done has a green tick in front of its title, on the bar and in the tasks panel, so finished work waiting for you to close it does not look like work still going on.

## Important and urgent

Every task can be marked important, urgent, both or neither. Each line has two small toggles for it, an exclamation mark for important and a clock for urgent. Off, they stay out of sight until you point at the line or select it, and then show over the end of the title; on, they show in colour just left of the age, red for important and amber for urgent. Hover one to see its name. The flags also show in the title itself, on the bar and in the tasks panel's List and Matrix: an important task's title is **bold** and an urgent task's is *italic*, so a task that is both is bold italic and a task with neither is plain.

The two flags sort the list into the four quadrants of Stephen Covey's time management matrix, from *The 7 Habits of Highly Effective People*:

- I: urgent and important
- II: important, not urgent
- III: urgent, not important
- IV: neither

Tasks waiting on you stay on top whatever their flags, with the latest question first. Below them come quadrants I, II, III and IV, with a little space between groups. A new task goes to the top of its quadrant (a new task has no flags, so that is quadrant IV). Setting or clearing a flag moves the task to the top of the quadrant it lands in. A task that is done keeps its place; a closed task leaves, and one you reopen comes back at the top of its quadrant. Undo after closing puts the task back where it was. A status line from a session never moves a line.

Within a quadrant the order is yours. Drag a line up or down and let go: it goes just above the line you let go over if you are on its top half, just below it if you are on its bottom half. Let go among another quadrant's lines and the task takes that quadrant's flags. A press that does not move is still a click. Tasks waiting on you cannot be dragged and nothing can be dropped above them; once you answer, the task drops into its quadrant at its place. The list does not redraw while you hold the mouse button down, so nothing moves under the pointer.

## The tasks window

"+2 more" (or **All tasks**), at the right end of the row under the box, opens the tasks panel: a full-height strip docked just inside the bar, with every task. It is registered with the shell after the bar, so the bar stays the outer strip and maximized windows start beside both. It is on every virtual desktop, always on top, and not in Alt+Tab, on the taskbar or tiled. Click the same button again, or the x in its toolbar, to close it (`deskbar.cmd tasks-window show` and `hide` do the same). Drag its inner edge to set the width: 460 DIP to start, 360 at the least, half the screen at the most. Deskbar remembers the width and whether the panel was open, so it comes back after a restart. A floating bar still gets the panel, on its nearer screen edge; minimizing the bar takes the panel away and restoring it brings it back. A full-screen app makes the panel step out of the topmost band until you leave it.

The search box at the top (Ctrl+F, Esc clears it) keeps the tasks that match every word you type, in the title, status line or question. The **Claude working** chip keeps only tasks with a live Claude session, whose session squares glow while Claude is working. Below 692 DIP the lines are slim: the status chip, the age and the assignee move into the tooltip ("Done · 5h · Claude"; a done task keeps its green tick on the line) and Close and Reopen into the ⋯ menu. The List and Matrix switch shows from 780 DIP. Filters and search start clear each time the panel opens.

The panel opens on the list itself, not on a form. Open tasks are grouped as on the bar: **Waiting on you** first, then the four quadrants under Covey's own words, "I Urgent and important", "II Important, not urgent", "III Urgent, not important" and "IV Not urgent, not important". The chips along the top filter by status and show how many tasks each one holds: Open, Waiting on you, Done, In progress, Backlog and **Closed**. A status chip keeps the quadrants and shows only that status in each. Closed tasks only show under their own chip, the most recently closed first, each with **Reopen**. **Workspace: all** narrows the list to one workspace, or to tasks on none. **Assigned: all** narrows it to the tasks assigned to you (**me**) or to **Claude**. Once you have a [goal](goals.md), **Goal: all** next to it narrows the list and the Matrix to one goal's tasks, or to tasks with no goal.

Each line has the same two toggles as on the bar, and drags the same way: within its group to reorder, into another quadrant to take its flags. A long title wraps onto more lines rather than being cut short. To edit a task in place, select its line and press F2, or choose **Edit** in its menu: the title and its description open together in two boxes (Tab moves between them), Enter saves both, Shift+Enter adds a line break, and Esc or a click elsewhere cancels. The description is yours: markdown you write under the title, for any task, shown on its line as a second block and on the bar as a first line where a Claude task shows its status line. A Claude task's status line stays the session's and is not editable. Each of your own comments has **Edit** beside its time, which rewrites it in place (Enter saves, Esc keeps it); a comment Claude wrote cannot be edited. A comment can be **settled**, with the **Settle** button beside it, once its question has its answer: it is kept, but folds away behind one dim line at the top of the thread, "2 settled comments", which unfolds them on a click (and **Unsettle** brings one back). When a session settles its question with `task answered`, its comments written since it asked are settled for you, and a session can settle one of its own with `task comment-settle`. The bar shows a title's first line, wrapped onto two lines at most, with the whole title in its tooltip.

**List** and **Matrix** at the top right switch between this list and Covey's matrix itself: a 2 by 2 grid with urgent and not urgent across the top and important and not important down the side, so quadrant I sits top left and quadrant II top right. Each cell lists its quadrant's open tasks in their order, with the same lines, and scrolls on its own when it is long. Tasks waiting on you sit at the top of their own quadrant's cell, in orange. Drag a line within a cell to reorder it, or into another cell, an empty one included, to give it that cell's flags. Quadrant II, important but not urgent, is drawn a little brighter, since it is the one Covey says to put first. The window remembers which view you left it on.

Each line shows the task's title, then its question or status line under it, then its status in a small chip (green **Done**, orange **Waiting**, blue **In progress**, grey **Backlog**), then its sessions (a square for each, blue while it is live, with "2 sessions, 1 live" or "1 session, ended"), its workspace and its comment count. A Claude task is marked by the orange edge at the start of its line, as on the bar. Every open line, a note or a Claude task, a done one included, has a tick box that closes it, with the same six seconds of Undo as on the bar. Each part of a line (flags, status, sessions, who it is assigned to, age, workspace, comments, the ⋯ button) sits in a column of its own, the same on every line, flush with the panel's right edge, and every line spans the whole panel, so a missing part leaves a gap instead of moving its neighbours. **Close** in a line's ⋯ menu closes any open task, a Claude task still in progress included.

- Click a line to select it and open its comments; click it again to close them, or press Esc with it selected. A click goes nowhere. Double-click it to go to its workspace (a Claude task's session window): the tasks panel stays open. The same on the bar: a click on a task opens it in the tasks panel, selected, scrolled into view and expanded to its comments, and a double-click takes you to its workspace and window. The line stays highlighted, as on the bar, and when the window opens it scrolls to that task.
- Click the comment glyph (with the count beside it when there are comments) to open the task's comments. Each comment shows who wrote it, **Claude** or **you**, and when, and its text is formatted: bold, italics, code, code blocks, links and lists. Only web links can be clicked, and they open in your default browser. Type in the box under the comments and press Enter to add one as **you**. Shift+Enter starts a new line.
- Pictures go in a comment too. Take a screenshot (Win+Shift+S or Print Screen) or copy an image file in Explorer, click in the comment box and press Ctrl+V, or drop image files on the box. Each picture waits under the box as a small thumbnail with an **x** that takes it out again; Ctrl+V with only text on the clipboard pastes the text as usual. Enter posts the words and the pictures together, and a comment can be pictures alone. In the thread a comment's pictures show as thumbnails under its text (they always show, even when the text is folded to three lines), and a click on one opens it full size in your image viewer. The description takes pictures the same way: in the editor, paste or drop them into the description's box, and they wait under it with the ones it already has, each with its **x**; Enter saves them all and Esc keeps none of the new ones. An open line shows its description's pictures under the text, and a closed line whose description is only pictures says "image".
- The pictures are saved when the comment is posted or the description saved, never before, as files in `%APPDATA%\Deskbar\task-images`, one folder per task. A picture wider than 4096 pixels is scaled down to 4096. Closing a task keeps its pictures; deleting it deletes them. A Claude session sees each picture's full path in `task list` and reads it, and Deskbar adds one rule to Claude Code's settings so a session can read that folder without asking you first (see [Claude Code sessions](claude-sessions.md)).
- A thread keeps itself short. Each comment shows about three lines, and a longer one ends in **Show more**, which opens it in place (**Show less** folds it back). When a thread has more than three comments it shows the latest three under a dim "3 earlier comments" line that unfolds the rest; settled comments fold away separately and do not count. The panel remembers what you unfolded for as long as it is open, through every redraw.
- Right-click a line, or click **...** at its end, for its menu. **Edit** comes first and opens the title for editing in place. The rest of the menu sets the status by hand (Backlog, In progress, Waiting on you or Done), closes or reopens the task, hands it over with **Assign to** (**Me** or **Claude**, the current one ticked), moves it to another workspace or deletes it. Delete needs a second click, on **Delete for good**, because it cannot be undone. Once you have a goal, it also has **Goal**: **None**, then this month's and this week's open goals, with the task's own ticked. Linking a task to a goal changes nothing else about it.
- **+ Add** opens a box for a note. Beside it are the important and urgent toggles, then the chip saying which workspace it goes on, which you can change. Enter adds the note at the top of the quadrant the toggles give; Esc closes the box. In the List the toggles start off and are off again after each note. In the Matrix they follow the active cell, which has a blue edge: the cell you last clicked in, or the one holding the task you selected, and quadrant II until then. Click in a cell and the toggles take its flags; flip a toggle and the active cell moves to match. A note added there lands at the top of the active cell.

## Subtasks

A task can have subtasks, one level deep: a subtask cannot have subtasks of its own.

- Pick **Add subtask…** on a line's menu, on the bar or in the tasks panel (after **Start Claude on this**). The add box opens with the parent as a chip, "↳ <parent>", next to the workspace chip, which moves to the parent's workspace. Type a title and press Enter. Esc, or a click on the parent chip, drops the parent and leaves what you typed as a plain note. On a subtask's line, **Add subtask…** adds a sibling under the same parent.
- A subtask's line starts with "↳". In the tasks panel's full line the parent's name follows, as in "↳ Ship the release › Write the notes"; on the bar and on a slim line it is "↳" alone, and the tooltip names the parent. A parent's line says how many open subtasks it has, as in "Ship the release · 2 subtasks". Lines keep their place in the list: a subtask is not moved under its parent.
- Closing a parent leaves its subtasks as they are. Deleting a parent makes its subtasks plain tasks.
- What a subtask is for: a Claude session that takes one up is told about the family, so the title can be all you write. See [With Claude sessions](#with-claude-sessions).

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
'Buy "the good" soap & tablets' | & $ts task add -   # a title with quotes or &: - reads it from stdin
& $ts task add "Renew the passport" --important --urgent   # quadrant I, at its top
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
- `task add <title>|- [--workspace <name>|none] [--important] [--urgent]`, with `-` reading the title from stdin; the flags put the note at the top of the quadrant they give
- `task close <id>` and `task reopen <id>`
- `task status <id> <backlog|in-progress|waiting|done>`
- `task rename <id> <title>`
- `task move <id> <workspace>|none`
- `task comment <id> <markdown>`
- `task delete <id>`
- `task flag <id> important|urgent on|off`, the toggles' twin; the task moves to the top of the quadrant it lands in
- `task order <id> top|end|<n>`, the drag's twin, within the task's own quadrant; a task waiting on you is refused
- `tasks-window show` and `tasks-window hide`, to open or bring forward the tasks panel, or close it
- `tasks-window list` and `tasks-window matrix`, to switch the panel's view (remembered)
- `task assign <id> me|claude`, the **Assign to** menu's twin; `task list` gives each task's `assignee`, `me` or `claude`
- `task comment-settle <comment-id> --session <id>`, a session settling one of its own comments (another's is refused); `task list`'s `thread` shows each comment's `settled` time, or null
- `task describe <id> <markdown>|-`, the description Edit sets (empty clears it), and `task comment-edit <comment-id> <markdown>|-`, which rewrites one of your own comments and refuses a session's; `task list` gives each task's `description` and its `thread`, each comment's id, `you` or `claude` for who wrote it, and its `markdown`, the comment's text; in both, a pasted picture's line carries its full path, ready for a session to read
- `task goal <id> <goal-id>|none`, the **Goal** menu's twin; `task list` gives each task's `goal` (see [Goals](goals.md))
- `task add <title> --parent <id>` adds a subtask; without `--workspace` it goes on the parent's workspace
- `task parent <id> <parent-id>|none` puts a task under another, or makes it a plain task again; a second level is refused
- `task family <id>` prints what a Claude session on the task reads about its family (plain text); `task list` gives each task's `parent` (`{id, title}` or null) and `subtasks` (ids)

A Claude Code session's own forms, each with `--session <id>` (see [Claude Code sessions](claude-sessions.md)):

- `task join <id>` and `task new <title>`, to take up a task or create one
- `task status-line <text>`, `task ask <question>`, `task comment <markdown>`, `task answered` (its question is settled: back to in progress, the blink stops; nothing happens when the task is not waiting) and `task done`, on the session's own task
- `task later <title>`, a note for later on the session's workspace
- `task subtask <title>`, a subtask of the session's own task (a sibling when that is a subtask), assigned to Claude so the next session is offered it
- `task family`, its own task's family, as its `task join` reply gave it

Every form that takes text, `task add <title>` and `task comment <id> <markdown>` included, takes `-` in the text's place and reads the text from stdin. Use it for any text with quotes or `& | < >` in it: `deskbar.cmd` hands its command line to cmd, which could run part of such a text as a command.

## With Claude sessions

Every Claude Code session keeps a task in this list. When it starts, it is offered the open tasks on its workspace, joins the one its work continues or creates its own, and keeps a short status line under the title. A task a session works on is Claude's: it has an orange edge (and no icon: the title starts right after the tick box, as on a note), and a session that joins one of your notes makes it Claude's work.

When a session needs a decision from you, its task moves to the top as **Waiting on you**, with the question on the line, and the workspace blinks. Your message alone does not answer it: the session settles the question itself with `task answered`, once your reply settles it, and the task goes back to in progress. **The bar lists only the tasks assigned to you.** Every task has an assignee, you or Claude. A task you create is yours. A session's task is Claude's while it works, becomes yours when the session asks you something or marks it done, and goes back to Claude when the session settles the question with `task answered` or takes up more work on it after it was done. So a Claude task is on the bar because it is assigned to you, which follows from the session (and from the status you set by hand: Waiting on you and Done assign it to you, and reopening a task works its assignee out again), and once the session has settled the question it leaves the bar (it stays in the tasks panel, which shows every task), and it comes back at the top when the session asks again. Nothing else takes it off: going to its workspace, switching to the session's window, or clicking or double-clicking the line leaves it there. You can also hand any task over yourself in the tasks panel, with **Assign to** in its menu: a note you give to Claude leaves the bar, and is offered to a new session starting on its workspace; a task you give back to yourself stays on the bar. You assign only in the tasks panel, never on the bar. A task that is waiting on you and that you hand to Claude leaves the bar and its "+N waiting" count, but stays Waiting on you in the tasks panel with its question, and its workspace keeps blinking until you visit it or the session answers. "+N more" counts only what the bar lists. A session marks its task done when it has finished; the line keeps its place and has the same tick box as any other, because only you close a task. A message to a session whose task is done leaves it done (it may only ask about the finished work); when the message gives the session more work on it, the session puts it back in progress itself with `task join`, and a session whose task you closed is told that new work needs a new task.

A session that joins a subtask is told about its family in the reply to `task join`: the parent's line, your description and its last comments, and the other subtasks, each with its Claude sessions, their folders and the file each session's conversation is kept in. It is asked to read the end of the parent session's conversation before it starts, and to stay clear of what the other subtasks are doing. A session on a parent is told about its subtasks the same way. The family comes again when a session resumes, and `task family` prints it on demand.

A task whose sessions have all ended stays open and says so. Sessions nobody is sitting at (`claude -p`, Agent SDK runs) make no tasks. The setting is **Claude Code sessions keep their tasks in the task list** in Manage; see [Claude Code sessions](claude-sessions.md).

## Related

- [Goals](goals.md) covers the weekly and monthly goals tasks count toward.
- [Driving it from scripts](scripts.md) lists every other command.
- [Docking and taskbar pieces](docking.md) covers the rest of the bottom strip.
