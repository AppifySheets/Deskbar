using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
using Xunit;
// System.Threading.Tasks (an implicit using) has a TaskStatus too; the alias says which one this file means.
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Tasks;

// Task fa22a198: three tasks still asked "Update Deskbar to v2.6.77" after the user had installed it, because the answer
// that filled their ticks (TaskService.Arrived) settled nothing and told no session. The running build now settles such a
// question and wakes the session (RunningQuestions). Driven through TaskService, the way the running-change watch drives
// it: the question is asked, the build is heard of, then GitHub's answers land.
public sealed class RunningQuestionsTests
{
    DateTimeOffset clock = new(2026, 11, 27, 21, 45, 0, TimeSpan.Zero);
    readonly MemoryStore store = new();
    readonly Guid ws = Guid.NewGuid();

    static readonly RunningBuild Build = new("AppifySheets", "Deskbar", "2.6.77", "7eadd69079");
    static readonly PullRequestLink Pr697 = new("AppifySheets", "Deskbar", 697);
    static readonly PullRequestLink Pr698 = new("AppifySheets", "Deskbar", 698);
    static readonly PullRequestLink Portal = new("AppifySheets", "portal", 301);

    static readonly PullRequestArrival Running = new(PullRequestState.Merged, true);
    static readonly PullRequestArrival MergedNotHere = new(PullRequestState.Merged, false);

    const string Update =
        "Update Deskbar to [v2.6.77](https://github.com/AppifySheets/Deskbar/releases/tag/v2.6.77) (tray menu, Check for updates): it carries [Deskbar #697](https://github.com/AppifySheets/Deskbar/pull/697). You are still on 2.6.76.";

    static TaskSession Session(string id, DateTimeOffset at) => new(id, @"C:\repos\TaskSpaces", at, Maybe<DateTimeOffset>.None);

    static Dictionary<string, PullRequestArrival> Facts(params (PullRequestLink Link, PullRequestArrival Arrival)[] facts) =>
        facts.ToDictionary(f => f.Link.Key, f => f.Arrival);

    // A task with the pull requests recorded, waiting on the question, the service, and the nudges it sends.
    (TaskService Service, Guid Id, List<WatchNudge> Nudges) Waiting(string question, bool deploy, IReadOnlyList<string>? options,
        params PullRequestLink[] links)
    {
        var s = new TaskService(store, () => clock);
        var id = s.Create("that hover text seems redundant", ws, Session("f5d44c39", clock)).Value.Id;
        links.ToList().ForEach(l => s.AddPullRequest(id, l));
        Assert.True(s.Ask(id, question, options: options, deploy: deploy).IsSuccess);
        var nudges = new List<WatchNudge>();
        s.WatchNudges.Subscribe(nudges.Add);
        return (s, id, nudges);
    }

    // The update is installed overnight: Deskbar restarts into the new build, hears of it, then GitHub answers.
    void Updated(TaskService s, Dictionary<string, PullRequestArrival> facts)
    {
        clock = clock.AddHours(7.5);
        s.Arrived(Build, new Dictionary<string, PullRequestArrival>());
        clock = clock.AddSeconds(30);
        s.Arrived(Build, facts);
    }

    TaskItem Task(TaskService s, Guid id) => s.Board.Tasks.Single(t => t.Id == id);

    [Fact]
    public void A_deploy_hand_over_is_settled_once_the_running_build_carries_the_change()
    {
        var (s, id, nudges) = Waiting(Update, deploy: true, options: null, Pr697);

        Updated(s, Facts((Pr697, Running)));

        var task = Task(s, id);
        Assert.Equal(TaskStatus.InProgress, task.Status);
        Assert.False(task.Question.HasValue);
        Assert.Equal("Question settled: this Deskbar runs 2.6.77, which carries [Deskbar #697](https://github.com/AppifySheets/Deskbar/pull/697).",
            task.Comments.Last().Markdown);
        var nudge = Assert.Single(nudges);
        Assert.Equal(id, nudge.TaskId);
        Assert.Contains("the running Deskbar is now 2.6.77+7eadd69079, which carries Deskbar #697 (https://github.com/AppifySheets/Deskbar/pull/697)", nudge.Text);
        Assert.Contains("task deployed", nudge.Text);
    }

    // 6da6758f asked the same without --deploy, and with no PR link at all: an instruction with no "?" counts too.
    [Fact]
    public void A_plain_update_instruction_is_settled_too()
    {
        var (s, id, nudges) = Waiting(
            "Update Deskbar to [v2.6.77](https://github.com/AppifySheets/Deskbar/releases/tag/v2.6.77) (tray menu, Update): it puts a short chat back at the bottom. Your running copy is still 2.6.76.",
            deploy: false, options: null, Pr697);

        Updated(s, Facts((Pr697, Running)));

        Assert.Equal(TaskStatus.InProgress, Task(s, id).Status);
        Assert.Single(nudges);
    }

    [Fact]
    public void A_change_not_in_this_build_settles_nothing()
    {
        var (s, id, nudges) = Waiting(Update, deploy: true, options: null, Pr697);

        Updated(s, Facts((Pr697, MergedNotHere)));

        Assert.Equal(TaskStatus.WaitingOnYou, Task(s, id).Status);
        Assert.Empty(nudges);
    }

