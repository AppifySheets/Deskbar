using System.Text;
using Deskbar.Core.Control;

namespace Deskbar.Windows.Control;

// The console half of the remote control: what the Store package's `deskbar` alias runs.
//
// Deskbar.exe is a GUI exe, and PowerShell does not wait for one, so a caller loses its output and exit
// code (see ControlCommandLine and its deskbar.cmd shim). A package cannot ship a .cmd behind an app
// execution alias, so the alias points at a small CONSOLE exe (Deskbar.Cli) whose whole job is this
// method. The asking itself is ControlClient's; the sender is a parameter so a test needs no pipe.
//
// Contract, the same as the shim's: print nothing but the reply (stdout on success, stderr otherwise)
// and return the reply's exit code. The arguments are forwarded as they are, with no `ctl` verb, because
// the alias already means "ctl". The one addition is the shim's too: `open` and `attention` are told the
// caller's folder (ControlArguments.WithCallerFolder), because in the Store build the deskbar.cmd shim
// points at this exe, so a Claude Code session's `open` and its hook's `attention` arrive here.
// `callerFolder` is a parameter so a test can name one; left null it is this process's current folder.
//
// Two commands read stdin (ControlArguments.WithInput): `claude-hook` (Claude Code's hooks, plan Task 7), whose stdin is
// the hook's JSON, sent on as --json, and a `task` form that takes free text, given `-` in the text's place (review of
// PR B, I-1: free text never goes through cmd's command line). A hook's reply is shaped by ControlArguments.HookReply
// (Deskbar not running is exit 0 and silence), and an empty answer prints nothing at all, not an empty line, because
// what it prints is context the session reads. `stdin` is a parameter so a test can hand the text in; left null it is
// this process's own stdin, read as UTF-8 (ReadStandardInput).
public static class ControlConsole
{
    public static int Run(
        IReadOnlyList<string> args,
        string pipeName,
        Func<string, IReadOnlyList<string>, Task<ControlReply>> send,
        TextWriter stdout,
        TextWriter stderr,
        string? callerFolder = null,
        TextReader? stdin = null)
    {
        var forwarded = ControlArguments.WithInput(
            ControlArguments.WithCallerFolder(args, callerFolder ?? Environment.CurrentDirectory),
            () => stdin?.ReadToEnd() ?? ReadStandardInput());
        // Waited for synchronously on a pool thread: a console Main has no sync context to deadlock,
        // but the Task.Run keeps that true if a caller ever has one.
        var reply = ControlArguments.HookReply(forwarded, Task.Run(() => send(pipeName, forwarded)).GetAwaiter().GetResult());
        if (!Silent(forwarded, reply))
            (reply.ExitCode == ControlReply.Ok ? stdout : stderr).WriteLine(reply.Output);
        return reply.ExitCode;
    }

    // A hook's empty answer (Notification, Stop, SessionEnd, or Deskbar not running): nothing at all is printed, so the
    // session's context gains no blank line. Every other command prints its reply even when it is empty, as before.
    public static bool Silent(IReadOnlyList<string> forwarded, ControlReply reply) =>
        ControlArguments.IsClaudeHook(forwarded) && reply.Output.Length == 0;

    // This process's stdin as UTF-8, the way Claude Code writes a hook's JSON. Not Console.In: that decodes with the
    // console's input code page (the OEM one, 437 or 850), which would garble any non-ASCII prompt or folder name.
    // Used by the GUI exe's `ctl` too (ControlCommandLine), whose stdin is the pipe the hook script inherited.
    public static string ReadStandardInput()
    {
        using var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return reader.ReadToEnd();
    }
}
