using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// A LINK TO A TASK that opens it in Deskbar when clicked (task 5016d533: "create a link to the task and include it in
// github pr / issues / elsewhere so that when i click on it, it opens the task in deskbar").
//
// Two forms, one id:
//
//   * deskbar://task/<id>, the protocol Deskbar registers on the PC (TaskLinkProtocol for the direct build, the
//     package manifest's windows.protocol for the Store one). Windows starts Deskbar.exe with the link, which hands
//     `task open <id>` to the running copy (ControlCommandLine.RunTaskLink) and the task opens in its own window.
//   * https://deskbar.app/task#<id>, the form that is WRITTEN anywhere. GitHub keeps only http and https links in a
//     pull request, issue or comment: rendered through its markdown API on 2026-11-26, [a](deskbar://task/...) came
//     back as the bare text "a", with no link at all, while the https one stayed a link. So what goes into a pull
//     request is the web form, and that page (site/task in the deskbar-site repository) hands the id on to the
//     deskbar: protocol in the browser. The id rides in the #fragment, which a browser never sends to a server.
//
// Both forms carry the whole id (a GUID), so a link can never become ambiguous as the board grows; the eight
// characters people see ("task 5016d533") are the link's TEXT, and an eight-character prefix is still accepted when
// read back, the way every other task command takes one.
public static class TaskLink
{
    // The URL scheme Deskbar registers, and the page that hands a web link to it.
    public const string Scheme = "deskbar";
    public const string WebPage = "https://deskbar.app/task";

    // The link to write in a pull request, an issue, a comment: anywhere that is not Deskbar itself.
    public static string Web(Guid id) => $"{WebPage}#{id:D}";

    // The link Windows hands to Deskbar.exe.
    public static string App(Guid id) => $"{Scheme}://task/{id:D}";

    // The short form every message uses for a task: the first eight characters of its id.
    public static string Short(Guid id) => id.ToString("D")[..8];

    // The markdown a session puts in a pull request or issue: [Deskbar task 5016d533](https://deskbar.app/task#...).
    public static string Markdown(Guid id) => $"[Deskbar task {Short(id)}]({Web(id)})";

    // deskbar://task/<id>, deskbar:task/<id> (Windows may pass either, and a browser sometimes drops the slashes),
    // with or without a trailing slash, and the web form https://deskbar.app/task#<id> (or /task/#<id>, www. too).
    // The id is a whole GUID or a start of one of at least eight hex digits, so nothing else that arrives through
    // the protocol is ever forwarded as a command argument.
    static readonly Regex LinkPattern = new(
        @"^(?:deskbar:(?://)?task/|https?://(?:www\.)?deskbar\.app/task/?#)" +
        @"(?<id>[0-9a-f]{8}(?:-[0-9a-f]{4}){0,3}(?:-[0-9a-f]{12})?)/?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // The task id a link names, lower case, as TaskBoard.Find takes it; None for anything that is not a task link.
    public static Maybe<string> TaskIdOf(string? link) =>
        link is { Length: > 0 } && LinkPattern.Match(Uri.UnescapeDataString(link.Trim())) is { Success: true } match
            ? match.Groups["id"].Value.ToLowerInvariant()
            : Maybe<string>.None;

    // Whether a link opens a task, the web form included: Deskbar's own markdown opens it directly rather than going
    // out to the browser and back (MarkdownBlocks).
    public static bool Is(string? link) => TaskIdOf(link).HasValue;
}
