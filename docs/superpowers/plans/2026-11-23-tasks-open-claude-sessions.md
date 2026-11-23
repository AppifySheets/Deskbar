# Tasks Open Claude Sessions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A task is the handle on its Claude session: Deskbar starts sessions from tasks in the background, attaches a
Windows Terminal window to them on a click, keeps every open task's session running across reboots and crashes, and
gives every live session a task.

**Architecture:** Claude Code's own background sessions (`claude --bg`, `attach`, `--bg --resume`, `agents --json`)
do the running; Deskbar only reads `claude agents --json` on a timer and issues those four commands. The decisions
(who is live, what to resume, which folder, the first message, a cut-off turn) are pure functions in
`Deskbar.Core.Tasks` and unit-tested; the process calls live in `Deskbar.Windows/Claude`; the App wires a poll, the
menus and the thread.

**Tech Stack:** C# / .NET 10, WPF, CSharpFunctionalExtensions (`Result`, `Maybe`), System.Reactive, xunit 2.9 with
plain `Assert`.

**Spec:** `docs/superpowers/specs/2026-11-23-tasks-open-claude-sessions-design.md`. Measurements behind it are comments
on Deskbar task 8f70d8ea.

## Global Constraints

- Work in this worktree (`.claude/worktrees/task-starts-claude`, branch `design/task-starts-claude`); one PR per
  task or a short stack, rebase-and-merge, never push to main, never merge.
- Routine tests: `dotnet test Deskbar.sln --filter "Category!=Integration"`. `Deskbar.Windows.Tests` runs only on a
  hidden desktop: `pwsh scripts/test-hidden.ps1`. No test starts `claude`, `wt` or a terminal.
- Before every PR: `dotnet build Deskbar.sln -c Release -warnaserror`.
- C# style per CLAUDE.md: expression bodies, `var`, immutable records, `Result`/`Maybe` for expected failures,
  Rx over events, long intent comments.
- The four CLI calls Deskbar depends on, and no others: `claude --bg <message>`, `claude attach <short id>`,
  `claude --bg --resume <session id> [message]`, `claude agents --json`.
- An unreadable or unknown `claude agents --json` means "cannot tell": nothing is ended, nothing is resumed.
- Every process Deskbar starts for this runs without `CLAUDECODE`, `CLAUDE_CODE_ENTRYPOINT`,
  `CLAUDE_CODE_SESSION_ATTENDED` and `ELECTRON_RUN_AS_NODE` in its environment.
- Terminal look "Dark Modern": scheme background `#1F1F1F`, foreground `#CCCCCC`, cursor `#D97757`, selection
  `#264F78`, the ANSI colours of the trial fragment's "Deskbar Dark Modern" scheme; font JetBrains Mono 10 pt, line
  height 1.15, padding `14, 10, 14, 10`, bar cursor, no scrollbar, cleartype, `suppressApplicationTitle: true`.
- The first message, exactly (title and status line as stored, the whole cut to 1000 characters):
  `Deskbar task <id>: <title>\n<status line>\nJoin this task (deskbar task join <id>) and start on it.` with the
  status line row left out when empty. `<id>` is the task's full Guid.
- "continue" is sent as the message `continue`.
- Every pipe verb added appears in `ControlUsage` (a test fails otherwise); README and the manual test script are
  updated in the task that adds the behaviour.

## Review Focus

1. A background session's SessionEnd arrives when its turn finishes, while `claude agents` still lists it. Expect:
   the task keeps showing it live and Deskbar does not resume it.
2. The first poll after turning the feature on (or after a reboot) finds many open tasks with dead sessions. Expect:
   resumes go one at a time, five seconds apart, never two for one session, and the bar stays responsive.
3. Within 30 seconds of a resume, `claude agents` may report the woken session `busy` though it is idle (measured).
   Expect: that session is neither resumed again nor shown as working until the grace has passed.
4. A folder that was never trusted, or `claude`/`wt` missing from the PATH. Expect: one clear message in the task's
   thread, no retry loop, nothing else in Deskbar affected.
5. Petre closes a task while its resume is queued. Expect: the queued resume is dropped.

