# Manager Inbox Page Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The Manager button and the manager line open the manager as a page inside the tasks panel's Inbox (its chat and a message box) instead of a docked terminal.

**Architecture:** Core gains the manager's page messages on ManagerRecord and the text a page message is delivered as. ManagerActions gains Open(attach) and Send. TasksBoard gains ShowManager and a chat source for the manager. TasksWindow gains a manager page drawn by its own small path, reusing ChatStreamPanel and the transcript poll.

**Tech Stack:** .NET 10 WPF, CSharpFunctionalExtensions, System.Reactive, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-28-manager-inbox-page-design.md`

## Global Constraints

- Box hint: "Message the manager…". Menu item: "Open in terminal". Empty-day note: "No manager yet today: send a message to start it."
- Page message opening, verbatim: "Deskbar: the user wrote to you on your page in Deskbar. Their own words, as if typed in your terminal:"
- `ManagerRecord.Messages` keeps the newest 100.
- `ManagerActions.Open(bool attach = true)`: the pipe's `task manager` keeps the terminal.
- The 40-line briefing cap is not touched.
- Repo C# style: expression bodies, var, readonly collections, ample comments carrying the why.

## Review Focus

- A send while the manager's first turn is running (listed, busy, no waiter yet): must go to the inbox, never stop the turn. Test in Task 2.
- Two sends in quick succession while a click is pending: both delivered, in order. Test in Task 2.
- A redraw while the manager page is open (board change): the stream is kept and the page holds its place, as a task page does. Test in Task 4.
- Esc while the keyboard is in the manager box: the box's, not back. Test in Task 4.
- A send with Claude sessions off: refused with FeatureOff, nothing stored. Test in Task 2.

---

### Task 1: Core: page messages and the delivered text

**Files:**
- Modify: `src/Deskbar.Core/Tasks/ManagerSession.cs` (ManagerRecord, constants, StartArguments)
- Modify: `src/Deskbar.Core/Tasks/ClaudeChatStream.cs` (DeliveredByDeskbar)
- Test: `tests/Deskbar.Core.Tests/` next to the existing ManagerSession and ClaudeChatStream tests

**Interfaces:**
- Produces: `public sealed record ManagerMessage(Guid Id, DateTimeOffset At, string Text)`;
  `ManagerRecord(..., IReadOnlyList<ManagerMessage>? Messages = null)` in Equals and GetHashCode (content compare);
  `ManagerSession.PageId` (fixed Guid), `ManagerSession.PageMessageOpening`, `ManagerSession.MessagesKept = 100`,
  `ManagerSession.PageMessage(string text, Maybe<string> openTaskLine) : string`,
  `ManagerSession.WithMessage(ManagerRecord record, ManagerMessage message) : ManagerRecord` (appends, keeps the newest 100),
  `ManagerSession.StartArguments(string briefingPath, string shimPath, string? firstMessage = null)` (null: FirstMessage).

- [ ] Tests: `PageMessage_starts_with_the_opening_and_carries_the_text_and_the_open_task_line`; `WithMessage_keeps_the_newest_100`;
  `Records_with_equal_messages_are_equal` (separate list instances); `StartArguments_end_with_the_given_first_message`;
  `Visible_drops_a_prompt_that_is_a_page_message` (ClaudeChatStream); `OpenTaskLine_counts_a_page_message_as_the_users`.
  Also a state.json round trip of a record with Messages, next to the existing AppState serialization tests.
- [ ] Run, see them fail; implement; run `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~Manager|FullyQualifiedName~ChatStream"`, green.
- [ ] Commit `feat: the manager's page messages and the text they are delivered as`.

### Task 2: ManagerActions: Open(attach) and Send

**Files:**
- Modify: `src/Deskbar.App/ManagerActions.cs`
- Test: the existing ManagerActions tests (find with `grep -rl "new ManagerActions(" tests`)

**Interfaces:**
- Consumes: Task 1.
- Produces: `public Result Open(bool attach = true)`; `public Result Send(string text, Maybe<TaskItem> openTask)`.

Send: FeatureOff refusal; blank text does nothing; stores `WithMessage` and `ManagerQuestion.AfterPrompt(record, sessionId, PageMessage)`
with Unread cleared; then delivers `PageMessage(text, ManagerSession.OpenTaskLine(...))`:
- no stored manager for today, or Next says StartNew: Started(day) with `firstMessage` = the page message, and the message
  is stored onto the new record once it is (the store happens after the record exists);
- Next says Resume: ResumedWith (Gone check stays);
- Attach/Claim: `CommentWake.PlanFor` over the one session, as Wake does (Inbox / Resume / StopThenResume).
- While `pending`: the text joins `queuedSends` (a list) and each is sent in order when the click or wake ends.

