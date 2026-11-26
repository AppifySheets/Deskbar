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
