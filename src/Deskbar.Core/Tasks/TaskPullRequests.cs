using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// A TASK KNOWS ITS PULL REQUESTS (task e7aa49b1, 2026-11-25). Petre: "when a session creates a pr, add it to the task so
// that i can search by pr id in tasks". Every merge here names a pull request by number, and so does every release note
// and CI run, so the number is what he has in hand when he wants the task behind it.
//
// HOW A PULL REQUEST GETS ON A TASK. Two ways, both through TaskBoard.AddPullRequest onto the session's own task:
//   * by itself: Deskbar's PostToolUse hook (ClaudeHooks in the app) runs when a session's Bash or PowerShell command
//     matches `gh pr create*`, which is how sessions here open pull requests. `gh pr create` prints the new pull
//     request's URL, and Created reads it out of the tool's output. The hook's `if` filter keeps it from running on
//     every other command; that filter is best effort (Claude Code runs the hook anyway when it cannot split a command),
//     so Created checks the command again and ignores anything else.
//   * by hand: `task pr <url> --session <id>`, for a pull request opened some other way (the GitHub web page, `gh api`,
//     a script), or one opened before this existed.
//
// HOW THE SEARCH FINDS IT (Of, Number). The tasks panel's search reads "523", "#523", "PR 523", "pr#523", "Deskbar #523"
// and a pull request's URL as a pull request, and finds every task that has it (TaskSearch.Found). A task's pull
// requests for the search are the ones recorded on it AND the ones linked in its question, status line, description
// and comments: tasks from before this existed link theirs in the thread, and a session's "Merge [Deskbar #530](...)"
// ask is about its own pull request in practice. A thread that only mentions someone else's pull request then turns up
// too, which a search can afford: it shows one task too many, never one too few.
public static class TaskPullRequests
{
    // `gh pr create` as a command of its own anywhere in the line: at the start, or after a separator (; & | && ||, a
    // newline, an opening parenthesis or brace). "echo gh pr create" and "gh pr view" do not count.
    static readonly Regex CreateCommand = new(@"(^|[;&|(){}\n]\s*)gh\s+pr\s+create\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // What the search box reads as a pull request (see Query), nothing else on the line: "pr" or "pull" (glued to the
    // number or not), or a repository word starting with a letter followed by a space or a '#', then an optional '#' and
    // the number. A word must end before the number, so "1234" is the number 1234 and never the word "123" and 4.
    static readonly Regex Spoken = new(
        @"^\s*(?:(?<pr>pr|pull)(?![A-Za-z])\s*|(?<word>[A-Za-z][A-Za-z0-9_.-]*)\s*(?=#)|(?<word>[A-Za-z][A-Za-z0-9_.-]*)\s+)?#?(?<number>\d{1,7})\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The pull requests a session's command created: the GitHub pull request links in the tool's output when the
    /// command runs `gh pr create`, none otherwise. A failed `gh pr create` that says "a pull request for branch ...
    /// already exists: &lt;url&gt;" names the branch's pull request, which is this task's all the same.
    /// </summary>
    public static IReadOnlyList<PullRequestLink> Created(Maybe<string> command, string output) =>
        command.HasValue && CreateCommand.IsMatch(command.Value) ? AskedPullRequests.In(output) : [];

    /// <summary>
    /// Every pull request the search counts as the task's (see the header): the recorded ones, then the ones its texts
    /// link, each once.
    /// </summary>
    public static IReadOnlyList<PullRequestLink> Of(TaskItem task) =>
        task.PullRequests
            .Concat(new[] { task.Question.GetValueOrDefault(""), task.StatusLine, task.Description }
                .Concat(task.Comments.Select(c => c.Markdown))
                .SelectMany(AskedPullRequests.In))
            .DistinctBy(p => p.Key)
            .ToArray();

    /// <summary>
    /// What a search text names as a pull request: a URL is that exact pull request (owner, repo and number); "523",
    /// "#523", "PR 523" and "pull 523" are number 523 in any repository; "Deskbar #523" is 523 in a repository called
    /// Deskbar. None for anything else.
    /// </summary>
    public static Maybe<PullRequestQuery> Query(string text)
    {
        var links = AskedPullRequests.In(text);
        if (links.Count == 1 && text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length == 1)
            return new PullRequestQuery(links[0].Number, Maybe.From(links[0].Repo), Maybe.From(links[0].Owner));
        var match = Spoken.Match(text ?? "");
        if (!match.Success || !int.TryParse(match.Groups["number"].Value, out var number)) return Maybe<PullRequestQuery>.None;
        var word = match.Groups["word"];
        return new PullRequestQuery(number, word.Success ? Maybe.From(word.Value) : Maybe<string>.None, Maybe<string>.None);
    }
}

/// <summary>
/// A pull request as the search box names it (TaskPullRequests.Query): its number, and its repository and owner when the
/// text said them. Repository and owner compare without case, as GitHub's do.
/// </summary>
public sealed record PullRequestQuery(int Number, Maybe<string> Repo, Maybe<string> Owner)
{
    public bool Matches(PullRequestLink link) =>
        link.Number == Number
        && Repo.Map(r => r.Equals(link.Repo, StringComparison.OrdinalIgnoreCase)).GetValueOrDefault(true)
        && Owner.Map(o => o.Equals(link.Owner, StringComparison.OrdinalIgnoreCase)).GetValueOrDefault(true);

    /// <summary>Whether the task has this pull request (TaskPullRequests.Of).</summary>
    public bool Matches(TaskItem task) => TaskPullRequests.Of(task).Any(Matches);
}
