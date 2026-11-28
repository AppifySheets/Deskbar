using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

/// <summary>What the user clicked on a permission card.</summary>
public enum PermissionDecision
{
    /// <summary>Allow this one call (the terminal's "Yes").</summary>
    Allow,

    /// <summary>Deny it (the terminal's "No"); Claude reads the fixed <see cref="PermissionAnswer.DenyMessage"/>.</summary>
    Deny,

    /// <summary>Allow it, and switch the session to auto mode (the terminal's "Yes, and switch to auto mode", task bd55bd54).</summary>
    AllowAuto,

    /// <summary>Hand the question back to the terminal: the hook prints nothing.</summary>
    Pass,
}

// WHAT THE HOOK PRINTS, AND WHEN (Allow / Deny from a task's page). The hook script waits for an answer file in its slot
// (PermissionDesk) holding one line, "<code> <word>", and prints a FIXED line for the word (allow, deny, auto), never
// text built from data, but for the fetched lines below. This class is the rule in C#: the script implements the same rule in cmd, and its tests hold it to
// this one.
//
// THE RULE FAILS CLOSED. Only a line whose code is exactly the code Deskbar handed that hook, followed by allow, deny,
// auto or fetch, gives an answer. A wrong code, a missing code, an unknown word, extra words, `pass` or an empty file give
// NOTHING, and a PermissionRequest hook that prints nothing leaves Claude Code's own terminal dialog (spike, run B5: in a
// background session the dialog stays). No path prints allow by default.
//
// A FETCHED LINE is the one exception to "never text built from data", for two kinds of answer:
//   * a DIALOG'S SLOT (task c047d0a0): the answer to AskUserQuestion is the picked labels and typed text, and the answer
//     to a plan (ExitPlanMode, task 8dbbaa0b, PlanAsk) echoes the plan back or carries the user's note, so its line
//     cannot be a literal of the script. Such a slot takes only the word "dialog";
//   * a SUGGESTION picked on a tool permission's card (task bd55bd54): "Yes, and don't ask again for: git init *" answers
//     with the very permission_suggestions entry Claude Code offered as updatedPermissions (SuggestionLine), which is data
//     too. A tool permission's slot takes the word "fetch" for it, besides allow, deny and auto.
// After the same code check, the script asks Deskbar for the line over the control pipe (`claude-permission --fetch`, its
// code on stdin; PermissionDesk.Fetch) and lets the reply, one line of pure-ASCII JSON, straight through to its own
// output, never parsing it. The line is never a file: a file beside the answer could be swapped by a process that watches
// the slot without knowing the code (the branch's review, finding 1), whereas the pipe's server is checked to be
// Deskbar's own executable (PipeTrust).
//
// The allow and deny lines are the ones measured to work in the spike (runs 1, 2 and 10). The deny message is fixed (the
// PM's ruling Q9: no free text, which would have to travel through a file and cmd into JSON). "Yes, and switch to auto
// mode" sets the mode with destination "session" only, the same in-memory switch the terminal's own choice makes:
// measured 2026-11-28 on Claude Code 2.1.332, the call ran and the session's next command said "Allowed by auto mode".
// (The card's old "Allow and stop asking for edits" went with task bd55bd54: the terminal no longer offers it, measured the
// same day, so the card now offers what the terminal offers, PermissionChoices.)
public static class PermissionAnswer
{
    /// <summary>What Claude reads when the user denies a call from the card.</summary>
    public const string DenyMessage = "The user denied this in Deskbar.";

    public const string AllowLine =
        """{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"}}}""";

    public const string DenyLine =
        """{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"deny","message":"The user denied this in Deskbar."}}}""";

    public const string AllowAutoLine =
        """{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow","updatedPermissions":[{"type":"setMode","mode":"auto","destination":"session"}]}}}""";

    /// <summary>
    /// The word that answers a dialog (AskUserQuestion, task c047d0a0; a plan, task 8dbbaa0b): the line printed is then
    /// the one Deskbar hands the script when it fetches it (PermissionDesk.Fetch, DialogAsk.AnswerLine, PlanAsk.AnswerLine). Taken only by a slot
    /// registered as a dialog's (<see cref="DialogKind"/>), and a dialog's slot takes no other word: a plain allow is not
    /// an answer to a dialog.
    /// </summary>
    public const string DialogWord = "dialog";

