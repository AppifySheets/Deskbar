# Tasks Board Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the timer-based tasks with one pile of tasks at the bottom of the bar, a board window, comment
threads, and Claude Code sessions that join or create a task through Deskbar's hooks.

**Architecture:** The shipped model in `Deskbar.Core/Tasks` is reworked in place: `TaskItem` loses its timer and gains
a status, sessions and comments; `TaskBoard` stays pure and immutable; `TaskService` stays the single owner that saves
to `tasks.json` (now version 2, migrated from version 1). The bar's footer gets a task list; the timers, pies, due
cards and the unmerged task window go. Claude Code sessions reach Deskbar through one new pipe verb, `claude-hook`,
whose client forwards the hook's stdin JSON and prints the reply as the session's context.

**Tech Stack:** C# / .NET 10, WPF, CSharpFunctionalExtensions, System.Reactive, xUnit 2.9 with plain `Assert` and
hand-written fakes (`tests/Deskbar.Core.Tests/Fakes.cs`); STA window tests via `StaThread.Run`.

**Spec:** `docs/superpowers/specs/2026-11-22-tasks-board-design.md`. Read it first; this plan argues from it. The
older `docs/superpowers/specs/2026-11-19-tasks-in-the-bar-design.md` explains the store's history only.

**Branch history:** `feat/tasks-board` starts from `origin/main` plus the spec. The unmerged `feat/task-window` branch
(window, due-card routing, load-problem line) is abandoned, not merged. Two of its pieces are worth taking back, and
the tasks below say where: `TaskService.LoadProblem` / `DismissLoadProblem` (commit `78ff230`) and the
`WorkspaceManager.ClearAttention` glow clearing with its snapshot before the COM call (commits `78ff230`, `a12a2f7`,
`7ba2ab6`). Read them with `git show <sha>`; do not cherry-pick whole commits.

## Global Constraints

- Statuses are exactly `Backlog`, `InProgress`, `WaitingOnYou`, `Done`, `Closed`; shown as "Backlog", "In progress",
  "Waiting on you", "Done", "Closed".
- Only Petre closes a task. A Claude session never deletes a task and never closes one; it marks its own task Done.
- A task is Claude's when it has at least one session. Nothing stores "Claude's" separately.
- The bar shows the three first tasks of the pile; "Waiting on you" is orange `FloatingBar.AskingOrange` (`#FFA726`).
- No timers, no countdown, no new global hotkey, no sound, no Windows toast.
- No WebView. No new NuGet package unless a task says so.
- Other programs reach the app only through the control pipe; every bar and window action has a pipe twin listed in
  `ControlUsage`, and every new verb is added to `Help_lists_every_command`
  (`tests/Deskbar.Core.Tests/RemoteControlMenuTests.cs:629`).
- Every stored time is a UTC `DateTimeOffset`; every task carries `UpdatedAt`; ids are `Guid.NewGuid()` made here.
- The Claude hook script always exits 0; its command line stays the quoted forward-slash path (`ClaudeSkill.cs`
  comments explain why).
- The word is "workspace". Code style per CLAUDE.md: `private` implied, expression bodies, `var`, immutable records,
  readonly collections, `Result`/`Maybe`, Rx over events, long comments carrying the reasoning, no em dashes anywhere.
- Every merge releases: no merged pull request may leave the app without a task UI (spec, "Pull request order").
- Before each pull request: `dotnet build Deskbar.sln -c Release -warnaserror` (0 warnings) and
  `dotnet test Deskbar.sln --filter "Category!=Integration"` green. Release notes in the PR body, no version edits.
- Petre runs a Debug build from this worktree's `bin`. Stop it before building and start the new one right after, in
  one command (memory: restart immediately). Implementers never start or stop the app; the controller does.

## Review Focus

1. Deskbar is not running, or slow, when a hook fires: the session's prompt goes on at once with no context and no
   error. Test in Task 7 (`A_hook_with_deskbar_down_prints_nothing_and_exits_0`).
2. A `tasks.json` written by 2.4.143 to 2.5.x (version 1, with running timers and history): every live task comes back
   as a Backlog note on its workspace, done ones as Closed, and the old file is kept as `tasks.json.v1`. Test in Task 2.
3. More than three tasks wait on Petre: none disappears without a sign; the "more" button counts the hidden waiting
   ones in orange. Test in Task 4.
4. A session is resumed or its context cleared (SessionStart with `source` `resume`, `clear` or `compact`): it keeps
   its task instead of being asked to pick again. Test in Task 8.
5. A session runs in a folder no VS Code window shows (a terminal, or a folder nobody opened): its task is made with
   no workspace and clicking it does nothing harmful. Test in Task 8.

