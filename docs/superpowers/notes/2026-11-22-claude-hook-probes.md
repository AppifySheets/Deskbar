# Claude hook probes, 2026-11-22

Task 6 of `docs/superpowers/plans/2026-11-22-tasks-board.md`: what Claude Code's hooks see, and how long a hook
that reaches Deskbar takes, measured before Task 7 builds the hooks. Claude Code 2.1.330 (CLI and the VS Code
extension), Git Bash 5.2.37, Deskbar 2.5.170 (a Release build from the tasks-board worktree) running throughout.
The probe scripts lived in the session's scratch folder and are not kept; everything they printed that matters is
below. Session ids are cut to their first 8 characters and the scratch path is written `<scratch>`.

Deskbar was never stopped, and the probes sent it only `version`.

## How long a hook takes (Step 1)

Claude Code runs a hook command through Git Bash, so each case is `bash.exe -c "<command>"` started from a
PowerShell stopwatch, 20 runs, the whole process included.

| Case | Median | Best | Worst |
| --- | ---: | ---: | ---: |
| `bash -c true` (Git Bash starting, nothing else) | 21 ms | 21 | 38 |
| `bash -c noop.cmd` (Git Bash plus cmd.exe running a one-line .cmd) | 36 ms | 33 | 55 |
| `deskbar.cmd version` (the hook path today: shim, then `Deskbar.exe ctl`, dev Release build) | **297 ms** | 283 | 321 |
| `deskbar.cmd version --wait x` (same exe, fails on the argument before touching the pipe) | 271 ms | 257 | 296 |
| a copy of the shim pointed at the installed 2.2.6 release (single-file, self-contained) | **406 ms** | 389 | 584 |
| `deskbar-cli.exe version` (the Store package's console client, framework-dependent, built to scratch) | 116 ms | 111 | 411 (first run, cold) |
| a 1.8 MB NativeAOT console exe that writes `{"args":["version"]}` to the pipe and prints the reply | **48 ms** | 45 | 119 (first run, cold) |

What the 297 ms is made of: about 21 ms Git Bash, about 15 ms cmd.exe, about 235 ms starting the WPF exe up to
`ControlCommandLine.Run`, and about 25 ms for the pipe round trip itself. The pipe is cheap and starting the exe is
expensive. The release users run is the single-file exe, and it costs another 110 ms on top of the dev build.

### With Deskbar not running

Not run against the real app, because stopping Petre's Deskbar was ruled out. The pipe name is
`Deskbar.Control.<login session id>` (`ControlWire.PipeName`) and neither `ctl` nor `deskbar-cli` lets a caller
override it, so the real exe cannot be pointed at an empty name. Instead:

- From the code: `ControlClient.SendAsync` calls `NamedPipeClientStream.ConnectAsync(2000)`. On timeout it returns
  exit 2 with "Deskbar is not running (nothing answered on its control pipe). Start it and try again.", which
  `ControlCommandLine.Print` writes to stderr. Today's `claude-hook.cmd` sends both streams to `nul` and exits 0, so
  the session sees nothing.
- Measured: the same `ConnectAsync(2000)` with the same options (`Asynchronous | CurrentUserOnly`), from
  PowerShell 7.4 (.NET 8), on a name nobody serves: 1989, 1993, 1996, 2001 and 2006 ms, each ending in
  `System.TimeoutException`. When no pipe of that name exists .NET keeps retrying until the timeout runs out. It
  does not fail fast.
- Measured: `WaitNamedPipeW` (Unicode, kernel32) on the same missing name returns false with error 2
  (`ERROR_FILE_NOT_FOUND`) in 0.02 to 0.05 ms (3.2 ms on the first call).

So with Deskbar down, every hook costs exe start plus 2 s: about 2.3 s on the dev build and 2.4 s on the release.
On UserPromptSubmit that delay lands before every prompt. A client that asks `WaitNamedPipeW` first would know at
once that nobody is listening.

## What the hooks see (Step 2)

A scratch project with a throwaway `.claude/settings.local.json` registering one command hook on SessionStart,
UserPromptSubmit, Stop and SessionEnd. The hook appended its stdin and its sorted `env` (secrets redacted) to a
scratch log. Every run used `--setting-sources project,local` (the SDK run: `settingSources: ['project','local']`),
so Petre's own Notification and Stop hooks did not fire and his bar did not blink. All runs except the last one
started with every `CLAUDE*`, `AI_AGENT`, `VSCODE*` and `ANTHROPIC*` variable removed, which is how a terminal or a
scheduled task starts them. The hook settings, the five probe sessions' transcripts and their `session-env`
folders were deleted afterwards.

Runs:

1. `claude -p "say ok"`
2. `claude -p "say ok" --output-format stream-json --verbose`
3. `claude -p --input-format stream-json --output-format stream-json --verbose`, the prompt on stdin (the SDK's
   transport)
4. A real TypeScript Agent SDK run (`@anthropic-ai/claude-agent-sdk` 0.3.330, `query()`, pointed at the installed
   `claude.exe`)
5. `claude -p "say ok again" --resume <run 1's id>`
6. `claude -p "say ok"` started from inside this VS Code session WITHOUT clearing the environment, the way a
   session that runs `claude -p` in its shell tool would start it

All four events fired in every run, in the order SessionStart, UserPromptSubmit, Stop, SessionEnd.

### Variables

The VS Code column comes from the dispatching session's own environment (it was not probed with a hook here). The
terminal column was not measured, because an interactive session cannot be driven from an agent's shell. Its values
are read from the 2.1.330 binary (below).

| Variable | VS Code session | Interactive terminal | `-p` (runs 1, 2, 3, 5) | Agent SDK (run 4) | `-p` inheriting VS Code's env (run 6) |
| --- | --- | --- | --- | --- | --- |
| `CLAUDE_CODE_ENTRYPOINT` | `claude-vscode` | `cli` (expected) | `sdk-cli` | `sdk-ts` | **`claude-vscode`** (inherited) |
| `CLAUDE_CODE_SESSION_ATTENDED` | `1` | `1` (expected) | `0` | `0` | **`0`** |
| `CLAUDE_CODE_CHILD_SESSION` | | | `1` | `1` | `1` |
| `CLAUDECODE` | | | `1` | `1` | `1` |
| `CLAUDE_CODE_SESSION_ID` | | | the session id | the session id | the session id |
| `CLAUDE_PROJECT_DIR` | | | the project folder, forward slashes | same, in 8.3 form | same |
| `AI_AGENT` | | | `claude-code_2-1-330_harness` | same | same |
| `CLAUDE_AGENT_SDK_VERSION` | set | | absent | `0.3.330` | `0.3.330` (inherited) |
| `CLAUDE_ENV_FILE` | | | SessionStart only | SessionStart only | SessionStart only |
| `CLAUDE_EFFORT` | | | Stop only (`medium`) | Stop only | every event (inherited `high`, Stop `medium`) |
| `CLAUDE_CODE_MESSAGING_SOCKET` | | | every event but SessionEnd | absent | every event but SessionEnd |
| `VSCODE_PID` | | | absent | absent | present (inherited) |

`CLAUDE_CODE_CHILD_SESSION=1` is in every hook's environment, so it says nothing about how the session itself was
started. No variable other than these two differs between an interactive and a headless session. `CI`,
`TERM_PROGRAM` and a TTY flag are not set.

How Claude Code sets the two that matter, read from `~/.local/bin/claude.exe` (2.1.330):

- `CLAUDE_CODE_ENTRYPOINT`: if it is already in the environment it is kept (only `cli` becomes `sdk-cli` under
  `-p`). Otherwise `mcp` for `mcp serve`, `claude-code-github-action` under `CLAUDE_CODE_ACTION`, else `sdk-cli`
  with `-p` and `cli` without. The SDK sets `sdk-ts` before it starts the CLI. This is why run 6 reports
  `claude-vscode`: a child started from a VS Code session inherits the parent's value.
- `CLAUDE_CODE_SESSION_ATTENDED` is computed fresh for every session and never inherited: `"1"` when the session
  is interactive, or when it is not a child session and its entrypoint is in a fixed list of attended hosts
  (`claude-vscode`, `claude-desktop`, `claude-desktop-3p`, `local-agent`, `remote`, `remote_desktop`,
  `remote_mobile`, `ssh-remote`, the Slack and Teams entrypoints and a few others). It is always `"0"` for
  background and daemon sessions (`CLAUDE_CODE_SESSION_KIND` `bg`, `daemon`, `daemon-worker`). This is why run 6
  reports `0` even though it inherited `claude-vscode`.

### The hook's stdin, per event

Run 1, `claude -p "say ok"`. `transcript_path` is
`C:\\Users\\<user>\\.claude\\projects\\<project key>\\<session id>.jsonl` in every event.

```json
{"session_id":"92943450","transcript_path":"...","cwd":"<scratch>\\t6\\hookproj","hook_event_name":"SessionStart","source":"startup"}
{"session_id":"92943450","transcript_path":"...","cwd":"<scratch>\\t6\\hookproj","prompt_id":"0e3c47f3","permission_mode":"default","hook_event_name":"UserPromptSubmit","prompt":"say ok"}
{"session_id":"92943450","transcript_path":"...","cwd":"<scratch>\\t6\\hookproj","prompt_id":"0e3c47f3","permission_mode":"default","effort":{"level":"medium"},"hook_event_name":"Stop","stop_hook_active":false,"last_assistant_message":"ok","background_tasks":[],"session_crons":[]}
{"session_id":"92943450","transcript_path":"...","cwd":"<scratch>\\t6\\hookproj","prompt_id":"0e3c47f3","hook_event_name":"SessionEnd","reason":"other"}
```

Run 5, the same session resumed. The session id is the same and `source` is `resume`, with four new fields:

```json
{"session_id":"92943450","transcript_path":"...","cwd":"<scratch>\\t6\\hookproj","hook_event_name":"SessionStart","source":"resume","seconds_since_last_response":102,"context_tokens":28259,"prompt_cache_likely_expired":false,"estimated_cache_write_usd":0.2261}
```

| Event | Fields |
| --- | --- |
| SessionStart | `session_id`, `transcript_path`, `cwd`, `hook_event_name`, `source` (`startup` seen, `resume` seen); on resume also `seconds_since_last_response`, `context_tokens`, `prompt_cache_likely_expired`, `estimated_cache_write_usd` |
| UserPromptSubmit | `session_id`, `transcript_path`, `cwd`, `prompt_id`, `permission_mode`, `hook_event_name`, `prompt` |
| Stop | `session_id`, `transcript_path`, `cwd`, `prompt_id`, `permission_mode`, `effort`, `hook_event_name`, `stop_hook_active`, `last_assistant_message`, `background_tasks`, `session_crons` |
| SessionEnd | `session_id`, `transcript_path`, `cwd`, `prompt_id`, `hook_event_name`, `reason` (`other` when `-p` finishes) |

Notes from the samples:

- No field says whether the session is interactive. The JSON carries the same fields in all six runs, so only the
  environment can tell headless from interactive.
- Run 6 adds `scratchpad_dir` to every event (a session started from inside another session gets one). Ignore
  unknown fields; do not reject them.
- `cwd` is the path in the form the process was given. Runs 1, 2, 3, 5 and 6 report the long form
  (`C:\\Users\\<user>\\...`); the SDK run, started with an 8.3 path, reports the 8.3 short form (`C:\\Users\\<USER~1.XYZ>\\...`) in `cwd`
  and in `CLAUDE_PROJECT_DIR`. `CLAUDE_PROJECT_DIR` always uses forward slashes, `cwd` backslashes. Anything that
  matches a folder against a VS Code window title has to normalise both (full path, long form, one separator)
  first.
- `clear`, `compact` and `fork` sources were not produced here. They need an interactive session.

## Deferred: which window opens a `vscode://` link (Step 3)

Not done. Bringing VS Code windows forward through Deskbar and opening `vscode://anthropic.claude-code/open?...`
links takes over Petre's screen while he works, so it waits for the live check with him. The same live check can
capture the hook JSON and environment from a real VS Code session and a real terminal session (register the
probe hook in a scratch project, open it in each, send one prompt), which fills the two unmeasured columns above.

## Proposed rulings (Step 4)

### (a) The hook path

The median is 297 ms on the dev build and 406 ms on the release users run, so it is over the 300 ms line, and with
Deskbar down every hook costs about 2.3 to 2.4 s. Notification and Stop already pay this today, but nothing waits on
them. UserPromptSubmit would put it in front of every prompt.

Proposed, before Task 7 starts:

1. A small NativeAOT hook client (measured: 48 ms median through Git Bash, 1.8 MB) that knows one job: read the
   hook's stdin, read `CLAUDE_CODE_SESSION_ATTENDED`, `CLAUDE_CODE_ENTRYPOINT` and `CLAUDE_PROJECT_DIR` from its
   own environment, ask `WaitNamedPipeW` whether Deskbar's pipe exists (returns in well under a millisecond when it
   does not), send one `claude-hook` request, print the reply's text and exit 0 whatever happens. It speaks the
   pipe's JSON line itself. It cannot reference `ControlWire` without dragging in reflection-based System.Text.Json,
   so the wire format needs a test that the two ends agree.
