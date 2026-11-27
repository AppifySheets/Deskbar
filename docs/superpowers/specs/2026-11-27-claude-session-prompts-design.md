# Every Claude Code dialog becomes its task's question

[Deskbar task b310a649](https://deskbar.app/task#b310a649-1748-4bfe-b062-88378aa7e808). Approved by Petre on 2026-11-27
("okay").

## Intent

When a Claude Code session (terminal, background or VS Code) stops on something only Petre can answer, its task shows
it as a question on the bar, Waiting on you, and the question goes once he answers in the session. Until now only the
AskUserQuestion dialog did that (task 493b7ba9), and permission prompts only with the opt-in Allow / Deny card (task
6306d261). Plan approval, MCP input requests, "Session paused", sandbox network requests and the rest just blinked the
workspace.

Non-goals: answering any of these from Deskbar (the card stays the only answer surface, for permissions); the idle
"Claude is waiting for your input" notice (the end-of-turn nudge covers a parked session); auth and "finished" notices.

## What Claude Code does (measured in the 2.1.332 binary, 2026-11-27)

Every blocking dialog Claude Code draws is in one registry, and each entry may carry a notification. Six seconds after
a dialog opens with no keypress, Claude Code fires the Notification hook once with that entry's text and type, the
type defaulting to `permission_prompt`. Texts seen include "Claude needs your permission", "Claude Code needs your
approval for the plan", "Claude Code wants to enter plan mode", "Claude Code needs your input" (MCP elicitation),
"An MCP server needs your input", "Session paused", "A sandboxed command needs network access", "Claude proposed a
session goal", "Claude wants to use your browser". The SDK path (VS Code) fires "Claude needs your permission to use
<tool>" after the same 6 s. Types that mean a dialog is waiting: `permission_prompt`, `elicitation_dialog`,
`elicitation_url_dialog`, `worker_permission_prompt`, `agent_needs_input`. Not questions: `idle_prompt`,
`auth_success`, `auth_storage_failure`, `agent_completed`, `push_notification`, `computer_use_exit`,
`plugin_notification`. `elicitation_response` and `elicitation_complete` report an MCP request answered.

The transcript holds nothing of a pending tool call until it is decided (TranscriptMark's probe), so a permission's
tool and command must come from the PermissionRequest hook, which fires when the dialog opens.

## Approaches

1. Notification hook plus details from PermissionRequest and Elicitation hooks, cleared by the transcript mark.
   Chosen: one mechanism covers every present and future dialog, the 6 s delay keeps quickly answered prompts off the
   bar, and the hooks it adds fire only when a dialog opens.
2. A PostToolUse entry with no matcher to see a prompt answered. Rejected: a process and a pipe turn for every tool
   call a session makes.
3. Polling `claude agents --json`. Rejected: it lists background sessions only.

## Design

- A Notification of a question type puts a question on the session's open task, through `task ask`'s own path, under
  the AskUserQuestion rules: a task already Waiting on you keeps its question, a Done task stays Done. The workspace
  blinks as before.
- Text (SessionPrompt): "Allow <tool> in the session: <main field>" for a tool permission, "Approve the plan in the
  session: <plan's first line>" for ExitPlanMode, "Answer <server> in the session: <message>" for an MCP request, and
  "Answer in the session: <notification text>" for anything else. A tool permission while the Allow / Deny card shows
  for the session adds no question: the card already says it.
- Details: a PermissionRequest entry on the task script (only while the card is off; with it on, `claude-permission`
  notes the same details) and an Elicitation entry, both silent. A detail is used only for a permission or plan text
  and only within two minutes.
- Clearing (SessionPrompts): the question placed is remembered with a TranscriptMark (the subagent's transcript when an
  agent id came with it). Every 5 s, a mark that moved on resolves it as `task answered` would, only while the task
  still shows that exact question. A Stop, the user's own prompt, a SessionEnd or an `elicitation_response` resolve it
  at once. The AskUserQuestion question gets the same mark, so Esc on that dialog clears it too (manual step 68a).

## Left to the builder

Names, file split and the manual test steps.
