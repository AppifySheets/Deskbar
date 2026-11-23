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
            return type.GetString() switch
            {
                "user" => new Turn(false),
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
}
