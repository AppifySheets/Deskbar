using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// WHAT CLAUDE DID IN A SESSION, AS A CHAT: the user's messages, Claude's messages, and between two messages the run of
// steps (read a file, ran a command, edited a file) Claude took. A task's page shows the owner's comments today and
// nothing of Claude's own side; this is the model behind a chat that does (task: Claude's steps from the transcript).
//
// Deskbar never runs Claude for this. It only READS the session's transcript, the same .jsonl file ClaudeTranscript
// finds, and turns its lines into ChatItems. No WPF, no session start, nothing is ever written to the transcript.
//
// THE TRANSCRIPT, as measured on 2026-11-26 in this machine's own %USERPROFILE%\.claude\projects (structure only, no
// content kept; every test line here is made up):
//   * One JSON object per line, appended as it happens, never rewritten. Dozens of `type`s exist; a chat needs four:
//     "user", "assistant", "attachment" (only its queued_command kind) and "system" (only compact_boundary). All the
//     rest (cost-state, ai-title, last-prompt, mode, queue-operation, api-request*, data-flow-records, file-history-*,
//     pr-link, tools-used, session-owner and so on) is metadata and is ignored by name, never by guessing.
//   * ONE CONTENT BLOCK PER ASSISTANT LINE. A reply that thinks, says something and calls two tools is four lines
//     that share `message.id`, each with a single block of type "thinking", "text" or "tool_use". So a chat item
//     is built from lines, and two text lines of one message.id (Claude spoke, ran a tool, spoke again) stay two
//     messages while two adjacent text lines of the SAME id are joined. Thinking blocks are not shown.
//   * A TOOL'S OUTCOME COMES LATER, as a "user" line whose content has a tool_result block carrying `tool_use_id` and,
//     when the tool failed, `is_error: true`. It is not a user message and never shown as one; it only marks a step
//     done or failed. Its `toolUseResult` sibling (the raw output) is never read: it is what makes lines huge.
//   * WHO WROTE A "user" LINE is `origin.kind`: "human" (a typed prompt, absent in older versions), "task-notification"
//     (a background task or subagent reporting back), "peer" (another session's message). Only human ones are the
//     owner's words. `isMeta:true` lines (skill bodies, hook feedback, image source notes, the local-command caveat)
//     and `isCompactSummary:true` (the summary /compact writes) are Claude Code talking to the model, not to the
//     owner. A human prompt's text blocks also carry injected blocks of Claude Code's own (a <browser_instruction>
//     block, <system-reminder> blocks, "[Image: source: ...]" notes); those are dropped from the text.
//   * A PROMPT TYPED WHILE CLAUDE WAS BUSY has no "user" line. It is an "attachment" line of attachment.type
//     "queued_command" with commandMode "prompt" and origin.kind "human", prompt a string or a list of text blocks.
//   * A SUBAGENT'S OWN TURNS carry `isSidechain:true`. Current versions keep them in separate agent-<id>.jsonl files,
//     older ones wrote them into the parent's file. They are not the main chat, so they are skipped, with one
//     exception: a transcript whose ONLY chat content is sidechain (such a subagent file read on its own) shows that.
//   * /compact appends a system line subtype "compact_boundary" and then the isCompactSummary user line. The history
//     before the boundary stays in the file, so it is kept and a Compacted marker sits where the boundary was.
//   * An Esc interrupt is written as a user text "[Request interrupted by user]" (or "... for tool use"), which
//     becomes an Interrupted marker. A slash command's own lines (<command-name>..., <local-command-stdout>...) are
//     skipped, the same rule ClaudeTranscript.WasCutOff uses.
//
// WHAT IT COSTS. Parse and the reader parse every line they are given (JsonDocument over the line, no copying of the
// tool output), so CPU is linear in the bytes given. MEMORY IS NOT: only the last `maxItems` items (and the maps of
// their tool ids) are kept, trimmed in batches, so a 50 MB transcript never sits in memory as items. The reader goes
// further and, on its first read of a big file, reads only the last InitialTailBytes of it (8 MB, a few ms), so it is
// the cost of the tail, not of the file. A text is cut at MaxTextLength characters. A snapshot costs O(items + steps
// in the kept runs); with 200 items that is microseconds, so a caller may take one per poll.
public static class ClaudeChat
{
    /// <summary>How many items the chat keeps when the caller names no number.</summary>
    public const int DefaultMaxItems = 200;

