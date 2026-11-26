using System.Runtime.CompilerServices;

namespace Deskbar.App;

// THE EXE'S ENTRY POINT, in place of the Main WPF generates from App.xaml (the project's StartupObject names this one).
//
// One reason: `Deskbar.exe ctl <command>` is a CLIENT of the running copy (ControlCommandLine), and it used to be
// answered from App.OnStartup, after WPF had built the whole Application: the App object, App.xaml and its Windows 11
// Fluent theme (ThemeMode="System"), the dispatcher. A client needs none of that, and since Claude Code's hooks run
// `ctl claude-hook` on every prompt (plan Task 7, docs/superpowers/plans/2026-11-22-tasks-board.md), what a `ctl`
// costs to start is now paid in front of every prompt. Measured through Git Bash, 20 runs of `deskbar.cmd version`
// against the running app: the before and after medians are in the commit and the task report; the probe note
// (docs/superpowers/notes/2026-11-22-claude-hook-probes.md) has how the number was taken.
//
// So the command line is looked at first, and a `ctl` never touches App or anything WPF. Everything else is exactly
// what the generated Main did: make the App, load App.xaml, run. The exit code is Run's, which is what Shutdown(code)
// set; returning it keeps `ctl`'s exit codes and an early Shutdown's alike.
//
// The app's half lives in RunApp, kept out of Main by NoInlining (review of Task 7, M-1). The JIT compiles a method
// whole, and compiling a Main that names App made the runtime load PresentationFramework, System.Xaml and WindowsBase
// for a `ctl` that never runs those lines (measured: they were in the client's module list). With the App lines in a
// method of their own, nothing WPF is loaded until RunApp is first called, which a `ctl` never does.
//
// RunApp also catches the one start failure worth living through: Windows refusing a window (RefusedWindow,
// RefusedStart; task c1e6c1ae). The next copy is started, and waits until windows can be made. And before its first
// window it sees to it that every processor has window class names to give (ClassNameSlots; task c5e9b17f), which is
// what those refusals turned out to be about.
static class Program
{
    [STAThread]
    // A task link (deskbar://task/<id>, task 5016d533) is a client too, so it is answered here as well, before WPF:
    // Windows starts this exe with the link when one is clicked, and TaskLinkCommandLine hands it to the running copy.
    static int Main(string[] args) =>
        ControlCommandLine.Wants(args) ? ControlCommandLine.Run(args)
        : TaskLinkCommandLine.Wants(args) ? TaskLinkCommandLine.Run(args)
        : RunApp(args);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int RunApp(string[] args)
    {
        // A copy started because the last one was refused a window waits first, before it asks Windows for one
        // (RefusedStart). Nothing exists yet that could be kept waiting: no window, no mutex, no tray icon.
        var attempt = Windows.Recovery.RefusedStart.AttemptOf(args);
        if (attempt > 0)
        {
            ClickTrace.Write($"refused window: start {attempt} of {Windows.Recovery.RefusedStart.MaxAttempts}, waiting until a window can be made");
            var room = Windows.Recovery.RefusedStart.WaitForWindows(Windows.Diagnostics.RefusedWindow.CanCreateWindow, System.Threading.Thread.Sleep);
            ClickTrace.Write(room ? "refused window: a probe window was made, starting" : "refused window: still no window after the wait, trying anyway");
        }

        // CLASS NAME SLOTS, BEFORE THE FIRST WINDOW (task c5e9b17f; ClassNameSlots has the measurements). WPF registers
        // a window class for every window, and on 2026-11-25 nine starts in a row were refused one, because the
        // processor the UI thread happened to run on had no usable slot left while others had thousands. So every
        // processor that is short is topped up first, from a thread of its own. Under twenty milliseconds on 32
        // processors when nothing is short, which is every machine whose table never filled; never fatal, a failure
        // is traced and the start goes on as it always did.
        var slots = Windows.Recovery.ClassNameSlots.Balance();
        if (slots.IsFailure) ClickTrace.Write(slots.Error);
        else if (!slots.Value.Healthy) ClickTrace.Write($"class names, before the first window: {slots.Value}");

        try
        {
            var app = new App();
            app.InitializeComponent();
            return app.Run();
        }
        // Windows refused the start a window: in `new App()` (the dispatcher's message window), in App.xaml (WPF's
        // system-resources listener), or in OnStartup before its crash handler is in place. Five of the eight crashes
        // in the desktop heap incident were here (RefusedWindow has them). Nothing is on screen and nothing can be
        // retried in this process, so the next copy is started to try again once windows can be made, and this one
        // ends. No message box: showing one needs the very thing Windows is refusing.
        catch (Exception fault) when (Windows.Diagnostics.RefusedWindow.Is(fault))
        {
            ClickTrace.Write($"refused window while starting: {fault.GetType().Name}: {fault.Message}");
            var next = Windows.Recovery.RefusedStart.Start(Environment.ProcessPath, args);
            ClickTrace.Write(next.IsSuccess ? "refused window: the next copy will try again" : $"refused window: {next.Error}");
            return RefusedExitCode;
        }
    }

    // The exit code of a start Windows refused a window. Any nonzero code would do; this one (ERROR_NOT_ENOUGH_MEMORY)
    // is easy to recognise in a log.
    const int RefusedExitCode = 8;
}
