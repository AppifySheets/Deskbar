# Recurring Tasks Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A task can carry a schedule; on each occurrence Deskbar starts a fresh, quiet Claude session on it that does the job in its description, finishes with `task done`, is stopped once idle, and the task sleeps (snoozed) until the next occurrence.

**Architecture:** Core gets a pure schedule (`RepeatSchedule`, parser, next occurrence over a `TimeZoneInfo`), two optional task fields (`Repeat`, `NextRun`), board rules for setting, finishing a run, reopening and waking, and a pure sweep decision (`RecurringRuns.Plan`). The keeper, the comment wake and the first message learn "between runs". The App runs the decision on the existing 30-second snooze sweep through a small `RecurringRunner` that starts sessions with `ClaudeTaskActions.Start(quiet: true)`. The CLI and the tasks panel's "⋯" menu share one parser.

**Tech Stack:** C# / .NET 10, WPF, System.Reactive, CSharpFunctionalExtensions, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-26-recurring-tasks-design.md` (task [0b78269e](https://deskbar.app/task#0b78269e-3365-4401-9d2d-635512282d7a)). Read it with this plan.

## Global Constraints

- Schedule text, one parser for CLI and menu: `every <n>m`, `every <n>h` (minimum 15 minutes, maximum 24 hours), each optionally followed by a same-day window `HH:MM-HH:MM` (start before end); `daily HH:MM`; `weekdays HH:MM`; `weekly <day> HH:MM` (day by full or three-letter English name). Case and extra spaces ignored; `H:MM` accepted, written back as `HH:MM`.
- Schedules are local wall-clock time; every stored time is UTC. A daylight-saving change moves `daily 09:00` with the clock.
- The next occurrence is always computed from now (when the repeat is set, when a run starts, when a run ends, on a skip). No catch-up, ever.
- The repeat carries its model, `TaskRepeat.DefaultModel = "sonnet"`, checked by `ClaudeStart.Model`; other task sessions keep `claude-model`.
- The repeat carries its folder, stored as `ClaudeStart.RepoOf(folder)` (worktrees are deleted after their merge).
- tasks.json stays version 2; `repeat` and `nextRun` are optional fields, read tolerantly, left out when none.
- A run never takes focus, opens no window and switches no workspace (`ClaudeTaskActions.Start(..., quiet: true)`).
- At most one run per task: an occurrence while a session of the task is listed is skipped, not queued.
- The manager may run `task repeat <id> ...`; its `--session` forms stay refused (`RemoteControl.Tasks.cs:110`). It still may not close or finish anything.
- Every new or changed member carries a comment that says WHY, naming the spec and task 0b78269e, in the style of the files around it (long comments are the project's record).
- C# style: expression-bodied members, `var`, records and init-only properties, readonly collections, LINQ over loops, `Maybe`/`Result` for expected failures, no `private` keyword, Rx over events.
- `ControlUsage` lists `task repeat` (a dispatched command missing there is a bug).
- Core tests: `dotnet test tests/Deskbar.Core.Tests --filter "<filter>"`. Windows tests ONLY through `pwsh scripts/test-hidden.ps1 -Arguments 'tests/Deskbar.Windows.Tests --filter "<filter>"'`, never on the desktop.
- Before the PR: `dotnet build Deskbar.sln -c Release -warnaserror` and `dotnet test Deskbar.sln --filter "Category!=Integration"` (Windows tests through the script). The PR has a `## Release notes` section for users.
- Visual change (the repeat mark) gets a mockup and Petre's pick before code (Task 9).
- Commits end with one italic line naming the executing model's short name; no Co-Authored-By trailer. No real people's names anywhere.

## Review Focus

- A daylight-saving change between now and the next run: `daily 09:00` still runs at 09:00 on the clock, and `daily 02:30` on the spring-forward night runs at 03:00, once. Tests in Task 1.
- The PC asleep or off through six hourly occurrences: one run when it is back, the next an hour after that start. Test in Task 4.
- Petre has the last run's terminal open when the next occurrence comes: skipped, NextRun advanced, no second session, and the open session is not stopped under him. Tests in Tasks 4 and 5.
- A run that cannot start (folder removed, folder untrusted, Claude missing) on an hourly schedule: one question on the task, then silence until Petre acts, never a comment per hour. Tests in Tasks 4 and 7.
- A hand-edited or newer tasks.json with a `repeat` the parser refuses (`"every 2m"`, `42`): the file loads, that task simply has no repeat, nothing is lost. Test in Task 2.

## Calls made in this plan (the design left them open, or the code disagreed)

- "Between runs" is an open task with a repeat in Backlog or Done. The design's "not InProgress" would include Waiting on you, and `SessionKeeper.Stops` would then stop a session asking Petre something, which its header forbids (`SessionKeeper.cs:193-198`).
- The sweep does not set InProgress; the new session's `task join` does. Set by the sweep, a start that failed would leave an InProgress task whose previous run's session the keeper then resumes (`SessionKeeper.cs:138-156`). The sweep also skips InProgress (a run in flight, or one the keeper is reviving).
- A failed start asks once (`StartFailedQuestion`), like a gone folder, so a broken hourly job does not write a line an hour. Setting the repeat again answers either question.
- Every recurring task runs Claude: setting a repeat assigns it to Claude. The Petre-only reminder variant is not built.
- The stored folder is the repo, not a worktree (`ClaudeStart.RepoOf`, as `ClaudeStart.Folders` does at `ClaudeStart.cs:84-86`).
- A between-runs stop writes no thread line (`ClaudeSessions.cs:639` would add one an hour).
- The time zone is injected into `TaskService` (`TimeZoneInfo`), so Core stays testable; the App passes `TimeZoneInfo.Local`.
- Windows are same-day (start before end); intervals run 15 minutes to 24 hours.

---

### Task 1: The schedule (Core, pure)

**Files:**
- Create: `src/Deskbar.Core/Tasks/TaskRepeat.cs`
- Test: `tests/Deskbar.Core.Tests/Tasks/TaskRepeatTests.cs`

**Interfaces:**
- Produces: `public sealed record RepeatWindow(TimeOnly From, TimeOnly To)`.
- Produces: `public abstract record RepeatSchedule` with nested `Every(TimeSpan Interval, Maybe<RepeatWindow> Window)`, `Daily(TimeOnly At)`, `Weekdays(TimeOnly At)`, `Weekly(DayOfWeek Day, TimeOnly At)`; members `string Text` (canonical CLI text: `every 1h`, `every 90m`, `every 1h 09:00-19:00`, `daily 09:00`, `weekdays 09:00`, `weekly monday 09:00`), `string Label` (menu and tooltip words: `every hour`, `every 2 hours`, `every 90 minutes`, `every hour, 09:00-19:00`, `every day at 09:00`, `every weekday at 09:00`, `every Monday at 09:00`), `DateTimeOffset Next(DateTimeOffset now, TimeZoneInfo zone)` (UTC result, strictly after `now`).
- Produces: `public sealed record TaskRepeat(RepeatSchedule Schedule, string Model, string Folder)` with `public const string DefaultModel = "sonnet"`.
- Produces: `public static class TaskRepeats` with `Result<RepeatSchedule> Parse(string text)`, `TimeSpan MinInterval` (15 min), `TimeSpan MaxInterval` (24 h), `IReadOnlyList<(string Label, string Text)> MenuChoices` (`("Every hour", "every 1h")`, `("Every hour, 09:00-19:00", "every 1h 09:00-19:00")`, `("Every day at 09:00", "daily 09:00")`, `("Every weekday at 09:00", "weekdays 09:00")`, `("Every Monday at 09:00", "weekly monday 09:00")`), and `Result<RepeatRequest> ParseRequest(IReadOnlyList<string> words)`.
- Produces: `public abstract record RepeatRequest` with `Off` and `Set(RepeatSchedule Schedule, Maybe<string> Model, Maybe<string> Folder)`: the words after `task repeat <id>` (or a Custom… line), `off` alone, or schedule words plus `--model <m>` and `--folder <p>` once each in any position; the model checked by `ClaudeStart.Model`.

- [ ] **Step 1: Write the failing tests.** Fixed zone `TimeZoneInfo.CreateCustomTimeZone("T+4", TimeSpan.FromHours(4), "T+4", "T+4")`; `Wednesday = new DateTimeOffset(2026, 11, 25, 14, 20, 0, TimeSpan.FromHours(4))`; DST zone `TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time")` (2026: forward 29 March 02:00, back 25 October 03:00).
  - `Every_interval_is_an_interval_from_now`: `every 1h` from Wednesday is 15:20 local; `every 15m` is 14:35.
  - `A_window_runs_inside_it_and_starts_the_day_at_its_start` (Theory): `every 1h 09:00-19:00` from 14:20 is 15:20; from 18:30 is Thu 09:00; from 19:00 is Thu 09:00; from 07:00 is 09:00 the same day; set at 20:00 is Thu 09:00.
  - `Daily_weekdays_and_weekly_are_the_next_clock_time_strictly_after_now`: `daily 09:00` from Wednesday is Thu 26 09:00; `daily 15:00` is today 15:00; `weekdays 09:00` from Fri 27 Nov 10:00 is Mon 30 Nov 09:00; `weekly monday 09:00` from Mon 30 Nov 08:00 is 30 Nov 09:00 and from Mon 30 Nov 09:00 is Mon 7 Dec 09:00.
  - `Daily_nine_keeps_nine_across_spring_forward`: from 2026-03-28 20:00 +01:00 `daily 09:00` is 2026-03-29 09:00 +02:00.
  - `A_time_in_the_spring_gap_runs_at_the_first_valid_minute_and_an_ambiguous_one_at_its_first_occurrence`: `daily 02:30` from 2026-03-28 20:00 +01:00 is 2026-03-29 03:00 +02:00; from 2026-10-24 20:00 +02:00 is 2026-10-25 02:30 +02:00.
  - `Parse_accepts_every_form_and_text_round_trips` (Theory over `every 15m`, `EVERY 2H`, `every 90m`, `every 1h 9:00-19:00`, `daily 09:00`, `Weekdays 07:30`, `weekly Mon 09:00`, `weekly sunday 18:00`): `Parse(Parse(s).Value.Text).Value == Parse(s).Value`, and `every 1h 9:00-19:00` writes `every 1h 09:00-19:00`, `every 120m` writes `every 2h`.
  - `Parse_refuses_nonsense_with_a_line_naming_the_forms` (Theory: `""`, `hourly`, `every 10m`, `every 25h`, `every 1h 19:00-09:00`, `daily 25:00`, `weekly funday 09:00`, `every 0h`): `IsFailure`, error mentions `every <n>m`.
  - `Labels_say_it_in_words`: each `MenuChoices` text parses to a schedule whose `Label` is the choice's label lower-cased after its first word (`every hour`, `every hour, 09:00-19:00`, `every day at 09:00`, `every weekday at 09:00`, `every Monday at 09:00`).
  - `ParseRequest_reads_off_and_options_anywhere`: `["off"]` is `Off`; `["every", "1h", "--model", "haiku"]` is `Set(every 1h, "haiku", None)`; `["--folder", @"C:\r", "daily", "09:00"]` is `Set(daily 09:00, None, @"C:\r")`; `["every", "1h", "--model", "bad name"]`, `["every", "1h", "--model", "a", "--model", "b"]` and `[]` fail.
- [ ] **Step 2: Run** `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~TaskRepeatTests"`. Expected: FAIL, types missing.
- [ ] **Step 3: Implement `TaskRepeat.cs`.** Regexes as `TaskSnooze.Parse` does, one per form, tried in turn. `Next` works on the local wall clock (`TimeZoneInfo.ConvertTime(now, zone)`), then turns a wall time back into an instant: a time in a spring gap (`zone.IsInvalidTime`) moves forward a minute at a time to the first valid one; an ambiguous one takes `zone.GetAmbiguousTimeOffsets(wall).Max()` (the first occurrence). Window rule: before today's `From`, today's `From`; else `now + Interval` when it is the same local day and at or before `To`; else tomorrow's `From`. Header comment: the forms, why computed from now (no catch-up), why a same-day window only.
- [ ] **Step 4: Run** the same filter. Expected: PASS.
- [ ] **Step 5: Commit** `feat: recurring task schedules and their parser`.

### Task 2: The fields and the file

**Files:**
- Modify: `src/Deskbar.Core/Tasks/TaskItem.cs` (after `SnoozedUntil`, ~line 298)
- Modify: `src/Deskbar.Core/Tasks/JsonTaskStore.cs` (header paragraph; `TaskDto` ~96-152; `ToDto` ~236; `FromDto` ~264; a converter beside `TolerantDeployedListConverter` ~411)
- Test: `tests/Deskbar.Core.Tests/Tasks/RecurringTasksTests.cs` (new; file section)

**Interfaces:**
- Consumes: `TaskRepeat`, `TaskRepeats.Parse` (Task 1).
- Produces: `TaskItem.Repeat { get; init; } : Maybe<TaskRepeat>`, `TaskItem.NextRun { get; init; } : Maybe<DateTimeOffset>` (UTC), `TaskItem.IsBetweenRuns => Repeat.HasValue && IsOpen && Status is TaskStatus.Backlog or TaskStatus.Done`.
- Produces in the file: `"repeat": {"schedule": "<Text>", "model": "...", "folder": "..."}` and `"nextRun"`, both absent when none.

- [ ] **Step 1: Write the failing tests** (in a new `RecurringTasksTests` class, temp-dir pattern of `TaskSnoozeTests`):
  - `A_repeat_and_its_next_run_survive_a_save_and_load`: a task with `Repeat = new TaskRepeat(every 1h 09:00-19:00, "sonnet", @"C:\repos\TaskSpaces")` and `NextRun` saved by `JsonTaskStore` loads equal (schedule, model, folder, NextRun in UTC).
  - `A_task_without_a_repeat_writes_neither_field`: the saved JSON text contains neither `"repeat"` nor `"nextRun"`.
  - `A_bad_repeat_reads_as_none_and_never_fails_the_file` (Theory over `"repeat":42`, `"repeat":{"schedule":"every 2m","model":"sonnet","folder":"C:\\r"}`, `"repeat":{"schedule":"daily 09:00"}`, `"nextRun":"soon"`): `Load()` succeeds, the task has `Repeat` none (or `NextRun` none for the last), its title intact.
  - `Between_runs_is_an_open_backlog_or_done_task_with_a_repeat` (Theory over the five statuses): true for Backlog and Done only; false for any status without a repeat.
- [ ] **Step 2: Run** `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~RecurringTasksTests"`. Expected: FAIL.
- [ ] **Step 3: Implement.** `RepeatDto(string? Schedule, string? Model, string? Folder)` behind a `TolerantRepeatConverter` (anything but an object of strings reads as null), `[property: JsonIgnore(Condition = WhenWritingNull)]` on both new `TaskDto` fields, `NextRun` through `TolerantTimeConverter`. `FromDto`: a repeat needs a schedule that parses, a model `ClaudeStart.Model` accepts and a non-blank folder, else none. Add the header paragraph ("RECURRING TASKS ... still version 2; an older build drops them on its next save, which costs the schedule and never a task").
- [ ] **Step 4: Run** the filter. Expected: PASS.
- [ ] **Step 5: Commit** `feat: tasks keep a repeat and their next run in tasks.json`.

### Task 3: The board's rules and the service

**Files:**
- Modify: `src/Deskbar.Core/Tasks/TaskBoard.cs` (`MarkDone` ~415, `Reopen` ~458, `Wake` ~659; a new "recurring tasks" section after snoozing ~682)
- Modify: `src/Deskbar.Core/Tasks/TaskService.cs` (constructor ~20; `MarkDone` ~125, `Reopen` ~131; new methods beside `Snooze` ~157)
- Create: `src/Deskbar.Core/Tasks/RecurringRuns.cs` (the two questions only; Task 4 adds the sweep)
- Test: `tests/Deskbar.Core.Tests/Tasks/RecurringTasksTests.cs` (board section)

**Interfaces:**
- Consumes: Tasks 1 and 2.
- Produces: `public static class RecurringRuns` with `const string FolderGoneQuestion = "This task repeats, but the folder its runs start in is gone. Set the repeat again with a folder (Repeat in its menu, or task repeat <id> <schedule> --folder <path>)."`, `const string StartFailedQuestion = "This task repeats, but its last run could not start; what Claude Code said is in the thread. Set the repeat again, or wake it, once it can run."`, `bool IsRepeatQuestion(Maybe<string> question)` (either of the two), `string FolderGoneNote(string folder)` (`"Scheduled run skipped: {folder} no longer exists."`).
- Produces on `TaskBoard`:
  - `Result<TaskBoard> SetRepeat(Guid id, TaskRepeat repeat, DateTimeOffset now, TimeZoneInfo zone)`: refused on a closed task; sets `Repeat`, `NextRun = Schedule.Next(now, zone)`, `Assignee = Assignee.Claude` (a repeat means Claude runs it); a task waiting with a repeat question (`RecurringRuns.IsRepeatQuestion`, Task 4) goes back to Backlog without it; then, when the task `IsBetweenRuns`, `SnoozedUntil = NextRun`.
  - `Result<TaskBoard> ClearRepeat(Guid id, DateTimeOffset now)`: `Repeat` and `NextRun` none; a snooze equal to the old `NextRun` ends.
  - `Result<TaskBoard> MarkDone(Guid id, DateTimeOffset now, TimeZoneInfo? zone = null)`: unchanged for a task without a repeat. A recurring task goes to Backlog (no Raised stamp, no Pin, question cleared, Assignee stays Claude), `NextRun = Next(now)`, snoozed until it.
  - `Result<TaskBoard> Reopen(Guid id, DateTimeOffset now, TimeZoneInfo? zone = null)`: as today, plus for a recurring task `NextRun = Next(now)` and the snooze until it.
  - `Wake(Guid id, DateTimeOffset now)` (signature unchanged): a task between runs also gets `NextRun = now`, so the next sweep runs it ("Wake now" starts a run).
  - `Result<TaskBoard> RunStarted(Guid id, DateTimeOffset now, TimeZoneInfo zone)`: snooze cleared, `NextRun = Next(now)`. The status is left to the new session's `task join`.
  - `Result<TaskBoard> Rescheduled(Guid id, DateTimeOffset now, TimeZoneInfo zone)`: `NextRun = Next(now)`; snoozed until it only when `IsBetweenRuns`.
  - `zone` null means `TimeZoneInfo.Local`.
- Produces on `TaskService`: constructor parameter `TimeZoneInfo? zone = null` (last), `TimeZoneInfo Zone`; `Result SetRepeat(Guid id, TaskRepeat repeat)`, `Result ClearRepeat(Guid id)`, `Result RunStarted(Guid id)`, `Result Rescheduled(Guid id)` (all `ApplyIfChanged`); `MarkDone` and `Reopen` pass `Zone`.

- [ ] **Step 1: Write the failing tests** (fixed zone T+4, clock Wednesday 14:20 local, `every 1h` unless said):
  - `SetRepeat_snoozes_a_backlog_note_until_its_next_run_and_gives_it_to_claude`: NextRun 15:20, SnoozedUntil 15:20, Assignee Claude, status Backlog.
  - `SetRepeat_on_a_task_in_progress_sets_the_next_run_and_does_not_snooze_it`.
  - `SetRepeat_refuses_a_closed_task_and_changes_nothing`.
  - `SetRepeat_answers_a_repeat_question`: a task asked `RecurringRuns.FolderGoneQuestion` is Backlog, Question none, snoozed after SetRepeat.
  - `ClearRepeat_ends_the_repeat_and_the_snooze_it_made_but_not_a_later_snooze_of_petres`.
  - `Done_on_a_recurring_task_is_the_end_of_a_run`: after `Join` and `MarkDone`, Status Backlog, Raised unchanged, Question none, Assignee Claude, NextRun and SnoozedUntil both `now + 1h`. (Guards `Woken`, `TaskBoard.cs:678`: the snooze must survive the status change.)
  - `Done_on_a_plain_task_is_unchanged`: still Done, pinned, Assignee Me.
  - `Close_keeps_the_repeat_and_reopen_resumes_it_from_now`: reopened two days later, NextRun is reopen time + 1h and the task is snoozed until it.
  - `Wake_now_on_a_task_between_runs_makes_its_run_due_now`: NextRun == now, not snoozed.
  - `RunStarted_wakes_the_task_and_sets_the_following_occurrence`: NextRun now + 1h, not snoozed, status unchanged.
  - `Rescheduled_snoozes_only_between_runs`: Backlog task snoozed until the new NextRun; a WaitingOnYou task gets the new NextRun and stays visible.
  - `The_service_uses_its_zone`: `new TaskService(store, () => clock, zone: T+4)` with `daily 09:00` set at 14:20 local has NextRun Thu 09:00 +04:00.
- [ ] **Step 2: Run** the `RecurringTasksTests` filter. Expected: FAIL.
- [ ] **Step 3: Implement.** Changes that move the status and snooze at once (MarkDone, Reopen, SetRepeat out of a question) apply the snooze in a second `Replaced` after the `Change`, because `Woken` drops a snooze on any status change in the same step; say so in the comment.
- [ ] **Step 4: Run** the filter, then `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~TaskBoardTests|FullyQualifiedName~TaskSnoozeTests"` for regressions. Expected: PASS.
- [ ] **Step 5: Commit** `feat: a recurring task's done ends a run and sleeps until the next`.

### Task 4: The sweep's decision (Core, pure)

**Files:**
- Modify: `src/Deskbar.Core/Tasks/RecurringRuns.cs` (made in Task 3)
- Modify: `src/Deskbar.Core/Tasks/TaskService.cs` (one method beside `WakeDue` ~165)
- Test: `tests/Deskbar.Core.Tests/Tasks/RecurringRunsTests.cs`

**Interfaces:**
- Consumes: `TaskItem.Repeat`, `NextRun`, `IsBetweenRuns`; `RecurringRuns.FolderGoneQuestion`, `IsRepeatQuestion` (Task 3).
- Produces: `public abstract record RunDecision(Guid TaskId)` with `Start(Guid TaskId, TaskRepeat Repeat)`, `Skip(Guid TaskId, string Why)`, `FolderGone(Guid TaskId, string Folder)`, `Schedule(Guid TaskId)`.
- Produces: `RecurringRuns.Plan(TaskBoard board, DateTimeOffset now, Func<TaskItem, bool> running, Func<string, bool> folderExists) : IReadOnlyList<RunDecision>`.
- Produces: `TaskService.DueRuns(Func<TaskItem, bool> running, Func<string, bool> folderExists) : IReadOnlyList<RunDecision>`.

Rules, in order, for each open task with a repeat: no `NextRun` → `Schedule`; `NextRun > now` → nothing; `running(task)` → `Skip("a session is still running")`; Status InProgress or WaitingOnYou → `Skip` (a run in flight, or a question open: the keeper looks after the first, Petre the second); folder missing → `FolderGone`; else `Start`. Closed tasks get nothing.

- [ ] **Step 1: Write the failing tests:**
  - `A_due_task_between_runs_starts_with_its_repeat`: one `Start` carrying the task's `TaskRepeat`.
  - `A_task_whose_run_is_later_gets_nothing`.
  - `A_due_task_with_a_listed_session_is_skipped`: `running` true for it → `Skip`.
  - `A_due_task_in_progress_or_waiting_is_skipped` (Theory).
  - `A_closed_task_gets_nothing_however_late`.
  - `A_due_task_whose_folder_is_gone_says_so_once`: `FolderGone` with the folder; the same task already waiting with `FolderGoneQuestion` gets `Skip`, not a second `FolderGone`.
  - `A_task_without_a_next_run_is_scheduled`.
  - `Six_missed_hours_make_one_run`: NextRun six hours ago gives exactly one `Start`; after `board.RunStarted` the next `Plan` at the same `now` gives nothing and NextRun is now + 1h.
- [ ] **Step 2: Run** `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~RecurringRunsTests"`. Expected: FAIL.
- [ ] **Step 3: Implement** `RecurringRuns.Plan` (header: the rules above and why each skip is a skip, not a queue) and `TaskService.DueRuns`.
- [ ] **Step 4: Run** the filter. Expected: PASS.
- [ ] **Step 5: Commit** `feat: the sweep decides which recurring tasks run now`.

### Task 5: Sessions between runs: the keeper, the comment wake, the first message

**Files:**
- Modify: `src/Deskbar.Core/Tasks/SessionKeeper.cs` (`Plan` resumes ~138 and restarts ~161; `Stops` ~228-254; `KeeperStep.Stop` ~339)
- Modify: `src/Deskbar.Core/Tasks/CommentWake.cs` (`WakePlan`; `Plan` ~43)
- Modify: `src/Deskbar.Core/Tasks/ClaudeStart.cs` (`FirstMessage` ~25)
- Test: `tests/Deskbar.Core.Tests/Tasks/SessionKeeperTests.cs`, `CommentWakeTests.cs`, `ClaudeStartTests.cs`

**Interfaces:**
- Consumes: `TaskItem.IsBetweenRuns`.
- Produces: `KeeperStep.Stop` gains `public bool BetweenRuns { get; init; }`.
- Produces: `WakePlan.FreshRun : WakePlan` (no members): the comment starts a new run instead of reaching an old session.
- Produces: `ClaudeStart.FirstMessage(TaskItem task)` (signature unchanged) words a task with a repeat as a scheduled run.

- [ ] **Step 1: Write the failing tests:**
  - SessionKeeperTests `A_recurring_task_between_runs_is_never_resumed` (Backlog and Done with a repeat, session unlisted, long gone): no step. `A_recurring_task_in_progress_is_resumed_like_any_open_task`: one `Resume`. `A_limited_session_of_a_recurring_task_between_runs_is_not_restarted`: no restart step.
  - SessionKeeperTests `Stops_names_the_idle_background_session_of_a_recurring_task_between_runs`: one `Stop` with `BetweenRuns == true`; `Stops_leaves_it_while_a_window_is_attached`; `Stops_leaves_a_recurring_task_that_waits_on_you`; existing Done and Closed stops have `BetweenRuns == false`.
  - CommentWakeTests `A_comment_between_runs_with_no_session_listed_starts_a_fresh_run` (also for a task with no sessions at all): `WakePlan.FreshRun`. `A_comment_between_runs_while_the_last_run_is_still_listed_goes_to_its_inbox`: `Inbox`. `A_comment_on_a_recurring_task_in_progress_takes_the_usual_road`.
  - ClaudeStartTests `A_recurring_task_starts_as_a_scheduled_run`: contains `$"deskbar task join {id}"`, `"scheduled run"`, `"deskbar task done"`, `"deskbar task ask"` and the schedule's `Label`; with a 2000-character title it is at most 1000 characters and its last line is whole. `A_plain_task_starts_as_before`: unchanged text.
- [ ] **Step 2: Run** `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~SessionKeeperTests|FullyQualifiedName~CommentWakeTests|FullyQualifiedName~ClaudeStartTests"`. Expected: FAIL.
- [ ] **Step 3: Implement.** Keeper: skip `IsBetweenRuns` in both `Plan` queries; in `Stops`, a stoppable task is `Finished(task) || task.IsBetweenRuns`, and between runs takes the Done branch (idle for `StopAfter`, no attached window). CommentWake: `FreshRun` when `task.IsBetweenRuns` and no session of the task is listed, checked first. First message, last block built first and kept whole: "This is a scheduled run of a recurring task (<Label>). Join it (deskbar task join <id>), do what its description says, comment once with the result only if there is something to say, and finish with deskbar task done. Use deskbar task ask only when the user must act." (Wording may be tightened; the tested phrases stay.)
- [ ] **Step 4: Run** the filter. Expected: PASS.
- [ ] **Step 5: Commit** `feat: sessions between runs are stopped, never resumed, and a comment starts a run`.

### Task 6: `task repeat` on the pipe

**Files:**
- Modify: `src/Deskbar.Core/Control/RemoteControl.Tasks.cs` (header list ~12-39; `Task` switch ~115-220; `Describe` ~366-426; `TaskSessionCommands` ~628)
- Modify: `src/Deskbar.Core/Control/RemoteControl.ClaudeHook.cs` (`SessionTask` switch ~383)
- Modify: `src/Deskbar.Core/Control/ControlUsage.cs` (after `wake`, ~199)
- Modify: `src/Deskbar.Core/Tasks/TaskOverview.cs` (the manager's line names the repeat)
- Test: `tests/Deskbar.Core.Tests/RemoteControlTaskTests.cs`, `tests/Deskbar.Core.Tests/RemoteControlClaudeHookTests.cs`

**Interfaces:**
- Consumes: `TaskRepeats.ParseRequest`, `TaskService.SetRepeat/ClearRepeat`, `ClaudeStart.RepoOf`, `CallerFolder` (`RemoteControl.ClaudeHook.cs:486`), `FolderRefusal` (`RemoteControl.Tasks.cs:604`).
- Produces: `task repeat <id> off`; `task repeat <id> <schedule> [--model <m>] [--folder <p>]`; session form `task repeat <schedule>|off [--model <m>] [--folder <p>] --session <sid>` on its own task (folder default: the repo of the session's recorded folder).
- Produces: `TaskSessionCommands.FolderExists { get; init; } : Func<string, bool>`, default `_ => true` (the App sets `Directory.Exists`).
- Produces in `Describe`: `repeat = { schedule, label, model, folder }` or null, `nextRun` or null.

Folder for the id form: `--folder` (through `Folder(f) ?? f`), else the only `commands.Folders(task)` candidate, else `FolderRefusal`; a folder `FolderExists` denies is refused. Model: `--model` or `TaskRepeat.DefaultModel`. Everything is parsed and looked up before the service is called.

- [ ] **Step 1: Write the failing tests** (through a control built with `TaskSessionCommands { FolderExists = ... }`, as `RunFrom` builds one):
  - `Repeat_sets_the_schedule_the_default_model_and_the_folder`: `task repeat <id> every 1h 09:00-19:00 --folder C:\repos\TaskSpaces` replies with `repeat.schedule == "every 1h 09:00-19:00"`, `repeat.model == "sonnet"`, `repeat.folder == @"C:\repos\TaskSpaces"`, `nextRun` not null.
  - `Repeat_takes_a_model_and_stores_a_worktree_as_its_repo`: `--model haiku --folder C:\repos\TaskSpaces\.claude\worktrees\x` stores `C:\repos\TaskSpaces`.
  - `Repeat_without_a_folder_uses_the_only_candidate_and_refuses_several`.
  - `Repeat_refuses_a_bad_schedule_a_bad_model_and_a_missing_folder_and_changes_nothing` (Theory; `AssertRefusedAndUnchanged`).
  - `Repeat_off_ends_it`: `repeat` null after.
  - `List_shows_repeat_and_next_run_and_null_without_them`.
  - `Manager_may_set_a_repeat_by_id` (`RunAsManager`); add `"repeat"` to the `Manager_may_use_no_other_session_form` InlineData.
  - `Usage_lists_task_repeat`: `ControlUsage.All` has a `task` command whose arguments start `repeat <id>`.
  - ClaudeHookTests `A_session_sets_a_repeat_on_its_own_task_in_its_repo` and `A_session_without_a_task_is_refused`.
- [ ] **Step 2: Run** `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~RemoteControlTaskTests|FullyQualifiedName~RemoteControlClaudeHookTests"`. Expected: FAIL.
- [ ] **Step 3: Implement.** `["repeat", var id, .. var rest]` dispatches through `WithSessions`; add `["repeat", ..]` to the session forms. ControlUsage line: `repeat <id> <schedule>|off [--model <model>] [--folder <path>]`, description naming the forms, the 15-minute minimum, Sonnet as the default, the skip rule and that `task list` shows `repeat` and `nextRun`. TaskOverview: append `repeats <Label>` to a recurring task's line.
- [ ] **Step 4: Run** the filter, then `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~TaskOverview"`. Expected: PASS.
- [ ] **Step 5: Commit** `feat: task repeat on the control pipe`.

### Task 7: The App runs them

**Files:**
- Create: `src/Deskbar.App/RecurringRunner.cs`
- Modify: `src/Deskbar.App/ClaudeTaskActions.cs` (`Start` ~447; `Offer` ~397 split)
- Modify: `src/Deskbar.App/CommentWaker.cs` (constructor ~37; `Deliver` ~94)
- Modify: `src/Deskbar.App/ClaudeSessions.cs` (`Stop` ~628)
- Modify: `src/Deskbar.App/App.xaml.cs` (TaskService ~1644; snooze sweep ~1651; CommentWaker ~1980; after ClaudeTaskActions ~2029; `FolderExists` where `TaskSessionCommands` is handed to RemoteControl)
- Test: `tests/Deskbar.Windows.Tests/RecurringRunnerTests.cs` (new), `CommentWakerTests.cs`, `ClaudeTaskActionsTests.cs`, `ClaudeSessionsTests.cs`

**Interfaces:**
- Consumes: `TaskService.DueRuns`, `RunStarted`, `Rescheduled`, `Wake`, `Note`, `Ask`; `RunDecision`; `RecurringRuns` questions; `WakePlan.FreshRun`; `KeeperStep.Stop.BetweenRuns`.
- Produces: `ClaudeTaskActions.Start(TaskItem task, string folder, Maybe<string> model = default, bool quiet = false, Action<string>? failed = null)`: a quiet start's later failure goes to `failed` when given, else to the thread as today.
- Produces: `ClaudeTaskActions.PickFolder(TaskItem task, Action<string> chosen) : ContextMenu?`, the folder half of `Offer` (one folder: `chosen` at once and null; else the pick list with "Choose a folder…"); `Offer` uses it.
- Produces: `sealed class RecurringRunner(TaskService tasks, Func<bool> enabled, Func<TaskItem, bool> running, Func<string, bool> folderExists, Func<TaskItem, string, Maybe<string>, Action<string>, Result> start, Action<string>? trace = null)` with `void RunDue()` and `Result RunNow(Guid id)` (= `tasks.Wake(id)` then `RunDue()`).
- Produces: `CommentWaker` constructor gains `Func<Guid, Result>? startRun = null` (last).

RunDue, nothing while `enabled()` is false: `Start` → `RunStarted`, then `start(task, repeat.Folder, repeat.Model, failed)` where `failed` notes Claude's words and asks `StartFailedQuestion`; `Skip` → `Rescheduled` and a trace line; `FolderGone` → `Rescheduled`, `Note(FolderGoneNote)`, `Ask(FolderGoneQuestion)`; `Schedule` → `Rescheduled`.

- [ ] **Step 1: Write the failing tests:**
  - RecurringRunnerTests `RunDue_starts_a_due_run_quietly_in_its_folder_with_its_model` (the fake `start` records `(task.Id, folder, model)`; NextRun moved on), `RunDue_skips_a_task_with_a_running_session_and_reschedules_it` (no start), `RunDue_asks_once_when_the_folder_is_gone` (one Deskbar note, Status WaitingOnYou with `FolderGoneQuestion`, a second `RunDue` an hour later adds nothing), `A_start_that_fails_later_asks_and_the_runs_wait` (invoke the captured `failed`: question `StartFailedQuestion`, the next due `RunDue` starts nothing), `RunDue_does_nothing_while_claude_tasks_are_off`, `RunNow_starts_a_task_between_runs_at_once`.
  - CommentWakerTests `A_comment_between_runs_starts_a_run_and_resumes_nothing`.
  - ClaudeTaskActionsTests `A_quiet_start_hands_its_failure_to_the_caller_and_says_nothing_else`; `PickFolder_with_one_folder_answers_at_once`.
  - ClaudeSessionsTests `A_stop_between_runs_is_traced_but_not_said_in_the_thread` (the Done and Closed stops still say "Stopped, since the task is finished.").
- [ ] **Step 2: Run** `pwsh scripts/test-hidden.ps1 -Arguments 'tests/Deskbar.Windows.Tests --filter "FullyQualifiedName~RecurringRunner|FullyQualifiedName~CommentWaker|FullyQualifiedName~ClaudeTaskActions|FullyQualifiedName~ClaudeSessions"'`. Expected: FAIL.
- [ ] **Step 3: Implement.** App: `TaskService(..., zone: TimeZoneInfo.Local)`; the runner built after `claudeActions` with `running = claudeActions.HasRunningSession`, `folderExists = Directory.Exists`, `start = (t, f, m, failed) => claudeActions.Start(t, f, m, quiet: true, failed)`; the snooze sweep calls `recurringRunner?.RunDue()` before `tasks.WakeDue()`, at start-up and on every `SnoozeSweep`; `CommentWaker` gets `startRun: id => recurringRunner?.RunNow(id) ?? Result.Failure("not ready")`, and `Deliver` handles `FreshRun` before its no-sessions return. `ClaudeSessions.Stop` skips the thread line when `stop.BetweenRuns` (an hourly job would add a line an hour).
- [ ] **Step 4: Run** the same command. Expected: PASS.
- [ ] **Step 5: Commit** `feat: Deskbar starts recurring runs on time, quietly`.

### Task 8: Repeat in the "⋯" menus

**Files:**
- Create: `src/Deskbar.App/RepeatMenu.cs`
- Modify: `src/Deskbar.App/TasksBoard.cs` (constructor ~237; actions ~320; `Snooze`/`Wake` ~1072; `RowOf` ~1438; `TaskRowView` ~1702; `TasksWindowActions` ~1942-2030)
- Modify: `src/Deskbar.App/TasksWindow.xaml.cs` (`OpenMenuFor`, after Snooze ~3004)
- Modify: `src/Deskbar.App/TaskDetailWindow.xaml.cs` (`OpenMenu`, after "Assign to" ~770)
- Modify: `src/Deskbar.App/App.xaml.cs` (`new TasksBoard` ~2651)
- Test: `tests/Deskbar.Windows.Tests/TasksBoardTests.cs`, `TaskDetailWindowTests.cs`

**Interfaces:**
- Consumes: `TaskRepeats.MenuChoices`, `TaskRepeats.ParseRequest`, `ClaudeTaskActions.PickFolder`, `RecurringRunner.RunNow`, `PromptDialog.Ask`.
- Produces: `TaskRowView.RepeatText` (`string?`, the schedule's `Text`), `RepeatLabel` (`string?`), `NextRun` (`DateTimeOffset?`).
- Produces: `TasksWindowActions.Repeat { get; init; } : Action<Guid, string>?` (a schedule text, Custom… words, or `"off"`).
- Produces: `TasksBoard.Repeat(Guid id, string words) : Result`: parse; `off` clears; otherwise the folder is the request's, else `pickFolder(task, chosen)` (the Start Claude pick), and `SetRepeat` runs when one is chosen; refusals on the problem line (`Report`).
- Produces: `TasksBoard` constructor parameters `Func<TaskItem, Action<string>, ContextMenu?>? pickFolder = null`, `Func<Guid, Result>? runNow = null`; `Wake` on a task between runs calls `runNow`.
- Produces: `static class RepeatMenu` with `List<object> Items(Guid id, string? current, Action<Guid, string> repeat, Func<string, Maybe<string>> askCustom)`: the five choices (the current one ticked), "Custom…" (a `PromptDialog` taking the CLI text, `--model` included, prefilled with `current`), and "Off" only while the task repeats. Both menus add it as a "Repeat" submenu ("Repeats" while it does), on open lines only.

- [ ] **Step 1: Write the failing tests:**
  - `The_line_menu_has_Repeat_with_five_choices_custom_and_no_off_on_a_plain_task`; `A_recurring_line_ticks_its_schedule_and_offers_Off`.
  - `Picking_every_hour_with_one_known_folder_sets_the_repeat` (Sonnet, that folder, NextRun set).
  - `Picking_Off_ends_the_repeat`.
  - `Custom_with_a_bad_schedule_says_why_on_the_problem_line`.
  - `Wake_now_on_a_recurring_task_runs_it` (the fake `runNow` called with the id).
  - TaskDetailWindowTests `The_window_menu_has_Repeat_too`.
- [ ] **Step 2: Run** `pwsh scripts/test-hidden.ps1 -Arguments 'tests/Deskbar.Windows.Tests --filter "FullyQualifiedName~TasksBoardTests|FullyQualifiedName~TaskDetailWindowTests"'`. Expected: FAIL.
- [ ] **Step 3: Implement** as the interfaces say; App passes `claudeActions.PickFolder` and `recurringRunner.RunNow`.
- [ ] **Step 4: Run** the same command. Expected: PASS.
- [ ] **Step 5: Commit** `feat: Repeat in the tasks panel's and the task window's menus`.

### Task 9: The repeat mark (mockup first)

**Files:**
- Create (scratch, not committed): a mockup page of a panel line and a bar line, today vs with the mark
- Modify: `src/Deskbar.App/TasksWindow.xaml.cs` (beside `SnoozeButton` ~1329-1353)
- Modify: `src/Deskbar.App/FloatingBar.TaskList.cs` (`TaskLineButton` ~1463, the `TaskLineFace` Right slot)
- Test: `tests/Deskbar.Windows.Tests/TasksBoardTests.cs`, `FloatingBarTaskListTests.cs`

**Interfaces:**
- Consumes: `TaskRowView.RepeatLabel`, `NextRun`; `TaskItem.Repeat`, `NextRun` on the bar.

- [ ] **Step 1: Mockup and ask.** Draw both lines dark and light with three candidates: Segoe Fluent RepeatAll (U+E8EE), Sync (U+E895), and the text "↻", in `DimInk`, after the snooze moon on the panel and in the bar line's Right slot. Recommend one, publish it, and ask Petre with `deskbar task ask` on task 0b78269e. Do not write Step 3 until he picks.
- [ ] **Step 2: Write the failing tests:** `A_recurring_line_shows_the_repeat_mark_with_schedule_next_run_and_model` (tooltip like "Repeats every hour, 09:00-19:00 · next run Thu 09:00 · sonnet", local time via `TaskSnooze.UntilText` style), `A_plain_line_has_no_repeat_mark`, and on the bar `A_recurring_bar_line_shows_the_mark_in_its_right_slot`.
- [ ] **Step 3: Run** `pwsh scripts/test-hidden.ps1 -Arguments 'tests/Deskbar.Windows.Tests --filter "FullyQualifiedName~TasksBoardTests|FullyQualifiedName~FloatingBarTaskListTests"'`. Expected: FAIL.
- [ ] **Step 4: Implement** Petre's pick (no mark in compact Matrix lines, like the moon). Run Step 3's command. Expected: PASS.
- [ ] **Step 5: Commit** `feat: a recurring task's line shows a repeat mark`.

### Task 10: Docs, manual steps, and the finish

**Files:**
- Modify: `README.md` (after the snooze paragraph, ~532-537)
- Modify: `docs/features/tasks.md` (a "Recurring tasks" section after "Duplicates and related tasks"; `task repeat` under "From a script")
- Modify: `docs/features/claude-sessions.md` (how a run starts, ends and is stopped; the model)
- Modify: `docs/superpowers/notes/manual-test-script.md` (a "## Recurring tasks" section)

- [ ] **Step 1: Write the docs** for users: what a repeat is, the menu choices and Custom… forms, Sonnet by default, runs are quiet and skip while the last one runs, no catch-up, "Wake now" runs one now, a comment starts a run, close stops it, a gone folder asks. Name the screencasts to re-film (the tasks panel menu) in the PR.
- [ ] **Step 2: Manual steps RT1-RT10:** set "Every hour" on a note whose description says to comment the time; the line leaves the panel to the Snoozed chip with the mark; Wake now starts a run with no window and no focus change; the run comments and calls done; the line goes back to Snoozed with the next run an hour on; a minute later the session leaves `claude agents`; open the run's window and wake again: skipped while it is open; comment between runs: a fresh run reads it; remove the folder: one question, no hourly lines; `deskbar.cmd task repeat <id> off`; set `daily HH:MM` two minutes ahead and see it run; close and reopen.
- [ ] **Step 3: Verify the whole branch.** `dotnet build Deskbar.sln -c Release -warnaserror` (0 warnings), `dotnet test Deskbar.sln --filter "Category!=Integration&FullyQualifiedName!~Deskbar.Windows.Tests"`, and `pwsh scripts/test-hidden.ps1`. Expected: all green.
- [ ] **Step 4: Commit** `docs: recurring tasks for users and the manual test steps`, then open the PR with a `## Release notes` section, and watch its CI and its base as the repo's CLAUDE.md says.
