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

    // Another repository's merged pull request, as its look answers it: the state alone, never Running.
    static readonly PullRequestArrival MergedElsewhere = new(PullRequestState.Merged, false);

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

    // A Done task with these pull requests, all reported deployed with the note.
    static TaskItem Reported(string note, params PullRequestLink[] links)
    {
        var (board, id) = Board(TaskStatus.Done, links);
        return board.MarkDeployed(id, note, Made.AddHours(1)).Value.Tasks.Single(t => t.Id == id);
    }

    const string Portal = "2026.11.25.39-3749d21 in the portal logs";

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
    public void Not_asked_yet_is_checking_never_the_full_tick()
    {
        // Task 8db4c78e: one answer still to come draws the ring, not the disc that says the change runs, even when the
        // answers already in say it runs.
        Assert.Equal(new TaskDelivery(DeliveryStage.Checking, RunningChanges.CheckingNote),
            RunningChanges.Of(Task(TaskStatus.Done, Pr573, Pr574), Build, Facts((Pr573, Running))));
        Assert.True(RunningChanges.Of(Task(TaskStatus.Done, Pr573), Build, Facts()).Shipping);
    }

    [Fact]
    public void A_failed_look_keeps_the_full_tick()
    {
        // A pull request whose look failed is a claim Deskbar cannot make, either way: the full disc, as before.
        var unreachable = new HashSet<string> { Pr574.Key };
        Assert.Equal(TaskDelivery.None, RunningChanges.Of(Task(TaskStatus.Done, Pr573, Pr574), Build, Facts((Pr573, MergedNotHere)), unreachable));
    }

    [Fact]
    public void Only_done_tasks_with_a_recorded_pull_request_count()
    {
        var facts = Facts((Pr573, MergedNotHere), (Elsewhere, MergedElsewhere));

        Assert.Equal(TaskDelivery.None, RunningChanges.Of(Task(TaskStatus.InProgress, Pr573), Build, facts));
        Assert.Equal(TaskDelivery.None, RunningChanges.Of(Task(TaskStatus.InProgress, Elsewhere), Build, facts));
        Assert.Equal(TaskDelivery.None, RunningChanges.Of(Task(TaskStatus.Done), Build, facts));
    }

    // --- another repository: a reported deploy (phase 2) ----------------------------------------------------------------

    [Fact]
    public void A_change_merged_elsewhere_waits_for_its_deploy_report()
    {
        var delivery = RunningChanges.Of(Task(TaskStatus.Done, Elsewhere), Build, Facts((Elsewhere, MergedElsewhere)));

        Assert.Equal(DeliveryStage.Merged, delivery.Stage);
        Assert.True(delivery.Shipping);
        Assert.Equal("Merged in iam.ge #537, not reported deployed yet.", delivery.Note);
    }

    [Fact]
    public void A_change_open_elsewhere_says_it_is_not_merged()
    {
        var delivery = RunningChanges.Of(Task(TaskStatus.Done, Elsewhere), Build, Facts((Elsewhere, Open)));

        Assert.Equal(DeliveryStage.Open, delivery.Stage);
        Assert.Equal("iam.ge #537 is not merged yet.", delivery.Note);
    }

    [Fact]
    public void A_reported_deploy_runs_with_its_evidence()
    {
        var delivery = RunningChanges.Of(Reported(Portal, Elsewhere), Build, Facts((Elsewhere, MergedElsewhere)));

        Assert.Equal(DeliveryStage.Running, delivery.Stage);
        Assert.False(delivery.Shipping);
        Assert.Equal($"Deployed: {Portal}", delivery.Note);
    }

    [Fact]
    public void A_reported_deploy_runs_without_any_word_from_GitHub()
    {
        // gh missing or signed out: the report was measured where the app runs, so it is enough.
        Assert.Equal(DeliveryStage.Running, RunningChanges.Of(Reported(Portal, Elsewhere), Build, Facts()).Stage);
    }

    [Fact]
    public void Unknown_and_unreported_elsewhere_is_checking_until_its_look_fails()
    {
        Assert.Equal(DeliveryStage.Checking, RunningChanges.Of(Task(TaskStatus.Done, Elsewhere), Build, Facts()).Stage);
        Assert.Equal(TaskDelivery.None, RunningChanges.Of(Task(TaskStatus.Done, Elsewhere), Build, Facts(), new HashSet<string> { Elsewhere.Key }));
    }

    [Fact]
    public void A_task_in_both_repositories_needs_both()
    {
        var task = Reported(Portal, Pr573, Elsewhere);

        var waiting = RunningChanges.Of(task, Build, Facts((Pr573, MergedNotHere)));
        Assert.Equal(DeliveryStage.Merged, waiting.Stage);
        Assert.Equal("Merged in Deskbar #573, not in this Deskbar (2.6.23) yet; it ships with the next release.", waiting.Note);

        var running = RunningChanges.Of(task, Build, Facts((Pr573, Running)));
        Assert.Equal(DeliveryStage.Running, running.Stage);
        Assert.Equal($"Deployed: {Portal} Running in this Deskbar (2.6.23).", running.Note);
    }

    [Fact]
    public void A_report_never_overrides_the_running_build()
    {
        // A Deskbar pull request reported deployed still waits for this copy to hold it.
        var delivery = RunningChanges.Of(Reported(Portal, Pr573), Build, Facts((Pr573, MergedNotHere)));

        Assert.Equal(DeliveryStage.Merged, delivery.Stage);
    }

    [Fact]
    public void A_pull_request_recorded_after_the_report_holds_the_ring_and_the_latest_note_is_shown()
    {
        var second = new PullRequestLink("AppifySheets", "iam.ge", 540);
        var (board, id) = Board(TaskStatus.Done, Elsewhere);
        var first = board.MarkDeployed(id, Portal, Made.AddHours(1)).Value.AddPullRequest(id, second, Made.AddHours(2)).Value;
        var facts = Facts((Elsewhere, MergedElsewhere), (second, MergedElsewhere));

        var waiting = RunningChanges.Of(first.Tasks.Single(), Build, facts);
        Assert.Equal(DeliveryStage.Merged, waiting.Stage);
        Assert.Equal("Merged in iam.ge #540, not reported deployed yet.", waiting.Note);

        var both = first.MarkDeployed(id, "2026.11.26.4-aa11bb2 on the health page", Made.AddHours(3)).Value;
        Assert.Equal("Deployed: 2026.11.26.4-aa11bb2 on the health page", RunningChanges.Of(both.Tasks.Single(), Build, facts).Note);
    }

    // --- recording a deploy (TaskBoard.MarkDeployed) -------------------------------------------------------------------

    [Fact]
    public void A_deploy_needs_a_note()
    {
        var (board, id) = Board(TaskStatus.Done, Elsewhere);

        Assert.True(board.MarkDeployed(id, "  ", Made).IsFailure);
    }

    [Fact]
    public void A_deploy_needs_a_recorded_pull_request()
    {
        var (board, id) = Board(TaskStatus.Done);

        var refused = board.MarkDeployed(id, Portal, Made);

        Assert.True(refused.IsFailure);
        Assert.Equal(TaskBoard.NoPullRequestToDeploy, refused.Error);
    }

    [Fact]
    public void A_deploy_records_every_pull_request_once()
    {
        var (board, id) = Board(TaskStatus.Done, Pr573, Elsewhere);
        var at = Made.AddHours(4);

        var once = board.MarkDeployed(id, $"  {Portal}\n", at).Value;
        var task = once.Tasks.Single();

        Assert.Equal([new DeployedPullRequest(Pr573.Key, at, Portal), new DeployedPullRequest(Elsewhere.Key, at, Portal)], task.Deployed);
        Assert.Equal(at, task.UpdatedAt);
        Assert.Equal(TaskStatus.Done, task.Status);
        // Reported again: nothing to record, the same board, so nothing is saved.
        Assert.Same(once, once.MarkDeployed(id, "again", at.AddHours(1)).Value);
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

        Assert.Equal([Pr573.Key, Pr574.Key, Elsewhere.Key], RunningChanges.ToLook(both, Build, Facts()).Select(l => l.Key));
        Assert.Equal([Pr574.Key, Elsewhere.Key], RunningChanges.ToLook(both, Build, Facts((Pr573, MergedNotHere), (Pr574, Open))).Select(l => l.Key));
        Assert.Empty(RunningChanges.ToLook(both, Build, Facts((Pr573, Running), (Pr574, Closed), (Elsewhere, MergedElsewhere))));
    }

    [Fact]
    public void A_pull_request_elsewhere_reported_deployed_is_still_asked_until_it_settles()
    {
        var (board, id) = Board(TaskStatus.Done, Pr573, Elsewhere);
        var reported = board.MarkDeployed(id, Portal, Made).Value;

        // A report does not fill an open pull request (review of phase 2), so its state is still wanted until it settles.
        Assert.Equal([Pr573.Key, Elsewhere.Key], RunningChanges.ToLook(reported, Build, Facts()).Select(l => l.Key));
        Assert.Equal([Pr573.Key], RunningChanges.ToLook(reported, Build, Facts((Elsewhere, MergedElsewhere))).Select(l => l.Key));
    }

    [Fact]
    public void A_report_does_not_fill_a_pull_request_that_is_still_open()
    {
        // The session checked the backend (537) and reported; the frontend (540) is still open, stamped by the same report.
        var frontend = new PullRequestLink("AppifySheets", "iam.ge", 540);
        var task = Reported(Portal, Elsewhere, frontend);

        var delivery = RunningChanges.Of(task, Build, Facts((Elsewhere, MergedElsewhere), (frontend, Open)));

        Assert.Equal(DeliveryStage.Open, delivery.Stage);
        Assert.Contains("iam.ge #540 is not merged yet.", delivery.Note);
        Assert.Equal(DeliveryStage.Running, RunningChanges.Of(task, Build, Facts((Elsewhere, MergedElsewhere), (frontend, MergedElsewhere))).Stage);
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
        // What a real build carries: the commit cut to ten digits (measured on the installed 2.6.28).
        Assert.Equal("5051c48433", RunningBuild.Of("AppifySheets", "Deskbar", "2.6.28+5051c48433").Value.Commit);
        Assert.True(RunningBuild.Of("AppifySheets", "Deskbar", "2.6.23").HasNoValue);
        Assert.True(RunningBuild.Of("AppifySheets", "Deskbar", "2.6.23+012345").HasNoValue);
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
        // The running commit each look was given, by pull request: this build's commit, or none for another repository.
        public Dictionary<string, Maybe<string>> Commits { get; } = [];
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
                Commits[link.Key] = commit;
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
        Assert.Equal(Maybe.From(Commit), watch.Commits[Pr573.Key]);

        // Settled for this run: the running build cannot change without a restart.
        watch.Clock.AdvanceBy(RunningChangeWatch.Poll.Ticks * 5);
        Assert.Equal([Pr573.Key], watch.Looked);
    }

    // Task 8db4c78e, the case Petre caught: a task turns Done (its pull request just merged) between two looks. Until the
    // next look answers it shows the Checking ring, never the full disc; then the ring the answer says.
    [Fact]
    public void A_task_turning_done_between_looks_is_checking_until_the_next_look()
    {
        var (board, id) = Board(TaskStatus.InProgress, Pr573);
        using var watch = new Watch(board);
        watch.GitHub[Pr573.Key] = MergedNotHere;
        watch.Clock.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);
        Assert.Empty(watch.Looked);

        Assert.True(watch.Tasks.MarkDone(id).IsSuccess);
        Assert.Equal(DeliveryStage.Checking, watch.Delivery.Stage);
        Assert.True(watch.Delivery.Shipping);

        watch.Clock.AdvanceBy(RunningChangeWatch.Poll.Ticks);
        Assert.Equal(DeliveryStage.Merged, watch.Delivery.Stage);
    }

    // A machine that cannot ask GitHub (every look of the last round failed: no gh, signed out, offline) keeps a task that
    // turns Done on the full disc, as before task 8db4c78e, rather than a ring for the minute until its own look fails.
    [Fact]
    public void Where_GitHub_cannot_be_asked_a_task_turning_done_keeps_the_full_tick()
    {
        var (first, _) = Board(TaskStatus.Done, Pr573);
        var made = first.Create("Second change", Maybe<Guid>.None, Session, Made, Guid.NewGuid()).Value;
        var board = made.Board.AddPullRequest(made.Task.Id, Pr574, Made).Value;
        using var watch = new Watch(board);

        // The fake GitHub answers nothing: the only look fails.
        watch.Clock.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);
        Assert.True(watch.Tasks.MarkDone(made.Task.Id).IsSuccess);

        Assert.Equal(TaskDelivery.None, watch.Tasks.DeliveryOf(watch.Tasks.Board.Tasks.Single(t => t.Id == made.Task.Id)));
    }

    // At a start the views learn the build before the first answer, so a Done task waits in Checking, not on the full
    // disc; and each answer lands on its own, so one task does not wait for every other pull request to be asked.
    [Fact]
    public void Each_answer_lands_as_it_comes()
    {
        var (board, _) = Board(TaskStatus.Done, Pr573, Pr574);
        using var watch = new Watch(board);
        var seen = new List<DeliveryStage>();
        watch.Tasks.Changed.Subscribe(_ => seen.Add(watch.Delivery.Stage));
        watch.GitHub[Pr573.Key] = Running;
        watch.GitHub[Pr574.Key] = Running;

        watch.Clock.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);

        // Checking at once, still Checking with one answer in, Running with both.
        Assert.Equal([DeliveryStage.Checking, DeliveryStage.Checking, DeliveryStage.Running], seen);
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
    public void A_failed_look_keeps_the_full_tick_and_is_tried_again_after_the_backoff()
    {
        var (board, _) = Board(TaskStatus.Done, Pr573);
        using var watch = new Watch(board);

        watch.Clock.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);
        Assert.Equal(TaskDelivery.None, watch.Delivery);

        // Not every minute: a commit GitHub has never seen fails every look, for good.
        watch.GitHub[Pr573.Key] = MergedNotHere;
        watch.Clock.AdvanceBy(RunningChangeWatch.Poll.Ticks * 3);
        Assert.Single(watch.Looked);
        Assert.Equal(TaskDelivery.None, watch.Delivery);

        watch.Clock.AdvanceBy(RunningChangeWatch.FailureBackoff.Ticks);
        Assert.Equal(2, watch.Looked.Count);
        Assert.Equal(DeliveryStage.Merged, watch.Delivery.Stage);
    }

    [Fact]
    public void Another_repository_is_asked_without_a_commit_and_marked_by_hand()
    {
        var (board, id) = Board(TaskStatus.Done, Elsewhere);
        using var watch = new Watch(board);
        watch.GitHub[Elsewhere.Key] = MergedElsewhere;

        watch.Clock.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);

        Assert.True(watch.Commits[Elsewhere.Key].HasNoValue);
        Assert.Equal(DeliveryStage.Merged, watch.Delivery.Stage);
        Assert.True(watch.Tasks.CanMarkDeployed(watch.Tasks.Board.Tasks.Single()));

        // "Mark deployed": the ring fills at once (the board's own pulse redraws), and the item goes.
        Assert.True(watch.Tasks.MarkDeployed(id, TaskService.MarkedByHand).IsSuccess);
        Assert.Equal(new TaskDelivery(DeliveryStage.Running, "Deployed: Marked deployed by hand"), watch.Delivery);
        Assert.False(watch.Tasks.CanMarkDeployed(watch.Tasks.Board.Tasks.Single()));
        watch.Clock.AdvanceBy(RunningChangeWatch.Poll.Ticks * 3);
        Assert.Single(watch.Looked);
    }

    [Fact]
    public void A_reported_task_shows_its_note_even_when_GitHub_cannot_be_asked()
    {
        var (board, id) = Board(TaskStatus.Done, Elsewhere);
        using var watch = new Watch(board.MarkDeployed(id, Portal, Made).Value);

        // The fake GitHub has no answer for it: the look fails, and an unknown state trusts the report.
        watch.Clock.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);

        Assert.Equal([Elsewhere.Key], watch.Looked);
        Assert.Equal(new TaskDelivery(DeliveryStage.Running, $"Deployed: {Portal}"), watch.Delivery);
    }

    [Fact]
    public void Mark_deployed_is_offered_only_for_a_change_merged_elsewhere()
    {
        var (board, _) = Board(TaskStatus.Done, Pr573);
        using var watch = new Watch(board);
        watch.GitHub[Pr573.Key] = MergedNotHere;

        watch.Clock.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);

        // Merged into Deskbar but not in this build: no report can fill that ring, so no item.
        Assert.Equal(DeliveryStage.Merged, watch.Delivery.Stage);
        Assert.False(watch.Tasks.CanMarkDeployed(watch.Tasks.Board.Tasks.Single()));
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