    /// <summary>
    /// The word that answers a tool permission with a picked suggestion (task bd55bd54): the line printed is the one
    /// Deskbar hands the script when it fetches it (PermissionDesk.AnswerSuggestion, <see cref="SuggestionLine"/>). Taken
    /// only by a tool permission's slot, never a dialog's.
    /// </summary>
    public const string FetchWord = "fetch";

    /// <summary>The second word of the registration's reply for a dialog's slot: "&lt;code&gt; dialog" (RemoteControl.Permission).</summary>
    public const string DialogKind = "dialog";

    /// <summary>
    /// The line the hook prints for an answer file's text, when the hook was handed <paramref name="code"/>: for a tool
    /// permission's slot (<paramref name="fetched"/> has no value) as <see cref="LineFor(string,string)"/> says; for a
    /// dialog's slot, the line its fetch returned ("" when it returned nothing), for exactly "&lt;code&gt; dialog" only.
    /// </summary>
    public static Maybe<string> LineFor(string answerText, string code, Maybe<string> fetched) =>
        fetched.HasNoValue
            ? LineFor(answerText, code)
            : Code.IsMatch(code)
              && answerText.TrimEnd('\r', '\n').Split(' ') is [var given, DialogWord]
              && given == code
                ? Fetched(fetched)
                : Maybe<string>.None;

    /// <summary>A code as PermissionDesk makes it: 32 lowercase hex digits.</summary>
    public static readonly Regex Code = new("^[0-9a-f]{32}$", RegexOptions.Compiled);

    /// <summary>The word the answer file carries for a decision.</summary>
    public static string WordOf(PermissionDecision decision) => decision switch
    {
        PermissionDecision.Allow => "allow",
        PermissionDecision.Deny => "deny",
        PermissionDecision.AllowAuto => "auto",
        _ => "pass",
    };

    /// <summary>
    /// The line a tool permission's hook prints for an answer file's text, when the hook was handed
    /// <paramref name="code"/>; none for anything but a valid answer (see the header). The fetch word gives nothing here:
    /// its line comes from Deskbar (<see cref="ToolLineFor"/>).
    /// </summary>
    public static Maybe<string> LineFor(string answerText, string code) => ToolLineFor(answerText, code, Maybe<string>.None);

    /// <summary>
    /// The line a tool permission's hook prints for an answer file's text, when the hook was handed <paramref name="code"/>
    /// and its fetch, made only for the fetch word, returned <paramref name="fetched"/>: a fixed line for allow, deny and
    /// auto, the fetched line for fetch, and none for anything else, the dialog word included.
    /// </summary>
    public static Maybe<string> ToolLineFor(string answerText, string code, Maybe<string> fetched) =>
        Code.IsMatch(code) && answerText.TrimEnd('\r', '\n').Split(' ') is [var given, var word] && given == code
            ? word switch
            {
                "allow" => AllowLine,
                "deny" => DenyLine,
                "auto" => AllowAutoLine,
                FetchWord => Fetched(fetched),
                _ => Maybe<string>.None,
            }
            : Maybe<string>.None;

    static Maybe<string> Fetched(Maybe<string> fetched) =>
        fetched.Map(f => f.TrimEnd('\r', '\n')).Where(f => f.Length > 0);

    /// <summary>
    /// The line that allows the call and applies one permission_suggestions entry, handed back exactly as Claude Code
    /// offered it, as the hooks reference's "PermissionRequest decision control" says to. Every non-ASCII character is
    /// written as a \u escape (the default encoder), so the line is pure ASCII and one line, as the script needs.
    /// </summary>
    public static string SuggestionLine(PermissionSuggestion suggestion) =>
        new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = PermissionRequest.Event,
                ["decision"] = new JsonObject
                {
                    ["behavior"] = "allow",
                    ["updatedPermissions"] = new JsonArray(JsonNode.Parse(suggestion.Json)),
                },
            },
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
}