    /// <summary>A message's text is cut here (with an ellipsis); a pasted log must not become a 2 MB string.</summary>
    public const int MaxTextLength = 20_000;

    /// <summary>A step's label is cut here (with an ellipsis).</summary>
    public const int MaxLabelLength = 60;

    /// <summary>
    /// The chat in a whole transcript's lines: at most the last <paramref name="maxItems"/> items, oldest first. Lines
    /// that are not JSON, not an object or not a chat line are skipped; this never throws for a bad line. For a file on
    /// disk that keeps growing use <see cref="ClaudeChatReader"/>, which reads only what is new.
    /// </summary>
    public static IReadOnlyList<ChatItem> Parse(IEnumerable<string> jsonLines, int maxItems = DefaultMaxItems)
    {
        var builder = new ChatBuilder(maxItems);
        foreach (var line in jsonLines) builder.Add(line);
        return builder.Snapshot();
    }

    // ---- labels ----------------------------------------------------------------------------------------------------

    // The kind of a tool, from its name. A name this does not know is Other and its label is the name (never a guess),
    // so a new tool shows up as itself instead of being dropped. MCP tools (mcp__<server>__<tool>) are Other as well.
    internal static StepKind KindOf(string tool) => tool switch
    {
        "Read" or "NotebookRead" => StepKind.Read,
        "Edit" or "MultiEdit" or "NotebookEdit" => StepKind.Edit,
        "Write" => StepKind.Write,
        "Bash" or "PowerShell" or "BashOutput" or "KillShell" => StepKind.Run,
        "Grep" or "Glob" or "LS" or "ToolSearch" => StepKind.Search,
        "WebFetch" or "WebSearch" => StepKind.Web,
        "Agent" or "Task" => StepKind.Subagent,
        "TodoWrite" or "TaskCreate" or "TaskUpdate" or "TaskList" or "TaskGet" or "EnterPlanMode" or "ExitPlanMode" => StepKind.Plan,
        _ => StepKind.Other,
    };

    // A short label from the tool's input: the target without a verb ("Dock.cs", "dotnet test"), because the kind
    // already says what was done and the page writes "Read Dock.cs" itself. Whatever the input lacks, the label falls
    // back to the tool's name rather than to nothing.
    internal static string LabelOf(string tool, JsonElement input)
    {
        string? Str(string name) =>
            input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;

        var label = tool switch
        {
            "Read" or "Edit" or "Write" or "MultiEdit" => FileNameOf(Str("file_path")),
            "NotebookRead" or "NotebookEdit" => FileNameOf(Str("notebook_path")),
            "Bash" or "PowerShell" => CommandLabel(Str("command")),
            "Grep" or "Glob" => Str("pattern"),
            "LS" => FileNameOf(Str("path")),
            "ToolSearch" or "WebSearch" => Str("query"),
            "WebFetch" => Str("url") is { } url ? Regex.Replace(url, @"^[a-zA-Z][a-zA-Z0-9+.-]*://", "") : null,
            "Agent" or "Task" => Str("description") ?? Str("subagent_type"),
            "TaskCreate" => Str("subject"),
            "Skill" => Str("skill") is { } skill ? "Skill " + skill : null,
            _ => null,
        };
        return Shorten(label) is { Length: > 0 } text ? text : tool;
    }

