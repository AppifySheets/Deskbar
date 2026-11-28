using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

/// <summary>
/// One button of a permission card: its words, and what a click answers. <see cref="Suggestion"/> is set for a
/// "Yes, and ..." built from one of Claude Code's permission_suggestions (its index in
/// <see cref="PermissionRequest.Suggestions"/>); <see cref="Decision"/> is then Allow, and the answer goes through
/// PermissionDesk.AnswerSuggestion. <see cref="Primary"/> marks the one drawn filled, as the terminal's cursor starts on it.
/// </summary>
public sealed record PermissionChoice(string Words, PermissionDecision Decision, int? Suggestion = null, bool Primary = false);

// THE CARD OFFERS WHAT THE TERMINAL OFFERS (task bd55bd54, Petre's pick "A" of the mockup on 2026-11-28: "questions seems
// to be different", a card with Allow / Deny beside a terminal dialog offering "Yes, and switch to auto mode").
//
// Claude Code's dialog is Yes, then a "Yes, and ..." per choice it builds, then No. Measured on Claude Code 2.1.332 with a
// pseudo-console probe, printing the hook's JSON beside the dialog it drew:
//
//   prompt                    permission_suggestions                     the terminal's list
//   echo "$APPDATA" > f       none                                       Yes / switch to auto mode / No
//   git init                  addRules "git init *" (localSettings)      Yes / don't ask again for: git init * / auto / No
//   mkdir sub1                addDirectories (session), setMode edits    Yes / always allow access to <folder> / auto / No
//   Write notes.txt           setMode acceptEdits                        Yes / No
//
// So this list is:
//   * Yes.
//   * One "Yes, and ..." per addRules entry that allows ("don't ask again for: <rules>", the terminal's words) and per
//     addDirectories entry ("allow access to <folder>", said with where it is kept, since the entry is handed back as it
//     came and a session one does not outlive the session). Its answer hands the entry back (PermissionAnswer.SuggestionLine).
//   * NOT setMode entries: the terminal showed none of them in the runs above (it words one specially for a write inside
//     ~/.claude, which is not worth copying). Nor the remove/replace kinds, which no run offered.
//   * "Yes, and switch to auto mode" for a shell command (Bash, PowerShell) while the session is not in auto mode already:
//     the terminal offered it for every command above and not for the file write. Its answer is a fixed line, measured to
//     switch the session (PermissionAnswer.AllowAutoLine). Whether auto mode is open to the account is not in the hook's
//     JSON; it is on this machine (the terminal offers it), and what Claude Code makes of the switch on an account
//     without auto mode is not measured.
//   * No.
// "Answer in the terminal" is the card's own last button and not part of this list (TasksWindow.PermissionCard).
//
// The terminal's reason line ("A variable in this command can't be checked before it runs") is not in the hook's JSON, so
// the card cannot repeat it.
public static class PermissionChoices
{
    public const string YesText = "Yes";
    public const string NoText = "No";
    public const string AutoText = "Yes, and switch to auto mode";

    // The tools whose question is a shell command: only for those did the terminal offer auto mode.
    static readonly string[] ShellTools = ["Bash", "PowerShell"];

    // The modes the terminal offered auto mode from. A session already in auto (or bypassing permissions, or planning) is
    // not offered a switch.
    static readonly string[] AutoFrom = ["default", "acceptEdits"];

    /// <summary>The card's buttons for a tool permission, in the terminal's order.</summary>
    public static IReadOnlyList<PermissionChoice> Of(PermissionRequest request) =>
    [
        new(YesText, PermissionDecision.Allow, Primary: true),
        .. request.Suggestions.Select((s, i) => (Words: WordsFor(s), Index: i))
            .Where(s => s.Words is not null)
            .Select(s => new PermissionChoice(s.Words!, PermissionDecision.Allow, s.Index)),
        .. OffersAuto(request) ? [new PermissionChoice(AutoText, PermissionDecision.AllowAuto)] : Array.Empty<PermissionChoice>(),
        new(NoText, PermissionDecision.Deny),
    ];

    static bool OffersAuto(PermissionRequest request) =>
        ShellTools.Contains(request.Tool, StringComparer.Ordinal)
        && request.Mode.Map(m => AutoFrom.Contains(m, StringComparer.Ordinal)).GetValueOrDefault(true);

    /// <summary>A suggestion's words on the card, or null for one the card does not offer (see the header).</summary>
    public static string? WordsFor(PermissionSuggestion suggestion) =>
        suggestion switch
        {
            { Type: "addRules", Behavior: "allow", Names.Count: > 0 } => $"Yes, and don't ask again for: {string.Join(", ", suggestion.Names)}",
            { Type: "addDirectories", Names.Count: > 0 } => $"Yes, and allow access to {string.Join(", ", suggestion.Names)}{Kept(suggestion.Destination)}",
            _ => null,
        };

    // Where an entry is kept, in words: the hooks reference's destinations.
    static string Kept(string destination) => destination switch
    {
        "session" => " for this session",
        "localSettings" or "projectSettings" => " from this project",
        "userSettings" => " in every project",
        _ => "",
    };
}
