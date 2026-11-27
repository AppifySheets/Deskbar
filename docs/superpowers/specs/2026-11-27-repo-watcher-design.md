# One watch for every repository

Date: 2026-11-27. [Deskbar task c8e6967d](https://deskbar.app/task#c8e6967d-1586-4183-a79c-08ed98d63f89). Related:
task 43765c61 (a reported deploy wakes the manager, [Deskbar #654](https://github.com/AppifySheets/Deskbar/pull/654)).

## Why

Petre's words: "when multiple features are being implemented per repo, let one agent per repo take on the role of the
watcher. it should monitor pr merges and deploys and report to the manager. if that agent dies, manager should give the
watcher role to another one."

Today every session with an open pull request runs its own watch (the session briefing's "keep it on top of its base"
line, a Monitor polling GitHub every minute, re-armed every 30 minutes at a turn each, and silently gone when a session
forgets) and, after the merge, waits on the release and the deploy itself. Five features in one repository are five
identical loops.

How the design got here, in Petre's answers:

1. Asked what the watcher takes over: **all of it** (the base, every open pull request, merges, releases, deploys); the
   other sessions stop watching their own pull request.
2. Asked who hands the role on: **Deskbar**, not the manager (no tokens, every day).
3. "would it make sense to have a single session for ALL repos?", then, on the cost of one session's context growing
   across every repository: **"dumb, haiku or something"**.
4. Offered the choice of cutting the model out entirely: **"do what you like"**. Cut (approach 4 below).

## Approaches

1. One watcher per repository: a feature session that also watches, with a watch script, a heartbeat and handover
   between sessions. Dropped after answer 3: its machinery existed only because the watcher was somebody's feature
   session.
2. One long-lived Haiku session for all repositories. Cheap per token, but its context grows all day across every
   repository, and a long-lived session is the one that dies on a usage limit or a reboot.
3. Deskbar polls; a fresh Haiku run follows each merge's release and deploy. Written up, then cut: the run could only
   see GitHub, where following a merge commit's runs is mechanical, and most deploys here (a Portainer redeploy, an
   app's log) are beyond it, so it would hand them back to the session it reported to. A new kind of background session
   (folder, briefing, queue, death detection) for that was not worth it.
4. **Deskbar polls and follows the merge commit's runs itself, no model (picked).** Has the base moved, does a pull
   request conflict, did it merge, did the runs on the merge commit pass: all mechanical, all one poll, no tokens.
   "If that agent dies" goes away with the agent; when Deskbar cannot poll, the sessions fall back (below).

## What was found before designing

- Deskbar already runs gh.exe, signed in as Petre (`GitHubCli`, 5000 calls an hour), for questions and the running-change
  rings. That look cannot be reused as it is: it asks only for state, closedAt and mergeCommit, treats merged as final
  for the process's life and backs off 30 minutes after a failure. The poll needs its own call and its own pace.
- A comment from a session does not wake another task's session; CommentWaker delivers Petre's comments, the manager's
  and Deskbar's own requests (Reevaluate, the stalled ask, Regenerate) through one queue, which can resume a stopped
  session. A nudge fits there.
- StalledTasks asks every In progress Claude task quiet for 15 minutes whether it waits on Petre, then hands it back.

## The design

### In flight

A task is **in flight in a repository** when it has a Claude session, is not Done or Closed, is not a recurring task
between runs, and has a pull request in that repository not known to be closed unmerged. Its Done ends flight: Done
comes last in the briefing's order (merged, released, deployed, done), the same in Deskbar's own repository where done
follows the release. Snoozed tasks count: their pull request still falls behind main.

Every repository with a task in flight is watched, even one with a single pull request: the poll is free.

### Deskbar's poll

`RepoWatch` in Core holds the rules as a pure function; a watcher in the app asks gh once a minute, per repository with
a task in flight, one GraphQL query (`gh api graphql`): each in-flight pull request's state, mergeable flag, base branch
and merge commit, and each of those base branches' head.

- **Base moved** (a head other than the last one seen) or **a pull request turned conflicting**: nudge that pull
  request's session: "the base moved to abc1234" or "conflicts with main": rebase and push, by the repository's own
  rules. One nudge per pull request per base head. A snoozed task is nudged only for a conflict, so its stopped session
  is not resumed every time main moves.
- **Merged**: only a pull request Deskbar saw open and then merged is followed (below), and its session is nudged once:
  "merged; Deskbar follows the runs on the merge commit and nudges you with the result". One first seen already merged
  (at rollout, after Deskbar was off, a link recorded late) is left to its session under the old rule.
- **Stalled asks stand down** for a task whose merge is being followed: it waits on Deskbar by design, and StalledTasks
  would otherwise ask it every 15 minutes and hand it back to Petre.
- A stacked pull request (base is another pull request's branch) watches that branch's head the same way.

### Following a merge

For each followed merge, the poll also asks for the workflow runs on its merge commit (`gh run list --commit <sha>
--json status,conclusion,name,url`) until every run has finished, or 3 hours pass.

- **All finished green**: nudge once: "the runs on your merge commit finished green (names); carry on in the briefing's
  order". In Deskbar's own repository that is the release.
- **One finished red**: nudge "<run> failed on your merge commit: <url>", and wake the manager (below).
- **No runs after 10 minutes**: nudge "merged; no workflow runs on the merge commit"; the session goes on as today.
- **Still running after 3 hours**: the follow ends with a nudge saying so.

The deploy stays the session's: it knows its app, and most deploys here are beyond GitHub. What changes is that it no
longer waits in a loop for the runs; it stops and is nudged.

### The nudge

A delivery on CommentWaker's queue like Reevaluate, with SessionBriefing.WatchNudge wrapping the text. It leaves no
comment in the thread, so twenty base moves a day do not fill twenty threads, and it takes the queue's usual roads (an
open task's stopped session is resumed: it has to rebase anyway). Undeliverable: dropped with a trace line.

### The sessions

- The prompt line (SessionBriefing.Prompt) says, while the task is in flight and the last poll of its repository
  succeeded within 5 minutes: "Deskbar watches your pull request and nudges you when its base moves, it conflicts or
  its merge's runs finish: do not run your own watch loop; stop one that runs."
- The briefing's "keep it on top of its base" line reads "unless your prompt line says Deskbar watches your pull
  request". So with gh failing for 5 minutes, or Deskbar not running at all (no prompt line), a session watches by
  itself as today. The repository's CLAUDE.md rebase rules (no CI, rebase only before the merge) still govern how.
- The "After the merge" line says Deskbar follows the merge commit's runs while it watches, so the session need not
  wait on them in a loop; the deploy check stays the session's.
- This repository's CLAUDE.md watch section and the refresh-pr skill say the same (other repositories' CLAUDE.md files
  are left alone; the briefing line covers them).

### Reporting to the manager

A red run on a merge commit wakes the manager as a third ManagerWakes trigger beside hand-backs and deploys: same
30-minute gap, same busy rule, today's manager only, with the repository, the run's link and the task. Green runs and
routine merges never reach it (task 6a87df67: "manager doesn't have to tell me each merge"). The deploy trigger from PR
654 stays.

## Non-goals

- Deskbar merges, pushes and rebases nothing: it nudges.
- No new UI; nudges and the manager's wake are the whole surface.
- No deploy detection beyond GitHub's runs.

## Calls made in writing this up

- No Claude session holds a watcher role: Deskbar's poll is the watcher ("do what you like").
- Every repository with a task in flight is watched, even one with a single pull request.
- Flight ends at the task's Done.
- Nudges leave no thread line; a snoozed task is nudged only on a conflict.
- Only merges seen happening are followed; a task whose merge is followed gets no stalled ask.
- A red run wakes the manager; green ones do not.
- When the poll fails or Deskbar is down, sessions watch for themselves, as today.

## Left to the builder

- The GraphQL query's shape; the merge commit's runs by `gh run list`.
- Where the followed merges live (state.json, so a restart loses none); base heads and nudge memory may stay in
  memory, a restart then nudges once at most.
- Trace lines for poll answers and nudges, behind the one trace switch.
- Tests: the RepoWatch rules, StalledTasks' exception, ManagerWakes' third trigger, the prompt line.
