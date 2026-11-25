# The Manager Session Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Manager button on the bar opens one Fable background Claude session a day that coordinates the task board through `deskbar.cmd task` commands and nothing else.

**Architecture:** The pure rules (start and resume arguments, the briefing, the day rule, who counts as the manager, the overview text) live in `Deskbar.Core` as `ManagerSession` and `TaskOverview`, each with tests. The Windows side reuses `ClaudeSessions` (run `claude`, find the session in `claude agents --json`, stop it) and `ClaudeTaskActions` (put a Windows Terminal window on a session), with a small `ManagerActions` class driving one click. The manager is recognised everywhere by its folder, `<state dir>\manager`.

**Tech Stack:** C# / .NET 10, WPF, CSharpFunctionalExtensions, System.Reactive, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-25-manager-session-design.md`

## Global Constraints

- Model: `--model fable`. Session name: `Deskbar manager`. Window title: `ClaudeStart.AttachTitle("Deskbar manager")`.
- Folder: `Path.Combine(AppPaths.NewStateDir, "manager")`, so `DESKBAR_APPDATA` moves it too.
- One manager per local calendar day (`DateOnly.FromDateTime(DateTime.Now)`).
- The manager may never close, delete, or set a task's status to done or closed. Refusal text, exact:
  `The manager coordinates; it does not close, delete or finish tasks. Ask the user.`
- A verbatim quote of the user in a manager comment starts with the exact marker `The user says:` (the spec's "Petre
  says:" written for every user of the product, since the text ships to them too).
- Nothing new runs on a timer. The manager is started, resumed or stopped only by a click or `task manager`.
- Every PR that changes `src/` carries a `## Release notes` section for users; no version edits.
- Comments in code carry the reasoning, as the rest of the repo does; C# style per the user's rules (expression bodies,
  immutability, `var`, Result for expected failures).
- Window tests run only through `pwsh scripts/test-hidden.ps1`; Core tests run anywhere.

## Review Focus

1. **First click, untrusted folder.** `claude --bg` fails with "Workspace not trusted": no id is stored, the existing
   trust offer opens a terminal in the manager folder, nothing retries. (Task 8, test `First_start_untrusted_offers_trust_and_stores_nothing`.)
2. **Two clicks during a start.** A second click while the first start is still finding its session starts nothing more.
   (Task 8, test `Second_click_while_starting_starts_nothing`.)
3. **Yesterday's manager is busy.** A new day must not kill a turn in flight: a busy old manager is attached to, and the
   new one starts on a later click once it is idle. (Task 2, test `Next_attaches_yesterdays_busy_manager`.)
4. **Deskbar restarted while the manager runs.** The stored id is listed, so the click attaches; no second session.
   (Task 2, test `Next_attaches_todays_listed_manager`; Task 8 covers the wiring.)
5. **Short and long path forms.** A hook or `--from` may carry the 8.3 form of the folder; the manager is still
   recognised. (Task 2, test `IsManager_matches_short_path_form`.)

---

### Task 1: Measure the CLI flags the manager depends on

No product code. Results go in `docs/superpowers/notes/2026-11-25-manager-session-probes.md`, one line per fact with
the command and what it printed. Every probe session is stopped and removed (`claude stop <short>`, `claude rm <short>`)
within 90 seconds of its start, so the keeper (adoption after 2 minutes) never makes it a task. Use `--model haiku`
except in step 2.

**Files:**
- Create: `docs/superpowers/notes/2026-11-25-manager-session-probes.md`

- [ ] **Step 1:** In a scratch folder already trusted by Claude Code (this worktree), write `probe-brief.md` containing
  `Answer every message with the word PINEAPPLE first.` and run
  `claude --bg --model haiku -n "probe manager" --strict-mcp-config --append-system-prompt-file probe-brief.md --allowedTools "Bash(deskbar.cmd task:*)" --disallowedTools Read Edit Write NotebookEdit Agent WebFetch WebSearch Monitor -- "Run: deskbar.cmd task overview. Then say which tools you have."`
  Record: did it start, did `claude agents --json` list it with `name` "probe manager", did `claude logs <short>` show
  PINEAPPLE, and which permission rule form let the Bash call run without a prompt (try `Bash(deskbar.cmd task:*)`,
  then the full shim path form if the short one prompts). `task overview` does not exist yet; an "unknown command"
  reply still proves the call ran.
- [ ] **Step 2:** Same start with `--model fable` and no Bash ask (one short turn): record the model `claude logs`
  reports.
