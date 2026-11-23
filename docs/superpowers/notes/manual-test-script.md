# Deskbar manual test script (per release)

Setup: build + run TaskSpaces.App (the project keeps its name; the exe is Deskbar.exe); open
Task View to observe desktops. Until 2.0 is on main, set `DESKBAR_APPDATA` to a scratch folder
first so a Debug build does not migrate the real profile. The override does not let it run
beside 1.x: the check for a running 1.x ignores it, so exit the 1.x app from its tray icon first.

1.  Tray icon visible; menu lists workspaces + Manage… + Exit.
2.  Add workspace "Work" -> desktop "Work" exists in Task View.
3.  Add rule ProcessName=notepad -> Work; open Notepad -> it lands on Work's desktop
    (disappears from current taskbar; visible after switching to Work).
4.  Tray menu -> Work: taskbar shows only Work's windows.
5.  Windows tab: select a window, Send to workspace -> it moves.
6.  Windows tab: rename a browser window to "Amy related" -> taskbar shows "Amy related";
    navigate to another page -> short name comes back within ~a second.
7.  Restore title -> original title returns.
8.  Rules tab: add rename rule TitleRegex "Remote Desktop" -> "RDP"; open mstsc -> renamed.
9.  Rename workspace -> desktop renames in Task View. Remove workspace -> desktop closes,
    its windows merge into the previous desktop (Windows behavior), nothing is lost.
10. Exit via tray -> all renamed titles restored; desktops remain (by design).
11. Restart app -> workspaces re-bind to the same desktops (no duplicates).
12. Invalid regex in Rules tab -> message box; rules not saved; nothing crashes.
13. Start-with-Windows checkbox -> HKCU\...\Run entry appears/disappears (regedit).
14. Put Notepad in Work via a rule; exit the app (inventory persists); close Notepad;
    relaunch the app -> prompt lists "Work (1 app(s))"; Restore relaunches Notepad and
    it lands on Work's desktop.
15. Left-click tray icon -> switcher panel opens near the tray, dark-themed, one group
    per workspace with window counts; current workspace bold. **Superseded by Task 9
    (items 25-26 below): left-click now opens the tray MENU; the panel is reached by
    hover or hotkey.** Re-verify panel appearance/grouping via item 25's hover instead.
16. Panel: click a window row on another workspace -> lands on that workspace with the
    window focused; panel closes.
17. Panel: right-click a window -> Pin to all workspaces -> window follows across every
    workspace and appears in the 📌 Pinned group. Unpin reverses it. Send to a
    workspace from a pinned window unpins it.
18. Panel: windows on a non-workspace desktop appear under that desktop's own name
    (e.g. "Desktop 1"), including the current desktop.
19. Panel: renamed windows show "ShortName · was: Original Title"; icons show on every
    row (panel AND Manage window's Windows tab).
20. Roster: close an app that was in a workspace -> it stays listed, dimmed
    "(not running)"; click it -> it relaunches with its original command line and lands
    back in its workspace. ▶ on the header starts everything missing and switches.
21. Add app… -> pick an exe + optional args -> appears dimmed in the group;
    Remove from workspace deletes it.
22. Rename persistence: manually rename a window, exit Deskbar, relaunch ->
    within ~5s the window is renamed again (same title as before rename required).
23. Rename sweep: rename a browser window, navigate tabs rapidly -> the short name
    reasserts within ~5s even if an occasional title flip slips through.
24. Restart Deskbar with workspace apps still open -> restore prompt does NOT
    offer duplicates (only genuinely-missing apps listed).
25. Hover the tray icon for about half a second -> the switcher panel peeks open
    WITHOUT taking focus (taskbar/whatever app you were using stays highlighted as
    active); move the mouse away from the panel -> it hides itself again. Hover once
    more and click a row/button INSIDE the peeked panel first -> from then on it
    behaves like a normal clicked-open panel (stays open, responds to child dialogs,
    Deactivate-hides when you click elsewhere).
26. Left-click the tray icon -> the tray MENU opens (same items/behavior as
    right-click); the switcher panel does NOT open from a left-click anymore.
27. Ctrl+Alt+Right / Ctrl+Alt+Left cycles through Deskbar workspaces in the order
    they're defined (not the native Windows desktop order), wrapping from last back
    to first (and vice versa), and skipping any plain OS desktop that isn't a
    workspace.
28. With at least two workspaces defined, Ctrl+Alt+2 jumps straight to the second
    workspace (by defined order); Ctrl+Alt+9 with fewer than 9 workspaces does nothing
    (no crash, no message box).
29. If another application already owns one of these chords (e.g. some machines bind
    Ctrl+Alt+Left/Right to a display-rotate shortcut), Deskbar shows exactly ONE
    warning message box at startup listing the chord(s) it could not register, and
    every other hotkey it DID register still works normally.
30. Panel/Windows tab: drag a window row onto another workspace's group -> it moves
    there (and is unpinned first if it was pinned); drag a row onto the 📌 Pinned
    group -> it pins (follows every workspace) instead of moving.
31. Manage window's Windows tab shows the exact same grouped view as the panel (same
    groups, same running/roster rows); drag-and-drop works identically there; each
    row's right-click menu covers rename, restore title, pin/unpin, and send to
    workspace.
32. Windows on an unbound/default OS desktop appear grouped under a sensibly-derived
    name for that desktop (e.g. "Desktop 1"), including whichever such desktop is
    currently active; any oddball shell/system windows that don't belong to a real
    desktop grouping appear under an "Unplaced" catch-all group.
33. The old ＋ button is gone from the workspace header; adding an app to a workspace
    is done via "Add app…" in the workspace header's right-click context menu.
