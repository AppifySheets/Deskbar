using System.Diagnostics;
using System.Globalization;
using CSharpFunctionalExtensions;

namespace Deskbar.Windows.Recovery;

// A COPY THAT CANNOT START BECAUSE WINDOWS REFUSES IT A WINDOW tries again later, in a fresh process (task c1e6c1ae).
//
// Five of the eight crashes in the 2026-11-25 desktop heap incident (RefusedWindow has the event log) happened before
// the app had anything on screen: `new App()` could not make the dispatcher's message window, or App.xaml could not
// make WPF's system-resources listener. A copy in that state has nothing to carry on with, and it cannot retry in the
// same process either: a failed `new App()` leaves the thread's dispatcher half made, and WPF allows one Application
// per process, so a failed InitializeComponent cannot be followed by a second `new App()`.
//
// So it starts its successor and ends, and the successor WAITS before it builds anything (Program.RunApp reads the
// switch first): at least MinimumWait, then until a probe window can be made (RefusedWindow.CanCreateWindow, asked every
// ProbeEvery), for up to ProbeFor. The desktop heap is shared by everything on the desktop, so it frees up when
// something else closes, on a scale of seconds to minutes, and the probe starts the app the moment it has. Before this
// the app stayed dead until Petre started it by hand, and in that incident starting it by hand while the updater was
// mid-handoff made things worse. (The probe was the idea of a session that started on the same task in parallel.)
//
// BOUNDED, because an endless chain of processes is a problem of its own: MaxAttempts starts in a row, then it stops,
// and it is back to being started by hand. MinimumWait keeps a machine where the small probe passes but the app's own
// windows are still refused from cycling processes as fast as they can start. The attempt number travels on the command
// line (`--refused-start=N`) because it has to survive exactly one process boundary and nothing else, the reason the
// updater's own switch is one.
//
// The successor also gets CrashRelaunch's switch, so it waits for the mutex the way a crash relaunch does: a copy that
// was refused a window inside OnStartup already held the single-instance mutex, and a successor that looked once would
// say "already running" (with a message box, which needs a window) instead of starting.
//
// Every other argument is passed on as it was, the updater's `--await-previous` included: harmless when repeated (its
// parent wait is bounded, and the parent is this copy, which is about to end), and dropping it would change what the
// successor does.
public static class RefusedStart
{
    public const string Switch = "--refused-start";

    // How long a successor waits at the least, and then how often and for how long it probes for a window (see above).
    // Ten minutes of probing per start, ten starts: the incident lasted about fifteen minutes and was ended by a person.
    public static readonly TimeSpan MinimumWait = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan ProbeEvery = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan ProbeFor = TimeSpan.FromMinutes(10);
    public const int MaxAttempts = 10;

    // The successor's wait: MinimumWait, then `canCreate` asked every ProbeEvery until it says yes or ProbeFor has
    // passed. Whether it said yes; the start goes ahead either way, since only trying tells whether the app's own
    // windows fit. The probe and the sleep are passed in so a test runs it in no time.
    public static bool WaitForWindows(Func<bool> canCreate, Action<TimeSpan> sleep)
    {
        sleep(MinimumWait);
        var waited = TimeSpan.Zero;
        while (!canCreate())
        {
            if (waited >= ProbeFor) return false;
            sleep(ProbeEvery);
            waited += ProbeEvery;
        }
        return true;
    }

    // Which try this copy is: 0 for an ordinary start, N for the Nth start after a refused one.
    public static int AttemptOf(IReadOnlyList<string> args) =>
        args.Select(Parse).FirstOrDefault(n => n > 0);

    // The successor's arguments, or None once the tries are used up.
    public static Maybe<IReadOnlyList<string>> NextArgs(IReadOnlyList<string> args)
    {
        var attempt = AttemptOf(args);
        if (attempt >= MaxAttempts) return Maybe<IReadOnlyList<string>>.None;
        var kept = args.Where(a => Parse(a) == 0 && a != Switch).ToList();
        var crash = kept.Contains(CrashRelaunch.Switch) ? [] : new[] { CrashRelaunch.Switch };
        return kept.Concat(crash).Append($"{Switch}={attempt + 1}").ToList();
    }

    // Starts the successor. Result rather than an exception for CrashRelaunch.Start's reason: the caller is a process
    // that is already failing, and a launch that cannot happen must come back as something to report, not a second
    // throw. UseShellExecute off, CreateProcess straight away, which needs no window of its own.
    public static Result Start(string? exePath, IReadOnlyList<string> args) =>
        exePath is null
            ? Result.Failure("not trying again: the process has no exe path to start")
            : NextArgs(args).ToResult($"not trying again: {MaxAttempts} starts in a row were refused a window")
                .Bind(next => Result.Try(
                    () =>
                    {
                        var start = new ProcessStartInfo(exePath) { UseShellExecute = false };
                        next.ToList().ForEach(start.ArgumentList.Add);
                        Process.Start(start)?.Dispose();
                    },
                    e => $"could not start the next copy: {e.Message}"));

    // N from `--refused-start=N`, 0 for anything else.
    static int Parse(string arg) =>
        arg.StartsWith(Switch + "=", StringComparison.Ordinal)
        && int.TryParse(arg.AsSpan(Switch.Length + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n
            : 0;
}