- [ ] **Step 3:** `claude stop <short>` then `claude --bg --resume <full id> --model haiku --append-system-prompt-file probe-brief.md -- "Say hello."`
  and the same resume without the two flags: record whether PINEAPPLE and the model survive each form.
- [ ] **Step 4:** Commit the notes file.

```bash
git add docs/superpowers/notes/2026-11-25-manager-session-probes.md
git commit -m "docs: probes for the manager session's claude flags"
```

The findings decide Task 2's `StartArguments` and `ResumeArguments` (which flags, which allowed-tools rule form, whether
resume repeats the flags). If `--model fable` is refused, stop and tell the user.

### Task 2: `ManagerSession`, the pure rules

**Files:**
- Create: `src/Deskbar.Core/Tasks/ManagerSession.cs`
- Test: `tests/Deskbar.Core.Tests/Tasks/ManagerSessionTests.cs`

**Interfaces:**
- Consumes: `ClaudeAgent` (`ClaudeAgents.cs:33`), `FolderPath.Same(string?, string?, Func<string,string>)`.
- Produces:
  - `public sealed record ManagerRecord(string SessionId, DateOnly Day);`
  - `public static class ManagerSession` with
    - `const string FolderName = "manager"`, `const string Name = "Deskbar manager"`, `const string BriefingFile = "manager-briefing.md"`, `const string QuoteMarker = "The user says:"`, `const string Refusal = "The manager coordinates; it does not close, delete or finish tasks. Ask the user."`
    - `static string FolderIn(string stateDir)`
    - `static bool IsManager(string? folder, string managerFolder, Func<string, string> longPath)`
    - `static IReadOnlyList<ClaudeAgent> Others(IEnumerable<ClaudeAgent> listed, string managerFolder, Func<string, string> longPath)`
    - `static IReadOnlyList<string> StartArguments(string briefingPath)` and `static IReadOnlyList<string> ResumeArguments(string sessionId, string briefingPath)`
    - `const string FirstMessage` and `static string Briefing(string shim)`
    - `static ManagerStep Next(Maybe<ManagerRecord> stored, DateOnly today, IReadOnlyCollection<ClaudeAgent> listed, Func<string, bool> transcriptExists)`
  - `public abstract record ManagerStep` with `Attach(ClaudeAgent Agent)`, `Resume(string SessionId)`, `StartNew(Maybe<ClaudeAgent> StopFirst)`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] void Next_attaches_todays_listed_manager()          // stored today, listed  -> Attach(that agent)
[Fact] void Next_resumes_todays_unlisted_manager_with_transcript() // -> Resume(id)
[Fact] void Next_starts_new_when_todays_transcript_is_gone()       // -> StartNew(None)
[Fact] void Next_starts_new_and_stops_yesterdays_idle_manager()    // stored yesterday, listed idle -> StartNew(Some(agent))
[Fact] void Next_attaches_yesterdays_busy_manager()                // stored yesterday, listed Busy -> Attach(agent)
[Fact] void Next_starts_new_when_nothing_stored()                  // -> StartNew(None)
[Fact] void IsManager_matches_short_path_form()   // longPath maps @"C:\Users\PETRE~1.CHI\x" to the long form; Same is true
[Fact] void IsManager_is_false_for_a_subfolder_and_for_null()
[Fact] void Others_drops_only_sessions_in_the_manager_folder()
[Fact] void StartArguments_run_fable_in_the_background_with_the_briefing()
    // Assert.Equal("--bg", args[0]); Assert.Contains("fable", args after "--model"); Assert.Contains("--strict-mcp-config", args);
    // the briefing path follows "--append-system-prompt-file"; the last argument is FirstMessage
[Fact] void Briefing_names_the_refused_verbs_and_the_quote_marker()
    // Contains "task overview", "task show", "The user says:", "close", "delete", and "never" before "Monitor"
