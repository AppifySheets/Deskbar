# Tasks open Claude sessions

Date: 2026-11-23. Deskbar task 8f70d8ea, "Click a task: Claude takes it, or jumps to its session".

## Why

Petre runs many Claude Code sessions at once, and Deskbar's task list already records which sessions worked on which
task (`TaskItem.Sessions`: id, folder, joined, ended). He wants the task to be the handle on the work:

- "i want to click on a task and have claude take it. it would know where to start it"
- "i also want to click on a task and go to the correct session"
- "it would also be nice to have the ability to open previous sessions from task history; this way i'd close claude
  sessions and come back to them in the future"
- "i'd have tasks for all running claude sessions, without uis; or with uis"
- "i'd close it and come back to it by clicking the question or task", and the session keeps working meanwhile.
- "deskbar is being restarted often", so nothing may depend on Deskbar staying up.
- "if i restart a computer or claude sessions end, i want to resume all claude sessions; i want to see all claude
  sessions working in the tasks window", and "if a session has ended and done its work, i will mark the task as
  done".

## Goals

- A session started from a task runs in the background. Closing the window showing it does not stop it, and neither
  does restarting Deskbar.
- Going to a task with a running session opens a window on that session, or brings an open one to the front.
- Going to a task whose session has ended brings the session back in the background, by its id, and opens a window
  on it.
- A task with no session can be handed to Claude: a background session starts in the right repo with the task as its
  first message, and works on it at once.
- The task shows whether its session is working or idle, from what Claude Code itself reports, not from hooks that a
  crash never sends.
- The task's thread lists every session that worked on it, and each one can be reopened from there.
- Every open Claude task keeps its session running. After a reboot, a crash or any other end, Deskbar resumes it in
  the background, and a turn that was cut off carries on by itself. Marking the task done is how a session is let go.
- Every live Claude session on the machine has a task, so the tasks window shows all of them working or idle.

## Non-goals

