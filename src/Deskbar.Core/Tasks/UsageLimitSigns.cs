using System.Text.Json;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// WHAT CLAUDE CODE'S TRANSCRIPTS SAY ABOUT THE ACCOUNT'S USAGE LIMIT (Deskbar task e948b383, 2026-11-28).
//
// The keeper wakes a session stopped on the usage limit at the reset moment the limit's error named (SessionKeeper,
// task daeaa26c). That moment is only the latest the limit can last. Petre can lift it sooner, and on 2026-11-28 he did:
// the five-hour limit stopped several sessions at 07:25 UTC with "resets 14:10", he had usage credits granted at about
// 07:28, and sessions he or the manager were talking to worked again from then on, while the sessions Deskbar was
// holding waited for 14:10. His words: "i have used the reset limit function and that should, through deskbar, reach all
// sessions", and then "deskbar should basically poll if there's still a limit and resume if not".
//
// Two sources of proof that the limit is gone feed the keeper (ClaudeSessions combines them):
//   * UsageLimitProbe: while a session is stopped on the limit, Deskbar asks Claude itself every few minutes. This is
//     the poll Petre asked for, and the only source that works when every session is stopped.
//   * This file: a "release" note in any recent transcript, which is quicker whenever some session works.
//
// THE RELEASE NOTE, measured on Petre's machine on 2026-11-28 (Claude Code 2.1.332). When a session that was warned about
// the limit sends its next request and the limit is gone, Claude Code writes a meta user entry
// {"isMeta": true, "usageLimitNote": "release"} ("[Earlier usage-limit notes no longer apply. Continue working
// normally...]") before the reply. Session 2c94ab8f wrote one at 07:39:59, and others at 07:28:39 and 07:30:40, minutes
// after the 07:25 limit. It is Claude Code's own judgment, so it is trusted as is.
//
// WHY AN ORDINARY REPLY IS NOT PROOF. When the limit is reached in the middle of a turn, Claude Code lets the turn run
// on as a GRACE ALLOWANCE and writes a "wrap_up" note ({"usageLimitNote": "wrap_up"}, "[Usage limit reached; a short
// grace allowance remains, then this turn is cut off without warning...]") after the reply that brought the news.
// Replies in that allowance are answered WHILE the limit holds: over 41 wrap_up notes measured, replies went on from 3
// seconds to 30 minutes after the note. Telling them apart needs the note, which can lie outside the transcript tail
// that is read, and a reply streamed across the limit moment before its session hears of it looks the same as one after
// a lift (review findings 1 and 2). A false proof restarts every stopped session while the limit holds, three times in
// ten minutes ends in the keeper's sticky give-up, so replies are left out and the probe covers what they would.
//
// THE OTHER KIND OF LIMIT STOP. A session told to wrap up often finishes its turn properly and stops, saying so ("Your
// usage limit was reached, so I stopped before building anything", session 2c94ab8f). No API error is written, so
// ClaudeTranscript.LastTurnError sees a finished turn and the keeper sees nothing to wake. StoppedOnNote finds that case:
// the last turn finished after a wrap_up note with no release after it.
//
// No file IO lives here: the caller supplies lines (the tail of each transcript).
public static class UsageLimitSigns
{
    /// <summary>
    /// The release notes in one transcript, oldest first, and the latest usage-limit error in it. Read once per file
    /// version and kept by the caller.
    /// </summary>
    public static TranscriptLimitSigns Read(IEnumerable<string> jsonLines)
    {
        var signs = Signs(jsonLines).ToList();
        var releases = signs.Where(s => s.Kind == SignKind.Release).Select(s => s.At).OrderBy(at => at).ToList();
        var latestError = signs.Where(s => s.Kind == SignKind.LimitError).MaxBy(s => s.At);
        return new TranscriptLimitSigns(releases,
            latestError is null ? Maybe<LimitErrorSign>.None : new LimitErrorSign(latestError.At, latestError.ResetsAt!.Value));
    }