```

- [ ] **Step 2:** Run `dotnet test tests/Deskbar.Core.Tests --filter ManagerSessionTests`. Expected: build fails, `ManagerSession` not defined.
- [ ] **Step 3: Implement `ManagerSession`.** `Next` in this order: nothing stored, so StartNew(None); stored today and
  listed, so Attach; stored today, not listed, transcript there, so Resume; stored today, gone, so StartNew(None);
  stored earlier and listed busy, so Attach; stored earlier and listed idle, so StartNew(Some(agent)); stored earlier,
  not listed, so StartNew(None). Arguments use the flag forms Task 1 measured. `Briefing(shim)` is the spec's "Where it
  runs and what it is told" list, written as short imperative lines, under about 40 lines, naming `{shim} task
  overview`, `{shim} task show <id>`, `{shim} task comment <id> -` (text on stdin), the refused verbs, the quote marker
  rule (quote the user's words unchanged after the marker, never paraphrase into it), and "no Monitor, loop, scheduled
  wakeup, subagent, file reading or transcript reading; replies of a few lines". `FirstMessage`: `What needs me now,
  and what is stuck? Run the overview once and answer in a few lines.`
- [ ] **Step 4:** Run the same filter. Expected: all pass.
- [ ] **Step 5: Commit** `feat: the manager session's rules in Core`.

### Task 3: Store today's manager

**Files:**
- Modify: `src/Deskbar.Core/Persistence/AppState.cs` (next to `ProReminderSince`, about line 465)
- Modify: `src/Deskbar.Core/WorkspaceManager.cs` (next to `StampProReminderStart`, about line 2172)
- Test: `tests/Deskbar.Core.Tests/ManagerStateTests.cs` (modelled on `ProReminderStateTests.cs`)

**Interfaces:**
- Produces: `AppState.Manager : ManagerRecord?` (init, default null), `WorkspaceManager.SetManagerSession(ManagerRecord record) : void`.

- [ ] **Step 1:** Tests: `Manager_round_trips_through_state_json` (save and load keep id and day) and
  `Old_state_without_manager_loads_null`.
- [ ] **Step 2:** Run `dotnet test tests/Deskbar.Core.Tests --filter ManagerStateTests`. Expected: FAIL, no `Manager`.
- [ ] **Step 3:** Add the property and setter (`Persist(State with { Manager = record })`).
- [ ] **Step 4:** Run again. Expected: PASS.
- [ ] **Step 5: Commit** `feat: state remembers today's manager session`.

### Task 4: `task overview` and `task show`

**Files:**
- Create: `src/Deskbar.Core/Tasks/TaskOverview.cs`
- Modify: `src/Deskbar.Core/Control/RemoteControl.Tasks.cs` (the switch at about line 92; header list lines 12-31; `TaskSessionCommands` at 442)
- Modify: `src/Deskbar.Core/Control/ControlUsage.cs` (Tasks section)
- Test: `tests/Deskbar.Core.Tests/Tasks/TaskOverviewTests.cs`, `tests/Deskbar.Core.Tests/RemoteControlTaskTests.cs` (`Task_is_in_the_help` at about 722)

**Interfaces:**
- Produces:
  - `TaskOverview.Text(TaskBoard board, DateTimeOffset now, Func<Guid, string> workspaceName, Func<string, Maybe<bool>> busy) : string`
  - `TaskSessionCommands.Busy : Func<string, Maybe<bool>>` (init, default `_ => Maybe<bool>.None`): a session's busy flag from the latest `claude agents` list.
  - Pipe: `task overview`, `task show <id>`.

- [ ] **Step 1: Failing tests** in `TaskOverviewTests`:

```csharp
[Fact] void One_line_per_open_task_in_pile_order()
[Fact] void Line_shows_id8_status_flags_workspace_title_statusline_question_and_sessions()
    // e.g. "bbf9a6ed  waiting      !*  Deskbar: Manager session | Design written | asks: Approve the design? | claude: idle"
[Fact] void Title_is_cut_to_60_characters_with_an_ellipsis()
[Fact] void Subtasks_are_indented_two_spaces_under_their_parent()
[Fact] void Snoozed_tasks_are_counted_on_one_closing_line()   // "+ 3 snoozed"
[Fact] void Sessions_read_none_working_idle_or_ended()       // no sessions: "none"; live and busy: "working"; live not busy: "idle"; none live: "ended"
```

  and in `RemoteControlTaskTests`: `Overview_prints_the_text`, `Show_prints_one_task_as_list_describes_it`,
  `Show_unknown_id_fails`; extend `Task_is_in_the_help` with the two new lines in their place.
- [ ] **Step 2:** Run `dotnet test tests/Deskbar.Core.Tests --filter "TaskOverviewTests|RemoteControlTaskTests"`. Expected: FAIL.
- [ ] **Step 3:** Implement `TaskOverview.Text`; add `["overview"] => Said(TaskOverview.Text(...))` and
  `["show", var id] => service.Board.Find(id).Match(t => Success(Listed(t)), ControlReply.Fail)` to the switch; add both
  to `ControlUsage` ("one line per open task, for a quick look" and "one task in full, its thread included").
