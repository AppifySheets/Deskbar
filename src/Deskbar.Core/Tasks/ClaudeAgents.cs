using System.Text.Json;
using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// ONE LIVE CLAUDE CODE SESSION, AS `claude agents --json` LISTS IT. Design:
// docs/superpowers/specs/2026-11-23-tasks-open-claude-sessions-design.md; the measurements are in
// docs/superpowers/notes/2026-11-23-claude-background-probes.md.
//
// The command prints a JSON array with one object per session Claude Code knows is alive. A row measured on this
// machine:
//
//   {"pid":35592,"id":"884c127f","cwd":"E:\\repos\\TaskSpaces","kind":"background","startedAt":1795424165340,
//    "sessionId":"884c127f-9633-40eb-bed7-593800bca40f","name":"Probe ...","status":"busy","state":"working"}
//
//   SessionId  sessionId: the full id, the same across a resume; it is how a task's session is matched to a row.
//   ShortId    id: the eight-character handle `claude attach` takes. Only background rows have one; an interactive row
//              (a terminal or VS Code tab somebody is sitting in) has no "id" and no "state", so this is Maybe.
//   Folder     cwd, kept exactly as given (the normalising happens where folders are compared, as for hook events).
//   Background kind == "background". A kind that is neither "interactive" nor "background" is still read, as not
//              background, so a kind added by a later Claude Code is listed rather than refused.
//   Busy       status == "busy". NOT proof of work: a session just resumed reports busy for up to 30 seconds while idle
//              (measured), so callers apply a grace before trusting it. Nor of a turn in flight: a session whose turn
//              ended with a background job of its own still running stays busy until the job ends (TurnOver).
//   State      state: "working", "done" or "blocked" on a background row; what TurnOver reads.
//   Name       name, or "" when the row has none.
//
// A finished background session (status "idle", state "done") is STILL listed, so presence in the list means "can be
// attached", never "is working".
//
// An unreadable list is a failure, not an empty list. Callers treat a failure as "cannot tell": nothing is ended and
// nothing is resumed, because an empty list would read as "every session died" and wake them all. Unknown fields are
// skipped; a row without a sessionId is an unknown shape and fails the whole parse.
public sealed record ClaudeAgent(string SessionId, Maybe<string> ShortId, string Folder, bool Background, bool Busy, string Name)
{
    /// <summary>
    /// startedAt (milliseconds since 1970), or none when the row has no number there. It tells the newest of several
    /// sessions in the manager's folder (ManagerSession.Next). An init property, so every agent built before it still is.
    /// </summary>
    public Maybe<DateTimeOffset> StartedAt { get; init; } = Maybe<DateTimeOffset>.None;

    /// <summary>
    /// state: "working", "done" or "blocked" on a background row, none on an interactive one (it has no such field). An
    /// init property for the same reason as StartedAt.
    /// </summary>
    public Maybe<string> State { get; init; } = Maybe<string>.None;

    /// <summary>
    /// Whether a process runs the session: the row has a "pid" (task daeaa26c). Claude Code goes on listing a background
    /// session whose process has ended, with no pid and no status (measured 2026-11-26, CLI 2.1.332: six task sessions
    /// whose last turns ended normally on 2026-11-25 were listed so the next morning, state "blocked"). Such a session
    /// cannot take a turn until something resumes or attaches it, and the comment waiter its last Stop started may still
    /// be running as an orphan, which claims a comment from the inbox and hands it to nobody (measured the same day: the
    /// six inboxes were emptied within a minute, no session reacted). True unless the row says otherwise, so every agent
    /// built before this still counts as running.
    /// </summary>
    public bool HasProcess { get; init; } = true;

    /// <summary>
    /// status as the list gives it ("busy", "idle", "waiting"), or none when the row has none. <see cref="Busy"/> reads
    /// only "busy", so a session held by a permission prompt (status "waiting", state "blocked", measured 2026-11-26)
    /// reads as not busy there; SettingsSwitch must not take that for a pause, so it reads this. An init property for
    /// the same reason as StartedAt.
    /// </summary>
    public Maybe<string> Status { get; init; } = Maybe<string>.None;

