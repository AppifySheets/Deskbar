using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests.Tasks;

// The manager session's pure rules (design: docs/superpowers/specs/2026-11-25-manager-session-design.md): which step a
// click takes, which folder is the manager's, and the exact claude arguments measured in the probe notes.
public class ManagerSessionTests
{
    static readonly DateOnly today = new(2026, 11, 25);
    const string Id = "aaaaaaaa-1111-2222-3333-444444444444";
    const string Shim = "C:/Users/me/AppData/Roaming/Deskbar/deskbar.cmd";

    static ClaudeAgent Agent(string id, bool busy = false, string folder = @"C:\state\manager") =>
        new(id, id[..8], folder, true, busy, ManagerSession.Name);

    static Maybe<ManagerRecord> Stored(DateOnly day) => new ManagerRecord(Id, day);

    const string Other = "bbbbbbbb-1111-2222-3333-444444444444";
    const string Third = "cccccccc-1111-2222-3333-444444444444";

    // The manager folder, as ManagerActions tells it (ManagerSession.IsManager on the agent's folder).
    static bool InFolder(ClaudeAgent agent) => ManagerSession.IsManager(agent.Folder, @"C:\state\manager", Long);

    static string Long(string path) => path.Replace(@"PETRE~1.CHI", "petre.chitashvili");

    [Fact]
    public void Next_attaches_todays_listed_manager()
    {
        var agent = Agent(Id);
        var step = ManagerSession.Next(Stored(today), today, [agent], _ => true, InFolder);
        Assert.Equal(new ManagerStep.Attach(agent), step);
    }

    [Fact]
    public void Next_resumes_todays_unlisted_manager_with_transcript() =>
        Assert.Equal(new ManagerStep.Resume(Id), ManagerSession.Next(Stored(today), today, [], _ => true, InFolder));

    [Fact]
    public void Next_starts_new_when_todays_transcript_is_gone() =>
        Assert.Equal(new ManagerStep.StartNew(Maybe<ClaudeAgent>.None), ManagerSession.Next(Stored(today), today, [], _ => false, InFolder));

    [Fact]
    public void Next_starts_new_and_stops_yesterdays_idle_manager()
    {
        var agent = Agent(Id);
        var step = ManagerSession.Next(Stored(today.AddDays(-1)), today, [agent], _ => true, InFolder);
        Assert.Equal(new ManagerStep.StartNew(agent), step);
    }

    [Fact]
    public void Next_attaches_yesterdays_busy_manager()
    {
        var agent = Agent(Id, busy: true);
        var step = ManagerSession.Next(Stored(today.AddDays(-1)), today, [agent], _ => true, InFolder);
        Assert.Equal(new ManagerStep.Attach(agent), step);
    }

    [Fact]
    public void Next_starts_new_when_yesterdays_manager_is_not_listed() =>
        Assert.Equal(new ManagerStep.StartNew(Maybe<ClaudeAgent>.None),
            ManagerSession.Next(Stored(today.AddDays(-1)), today, [], _ => true, InFolder));

    // A session elsewhere is not the manager, so it is no reason not to start one.
    [Fact]
    public void Next_starts_new_when_nothing_stored() =>
        Assert.Equal(new ManagerStep.StartNew(Maybe<ClaudeAgent>.None),
            ManagerSession.Next(Maybe<ManagerRecord>.None, today, [Agent(Id, folder: @"C:\repos\x")], _ => true, InFolder));

    // THE FOLDER IS THE MANAGER'S IDENTITY (task review, Important). A start whose session was not listed in time stores
    // nothing; the session still runs in the manager folder. The next click must take it as today's manager, never
    // start a second Fable beside it that nothing would ever stop.
    [Fact]
    public void Next_claims_a_manager_in_the_folder_when_nothing_is_stored()
    {
        var orphan = Agent(Other);
        Assert.Equal(new ManagerStep.Claim(orphan), ManagerSession.Next(Maybe<ManagerRecord>.None, today, [orphan], _ => true, InFolder));
    }

    // The stored manager is gone from the list, but another session runs in its folder: that one is claimed, not the
    // stored one resumed beside it (nor a new one started when its transcript is gone).
    [Fact]
    public void Next_claims_another_manager_in_the_folder_when_the_stored_one_is_not_listed()
    {
        var orphan = Agent(Other);
        Assert.Equal(new ManagerStep.Claim(orphan), ManagerSession.Next(Stored(today), today, [orphan], _ => true, InFolder));
        Assert.Equal(new ManagerStep.Claim(orphan), ManagerSession.Next(Stored(today), today, [orphan], _ => false, InFolder));
        Assert.Equal(new ManagerStep.Claim(orphan), ManagerSession.Next(Stored(today.AddDays(-1)), today, [orphan], _ => true, InFolder));
    }

