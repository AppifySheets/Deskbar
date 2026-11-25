# Manager session: claude flag probes (2026-11-25)

Measured with Claude Code 2.1.332 (the CLI on this machine; the brief said 2.1.331), working directory the manager-session worktree
(trusted through its parent `E:/repos/TaskSpaces`). Every probe session was stopped and removed within about 30 seconds of its start.
`P` below is `C:/Users/petre.chitashvili/.claude/jobs/b6bd0c26/tmp/probe-brief.md` (one line: `Answer every message with the word PINEAPPLE first.`).

## Start flags

- `claude --bg --model haiku -n "probe manager" --strict-mcp-config --append-system-prompt-file P --allowedTools "Bash(deskbar.cmd task:*)" --disallowedTools Read Edit Write NotebookEdit Agent WebFetch WebSearch Monitor -- "<prompt>"` printed `backgrounded · 19686b7a · probe manager`. It starts; the `--` before the prompt works and the prompt is not swallowed by `--disallowedTools`.
- `claude agents --json` listed it with `"name": "probe manager"`, `"kind": "background"`, `"status": "busy"`, `"state": "working"`. The JSON has `state` as well as `status` (`done`, `working`, `blocked`); a prompt wait reads `"status": "waiting"`, `"waitingFor": "permission prompt"`, `"state": "blocked"`. Only a running session has a `pid`.
- `claude logs 19686b7a` (raw terminal redraws; strip ANSI with `sed 's/\x1b\[[0-9;]*[A-Za-z]//g'`) showed `● PINEAPPLE` before every reply. `--append-system-prompt-file` works.
- The session's tool list (read from its transcript) had no Read, Edit, Write, NotebookEdit, Agent, WebFetch, WebSearch or Monitor, and no MCP tools. `--disallowedTools` and `--strict-mcp-config` work. It still had Bash, PowerShell, Glob, Grep, Skill, ToolSearch, Artifact, EnterWorktree, Workflow and others.
- Banner showed `Haiku 5.5`; transcript model `claude-haiku-5-5`.
- The session's `permissionMode` was `auto` (`⏵⏵ auto mode on` in the status line) without `--permission-mode`, so the first probe proves nothing about the allow rule: auto mode ran everything.
- In that first probe the model chose the PowerShell tool and ran `& "$env:APPDATA\Deskbar\deskbar.cmd" help`, then `task list` (read only). `Bash(...)` rules do not cover PowerShell. To confine the manager to the shim, PowerShell has to be disallowed (or given its own rule), or the manager will run anything through it.

## Model

- Same start with `--model fable` (no `--allowedTools`), prompt `Say hi in one short line.`: banner `Fable 5.2 · Claude Team`, reply `● PINEAPPLE` then `Hi Petre, ready when you are.` Fable is accepted.

## Allowed-tools rule form (with `--permission-mode default`, PowerShell disallowed, prompt tells it to use Bash)

- Rule `Bash(deskbar.cmd task:*)`, model ran `"$APPDATA/Deskbar/deskbar.cmd" task overview` (it expanded the shim itself): permission prompt, `waitingFor: permission prompt`, text `A variable in this command can't be checked before it runs`. A variable in the command defeats any rule. Stopped and removed.
- Rule `Bash(deskbar.cmd task:*)`, command text literally `deskbar.cmd task overview`: no prompt, but Bash replied `Exit code 127 ... deskbar.cmd: command not found`. The short form matches, but `deskbar.cmd` is not on Git Bash's PATH, so it cannot work.
- Rule `Bash(C:/Users/petre.chitashvili/AppData/Roaming/Deskbar/deskbar.cmd task:*)`, command text `C:/Users/petre.chitashvili/AppData/Roaming/Deskbar/deskbar.cmd task overview`: ran with no prompt. Deskbar answered with its task usage list; `overview` is not a subcommand yet, so nothing else ran. The full-path form works, provided the manager is told the literal path (no `$APPDATA`).
- Note: the path has to be the user's real expanded path, so Task 2 must build the rule and the briefing from the actual `%APPDATA%` at runtime.

## Resume

- `claude --bg --resume <full id> --model haiku --append-system-prompt-file P -- "Say hello."` against a stopped background session printed `note: background session 14cdea32 keeps its own saved options, so the flags you passed started a copy as 3da98176`. Flags on resume start a COPY under a new id; the copy was stopped and removed.
- Same with a short id instead of the full one: `note: started a copy of that conversation as cca8c317. To continue a session under its own id, pass its full session id (lowercase...)`. Also a copy, removed.
- `claude --bg --resume 14cdea32-78db-4056-9ebd-84aaf4ec1b5c -- "Say hello."` (no flags): `note: woke session 14cdea32 with its saved options (-n, --strict-mcp-config, --append-system-prompt-file, --disallowedTools, --model).` Same id, name still `probe manager`, reply `● PINEAPPLE` then `Hello, Petre.`, banner still Fable 5.2. The briefing and model survive a bare resume.
- In this session only the flags I started with were listed as saved; the later dontAsk probe, started with `--allowedTools` and `--permission-mode`, shows both saved too.
- The forked copies from the two flag forms above were not checked for PINEAPPLE or model: they are forks, not used, so what they carry does not matter.

