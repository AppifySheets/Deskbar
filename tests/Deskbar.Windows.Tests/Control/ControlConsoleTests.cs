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
