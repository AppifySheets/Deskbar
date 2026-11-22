using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Rules;

// Pure functions: window metadata + rule list in, decision out. No I/O, no state --
// this is the spec's "RulesEngine" component and the most heavily unit-tested code.
public static class RulesEngine
{
    public static Maybe<Guid> MatchWorkspace(WindowInfo window, IReadOnlyList<WorkspaceRule> rules) =>
        rules.TryFirst(r => Matches(window, r.Kind, r.Pattern)).Map(r => r.WorkspaceId);

    public static Maybe<string> MatchRename(WindowInfo window, IReadOnlyList<RenameRule> rules) =>
        RenameRuleFor(window, rules).Map(r => r.ShortName);

    // The rule itself rather than its name: "Restore title" on the bar removes the rule that renamed the window,
    // and needs to know which one that was (WorkspaceManager.RestoreTitle).
    public static Maybe<RenameRule> RenameRuleFor(WindowInfo window, IReadOnlyList<RenameRule> rules) =>
        rules.TryFirst(r => Matches(window, r.Kind, r.Pattern));

    static bool Matches(WindowInfo window, RuleMatchKind kind, string pattern) => kind switch
    {
        RuleMatchKind.ProcessName => window.ProcessName.Equals(pattern, StringComparison.OrdinalIgnoreCase),
        RuleMatchKind.TitleRegex => SafeIsMatch(window.Title, pattern),
        RuleMatchKind.BrowserProfile => BrowserProfile.FromCommandLine(window.CommandLine)
            .Map(p => p.Equals(pattern, StringComparison.OrdinalIgnoreCase))
            .GetValueOrDefault(false),
        _ => false,
    };

    // A malformed regex must degrade to "no match", never crash the pipeline. The bar writes its patterns
    // through RenamePattern.ToRegex, which escapes everything but the wildcard, but a state.json edited by
    // hand (or written by an older build's rule editor) can still hold anything.
    static bool SafeIsMatch(string input, string pattern)
    {
        try { return Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)); }
        catch (Exception e) when (e is ArgumentException or RegexMatchTimeoutException) { return false; }
    }
}
