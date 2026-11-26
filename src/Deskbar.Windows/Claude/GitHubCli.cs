using System.IO;
using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.Windows.Claude;

// ASKS GITHUB WHERE A PULL REQUEST STANDS, through the `gh` program (task b7f987b4: a merged pull request settles the
// question that asked for the merge; PullRequestWatch and AskedPullRequests in Core hold the rules).
//
// Why gh and not GitHub's REST API over HTTP: gh is signed in as Petre, so it sees his private repositories too, and its
// rate limit is the signed-in 5000 an hour rather than the anonymous 60. The sessions that ask "merge this" already use
// gh for every pull request, so it is on any machine where such questions are asked. Where it is not, every look fails,
// nothing is settled, and the questions wait for `task answered` as before.
//
// gh is run through ClaudeCli with gh.exe as the program: that class is a general "run a program, read stdout, bound
// the time" runner whose only Claude-specific parts are the default program and the variables it strips, and those
// variables (CLAUDECODE and friends) mean nothing to gh. Found fresh on every look, for the reason ClaudeLocator gives:
// a gh installed while Deskbar runs should be used from the next look on, so a miss is never cached.
public static class GitHubCli
{
    // `gh pr view` answers in about a second (measured 2026-11-25); twenty covers a slow network without holding the
    // next look back for long (a look still out makes the next tick skip).
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// What GitHub says about one pull request: <c>gh pr view &lt;url&gt; --json state,closedAt</c>, read. A failure when
    /// gh cannot be found or run, or prints anything but the expected JSON.
    /// </summary>
    public static Task<Result<PullRequestFacts>> PullRequest(PullRequestLink link) =>
        Find() is { HasValue: true } gh
            ? new ClaudeCli(gh.Value).Run(["pr", "view", link.Url, "--json", "state,closedAt"], null, Timeout)
                .Bind(AskedPullRequests.Parse)
            : Task.FromResult(Result.Failure<PullRequestFacts>("gh.exe is not on the PATH or in %ProgramFiles%\\GitHub CLI"));

    /// <summary>
    /// What GitHub says about one pull request against the running commit (task 8470d176; RunningChanges holds the
    /// rules): <c>gh pr view &lt;url&gt; --json state,closedAt,mergeCommit</c>, and for a merged one GitHub's compare of
    /// its merge commit with <paramref name="runningCommit"/> (<c>gh api repos/o/r/compare/&lt;merge&gt;...&lt;running&gt;
    /// --jq .status</c>). `per_page=1` keeps the compare's list of commits to one; only its status is read. A failure
    /// when gh cannot be run, either answer is not what was expected, or GitHub does not know the running commit (a
    /// local build answers 404).
    /// </summary>
    public static Task<Result<PullRequestArrival>> Arrival(PullRequestLink link, string runningCommit) =>
        Find() is { HasValue: true } gh
            ? new ClaudeCli(gh.Value).Run(["pr", "view", link.Url, "--json", "state,closedAt,mergeCommit"], null, Timeout)
                .Bind(RunningChanges.ParseMerge)
                .Bind(pr => pr.State == PullRequestState.Merged
                    ? new ClaudeCli(gh.Value)
                        .Run(["api", $"repos/{link.Owner}/{link.Repo}/compare/{pr.MergeCommit.Value}...{runningCommit}?per_page=1", "--jq", ".status"], null, Timeout)
                        .Bind(RunningChanges.Contains)
                        .Map(running => new PullRequestArrival(pr.State, running))
                    : Task.FromResult(Result.Success(new PullRequestArrival(pr.State, false))))
            : Task.FromResult(Result.Failure<PullRequestArrival>("gh.exe is not on the PATH or in %ProgramFiles%\\GitHub CLI"));

    /// <summary>
    /// The full path of gh.exe: on Deskbar's own PATH, then the user's and the machine's as the registry holds them now,
    /// then the folder GitHub's installer (and winget) puts it in. None when it is in none of them.
    /// </summary>
    public static Maybe<string> Find() =>
        new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine }
            .SelectMany(target => (Environment.GetEnvironmentVariable("PATH", target) ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(folder => folder.Trim('"'))
            .Where(folder => folder.Length > 0)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI"))
            .Select(folder => Path.Combine(folder, "gh.exe"))
            .TryFirst(File.Exists);
}
