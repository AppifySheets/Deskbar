---
title: Claude Code sessions
summary: Run many Claude Code sessions side by side, a workspace each, and see on the bar which one has finished or is waiting for you.
order: 1
clip: claude-many
---

Give each Claude Code session a workspace of its own, and the bar shows every one of them at once: which session has finished, which one is waiting for a permission, and one chord or one click to get there.

## How to use it

Supervising coding agents is not one task in one editor. Each agent you run wants its own editor window, its own terminal and its own browser tabs, often its own running copy of the thing under test. A workspace is a real Windows virtual desktop, so one per session keeps each cluster of windows together and off the taskbar of the others. Related sessions can share a group, for example two agents working on one project.

When a session finishes a reply, or waits for a permission, while you are on another workspace, that workspace's name and colour rail flash orange on the bar and the session's VS Code icon glows. The row stops once you go there. The window's border then blinks orange for five seconds so you can see which one asked, and the glow ends with it, or as soon as you click or type in that window. A window whose taskbar button flashed, a chat message for example, blinks the same way.

Deskbar finds the session's window by its folder. When several VS Code windows show the same folder, say one repo open in a few worktree sessions, it picks the one whose title starts with the session's name, which is there while the session's Claude tab is the active tab in its window. If no title says, the row still blinks but no icon glows, rather than lighting the wrong one.

To go there, click the workspace's row on the bar, or hold `Win+Ctrl` and tap `Tab`. The chord walks your workspaces in most-recently-used order, so when you are alternating between two sessions, one tap takes you back to the other. You land in the window you were last using in that workspace, so you arrive typing.

Quick find (`Win+Alt+Space`) searches every VS Code editor tab on every workspace, and an open Claude Code session is one of them, because each session is a tab named after the session. Type a few letters of its name and press Enter.

Links and windows a session opens land in that session's workspace, not on the one you are looking at. Deskbar's skill tells every Claude Code session on the machine to open links with `open`, start programs with `start` and move the windows it opens with `move --no-follow`. Apart from the skill, a window opened by another app joins that app's workspace, so a link you click in the session's editor opens its browser window beside the editor.

Seven VS Code windows all named `VSC` tell you nothing. **Name *app* windows by folder** on a VS Code icon's menu names each window after the folder, project or session it holds instead, and follows the window when it opens something else.

## Each session keeps a task

Every Claude Code session works on a task in the bar's [task list](tasks.md), so the list shows what each one is doing without you switching to it.

