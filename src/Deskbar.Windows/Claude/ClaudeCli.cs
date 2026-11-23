using System.Diagnostics;
using System.IO;
using System.Text;
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
// The environment: the variables below are removed from the child's block. They describe the Claude Code or
// Electron host that started Deskbar (a dev build run from a Claude session carries CLAUDECODE, one run from a
// background session's shell also CLAUDE_JOB_DIR and CLAUDE_CODE_CHILD_SESSION, one run from VS Code's terminal
// ELECTRON_RUN_AS_NODE), and a `claude` that inherits them thinks it is a nested session or runs as a bare Node
// script. LaunchEnvironment.Clean already removes all of them from Deskbar's own process at startup (Ruling 14), so
// in the app this list is a second line of defence; it is kept because ClaudeCli is also used without the app (its
// tests, a probe) and the per-child block is the one place that cannot be undone by something setting a variable
// on the process later.
public sealed class ClaudeCli(string executable = "claude") : IClaudeCli
{
    static readonly IReadOnlyList<string> StrippedVariables =
        ["CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SESSION_ATTENDED", "CLAUDE_JOB_DIR", "CLAUDE_CODE_CHILD_SESSION",
            "ELECTRON_RUN_AS_NODE"];

    public async Task<Result<string>> Run(IReadOnlyList<string> arguments, string? workingDirectory, TimeSpan timeout)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            // Claude Code writes UTF-8. Without these .NET decodes the pipe with the console's ANSI code page, and
            // the "·" of "backgrounded · <id>" arrived as "Â·" (measured on Petre's machine, 2026-11-23: Start then
            // could not find the short id), and non-Latin session names in `agents --json` would be garbled too.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
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

            // Both streams are read at once, so a full pipe on one cannot stall the program while this waits on
            // the other. They are pumped into buffers, not read with ReadToEndAsync, because end-of-file can
            // arrive late: `claude --bg` hands the session to a daemon, and a daemon that inherited our stdout
            // or stderr handle keeps the pipe open for as long as it lives. The pumps may then never finish, so
            // after the program exits the wait for them is bounded (PipeGrace) and whatever was read is used.
            var stdout = new Pump(process.StandardOutput);
            var stderr = new Pump(process.StandardError);

            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Past its time: end it and anything it started, so nothing is left holding the pipes. Killing a tree
                // can itself throw (an AggregateException when a child cannot be ended, InvalidOperationException when
                // the program exited meanwhile, Win32Exception when access is denied); none of that changes the answer,
                // which is that the call timed out, and none of it may escape as an exception (Ruling 13).
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception e) when (e is AggregateException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    return Result.Failure<string>($"{executable} timed out after {timeout.TotalSeconds:0.#} s and could not be ended: {e.Message}");
                }
                return Result.Failure<string>($"{executable} timed out after {timeout.TotalSeconds:0.#} s and was ended.");
            }

            // The program is gone; give the pipes a moment to deliver their last bytes, then stop waiting.
            // The whole call is therefore bounded by timeout + PipeGrace.
            try
            {
                await Task.WhenAll(stdout.Done, stderr.Done).WaitAsync(PipeGrace);
            }
            catch (TimeoutException)
            {
                // A pipe is still held open by something the program started; use what has arrived.
            }

            var output = stdout.Text.TrimEnd();
            var errors = stderr.Text.TrimEnd();
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

    // How long the pipes get to deliver their last bytes after the program has exited.
    static readonly TimeSpan PipeGrace = TimeSpan.FromSeconds(2);

    // Reads a stream chunk by chunk into a buffer, so a partial read is available when the end never comes.
    sealed class Pump
    {
        readonly StringBuilder buffer = new();
        public Task Done { get; }

        public Pump(StreamReader reader) =>
            Done = Task.Run(async () =>
            {
                var chunk = new char[4096];
                int read;
                while ((read = await reader.ReadAsync(chunk, 0, chunk.Length)) > 0)
                    lock (buffer) buffer.Append(chunk, 0, read);
            });

        public string Text
        {
            get { lock (buffer) return buffer.ToString(); }
        }
    }

    // The exit code, then whichever of stdout and stderr have something to say.
    string FailureText(int exitCode, string output, string errors) =>
        string.Join("\n", new[] { $"{executable} exited with code {exitCode}.", output, errors }
            .Where(part => part.Length > 0));
}
