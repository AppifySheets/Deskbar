using System.Diagnostics;
using System.Runtime.InteropServices;
using Deskbar.Core.Control;
using Deskbar.Core.Tasks;
using Deskbar.Windows.Control;

namespace Deskbar.App;

// `Deskbar.exe deskbar://task/<id>`: what Windows runs when a task link is clicked (task 5016d533: "create a link to
// the task and include it in github pr / issues / elsewhere so that when i click on it, it opens the task in deskbar",
// "so that i can easily go back to my task").
//
// The link reaches this exe as its argument, from the deskbar: protocol registered for it (TaskLinkProtocol for the
// direct build, the package manifest's windows.protocol for the Store one; https://deskbar.app/task#<id> in a pull
// request hands on to that protocol in the browser, see TaskLink). Like `ctl`, this process is a CLIENT of the running
// copy and never touches WPF: it sends `task open <id>`, the twin of a line menu's "Open in window", over the control
// pipe and ends. The running copy opens the task in a window of its own and brings it onto the desktop in front
// (TasksBoard.OpenWindow), so clicking a link from any workspace shows the task where the user is.
//
// EVERY deskbar: ARGUMENT ENDS HERE, a valid task link or not (review of the change, finding 1). Anything the protocol
// hands over that is not exactly one task link is refused with a message, and never falls through to starting the app:
// a stray argument would reach OnStartup's own switches (--after-crash, --await-previous) when a caller passed a quote
// unencoded, and with Deskbar running it would only show "already running".
//
// When no Deskbar answers, it is started once, and the link is sent again until it listens, so a link clicked before
// Deskbar is up still opens its task. Whether a Deskbar is up or starting is read from its single-instance mutex, which
// only the app takes, never a client like this one or a hook's `ctl` (finding 2: counting Deskbar* processes counted
// two link clients each waiting for the other). Starting is serialised between link clients by a mutex of their own,
// so two quick clicks start one Deskbar.
static class TaskLinkCommandLine
{
    public static bool Wants(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0].StartsWith(TaskLink.Scheme + ":", StringComparison.OrdinalIgnoreCase);

    // The app's single-instance mutex (App.OnStartup): held from the first moments of a start until the app exits.
    const string AppMutex = @"Local\Deskbar.SingleInstance";

    // Held by the one link client that decides whether to start Deskbar, until the started copy holds AppMutex.
    const string StartingMutex = @"Local\Deskbar.TaskLinkStart";

    // How long a Deskbar that was down or starting is given to open its task: the state loads first, and on a busy
    // machine after sign-in that has taken several seconds.
    static readonly TimeSpan StartWait = TimeSpan.FromSeconds(30);
    static readonly TimeSpan Retry = TimeSpan.FromMilliseconds(500);

    public static int Run(IReadOnlyList<string> args)
    {
        // A click has no console to print to, so whatever goes wrong is said in a message box.
        if (args.Count != 1 || TaskLink.TaskIdOf(args[0]) is not { HasValue: true } found)
            return Tell($"This is not a link to a Deskbar task:\n\n{string.Join(" ", args)}", ControlReply.Failed);
        var id = found.Value;
        string[] open = ["task", "open", id];

        // The shell started this process from a click, so it may bring a window forward; the window to bring forward
        // belongs to the running Deskbar, another process. Passing the right on lets that window come to the front
        // rather than flash in the taskbar.
        AllowSetForegroundWindow(AsfwAny);

        var reply = Send(open);
        if (Settled(reply)) return Result(id, reply);

        // Nobody answered, or a Deskbar answered before its tasks were ready (finding 3: the pipe listens a little
        // before the task board exists during a start). Start one if none is up or starting, then ask until it opens
        // the task or the wait runs out.
        if (reply.ExitCode == ControlReply.NotRunning) StartDeskbarUnlessUp();
        var deadline = Stopwatch.StartNew();
        while (!Settled(reply) && deadline.Elapsed < StartWait)
        {
            Thread.Sleep(Retry);
            reply = Send(open);
        }
        return Result(id, reply);
    }

    // A reply worth no second try: the task opened, or a refusal that waiting will not change (an unknown task). Not
    // settled: no Deskbar listening yet, or one listening whose task windows are not wired yet.
    static bool Settled(ControlReply reply) =>
        reply.ExitCode != ControlReply.NotRunning
        && !(reply.ExitCode != ControlReply.Ok && reply.Output.Contains(RemoteControl.TaskWindowUnavailable, StringComparison.Ordinal));

    static int Result(string id, ControlReply reply) =>
        reply.ExitCode == ControlReply.Ok
            ? reply.ExitCode
            : Tell(reply.ExitCode == ControlReply.NotRunning
                ? $"Deskbar did not answer, so task {id[..8]} could not be opened. Start Deskbar and click the link again."
                : $"Deskbar could not open task {id[..8]}.\n\n{reply.Output}", reply.ExitCode);

    static int Tell(string text, int exitCode)
    {
        MessageBoxW(IntPtr.Zero, text, "Deskbar", MbOk | MbIconInformation | MbSetForeground);
        return exitCode;
    }

    static ControlReply Send(IReadOnlyList<string> args) =>
        Task.Run(() => ControlClient.RunAsync(ControlCommandLine.PipeName, args)).GetAwaiter().GetResult();

    // Starts the same exe, bare, as Start or the Run value would, unless a Deskbar already holds its single-instance
    // mutex (up, or starting: an updater handover included, where a third copy is how the desktop heap incident began).
    // One link client at a time decides, and it keeps the decision until the copy it started holds the mutex, so the
    // next click finds that copy rather than starting another.
    static void StartDeskbarUnlessUp()
    {
        using var starting = new Mutex(initiallyOwned: false, StartingMutex);
        var owned = false;
        try
        {
            try
            {
                owned = starting.WaitOne(StartWait);
            }
            catch (AbandonedMutexException)
            {
                owned = true; // a link client that died while deciding; the decision is this one's now
            }
            if (!owned || AppIsUp() || Environment.ProcessPath is not { } exe) return;
            using var _ = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
            var wait = Stopwatch.StartNew();
            while (!AppIsUp() && wait.Elapsed < StartWait) Thread.Sleep(Retry);
        }
        // A start that fails is left to the wait and the message.
        catch (Exception fault) when (fault is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        finally
        {
            if (owned) starting.ReleaseMutex();
        }
    }

    static bool AppIsUp()
    {
        if (!Mutex.TryOpenExisting(AppMutex, out var held)) return false;
        held.Dispose();
        return true;
    }

    const int AsfwAny = -1;
    const uint MbOk = 0x0, MbIconInformation = 0x40, MbSetForeground = 0x10000;

    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
