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

Links and windows a session opens land in that session's workspace, not on the one you are looking at. Deskbar's skill tells every Claude Code session on the machine to open links with `open`, start programs with `start` and move the windows it opens with `move --no-follow`. Apart from the skill, a window opened by another app joins that app's workspace, so a link you click in the session's editor opens its browser window beside the editor. A background session has no editor or terminal window of its own, so a program it starts, such as the browser a browser-automation tool opens or an app it builds and runs, goes to the workspace of the session's task instead.

Seven VS Code windows all named `VSC` tell you nothing. **Name *app* windows by folder** on a VS Code icon's menu names each window after the folder, project or session it holds instead, and follows the window when it opens something else.

## Each session keeps a task

Every Claude Code session works on a task in the bar's [task list](tasks.md), so the list shows what each one is doing without you switching to it.

- When a session starts, Deskbar tells it the open tasks on its workspace that are Claude's to take (a note you assigned to Claude in the [tasks window](tasks.md#the-tasks-window), or a task a session had before) and Claude tasks on no workspace. Once it knows what it is working on, it joins the task its work continues or creates one. A session in a folder no VS Code window shows, say one running in a terminal window, is offered that workspace's tasks and makes its task there: the workspace of the latest task worked on in the same repo (a worktree counts as its repo). Only when the board has never seen that repo does it get the Claude tasks on no workspace alone, and make its task on none. Your own notes on no workspace are never offered to a session, and neither is a note on its workspace that you left assigned to yourself.
- A session that joins one of your notes makes it Claude's work: the line turns orange, and the session can mark it done.
- The session keeps a short status line under the task's title, such as "waiting for CI on #398".
- Before a session starts on a task, it looks through the open and closed tasks for the same or related work. It links what it finds with `task relate`, reads the linked tasks' sessions' conversations (the `task join` reply lists them as `linked`), and when its task turns out to be work already done or under way elsewhere it closes it as a [duplicate](tasks.md#duplicates-and-related-tasks) with `task duplicate-of` rather than redoing it. That is the one close a session makes; reopen the task if it got it wrong.
- A session that joins a [subtask](tasks.md#subtasks) is told about its family: the parent, with your description and its last comments, the other subtasks, and every Claude session on them with its folder and the file its conversation is kept in. It reads the end of the parent session's conversation before it starts, so a subtask's title can be the whole brief. A session on a parent is told about its subtasks, a resumed session hears it again, and `task family` prints it on demand. A session can split its own work with `task subtask`: the new subtask is Claude's, and a background session starts on it at once in the splitting session's repo, with no window of its own (it shows in the tasks panel, where Open Claude attaches to it; `--model <name>` picks its model). With `--no-start`, which a session adds only when you asked for the piece to wait, it stays in the backlog and the next session on the workspace is offered it, and **Start Claude on this** on a subtask with no session of its own offers the parent's repo first.
- A task you wrote a description for (Edit in the [tasks window](tasks.md#the-tasks-window)) shows the first part of it in the briefing, so a note you hand to Claude can carry its brief. The description is yours: a session never writes it, and its own status line is the only line it keeps.
- When it needs a decision from you, it asks through its task. The task moves to the top as "Waiting on you" with the question, and the workspace blinks. The session itself says when the question is settled, with `task answered`: it runs that once your reply settles the question, or once it settles it itself, and the task goes back to in progress, the blink stops and the task leaves the bar's list. Your message alone does not do it, so a `/loop` or scheduled check-in cannot answer for you; while the question is open, each prompt reminds the session of it. The bar lists only the tasks assigned to you, and a session's task is yours exactly while it is waiting on you or done (`task ask` and `task done` hand it to you, `task answered` and your next message after it was done hand it back to Claude). It stays in the tasks window, and comes back to the top of the bar when the session asks again. Switching to the session's window or its workspace does not take it off.
- The question is in the task's thread too, as the session's message, in orange while it waits on you, so your reply sits right under what it answers. Open the task's line in the tasks panel and the question leaves the line, since the thread under it shows it; fold the line and the question is back under the title. The task's own window works the same way: its thread holds the question, and the box at the top keeps only the rest. A question asked again word for word is not repeated in the thread, and once it is settled it folds away with the session's other comments. Questions asked before this change are not in their threads and stay on their lines.
- A question can offer answers. A session that expects a short answer ("Merge it", "Not yet") ends its question text with a line `--options` and then up to 4 answers, one per line, each at most 40 characters and all different. Deskbar keeps them with the question, so the Inbox page shows them as buttons under the question; one click answers with the answer's exact words as your comment, as typing it would (see [The Inbox](tasks.md#the-inbox)). They are on stdin like the rest of the text, never a `--option` word on the command line. A list that breaks a limit refuses the whole ask and nothing is stored. The start briefing has no room for this, so a session that asks without options is told about them in a `hint` field of the reply to `task ask`. The answers go whenever the question does: when it is answered, replaced by a new ask, set aside by your reply, or the task is done or closed.
- A question that asks you to merge a pull request settles itself when the pull request is merged. When a waiting task's question links a GitHub pull request, Deskbar asks GitHub every 30 seconds, through the `gh` program, where it stands. Once it is merged or closed (every one, when the question links several), the task goes back to in progress and leaves the bar's list straight away, without waiting for the session to run `task answered`, and a line from Deskbar in the thread says so: "Question settled: [Deskbar #504](https://github.com/AppifySheets/Deskbar/pull/504) was merged." A question that only mentions a pull request that was already merged before it was asked is left for you. Without `gh`, or with `gh` signed out or offline, nothing changes and the session settles the question as before.
- A question you have replied to stops waiting on you once Claude works on it again. When you answer a waiting task, with a comment on it or a message in its session's terminal, and its session then gets to work, the task leaves "Waiting on you" and shows under "Claude working" within about half a minute, and a line from Deskbar in the thread keeps the question: "Set aside while Claude works on your reply:", with the question quoted under it. That line folds away once the question is back, answered or settled. At the end of that turn the session is asked whether the question still holds: if it does, it asks again and the task is back in "Waiting on you" where it was; if your reply settled it, the session runs `task answered`. If the session stops without doing either, the task comes back to you with the question as it stood. A session that is busy only because it just asked, or because it is rebasing its pull request while you have not replied, keeps the task waiting, and so does a task you snoozed.
- Settling a question with `task answered` also settles the comments the session wrote since it asked: they stay, folded away in the tasks window behind a "N settled comments" line. A session can settle one of its own comments with `task comment-settle <comment-id>`.
- Sessions are taught to keep a task lean: say what needs you, and nothing else. The title is a few words, the status line one short phrase, and a comment is rare, for a result you must know (a decision made for you, a blocker) in a line or two, with no progress logs or findings dumps. When the next move is yours, a pull request to merge among them, the session asks through the task, so it waits on you at the top of the list with what you must do. It marks the task done only when nothing waits on you; once you have merged its pull request, it settles the question and marks the task done. Only you close a task. A message to a session whose task is done takes it back to in progress. If you close a session's task, its next prompt tells it so: the session reopens the task if your message carries on with that work, and makes a new task for anything else. Resuming a session whose task you closed reopens the task.
- On the bar a Claude task is told from your notes by its orange edge alone: it has no VS Code icon, and its title starts at the line's left edge, as a note's does.
- A resumed session keeps its task as it was; a session that ends leaves its task open, with "no live session".
- Sessions run without anyone at them, such as `claude -p`, Agent SDK runs and scheduled agents, are told nothing and make no tasks.

A finished reply or a permission prompt still only blinks the workspace; it does not change the task.

A session can also add a note for later to its own workspace (the one its VS Code window is on, else its own task's, else its repo's latest task's), and a script can add one with `task add`.

## Going to a session, and starting one

Double-click a task's line, on the bar or in the tasks window, and you go to its workspace and then to its session:

- A background session (one started with `claude --bg`, by Deskbar or by you) gets a Windows Terminal window attached to it with `claude attach`, titled after the task with "· Claude" on the end and in Deskbar's own terminal look. On the bar and in quick find it carries Claude's icon, taken from your `claude.exe`, rather than the terminal's, so a session's window stands out from your other terminals. If that window is already open, it comes to the front instead: only one window can be attached to a session at a time, and a second one would take the session from the first.
- A session you started in VS Code or a terminal brings its VS Code window to the front.
- A session that has ended is resumed first, then its window opens.
- A note with no session just takes you to its workspace. A double-click never starts a session.

The first click of that double-click opens the task in the tasks panel. To go to the session without that, right-click a Claude task's line and pick **Open Claude**, right after Edit, on the bar and in the tasks window: it does what the double-click does, and the task stays as it was. In the tasks panel a Claude task's line also has an **Open Claude** button, the small open-in-window icon next to its comments, that does the same in one click.

Closing the terminal window only detaches it. The session keeps running, and the next double-click opens a new window on it.

Switching to a session's terminal window works the other way round too: its task becomes the selected one, highlighted on the bar and in the tasks panel as if you had clicked it, so you can see which task the terminal in front belongs to. If its line is scrolled out of sight, on the bar or in the open tasks panel, the list scrolls just far enough to show it. It stays selected when you move on to another window. A VS Code window does not select anything, since one folder can hold the sessions of many tasks.

The window uses a Windows Terminal profile of Deskbar's own, "Deskbar Claude": VS Code's Dark Modern colours, JetBrains Mono (Cascadia Mono where that is not installed), Claude's orange bar cursor and no scrollbar. Deskbar installs it while **Claude Code sessions keep their tasks in the task list** is ticked and removes it when you untick that box or **Teach Claude Code about Deskbar** above it.

This needs the Claude Code CLI on the PATH (the native install puts `claude.exe` in `%USERPROFILE%\.local\bin`) and Windows Terminal. Without them the items are hidden or say why they cannot work: a start or a window that fails tells you in a message box as well as in the task's thread. When Claude Code is not installed at all, "Start Claude on this" asks whether to install it. Yes opens a terminal in the folder you chose that runs Claude Code's official installer and then starts `claude` there, so you can sign in and trust the folder; choose "Start Claude on this" again once you have. No leaves the reason, with the manual install line, in the task's thread. Claude is not started on a closed task.

To hand a task to Claude, pick **Start Claude on this** on its menu (right after Edit, on the bar and in the tasks window) or the button of that name in its thread. The menu has the item only on a task no session has worked on. A task Claude already has shows **Open Claude** there instead, which resumes an ended session. Its thread has the button only while none of its sessions is running: a running session is listed there with **Open**, and once every session has ended the button starts a fresh one. Deskbar starts a background session with the task as its first message, so it gets to work at once, joins it to the task, and opens a window on it on the task's workspace. The folder is the repo of the task's latest session. A task without sessions offers the repos its workspace knows, from its other tasks' sessions and its VS Code windows: with one, it starts there; with several or none, a short list at the pointer ends with "Choose a folder…". A second session beside a running one is rarely what you meant, so nothing in the window offers it; `deskbar task start` still starts one.

A folder Claude Code has never been told to trust cannot start a background session. Claude's message goes into the task's thread, and Deskbar offers to open a terminal running `claude` in that folder so you can accept the trust prompt yourself. Deskbar never accepts it for you.

A task's thread lists its sessions, newest first, with when each joined and whether it is working, idle, ended or expired. Open on a line does what the double-click does for that session. An expired session is one whose transcript Claude Code has deleted (after 30 days by default), so it has no Open.

## Tasks keep their sessions running

A task that is open (backlog, in progress or waiting on you) is meant to have a session running. A reboot, a sign-out, a crash of Claude Code's background daemon or a closed VS Code session ends sessions, and Deskbar resumes the ones on open tasks, one every five seconds, a cut-off turn told to continue. Each resume adds a line to the task's thread.

Deskbar waits two minutes first. Nothing is resumed in the first two minutes after Deskbar starts, and a session that has gone from Claude Code's list is resumed only once it has been gone for two minutes. VS Code brings its Claude panels back by itself after a restart, an update or a window reload, and a resume in that gap would start a second copy beside the one VS Code reopens. So after a reboot the sessions come back when Deskbar next starts, two minutes in. Right before each resume Deskbar reads the list again, and a session that is back by then is left alone.

A turn you stopped yourself with Esc is not continued: the session is resumed and waits for you. A session you cleared with `/clear` is never resumed, because that conversation is over; the fresh one that `/clear` starts takes its place on the task.

Marking a task done lets its session go: a done or closed task is never resumed. Reopen the task and it is looked after again.

Deskbar gives up on a session when it has been resumed three times in ten minutes, when its transcript is gone, when no folder is known for it or when the resume fails. It says why once, in the thread, and the task waits on you with the reason as its question. It does not try again by itself. Set the task's status (to in progress, say) and the next check tries again. Double-clicking the task also clears the give-up once its session is back: the task goes back to in progress and the count of resumes starts afresh.

## Recurring tasks

A [recurring task](tasks.md#recurring-tasks) starts a fresh Claude session at each time it is due. Deskbar starts it in the background, in the task's folder, on Sonnet unless the repeat names another model, and it opens no window and takes no focus. The session reads the task, does the work, comments and marks the task done. About a minute after it goes idle Deskbar stops it, unless you have it open in a window, so finished runs do not pile up in `claude agents`.

A time that comes while the last run is still going is skipped. After sleep or shutdown nothing is caught up. **Wake now** starts a run at once. A comment you post between runs starts a fresh run too, but only once the last run's session has been stopped, about a minute after it goes idle; until then the comment goes to that session. If the folder is gone or a run cannot start, the task asks you once. A repeat can be set or ended from a script with `deskbar.cmd task repeat`. A task's own session may set its task's repeat or turn it off (`task repeat ... --session <id>`). The manager may set a repeat on any task by its id, but it may not turn one off.

## Settings

The setting is **Teach Claude Code about Deskbar**, in the Deskbar on this PC section of Manage's Settings tab. With Claude Code installed (there is a `.claude` folder in your profile) and the box ticked, Deskbar keeps two things in place:

- A skill at `%USERPROFILE%\.claude\skills\deskbar\SKILL.md`, rewritten when Deskbar updates.
- Two hooks, Notification and Stop, in `%USERPROFILE%\.claude\settings.json`, beside any hooks you already have. They run `%APPDATA%\Deskbar\claude-hook.cmd`, which hands the hook's details to `deskbar.cmd claude-hook` and always exits 0. When Deskbar is not running the hook prints nothing and the session carries on.

Under it, **Claude Code sessions keep their tasks in the task list** adds three more hooks, SessionStart, UserPromptSubmit and SessionEnd, running `%APPDATA%\Deskbar\claude-task-hook.cmd`. It is on by default, can be used only while the box above is ticked, and `deskbar.cmd claude-tasks on|off` sets it from a script. Unticking it takes out only those three and their script. While it is on, the skill also has a "Your task" section with the rules above.

While it is on, Deskbar also keeps one permission rule in the same file, under `permissions.allow`: `Read(//c/Users/<you>/AppData/Roaming/Deskbar/task-images/**)`. That folder holds the pictures you paste into tasks (see [Tasks](tasks.md#the-tasks-window)), and it is outside every session's own folder, so a session in Claude Code's default permission mode would otherwise stop and ask you before reading one. The rule lets sessions read that one folder and nothing else; your own rules stay as they are, and unticking the box takes the rule out again.

### Permission questions on the task's page

Under the tasks box, **Answer Claude's permission questions in Deskbar** is off until you tick it (`deskbar.cmd claude-permissions on|off` from a script). Ticked, with Claude Code 2.1.332 or newer, Deskbar adds a PermissionRequest hook to `%USERPROFILE%\.claude\settings.json`, running `%APPDATA%\Deskbar\claude-permission-hook.cmd`, and takes only that entry out again when you untick it.

When a background session (one started with `claude --bg`, which every session Deskbar starts is) is about to ask whether it may run a command or edit a file, and its task is open, the question also shows on the task's page in the Inbox view: a card at the page's bottom with the whole command or file path, nothing cut off (a long one folds by whole lines behind "Show all"; hidden or direction-changing characters show as marks like ⟨U+202E⟩), and the buttons **Allow**, **Deny**, **Allow and stop asking for edits** (only for a file edit: the session then edits without asking, also after a restart) and **Answer in the terminal**. The line wears a shield with a keyhole, first under Needs you, and on the bar its second line says "Asks permission". The terminal's own dialog is there the whole time, so you can answer there instead; the card then goes once the session moves on.

Only you answer, in Deskbar's window: a click on a button, or a button you reached with Tab and pressed with Enter. No single key answers, and no `deskbar.cmd` command can. The hook gets a one-time code from Deskbar that is never written to a file before your click, so another program cannot write an answer the hook would take by watching files; a program running as you that can read other programs' memory could still learn it, which Deskbar does not guard against. The hook also makes sure it is talking to Deskbar itself before it takes a code: the program answering on Deskbar's control pipe must be Deskbar's own executable, so another program that takes the pipe's name while Deskbar is closed or starting gets nothing and the terminal asks. A program running as you that can replace or patch Deskbar's executable, or inject code into it, is out of reach of that check. When a session asks again, its older question leaves the page, so a click always answers the question the session is waiting on. Whenever Deskbar cannot show the question (closed, restarted, the Inbox view off, a session in a terminal or VS Code, Claude's own multiple-choice and plan dialogs) the hook says nothing and the terminal asks as before. Nothing about a question is written to Deskbar's trace. If you have PermissionRequest hooks of your own, Claude Code runs them as well and takes whichever answers first.

### Your comments wake the session

While it is on, a comment you post on a Claude task reaches that task's session and wakes it if it sits idle, so you can hand it a screenshot or a correction from the tasks panel without finding its window. A comment goes to one session: the task's live one, or else its newest. How it gets there depends on the session:

- Running and idle: within a few seconds it takes a turn with your comment, pictures included, as its message.
- Running and busy: your comment arrives at its next step, as soon as whatever it is running finishes.
- Not running (its window closed, the machine restarted, or Deskbar stopped it because its task was done): Deskbar resumes it in the background with your comment. Double-click the task to open it.
- A task whose sessions can no longer be resumed gets a line from Deskbar in its thread saying the comment was not sent.

A comment on a done task wakes its session too; if the comment is more work, the session puts the task back in progress, and if it only asks about the finished work, the task stays done. Only your own comments wake a session: a session's comments, Deskbar's own lines, an edit, and a `task comment <id>` run from inside a Claude session do not.

It works through a third hook on Stop, `claude-wait-hook.cmd`, which Claude Code lets run on after each turn (`"asyncRewake": true`, with a timeout of 30 days). The script asks Deskbar once where the session's inbox is, then checks every two seconds for a file in `%APPDATA%\Deskbar\claude-inbox`, where Deskbar puts your comment; when it finds one, the session wakes with it. It keeps waiting through a Deskbar restart. Deskbar adds this hook only for Claude Code 2.1.322 or newer, which it checks with `claude --version` when it starts: an older one might not run the hook in the background, and would then sit waiting after every turn. If a background session sits idle for longer than 30 days, Deskbar stops it and resumes it with your comment instead; a terminal or VS Code session idle that long gets the comment at its next turn. Whether a VS Code session wakes the same way as a terminal or background one is still to be confirmed; if it does not, it reads your comment at its next turn.

While sessions keep their tasks, Deskbar reads Claude Code's list of running sessions (`claude agents --json`) every 15 seconds, and that list decides which of a task's sessions are running. A session the list has shown for two minutes that no task knows gets a task, named after the session, on the workspace of the VS Code window showing its folder (with no such window, the workspace of the latest task worked on in its repo), if its hooks have told Deskbar since it started that someone is at it (a session start or a prompt). The list shows a `claude -p` run like a person's session, so this is what keeps scripted runs off the board; the cost is that an idle session nobody has typed into since Deskbar started gets its task at its next prompt. The two minutes give a session started by hand its first turn, in which it joins or creates its own task; if it joins one later anyway, the task Deskbar made for it is deleted, unless you changed it: renamed it, moved it, flagged it, linked it to a goal, set its status, assigned it, gave it a description or commented on it. When no session of an open task (backlog, in progress or waiting on you) is in the list, and its latest one has been gone for two minutes, Deskbar resumes it in its own folder with `claude --bg --resume`, one session every five seconds so a reboot does not start twenty at once, and adds a line to the task's thread saying when. A session whose last turn was cut off is resumed with a message telling it to continue. A resume that fails, for example in a folder Claude Code was never told to trust, puts Claude's own message in the thread and is not tried again: its task waits on you until you act on it. A session that stops again straight after three resumes in ten minutes, or whose transcript is gone, is left alone, and its task waits on you with the reason until you act on it. A list that cannot be read changes nothing, and Manage says why in a line under the resume box until the list reads again.

When a task is done or closed, Deskbar stops its background session once it has sat idle for a minute, so finished sessions do not pile up in memory. Stopping keeps the conversation: the thread says when it was stopped, and opening the task resumes it. A done task's session keeps running while its window is open, because you may still be reading its last reply; close the window and it is stopped. Closing the task stops it even with the window open, and the window closes with it.

Under the tasks box, **Resume the sessions of open tasks after a restart or a crash** turns the resuming off and keeps the rest: the list is still read and unknown sessions still get tasks. `deskbar.cmd claude-resume on|off` sets it from a script. Claude Code deletes a session's transcript after 30 days by default, and an ended session cannot be resumed without it; raise `cleanupPeriodDays` in `~/.claude/settings.json` to keep them longer. Deskbar never changes that setting.

The task hooks have a script of their own so that going back to an older Deskbar is harmless: an older version does not know that script, leaves it alone, and refuses what it asks for, so nothing blinks and the session reads nothing.

The first time Deskbar changes `settings.json` it keeps the original as `settings.json.deskbar-backup`. It leaves a `settings.json` that has comments in it alone.

Untick **Teach Claude Code about Deskbar**, or run `deskbar.cmd claude-skill off`, and the skill and all of Deskbar's own hooks are removed. Your other hooks stay as they are.

## The manager session

The **Manager** button, at the end of the row under the bar's task box, opens a background Claude session that looks after the whole board rather than one task. Ask it what needs you or what is stuck, or tell it what to pass on. It reads the board with `task overview` and `task show`, and it acts only through `deskbar.cmd task` commands. A comment it leaves on a task wakes that task's idle session. When it passes on your answer to a session waiting on you, it quotes you after "The user says:", and only those words count as yours. It cannot close, delete or finish a task, or edit or settle a comment: those are yours, and it says so and asks you. It runs on Fable, has no file, web or timer tools, and does nothing until you write to it.

The manager starts with Claude Code's Remote Control on, so it appears as "Deskbar manager" in the Claude app on your phone and you can talk to it away from the desk, the keyboard's microphone included. It is the only session that does: the sessions Deskbar starts on tasks are started with Remote Control off, even when your Claude Code settings turn it on, so the phone's list holds the manager and not every task. A session started before this, with Remote Control on, keeps it when it is resumed, because a resume cannot change it without starting a copy.

When the manager needs a decision only you can make, such as whether two tasks are the same job or which of several waiting things comes first, it asks you. Its question shows as a violet line at the very top of the bar's task list and of the tasks panel, above every task's question, and it stays in sight when the list scrolls. There is only ever one; a new question replaces the old. Click the line to open the manager and answer it there. Opening the window alone leaves the line in place; your reply clears it, and if your reply leaves the question open the manager asks again. Every task's own question still comes straight to you: the manager does not vet them.

There is one manager a day. A click opens a window on it and brings that window forward when it is already open. The window docks in a full-height column just inside the tasks panel, or just inside the bar while the panel is closed, and other windows keep clear of it as they do of the panel. It is pinned to every desktop, so it follows you from workspace to workspace and sits in the bar's pinned row. Drag its inner edge to change its width, which is remembered; dragged by its title bar it goes back to its place, and closing it gives the space back. It has Claude's icon in violet, where a task's session has the plain one, a violet ring while it has focus, and its icon glows while the manager's question waits on you. When it has ended, the click resumes it. The next day a click stops yesterday's (unless it is still in the middle of a reply) and starts a new one. It runs in a folder of its own, `%APPDATA%\Deskbar\manager`, so it never counts as a task's session and is never adopted as one. The first start on a machine finds that folder untrusted, and Deskbar offers a terminal there for you to accept Claude Code's trust prompt; click **Manager** again once you have. Like **Start Claude on this**, it needs **Claude Code sessions keep their tasks in the task list**.

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

The [manager session](#the-manager-session) has a script form too, and so does its view of the board:

```powershell
& $ts task manager          # what the Manager button does
& $ts task overview         # one line per open task
& $ts task show <id>        # one task in full, thread included
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
"OK to merge?`n--options`nMerge it`nNot yet" | & $ts task ask - --session <id>   # the same, offering two answers (up to 4, each up to 40 characters)
& $ts task answered --session <id>                        # the question is settled: back to in progress, the blink stops
& $ts task done --session <id>
'Rename the guard' | & $ts task later - --session <id>    # a note on the session's workspace, related to its task
'Split the docs' | & $ts task subtask - --session <id>    # a subtask of the session's own task, its own session started at once (--no-start: none)
& $ts task family --session <id>                          # its task's parent, siblings and their sessions
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
