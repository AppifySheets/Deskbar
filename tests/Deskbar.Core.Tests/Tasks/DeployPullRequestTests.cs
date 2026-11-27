using CSharpFunctionalExtensions;
using Deskbar.Core.Control;
using Deskbar.Core.Tasks;
using Xunit;

namespace Deskbar.Core.Tests.Tasks;

// A task's DEPLOY pull request (task 1565789d, TaskItem.DeployPullRequests): a pull request the session did not open,
// whose merge comes before the deploy, like the stacks pull request an iam.ge release build opens. The minute poll tells
// the session once when it is merged or closed (even when it was merged before it was registered), never asks it to
// rebase, never follows its runs, and its merge settles no question. Pure rules first, then the board, the file and the
// pipe end to end.
public sealed class DeployPullRequestTests : IDisposable
{
    static readonly DateTimeOffset Now = new(2026, 11, 27, 16, 0, 0, TimeSpan.Zero);
    const string Stacks = "appifysheets/personalized-health-stacks";
    const string StacksUrl = "https://github.com/AppifySheets/personalized-health-stacks/pull/309";

    static readonly PullRequestLink Deploy = new("AppifySheets", "personalized-health-stacks", 309);
    static readonly PullRequestLink Code = new("AppifySheets", "personalized-health", 579);

