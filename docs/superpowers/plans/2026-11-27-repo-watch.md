# Repo Watch Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deskbar polls GitHub once a minute for every in-flight task's pull request, nudges the task's session when
its base moves, it conflicts, it merges or its merge commit's runs finish, and wakes the manager on a red run, so
sessions stop running their own watch loops.

**Architecture:** Pure rules in Core (`RepoWatch`: in-flight set, poll step, follow step, JSON parsers), state and
streams in `TaskService`, a scheduler-driven `RepoWatchPoll` in Core with gh calls injected from `GitHubCli` (the shape
of `RunningChangeWatch`). Nudges ride `CommentWaker`'s delivery queue without a thread line. The prompt line, the
session briefing, StalledTasks and ManagerWakes learn about the watch.

**Tech Stack:** C# / .NET 10, System.Reactive, CSharpFunctionalExtensions, xUnit, gh CLI (`gh api graphql`, `gh run list`).

**Spec:** `docs/superpowers/specs/2026-11-27-repo-watcher-design.md` (read it first).

## Global Constraints

- Repository CLAUDE.md and the user's C# rules: expression-bodied members, immutable records, `var`, lambdas over loops,
  `Result`/`Maybe` from CSharpFunctionalExtensions, Rx over events, long explanatory comments naming task c8e6967d and
  the spec.