    // The file's own name: everything after the last / or \, so the folder (a path on this PC, usually long and
    // private) never reaches the page. Both separators are handled by hand because the path in a transcript is the
    // one Claude was given, whatever the machine running the parser thinks a separator is.
    static string? FileNameOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var trimmed = path.TrimEnd('/', '\\');
        var name = trimmed[(trimmed.LastIndexOfAny(['/', '\\']) + 1)..];
        return name.Length > 0 ? name : trimmed;
    }

    // A shell command's first words: its first non-empty line with a leading `cd <folder> && ` or `cd <folder>; ` taken
    // off (Claude starts many commands that way, and the label would otherwise be a folder path), whitespace collapsed.
    static string? CommandLabel(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var first = command.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        first = LeadingCd.Replace(first, "");
        return first.Length > 0 ? first : command.Trim();
    }

    static readonly Regex LeadingCd = new(@"^cd\s+(?:""[^""]*""|'[^']*'|\S+)\s*(?:&&|;)\s*", RegexOptions.Compiled);

    static string? Shorten(string? text)
    {
        if (text is null) return null;
        var one = Regex.Replace(text, @"\s+", " ").Trim();
        return one.Length <= MaxLabelLength ? one : one[..(MaxLabelLength - 1)] + "…";
    }

    // ---- the builder -----------------------------------------------------------------------------------------------

    // Turns lines into items, one at a time, so Parse (all lines at once) and ClaudeChatReader (a few lines per poll)
    // share every rule. It holds two lanes, the main chat and the sidechain, and Snapshot picks the main one unless it
    // is empty (see the file's comment on sidechains).
    internal sealed class ChatBuilder(int maxItems)
    {
        readonly Lane main = new(maxItems), side = new(maxItems);

        public void Add(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) return;
                var lane = ClaudeTranscript.IsTrue(root, "isSidechain") ? side : main;
                switch (type.GetString())
                {
                    case "assistant": AddAssistant(lane, root); break;
                    case "user": AddUser(lane, root); break;
                    case "attachment": AddQueued(lane, root); break;
                    case "system" when Str(root, "subtype") == "compact_boundary":
                        lane.AddItem(new ChatMarker(IdOf(root, lane), ChatMarkerKind.Compacted, ClaudeTranscript.TimestampOf(root)));
                        break;
                }
            }
            catch (JsonException)
            {
                // A line that does not parse is skipped, as ClaudeTranscript does: the last line of a file being
                // written may be cut, and a bad line in the middle must not hide the rest of the chat.
            }
        }

        public IReadOnlyList<ChatItem> Snapshot() => main.Count > 0 || side.Count == 0 ? main.Snapshot() : side.Snapshot();

        static void AddAssistant(Lane lane, JsonElement root)
        {
            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return;
            var isError = ClaudeTranscript.IsTrue(root, "isApiErrorMessage");
            // "<synthetic>" is the model name Claude Code gives text it made up itself ("No response requested.").
            // An API error uses it too, and that one is worth showing: it says why the session stopped.
            if (!isError && Str(message, "model") == "<synthetic>") return;
            var at = ClaudeTranscript.TimestampOf(root);
            var messageId = Str(message, "id");
            if (!message.TryGetProperty("content", out var content)) return;
            if (content.ValueKind == JsonValueKind.String)
            {
                lane.AddClaudeText(IdOf(root, lane), messageId, content.GetString(), isError, at);
                return;
            }
            if (content.ValueKind != JsonValueKind.Array) return;
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object) continue;
                switch (Str(block, "type"))
                {
                    case "text":
                        lane.AddClaudeText(IdOf(root, lane), messageId, Str(block, "text"), isError, at);
                        break;
                    case "tool_use" when Str(block, "name") is { Length: > 0 } name:
                        var input = block.TryGetProperty("input", out var i) ? i : default;
                        lane.AddStep(Str(block, "id") ?? IdOf(root, lane), KindOf(name), LabelOf(name, input), at);
                        break;
                }
            }
        }

        static void AddUser(Lane lane, JsonElement root)
        {
            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return;
            if (!message.TryGetProperty("content", out var content)) return;

            // A tool's outcome: mark its step, whoever wrote the rest of the line.
            if (content.ValueKind == JsonValueKind.Array)
                foreach (var block in content.EnumerateArray())
                    if (block.ValueKind == JsonValueKind.Object && Str(block, "type") == "tool_result" && Str(block, "tool_use_id") is { } toolId)
                        lane.Finish(toolId, failed: ClaudeTranscript.IsTrue(block, "is_error"));

            if (ClaudeTranscript.IsTrue(root, "isMeta") || ClaudeTranscript.IsTrue(root, "isCompactSummary")) return;
            if (!IsHuman(root.TryGetProperty("origin", out var origin) ? origin : default)) return;
            AddHumanText(lane, IdOf(root, lane), content, ClaudeTranscript.TimestampOf(root));
        }

        // A prompt typed while Claude was busy (see the file's comment): the only trace of it is the queued_command.
        static void AddQueued(Lane lane, JsonElement root)
        {
            if (!root.TryGetProperty("attachment", out var attachment) || attachment.ValueKind != JsonValueKind.Object) return;
            if (Str(attachment, "type") != "queued_command" || Str(attachment, "commandMode") != "prompt") return;
            if (!IsHuman(attachment.TryGetProperty("origin", out var origin) ? origin : default)) return;
            if (!attachment.TryGetProperty("prompt", out var prompt)) return;
            AddHumanText(lane, IdOf(root, lane), prompt, ClaudeTranscript.TimestampOf(root));
        }

        // origin absent (older versions, and every human prompt before origin existed) or kind "human".
        static bool IsHuman(JsonElement origin) =>
            origin.ValueKind != JsonValueKind.Object || Str(origin, "kind") is null or "human";

        static void AddHumanText(Lane lane, string id, JsonElement content, Maybe<DateTimeOffset> at)
        {
            var texts = new List<string>();
            var images = 0;
            var interrupted = false;
            void Take(string? raw)
            {
                if (raw is null) return;
                if (raw.StartsWith("[Request interrupted by user", StringComparison.Ordinal)) { interrupted = true; return; }
                var text = InjectedBlock.Replace(raw, "").Trim();
                if (text.Length == 0 || NotAPrompt.Any(p => text.StartsWith(p, StringComparison.Ordinal))) return;
                texts.Add(text);
            }

            switch (content.ValueKind)
            {
                case JsonValueKind.String:
                    Take(content.GetString());
                    break;
                case JsonValueKind.Array:
                    foreach (var block in content.EnumerateArray())
                        switch (block.ValueKind == JsonValueKind.Object ? Str(block, "type") : null)
                        {
                            case "text": Take(Str(block, "text")); break;
                            case "image": images++; break;
                        }
                    break;
            }

            if (interrupted && texts.Count == 0) lane.AddItem(new ChatMarker(id, ChatMarkerKind.Interrupted, at));
            else if (texts.Count > 0 || images > 0) lane.AddUserText(id, string.Join("\n\n", texts), images, at);
        }

        // Claude Code's own blocks inside a prompt's text: <system-reminder>..</system-reminder>, the Chrome tool's
        // <browser_instruction>..</browser_instruction>, and the "[Image: source: path]" note it adds after a pasted
        // image. The owner did not write them. The browser block runs to its closing tag, or to the end of the text.
        static readonly Regex InjectedBlock = new(
            @"<system-reminder>.*?</system-reminder>|<browser_instruction>.*?(?:</browser_instruction>|$)|^\[Image: source:[^\]]*\]",
            RegexOptions.Singleline | RegexOptions.Compiled);

        // Text that opens one of these is Claude Code talking, not the owner: a slash command's own lines (the same list
        // WasCutOff skips) and a background task's report in case an older version wrote it without an origin.
        static readonly IReadOnlyList<string> NotAPrompt = [.. ClaudeTranscript.LocalCommandPrefixes, "<task-notification>", "Another Claude session sent a message"];

        static string? Str(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        // The line's own uuid. A line without one (a hand-made test line, an unknown version) gets a number that is
        // unique within the lane; it is only a key for the page to match one snapshot's item with the next.
        static string IdOf(JsonElement root, Lane lane) => Str(root, "uuid") ?? lane.NextId();
    }

    // One lane's items. A node is either a finished item or a run of steps still open to more.
    sealed class Lane(int maxItems)
    {
        readonly List<Node> nodes = [];
        readonly Dictionary<string, StepState> steps = new(StringComparer.Ordinal);
        readonly int max = Math.Max(1, maxItems);
        int counter;

        public int Count => nodes.Count;
        public string NextId() => "n" + ++counter;

        public void AddItem(ChatItem item) => Append(new Node { Item = item });

        public void AddUserText(string id, string text, int images, Maybe<DateTimeOffset> at)
        {
            // The same prompt can be written twice, as a queued_command and then as the user line it became; the
            // second copy is not a second message.
            if (nodes.Count > 0 && nodes[^1].Item is ChatMessage { Role: ChatRole.User } last && last.Text == Cut(text) && last.Images == images) return;
            Append(new Node { Item = new ChatMessage(id, ChatRole.User, Cut(text), images, false, at) });
        }

        public void AddClaudeText(string id, string? messageId, string? text, bool isError, Maybe<DateTimeOffset> at)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            // Two adjacent text lines of one API message are one message (Claude's text in two blocks); text before and
            // after a tool use are two, because a run of steps sits between them.
            if (messageId is not null && nodes.Count > 0 && nodes[^1] is { MessageId: { } previous, Item: ChatMessage { Role: ChatRole.Claude } message } node && previous == messageId)
            {
                node.Item = message with { Text = Cut(message.Text + "\n\n" + text.Trim()) };
                return;
            }
            Append(new Node { MessageId = messageId, Item = new ChatMessage(id, ChatRole.Claude, Cut(text.Trim()), 0, isError, at) });
        }

        public void AddStep(string id, StepKind kind, string label, Maybe<DateTimeOffset> at)
        {
            if (steps.ContainsKey(id)) return;
            if (nodes.Count == 0 || nodes[^1].Run is null) Append(new Node { Run = [], Id = id, At = at });
            var run = nodes[^1];
            var step = new StepState(id, kind, label, at);
            run.Run!.Add(step);
            steps[id] = step;
        }

        // A result for a step that is not kept (trimmed, or from the other lane) changes nothing.
        public void Finish(string toolUseId, bool failed)
        {
            if (!steps.TryGetValue(toolUseId, out var step)) return;
            step.Done = true;
            step.Failed = failed;
        }

        void Append(Node node)
        {
            nodes.Add(node);
            // Trim in batches (when twice the cap is held) so the list is not shifted on every line; Snapshot returns
            // only the last `max` either way. The tool ids of a trimmed run go with it.
            if (nodes.Count <= max * 2) return;
            var drop = nodes.Count - max;
            foreach (var gone in nodes.Take(drop))
                if (gone.Run is not null)
                    foreach (var step in gone.Run) steps.Remove(step.Id);
            nodes.RemoveRange(0, drop);
        }

        public IReadOnlyList<ChatItem> Snapshot() =>
            nodes.Skip(Math.Max(0, nodes.Count - max))
                .Select(n => n.Item ?? new ChatSteps(n.Id!, n.Run!.Select(s => new ChatStep(s.Id, s.Kind, s.Label, s.Failed, s.Done, s.At)).ToList(), n.At))
                .ToList();

        static string Cut(string text) => text.Length <= MaxTextLength ? text : text[..(MaxTextLength - 1)] + "…";

        sealed class Node
        {
            public ChatItem? Item;
            public string? MessageId;
            public List<StepState>? Run;
            public string? Id;
            public Maybe<DateTimeOffset> At = Maybe<DateTimeOffset>.None;
        }

        sealed class StepState(string id, StepKind kind, string label, Maybe<DateTimeOffset> at)
        {
            public string Id { get; } = id;
            public StepKind Kind { get; } = kind;
            public string Label { get; } = label;
            public Maybe<DateTimeOffset> At { get; } = at;
            public bool Done;
            public bool Failed;
        }
    }
}

