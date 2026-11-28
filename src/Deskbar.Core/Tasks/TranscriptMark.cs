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
// THE ASKING MESSAGE CAN LAND AFTER THE MARK (task 0978f3f4, "no question in chat"). Measured on 2026-11-28 with Claude
// Code 2.1.332, a background session asking AskUserQuestion: the PreToolUse hook placed the question and took the mark at
// 09:09:03.48, and the assistant line carrying that very AskUserQuestion tool_use was written to the transcript at
// 09:09:03.78. The next five-second tick read it as the conversation going on and settled the question while the
// dialog still waited (`claude agents` still said "input needed" half an hour later). So a mark taken for a dialog
// tool names that tool (Asking), and an "assistant" line that carries a tool_use of that tool, or belongs to the same
// API message as one (Claude Code writes a message's thinking, text and tool_use blocks as separate lines sharing
// message.id), is the question itself arriving late, not an answer. The answer, a refusal or Esc always writes a
// "user" line (the tool's result or the interruption), so nothing that settles a dialog is lost. Only the dialog tools
// (AskUserQuestion, ExitPlanMode) name themselves: their answer IS the tool's result, so the user line follows at once.
// A tool permission (Bash, say) does not, since there the assistant line is the first sign of an Allow and the user
// line waits until the command finishes, which can be minutes.
//
// Read-only, shared with Claude Code's writer (FileShare.ReadWrite | Delete), and only the bytes past the mark: the length
// is checked first, so a question whose transcript has not grown costs one file-size look per tick.
public sealed record TranscriptMark(string Path, long Length, Maybe<string> Asking = default)
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

    /// <summary>
    /// The transcript's length now, or None when there is no transcript to watch (no path, or no file). <paramref
    /// name="asking"/> names the dialog tool the mark is for, whose own late-written line must not count (the header).
    /// </summary>
    public static Maybe<TranscriptMark> Take(Maybe<string> path, Maybe<string> asking = default) =>
        path.Bind(p => Result.Try(() => new FileInfo(p)).Ensure(f => f.Exists, "no transcript")
            .Map(f => new TranscriptMark(p, f.Length, asking)).AsMaybe());

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
            var lines = whole.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Read).Where(l => l.HasValue).Select(l => l.Value).ToList();
            // The API messages that carry the asking tool's tool_use: every line of them is the question, not an answer.
            var askingMessages = lines.Where(l => l.Type == "assistant" && Asking.HasValue && l.Tools.Contains(Asking.Value))
                .Select(l => l.Message).Where(m => m.Length > 0).ToHashSet(StringComparer.Ordinal);
            return lines.Any(l => Moving.Contains(l.Type, StringComparer.Ordinal) && !IsAsking(l, askingMessages));
        }).GetValueOrDefault(false);

    // An assistant line of the asking message (see the header): it carries the dialog tool's tool_use, or shares its message.id.
    bool IsAsking(Line line, IReadOnlySet<string> askingMessages) =>
        line.Type == "assistant"
        && Asking.HasValue
        && (line.Tools.Contains(Asking.Value) || (line.Message.Length > 0 && askingMessages.Contains(line.Message)));

    // What a transcript line says that MovedOn needs: its type, its API message id ("" when none) and the tools its
    // tool_use blocks name. None for a line that does not parse or is not an object with a string type.
    sealed record Line(string Type, string Message, IReadOnlyList<string> Tools);

    static Maybe<Line> Read(string text) =>
        Result.Try(() =>
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String) return Maybe<Line>.None;
            var message = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object ? m : default;
            var id = message.ValueKind == JsonValueKind.Object && message.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String
                ? i.GetString() ?? "" : "";
            var tools = message.ValueKind == JsonValueKind.Object && message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array
                ? c.EnumerateArray()
                    .Where(b => b.ValueKind == JsonValueKind.Object
                                && b.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "tool_use"
                                && b.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                    .Select(b => b.GetProperty("name").GetString() ?? "")
                    .ToList()
                : [];
            return Maybe.From(new Line(type.GetString() ?? "", id, tools));
        }).GetValueOrDefault(Maybe<Line>.None);
}
