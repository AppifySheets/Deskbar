using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
using Microsoft.Reactive.Testing;
using Xunit;
// System.Threading.Tasks (an implicit using) has a TaskStatus too; the alias says which one this file means.
using TaskStatus = Deskbar.Core.Tasks.TaskStatus;

namespace Deskbar.Core.Tests.Tasks;

// A merged pull request settles the question that asked for the merge (task b7f987b4; AskedPullRequests holds the rule,
// PullRequestWatch the look every half minute, TaskBoard.SettleQuestion the change). GitHub is a fake here: a dictionary
// the test fills, so no test runs gh.
public sealed class AskedPullRequestsTests
{
    static readonly DateTimeOffset Asked = new(2026, 11, 25, 7, 0, 0, TimeSpan.Zero);

    const string Url504 = "https://github.com/AppifySheets/Deskbar/pull/504";
    const string Url505 = "https://github.com/AppifySheets/Deskbar/pull/505";
    const string Key504 = "appifysheets/deskbar#504";
    const string Key505 = "appifysheets/deskbar#505";

    static readonly PullRequestFacts Open = new(PullRequestState.Open, Maybe<DateTimeOffset>.None);

    static PullRequestFacts Merged(DateTimeOffset at) => new(PullRequestState.Merged, at);

    static PullRequestFacts ClosedAt(DateTimeOffset at) => new(PullRequestState.Closed, at);

    static TaskSession Session(string id) => new(id, @"C:\repos\TaskSpaces", Asked.AddHours(-1), Maybe<DateTimeOffset>.None);

    // A board with one Claude task whose session asked `question` at Asked.
    static (TaskBoard Board, Guid Id) Waiting(string question)
    {
        var made = TaskBoard.Empty.Create("Floating bar over panel", Maybe<Guid>.None, Session("s1"), Asked.AddHours(-1), Guid.NewGuid()).Value;
        return (made.Board.Ask(made.Task.Id, question, Asked).Value, made.Task.Id);
    }

    // --- the links in a question ---------------------------------------------------------------------------------------

    [Fact]
    public void A_markdown_link_to_a_pull_request_is_read()
    {
        var links = AskedPullRequests.In($"Merge [Deskbar #504]({Url504}): CI is green");

        var link = Assert.Single(links);
        Assert.Equal(("AppifySheets", "Deskbar", 504), (link.Owner, link.Repo, link.Number));
        Assert.Equal(Url504, link.Url);
        Assert.Equal("Deskbar #504", link.Label);
    }

    [Fact]
    public void Two_spellings_of_one_pull_request_are_one_and_a_trailing_path_is_ignored()
    {
        var links = AskedPullRequests.In($"Merge {Url504}/files, see https://GITHUB.com/appifysheets/deskbar/pull/504#issuecomment-1 and [#505]({Url505})");

        Assert.Equal([Key504, Key505], links.Select(l => l.Key));
    }

    [Fact]
    public void A_bare_number_and_an_issue_link_are_not_pull_requests()
    {
        Assert.Empty(AskedPullRequests.In("Merge #504, it fixes https://github.com/AppifySheets/Deskbar/issues/12"));
        Assert.Empty(AskedPullRequests.In(null));
    }

    [Fact]
    public void Only_waiting_tasks_with_a_linked_pull_request_are_watched()
    {
        var (board, id) = Waiting($"Merge [Deskbar #504]({Url504})");
        var plain = board.Create("Other", Maybe<Guid>.None, Session("s2"), Asked, Guid.NewGuid()).Value;
        var noLink = plain.Board.Ask(plain.Task.Id, "Option A or B?", Asked).Value;

        var watched = AskedPullRequests.Watched(noLink);

        Assert.Equal(id, Assert.Single(watched).Task.Id);
    }

    // --- when a question is settled ------------------------------------------------------------------------------------

    [Fact]
    public void A_pull_request_merged_after_the_ask_settles_it()
    {
        var links = AskedPullRequests.In(Url504);

        Assert.True(AskedPullRequests.Settles(links, new Dictionary<string, PullRequestFacts> { [Key504] = Merged(Asked.AddMinutes(5)) }, Asked));
    }

