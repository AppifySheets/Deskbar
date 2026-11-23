# Stalled Claude Tasks Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Claude task that no session has worked on for 15 minutes is handed back to Petre and shows on the bar; it goes back to Claude when a session works on it again. The Stop hook asks a parked session to run `task ask` before it stops.

**Architecture:** A pure Core function (`StalledTasks.Step`) reads the busy sessions from the poll Deskbar already runs (`claude agents --json`, every 15 s) and reassigns tasks; `TaskService` holds its in-memory clock and `ClaudeSessions.Heard` calls it after `Reconcile`. The mark `TaskItem.HandedBack` tells a handed-back task from one Petre assigned to himself. The Stop branch of `RemoteControl.Answer` additionally prints a `{"decision":"block"}` JSON when the session's task is parked.

**Tech Stack:** C# / .NET 10, xUnit, CSharpFunctionalExtensions, System.Reactive (existing).

**Spec:** `docs/superpowers/specs/2026-11-23-stalled-claude-tasks-design.md`

## Global Constraints

- Quiet threshold: 15 minutes (`StalledTasks.After = TimeSpan.FromMinutes(15)`).
- Busy grace: a session counts as working only once it has been listed 30 seconds (`StalledTasks.BusyGrace = TimeSpan.FromSeconds(30)`).
- Only open tasks that are In progress and have sessions (`IsClaudes`) are touched. Notes (no sessions) never.
- An unreadable agents list changes nothing (the poll already returns early; keep it that way).
- `HandedBack` is an optional tasks.json field, read tolerantly with the existing `TolerantTimeConverter`; no version bump.
- A poll that changes nothing returns the same `TaskBoard` reference, so nothing is saved or redrawn.
- Long explanatory comments on every new rule, in the file's existing style (CLAUDE.md: comments carry the reasoning).
- Work in this worktree; `dotnet build Deskbar.sln -c Release -warnaserror` and `dotnet test Deskbar.sln --filter "Category!=Integration"` green before the PR. Window tests only through `pwsh scripts/test-hidden.ps1`.

## Review Focus

- A task Petre assigned to himself in the tasks window must never be taken back by Claude when its session goes busy (pinned in Task 1 and Task 2).
- A session busy for an hour without touching its task, then idle, must not be handed back at once: the clock starts from the last busy poll, not the old UpdatedAt (Task 2).
- Deskbar restarted while a task was already quiet for an hour: handed back on the first poll, not 15 minutes later (Task 2).
- The Stop hook must never block a headless session, a session with background work, a task waiting on Petre, or a second time in one turn (Task 3).
- The Stop hook still blinks the workspace exactly as before when it also blocks (Task 3).

---

### Task 1: The HandedBack mark on a task

**Files:**
- Modify: `src/Deskbar.Core/Tasks/TaskItem.cs` (new init property beside `WaitingSince`)
- Modify: `src/Deskbar.Core/Tasks/TaskBoard.cs` (`Change`, `Assign`; new `HandBack`, `TakeBack`)
- Modify: `src/Deskbar.Core/Tasks/JsonTaskStore.cs` (DTO field `HandedBack`, write and read like `WaitingSince`)
- Test: `tests/Deskbar.Core.Tests/Tasks/TaskAssigneeTests.cs`, `tests/Deskbar.Core.Tests/Tasks/JsonTaskStoreTests.cs`

