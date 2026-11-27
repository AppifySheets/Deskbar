using System.Text;
using System.Text.Json;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// WHERE A SESSION'S TRANSCRIPT STOOD WHEN IT ASKED A PERMISSION QUESTION, and whether it has moved on since (task 6306d261,
// the Allow / Deny card for sessions in a terminal or VS Code).
//
// A background session's card leaves the page when `claude agents --json` no longer lists the session as blocked
// (PermissionKeeper). That list holds background sessions ONLY (read on 2026-11-27: 32 entries, every one "kind":
// "background"), so for a session in a terminal or in VS Code it would never show "blocked" and the card would go about ten
// seconds after it came. Such a session's card is watched through its transcript instead, which the hook's JSON names
// (transcript_path). Measured on 2026-11-27 with Claude Code 2.1.332, an interactive session in a pseudo-console, a
// PermissionRequest hook sleeping 40 s:
//
//   * the terminal's own dialog was drawn at the same moment the hook started, so the terminal is never held up;
//   * while the dialog waited NOTHING was appended to the transcript, apart from one "ai-title" line (the session's title
//     being generated, unrelated to the question);
//   * the moment the dialog was answered in the terminal (or the hook allowed), the tool's lines were appended at once,
//     beginning file-history-snapshot, tools-used, assistant, user: the tool_use itself is written only after the decision;
//   * answering in the terminal did NOT end the hook: it slept on to its own end, so the hook's exit cannot be the signal.
//
// A SUBAGENT'S QUESTION is watched in the subagent's own transcript: current Claude Code writes a subagent's turns to
// <session transcript without .jsonl>\subagents\agent-<agent_id>.jsonl (seen on this machine, 2026-11-27, and ClaudeChat
// says the same), so the main transcript would stay still while the subagent's question was answered, or move on while
// it still waited. When that file is not there the question has no mark and goes only with the session's next event, a
// newer question or its expiry: a card that stays too long, never one that goes too soon.
//
// So a question is settled once a line whose top-level "type" is "assistant" or "user" lands past the mark. Only those two
// count: the transcript also takes bookkeeping lines (ai-title, last-prompt, mode, attachment, ...) that say nothing about
// the question, and a title line did arrive during a wait. A denial in the terminal or Esc writes a "user" line too (the
// tool's refusal or the interruption). A transcript that is gone or shorter than the mark counts as moved on: the session
// is not the one that asked.
//
// Read-only, shared with Claude Code's writer (FileShare.ReadWrite | Delete), and only the bytes past the mark: the length
// is checked first, so a question whose transcript has not grown costs one file-size look per tick.
public sealed record TranscriptMark(string Path, long Length)
{
    // The lines that mean the conversation went on: the tool ran or was refused, or Claude or the user spoke.
    static readonly string[] Moving = ["assistant", "user"];

    /// <summary>
    /// The transcript a question is answered in: the session's own, or for a subagent's question the subagent's file
    /// beside it (the header).
    /// </summary>
    public static Maybe<string> For(Maybe<string> sessionTranscript, Maybe<string> agentId) =>
        agentId.HasNoValue ? sessionTranscript
            : sessionTranscript.Bind(t => Result.Try(() => System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(t) ?? "", System.IO.Path.GetFileNameWithoutExtension(t),
                    "subagents", $"agent-{agentId.Value}.jsonl")).AsMaybe());

    /// <summary>The transcript's length now, or None when there is no transcript to watch (no path, or no file).</summary>
    public static Maybe<TranscriptMark> Take(Maybe<string> path) =>
        path.Bind(p => Result.Try(() => new FileInfo(p)).Ensure(f => f.Exists, "no transcript")
            .Map(f => new TranscriptMark(p, f.Length)).AsMaybe());

    /// <summary>
    /// Whether the session has moved on from the question: a conversation line ("assistant" or "user") written past the
    /// mark, or the transcript gone or cut short. A line still being written (no newline yet, or JSON that does not parse)
    /// is skipped and looked at again on the next tick.
    /// </summary>
    public bool MovedOn() =>
        Result.Try(() =>
        {
            var file = new FileInfo(Path);
            if (!file.Exists || file.Length < Length) return true;
            if (file.Length == Length) return false;
            using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(Length, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var appended = reader.ReadToEnd();
            // Only whole lines: the text after the last newline may be a line Claude Code is still writing.
            var whole = appended[..(appended.LastIndexOf('\n') + 1)];
            return whole.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(IsConversation);
        }).GetValueOrDefault(false);

    static bool IsConversation(string line) =>
        Result.Try(() =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && Moving.Contains(type.GetString(), StringComparer.Ordinal);
        }).GetValueOrDefault(false);
}