Each item has its test in the task that owns it (Tasks 5, 6, 6, 8, 6).

---

### Task 1: Probe the unmeasured behaviours

Measure before building (spec, Constraints). Use a throwaway folder that has been trusted once and a hidden or
off-screen window where possible; stop and `claude rm` every probe session afterwards.

**Files:**
- Create: `docs/superpowers/notes/2026-11-23-claude-background-probes.md`

- [ ] **Step 1:** Close a real Windows Terminal window (title-bar X) on `claude attach <id>`; record whether the
  session is still in `claude agents --json` and keeps working.
- [ ] **Step 2:** Attach from a second window while one is attached; record what each window shows.
- [ ] **Step 3:** Record `CLAUDE_CODE_SESSION_ATTENDED` and `CLAUDE_CODE_ENTRYPOINT` as a background session's hooks
  see them (a SessionStart hook in a scratch project's `.claude/settings.json` that writes them to a file). If the
  pair makes `ClaudeHookEvent.IsHeadless` true, Task 8's own join is the only way a started session reaches its task;
  note it.
- [ ] **Step 4:** Produce a cut-off turn: start a background session on a long reply, kill its session `claude.exe`
  (not `claude stop`) mid-generation, and save the transcript's last 15 lines (types, roles, `stop_reason`) in the
  note. Confirm Task 4's rule against them, and amend Task 4's test data if they differ.
- [ ] **Step 5:** Record whether `wt -w new --title "X" ...` with `suppressApplicationTitle` keeps the title "X" on
  the window, and whether `"face": "JetBrains Mono, Cascadia Mono"` falls back when the first font is missing.
- [ ] **Step 6:** Record `claude --bg` stdout for an untrusted folder and for a missing `claude` (exit codes too).
- [ ] **Step 7:** Commit the note: `git commit -m "docs: probes for Claude background sessions"`.

### Task 2: Read `claude agents --json`

**Files:**
- Create: `src/Deskbar.Core/Tasks/ClaudeAgents.cs`
- Test: `tests/Deskbar.Core.Tests/Tasks/ClaudeAgentsTests.cs`

**Interfaces:**
- Produces: `public sealed record ClaudeAgent(string SessionId, Maybe<string> ShortId, string Folder, bool Background, bool Busy, string Name)`;
  `public static class ClaudeAgents { public static Result<IReadOnlyList<ClaudeAgent>> Parse(string json); public static Maybe<string> ShortIdFromStart(string stdout); }`

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void An_interactive_and_a_background_session_are_read()
{
    var json = """[{"pid":1,"cwd":"E:\\repos\\X","kind":"interactive","sessionId":"aaaa","name":"A","status":"idle"},
                   {"pid":2,"id":"884c127f","cwd":"E:\\repos\\X","kind":"background","sessionId":"884c127f-96","name":"B","status":"busy","state":"working"}]""";
    var agents = ClaudeAgents.Parse(json).Value;
    Assert.Equal([false, true], agents.Select(a => a.Background));
    Assert.Equal("884c127f", agents[1].ShortId.Value);
    Assert.True(agents[1].Busy);
    Assert.Equal(@"E:\repos\X", agents[0].Folder);
}
[Fact] public void A_background_session_that_is_done_is_still_listed() // state "done", status "idle"
[Fact] public void Anything_but_an_array_of_objects_with_a_session_id_is_a_failure() // "{}", "", "[1]", "[{}]"
[Fact] public void Unknown_fields_are_ignored()
[Fact] public void The_short_id_is_read_from_the_start_output()
{
    Assert.Equal("6785e6b5", ClaudeAgents.ShortIdFromStart("Starting background service…\nbackgrounded · 6785e6b5 · deskbar probe\n").Value);
    Assert.True(ClaudeAgents.ShortIdFromStart("Workspace not trusted. Run `claude` in X once").HasNoValue);
}
```

- [ ] **Step 2:** Run `dotnet test tests/Deskbar.Core.Tests --filter ClaudeAgentsTests`; expect build failure.
- [ ] **Step 3:** Implement with `System.Text.Json` (`JsonDocument`); `Busy` is `status == "busy"`; a row without
  `sessionId` fails the whole parse (unknown shape). `ShortIdFromStart` matches `backgrounded · ([0-9a-f]{8})`.
- [ ] **Step 4:** Run the tests; expect PASS.
- [ ] **Step 5:** Commit `feat: read claude agents --json`.

### Task 3: Run the Claude CLI and capture its output

**Files:**
- Create: `src/Deskbar.Core/Tasks/IClaudeCli.cs`, `src/Deskbar.Windows/Claude/ClaudeCli.cs`
- Test: `tests/Deskbar.Windows.Tests/ClaudeCliTests.cs`

**Interfaces:**
- Produces: `public interface IClaudeCli { Task<Result<string>> Run(IReadOnlyList<string> arguments, string? workingDirectory, TimeSpan timeout); }`
  (stdout on exit code 0; on any other exit, a failure carrying stdout and stderr). `public sealed class ClaudeCli(string executable = "claude") : IClaudeCli`.

- [ ] **Step 1: Write the failing tests**, run against `cmd.exe` as the executable so no Claude starts:
  `Output_of_a_successful_run_is_returned` (`/c echo hi` gives `"hi"` trimmed),
  `A_non_zero_exit_is_a_failure_with_the_output` (`/c echo nope & exit 3`),
  `A_run_past_its_timeout_is_killed_and_fails` (`/c ping -n 30 127.0.0.1`, 1 s),
  `The_Claude_and_Electron_variables_are_not_passed_on` (set `CLAUDECODE=1` in the test process, run `/c set CLAUDE`,
  assert the output lacks `CLAUDECODE`),
  `A_missing_executable_is_a_failure_not_an_exception`.
- [ ] **Step 2:** Run them; expect build failure.
- [ ] **Step 3:** Implement with `Process` (`UseShellExecute=false`, both streams redirected, `CreateNoWindow`), the
  four variables of Global Constraints removed from `StartInfo.Environment`, `WaitForExitAsync` with the timeout, and
  `Kill(entireProcessTree: true)` on timeout. Comment why this is not `ProgramStarter`: it needs stdout, and
  `claude --bg` hands the session to its own daemon, so nothing started here outlives the call. In the packaged build
  this is a child of the package; record that as checked in Task 1's note if it can be, otherwise leave a
  `PackageIdentity.IsPackaged` comment naming the store packaging spec.
- [ ] **Step 4:** `pwsh scripts/test-hidden.ps1 -Filter ClaudeCliTests`; expect PASS.
- [ ] **Step 5:** Commit `feat: run the Claude CLI and read its output`.

### Task 4: Transcripts: where they are, expired, cut off

**Files:**
- Create: `src/Deskbar.Core/Tasks/ClaudeTranscript.cs`
- Test: `tests/Deskbar.Core.Tests/Tasks/ClaudeTranscriptTests.cs`

**Interfaces:**
- Produces: `public static class ClaudeTranscript { public static string PathOf(string claudeHome, string folder, string sessionId); public static bool WasCutOff(IEnumerable<string> jsonLines); }`

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void The_path_encodes_every_character_but_letters_and_digits_as_a_dash()
{
    Assert.Equal(@"C:\h\projects\E--repos-TaskSpaces\6785e6b5.jsonl", ClaudeTranscript.PathOf(@"C:\h", @"E:\repos\TaskSpaces", "6785e6b5"));
    Assert.EndsWith(@"C--Users-petre-chitashvili-AppData-Local-Temp-claude-hl-probe\x.jsonl",
        ClaudeTranscript.PathOf(@"C:\h", @"C:\Users\petre.chitashvili\AppData\Local\Temp\claude\hl-probe", "x"));
}
[Fact] public void A_turn_ending_in_an_assistant_end_turn_is_finished() // assistant{stop_reason:end_turn}, then system/turn_duration, cost-state lines
[Fact] public void A_tool_use_without_its_result_is_cut_off()      // last message line: assistant{stop_reason:tool_use}
[Fact] public void A_tool_result_with_no_reply_after_it_is_cut_off() // last message line: user{tool_result}
[Fact] public void A_prompt_with_no_reply_is_cut_off()              // last message line: user{text}
[Fact] public void Metadata_lines_after_the_reply_do_not_count()    // ai-title, agent-name, last-prompt after end_turn: finished
[Fact] public void An_empty_or_unreadable_transcript_is_not_cut_off()
```

- [ ] **Step 2:** Run; expect build failure.
- [ ] **Step 3:** Implement: the last line whose `type` is `user` or `assistant` decides; cut off unless it is
  `assistant` with `message.stop_reason == "end_turn"`. Unparseable lines are skipped. Comment with the measured
  sample from task 8f70d8ea (and Task 1's).
- [ ] **Step 4:** Run; expect PASS.
- [ ] **Step 5:** Commit `feat: find a Claude transcript and tell a cut-off turn`.

### Task 5: The board follows the live list

**Files:**
- Modify: `src/Deskbar.Core/Tasks/TaskBoard.cs`, `src/Deskbar.Core/Tasks/TaskService.cs` (the SessionEnd hook in
  `src/Deskbar.Core/Control/RemoteControl.ClaudeHook.cs:119` stays as it is; `Reconcile` corrects it)
- Test: `tests/Deskbar.Core.Tests/Tasks/TaskBoardTests.cs`, `TaskServiceTests.cs`, `RemoteControlClaudeHookTests.cs`

**Interfaces:**
- Consumes: `ClaudeAgent` (Task 2).
- Produces: `TaskBoard Reconcile(IReadOnlyCollection<ClaudeAgent> live, DateTimeOffset now)`;
  `Result<(TaskBoard Board, TaskItem Task)> AdoptSession(ClaudeAgent agent, Maybe<Guid> workspace, DateTimeOffset now, Guid newId)`;
  on `TaskService`: `void Reconcile(IReadOnlyCollection<ClaudeAgent> live)`, `Result<TaskItem> Adopt(ClaudeAgent agent, Maybe<Guid> workspace)`.

- [ ] **Step 1: Write the failing tests** in `TaskBoardTests`:
  `Reconcile_ends_a_live_session_missing_from_the_list`,
  `Reconcile_revives_an_ended_session_the_list_still_shows` (the latest entry for that id gets `Ended = None`),
  `Reconcile_leaves_tasks_without_sessions_alone`,
  `Reconcile_does_not_change_UpdatedAt_when_nothing_changed` (no save storm every poll),
  `Adopting_a_session_makes_an_in_progress_Claude_task_titled_by_its_name`,
  `Adopting_a_session_no_name_gives_falls_back_to_the_folder_name`.
  In `RemoteControlClaudeHookTests`: `SessionEnd_of_a_background_session_still_listed_keeps_it_live` (Review Focus 1:
  the hook still ends it, and the next `Reconcile` with the session listed revives it).
- [ ] **Step 2:** Run; expect failures.
- [ ] **Step 3:** Implement on `TaskBoard` (pure) and `TaskService` (commit only when the board changed, so
  `Changed` does not pulse on every poll).
- [ ] **Step 4:** Run; expect PASS.
- [ ] **Step 5:** Commit `feat: the task board follows Claude's own list of live sessions`.

### Task 6: The keeper decides what to resume

**Files:**
- Create: `src/Deskbar.Core/Tasks/SessionKeeper.cs`
- Test: `tests/Deskbar.Core.Tests/Tasks/SessionKeeperTests.cs`

**Interfaces:**
- Consumes: `TaskBoard`, `ClaudeAgent`.
- Produces:
  - `public sealed record ResumeLedger(IReadOnlyDictionary<string, IReadOnlyList<DateTimeOffset>> Resumes) { public static ResumeLedger Empty; public ResumeLedger Record(string sessionId, DateTimeOffset at); }`
  - `public abstract record KeeperStep { sealed record Resume(Guid TaskId, TaskSession Session, bool SendContinue); sealed record GiveUp(Guid TaskId, string Reason); sealed record Adopt(ClaudeAgent Agent); }`
  - `public static IReadOnlyList<KeeperStep> SessionKeeper.Plan(TaskBoard board, IReadOnlyCollection<ClaudeAgent> live, ResumeLedger ledger, Func<TaskSession, bool> transcriptExists, Func<TaskSession, bool> cutOff, DateTimeOffset now)`
  - Constants: `Grace = 30 s`, `LoopWindow = 10 min`, `MaxResumesInWindow = 3`, `Spacing = 5 s` (used by Task 8).

- [ ] **Step 1: Write the failing tests:**
  `The_latest_session_of_an_open_task_that_is_not_listed_is_resumed` (backlog, in progress, waiting: one test each
  via `[Theory]`),
  `Done_and_closed_tasks_are_never_resumed`,
  `A_listed_session_is_not_resumed_even_when_done` (Review Focus 1),
  `Only_the_latest_session_of_a_task_is_resumed`,
  `A_session_resumed_within_the_grace_is_left_alone` (Review Focus 3),
  `A_cut_off_turn_is_resumed_with_continue`,
  `The_fourth_resume_within_ten_minutes_is_a_give_up_with_the_reason`,
  `An_expired_transcript_is_a_give_up_once_not_a_resume` (a task already waiting with that reason gets no step),
  `A_listed_session_no_task_knows_is_adopted`,
  `A_session_on_two_tasks_is_resumed_once`,
  `Steps_come_in_task_order_waiting_first_so_answers_come_back_first`.
- [ ] **Step 2:** Run; expect build failure.
- [ ] **Step 3:** Implement as a pure function. Comment the rule and Petre's words ("if a session has ended and done
  its work, i will mark the task as done").
- [ ] **Step 4:** Run; expect PASS.
- [ ] **Step 5:** Commit `feat: decide which Claude sessions to resume`.

### Task 7: First message, folder, and the terminal

**Files:**
- Create: `src/Deskbar.Core/Tasks/ClaudeStart.cs`, `src/Deskbar.Windows/Claude/TerminalFragment.cs`
- Test: `tests/Deskbar.Core.Tests/Tasks/ClaudeStartTests.cs`, `tests/Deskbar.Windows.Tests/TerminalFragmentTests.cs`

**Interfaces:**
- Produces:
  - `public static string ClaudeStart.FirstMessage(TaskItem task)` (exact text in Global Constraints).
  - `public static IReadOnlyList<string> ClaudeStart.Folders(TaskItem task, TaskBoard board, IEnumerable<string> editorFolders)`:
    the session folder for a task with sessions; else the distinct folders of every session of every task on the
    task's workspace plus `editorFolders`, latest-used first.
  - `public static string ClaudeStart.RepoOf(string folder)` (the part before `\.claude\worktrees\`).
  - `public static ProgramLaunch ClaudeStart.AttachWindow(string shortId, string folder, string title)`: `wt.exe`
    with `-w new --title <title> -p "Deskbar Claude" -d <folder> claude attach <shortId>`.
  - `public static class TerminalFragment { public static Result Sync(bool on, string localAppData); public const string ProfileName = "Deskbar Claude"; }`:
    writes `Microsoft\Windows Terminal\Fragments\Deskbar\deskbar-claude.json` (one profile, one scheme, values in
    Global Constraints, font face from Task 1 step 5), deletes `Fragments\DeskbarClaudeTry`; `on: false` deletes
    Deskbar's folder.

- [ ] **Step 1: Write the failing tests:** `The_first_message_names_the_task_and_asks_to_join_it`,
  `The_first_message_leaves_out_an_empty_status_line`, `The_first_message_is_cut_to_1000_characters`,
  `A_task_with_sessions_starts_in_its_latest_sessions_folder`,
  `A_task_without_sessions_offers_its_workspaces_folders_latest_first`,
  `A_worktree_folder_maps_to_its_repo`, `The_attach_window_command_is_exact` (assert the whole argument list),
  `The_fragment_has_the_Dark_Modern_values` (parse the written JSON, assert background, cursor, font size 10,
  `suppressApplicationTitle`), `Turning_off_removes_the_fragment_and_the_trial_one`.
- [ ] **Step 2:** Run; expect failures.
- [ ] **Step 3:** Implement. `TerminalFragment` writes through the same durable-write helper state.json uses.
- [ ] **Step 4:** Run (Core with `dotnet test`, Windows with `pwsh scripts/test-hidden.ps1`); expect PASS.
- [ ] **Step 5:** Commit `feat: the first message, the folder and the Deskbar Claude terminal profile`.

### Task 8: The App keeps sessions running and gives each a task

**Files:**
- Create: `src/Deskbar.App/ClaudeSessions.cs`
- Modify: `src/Deskbar.App/App.xaml.cs` (construct and dispose it next to `TaskService`, ~L1346),
  `src/Deskbar.Core/AppState.cs` (`bool NoClaudeResume` + `[JsonIgnore] ClaudeResume`),
  `src/Deskbar.Core/WorkspaceManager.cs` (`Result SetClaudeResume(bool on)`), `ManageWindow.xaml(.cs)` (a checkbox
  under "Claude Code sessions keep their tasks in the task list": "Resume the sessions of open tasks after a restart
  or a crash", and the retention line "Claude Code keeps sessions for 30 days; raise cleanupPeriodDays in
  ~/.claude/settings.json to keep them longer"), `src/Deskbar.Core/Control/RemoteControl.cs` + `ControlUsage.cs` (`claude-resume on|off`).
- Test: `tests/Deskbar.Windows.Tests/ClaudeSessionsTests.cs` (fake `IClaudeCli`, virtual-time scheduler).

**Interfaces:**
- Consumes: Tasks 2 to 7.
- Produces: `sealed class ClaudeSessions(TaskService tasks, WorkspaceManager manager, IClaudeCli cli, IScheduler scheduler, Func<string, Maybe<Guid>> workspaceOfFolder, string claudeHome) : IDisposable`
  with `IObservable<IReadOnlyCollection<ClaudeAgent>> Live` and `Task<Result<string>> Start(TaskItem task, string folder)` (returns the session id).

- [ ] **Step 1: Write the failing tests:**
  `It_polls_every_15_seconds_only_while_Claude_tasks_are_on`,
  `A_failed_or_unknown_read_changes_nothing` (Global Constraints),
  `Resumes_run_one_at_a_time_five_seconds_apart` (Review Focus 2: twenty dead sessions, assert the CLI call times),
  `A_resume_passes_continue_only_for_a_cut_off_turn` (assert the exact argument lists
  `--bg --resume <id>` and `--bg --resume <id> continue`),
  `A_queued_resume_for_a_task_closed_meanwhile_is_dropped` (Review Focus 5),
  `A_failed_resume_comments_once_on_the_task_and_is_not_retried_until_the_next_window` (Review Focus 4),
  `With_resume_off_nothing_is_resumed_but_adoption_and_liveness_still_run`,
  `Start_runs_claude_bg_with_the_first_message_in_the_folder_and_joins_the_task_itself` (fake returns
  `backgrounded · 6785e6b5`, then the next `agents` read shows `6785e6b5-...`; assert `Join` with that full id: the
  join must not depend on the hook, see Task 1 step 3),
  `Start_in_an_untrusted_folder_fails_with_Claude_s_message`.
- [ ] **Step 2:** Run with `pwsh scripts/test-hidden.ps1 -Filter ClaudeSessionsTests`; expect failures.
- [ ] **Step 3:** Implement: `Observable.Interval(15 s, scheduler)` while `ClaudeTasksInEffect`, the CLI call off the
  UI thread, `ObserveOn` the dispatcher, then `tasks.Reconcile`, `SessionKeeper.Plan`, and the steps (`Adopt` via
  `tasks.Adopt` with `workspaceOfFolder`; `GiveUp` via `tasks.Ask`; `Resume` queued and drained one per `Spacing`,
  re-checking the task is open before each). Each resume comments on its task: `Resumed (<reason>) at <HH:mm>`.
  App wires `workspaceOfFolder` with `EditorFolder.Best` over the known editor windows, as `SessionWindow` does
  (App ~L1738), and `TerminalFragment.Sync` on the `ClaudeTasksInEffect` change next to `ClaudeSkill.Sync` (~L1546).
- [ ] **Step 4:** Run; expect PASS. Then the whole suite and the Release build.
- [ ] **Step 5:** Commit `feat: keep open tasks' Claude sessions running and give every session a task`.

### Task 9: Going to a task, starting Claude on it, and the session list

**Files:**
- Modify: `src/Deskbar.App/App.xaml.cs` (`GoToTask`, L897), `FloatingBar.TaskEdit.cs` (`OpenTaskLineMenu`, L60),
  `TasksWindow.xaml.cs` (`OpenMenuFor` L1235, `Thread` L965), `TasksBoard.cs` (`TasksWindowActions` L703,
  `TaskRowView` L641, `Row` L543), `src/Deskbar.Core/Control/RemoteControl.Tasks.cs` + `ControlUsage.cs`.
- Test: `tests/Deskbar.Core.Tests/RemoteControlTaskTests.cs`, `tests/Deskbar.Windows.Tests/TasksBoardTests.cs`,
  `FloatingBarTaskListTests.cs`.

**Interfaces:**
- Consumes: `ClaudeSessions.Live`, `ClaudeSessions.Start`, `ClaudeStart.*`.
- Produces: `record SessionView(string Id, DateTimeOffset Joined, SessionState State)` with
  `enum SessionState { Working, Idle, Ended, Expired }`; `TaskRowView.SessionList : IReadOnlyList<SessionView>`;
  `TasksWindowActions.StartClaude : Action<Guid>` and `OpenSession : Action<Guid, string>`; pipe verbs
  `task go <id>`, `task start <id> [--folder <path>]`, `task open-session <id> <session id>`.

- [ ] **Step 1: Write the failing tests:**
  `Going_to_a_task_with_a_listed_background_session_opens_an_attach_window_on_its_workspace` (fake launcher records
  the `ClaudeStart.AttachWindow` launch), `Going_again_brings_the_open_window_forward_instead`,
  `Going_to_a_task_with_an_ended_session_resumes_then_attaches`,
  `Going_to_a_task_whose_session_is_interactive_goes_to_its_editor_window_as_before`,
  `Going_to_a_note_without_sessions_only_switches_workspace`,
  `Start_Claude_on_this_is_on_both_menus_after_Edit`,
  `Starting_on_a_task_with_a_live_session_asks_first`,
  `Several_folders_offer_a_pick_list_ending_in_Choose_a_folder`,
  `The_thread_lists_sessions_newest_first_with_Open_except_expired`,
  `task_go_start_and_open_session_are_in_ControlUsage_and_dispatch`.
- [ ] **Step 2:** Run; expect failures.
- [ ] **Step 3:** Implement. The attach window is found by its title (`<task title> · Claude`, Task 1 step 5) among
  `manager.KnownWindows` and moved to the task's workspace like any new window; going again activates it. The pick
  list is a `ContextMenu` at the line. Untrusted folder: a thread comment with Claude's message and a menu item
  "Open a terminal there to trust it" (plain `wt -d <folder> claude`, Petre accepts the prompt himself).
- [ ] **Step 4:** Run both suites; expect PASS. Release build with `-warnaserror`.
- [ ] **Step 5:** Commit `feat: a task opens, starts and lists its Claude sessions`.

### Task 10: Docs and the live check

**Files:**
- Modify: `README.md` (`## Tasks` L441 and `### Claude Code` L533), `docs/superpowers/notes/manual-test-script.md`
  (new section after `## Claude sessions and their tasks`, numbered on from the last step)

- [ ] **Step 1:** README: what a click on a task does now, "Start Claude on this", resume after a restart, marking a
  task done to let its session go, the Dark Modern terminal, the 30-day transcript note, the new pipe verbs.
- [ ] **Step 2:** Manual test script section `## Tasks open Claude sessions`: start from a task, close the window and
  see the task stay working, go again, restart Deskbar mid-turn, reboot with three open Claude tasks (one mid-turn),
  mark one done and confirm it is not resumed, an untrusted folder, an expired session.
- [ ] **Step 3:** Name the screencasts to re-film in the PR body (memory: features update docs).
- [ ] **Step 4:** Commit `docs: tasks open Claude sessions`.
