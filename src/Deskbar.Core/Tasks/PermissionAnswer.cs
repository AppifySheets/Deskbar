using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

/// <summary>What the user clicked on a permission card.</summary>
public enum PermissionDecision
{
    /// <summary>Allow this one call.</summary>
    Allow,

    /// <summary>Deny it; Claude reads the fixed <see cref="PermissionAnswer.DenyMessage"/>.</summary>
    Deny,

    /// <summary>Allow it, and switch the session to accept edits from now on.</summary>
    AllowEdits,

    /// <summary>Hand the question back to the terminal: the hook prints nothing.</summary>
    Pass,
}

// WHAT THE HOOK PRINTS, AND WHEN (Allow / Deny from a task's page). The hook script waits for an answer file in its slot
// (PermissionDesk) holding one line, "<code> <word>", and prints a FIXED line for the word: these three, never text
// built from data. This class is the rule in C#: the script implements the same rule in cmd, and its tests hold it to
// this one.
//
// THE RULE FAILS CLOSED. Only a line whose code is exactly the code Deskbar handed that hook, followed by allow, deny or
// edits, gives an answer. A wrong code, a missing code, an unknown word, extra words, `pass` or an empty file give
// NOTHING, and a PermissionRequest hook that prints nothing leaves Claude Code's own terminal dialog (spike, run B5: in a
// background session the dialog stays). No path prints allow by default.
//
// A DIALOG'S SLOT is the one exception to "never text built from data" (task c047d0a0): the answer to AskUserQuestion
// is the picked labels and typed text, and the answer to a plan (ExitPlanMode, task 8dbbaa0b, PlanAsk) echoes the plan
// back or carries the user's note, so its line cannot be a literal of the script. After the same code check, and
// only for a slot whose registration said it is a dialog's, the script asks Deskbar for the line over the control pipe
// (`claude-permission --fetch`, its code on stdin; PermissionDesk.Fetch) and lets the reply, one line of pure-ASCII JSON
// (DialogAsk.AnswerLine), straight through to its own output, never parsing it. The line is never a file: a file beside
// the answer could be swapped by a process that watches the slot without knowing the code (the branch's review,
// finding 1), whereas the pipe's server is checked to be Deskbar's own executable (PipeTrust).
//
// The three lines are the ones measured to work in the spike (runs 1, 2 and 10). The deny message is fixed (the PM's
// ruling Q9: no free text, which would have to travel through a file and cmd into JSON). "Allow and stop asking for
// edits" switches the mode with destination "session" only: the hooks reference says that one is in memory, so nothing
// is written to the user's settings files (the spike measured that a background session keeps the mode across a stop and
// wake, which the card says).
public static class PermissionAnswer
{
    /// <summary>What Claude reads when the user denies a call from the card.</summary>
    public const string DenyMessage = "The user denied this in Deskbar.";

    public const string AllowLine =
        """{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"}}}""";

    public const string DenyLine =
        """{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"deny","message":"The user denied this in Deskbar."}}}""";

    public const string AllowEditsLine =
        """{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow","updatedPermissions":[{"type":"setMode","mode":"acceptEdits","destination":"session"}]}}}""";

    /// <summary>
    /// The word that answers a dialog (AskUserQuestion, task c047d0a0; a plan, task 8dbbaa0b): the line printed is then
    /// the one Deskbar hands the script when it fetches it (PermissionDesk.Fetch, DialogAsk.AnswerLine, PlanAsk.AnswerLine). Taken only by a slot
    /// registered as a dialog's (<see cref="DialogKind"/>), and a dialog's slot takes no other word: a plain allow is not
    /// an answer to a dialog, and no line is fetched for a tool permission.
    /// </summary>
    public const string DialogWord = "dialog";

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
              && fetched.Value.TrimEnd('\r', '\n') is { Length: > 0 } line
                ? line
                : Maybe<string>.None;

    /// <summary>A code as PermissionDesk makes it: 32 lowercase hex digits.</summary>
    public static readonly Regex Code = new("^[0-9a-f]{32}$", RegexOptions.Compiled);

    /// <summary>The word the answer file carries for a decision.</summary>
    public static string WordOf(PermissionDecision decision) => decision switch
    {
        PermissionDecision.Allow => "allow",
        PermissionDecision.Deny => "deny",
        PermissionDecision.AllowEdits => "edits",
        _ => "pass",
    };

    /// <summary>
    /// The line the hook prints for an answer file's text, when the hook was handed <paramref name="code"/>; none for
    /// anything but a valid answer (see the header).
    /// </summary>
    public static Maybe<string> LineFor(string answerText, string code) =>
        Code.IsMatch(code) && answerText.TrimEnd('\r', '\n').Split(' ') is [var given, var word] && given == code
            ? word switch
            {
                "allow" => AllowLine,
                "deny" => DenyLine,
                "edits" => AllowEditsLine,
                _ => Maybe<string>.None,
            }
            : Maybe<string>.None;
}