---

# Pull request A: the pile replaces the timers

### Task 1: The task model without timers

**Files:**
- Modify: `src/Deskbar.Core/Tasks/TaskItem.cs`, `src/Deskbar.Core/Tasks/TaskBoard.cs`
- Delete: `src/Deskbar.Core/Tasks/TaskPresets.cs`, `src/Deskbar.Core/Tasks/TaskEntry.cs`,
  `tests/Deskbar.Core.Tests/Tasks/TaskPresetsTests.cs`
- Test: `tests/Deskbar.Core.Tests/Tasks/TaskBoardTests.cs` (rewritten)

**Interfaces:**
- Produces (namespace `Deskbar.Core.Tasks`):
  - `enum TaskStatus { Backlog, InProgress, WaitingOnYou, Done, Closed }`
  - `sealed record TaskSession(string Id, string Folder, DateTimeOffset Joined, Maybe<DateTimeOffset> Ended)`
    with `bool IsLive => Ended.HasNoValue`
  - `sealed record TaskComment(Guid Id, Maybe<string> SessionId, DateTimeOffset At, string Markdown)`
    (`SessionId` None means Petre wrote it)
  - `sealed record TaskItem(Guid Id, string Title, Maybe<Guid> WorkspaceId, TaskStatus Status, string StatusLine,
    Maybe<string> Question, DateTimeOffset Created, DateTimeOffset UpdatedAt, DateTimeOffset Raised,
    IReadOnlyList<TaskSession> Sessions, IReadOnlyList<TaskComment> Comments)` with `bool IsClaudes => Sessions.Count > 0`,
    `bool IsOpen => Status != TaskStatus.Closed`, `bool HasLiveSession`
  - `TaskBoard` (pure; every change takes `now` and stamps `UpdatedAt`):
    `Result<(TaskBoard, TaskItem)> AddNote(string title, Maybe<Guid> workspace, DateTimeOffset now, Guid newId)`,
    `Result<(TaskBoard, TaskItem)> Create(string title, Maybe<Guid> workspace, TaskSession session, DateTimeOffset now, Guid newId)`,
    `Result<TaskBoard> Join(Guid id, TaskSession session, DateTimeOffset now)`,
    `Result<TaskBoard> SetStatusLine(Guid id, string text, DateTimeOffset now)`,
    `Result<TaskBoard> Ask(Guid id, string question, DateTimeOffset now)`,
    `TaskBoard Answered(string sessionId, DateTimeOffset now)`,
    `TaskBoard SessionEnded(string sessionId, DateTimeOffset now)`,
    `Result<TaskBoard> MarkDone(Guid id, DateTimeOffset now)`, `Result<TaskBoard> Close(Guid id, DateTimeOffset now)`,
    `Result<TaskBoard> Reopen(Guid id, DateTimeOffset now)`, `Result<TaskBoard> SetStatus(Guid id, TaskStatus status, DateTimeOffset now)`,
    `Result<TaskBoard> Rename(Guid id, string title, DateTimeOffset now)`,
    `Result<TaskBoard> MoveTo(Guid id, Maybe<Guid> workspace, DateTimeOffset now)`, `Result<TaskBoard> Delete(Guid id)`,
    `Result<TaskBoard> Comment(Guid id, Maybe<string> sessionId, string markdown, DateTimeOffset now, Guid newId)`,
    `Maybe<TaskItem> TaskOf(string sessionId)`, `IReadOnlyList<TaskItem> Offered(Maybe<Guid> workspace)`,
    `IReadOnlyList<TaskItem> Pile()`, kept: `Prune`, `Find`, `Empty`

