using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// WHERE A LINK BELONGS, when that is not simply the task it was clicked on (task 26f15d70, second round).
//
// The first round (PR 579) opened a task's link in the task's own workspace. Petre then clicked a link to
// [Deskbar #587](https://github.com/AppifySheets/Deskbar/pull/587) on a task he had added from his Personal workspace,
// and the page opened in Personal: "opened in personal, should have in deskbar". The task was Deskbar work filed under
// Personal, and the link said so better than the task's workspace did.
//
// So a link to a GitHub repository goes where that repository's work lives, and the board already records where that
// is: every task that mentions the repository (in its title, description, status line, question, comments or pull
// requests) sits on some workspace. Measured on Petre's board on 2026-11-26, open and closed tasks together:
//
//   appifysheets/deskbar                       Deskbar 70, Personal 2
//   eurocreditdigital/eurocredit.crm2alta.api  Services 5
//   appifysheets/deskbar-site                  Deskbar site 1, Deskbar 1
//   gepha-geo/core-apis                        GPH 2          (and six more repositories, each on one workspace)
//
// The rule: the workspace holding MORE THAN HALF of the tasks that mention the repository (counting only tasks on a
// workspace that still exists) is the link's home. A split with no majority (deskbar-site above) has no home, and the
// link falls back to the clicked task's workspace, as in the first round. So does any link that is not to a GitHub
// repository: a Jira ticket, a page, a file. A repository only ever mentioned on the clicked task gives that task's own
// workspace, the same answer as before.
//
// RepoWorkspace is the folder-side twin: it finds a repository's workspace from the folders of the sessions that worked
// in it, for a session's new task. A link carries an address, not a folder, so it is matched by the address instead.
public static class LinkHome
{
    // A GitHub address whose first two path segments are an owner and a repository, as GitHub allows them (letters,
    // digits, '-', '_' and '.'). The repository runs as far as those characters do, so whatever follows it (a '/', a
    // query, a bracket, or a sentence's comma or backtick) ends it; a trailing full stop, which GitHub allows nowhere at
    // the end of a name, and a ".git" are taken off afterwards (Name). Anything deeper (a pull request, a file, an issue)
    // belongs to the same repository.
    static readonly Regex Repository = new(
        @"https?://(?:www\.)?github\.com/(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // GitHub's own pages whose first segment is not an owner ("github.com/orgs/x/people", "github.com/settings/tokens"),
    // so their second segment is no repository.
    static readonly IReadOnlySet<string> NotOwners = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "orgs", "settings", "notifications", "marketplace", "sponsors", "features", "topics", "collections", "apps",
        "users", "search", "explore", "login", "pulls", "issues", "codespaces", "organizations", "enterprises", "about",
    };

    /// <summary>
    /// The lower-cased "owner/repo" a GitHub link points into, or None for any other link (another site, a GitHub page
    /// of its own such as settings, a file).
    /// </summary>
    public static Maybe<string> RepositoryOf(string address) =>
        Repositories(address).TryFirst();

    /// <summary>
    /// The workspace a link belongs in by what it points at (see the header): for a GitHub repository, the workspace
    /// holding more than half of the tasks that mention it, among the tasks on a workspace that still <paramref name="exists"/>.
    /// None when the link is not to a repository, or no workspace holds a majority; the caller then uses the task's own.
    /// </summary>
    public static Maybe<Guid> WorkspaceFor(string address, IEnumerable<TaskItem> tasks, Func<Guid, bool> exists) =>
        RepositoryOf(address).Bind(repository =>
        {
            var homes = tasks
                .Where(t => t.WorkspaceId.Map(exists).GetValueOrDefault(false))
                .Where(t => Mentions(t, repository))
                .Select(t => t.WorkspaceId.Value)
                .ToArray();
            return homes
                .GroupBy(id => id)
                .Where(g => g.Count() * 2 > homes.Length)
                .Select(g => g.Key)
                .TryFirst();
        });

    // Every place a task carries text a session or Petre wrote, plus the pull requests its sessions opened.
    static bool Mentions(TaskItem task, string repository) =>
        new[] { task.Title, task.Description, task.StatusLine, task.Question.GetValueOrDefault(""), task.QuestionSetAside.GetValueOrDefault("") }
            .Concat(task.Comments.Select(c => c.Markdown))
            .Concat(task.PullRequests.Select(p => p.Url))
            .Any(text => Repositories(text).Contains(repository));

    static IEnumerable<string> Repositories(string? text) =>
        Repository.Matches(text ?? "")
            .Where(m => !NotOwners.Contains(m.Groups["owner"].Value))
            .Select(m => (Owner: m.Groups["owner"].Value, Repo: Name(m.Groups["repo"].Value)))
            .Where(x => x.Repo.Length > 0)
            .Select(x => $"{x.Owner}/{x.Repo}".ToLowerInvariant());

    // "Deskbar." at the end of a sentence and "Deskbar.git" are the repository Deskbar.
    static string Name(string captured)
    {
        var name = captured.TrimEnd('.');
        return name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4].TrimEnd('.') : name;
    }
}
