using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
using Xunit;
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Tasks;

// The poll rules of the repository watch (Deskbar task c8e6967d): pure functions over a board, the memory and what gh
// said, so no test runs gh or a clock.
public sealed class RepoWatchTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 27, 10, 0, 0, TimeSpan.Zero);
    const string Repository = "appifysheets/deskbar";

    static readonly PullRequestLink Pr1 = new("AppifySheets", "Deskbar", 1);
    static readonly PullRequestLink Pr2 = new("AppifySheets", "Deskbar", 2);
    static readonly TaskSession Session = new("s1", @"C:\repos\TaskSpaces", Now.AddHours(-1), Maybe<DateTimeOffset>.None);

    // A board of tasks, one per pull request given, each with a session and (as Create makes it) In progress.
    static (TaskBoard Board, IReadOnlyList<Guid> Ids) Board(params PullRequestLink[] links)
    {
        var board = TaskBoard.Empty;
        var ids = new List<Guid>();
        foreach (var link in links)
        {
            var made = board.Create($"Task for {link.Number}", Maybe<Guid>.None, new TaskSession($"s{link.Number}", @"C:\x", Now, Maybe<DateTimeOffset>.None), Now, Guid.NewGuid()).Value;
            board = made.Board.AddPullRequest(made.Task.Id, link, Now).Value;
            ids.Add(made.Task.Id);
        }
        return (board, ids);
    }

    static PullFacts Open(PullRequestLink link, string head, Mergeable mergeable = Mergeable.Mergeable) =>
        new(link, PullRequestState.Open, mergeable, "main", head, Maybe<string>.None);

    static PullFacts Merged(PullRequestLink link) =>
        new(link, PullRequestState.Merged, Mergeable.Unknown, "main", "h2", Maybe.From("merge1"));

    static RepoWatchStep Poll(RepoWatchState state, TaskBoard board, params PullFacts[] facts) =>
        RepoWatch.Poll(state, board, Repository, facts, Now);

    [Fact]
    public void FirstPollRecordsHeadsWithoutNudging()
    {
        var (board, _) = Board(Pr1);

        var step = Poll(RepoWatchState.Empty, board, Open(Pr1, "h1"));

        Assert.Empty(step.Nudges);
        Assert.Equal("h1", step.State.Heads["appifysheets/deskbar:main"]);
    }

    [Fact]
    public void BaseMoveNudgesEachOpenPullRequestOnThatBase()
    {
        var (board, ids) = Board(Pr1, Pr2);
        var first = Poll(RepoWatchState.Empty, board, Open(Pr1, "h1"), Open(Pr2, "h1")).State;

        var step = Poll(first, board, Open(Pr1, "h2"), Open(Pr2, "h2"));

        Assert.Equal(ids.Order(), step.Nudges.Select(n => n.TaskId).Order());
        Assert.All(step.Nudges, n => Assert.Contains("moved to h2", n.Text));
        // The same head again is not a move.
        Assert.Empty(Poll(step.State, board, Open(Pr1, "h2"), Open(Pr2, "h2")).Nudges);
    }

    [Fact]
    public void SnoozedTaskIsNotNudgedForABaseMove()
    {
        var (board, ids) = Board(Pr1);
        board = board.Snooze(ids[0], Now.AddDays(1), Now).Value;
        var first = Poll(RepoWatchState.Empty, board, Open(Pr1, "h1")).State;

        Assert.Empty(Poll(first, board, Open(Pr1, "h2")).Nudges);
    }

    [Fact]
    public void SnoozedTaskIsNudgedForAConflict()
    {
        var (board, ids) = Board(Pr1);
        board = board.Snooze(ids[0], Now.AddDays(1), Now).Value;

        var step = Poll(RepoWatchState.Empty, board, Open(Pr1, "h1", Mergeable.Conflicting));

        Assert.Equal(ids[0], step.Nudges.Single().TaskId);
        Assert.Contains("conflicts with main", step.Nudges.Single().Text);
    }

    [Fact]
    public void ConflictNudgesOncePerBaseHead()
    {
        var (board, _) = Board(Pr1);
        var first = Poll(RepoWatchState.Empty, board, Open(Pr1, "h1", Mergeable.Conflicting));
        Assert.Single(first.Nudges);

        Assert.Empty(Poll(first.State, board, Open(Pr1, "h1", Mergeable.Conflicting)).Nudges);
        // The base moved and it still conflicts: a new head, a new nudge (one text, the move and the conflict joined).
        var moved = Poll(first.State, board, Open(Pr1, "h2", Mergeable.Conflicting));
        Assert.Single(moved.Nudges);
        Assert.Contains("conflicts with main", moved.Nudges[0].Text);
    }

    // Review focus 5: GitHub still computing the flag is neither a conflict nor a clear.
    [Fact]
    public void UnknownMergeableNudgesNothing()
    {
        var (board, _) = Board(Pr1);
        var conflicted = Poll(RepoWatchState.Empty, board, Open(Pr1, "h1", Mergeable.Conflicting)).State;

        var step = Poll(conflicted, board, Open(Pr1, "h1", Mergeable.Unknown));

        Assert.Empty(step.Nudges);
        Assert.Equal(conflicted.ConflictNudged, step.State.ConflictNudged);
    }

    [Fact]
    public void OpenThenMergedIsFollowedAndNudged()
    {
        var (board, ids) = Board(Pr1);
        var seen = Poll(RepoWatchState.Empty, board, Open(Pr1, "h1")).State;

        var step = Poll(seen, board, Merged(Pr1));

        var followed = Assert.Single(step.State.Following);
        Assert.Equal(new FollowedMerge(ids[0], Pr1, "merge1", Now), followed);
        Assert.Contains("merged", step.Nudges.Single().Text);
    }

    [Fact]
    public void FirstSeenMergedIsNotFollowed()
    {
        var (board, _) = Board(Pr1);

        var step = Poll(RepoWatchState.Empty, board, Merged(Pr1));

        Assert.Empty(step.State.Following);
        Assert.Empty(step.Nudges);
    }

    [Fact]
    public void DoneTaskIsNotInFlight()
    {
        var (board, ids) = Board(Pr1);
        board = board.SetStatus(ids[0], TaskStatus.Done, Now).Value;

        Assert.Empty(RepoWatch.InFlight(board, RepoWatchState.Empty));
    }

    [Fact]
    public void TaskWithoutSessionIsNotInFlight()
    {
        var (board, _) = Board(Pr1);
        var bare = new TaskBoard(board.Tasks.Select(t => t with { Sessions = [] }).ToArray());

        Assert.Empty(RepoWatch.InFlight(bare, RepoWatchState.Empty));
    }

    [Fact]
    public void ClosedUnmergedLeavesFlight()
    {
        var (board, _) = Board(Pr1);
        Assert.Single(RepoWatch.InFlight(board, RepoWatchState.Empty)[Repository]);
        var seenClosed = RepoWatchState.Empty with { Seen = new Dictionary<string, PullRequestState> { [Pr1.Key] = PullRequestState.Closed } };

        Assert.Empty(RepoWatch.InFlight(board, seenClosed));
    }

    [Fact]
    public void WatchedOnlyWithinFiveMinutesOfSuccess()
    {
        var (board, _) = Board(Pr1);
        var task = board.Tasks.Single();
        var state = RepoWatchState.Empty with
        {
            LastSuccess = new Dictionary<string, DateTimeOffset> { [Repository] = Now },
            Seen = new Dictionary<string, PullRequestState> { [Pr1.Key] = PullRequestState.Open },
        };

        Assert.True(RepoWatch.Watched(state, task, Now.AddMinutes(5)));
        Assert.False(RepoWatch.Watched(state, task, Now.AddMinutes(5).AddSeconds(1)));
        Assert.False(RepoWatch.Watched(RepoWatchState.Empty, task, Now));
    }

    // Review finding on task c8e6967d: a link Seen Merged that nothing follows has no watcher, so the prompt must not say so.
    [Fact]
    public void MergedAndNotFollowedIsNotWatched()
    {
        var (board, ids) = Board(Pr1);
        var task = board.Tasks.Single();
        var merged = RepoWatchState.Empty with
        {
            LastSuccess = new Dictionary<string, DateTimeOffset> { [Repository] = Now },
            Seen = new Dictionary<string, PullRequestState> { [Pr1.Key] = PullRequestState.Merged },
        };

        Assert.False(RepoWatch.Watched(merged, task, Now));
        Assert.True(RepoWatch.Watched(merged with { Following = [new FollowedMerge(ids[0], Pr1, "merge1", Now)] }, task, Now));
    }

    // Review finding on task c8e6967d: a link seen for the first time only records the head, whatever an older head says.
    [Fact]
    public void LinkSeenFirstTimeIsNotNudgedForABaseMove()
    {
        var (board, _) = Board(Pr1, Pr2);
        var first = Poll(RepoWatchState.Empty, board, Open(Pr1, "h1")).State;

        var step = Poll(first, board, Open(Pr1, "h1"), Open(Pr2, "h2"));

        Assert.Empty(step.Nudges);
        Assert.Equal("h2", step.State.Heads["appifysheets/deskbar:main"]);
    }

    // Review finding on task c8e6967d: Heads follows merged pull requests too, so it does not go stale while none is open.
    [Fact]
    public void MergedFactUpdatesTheBaseHead()
    {
        var (board, _) = Board(Pr1);
        var seen = Poll(RepoWatchState.Empty, board, Open(Pr1, "h1")).State;

        var step = Poll(seen, board, Merged(Pr1));

        Assert.Equal("h2", step.State.Heads["appifysheets/deskbar:main"]);
    }

    // Review finding on task c8e6967d: the rebase a base-move nudge asks for covers the conflict at that head.
    [Fact]
    public void ConflictAtTheHeadOfABaseMoveNudgeIsNotToldAgain()
    {
        var (board, _) = Board(Pr1);
        var first = Poll(RepoWatchState.Empty, board, Open(Pr1, "h1")).State;
        var moved = Poll(first, board, Open(Pr1, "h2", Mergeable.Unknown));
        Assert.Contains("moved to h2", Assert.Single(moved.Nudges).Text);

        Assert.Empty(Poll(moved.State, board, Open(Pr1, "h2", Mergeable.Conflicting)).Nudges);
    }
}
