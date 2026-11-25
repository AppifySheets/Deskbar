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
- Only the flags I started with were listed as saved. `--allowedTools` and `--permission-mode` were not in that list (not started with them in that session, so not shown as proof either way; Task 2 should check).

## So

- Start: `--bg --model fable -n "manager" --strict-mcp-config --append-system-prompt-file <briefing path> --allowedTools "Bash(<expanded shim path with forward slashes> task:*)" --disallowedTools Read Edit Write NotebookEdit Agent WebFetch WebSearch Monitor PowerShell -- "<first prompt>"`.
- Resume: `--bg --resume <FULL session id> -- "<prompt>"`, with no other flags. Repeating flags forks a copy.
- Allowed-tools rule: the full shim path form works; the short `deskbar.cmd` form matches but the command is not found in Git Bash. The briefing must give the literal path, never `$APPDATA`.
- Disallow PowerShell too, or the manager can bypass the Bash rule (auto mode is the default for these sessions).
- Still unmeasured: whether `--allowedTools` and `--permission-mode` survive a bare resume.