    // Several in the folder: the busy one first (it is the one at work), else the newest by its start time.
    [Fact]
    public void Next_claims_the_busy_one_first_else_the_newest()
    {
        var older = Agent(Other) with { StartedAt = DateTimeOffset.UnixEpoch.AddDays(1) };
        var newer = Agent(Third) with { StartedAt = DateTimeOffset.UnixEpoch.AddDays(2) };
        var busy = Agent(Other, busy: true) with { StartedAt = DateTimeOffset.UnixEpoch.AddDays(1) };

        Assert.Equal(new ManagerStep.Claim(newer), ManagerSession.Next(Maybe<ManagerRecord>.None, today, [older, newer], _ => true, InFolder));
        Assert.Equal(new ManagerStep.Claim(busy), ManagerSession.Next(Maybe<ManagerRecord>.None, today, [busy, newer], _ => true, InFolder));
    }

    // Only a background session is a manager Deskbar started. The trust terminal of the first start runs an interactive
    // `claude` in the manager folder; it is not claimed, since no window can be attached to it.
    [Fact]
    public void Next_does_not_claim_an_interactive_session_in_the_folder()
    {
        var trustTerminal = Agent(Other) with { Background = false, ShortId = Maybe<string>.None };
        Assert.Equal(new ManagerStep.StartNew(Maybe<ClaudeAgent>.None),
            ManagerSession.Next(Maybe<ManagerRecord>.None, today, [trustTerminal], _ => true, InFolder));
    }

    // While the stored manager is listed, the existing outcomes stand whatever else is in the folder.
    [Fact]
    public void Next_keeps_the_stored_managers_outcome_while_it_is_listed()
    {
        var stored = Agent(Id);
        var orphan = Agent(Other);
        Assert.Equal(new ManagerStep.Attach(stored), ManagerSession.Next(Stored(today), today, [orphan, stored], _ => true, InFolder));
        Assert.Equal(new ManagerStep.StartNew(stored), ManagerSession.Next(Stored(today.AddDays(-1)), today, [orphan, stored], _ => true, InFolder));
    }

    [Fact]
    public void IsManager_matches_short_path_form() =>
        Assert.True(ManagerSession.IsManager(@"C:\Users\PETRE~1.CHI\x", @"C:\Users\petre.chitashvili\x", Long));