/// <summary>One thing in the chat. <see cref="Id"/> is stable for as long as the item exists, so a page can match an
/// item of one snapshot with the next and redraw only what changed: a run of steps keeps its Id while it grows.</summary>
public abstract record ChatItem(string Id, Maybe<DateTimeOffset> At);

public enum ChatRole { User, Claude }

/// <summary>A message. <see cref="Images"/> is how many pictures the owner attached (their bytes are not in the chat).
/// <see cref="IsError"/> is Claude Code's own report of an API error (a usage limit, an overloaded server) written in
/// Claude's place.</summary>
public sealed record ChatMessage(string Id, ChatRole Role, string Text, int Images, bool IsError, Maybe<DateTimeOffset> At) : ChatItem(Id, At);

/// <summary>The steps Claude took between two messages, oldest first. The item's At is its first step's.</summary>
public sealed record ChatSteps(string Id, IReadOnlyList<ChatStep> Steps, Maybe<DateTimeOffset> At) : ChatItem(Id, At);

public enum ChatMarkerKind { Compacted, Interrupted }

/// <summary>A break in the chat: Claude Code compacted the conversation here, or the owner pressed Esc.</summary>
public sealed record ChatMarker(string Id, ChatMarkerKind Kind, Maybe<DateTimeOffset> At) : ChatItem(Id, At);

