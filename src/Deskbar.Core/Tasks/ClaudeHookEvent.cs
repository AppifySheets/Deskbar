using System.Text.Json;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// ONE CLAUDE CODE HOOK, AS DESKBAR READS IT. Design: docs/superpowers/specs/2026-11-22-tasks-board-design.md
// ("Claude sessions", "Non-interactive sessions"); plan Task 7; the measurements behind every rule here are in
// docs/superpowers/notes/2026-11-22-claude-hook-probes.md.
//
// Claude Code runs Deskbar's hook scripts (claude-hook.cmd and claude-task-hook.cmd, ClaudeSkill in the app) on five
// events and hands each run a JSON object on stdin. The script passes it on to the running Deskbar as `claude-hook --json <text>`, together with two
// variables from the hook's environment, and this record is what comes out:
//
//   Event      hook_event_name: SessionStart, UserPromptSubmit, Notification, Stop or SessionEnd.
//   SessionId  session_id: the same across a resume, which is how a resumed session finds its task again.
//   Folder     cwd, kept EXACTLY as given. It is not the only form a folder arrives in: the probes saw cwd in 8.3 short
//              form (C:\Users\PETRE~1.CHI\...) when the session was started with a short path, and the script's
//              CLAUDE_PROJECT_DIR (the verb's --from) always uses forward slashes. Matching either against a VS Code
//              window's title is Task 8's job, which normalises both sides (long path, one separator, case) first, so
//              nothing is lost by storing it raw here.
//   Source     source, SessionStart only: startup, resume, clear, compact or fork.
//   Headless   whether nobody is sitting in this session (below).
//
// Unknown fields are skipped, not refused: Claude Code adds fields between versions (the probes saw a resume add four,
// and a session started inside another add scratchpad_dir). Only a missing event name or session id is refused,
// because without those there is nothing to act on, and the verb then exits 1 with nothing printed.
//
// HEADLESS (Ruling 33). The hook's JSON says nothing about whether the session is interactive, so the environment
// decides, and the script passes two variables through:
//
//   * CLAUDE_CODE_SESSION_ATTENDED, computed by Claude Code for every session and never inherited: "1" when someone is
//     at it. It is the main signal because the entrypoint lies in exactly the case that matters: a `claude -p` started
//     from inside a VS Code session inherits CLAUDE_CODE_ENTRYPOINT=claude-vscode but gets ATTENDED=0 (probe run 6).
//     Exactly "1" is attended; any other value that arrived is headless.
//   * When ATTENDED did not arrive (an older Claude Code without it): cmd passes an unset variable as an empty string
//     inside a batch file, or as the literal %CLAUDE_CODE_SESSION_ATTENDED% when a command runs at a prompt, so both
//     count as "not there". Then CLAUDE_CODE_ENTRYPOINT decides: sdk-cli (`claude -p`), sdk-ts and sdk-py (the Agent
//     SDKs), and no entrypoint at all, are headless; anything else (cli, claude-vscode, ...) is a person's session.
//
//   Transcript transcript_path: the session's JSONL transcript, on every event. Read only by Notification and Stop,
//              for the session's name (SessionTitle), which picks the asking window out of several on one folder.
public sealed record ClaudeHookEvent(string Event, string SessionId, string Folder, Maybe<string> Source, bool Headless,
    Maybe<string> Transcript = default)
{
    static readonly string[] HeadlessEntrypoints = ["sdk-cli", "sdk-ts", "sdk-py"];

    public static Result<ClaudeHookEvent> Parse(string json, string? entrypoint, string? attended, string? job = null)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Result.Failure<ClaudeHookEvent>("the hook's JSON is not an object");

            var hookEvent = Text(root, "hook_event_name");
            var session = Text(root, "session_id");
            if (hookEvent.HasNoValue) return Result.Failure<ClaudeHookEvent>("the hook's JSON has no hook_event_name");
            if (session.HasNoValue) return Result.Failure<ClaudeHookEvent>("the hook's JSON has no session_id");

            return new ClaudeHookEvent(hookEvent.Value, session.Value, Text(root, "cwd").GetValueOrDefault(""),
                Text(root, "source"), IsHeadless(entrypoint, attended, job), Text(root, "transcript_path"));
        }
        catch (JsonException e)
        {
            return Result.Failure<ClaudeHookEvent>($"the hook's JSON does not parse: {e.Message}");
        }
    }

    // A non-empty string field, or nothing: a number or an empty string where a name belongs is as good as missing.
    static Maybe<string> Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : Maybe<string>.None;

    // A BACKGROUND SESSION (`claude --bg`) runs its hooks with ATTENDED=0 although a person can attach to it (probe notes,
    // "What a background session's hooks see"), so the flag alone would drop every one of its hooks. Its hooks carry
    // CLAUDE_JOB_DIR, which a `claude -p` run does not have: a job directory that arrived means attended. It arrives the
    // way the other variables do (empty or a literal %NAME% when unset), hence the same Arrived test.
    static bool IsHeadless(string? entrypoint, string? attended, string? job) =>
        Arrived(job) is not null ? false
        : Arrived(attended) is { } given
            ? given != "1"
            : Arrived(entrypoint) is not { } entry || HeadlessEntrypoints.Contains(entry, StringComparer.OrdinalIgnoreCase);

    // A variable's value as the script passed it, or null when the variable was not set: empty (cmd in a batch file) or
    // the literal %NAME% (cmd at a prompt).
    static string? Arrived(string? value) =>
        string.IsNullOrWhiteSpace(value) || (value.Length > 1 && value.StartsWith('%') && value.EndsWith('%')) ? null : value.Trim();
}
