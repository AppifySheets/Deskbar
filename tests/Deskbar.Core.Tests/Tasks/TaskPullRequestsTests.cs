using CSharpFunctionalExtensions;
using Deskbar.Core.Control;
using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests.Tasks;

// A task keeps the pull requests its sessions opened, and the tasks panel's search finds the task by the number (task
// e7aa49b1; TaskPullRequests.cs). Pure parts first (what counts as opening one, how the search box names one), then the
// board, the file, the hook's JSON and the pipe end to end.
public sealed class TaskPullRequestsTests : IDisposable
{
    static readonly DateTimeOffset clock = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);

    const string Url523 = "https://github.com/AppifySheets/Deskbar/pull/523";

    readonly string dir = Path.Combine(Path.GetTempPath(), "deskbar-prs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    static TaskSession Session(string id) => new(id, @"C:\repos\TaskSpaces", clock, Maybe<DateTimeOffset>.None);

    static PullRequestLink Pr(int number, string repo = "Deskbar") => new("AppifySheets", repo, number);

    static (TaskBoard Board, TaskItem Task) Made(string title = "Tasks search")
    {
        var made = TaskBoard.Empty.Create(title, Maybe<Guid>.None, Session("s1"), clock, Guid.NewGuid()).Value;
        return (made.Board, made.Task);
    }

    // --- what counts as opening one --------------------------------------------------------------

    // gh prints the new pull request's URL on its own line; that is the link, whatever else the output says.
    [Theory]
    [InlineData("gh pr create --title x --body-file b.md")]
    [InlineData("cd E:/repos/x && gh pr create --fill")]
    [InlineData("git push -u origin b; gh pr create --draft")]
    [InlineData("$body | gh pr create --body-file -")]
    public void A_gh_pr_create_yields_the_link_it_printed(string command) =>
        Assert.Equal([Pr(523)], TaskPullRequests.Created(command, $"Creating pull request for b into main\n\n{Url523}\n"));

    // Any other command, even one whose output has a pull request link, adds nothing: the hook's filter is best effort.
    [Theory]
    [InlineData("gh pr view 523 --json url")]
    [InlineData("echo gh pr create")]
    [InlineData("gh pr list")]
    public void Any_other_command_yields_nothing(string command) =>
        Assert.Empty(TaskPullRequests.Created(command, Url523));

    [Fact]
    public void No_command_yields_nothing() =>
        Assert.Empty(TaskPullRequests.Created(Maybe<string>.None, Url523));

    // --- how the search box names one ------------------------------------------------------------

    [Theory]
    [InlineData("523", 523, null)]
    [InlineData("#523", 523, null)]
    [InlineData("PR 523", 523, null)]
    [InlineData("pr#523", 523, null)]
    [InlineData("pr523", 523, null)]
    [InlineData("pull 523", 523, null)]
    [InlineData("Deskbar #523", 523, "Deskbar")]
    [InlineData("Deskbar 523", 523, "Deskbar")]
    [InlineData("1234", 1234, null)]
    [InlineData(Url523, 523, "Deskbar")]
    [InlineData(Url523 + "/files", 523, "Deskbar")]
    public void The_search_reads_a_pull_request(string text, int number, string? repo)
    {
        var query = TaskPullRequests.Query(text);
        Assert.True(query.HasValue, text);
        Assert.Equal(number, query.Value.Number);
        Assert.Equal(repo, query.Value.Repo.GetValueOrDefault());
    }

    [Theory]
    [InlineData("store tier")]
    [InlineData("v2.6")]
    [InlineData("")]
    [InlineData("fix 523 now")]
    public void Other_text_is_no_pull_request(string text) =>
        Assert.True(TaskPullRequests.Query(text).HasNoValue, text);

    // "Deskbar #523" is 523 in Deskbar only; "#523" is 523 in any repository.
    [Fact]
    public void A_named_repository_must_match()
    {
        Assert.True(TaskPullRequests.Query("Deskbar #523").Value.Matches(Pr(523)));
        Assert.False(TaskPullRequests.Query("Other #523").Value.Matches(Pr(523)));
        Assert.True(TaskPullRequests.Query("#523").Value.Matches(Pr(523, "Other")));
        Assert.False(TaskPullRequests.Query("#52").Value.Matches(Pr(523)));
    }

    // --- the board and the search ------------------------------------------------------------------

    // The same pull request again, in another spelling, returns the same board: nothing to save.
    [Fact]
    public void A_pull_request_is_added_once()
    {
        var (board, task) = Made();
        var once = board.AddPullRequest(task.Id, Pr(523), clock).Value;
        var twice = once.AddPullRequest(task.Id, new PullRequestLink("appifysheets", "deskbar", 523), clock.AddMinutes(1)).Value;

        Assert.Equal([Pr(523)], once.Tasks.Single().PullRequests);
        Assert.Same(once, twice);
    }

    // Opening a pull request asks nothing of Petre: status and raised time stay.
    [Fact]
    public void Adding_one_moves_nothing()
    {
        var (board, task) = Made();
        var after = board.AddPullRequest(task.Id, Pr(523), clock.AddHours(1)).Value.Tasks.Single();

        Assert.Equal(task.Status, after.Status);
        Assert.Equal(task.Raised, after.Raised);
        Assert.Equal(clock.AddHours(1), after.UpdatedAt);
    }

    // The whole point: a closed task is found by its pull request's number, as by its id, past every filter.
    [Fact]
    public void The_search_finds_a_closed_task_by_its_pull_request()
    {
        var (board, task) = Made();
        var other = Made("Something else");
        var closed = board.AddPullRequest(task.Id, Pr(523), clock).Value.Close(task.Id, clock, stoppingClaude: true).Value;
        var both = new TaskBoard([.. closed.Tasks, .. other.Board.Tasks]);

        foreach (var text in new[] { "523", "#523", "PR 523", "Deskbar #523", Url523 })
        {
            var found = new TaskSearch(text).Found(both);
            Assert.True(found.HasValue, text);
            Assert.Equal([task.Id], found.Value.Tasks.Select(t => t.Id));
        }
        Assert.True(new TaskSearch("524").Found(both).HasNoValue);
    }

    // Tasks from before this existed link their pull requests in the thread, and the search finds those too.
    [Fact]
    public void The_search_finds_a_pull_request_linked_in_the_thread()
    {
        var (board, task) = Made();
        var commented = board.Comment(task.Id, "s1", $"Opened [Deskbar #523]({Url523}).", clock, Guid.NewGuid()).Value;

        Assert.True(new TaskSearch("#523").Found(commented).HasValue);
        Assert.Equal([Pr(523)], TaskPullRequests.Of(commented.Tasks.Single()));
    }

    // --- the file ------------------------------------------------------------------------------------

    [Fact]
    public void The_pull_requests_survive_a_save_and_a_load()
    {
        var (board, task) = Made();
        var saved = board.AddPullRequest(task.Id, Pr(523), clock).Value.AddPullRequest(task.Id, Pr(530), clock).Value;
        var store = new JsonTaskStore(dir);

        Assert.True(store.Save(saved).IsSuccess);
        Assert.Contains("\"pullRequests\"", File.ReadAllText(Path.Combine(dir, "tasks.json")));
        Assert.Equal([Pr(523), Pr(530)], store.Load().Value.Tasks.Single().PullRequests);
    }

    // A task without any writes no field at all, so files stay as they were.
    [Fact]
    public void No_pull_requests_write_no_field()
    {
        var (board, _) = Made();
        Assert.True(new JsonTaskStore(dir).Save(board).IsSuccess);
        Assert.DoesNotContain("pullRequests", File.ReadAllText(Path.Combine(dir, "tasks.json")));
    }

    // A hand edit of the wrong type reads as none, and an entry that is not a link is dropped; the file still loads.
    [Theory]
    [InlineData("\"pullRequests\": 5", 0)]
    [InlineData("\"pullRequests\": [5, \"nonsense\", \"https://github.com/AppifySheets/Deskbar/pull/523\"]", 1)]
    public void An_odd_field_never_fails_the_file(string field, int expected)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "tasks.json"), $$"""
            {"version":2,"tasks":[{"id":"6e4dc116-25fd-4f3a-9c1e-0d2b3a4c5d6e","title":"t","status":"InProgress",
             "created":"2026-11-25T10:00:00Z","updatedAt":"2026-11-25T10:00:00Z",{{field}}}]}
            """);

        var loaded = new JsonTaskStore(dir).Load();
        Assert.True(loaded.IsSuccess, loaded.IsFailure ? loaded.Error : "");
        Assert.Equal(expected, loaded.Value.Tasks.Single().PullRequests.Count);
    }

    // Reported deploys (task 8470d176, phase 2; TaskItem.Deployed) are kept like the pull requests themselves.
    [Fact]
    public void The_deploy_reports_survive_a_save_and_a_load()
    {
        var (board, task) = Made();
        var at = clock.AddHours(2);
        var saved = board.AddPullRequest(task.Id, Pr(537, "iam.ge"), clock).Value.MarkDeployed(task.Id, "2026.11.25.39-3749d21 in the portal logs", at).Value;
        var store = new JsonTaskStore(dir);

        Assert.True(store.Save(saved).IsSuccess);
        Assert.Contains("\"deployed\"", File.ReadAllText(Path.Combine(dir, "tasks.json")));
        Assert.Equal([new DeployedPullRequest(Pr(537, "iam.ge").Key, at, "2026.11.25.39-3749d21 in the portal logs")],
            store.Load().Value.Tasks.Single().Deployed);
    }

    [Fact]
    public void No_deploy_reports_write_no_field()
    {
        var (board, task) = Made();
        Assert.True(new JsonTaskStore(dir).Save(board.AddPullRequest(task.Id, Pr(523), clock).Value).IsSuccess);
        Assert.DoesNotContain("deployed", File.ReadAllText(Path.Combine(dir, "tasks.json")));
    }

    // A hand edit of the wrong type reads as none, and an entry missing a part is dropped; the file still loads.
    [Theory]
    [InlineData("\"deployed\": \"yes\"", 0)]
    [InlineData("\"deployed\": [5, {\"key\":\"appifysheets/iam.ge#537\"}, {\"key\":\"AppifySheets/iam.ge#537\",\"at\":\"2026-11-25T12:00:00Z\",\"note\":\"v1 in the logs\"}]", 1)]
    public void An_odd_deployed_field_never_fails_the_file(string field, int expected)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "tasks.json"), $$"""
            {"version":2,"tasks":[{"id":"6e4dc116-25fd-4f3a-9c1e-0d2b3a4c5d6e","title":"t","status":"Done",
             "created":"2026-11-25T10:00:00Z","updatedAt":"2026-11-25T10:00:00Z",{{field}}}]}
            """);

        var loaded = new JsonTaskStore(dir).Load();
        Assert.True(loaded.IsSuccess, loaded.IsFailure ? loaded.Error : "");
        var deployed = loaded.Value.Tasks.Single().Deployed;
        Assert.Equal(expected, deployed.Count);
        // The key is read in lower case, as PullRequestLink.Key makes it.
        Assert.All(deployed, d => Assert.Equal("appifysheets/iam.ge#537", d.Key));
    }

    // --- the hook's JSON ------------------------------------------------------------------------------

    // The shape measured on 2026-11-25 (Claude Code 2.1.332): tool_input.command and tool_response.stdout.
    static string PostToolUse(string session, string command, string stdout) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            session_id = session,
            cwd = @"C:\repos\TaskSpaces",
            hook_event_name = "PostToolUse",
            tool_name = "Bash",
            tool_input = new { command, description = "Open the PR" },
            tool_response = new { stdout, stderr = "", interrupted = false, isImage = false },
        });

    [Fact]
    public void The_hook_event_carries_the_command_and_its_output()
    {
        var hook = ClaudeHookEvent.Parse(PostToolUse("s1", "gh pr create --fill", Url523 + "\n"), "claude-vscode", "1").Value;

        Assert.Equal("gh pr create --fill", hook.ToolCommand.Value);
        Assert.Contains(Url523, hook.ToolOutput);
    }

    // A failed call's tool_response is the error text itself, a string.
    [Fact]
    public void A_string_response_is_the_output()
    {
        var json = $$"""{"session_id":"s1","hook_event_name":"PostToolUse","tool_input":{"command":"gh pr create"},"tool_response":"a pull request for branch b already exists: {{Url523}}"}""";
        Assert.Contains(Url523, ClaudeHookEvent.Parse(json, "claude-vscode", "1").Value.ToolOutput);
    }

    // --- the pipe, end to end -------------------------------------------------------------------------

    readonly TaskService tasks = new(new MemoryTaskStore(), () => clock);

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

    ControlReply Hooked(WorkspaceManager manager, string json) =>
        Ran(manager, "claude-hook", "--entry", "claude-vscode", "--attended", "1", "--from", @"C:\repos\TaskSpaces", "--json", json);

    // A session's `gh pr create` puts the pull request on its task, and the session reads nothing.
    [Fact]
    public void A_session_that_opens_a_pull_request_puts_it_on_its_task()
    {
        var manager = Started();
        Ran(manager, "task", "new", "Tasks search", "--session", "s1", "--from", @"C:\repos\TaskSpaces");

        var reply = Hooked(manager, PostToolUse("s1", "gh pr create --fill", Url523 + "\n"));

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Equal("", reply.Output);
        Assert.Equal([Pr(523)], tasks.Board.Tasks.Single().PullRequests);
    }

    // Another session's command, or one that is no gh pr create, changes nothing.
    [Fact]
    public void Nothing_else_adds_a_pull_request()
    {
        var manager = Started();
        Ran(manager, "task", "new", "Tasks search", "--session", "s1", "--from", @"C:\repos\TaskSpaces");

        Hooked(manager, PostToolUse("s1", "gh pr view 523", Url523));
        Hooked(manager, PostToolUse("s2", "gh pr create --fill", Url523));

        Assert.Empty(tasks.Board.Tasks.Single().PullRequests);
    }

    // `task pr <url> --session` for one opened another way, and `task pr <id> <url>` by hand; a non-link is refused.
    [Fact]
    public void The_pr_forms_add_one_by_hand()
    {
        var manager = Started();
        Ran(manager, "task", "new", "Tasks search", "--session", "s1", "--from", @"C:\repos\TaskSpaces");
        var id = tasks.Board.Tasks.Single().Id.ToString()[..8];

        Assert.Equal(ControlReply.Ok, Ran(manager, "task", "pr", Url523, "--session", "s1").ExitCode);
        Assert.Equal(ControlReply.Ok, Ran(manager, "task", "pr", id, "https://github.com/AppifySheets/Deskbar/pull/530").ExitCode);
        Assert.NotEqual(ControlReply.Ok, Ran(manager, "task", "pr", "#531", "--session", "s1").ExitCode);
        Assert.NotEqual(ControlReply.Ok, Ran(manager, "task", "pr", Url523).ExitCode);

        Assert.Equal([Pr(523), Pr(530)], tasks.Board.Tasks.Single().PullRequests);
        Assert.Contains(Url523, Ran(manager, "task", "list").Output);
    }

    // `task deployed <note> --session` reports a deploy the session checked (task 8470d176, phase 2), and needs a pull
    // request on its task; without --session the one-word form is refused as a session's.
    [Fact]
    public void A_session_reports_its_deploy()
    {
        var manager = Started();
        Ran(manager, "task", "new", "Portal accounts", "--session", "s1", "--from", @"C:\repos\iam.ge");

        var none = Ran(manager, "task", "deployed", "v1 in the logs", "--session", "s1");
        Assert.NotEqual(ControlReply.Ok, none.ExitCode);
        Assert.Contains(TaskBoard.NoPullRequestToDeploy, none.Output);

        Ran(manager, "task", "pr", "https://github.com/AppifySheets/iam.ge/pull/537", "--session", "s1");
        var reply = Ran(manager, "task", "deployed", "2026.11.25.39-3749d21 in the portal logs", "--session", "s1");

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Contains("\"deployed\"", reply.Output);
        Assert.Equal(["2026.11.25.39-3749d21 in the portal logs"], tasks.Board.Tasks.Single().Deployed.Select(d => d.Note));
        Assert.Contains("portal logs", Ran(manager, "task", "list").Output);

        var refused = Ran(manager, "task", "deployed", "v2 in the logs");
        Assert.NotEqual(ControlReply.Ok, refused.ExitCode);
        Assert.Contains("is a session's form and needs --session", refused.Output);
    }

    // `task deployed <reason> --unknown --session` (task 485ba96b): the session cannot see its deploy and says why; the
    // flag with no reason is refused rather than stored as a deploy note, and `task show` carries the reason.
    [Fact]
    public void A_session_reports_it_cannot_check_its_deploy()
    {
        var manager = Started();
        Ran(manager, "task", "new", "Portal accounts", "--session", "s1", "--from", @"C:\repos\iam.ge");
        Ran(manager, "task", "pr", "https://github.com/AppifySheets/iam.ge/pull/537", "--session", "s1");

        Assert.NotEqual(ControlReply.Ok, Ran(manager, "task", "deployed", "--unknown", "--session", "s1").ExitCode);
        var reply = Ran(manager, "task", "deployed", "no access to the Portainer logs", "--unknown", "--session", "s1");

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Contains("\"deployUnknown\"", reply.Output);
        Assert.Equal("no access to the Portainer logs", tasks.Board.Tasks.Single().DeployUnknown.Value.Reason);
        Assert.Empty(tasks.Board.Tasks.Single().Deployed);

        var refused = Ran(manager, "task", "deployed", "no logs", "--unknown");
        Assert.Contains("is a session's form and needs --session", refused.Output);

        var id = tasks.Board.Tasks.Single().Id.ToString()[..8];
        Assert.Equal(ControlReply.Ok, Ran(manager, "task", "deployed", id, "no health page", "--unknown").ExitCode);
        Assert.Equal("no health page", tasks.Board.Tasks.Single().DeployUnknown.Value.Reason);
    }

    // `task deployed <id> <note>`: the user's or the manager's form, the remote twin of "Mark deployed".
    [Fact]
    public void Anyone_can_report_a_deploy_by_the_task()
    {
        var manager = Started();
        Ran(manager, "task", "new", "Portal accounts", "--session", "s1", "--from", @"C:\repos\iam.ge");
        var id = tasks.Board.Tasks.Single().Id.ToString()[..8];
        Ran(manager, "task", "pr", id, "https://github.com/AppifySheets/iam.ge/pull/537");

        Assert.Equal(ControlReply.Ok, Ran(manager, "task", "deployed", id, TaskService.MarkedByHand).ExitCode);
        Assert.NotEqual(ControlReply.Ok, Ran(manager, "task", "deployed", id, " ").ExitCode);

        Assert.Equal([TaskService.MarkedByHand], tasks.Board.Tasks.Single().Deployed.Select(d => d.Note));
    }

    [Fact]
    public void Help_lists_both_deployed_forms()
    {
        Assert.Contains(ControlUsage.All, c => c.Verb == "task" && c.Arguments.StartsWith("deployed <note>|- --session", StringComparison.Ordinal));
        Assert.Contains(ControlUsage.All, c => c.Verb == "task" && c.Arguments.StartsWith("deployed <id> <note>", StringComparison.Ordinal));
    }

    // The note is free text, so `-` reads it from stdin, and the trace writes its length, never the words.
    [Fact]
    public void The_deploy_note_comes_from_stdin_and_stays_out_of_the_trace()
    {
        Assert.Equal(["task", "deployed", "v1 in the logs", "--session", "s1"],
            ControlArguments.WithTextInput(["task", "deployed", "-", "--session", "s1"], () => "v1 in the logs\n"));
        Assert.Equal(["task", "deployed", "6e4dc116", "v1 in the logs"],
            ControlArguments.WithTextInput(["task", "deployed", "6e4dc116", "-"], () => "v1 in the logs"));
        Assert.Equal("task deployed 6e4dc116 (14 characters)", ControlArguments.ForTrace(["task", "deployed", "6e4dc116", "v1 in the logs"]));
        Assert.Equal("task deployed (14 characters) --session s1", ControlArguments.ForTrace(["task", "deployed", "v1 in the logs", "--session", "s1"]));
    }
}