public enum StepKind { Read, Edit, Write, Run, Search, Web, Subagent, Plan, Other }

/// <summary>
/// One tool use. <see cref="Label"/> is the target without a verb ("Dock.cs", "dotnet test"), at most 60 characters.
/// <see cref="Done"/> is false while no result has been seen (still running, or the session was cut off) and
/// <see cref="Failed"/> is true when the result said it failed; a step with no result is never Failed.
/// </summary>
public sealed record ChatStep(string Id, StepKind Kind, string Label, bool Failed, bool Done, Maybe<DateTimeOffset> At);

/// <summary>What one <see cref="ClaudeChatReader.Read"/> found. <see cref="Items"/> is the chat as it is now (the last
/// maxItems), <see cref="Changed"/> is false when the file held nothing new and the items are the ones last returned.</summary>
public sealed record ChatUpdate(IReadOnlyList<ChatItem> Items, bool Changed, long Position);

/// <summary>
/// Reads a transcript that is still being written, a few lines per poll. A caller keeps one reader per task and calls
/// <see cref="Read"/> on a timer; it costs a file-length check when nothing was added.
///
/// Why a reader with state, and not a function from (file, position) to (new items, position): what a line means can
/// depend on lines before it. A tool's result (marking its step failed) arrives in a later chunk than the tool use, and
/// a run of steps goes on across chunks, so the new lines alone would yield a failure with no step and a second run
/// where one belongs. The reader keeps the builder's state between calls and returns the whole current chat, whose
/// items keep their Ids, so the page redraws only what differs.
///
/// A LINE IS CONSUMED ONLY WITH ITS NEWLINE. The position never moves past a half-written last line; the next call
/// reads it again whole. The file is opened with sharing for read and write, because Claude Code has it open. A file
/// that is missing, locked or shorter than the position (replaced, truncated) is not an error: missing or locked
/// means "no change", shorter means start over.
/// </summary>
public sealed class ClaudeChatReader
{
    /// <summary>How much of a file's end the first read looks at (see the cost note on <see cref="ClaudeChat"/>).</summary>
    public const long DefaultInitialTailBytes = 8L * 1024 * 1024;