    [Fact]
    public void An_open_pull_request_does_not_settle_it()
    {
        var links = AskedPullRequests.In(Url504);

        Assert.False(AskedPullRequests.Settles(links, new Dictionary<string, PullRequestFacts> { [Key504] = Open }, Asked));
    }

    // A question that only mentions a pull request already merged ("now that #500 is in, A or B?") is still for Petre.
    [Fact]
    public void A_pull_request_merged_before_the_ask_does_not_settle_it()
    {
        var links = AskedPullRequests.In(Url504);

        Assert.False(AskedPullRequests.Settles(links, new Dictionary<string, PullRequestFacts> { [Key504] = Merged(Asked.AddMinutes(-5)) }, Asked));
    }

    [Fact]
    public void Every_linked_pull_request_must_be_done_and_one_of_them_after_the_ask()
    {
        var links = AskedPullRequests.In($"Merge {Url505}, which replaces {Url504}");
        var oldClosed = ClosedAt(Asked.AddDays(-1));

        Assert.False(AskedPullRequests.Settles(links, new Dictionary<string, PullRequestFacts> { [Key504] = oldClosed, [Key505] = Open }, Asked));
        Assert.True(AskedPullRequests.Settles(links, new Dictionary<string, PullRequestFacts> { [Key504] = oldClosed, [Key505] = Merged(Asked.AddMinutes(1)) }, Asked));
    }

    // gh missing, offline or without access: unknown is never read as closed.
    [Fact]
    public void A_pull_request_GitHub_could_not_tell_about_settles_nothing()
    {
        var links = AskedPullRequests.In($"{Url504} {Url505}");

        Assert.False(AskedPullRequests.Settles(links, new Dictionary<string, PullRequestFacts> { [Key504] = Merged(Asked.AddMinutes(1)) }, Asked));
    }

    [Fact]
    public void The_note_says_what_happened_to_each_pull_request()
    {
        var links = AskedPullRequests.In($"{Url504} {Url505}");
        var facts = new Dictionary<string, PullRequestFacts> { [Key504] = Merged(Asked), [Key505] = ClosedAt(Asked) };

        var note = AskedPullRequests.Note(links, facts);

        Assert.Equal($"Question settled: [Deskbar #504]({Url504}) was merged and "
            + $"[Deskbar #505]({Url505}) was closed without merging.", note);
    }

    // --- what gh prints (measured 2026-11-25) --------------------------------------------------------------------------

