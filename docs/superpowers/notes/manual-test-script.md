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
    list jumps. Click in the box, type "probe", press Enter: the note appears as a line with no tick box, its
    title at the left edge and the workspace's chip at the right, the add chip stays where it is, and the keyboard
    is back in the window you were typing in. Right-click the line and pick Close: the line stays for six seconds,
    struck through, with "Undo" at its start, then leaves.
43. `$ts task add "a"`, "b", "c", "d" and "e" (five tasks). The list shows three lines and a small "+2 more"
    at the right end of the chip row under the box (no line of its own at the bottom). Click it: all five show and "Less" is at the end. Click "Less": back to three.
44. With the five tasks of step 43 open, `$ts task status <id> waiting` on all five. They sort to the top, three
    show and the two hidden ones wait on you: the line under them reads "+2 waiting" in orange. Set two of them
    back to `backlog`: it reads "+2 more" in the plain ink again.
45. Click in the box, type some text, pick "none", then click into another app and switch desktops. The text
    and the "none" choice are still there when you come back; click in the box and carry on. Enter and Esc both
    give the keyboard back to the window you were typing in (type a few letters after each to check). With the
    box empty and idle, click a chip: it starts a note, as a click in the box does.
46. Click in the box, then the "other..." chip, and pick a workspace: the chip takes its name and is ticked,
    the text stays, and Enter files the note there.
46a. Enter asks for the workspace (task 6184e8bf). Click in the box, type "probe", press Enter: nothing is added
    yet; the workspace menu opens under the chip with the white keyboard edge on the workspace you are on. Enter
    again: the note lands there. Type "probe 2", Enter, then type "sp" (or the first letters of another
    workspace): the keyboard edge jumps to it; Enter files the note there. Type "probe 3", Enter, then Esc: the
    menu closes, nothing is added, "probe 3" is still in the box with the caret in it. Hold Enter down for a
    second on "probe 4": only the menu opens, nothing is picked by the repeats. Ctrl+Enter on "probe 5": the
    menu opens, Enter picks, and Claude starts on it. In the tasks panel's + Add box, Enter, Enter and Esc do
    the same.
