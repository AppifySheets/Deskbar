# A Comment Wakes Its Session (PR 2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A comment Petre posts on a task reaches the task's Claude session and wakes it if it is idle; a session that
is not running is resumed with the comment.

**Architecture:** A third Claude Code hook entry, on Stop, with `"asyncRewake": true`, runs `claude-wait-hook.cmd`. The
script asks Deskbar once (`deskbar.cmd claude-wait`, the hook JSON on stdin) for this session's inbox file and a
generation number, then polls every 2 seconds: when the inbox exists it claims it by renaming, prints it to stderr and
exits 2 (Claude Code wakes the session with it); when a newer waiter took over, or Deskbar says nothing, it exits 0.
Deskbar's side: TaskService announces Petre's comments (Rx), and a `CommentWaker` in the app decides per comment, from
the latest `claude agents --json`, to write the inbox (running), resume with the comment (not running), or stop and
resume (a background session whose waiter lapsed).

**Tech Stack:** .NET 10, WPF, CSharpFunctionalExtensions, System.Reactive, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-24-task-images-design.md` (PR 2 section).

## Measured 2026-11-24 (throwaway background sessions; these settle the spec's open points)

- `asyncRewake` wakes an idle session within 2 s; the text arrives as `Stop hook blocking error from command "Stop": <stderr>`.
- `"timeout": 2592000` (30 days) is accepted; a hook waited past the 600 s default with a set timeout.
- A waiter armed at one Stop still wakes the session after a later turn has come and gone.
- A waiter that exits 2 while the session is busy in a tool call is delivered at the session's next step in that same
  turn (after the running tool returns), never dropped. So no generation bump at turn start is needed.
- Twenty cmd waiters polling a file every 2 s with `ping -n 3 127.0.0.1 >nul` as the sleep used under 1 s of CPU in
  30 s on a 32-core machine. (cmd's `timeout` refuses the redirected stdin a hook has.)
- Not measured: a VS Code session waking. Manual step CW5 measures it; the docs say what happens if it does not.

## Global Constraints

- Only Petre's own new comments wake: `TaskService.Comment` with no session, posted from the panel, or `task comment <id>` run outside a Claude session. Not a session's comment, not a comment another session posts by id (the client sees `CLAUDECODE`), not an edit, settle or Deskbar's own line (`TaskComment.ByDeskbar`).
- One comment goes to one session: the task's newest session with a transcript (`TaskViews` already orders sessions live-first then newest; use the same choice as double-click's Open).
- The waiter never holds the control pipe (it serves one client at a time): one short `claude-wait` call, then files only.
- Inbox folder: `<state dir>\claude-inbox\`; per session `<session id>.md` (the message) and `<session id>.gen` (the newest generation, a number).
- Hook timeout: 2592000 seconds. Poll interval: 2 seconds.
- Headless sessions (`ClaudeHookEvent.Headless`) get no waiter: `claude-wait` answers nothing and the script exits 0.
- Message text (for Claude, no em dashes): `Deskbar: the user commented on your task <id8> "<title>":` then a blank line, the comment's markdown with image paths absolute (`TaskService.ExpandImages`), then, when it has images, `Read each image path above with your Read tool.` For a Done task append the existing Done rule sentence from `SessionBriefing.Prompt`.
- Windows tests run on the hidden desktop: `pwsh scripts/test-hidden.ps1 -Arguments 'tests/Deskbar.Windows.Tests --filter FullyQualifiedName~<Class>'`. Before the PR: `dotnet build Deskbar.sln -c Release -warnaserror`.

## Review Focus

1. Two comments in quick succession arrive together in one wake, neither lost. (Task 5: append to the inbox.)
2. A comment on a task whose only session ended long ago and whose transcript is gone: nothing is resumed and nothing throws; the thread gets Deskbar's line saying why. (Task 5)
3. A session that ends (SessionEnd) leaves no inbox or gen file. (Task 3)
4. Deskbar restarting while waiters wait: they keep polling files and a comment after the restart still wakes them. (Task 4 script test: the script never calls Deskbar again after registering.)
5. A stale waiter (an older generation) that sees the inbox does not claim it. (Task 4 script test.)

---

### Task 1: Who wakes, and with what (Core)

**Files:**
- Create: `src/Deskbar.Core/Tasks/CommentWake.cs`
- Modify: `src/Deskbar.Core/Tasks/SessionBriefing.cs` (add `Commented`)
- Test: `tests/Deskbar.Core.Tests/Tasks/CommentWakeTests.cs`

**Interfaces:**
- Produces:
  - `public abstract record WakePlan { sealed record Inbox(string SessionId); sealed record Resume(TaskSession Session); sealed record StopThenResume(TaskSession Session, string ShortId); sealed record Nothing(string Why); }`
  - `public static WakePlan CommentWake.Plan(TaskItem task, IReadOnlyList<ClaudeAgent> listed, Func<TaskSession, bool> hasTranscript, Func<string, bool> waiterLapsed)`:
    newest session with a transcript (live first, then newest joined); listed and waiter not lapsed: `Inbox`; listed, lapsed, `Background` with a short id: `StopThenResume`; listed, lapsed, not background: `Inbox` (it arrives at its next turn); not listed: `Resume`; no session with a transcript: `Nothing("no session of this task can be resumed")`.
  - `public static string SessionBriefing.Commented(TaskItem task, string expandedMarkdown)` (the Global Constraints' text).

- [ ] **Step 1: Write the failing tests** for each branch of `Plan` (idle listed, lapsed background, lapsed interactive, not listed, no transcript, two sessions picks the live newest) and for `Commented` (title, id prefix, markdown kept whole, the Read sentence only with an image line, the Done sentence only for Done, no em dash).
- [ ] **Step 2: Run** `dotnet test tests/Deskbar.Core.Tests --filter FullyQualifiedName~CommentWakeTests`: FAIL.
- [ ] **Step 3: Implement**, header comment naming the spec and the measured facts above.
- [ ] **Step 4: Run** the same: PASS. **Step 5: Commit** `feat: CommentWake decides how a comment reaches its session`.

### Task 2: Petre's comments are announced, a session's are not (Core)

**Files:**
- Modify: `src/Deskbar.Core/Tasks/TaskService.cs` (`Comment`, new `Commented` observable)
- Modify: `src/Deskbar.Core/Control/RemoteControl.Tasks.cs:108` (`task comment <id> <markdown> [--from-claude]`)
- Modify: `src/Deskbar.Core/Control/ControlArguments.cs` (`WithCaller`: appends `--from-claude` to `task comment <id> ...` when the client's `CLAUDECODE` is set)
- Modify: `src/Deskbar.App/ControlCommandLine.cs:51`, `src/Deskbar.Windows/Control/ControlConsole.cs:37` (call it with `Environment.GetEnvironmentVariable("CLAUDECODE")`)
- Test: `tests/Deskbar.Core.Tests/Tasks/TaskServiceTests.cs`, `RemoteControlTaskTests.cs`, `Control/ControlArgumentsTests.cs` (or wherever WithCallerFolder is tested)

**Interfaces:**
- Produces: `IObservable<(Guid TaskId, TaskComment Comment)> TaskService.Commented`; `Result TaskService.Comment(Guid id, Maybe<string> sessionId, string markdown, bool wakes = true)` (announces only when `sessionId` is none and `wakes`); `static IReadOnlyList<string> ControlArguments.WithCaller(IReadOnlyList<string> args, string? claudeCode)`.

- [ ] **Step 1: Failing tests:** a comment with no session is announced once with its id; a session's comment, a Deskbar line, an edit and `wakes: false` are not; `task comment <id> x --from-claude` stores the comment and announces nothing; `WithCaller(["task","comment","3f2a","-"], "1")` ends with `--from-claude`, other verbs and an unset variable are unchanged.
- [ ] **Step 2: Run**: FAIL. **Step 3: Implement** (a `Subject<(Guid, TaskComment)>`, emitted after the save succeeds). **Step 4: Run** Core tests: PASS. **Step 5: Commit** `feat: Petre's own comments are announced for waking`.

