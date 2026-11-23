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
- It adds comments with the detail worth keeping, in markdown, and marks the task done when it has clearly finished. Only you close a task. A message to a session whose task is done takes it back to in progress. If you close a session's task, its next prompt tells it so, and new work needs a new task.
- On the bar a Claude task is told from your notes by its orange edge alone: it has no VS Code icon, and its title starts right after the tick box, as a note's does.
- A resumed session keeps its task as it was; a session that ends leaves its task open, with "no live session".
- Sessions run without anyone at them, such as `claude -p`, Agent SDK runs and scheduled agents, are told nothing and make no tasks.

A finished reply or a permission prompt still only blinks the workspace; it does not change the task.

A session can also add a note for later to its own workspace, and a script can add one with `task add`.

## Settings

The setting is **Teach Claude Code about Deskbar**, in the Deskbar on this PC section of Manage's Settings tab. With Claude Code installed (there is a `.claude` folder in your profile) and the box ticked, Deskbar keeps two things in place:

- A skill at `%USERPROFILE%\.claude\skills\deskbar\SKILL.md`, rewritten when Deskbar updates.
- Two hooks, Notification and Stop, in `%USERPROFILE%\.claude\settings.json`, beside any hooks you already have. They run `%APPDATA%\Deskbar\claude-hook.cmd`, which hands the hook's details to `deskbar.cmd claude-hook` and always exits 0. When Deskbar is not running the hook prints nothing and the session carries on.

Under it, **Claude Code sessions keep their tasks in the task list** adds three more hooks, SessionStart, UserPromptSubmit and SessionEnd, running `%APPDATA%\Deskbar\claude-task-hook.cmd`. It is on by default, can be used only while the box above is ticked, and `deskbar.cmd claude-tasks on|off` sets it from a script. Unticking it takes out only those three and their script. While it is on, the skill also has a "Your task" section with the rules above.

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
