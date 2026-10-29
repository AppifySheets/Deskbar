using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CSharpFunctionalExtensions;
using Deskbar.Windows.Processes;

namespace Deskbar.Windows.Shell;

// RESTART THE TASKBAR, which means restarting the Explorer process that owns it. Petre: "add restart explorer in
// the right click menu of the bar", "which restarts the taskbar". On 2026-10-27 Explorer crashed and came back
// with four of his ten pins, and the usual cure for a taskbar that misbehaves is this restart, done by hand from
// Task Manager.
//
// Shut down through Restart Manager, the documented API Task Manager's own "Restart" on Windows Explorer uses: it
// asks Explorer to end its session the way a sign-out would, so the shell saves its state and leaves cleanly, and
// it only forces the process when Explorer does not answer. Killing the process instead (taskkill /f) is what a
// crash looks like, and a crash is what lost the pins.
//
// Only the process that owns the taskbar (Shell_TrayWnd). File Explorer windows opened in their own process
// (Explorer's /factory process) are left alone.
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
public static class TaskbarRestart
{
    // How long a new taskbar is given to appear on its own after the old Explorer has gone. Winlogon restarted the
    // crashed shell on 2026-10-27 within a few seconds of it stopping (Winlogon event 1002 at 12:06:43, the
    // taskbar back by 12:06:49), so five seconds covers that case without a long blank screen when nothing comes.
    static readonly TimeSpan ComeBackWithin = TimeSpan.FromSeconds(5);
    static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);

    public static Result<string> Restart() =>
        ShellProcessId().ToResult("No taskbar is running, so there is no Explorer to restart.")
            .Bind(ShutDown)
            .Bind(stopped => WaitForShell(stopped) is { } back
                ? Result.Success($"explorer {stopped} shut down, and Windows brought the taskbar back as {back}")
                : DetachedLauncher.Start("explorer.exe", [], null)
                    .Map(started => $"explorer {stopped} shut down, nothing came back in {ComeBackWithin.TotalSeconds:0}s, started explorer.exe ({started?.ToString() ?? "pid unknown"})")
                    .MapError(e => $"Explorer was shut down but could not be started again: {e}. Start explorer.exe from Task Manager (Run new task)."));

    // The process that owns the taskbar, or none while there is no taskbar.
    static Maybe<int> ShellProcessId()
    {
        var tray = FindWindowW("Shell_TrayWnd", null);
        if (tray == 0) return Maybe<int>.None;
        GetWindowThreadProcessId(tray, out var processId);
        return processId == 0 ? Maybe<int>.None : (int)processId;
    }

    // Polls for a taskbar owned by any process other than the one just shut down, which may still own a dying
    // window for a moment.
    static int? WaitForShell(int stopped)
    {
        var until = DateTime.UtcNow + ComeBackWithin;
        while (DateTime.UtcNow < until)
        {
            var back = ShellProcessId().Where(id => id != stopped);
            if (back.HasValue) return back.Value;
            Thread.Sleep(Poll);
        }
        return null;
    }

    // One Restart Manager session holding the one process. RmShutdown returns once the process has gone (or failed
    // to go), so nothing has to be polled for here.
    static Result<int> ShutDown(int processId) =>
        Result.Try(() =>
        {
            using var process = Process.GetProcessById(processId);
            // RM_UNIQUE_PROCESS names a process by id AND start time, so a recycled id cannot stand in for it.
            var start = process.StartTime.ToFileTime();
            return new UniqueProcess { ProcessId = processId, StartLow = (uint)start, StartHigh = (uint)(start >> 32) };
        }, e => $"Could not read the Explorer process: {e.Message}")
        .Bind(target =>
        {
            var key = new StringBuilder(SessionKeyLength + 1);
            var started = RmStartSession(out var session, 0, key);
            if (started != 0) return Result.Failure<int>($"Restart Manager would not start a session (error {started}).");
            try
            {
                var registered = RmRegisterResources(session, 0, null, 1, [target], 0, null);
                if (registered != 0) return Result.Failure<int>($"Restart Manager would not take the Explorer process (error {registered}).");
                var shut = RmShutdown(session, ForceShutdown, 0);
                return shut == 0 ? processId : Result.Failure<int>($"Explorer did not shut down (error {shut}).");
            }
            finally
            {
                RmEndSession(session);
            }
        });

    const int SessionKeyLength = 32; // CCH_RM_SESSION_KEY: a GUID in hex
    const uint ForceShutdown = 1;     // RmForceShutdown: end the process if it does not answer the request

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