    readonly string dir = Path.Combine(Path.GetTempPath(), "deskbar-deploy-prs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    // A task with a session (In progress, as Create makes it) and the stacks pull request as its deploy pull request.
    static (TaskBoard Board, Guid Id) WithDeploy()
    {
        var made = TaskBoard.Empty.Create("Cycle tracker", Maybe<Guid>.None,
            new TaskSession("s1", @"C:\repos\iam.ge", Now, Maybe<DateTimeOffset>.None), Now, Guid.NewGuid()).Value;
        return (made.Board.AddDeployPullRequest(made.Task.Id, Deploy, Now).Value, made.Task.Id);
    }

    static PullFacts Facts(PullRequestState state, string head = "h1", Mergeable mergeable = Mergeable.Mergeable) =>
        new(Deploy, state, mergeable, "main", head, state == PullRequestState.Merged ? Maybe.From("m1") : Maybe<string>.None);

    static RepoWatchStep Poll(RepoWatchState state, TaskBoard board, PullFacts facts) =>
        RepoWatch.Poll(state, board, Stacks, [facts], Now);

    // --- the poll rules ----------------------------------------------------------------------------------------------

    [Fact]
    public void A_deploy_pull_request_is_in_flight_with_its_repository()
    {
        var (board, id) = WithDeploy();

        var flying = RepoWatch.InFlight(board, RepoWatchState.Empty);

        Assert.Equal([(id, Deploy)], flying[Stacks].Select(x => (x.Task.Id, x.Link)));
    }

    // The branch is CI's: a base move or a conflict is not the session's to fix, so neither is told.
    [Fact]
    public void An_open_deploy_pull_request_is_never_asked_to_rebase()
    {
        var (board, _) = WithDeploy();
        var first = Poll(RepoWatchState.Empty, board, Facts(PullRequestState.Open, "h1")).State;

        Assert.Empty(Poll(first, board, Facts(PullRequestState.Open, "h2", Mergeable.Conflicting)).Nudges);
    }

    // The case that started this: #309 was merged before the session registered it, so it was never seen open.
    [Fact]
    public void A_deploy_pull_request_merged_before_it_was_seen_is_told_once()
    {
        var (board, id) = WithDeploy();

        var step = Poll(RepoWatchState.Empty, board, Facts(PullRequestState.Merged));

        Assert.Equal([new WatchNudge(id, RepoWatch.DeployMergedText(Deploy))], step.Nudges);
        Assert.Equal([new DeployEnd(id, Deploy.Key)], step.DeployEnded);
        Assert.Contains("task deployed", step.Nudges.Single().Text);
        // Its merge commit's runs are not followed: the deploy is what the session watches next.
        Assert.Empty(step.State.Following);
    }

    [Fact]
    public void A_deploy_pull_request_seen_open_then_merged_is_told()
    {
        var (board, id) = WithDeploy();
        var first = Poll(RepoWatchState.Empty, board, Facts(PullRequestState.Open));

        var step = Poll(first.State, board, Facts(PullRequestState.Merged));

        Assert.Empty(first.Nudges);
        Assert.Empty(first.DeployEnded);
        Assert.Equal([id], step.Nudges.Select(n => n.TaskId));
        Assert.Empty(step.State.Following);
    }

    [Fact]
    public void A_deploy_pull_request_closed_without_merging_is_told()
    {
        var (board, id) = WithDeploy();

        var step = Poll(RepoWatchState.Empty, board, Facts(PullRequestState.Closed));

        Assert.Contains("closed without merging", step.Nudges.Single().Text);
        Assert.Equal([new DeployEnd(id, Deploy.Key)], step.DeployEnded);
    }

    // Several per task (Petre: "it should be able to watch multiple prs actually"): each is told as it ends, and a merge
    // names the others not merged yet; one ending in the same poll is not named as still open.
    [Fact]
    public void Each_of_several_deploy_pull_requests_is_told_and_a_merge_names_the_rest()
    {
        var (board, id) = WithDeploy();
        var replaced = Deploy with { Number = 308 };
        var other = Deploy with { Number = 310 };
        board = board.AddDeployPullRequest(id, replaced, Now).Value.AddDeployPullRequest(id, other, Now).Value;
        PullFacts Of(PullRequestLink link, PullRequestState state) => Facts(state) with
        {
            Link = link,
            MergeCommit = state == PullRequestState.Merged ? Maybe.From("m" + link.Number) : Maybe<string>.None,
        };

        var step = RepoWatch.Poll(RepoWatchState.Empty, board, Stacks,
            [Of(replaced, PullRequestState.Closed), Of(Deploy, PullRequestState.Merged), Of(other, PullRequestState.Open)], Now);

        var text = step.Nudges.Single().Text;
        Assert.Contains("closed without merging", text);
        Assert.Contains("replaced it", text);
        Assert.Contains($"Not merged yet: {other.Label}", text);
        Assert.DoesNotContain(replaced.Url + ")", text.Split("is merged.")[1]);
        Assert.Equal([replaced.Key, Deploy.Key], step.DeployEnded.Select(e => e.Key));
    }

    // Once told, the task keeps the key and the poll leaves the pull request alone, across restarts too (it is saved).
    [Fact]
    public void A_told_deploy_pull_request_leaves_the_poll()
    {
        var (board, id) = WithDeploy();

        var told = board.DeployPullRequestsEnded(id, [Deploy.Key]);

        Assert.Equal([Deploy.Key], told.Tasks.Single().DeployPullRequestsEnded);
        Assert.False(RepoWatch.InFlight(told, RepoWatchState.Empty).ContainsKey(Stacks));
        // Telling it again, or a key that is not a deploy pull request of the task, changes nothing.
        Assert.Same(told, told.DeployPullRequestsEnded(id, [Deploy.Key, Code.Key]));
    }

    // The prompt line's "Deskbar watches your pull request" is about the task's own work only.
    [Fact]
    public void A_deploy_pull_request_alone_does_not_make_the_task_watched()
    {
        var (board, _) = WithDeploy();
        var state = Poll(RepoWatchState.Empty, board, Facts(PullRequestState.Open)).State;

        Assert.False(RepoWatch.Watched(state, board.Tasks.Single(), Now));
    }

    // --- the board -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Adding_one_is_idempotent_and_moves_it_off_the_task_s_own_pull_requests()
    {
        var made = TaskBoard.Empty.Create("Cycle tracker", Maybe<Guid>.None,
            new TaskSession("s1", @"C:\repos\iam.ge", Now, Maybe<DateTimeOffset>.None), Now, Guid.NewGuid()).Value;
        var board = made.Board.AddPullRequest(made.Task.Id, Code, Now).Value.AddPullRequest(made.Task.Id, Deploy, Now).Value;

        var added = board.AddDeployPullRequest(made.Task.Id, Deploy, Now).Value;

        Assert.Equal([Code], added.Tasks.Single().PullRequests);
        Assert.Equal([Deploy], added.Tasks.Single().DeployPullRequests);
        Assert.Same(added, added.AddDeployPullRequest(made.Task.Id, Deploy, Now).Value);
    }

    // A question that links the deploy pull request goes on to ask for the redeploy, so the merge must not settle it.
    [Fact]
    public void A_question_settles_on_its_own_pull_requests_not_on_the_deploy_one()
    {
        var (board, id) = WithDeploy();
        board = board.Ask(id, $"Merge [stacks #309]({StacksUrl}), then press Pull and redeploy in Portainer", Now, Maybe<(string, Guid)>.None).Value;

        Assert.Empty(AskedPullRequests.Watched(board));

        board = board.Ask(id, $"Merge [personalized-health #579]({Code.Url}) and [stacks #309]({StacksUrl})", Now, Maybe<(string, Guid)>.None).Value;
        Assert.Equal([Code], AskedPullRequests.Watched(board).Single().Links);
    }

    // --- the file ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Both_lists_survive_a_save_and_a_load()
    {
        var (board, id) = WithDeploy();
        var store = new JsonTaskStore(dir);

        Assert.True(store.Save(board.DeployPullRequestsEnded(id, [Deploy.Key])).IsSuccess);
        var loaded = store.Load().Value.Tasks.Single();

        Assert.Equal([Deploy], loaded.DeployPullRequests);
        Assert.Equal([Deploy.Key], loaded.DeployPullRequestsEnded);
    }

    [Fact]
    public void A_task_without_one_writes_no_field()
    {
        var made = TaskBoard.Empty.Create("t", Maybe<Guid>.None, new TaskSession("s1", @"C:\x", Now, Maybe<DateTimeOffset>.None), Now, Guid.NewGuid()).Value;

        Assert.True(new JsonTaskStore(dir).Save(made.Board).IsSuccess);
        Assert.DoesNotContain("deployPullRequests", File.ReadAllText(Path.Combine(dir, "tasks.json")), StringComparison.OrdinalIgnoreCase);
    }

    // --- the service ---------------------------------------------------------------------------------------------------

    // TaskService.Watched keeps what the step told on the task before it announces the nudge.
    [Fact]
    public void The_service_keeps_a_told_deploy_pull_request_on_the_task()
    {
        var service = new TaskService(new MemoryTaskStore(), () => Now);
        var id = service.Create("Cycle tracker", Maybe<Guid>.None, new TaskSession("s1", @"C:\repos\iam.ge", Now, Maybe<DateTimeOffset>.None)).Value.Id;
        Assert.True(service.AddDeployPullRequest(id, Deploy).IsSuccess);
        var heard = new List<WatchNudge>();
        using var _ = service.WatchNudges.Subscribe(heard.Add);

        service.Watched(RepoWatch.Poll(service.Watch, service.Board, Stacks, [Facts(PullRequestState.Merged)], Now));

        Assert.Equal([Deploy.Key], service.Board.Tasks.Single().DeployPullRequestsEnded);
        Assert.Single(heard);
    }

    // --- the pipe ------------------------------------------------------------------------------------------------------

    readonly TaskService tasks = new(new MemoryTaskStore(), () => Now);

    sealed class MemoryTaskStore : ITaskStore
    {
        public Result<TaskBoard> Load() => TaskBoard.Empty;

        public Result Save(TaskBoard board) => Result.Success();
    }

    WorkspaceManager Started()
    {
        var manager = new WorkspaceManager(new FakeDesktops(), new FakeMonitor(), new FakeTitles(), new FakeStore(), ownProcessId: 4242);
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    ControlReply Ran(WorkspaceManager manager, params string[] args) => new RemoteControl(manager, tasks: tasks).Execute(args);

    [Fact]
    public void The_deploy_forms_register_one_and_task_show_lists_it()
    {
        var manager = Started();
        Ran(manager, "task", "new", "Cycle tracker", "--session", "s1", "--from", @"C:\repos\iam.ge");
        var id = tasks.Board.Tasks.Single().Id.ToString()[..8];

        Assert.Equal(ControlReply.Ok, Ran(manager, "task", "pr", StacksUrl, "--deploy", "--session", "s1").ExitCode);
        Assert.Equal(ControlReply.Ok, Ran(manager, "task", "pr", id, "https://github.com/AppifySheets/personalized-health-stacks/pull/310", "--deploy").ExitCode);
        Assert.NotEqual(ControlReply.Ok, Ran(manager, "task", "pr", "#311", "--deploy", "--session", "s1").ExitCode);
        // Without --session the one-link form is a session's and is refused as such, not read as a task id.
        Assert.Contains("needs --session", Ran(manager, "task", "pr", StacksUrl, "--deploy").Output);

        Assert.Equal([309, 310], tasks.Board.Tasks.Single().DeployPullRequests.Select(p => p.Number));
        Assert.Empty(tasks.Board.Tasks.Single().PullRequests);
        Assert.Contains("deployPullRequests", Ran(manager, "task", "show", id).Output);
    }

    [Fact]
    public void Help_lists_both_deploy_forms()
    {
        Assert.Contains(ControlUsage.All, c => c.Verb == "task" && c.Arguments.StartsWith("pr <url> --deploy --session", StringComparison.Ordinal));
        Assert.Contains(ControlUsage.All, c => c.Verb == "task" && c.Arguments == "pr <id> <url> --deploy");
    }

    // An ask that links a pull request the task does not record answers with how to register it; one it records does not.
    [Fact]
    public void An_ask_linking_an_unrecorded_pull_request_says_how_to_register_it()
    {
        var manager = Started();
        Ran(manager, "task", "new", "Cycle tracker", "--session", "s1", "--from", @"C:\repos\iam.ge");

        var asked = Ran(manager, "task", "ask", $"Merge [stacks #309]({StacksUrl}), then redeploy", "--session", "s1");
        Assert.Contains("deployHint", asked.Output);
        Assert.Contains("--deploy", asked.Output);

        Ran(manager, "task", "pr", StacksUrl, "--deploy", "--session", "s1");
        var again = Ran(manager, "task", "ask", $"Merge [stacks #309]({StacksUrl}), then redeploy", "--session", "s1");
        Assert.DoesNotContain("deployHint", again.Output);
    }
}
