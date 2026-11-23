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

- Starting anything on its own. A session starts only on Petre's click (CLAUDE.md: "No launching, no restore
  prompt"; this is a launch at his request, like `deskbar.cmd start`). A reboot does not bring sessions back by
  itself either.
- Stopping a session Petre did not ask to stop. Closing a window never stops one.
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

- Windows Terminal, one window per session: `wt -w new` with the chosen profile, the session's folder, a title naming
  the task, and `claude attach <id>`. The profile is one of the three looks on trial (Dark Modern, Night, Warm, all
  10 pt, in a Windows Terminal fragment); Petre picks one, and Deskbar ships it as its own fragment so the profile
  exists on any machine with the feature on.
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

A reboot or sign-out ends every background session. Clicking the task afterwards resumes it as an ended session:
the conversation comes back, a turn that was running when the machine went down does not.

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
- Measure, don't theorise (project CLAUDE.md). Three behaviours are unmeasured and come first in the plan, in a
  throwaway folder and off Petre's screen where possible: closing a real Windows Terminal window on an attached
  session, attaching from a second window while one is attached, and Deskbar being killed while it is starting a
  session.
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
