# Manager event wakes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deskbar wakes today's manager session, batched, when a Claude task is handed back or a question has waited 2 hours, and the Manager button shows a dot until Petre opens it.

**Architecture:** A pure `ManagerWakes.Plan` in Core decides from the stored `ManagerRecord`, the board, the listed sessions and the time whether to wake and with what message. An App-side `ManagerWaker` runs it on every `ClaudeSessions.Live` emission and delivers through the comment roads (`CommentWake`, `CommentInbox`, a bare resume). The manager gets the comment waiter like any session.

**Tech Stack:** C# / .NET 10, WPF, System.Reactive, CSharpFunctionalExtensions, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-25-manager-event-wakes-design.md`

## Global Constraints

- At most one wake per 30 minutes (`ManagerWakes.MinGap = 30 min`); a question triggers at 2 hours (`ManagerWakes.QuestionAge = 2 h`).
- Only while today's manager exists (`record.Day == today`) and `AppState.ClaudeTasksInEffect`.
- Never wake a manager listed busy.
- A resume of the manager is BARE (`--bg --resume <full id> -- <message>`): any flag forks a copy (step 1 probes).
- No new timer: the tick is `ClaudeSessions.Live`.
- No real people's names in code, tests or text; the briefing says "the user".

## Review Focus

- A Deskbar restart mid-day: nothing already reported is reported again (`Since` persisted).
- A step 1 state.json whose `Manager` has only SessionId and Day: loads, `Since` taken as the first tick's time.
- A task handed back twice in a day: the second report says not to nudge again.
- A question answered between ticks: no longer waiting, so not reported.
- The manager busy at every tick for an hour: the batch is held, not lost, and goes once it is idle.

---

### Task 1: ManagerRecord fields and ManagerWakes.Plan (Core)

**Files:**
- Modify: `src/Deskbar.Core/Tasks/ManagerSession.cs` (ManagerRecord)
- Create: `src/Deskbar.Core/Tasks/ManagerWakes.cs`
- Test: `tests/Deskbar.Core.Tests/Tasks/ManagerWakesTests.cs`, `tests/Deskbar.Core.Tests/ManagerStateTests.cs` (round trip)

**Interfaces:**
- Produces: `record ManagerRecord(string SessionId, DateOnly Day, DateTimeOffset? Since = null, bool Unread = false, IReadOnlyList<Guid>? HandedBack = null)`.
- Produces: `static ManagerWakes.Plan(ManagerRecord record, TaskBoard board, IReadOnlyCollection<ClaudeAgent> listed, DateOnly today, DateTimeOffset now) : ManagerWake`, where `ManagerWake` is `Nothing(ManagerRecord Record)` (the record to store when it changed, e.g. Since filled in) or `Wake(string Message, ManagerRecord Record)`.

- [ ] Tests (ManagerWakesTests): `Not_today_nothing`, `No_since_takes_now_and_does_not_wake`, `Hand_back_after_since_wakes`, `Hand_back_before_since_does_not`, `Question_two_hours_old_wakes_once`, `New_waiting_alone_does_not_wake`, `New_waiting_rides_along_with_a_trigger`, `Within_thirty_minutes_of_since_waits`, `Busy_manager_waits_and_keeps_since`, `Second_hand_back_today_says_do_not_nudge`, `Wake_sets_since_now_unread_and_handed_back`, `Closed_or_answered_task_not_reported`. Message assertions: contains each task's 8-char id and title.
- [ ] ManagerStateTests: a state.json Manager with only sessionId and day loads with Since null, Unread false.
- [ ] Implement. Busy = the record's session listed with `Busy`. "Since last wake": `HandedBack > Since`, `WaitingSince + 2h in (Since, now]`, `WaitingSince > Since`. The 30-minute gap is measured from `Since`.
- [ ] Run `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~ManagerWakes|FullyQualifiedName~ManagerState"`: PASS. Commit.

### Task 2: the manager gets a waiter, and the briefing learns wakes

**Files:**
- Modify: `src/Deskbar.Core/Control/RemoteControl.ClaudeHook.cs` (ClaudeWait carve-out), `src/Deskbar.Core/Tasks/ManagerSession.cs` (Briefing)
- Test: `tests/Deskbar.Core.Tests/RemoteControlClaudeHookTests.cs`, `tests/Deskbar.Core.Tests/Tasks/ManagerSessionTests.cs`

- [ ] Flip the existing test that says the manager gets no waiter: the manager folder now registers one (`<id> <generation>`).
- [ ] Briefing: replace "Nothing runs on a timer; you act only when the user messages you." with lines saying Deskbar may wake it with a list of events; on a wake, nudge each handed-back task's session once with one comment (next move the user's: run task ask; else task done or carry on), never one marked do-not-nudge; then list what waits on the user, oldest and most blocking first, with a suggested answer each, in a few lines. Test pins the new lines.
- [ ] Run the two test classes: PASS. Commit.

### Task 3: ManagerWaker and the button dot (App)

**Files:**
- Create: `src/Deskbar.App/ManagerWaker.cs`
- Modify: `src/Deskbar.App/App.xaml.cs` (wire), `src/Deskbar.App/ManagerActions.cs` (Since on start/claim, Unread cleared on Open), `src/Deskbar.App/FloatingBar.TaskList.cs` (dot)
- Test: `tests/Deskbar.Windows.Tests/ManagerWakerTests.cs`

**Interfaces:**
- Consumes: `ManagerWakes.Plan`, `CommentWake.Plan` (via a new overload taking `IReadOnlyList<TaskSession>`), `CommentInbox.Deliver`, `ClaudeSessions.RunAndFind(ManagerSession.ResumeArguments(id) + ["--", message], folder, id)`, `ClaudeSessions.StopNow`.
- Produces: `FloatingBar.ManagerUnread : Func<bool>?` read on each task-list paint.

- [ ] Tests: a Wake plan with the manager listed idle goes to the inbox and stores the record; the manager not listed is resumed with the message; one delivery at a time (a second Live emission during a delivery does nothing); a failed resume leaves the message in the inbox.
- [ ] Implement, delivering on the UI scheduler, one at a time; store the plan's record with `WorkspaceManager.SetManagerSession` after delivery (or a Nothing record when it changed).
- [ ] The dot: the bar's attention-dot brush on the Manager button's corner while `ManagerUnread()`; the bar repaints the task list on the state change.
- [ ] `dotnet build Deskbar.sln -c Release -warnaserror`; run ManagerWaker and ManagerActions tests on the hidden desktop (`pwsh scripts/test-hidden.ps1`). Commit.

### Task 4: manual steps and docs

- [ ] Manual steps MW1-MW4 in `docs/superpowers/notes/manual-test-script.md`: a stalled task wakes the manager, which nudges it; a 2-hour question shows in the list; the dot appears and clears on open; no wake on a day the manager was not opened.
- [ ] README's manager paragraph mentions wakes. Commit.
