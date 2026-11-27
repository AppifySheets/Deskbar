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

    // Captured from the real gh on 2026-11-27 (Deskbar pull request 646 open, 665 merged; the merge commit's runs), the
    // shapes the rules were written against: an open one has mergeCommit null, a merged one mergeable UNKNOWN.
    [Fact]
    public void ParsesTheRealGhAnswers()
    {
        var link646 = new PullRequestLink("AppifySheets", "Deskbar", 646);
        var link665 = new PullRequestLink("AppifySheets", "Deskbar", 665);
        const string repository = """
            {"data":{"repository":{"p646":{"state":"OPEN","mergeable":"MERGEABLE","baseRefName":"main","baseRef":{"target":{"oid":"bc5072b3a049e56e01da198e86c74123ada18cc6"}},"mergeCommit":null},"p665":{"state":"MERGED","mergeable":"UNKNOWN","baseRefName":"main","baseRef":{"target":{"oid":"bc5072b3a049e56e01da198e86c74123ada18cc6"}},"mergeCommit":{"oid":"bc5072b3a049e56e01da198e86c74123ada18cc6"}}}}}
            """;
        const string runs = """
            [{"conclusion":"success","name":"Store submission","status":"completed","url":"https://github.com/AppifySheets/Deskbar/actions/runs/45978464409"},{"conclusion":"success","name":"Release","status":"completed","url":"https://github.com/AppifySheets/Deskbar/actions/runs/45977664277"}]
            """;

        var facts = RepoWatch.ParseRepository([link646, link665], repository).Value;

        Assert.Equal(new PullFacts(link646, PullRequestState.Open, Mergeable.Mergeable, "main", "bc5072b3a049e56e01da198e86c74123ada18cc6", Maybe<string>.None), facts[0]);
        Assert.Equal(PullRequestState.Merged, facts[1].State);
        Assert.Equal("bc5072b3a049e56e01da198e86c74123ada18cc6", facts[1].MergeCommit.Value);
        Assert.All(RepoWatch.ParseRuns(runs).Value, run => Assert.True(run.Finished && run.Conclusion == "success"));
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
