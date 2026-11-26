using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
using Microsoft.Reactive.Testing;
using Xunit;
// System.Threading.Tasks (an implicit using) has a TaskStatus too; the alias says which one this file means.
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Tasks;

// A Done task's change shows a hollow ring until the running Deskbar holds it (task 8470d176; RunningChanges holds the
// rules, RunningChangeWatch the look, TaskService.DeliveryOf the answer the bar and the panel draw). GitHub is a fake
// here: a dictionary the test fills, so no test runs gh.
public sealed class RunningChangesTests
{
    static readonly DateTimeOffset Made = new(2026, 11, 25, 7, 0, 0, TimeSpan.Zero);

    const string Commit = "0123456789abcdef0123456789abcdef01234567";
    static readonly RunningBuild Build = new("AppifySheets", "Deskbar", "2.6.23", Commit);

    static readonly PullRequestLink Pr573 = new("AppifySheets", "Deskbar", 573);
    static readonly PullRequestLink Pr574 = new("AppifySheets", "Deskbar", 574);
    static readonly PullRequestLink Elsewhere = new("AppifySheets", "iam.ge", 537);

    static readonly TaskSession Session = new("s1", @"C:\repos\TaskSpaces", Made.AddHours(-1), Maybe<DateTimeOffset>.None);

    static readonly PullRequestArrival Running = new(PullRequestState.Merged, true);
    static readonly PullRequestArrival MergedNotHere = new(PullRequestState.Merged, false);
    static readonly PullRequestArrival Open = new(PullRequestState.Open, false);
    static readonly PullRequestArrival Closed = new(PullRequestState.Closed, false);

    // A board with one task, given the status and recorded pull requests.
    static (TaskBoard Board, Guid Id) Board(TaskStatus status, params PullRequestLink[] links)
    {
        var made = TaskBoard.Empty.Create("Subtask tick in thread", Maybe<Guid>.None, Session, Made, Guid.NewGuid()).Value;
        var board = links.Aggregate(made.Board, (b, l) => b.AddPullRequest(made.Task.Id, l, Made).Value);
        return (board.SetStatus(made.Task.Id, status, Made).Value, made.Task.Id);
    }

    static TaskItem Task(TaskStatus status, params PullRequestLink[] links)
    {
        var (board, id) = Board(status, links);
        return board.Tasks.Single(t => t.Id == id);
    }

    static Dictionary<string, PullRequestArrival> Facts(params (PullRequestLink Link, PullRequestArrival Arrival)[] facts) =>
        facts.ToDictionary(f => f.Link.Key, f => f.Arrival);

    // --- the rule ------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_merged_change_not_in_this_build_is_shipping()
    {
        var delivery = RunningChanges.Of(Task(TaskStatus.Done, Pr573), Build, Facts((Pr573, MergedNotHere)));

        Assert.Equal(DeliveryStage.Merged, delivery.Stage);
        Assert.True(delivery.Shipping);
        Assert.Equal("Merged in Deskbar #573, not in this Deskbar (2.6.23) yet; it ships with the next release.", delivery.Note);
    }

    [Fact]
    public void A_change_this_build_holds_is_running()
    {
        var delivery = RunningChanges.Of(Task(TaskStatus.Done, Pr573), Build, Facts((Pr573, Running)));

        Assert.Equal(DeliveryStage.Running, delivery.Stage);
        Assert.False(delivery.Shipping);
        Assert.Equal("Running in this Deskbar (2.6.23).", delivery.Note);
    }

    [Fact]
    public void Every_pull_request_must_run_before_the_ring_fills()
    {
        var delivery = RunningChanges.Of(Task(TaskStatus.Done, Pr573, Pr574), Build, Facts((Pr573, Running), (Pr574, MergedNotHere)));

        Assert.Equal(DeliveryStage.Merged, delivery.Stage);
        Assert.DoesNotContain("#573", delivery.Note);
        Assert.Contains("#574", delivery.Note);
    }

    [Fact]
    public void An_open_pull_request_says_open_rather_than_merged()
    {
        var delivery = RunningChanges.Of(Task(TaskStatus.Done, Pr573, Pr574), Build, Facts((Pr573, MergedNotHere), (Pr574, Open)));

        Assert.Equal(DeliveryStage.Open, delivery.Stage);
        Assert.True(delivery.Shipping);
        Assert.Contains("Deskbar #574 is not merged yet.", delivery.Note);
    }

    [Fact]
    public void A_pull_request_closed_without_a_merge_is_left_out()
    {
        Assert.Equal(DeliveryStage.Running,
            RunningChanges.Of(Task(TaskStatus.Done, Pr573, Pr574), Build, Facts((Pr573, Running), (Pr574, Closed))).Stage);
        Assert.Equal(TaskDelivery.None, RunningChanges.Of(Task(TaskStatus.Done, Pr574), Build, Facts((Pr574, Closed))));
    }