    /// <summary>
    /// Whether its turn is over, even if the list calls it busy (task 607af1db). A background session whose turn has
    /// ended while a job of its own runs on (a Bash started with run_in_background, a Monitor) is listed
    /// status "busy" until that job ends, but its state is no longer "working" (measured 2026-11-26, CLI 2.1.332: a
    /// probe that started `sleep 240` in the background and ended its turn read busy/working during the turn and
    /// busy/done from the end of it until the sleep finished; a second, whose turn ended with a question, read
    /// busy/blocked instead, and both read busy/working again for the turn the job's end started). So busy alone cannot tell a turn in flight from a
    /// session that is only waiting on its own job; busy with a state of "done" or "blocked" is the second. A busy row
    /// with no state (interactive) counts as working, since nothing says otherwise.
    /// </summary>
    public bool TurnOver => !Busy || State.Match(state => state is "done" or "blocked", () => false);
}

public static partial class ClaudeAgents
{
    public static Result<IReadOnlyList<ClaudeAgent>> Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
                return Result.Failure<IReadOnlyList<ClaudeAgent>>("claude agents did not print a JSON array");

            // Every element must be an object with a session id, or the whole list is not trusted.
            var rows = root.EnumerateArray().Select(Row).ToList();
            return rows.Any(row => row.HasNoValue)
                ? Result.Failure<IReadOnlyList<ClaudeAgent>>("a row of claude agents has no session id")
                : Result.Success<IReadOnlyList<ClaudeAgent>>(rows.Select(row => row.Value).ToList());
        }
        catch (JsonException e)
        {
            return Result.Failure<IReadOnlyList<ClaudeAgent>>($"claude agents did not print JSON: {e.Message}");
        }
    }

    // The short id `claude --bg` prints on its "backgrounded · <id> · <name>" line, or nothing when it printed
    // something else (for example "Workspace not trusted", where no session was started).
    public static Maybe<string> ShortIdFromStart(string stdout) =>
        Backgrounded().Match(stdout) is { Success: true } match ? match.Groups[1].Value : Maybe<string>.None;

    static Maybe<ClaudeAgent> Row(JsonElement row) =>
        row.ValueKind == JsonValueKind.Object && Text(row, "sessionId") is { HasValue: true } session
            ? new ClaudeAgent(session.Value, Text(row, "id"), Text(row, "cwd").GetValueOrDefault(""),
                Text(row, "kind").GetValueOrDefault("") == "background", Text(row, "status").GetValueOrDefault("") == "busy",
                Text(row, "name").GetValueOrDefault("")) { StartedAt = Started(row), State = Text(row, "state"), Status = Text(row, "status"), HasProcess = row.TryGetProperty("pid", out var pid) && pid.ValueKind == JsonValueKind.Number }
            : Maybe<ClaudeAgent>.None;

    // startedAt as a moment, when it is a whole number of milliseconds a DateTimeOffset can hold; else none.
    static Maybe<DateTimeOffset> Started(JsonElement row) =>
        row.TryGetProperty("startedAt", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var ms)
            && ms is >= 0 and <= 253_402_300_799_999
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : Maybe<DateTimeOffset>.None;

    // A non-empty string field, or nothing.
    static Maybe<string> Text(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : Maybe<string>.None;

    // Any run of non-hex characters between the word and the id, not the literal "·": if the output is ever decoded
    // with the wrong code page the separator arrives as "Â·" (it did once, see ClaudeCli), and "Â" is a letter, so
    // \W would not cover it. The id must still be found.
    [GeneratedRegex(@"backgrounded[^0-9a-f]+([0-9a-f]{8})\b")]
    private static partial Regex Backgrounded();
}
