using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// EVERY DIALOG A CLAUDE CODE SESSION WAITS ON BECOMES ITS TASK'S QUESTION (task b310a649, Petre: "model and implement all
// types of questions that claude code / vscode may show"; design docs/superpowers/specs/2026-11-27-claude-session-prompts-design.md).
//
// Until this, only the AskUserQuestion dialog reached a task (DialogQuestion, task 493b7ba9). A permission prompt, a plan
// to approve, an MCP server asking for input, "Session paused" and the rest only blinked the workspace, so the bar said
// nothing about which session was stuck on Petre or why.
//
// WHAT CLAUDE CODE SENDS (read in the 2.1.332 binary on 2026-11-27). Every blocking dialog Claude Code draws is in one
// registry, and an entry may carry a notification: six seconds after the dialog opens with no keypress, the Notification
// hook fires ONCE with that entry's text as `message` and its type as `notification_type`, the type defaulting to
// permission_prompt. So "Session paused" (a fallback model or usage credits to choose) arrives as permission_prompt too,
// which is why a detail is only ever attached to the two texts that are really about a tool (Named, below). The six
// seconds are useful: a prompt answered at once never reaches the bar. Texts seen there:
//
//   permission_prompt        "Claude needs your permission" (the terminal), "Claude needs your permission to use <tool>"
//                            (the SDK path VS Code takes), "Claude Code needs your approval for the plan", "Claude Code
//                            wants to enter plan mode", "Session paused", "A sandboxed command needs network access",
//                            "Claude proposed a session goal", "Claude wants to use your browser", ...
//   elicitation_dialog       "Claude Code needs your input" (an MCP server asks for values)
//   elicitation_url_dialog   the same for a link to open, or "An MCP server needs your input"
//   worker_permission_prompt "<teammate> needs permission for <tool>" (a teammate's question, shown in this session)
//   agent_needs_input        "Claude wants to use your computer", auto mode notices, a cloud session's question
//
// and, not questions: idle_prompt ("Claude is waiting for your input" a minute after a turn ends: the Stop nudge already
// covers a parked session), auth_success, auth_storage_failure, agent_completed, push_notification, computer_use_exit,
// plugin_notification. elicitation_response and elicitation_complete say an MCP request was answered (Settles).
//
// THE DETAIL. The notification names no command, and the transcript cannot either: a pending tool call is written only
// after it is decided (TranscriptMark's probe). The PermissionRequest hook, which fires when the dialog opens, does name
// it, and the Elicitation hook names the server and its message. SessionPrompts keeps the latest of those per session
// (PromptDetail) and this file words the question from it.
public static class SessionPrompt
{
    /// <summary>The first words of a question placed from a dialog that has no more specific wording.</summary>
    public const string Lead = "Answer in the session:";

    /// <summary>The first words of a plan's question when only the session can answer it.</summary>
    public const string PlanLead = "Approve the plan in the session";

    /// <summary>The first words of a plan's question while the plan card is on the task's page (PlanAsk).</summary>
    public const string PlanOnCardLead = "Approve Claude's plan";

    /// <summary>The tool Claude Code runs to ask for a plan's approval; its tool_input carries the plan.</summary>
    public const string PlanTool = "ExitPlanMode";

    static readonly IReadOnlyList<string> QuestionTypes =
        ["permission_prompt", "elicitation_dialog", "elicitation_url_dialog", "worker_permission_prompt", "agent_needs_input"];

    static readonly IReadOnlyList<string> SettlingTypes = ["elicitation_response", "elicitation_complete"];

    static readonly IReadOnlyList<string> ElicitationTypes = ["elicitation_dialog", "elicitation_url_dialog"];

    // The two notification texts that are about one tool call, so the PermissionRequest that opened the dialog names it.
    // Matched by their start: the SDK path appends " to use <tool>".
    const string PermissionText = "Claude needs your permission";
    const string PlanText = "Claude Code needs your approval for the plan";

    /// <summary>Whether a notification_type means a dialog is waiting on the user.</summary>
    public static bool IsQuestion(string type) => QuestionTypes.Contains(type, StringComparer.Ordinal);

    /// <summary>Whether a notification_type says an MCP request was answered, so its question can go.</summary>
    public static bool Settles(string type) => SettlingTypes.Contains(type, StringComparer.Ordinal);

