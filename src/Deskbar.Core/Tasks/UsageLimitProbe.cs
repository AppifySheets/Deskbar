using System.Text.Json;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// ASKING CLAUDE WHETHER THE USAGE LIMIT STILL HOLDS (Deskbar task e948b383, 2026-11-28). Petre: "deskbar should basically
// poll if there's still a limit and resume if not".
//
// The transcripts (UsageLimitSigns) only prove the limit is gone while some session is working. When every session is
// stopped on it and Petre lifts it, nothing writes a transcript line, and nothing would tell Deskbar. So while a session
// is stopped on the limit, Deskbar asks Claude itself, every ProbeEvery, with the smallest request Claude Code can send,
// and treats an answer as proof that the limit is gone (ClaudeSessions feeds it to the keeper beside the transcripts'
// proof). The reset moment the limit's error named stays the fallback: the keeper wakes at that moment anyway.
//
// WHAT THE PROBE IS, measured on Petre's machine on 2026-11-28 (Claude Code 2.1.332), the limit lifted:
//   claude -p --model haiku --no-session-persistence --setting-sources "" --strict-mcp-config --tools ""
//          --disable-slash-commands --output-format json "Reply with the single word OK"
// answered in 2.2 seconds with {"type":"result","subtype":"success","is_error":false,"result":"OK",...}, exit 0, using
// about 3,800 input tokens (mostly a cache write of Claude Code's own system prompt) and 4 output tokens of Haiku,
// priced at $0.0006. Each flag keeps it that small and keeps it out of Deskbar's way:
//   * haiku: the cheapest model. Every limit measured is account wide (UsageLimitSigns' header), so a Haiku answer
//     says the same about the account as an Opus one.
//   * --no-session-persistence: no transcript, so no session for the keeper to adopt or resume, and nothing for the
//     transcript scan to read.
//   * --setting-sources "": none of Petre's settings, so none of his hooks (Deskbar's own SessionStart hook would
//     otherwise brief it as a task session) and no plugins.
//   * --strict-mcp-config, --tools "", --disable-slash-commands: no MCP servers to start, no tools, no skills.
// While the limit holds, a NEW request is refused at once with the limit's error (measured: session 8258f03b's next
// prompt was refused within a second, 2026-11-28 07:26:46), which uses no usage. So the probe costs usage only when it
// succeeds, and its success ends the polling: the stopped sessions are woken and nothing is stopped any more.
// The refused case was not measured from `claude -p` itself; anything that is not that exact success with "OK" as its
// result (an error, a non-zero exit, output that is not the JSON above, a timeout) counts as "the limit still holds", the
// side that wakes nothing, and the next probe or the reset moment tries again.
//
// A PROOF THAT IS WRONG IS CAUGHT. Should the stopped sessions be under a limit Haiku does not share, the probe would
// answer every time and restart them every few minutes until their reset. So a session woken on a proof that hits the
// limit again with the SAME reset marks that reset as one proofs do not lift (TurnError.IgnoreLift, set by
// ClaudeSessions); its sessions then wait for the reset moment, and the probe is not sent for them.
//
// NOT ADOPTED. `claude agents --json` lists a running probe, as an interactive busy session in the temp folder
// (measured 2026-11-28, as for any `claude -p`). The keeper adopts a listed session no task knows only once its hooks
// have said a person is at it (ClaudeSessions.Take, Ruling 17), and the probe runs none, so it never becomes a task.
public static class UsageLimitProbe
{
    /// <summary>How often the probe runs while a session is stopped on the usage limit.</summary>
    public static readonly TimeSpan ProbeEvery = TimeSpan.FromMinutes(5);

    /// <summary>How long one probe may take before it counts as "the limit still holds".</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>The probe's arguments to `claude` (see the header for each).</summary>
    public static readonly IReadOnlyList<string> Arguments =
    [
        "-p", "--model", "haiku", "--no-session-persistence", "--setting-sources", "", "--strict-mcp-config",
        "--tools", "", "--disable-slash-commands", "--output-format", "json", "Reply with the single word OK",
    ];

    /// <summary>
    /// Whether the probe's output says Claude answered: exit 0 and Claude Code's JSON result with subtype "success" and
    /// is_error false. Everything else is "the limit still holds" (see the header).
    /// </summary>
    public static bool Answered(Result<string> output) =>
        output.IsSuccess && IsSuccessResult(output.Value);

    static bool IsSuccessResult(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && Text(root, "type") == "result"
                && Text(root, "subtype") == "success"
                && root.TryGetProperty("is_error", out var error) && error.ValueKind == JsonValueKind.False
                // The word it was asked for (second review, finding C): a refusal reported as a "success" whose result is
                // the limit's text would otherwise pass.
                && Text(root, "result")?.Trim().TrimEnd('.').Equals("OK", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