    /// <summary>
    /// When the session's last turn FINISHED after a wrap_up note with no release after it (see the header), the
    /// moment of that note; None otherwise. A turn that died on the limit's error is ClaudeTranscript.LastTurnError's
    /// case, not this one.
    /// </summary>
    public static Maybe<DateTimeOffset> StoppedOnNote(IReadOnlyList<string> jsonLines) =>
        Signs(jsonLines).Where(s => s.Kind is SignKind.WrapUp or SignKind.Release).LastOrDefault() is { Kind: SignKind.WrapUp } wrapUp
        && !ClaudeTranscript.WasCutOff(jsonLines)
        && ClaudeTranscript.LastTurnError(jsonLines).HasNoValue
            ? wrapUp.At
            : Maybe<DateTimeOffset>.None;

    /// <summary>
    /// A session that HOLDS ITSELF on a limit that is gone (task e948b383, measured 2026-11-28 on session a8c3dd64): its
    /// turn died on the limit's error naming 14:10; from 07:40 on Deskbar's stalled-task asks woke it and every reply went
    /// through, yet each time it answered "It's waiting for the usage limit to lift at 14:10" and stopped, because the
    /// error's text was still in its conversation. Its last turn finished, so neither the keeper (no error last) nor
    /// StoppedOnNote (no wrap_up note) sees it. The proof is its own: a real reply AFTER the error in its own transcript
    /// means its requests are answered again (a new turn under the limit is refused at once, never answered, and the
    /// grace allowance belongs to a turn already running when the limit came, which the error ends).
    /// That error (its moment and reset) when: a reply that is not an error, not a subagent's and not made up by Claude
    /// Code follows the latest limit error, and the last turn finished with no API error. None otherwise. The caller
    /// keeps only one whose reset is still ahead: past it, nothing holds the session back.
    /// </summary>
    public static Maybe<LimitErrorSign> HeldAfterLift(IReadOnlyList<string> jsonLines)
    {
        var lastError = jsonLines
            .Select((line, index) => (Line: line, Index: index))
            .Where(item => item.Line.Contains("isApiErrorMessage", StringComparison.Ordinal))
            .Select(item => (Sign: Sign(item.Line), item.Index))
            .LastOrDefault(item => item.Sign is { Kind: SignKind.LimitError });
        if (lastError.Sign is not { } error) return Maybe<LimitErrorSign>.None;
        var after = jsonLines.Skip(lastError.Index + 1).ToList();
        // The last real reply's text, when it has some (a reply that only calls a tool has none).
        var lastWords = after.Select(ReplyText).LastOrDefault(text => !string.IsNullOrWhiteSpace(text));
        // Second review, findings 1 and 2: a reply after the error alone is also what a session that WORKED after a lift
        // writes (one the keeper resumed, one Petre typed into), and the held nudge's own answer is one more reply, so the
        // rule would fire on healthy sessions and again after every Deskbar restart. So it fires only when the session's
        // last words are about the limit (measured: "It's waiting for the usage limit to lift at 14:10"), and never once
        // the session has been told the limit is over since the error: Deskbar's resume or held nudge (they share
        // ToldMarker) or Claude Code's own release note.
        return lastWords is { } words && words.Contains("limit", StringComparison.OrdinalIgnoreCase)
            && !after.Any(line => line.Contains(ToldMarker, StringComparison.OrdinalIgnoreCase))
            && !Signs(after).Any(sign => sign.Kind == SignKind.Release)
            && !ClaudeTranscript.WasCutOff(jsonLines)
            && ClaudeTranscript.LastTurnError(jsonLines).HasNoValue
                ? new LimitErrorSign(error.At, error.ResetsAt!.Value)
                : Maybe<LimitErrorSign>.None;
    }

    /// <summary>
    /// The words every message of Deskbar's that says the limit is over carries (SessionKeeper.LimitContinueMessage and
    /// SessionKeeper.HeldNudge), so a transcript shows the session was told (HeldAfterLift).
    /// </summary>
    public const string ToldMarker = "not wait for the reset time its message named";

    // A real reply's text: an assistant entry that is not an API error, not a subagent's and not made up ("<synthetic>"),
    // its text blocks joined. Null for anything else; "" for a real reply with no text.
    static string? ReplyText(string line)
    {
        if (!line.Contains("\"assistant\"", StringComparison.Ordinal) || line.Contains("isApiErrorMessage", StringComparison.Ordinal)) return null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var real = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "assistant"
                && !ClaudeTranscript.IsTrue(root, "isSidechain")
                && ClaudeTranscript.At(root, "message", "model") is { ValueKind: JsonValueKind.String } model
                && model.GetString() != "<synthetic>";
            return !real ? null
                : ClaudeTranscript.At(root, "message", "content") is { ValueKind: JsonValueKind.Array } content
                    ? string.Join(" ", content.EnumerateArray()
                        .Where(block => block.ValueKind == JsonValueKind.Object && block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                        .Select(block => block.GetProperty("text").GetString()))
                    : "";
        }
        catch (JsonException)
        {
            return null;
        }
    }

