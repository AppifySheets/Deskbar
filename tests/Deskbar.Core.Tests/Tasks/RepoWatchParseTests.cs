using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
using Xunit;

namespace Deskbar.Core.Tests.Tasks;

// The repository watch reads two gh answers (task c8e6967d, docs/superpowers/specs/2026-11-27-repo-watcher-design.md):
// one GraphQL answer per repository and `gh run list` per merge commit. Strings below are the shapes gh prints.
public sealed class RepoWatchParseTests
{
    static readonly PullRequestLink Pr12 = new("AppifySheets", "Deskbar", 12);
    static readonly PullRequestLink Pr13 = new("AppifySheets", "Deskbar", 13);

    [Fact]
    public void ParsesOpenConflictingPullRequest()
    {
        const string json = """
            {"data":{"repository":{"p12":{"state":"OPEN","mergeable":"CONFLICTING","baseRefName":"main",
            "baseRef":{"target":{"oid":"abc"}},"mergeCommit":null}}}}
            """;

        var facts = RepoWatch.ParseRepository([Pr12], json).Value.Single();

        Assert.Equal(Pr12, facts.Link);
        Assert.Equal(PullRequestState.Open, facts.State);
        Assert.Equal(Mergeable.Conflicting, facts.Mergeable);
        Assert.Equal("main", facts.BaseRef);
        Assert.Equal("abc", facts.BaseHead);
        Assert.True(facts.MergeCommit.HasNoValue);
    }

    [Fact]
    public void ParsesMergedWithMergeCommit()
    {
        const string json = """
            {"data":{"repository":{"p12":{"state":"MERGED","mergeable":"UNKNOWN","baseRefName":"main",
            "baseRef":{"target":{"oid":"abc"}},"mergeCommit":{"oid":"def456"}}}}}
            """;

        var facts = RepoWatch.ParseRepository([Pr12], json).Value.Single();

        Assert.Equal(PullRequestState.Merged, facts.State);
        Assert.Equal(Mergeable.Unknown, facts.Mergeable);
        Assert.Equal("def456", facts.MergeCommit.Value);
    }

    [Fact]
    public void MissingAliasFails()
    {
        const string json = """
            {"data":{"repository":{"p12":{"state":"OPEN","mergeable":"MERGEABLE","baseRefName":"main",
            "baseRef":{"target":{"oid":"abc"}},"mergeCommit":null}}}}
            """;

        Assert.True(RepoWatch.ParseRepository([Pr12, Pr13], json).IsFailure);
    }

    [Fact]
    public void ParsesRunsFinishedAndRunning()
    {
        const string json = """
            [{"name":"Release","status":"completed","conclusion":"success","url":"https://x/1"},
             {"name":"Deploy","status":"in_progress","conclusion":null,"url":"https://x/2"}]
            """;

        var runs = RepoWatch.ParseRuns(json).Value;

        Assert.Equal(new RunFacts("Release", "https://x/1", true, "success"), runs[0]);
        Assert.Equal(new RunFacts("Deploy", "https://x/2", false, ""), runs[1]);
    }

    [Fact]
    public void QueryNamesEveryPullRequest()
    {
        var query = RepoWatch.Query([Pr12, Pr13]);

        Assert.Contains("p12: pullRequest(number: 12)", query);
        Assert.Contains("p13: pullRequest(number: 13)", query);
        Assert.Contains("repository(owner: \"AppifySheets\", name: \"Deskbar\")", query);
    }
}