2. To keep the release one file, the main exe carries the client as an embedded resource and writes it next to
   `deskbar.cmd` at start, the way it rewrites the shim, only when the bytes differ. The hook's command is then the
   client's quoted path, with no `.cmd` in between. That also removes the cmd `%VAR%` trap (an unset variable stays
   the literal text `%CLAUDE_CODE_ENTRYPOINT%`).
3. If (1) and (2) are judged too much for this pull request, the fallback is to keep the script and the exe, and
   make `ControlClient` ask `WaitNamedPipeW` before `ConnectAsync`. That fixes the 2 s Deskbar-down case but leaves
   the 300 to 400 ms per prompt.

For the builder to check: whether a packaged (Store) Deskbar can write an exe into `%APPDATA%\Deskbar` that an
unpackaged Git Bash then runs (the shim already goes there, so probably yes), and whether CI's Windows runner has
the C++ tools NativeAOT needs (this machine needed `vswhere` on the PATH).

### (b) The headless rule

Headless unless `CLAUDE_CODE_SESSION_ATTENDED` is exactly `1`. When the variable is missing (an older Claude Code)
or arrives as the literal unexpanded `%CLAUDE_CODE_SESSION_ATTENDED%`, fall back to the entrypoint: headless when
`CLAUDE_CODE_ENTRYPOINT` is `sdk-cli`, `sdk-ts` or `sdk-py`, or missing.

The entrypoint must not be the main signal. Run 6 shows a `claude -p` started from a VS Code session reporting
`claude-vscode`, and that is exactly the case the rule exists for: a session that runs a headless child would
otherwise have the child offered tasks. `ATTENDED` is computed per session by Claude Code and gets this right.

This changes Task 7's interface: `ClaudeHookEvent.Parse` takes the attended value as well as the entrypoint, and the
verb gains `--attended <value>` (or, under ruling (a), the client reads both from its environment).

### (c) Open the link, or only bring the window forward

Pending the live check. Proposed until then: Task 10 builds "bring the session's VS Code window forward" as the
whole click. Opening the `vscode://` link is a second step added only if the live check shows the link lands in the
window Deskbar just brought forward, with both orders (A in front, B in front) tried. That way a failed check costs
nothing to undo.

## Concerns

- Loop check-ins (`/loop`) run inside an attended session, so their prompts presumably arrive with
  `ATTENDED=1` and look exactly like Petre's own. The spec counts them as headless. Not measured; the live check
  should send one `/loop` prompt with the probe hook on and see whether the UserPromptSubmit JSON differs.
- The VS Code and terminal columns rest on the parent session's environment and on reading the binary, not on a
  probe hook in those hosts.
