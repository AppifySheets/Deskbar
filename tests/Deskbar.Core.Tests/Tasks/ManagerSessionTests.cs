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

    static string Long(string path) => path.Replace(@"PETRE~1.CHI", "petre.chitashvili");

    [Fact]
    public void Next_attaches_todays_listed_manager()
    {
        var agent = Agent(Id);
        var step = ManagerSession.Next(Stored(today), today, [agent], _ => true);
        Assert.Equal(new ManagerStep.Attach(agent), step);
    }

    [Fact]
    public void Next_resumes_todays_unlisted_manager_with_transcript() =>
        Assert.Equal(new ManagerStep.Resume(Id), ManagerSession.Next(Stored(today), today, [], _ => true));

    [Fact]
    public void Next_starts_new_when_todays_transcript_is_gone() =>
        Assert.Equal(new ManagerStep.StartNew(Maybe<ClaudeAgent>.None), ManagerSession.Next(Stored(today), today, [], _ => false));

    [Fact]
    public void Next_starts_new_and_stops_yesterdays_idle_manager()
    {
        var agent = Agent(Id);
        var step = ManagerSession.Next(Stored(today.AddDays(-1)), today, [agent], _ => true);
        Assert.Equal(new ManagerStep.StartNew(agent), step);
    }

    [Fact]
    public void Next_attaches_yesterdays_busy_manager()
    {
        var agent = Agent(Id, busy: true);
        var step = ManagerSession.Next(Stored(today.AddDays(-1)), today, [agent], _ => true);
        Assert.Equal(new ManagerStep.Attach(agent), step);
    }

    [Fact]
    public void Next_starts_new_when_yesterdays_manager_is_not_listed() =>
        Assert.Equal(new ManagerStep.StartNew(Maybe<ClaudeAgent>.None),
            ManagerSession.Next(Stored(today.AddDays(-1)), today, [], _ => true));

    [Fact]
    public void Next_starts_new_when_nothing_stored() =>
        Assert.Equal(new ManagerStep.StartNew(Maybe<ClaudeAgent>.None),
            ManagerSession.Next(Maybe<ManagerRecord>.None, today, [Agent(Id)], _ => true));

    [Fact]
    public void IsManager_matches_short_path_form() =>
        Assert.True(ManagerSession.IsManager(@"C:\Users\PETRE~1.CHI\x", @"C:\Users\petre.chitashvili\x", Long));

    [Fact]
    public void IsManager_is_false_for_a_subfolder_and_for_null()
    {
        Assert.False(ManagerSession.IsManager(@"C:\state\manager\sub", @"C:\state\manager", Long));
        Assert.False(ManagerSession.IsManager(null, @"C:\state\manager", Long));
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
        Assert.Contains("Never use Monitor", text);
        Assert.True(text.Split('\n').Length < 40);
    }
}