- [ ] Tests: `Send_while_the_first_turn_runs_goes_to_the_inbox` (listed, busy, waiter lapsed); `Send_with_no_manager_starts_one_with_the_message`;
  `Send_to_a_listed_idle_manager_with_a_live_waiter_goes_to_the_inbox`; `Two_sends_during_a_pending_click_go_in_order`;
  `Send_with_Claude_sessions_off_is_refused_and_stores_nothing`; `Open_without_attach_attaches_no_window`;
  `Send_stores_the_message_and_marks_the_record_replying`.
- [ ] Red, implement, green (`dotnet test tests/Deskbar.Windows.Tests --filter "FullyQualifiedName~ManagerActions"` or wherever they live).
- [ ] Commit `feat: send a message to the manager without its terminal`.

### Task 3: TasksBoard: ShowManager, the manager's chat source, actions

**Files:**
- Modify: `src/Deskbar.App/TasksBoard.cs` (+ `TasksBoard.Inbox.cs` for ShowManager)
- Test: the existing TasksBoard tests

**Interfaces:**
- Consumes: Tasks 1, 2.
- Produces: `TasksBoard.ShowManager()`; `TasksBoard.SendToManager : Func<string, Maybe<TaskItem>, Result>?` and
  `TasksBoard.OpenManagerTerminal : Func<Result>?` (init, set by the App); `TasksWindowActions.SendToManager : Func<string, Result>?`,
  `TasksWindowActions.OpenManagerTerminal : Action?`, `TasksWindowActions.ManagerMessages : Func<IReadOnlyList<ManagerMessage>>?`,
  `TasksWindowActions.ManagerState : Func<string>?` (the header's words);
  `TasksWindow.ShowManagerPage()` (Task 4) is what ShowManager calls after `Show()` and `ShowInbox(true)`.
- ChatSourceOf(PageId): `ClaudeChatSource.Choose` over one `TaskSession(record.SessionId, managerFolder, ...)`, state from the sessions list.
- ShownStateKey adds the messages' count and newest id. OpenInboxPage is None while the manager page shows.
- ManagerQuestion.Seen is applied when ShowManager runs (the user is reading).

- [ ] Tests: `ShowManager_opens_the_panel_on_the_Inbox_with_the_manager_page`; `ChatSourceOf_PageId_reads_todays_manager`;
  `ChatSourceOf_PageId_with_no_manager_is_nothing`; `A_new_page_message_redraws_the_open_panel`.
- [ ] Red, implement, green. Commit `feat: the tasks panel can show the manager`.

### Task 4: TasksWindow: the manager page

**Files:**
- Create: `src/Deskbar.App/TasksWindow.ManagerPage.cs`
- Modify: `TasksWindow.Inbox.cs` (draw entry, signature, leave), `TasksWindow.InboxPage.cs` (OpenInboxPage, InboxFollowsSelection leave the manager page), `TasksWindow.Transcript.cs` (stream for PageId)
- Test: `tests/Deskbar.Windows.Tests/` next to the Inbox page tests

**Interfaces:**
- Consumes: Task 3's actions.
- Produces: `internal void ShowManagerPage()`, `internal bool ManagerPageShown`, `internal TextBox? ManagerBox`.

State: `managerPage` bool and `managerFrom` (Guid?, the task page open when it opened). The page: back arrow, violet mark,
"Manager", state words, "⋯" with "Open in terminal"; ChatStreamPanel (messages as the user's bubbles, transcript items from
FollowTranscript(PageId)) bottom-aligned in a FastScrollViewer, kept across redraws and held in place by the same pageHold
logic; the box docked at the foot (Enter sends through SendToManager and clears the box, Shift+Enter a new line, a refusal
keeps the text). Back: to `managerFrom`'s page if any, else the list. Opening a task page, ShowTask, another view leave it.

- [ ] Tests: `ShowManagerPage_draws_the_header_the_stream_and_the_box`; `Enter_in_the_box_sends_and_clears`;
  `Esc_in_the_box_does_not_go_back`; `Back_returns_to_the_task_page_it_was_opened_from`; `A_redraw_keeps_the_stream_and_its_place`;
  `Opening_a_task_page_leaves_the_manager_page`. Run on the hidden desktop (`test-hidden.ps1`), never on Petre's desktop.
- [ ] Red, implement, green. Commit `feat: the manager's page in the Inbox`.

### Task 5: App wiring and docs

**Files:**
- Modify: `src/Deskbar.App/App.xaml.cs` (both OpenManager wirings; SendToManager; OpenManagerTerminal)
- Modify: user docs that describe the Manager button (find with `grep -rn "Manager" README.md docs/*.md`), and the manual test script with steps MI1-MI6.

- [ ] Bar OpenManager and the panel's OpenManager: `tasksBoard.ShowManager()` then `managerActions.Open(attach: false)`; a refusal as today.
- [ ] `dotnet build Deskbar.sln -c Release -warnaserror` clean; `dotnet test Deskbar.sln --filter "Category!=Integration"` green (Windows tests hidden desktop).
- [ ] Commit `feat: the Manager button opens the manager's page`, then strongest-model review of the branch, PR.
