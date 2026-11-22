using System.IO;
using Deskbar.Core.Control;
using Deskbar.Windows.Control;

namespace Deskbar.Windows.Tests.Control;

// The console exe behind the Store package's `deskbar` alias: it must behave like the deskbar.cmd shim,
// that is print only the reply (stdout on success, stderr otherwise) and exit with the reply's code.
public class ControlConsoleTests
{
    [Fact]
    public void An_ok_reply_goes_to_stdout_with_exit_zero()
    {
        var (stdout, stderr) = (new StringWriter(), new StringWriter());

        var code = ControlConsole.Run(["windows"], "pipe", (_, _) => Task.FromResult(new ControlReply(ControlReply.Ok, "[]")), stdout, stderr);

        Assert.Equal(0, code);
        Assert.Equal("[]" + Environment.NewLine, stdout.ToString());
        Assert.Equal("", stderr.ToString());
    }

    [Fact]
    public void A_failure_goes_to_stderr_with_its_own_code()
    {
        var (stdout, stderr) = (new StringWriter(), new StringWriter());

        var code = ControlConsole.Run(["nope"], "pipe", (_, _) => Task.FromResult(ControlReply.Fail("unknown command")), stdout, stderr);

        Assert.Equal(ControlReply.Failed, code);
        Assert.Contains("unknown command", stderr.ToString());
        Assert.Equal("", stdout.ToString());
    }

    [Fact]
    public void Other_exit_codes_pass_through_untouched()
    {
        var (stdout, stderr) = (new StringWriter(), new StringWriter());

        var code = ControlConsole.Run(["move"], "pipe", (_, _) => Task.FromResult(new ControlReply(ControlReply.NothingMatched, "no match")), stdout, stderr);

        Assert.Equal(ControlReply.NothingMatched, code);
        Assert.Contains("no match", stderr.ToString());
    }

    [Fact]
    public void The_arguments_and_pipe_name_reach_the_sender_as_they_are()
    {
        string? seenPipe = null;
        IReadOnlyList<string>? seenArgs = null;

        ControlConsole.Run(["move", "--title", "a b"], "the-pipe", (pipe, args) =>
        {
            (seenPipe, seenArgs) = (pipe, args);
            return Task.FromResult(new ControlReply(ControlReply.Ok, ""));
        }, new StringWriter(), new StringWriter());

        Assert.Equal("the-pipe", seenPipe);
        Assert.Equal(["move", "--title", "a b"], seenArgs);
    }

    // Review of PR B, I-1: deskbar-cli (the Store build's `deskbar`) puts its stdin in place of a text form's `-` too.
    [Fact]
    public void A_task_text_form_takes_its_text_from_stdin()
    {
        IReadOnlyList<string>? seenArgs = null;
        const string text = "set \"a > pwned.txt\" & echo INJECTED";

        ControlConsole.Run(["task", "ask", "-", "--session", "s1"], "pipe", (_, args) =>
        {
            seenArgs = args;
            return Task.FromResult(new ControlReply(ControlReply.Ok, "{}"));
        }, new StringWriter(), new StringWriter(), callerFolder: @"C:\work\repo", stdin: new StringReader(text + "\n"));

        Assert.Equal(["task", "ask", text, "--session", "s1", "--from", @"C:\work\repo"], seenArgs);
    }

    // `claude-hook` is run by Claude Code's hooks with the hook's JSON on stdin. The client sends it on as `--json`, after
    // the caller's folder.
    [Fact]
    public void Claude_hook_sends_its_stdin_as_json()
    {
        IReadOnlyList<string>? seenArgs = null;

        var code = ControlConsole.Run(["claude-hook", "--entry", "claude-vscode"], "pipe", (_, args) =>
        {
            seenArgs = args;
            return Task.FromResult(new ControlReply(ControlReply.Ok, ""));
        }, new StringWriter(), new StringWriter(), callerFolder: @"C:\work\repo", stdin: new StringReader("""{"hook_event_name":"Stop"}"""));

        Assert.Equal(0, code);
        Assert.Equal(["claude-hook", "--entry", "claude-vscode", "--from", @"C:\work\repo", "--json", """{"hook_event_name":"Stop"}"""], seenArgs);
    }

    // Deskbar down: the session reads nothing and the hook exits 0 (a Stop hook exiting 2 tells Claude to keep going).
    [Fact]
    public void Claude_hook_with_deskbar_down_prints_nothing_and_exits_0()
    {
        var (stdout, stderr) = (new StringWriter(), new StringWriter());

        var code = ControlConsole.Run(["claude-hook"], "pipe",
            (_, _) => Task.FromResult(new ControlReply(ControlReply.NotRunning, "Deskbar is not running")),
            stdout, stderr, stdin: new StringReader("{}"));

        Assert.Equal(0, code);
        Assert.Equal("", stdout.ToString());
        Assert.Equal("", stderr.ToString());
    }

    // An empty answer (Notification, Stop, SessionEnd) prints nothing at all, not an empty line, so the session's
    // context gains nothing; a briefing is printed as it came.
    [Fact]
    public void Claude_hook_prints_its_answer_and_nothing_for_an_empty_one()
    {
        var (quiet, spoken) = (new StringWriter(), new StringWriter());

        ControlConsole.Run(["claude-hook"], "pipe", (_, _) => Task.FromResult(new ControlReply(ControlReply.Ok, "")),
            quiet, new StringWriter(), stdin: new StringReader("{}"));
        ControlConsole.Run(["claude-hook"], "pipe", (_, _) => Task.FromResult(new ControlReply(ControlReply.Ok, "Your task is X.")),
            spoken, new StringWriter(), stdin: new StringReader("{}"));

        Assert.Equal("", quiet.ToString());
        Assert.Equal("Your task is X." + Environment.NewLine, spoken.ToString());
    }

    // The Store build's shim points at this exe, so `open` and `attention` must carry the caller's folder
    // here exactly as ControlCommandLine adds it for `Deskbar.exe ctl`.
    [Theory]
    [InlineData("open")]
    [InlineData("attention")]
    public void Open_and_attention_carry_the_callers_folder(string command)
    {
        IReadOnlyList<string>? seenArgs = null;

        ControlConsole.Run([command], "pipe", (_, args) =>
        {
            seenArgs = args;
            return Task.FromResult(new ControlReply(ControlReply.Ok, ""));
        }, new StringWriter(), new StringWriter(), callerFolder: @"C:\work\repo");

        Assert.Equal([command, "--from", @"C:\work\repo"], seenArgs);
    }
}
