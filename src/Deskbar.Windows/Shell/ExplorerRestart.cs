using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CSharpFunctionalExtensions;
using Deskbar.Windows.Processes;

namespace Deskbar.Windows.Shell;

// RESTART EXPLORER, all of it: the process that owns the taskbar and the ones holding File Explorer windows. Petre:
// "add restart explorer in the right click menu of the bar", "which restarts the taskbar", then "i want to restart
// explorer as well", "so that the broken taskbar gets fixed". On 2026-10-27 Explorer crashed and came back with
// four of his ten pins, and the usual cure for a taskbar that misbehaves is this restart, done by hand from Task
// Manager. Every Explorer process goes, because the folder windows' process (/factory) talks to the shell and a
// half-restarted Explorer is not the clean start that fixes things.
//
// Shut down through Restart Manager, the documented API Task Manager's own "Restart" on Windows Explorer uses: it
// asks each process to end its session the way a sign-out would, so the shell saves its state and leaves cleanly,
// and it forces a process only when it does not answer. Killing the processes instead (taskkill /f) is what a crash
// looks like, and a crash is what lost the pins.
//
// The folder windows close with their process. Reading them beforehand and opening them again is the caller's job
// (ExplorerFolders, FloatingBar.RestartExplorer), because putting each back in its workspace is Deskbar's business,
// not the shell's.
//
// Brought back by whoever comes first. Winlogon restarts a shell that stopped unexpectedly, and whether it counts
// a Restart Manager shutdown as unexpected has not been measured on this machine. So this waits a few seconds for
// a taskbar to appear on its own, and starts explorer.exe only when none has. Starting it while another shell is
// already up would not replace the taskbar; it would open a File Explorer window. RmRestart is not used: it starts
// Explorer as Deskbar's child, which under the Store copy would run inside Deskbar's package, while
// DetachedLauncher breaks it away (store packaging design,
// docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md).
//
// Blocking for up to several seconds while Explorer shuts down, so callers run it off the UI thread. The answer
// says which way the shell came back, for the trace, so the first live restart settles the Winlogon question.
public static class ExplorerRestart
{
    // How long a new taskbar is given to appear on its own after the old Explorer has gone. Winlogon restarted the
    // crashed shell on 2026-10-27 within a few seconds of it stopping (Winlogon event 1002 at 12:06:43, the
    // taskbar back by 12:06:49), so five seconds covers that case without a long blank screen when nothing comes.
    static readonly TimeSpan ComeBackWithin = TimeSpan.FromSeconds(5);
    // ...and how long an explorer.exe Deskbar started itself is given to put its taskbar up.
    static readonly TimeSpan StartedWithin = TimeSpan.FromSeconds(20);
    static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);

    public static Result<string> Restart() =>
        ExplorerProcesses().ToResult("No Explorer is running in this session, so there is nothing to restart.")
            .Bind(ShutDown)
            .Bind(stopped => WaitForShell(stopped, ComeBackWithin) is { } back
                ? Result.Success($"explorer [{string.Join(", ", stopped)}] shut down, and Windows brought the taskbar back as {back}")
                : DetachedLauncher.Start("explorer.exe", [], null)
                    .MapError(e => $"Explorer was shut down but could not be started again: {e}. Start explorer.exe from Task Manager (Run new task).")
                    // Returned only once the taskbar is up, because the caller opens folders next, and an
                    // explorer.exe given a folder while no shell runs would become the shell itself.
                    .Bind(_ => WaitForShell(stopped, StartedWithin) is { } up
                        ? Result.Success($"explorer [{string.Join(", ", stopped)}] shut down, nothing came back in {ComeBackWithin.TotalSeconds:0}s, so Deskbar started explorer.exe, and the taskbar is {up}")
                        : Result.Failure<string>($"Explorer was shut down and started again, but no taskbar appeared within {StartedWithin.TotalSeconds:0}s. Start explorer.exe from Task Manager (Run new task).")));

    // Every explorer.exe in THIS session: another user signed in on the same machine has an Explorer of their own,
    // which is none of this user's business and which Restart Manager would refuse anyway.
    static Maybe<IReadOnlyList<UniqueProcess>> ExplorerProcesses()
    {
        var session = Process.GetCurrentProcess().SessionId;
        var found = Process.GetProcessesByName("explorer")
            .Select(process =>
            {
                using (process)
                {
                    try
                    {
                        if (process.SessionId != session) return (UniqueProcess?)null;
                        // RM_UNIQUE_PROCESS names a process by id AND start time, so a recycled id cannot stand in for it.
                        var start = process.StartTime.ToFileTime();
                        return new UniqueProcess { ProcessId = process.Id, StartLow = (uint)start, StartHigh = (uint)(start >> 32) };
                    }
                    catch (Exception)
                    {
                        // Gone between the listing and the question, or not ours to read.
                        return null;
                    }
                }
            })
            .OfType<UniqueProcess>()
            .ToList();
        return found.Count > 0 ? found : Maybe<IReadOnlyList<UniqueProcess>>.None;
    }

    // The process that owns the taskbar, or none while there is no taskbar.
    static Maybe<int> ShellProcessId()
    {
        var tray = FindWindowW("Shell_TrayWnd", null);
        if (tray == 0) return Maybe<int>.None;
        GetWindowThreadProcessId(tray, out var processId);
        return processId == 0 ? Maybe<int>.None : (int)processId;
    }

    // Polls for a taskbar owned by a process that was not just shut down, since a dying process may still own its
    // old window for a moment.
    static int? WaitForShell(IReadOnlyList<int> stopped, TimeSpan within)
    {
        var until = DateTime.UtcNow + within;
        while (DateTime.UtcNow < until)
        {
            var back = ShellProcessId().Where(id => !stopped.Contains(id));
            if (back.HasValue) return back.Value;
            Thread.Sleep(Poll);
        }
        return null;
    }

    // One Restart Manager session holding every Explorer process. RmShutdown returns once they have gone (or failed
    // to go), so nothing has to be polled for here.
    static Result<IReadOnlyList<int>> ShutDown(IReadOnlyList<UniqueProcess> targets)
    {
        var key = new StringBuilder(SessionKeyLength + 1);
        var started = RmStartSession(out var session, 0, key);
        if (started != 0) return Result.Failure<IReadOnlyList<int>>($"Restart Manager would not start a session (error {started}).");
        try
        {
            var registered = RmRegisterResources(session, 0, null, (uint)targets.Count, targets.ToArray(), 0, null);
            if (registered != 0) return Result.Failure<IReadOnlyList<int>>($"Restart Manager would not take the Explorer processes (error {registered}).");
            var shut = RmShutdown(session, ForceShutdown, 0);
            return shut == 0
                ? targets.Select(t => t.ProcessId).ToList()
                : Result.Failure<IReadOnlyList<int>>($"Explorer did not shut down (error {shut}).");
        }
        finally
        {
            RmEndSession(session);
        }
    }

    const int SessionKeyLength = 32; // CCH_RM_SESSION_KEY: a GUID in hex
    const uint ForceShutdown = 1;     // RmForceShutdown: end a process that does not answer the request

    // RM_UNIQUE_PROCESS: a DWORD then a FILETIME, which is two DWORDs and so 4-byte aligned. Spelt as two uints
    // because a long would be padded to offset 8 and the native side reads it at 4.
    [StructLayout(LayoutKind.Sequential)]
    struct UniqueProcess
    {
        public int ProcessId;
        public uint StartLow, StartHigh;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] static extern int RmStartSession(out uint session, int flags, StringBuilder sessionKey);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] static extern int RmRegisterResources(uint session, uint fileCount, string[]? files, uint processCount, UniqueProcess[] processes, uint serviceCount, string[]? services);
    [DllImport("rstrtmgr.dll")] static extern int RmShutdown(uint session, uint flags, nint progress);
    [DllImport("rstrtmgr.dll")] static extern int RmEndSession(uint session);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint FindWindowW(string className, string? windowName);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