    [Fact]
    public void Unknown_keeps_the_full_tick()
    {
        // One answer missing (a failed look, or not asked yet) is a claim Deskbar cannot make.
        Assert.Equal(TaskDelivery.None, RunningChanges.Of(Task(TaskStatus.Done, Pr573, Pr574), Build, Facts((Pr573, MergedNotHere))));
    }

    [Fact]
    public void Only_done_tasks_with_a_pull_request_of_this_repository_count()
    {
        var facts = Facts((Pr573, MergedNotHere), (Elsewhere, MergedNotHere));

        Assert.Equal(TaskDelivery.None, RunningChanges.Of(Task(TaskStatus.InProgress, Pr573), Build, facts));
        Assert.Equal(TaskDelivery.None, RunningChanges.Of(Task(TaskStatus.Done), Build, facts));
        Assert.Equal(TaskDelivery.None, RunningChanges.Of(Task(TaskStatus.Done, Elsewhere), Build, facts));
    }

    [Fact]
    public void The_repository_compares_without_case()
    {
        var shouted = new PullRequestLink("appifysheets", "DESKBAR", 573);

        Assert.True(Build.Builds(shouted));
        Assert.Equal(DeliveryStage.Merged, RunningChanges.Of(Task(TaskStatus.Done, shouted), Build, Facts((Pr573, MergedNotHere))).Stage);
    }

    // --- what is looked at ---------------------------------------------------------------------------------------------

    [Fact]
    public void Only_open_or_unknown_pull_requests_of_done_tasks_are_looked_at()
    {
        var (board, _) = Board(TaskStatus.Done, Pr573, Pr574, Elsewhere);
        // A second task, in progress, whose pull request must not be looked at.
        var other = board.Create("Still going", Maybe<Guid>.None, Session, Made, Guid.NewGuid()).Value;
        var both = other.Board.AddPullRequest(other.Task.Id, new PullRequestLink("AppifySheets", "Deskbar", 600), Made).Value;

        Assert.Equal([Pr573.Key, Pr574.Key], RunningChanges.ToLook(both, Build, Facts()).Select(l => l.Key));
        Assert.Equal([Pr574.Key], RunningChanges.ToLook(both, Build, Facts((Pr573, MergedNotHere), (Pr574, Open))).Select(l => l.Key));
        Assert.Empty(RunningChanges.ToLook(both, Build, Facts((Pr573, Running), (Pr574, Closed))));
    }

    // --- reading gh and the version ------------------------------------------------------------------------------------

    [Fact]
    public void Gh_pr_view_with_the_merge_commit_is_read()
    {
        // As measured on 2026-11-25 for #570.
        var merged = RunningChanges.ParseMerge("""{"closedAt":"2026-11-25T17:53:11Z","mergeCommit":{"oid":"64599cabe05eb19aa6d6b026311003faafbcc9b8"},"state":"MERGED"}""");
        var open = RunningChanges.ParseMerge("""{"closedAt":null,"mergeCommit":null,"state":"OPEN"}""");

        Assert.Equal((PullRequestState.Merged, Maybe.From("64599cabe05eb19aa6d6b026311003faafbcc9b8")), merged.Value);
        Assert.Equal(PullRequestState.Open, open.Value.State);
        Assert.True(open.Value.MergeCommit.HasNoValue);
    }

    [Fact]
    public void A_merged_pull_request_without_its_merge_commit_is_a_failure()
    {
        Assert.True(RunningChanges.ParseMerge("""{"closedAt":"2026-11-25T17:53:11Z","mergeCommit":null,"state":"MERGED"}""").IsFailure);
        Assert.True(RunningChanges.ParseMerge("not json").IsFailure);
    }

    [Theory]
    [InlineData("ahead", true)]
    [InlineData("identical", true)]
    [InlineData("behind", false)]
    [InlineData("diverged", false)]
    public void The_compare_status_says_whether_the_build_holds_the_merge(string status, bool holds) =>
        Assert.Equal(holds, RunningChanges.Contains(status).Value);

    [Fact]
    public void An_unknown_compare_status_is_a_failure() =>
        Assert.True(RunningChanges.Contains("""{"message":"Not Found"}""").IsFailure);

    [Fact]
    public void The_running_build_comes_from_the_informational_version()
    {
        var build = RunningBuild.Of("AppifySheets", "Deskbar", "2.6.23+0123456789ABCDEF0123456789abcdef01234567").Value;

        Assert.Equal("2.6.23", build.Version);
        Assert.Equal(Commit, build.Commit);
        Assert.True(RunningBuild.Of("AppifySheets", "Deskbar", "2.6.23").HasNoValue);
        Assert.True(RunningBuild.Of("AppifySheets", "Deskbar", "2.6.23+0123456").HasNoValue);
    }

