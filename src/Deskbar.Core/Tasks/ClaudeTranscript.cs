using System.Text.Json;

namespace Deskbar.Core.Tasks;

// WHERE A CLAUDE SESSION'S TRANSCRIPT IS, AND WHETHER ITS LAST TURN WAS CUT OFF. Later tasks use the first to decide
// whether a session can be resumed at all (no transcript file, nothing to resume: "expired", which a caller checks
// with File.Exists on PathOf) and the second to decide what to say when it is resumed (a cut-off turn gets the
// "Continue from where you left off" message, a finished one gets none).
//
// No file IO lives here, so both rules are tested without a disk: PathOf builds a string, WasCutOff reads lines the
// caller supplies.
public static class ClaudeTranscript
{
    // <claudeHome>\projects\<encoded folder>\<sessionId>.jsonl. Claude Code names the project folder after the working
    // folder with every character that is not an ASCII letter or digit turned into '-' (measured):
    //   E:\repos\TaskSpaces                                  -> E--repos-TaskSpaces
    //   E:\repos\TaskSpaces\.claude\worktrees\probe-bg       -> E--repos-TaskSpaces--claude-worktrees-probe-bg
    // `claudeHome` is the folder that holds `projects`, normally %USERPROFILE%\.claude.
    public static string PathOf(string claudeHome, string folder, string sessionId) =>
        Path.Combine(claudeHome, "projects", Encode(folder), sessionId + ".jsonl");

    // Every place the transcript can be, most likely first: PathOf for the folder the session joined from, then the same
    // file name in each of Claude Code's other project folders (`projectFolders`, the full paths of the folders under
    // <claudeHome>\projects, as the caller lists them). The caller takes the first that exists.
    //
    // Why PathOf alone is not enough (task 87f899ad, 2026-11-24): Claude Code files the transcript under the folder the
    // session is working in, and a session that enters a git worktree after joining its task keeps writing there, so
    // the transcript of a session that joined from E:\repos\TaskSpaces lives under
    // E--repos-TaskSpaces--claude-worktrees-<name>. Measured on Petre's machine: 8 of 37 task sessions had their
    // transcript only in another project folder, and the thread showed them "expired" (no Open) while
    // `claude --bg --resume <id>`, run from the joined folder, woke one of them fine and put it back in its worktree.
    // The keeper read the same miss as "transcript gone" and gave up on the task.
    public static IEnumerable<string> Candidates(string claudeHome, string folder, string sessionId, IEnumerable<string> projectFolders)
    {
        var first = PathOf(claudeHome, folder, sessionId);
        return projectFolders
            .Select(project => Path.Combine(project, sessionId + ".jsonl"))
            .Where(path => !string.Equals(path, first, StringComparison.OrdinalIgnoreCase))
            .Prepend(first);
    }

    static string Encode(string folder) =>
        new(folder.Select(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' ? c : '-').ToArray());

    // True when the transcript stops in the middle of a turn, as it does when Claude Code is killed mid-reply.
    // Only the LAST user or assistant entry decides: the turn is finished exactly when that entry is an assistant
    // message with stop_reason "end_turn". Anything else is a turn with no end:
    //   * assistant stop_reason tool_use: a tool was asked for and its result never came back
    //   * user with a tool_result: the result arrived and nothing answered it
    //   * user with a plain prompt: no reply at all (the measured crash, probes note "A crash mid-reply": the
    //     transcript ended with the user prompt and only metadata lines after it)
    //
    // Everything else in the file is metadata that follows a finished turn or is interleaved with one (system
    // stop_hook_summary and turn_duration, cost-state, last-prompt, ai-title, agent-name, attachment, api-request,
    // api-request-blob) and is skipped. Only `type` decides: api-request-blob carries a message with a role, yet it is
    // not a user or assistant entry. Lines that do not parse are skipped too, so a half-written last line is no
    // verdict. An empty or unreadable transcript is "not cut off": there is nothing to continue.
    //
    // Some type:"user" entries are not a prompt waiting for a reply, and reading them as one would make Deskbar tell
    // a session Petre stopped on purpose to carry on (final review, item 1). Shapes copied from real transcripts in
    // %USERPROFILE%\.claude\projects (2026-11-23):
    //   * an Esc interrupt is written as a user entry whose text is "[Request interrupted by user]" or
    //     "[Request interrupted by user for tool use]" (a text block in a list, after the assistant's partial
    //     reply). Petre stopped that turn himself, so it counts as FINISHED: the session is resumed without the
    //     "continue" message and waits for him.
    //   * isMeta:true entries (the <local-command-caveat> before a local slash command, a skill's injected body),
    //     isCompactSummary:true (the summary /compact writes) and isSidechain:true (a subagent's turns, which older
    //     versions wrote into the parent's file; assistant ones too) are skipped like metadata.
    //   * a local slash command (/model, /cost, /clear, /compact) writes its own name and its output as plain user
    //     entries WITHOUT isMeta: "<command-name>/model</command-name>..." and
    //     "<local-command-stdout>Set model to ...</local-command-stdout>" (measured). They run without a turn, so
    //     they are skipped too: the turn before them decides. A skill invoked as a slash command (/summary) also
    //     starts with <command-message>/<command-name>, and one cut off before its reply is then read as finished;
    //     that errs toward not continuing, the safe side.
    public static bool WasCutOff(IEnumerable<string> jsonLines) =>
        jsonLines
            .Select(Entry)
            .Where(e => e is not null)
            .LastOrDefault() is { } last && !last.Finished;

    // What matters about one line: whether it is a user or assistant entry and, if so, whether it ends a turn.
    sealed record Turn(bool Finished);

    static Turn? Entry(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type)) return null;
            if (IsTrue(root, "isSidechain")) return null;
            return type.GetString() switch
            {
                "user" when IsTrue(root, "isMeta") || IsTrue(root, "isCompactSummary") => null,
                "user" => TextOf(root) switch
                {
                    { } text when text.StartsWith("[Request interrupted by user", StringComparison.Ordinal) => new Turn(true),
                    { } text when LocalCommandPrefixes.Any(p => text.StartsWith(p, StringComparison.Ordinal)) => null,
                    _ => new Turn(false),
                },
                "assistant" => new Turn(root.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.Object
                    && message.TryGetProperty("stop_reason", out var reason)
                    && reason.ValueKind == JsonValueKind.String
                    && reason.GetString() == "end_turn"),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The openings a local slash command's own entries start with (see WasCutOff). The caveat is normally isMeta, and
    // is listed in case a version writes it without the flag.
    static readonly IReadOnlyList<string> LocalCommandPrefixes =
        ["<command-name>", "<command-message>", "<local-command-stdout>", "<local-command-stderr>", "<local-command-caveat>"];

    static bool IsTrue(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    // A user entry's text: the content itself when it is a string, else the first text block of the list. A list
    // with only tool_result blocks has no text and returns null (it is a reply waiting to be answered).
    static string? TextOf(JsonElement root) =>
        !root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var content)
            ? null
            : content.ValueKind switch
            {
                JsonValueKind.String => content.GetString(),
                JsonValueKind.Array => content.EnumerateArray()
                    .Where(b => b.ValueKind == JsonValueKind.Object
                        && b.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "text"
                        && b.TryGetProperty("text", out var x) && x.ValueKind == JsonValueKind.String)
                    .Select(b => b.GetProperty("text").GetString())
                    .FirstOrDefault(),
                _ => null,
            };
}