### Task 3: `claude-wait`, registering a waiter (Core)

**Files:**
- Create: `src/Deskbar.Core/Tasks/CommentInbox.cs`
- Modify: `src/Deskbar.Core/Control/RemoteControl.ClaudeHook.cs` (verb `claude-wait`; SessionEnd clears the inbox)
- Modify: `src/Deskbar.Core/Control/ControlArguments.cs` (`claude-wait` reads stdin like `claude-hook`, and its reply is printed raw)
- Modify: `src/Deskbar.Core/Control/ControlUsage.cs` (list the verb; the test that every dispatched command is listed)
- Test: `tests/Deskbar.Core.Tests/Tasks/CommentInboxTests.cs`, `RemoteControlTaskTests.cs`

**Interfaces:**
- Produces (`public sealed class CommentInbox(string folder, Func<DateTimeOffset> now)`):
  - `Result<(string InboxPath, string GenPath, long Generation)> Register(string sessionId)`: creates the folder, writes the next generation (milliseconds since epoch from `now`, larger than any before) to the gen file, records the waiter's expiry (`now + 2592000 s`).
  - `bool Lapsed(string sessionId)`: no registration, or past its expiry.
  - `Result Deliver(string sessionId, string message)`: appends `message` (a blank line between two) to the inbox file, creating it.
  - `void Forget(string sessionId)`: deletes both files and the registration.
- `claude-wait` replies three lines, `<inbox path>`, `<gen path>`, `<generation>`, or nothing for a headless session or an unreadable hook.