46b. The arrows cross groups (task f94524ce). Open any workspace menu (a line's workspace chip will do) with the
    keyboard on a workspace inside a group, say iam.ge. Up: the white edge goes to the line above, into the group
    above, past its caption, and on up to the top block with more presses. Down from the top block's last line:
    it lands in the first group. Right on a group's last chip: the next group's first. Up on the top line wraps to
    the bottom, Down on the bottom line to the top. Enter picks wherever the edge is.
47. Click a line of a note on another workspace: the tasks panel opens (or comes forward, and takes the foreground) on that task, selected,
    scrolled into view and expanded to its comments, and you stay where you are. Double-click the line: you go to
    its workspace (only when both presses land on the same line: click one line and double-click the line beside
    it, and nothing goes). Double-click a note on "none": nothing happens. In the panel, a click on a line selects and
    expands it, a double-click goes, and F2 (or Edit in the menu) edits the title; a double-click on a title no longer edits.
48. Untick "Task list" in Manage's Settings tab (strip items): the list disappears from the bar. Tick it
    again: it is back, with the same tasks.
49. Quit Deskbar, put a version 1 `tasks.json` (with timers) in the state folder and start it. The tasks
    come back as notes with no timers. There is no `tasks.json.v1` yet: it appears with the first change, so
    add or tick a note, and `tasks.json.v1` then sits beside the new file with the old file's bytes.
50. Change the icon size on Manage's Settings tab: the list's text keeps its proportion, the bar does not
    widen for a long title (it trims with an ellipsis), and `$ts task list` agrees with what is shown.
51. Add twelve notes and click "+9": eight lines show and the wheel over the list scrolls to the rest, while the
    workspace rows above stay where they were. Click "Less": three lines again.
52. Close the first note from its line's menu: only that note closes; its line stays struck through with
    "Undo" at its start. Click "Undo" within six seconds: the note is back as it was. Close two notes in a row:
    both lines stay in place, each with its own Undo, and leave after their six seconds. A done Claude task's
    Close does the same, and Undo brings it back done.
52a. (Task acd76fbe.) Give a note a title long enough to wrap: its first row stops before the workspace chip and
    its second row runs on under the chip to the line's right edge, ending in an ellipsis when even that is not
    enough. A short title stays one row. In the tasks panel, tick "Current workspace" while on another workspace,
    then click the note's line on the bar: the panel lets it through and shows it selected and expanded. After the six seconds, `$ts task list --closed` lists the closed note first and
    `$ts task reopen <id>` brings it back.
53. Make the bar as narrow as it goes (drag its edge in). Nothing in the list is cut off at the right: the box,
    "+N more", the lines' chips, and the add row (the chip, the two toggles and the play button), which wraps onto a second
    line, a long workspace name trimmed with an ellipsis.
54. Click the add chip ("Deskbar ▾", no "on" before it): a menu opens with "none" first, then each workspace as its chip in its
    colour, in the bar's order, grouped workspaces under their group's name (which cannot be clicked), the chosen
    one ticked. Pick "none": the chip reads "none" and the box reads "Add…". Open it again and pick a workspace:
    the chip is that workspace's, in its colour, and the box reads "Add to <that workspace>…". There is no separate
    "none" or "other…" chip. With twenty or so workspaces the menu scrolls rather than running off the screen.
55. Click a line's workspace chip: a menu of "none" and every workspace in its colour opens, the task's own ticked,
    and the click does not switch workspaces. Pick another: the line's chip changes, and `$ts task list` agrees. Add
    a note with a title longer than the bar on a narrow bar: the title ends in an ellipsis and the chip is whole.
55a. Dock the bar to a screen edge with twenty or so open tasks: the list fills the empty height between the last
    workspace row and the footer, at least three lines, "+N more" counting the rest. Scroll the list with the wheel:
    the lines past the last one come up, the bar keeps its height and the workspace rows stay put, and "+N more"
    counts the lines out of sight above and below as you go. Add workspaces or shorten the
    screen: the list gives up lines, never below three. Float the bar again: three lines, and the bar lands where
    it floated before, not clamped against the docked height. With notes above Claude lines (orange edge, no icon,
    the title starting at the same x as a note's), at the smallest text size, the docked list settles on one count and the bar stays responsive.
55b. Click a line: it stays highlighted (a lighter background and a blue edge on its left). Open the tasks panel:
    the same task is highlighted there and scrolled into view. Click another line in the panel: the bar's
    highlight follows. Close the highlighted task: no line is highlighted any more.

55b2. A bar line shows no age and no flag sign: the title, then the chip. Point at the line: its tooltip has
    "Created" and the date. Open the tasks panel at its usual slim width: every line shows its age ("now", "16m",
    "2h3m", "3d3h") just before the chip, whole; after a minute it moves on without the line redrawing. Point at an
    age: "Added 2 hours 3 minutes ago" and the date.

55c. Point at a note's line on the bar: nothing appears over its title. Right-click it and pick Important: its title
    turns bold and the line moves above the plain ones; right-click again and Important is ticked. Pick Urgent on
    another line: italic, and that line moves above the plain ones but below the important one. Set both on a third:
    bold italic, at the very top of the open lines. A task set to waiting stays above all of them, its title still
    bold or italic. A little space separates each group, and nothing names the groups on the bar.
55d. Drag a plain line down past another and let go on that line's lower half: it lands under it. A press and
    release without moving still goes to the task. Drag a plain line onto an important one: it becomes important
    (its title turns bold). With two waiting lines, drag the lower one above the other: they swap, and
    stay swapped after a restart. Drag a waiting line down among the plain lines: it goes to the foot of the waiting
    block. Drop a plain line on a waiting line's top edge: it lands under the waiting block. While dragging, add a task from the pipe: the list does not change until you
    let go. `$ts task list` shows `important`, `urgent`, `quadrant` and `order` matching what you see, and
    `$ts task order <id> 2` and `$ts task flag <id> urgent on` move lines as a drag and a click do.

## The tasks panel

The tasks panel (spec `docs/superpowers/specs/2026-11-22-tasks-board-design.md`, "The window" and "Comments", and
`docs/superpowers/specs/2026-11-23-tasks-panel-design.md`, which replaced the floating window). Steps 56 to 64 were
written for the floating window and now describe the panel; 64a to 64n cover what is new.

56. With three or fewer open tasks the button at the right end of the chip row reads "All tasks" ("All" on a narrow bar); with more, "+N more" ("+N"). Click it: a
    full-height panel opens just inside the bar (not over it), on the list and not on a form
    (no box has the keyboard). It has no title bar, is not in Alt+Tab or on the taskbar, and the bar does not list it.
57. Groups read WAITING ON YOU, then the quadrants that have tasks (I to IV), a new task on top. The chips count each
    status. Click "Closed": only closed tasks show, the latest closed first, each with "Reopen"; click "Reopen" on
    one and it goes back to the open view. "Workspace: all" lists every workspace as its coloured chip, grouped;
    pick one, then "none", then "All workspaces".
    57a. Tick the "Current workspace" checkbox beside "Workspace: all": the box shows a blue tick, the label names the
    workspace in front and only its tasks show. Switch to another named desktop: the list and the label follow it with
    no new click. Switch to a desktop with no name: only tasks on no workspace show. Close the panel and open it, then
    quit and start Deskbar: the box is still ticked. Untick it: every workspace shows. Tick it again, then pick a
    workspace in the "Workspace" menu: the box unticks.
58. Click into another app: the panel stays open. Close it with the x in its toolbar, then click "+N more": it
    comes back with the width you left it at. Quit and start Deskbar again: the width is kept (see 64e for the
    open state). `TasksWindow` in state.json is ignored.
59. Leave the panel open and switch to another desktop: it is there too, not moved. `$ts tasks-window hide` closes it,
    `$ts tasks-window show` opens it.
60. Click a task's comment count: its thread opens. Post a comment through the pipe with bold, italics, inline code,
    a link to https://deskbar.app and a two-item list: it shows formatted, by "you". Type a comment and press Enter:
    it appears under "you", the box empties and keeps the keyboard. Shift+Enter makes a new line instead. While you
    type, add a task from the pipe: what you typed stays. Click the link: it opens in the default browser. A `file:`
    link in a comment shows as plain text.
60a. Copy ten lines of text from Notepad and paste them into a comment box ([Deskbar task 0596fca2](https://deskbar.app/task#0596fca2-cf31-4cab-b972-8186f87802ca)):
    every line arrives, not only the first, and the box grows to about seven lines, then scrolls inside. Enter
    posts all ten.
61. "…" on a line: set the status to each of the four, Edit (opens the title in place: Enter keeps it, Esc drops it), Move to (the
    coloured workspace menu), Close (the line stays struck through with Undo for six seconds; Undo brings back
    the status it had, a waiting task its question too, and the selection) and Delete, then Delete for good. While that menu is open, change a task from the pipe:
    nothing moves until the menu closes.
61a. Click a line to open its thread, then close the task with its tick: the thread folds at once and the struck line
    stands alone with Undo. Undo: the task comes back with its thread open again. Close a task whose thread is folded
    and Undo it: the thread stays folded.
62. Click a note's line on a workspace: you go there and the panel stays open. "+ Add": a box
    opens, "<workspace> ▾" says where; Enter adds the note there, Esc closes the box.
63. With flagged tasks, the panel's List shows Waiting on you, then the quadrants captioned "I Urgent and
    important" to "IV Not urgent, not important". The toggles and the drag work as on the bar, and the bar
    follows every change. Pick the "In progress" chip: the quadrants stay, with only tasks in progress.
64. Click "Matrix": a 2 by 2 grid, URGENT and NOT URGENT across the top, IMPORTANT and NOT IMPORTANT down the side,
    I top left, II top right (drawn a little brighter), III bottom left, IV bottom right. A waiting task sits at the
    top of its own cell in orange. Drag a line into another cell, an empty one included: it takes that cell's flags.
    Add twenty tasks to one cell: that cell scrolls, the others do not move. Close and reopen the panel: still the
    Matrix. `$ts tasks-window list` switches it back. The switch needs a panel 780 DIP wide (see 64c).

64a. Open and close. Click "+N more" (or "All tasks"): the panel opens full height just inside the bar, and the
    bar stays the outer strip. Click the same button: it closes. Open it again and click the x in the toolbar: it
    closes. `$ts tasks-window show` and `$ts tasks-window hide` do the same. Maximize an app while it is open: the
    app fills the space beside the bar and the panel, not under either.
64b. Width. Drag the panel's inner edge (the one away from the bar). It starts at 460 DIP, stops at 360 on the narrow
    side and at half the screen on the wide side. Let go and reopen it: the width is kept.
64c. Layout by width. Drag the width across 692 DIP: below it the lines are slim, with the status ("Done · 5h ·
    Claude"), age and assignee in the tooltip, a done task's green tick still on the line, and Close and Reopen in the
    "..." menu; at 692 and above they are the full lines, each with its status chip. Drag across 780 DIP:
    the List and Matrix switch is hidden below it and shows from it. Check the lines follow while you drag, not
    only after you let go.
64d. Workspaces. With the panel open, switch to each other virtual desktop: the panel is on every one, at the same
    place and width, and it is not in Alt+Tab or on the taskbar. Tile a workspace: the panel is not tiled.
64e. Restart Deskbar with the panel open (exit it from the tray, start it again): the panel comes back open at its
    width. Close the panel and restart: it stays closed.
64f. Restart Explorer: `taskkill /f /im explorer.exe`, then `start explorer`. After the taskbar is back, check the
    bar is still the outer strip and the panel sits inside it, not between the bar and the screen edge, and that
    maximized windows still leave room for both.
64g. Minimize the bar (its minimize control): the panel goes away with it and the work area is the screen again.
    Restore the bar: the panel comes back, still inside the bar.
64h. Full-screen. Play a video full screen in a browser: neither the bar nor the panel is drawn over it. Leave full
    screen: the panel is back on top, and nothing was left behind.
64i. Search. Press Ctrl+F in the panel: the search box has the keyboard. Type two words from different parts of a
    task (its title and its status line): the task stays; a word that matches nothing hides it. Esc clears the
    box. Type again: a × shows at the box's right end (none while it is empty); click it: the box is empty, every
    task is back, the × is gone and the keyboard is still in the box. Close and reopen the panel: the search is empty.
64q. Search by pull request (task e7aa49b1). Have a Claude session on a task open a pull request with `gh pr create`.
    Open the task in its window: "Pull requests" lists it, and clicking it opens it in the browser. Close the task,
    then type the pull request's number in the panel's search box, then `#` and the number, then its link: each
    time the closed task shows, and only it. A task whose thread links a pull request is found by that number too.
64j. Claude working. With one task that has a live Claude session, one with an ended session and one note, click
    the switch's "Claude N" part (the "Claude working N" chip before task 10b49554): only the live one stays, N is 1,
    and its session square glows while Claude is working. Close and reopen the panel, then restart Deskbar: the part
    is still on (task 93fa0b9b). Click it again: all tasks are back, and a reopen keeps it off. The working and idle
    parts are steps WI1 to WI7.
64k. Floating bar. Undock the bar so it floats, near the left edge of a screen: the panel opens on that edge, by the
    bar. Move the bar to the right half: the panel stays on the left edge (a floating bar's moves are not followed,
    by decision). Dock the bar again: the panel docks inside it.
64l. Tray overflow. Dock the bar on the screen whose taskbar has the tray, at the edge where the tray's ^ is, and
    open the panel. Click the bar's tray chevron (or the taskbar's ^): the hidden-icons flyout opens over the bar and
    the panel, stays open for at least five seconds without flickering, and its icons can be clicked, including the
    ones over the panel. Close it (Esc or a click elsewhere): within a second the panel is back on top.
64m. Scale change. With the panel open beside the docked bar, open Settings, Display and change the bar's screen
    from 150% to 125% (or any other step). Within a couple of seconds the bar and the panel are re-docked at the new
    scale: the panel still sits just inside the bar with no gap and no overlap, it is the same width in DIPs as before
    (its lines slim or full as before), and a maximized window fills the space beside both. Change the scale back:
    the same.
64n. Full-screen already up. Play a video full screen on the bar's screen, and from a terminal on another screen run
    `$ts bar minimize`, then `$ts bar restore`, while the video still plays: the panel comes back behind the video,
    never over it, not even for a second. Leave full screen: within a second the panel is on top again. Do the same
    with the panel closed, opening it with `$ts tasks-window show` while the video plays.
64o. Windows under the panel. Close the panel. Restore (un-maximize) a terminal and drag its right edge out to the
    bar, so it spans most of the screen; put a small Notepad half under where the panel will be. Open the panel: the
    terminal now ends at the panel's edge, its left edge where it was, and Notepad moved beside the panel, same size.
    Neither came to the front. Drag Notepad back under the panel: it stays there (the panel clears its strip once,
    when it takes it). Widen the panel with its grip: Notepad is moved again, by the new width. In a tiled workspace
    the tiles are left to the tiler. With tracing on, each move is a "tasks panel: moved ... off the strip" line.
64p. Hover text over the bar. With the panel open beside the docked bar, rest the pointer on a task line in the panel
    near the bar, long enough for its hover text to open and reach onto the bar. Keep it there for a few seconds: the
    whole text stays readable on top of the bar, and does not slip under it after a second. Do the same on one of
    the bar's own buttons whose hover text reaches onto a screen strip or the panel. Then click the taskbar twice:
    the bar still comes back over it within a second.
64r. The Inbox (task 740a2231). With a question, a done task, a Claude task working and one idle, and notes in two
    quadrants: click "Inbox" in the toolbar. The panel turns blue-black, the header shows three buttons with counts
    ("Needs you", "Claude", "Notes") and the view switch is at the bottom. Each tab lists its lines under captions; the
    three counts add up to the List's lines. Click a line: its page opens over the list, scrolled to its end; Esc goes
    back to the same scroll place. Point at a line: its buttons show; the ⋯ and a right-click open the List's menu.
    Click a note's box: it closes with Undo. Click "Filter": the List's chips fold out under the header; pick "Done" and
    the button reads "Filter · 1". Type in the search (Ctrl+F): one list of matches, the buttons dimmed. Click a task on
    the bar: the panel switches to its tab and opens its page. Bring a Claude task's terminal to the front: the tab and
    the page stay. Quit and start Deskbar: the panel comes back on the Inbox. `$ts tasks-window list` goes back to the
    List, `$ts tasks-window inbox` to the Inbox.

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
    a live session square in the tasks panel, and the status line under the title changes as it works.
68. Ask the session to ask you something through its task. Switch to another workspace first: the task jumps to the
    top as Waiting on you with the question, in orange, and the session's workspace blinks. Reply in the session:
    your message alone changes nothing (since #435 a prompt no longer answers). When the session runs `task answered`
    the task goes back to in progress and the question goes, and the line leaves the bar's list (the tasks panel
    still has it, in progress). Before that, switch to its workspace and to the session's window, and click and
    double-click the line: it stays on the bar. Ask again: it is back at the top of the bar. Tick an in-progress Claude
    task in the tasks panel, then Undo: it comes back to the panel, not to the bar.
68a. Ask the session to put a multiple-choice question to you in Claude Code's own dialog (the AskUserQuestion tool,
    task 493b7ba9). Before you answer: the task is at the top as Waiting on you, its question starts "Answer in the
    session's terminal:" and names the question and its options, and the workspace blinks. Pick an answer in the
    terminal: the task goes back to in progress and the question goes. Do it again and press Esc instead: record
    whether the question stays until the session runs `task answered` (Claude Code's docs do not say whether a
    dismissed dialog runs the hook).
69. Ask the session to finish and mark its task done: the line stays on the bar until you close it. Send the session another
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
76. The assignee (Ruling 57). Add a note: the tasks panel's line says **Me** in its assignee column, and it is on the
    bar. In the panel's menu (the "..." button, and a right-click on the line) choose **Assign to**, **Claude**: the
    column says **Claude** and the note leaves the bar ("+N more" shrinks with it). There is no assign control on the bar.
    Start a Claude session on that workspace: its briefing offers the note. **Assign to**, **Me** brings it back to the
    bar. The "Assigned" button in the panel's toolbar offers Me, Claude and All and narrows the list, the Matrix and the
    chip counts; it starts at all each time the panel opens. Ask a session something: its task says **Me** and is on
    the bar; `task answered` makes it **Claude** and takes it off; `task done` makes it **Me** again. `$ts task assign
    <id> claude` does the same as the menu, and `$ts task list` shows `assignee`. Quit and start Deskbar: every
    assignment is as it was; an old tasks.json with no assignees gives a Claude task in progress to Claude and the rest to you.
77. Description and comments (Ruling 58). Select a note's line and press F2 (or choose Edit in its menu, or right-click
    the line): two boxes open, the title and a description under it. Type markdown such as `the **landlord** wants it`
    in the description, press Tab and back, and press Enter: the line shows the title with the description's first
    line under it, plain, dim and cut to one line, and the bar's line shows that first line after the title. Click the
    line to open it: the whole description shows under the title, rendered (a `## heading` line in semibold, without
    its hashes); click again to close it and the one line is back. Edit again, empty the description:
    it goes. Press Esc in the editor: nothing changes. Do the same on a Claude task: its status line stays Claude's
    (change it with `task status-line` and your description is untouched). Open a thread with one of your comments and
    one of Claude's: only yours has Edit beside its time. Edit it, Enter saves and Esc keeps it. From a script,
    `$ts task describe <id> -` with the text on stdin sets the description, and `$ts task comment-edit <comment-id> -`
    rewrites your comment (the ids are in `task list`'s `thread`) and is refused for a Claude comment.
78. Flags in the title (Ruling 59). With four tasks, one plain, one important, one urgent, one both: on the bar and in
    the tasks panel's List and Matrix the important title is bold, the urgent one italic, the last bold italic and the
    first plain. Flip a toggle: the title changes with it. At the bar's narrowest width the bold titles still trim
    with an ellipsis and push nothing off the line.
79. Settled comments (Ruling 62). Have a session ask a question with a comment of detail and a follow-up, then answer it (the
    session runs `task answered`): open the task's thread in the tasks panel. Those comments are gone behind a dim "2 settled
    comments" line, and your own comment and any written before the ask are still there. Click the line: they unfold, dim,
    and it reads "Hide 2 settled comments". "Settle" beside any comment folds it away; "Unsettle" brings it back. The
    comments are still in `task list`'s `thread` with their `settled` time, and survive a restart.
80. Folding a thread (Ruling 63). Click a task's line: its thread opens. Click it again: it closes. Open it and press Esc: it closes.
    On a task with nine long comments the thread shows the latest three, each cut to six lines with "Show more"; "3
    earlier comments" unfolds the rest, and "Show less" folds a long one back. Add a comment from a script or let a session add
    one: what you unfolded stays unfolded. Close the panel with its x and open it again: all threads are folded again.
81. Compact lines (Ruling 64). In the tasks panel's List and Matrix, a task with no second line is one text line tall plus
    a few DIP, and a task with a status line or description is two small lines, the dim one right under the title with no
    gap. Lines sit about as close as on the bar, a group's header has little space above and below, and the tick box and
    the orange edge start close to the panel's left edge. Hover and select a line: the background still covers the whole
    line. The tick box, the two toggles, the comments button and "..." still click first time.

## Claude sessions kept running

The `claude agents --json` poll, adoption and the automatic resume (spec
`docs/superpowers/specs/2026-11-23-tasks-open-claude-sessions-design.md`, "Which sessions are live" to "The setting";
`ClaudeSessions` in the app). Needs Claude Code with background sessions (`claude --bg`), and both Claude boxes in
Manage ticked. Use a throwaway folder Claude Code already trusts, not a real repo. Turn tracing on
(`%APPDATA%\Deskbar\trace.on`) to see the `claude sessions:` lines in `%TEMP%\deskbar-trace.log`.

82. In Manage's Settings tab, "Resume the sessions of open tasks after a restart or a crash" sits under the tasks box,
    with the 30 days / `cleanupPeriodDays` line under it. Untick the tasks box: the resume box greys out and unticks.
    Tick it again: back, ticked. `$ts claude-resume off` unticks it, `$ts claude-resume on` ticks it.
83. In a terminal in the throwaway folder, start `claude --bg "say hello"` without telling it about any task. For the
    first two minutes no task appears; then, within 15 seconds more, a task named after the session appears, In
    progress with a live session; with a VS Code window open on that folder, the task is on that window's workspace.
    Run `claude -p "say hi"` in the same folder while it works: `claude agents` lists it as interactive, yet it never
    gets a task (its hooks are headless, Ruling 17).
    Ask that session to join another task (`deskbar task join <id>`): the task Deskbar made for it disappears. Repeat
    with the made task renamed (or commented, flagged, moved, assigned to you, described) first: it stays, showing the
    session ended.
84. Note the task's session, then end it: `claude stop <short id>` (from `claude agents`). Nothing happens for two
    minutes (Ruling 18: VS Code might be bringing it back by itself); within 15 seconds after that the task's
    thread says "Resumed by Deskbar at HH:mm." and `claude agents` lists the session again with the SAME session id
    (not a copy). Close the task and stop the session again: nothing is resumed.
85. Make five tasks with sessions as in 83, then stop all five at once. They come back one at a time, about five
    seconds apart (the thread times, or `claude agents` refreshed while it happens), and none of them twice. The bar
    stays usable throughout.
86. Stop a session in the middle of a long reply (kill its `claude.exe` from Task Manager while it is working). Its
    resume says "its last turn was cut off, so it was told to continue", and the session carries on by itself.
86a. A session stopped on the usage limit (task daeaa26c). Hard to stage on purpose: wait for a background task session
    to hit a limit. Its thread gets "Stopped on the usage limit. Deskbar resumes it once the limit resets at HH:mm."
    once, and its status line on the bar reads "Stopped on the usage limit until HH:mm". Comment on the task before
    the reset: the session is stopped and resumed with the comment at once (`claude agents` shows the same id, not a
    copy); it hits the limit again, and the thread gets no second "Stopped on the usage limit" line. A few minutes after the reset, with no window
    open on it: the thread says "Resumed, since the usage limit it stopped on has reset", and the session carries on
    and writes its own status line.
86b. A listed session without a process (task daeaa26c). In `claude agents --json`, find a task session listed with no
    `pid` and no `status` (a background session whose process Claude Code ended). Comment on its task: within a few
    seconds the thread says "Resumed." and the session answers the comment. The trace shows "resumed with the
    comment", never "a comment is in ...'s inbox".
86c. Step 86a with its Deskbar window open (task 2eeb5589). Before the reset the thread says "... resets at HH:mm, and
    opens its window again." A few minutes after the reset, with the window open behind another window: the window
    closes, the thread says "Resumed, since the usage limit it stopped on has reset", and within half a minute the
    window opens again on its workspace, on the same session, carrying on. With the window in front while you work,
    nothing happens until you click into another window or leave the machine alone for two minutes.
87. Untick the resume box and stop a task's session: the task shows the session ended and nothing resumes it; start a
    new `claude --bg` in another folder and it still gets a task. Tick the box again: the ended session is resumed at
    the next poll (once it has been gone two minutes).
88. Make a task whose session's folder Claude Code no longer trusts (however your Claude Code version lets you take
    the trust back; see the probes note's "An untrusted folder"), and stop the session. The thread gets Claude's own
    "Workspace not trusted" message once, the task waits on you with "Deskbar could not resume this session. Claude
    Code's message is in this task's thread.", and the trace shows no second resume of it, however long you wait.
    Trust the folder and set the task back to In progress: the next poll resumes it.
89. Rename `claude.exe` away (or take `%USERPROFILE%\.local\bin` off Deskbar's PATH) and restart Deskbar: the tasks
    keep showing what they showed, nothing is ended or resumed, and the trace says ONCE that the list could not be
    read, not every 15 seconds. Manage's Settings tab shows the reason in one red line under the resume box (Ruling
    20). Put it back: the trace says it is read again, the line on Manage goes within 15 seconds, and the next poll
    picks up as before.
89a. Stalled tasks (spec `docs/superpowers/specs/2026-11-23-stalled-claude-tasks-design.md`). Give a VS Code session a
    task, let it set a status line and stop at the prompt without `task ask`. The task is off the bar (Claude's). Leave
    it 15 minutes: within 15 seconds after that the trace says `tasks: asked the session of <id> "<title>"` and the
    session takes a turn on a prompt opening "Deskbar: no session has worked on your task". Interrupt that turn (Esc)
    before it runs any `task` command. Five minutes after the ask the task is on the bar with its status line, assigned
    to you in the tasks panel, and the trace says `tasks: handed back <id> "<title>", its session did not answer`. Send
    the session a prompt: once it has been busy for a poll, the task leaves the bar and the trace says `taken back`.
    Repeat, but assign the task to yourself in the tasks panel before prompting: it stays on the bar while the session
    works.
89b. Stalled task, answered (task 8710ac09). As 89a, but let the session answer the ask. If it was waiting on you, the
    task is under Waiting on you with its question. If it says it waits on something else (start a long `gh run watch`
    in it first), its status line names that, the task stays off the bar, and 15 quiet minutes later it is asked again,
    never handed back while it keeps answering. A task whose sessions have no transcript left, so nothing can be
    resumed: the task comes to you right at the ask, and its thread says no session could be reached.
89c. Quiet Stop nudge (task 8710ac09). In a VS Code or terminal session with a task, let a turn end without
    `task ask` or `task done`. Under the reply there is no yellow "Stop hook reported a problem": one line reads
    "● Deskbar", then the session's short answer, and the session then stops (no second "Deskbar" line after it).
    Reply to a question of the session's, let it work, and let that turn end: the set-aside nudge arrives the same
    way. In a session resumed a moment ago (its waiter retired) the first nudge may still show the yellow block.

## Going to a Claude session, and starting one

Task 9 of the same spec ("Where the clicks are", "Choosing the folder", "The window on a session"; `ClaudeTaskActions`
in the app). Same setup as above: Claude Code with background sessions, Windows Terminal, both Claude boxes ticked, a
throwaway folder Claude Code trusts, and tracing on for the `claude actions:` and `claude sessions:` lines.

90. Right-click a note on a workspace whose tasks have sessions in the throwaway folder: the menu reads Edit, then
    "Start Claude on this". The tasks panel's "⋯" menu has it in the same place, and an open thread has a
    "Start Claude on this" button above the comments. A closed task has neither. A task that already has a session
    has "Open Claude" in that place on both menus and no "Start Claude on this" there (task b6f068b1: one Claude item
    per menu); its open thread has "Open Claude" in its row of buttons and no start button either (task 5e02e99f),
    until its session has expired.
91. Pick "Start Claude on this" on a note whose workspace knows exactly one folder: no question, no list. Within a few
    seconds a Windows Terminal window titled "<the note's title> · Claude" opens on the note's workspace, in the Deskbar
    Claude look, attached to a session that is already working on the note (its first message names the task). The
    note turns into a Claude task with that session. The window's icon on the bar is Claude's orange mark, not
    Windows Terminal's; a second tab opened in it (Ctrl+Shift+T) turns the icon back into the terminal's.
91a. The model (task e52e33cb). `$ts claude-model` answers `opus` on a profile that never set it. Start Claude on a
    note as in 91 and type `/status` (or `/model`) in its window: the model is Opus, whatever `"model"` in
    `~/.claude/settings.json` says. `$ts claude-model sonnet`, start Claude on another note: that session is on
    Sonnet, and the one from before is still on Opus, also after `claude stop <id>` and a click on its task (the
    resume keeps the model). `$ts task start <id> --model fable` starts one session on Fable and leaves the
    setting at sonnet. `$ts claude-model "--bare"` is refused and changes nothing. Set it back with
    `$ts claude-model opus`.
92. On a note whose workspace knows two folders (tasks with sessions in two repos, or a VS Code window on a second
    repo that some session also used): a short list of the folders opens at the pointer, ending with
    "Choose a folder…". "Choose a folder…" opens Windows' folder picker; cancelling it starts nothing.
93. Open the thread of a task whose session is still running (task 9b6275bc): the session's line reads working or
    idle, "Open Claude" is in the row of buttons at the top of the thread (task 1c17b7d6; the line itself has Open
    only when the task has a second session to open), and there is no "Start Claude on this" button. "Open in window" shows the same: no
    button in the Sessions column and no "Start Claude on this" on the "⋯" menu. Stop the session (`claude stop`):
    after the next poll the line reads ended. The thread still has "Open Claude" and no "Start Claude on this" (task
    5e02e99f: one Claude button, and Open Claude resumes the ended session); the task's own window has its start
    button back in the Sessions column and on its "⋯" menu.
94. Close the terminal window from 91 (its X). `claude agents` still lists the session, and the task still shows it.
    Double-click the task's line: you go to its workspace and a new terminal window attaches to the same session.
    Double-click again while it is open: that same window comes to the front, and no second window opens (a second
    attach would take the session over and close the first).
95. `claude stop <short id>` the session, then double-click the task: the thread says "Resumed by Deskbar at HH:mm.",
    and a few seconds later a terminal window opens on the resumed session (same session id in `claude agents`).
96. Open the task's thread: its sessions are listed newest first, each "Session <short id> · joined <time> · working,
    idle or ended", each with Open. Open on an ended one does what 95 does. Rename a session's transcript file under
    `%USERPROFILE%\.claude\projects\<folder>\` away: within 15 seconds that line says "expired" and has no Open; a
    double-click on a task whose latest session is expired only switches workspace. Put the file back.
97. Double-click a task whose session is a VS Code session you are sitting in: you go to its workspace and that VS Code
    window comes forward; no terminal opens. Double-click a plain note: only the workspace switch.
98. Start Claude on a note in a folder Claude Code has never trusted: the thread gets Claude's "Workspace not trusted"
    message, and a box asks "Open a terminal there so you can trust it?". Yes opens a plain Windows Terminal window
    running `claude` in that folder; accept the trust prompt there, then "Start Claude on this" again works. With
    `claude.exe` renamed away as in 89 (and nothing in `%USERPROFILE%\.local\bin`), "Start Claude on this" asks
    "Claude Code is not installed on this computer ... Install it now?". No writes the reason in the thread and opens
    nothing. Yes opens a Windows Terminal window (a PowerShell console without Windows Terminal) in the chosen folder:
    it says it is installing Claude Code, runs the official installer, then starts `claude` for the sign-in and the
    trust prompt; the window stays open afterwards. Once signed in, "Start Claude on this" starts the session. Best
    run on a machine or in Windows Sandbox that never had Claude Code, since the installer replaces the real one. `$ts task start <id>` on a closed task is refused at once with "The task is closed. Reopen it to
    start Claude on it."
    On a machine without Windows Terminal (Windows Server 2022, or a test machine where `wt.exe` is not on the PATH and
    not in `%LOCALAPPDATA%\Microsoft\WindowsApps`), the same Yes opens a plain console window running `claude`, and a
    double-click on a task with a background session opens a console window titled "<the task's title> · Claude"
    running `claude attach`; the title stays while Claude works, and a second double-click brings that window forward
    instead of opening another.
99. From a terminal: `$ts task go <id>` does what the double-click does; `$ts task start <id>` on a task with one folder
    starts there, and on one with several refuses, naming them, until `--folder <path>` picks one;
    `$ts task open-session <id> <short id>` opens that session. Each answers with the task's JSON.
100. Reboot the machine (or sign out and back in) with three open Claude tasks, one of them mid-turn when you go.
    Nothing is resumed for the first two minutes after Deskbar starts; then the three sessions come back one at a
    time about five seconds apart, each with a
    "Resumed by Deskbar" line in its thread. The mid-turn one says its last turn was cut off and carries on by itself.
    A task you marked done before the reboot is not resumed. (Steps 84 to 86 cover the same with `claude stop`; this
    is the real thing, so run it when a reboot is cheap.)
101. Restart Deskbar itself while a session is in the middle of a turn: the session is untouched (it is not Deskbar's
    child), the tasks show it running again within 15 seconds, and nothing is resumed twice.
102. Close any window on a task's background session and mark the task done. About a minute after the session goes
    idle, the thread says "Stopped by Deskbar at HH:mm, since the task is finished" and `claude agents` no longer
    lists it; nothing resumes it. Double-click the task: the session comes back under the SAME id and its window
    opens, and it keeps running while that window is open. Close the window: within a minute and a quarter it is
    stopped again. Reopen the task (In progress): it is resumed at the next poll and is not stopped. A task waiting
    on you, with its session idle, is never stopped. Do it once with a session that joined from the repo and then
    entered a git worktree: once stopped, its row in the thread reads "ended" with an Open button, never "expired",
    and Open brings it back under the same id.
102b. Open a task's background session in its window (double-click the task) and let it go idle, then close the task
    (tick it off). About a minute later the window closes by itself, the thread says "Stopped by Deskbar at HH:mm,
    since the task is finished" and `claude agents` no longer lists it. Do the same with Done instead of closing: the
    window stays open and the session keeps running until you close the window. A session that is also on an open
    task (it joined a new one) is not stopped when the old task is closed.
103. Take a give-up from 88 and set the task's status (to In progress, say; a keeper's question has no answer box): the
    next poll tries the resume again instead of staying silent.
104. Esc, then close: in a background session's terminal window, ask for a long reply and press Esc halfway through it,
    then `claude stop <short id>`. Two minutes later the session is resumed WITHOUT the "told to continue" wording:
    the thread says only "Resumed by Deskbar at HH:mm.", and the session sits idle waiting for you rather than
    finishing the reply you stopped.
105. A VS Code window reload with a Claude panel open on a task's session: Developer: Reload Window. The panel comes
    back on the same session within a few seconds, and Deskbar starts no background copy: `claude agents` never shows
    a second entry for it, and the thread gets no "Resumed by Deskbar" line (the two-minute wait covers the reload).
    The same after closing VS Code and opening it again within two minutes.
106. /clear in a VS Code session that is on a task: the old session shows ended on the task, the new conversation is on
    the same task at once (its SessionStart hook says "clear"; the task keeps its status), and the cleared session is
    never resumed, however long you wait (the thread gets no "Resumed by Deskbar" line for it). A /clear in a session
    with no task joins nothing. Note in the thread's session list whether the new conversation has a new session id:
    if it kept the old one, there is a single live line instead (unmeasured so far; either way nothing is resumed).
107. Double-click a task the keeper gave up on (a give-up from 88 after trusting the folder again, or one that hit "3
    resumes in 10 minutes"): its session is resumed and its window opens, and the task goes back to In progress
    with no question. The next stop of that session is resumed again two minutes later, not given up on at once.
108. The bar's list scrolls. On a floating bar with ten open tasks, three lines show and "+7 more". Turn the wheel
    over the list: it scrolls through all ten, the bar's height does not change, and "+7 more" keeps counting the
    lines out of sight. With a task waiting on you at the top, scroll down past it: the count turns orange and reads
    "+N more, 1 waiting". Scroll back up. Then bring a Claude terminal whose task line is scrolled out of sight to
    the front: the list scrolls just far enough to show that line, selected.
109. Claude working block (task 93fa0b9b). In the tasks panel's List, with one task waiting on you, one task a Claude
    session is working on and a note: the order is the waiting task, a "CLAUDE WORKING" caption with the working
    task under it, then the note's quadrant. With two working tasks in one quadrant, drag one above the other: they
    swap. Stop its session: the task drops back into its quadrant at its place; marked done instead, it joins the
    done tasks in the Waiting on you block. In the Matrix, its cell lists the waiting task first, then the working
    one.

## Subtasks

Spec `docs/superpowers/specs/2026-11-24-subtasks-design.md`; the look is Petre's pick A. `$ts` is
`%APPDATA%\Deskbar\deskbar.cmd`.

S1. Right-click a note on the bar: **Add subtask…** comes after Edit (after **Start Claude on this** when Claude is
    set up). Pick it: the add box takes the keyboard with a chip "↳ <the note>" after the workspace chip, the
    workspace chip shows the note's workspace (pick a note on another workspace to see it move), and the box says
    "A subtask: Enter adds it, Esc drops the parent". Type a title and press Enter: a new line "↳ <title>" appears,
    its tooltip says "Subtask of <the note>", and the note's line now ends "· 1 subtask". No line moved.
S2. Pick **Add subtask…** again, type something, press Esc once: the chip goes, the text stays and the placeholder is
    back to the plain one; Enter adds a plain note. Again, but click the chip instead of Esc: the same.
S3. **Add subtask…** on the subtask's own line adds a sibling: the note now says "· 2 subtasks".
S4. In the tasks panel at full width (692 DIP or more) the subtask reads "↳ <parent, cut> › <title>"; drag the panel
    narrower: the slim line reads "↳ <title>". In the Matrix too it is "↳" alone. Rename the parent in the panel: its
    subtasks' lines follow. The panel's "..." menu has **Add subtask…**: it opens the task's thread with a box where
    **Add subtask** was (S11a), not the add box at the top.
S5. Close one subtask: the parent's count drops by one. Delete the parent: the subtasks stay, as plain lines.
S6. `$ts task parent <note> <another note>` is refused when the note has subtasks ("has subtasks of its own"), and
    `$ts task parent <x> <a subtask>` is refused ("Subtasks go one level deep"). `$ts task add "Child" --parent <id>`
    with no `--workspace` lands on the parent's workspace. `$ts task family <subtask>` prints the family text;
    `$ts task family <a lone note>` says it has none. `$ts task list` shows `parent` and `subtasks`.
S7. On a task a Claude session has worked on, add a subtask with only a title and pick **Start Claude on this** on it:
    the folder offered first is the parent session's repo. Ask the new session what Deskbar told it when it joined:
    it names the parent, its status line, the parent session and its transcript file, and it has read the end of
    that transcript before starting (ask it what the parent session decided).
S8. In a session on a task, ask it to split off a piece with `task subtask`: a new Claude-assigned backlog subtask
    appears under its task. Start a second session on the workspace: it is offered that subtask. In a session on a
    subtask, `task subtask` makes a sibling, not a grandchild.
S9. Resume a session on a subtask (the history list): its start briefing carries the family again. Its prompt
    reminder mentions "It is a subtask of <id>" and `task family`.
S10. Subtasks under their parent. Add two subtasks to a note in quadrant IV and mark one of them important: on the
    bar and in the tasks panel both are drawn right under the parent, "↳ <title>" with no parent's name, and no group
    caption or gap opens between them. "· 2 subtasks" never breaks across two lines, at any panel width. Filter the
    panel to a workspace the parent is not on (put one subtask on another workspace first): that subtask shows in its
    own place with "↳ <parent> › <title>". Ask something from a session on a subtask: it moves up to Waiting on you.
S11. Open the parent in the tasks panel: with Subtasks ticked its thread lists neither subtask, since both have a line
    right under it (task 0eb943d8). Untick Subtasks: the thread lists both with their status and status line; click
    one and it opens right there, its own thread (its description and the description's pictures first, then sessions, comments, its comment box) under its entry inside the
    parent's thread, with no line of its own and no second "Subtask of" or **Add subtask**; click it again and it
    folds. Comment on it there: the comment shows in place. Each entry starts with a tick: tick one, it closes with
    Undo in the tick's place; Undo brings it back; after the undo window its entry says Closed and has no tick. A closed subtask in the thread opens the same way, without
    the filter changing.
S11a. Adding a subtask in place (task 0eb943d8). Open a note's thread and press **Add subtask**: a box takes its place,
    in the thread, with the keyboard; the add box at the top stays shut. Type a title, Shift+Enter, a second line, Enter:
    the note's thread closes and the new subtask, right under the note, is picked, glows and has its thread open; its
    description is the second line. Untick Subtasks and add another from the note's thread: the note's thread stays
    open and the new subtask opens inside it, under its entry, nothing picked. Again, then Esc: the box goes and **Add subtask** is back, the thread still open;
    Esc again folds the thread. Open the box, fold the thread with a click on the line, open it again: the button, not
    the box. Ctrl+V a picture into the box, Enter: the subtask carries it. On a subtask's line, **Add subtask…** adds a
    sibling the same way, in the subtask's thread.
S12. (Removed with the **Subtask of** menu item: dragging onto a line's middle does it now, S13.)
S13. Drag to nest. On the bar, drag a note slowly down over another note: over the line's top and bottom quarters the
    drop line shows between lines, over its middle the line is outlined. Release over the middle: the note becomes its
    subtask and is drawn under it. Drag a note over a subtask's line, or drag a task that has subtasks: no outline,
    only the drop line. Drag a note onto a waiting line's middle: it is outlined, and the note is drawn under it in the
    waiting block. Drag a waiting line onto a note's middle: it becomes the note's subtask and stays in the waiting
    block. Drag the subtask out and drop it between two other lines: it is a plain
    task again, where you dropped it. The same in the tasks panel's List; in the Matrix there is no outline.
S14. A parent closes last. Give a note two subtasks. Hover its tick, on the bar and in the tasks panel: the tooltip
    says "Close its 2 open subtasks first". Tick it on the bar: a message says the same and the line stays as it was,
    with no Undo. Tick it in the panel, and pick Close on its menu: the parent's own line says it in orange under its title, not the top of the
    panel, and nothing moves. Close a subtask: the orange line goes.
    `$ts task close <parent>` is refused with the same words. Close one subtask: the tooltip and the refusal say "Close
    its open subtask first". Close the other: the parent's tick closes it as before, with Undo.
S15. Claude still at it (task 37cd7081). Start Claude on a task and, while it works, hover its tick in the tasks panel:
    the tooltip says "Claude is still working on this: tick to stop Claude and close". Tick it: the task stays open and
    a menu at the pointer says "Claude is still working on this task." with "Stop Claude and close". Press Esc: nothing
    changed. Tick again and pick it: the line closes with Undo, `claude agents` no longer lists the session, and the
    thread says "Claude stopped by Deskbar at HH:mm". Double-click the closed task (Closed chip): the session resumes.
    `$ts task close <id>` on a working task is refused and names `--stop-claude`; with it, the task closes and the
    session stops. A Done task and a plain note still close at once on their tick. On a task whose session runs in
    VS Code, Stop Claude and close closes the task, the window stays open, and the thread says so.
S15a. A merged waiting task closes at once (task 3cdd0a52, narrowed by task 579f2e4c). Take a Claude task waiting on
    you whose session is still live and whose pull request has merged (it asked you to install an update). Its tick
    tooltip says "Tick off: closes Claude's task". Tick it on the bar or in the panel: it closes at once with Undo, and
    no menu opens. Within a few minutes
    `claude agents` no longer lists its background session (the keeper stops a closed task's session).
S15b. An unfinished task asks first (task 579f2e4c). Take a Claude task waiting on you with a question and no merged
    pull request (it asks a decision, or to merge a PR still open). Its tick tooltip in the panel says "Not finished and
    not merged yet: tick asks before closing". Tick it in the panel, then use Close in its menu on the bar: each time a
    menu at the pointer says "This task still asks you something, and its work is not merged yet." with "Close anyway".
    Press Esc: the task still waits, its question in place. Pick Close anyway: it closes with Undo, and Undo brings the
    question back. `$ts task close <id>` on such a task is refused and names `--anyway`; with it, the task closes.
S16. The Subtasks checkbox (task 173ef268). Give a note three subtasks and ask a question on one of them with
    `$ts task ask` from its session (or set it Waiting). In the tasks panel the toolbar's "Subtasks" box is ticked and
    the subtasks are under the parent. Untick it: the two plain subtasks go, the parent still says "· 3 subtasks", the
    asking one stays in Waiting on you, and opening the parent lists all three. Filter to another workspace than the
    parent's with a subtask there: that subtask still shows. Close and reopen the panel, then restart Deskbar: the box
    is still unticked. Switch to the Matrix: subtasks whose parent is in a cell are gone. On the bar, the parent's two plain
    subtasks are gone too and the asking one is still there. Tick it: everything is back, in the panel and on the bar.

S16a. A snoozed parent's subtasks (task 4c01747c). With the Subtasks box unticked, snooze the parent from S16. Its two
    plain subtasks stay gone from the panel (List and Matrix) and from the bar, though the parent is off both; the asking
    one still shows, and so does a subtask set Done, in Waiting on you. Type a word from a plain subtask's title in the
    panel's search box: it shows while the word is there. Filter the panel to another workspace than the parent's with
    a subtask there: that subtask still shows. Tick the box: both plain subtasks are back. Wake the parent: with the box
    unticked again, the plain ones fold under it as in S16.

S16b. A Claude parent's subtask on the bar (task 4c01747c). Give a task assigned to Claude a subtask assigned to you.
    With the Subtasks box unticked the bar shows no "↳" line for it, though the parent has no line on the bar; the
    panel still lists the parent with "· 1 subtask". Set a second subtask of it to Waiting on you: that one shows on the
    bar. Pick "Add subtask…" on its line and add one: the new line shows on the bar and glows; click another line and it
    folds away. Tick the box: the plain subtask's line is back on the bar.

S16c. A Done parent's open subtask (task 29d4a541). With the Subtasks box unticked, set the parent from S16 Done while
    one plain subtask is still open. In the Inbox's Needs you tab that subtask has its own line right under the parent,
    in Finished; in the List it is under the parent in Waiting on you, and in the Matrix it keeps its line. Tick the
    subtask closed from its line, then tick the parent: it closes, with no "Close its open subtask first". A plain
    subtask of a parent that is not Done still folds as in S16.

S17. A subtask's way to its parent (task 54b39c4c). Give a note three subtasks. In the tasks panel open the last
    subtask's thread: it starts with "↑ Subtask of <the note>". Click it: the note is picked, its thread opens and the
    list scrolls to it. Right-click a subtask's line in the panel: **Open parent** follows **Add subtask…** and does the
    same; a plain note's menu has no **Open parent**. Filter the panel to another workspace with a subtask moved there,
    open it and click "Subtask of": the filter goes back to all and the parent shows. On the bar, right-click a
    subtask's line: **Open parent** shows the parent in the tasks panel. Open a subtask in its own window: the side
    column has a Parent section above Subtasks, and its button opens the parent's window; Details no longer lists
    "Subtask of".
S18. A subtask on a line of its own moves out as a plain task (task 7378433e). Give a task assigned to Claude a
    subtask assigned to you, with the Subtasks box ticked: on the bar the subtask has a line of its own with the arrow,
    and no parent above it. Drag it down and back onto its own line: no drop line, and it is still a
    subtask. Drag it between two other lines: it is a plain task there, without the arrow. In the tasks panel, ask a
    question on a subtask of a plain note so it sits in Waiting on you, and drag it between two lines: it stays in
    Waiting on you and is a plain task.
S19. An open thread takes in its subtask (task 452487b4). In the tasks panel, with the Subtasks box ticked, give a
    task a subtask and put both in Waiting on you (`$ts task ask` on each). The subtask's line sits right under the
    parent's. Comment on the parent until its open thread scrolls. Open the parent's thread: the subtask's line leaves
    the list and shows once, under the thread's row of buttons, and stays there while the comments scroll. Tick the parent: the orange line under its title reads `Close its open subtask first: "<subtask title>"`.
    Close the parent's thread: the subtask's own line is back under the parent.

## Goals

Weekly and monthly goals, pull request 1: the pipe, the task's link, the tasks panel's menu and filter, the bar's
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
T7. Half a line at a crossing. Tile four windows on one screen: A B on top, C D underneath, the separations
    crossing in a "+".
    1. Drag the separation between A and B to the right. While dragging, only B follows; let go: A and B change
       width, C and D stay exactly where they were. The trace line ends `at a crossing, sizes ...`.
    2. Reset to grid, then drag the separation between A and C down. Only C follows; B and D stay.
    3. Reset to grid, drag the separation between A and B as in step 1, then drag the one under A: the vertical
       separations no longer line up, so the horizontal one moves whole and all four heights change.
    4. Reset to grid and drag A's bottom right corner, at the crossing: both whole separations move, all four
       tiles change. In every step the tiles where they were let go match what the drag showed.

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

## Recent windows of an app split last

Spec `docs/superpowers/specs/2026-11-23-split-recent-app-windows-last-design.md`. A tiled workspace with VS Code and a
browser, the trace on.

S1. Right-click a VS Code icon: "Split Code windows last" sits just above "Don't split this tile", unticked. Tick it.
    A Store app (Calculator) and Deskbar's own windows do not offer it.
S2. Click into VS Code, open a browser window from it (a link). VS Code keeps its size; the browser halves the other
    tile. The trace says `the largest free tile, the tile last used (...) is protected`.
S3. Click into the browser and open another browser window: it halves the browser, as before.
S4. With four VS Code windows used one after another, a new window halves the one used first (the fourth back).
S5. With only VS Code windows on screen 1 and anything on screen 2, a window opened from VS Code goes to screen 2.
S6. `$ts split-last 0x... off` unticks it (the menu shows it next time it opens); `maybe` prints the usage line.

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

## Pictures in tasks

Pasting and dropping pictures into a task's comments and description
(docs/superpowers/specs/2026-11-24-task-images-design.md, PR 1).

TI1. Win+Shift+S a region, open a task's comments in the tasks panel, click the comment box, Ctrl+V: a
     small thumbnail with an x waits under the box. Type a line, press Enter: the comment shows the line
     and the picture as a thumbnail under it; %APPDATA%\Deskbar\task-images\<task id>\ holds one PNG.
TI2. Copy two PNG files in Explorer and Ctrl+V in the box, or drop them on it: two thumbnails wait.
     Click one x: one is left. Enter with no text: a comment of the picture alone.
TI3. Copy plain text and Ctrl+V in the box: the text is pasted as before, no thumbnail.
TI4. Paste a picture, then let a session change its status line before posting: the picture is still
     waiting after the redraw.
TI5. A thumbnail in the thread is small (at most 64 DIP tall, 160 wide). Click it: the picture opens
     large over the screen under the cursor, the rest dimmed, never bigger than its own pixels. A click
     anywhere, Esc, or Alt+Tab closes it; "Open in viewer" opens the file in the default image viewer.
TI5a. Two screens with different scaling: click a thumbnail on each screen; the picture opens on the
     screen that was clicked, sharp, filling no more than that screen.
TI6. A comment with super long text and a picture: folded to six lines, the picture still shows in full
     under "Show more".
TI7. Edit a task (F2): paste a picture into the description box, Enter. The open line shows the
     description's text, then the picture. Edit again: the description box holds only the text and the
     picture waits under it with its x; Esc, then check no new file was written.
TI8. A description that is only a picture: the closed line, and the bar, say "image".
TI9. Delete the task (Delete for good): its task-images folder is gone. Close another task with a
     picture: its folder stays.
TI10. In a Claude session on the task, `deskbar.cmd task list`: the comment's `markdown` carries the
     picture's full path, and the session can Read it without a permission prompt;
     ~/.claude/settings.json has one `Read(//c/.../Deskbar/task-images/**)` rule under permissions.allow.
TI11. In the tasks panel, + Add, then Win+Shift+S a region and Ctrl+V in the add box: a thumbnail with an
     x waits under the box. Type a title, Enter: the new task has that title, its open line shows the
     picture under the description, and task-images\<new task id>\ holds one PNG.
TI12. + Add, Ctrl+V a picture, Enter with nothing typed: a task called "Picture" whose closed line says
     "image". Paste two first: "Pictures".
TI13. + Add, Ctrl+V a picture, Esc, then + Add again: the box is empty and no file was written.
     "Add subtask…" on a line, Ctrl+V into the box in its thread, Enter: the subtask carries the picture.
TI14. The bar's own box ("Add to Deskbar…"): click it, Win+Shift+S a region, Ctrl+V. A small thumbnail
     with an x waits between the box and the "on" chip; on a docked bar the task lines below make room.
     Enter with nothing typed: a task called "Picture" with the picture as its description. Again with
     a title: the task has that title. Paste two, click one x, Esc: the box and the row are empty.
TI15. Drop a PNG from Explorer on the bar's idle box: the thumbnail appears and the box has the
     keyboard; Enter adds the task.
ML1. The bar's box: type a title, Shift+Enter, two more lines. The box grows a line at a time and a
     docked bar's task lines make room. Enter: the task has the first line as its title and the other
     two as its description. Paste ten lines: the box stops at six and scrolls inside.
ML2. A long title with no line break wraps in the bar's box instead of scrolling sideways out of sight.
ML3. The tasks panel, + Add: Shift+Enter starts a new line, the box grows, Enter adds title and
     description the same way. With a picture pasted too, the description holds the text, then the picture.

## Comments wake their session

(docs/superpowers/plans/2026-11-24-comment-wakes-session.md.) ~/.claude/settings.json's Stop holds a
claude-wait-hook.cmd entry with "asyncRewake": true and "timeout": 2592000 while sessions keep their tasks.

CW1. An idle background session's task: paste a screenshot into a comment and press Enter. Within a few
     seconds the session takes a turn, reads the picture and answers about it.
CW2. Post two comments quickly on an idle session's task: it wakes once with both, in order.
CW3. A Done task whose session Deskbar stopped: comment "is it shipped?". The session is resumed with the
     comment (a "Resumed by Deskbar" line in the thread) and the task stays Done.
CW4. Comment while the session is busy with a long command: the comment arrives at its next step.
CW5. A VS Code session idle on its task: comment. Note whether it wakes; if not, it should get the
     comment with your next message to it. Record the result here.
CW6. A comment on one of your plain notes (no session): nothing happens and no Deskbar line appears.
CW7. Restart Deskbar while a session sits idle, then comment: it still wakes.

## Windows a hidden session opens

(Deskbar task e671709b.) A `claude --bg` session's windows go to its task's workspace. Tracing on, the log shows
`launched-by <process>(<pid>) launcher=session <short id> workspace=<name> moved=<bool>` for each.

HS1. Start Claude on a task in workspace A, then switch to workspace B. Ask the session to open a page with its
     browser-automation tool (Playwright). The browser window lands in A with the attention dot, and B is untouched.
HS2. Same, with the task's workspace in front: the window opens there and has no dot.
HS3. A session on a free task (no workspace): its window stays where it opened.
HS4. A browser already running: ask the session to run `start https://example.com`. The window or tab stays with
     the browser, as before (the session should use `deskbar.cmd open` for this).

## The terminal in front selects its task

(Task 4846fde1.) Two Claude tasks with their session terminals open ("<title> · Claude").

AT1. Click the first task's terminal: its line is highlighted on the bar and in the tasks panel.
AT2. Alt+Tab to the second task's terminal: the highlight moves to the second task's line.
AT3. Switch to a browser or VS Code: the highlight stays on the second task.
AT4. Click a different task's line on the bar, then the first terminal again: the highlight follows the terminal.
AT5. With the tasks panel open and scrolled to the top, bring forward the terminal of a task further down: the
     panel scrolls just far enough to show its line. Bring the same terminal forward again: nothing moves.
AT6. (Task 6bb3dac6.) Select some other task. Close a Claude task's terminal, then Open Claude on that task (bar menu
     or the panel line's button): as soon as the new terminal opens, the task's line is highlighted on the bar and in
     the panel, without clicking away and back. Same with Start Claude on this for a task with no session.
AT7. With a task's terminal in front and its line highlighted, click another task's line on the bar and keep typing in
     the terminal: the highlight stays on the task you clicked.

## A new task is highlighted

(Task ee75f046; mockup https://claude.ai/artifact/8r8fRpLoTqvrgpjELDgEd9, Petre's pick A and "yes" to the panel.)

NH1. Select some task on the bar. Type a note in the bar's box and press Enter: its line starts in a blue glow
     that fades in about two seconds into the selected look (blue edge, lighter fill) and stays selected. The task
     selected before is not any more.
NH2. Same on a floating bar with several flagged tasks, so the note (top of quadrant IV) lands below the three
     lines on show: the list scrolls to it (the bar's task scrolling, task ea017420), glowing.
NH3. With the tasks panel open, add from the bar: the panel shows the new task selected too.
NH4. In the panel, type "rent" in the search, then + Add, type "Call the bank", Enter: the search clears, the panel
     scrolls to the new line, which glows and stays selected; the bar shows it selected too.
NH5. Turn off Windows' "Animation effects" (Settings, Accessibility, Visual effects) and add a note: the line holds
     the glow's blue, still, for about two seconds, then turns to the selected look in one step.
NH6. With a Claude session's terminal in front, click the bar's box, type a note, Enter: the keyboard goes back to the
     terminal and the new note stays selected; the terminal's own task does not take the selection back. Then click
     another window and the terminal again: now its task is selected, as before.

## Snoozing a task

(Task 6da8d7eb.) The tasks panel open, full-width lines.

SZ1. Hover a note's line: a moon shows after the two toggles. Press it: 1 hour, 3 hours, "Tomorrow, <day> 09:00",
     "Monday, <date> 09:00". Pick 1 hour: the line leaves the panel and the bar, and a "Snoozed 1" chip appears.
SZ2. Press "Snoozed 1": the line is there with "Snoozed until <time>" under its title and a lit moon. Its moon (or "⋯",
     "Snoozed") offers "Wake now" first; pick it: the line goes back to its place in its quadrant, and the chip goes
     once the filter is changed.
SZ3. `deskbar.cmd task snooze <id> 1m`, wait a minute: the line comes back on its own within 30 seconds of the time.
SZ4. Snooze a Claude task, then have its session run `task ask`: the task comes back at once, waiting on you.
SZ4a. Snooze that waiting task with its moon: it leaves "Waiting on you" (the chip's count drops) and the bar. Wake it:
     it is back in "Waiting on you" with its question. Snooze it again and have the session ask once more: it comes
     back at once with the new question.
SZ5. Snooze a task for 3 hours and restart Deskbar: it is still snoozed. Snooze one for 1 minute, quit Deskbar for two,
     start it: it is back.
SZ6. A Matrix line has no moon; "⋯", "Snooze" reaches the same lengths.

## Links in a question

(Task 08cdb052.) The tasks panel open. From a Claude session with a task, run `task ask` with this on stdin:
`Merge [Deskbar #1](https://github.com/AppifySheets/Deskbar/pull/1), see https://deskbar.app`.

QL1. The closed line's question reads "Merge Deskbar #1, see https://deskbar.app", with both links in blue.
QL2. Click "Deskbar #1": the pull request opens in your browser, and the line is neither picked nor opened. Click the
     question's other words: the line is picked and opens, as before.
QL3. The bar's line for the task reads "Merge Deskbar #1, see https://deskbar.app" with no markdown, and a click on it
     picks the task as before.
QL4. Start a new Claude session and ask it to ask you to merge a pull request: its question names the pull request as
     a link you can click.

## Links open in the task's workspace

(Task 26f15d70.) Two workspaces, A and B, with a browser window open on each. A task on workspace B whose question or
description has a web link, and a comment on it with another link. Stand on workspace A with the tasks panel open.

TW-L1. Click the question's link: Deskbar switches to B and the page opens as a tab in B's browser window, not in A's.
TW-L2. Back on A, open the task's thread and click the comment's link: the same, B in front with the page there.
TW-L3. Close B's browser window, go back to A and click a link again: B comes to the front with a new browser window
       holding the page, and A's browser window is untouched.
TW-L4. On B, click a link of the same task: no switch, the page opens in B's browser as before.
TW-L5. A note on no workspace with a link in its description: open it in its window (Open in window) and click the
       link: it opens on the desktop you are on, with no switch.
TW-L6. Open the B task in its own window while on A and click a link in its description or its pull requests: the
       same as TW-L1.

## A merge settles its question

(Task b7f987b4.) The tasks panel open, `gh` signed in. Open a throwaway pull request on a repository of yours, and from a
Claude session with a task run `task ask` with `Merge [this](<its url>)` on stdin.

PM1. The task waits on you at the top of the bar, as before. Leave it for a minute: it stays.
PM2. Merge the pull request on GitHub. Within 30 seconds, and before the session runs `task answered`, the task leaves the
     bar and is in progress in the panel. Its thread ends with "Deskbar settled the question at <time>: [<repo> #<n>](...)
     was merged." The session's own `task answered` afterwards changes nothing.
PM3. Ask again, linking a pull request that was merged long ago ("now that [#1](...) is in, A or B?"): it keeps waiting.
PM4. Ask linking a pull request, then close it without merging: the question settles, and the line says "closed without
     merging".
PM5. (Task ede8dbe8.) Open the pull request some way other than the session's `gh pr create` (on the GitHub page), so
     the task does not list it, and have the idle session ask for its merge. Merge it: within 30 seconds the question
     settles, the task lists the pull request, and the session wakes with "Your question is settled: ... merged" and goes
     on to its runs and deploy without anyone commenting. With PM4's close instead, it wakes with "closed without merging"
     and the task lists nothing.

## A reply sets the question aside while Claude works

(Task 8bd18246.) The tasks panel open. From a Claude session with a task, run `task ask` with a question on stdin and let
the turn end.

RA1. The task waits on you at the top of the bar. While the session finishes the asking turn, it stays waiting.
RA2. Comment on the task with an instruction that takes the session a minute ("count the files under src, then tell me").
     Within about 45 seconds of the session starting on it, the task leaves "Waiting on you" and shows under "Claude
     working". Its thread ends with "Deskbar set the question aside at <time>: ...", quoting the question.
RA3. At the end of that turn the session reads "Stop hook feedback" naming the question, and either asks again (the task
     is back in "Waiting on you" where it was) or runs `task answered` (it stays in progress). Interrupt a session (Esc)
     while its question is set aside: within about 15 seconds Deskbar asks the session to write its question afresh
     (task 0cac3082; the session starts a turn reading "Deskbar: your task ... is about to wait on the user again").
RA3a. That turn ends with a new `task ask` (the task waits on you with the new text) or `task answered`. If the session is
     interrupted again or says nothing, about two minutes later the old question comes back as it was. With the
     session's terminal closed and no way to resume it, the old question comes back at once and the thread says no
     session could be asked.
RA4. Ask again, then type a reply in the session's terminal instead of commenting: the same as RA2 and RA3.
RA5. Ask again and reply nothing. A session woken by its own pull request watch (a rebase) leaves the task waiting.

## Done tasks wait on you

(Task eefb986a.) Three notes on one workspace, one of them flagged urgent and important.

DW1. Mark a plain note Done from its menu: it moves up into the top block with the questions, above the urgent one,
     with its green tick, and no space between it and a question above it.
DW2. Have a session ask something: its task goes above the done one.
DW3. In the tasks panel, the done task is under the "Waiting on you" caption. The "Done" chip shows it alone, under
     the same caption; the "Waiting on you" chip shows only the question.
DW4. With two done lines, drag the lower one above the other: they swap (task f41d7857). Drag a done line up onto the
     question: it stops at the top of the done lines, under the question. A plain line dropped on the done line's top
     edge goes to the top of its quadrant, below the block.
DW5. Set the done task back to In progress: it goes back to the place it had in its quadrant.

## A task in its own window

(Task dca4ebeb.) The tasks panel open, a task with a long description (a few headings, a list, a link, a picture) and
more than three comments, one of them yours.

TW1. Right-click its line in the panel: "Open in window" is second, after Edit. Pick it: a window titled after the task
     opens on this desktop and comes to the front, with the whole description rendered, its picture, and every comment
     (none folded behind "N earlier comments", none cut to three lines). The question or status line shows in full.
TW2. Pick "Open in window" again, from the bar's right-click menu of the same task: the same window comes forward, no
     second one. Open the task's thread in the panel: "Open in window" is the first button in it.
TW3. Leave the window on this desktop, switch to another desktop and run `deskbar.cmd task open <id>`: the window moves to
     the desktop you are on and comes to the front, without taking you back.
TW4. Press F2 (or Edit): the title and the description become boxes. Change both, paste a screenshot into the
     description, look at Preview, press Ctrl+S. The window and the panel's line both show the new title, description
     and picture. Edit again and press Esc: nothing changes.
TW5. Type a comment over two lines (Enter makes a new line) and press Ctrl+Enter: it posts as you, the box empties, and
     the comment shows in the window and in the panel's thread. While you type, have a session post a comment: your
     words stay in the box.
TW6. Narrow the window below about 1100 pixels: sessions, subtasks and details move under the thread. Widen it: they
     return to the right-hand column.
TW7. Move and resize the window, close it, open another task's window: it opens where the first one was, at its size.
     Restart Deskbar and open one again: same place. Maximize one, close it, open another: it opens maximized.
TW8. With one task window open, open a second task: the second opens a little down and to the right of the first.
TW9. A subtask in the side column opens in a window of its own. "Delete for good" from the window's "⋯" menu closes
     the window with the task.
TW9a. (Task 4c746a42.) On a tiled workspace, open a task's window: it takes a tile beside the other windows instead
     of floating over them, and closing it gives its tile back. Then open one on an untiled workspace: it opens at the
     place the last untiled one was left, not at the tile's size.
TW10. (Task e91f8aa5.) Have a session ask a question of ten lines or more, on a task whose description is long too. Open
     its line in the panel: the question and the description each stop after four lines, cut between two lines, and
     "Show all in window" sits under them. Click it: the task's window opens with both in full. A link inside the four
     lines of the question still opens in the browser. A task with a one-line question has no "Show all in window".
TW11. (Task 2088e1ed.) On the TW10 task, a thin band with a short bar in its middle sits under the question and under
     the description; the pointer over it turns into the up-down arrow. Drag the description's band down: more of it
     shows as you drag, a line at a time; up: less, down to one line. Release at a height: it stays there. Drag it to
     the bottom: the whole description shows and "Show all in window" goes if the question is whole too. A press on the
     band neither folds the line nor starts dragging the task. Fold the line and open it again, and close and reopen the
     panel: the heights you left stay. Restart Deskbar: back to four lines.
TW12. (Task 2088e1ed.) In a thread, a comment cut at six lines has the same band under its text; a comment shown whole
     has none. Drag it down and up: the comment shows more and less, live. Release halfway: that height stays, and "Show
     more" still opens it whole. Drag one to its bottom: its button says "Show less". Show less brings it back to six
     lines.
TW13. (Task ee9bd3fc.) A task whose last three comments together are taller than the panel, its line near the bottom
     of the list. Open its thread: the list scrolls so the line and the whole thread show, the line at the top if need
     be, and the thread ends above the bottom of the panel. The comments scroll inside the thread and start at the
     latest; the row of buttons stays above them and "Add a comment…" below. Wheel up inside the thread to its first
     comment and keep going: the list scrolls instead. Scroll the thread halfway and wait for a status line to change:
     it stays where you left it. Make the panel shorter: the thread shrinks with it. A subtask opened inside the thread
     scrolls with the parent's comments and has no scroll of its own. Same in a Matrix cell, against the cell's height.

## The narrow panel's titles

(Task 65ab598d.) The tasks panel docked, a Claude task with a long title (two lines or more at 460 DIP) and one with a
one-word title.

NT1. Drag the panel to its narrowest (360 DIP). Every title reads across the line in words, never one letter per line,
     and the line's buttons (squares, age, workspace chip, comments, Open Claude, "⋯") sit in a row under it.
NT2. Widen it to the default 460 (task 50667e10): on both tasks, the one-word one too, the buttons sit on a row of their
     own under the title, starting at the title's left edge, each at the same x on every line. A waiting task's question
     comes under that row.
NT3. (Tasks f564a7cd, ec731be5.) Without the pointer on a line, its flag toggles and the moon show between the
     workspace chip and the comments, about as light as the comment button: clearly readable, no empty hole there.
     Hover the line: they go full strength, never over the title's text. A lit toggle still stands out in its colour. Widen the
     panel past 850 DIP: on the full lines the off toggles are out of sight again until hover.
NT3b. (Task 735abbf5.) Look closely at the three toggles on a narrow line, and on a full line under the pointer: the
     "!", the clock and the moon are each whole. The moon is a full crescent, its outer arc on the right included, not a
     thin hook. The comment bubble and "⋯" beside them are whole too.
NT3a. (Task f564a7cd.) A task between an hour and a day old reads its whole age on a narrow line, "12h59m", never
     "12h5…".
NT4. Press F2 on a narrow line: the editor takes the whole line. Press Esc: the buttons come back.

## The tasks panel's columns

(Task 3df2d28f.) The tasks panel at 740 DIP or wider, on the List, with a Claude task and a note.

TC1. A header row sits over the lines: Task, Status, Sessions, Who, Age, Workspace, each over its column on every
     line, with a hairline before each of the last five. With the list's scroll bar showing, the header still
     lines up with the lines.
TC2. The toggles, comments, Open Claude and the menu sit together at each line's right end. A long title wraps at
     about twice the width it had before.
TC3. Drag the hairline before Sessions to the left: the Sessions column widens on every line as you drag, and the
     titles narrow. Drag it far left: it stops while the title still has about 80 DIP. Drag it right: it narrows to a
     few letters and an ellipsis, never to nothing.
TC4. Close and reopen the panel, then restart Deskbar: the width is kept.
TC5. Double-click the hairline before Sessions: the column goes back to its usual width.
TC6. Narrow the panel under 850 DIP: the lines go slim and the header goes. Switch to the Matrix (780 DIP or more):
     no header there either.

## The manager session

(Task bbf9a6ed.) The bar showing, `$ts` as above, a few open tasks and one Claude task waiting on you.

MG1. Click the bar's Manager button. The first time, Claude Code may show its folder trust prompt in the new window:
     accept it. A second click, once the session is up, starts the manager; ask it "what is on the board?" and it
     answers from `task overview`.
MG2. Click Manager again while its window is open: the same window comes forward, and no second session starts.
MG3. Close the manager's window and click again: it attaches to the same session (same conversation), not a new one.
MG4. Next day, or after editing the stored day in the state file to yesterday, click: yesterday's session is stopped
     (its terminal window has closed, claude stop closes it) and a NEW window opens on today's manager.
MG5. Ask the manager to pass "The user says: yes" to a waiting task. That task's session wakes and settles its question.
MG6. Ask the manager to close a task: it says it cannot, and the task stays open.
MG7. With nothing stored, have a manager session already running in the manager folder (for example a start that was
     listed late). The next click attaches to it and never starts a second one.
MG8. From any terminal, `$ts task overview` prints one line per open task.
MG9. (Task 1c9eefdf.) After a new day's manager has started, open the Claude app on the phone: "Deskbar manager" is in
     its list of sessions and a message sent from there reaches it. Start Claude on a task: that session is NOT in the
     phone's list, also with `"remoteControlAtStartup": true` in `~/.claude/settings.json`.
MG10. (Task 56b76f4a, needs a manager started after this build.) Ask the manager for a small piece of work no open task
     covers, in plain words and without saying "create a task", for example "the add box should keep its text when I
     press Esc". It adds a task on the right workspace with your words quoted in its description, starts a Claude
     session on it, and answers with the task's title and short id. Ask for work an open task already covers: that
     task gets a comment quoting you, and no new task appears.

Manager event wakes (docs/superpowers/specs/2026-11-25-manager-event-wakes-design.md):

MW1. Open the manager, close its window, and leave a Claude task's session idle for 15 minutes without `task ask` or
     `task done`, then let it ignore Deskbar's ask (89a) so the task is handed back 5 minutes later. Within about half an hour of the manager's start, the task's thread has one manager comment and the
     session took a turn on it. The Manager button shows no dot (task 6a87df67: a first nudge is not news for you).
MW2. Let the same session stop again the same way: the next wake lights the orange dot. Open the manager: the dot goes,
     and its last reply names that task in a line, without listing the questions or merges the bar already shows.
MW3. Leave a question unanswered for two hours on a day the manager is open: the manager is not woken for it, and no
     dot appears (task 6a87df67).
MW4. On a day the manager was never opened, nothing wakes it and no dot appears, whatever is handed back.
MW5. While you are typing to the manager (a turn in progress), no wake lands in the middle; it comes after the turn.
MW6. (Task 43765c61; no manager needed.) Have two Claude tasks, not Done, with merged pull requests in one repository,
     neither reported deployed. When one session reports `task deployed`, the other task's thread at once gets a
     Deskbar line naming the repository and the reporting task, its session wakes, checks, and reports `task deployed`
     if its change runs. A task whose pull request is still open, is in a different repository, or is Done gets
     nothing, and a "nothing to deploy" report tells nobody.

The manager's question line (task 61e4d143, docs/superpowers/specs/2026-11-25-manager-question-line-design.md):

MQ1. Open the manager and tell it: "Ask me with task ask whether to tidy the board tonight." A violet line starting
     "Manager" appears at the top of the bar's task list, and a violet "Manager asks" tile at the top of the tasks
     panel, above every task waiting on you, with the whole question and its age.
MQ2. Scroll the bar's task list and the panel: the manager line stays in sight above the lines.
MQ3. Close the manager's window and click the line on the bar, then on the panel: each opens the manager's window, and
     the line stays.
MQ4. Answer the manager in its window: the line goes from both places at once.
MQ5. Have it ask twice in a row: only the second question shows.
MQ6. Open the panel's Stats view: no manager line over it. Back to the list: the line is there again.
MQ7. The next day, click Manager: the new manager starts without yesterday's question, and the line is gone.

The manager's window set apart (task f171bca6):

MP1. Click Manager with no manager window open: the new window opens and within a moment sits in the bar's pinned
     row. Switch workspace: the window is still on screen.
MP2. With a manager window already open from an older build, restart Deskbar: the window gets pinned without a click.
MP3. On the bar and in quick find, the manager's icon is Claude's mark in violet; a task session's window keeps the
     plain orange one.
MP4. Click into the manager's window: its ring is violet, not white. Click into another window: that one's ring is
     its workspace colour again.
MP5. Have the manager ask you something (as MQ1): its window's icon on the bar glows orange until you answer in it,
     and clicking into the window alone does not stop the glow.
MP6. With the tasks panel open, click Manager: the manager's window fills a column just inside the panel, top to bottom
     of the work area, and a maximized window on that screen shrinks to stop at its edge.
MP7. Close the tasks panel: the manager's column moves out to sit just inside the bar. Open the panel again: the panel
     comes back between the bar and the manager, never inside the manager.
MP8. Drag the manager's window by its title bar and let go: it goes straight back. Drag its inner edge 100 DIP wider:
     the column takes the new width, and after a Deskbar restart it is still that wide.
MP9. Close the manager's window: the column is given back and the maximized window grows to the panel's edge again.
MP10. Minimize the bar (or restart Explorer): the manager's column comes back inside the panel once the bar docks again.

## The thread's buttons

(Task 1c17b7d6.) The tasks panel open.

TB1. Open the thread of a Claude task with one session, still working or idle: it starts with one row of buttons,
     **Open in window**, **Open Claude** and **Add subtask**. The session's line under it has no Open of its own.
     **Open Claude** goes to the session, as the line's double-click does. Once that session has ended the row reads
     the same (task 5e02e99f): **Open Claude** resumes it, and **Start Claude on this** does not join the row. Only a
     task whose session has expired (its transcript deleted) has **Start Claude on this** in **Open Claude**'s place.
TB1a. (Task 6a57f59b.) Take a Claude task whose session ran in a git worktree that has since been removed (a merged
     PR's task, once its worktree is cleaned up). Its line in the tasks panel shows the plain spark, Start Claude on
     this, not the orange one; its "⋯" menu and its menu on the bar have **Start Claude on this** and no **Open
     Claude**; its thread's row agrees. Press the spark: Claude starts in the repo the worktree belonged to and a
     window opens on it. Before the fix the orange spark showed and a press did nothing at all.
TB2. Open the thread of a note: the row reads **Open in window**, **Start Claude on this**, **Add subtask**. Press
     **Add subtask**: the box opens right under the row and the button leaves the row; Esc brings it back.
TB3. Open the thread of a task with two sessions: each session line that can be opened has its Open again. Narrow
     the panel to its narrowest: the row wraps onto a second line instead of being cut off.

Since task 59b8f613 the row leaves out each button its line already shows (see the next section), so run TB1 to TB3
with the panel at its narrowest, where the line has room for Edit only: there the row keeps **Open in window** and
**Add subtask**, and the Claude button is on the line, not in the row.

## The line's buttons

(Task 59b8f613.) The tasks panel open, a Claude task and a note in the list.

LB1. At the default width (460 DIP) a slim line's strip reads, after the comments: the Claude spark (task 5e02e99f:
     orange on a Claude task, where it is Open Claude; plain on the note, where it is Start Claude on this; the same
     picture at the same x on both), then Edit, Open in window, Add subtask, Assign to, Related to, Move to (measured: six
     on a line with a comment count), then ⋯. Each sits at the same x on every line; a closed task leaves the Edit, Open
     in window and Add subtask slots empty.
LB2. Narrow the panel to its narrowest: only Edit (and Open in window on a line with no comments) is left before ⋯, and
     ⋯ sits right after it, inside the line. Widen it a little: Delete joins. Widen past 850 DIP: the full line has all
     seven at its right end from about 940 DIP. At exactly 850 the title keeps at least 320 DIP and the strip gives up
     its buttons first, down to ⋯ alone.
LB3. Press each: Edit opens the title in the line, Open in window the task's window, Add subtask the box in its thread,
     Assign to a Me / Claude menu under the button, Related to the link picker, Move to the workspace menu. Delete opens
     "Delete for good" under it and deletes only on that second click. ⋯ still lists every item.
LB4. Open a task's thread at the default width: its row of buttons is gone, since the line above has all of them. At
     the narrowest panel the row is back with Open in window and Add subtask.
LB5. The buttons are quiet like ⋯ until the pointer is on the line, and a press on one does not open the task.

## Subtasks in a parent's thread are full lines

(Task 249a1e8a.) The tasks panel open with "Subtasks" unticked, and a parent with two open subtasks, one of them a
Claude task with a comment.

SL1. Open the parent's thread: each subtask is a full line inside it, starting with "↳", with the toggles, the snooze
     moon, the comment count, Open Claude (or Start Claude on this), the menu's buttons as far as they fit, the age and
     the workspace chip, the same as the parent's line. They are quiet until the pointer is on the line.
SL2. Click a subtask's line: its thread opens right under it, inside the parent's thread, and the subtask stays there
     (it does not jump to a line of its own). Click it again: the thread folds.
SL3. On a subtask's line, press the important toggle, the snooze moon, Edit, Assign to and ⋯: each acts on the
     subtask, not on the parent, and a right-click on the line opens the subtask's menu.
SL4. Open a subtask's thread and press its Add subtask: the box opens under its row; Enter adds a sibling, which shows
     in the parent's thread and opens there.
SL5. Drag a subtask's line out of the parent's thread: it is freed and lands where it was dropped. A drag that stays
     inside the parent's line and thread changes nothing.
SL6. A subtask that has a line of its own elsewhere (one waiting on you, in the pinned block) is still the short
     "↳ title" entry in the parent's thread, and clicking it goes to that line.

## Done when running

Task 8470d176. A Done task whose pull request is in this repository shows a hollow green ring until the Deskbar you
are running holds the merge, then the full disc. Needs `gh` signed in, and a build from main (a local build whose
commit GitHub has never seen keeps every tick full).

DR1. Have a Claude session finish a task with a pull request (`gh pr create`) and mark it done after the merge, before
     the release that carries it is installed. Within a minute its tick on the bar and in the tasks panel is a hollow
     green ring, the panel's status chip is an outlined "Merged", and the line's tooltip says "Merged in Deskbar #N,
     not in this Deskbar (version) yet".
DR2. Install the release that carries it (the update) and let Deskbar restart: the ring is a full disc again, the
     chip says "Done", and the tooltip says "Running in this Deskbar (version)".
DR3. A done note, and a done task without a pull request, keep the full disc throughout.
DR4. A done task marked done while its pull request is still open: ring, chip "PR open"; merge it and within a minute
     the chip says "Merged".

Phase 2: a change to any other app fills its ring when a deploy is reported, since Deskbar cannot see that app's
servers.

DR5. Have a Claude session finish a task in another repository (its pull request recorded with `gh pr create`), merge
     it, deploy it, and let the session check the app's logs or health page and run `deskbar.cmd task deployed -
     --session <id>` with what it saw. Before the report the tick is a hollow ring, the chip says "Merged" and the
     tooltip says "Merged in <repo> #N, not reported deployed yet"; after it, the full disc, the chip says "Done" and the
     tooltip says "Deployed: <the session's note>". `deskbar.cmd task show <id>` lists the report under "deployed".
DR6. Another such task merged but not reported: its menu in the tasks panel ("⋯" or a right-click) and on the bar
     (right-click its line) has "Mark deployed". Pick it: the ring fills at once, the tooltip says "Deployed: Marked
     deployed by hand", and the item is gone from both menus. A done Deskbar change still waiting for the next release
     has no "Mark deployed".
DR7. A task with pull requests in Deskbar and in another repository: it keeps the ring until both hold (the Deskbar one
     in the copy you run, the other reported deployed), and the tooltip then says "Deployed: <note> Running in this
     Deskbar (version)." With `gh` signed out, a reported task still shows the full disc; an unreported one in another
     repository keeps the full disc too (unknown draws as before).
DR8. With several Done tasks under the questions, some full discs and some hollow rings (task a1debf34): on the bar,
     in the tasks panel's List and in each Matrix cell, the full discs come first and the rings after them, each half
     in the order you dragged it. When a ring fills (DR2, DR6) its line moves up among the discs. `deskbar.cmd task
     list` prints them in the same order.
DR9. A task that is not Done yet but whose pull request merged (task 1afbd44f), say one Waiting on you to redeploy in
     another repository: within a minute of the merge its line on the bar and in the tasks panel has the hollow ring
     in front of its title, next to its question, and its chip still says "Waiting". "Mark deployed" is on its menu;
     pick it and the ring fills into the disc. A task whose pull request is still open, or one still In progress with
     no pull request, has no mark at all, and so does a task reopened after it was Done (or a repeating task's
     next run) until it is Done again.

## The bar's waiting block folds after three

(Task 39f4cc8a.) A docked bar, the tasks panel closed, Subtasks ticked.

WG1. With six tasks waiting on you (questions or Done) and one important note: the bar shows three waiting lines,
     then one line "+3 more waiting on you", then the important note right under it. The group line is orange when a
     question is among the three it holds, grey when they are all Done. Hover it: the tooltip names the three.
WG2. Click the group line: the tasks panel opens, with the whole Waiting on you block at its top. Click the group
     line again: the panel stays open. "All tasks" still closes it.
WG3. With exactly four tasks waiting: all four show, no group line.
WG4. Bring to the front the Claude terminal of a task inside the group: its line appears in the block, selected, and
     the group line counts one fewer.
WG5. Tick one of the three waiting lines: it stays struck through in its place for six seconds, the group line does
     not move, and after the undo window the next waiting task takes the free place.
WG6. On a floating bar (three lines) with the same tasks: one waiting line, the group line "+5 more waiting on you",
     then the important note, all three in view without scrolling.
WG7. Select a task inside the group (WG4), then click another line: the first one folds back into the group at once.

## Working and idle Claude sessions

(Task 10b49554.) The tasks panel open on the List, with one Claude task whose session is working right now (give it a
prompt that takes a minute), one whose session sits idle at its prompt, and a note.

WI1. The working task sits under the "CLAUDE WORKING" caption, its session square glowing. Under it a dashed line
     captioned "idle" leads the idle task, drawn dimmer, with no glow. The note is in its quadrant below.
WI2. Let the working session finish its turn without asking anything. Within about half a minute its line moves under
     the "idle" divider and its glow goes. Give it a new prompt: within 15 seconds or so it moves back above the divider.
WI3. Have a session end its turn while a background job of its own runs on (a PR watch, or ask it to run
     `sleep 120` in the background and stop): it shows idle while it waits, and its thread lists the session as idle.
WI4. With no session working, the idle tasks have the ordinary caption "CLAUDE IDLE" instead of the divider.
WI5. The switch beside the status chips reads "Claude 2 | working 1 | idle 1". Click "working": only the working task
     stays, and the part is filled orange. Click "idle": only the idle task. Click "idle" again: everything is back and
     no part is filled. Turn "working" on, close and reopen the panel, then restart Deskbar: it is still on.
WI6. A status chip that is on ("Open" at first), a ticked "Subtasks" or "Current workspace", and the List / Matrix /
     Stats part on show are filled blue, not only ringed. The ones that are off have the plain grey fill.
WI7. Press "Start Claude on this" on a note: its line shows under "CLAUDE WORKING", glowing, within one poll (about 15
     seconds), not dimmed under "idle" for half a minute first.
WI8. With "working" on, select the working task and open its thread, then let its session finish its turn: the line and
     its thread stay on screen (the selected task is kept); select another task and it goes.
WI9. Drag an idle line and drop it where it was, or press Esc: it is still drawn dimmer. The Matrix keeps its old order
     and does not split working from idle (a compact line shows no sessions).

## Update offers once a day, critical ones at once

Task 5e1e1a12. Run on the GitHub copy with tracing on (`trace.on`), reading `update ...` lines in the trace log.

UC1. Manage → Settings: "Offer every new version as soon as it is out" sits under "Check GitHub for new versions",
     unticked, with the once-a-day note under it. The Store copy does not show it.
UC2. Untick it on a copy one release behind. The first check after start logs `StartClock` and nothing is offered;
     later checks log `Wait`. Set `LastUpdateOffer` in state.json to a time more than a day ago (Deskbar exited) and
     start it: the newest release is offered within 30 seconds, and the trace logs `Offer`.
UC3. Tick it: the next check (within five minutes) offers the newest release at once.
UC4. Merge a pull request labelled `critical-update`. Its release notes open with "Critical update." and carry the
     hidden marker (`gh release view vX --json body`); the next ordinary release's body carries the same marker. A
     copy below that release with the box unticked is offered the update at its next check, without the day.
UC5. "Check for updates…" from the tray finds the newest release whatever the day, box or no box.

## Add and start Claude

(Task 3c40dec9.) Claude sessions on, in a workspace whose tasks know exactly one folder.

AS1. Type a note into the bar's box. After the important and urgent toggles there is a play button, "Add and start
     Claude". Press it: the note is added, the box empties and the keyboard goes back to the window you were typing
     in. No Claude window opens and the desktop does not change (task 7b17a573): within a few seconds a small dark
     card in the bottom-right corner says "Claude started" with the task's title, keeps the keyboard where it was,
     stays while the pointer rests on it and fades after about five seconds. The task shows in the tasks panel's
     Claude block.
AS2. Type another note and press Ctrl+Enter instead: the same. Plain Enter only adds (and selects the new note).
AS3. The tasks panel, + Add: the same play button sits after the toggles, before the workspace chip, and Ctrl+Enter
     does the same. The box closes once the note is added, and the panel stays on the task and thread you had open.
AS3a. Click the "Claude started" card: the task's workspace comes to the front with its Claude window, as a
     double-click on the task's line does.
AS3b. Make a start fail (a workspace whose one folder is untrusted): Ctrl+Enter still tells you at once, with the
     trust offer, and no "Claude started" card shows.
AS3c. Two Ctrl+Enters in a row: one card, the newer task's. With two folders, pick one from the list: the keyboard
     goes back to the window you were typing in.
AS4. In a workspace whose tasks know several folders (or none), the button adds the note and opens the folder pick
     list at the pointer, as "Start Claude on this" does. Esc on the list leaves a plain note.
AS5a. (Task 3a872301.) Type `/` in the bar's box on a workspace whose tasks know this repo. A list opens under the box
     saying it is asking Claude Code, then, within a few seconds, the skills: `/refresh-pr` among them. Type `ref`: the
     list narrows to the skills that start with or contain it. Down, then Tab: the box reads `/<that skill> ` with the
     caret after the space. Type `245` and press Ctrl+Enter: the task "/refresh-pr 245" is added and a Claude session
     starts on it whose first message runs /refresh-pr.
AS5b. Type `/` again: the list now opens at once. Esc closes it and keeps the box's text; Esc again clears the box.
     Click a skill in the list: it goes into the box and the caret stays there. User-wide skills (/humanizer,
     /summary), plugins' (superpowers:...) and Claude Code's own (/simplify) are not in the list.
AS5c. On a workspace whose tasks know two repos, a repo's own skill shows that repo's name beside it, and starting a
     task titled with it starts in that repo without the folder list.
AS5d. The tasks panel, + Add: the same list under its box.
AS5e. (Task 29bdd158.) Open the thread of a Claude task whose session works in this repo and type `/` in its comment
     box: the same list opens under the box with this repo's own skills (`/refresh-pr`). Type `ref`, Tab: the box reads
     `/refresh-pr ` with the caret after the space. Enter while the list is open picks, not posts; type `245` and press
     Enter: the comment posts, and the session runs /refresh-pr with 245 (its next turn shows the Skill tool call).
     Esc with the list open closes only the list, even with the + Add box shown. A comment "/etc/hosts is wrong" goes
     as plain words. The task's own window (Open in window): `/` in its comment box shows the same list, Ctrl+Enter
     takes the pick and posts.
AS5. The button on an empty box adds nothing and starts nothing. With Claude sessions off in Manage the note is
     still added, and the same refusal "Start Claude on this" gives says why no session started.

## Add from the window on screen

(Task 494d34ee.) Claude sessions on, in a workspace whose tasks know exactly one folder.

AW1. Work in a browser window, then press the camera in the bar's add row, right after the important and urgent
     toggles. Without typing anything: a task called "Picture" is added to the workspace you are on, Claude starts on it
     in the background and the "Claude started" card shows. The keyboard goes back to the browser. Open the task: its
     description reads "Picture of the window "<the browser's title>" (msedge) on screen." with the picture under it,
     the browser window alone (no bar, no strip, no black border round it). The session renames the task after reading.
AW2. Type "Why is this red" in the box, then press the camera: the task is called "Why is this red", same picture.
AW3. A Notepad window (it has a classic frame): the picture has no black edge at its sides or bottom.
AW4. Switch to an empty desktop, click the desktop, press the camera: a message says there is no window on this desktop
     to take a picture of, and nothing is added. The keyboard is left as it was.
AW5. The tasks panel, + Add: the camera sits after the toggles, before the play button. Click your browser, then the
     panel's camera: the same task. With no window, the panel's problem line says so.
AW6. The bar in the Inbox look (panel on the Inbox): the camera sits after the toggles, drawn like an "off" toggle.

## Reevaluate

(Task 3dd07539.) The tasks panel open on the List, with a Claude task waiting on you whose question is already
settled (it asks you to merge a pull request that is merged), a Claude task in progress, and a note.

RE1. The waiting task's line has a refresh icon right after the orange Claude icon, tooltip "Reevaluate: ask Claude to
     check where this task really stands...". The task in progress has it too; the note has none, and no "⋯" menu has
     the item. Opening a task's thread does not add a second Reevaluate under the line.
RE2. Widen the panel past 850 DIP and narrow it to the least: the icon stays after the Claude icon at every width, and
     the Claude icons of all lines stay at one x. In the Matrix, an open Claude task's thread row has "Reevaluate".
RE3. Press it. The thread gets Deskbar's line "Asked Claude to reevaluate the task.", and within a minute the session
     runs `task answered` (the merged pull request settled the question): the task leaves Waiting on you, and its
     status line says where it stands. Its session's prompt starts "Deskbar: the user asked you to reevaluate".
RE4. Do the same on a task whose session has ended: it is resumed with the request, as a comment would resume it.
RE5. The bar's right-click menu of a Claude task has "Reevaluate" after "Add subtask…" (a bar line has no thread to
     open); a note's has none.
RE6. `deskbar.cmd task reevaluate <note id>` is refused ("no Claude session has worked on this task..."), and nothing
     changes on the note.
RE7. (Task c980f8c0.) Switch to the Inbox. Point at the state mark at the left of the waiting task's line: the tooltip
     starts with the state ("Asks you") and says a click asks Claude to check it. Click it: the thread gets "Asked Claude
     to reevaluate the task." and the page does not open. The note's box and a finished task's tick still close.
RE8. Open the waiting task's page. The mark at the top and the state words under the title ("Waits for your answer")
     each do the same on a click, with a faint fill on hover; the words stay where they were before the change. On a
     task waiting for your deploy the Merged, Built, Deploy, Running track is the button.
RE9. (Task 4712d013.) Take a Claude task whose open pull request is behind main (merge something else first) and
     click its state mark. Within a few minutes the session rebases the branch and pushes it ("force-with-lease" in its
     terminal), and GitHub no longer says the branch is out of date. On a task whose pull request has a failed check,
     the session reads the failed run and pushes a fix. A task whose pull request is green and up to date only gets its
     question checked: nothing is pushed.

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

## The workspace chip moves a task

(Task 3757a3e1.) The tasks panel open, a note on some workspace.

WC1. Point at the note's workspace chip: it dims a little and its tooltip reads "<workspace>: move it to another
     workspace". Click it: the compact workspace menu opens under the chip, the task's own workspace outlined, the same
     menu the line's Move to button opens. Pick another workspace: the note moves there and its chip changes.
WC2. The click on the chip does not open or select the task (the line's thread stays as it was).
WC3. Close the note with its tick: during the six seconds of Undo the chip is a plain label again, and a click on it
     opens nothing.
WC4. Open the note in its own window (Open in window): the workspace chip in the header does the same, the menu under
     the chip, and a pick moves the task.

## Wide panel title room (task 39d58e5b)

WT1. Set the tasks panel to about 720 DIP wide (the width of the "wider renders worse" screenshot): the lines are
     slim, each title across the whole line and its controls on the row under it, as on a narrow panel.
WT2. Widen it to the most it goes (on a 2560 pixel screen at 150%, 853 DIP): the full lines and the header come back.
     A title of about 80 characters takes two lines, no more; the buttons at each line's end are down to ⋯ and
     perhaps one more. Narrow it back under 850: slim again at the grip's drag end.
WT3. On a full line, drag a column's left divider far to the left: the column grows until the line's end is down to ⋯
     and the title is at about 320 DIP, then stops. It never squeezes the title further.

## A Done tick waits for GitHub

(Task 8db4c78e.) A Claude task with a pull request recorded on it, in this repository.

DT1. Once the pull request merges, mark the task Done (or let its session do it). From that moment the tick is the hollow
     green ring with the chip reading "Done" outlined; its tooltip says Deskbar is checking with GitHub. It never shows
     the full green disc first. Within about a minute the chip turns "Merged" (the copy you run does not hold the merge
     yet), and after you update to the release that holds it, the disc fills.
DT2. Restart Deskbar with several Done tasks that have pull requests: they show rings at first and each one settles on
     its own within a second or two of the next, rather than all at once at the end.
DT3. With gh signed out (`gh auth logout`, then log back in afterwards), restart: those Done tasks keep the full tick, as
     before this change, since Deskbar cannot ask.

## The bar's top and the strips are frosted

(Task d52ca36e.) See-through on in Manage, a picture wallpaper laid Fill, the window list on the screens.

BT1. Docked bar: the top (Start, search, pinned apps) shows the blurred wallpaper under the same veil as the idle rows,
     with no seam between the top and the first row. The footer stays flat.
BT2. Undock the bar and drag it: the top frosts with the rows and slides with them; the bar's rounded top corners stay
     clean, and docked again its top corners are square with no sliver of sharp picture in them.
BT3. Every screen's strip shows its own screen's wallpaper, blurred, under the veil; tabs, the name tag and the active
     tab's fill read as before. With tracing on, each strip writes one `strip frost:` line.
BT4. Change the wallpaper: within a second or two the strips and the bar show the new one. Move Manage's blur slider:
     both follow it.
BT5. Untick see-through in Manage: the strips and the bar's top go flat at once; tick it again and the frost returns.

## Quick find keeps up with typing

(Task 684210af.) Several Claude sessions running, so the app's UI thread is busy, and tracing on.

QT1. Press `Win+Alt+Space` and type `teams` straight away, quickly: every letter shows in the box as it is typed, the
     count beside the box reads `searching...` while the results trail the text, and the results for `teams` follow.
QT2. Type a word and press Enter at once, before the results move: Enter takes the top row for what is in the box.
QT3. Leave the box open with text in it for a few seconds, then type: no letter waits about a second before showing.
QT4. A search that took 100 ms or more writes a `quick find:` line to the trace naming the catalogue, ranking and draw
     times and how long it waited after the key.

## The question is in the chat

(Task 968c2216.) The tasks panel open on the List, and a Claude session on a task of its own.

QC1. Have the session run `task ask`: the task waits on you with the question under its title, in orange, and its
     comment count goes up by one.
QC2. Click the line to open its thread: the question leaves the line, and the thread's last message is the session's,
     the question, in orange. Click the line again to fold it: the question is back under the title.
QC3. Open the task in its own window (Open in window): the thread holds the question in orange, and the box at the top
     has no question in it (a task with no live session still says "no live session" there).
QC4. Have the session ask the same question again: no second copy in the thread. Ask a different one: a new message,
     the new question orange, the old one in the plain text colour.
QC5. Reply, then have the session run `task answered`: the question folds away under "N settled comments" with the
     session's other comments since it asked. On the task's Inbox page they stay where they were, dimmed (task 5ee182cf).

## Workspace bookmarks

(Task f7ac687c.) A named workspace with a few windows, a second workspace to start from, Portainer reachable on your
network, RustDesk installed, and the iam.ge PWA installed in Edge.

BM1. Drag the address-bar URL of a Portainer page onto the workspace's row: the readout says "→ bookmark in <workspace>"
     while you hold it over the row. Drop: a chip appears after the windows, named by the page title, faded, with a
     dashed outline and a small dot on its icon.
BM2. Point at the chip: it comes to full strength and the tooltip gives the name and the URL.
BM3. From another workspace, click the chip: Deskbar switches to the bookmark's workspace and the page opens in a
     browser window on that desktop.
BM4. Drop `rustdesk.exe` on the row: a chip named "RustDesk". Click it: RustDesk starts in that workspace. While its
     window is open the chip is hidden; close it to the tray and the chip comes back.
BM5. Drop the Start menu shortcut (.lnk) of a Store app or a PWA on the row: an app chip, named after the shortcut.
BM6. Right-click the iam.ge PWA window and pick "Bookmark in this workspace": an app chip in that workspace. It is hidden
     while the PWA is open, also when Edge was already running with other windows, and shows again when the PWA closes.
BM7. With RustDesk running, right-click the row and pick "Open all bookmarks": one switch, the links open, RustDesk is not
     started a second time. The item is not on the menu of a workspace with no bookmarks.
BM8. Right-click a chip: Open, Open all bookmarks, Rename… and Remove. Rename… changes the chip's name and tooltip;
     Remove takes the chip away, with no confirmation.
BM9. Fold the row: no chips. Unfold it. In a workspace with no windows and some bookmarks, the row shows only the chips.
BM10. Copy a web address: the row menu's "Bookmark link from clipboard" is enabled and adds a chip named after the host.
     Copy plain text: the item is disabled. Run it twice with the same address: the second is refused with a message.
BM11. The Portainer chip shows its favicon after a moment (the site has a self-signed certificate); a site with no
     favicon keeps its letter tile. Restart Deskbar with the network off: icons already fetched stay, the others are
     letter tiles, and nothing breaks. Switch the network on and restart: the missing icons arrive.
BM12. `deskbar.cmd bookmark list Work`, then `add Work --url <url>`, `open`, `open-all` and `remove`: each does what the
     menu item of the same name does, and `list` shows the change.
BM13. Give a workspace two link chips and have a browser window open on that workspace (on any screen). Click the first
     chip: the link opens as a tab in that browser window and, within 5 seconds, the chip turns bright with a solid
     outline while the second stays faded. `deskbar.cmd bookmark list <workspace>` shows `"running": true` for the first
     link only. Close only the tab: the chip stays bright. Click it again: the link opens again. Close the browser window:
     the chip goes back to faded within a few seconds, and `list` says `"running": false`. Now, with no browser window on
     that workspace, use "Open all bookmarks": the browser opens a window there and both chips turn bright. Restart
     Deskbar: every link chip starts faded.
BM14. Bookmark Portainer (which answers 404 for /favicon.ico and names its logo in the page), delete its file under
     %APPDATA%\Deskbar\bookmark-icons and restart Deskbar with tracing on: the chip shows Portainer's logo after a moment,
     and the trace has a `bookmark favicon` line that failed, then one marked `(declared)` that says ok. A site whose page
     names an icon on another host keeps its letter tile.

The next steps need two screens, the window list on the screens (the strips showing) and a pinned workspace or the
previous workspace's item on the strips.

BM15. In a workspace with a link chip and a RustDesk chip, look at both strips: the chips are there on each, after the
     window tabs and just before the jump items, faded with a dashed outline. Click the link chip on a strip: it opens as
     from the row. Right-click it: the chip menu. Switch to a workspace with no bookmarks: no chips on the strips.
BM16. Drag the RustDesk chip into the second screen's half of its own row: the half lights up and the readout says
     "→ put on screen 2". Drop: the chip moves to the second screen's part of the row, and on the strips it is now only
     on screen 2's strip. `deskbar.cmd bookmark list <workspace>` shows `"screen": 2`.
BM17. Click the RustDesk chip with RustDesk closed: its window opens on screen 2, and stays there even if RustDesk puts
     itself back where it was last time (with tracing on, a `drifted ... moving it back` line says when that happened).
     A quick click on a chip that slips a few pixels still opens it rather than moving it, and so does a chip dragged
     away and dropped back on itself: a drag that ends where it began is a click.
BM18. Drag the link chip into the other screen's half of its own row: the readout says a link keeps its browser's screen,
     and the drop changes nothing.
BM19. Drag the link chip onto another workspace's row: the readout says "→ move to <workspace>", and the drop moves the
     chip there. Drag it back onto a row that already has the same link: the readout says it can't move, and nothing
     changes.
BM20. `deskbar.cmd bookmark move <workspace> RustDesk --any-screen`: the chip goes back to the first part of the row and
     onto every strip. With the second screen unplugged, a chip that remembers screen 2 sits in the first part and opens
     on whatever screen the window picks.
BM21. Right-click a chip and pick Colour, then a swatch: the chip's outline and a small bar under its icon take the
     colour; the icon itself (a letter tile included) does not change. Custom… opens Windows' colour picker. Default
     clears it. `deskbar.cmd bookmark color <workspace> RustDesk #2E86DE` does the same, and `list` shows the colour.
     Open a coloured link: its open backing is a tint of its colour; an open link with no colour has the faint white one.
BM22. Bookmark two Portainer instances (100.65.0.11 and 100.65.0.8) in one workspace: both chips show the Portainer
     logo, and each wears the small coloured underline that repeated windows wear (sky, then amber), with no other
     change. Give one of them a colour: it keeps its own colour and the other loses its underline. A chip whose icon is
     unique in its row has no underline. Restart Deskbar: the same underlines come back, and state.json holds none.
BM23. Bookmark a Store app or a PWA, drag its chip to screen 2 and click it with the app closed: its window opens on
     screen 2, also when the app names its window a moment after showing it. Open the same app by hand ten seconds or more
     after the click: that window stays where it opens.

## Comments show whole

Task f0168cdd. A comment in the tasks panel's thread shows whole unless it is super long.

CW1. Open the thread of a task with a manager note of about ten lines (a resume message): the whole note shows, with no
     "Show more" and no grip band under it.
CW2. Comment twenty paragraphs on a task (`deskbar.cmd task comment` with a long heredoc): the thread shows its first
     six lines, then "Show more" and the grip. Show more opens it whole; Show less folds it back to six lines.
CW3. Narrow the panel with its width grip until a twelve-line comment wraps past fifteen lines: it folds to six with
     "Show more". Widen it again: it shows whole and the button goes.

## Recurring tasks

(Task [Deskbar task 0b78269e](https://deskbar.app/task#0b78269e-3365-4401-9d2d-635512282d7a).) A note task whose description says to comment the current time and then call it done, in a folder that is a git repo. Tracing is not needed.

RT1. On the note's "⋯" menu in the tasks panel pick Repeat, then "Every hour" (when the task's workspace knows several folders, a list opens: pick the repo): the line leaves the panel's lists, shows under the Snoozed chip with the repeat mark, and the mark's tooltip reads `Repeats every hour · next run <time> · sonnet`.
RT2. Pick "Wake now": a run starts with no window and no change of focus, and the line shows Claude working.
RT3. The run comments the time and calls the task done. The line goes back under Snoozed, and the next run is an hour on.
RT4. About a minute after the run goes idle its session is gone from `claude agents`.
RT5. Wake again, open the run's window while it works, and wake again once it is idle: the run is skipped while the window is open, and the session is not stopped under you. The skip shows as `recurring: <id> not started again` in the trace log (with `%APPDATA%\Deskbar\trace.on`), or simply no second session appears in `claude agents`.
RT6. After RT4 (the last run's session is gone from `claude agents`), post a comment on the task: a fresh run starts and its answer reads the comment. A comment posted before that goes to the last run's session instead.
RT7. Rename the task's folder, then Wake now: the task asks once, and no further question comes at the next hour.
RT8. Put the folder back, then run `deskbar.cmd task repeat <id> off` between runs: the task becomes Done, and closing it ends the job. Run `deskbar.cmd task repeat <id> off` on a task that is mid-run: only the schedule goes, the run finishes.
RT9. Set Custom… to `daily HH:MM` two minutes ahead: it runs at that time without a window.
RT10. Close the task, wait past a due time, reopen it: nothing ran while it was closed, and the next run counts from the reopening.

Test builds (Deskbar task 7dd83c89, https://deskbar.app/task#7dd83c89-2f70-4852-bbed-86d356d4472f):

TB1. Start a Debug build from a worktree (with `DESKBAR_APPDATA` set to a scratch folder): the tray tooltip reads "Deskbar Test build <version> (<worktree>)", and the tray menu and the bar's background menu both start with a greyed "Test build <version> (<worktree>), gets no updates" line.
TB2. With a `Deskbar-<x.y.z>-win-x64.exe` in `%LOCALAPPDATA%\Programs\Deskbar`, the line under it is "Restart into Deskbar <x.y.z>": clicking it starts that release, which takes over the bar against the REAL profile (no scratch `DESKBAR_APPDATA` carried over) and, from Process Explorer, with its own folder as its working directory. Do this one only when Petre has asked to go back to his release.
TB3. Check for updates… in a test build asks to restart into the installed release instead of saying it is the latest version. With no release installed, the line under the marking is missing and Check for updates… offers the releases page.
TB4. A release (single-file exe) and the Store copy show no test-build line and their usual tooltip.

The list holds still while you are in a thread ([Deskbar task 106bc422](https://deskbar.app/task#106bc422-eb9b-46ea-9ea3-6dbc3a0ab9fe)). Two throwaway notes, A and B, on one workspace, the tasks panel open.

LH1. Open A's comments and keep moving the pointer over the panel. From a terminal run `deskbar.cmd task close <B>`: B's line stays where it was, drawn closed, and A does not move. Close A's comments: B leaves.
LH2. Reopen B (`deskbar.cmd task reopen <B>`), start Claude on it and tell its session to ask you something with `task ask`. While it works, open A's comments and keep touching the panel: when the question comes, B keeps its place with the question under its title. Leave the panel alone for one to two minutes: B moves to Waiting on you.
LH3. Open A's comments, touch the panel, then `deskbar.cmd task add -` a note: it does not show yet. Type a note in the panel's **+ Add** box and press Enter: both notes show.
LH4. With no comments open, run `deskbar.cmd task close <A>`: A's line leaves at once, as before.
LH5. Ticked lines stay while you go through the list ([Deskbar task af38579b](https://deskbar.app/task#af38579b-82b3-487a-9538-8befdce2df0b)). Three throwaway notes, no comments open. Close the first with its close button, wait ten seconds while moving the pointer over the panel, then close the second: the first is still there, struck through with Undo, and nothing has moved up. Press Undo on the first: it comes back. Leave the panel alone for one to two minutes: the second leaves and the list closes up once. Close the third, keep the pointer moving over the panel for ten seconds, then hide the panel: on the next open the third is gone.

Deskbar watches the pull requests of tasks in flight ([Deskbar task c8e6967d](https://deskbar.app/task#c8e6967d), spec `docs/superpowers/specs/2026-11-27-repo-watcher-design.md`). A throwaway repository with `gh` signed in, a Claude task with a session and an open pull request on it, the trace on (`%APPDATA%\Deskbar\trace.on`).

RW1. Merge another pull request into the base branch (or push to it) while yours is open: within about two minutes the task's session is nudged ("The base main of ... moved to ..."), rebases and pushes. No line is added to the task's thread.
RW2. Make the pull request conflict with its base (change the same line on the base): the session is nudged once ("... conflicts with main"), and not again each minute until the base moves again.
RW3. Merge the pull request: the session is nudged that it merged and that Deskbar follows the runs on its merge commit, and the session starts no loop of its own.
RW4. Let the merge commit's runs finish green: the session is nudged with the runs' names and conclusions. Make one run fail instead (re-run on a repository whose workflow fails): the session is nudged with the run's link, and on a day the manager is open it is woken with a "Failed runs on merge commits" line.
RW5. Sign `gh` out (`gh auth logout`) and wait six minutes: a new prompt in the task's session no longer says Deskbar watches its pull request, and the session watches for itself with its own Monitor. Sign in again: within about a minute of the next poll the prompt line says Deskbar watches again.

## Permission cards for every session

The Allow / Deny card comes from any session, not only a background one ([Deskbar task 6306d261](https://deskbar.app/task#6306d261-cdee-4830-b077-2b44ee656a86)). "Answer Claude's permission questions in Deskbar" ticked on Manage → Settings, the Inbox view on, a throwaway note with a Claude session in Claude Code's default permission mode.

PA1. In a plain terminal, `claude` in a scratch folder, joined to the note; ask it to run `mkdir pa1`. The terminal shows its own dialog at once, and the note's page shows the card with the command. Click **Allow**: the folder is made and the terminal's dialog closes.
PA2. Same again with `mkdir pa2`, but answer **Yes** in the terminal: the folder is made, and the card leaves the page within about five seconds, while the session is still working.
PA3. Same in a VS Code Claude tab with `mkdir pa3`: the card shows beside VS Code's own prompt; **Deny** refuses the command and the session says so.
PA4. A background session (`claude --bg`) still behaves as before: card, Allow, and answering in the attached terminal takes the card off.

## Claude's multiple-choice questions on the task's page (task c047d0a0)

With "Answer Claude's permission questions in Deskbar" ticked and the Inbox view on:

DQ1. Ask a session with a task to put a two-question choice to you through its dialog. Within a few seconds the task's page shows a "CLAUDE ASKS YOU" card with both questions, their headers, options, descriptions and the Recommended tag. The line stays under Needs you, not "Asks permission".
DQ2. Send answers is off until both questions have a pick. Pick both and Send: the terminal's dialog closes on its own, the session goes on with your picks, the card leaves, and the chat gets "Answered Claude's question:" with both picks.
DQ3. Type in Other for one question and Send: the session reads your words as that answer.
DQ4. Open another dialog and write a reply in the page's comment box instead: it answers the dialog, nothing is posted as a comment, and the chat line shows your words.
DQ5. Open another dialog and answer it in the terminal: the card leaves within a few seconds and the chat still gets the "Answered" line.
DQ6. Answer in the terminal on the card: the card goes and the terminal's dialog still waits for you.
DQ7. A session in VS Code: the card shows, and answering it closes VS Code's dialog. (Not measured yet.)
PL1. Start a session on a task in plan mode (`claude --permission-mode plan`, or Shift+Tab to plan mode) and ask for a small change. When it asks for the plan's approval, the task's page shows a "CLAUDE'S PLAN, FOR YOUR APPROVAL" card with the whole plan as formatted text, a note box, and Approve, Approve and accept edits, Keep planning (off) and Answer in the terminal. About six seconds later the task is under Needs you with "Approve Claude's plan: " and the plan's first line, not under "Asks permission".
PL2. Approve: the terminal's dialog closes on its own, the session starts on the plan and asks before its first edit (an Allow / Deny card), the plan card leaves, and the chat gets "Approved Claude's plan."
PL3. Another plan, Approve and accept edits: the session edits without asking, and the chat gets "Approved Claude's plan, with edits accepted from now on."
PL4. Another plan: type a note (Keep planning turns on) and press Keep planning. The session reworks the plan as the note says and a new plan card appears; the chat gets "Asked Claude to keep planning:" with the note.
PL5. Another plan, answered in the terminal: the card leaves within a few seconds and the task's question settles.
PL6. Answer in the terminal on the card: the card goes and the terminal's dialog still waits.
PL7. Type half a note, switch to another task and back: the note is still in the box.

## Click to copy a command

A command in a comment or chat message copies whole on a click ([Deskbar task b6af9445](https://deskbar.app/task#b6af9445-56f0-42ea-9df1-f9ee58a89179)). A throwaway note with a comment holding `` `docker logs some-very-long-container-name-that-wraps` ``, a fenced block of two lines and "Pull and redeploy" in double quotes.

CC1. In the tasks panel's thread, point at the inline code: the pointer is a hand and "Click to copy" shows. Click it: the code flashes green and Ctrl+V in Notepad pastes the whole command, with no line break where it wrapped.
CC2. Click the fenced block: both lines paste, as written. Click "Pull and redeploy": the words paste without the quote marks.
CC3. The same in the Inbox chat and in the task's own window. A link in the comment still opens on a click, and dragging from the plain text beside the code still selects text.
CC4. A task line's question or status line with code in it: a click picks the task, as before, and copies nothing.
CC5. (Task f39f5db3.) Click the inline code: a small "Copied" note shows just under the pointer and goes after about a second. Click a second command straight away: the one note moves to the new click, and there are never two. Do it on the floating bar's tasks and in the panel where it meets the bar: the note stays above the bar.

## Claude Code's dialogs ask through the task

Every dialog a session stops on becomes its task's question ([Deskbar task b310a649](https://deskbar.app/task#b310a649-1748-4bfe-b062-88378aa7e808)). Sessions keep their tasks, "Answer Claude's permission questions in Deskbar" off, a throwaway note joined by a `claude` session in a scratch folder, in Claude Code's default permission mode.

SP1. Ask the session to run `mkdir sp1` and leave the dialog alone. After about six seconds the note moves to Waiting on you with "Allow Bash in the session: mkdir sp1". Answer Yes in the terminal: within about five seconds the note is back in progress.
SP2. Same with `mkdir sp2`, but answer at once: the note never leaves in progress.
SP3. `/plan`, ask for a small plan, and leave the approval dialog: "Approve the plan in the session: " and the plan's first line. Reject it with Esc: the question goes within about five seconds.
SP4. Ask the session to use AskUserQuestion, then press Esc on the dialog (step 68a): the question goes within about five seconds.
SP5. `task ask` something on the note from the session, then trigger a permission dialog: the asked question stays, and stays after the dialog is answered.
SP6. The same as SP1 in a VS Code Claude tab: the question names the tool, and answering in VS Code clears it.

## The Inbox page keeps its whole chat

Settled and older comments stay in the page's chat ([Deskbar task 5ee182cf](https://deskbar.app/task#5ee182cf-1c44-4560-94e5-1080e28f3d5d)). A note joined by a throwaway session in a scratch folder, the Inbox chosen.

PW1. Have the session write five comments, then `task ask` and `task answered` after your reply. Open the note's page: every comment is in the chat, the settled ones dimmed in their places, with no "N settled comments" or "N earlier comments" line, and the session's steps from before the oldest comment are still there.
PW2. Keep the page open and have the session settle one more comment (`task comment-settle <id>`): it dims where it is, and nothing above it disappears.
PW3. Back to the List and open the same note's thread: there the settled comments fold behind "N settled comments" and only the latest three show, as before.
PW4. Open the page of a task with one or two comments: the chat sits at the bottom, its newest message just above the comment box at the bottom edge, and the empty room is between the header and the chat (task 6da6758f, which undid the top start). Add comments until it is taller than the page: it scrolls and stays at the newest.

## A deploy hand-over is not a question

A session whose change is merged and built, with only your deploy left, hands it over with `task ask - --deploy` ([Deskbar task a87891ca](https://deskbar.app/task#a87891ca-2dea-422a-b40e-a3a534144380)). A throwaway note joined by a session, with any merged pull request registered on it (`task pr <url> --session <id>`).

DH1. From the session, `task ask - --deploy` with "Redeploy the test stack". The note goes to the top of Waiting on you with the waiting-for-deploy mark, not the speech bubble, and the List's chip says "Deploy", outlined.
DH2. `task deployed -` with "seen in the logs" from the session: the note leaves Waiting on you and goes back to in progress at once, with no `task answered`.
DH3. A plain `task ask` whose text says "redeploy": the command's reply carries a `handOverHint`.
DH4. Ask with `--deploy`, reply in the comment box so the question is set aside, and let the session stop: its nudge names `task ask - --deploy`, and a question restored without a new ask still shows the deploy mark.

## A deploy the session cannot check

A session that cannot see where its change runs says so with `task deployed - --unknown`, the reason on stdin ([Deskbar task 485ba96b](https://deskbar.app/task#485ba96b-031d-4549-946e-ada7fc6c1d99)). A throwaway note joined by a session, with a merged pull request in a repository other than Deskbar registered on it (`task pr <url> --session <id>`), and the note marked done.

DU1. From the session, `task deployed - --unknown` with "no access to the test server's logs". The bar's tick turns into a dashed ring, the List's chip says "Can't check" in a dashed grey pill, and the Inbox line has the dashed ring with "Can't check the deploy: no access to the test server's logs" as its text.
DU2. Open the note's page: the header says "Finished. Can't check the deploy", the track reads Merged › Deploy: can't check (the last step dashed), and the reason sits under it with "Check it yourself and close the task, or use Mark deployed."
DU3. "Mark deployed" on the line's menu: the dashed ring fills to the disc and the chip says "Done".
DU4. `task deployed --unknown` with no text, from the session: refused with "needs the reason", and nothing is stored.

## The manager opens as an Inbox page

The Manager button and the manager's violet line open the manager inside the tasks panel, like a task's Inbox page ([Deskbar task 6c4c067f](https://deskbar.app/task#6c4c067f-e0f8-4853-831a-d86150aaa423)). Claude sessions on, the Inbox chosen.

MI1. Click Manager on the bar: the panel comes forward on the Inbox with the manager's page (violet tile, "Manager", the state under it, a box at the foot reading "Message the manager…"). No terminal window opens, and the manager is started or resumed as before.
MI2. Type a message and press Enter: your bubble shows at once and the box empties; the manager's reply arrives in the chat as it would on a task's page. No "Manager answered" line appears while the page shows.
MI3. Open a task's page, then click Manager and ask "is the current task stuck?": the manager names that task. The back arrow returns to the task's page; from the list, back returns to the list where it was scrolled.
MI4. Press Esc with the keyboard in the box: the page stays. Click outside the box and press Esc: back.
MI5. The Claude button left of the manager's box (the one a task's page has there; no "⋯" at the top any more): the manager's terminal opens docked beside the panel, as the button did before. `deskbar.cmd task manager` opens that terminal too.
MI6. Close the manager's session (`claude stop <id>`), then send a message from the page: it is resumed with your message, and your bubble shows once, not twice.

## A parent's page shows what keeps it open

A finished task's page names its open subtasks at the top, and a refused close says why there too ([Deskbar task 2106492b](https://deskbar.app/task#2106492b-8fb7-4fd1-ad56-e605c33099b6)). Two notes, one a subtask of the other, the parent marked done, the Inbox chosen.

CB1. Open the parent's page from the Finished tab and give it enough comments to scroll: under the state line, in the header that does not scroll, an orange-edged block reads "OPEN SUBTASK" with "↳ <subtask> · <status>" under it.
CB2. Click Close task: the task stays, and the block now starts with "Close its open subtask first: "<subtask>"" in orange. The list's line behind the page carries the same words, and the page's body does not repeat them.
CB3. Click the subtask in the block: its own page opens, and Back returns to the parent's page.
CB4. Give the parent four open subtasks: the block lists three and "and 1 more, in the thread below".
CB5. Open the parent in its own window (Open in window) and press Close task there: the words show on the window's line above the page, once, and the subtasks under the state line.