## So

- Start (final): `--bg --model fable -n "manager" --strict-mcp-config --permission-mode dontAsk --append-system-prompt-file <briefing path> --allowedTools "Bash(<expanded shim path with forward slashes> task:*)" --disallowedTools Read Edit Write NotebookEdit Agent WebFetch WebSearch Monitor PowerShell -- "<first prompt>"`. (Probed with `--model haiku`; `--model fable` was probed separately without the permission flags.)
- Resume (final): `--bg --resume <FULL session id> -- "<prompt>"`, with no other flags. Repeating flags forks a copy. Wait until `claude agents --json` no longer lists the session as running before resuming, or it forks a copy ("already running").
- Bare resume keeps `--permission-mode`, `--allowedTools`, `--disallowedTools`, `--model`, `--append-system-prompt-file`, `--strict-mcp-config` and `-n` (measured, see the dontAsk section).
- `--permission-mode dontAsk` is needed: sessions default to auto mode, which runs everything. Under dontAsk a state-changing command with no matching rule (`git branch probe-dontask-x`) is denied with no prompt (`Permission to use Bash has been denied because Claude Code is running in don't ask mode`), no branch is created, and the full-path shim call still runs. Read-only commands such as `git status` are still allowed without a rule.
- Allowed-tools rule: the full shim path form works; the short `deskbar.cmd` form matches but the command is not found in Git Bash. The briefing must give the literal path, never `$APPDATA`.
- Disallow PowerShell too, or the manager can bypass the Bash rule.

## dontAsk probes (coordinator's follow-up)