- [ ] **Step 1: Failing tests:** Register twice gives a larger generation and the gen file holds the second; Deliver twice appends both; Forget deletes both files; Lapsed before registering, false after, true once the clock passes the timeout; `claude-wait` with a headless hook replies nothing; SessionEnd removes the files.
- [ ] **Step 2-5:** run (FAIL), implement, run Core tests (PASS), commit `feat: claude-wait registers a session's comment waiter`.

### Task 4: The waiter script and its hook entry (App)

**Files:**
- Modify: `src/Deskbar.App/ClaudeSkill.cs` (write `claude-wait-hook.cmd` with the task script, remove it with it)
- Modify: `src/Deskbar.App/ClaudeHooks.cs` (the Stop entry `{"type":"command","command":"\"…/claude-wait-hook.cmd\"","asyncRewake":true,"timeout":2592000}` while the task events are on; recognised as Deskbar's by the script name)
- Test: `tests/Deskbar.Windows.Tests/ClaudeSkillTests.cs`, new `tests/Deskbar.Windows.Tests/ClaudeWaitScriptTests.cs`

**Interfaces:**
- Consumes: `claude-wait` (Task 3).
- Produces: `internal const string ClaudeSkill.WaitHookScriptName = "claude-wait-hook.cmd"`; `internal static string ClaudeSkill.WaitScript(...)` (the script text, written like the other two, first line saying Deskbar wrote it).

The script (cmd, no PowerShell, no console needed): read the three lines from `call "%~dp0deskbar.cmd" claude-wait` (stdin passes through); none: `exit /b 0`. Loop: if the gen file's number is not ours, `exit /b 0`; if the inbox exists, `move` it to `<inbox>.<generation>` (only one waiter wins a move), `type` that to stderr, delete it, `exit /b 2`; else `ping -n 3 127.0.0.1 >nul` and loop. It never calls Deskbar again, so a Deskbar restart does not disturb it.

- [ ] **Step 1: Failing tests:** ClaudeSkillTests: with the task events on, Stop holds the user's bell, Deskbar's ask entry and the wait entry with `asyncRewake: true` and `timeout: 2592000`; Sync again changes nothing; tasks off removes the wait entry and the script; an older wait entry with another command is replaced. ClaudeWaitScriptTests (a temp folder with a stub `deskbar.cmd` that prints three lines): the script exits 2 printing the inbox text to stderr once the inbox appears; with the gen file holding a larger number it exits 0 and leaves the inbox; with a stub printing nothing it exits 0 at once.
- [ ] **Step 2-5:** run (FAIL), implement, run both classes on the hidden desktop (PASS), commit `feat: a waiting Stop hook wakes a session when its inbox fills`.

### Task 5: CommentWaker, delivering (App)

**Files:**
- Create: `src/Deskbar.App/CommentWaker.cs`
- Modify: `src/Deskbar.App/ClaudeSessions.cs` (`ResumeWith(Guid taskId, TaskSession session, string message)`, the click's `WakeNow` with the comment as the message; `StopNow(string shortId)` if not public already)
- Modify: `src/Deskbar.App/App.xaml.cs` (build `CommentInbox` on `<state dir>\claude-inbox`, hand it to RemoteControl and CommentWaker, dispose with the app)
- Test: `tests/Deskbar.Windows.Tests/CommentWakerTests.cs` (fake `IClaudeCli`, as `ClaudeSessionsTests` does)

**Interfaces:**
- Consumes: `TaskService.Commented`, `CommentWake.Plan`, `SessionBriefing.Commented`, `CommentInbox`, `ClaudeSessions.Live`, `ResumeWith`, stop.
- Produces: `sealed class CommentWaker(TaskService tasks, ClaudeSessions sessions, CommentInbox inbox, Action<string>? trace) : IDisposable`.

- [ ] **Step 1: Failing tests:** an idle listed session's comment lands in its inbox file; two comments append; a session not listed is resumed with `--bg --resume <id> <message>` from its folder; a lapsed background one is stopped then resumed; a task whose session has no transcript gets one Deskbar line in its thread and no CLI call; a session's own comment does nothing.
- [ ] **Step 2-5:** run (FAIL), implement (subscribe on the UI scheduler; the plan reads the latest `Live`), run (PASS), commit `feat: Petre's comment wakes the task's Claude session`.

### Task 6: Docs, manual steps, release

- Modify: `docs/features/claude-sessions.md` (the third hook entry, what it does, the inbox folder, VS Code caveat), `docs/features/tasks.md` (a comment reaches the session), `docs/superpowers/notes/manual-test-script.md` (CW1 an idle background session wakes on a comment with a picture and reads it; CW2 two quick comments arrive together; CW3 a stopped Done task's session is resumed by a comment; CW4 a busy session gets it at its next step; CW5 a VS Code session: does it wake? note the result).
- [ ] Release build with warnings as errors; full routine suite on the hidden desktop; commit `docs: comments wake their session`; push; PR with `## Release notes`; base watch; CI to green.