- [ ] **Step 1: Write the failing tests** in `TaskBoardTests.cs` (clock: a captured `DateTimeOffset` moved by the test):
  - `A_note_starts_in_the_backlog_with_no_sessions`: `AddNote("Dishwasher tablets", None)` → `Status == Backlog`,
    `IsClaudes == false`, `StatusLine == ""`.
  - `An_empty_title_is_refused`: `AddNote("  ", None)` fails with "A task needs a title" (keep the existing message).
  - `A_session_creates_a_task_in_progress`: `Create("Tasks redesign", ws, session("s1"))` → `InProgress`, one live session.
  - `Joining_a_note_makes_it_claudes`: `AddNote` on `ws`, then `Join(id, session("s1"))` → `InProgress`, `IsClaudes`.
  - `A_closed_task_cannot_be_joined`: `Join` on a Closed task fails with "The task is closed."
  - `Asking_waits_on_you_and_answering_resumes`: `Ask(id, "OK to restart?")` → `WaitingOnYou`, `Question` set, `Raised`
    moved to now; `Answered("s1")` → `InProgress`, `Question` None.
  - `Answering_a_done_task_resumes_it`: `MarkDone`, then `Answered("s1")` → `InProgress` (spec call: he is continuing).
  - `Answering_never_touches_a_closed_task_or_another_sessions_task`.
  - `A_session_ending_keeps_the_task_open`: `SessionEnded("s1")` → status unchanged, session `Ended` set, `HasLiveSession` false.
  - `Only_close_closes_and_reopen_goes_back`: `MarkDone` → `Done`; `Close` → `Closed`; `Reopen` → `InProgress` when a
    session is live, `Backlog` when none.
  - `A_status_line_never_moves_a_task`: `SetStatusLine` leaves `Raised` unchanged.
  - `The_pile_puts_waiting_first_then_done_then_newest`: five tasks (one Closed, one Done, two WaitingOnYou raised at
    different times, one Backlog) → `Pile()` ids in order: newer waiting, older waiting, done, backlog; Closed absent.
  - `Offered_is_this_workspaces_open_tasks_and_claudes_free_ones`: tasks on `ws`, on `other`, a free note, a free
    Claude task, a closed one on `ws` → `Offered(ws)` is the open `ws` tasks plus the free Claude task, no free note.
  - `TaskOf_finds_the_open_task_a_session_works_on`; `Comments_keep_their_order_and_author`.
  - Keep and adapt the existing `Prune` and `Find` tests (prefix of at least 4, ambiguity message).
- [ ] **Step 2: Run them to verify they fail**
  Run: `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~TaskBoardTests"`
  Expected: build errors on the new types (that is the failure).
- [ ] **Step 3: Implement** the records and `TaskBoard` methods above. `Raised` is set at creation and by `Ask`, nothing
  else. `Pile()` orders `WaitingOnYou` by `Raised` descending, then `Done` by `UpdatedAt` descending, then the rest by
  `Raised` descending, and leaves out `Closed`. Remove every timer type (`TaskTimer`, `TimerRecord`, `TimerEnd`,
  `TaskState`) and fix the compile errors this causes in Core only by deleting their users; App and Windows users go in
  Task 3 and Task 4, so the solution build is allowed to fail until then. Header comment pointing at the spec.
- [ ] **Step 4: Run the tests to verify they pass** (same command). Expected: PASS.
- [ ] **Step 5: Commit** `feat: tasks have a status, sessions and comments instead of timers`

### Task 2: The service and the store, version 2 with migration

**Files:**
- Modify: `src/Deskbar.Core/Tasks/TaskService.cs`, `src/Deskbar.Core/Tasks/JsonTaskStore.cs`
- Test: `tests/Deskbar.Core.Tests/Tasks/TaskServiceTests.cs`, `tests/Deskbar.Core.Tests/Tasks/JsonTaskStoreTests.cs`

**Interfaces:**
- Consumes: Task 1's `TaskBoard` methods.
- Produces: `TaskService(ITaskStore store, Func<DateTimeOffset> now, Func<Guid>? newId = null, Action<string>? trace = null)`
  with `TaskBoard Board`, `IObservable<Unit> Changed`, `Result Load()`, `Maybe<string> LoadProblem`,
  `void DismissLoadProblem()`, `void Prune(IReadOnlySet<Guid>)`, and one `Result`-returning wrapper per board change:
  `AddNote`, `Create`, `Join`, `SetStatusLine`, `Ask`, `MarkDone`, `Close`, `Reopen`, `SetStatus`, `Rename`, `MoveTo`,
  `Delete`, `Comment` (same parameters minus `now`/`newId`; `AddNote` and `Create` return `Result<TaskItem>`), plus
  `void Answered(string sessionId)` and `void SessionEnded(string sessionId)`. `Tick`, `Dwell`, `CameDue`, `Park`,
  `Entries`, `DueWorkspaces`, `RunningWorkspaces` are removed.

- [ ] **Step 1: Write the failing tests**
  - `TaskServiceTests`: `Every_change_saves_and_pulses`; `A_failed_save_keeps_the_change_in_memory`;
    `A_failed_load_is_reported_until_dismissed` (port from commit `78ff230`); `Answering_an_unknown_session_saves_nothing`.
  - `JsonTaskStoreTests`:
    - `A_version_2_file_round_trips` (statuses written as names, sessions, comments, UTC times).
    - `A_version_1_file_migrates`: a literal version-1 JSON (shape: `{"tasks":[{"id":..,"title":..,"workspaceId":..,
      "created":..,"updatedAt":..,"timer":{"started":..,"lengthSeconds":300},"doneAt":null,"history":[]}]}`) with one
      running, one open and one done task → two `Backlog` notes keeping their workspace and one `Closed`; no sessions;
      `Raised == Created`.
    - `The_version_1_file_is_kept_beside_the_new_one`: after the first `Save` following a v1 `Load`,
      `tasks.json.v1` exists with the original bytes.
    - `An_unknown_status_reads_as_backlog`; keep the existing missing, empty, corrupt and `.previous` tests.
