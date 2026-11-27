# One watcher per repository

Date: 2026-11-27. [Deskbar task c8e6967d](https://deskbar.app/task#c8e6967d-1586-4183-a79c-08ed98d63f89). Related:
task 43765c61 (a reported deploy wakes the manager, [Deskbar #654](https://github.com/AppifySheets/Deskbar/pull/654)).

## Why

Petre's words: "when multiple features are being implemented per repo, let one agent per repo take on the role of the
watcher. it should monitor pr merges and deploys and report to the manager. if that agent dies, manager should give the
watcher role to another one."

Today every session with an open pull request runs its own watch (the session briefing's "keep it on top of its base"
line, a Monitor polling GitHub every minute) and, after the merge, follows the release and the deploy itself. With five
features in one repository that is five identical loops, five Monitors to re-arm every 30 minutes (a turn each), and five
sessions each looking for the same deploy.

Asked to pick, Petre chose:

- **A: the watcher takes over all of it** for its repository: the base branch, every open pull request of the
  repository's tasks, merges, releases and deploys. The other sessions stop watching their own pull request.
- **Deskbar hands the role on**, not the manager: Deskbar already sees a session end, costs no tokens, and runs every
  day, while the manager runs only on days Petre opened it and pays a Fable turn per handover.

## What was found before designing

- `task deployed <id> -` already works on any task (RemoteControl.Tasks), so a watcher can report another task's
  deploy with no new verb.
- A comment from a session does NOT wake the other task's session: CommentWaker delivers only Petre's comments and the
  manager's (`TaskService.Commented` leaves sessions out). Reevaluate, the stalled ask and the regenerate request ride
  the same delivery queue without a thread comment. A watcher's nudge fits there.
- The keeper stops a Done task's background session after a minute idle (`SessionKeeper.StopAfter`). So a watcher whose
  own task finishes stops soon after, and the role must move: that is the most common "dies".
- `claude agents --json` (ClaudeAgents) lists sessions with Busy and HasProcess; the waker reads it every poll.
- PR 654 lists tasks waiting on a deploy for the manager. Once the watcher reports each task's deploy itself, that list
  is usually empty; it stays as the fallback for a repository with no watcher.

## Approaches

1. **Deskbar keeps the role, a watch script does the routine, the watcher's turns do the judgement (picked).** Deskbar
   stores which task watches which repository, assigns and hands it on from the sessions poll, and prints one tested
   watch loop. The loop nudges sessions on base moves and conflicts without a turn of the watcher's; the watcher takes
   a turn only on a merge (release, deploy evidence, `task deployed`) or a failure.
2. Sessions agree among themselves (a lock file per repository, whoever notices a dead holder takes over). No Deskbar
   change, but races, and it is exactly "Deskbar hands the role on" undone.
3. Deskbar itself is the watcher, no agent. Cheapest, but deploy evidence differs per app and needs judgement, and
   Petre asked for an agent. Parts of it survive in 1: the routine nudges cost no turn.

## The design

### Who is in flight, and when a repository gets a watcher

A task is **in flight in a repository** when it has a Claude session, is not Done or Closed, is not a recurring task
between runs, and has a pull request in that repository that is not known to be closed unmerged. Its session's Done
ends flight, not a deploy report: Done already comes last in the briefing's order (merged, released, deployed, done),
and it works the same in Deskbar's own repository, where done follows the release and no deploy is reported. It also
does not depend on GitHub's answers, which a build without a commit in its version never asks for (my call, after the
reviewer found both gaps). Snoozed tasks count: their pull request still falls behind main.

- A repository **gets** a watcher when two or more tasks are in flight in it ("multiple features per repo").
- It **keeps** one until none is in flight. When it drops to one, that task holds the role (or keeps it): watching a
  repository with only your own pull request is what every session does today, so nothing changes for it.
- The manager's session never holds the role.

### Picking and handing on (Deskbar, pure function)

`RepoWatchers.Plan(board, listed, pullRequestStates, beats, now)` in Core, run on every sessions poll like ManagerWaker.
State lives on the board, `TaskBoard.Watchers`: repository ("owner/repo", lower case as PullRequestLink.Key has it) to
{task id, session id, since, last beat}.

- **Eligible holder:** a task in flight in the repository whose session (TaskViews.SessionToGoTo) is listed with a
  process.
- **Pick:** the eligible task started most recently, since it is likely to stay longest. Never move a role whose holder
  is still eligible and beating.
- **The role moves when** the holder's task leaves flight (Done or Closed), or its session has not been listed with a
  process for 5 minutes (longer than the keeper's 2 minutes before it resumes an open task's missing session, so a
  holder the keeper brings back keeps its role), or its beat is stale (below).
- **No eligible task:** the role stays empty; the in-flight sessions are told to watch their own pull requests again
  (their prompt line, below), so nothing is left unwatched.

### The beat

A Monitor lasts 30 minutes and a watcher can forget to re-arm it, which Deskbar cannot see. So the watch loop calls
`task watching --session <id>` every minute, and that call is the beat. A beat older than 5 minutes gets the holder one
nudge ("your watch stopped; re-arm it"); older than 10, the role moves.

### Telling sessions

- **The new holder** gets a nudge (below) saying it watches `owner/repo` now and to run `task watching`.
- **Every session's prompt line** (SessionBriefing.Prompt) gains one clause while its task is in flight in a watched
  repository: for the holder, "you watch owner/repo for the other sessions (task watching)"; for the others, "owner/repo
  is watched by task X: do not run your own pull request watch; stop it if one runs; you will be nudged to rebase and
  told when your change ships". With no holder, nothing is added and the briefing's own rule applies.
- The session briefing's "keep it on top of its base" line gets "unless your prompt line names your repository's
  watcher". The watcher replaces the watch loop wherever it is written, a repository's CLAUDE.md included; that
  CLAUDE.md's rebase rules (no CI, rebase only before the merge) still hold when the nudge comes. In this repository,
  CLAUDE.md's watch section and the refresh-pr skill say so too (my call; other repositories' CLAUDE.md files are left
  alone, the prompt line covers them).

### The nudge

`task nudge <id> - --session <watcher>`: refused unless the caller holds the role for a repository the target task is
in flight in. It rides CommentWaker's delivery queue like Reevaluate (SessionBriefing.WatcherNudge wraps the text),
and leaves no comment in the thread, so twenty base moves a day do not fill twenty threads. It takes the queue's
usual roads, resume included: an open task's stopped session has to wake to rebase anyway. When no session can take
it, the nudge is dropped without a thread line (my call).

### What `task watching` prints

For the holder only (others get "task X watches owner/repo"): the repository, each in-flight task (short id, title,
pull request links and their last known state), and the duties:

1. Run the watch loop in a Monitor and re-arm it when it expires. Deskbar prints the loop (`task watching --script`), a
   bash loop like the one in the Deskbar CLAUDE.md, extended to every pull request of the list, which it re-reads each
   minute. On a base move or a conflict, the loop itself runs `task nudge` for each affected open pull request, with
   no turn of the watcher's. It prints a line (which wakes the watcher) only for a merge or a failed run.
2. On a merge: follow the repository's release run if it has one, find the deploy evidence the repository's CLAUDE.md
   names, and report `task deployed <id> -` for each in-flight task whose change the evidence shows, then nudge that
   task's session to finish (task done in the briefing's order). The watcher reports a task's deploy only on evidence
   for that task's service, as the briefing asks of every session. Where the order stops at the release (Deskbar's own
   repository), it nudges "released in vX, finish" once the release run is green and reports nothing. A report stamps
   all of the task's pull requests, in every repository, as `task deployed` does today; left as is.