    readonly int maxItems;
    readonly long initialTailBytes;
    ClaudeChat.ChatBuilder builder;
    IReadOnlyList<ChatItem> items = [];

    public ClaudeChatReader(int maxItems = ClaudeChat.DefaultMaxItems, long initialTailBytes = DefaultInitialTailBytes)
    {
        this.maxItems = maxItems;
        this.initialTailBytes = Math.Max(1, initialTailBytes);
        builder = new ClaudeChat.ChatBuilder(maxItems);
    }

    /// <summary>The offset in the file just after the last line consumed.</summary>
    public long Position { get; private set; }

    public ChatUpdate Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            var before = Position;
            var reset = length < Position;
            if (reset)
            {
                builder = new ClaudeChat.ChatBuilder(maxItems);
                Position = 0;
                items = [];
            }
            else if (length == Position) return new ChatUpdate(items, false, Position);

            var start = Position;
            var skipPartialFirstLine = start == 0 && length > initialTailBytes;
            if (skipPartialFirstLine) start = length - initialTailBytes;

            stream.Seek(start, SeekOrigin.Begin);
            var consumed = Consume(stream, length - start, skipPartialFirstLine);
            // A tail with no newline in it (one line longer than the tail) consumes nothing: stay at 0 and try again
            // whole next time rather than parking in the middle of a line.
            var position = consumed == 0 && skipPartialFirstLine ? 0 : start + consumed;
            var changed = reset || position != before;
            Position = position;
            if (changed) items = builder.Snapshot();
            return new ChatUpdate(items, changed, Position);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new ChatUpdate(items, false, Position);
        }
    }

    // Reads up to `count` bytes, feeds each COMPLETE line (ended by \n) to the builder and returns how many bytes those
    // lines took, newlines included. Bytes after the last newline are left unconsumed for the next call. Lines are cut
    // on the byte 0x0A, which cannot occur inside a multi-byte UTF-8 character, so decoding a whole line is safe. With
    // `skipFirstLine` the bytes up to the first newline are counted but not parsed: they are the end of a line whose
    // start was cut off by reading only a file's tail.
    long Consume(Stream stream, long count, bool skipFirstLine)
    {
        var line = new List<byte>();
        var buffer = new byte[256 * 1024];
        long total = 0, consumed = 0;
        while (total < count)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, count - total));
            if (read == 0) break;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != (byte)'\n') { if (!skipFirstLine) line.Add(buffer[i]); continue; }
                consumed = total + i + 1;
                if (!skipFirstLine) builder.Add(Encoding.UTF8.GetString(line.ToArray()).TrimEnd('\r'));
                skipFirstLine = false;
                line.Clear();
            }
            total += read;
        }
        return consumed;
    }
}