- `claude --bg --model haiku -n "probe manager" --strict-mcp-config --permission-mode dontAsk --append-system-prompt-file P --allowedTools "Bash(C:/Users/petre.chitashvili/AppData/Roaming/Deskbar/deskbar.cmd task:*)" --disallowedTools Read Edit Write NotebookEdit Agent WebFetch WebSearch Monitor PowerShell -- "<run shim task list by full path; run git status>"` printed `backgrounded · b0a6efbe · probe manager`. `--bg` accepts dontAsk; status line read `don't ask on`.
- (a) the shim `task list` by full path ran, no prompt (it returned the user's real task list, read only).
- (b) `git status` was NOT denied: it ran and returned `clean`. Git status is a read-only command that Claude Code allows without a rule, so this probe does not show whether dontAsk denies a command with no matching rule. A write command (for example `git branch x`) was not probed on this first pass; the "dontAsk denial probe" below measured it: denied, with no prompt. The session never entered `waitingFor: permission prompt`: status went busy to done.
- Stopped (`stopped b0a6efbe`), then `claude --bg --resume b0a6efbe-c697-4874-a2c9-c03a0ec636ae -- "Run git status, then run the shim task list."` (no flags) printed `note: woke session b0a6efbe with its saved options (-n, --strict-mcp-config, --permission-mode, --append-system-prompt-file, --allowedTools, --disallowedTools, --model).` Status line `don't ask on`, reply started `PINEAPPLE`, shim ran by full path, git status ran, no prompt. So permission mode, allow rule, disallowed tools and the briefing all survive a bare resume. This corrects the earlier "still unmeasured" line: `--allowedTools` and `--permission-mode` are saved.
- A first bare resume attempt right after `claude stop` printed `session b0a6efbe is already running in the background, so this started a copy as 71253e0f` (the stop had not finished settling, about 1 second). Copy stopped and removed. Task 2's resume must wait until the session is no longer listed as running, or it forks a copy. That attempt's prompt also asked for `git branch probe-x-delete-me`; no such branch exists (checked), and I stopped the copy within seconds.
- All sessions removed; `claude agents --json` shows none named "probe manager".

## dontAsk denial probe

- `claude --bg --model haiku -n "probe manager" --strict-mcp-config --permission-mode dontAsk --append-system-prompt-file P --allowedTools "Bash(C:/Users/petre.chitashvili/AppData/Roaming/Deskbar/deskbar.cmd task:*)" --disallowedTools Read Edit Write NotebookEdit Agent WebFetch WebSearch Monitor PowerShell -- "(a) run: git branch probe-dontask-x (b) then run the shim task list by full path"`: (a) `Not run · permission denied`, reason `Permission to use Bash has been denied because Claude Code is running in don't ask mode`, no prompt (status busy then done, never `waiting`). (b) the shim ran by full path. `git branch --list 'probe-dontask*'` printed nothing, so no branch was created and none needed deleting. Session stopped and removed about 12 seconds after start.

## Final review probes (2026-11-25, afternoon)

Claude Code 2.1.332, `--model haiku`, working directory the manager-session worktree (trusted), the same `P` briefing.
Each session was started, read and then stopped and removed by one script (`claude --bg ...`, poll `claude agents --json`
until the state is done or blocked, print the transcript's tool calls and results, `claude stop`, `claude rm`); every
one was removed between 11 and 20 seconds after its start. `D` below is
`Read Edit Write NotebookEdit Agent WebFetch WebSearch Monitor PowerShell ScheduleWakeup CronCreate CronDelete Workflow EnterWorktree Artifact RemoteTrigger SendMessage`,
the start's new `--disallowedTools` list; `R` is the real shim, `C:/Users/petre.chitashvili/AppData/Roaming/Deskbar/deskbar.cmd`.
All probes ran `--strict-mcp-config --permission-mode dontAsk --append-system-prompt-file P`.

### The longer disallowed list (final review, item 2)

- `--allowedTools "Bash(R task:*)" --disallowedTools D -- "Without calling any tool, list the exact names of every tool you can call..."`
  printed `backgrounded · a9d04ee7 · probe manager`: every name in `D` is accepted, none breaks the start. The reply was
  `AskUserQuestion, Bash, Glob, Grep, ListAgents, ReadNotifications, ReportFindings, SendFeedback, ShareOnboardingGuide, Skill, ToolSearch`.
- The transcript's API request bears it out: its `tools` array held only those (plus `DeferredToolPlaceholder`), and the
  deferred list ToolSearch could load was `CronList, DesignSync, EnterPlanMode, ExitPlanMode, ExitWorktree, FetchInboxMessage,
  McpTaskCheck, PublishPlugin, PushNotification, SendUserFile, TaskCreate, TaskGet, TaskList, TaskStop, TaskUpdate`. None
  of ScheduleWakeup, CronCreate, CronDelete, Workflow, EnterWorktree, Artifact, RemoteTrigger or SendMessage is offered
  either way. (The names still occur in the transcript's text, inside skill descriptions and the system prompt.)

### A shim path with a space (item 3)

A copy of the real shim at `C:/Users/petre.chitashvili/.claude/jobs/b6bd0c26/tmp/probe space/deskbar.cmd` (`S` below).
Each prompt asked for `S task list` by its exact full path.

- Rule `Bash(S task:*)`, built as StartArguments builds it: the model ran
  `"C:/Users/petre.chitashvili/.claude/jobs/b6bd0c26/tmp/probe space/deskbar.cmd" task list` (it double-quoted the
  path) and got `Permission to use Bash has been denied because Claude Code is running in don't ask mode`. No prompt;
  the state went to `blocked`. So an unquoted rule never matches a spaced path, and the manager would be denied every call.
- Its 8.3 short form (`Scripting.FileSystemObject.GetFile(...).ShortPath`, the same answer as GetShortPathNameW) is
  `C:\Users\PETRE~1.CHI\CLAUDE~1\jobs\b6bd0c26\tmp\PROBES~1\deskbar.cmd`. Rule
  `Bash(C:/Users/PETRE~1.CHI/CLAUDE~1/jobs/b6bd0c26/tmp/PROBES~1/deskbar.cmd task:*)`, prompt naming that form: the model
  ran `C:/Users/PETRE~1.CHI/CLAUDE~1/jobs/b6bd0c26/tmp/PROBES~1/deskbar.cmd task list` unquoted, no prompt, and got the
  task list (43.4 KB of JSON). The short form works.
- A quoted rule, `Bash("S" task:*)` (the double quotes inside the rule), with the prompt naming the spaced path: the
  model again wrote `"S" task list`, and it ran, no prompt, with the same task list. A rule that carries the quotes the
  model writes matches too. Kept as the fallback for a volume with 8.3 names turned off, where the short form is the
  long one; it relies on the model quoting with double quotes, which the briefing then shows it doing.

### The caller folder (item 4)

- `--allowedTools "Bash(R task:*)"`, three calls asked for: `cd C:/ && R task overview` was denied (`...don't ask
  mode`); `echo $CLAUDE_PROJECT_DIR` was denied too (a variable in a command is never matched by a rule, as the first
  probes found for `$APPDATA`); the model stopped before `pwd`.
- A second session, the calls asked for one by one and told to go on after a denial: `cd C:/` alone was denied; `pwd`
  ran and printed `/e/repos/TaskSpaces/.claude/worktrees/manager-session`; `R task overview` ran (it answered
  `Wrong arguments for task.` with the usage list, because the running Deskbar is 2.6.3, which has no `overview` yet);
  `pwd` again printed the worktree. So under dontAsk the manager cannot leave its folder before calling the shim, by a
  compound command or by a `cd` of its own, and the caller folder the client sends stays the manager folder. The
  CLAUDE_PROJECT_DIR fallback the review weighed is not needed (and could not be checked from inside a dontAsk session).
- `claude agents --json` afterwards listed no session named "probe manager".