    [Fact]
    public void IsManager_is_true_for_a_subfolder_but_not_a_sibling_with_the_same_prefix()
    {
        Assert.True(ManagerSession.IsManager(@"C:\state\manager\.claude", @"C:\state\manager", Long));
        Assert.True(ManagerSession.IsManager("C:/STATE/manager/sub/", @"C:\state\manager\", Long));
        Assert.False(ManagerSession.IsManager(@"C:\state\manager2", @"C:\state\manager", Long));
        Assert.False(ManagerSession.IsManager(@"C:\state\manager2\sub", @"C:\state\manager", Long));
    }

    [Fact]
    public void IsManager_is_false_for_null_and_empty()
    {
        Assert.False(ManagerSession.IsManager(null, @"C:\state\manager", Long));
        Assert.False(ManagerSession.IsManager("", @"C:\state\manager", Long));
        Assert.False(ManagerSession.IsManager(@"C:\state\manager", "", Long));
    }

    [Fact]
    public void Others_drops_only_sessions_in_the_manager_folder()
    {
        var manager = Agent("bbbbbbbb-0000-0000-0000-000000000000");
        var other = Agent("cccccccc-0000-0000-0000-000000000000", folder: @"C:\repos\x");
        Assert.Equal([other], ManagerSession.Others([manager, other], @"C:\state\manager", Long));
    }

    [Fact]
    public void FolderIn_is_the_manager_folder_under_the_state_directory() =>
        Assert.Equal(Path.Combine(@"C:\state", "manager"), ManagerSession.FolderIn(@"C:\state"));

    [Fact]
    public void StartArguments_run_fable_in_the_background_with_the_briefing()
    {
        var args = ManagerSession.StartArguments(@"C:\state\manager-briefing.md", @"C:\Users\me\AppData\Roaming\Deskbar\deskbar.cmd").ToList();

        Assert.Equal("--bg", args[0]);
        Assert.Equal("fable", args[args.IndexOf("--model") + 1]);
        Assert.Contains("--strict-mcp-config", args);
        Assert.Equal("dontAsk", args[args.IndexOf("--permission-mode") + 1]);
        Assert.Equal(@"C:\state\manager-briefing.md", args[args.IndexOf("--append-system-prompt-file") + 1]);
        // The allow rule carries the shim's path with forward slashes, the one form measured to match.
        Assert.Equal($"Bash({Shim} task:*)", args[args.IndexOf("--allowedTools") + 1]);
        // PowerShell is disallowed too: a Bash rule does not cover it.
        Assert.Contains("PowerShell", args);
        Assert.Equal("--", args[^2]);
        Assert.Equal(ManagerSession.FirstMessage, args[^1]);
    }

    // Final review, item 2: every tool the spec and the briefing rule out is in --disallowedTools, between the flag and
    // the `--` that ends it: file, web, agent and timer tools, PowerShell, worktrees, artifacts and messages to sessions.
    [Fact]
    public void StartArguments_disallow_every_tool_the_manager_must_not_use()
    {
        var args = ManagerSession.StartArguments("b.md", Shim).ToList();
        var from = args.IndexOf("--disallowedTools") + 1;
        var disallowed = args.Skip(from).Take(args.IndexOf("--") - from).ToList();

        Assert.Equal(
            ["Read", "Edit", "Write", "NotebookEdit", "Agent", "WebFetch", "WebSearch", "Monitor", "PowerShell",
             "ScheduleWakeup", "CronCreate", "CronDelete", "Workflow", "EnterWorktree", "Artifact", "RemoteTrigger", "SendMessage"],
            disallowed);
    }

    [Fact]
    public void ResumeArguments_are_bare_because_flags_on_a_resume_fork_a_copy() =>
        Assert.Equal(["--bg", "--resume", Id], ManagerSession.ResumeArguments(Id));

    [Fact]
    public void Briefing_names_the_refused_verbs_and_the_quote_marker()
    {
        var text = ManagerSession.Briefing(Shim);

        Assert.Contains($"{Shim} task overview", text);
        Assert.Contains($"{Shim} task show", text);
        Assert.Contains(ManagerSession.QuoteMarker, text);
        Assert.Contains("close", text);
        Assert.Contains("delete", text);
        Assert.Contains(ManagerSession.Refusal, text);
        Assert.Contains(ManagerSession.CommentRefusal, text);
        Assert.Contains("Never use Monitor", text);
        // Final review, item 6: the other verbs are named as allowed, `<shim> task` prints them, and free text goes on
        // stdin with `-`, never quoted on the command line.
        Assert.Contains("start, flag, order, move, assign, add, rename, describe, and a subtask by add --parent", text);
        Assert.Contains($"{Shim} task prints them", text);
        Assert.Contains("Free text always goes on stdin with - in its place, never quoted on the command line.", text);
        Assert.True(text.Split('\n').Length < 40);
    }

    // Task 61e4d143: the manager's own question. The briefing names both verbs, and keeps the manager from relaying a
    // task's question, which already reaches the user (the 40 line cap is asserted above).
    [Fact]
    public void Briefing_names_the_managers_own_ask_and_answered()
    {
        var text = ManagerSession.Briefing(Shim);

        Assert.Contains($"{Shim} task ask -", text);
        Assert.Contains($"{Shim} task answered", text);
        Assert.Contains("Never relay a task's own question", text);
    }

    // Step 2 (docs/superpowers/specs/2026-11-25-manager-event-wakes-design.md): the briefing says what to do with a wake,
    // whose message ManagerWakes starts with "Deskbar wake." and marks a repeat hand-back AGAIN.
    [Fact]
    public void Briefing_says_what_a_Deskbar_wake_asks_for()
    {
        var text = ManagerSession.Briefing(Shim);

        Assert.Contains("\"Deskbar wake.\"", text);
        Assert.Contains($"{Shim} task comment <id> -) to wake its session", text);
        Assert.Contains("A task marked AGAIN gets no comment", text);
        Assert.Contains("oldest and most blocking first, each with your suggested answer", text);
        Assert.DoesNotContain("Nothing runs on a timer", text);
    }
}