**Interfaces:**
- Produces: `TaskItem.HandedBack : Maybe<DateTimeOffset> { get; init; }`
- Produces: `TaskBoard.HandBack(Guid id, DateTimeOffset now) : TaskBoard` (Assignee Me, HandedBack now, UpdatedAt now; a task that is not open, not In progress or not Claude's returns this same board)
- Produces: `TaskBoard.TakeBack(Guid id, DateTimeOffset now) : TaskBoard` (only a task with HandedBack and Assignee Me: Assignee Claude, HandedBack None, UpdatedAt now; anything else returns this same board)

- [ ] **Step 1: Write the failing tests** in `TaskAssigneeTests.cs`, using the file's `Claudes`, `Get`, `clock`:

```csharp
[Fact]
public void HandBack_gives_a_quiet_task_to_Petre_and_marks_it()
{
    var board = Claudes(TaskBoard.Empty, id1).HandBack(id1, clock);
    Assert.Equal(Assignee.Me, Get(board, id1).Assignee);
    Assert.Equal(clock, Get(board, id1).HandedBack.Value);
    Assert.Equal(TaskStatus.InProgress, Get(board, id1).Status);
}

[Fact]
public void TakeBack_returns_a_handed_back_task_to_Claude_and_clears_the_mark()
{
    var board = Claudes(TaskBoard.Empty, id1).HandBack(id1, clock).TakeBack(id1, clock);
    Assert.Equal(Assignee.Claude, Get(board, id1).Assignee);
    Assert.True(Get(board, id1).HandedBack.HasNoValue);
}

[Fact]
public void TakeBack_leaves_a_task_Petre_assigned_to_himself()
{
    var board = Claudes(TaskBoard.Empty, id1).Assign(id1, Assignee.Me, clock).Value;
    Assert.Same(board, board.TakeBack(id1, clock));
}

[Fact]
public void Assigning_a_handed_back_task_to_himself_clears_the_mark()
{
    var board = Claudes(TaskBoard.Empty, id1).HandBack(id1, clock).Assign(id1, Assignee.Me, clock).Value;
    Assert.True(Get(board, id1).HandedBack.HasNoValue);
    Assert.Equal(Assignee.Me, Get(board, id1).Assignee);
}

[Theory]
[InlineData("ask")] [InlineData("done")] [InlineData("close")] [InlineData("status-backlog")]
public void Any_other_change_of_assignee_or_status_clears_the_mark(string change)
{
    var handed = Claudes(TaskBoard.Empty, id1).HandBack(id1, clock);
    var board = change switch
    {
        "ask" => handed.Ask(id1, "OK?", clock).Value,
        "done" => handed.MarkDone(id1, clock).Value,
        "close" => handed.Close(id1, clock).Value,
        _ => handed.SetStatus(id1, TaskStatus.Backlog, clock).Value,
    };
    Assert.True(Get(board, id1).HandedBack.HasNoValue);
}

[Fact]
public void HandBack_ignores_a_note_and_a_task_waiting_on_Petre()
{
    var note = Note(TaskBoard.Empty, id1);
    Assert.Same(note, note.HandBack(id1, clock));
    var waiting = Claudes(TaskBoard.Empty, id2).Ask(id2, "OK?", clock).Value;
    Assert.Same(waiting, waiting.HandBack(id2, clock));
}
```

And in `JsonTaskStoreTests.cs`, following that file's round-trip pattern: a task with `HandedBack` set saves and loads with the same instant (UTC), and `"handedBack":"garbage"` loads as `None` without failing the file.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~TaskAssigneeTests|FullyQualifiedName~JsonTaskStoreTests"`
Expected: compile errors for `HandBack`, `TakeBack`, `HandedBack`.

- [ ] **Step 3: Implement**

- `TaskItem.HandedBack`, with a comment naming the spec and why the mark exists (only a handed-back task goes back on busy).
- In `TaskBoard.Change` (which `ChangeOpen` uses), clear the mark when the change moved the assignee or the status: `var next = change(t); return next.Assignee != t.Assignee || next.Status != t.Status ? next with { HandedBack = None } : next;`. `HandBack` and `TakeBack` go through `Replaced`, not `Change`, so they set the mark themselves.
- `Assign`: return this same board only when the assignee is the same AND there is no mark; otherwise also set `HandedBack = None`.
- `JsonTaskStore`: `[property: JsonConverter(typeof(TolerantTimeConverter))] DateTimeOffset? HandedBack = null` on the DTO, written as UTC, read as `Maybe`, exactly as `WaitingSince`. Extend the header comment's list of optional fields.

- [ ] **Step 4: Run the tests to see them pass**

Run: the same command. Expected: PASS, and the rest of `Deskbar.Core.Tests` still green.

- [ ] **Step 5: Commit**

```bash
git add src/Deskbar.Core/Tasks tests/Deskbar.Core.Tests/Tasks
git commit -m "feat: a task can be handed back to Petre and taken back by Claude"
```

---

### Task 2: The quiet clock and the poll

**Files:**
- Create: `src/Deskbar.Core/Tasks/StalledTasks.cs`
- Modify: `src/Deskbar.Core/Tasks/TaskService.cs` (new `Stalled` method and its clock field)
- Modify: `src/Deskbar.App/ClaudeSessions.cs` (`Heard`, right after `tasks.Reconcile(listed)`)
- Modify: `README.md` (the Claude Code section, after the paragraph on `claude agents --json`), `docs/superpowers/notes/manual-test-script.md` (a tasks step)
- Test: `tests/Deskbar.Core.Tests/Tasks/StalledTasksTests.cs`

**Interfaces:**
- Consumes: `TaskBoard.HandBack`, `TaskBoard.TakeBack`, `TaskItem.HandedBack` (Task 1)
- Produces:

```csharp
public sealed record StalledStep(TaskBoard Board, ImmutableDictionary<Guid, DateTimeOffset> LastBusy,
    IReadOnlyList<Guid> HandedBack, IReadOnlyList<Guid> TakenBack);

public static class StalledTasks
{
    public static readonly TimeSpan After = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan BusyGrace = TimeSpan.FromSeconds(30);
    public static StalledStep Step(TaskBoard board, IReadOnlySet<string> working,
        ImmutableDictionary<Guid, DateTimeOffset> lastBusy, DateTimeOffset now);
}
```

- Produces: `TaskService.Stalled(IReadOnlySet<string> working) : void` (keeps `LastBusy` in a field, commits only when the board changed, traces `tasks: handed back <id8> "<title>"` and `tasks: taken back <id8> "<title>"`)

The rule `Step` implements, per open task that is In progress and has sessions:

- working = any of its session ids is in `working`.
- `LastBusy[t]` becomes `now` when working; otherwise it keeps what it had (absent stays absent). Entries for tasks no longer considered are dropped.
- quiet since = the later of `LastBusy[t]` (if any) and `t.UpdatedAt`. With no entry (first poll after a start) that is `UpdatedAt`, so a restart does not hold back a task that was already quiet.
- assigned to Claude, not working, `now - quietSince >= After`: `HandBack`.
- carries `HandedBack`, assigned to Petre, working: `TakeBack`.
- nothing changed: the same board reference.

- [ ] **Step 1: Write the failing tests** in `StalledTasksTests.cs` (clock and helpers as in `TaskAssigneeTests`; a task created at `t0` by `Create(..., Session("s1"), t0, id1)`):

```csharp
[Fact] public void Quiet_for_15_minutes_hands_the_task_back()
// Step(board, working: {}, lastBusy: empty, now: t0 + 15 min) => Assignee Me, HandedBack has value, HandedBack list == [id1]

[Fact] public void Quiet_for_14_minutes_keeps_it_with_Claude()
// now: t0 + 14 min => same board reference, empty lists

[Fact] public void A_busy_session_keeps_it_with_Claude_and_restarts_the_clock()
// Step(working {"s1"}, now t0 + 60 min) => LastBusy[id1] == t0+60; then Step(working {}, that LastBusy, now t0 + 70 min) => not handed back; at t0 + 75 min => handed back

[Fact] public void After_a_restart_a_task_quiet_for_an_hour_comes_back_on_the_first_poll()
// lastBusy empty, UpdatedAt t0, now t0 + 60 min => handed back at once

[Fact] public void A_status_line_update_counts_as_progress()
// SetStatusLine at t0 + 10 min; Step at t0 + 20 min => not handed back

[Fact] public void A_handed_back_task_goes_back_to_Claude_when_its_session_works()
// handed back, then Step(working {"s1"}) => Assignee Claude, TakenBack == [id1]

[Fact] public void A_task_Petre_assigned_to_himself_stays_his_when_the_session_works()
// Assign(Me) by hand, Step(working {"s1"}) => same board reference

[Fact] public void Notes_and_tasks_waiting_on_Petre_are_left_alone()
// a note and an asked task, now + 2 hours => same board reference
```

Write each as a full xUnit test with those assertions.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~StalledTasksTests"`
Expected: compile error, `StalledTasks` not defined.

- [ ] **Step 3: Implement `StalledTasks.Step`** with a header comment carrying the spec's "why" (Ruling 57's gap, the 2026-11-23 count of 13 idle sessions of 14) and each rule above. Functional style: a fold (`Aggregate`) over the considered tasks.