- VS Code as the surface for these sessions. Decided 2026-11-23 after a spike (the task's comments carry it): a
  VS Code Claude panel costs about 300 to 380 MB per session against a few MB for a terminal session, and the
  VS Code route needed a companion extension and a guess at which window is the main one. Petre: "okay, not so bad.
  we can use it" about the CLI in Windows Terminal.
- Moving a session started in VS Code into a terminal. Only background sessions can be attached (below). A VS Code
  session's task still shows working or idle and still goes to its VS Code window as today.
- Deskbar running Claude itself (`claude -p` workers it owns). Measured and dropped: Claude Code's own background
  sessions do the same job, outlive Deskbar, and keep the full interactive UI.
- A chat UI drawn by Deskbar.

## Anti-goals

- Starting a NEW session on its own. A new session starts only on Petre's click. Resuming the session of an open task
  is the one automatic start, and Petre asked for it by name (2026-11-23); CLAUDE.md's "No launching, no restore
  prompt" was about apps and workspaces and still holds for them.
- Stopping a session Petre did not ask to stop. Closing a window never stops one.
- Resuming the session of a task that is done or closed.
- A resume loop: a session that ends again straight after being resumed is not resumed forever (see "Keeping open
  tasks running").
- Two drivers on one session: Deskbar never starts `--bg --resume` on a session `claude agents` already lists.

## What was measured

All on Petre's machine on 2026-11-23, Claude Code CLI 2.1.330. Each measurement is a comment on the task.

- `claude --bg "<prompt>"` starts a full interactive session in the background and prints a short id, the first
  eight characters of its session id (`884c127f` for `884c127f-9633-...`). It refuses a folder that has never been
  trusted ("Workspace not trusted. Run `claude` in <folder> once and accept the trust prompt").
- The session runs under `claude daemon run`, whose parent is `WmiPrvSE.exe`, with one `claude --bg-pty-host` per
  session. Nothing ties it to the process that ran `claude --bg`, so neither a window nor Deskbar owns it.
- `claude attach <id>` shows the session in a terminal. Attached from a hidden console, then that console and its
  `claude.exe` killed: the session kept running and finished its turn. Closing a real Windows Terminal window is not
  measured; it is gentler than a kill. While attached, `←` goes to Claude's own list of background sessions and
  Ctrl+Z drops back to the shell; the session keeps running either way.
- `claude agents --json` prints every live session on the machine, interactive (VS Code, terminal) and background,
  without a terminal: `pid`, `cwd`, `kind` (`interactive` or `background`), `startedAt`, `sessionId`, `name`,
  `status` (`busy` or `idle`), and for background ones `id` and `state` (`working`, `done`). It listed 23 sessions.
- `claude --bg --resume <session-id>` continues a session in the background under the same id, or starts a copy and
  says so when it is already running. `claude stop <id>` stops one and keeps its conversation; `claude rm <id>`
  removes it from the list; `claude respawn` restarts one on the current Claude Code version.
- The daemon is transient: it was gone once the last background session was removed, and the next `--bg` starts it.
- `claude -p` (headless) also works and resumes by id, and `--output-format stream-json --verbose` streams each tool
  call as it happens. Kept as a fallback, not used.
- Claude Code deletes transcripts after `cleanupPeriodDays`, 30 by default. Petre's oldest TaskSpaces transcript was
  exactly 30 days old and the setting is not set.
- A process that inherited `ELECTRON_RUN_AS_NODE=1` breaks `code`; Deskbar already strips it at startup
  (`LaunchEnvironment.Clean`). Not needed on this route, but the same care applies to anything Deskbar starts.

## How it behaves

### Where the clicks are

- **Going to a task** stays the double-click on a line (Ruling 54, the tasks window and the bar alike) and `Go` in
  `TasksBoard`. Today it switches to the task's workspace (`App.GoToTask`, whose comment already says this change
  lands there). New behaviour, by the session `TaskViews.SessionToGoTo` picks:
  - a running background session: switch to the task's workspace, then bring its terminal window to the front, or
    open one (`claude attach`) when none is open;
  - a running VS Code or terminal session that is not a background one: as today, its window (`EditorFolder`);
  - an ended session: `claude --bg --resume <session id>` in the session's folder, then open a window on it;
  - no session: as today, the workspace switch. A double-click never starts a session: a note is often an errand.
- **Handing a task to Claude** is a menu item on the line's menu ("Start Claude on this", after Edit) and a button in
  the task's thread. On a task with a running session it asks first, since a second session on one task is rarely
  meant.
- **Answering a question**: a task waiting on Petre goes to its session the same way, so clicking the question opens
  the session where it asked.
- **Task history**: the thread lists the task's sessions, newest first, each with when it joined, whether it is
  working, idle or ended, and an Open button that does what going to that session does.
- Every one of these has a control-pipe twin (`ControlUsage`): `task go <id>`, `task start <id>`,
  `task open-session <id> <session id>`. Names are the builder's within the existing `task` verb family.

### Which sessions are live

Deskbar reads `claude agents --json` on a timer while a Claude task is on the bar or the tasks window is open, and once
before acting on a click. Its rows are the only truth for "running" and "working or idle":

- a session in the list is live; `status` says working (`busy`) or idle;
- a session the task knows that is not in the list has ended, whatever SessionEnd said or failed to say;
- a background session is attachable; an interactive one is not.

This replaces SessionEnd as the end signal and fixes the stale "live" sessions a crash or reboot leaves. The hook
still records joins. If `claude` is not on the PATH or the call fails, Deskbar falls back to today's hook-based view
and says so in the task's thread once.

Restarting Deskbar loses nothing: the task to session link is in `tasks.json`, and the next read of
`claude agents --json` finds every session that is still running.

### Every live session has a task

A live session in `claude agents --json` that no task knows gets a task from Deskbar: its title is the session's
`name`, it is a Claude task with that session joined, and it lands on the workspace of the VS Code window showing the
session's folder, or on none, as `task new` does. The SessionStart hook stays the first way in (the session names or
joins its own task); this only catches the sessions that did not. A session the hook joins a moment later moves to that
task, and the task Deskbar made for it is deleted if nothing else was added to it.

### Keeping open tasks running

The rule, checked on every read of `claude agents --json`: the latest session of every open Claude task (backlog, in
progress or waiting on Petre; not done, not closed) is running. When it is not:

