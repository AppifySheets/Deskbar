using System.Diagnostics;
using System.IO;
using System.Text;
using Deskbar.App;

namespace Deskbar.Windows.Tests;

// The waiting Stop hook's script, claude-wait-hook.cmd (ClaudeSkill.WaitScript; a comment wakes its session, plan
// docs/superpowers/plans/2026-11-24-comment-wakes-session.md, Task 4), run for real by cmd in a scratch folder beside a
// stub deskbar.cmd that answers as `claude-wait` does through the real client: ONE line, "<session id> <generation>",
// ending in LF as Console.WriteLine leaves it (the final review's C1 was a script that only read CRLF lines). Claude Code
// wakes the session when the script exits 2, with its stderr as the message; exit 0 wakes nothing.
public sealed class ClaudeWaitScriptTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("deskbar-wait-script-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    // A Deskbar folder (where the shim and the script sit, as in %APPDATA%\Deskbar) with the stub shim and the script.
    // `answer` is the stub's reply, written with LF only; null: it prints nothing, as when Deskbar is not running.
    string Prepared(string folderName, string? answer)
    {
        var folder = Path.Combine(root, folderName);
        Directory.CreateDirectory(Path.Combine(folder, "claude-inbox"));
        File.WriteAllText(Path.Combine(folder, "reply.txt"), answer ?? "", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(folder, "deskbar.cmd"), "@type \"%~dp0reply.txt\"\r\n");
        var script = Path.Combine(folder, ClaudeSkill.WaitHookScriptName);
        File.WriteAllText(script, ClaudeSkill.WaitScript());
        return script;
    }

    static string Inbox(string script, string session) => Path.Combine(Path.GetDirectoryName(script)!, "claude-inbox", session + ".md");

    static string Gen(string script, string session) => Path.Combine(Path.GetDirectoryName(script)!, "claude-inbox", session + ".gen");

    static Process Started(string script)
    {
        var start = new ProcessStartInfo("cmd.exe", $"/d /c \"\"{script}\"\"")
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var process = Process.Start(start)!;
        process.StandardInput.Write("""{"session_id":"s1","hook_event_name":"Stop"}""");
        process.StandardInput.Close();
        return process;
    }

    static (int ExitCode, string Stderr) Finished(Process process, TimeSpan limit)
    {
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(limit))
        {
            process.Kill(entireProcessTree: true);
            return (-1, "still waiting");
        }
        return (process.ExitCode, stderr.Result);
    }

    static (int ExitCode, string Stderr) Ran(string script, TimeSpan limit, Action? meanwhile = null)
    {
        using var process = Started(script);
        var stderr = process.StandardError.ReadToEndAsync();
        meanwhile?.Invoke();
        if (!process.WaitForExit(limit))
        {
            process.Kill(entireProcessTree: true);
            return (-1, "still waiting");
        }
        return (process.ExitCode, stderr.Result);
    }

    static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    [Fact]
    public void A_waiting_comment_wakes_the_session_once()
    {
        var script = Prepared("Deskbar", "s1 123\n");
        File.WriteAllText(Gen(script, "s1"), "123");
        File.WriteAllText(Inbox(script, "s1"), "Deskbar: the user commented on your task\n\nlook at this\n");

        var (exitCode, stderr) = Ran(script, Limit);

        Assert.Equal(2, exitCode);
        Assert.Contains("look at this", stderr);
        Assert.False(File.Exists(Inbox(script, "s1")));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Inbox(script, "s1"))!, "s1.md.*"));
    }

    [Fact]
    public void A_comment_that_arrives_while_it_waits_wakes_the_session()
    {
        var script = Prepared("Deskbar", "s1 123\n");
        File.WriteAllText(Gen(script, "s1"), "123");

        var (exitCode, stderr) = Ran(script, Limit, () =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(3));
            File.WriteAllText(Inbox(script, "s1"), "later comment\n");
        });

        Assert.Equal(2, exitCode);
        Assert.Contains("later comment", stderr);
    }

    // Final review, I2 and M1: a profile folder with a space, a letter outside ASCII and an ampersand still works, because
    // the script never reads a path back from Deskbar: it builds them from its own folder (%~dp0).
    [Fact]
    public void An_awkward_folder_name_still_works()
    {
        var script = Prepared("Dé skbar & co", "s1 123\n");
        File.WriteAllText(Gen(script, "s1"), "123");
        File.WriteAllText(Inbox(script, "s1"), "found it\n");

        var (exitCode, stderr) = Ran(script, Limit);

        Assert.Equal(2, exitCode);
        Assert.Contains("found it", stderr);
    }

    // Final review, I1: two sessions' turns ending in the same second start two waiters at once; each keeps to its own
    // session (they once shared a temporary file named from %RANDOM%, which cmd seeds by the second).
    [Fact]
    public void Two_waiters_started_together_keep_apart()
    {
        var first = Prepared("one", "s1 123\n");
        var second = Prepared("two", "s2 456\n");
        File.WriteAllText(Gen(first, "s1"), "123");
        File.WriteAllText(Gen(second, "s2"), "456");
        File.WriteAllText(Inbox(first, "s1"), "for one\n");
        File.WriteAllText(Inbox(second, "s2"), "for two\n");

        using var a = Started(first);
        using var b = Started(second);
        var (aExit, aErr) = Finished(a, Limit);
        var (bExit, bErr) = Finished(b, Limit);

        Assert.Equal((2, 2), (aExit, bExit));
        Assert.Contains("for one", aErr);
        Assert.Contains("for two", bErr);
    }

    // A newer waiter (a later Stop) wrote its own generation: this one leaves the inbox to it and ends quietly.
    [Fact]
    public void A_stale_waiter_leaves_the_inbox_and_ends()
    {
        var script = Prepared("Deskbar", "s1 123\n");
        File.WriteAllText(Gen(script, "s1"), "999");
        File.WriteAllText(Inbox(script, "s1"), "for the newer waiter\n");

        var (exitCode, _) = Ran(script, Limit);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(Inbox(script, "s1")));
    }

    // Deskbar not running (or a headless session): claude-wait answers nothing, and the script ends at once.
    [Fact]
    public void No_answer_means_no_waiter()
    {
        var (exitCode, _) = Ran(Prepared("Deskbar", null), Limit);

        Assert.Equal(0, exitCode);
    }

    // The session ended (SessionEnd took the gen file away) after it registered: the waiter ends too, leaving the inbox.
    [Fact]
    public void A_forgotten_session_ends_its_waiter()
    {
        var script = Prepared("Deskbar", "s1 123\n");
        File.WriteAllText(Inbox(script, "s1"), "never read\n");

        var (exitCode, _) = Ran(script, Limit);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(Inbox(script, "s1")));
    }
}
