using System.Reactive.Concurrency;
using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
using Microsoft.Reactive.Testing;
using Xunit;
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

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
        public Poll(bool withTask = true, PullRequestLink? other = null)
        {
            Clock.AdvanceTo(Made.UtcTicks);
            var board = TaskBoard.Empty;
            if (withTask)
            {
                var made = board.Create("Watched", Maybe<Guid>.None, new TaskSession("s1", @"C:\x", Made, Maybe<DateTimeOffset>.None), Made, Guid.NewGuid()).Value;
                board = made.Board.AddPullRequest(made.Task.Id, Pr1, Made).Value;
            }
            // A second task on a pull request of another repository, so a tick has two repositories to look at.
            if (other is not null)
            {
                var made = board.Create("Other", Maybe<Guid>.None, new TaskSession("s2", @"C:\y", Made, Maybe<DateTimeOffset>.None), Made, Guid.NewGuid()).Value;
                board = made.Board.AddPullRequest(made.Task.Id, other, Made).Value;
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

    // Review finding on task c8e6967d: a follow nudge must not reach a task that turned Done meanwhile; its follow ends
    // silently and no further look is made at its runs.
    [Fact]
    public void FollowOfADoneTaskEndsWithoutANudge()
    {
        var merged = false;
        using var poll = new Poll
        {
            Answer = links => merged
                ? Result.Success<IReadOnlyList<PullFacts>>([new PullFacts(Pr1, PullRequestState.Merged, Mergeable.Unknown, "main", "h2", Maybe.From("merge1"))])
                : Open(links, "h1"),
            RunAnswer = Result.Success<IReadOnlyList<RunFacts>>([new RunFacts("Release", "https://x/1", false, "")]),
        };
        poll.Clock.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);
        merged = true;
        poll.Clock.AdvanceBy(RepoWatchPoll.Every.Ticks);
        Assert.Single(poll.Tasks.Watch.Following);
        var nudgesBefore = poll.Nudges.Count;
        var runLooks = poll.Runs.Count;

        Assert.True(poll.Tasks.SetStatus(poll.Tasks.Board.Tasks.Single().Id, TaskStatus.Done).IsSuccess);
        poll.Clock.AdvanceBy(RepoWatchPoll.Every.Ticks);

        Assert.Empty(poll.Tasks.Watch.Following);
        Assert.Equal(nudgesBefore, poll.Nudges.Count);
        Assert.Equal(runLooks, poll.Runs.Count);
    }

    // Review finding on task c8e6967d: a runs look that keeps failing still gives the follow up after three hours.
    [Fact]
    public void FollowWhoseRunLookKeepsFailingGivesUpAfterThreeHours()
    {
        var merged = false;
        using var poll = new Poll
        {
            Answer = links => merged
                ? Result.Success<IReadOnlyList<PullFacts>>([new PullFacts(Pr1, PullRequestState.Merged, Mergeable.Unknown, "main", "h2", Maybe.From("merge1"))])
                : Open(links, "h1"),
        };
        poll.Clock.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);
        merged = true;
        poll.Clock.AdvanceBy(RepoWatchPoll.Every.Ticks);
        Assert.Single(poll.Tasks.Watch.Following);

        poll.Clock.AdvanceBy(TimeSpan.FromHours(3).Ticks + RepoWatchPoll.Every.Ticks);

        Assert.Empty(poll.Tasks.Watch.Following);
        Assert.Contains("after 3 hours", poll.Nudges[^1].Text);
    }

    // Review finding on task c8e6967d: a poll that throws for one repository must not skip the rest of the tick.
    [Fact]
    public void ARepositoryWhoseApplyThrowsDoesNotSkipTheOthers()
    {
        var other = new PullRequestLink("AppifySheets", "Portal", 5);
        var traced = new List<string>();
        using var poll = new Poll(other: other);
        poll.Answer = links => links[0].Key == Pr1.Key
            // A null fact makes RepoWatch.Poll throw while it applies this repository's answer.
            ? Result.Success<IReadOnlyList<PullFacts>>([null!])
            : Open(links, "h1");
        poll.Watcher.Dispose();
        using var watcher = new RepoWatchPoll(poll.Tasks,
            (repository, links) => System.Threading.Tasks.Task.FromResult(poll.Answer(links)),
            (link, sha) => System.Threading.Tasks.Task.FromResult(poll.RunAnswer),
            poll.Clock, Scheduler.Immediate, trace: traced.Add);

        poll.Clock.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);

        Assert.True(poll.Tasks.Watch.LastSuccess.ContainsKey("appifysheets/portal"));
        Assert.Contains(traced, line => line.Contains("appifysheets/deskbar") && line.Contains("threw"));
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
