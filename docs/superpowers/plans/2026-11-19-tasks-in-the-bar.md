# Tasks in the bar Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Park a workspace for 1, 5, 10 or 30 minutes from a stopwatch in the bar's strip, get it handed back when the
time is up, and keep one list of what needs Petre next.

**Architecture:** The model, the rules and persistence live in `Deskbar.Core/Tasks` as an immutable `TaskBoard` owned
by a `TaskService` that saves to `tasks.json` and is ticked by the App's existing 1 s timer. `WorkspaceManager` stamps
due and running marks onto the overview the way it stamps Claude asks, so the bar's blink is reused as is. The bar gets
a stopwatch in its strip, pies in the name gutter, a due card for when it is out of sight, and (second pull request) a
task window modelled on Quick find.

**Tech Stack:** C# / .NET 10, WPF, CSharpFunctionalExtensions, System.Reactive, xUnit 2.9 with plain `Assert` (no
assertion or mocking library; fakes are hand-written, see `tests/Deskbar.Core.Tests/Fakes.cs`).

**Spec:** `docs/superpowers/specs/2026-11-19-tasks-in-the-bar-design.md` (branch `feat/tasks-in-the-bar`). Read it
first; this plan argues from it.

## Global Constraints

- Presets are exactly 1, 5, 10 and 30 minutes, written `1m`, `5m`, `10m`, `30m` on the command line and `1 min`,
  `5 min`, `10 min`, `30 min` in the UI. Nothing else is accepted.
- Coming back = a desktop switch onto the task's workspace, then staying there 2 seconds (`TaskService.Dwell`).
  Already being there is never coming back.
- Nothing restarts a timer on its own.
- Any number of tasks per workspace. Parking reuses a live task on that workspace only when the title matches
  (trimmed, ignoring case); any other title makes a new task. Coming back ends every timer on the workspace.
