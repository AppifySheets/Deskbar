using CSharpFunctionalExtensions;
using Deskbar.Core.Control;
using Deskbar.Core.Tasks;
using Xunit;
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Tasks;

// A SESSION THAT CANNOT SEE ITS DEPLOY SAYS SO (task 485ba96b). Petre: "if the task has no way of knowing if it's been
// deployed, show a chip for not knowing, not a question mark, and state that the session has no access to logs or
// whatever else it needs". `task deployed - --unknown` records the reason (TaskItem.DeployUnknown), the delivery becomes
// DeliveryStage.Unchecked with the reason as its words, the Inbox draws InboxMark.DeployUnchecked instead of "finished,
// not running yet" or the "?" bubble, and a real deploy report replaces it.
public sealed class DeployUnknownTests : IDisposable
{
    static readonly DateTimeOffset Now = new(2026, 11, 28, 10, 0, 0, TimeSpan.Zero);
    static readonly PullRequestLink Code = new("AppifySheets", "iam.ge", 537);
    static readonly RunningBuild Build = new("AppifySheets", "Deskbar", "2.6.80", "0123456789abcdef0123456789abcdef01234567");
    const string NoLogs = "no access to the Portainer logs on contabo1";

    readonly string dir = Path.Combine(Path.GetTempPath(), "deskbar-deploy-unknown-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    static (TaskBoard Board, Guid Id) Merged(TaskStatus status = TaskStatus.InProgress)
    {
        var made = TaskBoard.Empty.Create("Portal: last seen column", Maybe<Guid>.None,
            new TaskSession("s1", @"C:\repos\iam.ge", Now.AddHours(-2), Maybe<DateTimeOffset>.None), Now.AddHours(-2), Guid.NewGuid()).Value;
        var board = made.Board.AddPullRequest(made.Task.Id, Code, Now.AddHours(-1)).Value;
        return (status == TaskStatus.InProgress ? board : board.SetStatus(made.Task.Id, status, Now.AddMinutes(-30)).Value, made.Task.Id);
    }

    static TaskItem Of(TaskBoard board, Guid id) => board.Tasks.Single(t => t.Id == id);

    static Dictionary<string, PullRequestArrival> MergedFacts() => new() { [Code.Key] = new PullRequestArrival(PullRequestState.Merged, false) };

    [Fact]
    public void The_reason_is_recorded_with_its_time()
    {
        var (board, id) = Merged();

        var task = Of(board.MarkDeployUnknown(id, "  " + NoLogs + "\n", Now).Value, id);

        Assert.Equal(new DeployUnknownReport(Now, NoLogs), task.DeployUnknown.Value);
        Assert.Equal(Now, task.UpdatedAt);
    }

    [Fact]
    public void A_blank_reason_a_task_without_a_pull_request_and_a_known_deploy_are_refused()
    {
        var (board, id) = Merged();
        Assert.True(board.MarkDeployUnknown(id, "  ", Now).IsFailure);

        var bare = TaskBoard.Empty.Create("Research", Maybe<Guid>.None,
            new TaskSession("s1", @"C:\repos\iam.ge", Now, Maybe<DateTimeOffset>.None), Now, Guid.NewGuid()).Value;
        Assert.Equal(TaskBoard.NoPullRequestToDeploy, bare.Board.MarkDeployUnknown(bare.Task.Id, NoLogs, Now).Error);

        var deployed = board.MarkDeployed(id, "v1 in the logs", Now).Value;
        Assert.Equal(TaskBoard.AlreadyReportedDeployed, deployed.MarkDeployUnknown(id, NoLogs, Now).Error);
    }

    [Fact]
    public void A_later_deploy_report_clears_it()
    {
        var (board, id) = Merged();
        var unknown = board.MarkDeployUnknown(id, NoLogs, Now).Value;

        var task = Of(unknown.MarkDeployed(id, "v1 in the logs", Now.AddMinutes(5)).Value, id);

        Assert.True(task.DeployUnknown.HasNoValue);
        Assert.Single(task.Deployed);
    }

    // The user made the deploy the session handed over, and the session cannot see how it went: that settles the hand-over
    // as a deploy report does, so the session can finish.
    [Fact]
    public void It_settles_a_deploy_hand_over()
    {
        var (merged, id) = Merged();
        var handedOver = merged.Ask(id, "Redeploy the portal stack in Portainer", Now, deploy: true).Value;

        var task = Of(handedOver.MarkDeployUnknown(id, NoLogs, Now.AddMinutes(10)).Value, id);

        Assert.Equal(TaskStatus.InProgress, task.Status);
        Assert.True(task.Question.HasNoValue);
        Assert.False(task.AwaitsDeploy);
    }

    [Fact]
    public void A_done_task_whose_session_cannot_check_is_unchecked_with_the_reason()
    {
        var (board, id) = Merged(TaskStatus.Done);
        var task = Of(board.MarkDeployUnknown(id, NoLogs + ".", Now).Value, id);

        var delivery = RunningChanges.Of(task, Build, MergedFacts());

        Assert.Equal(DeliveryStage.Unchecked, delivery.Stage);
        Assert.True(delivery.Shipping);
        Assert.True(delivery.Unchecked);
        Assert.Equal("Can't check the deploy: " + NoLogs, delivery.Note);
        Assert.Equal(InboxMark.DeployUnchecked, InboxTabs.MarkOf(task, _ => false, hollow: t => RunningChanges.Of(t, Build, MergedFacts()).Shipping));
    }

    // An open task shows Unchecked too, as it shows Merged (RunningChanges.Of), and one still open on GitHub stays Open:
    // nothing was deployed from a pull request that has not merged.
    [Fact]
    public void Unchecked_only_follows_a_merge()
    {
        var (board, id) = Merged();
        var task = Of(board.MarkDeployUnknown(id, NoLogs, Now).Value, id);

        Assert.Equal(DeliveryStage.Unchecked, RunningChanges.Of(task, Build, MergedFacts()).Stage);
        var open = new Dictionary<string, PullRequestArrival> { [Code.Key] = new PullRequestArrival(PullRequestState.Open, false) };
        Assert.Equal(DeliveryStage.None, RunningChanges.Of(task, Build, open).Stage);
        var done = Of(board.SetStatus(id, TaskStatus.Done, Now).Value.MarkDeployUnknown(id, NoLogs, Now).Value, id);
        Assert.Equal(DeliveryStage.Open, RunningChanges.Of(done, Build, open).Stage);
    }

    [Fact]
    public void It_survives_a_save_and_a_load()
    {
        var (board, id) = Merged(TaskStatus.Done);
        var unknown = board.MarkDeployUnknown(id, NoLogs, Now).Value;

        Assert.True(new JsonTaskStore(dir).Save(unknown).IsSuccess);
        var loaded = new JsonTaskStore(dir).Load().Value;

        Assert.Equal(new DeployUnknownReport(Now, NoLogs), Of(loaded, id).DeployUnknown.Value);
    }

    [Fact]
    public void A_task_without_it_is_written_as_before()
    {
        var (board, _) = Merged(TaskStatus.Done);

        Assert.True(new JsonTaskStore(dir).Save(board).IsSuccess);

        Assert.DoesNotContain("deployUnknown", File.ReadAllText(Path.Combine(dir, "tasks.json")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_flag_takes_stdin_in_either_place()
    {
        Assert.Equal(["task", "deployed", NoLogs, "--unknown", "--session", "s1"],
            ControlArguments.WithTextInput(["task", "deployed", "-", "--unknown", "--session", "s1"], () => NoLogs + "\n"));
        Assert.Equal(["task", "deployed", "--unknown", NoLogs, "--session", "s1"],
            ControlArguments.WithTextInput(["task", "deployed", "--unknown", "-", "--session", "s1"], () => NoLogs));
    }

    [Fact]
    public void Help_lists_both_unknown_forms()
    {
        Assert.Contains(ControlUsage.All, c => c.Verb == "task" && c.Arguments.StartsWith("deployed <reason>|- --unknown --session", StringComparison.Ordinal));
        Assert.Contains(ControlUsage.All, c => c.Verb == "task" && c.Arguments.StartsWith("deployed <id> <reason>|- --unknown", StringComparison.Ordinal));
    }
}