- [ ] **Step 4: Implement `TaskService.Stalled`** with an `ImmutableDictionary<Guid, DateTimeOffset> lastBusy` field, `CommitIfChanged(step.Board)`, and the two trace lines.

- [ ] **Step 5: Wire `ClaudeSessions.Heard`**: after `tasks.Reconcile(listed);` add

```csharp
// Stalled tasks (spec 2026-11-23-stalled-claude-tasks-design.md): a session counts as working only when the list says
// busy AND it has been listed for BusyGrace, because a just-resumed session reports busy while idle for up to 30 s.
tasks.Stalled(listed.Where(a => a.Busy && now - firstSeen[a.SessionId] >= StalledTasks.BusyGrace)
    .Select(a => a.SessionId).ToHashSet());
```

- [ ] **Step 6: Docs.** README, after the paragraph on Claude Code's list of sessions: "A Claude task that no session has worked on for 15 minutes is handed back to you and shows on the bar with Claude's last status line; it goes back to Claude by itself when the session works on it again. A task you assign to yourself stays yours." Manual test script: a step that leaves a session idle 15 minutes, sees the task on the bar, prompts the session, and sees it leave within a poll.

- [ ] **Step 7: Run the tests**

Run: `dotnet test Deskbar.sln --filter "Category!=Integration"`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src tests README.md docs/superpowers/notes/manual-test-script.md
git commit -m "feat: a Claude task quiet for 15 minutes comes back to you on the bar"
```

---

### Task 3: The Stop hook asks a parked session to say so

**Files:**
- Modify: `src/Deskbar.Core/Tasks/ClaudeHookEvent.cs` (three new init properties)
- Modify: `src/Deskbar.Core/Control/RemoteControl.ClaudeHook.cs` (the `"Notification" or "Stop"` arm and the header's event list)
- Modify: `src/Deskbar.Core/Control/ControlUsage.cs` (the `claude-hook` text: Stop may print a block)
- Modify: `README.md` (the Claude Code paragraph on the two ask hooks)
- Test: `tests/Deskbar.Core.Tests/ClaudeHookEventTests.cs`, `tests/Deskbar.Core.Tests/RemoteControlClaudeHookTests.cs` (the task-hook section, `Hooked`/`Said`/`tasks`)

**Interfaces:**
- Produces on `ClaudeHookEvent`: `bool StopHookActive { get; init; }` (`stop_hook_active` true), `bool BackgroundWork { get; init; }` (`background_tasks` or `session_crons` is a non-empty array)
- Produces: `SessionBriefing.ParkedNudge : string`, the reason text, exactly:
  `If the next move is Petre's (a pull request to merge, something to try, a decision), run deskbar.cmd task ask with what he must do, the text on stdin. If it is not, stop without saying anything.`

