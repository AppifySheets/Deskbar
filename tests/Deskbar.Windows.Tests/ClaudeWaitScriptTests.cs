using System.Diagnostics;
using System.IO;
using Deskbar.App;

namespace Deskbar.Windows.Tests;

// The waiting Stop hook's script, claude-wait-hook.cmd (ClaudeSkill.WaitScript; a comment wakes its session, plan
// docs/superpowers/plans/2026-11-24-comment-wakes-session.md, Task 4), run for real by cmd in a scratch folder beside a
// stub deskbar.cmd that answers as `claude-wait` would. Claude Code wakes the session when the script exits 2, with its
// stderr as the message; exit 0 wakes nothing.
public sealed class ClaudeWaitScriptTests : IDisposable
{
    readonly string folder = Directory.CreateTempSubdirectory("deskbar-wait-script-").FullName;

    string Inbox => Path.Combine(folder, "s1.md");

    string Gen => Path.Combine(folder, "s1.gen");

    public void Dispose() => Directory.Delete(folder, recursive: true);

    // The stub shim prints the three lines claude-wait answers (or nothing), and the script is written beside it.
    string Prepared(bool answers = true)
    {
        File.WriteAllText(Path.Combine(folder, "deskbar.cmd"), answers ? $"@echo {Inbox}\r\n@echo {Gen}\r\n@echo 123\r\n" : "@rem Deskbar is not running\r\n");
        var script = Path.Combine(folder, ClaudeSkill.WaitHookScriptName);
        File.WriteAllText(script, ClaudeSkill.WaitScript());
        return script;
    }

    // Runs the script as Claude Code would, its hook JSON on stdin, until it exits or `limit` passes.
    static (int ExitCode, string Stderr) Ran(string script, TimeSpan limit, Action? meanwhile = null)
    {
        var start = new ProcessStartInfo("cmd.exe", $"/d /c \"\"{script}\"\"")
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(start)!;
        process.StandardInput.Write("""{"session_id":"s1","hook_event_name":"Stop"}""");
        process.StandardInput.Close();
        var stderr = process.StandardError.ReadToEndAsync();
        meanwhile?.Invoke();
        if (!process.WaitForExit(limit))
        {
            process.Kill(entireProcessTree: true);
            return (-1, "still waiting");
        }
        return (process.ExitCode, stderr.Result);
    }

    [Fact]
    public void A_waiting_comment_wakes_the_session_once()
    {
        var script = Prepared();
        File.WriteAllText(Gen, "123");
        File.WriteAllText(Inbox, "Deskbar: the user commented on your task\n\nlook at this\n");

        var (exitCode, stderr) = Ran(script, TimeSpan.FromSeconds(20));

        Assert.Equal(2, exitCode);
        Assert.Contains("look at this", stderr);
        Assert.False(File.Exists(Inbox));
        Assert.Empty(Directory.GetFiles(folder, "s1.md.*"));
    }

    [Fact]
    public void A_comment_that_arrives_while_it_waits_wakes_the_session()
    {
        var script = Prepared();
        File.WriteAllText(Gen, "123");

        var (exitCode, stderr) = Ran(script, TimeSpan.FromSeconds(20), () =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(3));
            File.WriteAllText(Inbox, "later comment\n");
        });

        Assert.Equal(2, exitCode);
        Assert.Contains("later comment", stderr);
    }

    // A newer waiter (a later Stop) wrote its own generation: this one leaves the inbox to it and ends quietly.
    [Fact]
    public void A_stale_waiter_leaves_the_inbox_and_ends()
    {
        var script = Prepared();
        File.WriteAllText(Gen, "999");
        File.WriteAllText(Inbox, "for the newer waiter\n");

        var (exitCode, _) = Ran(script, TimeSpan.FromSeconds(20));

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(Inbox));
    }

    // Deskbar not running (or a headless session): claude-wait answers nothing, and the script ends at once.
    [Fact]
    public void No_answer_means_no_waiter()
    {
        var (exitCode, _) = Ran(Prepared(answers: false), TimeSpan.FromSeconds(20));

        Assert.Equal(0, exitCode);
    }

    // The session ended (SessionEnd took the gen file away): the waiter ends too.
    [Fact]
    public void A_forgotten_session_ends_its_waiter()
    {
        var (exitCode, _) = Ran(Prepared(), TimeSpan.FromSeconds(20));

        Assert.Equal(0, exitCode);
    }
}
