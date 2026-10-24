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
public static class ControlConsole
{
    public static int Run(
        IReadOnlyList<string> args,
        string pipeName,
        Func<string, IReadOnlyList<string>, Task<ControlReply>> send,
        TextWriter stdout,
        TextWriter stderr,
        string? callerFolder = null)
    {
        var forwarded = ControlArguments.WithCallerFolder(args, callerFolder ?? Environment.CurrentDirectory);
        // Waited for synchronously on a pool thread: a console Main has no sync context to deadlock,
        // but the Task.Run keeps that true if a caller ever has one.
        var reply = Task.Run(() => send(pipeName, forwarded)).GetAwaiter().GetResult();
        (reply.ExitCode == ControlReply.Ok ? stdout : stderr).WriteLine(reply.Output);
        return reply.ExitCode;
    }
}