34. Tray menu -> "Show floating bar" -> a small translucent, always-on-top bar appears
    with one icon row per group: Pinned first if anything is pinned, then EVERY
    workspace (an empty one shows just its label), then each unbound desktop that has
    windows, then "Unplaced" if any window's desktop can't be resolved; click an
    icon -> lands on that window (switches workspace if needed, focuses it); the bar
    stays open after the click. Click a row's dim LABEL -> switches to that
    workspace/desktop (the 📌 Pinned and "Unplaced" labels are not clickable).
35. Drag the bar somewhere else by pressing a row LABEL or the padding/info line (not
    an icon) -> it moves with the cursor; restart the app -> the bar reappears in that
    same spot. Toggle it off/on from the tray menu (checkmark reflects state) ->
    restart the app -> it stays hidden/shown exactly as last left. Right-click the bar
    -> "Hide floating bar" also hides it and persists that.
36. With the bar showing, open a new app that belongs to a workspace -> its icon
    appears in that workspace's row live, without reopening the bar; close it -> the
    icon disappears.
37. Hover any bar icon -> the info line at the bottom of the bar names that window:
    full title, then dimmed process · group (· "was: <original title>" if Deskbar
    renamed it). Move off the icon -> the line returns to its hint text. The bar must
    NOT resize or jump position while hovering different icons.
38. Drag a bar icon onto another row -> the row highlights and the info line reads
    "→ move to <group>" while hovering it; dropping moves that window there (workspace
    row = move + unpin if pinned; 📌 row = pin; unbound-desktop row = move out of every
    workspace). Dropping onto its own row does nothing. The "Unplaced" row never
    highlights and never accepts a drop.
39. Drag a rule-matched browser window out of its workspace onto the "Main" (unbound)
    row, then browse to a different page so its title changes -> it STAYS on Main
    (rules must not drag it back). Its app still appears in the old workspace's
    not-running roster / ▶ Start list.
40. A single CLICK on a bar icon (no movement) still jumps to that window — the new
    drag behavior must not swallow plain clicks.
41. Update from 1.x (use a machine or profile you can afford to migrate, not a scratch
    `DESKBAR_APPDATA` run). First the refusal: with TaskSpaces 1.x running, double-click a
    downloaded `Deskbar.exe`. A "Deskbar" message says to exit TaskSpaces from its tray icon
    first, and after OK nothing else happens: no second bar, no `%APPDATA%\Deskbar\state.json`,
    and `taskspaces.cmd` still in `%APPDATA%\TaskSpaces`. Then the update itself: with 1.x still
    running and "Start with Windows" ticked, take the update from its tray menu. Deskbar starts.
    Then check five things. The workspaces, groups, colours and window renames match what 1.x
    showed. `time.json` and `last-used.json` are in `%APPDATA%\Deskbar` as well, so Manage's
    Time tab shows the hours 1.x counted and the bar's icons keep the aging 1.x gave them. In
    regedit the `TaskSpaces` Run value is gone and `Deskbar` points at the running exe, so a
    login starts one bar and not two. `%APPDATA%\Deskbar\deskbar.cmd help` prints the command
    list, and `taskspaces.cmd` is gone from `%APPDATA%\TaskSpaces`. The old folder's
    `state.json` is still there, untouched.

## Screen strips

The window list along the top of every screen (spec `docs/superpowers/specs/2026-11-19-screen-strips-design.md`,
measurements `docs/superpowers/notes/2026-11-19-screen-strips-probes.md`). Run with two screens, the trace on
(`%APPDATA%\Deskbar\trace.on`, log `%TEMP%\deskbar-trace.log`), and a build that is not Petre's only copy. Every strip
line in the log starts with `strip on <left>,<top>`.

S1.  Turn on from the menu: right-click the bar, Window list, At the top of each screen. A 24 DIP strip appears
     along the top of every screen, the name tag of the current workspace first, then its windows as tabs. The
     trace shows a `registered` and a `docked` line per screen. Maximized windows now start below the strip.
S2.  Turn it on from the pipe instead (`deskbar.cmd help` lists the command) and confirm the same result. Turn it
     off from the menu: every strip goes, and every screen's work area is back.
S3.  Dock the bar to the left edge, then the right edge, then across the seam of the two screens. The bar sits
     under the strip each time and never over it, and no strip moves.
S4.  Dock the bar across the seam of two screens that are NOT level. The bar's corner covers the right end of the
     lower screen's strip. Petre to decide how that should look.
S5.  Maximize a window, then switch workspaces back and forth. The maximized window's top edge does not move.
S6.  Minimize the bar. The strips stay, and no work area changes.
S7.  Switch to a workspace on another virtual desktop. Every screen shows its strip on every desktop, with no empty
     band of wallpaper at the top. If one is empty, the trace has a `strip pin failed` line for that screen.