    /// <summary>
    /// The task question for a notification, or none when the type is not a question or there is no text. The detail is
    /// the session's latest noted one (SessionPrompts.Detail); it names the question only where it is about the same
    /// thing: a tool permission or a plan for the PermissionRequest detail, an MCP dialog for the Elicitation detail.
    /// </summary>
    public static Maybe<string> Text(string type, string message, Maybe<PromptDetail> detail, bool planOnCard = false) =>
        !IsQuestion(type) || OneLine(message) is not { Length: > 0 } said
            ? Maybe<string>.None
            : Named(type, said, detail, planOnCard).GetValueOrDefault($"{Lead} {said}");

    /// <summary>
    /// The question worded from the detail, or none when the detail is not about this dialog (Text then says Claude
    /// Code's own words). The caller takes the detail's subagent for the transcript watch only when this has a value.
    /// The SDK's "Claude needs your permission to use &lt;tool&gt;" must name the detail's own tool.
    /// </summary>
    /// <paramref name="planOnCard"/>: the plan is on its card on the task's page (PlanAsk, task 8dbbaa0b), so the
    /// question does not send the user to the session.
    public static Maybe<string> Named(string type, string message, Maybe<PromptDetail> detail, bool planOnCard = false) =>
        OneLine(message) is var said && said.Length == 0 ? Maybe<string>.None : detail.Bind(d =>
            ElicitationTypes.Contains(type, StringComparer.Ordinal) && d.IsElicitation
                ? $"Answer {d.Server} in the session: {d.Summary}"
            : type == "permission_prompt" && said.StartsWith(PlanText, StringComparison.Ordinal) && d.Tool == PlanTool
                ? (planOnCard ? PlanOnCardLead : PlanLead) is var lead && d.Summary.Length > 0 ? $"{lead}: {d.Summary}" : lead
            : type == "permission_prompt" && said.StartsWith(PermissionText, StringComparison.Ordinal) && !d.IsElicitation && d.Tool != PlanTool
                && (said.Length == PermissionText.Length || said == $"{PermissionText} to use {d.Tool}")
                ? d.Summary.Length > 0 ? $"Allow {d.Tool} in the session: {d.Summary}" : $"Allow {d.Tool} in the session"
            : Maybe<string>.None);

    /// <summary>The text as one line: line breaks become spaces, runs of spaces one, the ends trimmed.</summary>
    public static string OneLine(string text) =>
        string.Join(' ', text.Split((char[])['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>
/// What the hook that opened a dialog said about it: the tool (or "mcp:" plus the server for an MCP request), one line
/// naming what it is about, the subagent that asked, and when it arrived (SessionPrompts uses it only while fresh).
/// </summary>
public sealed record PromptDetail(string Tool, string Summary, Maybe<string> AgentId, DateTimeOffset At)
{
    const string McpPrefix = "mcp:";

    /// <summary>The longest summary kept; the bar's line is one row, and the session's own dialog shows the rest.</summary>
    public const int SummaryLength = 160;

    public bool IsElicitation => Tool.StartsWith(McpPrefix, StringComparison.Ordinal);

    /// <summary>The MCP server's name, for an Elicitation detail.</summary>
    public string Server => IsElicitation ? Tool[McpPrefix.Length..] : "";

    /// <summary>
    /// A permission request's detail: its main field's first line (the command, the file, the URL: PermissionDisplay's
    /// choice), or for ExitPlanMode the plan's first line without its heading marks. Every hidden character is shown as
    /// a mark (PermissionDisplay.Visible), as on the card, since the line names what Petre is asked to allow.
    /// </summary>
    public static PromptDetail Of(PermissionRequest request, DateTimeOffset at) =>
        new(SessionPrompt.OneLine(request.Tool),
            Cut(request.Tool == SessionPrompt.PlanTool
                ? FirstLine(PermissionDisplay.Visible(request.Fields.FirstOrDefault(f => f.Name == "plan")?.Value ?? "")).TrimStart('#', ' ')
                : FirstLine(PermissionDisplay.Of(request).Parts.FirstOrDefault(p => p.Main)?.Text ?? "")),
            request.AgentId, at);

    /// <summary>An MCP server's request for input (the Elicitation hook): the server and its message.</summary>
    public static PromptDetail Elicitation(string server, string message, Maybe<string> agentId, DateTimeOffset at) =>
        new(McpPrefix + SessionPrompt.OneLine(server), Cut(SessionPrompt.OneLine(PermissionDisplay.Visible(message))), agentId, at);

    static string FirstLine(string text) =>
        text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";

    static string Cut(string text) => text.Length <= SummaryLength ? text : text[..(SummaryLength - 1)].TrimEnd() + "…";
}