The block is printed only when ALL hold: the event is `Stop`; `!hook.Headless`; `ClaudeTasksInEffect`; a task service exists; `!StopHookActive`; `!BackgroundWork`; the session's task (`Board.TaskOf(sessionId)`) is open, In progress and assigned to Claude. The output is `{"decision":"block","reason":"<ParkedNudge>"}` serialised with System.Text.Json. The attention (blink) still runs first, exactly as now; its own output stays suppressed.

- [ ] **Step 1: Write the failing tests**

In `ClaudeHookEventTests.cs`: `stop_hook_active: true` parses to `StopHookActive`; `background_tasks: [{"type":"monitor"}]` and `session_crons: [{}]` each parse to `BackgroundWork`; empty arrays and missing fields parse to false.

In `RemoteControlClaudeHookTests.cs` (extend `HookOf` with an optional extra JSON fragment):

```csharp
[Fact] public void Stop_on_a_parked_task_asks_the_session_to_say_so()
// session s1 creates its task (Said ... "task","new","Fix it"), Hooked(manager,"Stop") output parses as JSON with decision "block" and reason == SessionBriefing.ParkedNudge

[Fact] public void Stop_after_a_nudge_lets_the_session_stop()          // stop_hook_active true => output ""
[Fact] public void Stop_with_background_work_lets_the_session_stop()  // background_tasks non-empty => ""
[Fact] public void Stop_on_a_task_waiting_on_Petre_lets_the_session_stop() // after task ask => ""
[Fact] public void Stop_from_a_headless_session_never_blocks()       // attended "0" => ""
[Fact] public void Stop_without_a_task_lets_the_session_stop()       // no task => ""
[Fact] public void Stop_still_blinks_when_it_blocks()                // as the existing blink tests, plus the block output
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~ClaudeHook"`
Expected: FAIL (properties missing, Stop prints nothing).

- [ ] **Step 3: Implement** the two parse properties, `SessionBriefing.ParkedNudge`, and split the `"Stop"` arm: `Stop` runs the attention as now, then returns `Said(json)` when the conditions hold, else `Quiet`. The header comment's event list gains the rule and its cost (one short extra turn per parked reply; `stop_hook_active` and Claude Code's cap of 8 stop a loop).

- [ ] **Step 4: Measure the script passes stdout.** `claude-hook.cmd` runs the shim with `2>nul` only, so stdout should reach Claude Code. Check by piping a Stop JSON for a live test task into the built `Deskbar.exe ctl claude-hook ...` from a scratch `DESKBAR_APPDATA` copy and reading the output. Expected: the block JSON on stdout, exit 0.

- [ ] **Step 5: Docs.** README: the two ask hooks paragraph says Stop also asks a session whose task is still in progress, and has no background work, to run `task ask` when the next move is yours. ControlUsage: the same in one line.

- [ ] **Step 6: Run everything**

Run: `dotnet build Deskbar.sln -c Release -warnaserror` then `dotnet test Deskbar.sln --filter "Category!=Integration"`
Expected: build clean, tests green.

- [ ] **Step 7: Commit**

```bash
git add src tests README.md
git commit -m "feat: the Stop hook asks a parked session to ask Petre"
```
