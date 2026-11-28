using System.Diagnostics;
using System.IO;
using System.Text;
using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.Windows.Claude;

// ASKS CLAUDE CODE WHICH SKILLS A FOLDER HAS (task 3a872301; ClaudeSkills in Core has the design and the measurements).
// Runs `claude -p` in the folder with ClaudeSkills.ProbeArguments, reads its output a line at a time until the init
// line, takes its "skills" and "slash_commands" arrays (ClaudeInit; task b3fb4e8c says why both), and ends the process
// and anything it started. That line comes about 3 s after the start and before any model call, and the run points the
// API at a closed port (ClaudeSkills.NoModel) as well, so a probe that was not ended in time fails at its first request
// instead of spending anything.
//
// Not ClaudeCli.Run: that waits for the program to exit and hands back all of its output, and this one must stop
// reading, and stop the program, at one line. The environment is cleaned the same way (ClaudeCli.StrippedVariables
// says why), and claude.exe is found the same way (ClaudeLocator).
//
// Hooks: a `-p` run is headless (CLAUDE_CODE_SESSION_ATTENDED=0), so Deskbar's own SessionStart hook answers nothing
// and records nothing for it (ClaudeHookEvent.IsHeadless). The user's other SessionStart hooks do run, as they would
// for any `claude -p`; nothing else does, because the run ends before the prompt is submitted.
public static class ClaudeSkillProbe
{
    /// <summary>
    /// The skills and commands Claude Code has in <paramref name="folder"/>, or why they could not be read (claude missing, the
    /// folder gone, no init line within <paramref name="timeout"/>). Never throws for those.
    /// </summary>
    public static Task<Result<ClaudeInit>> Skills(string folder, TimeSpan timeout) =>
        !Directory.Exists(folder)
            ? Task.FromResult(Result.Failure<ClaudeInit>($"The folder {folder} no longer exists."))
            : ClaudeLocator.Find() is { HasValue: true } program
                ? Run(program.Value, folder, timeout)
                : Task.FromResult(Result.Failure<ClaudeInit>(ClaudeLocator.NotFound));

    static async Task<Result<ClaudeInit>> Run(string program, string folder, TimeSpan timeout)
    {
        var info = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            // Claude Code writes UTF-8 (ClaudeCli says what the console's code page did to it).
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = folder,
        };
        ClaudeSkills.ProbeArguments.ToList().ForEach(info.ArgumentList.Add);
        ClaudeCli.StrippedVariables.ToList().ForEach(name => info.Environment.Remove(name));
        info.Environment["ANTHROPIC_BASE_URL"] = ClaudeSkills.NoModel;

        try
        {
            using var process = Process.Start(info);
            if (process is null) return Result.Failure<ClaudeInit>("Could not start claude.");
            process.StandardInput.Close();
            // Stderr is drained so a full pipe cannot stall the program; what it says is kept for a failure.
            var errors = process.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                var found = await InitLine(process.StandardOutput, cts.Token);
                return found.ToResult(errors.IsCompleted && (await errors).Trim() is { Length: > 0 } said
                    ? $"Claude Code did not list its skills: {said}"
                    : "Claude Code ended without listing its skills.");
            }
            catch (OperationCanceledException)
            {
                return Result.Failure<ClaudeInit>($"Claude Code did not list its skills within {timeout.TotalSeconds:0} s.");
            }
            finally
            {
                End(process);
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return Result.Failure<ClaudeInit>($"Could not run claude: {e.Message}");
        }
    }

    // Reads lines until one is the init line (its skills) or the output ends (none).
    static async Task<Maybe<ClaudeInit>> InitLine(StreamReader output, CancellationToken token)
    {
        while (await output.ReadLineAsync(token) is { } line)
            if (ClaudeSkills.FromInit(line) is { HasValue: true } init) return init;
        return Maybe<ClaudeInit>.None;
    }

    // The probe and whatever it started (its hooks' shells) are ended once the line is read, or on a failure. Killing a
    // tree can throw when the program has exited meanwhile or a child cannot be ended; neither changes the answer.
    static void End(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is AggregateException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }
}
