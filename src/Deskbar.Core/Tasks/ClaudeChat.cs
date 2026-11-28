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
//     messages while two adjacent text lines of the SAME id are joined.
//   * A THINKING BLOCK IS EMPTY, OR IT IS A NARRATION. Current models write `"thinking":""` (the reasoning is not kept),
//     except for a short line saying what Claude is about to do ("The round's 33 lines are mapped, checking precedents
//     next"), which the terminal prints as an ordinary line ending in "· summary". Measured on 2026-11-28 over every
//     transcript on this machine: 12,457 non-empty thinking blocks (Opus 5.5, Fable 5.1 and 5.2), 81 to about 400
//     characters, and every one of them carried the narration kind in its signature. So a non-empty thinking block is
//     shown as Claude's message, its own one (never joined to the text after it, as the terminal keeps them apart),
//     and an empty one is skipped (task daf3b1b2: "some messages are missing from the chat that are present in the
//     terminal"). The signature itself is not decoded: an undocumented format is no gate to hang a message on.
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
//   * ANOTHER SESSION'S MESSAGE (SendMessage from a second Claude session) is origin.kind "peer" and its text is wrapped
//     in <cross-session-message from="..." from-name="<that session's title>" ...>body</cross-session-message>. It
//     comes in one of two shapes, never both for one message (measured 2026-11-28, 81 messages): a queued_command
//     attachment whose prompt IS the tag (the message arrived mid-turn), or an isMeta "user" line reading "Another
//     Claude session sent a message:\n<cross-session-message ...>...</cross-session-message>" and then a note of
//     Claude Code's own for the model. The terminal shows either as "Message from @<name>: body", so the chat shows
//     the body as a Peer message signed with the name. Other peer lines stay hidden: a subagent's hand-back is a peer
//     line too, but wrapped in <agent-message>, and it is Claude's own helper reporting, which the terminal folds away.
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
        return Shorten(Redact(label)) is { Length: > 0 } text ? text : tool;
    }

    // WHETHER A TOOL USE IS DESKBAR HOUSEKEEPING: a shell command that is a call of Deskbar's own shim (`deskbar.cmd task
    // status-line ...`, `task comment`, `task ask`, `task join`, in any path form: `"$APPDATA/Deskbar/deskbar.cmd" task ...`,
    // `& "C:\x\deskbar.cmd" task ...`). A task session makes dozens of them: they report to the board (whose own lines and
    // comments already show the result), so listing them under "N steps" is noise (the owner's ruling for the Inbox steps,
    // 2026-11-26).
    //
    // THE RULE (tester finding M4): a command whose ONLY real program is deskbar.cmd (or deskbar) with `task` as its verb.
    // The shim may stand anywhere in the command, so the forms Claude really writes all count: first or after a pipe, `&`,
    // `;` or `&&`; quoted, with a path that has spaces, with forward or back slashes, as `$env:APPDATA\Deskbar\deskbar.cmd`,
    // `$APPDATA/Deskbar/deskbar.cmd`, `%APPDATA%\...` or a full path; fed by `echo x |`, `printf ... |`, `cat <<'EOF' |`, a
    // `$text | &` pipe or a here-string `@' ... '@ | &`; with a heredoc body or a redirection after it. What else may stand
    // in the command: only words that run nothing of their own (echo, printf, cat, type, Write-Output, Write-Host,
    // Get-Content, cd, Set-Location, a `$variable = ...`, a bare quoted string). One other program (`git log; deskbar.cmd task
    // done`) makes it an ordinary step, which is shown as ever. The reader still reports a housekeeping step, flagged, so
    // nothing is lost here; ClaudeChatStream drops it.
    internal static bool IsHousekeeping(string tool, JsonElement input) =>
        tool is "Bash" or "PowerShell"
        && input.ValueKind == JsonValueKind.Object && input.TryGetProperty("command", out var command) && command.ValueKind == JsonValueKind.String
        && command.GetString() is { } text
        && OnlyDeskbarCalls(text);

    static bool OnlyDeskbarCalls(string command)
    {
        // Bodies first: a here-string's or heredoc's text is data (it may even mention deskbar.cmd), never a program.
        var text = HereStringBody.Replace(command, " ");
        // The rest of the heredoc's own line (`| deskbar.cmd task ...`) stays: it is the command.
        text = HeredocBody.Replace(text, "$3 ");
        // The shim, followed by its verb `task`, becomes one word; then quoted strings are data too, and so are redirections.
        text = ShimBeforeTask.Replace(text, "DESKBARCALL");
        text = QuotedString.Replace(text, "Q");
        text = Redirection.Replace(text, " ");
        var calls = 0;
        foreach (var segment in CommandBreak.Split(text).Select(s => s.Trim()).Where(s => s.Length > 0))
        {
            var program = segment.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
            if (program == "DESKBARCALL") calls++;
            else if (!RunsNothing(program)) return false;
        }
        return calls > 0;
    }

    static bool RunsNothing(string program) =>
        program is "Q" or "(" or ")" or "{" or "}" or "@" || program.StartsWith('$')
        || NoProgram.Contains(program);

    static readonly HashSet<string> NoProgram = new(StringComparer.OrdinalIgnoreCase)
    {
        "echo", "printf", "cat", "type", "write-output", "write-host", "get-content", "cd", "set-location", "pushd", "popd", "export", "set", "true",
    };

    static readonly Regex HereStringBody = new(@"@'[\s\S]*?'@|@""[\s\S]*?""@", RegexOptions.Compiled);
    static readonly Regex HeredocBody = new(@"<<-?\s*([""']?)(\w+)\1([^\n]*)\n(?:[^\n]*\n)*?[ \t]*\2[ \t]*(?:\r?\n|$)", RegexOptions.Compiled);
    static readonly Regex ShimBeforeTask = new(
        @"(?<![\w.-])(?:""(?:[^""]*[\\/])?deskbar(?:\.cmd)?""|'(?:[^']*[\\/])?deskbar(?:\.cmd)?'|(?:[^\s""'|&;]*[\\/])?deskbar(?:\.cmd)?)(?=\s+task\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex QuotedString = new(@"""[^""]*""|'[^']*'", RegexOptions.Compiled);
    static readonly Regex Redirection = new(@"\d*>&\d+|\d*>>?\s*[^\s|;&]+|<\s*[^\s|;&<]+", RegexOptions.Compiled);
    static readonly Regex CommandBreak = new(@"[|;&\r\n]+", RegexOptions.Compiled);

    // SECRETS NEVER REACH THE PAGE. A step's label is a piece of a command line or a URL, and a command line can hold a
    // credential (`curl -H "Authorization: Bearer ..."`, `--token=...`, `gh api ... -f password=...`). The label is
    // redacted BEFORE it is cut to 60 characters, so a secret that straddles the cut is not shown in part. What goes:
    //   * the value of an Authorization header, to the end of its line or its closing quote;
    //   * the token after the word Bearer or Basic;
    //   * the value of any NAME that holds a secret word (token, key, secret, password, passwd, pwd, credential, auth),
    //     written `NAME=value`, `NAME = value`, `NAME: value`, `"NAME": "value"` (JSON) or `--NAME=value`, also behind
    //     `$env:` and as a header such as `X-Api-Key: value`;
    //   * the word after `--password`, `--token`, `--secret`, `--api-key`, `-p` or `-P` and after `setx NAME`, the part after
    //     the colon of `-u user:pass`, and the password of `scheme://user:pass@host` (tester finding M1, 2026-11-26);
    //   * a long run of hex (32 or more) or of base64 characters (32 or more with a digit and a letter in it), which is
    //     what a pasted key or hash-like token looks like. A path stays readable because `.` and `\` end a run.
    // The label shows `***` where a value was. This is a filter, not a guarantee: a secret written as a bare word of
    // fewer than 32 characters, with no keyword before it, cannot be told from any other word.
    internal static string? Redact(string? label)
    {
        if (string.IsNullOrEmpty(label)) return label;
        var text = Authorization.Replace(label, "$1***");
        text = BearerToken.Replace(text, "$1***");
        text = UserInfo.Replace(text, "$1***");
        text = BasicAuthFlag.Replace(text, "$1***");
        text = SecretOption.Replace(text, "$1***");
        text = SetxValue.Replace(text, "$1***");
        text = NamedValue.Replace(text, "$1***");
        text = HexRun.Replace(text, "***");
        return Base64Run.Replace(text, match => match.Value.Any(char.IsDigit) && match.Value.Any(char.IsLetter) ? "***" : match.Value);
    }

    // The words that make a name a secret's. Also found inside longer names (Authorization, X-Api-Key, API_TOKEN, DB_PASSWORD,
    // client_secret, even "author"): masking a little too much is fine, too little is not.
    const string SecretWords = "(?:token|key|secret|password|passwd|pwd|credential|auth)";

    static readonly Regex Authorization = new(@"(?i)(authorization\s*[:=]\s*[""']?)[^\r\n""']+", RegexOptions.Compiled);
    static readonly Regex BearerToken = new(@"(?i)\b((?:bearer|basic)\s+)[^\s""']+", RegexOptions.Compiled);

    // NAME=value, NAME = value, NAME: value, "NAME": "value", $env:NAME = value, X-Api-Key: value, --token=value: a name that
    // holds a secret word, a `=` or `:` (spaces allowed on both sides, a closing quote allowed before it), then the value,
    // quoted (it may hold spaces) or one bare word.
    static readonly Regex NamedValue = new(@"(?i)(\b[\w-]*" + SecretWords + @"[\w-]*[""']?\s*[=:]\s*)(?:""[^""]*""|'[^']*'|[^\s&""',}]+)", RegexOptions.Compiled);

    // --password x, --token x, --secret x, --api-key x, -p x, -P x: the word after the option, unless that is an option too.
    // `-p` also masks the harmless word of `mkdir -p dir`, which is the price of not missing `mysql -p secret`.
    static readonly Regex SecretOption = new(
        @"(?i)(?<![\w-])((?:--(?:password|passwd|pwd|pass|token|access-token|secret|api-key|apikey|key|credential|auth)|-p)\s+)(?!-)(?:""[^""]*""|'[^']*'|[^\s""']+)",
        RegexOptions.Compiled);

    // setx NAME value: the value of a secret-named variable.
    static readonly Regex SetxValue = new(@"(?i)(\bsetx\s+[\w-]*" + SecretWords + @"[\w-]*\s+)(?:""[^""]*""|'[^']*'|\S+)", RegexOptions.Compiled);

    // -u user:pass and --user user:pass (curl and friends): the part after the colon.
    static readonly Regex BasicAuthFlag = new(@"(?i)(?<![\w-])((?:-u|--user)\s+[""']?[^\s:""']+:)[^\s""']*", RegexOptions.Compiled);

    // The password of user:pass@host in any URL, with or without its scheme (a WebFetch label has the scheme cut off).
    static readonly Regex UserInfo = new(@"((?:[a-zA-Z][a-zA-Z0-9+.-]*://)?[^\s:/@""']+:)[^\s@/""']+(?=@)", RegexOptions.Compiled);
    static readonly Regex HexRun = new(@"\b[0-9a-fA-F]{32,}\b", RegexOptions.Compiled);
    static readonly Regex Base64Run = new(@"[A-Za-z0-9+_-]{32,}", RegexOptions.Compiled);

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
                    // A narration (see the file's comment): its own message, so no message id to join it by. An empty
                    // thinking block (the usual case) is dropped inside AddClaudeText.
                    case "thinking" when Str(block, "thinking") is { } thought && IsNarration(thought):
                        lane.AddClaudeText(IdOf(root, lane), null, thought, false, at);
                        break;
                    case "tool_use" when Str(block, "name") is { Length: > 0 } name:
                        var input = block.TryGetProperty("input", out var i) ? i : default;
                        lane.AddStep(Str(block, "id") ?? IdOf(root, lane), KindOf(name), LabelOf(name, input), at, IsHousekeeping(name, input));
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

            var origin = root.TryGetProperty("origin", out var o) ? o : default;
            // Another session's message is written isMeta (see the file's comment), so it is taken before that rule.
            if (IsPeer(origin))
            {
                AddPeerText(lane, IdOf(root, lane), content, ClaudeTranscript.TimestampOf(root));
                return;
            }
            if (ClaudeTranscript.IsTrue(root, "isMeta") || ClaudeTranscript.IsTrue(root, "isCompactSummary")) return;
            if (!IsHuman(origin)) return;
            AddHumanText(lane, IdOf(root, lane), content, ClaudeTranscript.TimestampOf(root));
        }

        // A prompt typed while Claude was busy, or another session's message that arrived mid-turn (see the file's
        // comment): the only trace of either is the queued_command.
        static void AddQueued(Lane lane, JsonElement root)
        {
            if (!root.TryGetProperty("attachment", out var attachment) || attachment.ValueKind != JsonValueKind.Object) return;
            if (Str(attachment, "type") != "queued_command" || Str(attachment, "commandMode") != "prompt") return;
            if (!attachment.TryGetProperty("prompt", out var prompt)) return;
            var origin = attachment.TryGetProperty("origin", out var o) ? o : default;
            if (IsPeer(origin)) AddPeerText(lane, IdOf(root, lane), prompt, ClaudeTranscript.TimestampOf(root));
            else if (IsHuman(origin)) AddHumanText(lane, IdOf(root, lane), prompt, ClaudeTranscript.TimestampOf(root));
        }

        // origin absent (older versions, and every human prompt before origin existed) or kind "human".
        static bool IsHuman(JsonElement origin) =>
            origin.ValueKind != JsonValueKind.Object || Str(origin, "kind") is null or "human";

        // Another session, not a subagent of this one: every hand-back measured (2,785 of them) carried `handback` or
        // `senderTaskId` in its origin, and no cross-session message did (863).
        static bool IsPeer(JsonElement origin) =>
            origin.ValueKind == JsonValueKind.Object && Str(origin, "kind") == "peer"
            && !origin.TryGetProperty("handback", out _) && !origin.TryGetProperty("senderTaskId", out _);

        // A narration's shape: one short paragraph. Real reasoning, which an older model or another Claude Code version
        // may keep in the block, runs to paragraphs; the terminal folds that away, so the chat does too. The longest
        // narration measured was 641 characters.
        static bool IsNarration(string thought) =>
            thought.Trim() is { Length: > 0 and <= 1000 } one && !one.Contains("\n\n", StringComparison.Ordinal) && !one.Contains("\r\n\r\n", StringComparison.Ordinal);

        // Another session's message: the body of its <cross-session-message> tag, signed with the sender's from-name. A
        // peer line without that tag (a subagent's <agent-message> hand-back, an older wording) is not shown.
        static void AddPeerText(Lane lane, string id, JsonElement content, Maybe<DateTimeOffset> at)
        {
            var raw = content.ValueKind switch
            {
                JsonValueKind.String => content.GetString(),
                JsonValueKind.Array => string.Join("\n", content.EnumerateArray()
                    .Where(block => block.ValueKind == JsonValueKind.Object && Str(block, "type") == "text")
                    .Select(block => Str(block, "text"))),
                _ => null,
            };
            if (raw is null || CrossSession.Match(raw) is not { Success: true } match) return;
            var body = match.Groups["body"].Value.Trim();
            if (body.Length == 0) return;
            var name = FromName.Match(match.Groups["attrs"].Value) is { Success: true } named ? named.Groups["name"].Value.Trim() : "";
            lane.AddPeerText(id, name, body, at);
        }

        // The tag opens the text (after Claude Code's "Another Claude session sent a message:" lead in the user-line shape),
        // so a tag merely quoted inside some other text is not taken for a message. It runs to its closing tag, or to the
        // end of a cut text. Whatever Claude Code writes after the closing tag (a note to the model about trusting peers)
        // is outside the body and so never shown.
        static readonly Regex CrossSession = new(
            @"\A\s*(?:Another Claude session sent a message:\s*)?<cross-session-message(?<attrs>[^>]*)>(?<body>.*?)(?:</cross-session-message>|\z)",
            RegexOptions.Singleline | RegexOptions.Compiled);

        static readonly Regex FromName = new(@"\bfrom-name=""(?<name>[^""]*)""", RegexOptions.Compiled);

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
        readonly HashSet<string> ids = new(StringComparer.Ordinal);
        readonly int max = Math.Max(1, maxItems);
        int counter;

        public int Count => nodes.Count;
        public string NextId() => "n" + ++counter;

        public void AddItem(ChatItem item)
        {
            if (Seen(item.Id)) return;
            Append(new Node { Item = item });
        }

        // A LINE WRITTEN AGAIN. Some transcripts append an assistant line a second time, same uuid, far later in the file
        // (measured 2026-11-28: 336 text lines and 108 narrations on this machine). It is the same message, not a new one,
        // so an item whose id is already kept is not added again; without this the bubble showed twice and the panel got
        // two slots for one key. Only kept items are remembered, so the set stays as small as the lane.
        bool Seen(string id) => ids.Contains(id);

        public void AddUserText(string id, string text, int images, Maybe<DateTimeOffset> at)
        {
            if (Seen(id)) return;
            // The same prompt can be written twice, as a queued_command and then as the user line it became; the
            // second copy is not a second message.
            if (nodes.Count > 0 && nodes[^1].Item is ChatMessage { Role: ChatRole.User } last && last.Text == Cut(text) && last.Images == images) return;
            Append(new Node { Item = new ChatMessage(id, ChatRole.User, Cut(text), images, false, at) });
        }

        // Another session's message. Like a prompt, the same message written twice in a row is shown once (the two
        // shapes were never seen together, but nothing in Claude Code promises that).
        public void AddPeerText(string id, string from, string text, Maybe<DateTimeOffset> at)
        {
            if (Seen(id)) return;
            if (nodes.Count > 0 && nodes[^1].Item is ChatMessage { Role: ChatRole.Peer } last && last.From == from && last.Text == Cut(text)) return;
            Append(new Node { Item = new ChatMessage(id, ChatRole.Peer, Cut(text), 0, false, at) { From = from } });
        }

        public void AddClaudeText(string id, string? messageId, string? text, bool isError, Maybe<DateTimeOffset> at)
        {
            if (string.IsNullOrWhiteSpace(text) || Seen(id)) return;
            // Two adjacent text lines of one API message are one message (Claude's text in two blocks); text before and
            // after a tool use are two, because a run of steps sits between them.
            if (messageId is not null && nodes.Count > 0 && nodes[^1] is { MessageId: { } previous, Item: ChatMessage { Role: ChatRole.Claude } message } node && previous == messageId)
            {
                node.Item = message with { Text = Cut(message.Text + "\n\n" + text.Trim()) };
                return;
            }
            Append(new Node { MessageId = messageId, Item = new ChatMessage(id, ChatRole.Claude, Cut(text.Trim()), 0, isError, at) });
        }

        public void AddStep(string id, StepKind kind, string label, Maybe<DateTimeOffset> at, bool housekeeping)
        {
            if (steps.ContainsKey(id)) return;
            if (nodes.Count == 0 || nodes[^1].Run is null) Append(new Node { Run = [], Id = id, At = at });
            var run = nodes[^1];
            var step = new StepState(id, kind, label, at) { Housekeeping = housekeeping };
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
            if (node.Item is { } added) ids.Add(added.Id);
            // Trim in batches (when twice the cap is held) so the list is not shifted on every line; Snapshot returns
            // only the last `max` either way. The tool ids of a trimmed run, and a trimmed item's id, go with it.
            if (nodes.Count <= max * 2) return;
            var drop = nodes.Count - max;
            foreach (var gone in nodes.Take(drop))
            {
                if (gone.Item is { } item) ids.Remove(item.Id);
                if (gone.Run is not null)
                    foreach (var step in gone.Run) steps.Remove(step.Id);
            }
            nodes.RemoveRange(0, drop);
        }

        public IReadOnlyList<ChatItem> Snapshot() =>
            nodes.Skip(Math.Max(0, nodes.Count - max))
                .Select(n => n.Item ?? new ChatSteps(n.Id!, n.Run!.Select(s => new ChatStep(s.Id, s.Kind, s.Label, s.Failed, s.Done, s.At) { Housekeeping = s.Housekeeping }).ToList(), n.At))
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
            public bool Housekeeping;
        }
    }
}

