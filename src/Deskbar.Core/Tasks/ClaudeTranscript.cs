using System.Text.Json;
using CSharpFunctionalExtensions;

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
        LastEntry(jsonLines) is { } last && !last.Finished;

    // WHETHER THE LAST TURN DIED ON AN API ERROR, and if it was a usage limit, when that limit resets (task daeaa26c).
    //
    // Claude Code writes an API error as an assistant entry of its own with "isApiErrorMessage": true, a made-up model
    // ("<synthetic>") and stop_reason "stop_sequence", so WasCutOff already reads it as cut off. What it could not tell is
    // the two things that make such a session different from one killed mid-reply:
    //   * A TURN THAT ENDS ON AN API ERROR RUNS NO STOP HOOK. Claude Code runs StopFailure in its place and ignores that
    //     hook's exit code (hooks docs, "StopFailure"), so Deskbar's comment waiter, which each Stop starts, is not started
    //     again. The waiter before it may already have fired (it is what woke the session for the turn that failed), so
    //     the gen file says "a waiter is alive" while none is, and a comment put in the inbox is read by nobody.
    //     Measured on 2026-11-26, session 152ba85d: a comment woke it at 00:08 through its waiter, the turn hit the
    //     weekly limit, no Stop hook ran, and the manager's comments at 09:29 and 09:56 sat in its inbox until Petre typed
    //     in its window at 10:56.
    //   * A USAGE LIMIT SAYS WHEN IT ENDS. Every such entry measured on Petre's machine (509 of 509, 2026-11-26) carries
    //     "apiErrorParams": {"rate_limit_info": {"resetsAt": <unix seconds>, ...}} (also under "quotaLimits"), the moment
    //     the text's "resets 6am (Asia/Tbilisi)" names. Waking the session before then only hits the limit again.
    // Only the LAST user or assistant entry decides, the same entry WasCutOff reads: an error followed by a reply that
    // finished is history, not the session's state.
    public static Maybe<TurnError> LastTurnError(IEnumerable<string> jsonLines) =>
        LastEntry(jsonLines) is { Error: { } error } ? error : Maybe<TurnError>.None;

    // THE MODEL THE LAST REPLY CAME FROM (the Inbox page's picker, for a session whose model Deskbar did not set and whose
    // hooks did not name one). Every assistant entry carries message.model ("claude-haiku-5-5"; measured 2026-11-25,
    // docs/superpowers/specs/2026-11-25-session-model-pick-design.md), and after a switch to another model the later
    // entries name the new one (measured 2026-11-26 on a session resumed with --model sonnet). The field is
    // Claude Code's own and undocumented, so the picker shows it as "last reply from", never as a setting. An API error's
    // entry names a made-up "<synthetic>" model (see LastTurnError) and a subagent's sidechain entries name the
    // subagent's, so both are passed over.
    public static Maybe<string> LastModel(IEnumerable<string> jsonLines) =>
        jsonLines.Select(ModelOf).LastOrDefault(model => model is not null) is { } found ? found : Maybe<string>.None;

    // WHETHER A SWITCH'S COPY HAS ANSWERED (SettingsSwitch.Next, the JOINED phase): an assistant entry from a real reply
    // (as LastModel counts one) written after the copy joined its task. A copy's transcript starts with the whole old
    // conversation, written before the copy was listed and so before it joined; only its own turns come after. An API
    // error's entry (a model the account cannot use, a usage limit) and a subagent's are not a reply, and an entry with no
    // timestamp says nothing.
    public static bool RepliedSince(IEnumerable<string> jsonLines, DateTimeOffset since) =>
        jsonLines.Any(line => ReplyAt(line) is { } at && at > since);

    static DateTimeOffset? ReplyAt(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            return ModelIn(root) is not null && TimestampOf(root) is { HasValue: true } at ? at.Value : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static string? ModelOf(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            using var document = JsonDocument.Parse(line);
            return ModelIn(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static string? ModelIn(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "assistant"
        && !IsTrue(root, "isSidechain") && !IsTrue(root, "isApiErrorMessage")
        && At(root, "message", "model") is { ValueKind: JsonValueKind.String } model
        && model.GetString() is { Length: > 0 } name && name != "<synthetic>"
            ? name
            : null;

    static Turn? LastEntry(IEnumerable<string> jsonLines) =>
        jsonLines
            .Select(Entry)
            .Where(e => e is not null)
            .LastOrDefault();

    // What matters about one line: whether it is a user or assistant entry and, if so, whether it ends a turn, and whether
    // it is Claude Code's own report of an API error (Error, see LastTurnError).
    sealed record Turn(bool Finished, TurnError? Error = null);

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
                "assistant" when IsTrue(root, "isApiErrorMessage") => new Turn(false, new TurnError(ResetsAtOf(root)) { At = TimestampOf(root) }),
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
    internal static readonly IReadOnlyList<string> LocalCommandPrefixes =
        ["<command-name>", "<command-message>", "<local-command-stdout>", "<local-command-stderr>", "<local-command-caveat>"];

    // A usage limit's reset moment, from apiErrorParams.rate_limit_info.resetsAt or else quotaLimits.resetsAt (unix
    // seconds, see LastTurnError). An error that names none (an overloaded server, a lost connection) has no reset.
    static Maybe<DateTimeOffset> ResetsAtOf(JsonElement root) =>
        new[] { At(root, "apiErrorParams", "rate_limit_info", "resetsAt"), At(root, "quotaLimits", "resetsAt") }
            .Where(value => value is { ValueKind: JsonValueKind.Number })
            .Select(value => value!.Value.TryGetInt64(out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : (DateTimeOffset?)null)
            .FirstOrDefault(at => at is not null) is { } found ? found : Maybe<DateTimeOffset>.None;

    // The entry's "timestamp", when it is a moment.
    internal static Maybe<DateTimeOffset> TimestampOf(JsonElement root) =>
        root.TryGetProperty("timestamp", out var value) && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : Maybe<DateTimeOffset>.None;

    // The element at the end of a chain of object properties, or null when any step is missing or not an object.
    internal static JsonElement? At(JsonElement root, params string[] names) =>
        names.Aggregate((JsonElement?)root, (at, name) =>
            at is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var next) ? next : null);

    internal static bool IsTrue(JsonElement root, string name) =>
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

/// <summary>
/// The last turn of a session ended on an API error (ClaudeTranscript.LastTurnError). <paramref name="ResetsAt"/> is set
/// when the error was a usage limit: the moment the limit lifts.
/// </summary>
public sealed record TurnError(Maybe<DateTimeOffset> ResetsAt)
{
    /// <summary>When Claude Code wrote the error (the entry's timestamp), or none when the entry has none.</summary>
    public Maybe<DateTimeOffset> At { get; init; } = Maybe<DateTimeOffset>.None;
}
