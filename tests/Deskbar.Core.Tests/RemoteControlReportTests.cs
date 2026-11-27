using System.Text.Json;
using CSharpFunctionalExtensions;
using Deskbar.Core.Control;
using Deskbar.Core.Reporting;
using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests;

// `deskbar.cmd report <text>|-` (task c28ed674): the remote twin of "Report a problem…". The client half (stdin, the
// caller's folder, the trace line) is ControlArguments; the server half signs the text and hands it to the app.
public class RemoteControlReportTests
{
    const string ManagerDir = @"C:\State\manager";

    readonly FakeDesktops desktops = new();
    readonly FakeMonitor monitor = new();
    readonly FakeTitles titles = new();
    readonly FakeStore store = new();
    readonly List<string> filed = [];

    WorkspaceManager Started()
    {
        var manager = new WorkspaceManager(desktops, monitor, titles, store, ownProcessId: 4242);
        Assert.True(manager.Start().IsSuccess);
        return manager;
    }

    // A control whose fileReport records the text it was handed and answers `answer` (an id by default).
    ControlReply Run(Result<string>? answer, bool withFileReport, params string[] args) =>
        new RemoteControl(Started(),
            taskSessions: new TaskSessionCommands(_ => Result.Success(), _ => [], (_, _, _) => Result.Success(), (_, _) => Result.Success())
            {
                ManagerFolder = ManagerDir,
            },
            fileReport: withFileReport ? text => { filed.Add(text); return answer ?? Result.Success("0a1b2c3d-id"); } : null).Execute(args);

    static JsonElement Json(ControlReply reply) => JsonDocument.Parse(reply.Output).RootElement;

    // --- ControlArguments: stdin ---

    [Fact]
    public void Report_dash_is_replaced_by_stdin_with_the_trailing_line_break_trimmed() =>
        Assert.Equal(["report", "it broke\nbadly"], ControlArguments.WithTextInput(["report", "-"], () => "it broke\nbadly\r\n"));

    [Fact]
    public void Report_text_on_the_command_line_never_reads_stdin() =>
        Assert.Equal(["report", "hello"], ControlArguments.WithTextInput(["report", "hello"], () => throw new InvalidOperationException("stdin was read")));

    [Fact]
    public void A_dash_after_from_is_the_folder_not_the_text() =>
        Assert.Equal(["report", "text", "--from", "-"], ControlArguments.WithTextInput(["report", "text", "--from", "-"], () => throw new InvalidOperationException("stdin was read")));

    // --- ControlArguments: the caller's folder ---

    [Fact]
    public void Report_gets_the_callers_folder_appended() =>
        Assert.Equal(["report", "x", "--from", @"C:\work"], ControlArguments.WithCallerFolder(["report", "x"], @"C:\work"));

    // Like `task`: the caller's own --from is kept, but the client's folder comes last and wins.
    [Fact]
    public void Report_gets_the_callers_folder_last_even_when_the_caller_typed_a_from() =>
        Assert.Equal(["report", "x", "--from", @"C:\given", "--from", @"C:\work"], ControlArguments.WithCallerFolder(["report", "x", "--from", @"C:\given"], @"C:\work"));

    // --- ControlArguments: the trace line ---

    [Fact]
    public void Report_text_is_traced_as_its_length_and_keeps_the_folder() =>
        Assert.Equal(@"report (9 characters) --from C:\f", ControlArguments.ForTrace(["report", "my secret", "--from", @"C:\f"]));

    // Review of task c28ed674: a text that starts with `--` (a pasted CLI error) is the user's words all the same.
    [Fact]
    public void Report_text_that_starts_with_dashes_is_traced_as_its_length_too() =>
        Assert.Equal(@"report (17 characters) --from C:\f", ControlArguments.ForTrace(["report", "--foo is not good", "--from", @"C:\f"]));

    // --- the server half ---

    [Fact]
    public void A_report_from_another_folder_carries_the_command_line_note_and_answers_the_id()
    {
        var reply = Run(null, true, "report", "  the bar vanished  ", "--from", @"C:\elsewhere");

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Equal("0a1b2c3d-id", Json(reply).GetProperty("report").GetString());
        Assert.Equal([$"the bar vanished\n\n{RemoteControl.CommandReportNote}"], filed);
    }

    [Fact]
    public void A_report_from_the_manager_folder_carries_the_manager_note()
    {
        var reply = Run(null, true, "report", "the bar vanished", "--from", ManagerDir);

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Equal([$"the bar vanished\n\n{RemoteControl.ManagerReportNote}"], filed);
    }

    [Fact]
    public void A_failed_filing_is_the_replys_failure() =>
        Assert.Equal(ControlReply.Failed, Run(Result.Failure<string>("window gone"), true, "report", "x", "--from", @"C:\f").ExitCode);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_text_is_refused_and_nothing_is_filed(string text)
    {
        var reply = Run(null, true, "report", text, "--from", @"C:\f");

        Assert.Equal(ControlReply.Failed, reply.ExitCode);
        Assert.Empty(filed);
    }

    [Fact]
    public void Text_over_the_limit_with_its_note_is_refused_before_filing()
    {
        // Exactly at the limit before the note: the note pushes it over.
        var reply = Run(null, true, "report", new string('a', ReportLimits.TextChars), "--from", @"C:\f");

        Assert.Equal(ControlReply.Failed, reply.ExitCode);
        Assert.Empty(filed);
    }

    [Fact]
    public void Without_a_way_to_file_a_report_is_refused()
    {
        var reply = Run(null, false, "report", "x", "--from", @"C:\f");

        Assert.Equal(ControlReply.Failed, reply.ExitCode);
        Assert.Empty(filed);
    }

    [Fact]
    public void Report_is_listed_in_the_usage() =>
        Assert.Contains(ControlUsage.All, c => c.Verb == "report");
}
