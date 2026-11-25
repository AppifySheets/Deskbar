# A model for each task session

Date: 2026-11-25. Deskbar task e52e33cb, "give each claude session best model for the job, never default. maybe start
with one model and delegate to a cheaper one".

## Why

A session Deskbar starts on a task ran `claude --bg "<first message>"` with no model argument, so it got the model
in `~/.claude/settings.json` that day. Petre runs about 30 task sessions and hits the usage limits. His words:

- "give each claude session best model for the job, never default"
- Asked what it is for: "limits, weekly or 5hr"
- Asked who picks: "deskbar picks the model; maybe through the deskbar manager agent"
- "maybe each task starts with the best model and delegates to something cheaper"
- "can we also add a review step at the end by a smarter model? i don't know if this whole thing makes sense or if i
  should stick to opus or some default model"

## Goals

- Every session Deskbar starts on a task names its model. A change of Claude Code's own default no longer moves
  every task session with it.
- One model for all task sessions, changeable, with a one-off choice for a single start.
- Sessions keep mechanical work out of their own conversation, on a cheaper model.
- The strongest model reviews the diff before a pull request, and the plan before the code when there is one.

## Non-goals (this change)

- Deskbar choosing a different model per task (see Approaches, 1).
- Moving a running session to another model. Measured below: it means a copy with a new session id.
- A box for the model in Manage. The setting is on the pipe only for now.
- The manager session's own model (its design fixes it) and sessions Petre starts by hand.

## Anti-goals

- A task session started with no `--model`.
- Model names in the briefing: which models a person has depends on their plan, and the briefing goes to everyone.

## Measured (2026-11-25, Claude Code 2.1.332)

Model flags:

- `claude --bg --model haiku ...` starts a session whose turns are answered by `claude-haiku-5-5` (the transcript's
  `message.model`).
- `claude --bg --resume <id>` with no flags continues the same session on its saved options, the model among them
  ("woke session ... with its saved options (--tools, --setting-sources, -n, --model)"). The resume paths in
  `ClaudeSessions` therefore pass no model.
- `claude --bg --resume <id> --model sonnet` does not change the session. It starts a copy under a new id, running or
  stopped ("keeps its own saved options, so the flags you passed started a copy as ..."), and the copy's next turn is
  answered by Sonnet.
- A stripped one-shot call (`claude -p --model fable --no-session-persistence --tools "" --strict-mcp-config
  --setting-sources "" --disable-slash-commands --system-prompt ...`) sent about 700 input tokens and took about 2.5
  seconds. Recorded for approach 1, should it come back.

Where a week of usage went (Petre's transcripts, the 7 days to 2026-11-25, 253 sessions, 93 of them started by
Deskbar on a task; weighted with output at 5, cache writes at 2 and cache reads at 0.1 of an input token, which is an
assumption and not Anthropic's limit formula):

- 91% ran on Opus 5.5 and about 8% on Fable. Task sessions ran 99% on Opus. The settings file said `fable` on the
  day of the measurement, so that had changed recently.
- Task sessions carried about a quarter of the main transcripts' usage.
- The largest quarter of task sessions carried 63% of task usage; the smaller half carried 17%.
- About two thirds of all usage was cache reads (a long conversation read again on every turn), 23% cache writes,
  9% output.

## Approaches

1. **Deskbar sizes each task at start** with one small call to a strong model and passes the model it names. Designed
   first, then dropped on the numbers above: sizing moves the small tasks to a cheaper model and keeps the hard ones
   where they are, and the small half is 17% of a quarter of the usage. A few percent saved, against new code, a start
   that can misjudge a task from its title, and a shipped feature naming models a user's plan may lack.
2. **The manager picks.** It acts only in a turn Petre started, and a start from the bar or the panel never passes
   through it. Kept as the one-off `--model` on `task start`, which the manager can use.
3. **One named model, and the saving inside the session** (picked). The cost is in the long sessions, so what counts
   is how much a session keeps in its own conversation. The briefing sends mechanical work to a cheaper subagent, and
   the strongest model is spent where it reads little: on the diff and on the plan. This is Petre's "starts with the
   best model and delegates to something cheaper", applied inside the session.

Left for later: start cheap and move up when the work proves harder. A move is a copy with a new id (measured), which
touches joining, the ledger and the terminal window.

## Design

**The model.** `ClaudeStart.Arguments(task, model)` builds `--bg --model <model> <first message>`, and
`ClaudeSessions.Start` uses it with the model given for that start, otherwise `AppState.ClaudeTaskModelInEffect`.
`AppState.ClaudeTaskModel` is null in every older state.json, and null or blank means `ClaudeStart.DefaultModel`,
`opus`: what last week's task sessions ran on. No value hands the choice back to Claude Code's settings.

**Changing it.** `deskbar claude-model [<model>]` reads or sets the setting. `deskbar task start <id> [--folder <path>]
[--model <model>]` names the model of one session. Both go through `ClaudeStart.Model`, which takes an alias or a full
model name and refuses anything `claude` could read as another argument. Both are in `ControlUsage`.

**The briefing.** `SessionBriefing` gains two lines for every task session: keep your own conversation short by giving
test runs, builds, broad searches and rebases to a subagent on a cheaper model; and before opening a pull request have
a subagent on the strongest model you can use review the diff, and the plan before the code when there was one.

## To watch after it ships

- Whether the review line earns its place: on the first few pull requests, does the review find anything real?
- Rerun the usage measurement after a week and compare the share of cache reads and the size of the large sessions.
- On a plan without Opus the start fails with Claude's own message in the thread, and `claude-model` fixes it. If
  that turns out to be common among users, the default needs another look.