1. Resume it: `claude --bg --resume <session id>` in the session's folder, through `ProgramStarter`.
2. If its last turn was cut off (the transcript ends without the assistant's closing reply), send it `continue` as its
   first message, so the work carries on by itself. Petre chose this on 2026-11-23 over coming back idle, knowing a
   command that was half done can run twice; Claude checks the state before redoing anything.
3. Say so in the task's thread: "Resumed after <reason>" with the time.

This one rule covers a reboot (every session is gone when Deskbar starts at sign-in), a crash, a closed VS Code window,
and a session ended by hand on a task that is still open. A Deskbar restart resumes nothing, because every session is
still running. A session started in VS Code comes back as a background session, not in VS Code.

Guards:

- Done and closed tasks are skipped: "if a session has ended and done its work, i will mark the task as done".
- A session that ends again within a few minutes of being resumed is resumed at most twice more, with a growing pause.
  After that its task waits on Petre with the reason, and nothing more happens until he clicks it.
- A session whose transcript has expired cannot be resumed; its task says so once and waits on Petre.
- Resumes are spread out (one at a time, a few seconds apart), since after a reboot there may be twenty at once and
  each `claude.exe` takes hundreds of MB.
- A switch in Manage turns the automatic resume off without turning off the rest of the feature.

### Starting a session from a task

1. Choose the folder (below).
2. Run `claude --bg "<first message>"` in it, through `ProgramStarter` so it breaks away from the Store package. Read
   the short id from its output.
3. The session's SessionStart hook offers the open tasks, and the first message tells it which one to join, so the
   session joins the task through the existing hook path.
4. Open a window on it (below) on the task's workspace.

The first message is the task, so the session starts working at once (settled 2026-11-23: a background session given
nothing to do just waits, and Petre agreed the plan with the task as the first message):

```
Deskbar task <id>: <title>
<status line, when there is one>
Join this task (deskbar task join <id>) and start on it.
```

Plain text, cut to a sensible length, passed as one argument.

An untrusted folder makes `claude --bg` fail. Deskbar shows the message and offers to open a terminal running
`claude` in that folder so Petre can accept the trust prompt once. It never accepts trust for him.

### Choosing the folder

- A task that has sessions: the folder of the session being opened. For a new session on such a task, the latest
  session's folder.
- A task with no sessions (agreed 2026-11-23): the repos the task's workspace knows, which are the folders of every
  session of every task on that workspace plus the folders of its VS Code windows (`EditorFolder.Of`, resolved to full
  paths through those session folders). Exactly one: use it. Several or none: a short pick list of them, ending with
  "Choose a folder…".
- A session's folder can be a git worktree under `<repo>\.claude\worktrees\<name>`. Resume runs in the session's own
  folder, because transcripts are filed per folder.

### The window on a session

- Windows Terminal, one window per session: `wt -w new` with Deskbar's profile, the session's folder, a title naming
  the task, and `claude attach <id>`. The profile is the "Dark Modern" look Petre picked on 2026-11-23 from three on
  trial: VS Code's Dark Modern colours, JetBrains Mono 10 pt, line height 1.15, Claude's orange bar cursor, no
  scrollbar. Deskbar ships it as its own Windows Terminal fragment so the profile exists on any machine with the
  feature on, and removes the trial fragment (`Fragments\DeskbarClaudeTry`). JetBrains Mono is not part of Windows,
  so the profile falls back to Cascadia Mono where it is missing.
- Deskbar finds the new window by its title (Windows Terminal draws all its windows from one process, so the process
  does not tell them apart; `suppressApplicationTitle` keeps the title Deskbar gave) and puts it on the task's
  workspace the way it places any new window.
- A second click on a task whose window is open brings that window forward instead of attaching twice.
- Closing the window detaches. The session keeps running and the task keeps showing it.

### Ended sessions and transcript retention

Reopening an ended session needs its transcript, which Claude Code deletes after 30 days by default. Deskbar does not
change the user's retention setting on its own (a privacy choice). The thread shows a session whose transcript file
is gone as "expired" without an Open button, and Manage, beside the setting, says once that Claude Code keeps sessions
for 30 days unless `cleanupPeriodDays` is raised in `~/.claude/settings.json`.

A reboot or sign-out ends every background session. Deskbar resumes the ones on open tasks when it starts (see
"Keeping open tasks running"), and a turn that was cut off is sent `continue`.

### The setting

Under the existing "Claude Code sessions keep their tasks in the task list" (`AppState.ClaudeTasks`), since the feature
is meaningless without it. Turning it on also installs the Windows Terminal fragment; turning it off removes it and
stops nothing that is running.

## Constraints

- Windows only, Claude Code CLI on the PATH (the native install puts `claude.exe` in `%USERPROFILE%\.local\bin`),
  Windows Terminal installed. Without them the feature's items are hidden or say why they cannot work.
- The CLI's background sessions are new. Deskbar depends on four things only: `claude --bg`, `claude attach`,
  `claude --bg --resume` and `claude agents --json`, and treats an unknown JSON shape as "cannot tell", never as
  "ended".
- Measure, don't theorise (project CLAUDE.md). These are unmeasured and come first in the plan, in a throwaway folder
  and off Petre's screen where possible: closing a real Windows Terminal window on an attached session; attaching from
  a second window while one is attached; Deskbar being killed while it is starting a session; whether
  `claude --bg --resume <id>` takes a first message; whether a resume with no message calls the model at all; how to
  tell a cut-off turn from the transcript's last lines; and what one idle background session costs in memory.
- The control pipe stays the only way other programs drive Deskbar.
- No test may start Claude or a terminal on Petre's desktop. The pure parts (folder choice, first message, parsing
  `claude agents --json`, matching sessions to tasks, the retention check) are Core and unit-tested; the live path is
  a manual test script entry.
- The README's task section gains the feature, and the manual test script gains the live steps.

## Left to the builder

- The control-pipe verb names, the Manage wording and the menu wording, within the decisions above.
- The poll interval for `claude agents --json` and how long a click waits for it.
- How the window is found by title, and the title's wording.
- Whether "Stop session" belongs on the line's menu (it maps to `claude stop <id>`; useful, not asked for).

## Settled along the way

- The VS Code spike extension `deskbar-spike.claude-window4` is uninstalled (2026-11-23). Its stray tabs ("try 4",
  "try 5") are Petre's to close.