S8.  Lock and unlock with two screens. Read the trace: does a strip ever disappear and come back (a `removed` line
     followed by a `registered` line for the same screen)? Does any maximized window's top edge move? A `dock
     refused` line during the unlock is expected while a screen wakes; `taken back` should not climb.
S9.  Unplug one screen. How long does its strip sit over the other screen before it goes (the time between the
     unplug and the `removed` line)? Plug it back: the strip returns and the windows are not moved.
S10. Play a full-screen video on one screen. Do only that screen's strips step aside, or all of them? The trace
     geometry line of the affected strip says `stepped aside for a full-screen window`.
     Then the other way round: start the full-screen video first, and lock and unlock (or unplug and replug that
     screen) while it plays, so the strip is created with the video already up. The shell sends ABN_FULLSCREENAPP
     only when full screen starts or ends, so a strip made in the middle of it may never hear of the video. Does the
     new strip sit on top of the video? Its trace geometry line has no `stepped aside` part if it was never told.
S11. Snap a window to the left half, then turn strips on. The window stays snapped and its bottom edge stays on the
     screen.
S12. Put a restored window with its title bar at the very top of a screen, then turn strips on. It moves down by
     the strip (the trace says `moved ... off the band`) and does not come in front of the window you are in.
S13. Ask a Claude session for you while its window is on a strip. The tab has no orange glow. Is that a problem?
     Petre to decide.
S14. Connect over RDP and disconnect. Strips follow the screens that are there, and none is left over a screen that
     is gone.
S15. Exit from the tray. Every strip goes and every work area is back, on every screen.

## Pinned workspaces

P1. Pin a workspace (Messengers) from its row menu. Every screen's strip shows a "Messengers" item after its last
    tab, and the taskbar has a "messengers" button. The dot and underline are Messengers' colour. The bar's header has
    no item; Messengers' row is drawn first, with a pin.
P2. Click it on the second screen's strip. You are in Messengers, and every item now reads "↩ <where you were>" in
    that workspace's colour.
P3. Click it again. You are back where you were, and the item reads "Messengers" again.
P4. Go to Messengers with Win+Ctrl+Tab or Win+Ctrl+arrows instead, then use the item. "Back" is still the
    workspace you came from.
P5. A strip crowded with windows: the tabs narrow, the item keeps its full width, and the strip stays one line.
P6. `deskbar.cmd jump Messengers` from elsewhere goes in; run again from inside, it goes back.
P7. Unpin from the row menu. The items go from every strip and the taskbar.

The previous workspace's item (until chosen, on with the list on the screens and an auto-hiding taskbar; Manage,
"Show the previous workspace on the strips and the taskbar", or `deskbar.cmd previous-workspace on|off`).

P8. With nothing pinned, a fresh state.json and the taskbar NOT on auto-hide, switch between two workspaces. No strip, no titled
    list and no taskbar button shows a "↩ <previous>" item. The bar header's own ↩ button still goes back.
P9. Tick the setting in Manage, with no restart. Every screen's strip ends with "↩ <where you were>", the titled list
    (window list in the row or at the bottom) ends with it, and the taskbar has a "↩" button. Click one: you go back.
P10. Untick it. The item leaves every strip and the list, and the taskbar button closes, also with the bar minimized
    to its taskbar button. `deskbar.cmd previous-workspace on` and `off` do the same.
P11. With the setting off, pin a workspace and go into it. Its pinned item still reads "↩ <where you were>".
P12. Start again from a fresh state.json with the window list on the screens and the taskbar on auto-hide. Every
    strip and the taskbar show "↩ <previous>" without touching Manage, and Manage's box opens ticked. Move the list
    into the bar: the item goes. Untick the box, then put the list back on the screens: it stays off.

## Tasks in the bar

The task list at the bottom of the bar (spec `docs/superpowers/specs/2026-11-22-tasks-board-design.md`). `$ts` is
`%APPDATA%\Deskbar\deskbar.cmd`.

42. The list has no header: the text box is its first line, reading "Add to <the workspace you are on>…".
    Under it, always, "on" and ONE chip with a small arrow, naming the current workspace. Switch desktops:
    the box names the new one ("Add…" on an unnamed desktop) and the chip follows, and nothing in the
    list jumps. Click in the box, type "probe", press Enter: the note appears as a line with an empty tick box
    and the workspace's chip, the add chip stays where it is, and the keyboard is back in the window you were
    typing in. Click the tick box: the line stays for six seconds, struck through, with
    "Undo" in the tick box's place, then leaves.
43. `$ts task add "a"`, "b", "c", "d" and "e" (five tasks). The list shows three lines and a small "+2 more"
    under them. Click it: all five show and "Less" is at the end. Click "Less": back to three.
44. With the five tasks of step 43 open, `$ts task status <id> waiting` on all five. They sort to the top, three
    show and the two hidden ones wait on you: the line under them reads "+2 waiting" in orange. Set two of them
    back to `backlog`: it reads "+2 more" in the plain ink again.
45. Click in the box, type some text, pick "none", then click into another app and switch desktops. The text
    and the "none" choice are still there when you come back; click in the box and carry on. Enter and Esc both
    give the keyboard back to the window you were typing in (type a few letters after each to check). With the
    box empty and idle, click a chip: it starts a note, as a click in the box does.
46. Click in the box, then the "other..." chip, and pick a workspace: the chip takes its name and is ticked,
    the text stays, and Enter files the note there.
47. Click a line of a note on another workspace: you go there. Click a note on "none": nothing happens.
48. Untick "Task list" in Manage's Settings tab (strip items): the list disappears from the bar. Tick it
    again: it is back, with the same tasks.
49. Quit Deskbar, put a version 1 `tasks.json` (with timers) in the state folder and start it. The tasks
    come back as notes with no timers. There is no `tasks.json.v1` yet: it appears with the first change, so
    add or tick a note, and `tasks.json.v1` then sits beside the new file with the old file's bytes.
50. Change the icon size on Manage's Settings tab: the list's text keeps its proportion, the bar does not
    widen for a long title (it trims with an ellipsis), and `$ts task list` agrees with what is shown.
51. Add twelve notes and click "+9": eight lines show and the wheel over the list scrolls to the rest, while the
    workspace rows above stay where they were. Click "Less": three lines again.
52. Double-click the tick box of the first note: only that note closes; its line stays struck through with
    "Undo" where the tick box was, and the second click does not undo it or close the next note. Click "Undo"
    within six seconds: the note is back as it was. Tick two notes in a row: both lines stay in place, each with
    its own Undo, and leave after their six seconds. A done Claude task's close button does the same, and Undo
    brings it back done. After the six seconds, `$ts task list --closed` lists the closed note first and
    `$ts task reopen <id>` brings it back.
53. Make the bar as narrow as it goes (drag its edge in). Nothing in the list is cut off at the right: the box,
    "+N more", the lines' chips, and the add row ("on", the chip and the two toggles), which wraps onto a second
    line, a long workspace name trimmed with an ellipsis.
54. Click the add chip ("on Deskbar ▾"): a menu opens with "none" first, then each workspace as its chip in its
    colour, in the bar's order, grouped workspaces under their group's name (which cannot be clicked), the chosen
    one ticked. Pick "none": the chip reads "none" and the box reads "Add…". Open it again and pick a workspace:
    the chip is that workspace's, in its colour, and the box reads "Add to <that workspace>…". There is no separate
    "none" or "other…" chip. With twenty or so workspaces the menu scrolls rather than running off the screen.
55. Click a line's workspace chip: a menu of "none" and every workspace in its colour opens, the task's own ticked,
    and the click does not switch workspaces. Pick another: the line's chip changes, and `$ts task list` agrees. Add
    a note with a title longer than the bar on a narrow bar: the title ends in an ellipsis and the chip is whole.
55a. Dock the bar to a screen edge with twenty or so open tasks: the list fills the empty height between the last
    workspace row and the footer, at least three lines, "+N more" counting the rest. Add workspaces or shorten the
    screen: the list gives up lines, never below three. Float the bar again: three lines, and the bar lands where
    it floated before, not clamped against the docked height. With notes above Claude lines that show a session's
    window icon, at the smallest text size, the docked list settles on one count and the bar stays responsive.
55b. Click a line: it stays highlighted (a lighter background and a blue edge on its left). Open the tasks window:
    the same task is highlighted there and scrolled into view. Click another line in the window: the bar's
    highlight follows. Close the highlighted task: no line is highlighted any more.

55c. Point at a note's line: two dim toggles appear over the end of its title, an exclamation mark and a clock. Click the
    exclamation mark: it turns red and stays, and the line moves above the plain ones. Click the clock on another
    line: amber, and that line moves above the plain ones but below the important one. Set both on a third: it goes
    to the very top of the open lines. A task set to waiting stays above all of them, showing its flags. A little
    space separates each group, and nothing names the groups on the bar.
55d. Drag a plain line down past another and let go on that line's lower half: it lands under it. A press and
    release without moving still goes to the task. Drag a plain line onto an important one: it becomes important
    (its exclamation mark turns red). Try to drag a waiting line: nothing moves. Drop a line on a waiting line: it
    lands under the waiting block. While dragging, add a task from the pipe: the list does not change until you
    let go. `$ts task list` shows `important`, `urgent`, `quadrant` and `order` matching what you see, and
    `$ts task order <id> 2` and `$ts task flag <id> urgent on` move lines as a drag and a click do.

## The tasks window

The tasks window (spec `docs/superpowers/specs/2026-11-22-tasks-board-design.md`, "The window" and "Comments").

56. With three or fewer open tasks the line under the list reads "All tasks"; with more, "+N more". Click it: a
    window titled "Tasks" opens beside the bar (not over it), with a dark title bar, on the list and not on a form
    (no box has the keyboard). It is in Alt+Tab and on the taskbar, and the bar does not list it as a window.
57. Groups read WAITING ON YOU, then the quadrants that have tasks (I to IV), a new task on top. The chips count each
    status. Click "Closed": only closed tasks show, the latest closed first, each with "Reopen"; click "Reopen" on
    one and it goes back to the open view. "Workspace: all" lists every workspace as its coloured chip, grouped;
    pick one, then "none", then "All workspaces".
58. Click into another app: the window stays open. Resize it and move it to another screen, close it with its X,
    then click "+N more": it comes back where you left it, same size. Quit and start Deskbar again: it still opens
    there. Turn that screen off (or edit `TasksWindow` in state.json to an off-screen place): it opens beside the bar.
59. Leave the window open, switch to another desktop and click "+N more": the window comes to this desktop rather
    than taking you back. `$ts tasks-window hide` closes it, `$ts tasks-window show` opens or brings it forward.
60. Click a task's comment count: its thread opens. Post a comment through the pipe with bold, italics, inline code,
    a link to https://deskbar.app and a two-item list: it shows formatted, by "you". Type a comment and press Enter:
    it appears under "you", the box empties and keeps the keyboard. Shift+Enter makes a new line instead. While you
    type, add a task from the pipe: what you typed stays. Click the link: it opens in the default browser. A `file:`
    link in a comment shows as plain text.
61. "…" on a line: set the status to each of the four, Rename… (Enter keeps it, Esc drops it), Move to (the
    coloured workspace menu), Close (the line stays struck through with Undo for six seconds; Undo brings back
    the status it had, a waiting task its question too, and the selection) and Delete, then Delete for good. While that menu is open, change a task from the pipe:
    nothing moves until the menu closes.
62. Click a note's line on a workspace: you go there and the window stays open on its own desktop. "+ Add": a box
    opens, "on <workspace> ▾" says where; Enter adds the note there, Esc closes the box.
63. With flagged tasks, the window's List shows Waiting on you, then the quadrants captioned "I Urgent and
    important" to "IV Not urgent, not important". The toggles and the drag work as on the bar, and the bar
    follows every change. Pick the "In progress" chip: the quadrants stay, with only tasks in progress.
64. Click "Matrix": a 2 by 2 grid, URGENT and NOT URGENT across the top, IMPORTANT and NOT IMPORTANT down the side,
    I top left, II top right (drawn a little brighter), III bottom left, IV bottom right. A waiting task sits at the
    top of its own cell in orange. Drag a line into another cell, an empty one included: it takes that cell's flags.
    Add twenty tasks to one cell: that cell scrolls, the others do not move. Close and reopen the window: still the
    Matrix. `$ts tasks-window list` switches it back.

## Claude sessions and their tasks

The hooks and the briefing (spec `docs/superpowers/specs/2026-11-22-tasks-board-design.md`, "Claude sessions"; plan
Tasks 7 to 9). Needs Claude Code installed and both Claude boxes in Manage ticked. `$ts` is
`%APPDATA%\Deskbar\deskbar.cmd`.

65. In Manage's Settings tab, "Claude Code sessions keep their tasks in the task list" sits under "Teach Claude Code
    about Deskbar". Untick the teach box: the tasks box greys out and unticks. Tick it again: the tasks box is back,
    ticked. `%USERPROFILE%\.claude\settings.json` has Deskbar's entries on Notification and Stop running
    `claude-hook.cmd`, and on SessionStart, UserPromptSubmit and SessionEnd running `claude-task-hook.cmd`, beside
    your own hooks. Untick only the tasks box: the three task entries and `claude-task-hook.cmd` go, the other two stay.
66. With a note on a workspace whose VS Code window you open, start a new Claude Code session there. Its first reply
    knows its session id and lists the note among the tasks it could join (ask it what Deskbar told it). A note of
    yours on no workspace is not in that list.
67. Ask the session to start some work. It joins the note or creates a task: the line turns orange with a CL tag and
    a live session square in the tasks window, and the status line under the title changes as it works.
68. Ask the session to ask you something through its task. Switch to another workspace first: the task jumps to the
    top as Waiting on you with the question, in orange, and the session's workspace blinks. Answer in the session:
    the task goes back to in progress and the question goes.
69. Ask the session to finish and mark its task done: the line shows a close button. Send the session another
    message: the task is in progress again. Mark it done again and close it yourself, then send a message: the
    session says its task was closed and makes a new one only for new work.
70. Close the session's VS Code tab: the task stays open with "no live session". Resume the session (the history
    list): it names its task straight away and is not offered the list again, and a task that was done stays done
    until you send a message.
71. Run `/clear` in a session that has a task, then send a message. Note whether it keeps its task (same session id)
    or is offered the list again (a new id); either is accepted, but record which.
72. Send one `/loop` prompt, then a plain message, in a session whose task waits on you: the task keeps waiting and
    keeps blinking, and the session's reminder names the question and `task answered`. Reply to settle the question:
    the session runs `task answered`, the task is in progress and the blink stops (Ruling 49).
73. Run `claude -p "say ok"` in a workspace's folder from a terminal: no task appears and nothing changes on the
    list. Its finish still blinks the workspace, as before.
74. Quit Deskbar and send a session a message: the reply comes at once, with nothing about Deskbar in it and no error.
    Start Deskbar again and send another: the reminder line is back.
75. Swap back to an older Deskbar build and start a session: the bar does not blink on session start or on each
    message (the task hooks reach a script the older build leaves alone), and Notification and Stop still blink.
    Start the current build again: `$ts help` lists `claude-tasks` and the session forms of `task`.

## Goals

Weekly and monthly goals, pull request 1: the pipe, the task's link, the tasks window's menu and filter, the bar's
tooltip (spec `docs/superpowers/specs/2026-11-22-goals-planner-design.md`). The planner window and the bar's "Plan
this week" line come with pull request 2. `$ts` is `%APPDATA%\Deskbar\deskbar.cmd`.

G1. With no goals (a fresh profile, no `goals.json`): a line's "…" menu has no "Goal" item, the toolbar has no goal
    filter, and `goals.json` is still missing after a restart.
G2. `$ts goal add "Ship goals"` and `$ts goal add "Store listing" --month`: the toolbar now shows "Goal: all ▾"
    without reopening the window, and "…" on a line has "Goal" with None, "Store listing" (month) and "Ship goals"
    (week). Pick "Ship goals": the line stays where it was, its flags unchanged; open the menu again and it is ticked.
G3. Hover that task's line on the bar: the tooltip's last line reads "Goal: Ship goals". The line itself shows
    nothing new. `$ts goal rename <id> "Ship the goals planner"`: hover again and the tooltip says the new title; the
    line did not move, and a click on it right after the rename still goes to its workspace.
G4. "Goal: all ▾": All goals, No goal, then the goals. Pick "Ship goals": only its task shows, and the status chips
    count only it. Switch to the Matrix: the same. `$ts goal parent <week-id> <month-id>`, then pick "Store listing":
    the weekly goal's task shows too. Pick "No goal": every task without a goal. `$ts goal delete <week-id>`: the
    filter goes back to "Goal: all".
G5. `$ts goal done <id>`: "Goal" on a line no longer offers it, and `$ts task goal <task-id> <id>` is refused with
    "The goal is done". `$ts goal list` prints this month's goals then this week's with their progress, and
    `$ts task list` names each task's goal.

## Tiling on the split tree

Tiling's layout is a split tree since the split tree change (spec
`docs/superpowers/specs/2026-11-19-tiling-split-tree-design.md`). Run on a Pro copy (or while tiling is free), with
the trace on (`%APPDATA%\Deskbar\trace.on`, log `%TEMP%\deskbar-trace.log`), on a workspace tiled with the tile
button. `$ts` is `%APPDATA%\Deskbar\deskbar.cmd`. A, B and C are three windows of the workspace.

T1. Drop beside a stacked tile.
    1. Start from the automatic grid for three windows: one tall tile on the left, two stacked on the right. If the
       workspace has a stored layout, use "Reset to grid" first (T3).
    2. Call the left tile A, the top-right tile C, and the bottom-right tile B.
    3. Drag B by its title bar and let go with the pointer in the bottom quarter of A, near the middle of A's width
       and above the bottom 6% of the screen (that strip is the bottom band, T2). Now the left half is A over B
       and the right half is C, full height.
    4. Drag C and let go with the pointer in the right quarter of A. While you hold it, the preview shows A and C
       side by side on the top half and B full width underneath. Let go: that is the layout (A | C over B). B grew to
       the full width and A kept its height; nothing else moved. Before the split tree, C could only become a new
       column.
    5. Drag C into the right band (the outer 6% of the screen's width), letting go in the BOTTOM half of the screen,
       over B. Letting go in the top half is over C's own place, which is a nudge and puts it back. The left half is
       A over B again and C is the right half.
    6. Drag C and let go with the pointer in the right quarter of B. The preview, then the layout: A full width on
       top, B and C side by side underneath.
T2. Screen-edge bands. Drag a tile to the screen's left edge, inside the outer 6% of the screen and not over the
    middle of another tile. Let go over another tile's part of the band, not over the dragged tile's own place: a
    release inside its own place is a nudge and puts it back. The preview shows a full-height column on the left,
    and letting go makes it, taking half the width of what it lands beside. Repeat for the right, top (a full-width
    row) and bottom edges, and once into a corner: the nearer edge wins. Does 6% feel too thin or too thick? The
    depth is not a setting: it is a constant in code, `TileArrange.Band` in
    `src/Deskbar.Core/Tiling/TileArrange.Layout.cs`, for a developer to change and rebuild.
T3. Even out, then Reset to grid. From T1's A | C over B, drag the separation between A and C well off the middle.
    Right-click the workspace's row: "Even out tiles" sits directly under "Tile windows". The tile button's
    right-click menu offers the same item above "Keep the space when a window closes". Click it: every split is
    equal again and the shape is still A | C over B; the trace has one `tile: <workspace> sizes evened out` line.
    Open either menu again: the item now reads "Reset to grid". Click it: the screen goes back to the automatic
    grid. Open again: neither item is there. Then repeat the pair from the pipe, `$ts even <workspace>` and
    `$ts reset-grid <workspace>`: the same two steps, and both exit 0.
T4. A restart keeps a hand-made layout. Make A | C over B again and drag one split off the middle. Exit from the
    tray and start Deskbar again, then go to that workspace: the same shape and the same split, nothing re-gridded.
    `state.json` has the tree under the workspace's `TileLayouts` and `"TileSizes": []`.
T5. Upgrade from a build before the split tree, on a profile that build tiled with a dragged split. After the
    update the grid looks exactly as it did, to the pixel, and Even out offers itself as before.
T6. Minimized together, back in place. Make A over B on the left and C full height on the right (T1, step 3).
    1. Minimize all three at once (Win+D, or Win+M) and bring them back the same way (Win+D again, or
       Win+Shift+M). Each tile is where it was. The trace has one `came back to its place` line per window and no
       `arrived` line.
    2. Minimize all three again and bring them back one at a time from the taskbar, in any order: C first has the
       screen; A next sits left of C; B last goes below A, and the shape is whole again.
    3. Minimize A, then B, each with its own minimize button. Restore A, then B: the same shape again.
    4. Minimize A only. B and C are side by side. Switch to another desktop and back: B and C are still side by
       side (they used to flip to B over C). Restore A: back above B.

## Locked tiles and other screens on arrival

Spec `docs/superpowers/specs/2026-11-22-tile-lock-and-other-screens-design.md`. Two screens, a tiled workspace, the
trace on. `$ts` is `%APPDATA%\Deskbar\deskbar.cmd`.

L1. Lock from the bar. One tile A on screen 1, one tile B on screen 2. Right-click A's icon on the bar: "Don't split
    this tile" sits just above Close, unticked. Tick it; the trace has `tile: <workspace>: <hwnd> locked`. Right-click
    an icon of a window on another workspace, or a floating dialog's: the item is not there.
L2. A locked tile is not halved. Click A, then open a new window from it (Ctrl+N in a browser). It does not take half
    of A: the trace says every tile on screen 1 is locked and it goes to screen 2, where it splits B. Untick A and
    repeat: the new window halves A again.
L3. Lock from the pipe. `$ts windows`, take A's hwnd, `$ts lock-tile 0x... on`: the reply is A's window; the bar's
    item is ticked. `$ts lock-tile 0x... maybe` prints the usage line. On a window that is not a tile the reply is
    "That window is not in a tile."
L4. Empty screen first. Close or move everything of the workspace off screen 2, keep two tiles on screen 1, click one,
    open a new window. It fills all of screen 2; screen 1 does not change. The trace has `took empty screen 2`.
L5. A maximized window counts. Maximize a window of the workspace on screen 2 (it leaves the grid). Open a new window
    from a tile on screen 1: it halves that tile, and screen 2's maximized window is left alone.
L6. An opening elsewhere. Turn on "Keep the space when a window closes", close one of two tiles on screen 2, click a
    tile on screen 1 and open a new window: it fills screen 2's opening (`took the opening ... on another screen`).
L7. A drop stays put. With screen 2 empty, drag a window from another workspace onto screen 1's half of this
    workspace's row: it splits a tile on screen 1 and screen 2 stays empty.
L8. The lock goes with the window. Lock A, close it, open a new window that takes its place: its icon menu shows the
    item unticked.

## Arranging the bar

Arrange mode (spec `docs/superpowers/specs/2026-11-20-bar-edit-mode-design.md`). Start with
three ungrouped workspaces A, B and C, plus a group G holding D and E, and one pinned workspace.
`$ts` is `%APPDATA%\Deskbar\deskbar.cmd`.

AR1. Right-click row A and choose "Arrange workspaces". A grip appears before every row and group
     header name, icons dim, and a strip at the bottom of the bar reads "Drag rows to arrange · Esc"
     with a Done button. Right-click another row: the menu now says "Done arranging".
AR2. Click a row and an icon. Nothing switches, and no window is dragged. Right-click menus still
     open. Ctrl+drag on a row does not move the bar; dragging its edge still resizes it.
AR3. Drag A to the top quarter of C, then to the bottom quarter of C. A full-width line shows each
     time, and A lands before, then after, C.
AR4. Drag A to the bottom quarter of E, the last row in G. A short line shows inside G's bracket,
     and A becomes G's last member.
AR5. Drag B onto the middle of C (ungrouped). A prompt asks for a group name, prefilled "C".
     Cancel: nothing changes. Repeat and accept: a group of B and C exists at C's place.
AR6. Drag A onto the middle of D (in G). A joins G below D.
AR7. Drag a row out of a group of two to a top-level gap. The group dissolves and the other member
     stays in place.
AR8. Drag onto a group header's top quarter: the row lands above the whole group. Onto the rest of
     the header: it joins at the top. Fold the group and drop onto its line: it joins the group.
     Drop below the last row: it goes to the end of the bar.
AR9. Drag G's header past top-level rows: the whole group moves. Drag the pinned workspace below an
     unpinned row: it stays in the pinned block. Drag an unpinned row onto it: no group forms.
AR10. Start a drag and press Esc: the drag cancels and the mode stays on. Press Esc again: the mode
      ends and the grips go.
AR11. Choose "Arrange workspaces" again, then click Done: the mode ends. Again, then
      "Done arranging" on a menu: it ends. `$ts arrange on` and `$ts arrange off` do the same, both
      exit 0; after `arrange on`, Esc ends it only once the bar has focus.
AR12. With the mode on, click Start, search, a pinned app, the tray chevron, the clock and the bar's
      back button: each does what it does with the mode off. A tab on a screen strip is at full
      strength, activates its window on a click and drags its window to another row. Click the bar's
      minimize button: the bar minimizes. Restore it: the mode is still on.
AR13. Click Done, then minimize the bar. Right-click a strip: no "Arrange workspaces" item.
      `$ts arrange on` fails with "The bar is minimized." and nothing changes.

## Reporting a problem

"Report a problem…" (issue #331). Reports land in the private repo AppifySheets/Deskbar-reports, so
close every test issue there afterwards. Start each with "test" in the text.

RP1. Right-click the tray: "Report a problem…" sits where "Save debug dump" was, above Restart and
     Exit. The bar's background menu has the same item. Each opens the window; a second click brings
     the open window forward instead of opening another.
RP2. Send is greyed until "What happened?" has text other than spaces.
RP3. Win+Shift+S a region, then Ctrl+V with focus in the text box: a thumbnail appears and the text
     box gets no text. Copy plain text and Ctrl+V: it pastes as text. Copy two PNGs in Explorer and
     Ctrl+V: two thumbnails. Drop a JPEG from Explorer: a thumbnail. Drop a .txt: a note says only
     pictures can be added. A sixth picture is refused with a note; ✕ on a thumbnail removes it.
RP4. "View what it contains" opens the dump in Notepad, written now, with this window in its list.
RP5. Type a contact, send. Progress counts each screenshot, the dump, then the report. "Sent, thanks"
     shows a reference number, and that issue in Deskbar-reports has the screenshots, the dump link
     and the from-app label (and priority on a copy that owns Deskbar Pro).
RP6. Disconnect the network, send: "Could not send." with a folder under %APPDATA%\Deskbar\reports
     holding report.txt and every file; Open folder opens it. Reconnect, Try again: sent, and the
     folder is gone.
RP7. `$ts dump` still writes a dump file and answers its path, opening no window.
RP8. Leave the contact empty and press Send: nothing goes, a note under the contact line says we
     can't ask follow-up questions, the cursor is in the line and the button reads "Send anyway".
     Press it: the report goes. Open a new window, type `someone@example` and Send: a note says it
     doesn't look like an email; add `.com` and the note goes and the button reads Send again.

## Recent workspaces keep their colour

Each workspace row fades with the time since any of its windows was used: full colour up to 15 minutes,
grey and faint at 8 hours (docs/superpowers/specs/2026-11-22-recent-workspaces-design.md). Easiest to
check after a night: everything you did not touch yesterday should be faded in the morning.

RW1. A workspace untouched for hours: grey, faint rail; no tint; dimmed name; every icon grey.
RW2. A workspace you used a few minutes ago (not the one you are on): rail in its colour, a faint wash
     of that colour behind the row, a normal name, coloured icons.
RW3. Click a window in a faded row: the row is in full colour once you have switched there, and stays
     coloured after you switch away.
RW4. The row you are on is drawn as before (bright rail, tint, ring, coloured name), however long its
     windows have been left.
RW5. A group whose members are all faded: its name and the line down its left are faded too. Use one
     member and both come back.
RW6. A faded row asking for you (a session needs you) still pulses orange.
RW7. Leave the bar alone for an hour with a workspace near 15 minutes old: its row fades in visible
     steps without any click being lost while you use the bar.

## Results (2026-08-01)

Status: **pending human execution.** An agent cannot observe the desktop, Task View,
tray menu rendering, or perform interactive window renaming — none of the 13 checks
above can be validated without a human at the keyboard, so none are marked pass/fail
here to avoid fabricating results.

What an automated smoke run *could* verify stands in as a partial, non-visual proxy for
step 1's non-crash half and none of the others:

- `dotnet build src/TaskSpaces.App` succeeds (Debug, x64).
- `dotnet run --project src/TaskSpaces.App` launches, stays alive and responding for
  15+ seconds with no unhandled-exception output on the console.
- `%APPDATA%\TaskSpaces\state.json` is created on first run with the empty `AppState`
  shape (`Workspaces: []`, `WorkspaceRules: []`, `RenameRules: []`, `Inventory: {}`),
  confirming `VirtualDesktopService.Initialize()` succeeded (not compatibility mode on
  this machine/build) and `WorkspaceManager.Start()` completed its reconcile-and-persist
  path without throwing.
- The process was stopped cleanly (`Stop-Process`) with no crash dialog.

Task 9 (rehydration) smoke check, added in this session, same non-visual proxy, re-run
with a corrected observation method (fix round 1 — the first pass here mis-checked
`%APPDATA%\TaskSpaces\state.json` through a Bash-tool `powershell -Command "...$env:APPDATA..."`
invocation, where the surrounding POSIX shell pre-expands `$env` to empty before
`powershell` ever sees the string, silently mangling the path to `:APPDATA\...` and making
every check against it report "absent" regardless of the real file. Re-checked properly
via the PowerShell tool directly, with an explicit `$p = Join-Path $env:APPDATA ...`):
on this build/machine, both at Task 8 and at HEAD (Task 9), the app starts normally —
NOT in compatibility mode — and `%APPDATA%\TaskSpaces\state.json`'s `LastWriteTime` bumps
on every run (confirmed: `19:56:15` before this round's run, `20:00:30` after, with
content `{"Workspaces":[],"WorkspaceRules":[],"RenameRules":[],"Inventory":{}}`), i.e.
`VirtualDesktopService.Initialize()` succeeds and `WorkspaceManager.Start()` completes its
reconcile-and-persist path normally. `RehydratePrompt.HasAnythingToRestore` is guarded by
`!compatibilityMode` and by an empty `Inventory`; with the inventory empty here, no prompt
should appear, and none did: the process's `MainWindowTitle` stayed empty (a shown
`RehydratePrompt` would carry the title "TaskSpaces — Restore workspaces?") throughout the
15+ second observation window, and there was no crash. This does NOT exercise the
Restore/Skip button paths or an actual relaunch — that needs the human steps in item 14
above.

| # | Check | Result |
|---|-------|--------|
| 1 | Tray icon + menu contents | pending human execution |
| 2 | Add workspace creates desktop | pending human execution |
| 3 | ProcessName rule auto-places Notepad | pending human execution |
| 4 | Tray switch shows only that workspace's windows | pending human execution |
| 5 | Manual "Send to workspace" | pending human execution |
| 6 | Rename + re-apply after title change | pending human execution |
| 7 | Restore title | pending human execution |
| 8 | TitleRegex rename rule (mstsc) | pending human execution |
| 9 | Rename/remove workspace, desktop merge | pending human execution |
| 10 | Exit restores titles, desktops persist | pending human execution |
| 11 | Restart re-binds without duplicating desktops | pending human execution |
| 12 | Invalid regex rejected with message box | pending human execution |
| 13 | Start-with-Windows registry toggle | pending human execution |
| 14 | Rehydration prompt lists app counts; Restore relaunches into the right workspace | pending human execution (non-crash proxy verified: no prompt window shown when inventory is empty) |
| 15 | Superseded by Task 9 (see items 25-26): re-verify panel appearance/grouping via item 25's hover instead of left-click | n/a — superseded, see item 25 |
| 16 | Panel: click a window row -> jumps workspace + focuses, panel closes | pending human execution |
| 17 | Panel: pin/unpin via context menu; pinned group; send-to unpins | pending human execution |
| 18 | Panel: non-workspace desktop windows grouped under desktop name | pending human execution |
| 19 | Panel: renamed-window "was: ..." display; icons on every row | pending human execution |
| 20 | Roster: closed app stays dimmed "(not running)"; click relaunches; ▶ starts all | pending human execution |
| 21 | Add app… roster entry; Remove from workspace | pending human execution |
| 22 | Rename persistence across restart (sweep adopts persisted rename) | pending human execution |
| 23 | Rename sweep reasserts short name after title drift (~5s) | pending human execution |
| 24 | Restart with apps still open -> no duplicate restore offers | pending human execution |
| 25 | Hover peeks panel without stealing focus; moving away hides it; click-inside graduates it to normal behavior | pending human execution |
| 26 | Left-click tray icon opens the menu (not the panel) | pending human execution |
| 27 | Ctrl+Alt+Right/Left cycles workspaces in defined order, wrapping, skipping non-workspace desktops | pending human execution |
| 28 | Ctrl+Alt+2 (etc.) jumps directly to that workspace by defined order | pending human execution |
| 29 | A chord already owned by another app produces exactly one startup warning; other hotkeys keep working | pending human execution |
| 30 | Drag a window row onto another workspace's group -> moves (unpins if pinned); onto 📌 Pinned -> pins | pending human execution |
| 31 | Windows tab shows the same grouped view; drag works identically; right-click menu covers rename/restore/pin/send | pending human execution |
| 32 | Windows on unbound/default desktops group under a sensibly-named group (e.g. "Desktop 1"), including the current desktop; oddball shell windows appear under "Unplaced" | pending human execution |
| 33 | ＋ button is gone; Add app… lives in the workspace header's right-click menu | pending human execution |
| 34 | Floating bar shows icon rows per workspace, translucent, always on top; click icon -> lands on that window | pending human execution |
| 35 | Drag bar -> position survives app restart; tray toggle hides/shows it, state survives restart | pending human execution |
| 36 | Windows opening/closing update the bar live | pending human execution |

Re-run this script by hand before each release and replace the table above with actual
pass/fail results plus notes on the Windows build tested.