- Every stored time is a UTC `DateTimeOffset`; every task carries `UpdatedAt`; ids are `Guid.NewGuid()` made here.
- The word is "workspace" in code and UI. No renaming.
- No new global hotkey. No sound. No Windows toast (#123).
- Orange is `FloatingBar.AskingOrange` (`#FFA726`) and blinks only through the shared `AskingPulse` / `AskingCaption`
  brushes; the running pie is blue `#4F8CFF`.
- `TasksGate.Free = true`: everything is available in every copy for now (same pattern as `TilingGate.Free`).
- Code style per CLAUDE.md: `private` implied, expression bodies, `var`, immutable records, readonly collections,
  `Result`/`Maybe` from CSharpFunctionalExtensions for expected failures, long comments that carry the reasoning.
- Every pull request that changes `src/` has a `## Release notes` section for users. No version edits.
- Before each pull request: `dotnet build Deskbar.sln -c Release -warnaserror` and
  `dotnet test Deskbar.sln --filter "Category!=Integration"`, both clean.

## Review Focus

1. The PC sleeps or Deskbar is closed while timers run: on the next tick every expired timer is Due, and each one is
   announced (`CameDue`) exactly once, not once per tick. Test in Task 3.
2. Hopping through a workspace in under 2 seconds (Alt+Tab, the switcher) leaves its timer running; hopping A to B to A
   restarts the dwell on the second arrival. Test in Task 3.
3. A workspace is deleted while its task runs: the task becomes free, keeps its title and its timer, and the bar does
   not draw a pie for a row that no longer exists. Test in Task 3.
4. `tasks.json` is missing, empty or corrupt: Deskbar starts with an empty board (or the `.previous` copy) and keeps
   running. Test in Task 2.
5. The command line gets an unknown id, an ambiguous id prefix, an empty title or a length outside the presets: exit 1
   with a one-line reason, nothing changed. Test in Task 5.

---

# Pull request 1: tasks, timers and the bar

### Task 1: The task model and its rules

**Files:**
- Create: `src/Deskbar.Core/Tasks/TaskItem.cs`, `src/Deskbar.Core/Tasks/TaskPresets.cs`,
  `src/Deskbar.Core/Tasks/TaskBoard.cs`, `src/Deskbar.Core/Tasks/TasksGate.cs`
- Test: `tests/Deskbar.Core.Tests/Tasks/TaskBoardTests.cs`, `tests/Deskbar.Core.Tests/Tasks/TaskPresetsTests.cs`

**Interfaces:**
- Produces (namespace `Deskbar.Core.Tasks`):
  - `enum TaskState { Running, Due, Open, Done }`
  - `enum TimerEnd { CameBack, Done, Cleared, Replaced }`
  - `sealed record TaskTimer(DateTimeOffset Started, TimeSpan Length) { DateTimeOffset Ends }`
  - `sealed record TimerRecord(DateTimeOffset Started, TimeSpan Length, DateTimeOffset Ended, TimerEnd How)`
  - `sealed record TaskItem(Guid Id, string Title, Maybe<Guid> WorkspaceId, DateTimeOffset Created, DateTimeOffset UpdatedAt, Maybe<TaskTimer> Timer, Maybe<DateTimeOffset> DoneAt, IReadOnlyList<TimerRecord> History)`
    with `TaskState StateAt(DateTimeOffset now)` and `bool IsLive` (not done)
  - `static class TaskPresets { IReadOnlyList<TimeSpan> Lengths; Result<TimeSpan> Parse(string text); string Label(TimeSpan length) }`
  - `sealed record TaskBoard(IReadOnlyList<TaskItem> Tasks)` with `static TaskBoard Empty` and the methods below
  - `static class TasksGate { const bool Free = true; static bool Allowed(bool isPro) }`

- [ ] **Step 1: Write the failing tests**

```csharp
// TaskPresetsTests
[Theory, InlineData("1m", 1), InlineData("5m", 5), InlineData("10m", 10), InlineData("30m", 30)]
public void Parses_the_four_presets(string text, int minutes) =>
    Assert.Equal(TimeSpan.FromMinutes(minutes), TaskPresets.Parse(text).Value);

[Theory, InlineData("2m"), InlineData("10"), InlineData(""), InlineData("1h")]
public void Refuses_anything_else(string text) => Assert.True(TaskPresets.Parse(text).IsFailure);

[Fact] public void Labels_read_as_minutes() => Assert.Equal("10 min", TaskPresets.Label(TimeSpan.FromMinutes(10)));

// TaskBoardTests (t0 = 2026-11-19T10:00:00Z, ws = a fixed Guid)
[Fact] public void A_timer_runs_then_comes_due()
{
    var (board, task) = TaskBoard.Empty.Park("Wait for CI", ws, TimeSpan.FromMinutes(5), t0, id1).Value;
    Assert.Equal(TaskState.Running, task.StateAt(t0.AddMinutes(4)));
    Assert.Equal(TaskState.Due, task.StateAt(t0.AddMinutes(5)));
}

[Fact] public void Parking_the_same_title_reuses_the_live_task()
{
    var (b1, _) = TaskBoard.Empty.Park("Wait for CI", ws, TimeSpan.FromMinutes(5), t0, id1).Value;
    var (b2, again) = b1.Park(" wait for ci ", ws, TimeSpan.FromMinutes(10), t0.AddMinutes(1), id2).Value;
    Assert.Single(b2.Tasks);
    Assert.Equal(id1, again.Id);
    Assert.Equal(t0.AddMinutes(11), again.Timer.Value.Ends);
    Assert.Equal(TimerEnd.Replaced, again.History.Single().How);
}

[Fact] public void A_new_title_on_the_same_workspace_is_a_new_task()
{
    var (b1, _) = TaskBoard.Empty.Park("Wait for CI", ws, TimeSpan.FromMinutes(5), t0, id1).Value;
    Assert.Equal(2, b1.Park("Tidy the release notes", ws, Maybe.None, t0, id2).Value.Board.LiveOn(ws).Count);
}

[Fact] public void The_latest_live_title_is_the_parking_default()
{
    var b = TaskBoard.Empty.Park("a", ws, Maybe.None, t0, id1).Value.Board
        .Park("b", ws, Maybe.None, t0.AddMinutes(1), id2).Value.Board;
    Assert.Equal("b", b.LatestLiveOn(ws).Value.Title);
}

[Fact] public void An_empty_title_is_refused() =>
    Assert.True(TaskBoard.Empty.Park("   ", ws, Maybe.None, t0, id1).IsFailure);

[Fact] public void Coming_back_ends_a_running_or_due_timer_and_leaves_the_task_open()
{
    var (b1, _) = TaskBoard.Empty.Park("x", ws, TimeSpan.FromMinutes(1), t0, id1).Value;
    var task = b1.CameBack(ws, t0.AddSeconds(30)).Tasks.Single();
    Assert.Equal(TaskState.Open, task.StateAt(t0.AddMinutes(5)));
    Assert.Equal(TimerEnd.CameBack, task.History.Single().How);
}

[Fact] public void Coming_back_ends_every_timer_on_the_workspace()
{
    var b = TaskBoard.Empty.Park("a", ws, TimeSpan.FromMinutes(5), t0, id1).Value.Board
        .Park("b", ws, TimeSpan.FromMinutes(30), t0, id2).Value.Board.CameBack(ws, t0.AddMinutes(1));
    Assert.All(b.Tasks, t => Assert.Equal(TaskState.Open, t.StateAt(t0.AddHours(1))));
}

[Fact] public void A_task_moves_between_workspaces() =>
    Assert.Equal(other, TaskBoard.Empty.Park("a", ws, Maybe.None, t0, id1).Value.Board
        .MoveTo(id1, other, t0).Value.Tasks.Single().WorkspaceId.Value);

[Fact] public void Done_ends_the_timer_and_leaves_the_live_list()
{
    var b = TaskBoard.Empty.Park("a", ws, TimeSpan.FromMinutes(5), t0, id1).Value.Board.Done(id1, t0.AddMinutes(1)).Value;
    Assert.Equal(TaskState.Done, b.Tasks.Single().StateAt(t0.AddMinutes(9)));
    Assert.Equal(TimerEnd.Done, b.Tasks.Single().History.Single().How);
    Assert.Empty(b.LiveOn(ws));
}

[Fact] public void Every_change_moves_UpdatedAt() =>
    Assert.Equal(t0.AddMinutes(2), TaskBoard.Empty.Park("a", ws, Maybe.None, t0, id1).Value.Board
        .Rename(id1, "b", t0.AddMinutes(2)).Value.Tasks.Single().UpdatedAt);

[Fact] public void Unknown_ids_fail() => Assert.True(TaskBoard.Empty.Done(id1, t0).IsFailure);

[Fact] public void Unknown_workspaces_free_their_tasks_and_keep_the_timer()
{
    var b = TaskBoard.Empty.Park("a", ws, TimeSpan.FromMinutes(5), t0, id1).Value.Board.Prune(new HashSet<Guid>(), t0);
    Assert.True(b.Tasks.Single().WorkspaceId.HasNoValue);
    Assert.True(b.Tasks.Single().Timer.HasValue);
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~Deskbar.Core.Tests.Tasks"`
Expected: build fails, `TaskBoard` / `TaskPresets` not defined.

- [ ] **Step 3: Implement the model in the files above**

`TaskBoard` methods, each pure, each stamping `UpdatedAt = now` on what it changes:
- `Result<(TaskBoard Board, TaskItem Task)> Park(string title, Maybe<Guid> workspace, Maybe<TimeSpan> length, DateTimeOffset now, Guid newId)`:
  trims the title, fails on empty; when a live task on that workspace has the same title (trimmed, ignoring case), and
  a length is given, replaces its timer (the old one recorded `Replaced`) and returns it; otherwise adds a new task.
  Free tasks (no workspace) never match: each park of a free task is new.
- `IReadOnlyList<TaskItem> LiveOn(Guid workspace)`
- `Maybe<TaskItem> LatestLiveOn(Guid workspace)` (highest `UpdatedAt`)
- `Result<TaskBoard> SetTimer(Guid id, TimeSpan length, DateTimeOffset now)` (an existing timer is recorded `Replaced`)
- `Result<TaskBoard> ClearTimer(Guid id, DateTimeOffset now)` (recorded `Cleared`)
- `Result<TaskBoard> Rename(Guid id, string title, DateTimeOffset now)`
- `Result<TaskBoard> MoveTo(Guid id, Maybe<Guid> workspace, DateTimeOffset now)`
- `Result<TaskBoard> Done(Guid id, DateTimeOffset now)` (a timer is recorded `Done`)
- `Result<TaskBoard> Delete(Guid id)`
- `TaskBoard CameBack(Guid workspace, DateTimeOffset now)` (every live task on it with a timer: recorded `CameBack`)
- `TaskBoard Prune(IReadOnlySet<Guid> knownWorkspaces, DateTimeOffset now)` (unknown workspace ids become `Maybe.None`)
- `Result<TaskItem> Find(string idOrPrefix)`: a full Guid, or a unique prefix of at least 4 hex characters; fails
  "No task with id …" or "… matches several tasks".

`TaskItem.StateAt`: Done if `DoneAt` has a value; else Due if a timer's `Ends <= now`; else Running if a timer; else Open.

- [ ] **Step 4: Run the tests to verify they pass** (same command; expected PASS)

- [ ] **Step 5: Commit**

```bash
git add src/Deskbar.Core/Tasks tests/Deskbar.Core.Tests/Tasks
git commit -m "feat: the task model, with timers that come due and one live task per workspace"
```

### Task 2: Saving tasks

**Files:**
- Create: `src/Deskbar.Core/Tasks/ITaskStore.cs`, `src/Deskbar.Core/Tasks/JsonTaskStore.cs`
- Test: `tests/Deskbar.Core.Tests/Tasks/JsonTaskStoreTests.cs`

**Interfaces:**
- Consumes: `TaskBoard`, `TaskItem` (Task 1); `DurableFile.Write` / `DurableFile.Read<T>` (`src/Deskbar.Core/Persistence/DurableFile.cs:27,56`).
- Produces: `interface ITaskStore { Result<TaskBoard> Load(); Result Save(TaskBoard board); }` and
  `sealed class JsonTaskStore(string baseDirectory) : ITaskStore` writing `tasks.json` in `baseDirectory`.

- [ ] **Step 1: Write the failing tests** (each in a fresh temp directory)
  - `Round_trips_a_board`: a board with a free task, a workspace task with a running timer, a done task with history
    saves and loads equal field by field (Maybe values included).
  - `A_missing_file_is_an_empty_board`: `Load()` on an empty directory → `IsSuccess`, `Tasks` empty.
  - `A_corrupt_file_falls_back_to_previous`: save board A, save board B, overwrite `tasks.json` with `"{not json"` →
    `Load()` returns board A (the `.previous` copy).
  - `Times_are_written_in_UTC`: the saved text contains `"+00:00"` or `Z` for every time.
- [ ] **Step 2: Run them to verify they fail** (`--filter "FullyQualifiedName~JsonTaskStoreTests"`, expected: not defined)
- [ ] **Step 3: Implement `JsonTaskStore`** with private DTO records using nullable fields in place of `Maybe`
  (System.Text.Json, `JsonSerializerDefaults.Web`, indented, so the file stays hand-readable), following
  `JsonTimeStore`'s shape. Times are converted with `ToUniversalTime()` before writing.
- [ ] **Step 4: Run the tests to verify they pass**
- [ ] **Step 5: Commit** `feat: tasks are saved to tasks.json beside state.json`

### Task 3: The task service: ticking, coming back, coming due

**Files:**
- Create: `src/Deskbar.Core/Tasks/TaskService.cs`, `src/Deskbar.Core/Tasks/TaskEntry.cs`
- Test: `tests/Deskbar.Core.Tests/Tasks/TaskServiceTests.cs` (with a `FakeTaskStore` in the test file)

**Interfaces:**
- Consumes: Tasks 1 and 2.
- Produces:
  - `sealed class TaskService(ITaskStore store, Func<DateTimeOffset> now, Func<Guid>? newId = null, Action<string>? trace = null)`
    - `static readonly TimeSpan Dwell = TimeSpan.FromSeconds(2)`
    - `TaskBoard Board { get; }`, `IObservable<Unit> Changed`, `IObservable<TaskItem> CameDue`
    - `Result Load()`
    - `Result<TaskItem> Park(string title, Maybe<Guid> workspace, Maybe<TimeSpan> length)`
    - `Result SetTimer(Guid id, TimeSpan length)`, `Result ClearTimer(Guid id)`, `Result Rename(Guid id, string title)`,
      `Result MoveTo(Guid id, Maybe<Guid> workspace)`, `Result Done(Guid id)`, `Result Delete(Guid id)`
    - `void Tick(Maybe<Guid> currentWorkspace)`
    - `void Prune(IReadOnlySet<Guid> knownWorkspaces)`
    - `IReadOnlyList<TaskEntry> Entries(IReadOnlyCollection<Guid> askingWorkspaces, DateOnly today)`
    - `IReadOnlySet<Guid> DueWorkspaces()`, `IReadOnlyDictionary<Guid, TaskTimer> RunningWorkspaces()` (the soonest per workspace)
  - `enum TaskEntryKind { Ask, Task }`
  - `sealed record TaskEntry(TaskEntryKind Kind, Maybe<TaskItem> Task, Maybe<Guid> WorkspaceId, TaskState State)`

Every mutation saves through the store (a failed save is traced, never thrown) and pulses `Changed`.

- [ ] **Step 1: Write the failing tests** (clock is a captured `DateTimeOffset` field moved by the test)
  - `The_first_tick_is_not_an_arrival`: park on `ws` while current is `ws`, tick at +0 and +10 s → still Running.
  - `Arriving_and_staying_two_seconds_ends_the_timer`: current `other`, tick; current `ws`, tick at +0; tick at +1.9 s →
    Running; tick at +2 s → Open, history `CameBack`.
  - `Passing_through_does_not_end_the_timer`: `other` → `ws` (+0) → `other` (+1 s) → ticks to +10 s → Running.
  - `A_second_arrival_restarts_the_dwell`: `other` → `ws` (+0) → `other` (+1) → `ws` (+1.5) → tick +3 → Running; +3.5 → Open.
  - `Due_while_present_stays_due_until_an_arrival`: park 1 min on `ws` while on `ws`; tick at +2 min → Due; tick at +5 min → Due.
  - `Each_due_task_is_announced_once` (Review Focus 1): two tasks with 1 and 5 minute timers, clock jumps +2 h, tick
    three times → `CameDue` fired exactly twice in total.
  - `A_due_task_on_load_is_announced_on_the_first_tick`: store holds a task whose timer ended yesterday → `Load()`, `Tick` → one `CameDue`.
  - `Prune_frees_tasks_of_deleted_workspaces` (Review Focus 3): `RunningWorkspaces()` no longer contains the deleted id.
  - `Entries_sort_by_what_needs_you_next`: an ask for `wsA`, a due task (due 3 min ago), a due task (due 1 min ago), two
    running (4 and 22 min left), two open, one done today and one done yesterday →
    order: due 3 min ago, ask, due 1 min ago, running 4, running 22, open newest first, done today; yesterday's done absent.
    (Due sorts by when it came due, oldest first; an ask counts as due "now" and sits after timers that came due earlier.)
  - `Mutations_save_and_pulse`: `Rename` → store saved once, `Changed` fired once.
- [ ] **Step 2: Run them to verify they fail**
- [ ] **Step 3: Implement `TaskService`**

Tick keeps three private fields: the workspace seen on the previous tick (`Maybe<Guid>`, unset before the first tick),
the pending arrival (`Maybe<(Guid Workspace, DateTimeOffset At)>`), and the ids already announced due (`HashSet<Guid>`).

```
on Tick(current):
  if first tick: remember current; (no arrival)
  else if current != previous: pending = current.HasValue ? (current, now) : None; previous = current
  if pending is (ws, at) and current == ws and now - at >= Dwell: Board = Board.CameBack(ws, now); pending = None; save; pulse
  for each task with StateAt(now) == Due and id not announced: announced.Add(id); CameDue.OnNext(task)
  announced.RemoveWhere(id => task gone or no longer Due)
```

- [ ] **Step 4: Run the tests to verify they pass**
- [ ] **Step 5: Commit** `feat: coming back to a workspace for two seconds ends its timer`

### Task 4: Due and running marks on the overview, asks the list can read and dismiss

**Files:**
- Modify: `src/Deskbar.Core/Overview/Overview.cs:83` (`WorkspaceGroup` gains `Maybe<TaskTimer> Timer = default`)
- Modify: `src/Deskbar.Core/WorkspaceManager.cs` near `:667-700` (`askingWorkspaces`, `RequestAttention`, `Asking`)
- Test: `tests/Deskbar.Core.Tests/WorkspaceManagerTaskMarksTests.cs`

**Interfaces:**
- Consumes: `TaskTimer` (Task 1).
- Produces on `WorkspaceManager`:
  - `IReadOnlyCollection<Guid> AskingWorkspaces`
  - `bool ClearAttention(Guid workspaceId)` (true when an ask was removed; pulses `StateChanged`)
  - `void SetTaskMarks(IReadOnlySet<Guid> due, IReadOnlyDictionary<Guid, TaskTimer> running)` (pulses only when the marks changed)

- [ ] **Step 1: Write the failing tests** (manager built as in `RemoteControlAttentionTests.cs:17-23`, two workspaces)
  - `A_due_workspace_wants_attention`: `SetTaskMarks({ws}, {})` → `WindowsByWorkspace().Value` group for `ws` has `WantsAttention`.
  - `A_running_workspace_carries_its_timer`: the group's `Timer` equals the given `TaskTimer`.
  - `Unchanged_marks_do_not_pulse`: the same marks twice → `StateChanged` fired once.
  - `ClearAttention_removes_an_ask`: `RequestAttention(other)`, `ClearAttention(other)` → true, `AskingWorkspaces` empty,
    group not `WantsAttention`; a second call → false.
- [ ] **Step 2: Run them to verify they fail**
- [ ] **Step 3: Implement.** Keep the marks in two fields beside `askingWorkspaces`; `Asking(...)` stamps
  `WantsAttention = asking || due` and `Timer`, and its early return checks all four collections. Comment why the marks
  live here: they are live state stamped after the build, exactly like asks.
- [ ] **Step 4: Run the tests to verify they pass**, and the whole Core suite (the new defaulted record field must not break callers).
- [ ] **Step 5: Commit** `feat: a due task makes its workspace's row blink like an ask`

### Task 5: The `task` commands

**Files:**
- Create: `src/Deskbar.Core/Control/RemoteControl.Tasks.cs`
- Modify: `src/Deskbar.Core/Control/RemoteControl.cs:52` (constructor gains `TaskService? tasks = null`), `:68-136` (one arm `("task", var rest) => Task(rest)`)
- Modify: `src/Deskbar.Core/Control/ControlArguments.cs:132` (add `"task"` to `AboutTheCaller`)
- Modify: `src/Deskbar.Core/Control/ControlUsage.cs` (a "Tasks" section)
- Modify: `tests/Deskbar.Core.Tests/RemoteControlMenuTests.cs:583` (`Help_lists_every_command` gains `"task"`)
- Test: `tests/Deskbar.Core.Tests/RemoteControlTaskTests.cs`

**Interfaces:**
- Consumes: `TaskService` (Task 3), `TaskBoard.Find` (Task 1), `WorkspaceManager.AskingWorkspaces` (Task 4),
  `CallerOf(string? from)` (`RemoteControl.Open.cs:55`), `WorkspaceNamed(string)` (`RemoteControl.cs:146`),
  `Success` / `Reply` / `ControlReply.Fail`.
- Produces the commands (usage text exactly):

```
task list
task add <title> [--workspace <name>|none] [--in 1m|5m|10m|30m]
task timer <id> (--in 1m|5m|10m|30m | --clear)
task done <id>
task rename <id> <title>
task delete <id>
```

`Task(string[] rest)` first strips a trailing `--from <folder>` (appended for every `task` call because the verb is in
`AboutTheCaller`), then parses. `add` without `--workspace` uses `CallerOf(from).Workspace`; with no caller workspace it
fails "Not run from a workspace's folder; pass --workspace <name> or --workspace none". `list` returns
`TaskService.Entries(manager.AskingWorkspaces, today)` as JSON objects:
`{ kind: "task"|"ask", id, title, workspace, state: "running"|"due"|"open"|"done", endsAt, doneAt }` (`id` and
`title` null for asks, workspace by name). With `tasks` null every subcommand fails "Tasks are not available".

- [ ] **Step 1: Write the failing tests**
  - `Add_parks_the_callers_workspace`: `Execute(["task","add","Wait for CI","--in","10m","--from", folderOfDeskbar])` → exit 0,
    the service holds one Running task on `Deskbar`.
  - `Add_with_workspace_none_is_free`.
  - `List_includes_asks`: after `RequestAttention(ws)` → the JSON has an object with `"kind": "ask"`.
  - `Timer_done_rename_delete_by_prefix`: each by the first 6 characters of the id.
  - Review Focus 5: `Unknown_id_fails`, `Ambiguous_prefix_fails`, `Empty_title_fails`, `Length_outside_presets_fails`
    (`--in 7m`) → exit `ControlReply.Failed`, one line of output, board unchanged.
  - `Task_is_in_the_help` (pattern of `RemoteControlAttentionTests.Attention_is_in_the_help:114`).
- [ ] **Step 2: Run them to verify they fail**
- [ ] **Step 3: Implement** in `RemoteControl.Tasks.cs`, header comment pointing at the spec's "Remote control" section.
- [ ] **Step 4: Run the tests to verify they pass**, then the whole Core suite.
- [ ] **Step 5: Commit** `feat: deskbar.cmd task, to add, time and finish tasks from a script or a session`

### Task 6: Wiring the service into the app

**Files:**
- Modify: `src/Deskbar.App/App.xaml.cs` near `:1159-1197` (construction), `:1293` (RemoteControl), `:1527-1544` (1 s timer)

**Interfaces:**
- Consumes: Tasks 3 to 5.
- Produces: a `TaskService tasks` field on `App`, loaded from `AppPaths.NewStateDir`, for Tasks 7, 8 and 10.

- [ ] **Step 1: Construct** `tasks = new TaskService(new JsonTaskStore(stateDir), () => DateTimeOffset.UtcNow, trace: ClickTrace.On ? ClickTrace.Write : null)`
  and call `tasks.Load()` (a failure is traced; the board stays empty).
- [ ] **Step 2: Tick it** in the existing 1 s `topmost` timer, after `manager.ResyncActiveWindow()`:
  `tasks.Tick(manager.CurrentWorkspaceId)`.
- [ ] **Step 3: Keep the marks and the workspaces in step:** `tasks.Changed` and every tick →
  `manager.SetTaskMarks(tasks.DueWorkspaces(), tasks.RunningWorkspaces())`; `manager.StateChanged` →
  `tasks.Prune(manager.State.Workspaces.Select(w => w.Id).ToHashSet())`.
- [ ] **Step 4: Pass `tasks`** to `new Core.Control.RemoteControl(..., tasks: tasks)`.
- [ ] **Step 5: Verify live.** Build Debug, restart the app (stop the running copy first, see CLAUDE.md), then
  `deskbar.cmd task add "probe" --workspace none --in 1m` → exit 0; `deskbar.cmd task list` shows it Running, a minute
  later Due; `tasks.json` exists in the state folder. Delete the probe with `task delete`.
- [ ] **Step 6: Commit** `feat: tasks load at start, tick every second and survive restarts`

### Task 7: The stopwatch in the strip and the pies in the rows

**Files:**
- Create: `src/Deskbar.App/FloatingBar.Tasks.cs`
- Modify: `src/Deskbar.Core/Persistence/StripItem.cs:10` (append `Tasks`)
- Modify: `src/Deskbar.App/FloatingBar.Shell.cs:149-224` (create `stripTasks`, add it to the right-side list just inward of
  `stripLanguage`), `:402-438` (`FitClock`)
- Modify: `src/Deskbar.App/FloatingBar.xaml.cs:2434` (pass `g.Timer`), `:3689` (`GroupRow` gains `Maybe<TaskTimer> timer = default`),
  `:3883` (`RowLabel` gains the same and adds the pie beside `WorkspaceTimeMark`, `:5555-5575`)
- Modify: `src/Deskbar.App/ManageWindow.xaml.cs:593` (`StripItemName` for `Tasks`: "Tasks")
- Modify: `src/Deskbar.App/App.xaml.cs` (hand the bar `tasks` and the park action)
- Test: `tests/Deskbar.Windows.Tests/FloatingBarTasksTests.cs`

**Interfaces:**
- Consumes: `TaskService` (Tasks 3, 6), `WorkspaceGroup.Timer` (Task 4), `AskingPulse` (`FloatingBar.Asking.cs:41`),
  `clockTimer` (`FloatingBar.Shell.cs:133`).
- Produces on `FloatingBar`: `internal TaskService? Tasks { get; set; }`, `internal Action? OpenTasks { get; set; }`,
  `internal Button? StripTasks`, `internal string StopwatchText` (for tests), `void PaintStopwatch()` on every `clockTimer` tick.

Until the task window exists (Task 10), `OpenTasks` opens a context menu on the stopwatch, built with the bar's
`MenuGlyph` items: "Park <current workspace>" with 1 / 5 / 10 / 30 min (titled with `LatestLiveOn(workspace)`'s title,
else the remembered foreground window's title, `languageTarget`'s window), then each live task with its time left and "Done". This keeps the first release useful.

- [ ] **Step 1: Write the failing tests** (real bar on an STA thread, as the existing FloatingBar tests do; a `TaskService` over a fake store and a movable clock)
  - `The_stopwatch_rests_as_a_glyph`: no tasks → `StopwatchText == ""`.
  - `A_running_task_shows_its_time_left`: a 5 minute task, clock +28 s, `PaintStopwatch()` → `"4:32"`.
  - `A_due_task_reads_due`: clock +6 min → `"due"` and the button's foreground is the `AskingPulse` brush.
  - `The_countdown_gives_way_after_the_date`: narrow the bar until `FitClock` hides the date, then narrower → the countdown
    hides before the clock does; the stopwatch glyph stays visible while the clock is visible.
  - `Hidden_strip_item_hides_the_stopwatch`: `HiddenStripItems = [StripItem.Tasks]` → `StripTasks.Visibility == Collapsed`.
  - `A_running_workspace_row_draws_a_pie`: an overview whose group has `Timer` → the row's caption contains the pie element (find it by `Tag == "task-pie"`).
- [ ] **Step 2: Run them to verify they fail**
- [ ] **Step 3: Implement.** Stopwatch glyph from Segoe Fluent Icons (builder's pick), `StripButtonWidth` wide at rest; the
  running state adds a 9 DIP workspace-colour swatch and the time `m:ss`. The pie is an ellipse plus an arc path, 11 DIP,
  blue `#4F8CFF` on 13% white, redrawn on `clockTimer` ticks only (keep the live pies in a list rebuilt with each rebuild;
  never trigger a rebuild from the tick). The stopwatch shows only when `TasksGate.Allowed(Entitlement.Current.IsPro)`,
  always true while `TasksGate.Free` is, so a later gate is one constant (comment it like `TilingGate`).
- [ ] **Step 4: Run the tests to verify they pass**, then the whole Windows suite.
- [ ] **Step 5: Verify live**: restart the app, park the current workspace for 1 min from the stopwatch menu, switch away,
  watch the pie empty and the row blink at the end, come back and stay 2 s → blink stops, task Open.
- [ ] **Step 6: Commit** `feat: a stopwatch in the bar parks a workspace, and its row counts down`

### Task 8: The due card when the bar is out of sight

**Files:**
- Create: `src/Deskbar.App/DueCard.xaml`, `src/Deskbar.App/DueCard.xaml.cs`
- Modify: `src/Deskbar.App/FloatingBar.xaml.cs` (`internal bool OutOfSight => Minimized || !IsVisible || steppedAside || fullScreenAppOpen;`)
- Modify: `src/Deskbar.App/App.xaml.cs` (subscribe `tasks.CameDue`)
- Test: `tests/Deskbar.Windows.Tests/DueCardTests.cs`

**Interfaces:**
- Consumes: `TaskService.CameDue`, `TaskService.Changed` (Task 3); the method behind the `switch` remote command (`RemoteControl.Workspaces.cs`) to switch workspace.
- Produces: `sealed partial class DueCard : Window` with `DueCard(TaskItem task, string? workspaceName, Brush? colour, Action onClick)`,
  `void ShowStacked(int index)` (bottom right of the primary work area, 8 DIP gaps, index 0 lowest), `Guid TaskId`.

- [ ] **Step 1: Write the failing tests**
  - `The_bar_is_out_of_sight_when_minimized`: `MinimizeToTaskbar()` → `OutOfSight`; `ShowBar()` → not.
  - `A_card_does_not_take_the_focus`: shown card has `ShowActivated == false` and the `WS_EX_NOACTIVATE` style.
  - `Cards_stack_upwards`: index 1's `Top` is above index 0's.
- [ ] **Step 2: Run them to verify they fail**
- [ ] **Step 3: Implement.** In App: on `CameDue`, if `bar.OutOfSight`, show a card (title, workspace name and swatch,
  "came due"); click → switch to the workspace (free task: open the stopwatch menu) and close; on `tasks.Changed`
  close every card whose task is no longer Due. Restack after any close.
- [ ] **Step 4: Run the tests to verify they pass**
- [ ] **Step 5: Verify live**: minimize the bar, `deskbar.cmd task add "card probe" --workspace none --in 1m`, wait →
  a card bottom right, focus stays where it was; `deskbar.cmd task done <id>` → the card closes.
- [ ] **Step 6: Commit** `feat: a card says a task came due while the bar is out of sight`

### Task 9: Pull request 1

- [ ] **Step 1:** `dotnet build Deskbar.sln -c Release -warnaserror` and `dotnet test Deskbar.sln --filter "Category!=Integration"` → both clean.
- [ ] **Step 2:** Push `feat/tasks-in-the-bar` and open the pull request. Body ends with `*opus5.5*`. Release notes for users:
  the stopwatch, parking a workspace for 1 to 30 minutes, the blink and the card when the time is up, coming back ends
  it, and `deskbar.cmd task`.
- [ ] **Step 3:** Arm the base watch from CLAUDE.md and run `refresh-pr` whenever main moves.

---

# Pull request 2: the task window

Starts from main once pull request 1 is merged (new branch `feat/task-window`).

### Task 10: The task window and its controller

**Files:**
- Create: `src/Deskbar.App/TaskWindow.xaml`, `src/Deskbar.App/TaskWindow.xaml.cs` (display only),
  `src/Deskbar.App/TaskPanel.cs` (controller)
- Modify: `src/Deskbar.App/FloatingBar.Tasks.cs` (`OpenTasks` toggles the window instead of the menu; remove the menu),
  `src/Deskbar.App/App.xaml.cs` (construct, `monitor.Ignore(window handle)`, `bringForward` through `findActivator` as Quick find does at `:1625-1661`)
- Test: `tests/Deskbar.Windows.Tests/TaskPanelTests.cs`

**Interfaces:**
- Consumes: `TaskService` (Task 3), `WorkspaceManager.AskingWorkspaces` / `ClearAttention` (Task 4), the bar's remembered
  foreground window (`languageTarget`, `FloatingBar.Language.cs:73`).
- Produces:
  - `sealed record TaskDraft(string Title, Maybe<Guid> Workspace)`
  - `sealed record TaskRowView(string Key, TaskEntryKind Kind, string Title, string? WorkspaceName, Brush? Colour, TaskState State, string When)`
    (`Key` is the task id, or `"ask:" + workspace id`; `When` is `"4:32"`, `"3 min ago"`, `"now"` or `""`)
  - `TaskWindow`: `void Open(TaskDraft draft, IReadOnlyList<TaskRowView> rows, Rect under)`, `void Refresh(IReadOnlyList<TaskRowView> rows)`,
    `void Dismiss()`, `bool IsOpen`, `int RowsRebuilt` (tests), events-free: it calls back through the controller's delegates
  - `sealed class TaskPanel(TaskWindow window, TaskService tasks, WorkspaceManager manager, Func<TaskDraft> draftFromForeground, Func<Guid, Result> goTo, Action<nint> bringForward)`
    with `void Toggle(Rect stopwatchBounds)`, `Result Create(string title, Maybe<Guid> workspace, Maybe<TimeSpan> length)`,
    and one method per row action (`Go`, `DismissAsk`, `Rename`, `SetTimer`, `ClearTimer`, `MoveTo`, `Done`, `Delete`)

- [ ] **Step 1: Write the failing tests**
  - `The_draft_is_the_foreground_window_on_its_workspace`: foreground "FloatingBar.cs - Deskbar" on `Deskbar` → draft title and workspace match.
  - `The_draft_takes_the_latest_live_title`: `Deskbar` has "Wait for CI" (older) and "Tidy notes" (newer) → draft title "Tidy notes".
  - `A_new_title_on_the_same_workspace_adds_a_task`: draft "Tidy notes" edited to "Call back" + 5 min → `Deskbar` has three live tasks.
  - `A_minutes_button_creates_and_closes`: `Create("x", ws, 10 min)` → one Running task, window not open.
  - `Enter_without_a_button_is_no_timer`: → Open task.
  - `Rows_follow_the_entries_order` (same fixture as Task 3's ordering test, keys in that order).
  - `A_refresh_with_nothing_new_rebuilds_nothing`: `Refresh` twice with equal rows → `RowsRebuilt` unchanged; a countdown
    change rebuilds only that row's time text.
  - `Escape_and_click_away_dismiss`.
  - `A_failed_action_shows_the_reason`: `Rename` to "   " fails → the window shows the failure text, board unchanged.
  - `Dismissing_an_ask_clears_it`: → `manager.AskingWorkspaces` empty.
- [ ] **Step 2: Run them to verify they fail**
- [ ] **Step 3: Implement** layout A from the spec: title box focused with its text selected, workspace chips (current,
  "other workspace…", "none"), the five length buttons, then DUE / RUNNING / OPEN / "Done today (n)" collapsed. Row click
  goes there; double click on a title edits in place; "…" opens the actions menu; asks get a dismiss "✕" instead.
  Placed under the stopwatch on its screen, in that screen's DPI, inside the work area (reuse Quick find's `PlaceOn` approach).
  Live countdown from the controller's own 1 s timer while open, through `Refresh`.
- [ ] **Step 4: Run the tests to verify they pass**, then the whole Windows suite.
- [ ] **Step 5: Verify live**: open from the stopwatch, add a free task with no timer, park the workspace for 1 min, rename
  in place, mark done, check "Done today"; focus returns to the window that had it after Escape.
- [ ] **Step 6: Commit** `feat: the task window, a list of what needs you next under the stopwatch`

### Task 11: Pull request 2

- [ ] **Step 1:** Release build with `-warnaserror` and the test suite, both clean.
- [ ] **Step 2:** Push `feat/task-window`, open the pull request with release notes (the task window, editing, the dismiss
  button for a session's ask), body ending `*opus5.5*`.
- [ ] **Step 3:** Arm the base watch and refresh on every move of main.
