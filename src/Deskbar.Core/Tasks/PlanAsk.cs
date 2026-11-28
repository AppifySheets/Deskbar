using System.Text.Json;
using System.Text.Json.Nodes;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

/// <summary>What the user clicked on a plan card.</summary>
public enum PlanDecision
{
    /// <summary>Approve the plan; the session leaves plan mode and asks before each edit, as before.</summary>
    Approve,

    /// <summary>Approve the plan, and let the session edit files without asking from now on.</summary>
    ApproveEdits,

    /// <summary>Do not approve it: Claude reads the user's note and plans again.</summary>
    KeepPlanning,
}

// CLAUDE'S PLAN, APPROVED FROM A TASK'S PAGE (task 8dbbaa0b, a subtask of c047d0a0: Petre, "model this in the task so i
// don't have to open claude code"). A session in plan mode ends its planning with the ExitPlanMode tool, and Claude Code
// draws a dialog: approve, approve and accept edits, or keep planning with what should change. That dialog reaches the
// permission desk through the same PermissionRequest hook as a tool permission and as AskUserQuestion (DialogAsk), and
// this record is what the plan card draws and what its buttons send back.
//
// THE SHAPE, measured on 2026-11-28 with Claude Code 2.1.332 (a background session in plan mode whose PermissionRequest
// hook answered, four runs): tool_input is {"plan": "<markdown>", "planFilePath": "<file under ~/.claude/plans>"},
// permission_mode "plan", and no permission_suggestions. The model's own tool call is empty; Claude Code fills the plan
// in from the plan file before the hook sees it.
//
// THE THREE ANSWERS, each measured in the same runs:
//   * behavior "allow" with updatedInput set to the tool_input as it came approves the plan: Claude read "User has
//     approved your plan" and its next permission request said permission_mode "default".
//   * the same with updatedPermissions [{setMode acceptEdits, destination session}] approves it and the next request
//     said permission_mode "acceptEdits": its Write ran without a question. The same entry the permission card's "stop
//     asking for edits" sends (PermissionAnswer.AllowEditsLine), in memory only, nothing written to settings files.
//   * behavior "deny" with a message keeps the session in plan mode: the message is the tool's result, word for word,
//     Claude revised the plan as it said and asked again (a new ExitPlanMode, a new card).
//   * an allow WITHOUT updatedInput is not an answer: the terminal's dialog stayed. That is why the Allow / Deny card
//     never took this tool (the PM's ruling Q8).
//
// WHAT IS REFUSED, so the terminal keeps the dialog: input that is not an object with a non-empty "plan" string.
//
// The lines are pure-ASCII JSON (every other character a \u escape), handed to the hook script over the control pipe
// like a dialog's answers and never written to a file (PermissionDesk.AnswerPlan, Fetch; PermissionAnswer's header says
// why).
public sealed record PlanAsk(string Plan, string Input)
{
    /// <summary>The tool Claude Code runs to ask for a plan's approval.</summary>
    public const string Tool = SessionPrompt.PlanTool;

    /// <summary>The longest note sent back: a note is a few sentences, and the line travels through cmd's output.</summary>
    public const int NoteLength = 4000;

    /// <summary>How Claude is told the plan was not approved; the user's note follows it.</summary>
    public const string KeepPlanningLead = "The user did not approve this plan in Deskbar and asks you to keep planning. Their note:";

    /// <summary>The plan a permission request carries, or none when it is not ExitPlanMode or has no plan to show.</summary>
    public static Maybe<PlanAsk> Of(PermissionRequest request) =>
        request.Tool == Tool ? Parse(request.Input) : Maybe<PlanAsk>.None;

    /// <summary>The plan in an ExitPlanMode tool_input's JSON text, or none (see the header for what is refused).</summary>
    public static Maybe<PlanAsk> Parse(string input)
    {
        try
        {
            using var document = JsonDocument.Parse(input);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                   && root.TryGetProperty("plan", out var plan)
                   && plan.ValueKind == JsonValueKind.String
                   && plan.GetString() is { } text
                   && text.Trim().Length > 0
                ? new PlanAsk(text, input)
                : Maybe<PlanAsk>.None;
        }
        catch (JsonException)
        {
            return Maybe<PlanAsk>.None;
        }
    }

    /// <summary>
    /// The one line the hook prints for a decision. Approving echoes the tool_input back unchanged as updatedInput,
    /// because an allow without it is not taken (the header). Keep planning needs a note: without one Claude would not
    /// know what to change, and the terminal's own "keep planning" asks for it too.
    /// </summary>
    public Result<string> AnswerLine(PlanDecision decision, string note = "") =>
        decision == PlanDecision.KeepPlanning
            ? Result.SuccessIf(note.Trim().Length > 0, "Write what should change first.")
                .Ensure(() => note.Trim().Length <= NoteLength, $"The note is longer than {NoteLength} characters.")
                .Map(() => Line(new JsonObject { ["behavior"] = "deny", ["message"] = $"{KeepPlanningLead} {note.Trim()}" }))
            : Line(decision == PlanDecision.ApproveEdits
                ? new JsonObject
                {
                    ["behavior"] = "allow",
                    ["updatedInput"] = JsonNode.Parse(Input),
                    ["updatedPermissions"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "setMode", ["mode"] = "acceptEdits", ["destination"] = "session",
                    }),
                }
                : new JsonObject { ["behavior"] = "allow", ["updatedInput"] = JsonNode.Parse(Input) });

    // The default encoder escapes everything outside ASCII (and HTML-sensitive characters), which is still JSON.
    static string Line(JsonObject decision) =>
        new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = PermissionRequest.Event, ["decision"] = decision },
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

    /// <summary>Deskbar's line in the task's thread once the plan is answered from the card.</summary>
    public static string ThreadLine(PlanDecision decision, string note = "") => decision switch
    {
        PlanDecision.Approve => "Approved Claude's plan.",
        PlanDecision.ApproveEdits => "Approved Claude's plan, with edits accepted from now on.",
        _ => $"Asked Claude to keep planning:\n{note.Trim()}",
    };
}
