using CSharpFunctionalExtensions;
using Deskbar.Core.Control;
using Deskbar.Core.Tasks;
using Xunit;
// System.Threading.Tasks (an implicit using) has a TaskStatus too; the alias says which one this file means.
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Tasks;

// A DEPLOY HAND-OVER (task a87891ca, `task ask - --deploy`, TaskItem.QuestionIsDeploy). An iam.ge task that was merged,
// built and only waiting for Petre's redeploy was drawn as "Waits for your answer" with the question mark, because the
// briefing told its session to ask for the deploy like any question. Petre: "it's not a question. it should qualify as
// merged, built, waiting for deploy". The hand-over still waits on him, but it is marked, drawn as the merged ring
// (InboxMark.WaitsForDeploy), found by the deploy check without the word "deploy", and settled by the deploy report.
public sealed class DeployHandOverTests : IDisposable
{
    static readonly DateTimeOffset Now = new(2026, 11, 28, 10, 0, 0, TimeSpan.Zero);
    static readonly PullRequestLink Code = new("AppifySheets", "personalized-health", 581);
    const string Redeploy = "Press Pull and redeploy on the portal stack in Portainer: stacks #310 pins 2026.11.27.54.";

    readonly string dir = Path.Combine(Path.GetTempPath(), "deskbar-hand-over-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    // A task with a live session and its merged code pull request recorded, In progress as Create makes it.
    static (TaskBoard Board, Guid Id) Merged()
    {
        var made = TaskBoard.Empty.Create("add file download", Maybe<Guid>.None,
            new TaskSession("s1", @"C:\repos\iam.ge", Now.AddHours(-2), Maybe<DateTimeOffset>.None), Now.AddHours(-2), Guid.NewGuid()).Value;
        return (made.Board.AddPullRequest(made.Task.Id, Code, Now.AddHours(-1)).Value, made.Task.Id);
    }

    static (TaskBoard Board, Guid Id) HandedOver(string text = Redeploy)
    {
        var (board, id) = Merged();
        return (board.Ask(id, text, Now, deploy: true).Value, id);
    }

    static TaskItem Of(TaskBoard board, Guid id) => board.Tasks.Single(t => t.Id == id);

    static bool NotLimited(TaskItem task) => false;

    // --- the board ----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_hand_over_waits_on_the_user_and_is_drawn_as_merged_waiting_for_the_deploy()
    {
        var (board, id) = HandedOver();
        var task = Of(board, id);

        Assert.Equal(TaskStatus.WaitingOnYou, task.Status);
        Assert.Equal(Redeploy, task.Question.Value);
        Assert.True(task.QuestionIsDeploy);
        Assert.True(task.AwaitsDeploy);
        Assert.Equal(InboxMark.WaitsForDeploy, InboxTabs.MarkOf(task, NotLimited));
        // Its line's time is the ask's, as a question's is.
        Assert.Equal(task.WaitingSince.GetValueOrDefault(task.Raised), InboxTabs.SinceOf(task, InboxMark.WaitsForDeploy));
    }

    [Fact]
    public void A_plain_question_is_still_a_question()
    {
        var (board, id) = Merged();
        var task = Of(board.Ask(id, "Deploy now or after the demo?", Now).Value, id);

        Assert.False(task.QuestionIsDeploy);
        Assert.Equal(InboxMark.Asks, InboxTabs.MarkOf(task, NotLimited));
    }

    [Fact]
    public void Each_ask_says_afresh_whether_it_is_a_hand_over()
    {
        var (board, id) = HandedOver();

        var asked = board.Ask(id, "Which stack first, portal or back office?", Now.AddMinutes(1)).Value;

        Assert.False(Of(asked, id).QuestionIsDeploy);
    }

    [Fact]
    public void Answering_the_hand_over_drops_the_mark_with_the_question()
    {
        var (board, id) = HandedOver();

        var task = Of(board.Answered("s1", Now.AddMinutes(5)), id);

        Assert.True(task.Question.HasNoValue);
        Assert.False(task.QuestionIsDeploy);
    }

    // The user's reply sets the question aside while the session works; it comes back still a hand-over.
    [Fact]
    public void A_hand_over_set_aside_comes_back_as_a_hand_over()
    {
        var (board, id) = HandedOver();

        var aside = board.SetQuestionAside(id, Redeploy, "Set aside", Now.AddMinutes(1), Guid.NewGuid());
        Assert.True(Of(aside, id).QuestionIsDeploy);
        Assert.False(Of(aside, id).AwaitsDeploy);

        var back = Of(aside.RestoreQuestion(id, Now.AddMinutes(2)), id);
        Assert.True(back.AwaitsDeploy);
    }

    [Fact]
    public void The_deploy_report_settles_the_hand_over()
    {
        var (board, id) = HandedOver();

        var task = Of(board.MarkDeployed(id, "2026.11.27.54 in the portal logs", Now.AddMinutes(30)).Value, id);

        Assert.Equal(TaskStatus.InProgress, task.Status);
        Assert.Equal(Assignee.Claude, task.Assignee);
        Assert.True(task.Question.HasNoValue);
        Assert.False(task.QuestionIsDeploy);
        Assert.Equal([Code.Key], task.Deployed.Select(d => d.Key));
    }

    // Every pull request already reported: the report records nothing new but still settles the hand-over.
    [Fact]
    public void The_deploy_report_settles_a_hand_over_whose_pull_requests_are_already_reported()
    {
        var (merged, id) = Merged();
        var reported = merged.MarkDeployed(id, "seen in the logs", Now.AddMinutes(-5)).Value;
        var board = reported.Ask(id, Redeploy, Now, deploy: true).Value;

        var task = Of(board.MarkDeployed(id, "seen again", Now.AddMinutes(30)).Value, id);

        Assert.Equal(TaskStatus.InProgress, task.Status);
        Assert.True(task.Question.HasNoValue);
        Assert.Single(task.Deployed);
    }

    // The user replies "redeployed, check it", the question is set aside while the session looks, and the session
    // reports the deploy without a `task answered`: the set-aside hand-over must not come back (review finding).
    [Fact]
    public void The_deploy_report_settles_a_hand_over_set_aside_by_the_users_reply()
    {
        var (handedOver, id) = HandedOver();
        var aside = handedOver.SetQuestionAside(id, Redeploy, "Set aside", Now.AddMinutes(1), Guid.NewGuid());

        var task = Of(aside.MarkDeployed(id, "2026.11.27.54 in the portal logs", Now.AddMinutes(2)).Value, id);

        Assert.True(task.QuestionSetAside.HasNoValue);
        Assert.False(task.QuestionIsDeploy);
        Assert.Equal(TaskStatus.InProgress, task.Status);
    }

    // The nudges that ask a session to write a set-aside question afresh name --deploy for a hand-over (review finding),
    // or the re-ask comes back as a plain question.
    [Fact]
    public void The_set_aside_nudges_name_deploy_for_a_hand_over()
    {
        var (handedOver, id) = HandedOver();
        var aside = Of(handedOver.SetQuestionAside(id, Redeploy, "Set aside", Now.AddMinutes(1), Guid.NewGuid()), id);

        Assert.Contains("task ask - --deploy", SessionBriefing.SetAsideNudge(Redeploy, handOver: true));
        Assert.DoesNotContain("--deploy", SessionBriefing.SetAsideNudge(Redeploy));
        Assert.Contains("task ask - --deploy --session", SessionBriefing.Regenerate(aside));
    }

    // A plain question may be about something else, so a deploy report leaves it waiting, as before.
    [Fact]
    public void The_deploy_report_leaves_a_plain_question_waiting()
    {
        var (merged, id) = Merged();
        var board = merged.Ask(id, "Pick the export format", Now).Value;

        var task = Of(board.MarkDeployed(id, "seen in the logs", Now.AddMinutes(30)).Value, id);

        Assert.Equal(TaskStatus.WaitingOnYou, task.Status);
        Assert.Equal("Pick the export format", task.Question.Value);
    }

    // --- the deploy check -------------------------------------------------------------------------------------------

    [Fact]
    public void The_deploy_check_finds_a_hand_over_without_the_word_deploy()
    {
        var merged = new Dictionary<string, PullRequestArrival> { [Code.Key] = new(PullRequestState.Merged, false) };
        var (handedOver, a) = HandedOver("Press Pull on the portal stack in Portainer.");
        var (board, b) = Merged();
        var asked = board.Ask(b, "Press Pull on the portal stack in Portainer.", Now).Value;

        Assert.True(DeployWaits.WaitsOnDeploy(Of(handedOver, a), merged, _ => false));
        Assert.False(DeployWaits.WaitsOnDeploy(Of(asked, b), merged, _ => false));
    }

    // --- the file -----------------------------------------------------------------------------------------------------

    [Fact]
    public void The_mark_is_saved_only_for_a_hand_over()
    {
        var store = new JsonTaskStore(dir);
        var (handedOver, id) = HandedOver();

        Assert.True(store.Save(handedOver).IsSuccess);
        Assert.True(Of(store.Load().Value, id).AwaitsDeploy);

        var (plain, other) = Merged();
        Assert.True(store.Save(plain.Ask(other, "Pick the export format", Now).Value).IsSuccess);
        Assert.DoesNotContain("questionIsDeploy", File.ReadAllText(Path.Combine(dir, "tasks.json")));
        Assert.False(Of(store.Load().Value, other).QuestionIsDeploy);
    }

    // --- the pipe -----------------------------------------------------------------------------------------------------

    sealed class MemoryTaskStore : ITaskStore
    {
        public Result<TaskBoard> Load() => TaskBoard.Empty;

        public Result Save(TaskBoard board) => Result.Success();
    }

    readonly TaskService tasks = new(new MemoryTaskStore(), () => Now);

    WorkspaceManager Started()
    {
        var manager = new WorkspaceManager(new FakeDesktops(), new FakeMonitor(), new FakeTitles(), new FakeStore(), ownProcessId: 4242);
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    ControlReply Ran(WorkspaceManager manager, params string[] args) => new RemoteControl(manager, tasks: tasks).Execute(args);

    [Fact]
    public void Task_ask_with_deploy_hands_over_in_either_order_and_task_show_says_so()
    {
        var manager = Started();
        Ran(manager, "task", "new", "add file download", "--session", "s1", "--from", @"C:\repos\iam.ge");

        var reply = Ran(manager, "task", "ask", Redeploy, "--deploy", "--session", "s1");
        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.True(tasks.Board.Tasks.Single().AwaitsDeploy);
        Assert.Matches("\"questionIsDeploy\":\\s*true", reply.Output);
        // A hand-over is no place for the "ask it as a hand-over" hint.
        Assert.DoesNotContain(RemoteControl.HandOverHintText, reply.Output);

        Ran(manager, "task", "answered", "--session", "s1");
        Assert.Equal(ControlReply.Ok, Ran(manager, "task", "ask", "--deploy", Redeploy, "--session", "s1").ExitCode);
        Assert.True(tasks.Board.Tasks.Single().AwaitsDeploy);

        // Without --session it is a session's form, refused as such, in either order.
        Assert.Contains("needs --session", Ran(manager, "task", "ask", Redeploy, "--deploy").Output);
        Assert.Contains("needs --session", Ran(manager, "task", "ask", "--deploy", Redeploy).Output);
        // The flag with no text (the `-` forgotten) is refused, never stored as a question reading "--deploy".
        Assert.NotEqual(ControlReply.Ok, Ran(manager, "task", "ask", "--deploy", "--session", "s1").ExitCode);
        Assert.Equal(Redeploy, tasks.Board.Tasks.Single().Question.Value);
    }

    // The briefing names --deploy in a few words; a plain ask that mentions a deploy is taught it by the reply.
    [Fact]
    public void A_plain_ask_that_mentions_a_deploy_replies_with_the_hand_over_hint()
    {
        var manager = Started();
        Ran(manager, "task", "new", "add file download", "--session", "s1", "--from", @"C:\repos\iam.ge");

        var reply = Ran(manager, "task", "ask", Redeploy, "--session", "s1");

        Assert.False(tasks.Board.Tasks.Single().QuestionIsDeploy);
        Assert.Contains("handOverHint", reply.Output);
        Assert.DoesNotContain("handOverHint", Ran(manager, "task", "ask", "Pick the export format", "--session", "s1").Output);
    }

    // `task ask --deploy -` reads stdin as `task ask - --deploy` does: --deploy takes no value.
    [Fact]
    public void Deploy_is_a_flag_without_a_value_so_a_dash_after_it_still_reads_stdin()
    {
        var withText = ControlArguments.WithTextInput(["task", "ask", "--deploy", "-", "--session", "s1"], () => Redeploy);

        Assert.Equal(["task", "ask", "--deploy", Redeploy, "--session", "s1"], withText);
    }
}