- [ ] **Step 4:** Run again. Expected: PASS.
- [ ] **Step 5: Commit** `feat: task overview and task show`.

### Task 5: The manager's comments and refusals

**Files:**
- Modify: `src/Deskbar.Core/Tasks/TaskItem.cs` (`TaskComment`, about line 51)
- Modify: `src/Deskbar.Core/Tasks/TaskService.cs` (`Comment`, about line 194)
- Modify: `src/Deskbar.Core/Tasks/SessionBriefing.cs` (`Commented`, line 56)
- Modify: `src/Deskbar.Core/Control/RemoteControl.Tasks.cs` (comment forms at 115-117, the refused verbs, `by` at 250/258)
- Modify: `src/Deskbar.App/CommentWaker.cs:66`, `src/Deskbar.App/TasksBoard.cs:940`
- Test: `tests/Deskbar.Core.Tests/RemoteControlTaskTests.cs`, `tests/Deskbar.Core.Tests/Tasks/SessionBriefingTests.cs`

**Interfaces:**
- Consumes: `ManagerSession.IsManager`, `ManagerSession.Refusal`, `ManagerSession.QuoteMarker`.
- Produces:
  - `TaskComment.ManagerAuthor = "manager"`, `TaskComment.ByManager`; `BySession` becomes false for it.
  - `TaskSessionCommands.ManagerFolder : string` (init, default `""`, meaning no manager).
  - `SessionBriefing.Commented(TaskItem task, string expandedMarkdown, bool byManager = false)`.
  - Thread author shown as `manager` (pipe) and `Manager` (panel).

- [ ] **Step 1: Failing tests:**

```csharp
[Fact] void Manager_comment_is_stored_as_the_managers_and_wakes()
    // args: task comment <id> <md> --from-claude --from <manager folder>; thread "by" == "manager"; service.Commented fired once
[Fact] void A_sessions_comment_from_elsewhere_still_wakes_no_one()   // --from-claude, other folder: by "you", not announced (unchanged)
[Theory] [InlineData("close")] [InlineData("delete")] void Manager_may_not_close_or_delete(string verb)   // Fail(ManagerSession.Refusal), task unchanged
[Theory] [InlineData("done")] [InlineData("closed")] void Manager_may_not_finish_a_task(string status)
[Fact] void The_user_may_still_close_from_elsewhere()
[Fact] void Commented_by_manager_says_only_the_quoted_words_are_the_users()
    // SessionBriefing.Commented(task, md, byManager: true) contains "the manager", "The user says:", "task ask"
```

- [ ] **Step 2:** Run `dotnet test tests/Deskbar.Core.Tests --filter "RemoteControlTaskTests|SessionBriefingTests"`. Expected: FAIL.
- [ ] **Step 3:** Implement. In `Task(...)`, compute `var byManager = ManagerSession.IsManager(Folder(from), taskSessions?.ManagerFolder ?? "", LongPath)`
  once (an empty manager folder matches nothing); refuse the four verbs first when true; route the `FromClaude` comment
  form to `service.Comment(t.Id, TaskComment.ManagerAuthor, markdown)` when true. In `TaskService.Comment`, announce when
  the author is none or `ManagerAuthor` (and `wakes`). `CommentWaker` passes `comment.ByManager`. The manager wake text:
  it is the manager's comment; only the words after `The user says:` are the user's, to be taken as the user's own
  comment; everything else is a coordinator's request, routine parts done, anything needing the user's approval (a
  merge, anything destructive, a design choice) asked with `task ask`.
- [ ] **Step 4:** Run again. Expected: PASS. Then the whole Core suite: `dotnet test tests/Deskbar.Core.Tests`.
- [ ] **Step 5: Commit** `feat: the manager's comments wake sessions and it cannot close tasks`.

### Task 6: Hooks leave the manager out of tasks

**Files:**
- Modify: `src/Deskbar.Core/Control/RemoteControl.ClaudeHook.cs` (`TaskEvent` about 93, `SessionTask` about 228)
- Modify: `src/Deskbar.Core/Tasks/SessionBriefing.cs` (new `Manager`)
- Test: `tests/Deskbar.Core.Tests/RemoteControlClaudeHookTests.cs`

**Interfaces:**
- Produces: `SessionBriefing.Manager(string sessionId) : string`, one line: Deskbar's manager session, it does not join or
  create a task for itself, its rules are in its system prompt.

- [ ] **Step 1: Failing tests:**

