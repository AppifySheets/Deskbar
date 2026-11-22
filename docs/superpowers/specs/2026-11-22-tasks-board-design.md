# Tasks: one list of what's happening, Claude sessions included

Date: 2026-11-22. Replaces the parking-and-timers part of
`2026-11-19-tasks-in-the-bar-design.md` (shipped in v2.4.143, PR #356) and the task window built on
`feat/task-window` (never merged). Read that older spec for the store's history; this one decides what tasks are now.

## Why

Petre runs many Claude Code sessions at once, each in a VS Code window on some workspace, and comes back to them
later. He wants to see what is happening in each workspace at a glance, review what every session is working on, and
be told plainly when a session needs a decision from him. Alongside that he wants one place to jot something down
before he forgets it, work or not ("something I want to buy"), on a workspace or on none.

He tried the first version and did not like it: a stopwatch that parked a workspace for a few minutes, countdown pies,
and a window that opened onto an entry form. He wants no timers and no countdown.

Later, not now: a phone (the list may leave the machine; "it's me who's supposed to make a decision after all"),
possibly a paid feature for workplaces, and possibly a count-up focus timer.

## Goals

- One list of tasks, everything piled together: tasks Claude sessions work on and Petre's own notes.
- Every Claude session works against a task. Deskbar tells each session to join the open task its work continues, or
  to create one.
- At a glance on the bar: the three most recent tasks, Claude's easy to tell apart, the ones waiting on Petre first.
- A tasks window with every task and its status, opened from the bar's list.
- Clicking a task takes Petre to its session (or, for a note, to its workspace).
- Each task carries a markdown comment thread.
- Statuses: Backlog, In progress, Waiting on you, Done, and Closed, which only Petre sets.

## Non-goals (this design)

- Timers of any kind. The stopwatch, the pies, the due cards, `task timer` and `--in` are removed.
- A focus or pomodoro timer. Petre's idea, counting up, never down; a follow-up once this is in use.
- Sync with a phone or an external task manager. Microsoft To Do is out ("crap"); Google Tasks or Deskbar's own sync
  is the later step. Answering a session from the phone is part of that step.
- A paid gate. `TasksGate.Free` stays true.
- A separate notes list. Notes and Claude's tasks share the one pile.
- Replacing Petre's own `session-title.py` hook. A task title and the session title are close cousins; unifying them
  can come later.

## Anti-goals (would make it a failure even if it works)

- An entry form or a chore. Every earlier task manager failed Petre because it took effort; here Claude keeps its tasks
  current and Petre only types when he wants to.
- Claude's tasks lost in a crowd. A session waiting on Petre must never be pushed out of sight by his errands or by
  busy sessions.
- A list that churns: a status line changing every few seconds must not reorder what he is looking at.
- Claude settling Petre's own notes on its own.

## Constraints

- Deskbar is a general virtual-desktop product on the Store, not a Claude-only tool. Tasks work without Claude (notes,
  and anything that calls `deskbar task`); Claude is the first and best-integrated reporter.
- Other programs reach the app only through the control pipe (CLAUDE.md, #180). Every bar action has a pipe twin listed
  in `ControlUsage`.
- No new global hotkey, no sound, no Windows toast (CLAUDE.md, #123).
- Claude Code integration goes through what `ClaudeSkill.cs` already maintains: the skill file it writes into
  `~/.claude/skills/deskbar/`, and its hook entries in `~/.claude/settings.json` running `claude-hook.cmd`, with every
  rule that file states (only when `~/.claude` exists, only while the setting is on, exit 0 always, the quoted
  forward-slash command line, only Deskbar's own entries touched).
- The store stays behind `ITaskStore`; ids are Guids made on the device; every time is UTC; every task carries
  `UpdatedAt` (kept for the later sync).
- Code style per CLAUDE.md. Release notes in the pull request, no version edits.

## Approaches considered

1. **Grow the task model that shipped in #356** (chosen). One store holds every task: status, sessions, comments. The
   timer fields go. The branch's window controller and row actions are reworked into the board. Claude's hook script
   handles more events and passes Deskbar the session id. Cost: changes a shipped model and its file format, and the
   `feat/task-window` branch is reworked rather than merged.
2. **A separate list of "work items" next to the timer tasks.** Leaves what shipped untouched, but two things would be
   called tasks. Dropped once timers went, and it worked against "all together" anyway.
3. **Google Tasks as the store from day one.** Phone access at once, but Google Tasks has only open and completed, one
   plain notes field and no idea of a session, and the bar would depend on the network. Kept for the sync step behind
   `ITaskStore`.

Petre picked 1 on 2026-11-22.

## What Petre decided

### The task

A task has a title, a status, an optional workspace (or none), who made it (Petre, or a Claude session), the Claude
sessions that have worked on it, a short status line, and a comment thread. Several sessions can work on one task.

### Statuses

- **Backlog**: not started. Petre's notes start here. A Claude session can also add a follow-up here.
- **In progress**: a Claude session has joined it.
- **Waiting on you**: a session asked Petre something. His next message to that session moves the task back to In
  progress (the same moment the bar's blink clears today). The only status that asks him to act: it sorts first and is
  orange.
- **Done**: Claude marks it on a clear finish (for example a merged pull request). A Done task is not gone: it stays in
  the bar's list and the window, waiting for Petre to look and close it (Petre, 2026-11-22: "it should set it to done
  and wait for me to close it"). A Claude session never deletes a task.
- **Closed**: Petre closed it. Only Petre closes. Closed tasks leave the bar's list and the window's default view and
  stay readable, comments included, under a "Closed" filter. Ticking one of Petre's own notes closes it at once (he is
  the one deciding, so there is nothing to wait for).

Petre can set any status by hand from the window, and can reopen a closed task.

### Claude sessions

- At the start of a session Deskbar tells it the open tasks and tells it to join the one its work continues, or create
  a new one. Afterwards each prompt gets one line reminding it of its task.
- The session keeps its task's status line current ("waiting for CI on #398").
- When the session needs a decision from Petre it asks through its task: the task becomes Waiting on you, and the
  question shows on the line. A reply finishing is not an ask; that stays the blink it is today.
- A session only settles tasks it is working on. Deskbar's automatic changes touch only the session's own task.
  Deskbar cannot tell who typed a `deskbar task` command, so the skill tells sessions to leave Petre's notes alone
  unless he asks; that is a rule by instruction, accepted for a personal tool.
- A task whose sessions have all ended stays open and says "no live session".

### The bar

A list at the bottom of the bar: the three most recent tasks, a "N more" button that opens the window, and "+ Add".

- Order: Waiting on you first, then newest first. A status line changing does not move a line; only a new task and a
  new ask do.
- A Claude task is marked so it can be told apart at a glance: an orange edge and tint, a CLAUDE tag, the VS Code icon
  of its session's window, and the session's title in front of the text. Petre's notes have a plain tick box.
- "+ Add" opens a text box in the list itself, with the current workspace's chip ticked, plus "none" and "other…".
  Enter adds, Esc drops.
- Clicking a Claude task takes Petre to its session; clicking a note goes to its workspace; ticking a note's box closes
  it. A Done Claude task shows a close button on its line.

Mockups: `.superpowers/brainstorm/876-1795339550/content/claude-marked.html` (option A, chosen) and
`tasklist-bottom.html` (the add box).

### The window

Opened from "N more". It opens onto the board, not an entry form: every open task grouped by status (Waiting on you,
Done, In progress, Backlog), filters by status (Closed among them) and by workspace, and per line the title, status line, sessions (live or
ended), workspace and comment count. A line expands to its comment thread. "+ Add" is one button.

Mockup: `.superpowers/brainstorm/876-1795339550/content/tasks-window.html`.

### Comments

A thread per task. Each comment has an author (Petre, or a session) and a time, and its text is markdown, rendered.
Claude writes its first comment with the detail behind an ask; Petre or later sessions add more. Design the thread as
something sessions can read (the phone step will use it to carry Petre's answers back), but nothing passes comments to
sessions now.

### Going to a session

Claude Code's VS Code extension opens a session from `vscode://anthropic.claude-code/open?session=<id>` (its URI
handler, read from the installed extension 2.1.330). Deskbar brings that session's VS Code window forward, then opens
the link. A task with several sessions goes to the one waiting on Petre, else the most recent.

### Who gets it

Claude sessions report to the task list under a setting of its own, on by default (Petre, 2026-11-22, chose this over
reusing "Teach Claude Code about Deskbar" and over off by default). It sits next to that setting in Manage, has a pipe
twin like it, and turning it off takes out only the task hooks and the skill's task section; the existing skill and the
ask hooks keep working. Turning "Teach Claude Code about Deskbar" off turns this off too, since it writes into the same
files.

## Calls made in the conversation that the builder should keep

- Existing `tasks.json` tasks migrate: live ones become Backlog notes (their workspace kept), done ones become Closed,
  timer history is dropped. The old file is kept beside the new one, as the store already does for an unreadable file.
- A permission prompt (Claude Code's Notification hook) blinks as today and does not change the task's status. Only an
  explicit ask does.
- `claude-hook.cmd` also runs on SessionStart, UserPromptSubmit and SessionEnd and passes the hook's JSON (session id,
  folder, event) to Deskbar. What the session should read comes back as the hook's output, the way `session-title.py`
  already returns context.
- The skill file Deskbar writes gains a section on tasks: joining or creating, the status line, asking, commenting, and
  leaving Petre's notes alone.

## Calls made after the builder check (Claude's, for Petre to check)

- **What a Claude line shows.** The task's title, then its status line or, while it waits on Petre, the question.
  "The session's title" in the mockups is the task title; no per-session title is stored or read from Claude Code.
- **The bar's order.** Waiting on you first, then Done (waiting for Petre to close it), then everything else by the
  time it was created or last asked. A status line never moves a line. When more tasks wait on Petre than fit in the
  three slots, the "N more" button says how many of the hidden ones wait on him, in orange ("+2 waiting"), so none is
  out of sight without a sign.
- **What a session is offered, and joining a note.** At session start a session is offered the open tasks on its own
  workspace, plus the open Claude-made tasks on no workspace. Petre's notes on no workspace (errands) are never
  offered. A session may join one of Petre's notes on its workspace; it then becomes Claude's work (orange, Claude can
  mark it Done, Petre closes it), because a session working on it is what makes a task Claude's. "Claude's" is derived
  from having sessions, not stored.
- **Asking and the blink.** `task ask` also starts the blink. The blink clears on arrival at the workspace as today; the
  task stays Waiting on you until Petre's next message to that session (UserPromptSubmit), which moves it back to In
  progress.
- **After Done or Closed.** A message to a session whose task is Done moves it back to In progress (he is continuing
  the work). A session whose task Petre has closed is told so on its next prompt, and that new work needs a new task.
- **Non-interactive sessions.** Headless runs (`claude -p`, SDK runs, scheduled agents, loop check-ins) get no task
  instructions and make no tasks. The hook JSON says whether a session is interactive; if it does not, the builder
  finds the signal by measuring.
- **Pull request order, with no gap.** Every merge releases, so no merged pull request may leave the app without a
  task UI. The first pull request removes the timers and adds the bar's list with notes together; Claude's hooks and
  skill come next; the window and comments last.

## Left to the builder

- The pipe commands and their names (join, create, status line, ask, comment, done, rename, move, delete, list), as long
  as every bar and window action has a twin in `ControlUsage` and `task list` stays JSON.
- How the hook reaches the pipe. Measure first: a hook that runs on every prompt starts the Deskbar exe each time; if
  that adds a noticeable pause before Claude starts working, find a lighter path.
- Whether the `vscode://` link lands in the VS Code window Deskbar just brought forward when several are open. Measure
  it; if it does not, fall back to bringing the window forward only.
- Markdown rendering in WPF: which subset (paragraphs, emphasis, code, links, lists at least) and how. No WebView.
- How a session's VS Code window is found from its folder; `attention --from` already does this.
- Sessions run from a terminal rather than VS Code: a click goes to that terminal window's workspace.
- Layout details of the bar's list and the window within the mockups, the bar's existing gutter and lane rules, and
  DPI.
- Splitting the work into pull requests within the order and the no-gap rule above.
