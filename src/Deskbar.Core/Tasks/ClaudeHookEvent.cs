using System.Text.Json;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// ONE CLAUDE CODE HOOK, AS DESKBAR READS IT. Design: docs/superpowers/specs/2026-11-22-tasks-board-design.md
// ("Claude sessions", "Non-interactive sessions"); plan Task 7; the measurements behind every rule here are in
// docs/superpowers/notes/2026-11-22-claude-hook-probes.md.
//
// Claude Code runs Deskbar's hook scripts (claude-hook.cmd and claude-task-hook.cmd, ClaudeSkill in the app) on five
// events and hands each run a JSON object on stdin. The script passes it on to the running Deskbar as `claude-hook --json <text>`, together with three
// variables from the hook's environment, and this record is what comes out:
//
//   Event      hook_event_name: SessionStart, UserPromptSubmit, Notification, Stop or SessionEnd.
//   SessionId  session_id: the same across a resume, which is how a resumed session finds its task again.
//   Folder     cwd, kept EXACTLY as given. It is not the only form a folder arrives in: the probes saw cwd in 8.3 short
//              form (C:\Users\PETRE~1.CHI\...) when the session was started with a short path, and the script's
//              CLAUDE_PROJECT_DIR (the verb's --from) always uses forward slashes. Matching either against a VS Code
//              window's title is Task 8's job, which normalises both sides (long path, one separator, case) first, so
//              nothing is lost by storing it raw here.
//   Source     source, SessionStart only: startup, resume, clear, compact or fork.
//   Reason     reason, SessionEnd only (an init property): "clear" when /clear ended it (Ruling 19).
//   Headless   whether nobody is sitting in this session (below).
//
// Unknown fields are skipped, not refused: Claude Code adds fields between versions (the probes saw a resume add four,
// and a session started inside another add scratchpad_dir). Only a missing event name or session id is refused,
// because without those there is nothing to act on, and the verb then exits 1 with nothing printed.
//
// HEADLESS (Ruling 33). The hook's JSON says nothing about whether the session is interactive, so the environment
// decides, and the script passes three variables through
// (the third, CLAUDE_JOB_DIR, is for background sessions: see OwnJob):
//
//   * CLAUDE_CODE_SESSION_ATTENDED, computed by Claude Code for every session and never inherited: "1" when someone is
//     at it. It is the main signal because the entrypoint lies in exactly the case that matters: a `claude -p` started
//     from inside a VS Code session inherits CLAUDE_CODE_ENTRYPOINT=claude-vscode but gets ATTENDED=0 (probe run 6).
//     Exactly "1" is attended; any other value that arrived is headless.
//   * When ATTENDED did not arrive (an older Claude Code without it): cmd passes an unset variable as an empty string
//     inside a batch file, or as the literal %CLAUDE_CODE_SESSION_ATTENDED% when a command runs at a prompt, so both
//     count as "not there". Then CLAUDE_CODE_ENTRYPOINT decides: sdk-cli (`claude -p`), sdk-ts and sdk-py (the Agent
//     SDKs), and no entrypoint at all, are headless; anything else (cli, claude-vscode, ...) is a person's session.
//
//   Transcript transcript_path: the session's JSONL transcript, on every event. Read only by Notification and Stop,
//              for the session's name (SessionTitle), which picks the asking window out of several on one folder.
public sealed record ClaudeHookEvent(string Event, string SessionId, string Folder, Maybe<string> Source, bool Headless,
    Maybe<string> Transcript = default)
{
    static readonly string[] HeadlessEntrypoints = ["sdk-cli", "sdk-ts", "sdk-py"];

    /// <summary>
    /// reason, SessionEnd only: why the session ended (clear, logout, prompt_input_exit, other, ...). "clear" is the one
    /// Deskbar acts on (Ruling 19, TaskBoard.SessionCleared). An init property, so no constructor call has to say it.
    /// </summary>
    public Maybe<string> Reason { get; init; }

    /// <summary>
    /// Whether the hook came from a background session (`claude --bg`): its CLAUDE_JOB_DIR names this session's own job
    /// (OwnJob). Such a session sends SessionEnd at the end of every turn while `claude agents` still lists it, so while
    /// the list is read that SessionEnd is not taken as the end (final review, item 13; RemoteControl.Ended).
    /// </summary>
    public bool Background { get; init; }

    /// <summary>
    /// A Stop hook's stop_hook_active: Claude is already continuing because a Stop hook blocked the stop, so this turn
    /// was nudged once and must be let go (stalled tasks, spec 2026-11-23-stalled-claude-tasks-design.md, part 3). Only
    /// a JSON true counts; anything else reads as false.
    /// </summary>
    public bool StopHookActive { get; init; }

    /// <summary>
    /// A Stop hook's background_tasks or session_crons is a non-empty array: the session has a shell, monitor, subagent
    /// or scheduled wake-up of its own running, so it is waiting on its own work and not parked on Petre. A missing or
    /// odd-typed field reads as none.
    /// </summary>
    public bool BackgroundWork { get; init; }

    /// <summary>
    /// A PostToolUse hook's tool_input.command: the shell command the session ran (Bash or PowerShell), or none. Read so
    /// Deskbar can tell a `gh pr create` (task e7aa49b1, TaskPullRequests.Created).
    /// </summary>
    public Maybe<string> ToolCommand { get; init; }

    /// <summary>
    /// A PostToolUse hook's tool_response as text: every string inside it, one per line (Bash's is an object with stdout
    /// and stderr; a failed call's is the error text itself, and PowerShell's shape is not documented, so every string
    /// counts). "" when there is none. Where `gh pr create` prints the new pull request's link.
    /// </summary>
    public string ToolOutput { get; init; } = "";

    /// <summary>A PreToolUse or PostToolUse hook's tool_name (Bash, PowerShell, AskUserQuestion, ...), or none.</summary>
    public Maybe<string> ToolName { get; init; }

    /// <summary>
    /// An AskUserQuestion hook's questions as the task's question text (DialogQuestion, task 493b7ba9), or none for any
    /// other tool or a tool_input with no question in it.
    /// </summary>
    public Maybe<string> DialogQuestion { get; init; }

    /// <summary>
    /// The same questions led by DialogQuestion.PageLead (task 28fd8550), but only when the card on the task's page can
    /// draw this dialog (DialogAsk.Parse accepts it); none otherwise. Whether the card is ON is the server's to decide
    /// (RemoteControl.DialogAsked), so both texts are carried and it picks.
    /// </summary>
    public Maybe<string> DialogQuestionOnPage { get; init; }

    /// <summary>
    /// An AskUserQuestion PostToolUse hook's answers in the task's thread wording ("Header: answer", one per line;
    /// DialogAsk.Summary), whether they were picked in the terminal or on the task's card (task c047d0a0), or none. Read
    /// from tool_response.answers, which maps each question's text to its answer: the tool's result as the 2026-11-28
    /// probe's transcript recorded it, {"questions": [...], "answers": {"Pick a colour": "Blue"}}. A multi-select answer
    /// given as an array is joined with ", ".
    /// </summary>
    public Maybe<string> DialogAnswers { get; init; }

    /// <summary>
    /// A UserPromptSubmit hook's prompt: the message submitted, or none. Read so Deskbar can leave its own resume message
    /// out of the replies that set a waiting task's question aside (RepliedTasks, task 8bd18246).
    /// </summary>
    public Maybe<string> Prompt { get; init; }

    /// <summary>
    /// A Stop hook's last_assistant_message: the text of Claude's final message in the turn (Claude Code 2.1.47 on), or
    /// none. Read for the manager only (task 2c5de076, ManagerQuestion.AfterStop): its answer's first line goes on the
    /// manager line. Claude Code's docs advise this field over the transcript, which may not hold the last reply yet.
    /// </summary>
    public Maybe<string> LastMessage { get; init; }

    /// <summary>
    /// A Notification hook's notification_type (permission_prompt, elicitation_dialog, idle_prompt, ...), or none. Which
    /// ones mean a dialog is waiting on the user is SessionPrompt's list (task b310a649).
    /// </summary>
    public Maybe<string> NotificationType { get; init; }

    /// <summary>
    /// The hook's message: a Notification's text ("Claude needs your permission", "Session paused", ...) or an Elicitation's
    /// request from its MCP server. None for other events.
    /// </summary>
    public Maybe<string> Message { get; init; }

    /// <summary>An Elicitation hook's mcp_server_name: the MCP server asking for input, or none.</summary>
    public Maybe<string> McpServer { get; init; }

    /// <summary>
    /// agent_id, present when a SUBAGENT's hook fired: its dialog is answered in its own transcript (TranscriptMark.For).
    /// Only letters, digits, '-' and '_' are taken, since it becomes part of a file name.
    /// </summary>
    public Maybe<string> AgentId { get; init; }

    static readonly System.Text.RegularExpressions.Regex SafeAgentId = new("^[A-Za-z0-9_-]{1,64}$");

    public static Result<ClaudeHookEvent> Parse(string json, string? entrypoint, string? attended, string? job = null)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Result.Failure<ClaudeHookEvent>("the hook's JSON is not an object");

            var hookEvent = Text(root, "hook_event_name");
            var session = Text(root, "session_id");
            if (hookEvent.HasNoValue) return Result.Failure<ClaudeHookEvent>("the hook's JSON has no hook_event_name");
            if (session.HasNoValue) return Result.Failure<ClaudeHookEvent>("the hook's JSON has no session_id");

            return new ClaudeHookEvent(hookEvent.Value, session.Value, Text(root, "cwd").GetValueOrDefault(""),
                Text(root, "source"), IsHeadless(entrypoint, attended, job, session.Value), Text(root, "transcript_path"))
            {
                Reason = Text(root, "reason"),
                Background = OwnJob(job, session.Value),
                StopHookActive = root.TryGetProperty("stop_hook_active", out var active) && active.ValueKind == JsonValueKind.True,
                BackgroundWork = NonEmptyArray(root, "background_tasks") || NonEmptyArray(root, "session_crons"),
                ToolCommand = root.TryGetProperty("tool_input", out var input) && input.ValueKind == JsonValueKind.Object
                    ? Text(input, "command")
                    : Maybe<string>.None,
                ToolOutput = root.TryGetProperty("tool_response", out var response) ? string.Join("\n", Strings(response)) : "",
                ToolName = Text(root, "tool_name"),
                DialogQuestion = Text(root, "tool_name") == Tasks.DialogQuestion.Tool && root.TryGetProperty("tool_input", out var asked)
                    ? Tasks.DialogQuestion.From(asked)
                    : Maybe<string>.None,
                // The card refuses what DialogAsk.Parse refuses (no options, two questions alike), and then the terminal
                // keeps the dialog, so only a dialog the card can draw gets a page-led question.
                DialogQuestionOnPage = Text(root, "tool_name") == Tasks.DialogQuestion.Tool
                                       && root.TryGetProperty("tool_input", out var drawn)
                                       && DialogAsk.Parse(drawn.GetRawText()).HasValue
                    ? Tasks.DialogQuestion.From(drawn, onPage: true)
                    : Maybe<string>.None,
                DialogAnswers = Text(root, "tool_name") == Tasks.DialogQuestion.Tool
                                && Text(root, "hook_event_name") == "PostToolUse"
                                && root.TryGetProperty("tool_input", out var dialogInput)
                                && root.TryGetProperty("tool_response", out var dialogResult)
                    ? DialogAsk.AnswersIn(dialogResult).Bind(answers =>
                        DialogAsk.Parse(dialogInput.GetRawText()).Map(dialog => dialog.Summary(answers)))
                        .Where(summary => summary.Length > 0)
                    : Maybe<string>.None,
                Prompt = Text(root, "prompt"),
                LastMessage = Text(root, "last_assistant_message"),
                NotificationType = Text(root, "notification_type"),
                Message = Text(root, "message"),
                McpServer = Text(root, "mcp_server_name"),
                AgentId = Text(root, "agent_id").Where(SafeAgentId.IsMatch),
            };
        }
        catch (JsonException e)
        {
            return Result.Failure<ClaudeHookEvent>($"the hook's JSON does not parse: {e.Message}");
        }
    }

    // Every string value inside an element, depth first: a string is itself, an object or array is its members'.
    static IEnumerable<string> Strings(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => [element.GetString() ?? ""],
        JsonValueKind.Object => element.EnumerateObject().SelectMany(p => Strings(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(Strings),
        _ => [],
    };

    // A non-empty string field, or nothing: a number or an empty string where a name belongs is as good as missing.
    static bool NonEmptyArray(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0;

    static Maybe<string> Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : Maybe<string>.None;

    // A BACKGROUND SESSION (`claude --bg`) runs its hooks with ATTENDED=0 although a person can attach to it (probe notes,
    // "What a background session's hooks see"), so the flag alone would drop every one of its hooks. Its hooks carry
    // CLAUDE_JOB_DIR, but only a job directory that is THIS session's own counts (OwnJob). It arrives the way the other
    // variables do (empty or a literal %NAME% when unset), hence the same Arrived test.
    static bool IsHeadless(string? entrypoint, string? attended, string? job, string session) =>
        OwnJob(job, session) ? false
        : Arrived(attended) is { } given
            ? given != "1"
            : Arrived(entrypoint) is not { } entry || HeadlessEntrypoints.Contains(entry, StringComparer.OrdinalIgnoreCase);

    // The job directory's last segment is the first 8 characters of the session id (measured 2026-11-23, before and
    // after a `claude --bg --resume`). Matching on it is what keeps Ruling 33 shut: CLAUDE_JOB_DIR is an ordinary
    // environment variable of the background process, so a `claude -p` run from inside its Bash tool inherits it, and
    // a bare "a job directory arrived" test would count that headless run as attended. An inherited directory names
    // ANOTHER session, so it overrides nothing and the ATTENDED / entrypoint rule decides as before.
    static bool OwnJob(string? job, string session) =>
        Arrived(job) is { } dir
        && dir.TrimEnd('\\', '/').Split('\\', '/').Last() is { Length: 8 } last
        && session.StartsWith(last, StringComparison.OrdinalIgnoreCase);

    // A variable's value as the script passed it, or null when the variable was not set: empty (cmd in a batch file) or
    // the literal %NAME% (cmd at a prompt).
    static string? Arrived(string? value) =>
        string.IsNullOrWhiteSpace(value) || (value.Length > 1 && value.StartsWith('%') && value.EndsWith('%')) ? null : value.Trim();
}