```csharp
[Fact] void Manager_session_start_gets_the_manager_line_not_the_join_offer()   // no "has no task yet"
[Fact] void Manager_prompt_gets_nothing()                                       // Quiet reply, no tokens per prompt
[Fact] void Manager_session_is_not_marked_attended()                            // service.WasAttended(id) false after both events
[Fact] void Manager_session_cannot_join_or_create_a_task()                     // task join/new --session from the manager folder fail with ManagerSession.Refusal
```

- [ ] **Step 2:** Run `dotnet test tests/Deskbar.Core.Tests --filter RemoteControlClaudeHookTests`. Expected: FAIL.
- [ ] **Step 3:** In `TaskEvent`, before `Attended(...)`, a hook whose folder is the manager's answers SessionStart with
  `SessionBriefing.Manager` and UserPromptSubmit with `Quiet`; SessionEnd as today. In `SessionTask`, `join` and `new`
  from the manager folder fail with the refusal.
- [ ] **Step 4:** Run again. Expected: PASS.
- [ ] **Step 5: Commit** `feat: the hooks never put the manager on a task`.

### Task 7: The keeper leaves the manager alone

**Files:**
- Modify: `src/Deskbar.App/ClaudeSessions.cs` (constructor at about 203; `Heard` at 485 and 500)
- Test: `tests/Deskbar.Windows.Tests/ClaudeSessionsTests.cs`

**Interfaces:**
- Consumes: `ManagerSession.Others`.
- Produces: constructor parameter `string? managerFolder = null` (none, nothing is left out).

- [ ] **Step 1: Failing tests:** `Manager_session_is_never_adopted` (listed with cwd = manager folder, attended, past
  `AdoptAfter`: no new task) and `Manager_session_is_never_stopped` (as an idle session of a Done task would be: no
  `claude stop`).
- [ ] **Step 2:** Run `pwsh scripts/test-hidden.ps1` with the filter `ClaudeSessionsTests`. Expected: FAIL.
- [ ] **Step 3:** Pass `ManagerSession.Others(listed, managerFolder, longPath)` to `SessionKeeper.Plan` and
  `SessionKeeper.Stops`; `live.OnNext` keeps the full list, since the manager's click reads it.
- [ ] **Step 4:** Run again. Expected: PASS.
- [ ] **Step 5: Commit** `feat: the keeper neither adopts nor stops the manager`.

### Task 8: One click opens the manager

**Files:**
- Create: `src/Deskbar.App/ManagerActions.cs`
- Modify: `src/Deskbar.App/ClaudeSessions.cs` (public `RunAndFind`, `StopSession`; the private `Find` reused)
- Modify: `src/Deskbar.App/ClaudeTaskActions.cs` (`Attach` at 213 split; `Launch` at 271 takes a workspace; the trust offer made callable without a task)
- Modify: `src/Deskbar.App/App.xaml.cs` (build `ManagerActions`; `TaskSessionCommands` at 1661 gets `ManagerFolder`, `Manager`, `Busy`; `ClaudeSessions` gets the folder)
- Modify: `src/Deskbar.Core/Control/RemoteControl.Tasks.cs`, `src/Deskbar.Core/Control/ControlUsage.cs` (`task manager`)
- Test: `tests/Deskbar.Windows.Tests/ManagerActionsTests.cs` (fakes as in `ClaudeTaskActionsTests.cs`), `tests/Deskbar.Core.Tests/RemoteControlTaskTests.cs`

**Interfaces:**
- Consumes: `ManagerSession.Next`, `StartArguments`, `ResumeArguments`, `Briefing`; `WorkspaceManager.SetManagerSession`; `AppState.Manager`.
- Produces:
  - `ClaudeSessions.RunAndFind(IReadOnlyList<string> args, string folder) : Task<Result<ClaudeAgent>>` (runs, reads the short id, finds it, no claim, no join).
  - `ClaudeSessions.StopSession(ClaudeAgent agent) : void`.
  - `ClaudeTaskActions.AttachTo(string title, Maybe<Guid> workspace, ClaudeAgent agent, Action<string> failed) : void`.
  - `ManagerActions.Open() : Result` (UI thread; work continues asynchronously; failures go to `seams.Tell`).
  - `TaskSessionCommands.Manager : Func<Result>` (init, default fails "not available"); pipe `task manager`.

- [ ] **Step 1: Failing tests:**

