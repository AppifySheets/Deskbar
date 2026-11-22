# Goals Planner Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Weekly and monthly goals that tasks count toward, a planner window to set and review them, and one quiet
"Plan this week" line on the bar when a new week or month starts.

**Architecture:** A new `Deskbar.Core.Goals` namespace beside `Deskbar.Core.Tasks`: pure records (`GoalPeriod`,
`Goal`, `GoalBoard`), a store behind `IGoalStore` writing `goals.json` through `DurableFile`, and a `GoalService` that
owns the board, reads the task board for progress, and is the one place a task is linked to a goal. The link itself is
an optional `goalId` on the task in `tasks.json` (no version bump). Pull request 1 brings the model, the store, the
pipe and the task link in the tasks window and the bar's tooltip; pull request 2 brings the planner window and the
review line.

**Tech Stack:** C# / .NET 10, WPF, CSharpFunctionalExtensions, System.Reactive, xUnit 2.9 with plain `Assert` and
hand-written fakes (`tests/Deskbar.Core.Tests/Fakes.cs`); window tests on an STA thread via `StaThread.Run`, run only
through `scripts/test-hidden.ps1`.

**Spec:** `docs/superpowers/specs/2026-11-22-goals-planner-design.md`. Read it first; its "What Petre decided" section
is binding and its "Calls made in writing this up" are kept. It builds on
`docs/superpowers/specs/2026-11-22-tasks-board-design.md` and the ledger rulings 21 to 31 in
`E:/repos/TaskSpaces-tasks/.superpowers/sdd/2026-11-22-tasks-board/progress.md` (selection, undo, docked height, flags,
drag order, Matrix), which describe the code this plan changes.

