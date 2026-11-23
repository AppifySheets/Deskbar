# Stalled Claude tasks come back to Petre

Date: 2026-11-23. Status: proposed, waiting for Petre's approval.

## Why

Ruling 57 took Claude's tasks off the bar: the bar lists only tasks assigned to Petre, and a session's task is
Claude's while it works. A task comes back only when the session runs `task ask` or `task done`.

Sessions often stop without doing either. On 2026-11-23, 13 of the 14 open tasks assigned to Claude had a live session
sitting idle at the prompt, and most status lines already said the next move was Petre's ("PR #442 green, ready to
merge", "Waiting on Petre: connection file, system id, ssh", "you check out"). None of them showed on the bar.

Petre: "i want the tasks that haven't had any progress from claude and are still open to reappear for me to act upon."
He picked two parts: hand a quiet task back to him (1), and have the Stop hook tell the session to ask before it stops
(3).

## Goals

- An open task assigned to Claude, with no session working on it for 15 minutes, is reassigned to Petre and shows on
  the bar.
- It goes back to Claude by itself once a session works on it again.
- A session that ends a turn waiting on Petre says so with `task ask`, so the bar shows a question, not only a status
  line.

## Non-goals

- Showing every Claude task on the bar. Petre asked for that first; it was argued down because 14 lines that do not
  need him would bury his own 8.
- Changing what `task ask`, `task answered` or `task done` do.
- Waking or resuming sessions. The keeper (SessionKeeper) already does that and is untouched.

## Anti-goals

- A task bouncing on and off the bar every few seconds.
- A task Petre assigned to himself in the tasks window being taken back by Claude.
- Every reply in every session costing a visible extra turn with nothing to say.

## Approaches considered

1. Reassign after 15 quiet minutes (picked). The assignee stays the single answer to "who is this on", so the bar, the
   tasks window's filter and `task list` agree.
2. Bar-only filter ("yours, or Claude's and quiet"). Cheaper, but the data would disagree with the bar, and the quiet
   clock would be lost on a restart. Rejected.
3. The Stop hook nudges the session to run `task ask` (picked, alongside 1). It fixes the cause, but it relies on the
   model and costs a turn, so it cannot replace 1.

## Part 1: the quiet hand-back

### The signal

Deskbar already reads `claude agents --json` every 15 seconds (ClaudeSessions.Poll). Every row, interactive VS Code
tabs included, carries `status`: `busy` or `idle` (measured 2026-11-23: this session `busy`, the rest `idle`). That is
the signal, not the Stop hook:

- it is already polled, so no new hook and no new process;
- it covers a session parked on a background watch, and a session that has gone missing from the list;
- after a Deskbar restart the next poll sees it again.

A task is QUIET when none of its sessions is listed as busy. That covers both an idle session and a session that has
gone from the list. An unreadable list is "cannot tell" and changes nothing, as for the keeper.

### The rule (pure, in Core, beside SessionKeeper)

For each open task that is assigned to Claude and In progress:

- quiet for 15 minutes or more: reassign to Petre and mark it handed back (`HandedBack = now`). The status stays In
  progress, and the status line is what the bar shows.
- a session on it busy again, after the 30-second grace (ClaudeAgents notes that a resumed session reports busy for up
  to 30 seconds while idle): back to Claude, and the mark is cleared.

Only a task carrying the mark goes back to Claude on busy, so a task Petre assigned to himself by hand stays his. The
mark is cleared by every other assignee change (Assign, Ask, Done, Close, Reopen, SetStatus).

When quiet started is kept in memory per task (first poll that saw it quiet). On the first poll after a start, the
clock starts at the task's UpdatedAt instead, so a restart does not hold back a task that was already quiet for an
hour. Nothing new is written for the clock; `HandedBack` is the one new optional field in tasks.json (version 2,
optional, read tolerantly like `assignee`).

### What Petre sees

The task comes back on the bar the way his own tasks are listed, with Claude's status line under the title. It is not
pinned on top like a question: it is his to look at, not a decision Claude is blocked on. When he prompts the session,
the session goes busy and the task leaves the bar within a poll.

Known effect: a session watching CI counts as quiet, so a "CI running" task comes back after 15 minutes. Accepted.

## Part 3: the Stop hook nudge

The Stop hook already runs `claude-hook` (it blinks the workspace). It gets a second job, while sessions keep their
tasks (ClaudeTasksInEffect) and only for an attended session:

Block the stop ONCE per turn, with `{"decision":"block","reason":...}`, when all of these hold:

- `stop_hook_active` is false (so a turn is nudged at most once; Claude Code also caps the loop at 8);
- the session's task is open, In progress and assigned to Claude;
- `background_tasks` and `session_crons` are empty (a session waiting on its own watch is not parked);
- the session has not changed its task's status during this turn (it ran no ask, answered or done).

The reason is short and leaves an out: "If the next move is Petre's (a PR to merge, something to try, a decision),
run `deskbar.cmd task ask` with what he must do. If it is not, stop without saying anything." The blink happens as it
does now.

Cost: in a session with a task, most replies get one short extra turn. It shows in the transcript as "Stop hook
feedback". If that turns out to be too noisy, the fallback is to drop the block and only sharpen the per-prompt
reminder (SessionBriefing.Prompt), at no cost per turn.

## Left to the builder

- Where the 15-minute clock lives (ClaudeSessions' poll handler, feeding a pure Core function).
- Trace lines: one per hand-back and one per return, in the tasks trace.
- Tests: the rule's cases (quiet under and over 15 minutes, busy inside and after the grace, manual assignment kept,
  unreadable list, restart seeding from UpdatedAt) and the Stop hook's block conditions.
- README and the manual test script get a line each.