- [ ] **Step 2: Run them to verify they fail**
  Run: `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~Tasks"`; expected: failures on the new tests.
- [ ] **Step 3: Implement.** File shape `{"version":2,"tasks":[...]}`; no `version` means 1. Statuses as string names
  with a tolerant converter (unknown → `Backlog`, like the existing `TolerantTimerEndConverter`). The v1 copy is made
  once, by `Save`, only when the loaded file was v1 and `tasks.json.v1` does not exist yet. Take `LoadProblem` from
  commit `78ff230`.
- [ ] **Step 4: Run the tests to verify they pass.** Same command; expected PASS.
- [ ] **Step 5: Commit** `feat: tasks.json version 2, and version 1 files come back as notes`

### Task 3: Take the timers out of the app and the pipe

**Files:**
- Delete: `src/Deskbar.App/DueCard.xaml`, `src/Deskbar.App/DueCard.xaml.cs`, `src/Deskbar.App/DueCards.cs`,
  `tests/Deskbar.Windows.Tests/DueCardTests.cs`, `tests/Deskbar.Core.Tests/WorkspaceManagerTaskMarksTests.cs`
- Modify: `src/Deskbar.App/FloatingBar.Tasks.cs` (stopwatch, pies, countdown, draft code removed; keep `WorkspaceName`
  and `NoteWorkspaceAccent`), `src/Deskbar.App/FloatingBar.Shell.cs:200-449` (strip without `stripTasks`),
  `src/Deskbar.App/FloatingBar.xaml.cs` (`ForgetTaskPies`, the `timer` parameters at `GroupRow` l.3787 and the caption
  l.5607, the pie at l.5687), `src/Deskbar.App/FloatingBar.Asking.cs:64-76` (`AnyTaskDue` gone),
  `src/Deskbar.App/App.xaml.cs` (l.1519 marks, l.1532-1548 due cards, l.1826-1827 tick), `src/Deskbar.Core/WorkspaceManager.cs`
  (`SetTaskMarks`, `dueWorkspaces`, `runningTimers`, their use in `Asking(Overview)`), `src/Deskbar.Core/Overview/Overview.cs:85`
  (`WorkspaceGroup.Timer`), `src/Deskbar.Core/Control/RemoteControl.Tasks.cs`, `src/Deskbar.Core/Control/ControlUsage.cs:46-56`
- Test: `tests/Deskbar.Core.Tests/RemoteControlTaskTests.cs`, `tests/Deskbar.Windows.Tests/FloatingBarTasksTests.cs`

**Interfaces:**
- Consumes: Task 2's `TaskService`.
- Produces: the `task` verb's forms: `task list`, `task add <title> [--workspace <name>|none]` (a note; workspace
  defaults to the caller's as today), `task close <id>`, `task reopen <id>`, `task status <id> <backlog|in-progress|waiting|done>`,
  `task rename <id> <title>`, `task move <id> <workspace>|none`, `task comment <id> <markdown>`, `task delete <id>`.
  `task list` replies a JSON array of `{id, title, workspace, status, statusLine, question, claudes, sessions, comments, updatedAt}`
  (`status` lowercase with hyphens, `comments` the count). Session-facing forms come in Task 8.

- [ ] **Step 1: Write the failing tests** in `RemoteControlTaskTests.cs`: `Add_makes_a_note_on_the_callers_workspace`,
  `Add_with_none_makes_a_free_note`, `Close_and_reopen`, `Status_takes_the_four_names_and_refuses_closed`
  ("Use close to close a task"), `List_is_the_pile_in_order_as_json`, `Timer_and_in_are_gone`
  (`task timer x --in 5m` → exit 1 with the task usage; `task add x --in 5m` → exit 1 "Unexpected --in"), keep the
  unknown-id, ambiguous-id and help tests. In `FloatingBarTasksTests.cs`: `The_strip_has_no_stopwatch` and
  `A_row_draws_no_pie` (replace the stopwatch and pie tests).
