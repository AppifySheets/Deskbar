using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
using Xunit;

namespace Deskbar.Core.Tests.Tasks;

// Following a merge commit's runs (Deskbar task c8e6967d): the rules in RepoWatch.Follow, with runs given as values.
public sealed class RepoWatchFollowTests
{
    static readonly DateTimeOffset Since = new(2026, 11, 27, 10, 0, 0, TimeSpan.Zero);
    static readonly PullRequestLink Pr = new("AppifySheets", "Deskbar", 7);
    static readonly Guid TaskId = Guid.NewGuid();
    static readonly FollowedMerge Merge = new(TaskId, Pr, "abc1234", Since);

    static RepoWatchState Following => RepoWatchState.Empty with { Following = [Merge] };

    static RunFacts Done(string name, string conclusion) => new(name, $"https://x/{name}", true, conclusion);

    static readonly RunFacts Running = new("Deploy", "https://x/Deploy", false, "");

    static FollowStep Follow(IReadOnlyList<RunFacts> runs, TimeSpan after) =>
        RepoWatch.Follow(Following, Merge, runs, Since + after);

    [Fact]
    public void RedRunEndsFollowAndReportsIt()
    {
        var step = Follow([Done("Build", "failure"), Running], TimeSpan.FromMinutes(3));

        Assert.Empty(step.State.Following);
        var red = Assert.Single(step.Red);
        Assert.Equal(new RedRun(TaskId, "AppifySheets/Deskbar", "Build", "https://x/Build", Since.AddMinutes(3)), red);
        var nudge = Assert.Single(step.Nudges);
        Assert.Equal(TaskId, nudge.TaskId);
        Assert.Contains("Build failed on the merge commit of Deskbar #7: https://x/Build", nudge.Text);
    }

    [Fact]
    public void AllGreenNudgesOnce()
    {
        var step = Follow([Done("Build", "success"), Done("Release", "success")], TimeSpan.FromMinutes(5));

        Assert.Empty(step.State.Following);
        Assert.Empty(step.Red);
        var nudge = Assert.Single(step.Nudges);
        Assert.Contains("Build (success), Release (success)", nudge.Text);
        Assert.Contains("released, deployed, done", nudge.Text);
    }

    // Review focus 1: release.yml cancels an older build when a newer merge lands; that is not a failure.
    [Fact]
    public void CancelledIsNotRed()
    {
        var step = Follow([Done("Release", "cancelled")], TimeSpan.FromMinutes(5));

        Assert.Empty(step.Red);
        Assert.Empty(step.State.Following);
        Assert.Contains("Release (cancelled, a newer run took over)",Assert.Single(step.Nudges).Text);
    }

    [Fact]
    public void RunningRunsWait()
    {
        var step = Follow([Done("Build", "success"), Running], TimeSpan.FromMinutes(30));

        Assert.Single(step.State.Following);
        Assert.Empty(step.Nudges);
        Assert.Empty(step.Red);
    }

    [Fact]
    public void NoRunsAfterTenMinutes()
    {
        var step = Follow([], TimeSpan.FromMinutes(10));

        Assert.Empty(step.State.Following);
        Assert.Contains("no workflow runs started", Assert.Single(step.Nudges).Text);
    }

    [Fact]
    public void NoRunsBeforeTenMinutesWaits()
    {
        var step = Follow([], TimeSpan.FromMinutes(9));

        Assert.Single(step.State.Following);
        Assert.Empty(step.Nudges);
    }

    [Fact]
    public void GivesUpAfterThreeHours()
    {
        var step = Follow([Running], TimeSpan.FromHours(3));

        Assert.Empty(step.State.Following);
        Assert.Contains("after 3 hours", Assert.Single(step.Nudges).Text);
    }
}
