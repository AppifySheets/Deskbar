using System.Diagnostics;
using System.IO;
using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.Windows.Claude;

// RUNS `claude` AND READS WHAT IT PRINTS. Design: docs/superpowers/specs/2026-11-23-tasks-open-claude-sessions-design.md.
//
// Why this is not ProgramStarter / DetachedLauncher: those start a program and let go of it, with no way to read
// its output. `claude agents --json` is useless unless its stdout is read, and `claude --bg` has to be waited for
// so a failure (an untrusted folder, measured: exit 1, nothing on stdout, the reason on stderr) can be reported.
// Nothing started here outlives the call either: `claude --bg` hands the session to Claude's own daemon and
// returns within a few seconds, so there is no long-lived child to break away from Deskbar.
//
// Under the Store copy (PackageIdentity.IsPackaged) this is a child of the package for those few seconds. That is
// harmless for the same reason, and has not been measured on a packaged install (see
// docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md); the session itself lives in the daemon.
//
// The environment: the four variables below are removed from the child's block. They describe the Claude Code or
// Electron host that started Deskbar (a dev build run from a Claude session carries CLAUDECODE, one run from VS
// Code's terminal carries ELECTRON_RUN_AS_NODE), and a `claude` that inherits them thinks it is a nested session
// or runs as a bare Node script. LaunchEnvironment.Clean strips only the Electron and VSCODE_ ones from Deskbar's
// own process, so the Claude ones are stripped here, per child, on top.
public sealed class ClaudeCli(string executable = "claude") : IClaudeCli
{
    static readonly IReadOnlyList<string> StrippedVariables =
        ["CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SESSION_ATTENDED", "ELECTRON_RUN_AS_NODE"];

    public async Task<Result<string>> Run(IReadOnlyList<string> arguments, string? workingDirectory, TimeSpan timeout)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        arguments.ToList().ForEach(info.ArgumentList.Add);
        if (workingDirectory is not null) info.WorkingDirectory = workingDirectory;
        StrippedVariables.ToList().ForEach(name => info.Environment.Remove(name));

        try
        {
            using var process = Process.Start(info);
            if (process is null) return Result.Failure<string>($"Could not start {executable}.");

            // No input is ever sent: closing stdin now means a program that waits for some gets end-of-file.
            process.StandardInput.Close();

            // Both streams are read at once, to the end, so a full pipe on one cannot stall the program while
            // this waits on the other.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Past its time: end it and anything it started, so nothing is left holding the pipes.
                process.Kill(entireProcessTree: true);
                return Result.Failure<string>($"{executable} timed out after {timeout.TotalSeconds:0.#} s and was ended.");
            }

            var output = (await stdout).TrimEnd();
            var errors = (await stderr).TrimEnd();
            return process.ExitCode == 0
                ? Result.Success(output)
                : Result.Failure<string>(FailureText(process.ExitCode, output, errors));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // A missing program, a folder that does not exist, access denied: expected situations, so a value.
            return Result.Failure<string>($"Could not run {executable}: {e.Message}");
        }
    }

    // The exit code, then whichever of stdout and stderr have something to say.
    string FailureText(int exitCode, string output, string errors) =>
        string.Join("\n", new[] { $"{executable} exited with code {exitCode}.", output, errors }
            .Where(part => part.Length > 0));
}
