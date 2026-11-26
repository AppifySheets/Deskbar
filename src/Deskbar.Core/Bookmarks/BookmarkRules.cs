using CSharpFunctionalExtensions;
using Deskbar.Core.Control;

namespace Deskbar.Core.Bookmarks;

// What makes a bookmark acceptable, in one place: the manager checks a bookmark on its way in, and
// BookmarkConverter checks it on its way out of state.json, so a hand-edited file cannot smuggle in what the menu
// refuses. A bookmark's Target ends up on a command line (a browser, a program, explorer.exe shell:AppsFolder\<id>),
// which is why the rules are strict about quotes and control characters.
public static class BookmarkRules
{
    // Returns the bookmark as it should be stored (Name trimmed; a Link's Target trimmed) or says why it is refused.
    public static Result<Bookmark> Check(Bookmark bookmark) =>
        Result.Success(bookmark)
            .Ensure(b => b.Id != Guid.Empty, "A bookmark needs an id.")
            .Ensure(b => Enum.IsDefined(b.Kind), "A bookmark needs a known kind.")
            .Ensure(b => !string.IsNullOrWhiteSpace(b.Name), "A bookmark needs a name.")
            .Ensure(b => b.Target is not null, "A bookmark needs a target.")
            .Bind(b => b.Kind switch
            {
                BookmarkKind.Link => CheckLink(b.Target.Trim()).Map(t => b with { Target = t }),
                BookmarkKind.Program => CheckProgram(b),
                _ => CheckApp(b),
            })
            .Map(b => b with { Name = b.Name.Trim() });

    // A new Link bookmark for a typed or pasted address, named `name` or else after the host.
    public static Result<Bookmark> Link(string url, string? name = null) =>
        CheckLink((url ?? "").Trim())
            .Map(target => new Bookmark(Guid.NewGuid(), string.IsNullOrWhiteSpace(name) ? new Uri(target).Host : name, BookmarkKind.Link, target))
            .Bind(Check);

    // Whether two bookmarks open the same thing: what the duplicate refusal compares.
    public static bool SameTarget(Bookmark a, Bookmark b) =>
        a.Kind == b.Kind && a.Kind switch
        {
            // AbsoluteUri, so a host's case and a default port do not make two spellings of one page.
            BookmarkKind.Link => Uri.TryCreate(a.Target, UriKind.Absolute, out var ua)
                                 && Uri.TryCreate(b.Target, UriKind.Absolute, out var ub)
                                 && string.Equals(ua.AbsoluteUri, ub.AbsoluteUri, StringComparison.Ordinal),
            // The same exe with other arguments is a different bookmark (two RustDesk sessions, say).
            BookmarkKind.Program => string.Equals(a.Target, b.Target, StringComparison.OrdinalIgnoreCase)
                                    && string.Equals((a.Arguments ?? "").Trim(), (b.Arguments ?? "").Trim(), StringComparison.Ordinal),
            _ => string.Equals(a.Target, b.Target, StringComparison.OrdinalIgnoreCase),
        };

    // Absolute http or https only: a file: or javascript: link or a custom scheme would run something other than a page.
    static Result<string> CheckLink(string target) =>
        Uri.TryCreate(target, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && uri.Host.Length > 0
        && ControlArguments.IsPlainText(target)
            ? target
            : Result.Failure<string>($"'{target}' is not a web link; it must start with http:// or https:// and hold no quote or line break.");

    static Result<Bookmark> CheckProgram(Bookmark b) =>
        Result.Success(b)
            .Ensure(p => ControlArguments.IsPlainText(p.Target) && Path.IsPathFullyQualified(p.Target)
                         && p.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase),
                $"'{b.Target}' is not the full path of a program (it must be a complete path ending in .exe).")
            .Ensure(p => !(p.Arguments ?? "").Any(char.IsControl), "The arguments cannot hold a line break.")
            .Ensure(p => string.IsNullOrEmpty(p.WorkingDirectory)
                         || (!p.WorkingDirectory.Any(char.IsControl) && Path.IsPathFullyQualified(p.WorkingDirectory)),
                "The working folder must be a complete path.");

    // An AppUserModelID reaches explorer.exe's command line: no quote, control character or whitespace.
    static Result<Bookmark> CheckApp(Bookmark b) =>
        Result.Success(b)
            .Ensure(a => a.Target.Length > 0 && ControlArguments.IsPlainText(a.Target) && !a.Target.Any(char.IsWhiteSpace),
                $"'{b.Target}' is not an app id.");
}