    [Fact]
    public void Gh_output_is_read()
    {
        var merged = AskedPullRequests.Parse("""{"closedAt":"2026-11-25T07:06:18Z","state":"MERGED"}""").Value;
        var open = AskedPullRequests.Parse("""{"closedAt":null,"state":"OPEN"}""").Value;

        Assert.Equal(new PullRequestFacts(PullRequestState.Merged, new DateTimeOffset(2026, 11, 25, 7, 6, 18, TimeSpan.Zero)), merged);
        Assert.Equal(Open, open);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"closedAt":null}""")]
    [InlineData("""{"state":"DRAFTY"}""")]
    [InlineData("[]")]
    public void Anything_else_gh_prints_is_a_failure(string output) =>
        Assert.True(AskedPullRequests.Parse(output).IsFailure);

    // --- the board's change ---------------------------------------------------------------------------------------------

    [Fact]
    public void Settling_takes_the_task_off_Petre_and_writes_why()
    {
        var question = $"Merge [Deskbar #504]({Url504})";
        var (board, id) = Waiting(question);
        board = board.Comment(id, "s1", "CI is green, details in the PR", Asked.AddMinutes(1), Guid.NewGuid()).Value;
        var at = Asked.AddMinutes(10);

        var settled = board.SettleQuestion(id, question, "Deskbar settled the question at 07:10.", at, Guid.NewGuid());

        var task = settled.Tasks.Single(t => t.Id == id);
        Assert.Equal(TaskStatus.InProgress, task.Status);
        Assert.True(task.Question.HasNoValue);
        Assert.True(task.Assignee.IsClaude);
        Assert.Equal(at, task.Comments[0].Settled);
        Assert.True(task.Comments[1].ByDeskbar);
        Assert.Equal("Deskbar settled the question at 07:10.", task.Comments[1].Markdown);
        // The session's own `task answered`, coming later, finds nothing to do.
        Assert.Same(settled, settled.Answered("s1", at.AddMinutes(1)));
    }

    // The session asked again while GitHub was being asked: the new question is not the one that was read.
    [Fact]
    public void A_question_asked_since_it_was_read_is_left_alone()
    {
        var (board, id) = Waiting($"Merge [Deskbar #504]({Url504})");
        var askedAgain = board.Ask(id, "Option A or B?", Asked.AddMinutes(1)).Value;

        Assert.Same(askedAgain, askedAgain.SettleQuestion(id, $"Merge [Deskbar #504]({Url504})", "note", Asked.AddMinutes(2), Guid.NewGuid()));
    }

    [Fact]
    public void A_task_no_longer_waiting_is_left_alone()
    {
        var question = $"Merge [Deskbar #504]({Url504})";
        var (board, id) = Waiting(question);
        var answered = board.Answered("s1", Asked.AddMinutes(1));

        Assert.Same(answered, answered.SettleQuestion(id, question, "note", Asked.AddMinutes(2), Guid.NewGuid()));
    }

    // --- the watch -------------------------------------------------------------------------------------------------------

    sealed class MemoryStore : ITaskStore
    {
        public TaskBoard Stored { get; set; } = TaskBoard.Empty;

        public Result<TaskBoard> Load() => Stored;

        public Result Save(TaskBoard board) => Result.Success();
    }

    sealed class Watch : IDisposable
    {
        public TestScheduler Clock { get; } = new();
        public Dictionary<string, Result<PullRequestFacts>> GitHub { get; } = [];
        public List<string> Looked { get; } = [];
        public TaskService Tasks { get; }
        public PullRequestWatch Watcher { get; }

        public Watch(TaskBoard board)
        {
            Clock.AdvanceTo(Asked.AddMinutes(1).UtcTicks);
            Tasks = new TaskService(new MemoryStore { Stored = board }, () => Clock.Now);
            Tasks.Load();
            Watcher = new PullRequestWatch(Tasks, link =>
            {
                Looked.Add(link.Key);
                return Task.FromResult(GitHub.TryGetValue(link.Key, out var answer) ? answer : Result.Failure<PullRequestFacts>("unknown"));
            }, Clock, System.Reactive.Concurrency.Scheduler.Immediate);
        }

        public void Dispose() => Watcher.Dispose();
    }

    [Fact]
    public void The_watch_settles_a_question_once_its_pull_request_merges()
    {
        var (board, id) = Waiting($"Merge [Deskbar #504]({Url504})");
        using var watch = new Watch(board);
        watch.GitHub[Key504] = Open;

        watch.Clock.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);
        Assert.Equal(TaskStatus.WaitingOnYou, watch.Tasks.Board.Tasks.Single().Status);
        Assert.Equal([Key504], watch.Looked);

        watch.GitHub[Key504] = Merged(Asked.AddMinutes(2));
        watch.Clock.AdvanceBy(PullRequestWatch.Poll.Ticks);

        var task = watch.Tasks.Board.Tasks.Single(t => t.Id == id);
        Assert.Equal(TaskStatus.InProgress, task.Status);
        Assert.Contains("[Deskbar #504]", task.Comments.Last().Markdown);
    }

    // --- a settled question wakes its session (task ede8dbe8) -------------------------------------------------------------

    // The colleague's case: the question linked a pull request that was not recorded on the task, so the minute poll never
    // watched it. Settling must record it and tell the session, or the session sits idle with its deploy check undone.
    [Fact]
    public void A_merge_that_settles_a_question_records_the_pull_request_and_wakes_the_session()
    {
        var (board, id) = Waiting($"Merge [Deskbar #504]({Url504})");
        using var watch = new Watch(board);
        var heard = new List<WatchNudge>();
        using var _ = watch.Tasks.WatchNudges.Subscribe(heard.Add);
        watch.GitHub[Key504] = Merged(Asked.AddMinutes(2));

        watch.Clock.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);

        var task = watch.Tasks.Board.Tasks.Single(t => t.Id == id);
        Assert.Equal(TaskStatus.InProgress, task.Status);
        Assert.Equal([Key504], task.PullRequests.Select(p => p.Key));
        var nudge = Assert.Single(heard);
        Assert.Equal(id, nudge.TaskId);
        Assert.Contains("Deskbar #504", nudge.Text);
        Assert.Contains("merged", nudge.Text);

        // The next look finds no waiting question: nothing more is said.
        watch.Clock.AdvanceBy(PullRequestWatch.Poll.Ticks);
        Assert.Single(heard);
    }

    // A pull request closed without a merge also settles the question and is told, but is not recorded: it never ran
    // anywhere, so it must not count for a deploy.
    [Fact]
    public void A_close_without_a_merge_wakes_the_session_but_records_nothing()
    {
        var (board, id) = Waiting($"Merge [Deskbar #504]({Url504})");
        using var watch = new Watch(board);
        var heard = new List<WatchNudge>();
        using var _ = watch.Tasks.WatchNudges.Subscribe(heard.Add);
        watch.GitHub[Key504] = ClosedAt(Asked.AddMinutes(2));

        watch.Clock.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);

        Assert.Empty(watch.Tasks.Board.Tasks.Single(t => t.Id == id).PullRequests);
        Assert.Contains("closed without merging", Assert.Single(heard).Text);
    }

    // When the minute poll already watches the pull request (recorded on the task, last seen open, polled recently), it
    // tells the merge itself, so the settlement does not tell it a second time.
    [Fact]
    public void A_merge_the_minute_poll_covers_is_not_told_twice()
    {
        var (asked, id) = Waiting($"Merge [Deskbar #504]({Url504})");
        var board = asked.AddPullRequest(id, AskedPullRequests.In(Url504).Single(), Asked).Value;
        using var watch = new Watch(board);
        watch.Tasks.Watched(new RepoWatchStep(RepoWatchState.Empty with
        {
            Seen = new Dictionary<string, PullRequestState> { [Key504] = PullRequestState.Open },
            LastSuccess = new Dictionary<string, DateTimeOffset> { ["appifysheets/deskbar"] = watch.Clock.Now },
        }, []));
        var heard = new List<WatchNudge>();
        using var _ = watch.Tasks.WatchNudges.Subscribe(heard.Add);
        watch.GitHub[Key504] = Merged(Asked.AddMinutes(2));

        watch.Clock.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);

        Assert.Equal(TaskStatus.InProgress, watch.Tasks.Board.Tasks.Single(t => t.Id == id).Status);
        Assert.Empty(heard);
    }

    // Review finding: the minute poll has no rule for a close, so a covered pull request closed without a merge is still
    // told by the settlement, or nobody tells it.
    [Fact]
    public void A_covered_close_is_still_told()
    {
        var (asked, id) = Waiting($"Merge [Deskbar #504]({Url504})");
        var board = asked.AddPullRequest(id, AskedPullRequests.In(Url504).Single(), Asked).Value;
        using var watch = new Watch(board);
        watch.Tasks.Watched(new RepoWatchStep(RepoWatchState.Empty with
        {
            Seen = new Dictionary<string, PullRequestState> { [Key504] = PullRequestState.Open },
            LastSuccess = new Dictionary<string, DateTimeOffset> { ["appifysheets/deskbar"] = watch.Clock.Now },
        }, []));
        var heard = new List<WatchNudge>();
        using var _ = watch.Tasks.WatchNudges.Subscribe(heard.Add);
        watch.GitHub[Key504] = ClosedAt(Asked.AddMinutes(2));

        watch.Clock.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);

        Assert.Contains("closed without merging", Assert.Single(heard).Text);
    }

    // Review finding: a pull request another task recorded is that task's work. The asking task hears of the merge but
    // does not take the pull request, or the minute poll would tell it again and its deploy would count it.
    [Fact]
    public void Another_tasks_pull_request_is_told_but_not_recorded()
    {
        var (asked, id) = Waiting($"Merge [Deskbar #504]({Url504}) so I can rebase");
        var owner = asked.Create("Owner of 504", Maybe<Guid>.None, Session("s2"), Asked.AddHours(-1), Guid.NewGuid()).Value;
        var board = owner.Board.AddPullRequest(owner.Task.Id, AskedPullRequests.In(Url504).Single(), Asked).Value;
        using var watch = new Watch(board);
        var heard = new List<WatchNudge>();
        using var _ = watch.Tasks.WatchNudges.Subscribe(heard.Add);
        watch.GitHub[Key504] = Merged(Asked.AddMinutes(2));

        watch.Clock.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);

        Assert.Empty(watch.Tasks.Board.Tasks.Single(t => t.Id == id).PullRequests);
        Assert.Equal(id, Assert.Single(heard).TaskId);
        Assert.False(watch.Tasks.Watch.Seen.ContainsKey(Key504));
    }

    // Review finding: a merge the settlement told itself is remembered as seen merged, so the minute poll, back from a
    // spell of failed looks with the link still seen Open, does not take it for a new merge.
    [Fact]
    public void A_merge_told_by_the_settlement_is_remembered_as_seen_merged()
    {
        var (asked, id) = Waiting($"Merge [Deskbar #504]({Url504})");
        var board = asked.AddPullRequest(id, AskedPullRequests.In(Url504).Single(), Asked).Value;
        using var watch = new Watch(board);
        // Seen Open, but the last successful poll is long past: the poll does not cover it now.
        watch.Tasks.Watched(new RepoWatchStep(RepoWatchState.Empty with
        {
            Seen = new Dictionary<string, PullRequestState> { [Key504] = PullRequestState.Open },
            LastSuccess = new Dictionary<string, DateTimeOffset> { ["appifysheets/deskbar"] = watch.Clock.Now.AddHours(-1) },
        }, []));
        var heard = new List<WatchNudge>();
        using var _ = watch.Tasks.WatchNudges.Subscribe(heard.Add);
        watch.GitHub[Key504] = Merged(Asked.AddMinutes(2));

        watch.Clock.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);

        Assert.Single(heard);
        Assert.Equal(PullRequestState.Merged, watch.Tasks.Watch.Seen[Key504]);
    }

    // Review nit: a link that merged before the task began waiting is neither told nor recorded.
    [Fact]
    public void A_link_merged_before_the_ask_is_left_out()
    {
        var (board, id) = Waiting($"Now that [Deskbar #505]({Url505}) is in, merge [Deskbar #504]({Url504})");
        using var watch = new Watch(board);
        var heard = new List<WatchNudge>();
        using var _ = watch.Tasks.WatchNudges.Subscribe(heard.Add);
        watch.GitHub[Key505] = Merged(Asked.AddDays(-1));
        watch.GitHub[Key504] = Merged(Asked.AddMinutes(2));

        watch.Clock.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);

        Assert.Equal([Key504], watch.Tasks.Board.Tasks.Single(t => t.Id == id).PullRequests.Select(p => p.Key));
        Assert.DoesNotContain("#505", Assert.Single(heard).Text);
    }

    [Fact]
    public void With_no_question_linking_a_pull_request_the_watch_asks_GitHub_nothing()
    {
        var (board, _) = Waiting("Option A or B?");
        using var watch = new Watch(board);

        watch.Clock.AdvanceBy(TimeSpan.FromMinutes(5).Ticks);

        Assert.Empty(watch.Looked);
        Assert.Equal(TaskStatus.WaitingOnYou, watch.Tasks.Board.Tasks.Single().Status);
    }

    [Fact]
    public void A_failed_look_leaves_the_question_waiting()
    {
        var (board, _) = Waiting($"Merge [Deskbar #504]({Url504})");
        using var watch = new Watch(board);

        watch.Clock.AdvanceBy(TimeSpan.FromMinutes(2).Ticks);

        Assert.NotEmpty(watch.Looked);
        Assert.Equal(TaskStatus.WaitingOnYou, watch.Tasks.Board.Tasks.Single().Status);
    }
    // --- a question that only asks for a merge (task b99e5596: the merge mark, not the "?" bubble) ---------------------

    // The opening, the words before the first link, says merge: first, or after "... and" within a few words.
    [Theory]
    [InlineData("Merge [Deskbar #504](" + Url504 + "): CI is green")]
    [InlineData("**Merge** [Deskbar #504](" + Url504 + ")")]
    [InlineData("Review and merge [Deskbar #504](" + Url504 + "), the tailnet policy")]
    [InlineData("Mark it ready and merge " + Url504)]
    [InlineData("merge " + Url504 + " once you have read it")]
    public void A_question_opening_with_merge_and_linking_a_pull_request_asks_a_merge(string question) =>
        Assert.True(AskedPullRequests.AsksMerge(question));

    // A decision first, a deploy, merge said too late or in another sense, or nothing to merge: still a question.
    [Theory]
    [InlineData("Answer in the session: should it stay with the person? Then merge [PR 504](" + Url504 + ")")]
    [InlineData("Redeploy the back office stack in Portainer: [stacks 311](" + Url504 + ") is merged")]
    [InlineData("Decide whether we should merge the two settings pages, see [Deskbar #504](" + Url504 + ")")]
    [InlineData("Merged [Deskbar #504](" + Url504 + "); should I release it?")]
    [InlineData("Merge the branch locally and try it")]
    [InlineData("Merge [the mockup](https://claude.ai/artifact/x) into the plan")]
    [InlineData("Start a session on task 18758102: releases are blocked since [Deskbar #504](" + Url504 + ")")]
    public void Any_other_question_does_not_ask_a_merge(string question) =>
        Assert.False(AskedPullRequests.AsksMerge(question));

    // On a task: only an open question with no answer buttons and no deploy hand-over; the Inbox marks it ReadyToMerge.
    [Fact]
    public void A_task_waiting_only_for_a_merge_wears_the_merge_mark()
    {
        var question = $"Merge [Deskbar #504]({Url504}): CI is green";
        var (board, id) = Waiting(question);
        var task = board.Tasks.Single();
        var withOptions = board.Ask(id, question, Asked, default, ["Merge it", "Not yet"]).Value.Tasks.Single();
        var handOver = board.Ask(id, question, Asked, deploy: true).Value.Tasks.Single();
        var plain = Waiting("Which colour, blue or green?").Board.Tasks.Single();

        Assert.True(AskedPullRequests.AsksMerge(task));
        Assert.Equal(InboxMark.ReadyToMerge, InboxTabs.MarkOf(task, _ => false));
        Assert.Equal(task.WaitingSince.GetValueOrDefault(task.Raised), InboxTabs.SinceOf(task, InboxMark.ReadyToMerge));
        Assert.Equal(InboxMark.Asks, InboxTabs.MarkOf(withOptions, _ => false));
        Assert.NotEqual(InboxMark.ReadyToMerge, InboxTabs.MarkOf(handOver, _ => false));
        Assert.Equal(InboxMark.Asks, InboxTabs.MarkOf(plain, _ => false));
    }

    // Once the merge settles the question the task is no longer waiting, so the mark goes with it.
    [Fact]
    public void A_settled_merge_question_no_longer_asks_a_merge()
    {
        var (board, id) = Waiting($"Merge [Deskbar #504]({Url504})");
        var settled = AskedPullRequests.Settlements(board, new Dictionary<string, PullRequestFacts> { [Key504] = Merged(Asked.AddMinutes(5)) }, Asked.AddMinutes(6));
        var after = board.SettleQuestion(id, settled.Single().Question, settled.Single().Note, Asked.AddMinutes(6), Guid.NewGuid()).Tasks.Single();

        Assert.False(AskedPullRequests.AsksMerge(after));
    }
}