- [ ] **Step 2: Run them to verify they fail**
  Run: `dotnet test tests/Deskbar.Core.Tests --filter "FullyQualifiedName~RemoteControlTask"` and
  `dotnet test tests/Deskbar.Windows.Tests --filter "FullyQualifiedName~FloatingBarTasks"`.
- [ ] **Step 3: Implement.** Remove every timer user listed above; `ControlUsage`'s Tasks section lists exactly the
  forms above. Search the test projects for `TaskService`, `TaskTimer` and `Stopwatch(` uses tied to tasks (code map
  names `AwaitParentExitTests`, `FloatingBarStartSearchTests`, `FloatingBarTrayArrowTests`, `HeaderDropPendingTests`,
  `TileSurfaceTests` as incidental matches) and fix only real ones. `StripItem.Tasks` stays: Task 4 uses it to hide the
  list.
- [ ] **Step 4: Run the tests, then both whole test projects once, then the Release build.** Expected: green, 0 warnings.
- [ ] **Step 5: Commit** `feat: the stopwatch, pies and due cards are gone, and task commands speak statuses`

### Task 4: The task list at the bottom of the bar

**Files:**
- Create: `src/Deskbar.App/FloatingBar.TaskList.cs` (partial FloatingBar)
- Modify: `src/Deskbar.App/FloatingBar.xaml` (a `TaskListLine` Border in the `ShellFooter` StackPanel, above
  `ShellStrip`, Collapsed until filled, like `ProReminderLine`), `src/Deskbar.App/App.xaml.cs` (hand the bar the service),
  `src/Deskbar.App/ManageWindow.xaml.cs:573` (`StripItem.Tasks` label "Task list")
- Test: `tests/Deskbar.Windows.Tests/FloatingBarTaskListTests.cs`

**Interfaces:**
- Consumes: `TaskService.Board.Pile()`, `TaskService.Changed`, `AddNote`, `Close`, `WorkspaceName(Guid)`, the bar's
  lane colours (`NoteWorkspaceAccent`), `manager.CurrentWorkspaceId`, the existing switch-to-workspace path.
- Produces: `internal sealed record TaskLine(Guid Id, string Title, string Detail, TaskStatus Status, bool Claudes,
  string? WorkspaceName, Brush? Colour, Maybe<nint> Window)`; `internal static IReadOnlyList<TaskLine> Lines(TaskBoard board, ...)`;
  `internal Func<Guid, Result> GoToTask` (set by App; Task 4 sends a note to its workspace and a Claude task to its
  workspace too, Task 10 changes Claude tasks to the session); `internal Action? OpenTasksWindow` (null until Task 10);
  test reads `TaskListShown`, `TaskListMoreText`, `TaskListAdding`.

- [ ] **Step 1: Write the failing tests** (STA, bar built as in `FloatingBarTasksTests`):
  - `Three_lines_show_and_the_rest_wait_behind_more`: five open tasks → three lines, button text "+2".
  - `Hidden_waiting_tasks_are_counted_in_orange`: five tasks waiting on you → button text "+2 waiting", foreground
    `AskingOrange`.
  - `A_claude_line_is_marked`: a task with a session → the line has the CLAUDE tag, the orange edge, and shows the
    question when waiting, else the status line.
  - `A_note_has_a_tick_box_that_closes_it`: tick → `Status == Closed`, line gone.
  - `A_done_claude_task_has_a_close_button`.
  - `A_claude_task_with_no_live_session_says_so`: every session ended → detail "no live session" after the status line.
  - `Add_puts_a_note_on_the_current_workspace`: start adding, type "Dishwasher tablets", choose "none", Enter → a free
    Backlog note; Esc drops the text and adds nothing.
  - `Clicking_a_line_goes_to_its_workspace` (through `GoToTask`).
  - `More_shows_everything_until_the_window_exists`: with `OpenTasksWindow` null, "+2" expands the list in place and
    collapses on a second click.
  - `The_list_hides_with_its_strip_item`: `StripItem.Tasks` hidden → `TaskListShown` false.
  - `A_failed_load_shows_one_line_with_a_dismiss`: `LoadProblem` set → first line "Tasks could not be read." plus the
    store's own reason as a tooltip, ✕ calls `DismissLoadProblem` (wording rules from commit `78ff230`'s
    `TaskPanel.LoadProblemLine`).
- [ ] **Step 2: Run them to verify they fail**
  Run: `dotnet test tests/Deskbar.Windows.Tests --filter "FullyQualifiedName~FloatingBarTaskList"`.
