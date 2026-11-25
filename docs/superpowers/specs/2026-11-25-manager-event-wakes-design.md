# The manager wakes on events

Date: 2026-11-25. Deskbar task cb3f3770, step 2 of the manager session (task bbf9a6ed, design
`2026-11-25-manager-session-design.md`, section "Later: event wakes").

## Why

The manager from step 1 sleeps until Petre writes to it, so it never notices anything by itself. Petre picked "1 now, 2
later": step 2 has Deskbar wake it when something worth a look happens. The task named three events: a task starts
waiting on you, a session ends without done, a question sits unanswered. Asked what a wake should produce and where he
would see it, Petre said "just guess", and approved the design below ("okay").

## What was found before designing

- "Ended without done" already exists as the stalled hand-back (`StalledTasks`, 15 minutes quiet): the task comes back
  to Petre on the bar and gets `TaskItem.HandedBack`. A wake only adds something if the manager acts on it.
- 24 tasks moved to waiting on you on 2026-11-25 by midday (the status log). One Fable turn per event is about 25 a day
  before batching, against step 1's "light".
- The manager's replies show only in its own window. A wake that writes nothing Petre will see is a turn for nothing.
- The comment inbox and its waiter (`CommentInbox`, `claude-wait-hook.cmd`, the asyncRewake Stop hook) already wake an
  idle running session in under 2 seconds. The manager is left out of it by one carve-out in `ClaudeWait`.

## Approaches

1. Deskbar alone, no manager turn: a fixed nudge comment on a stalled task, an age mark on old questions. No tokens, but
   no judgement, and it is not waking the manager at all.
2. A wake per event. Quickest, but about 25 Fable turns a day or more. Rejected against "light".
3. **Batched, filtered wakes (picked).** Deskbar collects events and wakes the manager at most once per 30 minutes, and
   only when the batch holds an event that needs judgement.

## The design

What wakes the manager (a trigger):

- a stalled hand-back: a Claude task handed back to Petre (`HandedBack`) since the last wake;
- a question unanswered for 2 hours: a task waiting on Petre whose `WaitingSince` passed the 2-hour mark since the last
  wake.

What rides along without waking it: a task that started waiting on Petre since the last wake. The panel already shows
those; the manager just includes them in its list.

What the manager does on a wake:

- for each stalled task, it reads the task's status line and comments on the task, which wakes that session (a manager
  comment wakes, step 1). A status line that says the next move is Petre's ("PR green, ready to merge") gets "run task
  ask"; anything else gets "finish with task done, or carry on". A task handed back a second time today is not nudged
  again: it goes on the list for Petre;
- it writes a short ranked list of what waits on Petre, oldest and most blocking first, with its suggested answer for
  each.

This changes one rule of step 1: the manager now resumes a task's own session without Petre asking. It still never
starts a new session, and it still only comments.

Where Petre sees it: the Manager button shows a dot from a wake until he next opens the manager; the list is the last
thing in its window.

Limits:

- Wakes happen only while today's manager exists, i.e. on a day Petre opened it. That is his opt-in.
- A manager mid-turn (listed busy) is not woken; the batch waits for the next tick after it goes idle.
- At most one wake per 30 minutes. Events are counted from the manager's start (its first turn already read the board),
  then from each wake, so nothing is reported twice and a Deskbar restart reports nothing again.

## How it reaches the manager (my call)

The same roads a comment takes to a task's session (`CommentWake`): the manager's own waiter when it runs (its inbox),
a bare `claude --bg --resume <id> -- "<message>"` when it does not run (measured in step 1 to keep every saved option),
and stop then resume for a background manager whose waiter lapsed. So the carve-out that kept the manager out of the
waiter goes: it gets a waiter like any session, and only Deskbar ever writes to its inbox.

## Where the state lives (my call)

`ManagerRecord` (in `AppState.Manager`) gains three fields, all optional so step 1's saved record still loads:

- `Since`: the moment events are counted from (the start, then each wake). A record without it takes the first tick's
  time.
- `Unread`: a wake was delivered and Petre has not opened the manager since. Drives the dot; cleared by `Open`.
- `HandedBack`: the tasks reported as handed back today, so a second hand-back is marked "do not nudge again".

The record is per day, so these reset with each day's new manager.

## Where it runs

- Core: `ManagerWakes` (pure): from the record, the board, the listed sessions and now, either nothing or a wake (the
  message, the record after it). Every rule above is a test there.
- App: `ManagerWaker` on the sessions poll `ClaudeSessions.Live` (every 15 seconds, already running; no new timer),
  delivering through the inbox or a resume, and saving the record.
- The briefing gains the wake rules; the bar's Manager button gains the dot.

## Left to the builder

- The exact wake message and briefing lines, short.
- The dot's look: the attention dot the bar already draws, in the chip row's style.