3. Never rebase or push another task's branch: nudge its session.
4. Report to the manager with `task watch-report -` only what blocks others: main red, a release run failed, a deploy
   not showing after its usual time. Merges and routine deploys are not reported (task 6a87df67: "manager doesn't have
   to tell me each merge").
5. Its own task's work goes on as before; the watch costs it a turn only on a merge or a failure.

### Reporting to the manager

`task watch-report -` stores the line with its repository and time. ManagerWakes gets a third trigger next to hand-backs
and deploys: reports since the last wake, under "Watchers report:", same 30-minute gap, same busy rule, same "today's
manager only". The deploy trigger from PR 654 stands down for a repository that has a watcher, which already nudges
the tasks waiting on its deploys; otherwise each report the watcher makes would wake the manager to repeat its nudges
(my call). Handovers ride along as one line each ("owner/repo: watcher moved from X to Y, Z ended") without waking
it. On a day with no manager, a report that needs Petre is the watcher's own `task ask`; the stored report is dropped
with the day's record.

## Non-goals

- The watcher does not merge anything, ever.
- No new UI. The role shows only in the prompt lines, `task watching` and the manager's wake. (A badge on the bar is a
  later idea if Petre wants to see it.)
- Repositories with one task in flight work exactly as today.

## Calls made in writing this up (check them)

- Flight ends at the task's Done, not at a deploy report.
- A holder keeps its role for 5 minutes without a listed session.
- The watch section of this repository's CLAUDE.md and the refresh-pr skill defer to the watcher.
- A nudge takes the delivery queue's usual roads; an undeliverable one leaves no thread line.
- PR 654's deploy trigger stands down where a watcher exists.
- The holder is the most recently started eligible task.

## Left to the builder

- Where `Watchers` and the beats are stored (on the board, saved like the rest; beats may stay in memory).
- The exact watch-loop script and its tests; it must pass the Deskbar CLAUDE.md probe traps (full shim path, stdin for
  text).
- Trace lines for assignment, handover and beat, behind the one trace switch.
- Tests: `RepoWatchersTests` for every rule above, ControlUsage entries for `nudge`, `watching` and `watch-report`.