- Poll interval 1 minute. Watched only while the repository's last successful poll is within 5 minutes.
- Follow a merge until every run on the merge commit finished, or 3 hours; "no runs" is decided after 10 minutes.
- A snoozed task is nudged only for a conflict, never for a base move alone.
- Only a pull request seen Open and then Merged is followed.
- Nudges leave no line in the task's thread, delivered or not.
- Build: `dotnet build Deskbar.sln -c Release -warnaserror` clean. Tests: `dotnet test tests/Deskbar.Core.Tests --filter "Category!=Integration"`.
  Windows tests only through `scripts/test-hidden.ps1` (never on Petre's desktop).

## Review Focus

1. A Deskbar release run cancelled by a newer merge (release.yml cancels older builds): `cancelled` is NOT red; it ends
   the follow as finished, names the run "cancelled (a newer run took over)", and never wakes the manager. Test in Task 3.
2. gh missing or signed out: every look fails, the repository's last success ages past 5 minutes, `Watched` turns false
   and the prompt line tells the session to watch for itself. Test in Task 4.
3. Deskbar restart mid-follow: followed merges are in memory only, so the merge is not followed after the restart; the
   StalledTasks exception lapses with it and the stalled ask reaches the session within 15 minutes. Test in Task 4
   (no followed merge, task quiet 15 minutes, stalled nudge produced).
4. First poll after start: base heads recorded, no base-move nudges at all (nothing seen to move). Test in Task 2.
5. `mergeable` UNKNOWN (GitHub still computing): neither a conflict nor a clear; no nudge, no memory change. Test in Task 2.

---

### Task 1: RepoWatch types and gh answer parsers

**Files:**
- Create: `src/Deskbar.Core/Tasks/RepoWatch.cs` (types and parsers here; rules in Tasks 2-3 in the same file)
- Test: `tests/Deskbar.Core.Tests/Tasks/RepoWatchParseTests.cs`

**Interfaces:**
- Produces:
  - `enum Mergeable { Unknown, Mergeable, Conflicting }`
  - `sealed record PullFacts(PullRequestLink Link, PullRequestState State, Mergeable Mergeable, string BaseRef, string BaseHead, Maybe<string> MergeCommit)`
  - `sealed record RunFacts(string Name, string Url, bool Finished, string Conclusion)` (conclusion lower case, "" while running)
  - `static string RepoWatch.Query(IReadOnlyList<PullRequestLink> links)`: the GraphQL text, one alias `p<Number>` per
    pull request: `pullRequest(number: N) { state mergeable baseRefName baseRef { target { oid } } mergeCommit { oid } }`
    inside `repository(owner: "<o>", name: "<r>")`.
  - `static Result<IReadOnlyList<PullFacts>> RepoWatch.ParseRepository(IReadOnlyList<PullRequestLink> links, string json)`
  - `static Result<IReadOnlyList<RunFacts>> RepoWatch.ParseRuns(string json)` for `gh run list --json name,status,conclusion,url`.

- [ ] **Step 1: Write failing tests**: `ParsesOpenConflictingPullRequest` (state OPEN, mergeable CONFLICTING, baseRefName
  "main", baseRef oid "abc", no mergeCommit), `ParsesMergedWithMergeCommit`, `MissingAliasFails` (a link with no alias
  in the answer is a failure, not a silent drop), `ParsesRunsFinishedAndRunning` (status "completed"/"in_progress",
  conclusion "success"/null), `QueryNamesEveryPullRequest` (contains `p12: pullRequest(number: 12)` and `p13:`).
- [ ] **Step 2: Run, expect FAIL** (types missing).
- [ ] **Step 3: Implement** with System.Text.Json (`JsonDocument`), as `RunningChanges.ParseMerge` does.
- [ ] **Step 4: Run, expect PASS.**
- [ ] **Step 5: Commit** `feat: parse gh's answers for the repository watch`.

### Task 2: The poll rules

**Files:**
- Modify: `src/Deskbar.Core/Tasks/RepoWatch.cs`
- Test: `tests/Deskbar.Core.Tests/Tasks/RepoWatchTests.cs`

**Interfaces:**
- Consumes: Task 1 types.
- Produces:
  - `sealed record WatchNudge(Guid TaskId, string Text)`
  - `sealed record FollowedMerge(Guid TaskId, PullRequestLink Link, string MergeCommit, DateTimeOffset Since)`
  - `sealed record RepoWatchState` with `IReadOnlyDictionary<string, PullRequestState> Seen` (by link key),
    `IReadOnlyDictionary<string, string> Heads` (by "owner/repo:baseRef", the last base head seen),
    `IReadOnlyDictionary<string, string> ConflictNudged` (link key to the base head it was nudged at),
    `IReadOnlyList<FollowedMerge> Following`, `IReadOnlyDictionary<string, DateTimeOffset> LastSuccess` (by "owner/repo");
    `static RepoWatchState Empty`.
  - `sealed record RepoWatchStep(RepoWatchState State, IReadOnlyList<WatchNudge> Nudges)`
  - `static IReadOnlyDictionary<string, IReadOnlyList<(TaskItem Task, PullRequestLink Link)>> RepoWatch.InFlight(TaskBoard board, RepoWatchState state)`:
    keyed by "owner/repo" lower case; a task with a session (`Sessions.Count > 0`), `IsOpen`, `Status != Done`,
    `!IsBetweenRuns`, and the link not `Seen` as Closed.
  - `static RepoWatchStep RepoWatch.Poll(RepoWatchState state, TaskBoard board, string repository, IReadOnlyList<PullFacts> facts, DateTimeOffset now)`
  - `static bool RepoWatch.Watched(RepoWatchState state, TaskItem task, DateTimeOffset now)`: in flight in a repository
    whose `LastSuccess` is within 5 minutes.

Rules for `Poll` (spec, "Deskbar's poll"): set `LastSuccess[repository] = now`; for each open pull request, a base head
different from a head already in `Heads` nudges its task "The base <baseRef> of <link.Label> moved to <head[..7]>:
rebase and push it now, by the repository's own rules." unless the task is snoozed; a head first seen is only recorded.
`Conflicting` nudges "<Label> conflicts with <baseRef>: rebase, resolve and push it now." once per base head
(`ConflictNudged`), snoozed tasks too. A link whose `Seen` was Open and is now Merged with a merge commit is added to
`Following` and nudges "<Label> merged. Deskbar follows the runs on its merge commit and nudges you with the result; you
need not wait on them in a loop." One task gets at most one nudge per poll (texts joined with a newline). Update `Seen`.

- [ ] **Step 1: Failing tests**: `FirstPollRecordsHeadsWithoutNudging`, `BaseMoveNudgesEachOpenPullRequestOnThatBase`,
  `SnoozedTaskIsNotNudgedForABaseMove`, `SnoozedTaskIsNudgedForAConflict`, `ConflictNudgesOncePerBaseHead`,
  `UnknownMergeableNudgesNothing`, `OpenThenMergedIsFollowedAndNudged`, `FirstSeenMergedIsNotFollowed`,
  `DoneTaskIsNotInFlight`, `TaskWithoutSessionIsNotInFlight`, `ClosedUnmergedLeavesFlight`,
  `WatchedOnlyWithinFiveMinutesOfSuccess` (now = success + 5 min is watched, + 5 min 1 s is not). Build boards with
  RunningChangesTests' `Board(...)` pattern (`TaskBoard.Empty.Create(...)`, `AddPullRequest`).
- [ ] **Step 2: Run, expect FAIL.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run, expect PASS.**
- [ ] **Step 5: Commit** `feat: the repository watch's rules nudge on base moves, conflicts and merges`.

### Task 3: Following a merge commit's runs

**Files:**
- Modify: `src/Deskbar.Core/Tasks/RepoWatch.cs`
- Test: `tests/Deskbar.Core.Tests/Tasks/RepoWatchFollowTests.cs`

**Interfaces:**
- Produces:
  - `sealed record RedRun(Guid TaskId, string Repository, string Name, string Url, DateTimeOffset At)`
  - `sealed record FollowStep(RepoWatchState State, IReadOnlyList<WatchNudge> Nudges, IReadOnlyList<RedRun> Red)`
  - `static FollowStep RepoWatch.Follow(RepoWatchState state, FollowedMerge merge, IReadOnlyList<RunFacts> runs, DateTimeOffset now)`

Rules (spec, "Following a merge"): any finished run with conclusion in {failure, timed_out, startup_failure,
action_required} ends the follow, nudges "<Name> failed on the merge commit of <Label>: <Url>. Look at it; the manager
is told too." and yields a `RedRun`. All finished, none red: ends, nudges "The runs on the merge commit of <Label>
finished: <name> (<conclusion>), ... Carry on in the briefing's order: released, deployed, done." with `cancelled`
written "cancelled (a newer run took over)". No runs and `now - Since >= 10 min`: ends, nudges "<Label> merged and no
workflow runs started on its merge commit; carry on as the briefing says." `now - Since >= 3 h`: ends, nudges "Deskbar
stopped following the runs on the merge commit of <Label> after 3 hours; check them yourself." Otherwise unchanged.
Ending removes the merge from `Following`.

- [ ] **Step 1: Failing tests**: `RedRunEndsFollowAndReportsIt`, `AllGreenNudgesOnce`, `CancelledIsNotRed`,
  `RunningRunsWait`, `NoRunsAfterTenMinutes`, `NoRunsBeforeTenMinutesWaits`, `GivesUpAfterThreeHours`.
- [ ] **Step 2: Run, expect FAIL.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run, expect PASS.**
- [ ] **Step 5: Commit** `feat: the repository watch follows a merge commit's runs`.

### Task 4: TaskService holds the watch; StalledTasks stands down

**Files:**
- Modify: `src/Deskbar.Core/Tasks/TaskService.cs`, `src/Deskbar.Core/Tasks/StalledTasks.cs`
- Test: `tests/Deskbar.Core.Tests/Tasks/StalledTasksTests.cs`, `tests/Deskbar.Core.Tests/Tasks/TaskServiceTests.cs`

**Interfaces:**
- Consumes: Tasks 2-3.
- Produces on `TaskService` (in memory, never saved; a restart starts from `RepoWatchState.Empty`):
  - `RepoWatchState Watch { get; }`
  - `void Watched(RepoWatchStep step)` and `void Followed(FollowStep step)`: store the state, `OnNext` each nudge, append
    red runs to an in-memory list.
  - `IObservable<WatchNudge> WatchNudges`
  - `IReadOnlyList<RedRun> RedRuns` (the day's, capped at 50)
  - `bool IsWatched(TaskItem task)` (RepoWatch.Watched with `now()`)
- `StalledTasks.Step` gains a trailing optional `IReadOnlySet<Guid>? following = null`; tasks in it are excluded from
  both the nudge and the hand-back selections. `TaskService.Stalled` passes the task ids of `Watch.Following`.

- [ ] **Step 1: Failing tests**: `FollowedMergeGetsNoStalledNudge`, `FollowedMergeIsNotHandedBack`,
  `WithoutFollowTheStalledNudgeReturns` (Review Focus 3), `NudgesFromAStepAreRaised` (TaskService),
  `IsWatchedLapsesWhenPollsFail` (Review Focus 2: one `Watched` step, clock + 6 min, false).
- [ ] **Step 2: Run, expect FAIL.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run, expect PASS.**
- [ ] **Step 5: Commit** `feat: TaskService holds the repository watch; a followed merge is not stalled`.

### Task 5: The poll runs, gh answers, nudges are delivered

**Files:**
- Create: `src/Deskbar.Core/Tasks/RepoWatchPoll.cs`
- Modify: `src/Deskbar.Windows/Claude/GitHubCli.cs`, `src/Deskbar.App/App.xaml.cs` (beside `runningChangeWatch`, ~l.113,
  ~l.2049, dispose at ~l.3000 and ~l.3458), `src/Deskbar.App/CommentWaker.cs`, `src/Deskbar.Core/Tasks/SessionBriefing.cs`
- Test: `tests/Deskbar.Core.Tests/Tasks/RepoWatchPollTests.cs` (TestScheduler from Microsoft.Reactive.Testing, as
  RunningChangeWatch's tests do if any; otherwise a `HistoricalScheduler`)

**Interfaces:**
- Consumes: Tasks 1-4.
- Produces:
  - `sealed class RepoWatchPoll : IDisposable`, ctor `(TaskService tasks, Func<string, IReadOnlyList<PullRequestLink>, Task<Result<IReadOnlyList<PullFacts>>>> lookRepository, Func<PullRequestLink, string, Task<Result<IReadOnlyList<RunFacts>>>> lookRuns, IScheduler scheduler, IScheduler? background = null, Action<string>? trace = null)`.
    Every `TimeSpan.FromMinutes(1)` (and at once): for each repository in `RepoWatch.InFlight`, look, then
    `tasks.Watched(RepoWatch.Poll(...))`; then for each `Following` merge look its runs and `tasks.Followed(...)`. A
    failed look is traced and skipped (its repository's `LastSuccess` ages). Skip a tick while a look is out.
  - `GitHubCli.Repository(string repository, IReadOnlyList<PullRequestLink> links)`: `gh api graphql -f query=<RepoWatch.Query>`, parsed by `RepoWatch.ParseRepository`.
  - `GitHubCli.Runs(PullRequestLink link, string sha)`: `gh run list --repo <owner>/<repo> --commit <sha> --json name,status,conclusion,url`, parsed by `RepoWatch.ParseRuns`.
  - `SessionBriefing.WatchNudge(TaskItem task, string text)`: "Deskbar's watch, about your task <short id>: <text>"
    plus one line: it is not the user, so it settles no question.
  - CommentWaker: a `Delivery` init property `bool Quiet`; when set, the `Nothing` road writes no thread note.
    `tasks.WatchNudges` mapped to `new Delivery(n.TaskId, "a watch nudge", t => SessionBriefing.WatchNudge(t, n.Text)) { Quiet = true }` and merged into the stream.
- [ ] **Step 1: Failing tests**: `PollLooksEachInFlightRepositoryOnce`, `FailedLookChangesNothing`,
  `FollowedMergeRunsAreLooked`.
- [ ] **Step 2: Run, expect FAIL.**
- [ ] **Step 3: Implement**, wiring in App.xaml.cs only when `ClaudeTasksInEffect` like the other watches, with the
  dispatcher scheduler and `ClickTrace` trace as `runningChangeWatch` has.
- [ ] **Step 4: Run tests and the Release build, expect PASS and clean.**
- [ ] **Step 5: Measure once by hand** (Global Constraints, no live experiments on Petre's bar): run the two gh
  commands against an open Deskbar pull request from a shell and check the parsers accept the output.
- [ ] **Step 6: Commit** `feat: Deskbar polls each repository with a task in flight and delivers nudges`.

### Task 6: The prompt line and the briefing

**Files:**
- Modify: `src/Deskbar.Core/Tasks/SessionBriefing.cs` (Prompt ~l.308, the two Commands lines ~l.520 and ~l.535),
  `src/Deskbar.Core/Control/RemoteControl.ClaudeHook.cs` (~l.348)
- Test: `tests/Deskbar.Core.Tests/Tasks/SessionBriefingTests.cs` (or the file that tests Prompt)

**Interfaces:**
- `SessionBriefing.Prompt(..., bool watched = false)`: while `own` is in progress and `watched`, append " Deskbar
  watches your pull request and nudges you when its base moves, it conflicts or its merge's runs finish: do not run
  your own watch loop, and stop one that runs." `Prompted` passes `service.IsWatched(own.Value)`.
- The "While your pull request is open" line starts: "Unless your prompt line says Deskbar watches your pull request,
  keep it on top of its base without being asked: ..." and adds "When Deskbar watches it, rebase and push as soon as its
  nudge comes."
- The "After the merge" line adds: "While Deskbar watches, it follows the runs on your merge commit and nudges you with
  the result, so do not wait on them in a loop; the deploy check stays yours."
- [ ] **Step 1: Failing tests**: `PromptSaysWatchedWhenWatched`, `PromptSilentWhenNotWatched`, `BriefingNamesTheWatchException`.
- [ ] **Step 2-4: Run FAIL, implement, run PASS** (keep any line-count test passing).
- [ ] **Step 5: Commit** `feat: sessions hear that Deskbar watches their pull request`.

### Task 7: A red run wakes the manager

**Files:**
- Modify: `src/Deskbar.Core/Tasks/ManagerWakes.cs`, `src/Deskbar.App/ManagerActions.cs` (~l.246)
- Test: `tests/Deskbar.Core.Tests/Tasks/ManagerWakesTests.cs`

**Interfaces:**
- `ManagerWakes.Plan(..., IReadOnlyList<RedRun>? red = null)`: red runs with `At > since` are a trigger; the message
  gains a section "Failed runs on merge commits:" with "- <repo>: <name> <url>, task <Name(task)>" and the line "Tell
  the user in one line only if several tasks are blocked by it; the task's session was nudged." ManagerActions passes
  `tasks.RedRuns`.
- [ ] **Step 1: Failing tests**: `RedRunWakesTheManager`, `OldRedRunWakesNothing`, `RedRunWaitsForTheGap`.
- [ ] **Step 2-4: FAIL, implement, PASS.**
- [ ] **Step 5: Commit** `feat: a red run on a merge commit wakes the manager`.

### Task 8: Docs

**Files:**
- Modify: `CLAUDE.md` (the "While a pull request is open" section), `.claude/skills/refresh-pr/SKILL.md`, `README.md`
  (where the manager and task sessions are described), `docs/superpowers/notes/manual-test-script.md` (steps RW1-RW5:
  base move nudges, conflict nudge, merge nudge, green runs nudge, gh signed out falls back).
- CLAUDE.md and the skill: the session's own Monitor is now the fallback, run only when the prompt line does not say
  Deskbar watches the pull request; the refresh itself is unchanged.
- [ ] **Step 1: Edit.** **Step 2: Commit** `docs: Deskbar watches pull requests; the session's loop is the fallback`.