- When a session starts, Deskbar tells it the open tasks on its workspace that are Claude's to take (a note you assigned to Claude in the [tasks window](tasks.md#the-tasks-window), or a task a session had before) and Claude tasks on no workspace. Once it knows what it is working on, it joins the task its work continues or creates one. A session in a folder no VS Code window shows makes its task on no workspace. Your own notes on no workspace are never offered to a session, and neither is a note on its workspace that you left assigned to yourself.
- A session that joins one of your notes makes it Claude's work: the line turns orange, and the session can mark it done.
- The session keeps a short status line under the task's title, such as "waiting for CI on #398".
- A task you wrote a description for (Edit in the [tasks window](tasks.md#the-tasks-window)) shows the first part of it in the briefing, so a note you hand to Claude can carry its brief. The description is yours: a session never writes it, and its own status line is the only line it keeps.
- When it needs a decision from you, it asks through its task. The task moves to the top as "Waiting on you" with the question, and the workspace blinks. The session itself says when the question is settled, with `task answered`: it runs that once your reply settles the question, or once it settles it itself, and the task goes back to in progress, the blink stops and the task leaves the bar's list. Your message alone does not do it, so a `/loop` or scheduled check-in cannot answer for you; while the question is open, each prompt reminds the session of it. The bar lists only the tasks assigned to you, and a session's task is yours exactly while it is waiting on you or done (`task ask` and `task done` hand it to you, `task answered` and your next message after it was done hand it back to Claude). It stays in the tasks window, and comes back to the top of the bar when the session asks again. Switching to the session's window or its workspace does not take it off.
- Settling a question with `task answered` also settles the comments the session wrote since it asked: they stay, folded away in the tasks window behind a "N settled comments" line. A session can settle one of its own comments with `task comment-settle <comment-id>`.
- Sessions are taught to keep a task lean: say what needs you, and nothing else. The title is a few words, the status line one short phrase, and a comment is rare, for what needs your attention or a result you must know (a PR to merge, a decision made for you, a blocker) in a line or two, with no progress logs or findings dumps. It marks the task done when it has clearly finished. Only you close a task. A message to a session whose task is done takes it back to in progress. If you close a session's task, its next prompt tells it so, and new work needs a new task.
- On the bar a Claude task is told from your notes by its orange edge alone: it has no VS Code icon, and its title starts right after the tick box, as a note's does.
- A resumed session keeps its task as it was; a session that ends leaves its task open, with "no live session".
- Sessions run without anyone at them, such as `claude -p`, Agent SDK runs and scheduled agents, are told nothing and make no tasks.

A finished reply or a permission prompt still only blinks the workspace; it does not change the task.

A session can also add a note for later to its own workspace, and a script can add one with `task add`.

## Going to a session, and starting one

Double-click a task's line, on the bar or in the tasks window, and you go to its workspace and then to its session:

- A background session (one started with `claude --bg`, by Deskbar or by you) gets a Windows Terminal window attached to it with `claude attach`, titled after the task with "· Claude" on the end and in Deskbar's own terminal look. If that window is already open, it comes to the front instead: only one window can be attached to a session at a time, and a second one would take the session from the first.
- A session you started in VS Code or a terminal brings its VS Code window to the front.
- A session that has ended is resumed first, then its window opens.
- A note with no session just takes you to its workspace. A double-click never starts a session.

Closing the terminal window only detaches it. The session keeps running, and the next double-click opens a new window on it.

The window uses a Windows Terminal profile of Deskbar's own, "Deskbar Claude": VS Code's Dark Modern colours, JetBrains Mono (Cascadia Mono where that is not installed), Claude's orange bar cursor and no scrollbar. Deskbar installs it while **Claude Code sessions keep their tasks in the task list** is ticked and removes it when you untick that box or **Teach Claude Code about Deskbar** above it.

This needs the Claude Code CLI on the PATH (the native install puts `claude.exe` in `%USERPROFILE%\.local\bin`) and Windows Terminal. Without them the items are hidden or say why they cannot work: a start or a window that fails tells you in a message box as well as in the task's thread. Claude is not started on a closed task.

To hand a task to Claude, pick **Start Claude on this** on its menu (right after Edit, on the bar and in the tasks window) or the button of that name in its thread. Deskbar starts a background session with the task as its first message, so it gets to work at once, joins it to the task, and opens a window on it on the task's workspace. The folder is the repo of the task's latest session. A task without sessions offers the repos its workspace knows, from its other tasks' sessions and its VS Code windows: with one, it starts there; with several or none, a short list at the pointer ends with "Choose a folder…". On a task whose session is still running it asks first, since a second session on one task is rarely what you meant.

A folder Claude Code has never been told to trust cannot start a background session. Claude's message goes into the task's thread, and Deskbar offers to open a terminal running `claude` in that folder so you can accept the trust prompt yourself. Deskbar never accepts it for you.

A task's thread lists its sessions, newest first, with when each joined and whether it is working, idle, ended or expired. Open on a line does what the double-click does for that session. An expired session is one whose transcript Claude Code has deleted (after 30 days by default), so it has no Open.

## Tasks keep their sessions running

A task that is open (backlog, in progress or waiting on you) is meant to have a session running. A reboot, a sign-out, a crash of Claude Code's background daemon or a closed VS Code session ends sessions, and Deskbar resumes the ones on open tasks, one every five seconds, a cut-off turn told to continue. Each resume adds a line to the task's thread.

Deskbar waits two minutes first. Nothing is resumed in the first two minutes after Deskbar starts, and a session that has gone from Claude Code's list is resumed only once it has been gone for two minutes. VS Code brings its Claude panels back by itself after a restart, an update or a window reload, and a resume in that gap would start a second copy beside the one VS Code reopens. So after a reboot the sessions come back when Deskbar next starts, two minutes in. Right before each resume Deskbar reads the list again, and a session that is back by then is left alone.

A turn you stopped yourself with Esc is not continued: the session is resumed and waits for you. A session you cleared with `/clear` is never resumed, because that conversation is over; the fresh one that `/clear` starts takes its place on the task.

Marking a task done lets its session go: a done or closed task is never resumed. Reopen the task and it is looked after again.

Deskbar gives up on a session when it has been resumed three times in ten minutes, when its transcript is gone, when no folder is known for it or when the resume fails. It says why once, in the thread, and the task waits on you with the reason as its question. It does not try again by itself. Set the task's status (to in progress, say) and the next check tries again. Double-clicking the task also clears the give-up once its session is back: the task goes back to in progress and the count of resumes starts afresh.

## Settings

The setting is **Teach Claude Code about Deskbar**, in the Deskbar on this PC section of Manage's Settings tab. With Claude Code installed (there is a `.claude` folder in your profile) and the box ticked, Deskbar keeps two things in place:

- A skill at `%USERPROFILE%\.claude\skills\deskbar\SKILL.md`, rewritten when Deskbar updates.
- Two hooks, Notification and Stop, in `%USERPROFILE%\.claude\settings.json`, beside any hooks you already have. They run `%APPDATA%\Deskbar\claude-hook.cmd`, which hands the hook's details to `deskbar.cmd claude-hook` and always exits 0. When Deskbar is not running the hook prints nothing and the session carries on.

Under it, **Claude Code sessions keep their tasks in the task list** adds three more hooks, SessionStart, UserPromptSubmit and SessionEnd, running `%APPDATA%\Deskbar\claude-task-hook.cmd`. It is on by default, can be used only while the box above is ticked, and `deskbar.cmd claude-tasks on|off` sets it from a script. Unticking it takes out only those three and their script. While it is on, the skill also has a "Your task" section with the rules above.

While sessions keep their tasks, Deskbar reads Claude Code's list of running sessions (`claude agents --json`) every 15 seconds, and that list decides which of a task's sessions are running. A session the list has shown for two minutes that no task knows gets a task, named after the session, on the workspace of the VS Code window showing its folder, if its hooks have told Deskbar since it started that someone is at it (a session start or a prompt). The list shows a `claude -p` run like a person's session, so this is what keeps scripted runs off the board; the cost is that an idle session nobody has typed into since Deskbar started gets its task at its next prompt. The two minutes give a session started by hand its first turn, in which it joins or creates its own task; if it joins one later anyway, the task Deskbar made for it is deleted, unless you changed it: renamed it, moved it, flagged it, linked it to a goal, set its status, assigned it, gave it a description or commented on it. When no session of an open task (backlog, in progress or waiting on you) is in the list, and its latest one has been gone for two minutes, Deskbar resumes it in its own folder with `claude --bg --resume`, one session every five seconds so a reboot does not start twenty at once, and adds a line to the task's thread saying when. A session whose last turn was cut off is resumed with a message telling it to continue. A resume that fails, for example in a folder Claude Code was never told to trust, puts Claude's own message in the thread and is not tried again: its task waits on you until you act on it. A session that stops again straight after three resumes in ten minutes, or whose transcript is gone, is left alone, and its task waits on you with the reason until you act on it. A list that cannot be read changes nothing, and Manage says why in a line under the resume box until the list reads again.

Under the tasks box, **Resume the sessions of open tasks after a restart or a crash** turns the resuming off and keeps the rest: the list is still read and unknown sessions still get tasks. `deskbar.cmd claude-resume on|off` sets it from a script. Claude Code deletes a session's transcript after 30 days by default, and an ended session cannot be resumed without it; raise `cleanupPeriodDays` in `~/.claude/settings.json` to keep them longer. Deskbar never changes that setting.

The task hooks have a script of their own so that going back to an older Deskbar is harmless: an older version does not know that script, leaves it alone, and refuses what it asks for, so nothing blinks and the session reads nothing.

The first time Deskbar changes `settings.json` it keeps the original as `settings.json.deskbar-backup`. It leaves a `settings.json` that has comments in it alone.

Untick **Teach Claude Code about Deskbar**, or run `deskbar.cmd claude-skill off`, and the skill and all of Deskbar's own hooks are removed. Your other hooks stay as they are.

## From a script

A session that has just set up a git worktree can give it a workspace and move the editor there:

```powershell
& "$env:APPDATA\Deskbar\deskbar.cmd" move wt-login --create --group EC --title wt-login --wait 20
```

Or start the editor straight into the workspace, without taking you along:

```powershell
$ts = "$env:APPDATA\Deskbar\deskbar.cmd"
& $ts start wt-login --create --group EC --cwd C:\src\login -- code .
```

`open <link>` is for a session or a terminal inside VS Code. It finds the VS Code window whose folder holds the caller's folder. When that window is in a workspace you are not looking at, the link gets a new window of your default browser there, and you stay where you are. Otherwise the link opens as usual.

`task add` with no `--workspace` puts the note on the caller's own workspace:

```powershell
& $ts task add "Wait for CI"
```

The double-click, "Start Claude on this" and a thread's Open have script forms too. `task start` without `--folder` starts only where exactly one folder is known; with several it names them and starts nothing:

```powershell
& $ts task go <id>
& $ts task start <id> --folder C:\src\login
& $ts task open-session <id> <session id or short id>
```

A session's own task forms carry `--session` with the session's id, which the start-of-session message gives it. Every form but `join`, `new` and `later` acts only on that session's own task.

Free text (a title, a status line, a question, a comment) goes on stdin, with `-` in its place: `deskbar.cmd` hands its command line to cmd, which can run part of a text with quotes or `& | < >` in it as a command. In Git Bash, where Claude Code runs its commands, that is a heredoc:

```bash
"$APPDATA/Deskbar/deskbar.cmd" task comment - --session <id> <<'EOF'
**Found it**: the guard ran twice
EOF
```

From PowerShell, pipe the text in:

```powershell
'Tasks board' | & $ts task new - --session <id>          # or: & $ts task join <task id> --session <id>
'waiting for CI' | & $ts task status-line - --session <id>
'OK to merge?' | & $ts task ask - --session <id>          # waits on you, and the workspace blinks
& $ts task answered --session <id>                        # the question is settled: back to in progress, the blink stops
& $ts task done --session <id>
'Rename the guard' | & $ts task later - --session <id>    # a note on the session's workspace
```

On a machine with the Microsoft Store copy, `deskbar` is on the PATH, so `deskbar windows` and `deskbar help` work from PowerShell, cmd and Git Bash.

## Related

- [Driving it from scripts](scripts.md) has every command.
- [Switching workspaces](switching.md) and [Quick find](quick-find.md) are the two ways to get to a session.
- [The bar](the-bar.md) explains the rows, rings and icons.
- [Tasks](tasks.md) covers the task list a session can add to.
- [Where windows go](where-windows-go.md) covers the launched-by rule and folder memory.
- [Naming windows](naming-windows.md) covers naming editor windows by folder.
- The README's [case for agents and workspaces](../../README.md#agentic-ai-multiplies-the-contexts-it-does-not-replace-them).
