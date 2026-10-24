using System.IO;
using CSharpFunctionalExtensions;
using Deskbar.Core.Migration;

namespace Deskbar.App;

// The Windows half of the Store copy's autostart takeover: it does what PackagedTakeover.Plan (Deskbar.Core)
// lists, and decides nothing. Store packaging design:
// docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md
//
// Runs once per start in App.OnStartup, after StartupRegistration.ReassertIfEnabled and before
// StartMenuShortcut.Ensure. The plan is empty outside the package, after the first packaged start (the
// marker) and under DESKBAR_APPDATA, so on every ordinary start this is a no-op.
//
// Every step is attempted and traced; a failure is traced and the next step still runs, because an
// autostart that could not be cleaned up is no reason for the app not to start. The marker is written
// even when a deletion failed: the deletion is not retried at every start, and the trace says what was
// left behind. Deliberately never called by a test against real paths; the tests cover the plan.
static class PackagedTakeoverApplier
{
    public static void Apply(IReadOnlyList<TakeoverStep> steps, string stateDir, Action<string> trace) =>
        steps.ToList().ForEach(step => Attempt(step, stateDir).Match(
            what => trace($"takeover: {what} (ok)"),
            failure => trace($"takeover: {failure.What} (failed: {failure.Message})")));

    static Result<string, (string What, string Message)> Attempt(TakeoverStep step, string stateDir) =>
        step switch
        {
            // Through StartupRegistration so the same owner guard applies as for Manage's box: only Deskbar
            // itself touches the Run key, never a test host. Disable deletes exactly the `Deskbar` value.
            DeleteDirectRunValue => Run("deleting the direct build's Run value Deskbar",
                () => StartupRegistration.Disable()),
            DeleteDirectShortcut => Run($"deleting the direct build's Start menu shortcut {StartMenuShortcut.ShortcutPath}",
                () => Result.Try(() => File.Delete(StartMenuShortcut.ShortcutPath), error => error.Message)),
            // The folder first: on a fresh Store install nothing may have created %APPDATA%\Deskbar yet, and
            // a marker that cannot be written would run the takeover again at the next start.
            WriteMarker => Run($"writing the marker {PackagedTakeover.MarkerName}",
                () => Result.Try(() =>
                {
                    Directory.CreateDirectory(stateDir);
                    File.WriteAllText(Path.Combine(stateDir, PackagedTakeover.MarkerName), "");
                }, error => error.Message)),
            _ => Result.Failure<string, (string, string)>(($"{step.GetType().Name}", "not a takeover step this build knows")),
        };

    // Any exception the action throws is caught here too, so one step can never stop the next.
    static Result<string, (string What, string Message)> Run(string what, Func<Result> action) =>
        Result.Try(action, error => error.Message)
            .Bind(result => result)
            .Map(() => what)
            .MapError(message => (what, message));
}
