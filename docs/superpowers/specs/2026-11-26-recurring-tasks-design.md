# Recurring tasks: design

[Deskbar task 0b78269e](https://deskbar.app/task#0b78269e-3365-4401-9d2d-635512282d7a)

## Intent

Petre wants Claude to do a job on a schedule, by itself: "something like check my tasks on youtrack, done every
hour". Each run "starts claude, does the job, then closes it, when the job is done", "so that i don't have to have
many claude sessions lying around doing nothing". And: "if the previous claude is still running, new one should not
start".

So a recurring task is a task with a schedule. On time Deskbar starts a fresh Claude session on it; the session does
what the task's description says, reports, and finishes; its session stops and its window closes; the task goes out
of sight until the next run.

## Goals

- A task can carry a schedule. On each occurrence, a Claude task gets a fresh session that does the job in its
  description and finishes on its own.
- A finished run leaves nothing running: the session stops and its terminal closes.
- At most one run per task at a time. An occurrence that falls while the previous run's session is still live is
  skipped, not queued.
- Between runs the task is off the bar and the panel's default view, the way a snoozed task is.
- A run that needs Petre asks (`task ask`), and the task comes up on the bar like any waiting task.

## Non-goals

- No catch-up. A machine that was off or asleep for six hourly occurrences runs once when it is back, not six times.
- No run history beyond the thread. Each run leaves its comment in the task's thread; Stats count the task once
  (the trade-off Petre accepted with option A).
- No calendar sync, no cron syntax, no per-run tasks (option B was rejected).
- No revival of the removed timer/due model (`TaskItem.cs` header: "no timers, no countdown and no due state").

## Anti-goals

- A run must never take Petre's focus or screen: no window jumping to the front every hour, no workspace switch.
- No idle sessions left behind: a run that has finished and still has a live session is the exact failure he
  named.
- No overlapping runs of one task.

## Approaches

- **A, chosen: one task, a fresh session per run.** The task holds the schedule. On time Deskbar starts a new
  session on it; when the run marks the task done, the session stops, the window closes and the task is put back to
  sleep until the next occurrence. One line on the board; each run's report is a comment in its thread. Cost: Stats
  count it once.
- **B, rejected: a template that spawns a task per run.** Full per-run history and Stats, but an hourly job leaves
  24 closed tasks a day.
- **Considered and dropped: resume one long-lived session each run** (wake it with a comment, as comments wake
  sessions today). It would remember the previous run, but it is the long-lived session Petre does not want, and its
  context grows run after run.

## How it works

### The schedule (Core)

A new optional field on `TaskItem`, `Repeat` (a `Maybe<TaskRepeat>`), stored as a nullable DTO field in tasks.json
(version stays 2, as with every optional field), plus `NextRun` (`Maybe<DateTimeOffset>`, UTC) holding when the next
occurrence is due.

Schedule forms, parsed by one Core function that the CLI and the menu share, the way `TaskSnooze.Parse` is shared:

- `every <n>m` / `every <n>h`, minimum 15 minutes, optionally with a daily window `09:00-19:00` (runs only inside it;
  the first run of a day is at the window's start). Default for the menu's hourly choice: no window.
- `daily HH:MM`, `weekdays HH:MM`, `weekly <day> HH:MM` (local time).

The next occurrence is always computed from now, after the run that just ended (or when the schedule is set), never
by adding intervals to a missed time. That is what makes "no catch-up" fall out.

### Between runs

When a repeat is set, and after every run, the task is snoozed until `NextRun` (the existing `SnoozedUntil`, so the
bar, the panel, the moon and "Wake now" all work unchanged). Petre's "Wake now" on a recurring task starts a run
straight away and the schedule continues from then.

### A run starts

The existing 30-second snooze sweep (`TaskService.WakeDue`, App.xaml.cs) also looks for recurring tasks whose
`NextRun` has passed. For each one:

- Skip, and set `NextRun` to the following occurrence, if the task has a live session (`claude agents` lists it), if
  it waits on Petre (`WaitingOnYou`, a question is open), or if it is closed. Closing a task ends its repeats; reopening
  resumes them from now.
- Otherwise: clear the snooze, set the status to InProgress, and start a fresh session with the quiet start
  (`ClaudeTaskActions.StartQuietly`, first message from `ClaudeStart`): no terminal, no focus taken. The run starts in
  the repeat's folder (below), never through the folder pick list, since nobody is there to answer it.

The repeat stores its folder. Setting a repeat records one: from the menu, the task's latest session's folder, or the
same folder pick "Start Claude" shows when there is none or several; from the CLI, `--folder <path>`, or the calling
session's folder with `--session`. A repeat whose folder has gone (a removed worktree) does not run; it comments once
in the thread and asks Petre for a folder. (My call: the reviewer found unattended starts refuse with zero or several
candidate folders, `RemoteControl.Tasks.cs:592`.)

The first message tells the session it is a scheduled run: do what the description says, write one comment with the
result only if there is something to say, and finish with `task done`; ask with `task ask` only when Petre must act.
Wording is the builder's, inside `ClaudeStart`'s 1000-character cap.

The model: there is no per-task model today, only the one `claude-model` setting (default opus) plus a `--model`
on a single start, and the session-model-pick design ruled a per-task model out. An hourly job on Opus burns the
weekly limit. OPEN, Petre's call: does the repeat carry its own model (stored with the schedule, chosen when the
repeat is set), or do runs use the global setting like every other start?

### A run ends

For a recurring task, `task done --session` means "this run is finished", not "the task is finished". So the board
moves it straight to Backlog and snoozes it until the next occurrence, instead of pinning it as Done for Petre to
close. The run's last comment stays in the thread.

Stopping the run's session: today `SessionKeeper.Stops` stops only a Done task's session, after a minute idle and
only while no Deskbar window is attached to it (`SessionKeeper.cs:245`). It learns a second case: a recurring task
between runs (has a repeat, not InProgress) whose session is idle is stopped the same way. If Petre has the run open
in a window, it stays until he closes it, and the next occurrence skips while it is live (his rule).

Waking between runs: `SessionKeeper.Plan` resumes the session of any open Claude task that is not listed, snoozed
or not (`SessionKeeper.cs:139`). That would revive each finished run two minutes later as an idle session, and the
next occurrence would then skip forever. So a recurring task between runs is exempt from the keeper's resume.
Snooze on other tasks keeps its current meaning. A comment from Petre on a recurring task between runs starts a run
straight away (a fresh session that reads the thread), as "Wake now" does, rather than resuming the old one. (My
calls, from the reviewer's findings.)

A run that dies (usage limit, crash, killed terminal) without marking done: the task is InProgress with no live
session, so it is not "between runs" and the keeper resumes it as it does any open task. That is fine for one run.
The sweep's "live session" check keeps a second run from starting on top of it.

### Setting it

- CLI: `deskbar task repeat <id> <schedule>` and `deskbar task repeat <id> off`, listed in `ControlUsage`; `task list`
  shows `repeat` and `nextRun`.
- Tasks panel and task window: a "Repeat" item in the line's "⋯" menu with a few choices (every hour, every hour
  09:00-19:00, every day at 09:00, every weekday at 09:00, every Monday at 09:00, Off) and "Custom…" taking the same
  text as the CLI.
- A small repeat mark on the task's line (panel and bar) with the schedule and next run in its tooltip. The glyph is
  the builder's; it gets the mockup-before-visual-changes treatment before code.

### Who may set it

Petre, through the menu or the CLI. A task session may set a repeat on its own task with `--session`. The manager
may set one with the id form, `task repeat <id> <schedule> --folder <path>` (its `--session` forms are refused today,
`RemoteControl.Tasks.cs:110`, and stay refused); that is how "do this every hour" said to the manager becomes a
recurring task. The manager still may not close or finish anything.

## Constraints

- Core stays pure and testable: the schedule's next-occurrence function and the sweep's decision (which tasks start,
  which skip) are pure functions over the board and now, tested in `Deskbar.Core.Tests`; the App only starts sessions.
- One Core parser for the schedule text, shared by CLI and menu.
- Times stored UTC; schedules interpreted in local time, so a daylight-saving change moves `daily 09:00` with the
  clock.

## Left to the builder

- The repeat glyph and tooltip wording (after a mockup).
- The first-message wording for a scheduled run.
- Whether the sweep runs inside `WakeDue` or beside it.
- How a task assigned to Petre (not Claude) behaves with a repeat. Proposed: it simply comes back on its schedule
  (reopened to Backlog and woken) with no session started. Cheap with the same machinery; confirm with Petre if it
  costs more than a few lines.
