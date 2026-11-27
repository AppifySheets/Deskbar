using System.Reactive.Concurrency;
using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
using Microsoft.Reactive.Testing;
using Xunit;

namespace Deskbar.Core.Tests.Tasks;

// The clock of the repository watch (Deskbar task c8e6967d, RepoWatchPoll): what it asks, when, and that a failed look
// changes nothing. The rules themselves are tested in RepoWatchTests and RepoWatchFollowTests.
public sealed class RepoWatchPollTests
{
    static readonly DateTimeOffset Made = new(2026, 11, 27, 10, 0, 0, TimeSpan.Zero);
    static readonly PullRequestLink Pr1 = new("AppifySheets", "Deskbar", 1);

    sealed class MemoryStore : ITaskStore
    {
        public TaskBoard Stored { get; set; } = TaskBoard.Empty;

        public Result<TaskBoard> Load() => Stored;

        public Result Save(TaskBoard board) => Result.Success();
    }

    sealed class Poll : IDisposable
    {
        public TestScheduler Clock { get; } = new();
        public List<string> Repositories { get; } = [];
        public List<string> Runs { get; } = [];
        public Func<IReadOnlyList<PullRequestLink>, Result<IReadOnlyList<PullFacts>>> Answer { get; set; } = _ => Result.Failure<IReadOnlyList<PullFacts>>("gh is signed out");
        public Result<IReadOnlyList<RunFacts>> RunAnswer { get; set; } = Result.Failure<IReadOnlyList<RunFacts>>("gh is signed out");
        public List<WatchNudge> Nudges { get; } = [];
        public TaskService Tasks { get; }
        public RepoWatchPoll Watcher { get; }

        // One Claude task with a session and the one pull request, so it is in flight.
        public Poll(bool withTask = true)
        {
            Clock.AdvanceTo(Made.UtcTicks);
            var board = TaskBoard.Empty;
            if (withTask)
            {
                var made = board.Create("Watched", Maybe<Guid>.None, new TaskSession("s1", @"C:\x", Made, Maybe<DateTimeOffset>.None), Made, Guid.NewGuid()).Value;
                board = made.Board.AddPullRequest(made.Task.Id, Pr1, Made).Value;
            }
            Tasks = new TaskService(new MemoryStore { Stored = board }, () => Clock.Now);
            Tasks.Load();
            Tasks.WatchNudges.Subscribe(Nudges.Add);
            Watcher = new RepoWatchPoll(Tasks,
                (repository, links) =>
                {
                    Repositories.Add($"{repository}:{string.Join(",", links.Select(l => l.Number))}");
                    return System.Threading.Tasks.Task.FromResult(Answer(links));
                },
                (link, sha) =>
                {
                    Runs.Add($"{link.Key}@{sha}");
                    return System.Threading.Tasks.Task.FromResult(RunAnswer);
                },
                Clock, Scheduler.Immediate);
        }

        public void Dispose() => Watcher.Dispose();
    }

    static Result<IReadOnlyList<PullFacts>> Open(IReadOnlyList<PullRequestLink> links, string head) =>
        Result.Success<IReadOnlyList<PullFacts>>([.. links.Select(l => new PullFacts(l, PullRequestState.Open, Mergeable.Mergeable, "main", head, Maybe<string>.None))]);

    [Fact]
    public void PollLooksEachInFlightRepositoryOnce()
    {
        using var poll = new Poll { Answer = links => Open(links, "h1") };

        poll.Clock.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);

        Assert.Equal(["appifysheets/deskbar:1"], poll.Repositories);
        Assert.True(poll.Tasks.IsWatched(poll.Tasks.Board.Tasks.Single()));

        // The next minute asks again, once; the base did not move, so nothing is nudged.
        poll.Clock.AdvanceBy(RepoWatchPoll.Every.Ticks);
        Assert.Equal(2, poll.Repositories.Count);
        Assert.Empty(poll.Nudges);
    }

    [Fact]
    public void NothingInFlightAsksNothing()
    {
        using var poll = new Poll(withTask: false);

        poll.Clock.AdvanceBy(RepoWatchPoll.Every.Ticks * 3);

        Assert.Empty(poll.Repositories);
    }

    // Review Focus 2: a failed look records nothing, so the repository is never counted as watched.
    [Fact]
    public void FailedLookChangesNothing()
    {
        using var poll = new Poll();

        poll.Clock.AdvanceBy(RepoWatchPoll.Every.Ticks * 2);

        Assert.NotEmpty(poll.Repositories);
        Assert.Empty(poll.Tasks.Watch.LastSuccess);
        Assert.False(poll.Tasks.IsWatched(poll.Tasks.Board.Tasks.Single()));
        Assert.Empty(poll.Nudges);
    }

    [Fact]
    public void BaseMoveBetweenPollsNudgesTheTask()
    {
        var head = "h1";
        using var poll = new Poll { Answer = links => Open(links, head) };
        poll.Clock.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);

        head = "h2";
        poll.Clock.AdvanceBy(RepoWatchPoll.Every.Ticks);

        Assert.Single(poll.Nudges);
        Assert.Contains("moved to h2", poll.Nudges[0].Text);
    }

    [Fact]
    public void FollowedMergeRunsAreLooked()
    {
        var merged = false;
        using var poll = new Poll
        {
            Answer = links => merged
                ? Result.Success<IReadOnlyList<PullFacts>>([new PullFacts(Pr1, PullRequestState.Merged, Mergeable.Unknown, "main", "h2", Maybe.From("merge1"))])
                : Open(links, "h1"),
            RunAnswer = Result.Success<IReadOnlyList<RunFacts>>([new RunFacts("Release", "https://x/1", true, "success")]),
        };
        poll.Clock.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);

        merged = true;
        poll.Clock.AdvanceBy(RepoWatchPoll.Every.Ticks);

        Assert.Equal([$"{Pr1.Key}@merge1"], poll.Runs);
        Assert.Equal(2, poll.Nudges.Count);
        Assert.Contains("merged", poll.Nudges[0].Text);
        Assert.Contains("finished", poll.Nudges[1].Text);
        Assert.Empty(poll.Tasks.Watch.Following);
    }
}
