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
static class Program
{
    [STAThread]
    static int Main(string[] args) => ControlCommandLine.Wants(args) ? ControlCommandLine.Run(args) : RunApp();

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int RunApp()
    {
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
