using Deskbar.Windows.Claude;

namespace Deskbar.Windows.Tests;

// ClaudeCli runs a program and reads what it prints. Every test here runs it against cmd.exe, so no Claude is
// ever started (the house rule: no test starts claude, wt or a terminal). cmd.exe is a stand-in with the same
// shape: arguments in, stdout and stderr out, an exit code.
public class ClaudeCliTests
{
    static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    static readonly ClaudeCli Cmd = new("cmd.exe");

    [Fact]
    public async Task Output_of_a_successful_run_is_returned()
    {
        var result = await Cmd.Run(["/c", "echo hi"], null, Generous);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error : "");
        Assert.Equal("hi", result.Value);
    }

    [Fact]
    public async Task A_non_zero_exit_is_a_failure_with_the_output()
    {
        var result = await Cmd.Run(["/c", "echo nope & exit 3"], null, Generous);

        Assert.True(result.IsFailure);
        Assert.Contains("nope", result.Error);
        Assert.Contains("3", result.Error);
    }

    [Fact]
    public async Task The_message_on_stderr_is_carried_in_the_failure()
    {
        // `claude --bg` in an untrusted folder prints its reason on stderr and nothing on stdout (measured).
        var result = await Cmd.Run(["/c", "echo not trusted 1>&2 & exit 1"], null, Generous);

        Assert.True(result.IsFailure);
        Assert.Contains("not trusted", result.Error);
    }

    [Fact]
    public async Task A_run_past_its_timeout_is_killed_and_fails()
    {
        var result = await Cmd.Run(["/c", "ping -n 30 127.0.0.1"], null, TimeSpan.FromSeconds(1));

        Assert.True(result.IsFailure);
        Assert.Contains("timed out", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_Claude_and_Electron_variables_are_not_passed_on()
    {
        var before = Environment.GetEnvironmentVariable("CLAUDECODE");
        Environment.SetEnvironmentVariable("CLAUDECODE", "1");
        try
        {
            // `set CLAUDE` lists every variable starting with CLAUDE; a clean child has none, and cmd then
            // exits 1 with "Environment variable CLAUDE not defined", so the text is read from either side.
            var result = await Cmd.Run(["/c", "set CLAUDE"], null, Generous);

            var text = result.IsSuccess ? result.Value : result.Error;
            Assert.DoesNotContain("CLAUDECODE=", text);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDECODE", before);
        }
    }

    // `claude --bg` hands its session to a daemon; one that inherits our stdout handle keeps the pipe open after
    // claude itself has exited. Here cmd exits at once while a background ping holds the inherited pipe for ~8 s.
    // The call must return near the pipe grace (2 s), not wait for the pipe, and still give what was printed.
    [Fact]
    public async Task A_grandchild_holding_the_pipe_open_does_not_hold_the_call()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = await Cmd.Run(["/c", "start /b ping -n 8 127.0.0.1 & echo hi"], null, TimeSpan.FromSeconds(5));
        clock.Stop();

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error : "");
        Assert.Contains("hi", result.Value);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task A_missing_executable_is_a_failure_not_an_exception()
    {
        var result = await new ClaudeCli("deskbar-no-such-program-xyz").Run(["--version"], null, Generous);

        Assert.True(result.IsFailure);
    }
}
