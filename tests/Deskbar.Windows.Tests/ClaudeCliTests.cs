using System.IO;
using CSharpFunctionalExtensions;
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

    // Task 0bdf74d0: a session whose git worktree was removed showed only Windows' "The directory name is invalid."
    // The folder is checked first, and the failure names it in plain words; nothing is started.
    [Fact]
    public async Task A_folder_that_is_gone_fails_with_its_name_before_anything_runs()
    {
        var gone = Path.Combine(Path.GetTempPath(), $"deskbar-gone-{Guid.NewGuid():N}");

        var result = await Cmd.Run(["/c", "echo hi"], gone, Generous);

        Assert.True(result.IsFailure);
        Assert.Contains(gone, result.Error);
        Assert.Contains("no longer exists", result.Error);
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
        // Every Claude variable the child must not see (final review, item 10, added the two a background session's
        // shell carries), set on this process for the length of the test and put back after.
        string[] names = ["CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SESSION_ATTENDED", "CLAUDE_JOB_DIR", "CLAUDE_CODE_CHILD_SESSION"];
        var before = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        names.ToList().ForEach(name => Environment.SetEnvironmentVariable(name, "1"));
        try
        {
            // `set CLAUDE` lists every variable starting with CLAUDE; a clean child has none, and cmd then
            // exits 1 with "Environment variable CLAUDE not defined", so the text is read from either side.
            var result = await Cmd.Run(["/c", "set CLAUDE"], null, Generous);

            var text = result.IsSuccess ? result.Value : result.Error;
            Assert.All(names, name => Assert.DoesNotContain(name + "=", text));
        }
        finally
        {
            before.ToList().ForEach(pair => Environment.SetEnvironmentVariable(pair.Key, pair.Value));
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

    // Task dbe25323: what the locator answers is a claude.exe that exists, or nothing; never a bare name for Windows to
    // fail on. A CI runner has no Claude Code, a dev machine does, so both answers are allowed.
    [Fact]
    public void The_located_claude_is_an_existing_exe_or_nothing() =>
        ClaudeLocator.Find().Tap(path =>
        {
            Assert.True(File.Exists(path), path);
            Assert.Equal("claude.exe", Path.GetFileName(path), ignoreCase: true);
        });
}
