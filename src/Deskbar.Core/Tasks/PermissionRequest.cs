using System.Text.Encodings.Web;
using System.Text.Json;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// ONE PERMISSION QUESTION FROM CLAUDE CODE, AS DESKBAR READS IT (Allow / Deny from a task's page; plan
// notes/tasks-inbox-permission-plan.md in the PM's workspace, built from the live spike of 2026-11-26).
//
// Claude Code runs a `PermissionRequest` hook when it is about to ask the user whether a tool may run. The hook's JSON on
// stdin carries session_id, transcript_path, cwd, permission_mode, tool_name, tool_input and permission_suggestions,
// and NO tool_use_id (measured in the spike, and the hooks reference says the same). This record keeps what the task's
// page needs to say exactly what Claude wants to do:
//
//   SessionId   session_id: which task the question belongs to (TaskBoard.TaskOf).
//   Tool        tool_name: Bash and PowerShell were both seen for shell commands in one day of runs, so both are shell.
//   Folder      cwd, as given: the folder the session works in, shown on the card.
//   Mode        permission_mode, when there is one (default, acceptEdits, auto, ...).
//   Fields      tool_input's top-level fields in the order Claude Code wrote them, each as text: a string as it is, any
//               other value as indented JSON. EVERY field is kept, so the card can show all of it (PermissionDisplay);
//               a tool_input that is not an object becomes the one field "input".
//   AgentId     agent_id, when a SUBAGENT asked (the hooks reference lists it among the fields of a hook fired inside one).
//               A subagent writes its turns to its own transcript, so the question is watched there (TranscriptMark.For).
//               Only letters, digits, '-' and '_' are taken, since it becomes part of a file name.
//   Input       tool_input exactly as Claude Code sent it, as JSON text ("" when there was none). An AskUserQuestion
//               dialog answered from the task's page must echo its questions back unchanged (DialogAsk, task c047d0a0),
//               and re-serialising the parsed fields could alter them; so the raw text is kept, in memory only, like the
//               rest of the request (PermissionDesk).
//   Suggestions permission_suggestions: the "Yes, and ..." choices Claude Code builds its own dialog from, each kept
//               whole, so the card can offer the same choice and hand that very entry back as updatedPermissions
//               (PermissionChoices, task bd55bd54). An entry that is not an object, or has no "type", is left out.
//
// Only a hook whose hook_event_name is PermissionRequest and that names a session and a tool is taken; anything else is
// refused, and the verb that reads it then answers nothing, which leaves Claude Code's own terminal dialog.
public sealed record PermissionRequest(string SessionId, string Tool, string Folder, Maybe<string> Mode,
    IReadOnlyList<PermissionField> Fields, Maybe<string> AgentId = default, string Input = "")
{
    /// <summary>permission_suggestions, in Claude Code's order; empty when it sent none (see the header).</summary>
    public IReadOnlyList<PermissionSuggestion> Suggestions { get; init; } = [];

    public const string Event = "PermissionRequest";

    // AskUserQuestion and ExitPlanMode are dialogs, not permissions: their "allow" needs an updatedInput carrying the
    // user's choice (the hooks reference, "PermissionRequest decision control"), which a plain Allow would not give. The
    // PM's ruling (Q8) kept both off the card. AskUserQuestion has its own card since task c047d0a0, which sends the
    // picks as that updatedInput (DialogAsk; measured 2026-11-28 in a background session: Claude read "Pick a
    // colour"="Blue" from the hook's answer and the dialog never needed the terminal). ExitPlanMode has its card since
    // task 8dbbaa0b, which echoes the plan back as that updatedInput, or denies with the user's note (PlanAsk).
    static readonly string[] DialogTools = [DialogQuestion.Tool, "ExitPlanMode"];

    /// <summary>
    /// Whether this is one of Claude Code's own dialogs, which a plain Allow cannot answer. AskUserQuestion is answered
    /// with the user's picks since task c047d0a0 (DialogAsk) and a plan from its own card since task 8dbbaa0b (PlanAsk);
    /// RemoteControl.Permission takes a dialog only when one of the two can draw it.
    /// </summary>
    public bool IsDialog => DialogTools.Contains(Tool, StringComparer.Ordinal);

    /// <summary>Whether this is Claude Code's multiple-choice dialog, answered from a card of its own (DialogAsk).</summary>
    public bool IsAskUserQuestion => Tool == DialogQuestion.Tool;

    /// <summary>Whether this is Claude Code's plan approval dialog, answered from a card of its own (PlanAsk).</summary>
    public bool IsPlan => Tool == PlanAsk.Tool;

    static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        // Non-ASCII kept as it is, so PermissionDisplay sees (and marks) the real characters rather than \u escapes.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static Result<PermissionRequest> Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Result.Failure<PermissionRequest>("the hook's JSON is not an object");
            if (Text(root, "hook_event_name").GetValueOrDefault("") != Event)
                return Result.Failure<PermissionRequest>("the hook is not a PermissionRequest");
            if (Text(root, "session_id") is not { HasValue: true } session)
                return Result.Failure<PermissionRequest>("the hook's JSON has no session_id");
            if (Text(root, "tool_name") is not { HasValue: true } tool)
                return Result.Failure<PermissionRequest>("the hook's JSON has no tool_name");

            IReadOnlyList<PermissionField> fields = !root.TryGetProperty("tool_input", out var input) ? []
                : input.ValueKind == JsonValueKind.Object
                    ? input.EnumerateObject().Select(p => new PermissionField(p.Name, ValueText(p.Value))).ToList()
                    : [new PermissionField("input", ValueText(input))];

            return new PermissionRequest(session.Value, tool.Value, Text(root, "cwd").GetValueOrDefault(""),
                Text(root, "permission_mode"), fields, Text(root, "agent_id").Where(SafeAgentId.IsMatch),
                root.TryGetProperty("tool_input", out var raw) ? raw.GetRawText() : "")
            {
                Suggestions = root.TryGetProperty("permission_suggestions", out var suggested) && suggested.ValueKind == JsonValueKind.Array
                    ? suggested.EnumerateArray().Select(PermissionSuggestion.Of).Where(s => s.HasValue).Select(s => s.Value).ToList()
                    : [],
            };
        }
        catch (JsonException e)
        {
            return Result.Failure<PermissionRequest>($"the hook's JSON does not parse: {e.Message}");
        }
    }

    static readonly System.Text.RegularExpressions.Regex SafeAgentId = new("^[A-Za-z0-9_-]{1,64}$");

    static string ValueText(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : JsonSerializer.Serialize(value, Indented);

    static Maybe<string> Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : Maybe<string>.None;
}