```csharp
[Fact] void First_click_starts_fable_in_the_manager_folder_and_stores_the_day()   // fake cli saw StartArguments; folder created; briefing file written; state has id + today
[Fact] void Click_on_todays_listed_manager_attaches_without_starting()
[Fact] void Click_on_todays_ended_manager_resumes_it()                             // fake cli saw ResumeArguments
[Fact] void Next_day_click_stops_the_idle_old_manager_then_starts()
[Fact] void First_start_untrusted_offers_trust_and_stores_nothing()
[Fact] void Second_click_while_starting_starts_nothing()
[Fact] void Briefing_file_is_rewritten_on_every_start_and_resume()
```

  and `Manager_verb_calls_the_manager_command` in `RemoteControlTaskTests` plus its `Task_is_in_the_help` line.
- [ ] **Step 2:** Run the Windows filter `ManagerActionsTests` through `pwsh scripts/test-hidden.ps1` and the Core filter. Expected: FAIL.
- [ ] **Step 3:** Implement. `Open`: read the latest list (`ClaudeSessions.Live`'s last value), call `ManagerSession.Next`,
  then Attach / Resume-then-attach / (stop, then) start-then-attach. A pending flag covers the time from click to
  attach. Before every start or resume, create the folder and write `ManagerSession.Briefing(shim)` to
  `manager-briefing.md` there. After a start, `SetManagerSession(new ManagerRecord(id, today))`. The window opens on the
  workspace in front (`Maybe<Guid>.None`), title `ClaudeStart.AttachTitle(ManagerSession.Name)`.
- [ ] **Step 4:** Run again. Expected: PASS.
- [ ] **Step 5: Commit** `feat: task manager opens today's manager session`.

### Task 9: The Manager button on the bar

**Files:**
- Modify: `src/Deskbar.App/FloatingBar.TaskList.cs` (chip row grid at about 681; `ApplyMoreText` at about 1131; init properties near `ToggleTasksPanel` at 184)
- Modify: `src/Deskbar.App/App.xaml.cs` (near 1940, where `ToggleTasksPanel` is set)
- Test: `tests/Deskbar.Windows.Tests/FloatingBarTaskListTests.cs`

**Interfaces:**
- Consumes: `ManagerActions.Open`.
- Produces: `FloatingBar.OpenManager : Action?` (init); a button with automation name `Manager`, text `Manager`, tooltip
  `Open the manager: one Claude session that coordinates your tasks`.

- [ ] **Step 1: Failing tests:** `Manager_button_shows_when_wired_and_opens_the_manager` (click invokes the action
  once), `No_manager_button_without_the_action`, `All_tasks_still_shortens_with_the_manager_button_beside_it`
  (`ApplyMoreText` counts the new button's width).
- [ ] **Step 2:** Run the filter `FloatingBarTaskListTests` through `pwsh scripts/test-hidden.ps1`. Expected: FAIL.
- [ ] **Step 3:** Add a third Auto column to `task-add-row`, after "All tasks". The button is built with `TaskButton(...,
  ignoredWhen: TaskClickIgnored)` and styled like `taskMoreButton` (dim ink, same padding and margin), collapsed when
  `OpenManager` is null. Subtract its width in `ApplyMoreText`.
- [ ] **Step 4:** Run again. Expected: PASS.
- [ ] **Step 5: Commit** `feat: a Manager button on the bar`.

### Task 10: Docs, full check, PR

**Files:**
- Modify: `README.md` (the tasks section: the Manager button, one paragraph)
- Modify: `docs/superpowers/notes/manual-test-script.md` (new section "The manager session", steps MG1-MG6)

- [ ] **Step 1:** README paragraph; manual steps: MG1 first click with the trust prompt, then a second click starts it
  and the overview answers; MG2 a second click brings the window forward; MG3 close the window, click, it attaches
  again; MG4 next day (or edit the stored day) a click stops the old and starts a new one; MG5 ask the manager to pass
  "The user says: yes" to a waiting task, and that session wakes and settles its question; MG6 ask it to close a task,
  and it says it cannot.
- [ ] **Step 2:** `dotnet build Deskbar.sln -c Release -warnaserror`. Expected: 0 warnings, 0 errors.
- [ ] **Step 3:** `pwsh scripts/test-hidden.ps1`. Expected: all green (clear `CLAUDE_JOB_DIR` first in a background
  session, or the two `ClaudeSkillTests` fail for that reason alone).
- [ ] **Step 4:** Commit, push the branch, open the PR with a `## Release notes` section (the Manager button, what it
  does, that it runs on Fable and only touches the task board), watch CI to green.