    enum SignKind { WrapUp, Release, LimitError }

    sealed record SignLine(SignKind Kind, DateTimeOffset At, DateTimeOffset? ResetsAt = null);

    // Only lines that can be about the limit are parsed: a tail is up to 256 KB and this runs over every recent transcript
    // (and, for StoppedOnNote, on every poll for each open task's session).
    static IEnumerable<SignLine> Signs(IEnumerable<string> jsonLines) =>
        jsonLines
            .Where(line => line.Contains("usageLimitNote", StringComparison.Ordinal) || line.Contains("isApiErrorMessage", StringComparison.Ordinal))
            .Select(Sign)
            .Where(sign => sign is not null)
            .Select(sign => sign!);

    // What one line says about the limit, or null when it says nothing (or has no timestamp to place it by).
    static SignLine? Sign(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || ClaudeTranscript.IsTrue(root, "isSidechain")) return null;
            if (ClaudeTranscript.TimestampOf(root) is not { HasValue: true } at) return null;
            var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var note = root.TryGetProperty("usageLimitNote", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            return (type, note) switch
            {
                ("user", "wrap_up") => new SignLine(SignKind.WrapUp, at.Value),
                ("user", "release") => new SignLine(SignKind.Release, at.Value),
                ("assistant", _) when ClaudeTranscript.IsTrue(root, "isApiErrorMessage") =>
                    ClaudeTranscript.ResetsAtOf(root) is { HasValue: true } resets ? new SignLine(SignKind.LimitError, at.Value, resets.Value) : null,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>A usage-limit error in a transcript: when it was written and when the limit it names resets.</summary>
public sealed record LimitErrorSign(DateTimeOffset At, DateTimeOffset ResetsAt);

/// <summary>What one transcript says about the account's usage limit (UsageLimitSigns.Read).</summary>
public sealed record TranscriptLimitSigns(IReadOnlyList<DateTimeOffset> Releases, Maybe<LimitErrorSign> LatestError)
{
    /// <summary>The latest release note, the latest moment this transcript proves the limit gone.</summary>
    public Maybe<DateTimeOffset> Lifted => Releases.TryLast();
}

/// <summary>
/// What every recent transcript together says about the account's usage limit: the latest proof that it is gone, and the
/// latest limit error anywhere (for a stop that names no reset of its own, SessionKeeper.NoteStopWakes). The app adds the
/// probe's proof to <see cref="Lifted"/> (ClaudeSessions.LimitLifted).
/// </summary>
public sealed record LimitSigns(Maybe<DateTimeOffset> Lifted, Maybe<LimitErrorSign> LatestError)
{
    public static readonly LimitSigns None = new(Maybe<DateTimeOffset>.None, Maybe<LimitErrorSign>.None);

    /// <summary>All the transcripts' signs together.</summary>
    public static LimitSigns Of(IEnumerable<TranscriptLimitSigns> transcripts) =>
        transcripts.Aggregate(None, (all, one) => new LimitSigns(
            Later(all.Lifted, one.Lifted),
            new[] { all.LatestError, one.LatestError }.Where(e => e.HasValue).Select(e => e.Value).MaxBy(e => e.At) is { } latest
                ? latest : Maybe<LimitErrorSign>.None));

    /// <summary>The later of two moments, either of which may be missing.</summary>
    public static Maybe<DateTimeOffset> Later(Maybe<DateTimeOffset> a, Maybe<DateTimeOffset> b) =>
        a.HasValue && b.HasValue ? (a.Value >= b.Value ? a : b) : a.HasValue ? a : b;

    /// <summary>
    /// Whether the limit has been proved gone since <paramref name="stoppedAt"/>: a session stopped on the limit then may
    /// be woken now, whatever reset moment its error named.
    /// </summary>
    public bool LiftedSince(DateTimeOffset stoppedAt) => Lifted.HasValue && Lifted.Value > stoppedAt;
}