/// <summary>
/// One entry of permission_suggestions (task bd55bd54): its type (addRules, addDirectories, setMode, ...), where it would
/// be kept (session, localSettings, ...), its rule behaviour (allow, for addRules), the rules or folders it names as text,
/// and the entry itself as JSON, kept so the answer hands Claude Code back exactly what it offered
/// (PermissionAnswer.SuggestionLine).
/// </summary>
public sealed record PermissionSuggestion(string Type, string Destination, string Behavior, IReadOnlyList<string> Names, string Json)
{
    public static Maybe<PermissionSuggestion> Of(JsonElement entry) =>
        entry.ValueKind == JsonValueKind.Object && Word(entry, "type") is { Length: > 0 } type
            ? new PermissionSuggestion(type, Word(entry, "destination"), Word(entry, "behavior"), NamesOf(entry), entry.GetRawText())
            : Maybe<PermissionSuggestion>.None;

    // What the entry names, for its words on the card: an addRules entry's rules (the rule's content, "git init *", or the
    // bare tool name when the rule has none), an addDirectories entry's folders.
    static IReadOnlyList<string> NamesOf(JsonElement entry) =>
        entry.TryGetProperty("rules", out var rules) && rules.ValueKind == JsonValueKind.Array
            ? rules.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.Object)
                .Select(r => Word(r, "ruleContent") is { Length: > 0 } content ? content : Word(r, "toolName"))
                .Where(n => n.Length > 0).ToList()
            : entry.TryGetProperty("directories", out var folders) && folders.ValueKind == JsonValueKind.Array
                ? folders.EnumerateArray().Where(f => f.ValueKind == JsonValueKind.String).Select(f => f.GetString() ?? "")
                    .Where(n => n.Length > 0).ToList()
                : [];

    static string Word(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}

/// <summary>One field of a permission request's tool_input: its name and its value as text.</summary>
public sealed record PermissionField(string Name, string Value);
