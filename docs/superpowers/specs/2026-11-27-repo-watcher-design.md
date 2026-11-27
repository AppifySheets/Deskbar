# One watch for every repository

Date: 2026-11-27. [Deskbar task c8e6967d](https://deskbar.app/task#c8e6967d-1586-4183-a79c-08ed98d63f89). Related:
task 43765c61 (a reported deploy wakes the manager, [Deskbar #654](https://github.com/AppifySheets/Deskbar/pull/654)).

## Why

Petre's words: "when multiple features are being implemented per repo, let one agent per repo take on the role of the
watcher. it should monitor pr merges and deploys and report to the manager. if that agent dies, manager should give the
watcher role to another one."

Today every session with an open pull request runs its own watch (the session briefing's "keep it on top of its base"
line, a Monitor polling GitHub every minute, re-armed every 30 minutes at a turn each) and, after the merge, follows the
release and the deploy itself. Five features in one repository are five identical loops and five sessions each waiting
for the same deploy.

How the design got here, in Petre's answers:

1. Asked what the watcher takes over: **all of it** (the base, every open pull request, merges, releases, deploys); the
   other sessions stop watching their own pull request.
2. Asked who hands the role on: **Deskbar**, not the manager (no tokens, every day; the manager runs only on days he
   opened it and pays a Fable turn per handover).
3. "would it make sense to have a single session for ALL repos?", then, on the cost of one session's context growing
   across every repository: **"dumb, haiku or something"**. So one watcher for all repositories, on a cheap model.

## Approaches

1. One watcher per repository, a feature session that also watches, with a Deskbar-provided watch script, a heartbeat
   and per-repository handover. The first written design. Dropped after answer 3: most of its machinery (picking a
   holder, beats, Monitor re-arms) exists only because the watcher was somebody's feature session.
2. One long-lived Haiku session for all repositories. Cheap per token, but its context grows all day across every
   repository and is read again each turn, and a long-lived session is the one that dies on a usage limit or a reboot.
3. **Deskbar watches, a fresh Haiku run follows each merge (picked, my proposal on answer 3).** The part with no
   judgement (has the base moved, does a pull request conflict, did it merge) is a poll Deskbar runs itself for every
   repository, at no token cost. The part that needs a model (follow the release, find the deploy) goes to a Haiku
   session started fresh for each batch of merges, which ends when it has reported. Its context never grows, and "if
   that agent dies, give the role to another one" becomes Deskbar starting a new run for what the dead one left.

## What was found before designing

- Deskbar already asks GitHub about pull requests through the authenticated gh CLI (`GitHubCli.Arrival`, used by
  RunningChangeWatch), so a per-minute poll of a few bases and pull requests stays far under gh's hourly limit. That
  look cannot be reused as it is: it asks only for state, closedAt and mergeCommit, treats merged as final for the
  process's life and backs off 30 minutes after a failure. The poll needs its own call (baseRefName, mergeable, the
  base head) and its own pace.
- A comment from a session does not wake another task's session; CommentWaker delivers Petre's comments, the manager's
  and its own requests (Reevaluate, the stalled ask, Regenerate) through one queue, which can resume a stopped session.
  A nudge fits there.
- `task deployed <id> -` works on any task and stamps all of its pull requests.
- The manager is already a Deskbar-started background session in its own folder with a briefing file and a cut-down tool
  list (ManagerSession.StartArguments). The watcher copies that shape.
- Recurring runs already establish "every run is a fresh session that reads what it needs, never one session woken
  again" (spec 2026-11-26-recurring-tasks-design.md).

## The design

### In flight

A task is **in flight in a repository** when it has a Claude session, is not Done or Closed, is not a recurring task
between runs, and has a pull request in that repository not known to be closed unmerged. Its Done ends flight: Done
comes last in the briefing's order (merged, released, deployed, done), the same in Deskbar's own repository where done
follows the release. Snoozed tasks count: their pull request still falls behind main.

Unlike the first design there is no "two or more" threshold: the poll is free, so a repository with one task in flight
is watched too, and no session runs its own loop any more.

### Deskbar's poll (no model)

`RepoWatch` in Core decides, a watcher in the app asks gh, once a minute, for every repository with a task in flight:

- the head of each in-flight pull request's base branch;
- each in-flight pull request's state and mergeable flag.

What it does with the answers:

- **Base moved** (a new head since the last poll) or **a pull request turned conflicting**: nudge that pull request's
  session, "base moved to abc1234" or "conflicts with main": rebase and push, by the repository's own rules. One nudge per
  pull request per base head, so a session is not nudged twice for one move. A snoozed task is nudged only when its
  pull request conflicts, never for a base move alone, so a snoozed task's stopped session is not resumed every time
  main moves (as ManagerWakes leaves snoozed tasks alone).
- **Merged**: only a pull request Deskbar saw open and then merged goes on the watcher's queue (below), and the task's
  session is nudged once, "merged; the watcher follows the release and deploy and will nudge you, carry on or stop".
  One first seen already merged (at rollout, after Deskbar was off, a link recorded late) is left to its session under
  the old rule, so the rollout starts no run for merges already being followed.
- **Stalled asks stand down** for a task with a merge on the queue and no report yet: it waits on the watcher by
  design, and StalledTasks would otherwise ask its session every 15 minutes and hand it back to Petre.
- The stacked case: a pull request whose base is another pull request's branch watches that branch's head the same way.

### The nudge

A delivery on CommentWaker's queue like Reevaluate, with SessionBriefing.WatchNudge wrapping the text. It leaves no
comment in the thread, so twenty base moves a day do not fill twenty threads, and it takes the queue's usual roads
(an open task's stopped session is resumed, since it has to rebase anyway). Undeliverable: dropped with a trace line.

Also a verb, `task nudge <id> -`, for the watcher's run only (refused from any other caller).

### The watcher run (Haiku)

A Deskbar-started background session like the manager: its own folder under the state directory (`watcher`), a
briefing file, `--model haiku`, `--permission-mode dontAsk`, and an allow list of `gh` and the shim's `task` verbs plus
Read (for a repository's CLAUDE.md). No Edit, Write, Agent, Monitor, timers or web tools.

- **Started** when the merge queue is not empty and no run is listed; its first message lists each queued merge:
  repository, pull request link, task short id and title, the task's folder.
- **Its job, per merge:** read the repository's CLAUDE.md for its release and deploy steps; follow the release run with
  `gh run watch` if there is one; look for the deploy evidence it can reach with gh (a deploy workflow run, a published
  tag, a GitHub deployment). Then report one line per merge with `task watch-report <id> -`, one of: released (and in
  what version), deployed (and the evidence), "yours to check" (the deploy is not visible from GitHub: a person
  redeploys in Portainer, the evidence is an app's log), nothing to follow (no release, no deploy step), or failed
  (a run went red, and where).
- **It waits only inside `gh run watch`**, never in a loop and never for a deploy GitHub cannot show: that one is
  reported "yours to check" at once, and the task's session checks its own evidence as the briefing says today. Not
  seeing a deploy is not a failure.
- **It marks nothing deployed and nothing done.** Deskbar turns each report into a nudge to the task's session with the
  watcher's line ("released in v2.6.70, finish" or "the deploy workflow for stack X ran green at 10:42; check your
  service's evidence and report task deployed"). The session, which knows its app and runs a stronger model, confirms
  and finishes in the briefing's order. A Haiku run's guess never reaches a task's deployed state.
- **Ends** once every queued merge has a report. A run takes merges that arrive while it works when it next asks
  `task watch-queue` (it prints what is still unreported), so one run can follow several.
- **Dies** (not listed, or listed without a process, for 5 minutes, or alive for 3 hours, longer than any release run
  it waits on): Deskbar stops it if it is still listed and starts a new run with the merges that have no report. This is
  the "give the role to another one". A merge whose run died twice is reported to the manager as failed rather than
  started a third time.
- Its folder is not a repository, so it never commits or pushes anything.

### The sessions

- The briefing's "keep it on top of its base" line becomes: "Deskbar watches your pull request and nudges you when its
  base moves or it conflicts; then rebase and push straight away. Do not run your own watch loop." The repository's
  CLAUDE.md rebase rules (no CI, rebase only before the merge) still win on how to rebase.
- The "After the merge" line adds that the watcher follows the release and deploy and nudges with what it found, so the
  session need not wait on it in a loop.
- This repository's CLAUDE.md watch section and the refresh-pr skill are changed to match (my call); other
  repositories' CLAUDE.md files are left alone, and a session reading an old watch rule there is covered by the
  briefing line.

### Reporting to the manager

A `failed` watch-report (release red, deploy not found within the repository's usual time, main red) wakes the manager
as a third ManagerWakes trigger beside hand-backs and deploys, same 30-minute gap, same busy rule, today's manager only.
Routine reports do not reach it (task 6a87df67: "manager doesn't have to tell me each merge"). The deploy trigger from
PR 654 stays: a session's confirmed `task deployed` is still the notice that other merged tasks of the repository may
be in the same deploy.

## Non-goals

- The watcher merges nothing and pushes nothing.
- No new UI. A watcher run is listed like the manager's in Claude's own session list; a badge or button is a later idea.
- No watcher across machines.

## Calls made in writing this up (check them)

- Every repository with a task in flight is watched, even one with a single pull request.
- A fresh Haiku run per batch of merges, never one long-lived session.
- The watcher reports; only the task's own session marks deployed and done.
- Flight ends at the task's Done.
- Nudges leave no thread line.
- A deploy GitHub cannot show is "yours to check" for the task's session, not a failure.
- Snoozed tasks are nudged only on a conflict.
- Only merges seen happening go on the queue; tasks with a queued merge get no stalled ask.
- A failed report wakes the manager; routine ones do not.

## Left to the builder

- Where the merge queue, the reports and the last-seen base heads live (state.json for the queue and reports so a
  restart loses nothing; base heads may stay in memory, a restart then nudges once at most).
- The watcher's briefing text and its tests (line cap like the manager's).
- gh call shape and backoff on failure (reuse RunningChangeWatch's).
- Trace lines for poll answers, nudges, runs started and stopped, behind the one trace switch.
- Tests: RepoWatch rules, the run lifecycle, ControlUsage entries for `nudge`, `watch-report` and `watch-queue`.