- [ ] **Step 3: Implement** to the chosen mockup (`.superpowers/brainstorm/876-1795339550/content/claude-marked.html`,
  option A, and `tasklist-bottom.html` for the add box). Rebuild the lines only when `Changed` pulses and the pile's
  shape changed (ids, statuses, titles, details); never during the bar's own rebuild (re-entrancy guard, CLAUDE.md).
  Typing in the bar uses the same activation path as the bar's search box. Brushes frozen. The bar is `SizeToContent`,
  so keep every new element present and Collapsed rather than conditional thickness (code map, section 6).
- [ ] **Step 4: Run the tests, then the Windows project once, then the Release build.**
- [ ] **Step 5: Commit** `feat: a task list at the bottom of the bar, Claude's tasks marked, notes added in place`

### Task 5: Pull request A

- [ ] **Step 1:** README's tasks section and manual test script steps 42 to 50 rewritten for the pile (no timers);
  commit `docs: the README and the manual test script describe the task list`.
- [ ] **Step 2:** Release build with `-warnaserror` and the test suite, both clean.
- [ ] **Step 3 (controller):** swap Petre's Deskbar to this build in one command and give him a short live checklist
  (add a note, tick it, five tasks and "+2", the list hidden from Manage's strip items).
- [ ] **Step 4:** Push `feat/tasks-board`, open the pull request with release notes for users (the stopwatch and timers
  are gone; a task list at the bottom of the bar; quick notes; old tasks come back as notes), body ending `*opus5.5*`.
  Arm the base watch from CLAUDE.md.

---

# Pull request B: Claude sessions work against a task

### Task 6: Measure before building the hooks

**Files:**
- Create: `docs/superpowers/notes/2026-11-22-claude-hook-probes.md`

- [ ] **Step 1:** Round-trip time of `deskbar.cmd ctl version` started the way a hook starts it (Git Bash running a
  `.cmd`), 20 runs, median and worst, with Deskbar running and with it stopped. Write the numbers.
- [ ] **Step 2:** The value of `CLAUDE_CODE_ENTRYPOINT` (and any other variable that differs) in a hook for: a VS Code
  extension session, an interactive terminal session, `claude -p "hi"`. Use a throwaway hook in a scratch project's
  `.claude/settings.local.json` that appends `env` to a scratch file; remove it after.
- [ ] **Step 3:** With two VS Code windows open on different folders, bring window A forward through Deskbar
  (`deskbar.cmd` focus path) and then start `vscode://anthropic.claude-code/open?session=<an id from A>`; record which
  window opens it. Repeat with B in front.
- [ ] **Step 4:** Ledger rulings from the numbers: (a) if the median exceeds 300 ms, the hook's path is the ruling's
  subject before Task 7 starts; (b) the exact entrypoint values treated as headless; (c) whether Task 10 opens the link
  or only brings the window forward. Commit `docs: what Claude's hooks see, and how long a hook takes`.

### Task 7: The `claude-hook` verb and the hook script

**Files:**
- Modify: `src/Deskbar.App/ControlCommandLine.cs`, `src/Deskbar.Windows/Control/ControlConsole.cs` (read stdin for
  `claude-hook` only), `src/Deskbar.Core/Control/ControlArguments.cs` (`AboutTheCaller` gains `claude-hook`),
  `src/Deskbar.App/ClaudeSkill.cs` (`WriteHookScript`), `src/Deskbar.App/ClaudeHooks.cs` (events),
  `src/Deskbar.Core/Persistence/AppState.cs`, `src/Deskbar.Core/WorkspaceManager.cs`, `src/Deskbar.App/ManageWindow.xaml(.cs)`,
  `src/Deskbar.Core/Control/RemoteControl.cs`, `src/Deskbar.Core/Control/ControlUsage.cs`
- Create: `src/Deskbar.Core/Control/RemoteControl.ClaudeHook.cs`, `src/Deskbar.Core/Tasks/ClaudeHookEvent.cs`
- Test: `tests/Deskbar.Core.Tests/ClaudeHookEventTests.cs`, `tests/Deskbar.Windows.Tests/ClaudeSkillTests.cs`,
  `tests/Deskbar.Core.Tests/RemoteControlClaudeHookTests.cs`

**Interfaces:**
- Produces:
  - `sealed record ClaudeHookEvent(string Event, string SessionId, string Folder, Maybe<string> Source, bool Headless)`
    with `static Result<ClaudeHookEvent> Parse(string json, string? entrypoint)` (fields `hook_event_name`,
    `session_id`, `cwd`, `source`; `Headless` from Task 6's ruling).
  - Verb `claude-hook [--entry <value>] [--from <folder>]` with the hook JSON as the client's stdin, forwarded to the
    server as one argument `--json <text>`. Reply output is the text the session reads (empty for Notification, Stop,
    SessionEnd); exit 0 always unless the JSON does not parse (exit 1, nothing printed to stdout).
  - Setting `AppState.NoClaudeTasks` / `ClaudeTasks`, `WorkspaceManager.SetClaudeTasks(bool)`, pipe twin
    `claude-tasks on|off`, Manage checkbox "Claude Code sessions keep their tasks in the task list" under the Teach
    Claude Code box, enabled only while that box is ticked. Effective value: `ClaudeSkill && ClaudeTasks`.
  - `ClaudeHooks.Sync(bool on, bool tasks, string settingsPath, string command)`: events `Notification`, `Stop` while
    on; plus `SessionStart`, `UserPromptSubmit`, `SessionEnd` while tasks is also on.
  - The script: `@call "<shim>" claude-hook --entry "%CLAUDE_CODE_ENTRYPOINT%" --from "%CLAUDE_PROJECT_DIR%" 2>nul`
    (no `>nul` on stdout; stdin passes through), then `@exit /b 0`; keep the no-folder branch.

- [ ] **Step 1: Write the failing tests:** `Parse_reads_the_documented_fields` (a SessionStart, a UserPromptSubmit and
  a SessionEnd sample from the hooks reference); `Parse_marks_headless_entrypoints`; `Notification_and_stop_still_ask`
  (the verb calls `Attention(from)` as today); `A_hook_with_deskbar_down_prints_nothing_and_exits_0` (script run
  through Git Bash as `The_hook_reaches_deskbar_when_git_bash_runs_it` does, with no pipe server);
  `The_task_hooks_come_and_go_with_their_setting` (settings.json gains and loses exactly the three entries);
  `Turning_teach_off_takes_the_task_hooks_too`; `Claude_tasks_is_in_the_help`.
- [ ] **Step 2: Run them to verify they fail.**
- [ ] **Step 3: Implement.** The server handler for `claude-hook` returns empty output for every event in this task;
  Task 8 fills SessionStart, UserPromptSubmit and SessionEnd. Keep every rule in `ClaudeSkill.cs`'s header comment.
- [ ] **Step 4: Run the tests, both projects, and the Release build.**
- [ ] **Step 5: Commit** `feat: Claude's hooks reach Deskbar with the session, under their own setting`

### Task 8: Sessions join or create a task

**Files:**
- Modify: `src/Deskbar.Core/Control/RemoteControl.ClaudeHook.cs`, `src/Deskbar.Core/Control/RemoteControl.Tasks.cs`,
  `src/Deskbar.Core/Control/ControlUsage.cs`, `src/Deskbar.Core/WorkspaceManager.cs` (`ClearAttention` glow, from the
  commits named in the header)
- Create: `src/Deskbar.Core/Tasks/SessionBriefing.cs` (the text a session reads)
- Test: `tests/Deskbar.Core.Tests/RemoteControlClaudeHookTests.cs`, `tests/Deskbar.Core.Tests/Tasks/SessionBriefingTests.cs`

**Interfaces:**
- Consumes: Task 7's `ClaudeHookEvent` and verb; Task 2's service; `RemoteControl.CallerOf(from)` for the workspace.
- Produces:
  - `static class SessionBriefing`: `string Start(ClaudeHookEvent e, Maybe<TaskItem> own, IReadOnlyList<TaskItem> offered)`,
    `string Prompt(Maybe<TaskItem> own, bool closedByPetre)`. Both name the session id and the exact commands to run.
  - Session-facing `task` forms, each with `--session <id>`: `task join <id>`, `task new <title>` (workspace from
    `--from`, none when no VS Code window shows the folder), `task status-line <text>`, `task ask <question>` (also asks
    for attention on the caller's workspace, as `attention` does), `task done`, `task comment <markdown>` (on the
    session's own task), `task later <title>` (a Backlog follow-up on the caller's workspace).
  - SessionStart (not headless): `source` `startup` → `Start` with the offered list; `resume`, `clear`, `compact`,
    `fork` → `Start` naming its own task when `TaskOf(session)` finds one. UserPromptSubmit → `Answered(session)`, then
    `Prompt`. SessionEnd → `SessionEnded(session)`, no output. Headless → no output, no change.

- [ ] **Step 1: Write the failing tests:** `A_new_session_is_offered_its_workspaces_tasks`; `A_resumed_session_keeps_its_task`;
  `A_prompt_answers_a_waiting_task_and_reminds_the_session`; `A_closed_task_tells_the_session_to_start_a_new_one`;
  `A_session_outside_any_vs_code_window_makes_a_free_task`; `Ask_waits_on_you_and_blinks_the_workspace`;
  `Done_marks_only_the_sessions_own_task`; `A_headless_session_changes_nothing`;
  `The_briefing_stays_under_10000_characters` (fifty offered tasks, titles truncated to 80 characters and the list cut
  at 20 with "and N more: deskbar.cmd task list"); `Clearing_an_ask_also_clears_its_windows_glow` (port).
- [ ] **Step 2: Run them to verify they fail.**
- [ ] **Step 3: Implement.** Briefing text is plain sentences for Claude, no em dashes.
- [ ] **Step 4: Run the tests, both projects, the Release build.**
- [ ] **Step 5: Commit** `feat: every Claude session joins or creates a task, and asks through it`

### Task 9: The skill teaches tasks, and pull request B

**Files:**
- Modify: `src/Deskbar.App/ClaudeSkill.cs` (`Text(...)` gains a "Your task" section when tasks are on: join or create,
  keep the status line current, ask through the task, comment with detail in markdown, mark done on a clear finish
  and never close, leave the user's notes alone unless asked), README, manual test script
- Test: `tests/Deskbar.Windows.Tests/ClaudeSkillTests.cs` (`The_skill_explains_tasks_only_while_they_are_on`)

- [ ] **Step 1-4:** failing test, implement, tests and Release build green.
- [ ] **Step 5:** Commit `feat: the Deskbar skill tells sessions how to keep their task`, then the PR steps of Task 5
  (controller live check: open a new Claude session in VS Code, see its task appear, ask from it, answer it).

---

# Pull request C: the board window

### Task 10: The tasks window and comment threads

**Files:**
- Create: `src/Deskbar.App/TasksWindow.xaml`, `src/Deskbar.App/TasksWindow.xaml.cs` (display only),
  `src/Deskbar.App/TasksBoard.cs` (controller), `src/Deskbar.App/MarkdownBlocks.cs` (markdown to WPF `FlowDocument`)
- Modify: `src/Deskbar.App/FloatingBar.TaskList.cs` ("+N" opens the window; Claude lines go to their session),
  `src/Deskbar.App/App.xaml.cs` (construct, `monitor.Ignore(handle)`, bring forward as Quick find does)
- Test: `tests/Deskbar.Windows.Tests/TasksBoardTests.cs`, `tests/Deskbar.Windows.Tests/MarkdownBlocksTests.cs`

**Interfaces:**
- Consumes: the service, `TaskBoard.Pile()`, Task 6 ruling (c), the bar's `BarScreenBounds()`.
- Produces: `TasksBoard(TasksWindow window, TaskService tasks, WorkspaceManager manager, Func<TaskItem, Result> goTo)`
  with `void Open(Rect near)`, `Toggle`, and one method per action: `Go`, `Close`, `Reopen`, `SetStatus`, `Rename`,
  `MoveTo`, `Delete`, `AddNote`, `Comment`; `static FlowDocument MarkdownBlocks.Render(string markdown)` covering
  paragraphs, `**bold**`, `*italic*`, inline and fenced code, links (open through `deskbar.cmd open`'s path), bullet
  and numbered lists; anything else shown as plain text.

- [ ] **Step 1: Write the failing tests:** `The_board_groups_open_tasks_by_status` (Waiting on you, Done, In progress,
  Backlog; Closed only under its filter); `Filters_by_status_and_workspace`; `A_line_expands_to_its_thread`;
  `Petre_can_comment_and_it_shows_as_you`; `Close_reopen_and_set_status_from_the_window`; `The_window_opens_on_the_board_not_a_form`
  (no text box focused on open); `Clicking_a_claude_task_goes_to_its_session` (the live waiting session first, else
  the latest); `A_rebuild_waits_while_a_menu_is_open` (the bar's #77 lesson); markdown: one test per construct above
  and `Unknown_markdown_shows_as_text`.
- [ ] **Step 2: Run them to verify they fail.**
- [ ] **Step 3: Implement** to `.superpowers/brainstorm/876-1795339550/content/tasks-window.html`. Placement keeps
  clear of the bar and inside the work area (the pure `PlaceUnder` from commit `183d85e` is the model).
- [ ] **Step 4: Run the tests, both projects, the Release build.**
- [ ] **Step 5: Commit** `feat: the tasks window, every task with its status and its comments`

### Task 11: Pull request C

- [ ] README and manual test script for the window; Release build and suite clean; controller live check (open from
  "+N", expand a thread, comment, close a Done task, click a Claude task and land in its session); push, PR with
  release notes, body ending `*opus5.5*`; base watch.