    // --- the watch and TaskService --------------------------------------------------------------------------------------

    sealed class MemoryStore : ITaskStore
    {
        public TaskBoard Stored { get; set; } = TaskBoard.Empty;

        public Result<TaskBoard> Load() => Stored;

        public Result Save(TaskBoard board) => Result.Success();
    }

    sealed class Watch : IDisposable
    {
        public TestScheduler Clock { get; } = new();
        public Dictionary<string, Result<PullRequestArrival>> GitHub { get; } = [];
        public List<string> Looked { get; } = [];
        public TaskService Tasks { get; }
        public RunningChangeWatch Watcher { get; }
        public int Pulses { get; private set; }

        public Watch(TaskBoard board)
        {
            Clock.AdvanceTo(Made.AddMinutes(1).UtcTicks);
            Tasks = new TaskService(new MemoryStore { Stored = board }, () => Clock.Now);
            Tasks.Load();
            Tasks.Changed.Subscribe(_ => Pulses++);
            Watcher = new RunningChangeWatch(Tasks, Build, (link, commit) =>
            {
                Assert.Equal(Commit, commit);
                Looked.Add(link.Key);
                return System.Threading.Tasks.Task.FromResult(GitHub.TryGetValue(link.Key, out var answer) ? answer : Result.Failure<PullRequestArrival>("unknown"));
            }, Clock, System.Reactive.Concurrency.Scheduler.Immediate);
        }

        public TaskDelivery Delivery => Tasks.DeliveryOf(Tasks.Board.Tasks.Single());

        public void Dispose() => Watcher.Dispose();
    }

    [Fact]
    public void The_watch_rings_a_merged_change_and_asks_once()
    {
        var (board, _) = Board(TaskStatus.Done, Pr573);
        using var watch = new Watch(board);
        watch.GitHub[Pr573.Key] = MergedNotHere;

        watch.Clock.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);
        Assert.Equal(DeliveryStage.Merged, watch.Delivery.Stage);

        // Settled for this run: the running build cannot change without a restart.
        watch.Clock.AdvanceBy(RunningChangeWatch.Poll.Ticks * 5);
        Assert.Equal([Pr573.Key], watch.Looked);
    }

    [Fact]
    public void An_open_pull_request_is_asked_again_until_it_merges()
    {
        var (board, _) = Board(TaskStatus.Done, Pr573);
        using var watch = new Watch(board);
        watch.GitHub[Pr573.Key] = Open;
        watch.Clock.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);
        Assert.Equal(DeliveryStage.Open, watch.Delivery.Stage);

        watch.GitHub[Pr573.Key] = Running;
        watch.Clock.AdvanceBy(RunningChangeWatch.Poll.Ticks);

        Assert.Equal(DeliveryStage.Running, watch.Delivery.Stage);
        Assert.Equal(2, watch.Looked.Count);
    }

    [Fact]
    public void A_failed_look_keeps_the_full_tick_and_is_tried_again()
    {
        var (board, _) = Board(TaskStatus.Done, Pr573);
        using var watch = new Watch(board);

        watch.Clock.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);
        Assert.Equal(TaskDelivery.None, watch.Delivery);

        watch.GitHub[Pr573.Key] = MergedNotHere;
        watch.Clock.AdvanceBy(RunningChangeWatch.Poll.Ticks);
        Assert.Equal(DeliveryStage.Merged, watch.Delivery.Stage);
    }

    [Fact]
    public void With_no_done_task_to_check_the_watch_asks_GitHub_nothing()
    {
        var (board, _) = Board(TaskStatus.InProgress, Pr573);
        using var watch = new Watch(board);

        watch.Clock.AdvanceBy(TimeSpan.FromMinutes(5).Ticks);

        Assert.Empty(watch.Looked);
    }

    [Fact]
    public void The_views_are_pulsed_only_when_an_answer_changes()
    {
        var (board, _) = Board(TaskStatus.Done, Pr573);
        using var watch = new Watch(board);
        watch.GitHub[Pr573.Key] = Open;
        watch.Clock.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);
        var afterFirst = watch.Pulses;

        // The same open answer again: nothing to redraw.
        watch.Clock.AdvanceBy(RunningChangeWatch.Poll.Ticks);
        Assert.Equal(afterFirst, watch.Pulses);

        watch.GitHub[Pr573.Key] = Running;
        watch.Clock.AdvanceBy(RunningChangeWatch.Poll.Ticks);
        Assert.Equal(afterFirst + 1, watch.Pulses);
    }
}