/// <summary>One thing in the chat. <see cref="Id"/> is stable for as long as the item exists, so a page can match an
/// item of one snapshot with the next and redraw only what changed: a run of steps keeps its Id while it grows.</summary>
public abstract record ChatItem(string Id, Maybe<DateTimeOffset> At);

/// <summary>Who wrote a message: the owner, this session's Claude, or another Claude session (SendMessage across sessions).</summary>
public enum ChatRole { User, Claude, Peer }

/// <summary>A message. <see cref="Images"/> is how many pictures the owner attached (their bytes are not in the chat).
/// <see cref="IsError"/> is Claude Code's own report of an API error (a usage limit, an overloaded server) written in
/// Claude's place.</summary>
public sealed record ChatMessage(string Id, ChatRole Role, string Text, int Images, bool IsError, Maybe<DateTimeOffset> At) : ChatItem(Id, At)
{
    /// <summary>For a <see cref="ChatRole.Peer"/> message, the sending session's name as Claude Code gave it (its title,
    /// often cut with "…"), or empty when it gave none. Empty for the other roles.</summary>
    public string From { get; init; } = "";
}

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
public sealed record ChatStep(string Id, StepKind Kind, string Label, bool Failed, bool Done, Maybe<DateTimeOffset> At)
{
    /// <summary>A call of Deskbar's own shim (`deskbar.cmd task ...`), which the page leaves out (ClaudeChatStream, ClaudeChat.IsHousekeeping).</summary>
    public bool Housekeeping { get; init; }
}

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