    // A question asked while this build already ran the change is about something else: it is left for the user.
    [Fact]
    public void A_question_asked_after_this_build_started_is_left_alone()
    {
        var s = new TaskService(store, () => clock);
        s.Arrived(Build, new Dictionary<string, PullRequestArrival>());
        clock = clock.AddMinutes(5);
        var id = s.Create("Chip", ws, Session("s1", clock)).Value.Id;
        s.AddPullRequest(id, Pr697);
        Assert.True(s.Ask(id, "Look at the new chip on the bar and tell me", deploy: false).IsSuccess);

        clock = clock.AddSeconds(30);
        s.Arrived(Build, Facts((Pr697, Running)));

        Assert.Equal(TaskStatus.WaitingOnYou, Task(s, id).Status);
    }

    // Review finding: a reboot, a crash or a later update restarts Deskbar too, so a plain instruction asked while the
    // change already ran must not read the restart as the install it asked for. It names no version, so it waits.
    [Fact]
    public void A_restart_does_not_settle_an_instruction_that_names_no_version()
    {
        var (s, id, nudges) = Waiting("Look at the new chip on the bar and tell me", deploy: false, options: null, Pr697);

        Updated(s, Facts((Pr697, Running)));

        Assert.Equal(TaskStatus.WaitingOnYou, Task(s, id).Status);
        Assert.Empty(nudges);
    }

    // The question asks for a version this build has not reached: it waits, even with the change inside.
    [Fact]
    public void A_version_newer_than_this_build_settles_nothing()
    {
        var (s, id, _) = Waiting(
            "Update Deskbar to [v2.6.78](https://github.com/AppifySheets/Deskbar/releases/tag/v2.6.78) (tray menu, Update). You run 2.6.77.",
            deploy: true, options: null, Pr697);

        Updated(s, Facts((Pr697, Running)));

        Assert.Equal(TaskStatus.WaitingOnYou, Task(s, id).Status);
    }

    // A deploy hand-over that names no version is still the update ask.
    [Fact]
    public void A_deploy_hand_over_without_a_version_is_settled()
    {
        var (s, id, _) = Waiting("Install the Deskbar update from the tray menu (Check for updates)", deploy: true, options: null, Pr697);

        Updated(s, Facts((Pr697, Running)));

        Assert.Equal(TaskStatus.InProgress, Task(s, id).Status);
    }

    [Theory]
    [InlineData("Update Deskbar to v2.6.77. You are still on 2.6.76.", "2.6.77")]
    [InlineData("[Deskbar 2.6.77](https://github.com/AppifySheets/Deskbar/releases/tag/v2.6.77)", "2.6.77")]
    [InlineData("Reach it at 10.0.0.12 after the update", "")]
    [InlineData("Nothing versioned here", "")]
    public void The_highest_version_a_question_names_is_read(string text, string expected) =>
        Assert.Equal(expected, RunningQuestions.HighestVersion(text).Map(v => v.ToString()).GetValueOrDefault(""));

    [Fact]
    public void A_question_that_asks_something_is_left_alone()
    {
        var (s, id, _) = Waiting("Should the chip be amber or grey?", deploy: false, options: null, Pr697);

        Updated(s, Facts((Pr697, Running)));

        Assert.Equal(TaskStatus.WaitingOnYou, Task(s, id).Status);
    }

    [Fact]
    public void A_question_with_answer_buttons_is_left_alone()
    {
        var (s, id, _) = Waiting("Pick a colour for the chip", deploy: false, options: ["Amber", "Grey"], Pr697);

        Updated(s, Facts((Pr697, Running)));

        Assert.Equal(TaskStatus.WaitingOnYou, Task(s, id).Status);
    }

    // "Merge #698 next" is about #698, which the task does not record: its own watch settles it, not the running build.
    [Fact]
    public void A_question_linking_a_pull_request_the_task_does_not_record_is_left_alone()
    {
        var (s, id, _) = Waiting("Merge [Deskbar #698](https://github.com/AppifySheets/Deskbar/pull/698) next", deploy: false, options: null, Pr697);

        Updated(s, Facts((Pr697, Running), (Pr698, Running)));

        Assert.Equal(TaskStatus.WaitingOnYou, Task(s, id).Status);
    }

    // The task also waits on the portal's redeploy, which Deskbar's own version says nothing about.
    [Fact]
    public void A_task_still_waiting_on_another_apps_deploy_is_left_alone()
    {
        var (s, id, _) = Waiting(Update, deploy: true, options: null, Pr697, Portal);

        Updated(s, Facts((Pr697, Running), (Portal, MergedNotHere)));

        Assert.Equal(TaskStatus.WaitingOnYou, Task(s, id).Status);
    }

    // Every look hands the whole set over again: a question settled once is not settled, noted or nudged twice.
    [Fact]
    public void A_settled_question_is_told_once()
    {
        var (s, id, nudges) = Waiting(Update, deploy: true, options: null, Pr697, Pr698);

        Updated(s, Facts((Pr697, Running), (Pr698, Running)));
        s.Arrived(Build, Facts((Pr697, Running), (Pr698, Running), (Portal, MergedNotHere)));

        Assert.Single(nudges);
        Assert.Contains("carries [Deskbar #697](https://github.com/AppifySheets/Deskbar/pull/697) and [Deskbar #698](https://github.com/AppifySheets/Deskbar/pull/698).",
            Task(s, id).Comments.Last().Markdown);
    }

    // A store that keeps the board in memory, as TaskServiceTests' does.
    sealed class MemoryStore : ITaskStore
    {
        TaskBoard stored = TaskBoard.Empty;

        public Result<TaskBoard> Load() => stored;

        public Result Save(TaskBoard board)
        {
            stored = board;
            return Result.Success();
        }
    }
}