**Branch:** `feat/goals-planner` in the worktree `E:/repos/TaskSpaces-goals`, stacked on `feat/tasks-window`
([Deskbar #421](https://github.com/AppifySheets/Deskbar/pull/421)), itself on `feat/tasks-board`
([Deskbar #420](https://github.com/AppifySheets/Deskbar/pull/420)). Rebase onto whatever those have become before
Task 1 starts.

## Global Constraints

- A goal has a title, a period (one week or one month), a status (`Open`, `Done`, `Dropped`) and an order within its
  period. No flags and no workspace on a goal.
- A week runs Monday to Sunday; a month is the calendar month. A period is stored as a local calendar date
  (`DateOnly`: the Monday of the week, the 1st of the month), never as an instant. Every other stored time is a UTC
  `DateTimeOffset`; every goal has a `Guid` made on the device and an `UpdatedAt`.
- The local date enters through an injected `Func<DateOnly> today`; the UTC clock through `Func<DateTimeOffset> now`.
  Nothing in Core reads `DateTime.Now`.
- A weekly goal may name one monthly goal as its parent, and only a goal of a month its week touches.
- A task belongs to at most one goal, weekly or monthly. Attaching never changes its flags, rank or status.
- Done means a task with status `Done` or `Closed`. Finishing every task never closes a goal; an open task never stops
  marking a goal done.
- Goals live in `goals.json` beside `tasks.json`, written with `DurableFile`, behind `IGoalStore` (two members, as
  `ITaskStore`). The review state (last week and last month seen) is stored there too.
- The task's link is `goalId` on the task in `tasks.json`, optional, no version bump.
- Every UI action has a pipe twin in `ControlUsage`, and every new verb joins `Help_lists_every_command`
  (`tests/Deskbar.Core.Tests/RemoteControlMenuTests.cs`).
- The bar gains only the "Plan this week" / "Plan this month" line: no goal chip, no progress on its lines. A task's
  tooltip names its goal.
- No new global hotkey, no sound, no Windows toast, no new NuGet package.
- Code style per CLAUDE.md and `implementer-context.md`: `private` implied, expression bodies, `var`, immutable
  records, readonly collections, `Result`/`Maybe`, Rx over events, frozen static brushes, long comments with the
  reasoning, a header comment on each new file pointing at the spec. No em dashes anywhere.
- Every build and test in the worktree runs `-c Release` (Petre may be running a Debug exe built from a worktree).
  `Deskbar.Windows.Tests` only through
  `pwsh scripts/test-hidden.ps1 -Arguments 'Deskbar.sln -c Release --no-build --filter "<filter>"'` after
  `dotnet build Deskbar.sln -c Release`. `Deskbar.Core.Tests` runs directly.
- Before each pull request: `dotnet build Deskbar.sln -c Release -warnaserror` with 0 warnings, both test projects
  green (`Category!=Integration&Category!=Screen`). Release notes in the PR body, no version edits.
- Commits end with the model's short name as the last paragraph (`*opus5.5*`), no Co-Authored-By, no "Generated
  with". Implementers never start, stop or swap Petre's Deskbar; the controller does.

## Review Focus

1. A week that crosses two months (Monday 30 November to Sunday 6 December 2026): the weekly goal's parent may be a
   goal of either month, and the planner shows one month without hiding the other's parent. Tests in Task 1
   (`A_week_across_two_months_touches_both`) and Task 9 (`The_parent_menu_offers_both_months_of_a_split_week`).
2. The clock says Monday in UTC while it is still Sunday evening locally, or the machine was off all Monday: "this
   week" is Petre's local week, and the prompt shows on the first day the app runs in the new week and not again once
   seen. Tests in Task 4 (`This_week_is_the_local_week_not_the_utc_one`,
   `The_prompt_is_due_on_the_first_run_of_a_week_even_on_a_tuesday`).
3. `goals.json` unreadable while `tasks.json` holds links: the unreadable file is kept aside, never saved over, and
   the tasks keep their `goalId` (nothing prunes links). Tests in Task 3 (`An_unreadable_file_is_kept_aside`) and
   Task 4 (`A_failed_load_leaves_task_links_alone`).
4. A task whose `goalId` names no goal (deleted elsewhere, or a file restored by hand): it reads as no goal in every
   view, sits in the planner's unattached column and can be linked again. Tests in Task 4
   (`A_dangling_link_reads_as_no_goal`) and Task 9 (`Unattached_is_newest_first_and_skips_closed_and_linked`).
5. A docked bar when the prompt line appears or goes: the list's fit pass settles on one count (no N, N+1 flip), and
   the line never appears under a press, a drag or an open menu. Test in Task 13
   (`The_docked_list_settles_with_the_prompt_line`, `The_prompt_waits_while_the_bar_is_pressed`).

## Calls this plan makes (the controller's to confirm)

- Goals reach a task only through `GoalService.LinkTask`, which needs an open goal; `TaskService.SetGoal` itself
  checks nothing about goals, so the tasks code never depends on the goals code.
- Nothing prunes task links. A link to a goal that no longer exists reads as no goal everywhere; deleting a goal
  detaches its tasks explicitly. Pruning on load would wipe every link the day `goals.json` fails to load.
- The review strip lists every open goal of an earlier period, not only the last one, so a skipped week is not lost.
  It shows whenever such goals exist, whether or not the bar's prompt was due.
- Carry over goes to the current period of the goal's kind, never to a different kind. A carried weekly goal keeps its
  parent only when its new week touches the parent's month; a carried monthly goal leaves its weekly goals where they
  were (their weeks served it, and that history stays).
- A dropped weekly goal counts in neither number of its month's "2 of 4 weeks' goals done".
- When the week shown is the current one, the planner shows today's month; any other week shows the month of its
  Thursday. A weekly goal's parent menu always offers both months of a split week.
- The planner lists a monthly goal's own tasks under it too, so they can be dragged off.
- Goals have an order but no way to change it yet: a new or carried goal goes to the end of its period.
- The planner opens from the bar's line, from `planner-window show` and from a "Planner" chip in the tasks window's
  toolbar. The bar gains nothing else, so this chip is the way in on a week without a prompt.
- First run after the update: nothing seen yet, so the line reads "Plan this month" (both due, per the spec).

## File map

Created:
- `src/Deskbar.Core/Goals/GoalPeriod.cs`: `GoalKind`, `GoalPeriod` (calendar arithmetic only).
- `src/Deskbar.Core/Goals/Goal.cs`: `GoalStatus`, `Goal`.
- `src/Deskbar.Core/Goals/GoalBoard.cs`: every rule for changing goals, pure, as `TaskBoard`.
- `src/Deskbar.Core/Goals/GoalProgress.cs`, `GoalReview.cs`: progress counts and the review's due state and list.
- `src/Deskbar.Core/Goals/IGoalStore.cs`, `JsonGoalStore.cs`: `goals.json`.
- `src/Deskbar.Core/Goals/GoalService.cs`: the owner, as `TaskService`.
- `src/Deskbar.Core/Goals/TaskGoalFilter.cs`: the tasks window's goal filter.
- `src/Deskbar.Core/Goals/PlannerViews.cs` (PR 2): the planner's pure choices.
- `src/Deskbar.Core/Control/RemoteControl.Goals.cs`: the `goal` verb and `planner-window`.
- `src/Deskbar.App/PlannerWindow.xaml(.cs)`, `PlannerWindow.Drag.cs`, `PlannerBoard.cs` (PR 2).
- `src/Deskbar.App/RememberedWindowPlace.cs` (PR 2, Task 10): placement shared by the two windows.
- `src/Deskbar.App/FloatingBar.PlanPrompt.cs` (PR 2).
- `docs/features/goals.md`.

Modified: `TaskItem.cs`, `TaskBoard.cs`, `TaskService.cs`, `JsonTaskStore.cs`, `DurableFile.cs`,
`RemoteControl.cs`, `RemoteControl.Tasks.cs`, `ControlUsage.cs`, `AppState.cs`, `WorkspaceManager.cs`,
`TasksBoard.cs`, `TasksWindow.xaml.cs`, `FloatingBar.TaskList.cs`, `App.xaml.cs`, `tests/Deskbar.Core.Tests/Fakes.cs`,
`README.md`, `docs/features/tasks.md`, `docs/features/scripts.md`, `docs/superpowers/notes/manual-test-script.md`.

---

# Pull request 1: goals, their store and pipe, and the task's link

### Task 1: Periods, goals and the board's rules

**Files:**
- Create: `src/Deskbar.Core/Goals/GoalPeriod.cs`, `src/Deskbar.Core/Goals/Goal.cs`, `src/Deskbar.Core/Goals/GoalBoard.cs`
- Test: `tests/Deskbar.Core.Tests/Goals/GoalPeriodTests.cs`, `tests/Deskbar.Core.Tests/Goals/GoalBoardTests.cs`

**Interfaces:**
- Produces (namespace `Deskbar.Core.Goals`):
  - `enum GoalKind { Week, Month }`
  - `sealed record GoalPeriod` with a private constructor, so every period starts on a Monday or a 1st:
    `static GoalPeriod WeekOf(DateOnly day)`, `static GoalPeriod MonthOf(DateOnly day)`,
    `static GoalPeriod Of(GoalKind kind, DateOnly day)`, `GoalKind Kind`, `DateOnly Start`, `DateOnly End`,
    `bool Contains(DateOnly day)`, `GoalPeriod Next()`, `GoalPeriod Previous()`, `bool Touches(GoalPeriod other)`
    (the date ranges overlap), `IReadOnlyList<GoalPeriod> Months()` (a week's one or two months, a month itself),
    `string Label` ("Week of 23 Nov 2026", "November 2026", invariant culture), `string Key` (`Start` as `yyyy-MM-dd`).
  - `enum GoalStatus { Open, Done, Dropped }` (Open first: the default a missing status reads as).
  - `sealed record Goal(Guid Id, string Title, GoalPeriod Period, GoalStatus Status, int Order, Maybe<Guid> ParentId,
    DateTimeOffset Created, DateTimeOffset UpdatedAt)` with `GoalKind Kind => Period.Kind`, `bool IsOpen`.
  - `sealed record GoalBoard(IReadOnlyList<Goal> Goals)` with init properties `Maybe<DateOnly> WeekSeen`,
    `Maybe<DateOnly> MonthSeen`, `static Empty`, and:
    `Result<(GoalBoard Board, Goal Goal)> Add(string title, GoalPeriod period, DateTimeOffset now, Guid newId)`,
    `Result<GoalBoard> Rename(Guid id, string title, DateTimeOffset now)`, `MarkDone(Guid id, now)`, `Drop(Guid id, now)`,
    `Reopen(Guid id, now)`, `Result<GoalBoard> Carry(Guid id, GoalPeriod to, DateTimeOffset now)`,
    `Result<GoalBoard> SetParent(Guid id, Maybe<Guid> parent, DateTimeOffset now)`,
    `Result<GoalBoard> Delete(Guid id, DateTimeOffset now)`, `GoalBoard Seen(GoalPeriod week, GoalPeriod month)`,
    `IReadOnlyList<Goal> In(GoalPeriod period)`, `IReadOnlyList<Goal> ChildrenOf(Guid monthly)`,
    `Maybe<Goal> Get(Guid id)`, `Result<Goal> Find(string idOrPrefix)`.

- [ ] **Step 1: Write the failing tests.** In `GoalPeriodTests`:
  - `A_week_starts_on_its_monday`: `WeekOf(2026-11-22)` (a Sunday) has `Start` 2026-11-16, `End` 2026-11-22;
    `WeekOf(2026-11-23)` has `Start` 2026-11-23.
  - `A_month_starts_on_the_first`: `MonthOf(2026-11-22)` is 2026-11-01 to 2026-11-30; `MonthOf(2024-02-10).End` is 2024-02-29.
  - `A_week_across_two_months_touches_both`: `WeekOf(2026-11-30).Months()` is November and December 2026, and the week
    `Touches` both; `WeekOf(2026-11-23).Months()` is November only.
  - `Next_and_previous_step_one_period`: week 2026-11-23 next is 2026-11-30; month 2026-12-01 previous is 2026-11-01;
    month 2027-01-01 previous is 2026-12-01.
  - `Labels_and_keys`: "Week of 23 Nov 2026", "November 2026", key "2026-11-23".
  In `GoalBoardTests` (clock: a captured `DateTimeOffset` the test moves):
  - `Adding_appends_to_its_period`: two goals added to one week get `Order` 0 and 1, `Open`, `ParentId` none;
    `In(week)` returns them in that order; a goal in another week does not appear.
  - `An_empty_title_is_refused`: "A goal needs a title".
  - `Done_drop_and_reopen`: `MarkDone` then `Reopen` returns to `Open`; `Reopen` on an open goal fails "The goal is open.";
    each stamps `UpdatedAt`.
  - `A_weekly_goal_takes_a_monthly_parent_of_a_month_it_touches`: weekly goal in week 2026-11-30, monthly goals in
    November and in December: both accepted; a monthly goal of October is refused with "'<title>' is a goal of
    October 2026, a month this week does not touch".
  - `Only_a_weekly_goal_has_a_parent_and_only_a_monthly_one_is_one`: refusals "Only a weekly goal has a monthly
    parent" and "'<title>' is not a monthly goal".
  - `Carrying_keeps_the_id_and_moves_to_the_end_of_the_new_period`: same `Id`, new `Period`, `Order` after the goals
    already there, status unchanged.
  - `Carrying_a_weekly_goal_keeps_its_parent_only_when_the_new_week_touches_that_month`: parent in November; carried
    to week 2026-11-30 keeps it, carried to week 2026-12-07 clears it.
  - `Carrying_never_changes_the_kind`: a weekly goal carried to a month period is refused, "A weekly goal moves to
    another week".
  - `Deleting_a_monthly_goal_frees_its_weekly_children`: children's `ParentId` none and `UpdatedAt` stamped.
  - `Seen_records_the_periods_and_is_the_same_board_when_nothing_changes`: `ReferenceEquals` on a second call.
  - `Find_takes_a_prefix_of_four`: as `TaskBoard.Find`, with "No goal with id x" and "'x' matches several goals".
- [ ] **Step 2: Run them to verify they fail.**
  Run: `dotnet test tests/Deskbar.Core.Tests -c Release --filter "FullyQualifiedName~Goals"`
  Expected: build errors on the missing types.
- [ ] **Step 3: Implement** the types above. `In` orders by `Order`, then `Created`, then `Id`. `Add` and `Carry`
  take `Order = 1 + max Order in that period` (0 in an empty one). A title is trimmed. Header comment on each file
  pointing at the spec's "The goal" and "Calls made".
- [ ] **Step 4: Run the tests to verify they pass.** Same command. Expected: PASS.
- [ ] **Step 5: Commit** `feat: weekly and monthly goals, with their periods and rules`

### Task 2: A task can name its goal

**Files:**
- Modify: `src/Deskbar.Core/Tasks/TaskItem.cs`, `src/Deskbar.Core/Tasks/TaskBoard.cs`,
  `src/Deskbar.Core/Tasks/TaskService.cs`, `src/Deskbar.Core/Tasks/JsonTaskStore.cs`
- Test: `tests/Deskbar.Core.Tests/Tasks/TaskBoardTests.cs`, `TaskServiceTests.cs`, `JsonTaskStoreTests.cs`

**Interfaces:**
- Produces:
  - `TaskItem.GoalId`: `public Maybe<Guid> GoalId { get; init; }` (an init property like the flags, so no
    constructor call changes).
  - `TaskBoard.SetGoal(Guid id, Maybe<Guid> goal, DateTimeOffset now) -> Result<TaskBoard>` (same board when
    unchanged; a closed task takes it too, it stays readable history), `TaskBoard.DetachGoal(Guid goalId,
    DateTimeOffset now) -> TaskBoard` (same board when no task names it).
  - `TaskService.SetGoal(Guid id, Maybe<Guid> goal) -> Result` (saves only on change), `TaskService.DetachGoal(Guid goalId)`.
  - `tasks.json`: an optional `goalId` (Guid or null) on each task, still `version` 2.

- [ ] **Step 1: Write the failing tests:**
  - `Setting_a_goal_changes_nothing_else`: a task in quadrant II with rank r, `SetGoal(id, g)` → `GoalId == g`,
    `Important`, `Urgent`, `Rank`, `Status`, `Raised` unchanged, `UpdatedAt` stamped.
  - `Setting_the_same_goal_is_the_same_board` (`ReferenceEquals`).
  - `Detaching_a_goal_frees_every_task_that_named_it` (two tasks on g, one on h: only the first two change).
  - `Undo_and_reopen_keep_the_goal`: close, `UndoClose`, `Reopen` keep `GoalId`.
  - Service: `Setting_a_goal_saves_and_pulses_once_and_not_again_for_the_same_goal`.
  - Store: `The_goal_round_trips` and `A_file_without_goal_ids_reads_as_no_goal` (the existing v2 sample text).
- [ ] **Step 2: Run them to verify they fail.**
  Run: `dotnet test tests/Deskbar.Core.Tests -c Release --filter "FullyQualifiedName~Tasks"`
- [ ] **Step 3: Implement.** `TaskDto` gains `Guid? GoalId = null` at the end. The store's header gains a paragraph on
  `goalId` beside the one on the flags: an older version 2 build ignores it and drops it on its next save, which costs
  the links and never a task; that is why goals themselves are in their own file (spec, "Calls made").
- [ ] **Step 4: Run the tests to verify they pass.** Same command.
- [ ] **Step 5: Commit** `feat: a task can belong to a goal, stored on the task`

### Task 3: goals.json

**Files:**
- Create: `src/Deskbar.Core/Goals/IGoalStore.cs`, `src/Deskbar.Core/Goals/JsonGoalStore.cs`
- Modify: `src/Deskbar.Core/Persistence/DurableFile.cs`, `src/Deskbar.Core/Tasks/JsonTaskStore.cs` (move its
  `KeepAside` / `MoveAside` into `DurableFile` and call them from there), `tests/Deskbar.Core.Tests/Fakes.cs`
- Test: `tests/Deskbar.Core.Tests/Goals/JsonGoalStoreTests.cs`

**Interfaces:**
- Consumes: Task 1's `GoalBoard`, `Goal`, `GoalPeriod`.
- Produces:
  - `interface IGoalStore { Result<GoalBoard> Load(); Result Save(GoalBoard board); }` (the seam a synced store
    replaces; the same two members as `ITaskStore`, same comment).
  - `JsonGoalStore(string baseDirectory, Action<string>? trace = null) : IGoalStore`.
  - `DurableFile.KeepAside(string path) -> string`: moves `path` and its `.previous` aside as
    `<name>.<yyyyMMdd-HHmmss>.bak` and says what happened to each (the text `JsonTaskStore` builds today).
  - `Fakes.cs`: `public sealed class FakeGoalStore : IGoalStore` with `GoalBoard Stored`, `int Saves`,
    `Maybe<string> LoadFails`, `bool SaveFails`.
  - The file: `{"version":1,"weekSeen":"2026-11-23","monthSeen":"2026-11-01","goals":[{"id","title","kind":"week"|"month",
    "period":"yyyy-MM-dd","status":"open"|"done"|"dropped","order","parentId","created","updatedAt"}]}`.

- [ ] **Step 1: Write the failing tests:** `A_board_round_trips` (three goals, a parent link, both seen dates, UTC
  times written whatever the offset in); `A_missing_file_is_an_empty_board`; `A_period_not_on_its_first_day_is_snapped`
  (a hand-edited week "2026-11-25" reads as 2026-11-23); `An_unknown_status_reads_as_open` and
  `An_unknown_kind_reads_as_a_week`; `A_newer_version_is_read_as_this_one`; `An_unreadable_file_is_kept_aside` (garbage
  in `goals.json` and `.previous`: `Load` fails, both are moved to `.bak` names, the error says so, and a following
  `Save` writes a fresh file without touching the `.bak` ones); `The_previous_copy_rescues_a_broken_file`. Keep every
  `JsonTaskStoreTests` test green after the move.
- [ ] **Step 2: Run them to verify they fail.**
  Run: `dotnet test tests/Deskbar.Core.Tests -c Release --filter "FullyQualifiedName~JsonGoalStore|FullyQualifiedName~JsonTaskStore"`
- [ ] **Step 3: Implement** with private DTO records and tolerant name converters, as `JsonTaskStore` does (a
  `Maybe` becomes a nullable field, every time `ToUniversalTime()` on write and read, `DateOnly` as `yyyy-MM-dd`).
  Header comment: why its own file (spec, "Calls made", the older build that drops what it does not know).
- [ ] **Step 4: Run the tests to verify they pass.** Same command, then the whole Core project once.
- [ ] **Step 5: Commit** `feat: goals.json, written as durably as tasks.json`

### Task 4: The goal service, progress and the review's due state

**Files:**
- Create: `src/Deskbar.Core/Goals/GoalService.cs`, `src/Deskbar.Core/Goals/GoalProgress.cs`,
  `src/Deskbar.Core/Goals/GoalReview.cs`, `src/Deskbar.Core/Goals/TaskGoalFilter.cs`
- Test: `tests/Deskbar.Core.Tests/Goals/GoalServiceTests.cs`, `GoalProgressTests.cs`, `GoalReviewTests.cs`

**Interfaces:**
- Consumes: Tasks 1 to 3; `TaskService.SetGoal`, `TaskService.DetachGoal`, `TaskService.Board`.
- Produces:
  - `sealed record GoalProgress(int TasksDone, int Tasks, int WeeksDone, int Weeks)` with
    `static GoalProgress Of(Goal goal, GoalBoard goals, TaskBoard tasks)` and `string Text`. Tasks: every task whose
    `GoalId` is the goal, done when `Done` or `Closed`. Weeks (monthly only): its children that are not `Dropped`,
    done when `Done`. Text: weekly "3 of 5 done", or "no tasks yet"; monthly "2 of 4 weeks' goals done" plus
    ", 1 of 2 tasks" when it has direct tasks, "1 of 2 tasks done" with no weekly goals, "nothing attached yet" with
    neither.
  - `enum ReviewDue { None, Week, Month }`; `static class GoalReview` with
    `ReviewDue Due(GoalBoard board, DateOnly today)` (Month when `MonthSeen` is none or before this month's 1st,
    else Week when `WeekSeen` is none or before this week's Monday, else None),
    `IReadOnlyList<Goal> Pending(GoalBoard board, DateOnly today)` (open goals whose period ends before the current
    period of their kind: monthly first, then weekly, each oldest period first, then `Order`), and
    `string PromptText(ReviewDue due)` ("Plan this week", "Plan this month", "" for None).
  - `sealed record TaskGoalFilter` with `static All`, `static NoGoal`, `static On(Goal goal, GoalBoard goals)` (a
    monthly goal admits its own tasks and its weekly children's), `bool Admits(TaskItem task, GoalBoard goals)` (a
    dangling id counts as no goal), `TaskBoard Narrow(TaskBoard board, GoalBoard goals)`.
  - `GoalService(IGoalStore store, TaskService tasks, Func<DateTimeOffset> now, Func<DateOnly> today,
    Func<Guid>? newId = null, Action<string>? trace = null)` with `GoalBoard Board`, `IObservable<Unit> Changed`,
    `Result Load()`, `Maybe<string> LoadProblem`, `GoalPeriod ThisWeek`, `GoalPeriod ThisMonth`,
    `Result<Goal> Add(string title, GoalPeriod period)`, `Result Rename(Guid id, string title)`, `MarkDone`, `Drop`,
    `Reopen`, `Result Carry(Guid id, GoalPeriod to)`, `Result CarryToNow(Guid id)` (to the current period of its kind),
    `Result SetParent(Guid id, Maybe<Guid> parent)`, `Result Delete(Guid id)` (also `tasks.DetachGoal(id)`),
    `Result LinkTask(Guid taskId, Maybe<Guid> goalId)` (the goal must exist and be open: "The goal is done",
    "The goal is dropped"; none always works), `Maybe<Goal> GoalOf(TaskItem task)`, `GoalProgress ProgressOf(Goal goal)`,
    `ReviewDue PromptDue()`, `IReadOnlyList<Goal> ToReview()`, `void Seen()` (saves only when the board changed).

- [ ] **Step 1: Write the failing tests** (`FakeGoalStore`, a real `TaskService` on an in-memory store, `today` and
  `now` captured fields):
  - `Every_change_saves_and_pulses` (one row per change above, as `TaskServiceTests` does).
  - `A_failed_save_keeps_the_change_in_memory`; `A_failed_load_is_reported` (`LoadProblem` set, board empty).
  - `A_failed_load_leaves_task_links_alone`: tasks linked to goals, the goal load fails, nothing in the task board
    changes and nothing is saved to the task store.
  - `Linking_needs_an_open_goal`; `Linking_to_none_always_works`; `Linking_changes_no_flag`.
  - `Deleting_a_goal_detaches_its_tasks_and_children`.
  - `A_dangling_link_reads_as_no_goal`: a task names a Guid no goal has; `GoalOf` is none, `TaskGoalFilter.NoGoal`
    admits it, `LinkTask` to a real goal works.
  - `This_week_is_the_local_week_not_the_utc_one`: `now` = Monday 2026-11-23 00:30 UTC, `today` = Sunday 2026-11-22;
    `ThisWeek.Start` is 2026-11-16 and `Add` puts the goal there.
  - `GoalProgressTests`: one per text form above, with a Closed task counted as done and a dropped weekly child left
    out of both counts.
  - `GoalReviewTests`: `Nothing_seen_is_a_month_due`; `A_new_week_inside_a_seen_month_is_a_week_due`;
    `A_new_month_is_a_month_due_even_when_the_week_was_seen`;
    `The_prompt_is_due_on_the_first_run_of_a_week_even_on_a_tuesday` (seen Monday 2026-11-16, today Tuesday
    2026-11-24 → Week; after `Seen` → None for the rest of that week, Week again on 2026-11-30);
    `Pending_lists_open_goals_of_earlier_periods_only` (done, dropped and current ones left out; a goal two weeks back
    included).
- [ ] **Step 2: Run them to verify they fail.**
  Run: `dotnet test tests/Deskbar.Core.Tests -c Release --filter "FullyQualifiedName~Goals"`
- [ ] **Step 3: Implement** as `TaskService` is built (`Apply`, `CommitIfChanged`, a failed save traced, never thrown).
  `Changed` pulses on goal changes only; views that show both also subscribe to `TaskService.Changed`. The header names
  the spec's sections and the later Claude-session hook (see the end of this plan).
- [ ] **Step 4: Run the tests to verify they pass.** Same command, then the whole Core project.
- [ ] **Step 5: Commit** `feat: the goal service, its progress counts and when the review is due`

### Task 5: The `goal` commands and `task goal`, wired in the app

**Files:**
- Create: `src/Deskbar.Core/Control/RemoteControl.Goals.cs`
- Modify: `src/Deskbar.Core/Control/RemoteControl.cs` (constructor parameter `GoalService? goals = null` at the end, a
  header paragraph, dispatch arm `("goal", _) => Goal(rest)`), `src/Deskbar.Core/Control/RemoteControl.Tasks.cs`
  (`["goal", var id, var goal]`, and `Describe` gains `goal`), `src/Deskbar.Core/Control/ControlUsage.cs` (a "Goals"
  section after "Tasks"), `src/Deskbar.App/App.xaml.cs` (build and load the service after `tasks`, pass it to
  `RemoteControl`)
- Test: `tests/Deskbar.Core.Tests/RemoteControlGoalTests.cs`, `RemoteControlTaskTests.cs`, `RemoteControlMenuTests.cs`

**Interfaces:**
- Consumes: Task 4's `GoalService`.
- Produces the forms (ids by full Guid or prefix of 4; `<date>` is any `yyyy-MM-dd` inside the period; a bare
  `--week` or `--month` means the current one, and no period option means the current week):
  - `goal list [--week [<date>] | --month [<date>] | --all]`: default this week's and this month's goals; JSON array
    of `{id, title, kind, period, status, order, parent, progress, tasksDone, tasks, weeksDone, weeks, taskIds}`
    (`kind` week|month, `period` the start date, `status` open|done|dropped, `weeks`/`weeksDone` null on a weekly goal).
  - `goal add <title> [--week [<date>] | --month [<date>]]`
  - `goal done <id>`, `goal drop <id>`, `goal reopen <id>`, `goal rename <id> <title>`, `goal delete <id>` (the reply
    is the goal as it was)
  - `goal carry <id> [--week [<date>] | --month [<date>]]`: default the current period of the goal's own kind
  - `goal parent <id> <monthly-id>|none`
  - `goal prompt [dismiss]`: `{due: "week"|"month"|null}`; `dismiss` is the bar line's close sign
  - `task goal <id> <goal-id>|none`
  Every reply but `list`, `prompt` and `delete` is the goal or task as it is after the change.

- [ ] **Step 1: Write the failing tests:** `Add_goes_to_this_week_by_default`; `Add_with_month_and_a_date`;
  `A_date_that_is_not_a_date_is_refused` ("'30-11' is not a date; use yyyy-MM-dd"); `List_is_this_week_and_month_as_json`
  (with progress fields); `List_all_includes_past_periods`; `Done_drop_reopen_rename_delete`;
  `Carry_moves_to_the_current_period_and_keeps_the_id`; `Carry_refuses_another_kind`; `Parent_takes_a_month_the_week_touches`;
  `Prompt_says_what_is_due_and_dismiss_clears_it`; `Unknown_and_ambiguous_ids_change_nothing`;
  `Goals_are_not_available_without_the_service` ("Goals are not available"); in `RemoteControlTaskTests`:
  `Task_goal_links_and_none_unlinks`, `Task_goal_refuses_a_done_goal`, `Task_list_names_the_goal`; in
  `RemoteControlMenuTests`: add `"goal"` to `Help_lists_every_command`.
- [ ] **Step 2: Run them to verify they fail.**
  Run: `dotnet test tests/Deskbar.Core.Tests -c Release --filter "FullyQualifiedName~RemoteControlGoal|FullyQualifiedName~RemoteControlTask|FullyQualifiedName~Help_lists"`
- [ ] **Step 3: Implement** in the shape of `RemoteControl.Tasks.cs` (parse and look up everything before the first
  service call; one-line failures; a `GoalUsage()` built from `ControlUsage`). `goal` is NOT added to
  `ControlArguments.AboutTheCaller`: no goal form depends on the caller's folder. In App, the service is built right
  after `tasks` with `new JsonGoalStore(stateDir, trace)`, `() => DateTimeOffset.UtcNow`,
  `() => DateOnly.FromDateTime(DateTime.Now)`; a failed load is traced like the tasks'.
- [ ] **Step 4: Run the tests, the Core project, then `dotnet build Deskbar.sln -c Release -warnaserror`.** 0 warnings.
- [ ] **Step 5: Commit** `feat: goal commands on the control pipe, and task goal links a task`

### Task 6: The tasks window links a task to a goal and filters by goal

**Files:**
- Modify: `src/Deskbar.App/TasksBoard.cs` (constructor gains `GoalService? goals = null`; subscribes to
  `goals.Changed`; `TasksView` gains `Goals` and `GoalFilterLabel`; `TaskRowView` gains `GoalId`; actions gain
  `SetGoal` and `FilterGoal`), `src/Deskbar.App/TasksWindow.xaml.cs` (the More menu's "Goal" submenu, the toolbar's
  goal filter, both in `Signature`), `src/Deskbar.App/App.xaml.cs` (hand `goals` to `TasksBoard`)
- Test: `tests/Deskbar.Windows.Tests/TasksBoardGoalTests.cs`

**Interfaces:**
- Consumes: `GoalService.Board`, `ThisWeek`, `ThisMonth`, `LinkTask`, `GoalOf`; `TaskGoalFilter`.
- Produces: `internal sealed record GoalChoice(Maybe<Guid> Goal, string Title, string Period)`;
  `TasksBoard.SetGoal(Guid task, Maybe<Guid> goal) -> Result`; `TasksBoard.FilterGoal(TaskGoalFilter shown)`;
  `TasksWindowActions` gains `Action<Guid, Maybe<Guid>> SetGoal` and `Action<TaskGoalFilter> FilterGoal`;
  `TasksWindow.OpenGoalMenu(Guid id) -> ContextMenu` (internal, for the tests).

- [ ] **Step 1: Write the failing tests** (the `TasksBoardTests` harness, plus a `GoalService` on `FakeGoalStore`):
  - `The_goal_menu_lists_this_weeks_and_months_open_goals_and_none`: "None" first, then this month's open goals, then
    this week's, the task's own goal checked; done, dropped and other periods' goals absent.
  - `Choosing_a_goal_links_the_task_and_none_unlinks_it` (through `LinkTask`; flags and order unchanged).
  - `With_no_goals_the_menu_says_how_to_add_one`: "None" plus a disabled item "No goals this week or month".
  - `The_goal_filter_narrows_the_list_and_the_matrix`: "Goal: all ▾" offers "All goals", "No goal", then the goals;
    a weekly goal shows its tasks only; a monthly goal also its weekly goals' tasks; the filter chips' counts follow.
  - `A_goal_renamed_from_the_pipe_redraws_the_filter_label`.
  - `The_goal_menu_holds_a_redraw_until_it_closes` (MustWait, the #77 lesson).
- [ ] **Step 2: Run them to verify they fail.**
  Run: `dotnet build Deskbar.sln -c Release`, then
  `pwsh scripts/test-hidden.ps1 -Arguments 'Deskbar.sln -c Release --no-build --filter "FullyQualifiedName~TasksBoardGoal"'`
- [ ] **Step 3: Implement.** `View()` narrows the board with `TaskGoalFilter.Narrow` before `TaskViews.Grouped`,
  `Matrix` and `Count`, so `TaskViews` keeps its signatures. The goal submenu is a plain submenu of TextBlock headers
  (no colours: goals have none). Every new static brush frozen. Without a `GoalService` (the existing tests) there is
  no "Goal" item and no goal filter.
- [ ] **Step 4: Run the focused tests, then the whole Windows project through the hidden desktop, then the Release
  build with `-warnaserror`.**
- [ ] **Step 5: Commit** `feat: the tasks window links a task to a goal and filters by goal`

### Task 7: The bar's tooltip names a task's goal

**Files:**
- Modify: `src/Deskbar.App/FloatingBar.TaskList.cs` (`TaskLine` gains `string? GoalTitle` init; `Lines(...)` gains a
  `Func<TaskItem, Maybe<string>> goalTitle` parameter; `ShapeOf` includes it; `TipOf` adds "Goal: <title>";
  `internal Func<TaskItem, Maybe<string>> GoalTitle { get; init; }` and `internal IObservable<Unit>? GoalChanges { init; }`
  subscribed to `PaintTaskList`), `src/Deskbar.App/App.xaml.cs` (both set from `goals`)
- Test: `tests/Deskbar.Windows.Tests/FloatingBarTaskListTests.cs`

- [ ] **Step 1: Write the failing tests:** `A_lines_tooltip_names_its_goal` ("Goal: Ship the goals planner" as the
  tooltip's last line; no such line without a goal); `Renaming_the_goal_updates_the_tooltip_without_moving_lines`
  (after the pulse the tooltip says the new title and `TaskClickIgnored` is not started: the places key is unchanged);
  `The_bar_shows_no_goal_chip` (the line's visual children are the same count with and without a goal).
- [ ] **Step 2: Run them to verify they fail** (hidden desktop, `FullyQualifiedName~FloatingBarTaskList`).
- [ ] **Step 3: Implement.** The goal title is part of the shape (a repaint), not of the places key (the click guard).
- [ ] **Step 4: Run the focused tests, the Windows project, the Release build.**
- [ ] **Step 5: Commit** `feat: a task's tooltip on the bar names its goal`

### Task 8: Pull request 1

- [ ] **Step 1:** Docs: `docs/features/goals.md` (what a goal is, how tasks count, the pipe commands, "the planner
  comes in the next release"), a Goals row in `docs/features/scripts.md`, the goal menu and filter in
  `docs/features/tasks.md`, one README paragraph linking to `goals.md`, manual test script steps for the More menu,
  the filter and the tooltip. Name the screencasts to re-film (none new for PR 1; `tasks-window` shows the toolbar).
  Commit `docs: goals, the goal menu and the goal filter`.
- [ ] **Step 2:** Release build with `-warnaserror` and both test projects, clean.
- [ ] **Step 3 (controller):** whole-PR review (ledger Ruling 6) and its one fix wave.
- [ ] **Step 4 (controller):** swap Petre's Deskbar to this build in one command; live checklist: `deskbar.cmd goal add
  "Ship goals"`, link a task from the More menu, hover the task on the bar, filter by the goal.
- [ ] **Step 5:** Push, open the PR against `feat/tasks-window` with release notes for users (goals from the command
  line, a task's More menu "Goal", the goal filter, the tooltip; the planner follows), body ending `*opus5.5*`. Arm the
  base watch from CLAUDE.md. Whether it carries the `no-release` label is an open question for the controller.

---

# Pull request 2: the planner window and the review line

### Task 9: The planner's choices, pure

**Files:**
- Create: `src/Deskbar.Core/Goals/PlannerViews.cs`
- Test: `tests/Deskbar.Core.Tests/Goals/PlannerViewsTests.cs`

**Interfaces:**
- Produces `static class PlannerViews`:
  - `GoalPeriod MonthShown(GoalPeriod week, DateOnly today)`: today's month when the week contains today, else the
    month of the week's Thursday (where most of the week lies).
  - `IReadOnlyList<Goal> ParentChoices(GoalBoard goals, Goal weekly)`: open monthly goals of every month the week
    touches, earlier month first, each in its order; the current parent included even if done.
  - `IReadOnlyList<TaskItem> Unattached(TaskBoard tasks, GoalBoard goals)`: open tasks with no goal or a dangling
    one, newest `Created` first.
  - `IReadOnlyList<TaskItem> TasksOf(Guid goal, TaskBoard tasks)`: its open tasks in the pile's order, then its
    closed ones, the latest closed first.

- [ ] **Step 1: Write the failing tests:** `The_month_shown_is_todays_on_the_current_week`;
  `Another_week_shows_the_month_of_its_thursday` (week 2026-11-30 seen from October shows December);
  `The_parent_menu_offers_both_months_of_a_split_week`; `Unattached_is_newest_first_and_skips_closed_and_linked`
  (a dangling link included); `A_goals_tasks_put_closed_ones_last`.
- [ ] **Step 2: Run them to verify they fail.** `dotnet test tests/Deskbar.Core.Tests -c Release --filter "FullyQualifiedName~PlannerViews"`
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the tests to verify they pass.**
- [ ] **Step 5: Commit** `feat: what the planner shows, decided in one place`

### Task 10: One remembered place for both windows

**Files:**
- Create: `src/Deskbar.App/RememberedWindowPlace.cs`
- Modify: `src/Deskbar.App/TasksWindow.xaml.cs` (`PlaceAt`, `PlaceDevice`, the first placement beside the bar and the
  normal-rectangle note move out; the Matrix width handling, `ListRect` and `listNormal`, stay),
  `src/Deskbar.Core/Persistence/AppState.cs` (`TasksWindowState? PlannerWindow`, same record, its own comment),
  `src/Deskbar.Core/WorkspaceManager.cs` (`Result SavePlannerWindow(TasksWindowState state)`, as `SaveTasksWindow`)
- Test: `tests/Deskbar.Windows.Tests/TasksBoardTests.cs` (unchanged, must stay green),
  `tests/Deskbar.Core.Tests/WorkspaceManagerTests.cs`

**Interfaces:**
- Produces `internal static class RememberedWindowPlace`: `bool PlaceAt(Window window, TasksWindowState place)`
  (false when no screen holds the place's middle), `void PlaceDevice(Window window, TasksWindowState place)`,
  `void PlaceNear(Window window, Rect bar, nint anchor, Size preferred)`, `Rect NoteNormal(Window window, Rect last)`
  (the window's rectangle in device pixels while it is in the normal state, else `last`).
- Produces `WorkspaceManager.SavePlannerWindow`.

- [ ] **Step 1: Write the failing test:** `Saving_the_planner_place_persists_and_an_unchanged_one_writes_nothing`
  (Core, as the tasks window's twin test).
- [ ] **Step 2: Run it to verify it fails.**
- [ ] **Step 3: Move** the code with its comments (the device-pixel reasoning of fix round 1, Important 3, and review
  I-2 on maximize) into `RememberedWindowPlace`; `TasksWindow` calls it. No behaviour change.
- [ ] **Step 4: Run the Core test, then every `TasksBoardTests` test through the hidden desktop, then the Release
  build.** All green, 0 warnings.
- [ ] **Step 5: Commit** `refactor: the tasks window's remembered place is shared, for the planner`

### Task 11: The planner window, its goals and its pipe twin

**Files:**
- Create: `src/Deskbar.App/PlannerWindow.xaml`, `PlannerWindow.xaml.cs` (display only), `src/Deskbar.App/PlannerBoard.cs`
  (controller)
- Modify: `src/Deskbar.App/TasksWindow.xaml.cs` (a "Planner" chip in the toolbar end),
  `src/Deskbar.Core/Control/RemoteControl.cs` and `RemoteControl.Goals.cs`
  (`Func<bool, Result>? setPlannerWindow = null`, `planner-window show|hide`), `ControlUsage.cs`, `App.xaml.cs`
- Test: `tests/Deskbar.Windows.Tests/PlannerBoardTests.cs`, `tests/Deskbar.Core.Tests/RemoteControlGoalTests.cs`,
  `RemoteControlMenuTests.cs` (`"planner-window"` in the help list)

**Interfaces:**
- Consumes: `GoalService`, `TaskService`, `PlannerViews`, `TasksBoard.Arrive`, Task 10's `RememberedWindowPlace` and
  `WorkspaceManager.SavePlannerWindow`.
- Produces:
  - `PlannerBoard(PlannerWindow window, GoalService goals, TaskService tasks, WorkspaceManager manager,
    Func<nint, bool>? bringForward = null)` with `void Open(Rect near)` (marks the prompt seen with `goals.Seen()`,
    shows the current week, and arrives: moved to the desktop in front, then activated, exactly as `TasksBoard.Open`),
    `bool IsOpen`, `nint EnsureHandle()`, `void Dismiss()`, `void Shutdown()`, `void Dispose()`, and one method per
    action: `StepWeek(int by)`, `AddGoal(string title, GoalKind kind)`, `Rename`, `MarkDone`, `Drop`, `Reopen`,
    `Delete`, `SetParent(Guid id, Maybe<Guid> parent)`.
  - `internal sealed record PlannerView(string WeekLabel, string MonthLabel, bool ThisWeek,
    IReadOnlyList<PlannerGoalView> Month, IReadOnlyList<PlannerGoalView> Week, IReadOnlyList<PlannerTaskView> Unattached,
    IReadOnlyList<PlannerGoalView> Review)`, `PlannerGoalView(Guid Id, string Title, GoalKind Kind, GoalStatus Status,
    string Progress, string? Parent, string Period, IReadOnlyList<PlannerTaskView> Tasks)`,
    `PlannerTaskView(Guid Id, string Title, TaskStatus Status)`, and `PlannerWindowActions` (the window's only way
    back, like `TasksWindowActions`).
  - Pipe: `planner-window show|hide`.

- [ ] **Step 0 (controller):** a mockup of the planner (month goals with progress on top, week goals with their tasks
  below, arrows, the add box with its week/month chip, the unattached column, the review strip) shown to Petre next to
  the tasks window, with a recommendation, before Step 3 (memory: mockup before visual changes).
- [ ] **Step 1: Write the failing tests:**
  - `Open_shows_this_month_then_this_week_with_progress` (labels "November 2026", "Week of 23 Nov 2026"; a weekly goal's
    line reads "3 of 5 done" with its tasks under it; a monthly goal "1 of 2 weeks' goals done").
  - `Arrows_step_the_week_and_the_month_follows` (to week 2026-11-30 the month is December).
  - `The_add_box_adds_to_the_week_shown_and_its_chip_switches_to_the_month`; Enter adds, Esc clears.
  - `A_weekly_goals_menu_sets_its_parent` (both months on a split week) and offers done, drop, reopen, rename, delete.
  - `Done_and_dropped_goals_stay_struck_through_and_dim`.
  - `Opening_counts_as_seeing_the_prompt` (`PromptDue` None after `Open`).
  - `Open_moves_the_window_before_it_activates` (as `TasksBoardTests`' Critical 1 test: the seam is called, the window
    never activates itself while a seam is set).
  - `A_remembered_place_comes_back` (AppState.PlannerWindow, device pixels at the screen's scale, as the tasks window's
    test after fix round 4).
  - `A_redraw_waits_for_an_open_menu_or_a_press`.
  - `Planner_window_show_and_hide` (Core, through the seam) and `"planner-window"` in the help list.
- [ ] **Step 2: Run them to verify they fail** (Core directly; Windows through the hidden desktop,
  `FullyQualifiedName~PlannerBoard`).
- [ ] **Step 3: Implement** to the chosen mockup. A regular window titled "Planner", resizable, in Alt+Tab and on
  the taskbar, hidden (not closed) by its X until the app exits, `monitor.Ignore`d before it first shows, its handle
  created early, as App does for the tasks window. It redraws on `goals.Changed`, `tasks.Changed` and a workspace-free
  signature compare, and waits under an open menu, a rename or a press (the tasks window's `MustWait`). The tasks
  window's "Planner" chip calls the same `Open` (the planner's only entry from the UI besides the bar's line; see
  "Calls this plan makes").
- [ ] **Step 4: Run the tests, both projects, the Release build.**
- [ ] **Step 5: Commit** `feat: the planner window, the week's and the month's goals with their progress`

### Task 12: Dragging tasks onto goals, and the review strip

**Files:**
- Create: `src/Deskbar.App/PlannerWindow.Drag.cs`
- Modify: `src/Deskbar.App/PlannerWindow.xaml.cs`, `src/Deskbar.App/PlannerBoard.cs` (`Link(Guid task, Maybe<Guid> goal)`,
  `Carry(Guid id)` via `CarryToNow`, review rows in the view)
- Test: `tests/Deskbar.Windows.Tests/PlannerBoardTests.cs`

**Interfaces:**
- Consumes: `GoalService.LinkTask`, `CarryToNow`, `MarkDone`, `Drop`, `ToReview`; `ArrangeMouse` (the drag seam
  `TasksWindow.DragMouse` uses).
- Produces: `internal static class PlannerDropTarget` with `internal sealed record Area(Maybe<Guid> Goal, Rect Bounds)`
  (none is the unattached column) and `static Area? At(IReadOnlyList<Area> areas, Point at)`;
  `PlannerWindow.DragMouse`, `PlannerWindow.Dragging`.

- [ ] **Step 1: Write the failing tests:**
  - `A_task_dragged_onto_a_goal_joins_it` (a weekly goal and a monthly goal both take a drop; the task leaves the
    column); `A_task_dragged_to_the_column_leaves_its_goal`; `A_drop_on_a_done_goal_changes_nothing`.
  - `A_press_released_in_place_is_a_click_not_a_drag` (threshold), `No_redraw_under_a_drag`, `A_lost_capture_calls_the_drag_off`
    (routed input through the `ArrangeMouse` seam, as the tasks window's drag tests do).
  - `The_review_strip_lists_last_periods_open_goals_with_three_actions`: "Carry over", "Done", "Drop", one click each.
  - `Carry_over_moves_the_goal_and_its_tasks_to_this_week` (same id; the tasks still name it).
  - `An_empty_strip_is_gone`.
- [ ] **Step 2: Run them to verify they fail** (hidden desktop, `FullyQualifiedName~PlannerBoard`).
- [ ] **Step 3: Implement** with the tasks window's drag rules (`TasksWindow.Drag.cs` header): arm on press, start at
  the system drag distance, capture on the surface, drop applied from the dispatcher after the release, an adorner for
  the preview, nothing redrawn during the press or drag.
- [ ] **Step 4: Run the tests, the Windows project, the Release build.**
- [ ] **Step 5: Commit** `feat: drag tasks onto goals, and review last week's goals in one click each`

### Task 13: "Plan this week" on the bar

**Files:**
- Create: `src/Deskbar.App/FloatingBar.PlanPrompt.cs`
- Modify: `src/Deskbar.App/FloatingBar.TaskList.cs` (a `planPromptHost` between `taskProblemHost` and the add row; the
  due state in the shape key and in the places key; the minute tick re-reads it), `src/Deskbar.App/App.xaml.cs`
- Test: `tests/Deskbar.Windows.Tests/FloatingBarPlanPromptTests.cs`

**Interfaces:**
- Consumes: `GoalService.PromptDue`, `Seen`, `Changed`; `PlannerBoard.Open`; `GoalReview.PromptText`.
- Produces on `FloatingBar`: `internal Func<ReviewDue> PlanDue { get; init; } = () => ReviewDue.None`,
  `internal Action? OpenPlanner { get; init; }`, `internal Action? DismissPlanPrompt { get; init; }`,
  test reads `internal string PlanPromptText` ("" while hidden).

- [ ] **Step 0 (controller):** mockup of the line on the bar, floating and docked, beside the failed-load line; Petre
  picks the look before Step 3.
- [ ] **Step 1: Write the failing tests:**
  - `The_line_shows_when_a_week_is_due_and_says_month_when_both_are`.
  - `Clicking_it_opens_the_planner_and_it_goes` (through `OpenPlanner`, which marks seen in the App).
  - `The_close_sign_dismisses_it_for_the_week` (`DismissPlanPrompt`; still gone after a repaint; back when `PlanDue`
    says Week again).
  - `It_is_the_first_line_of_the_list_and_hides_with_the_task_list` (`StripItem.Tasks` off hides it too).
  - `The_prompt_waits_while_the_bar_is_pressed` (a pulse under a press paints nothing until the release, #48).
  - `A_second_press_of_a_double_click_is_ignored` (the line takes `TaskClickIgnored`).
  - `The_docked_list_settles_with_the_prompt_line`: a docked bar, the line appears, `FitTaskListToFreeHeight` run
    three times gives one count, one line fewer than before.
  - `A_new_day_brings_the_line_without_a_pulse` (the minute tick sees `PlanDue` change and paints).
- [ ] **Step 2: Run them to verify they fail** (hidden desktop, `FullyQualifiedName~FloatingBarPlanPrompt`).
- [ ] **Step 3: Implement** to the chosen mockup. Painted only by `PaintTaskList`, so it inherits the rebuild guard,
  `TaskListMustWait` and the docked fit pass; it takes no width (inside `TakesNoWidth`, trimming). Brushes frozen. In
  App: `PlanDue = goals.PromptDue`, `OpenPlanner = () => plannerBoard?.Open(bar bounds)`,
  `DismissPlanPrompt = goals.Seen`, `GoalChanges` already repaints.
- [ ] **Step 4: Run the tests, the Windows project, the Release build.**
- [ ] **Step 5: Commit** `feat: the bar says "Plan this week" once a week, and "Plan this month" once a month`

### Task 14: Pull request 2

- [ ] **Step 1:** `docs/features/goals.md` gains the planner, the review and the bar's line; `scripts.md` gains
  `planner-window`; README paragraph updated; manual test script steps (open from the bar line, carry over, drag a
  task on and off, a split week's parent menu). Screencasts to film: `planner`, `plan-this-week`. Commit
  `docs: the planner and the weekly review`.
- [ ] **Step 2:** Release build with `-warnaserror` and both projects, clean.
- [ ] **Step 3 (controller):** whole-PR review and its fix wave; swap Petre's bar; live check (the line shows on a
  fresh `goals.json`, the planner opens on the desktop he is on, carry over, drag).
- [ ] **Step 4:** Push, PR stacked on PR 1 with release notes for users, body ending `*opus5.5*`; base watch.

---

## Later: Claude sessions (not in this plan)

When the Claude-session work (tasks plan, Tasks 6 to 9) and this plan have both landed, whichever lands second adds:
`SessionBriefing.Start` lists `goals.Board.In(goals.ThisWeek)` open goals by title and id, and the skill text in
`ClaudeSkill.Text(...)` says a session may attach its own task with `deskbar.cmd task goal <id> <goal-id>` and never
adds, drops or marks a goal done. The hook point is `RemoteControl.ClaudeHook.cs`, which already has the
`RemoteControl` that Task 5 hands a `GoalService`.
