# Claude session prompts Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every blocking Claude Code dialog becomes its session's task question and clears once answered.

**Architecture:** The Notification hook Deskbar already receives carries the dialog's text and type; a pure
`SessionPrompt` turns it (plus a detail noted from a PermissionRequest or Elicitation hook) into question text, and a
stateful `SessionPrompts` remembers what was placed with a `TranscriptMark`, so a 5 s tick and the session's own events
resolve it.

**Tech Stack:** C# / .NET 10, CSharpFunctionalExtensions, System.Reactive, xunit.

**Spec:** `docs/superpowers/specs/2026-11-27-claude-session-prompts-design.md`

## Global Constraints

- Question types: `permission_prompt`, `elicitation_dialog`, `elicitation_url_dialog`, `worker_permission_prompt`, `agent_needs_input`. Settling types: `elicitation_response`, `elicitation_complete`.
- A detail is used for at most `SessionPrompts.DetailFor` = 2 minutes, and only for a permission or plan text.
- Settle tick: `PermissionKeeper.ExpireEvery` (5 s).
- A question is only resolved while the task is Waiting on you with exactly the text that was placed.
- Hooks stay silent: nothing printed for PermissionRequest, Elicitation or Notification.

## Review Focus

- "Session paused" arrives as `permission_prompt` right after a quickly answered Bash prompt: it must not be named after the stale Bash detail (only "Claude needs your permission…" / plan texts take a detail).
- A task already waiting on `task ask` (a PR to merge) gets a permission prompt: its question stays.
- The Allow / Deny card is showing for the session: no second question.
- A harness-delivered UserPromptSubmit (task-notification) must not resolve a placed prompt; the user's own prompt does.
- A subagent's prompt without its transcript file: no mark, cleared only by events.

---

### Task 1: SessionPrompt text and SessionPrompts state (Core)

**Files:**
- Create: `src/Deskbar.Core/Tasks/SessionPrompt.cs`, `src/Deskbar.Core/Tasks/SessionPrompts.cs`
- Test: `tests/Deskbar.Core.Tests/Tasks/SessionPromptTests.cs`

**Interfaces (produces):**
- `record PromptDetail(string Tool, string Summary, Maybe<string> AgentId, DateTimeOffset At)`;
  `static PromptDetail PromptDetail.Of(PermissionRequest, DateTimeOffset)` (summary: the card's main part, first line,
  marked with `PermissionDisplay.Visible`, cut to 160 chars with "…"; ExitPlanMode: the plan's first non-empty line
  without leading `#`); `static PromptDetail PromptDetail.Elicitation(string server, string message, Maybe<string> agentId, DateTimeOffset at)` (Tool = "mcp:" + server).
- `static class SessionPrompt`: `bool IsQuestion(string type)`, `bool Settles(string type)`,
  `Maybe<string> Text(string type, string message, Maybe<PromptDetail> detail)`, `const string Lead = "Answer in the session:"`.
- `sealed class SessionPrompts(Func<DateTimeOffset> now)`: `Note(string session, PromptDetail)`,
  `Maybe<PromptDetail> Detail(string session)` (fresh only), `Placed(string session, Guid task, string question, Maybe<TranscriptMark>)`,
  `IReadOnlyList<Placement> Moved()` (removes and returns those whose mark moved on),
  `IReadOnlyList<Placement> Take(string session)` (removes and returns the session's), `record Placement(string Session, Guid Task, string Question, Maybe<TranscriptMark> Mark)`.

- [ ] Tests: permission with a Bash detail -> "Allow Bash in the session: git push"; plan message + ExitPlanMode detail -> "Approve the plan in the session: Fix the bar"; "Session paused" + a Bash detail -> "Answer in the session: Session paused"; elicitation_dialog + mcp detail -> "Answer github in the session: Pick a repo"; idle_prompt -> None; stale detail (>2 min) not returned by `Detail`; `Moved` returns a placement whose transcript grew an assistant line and keeps one that did not; `Take` empties the session.
- [ ] Implement, run `dotnet test tests/Deskbar.Core.Tests --filter SessionPrompt`, commit.

### Task 2: Wiring in RemoteControl, ClaudeHookEvent and ClaudeHooks

**Files:**
- Modify: `src/Deskbar.Core/Tasks/ClaudeHookEvent.cs` (NotificationType, Message, AgentId, McpServer, ElicitationMessage)
- Modify: `src/Deskbar.Core/Control/RemoteControl.ClaudeHook.cs`, `RemoteControl.Permission.cs`, `RemoteControl.cs`
- Modify: `src/Deskbar.App/ClaudeHooks.cs`, `src/Deskbar.App/App.xaml.cs` (5 s tick calling `RemoteControl.SettlePrompts()`)
- Test: `tests/Deskbar.Core.Tests/RemoteControlSessionPromptTests.cs`, `tests/Deskbar.Windows.Tests/ClaudeSkillTests.cs`

**Interfaces (consumes Task 1; produces):** `public void RemoteControl.SettlePrompts()`.

- [ ] Tests: Notification permission_prompt after a PermissionRequest puts "Allow Bash in the session: …" on the task (Waiting on you); a waiting task keeps its question; a Done task stays Done; with a card held for the session no question; Stop, a user prompt and elicitation_response resolve it; a `<task-notification>` prompt does not; `SettlePrompts` resolves after the transcript grows; a question the session changed meanwhile is left alone; AskUserQuestion's question is also settled by the mark; ClaudeHooks writes a PermissionRequest task entry only while the card is off and an Elicitation entry with the task events.
- [ ] Implement, `dotnet build Deskbar.sln -c Release -warnaserror`, run the two test projects' new tests, commit.

### Task 3: Docs

- [ ] `docs/features/claude-sessions.md`: the questions list; `docs/superpowers/notes/manual-test-script.md`: steps SP1-SP6; commit.
