using CSharpFunctionalExtensions;
using Windows.ApplicationModel;
using Windows.Foundation;

namespace Deskbar.App.Startup;

// The Store copy's start with Windows: the package's startup task, declared in
// src/Deskbar.App/Package/AppxManifest.xml with TaskId Deskbar. It is the mechanism the package declares
// for starting at login and the entry Settings, Apps, Startup shows and controls for the Store copy, so
// the box in Manage and that Settings page stay one switch.
// Design: docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md
//
// Synchronous on purpose. The only caller is Manage's checkbox, on the UI thread: the constructor reads
// the state to tick the box and the Checked handler flips it, and neither can await. The calls are local
// (no network, no Store), so blocking is normally a few milliseconds; the bounded wait keeps a hung
// broker from freezing Manage, and a timeout is handled like any other failure. Blocking does not
// deadlock: AsTask completes on a thread-pool thread, not through the UI thread's dispatcher.
//
// Never throws: outside a package GetAsync itself throws, and every failure becomes Unavailable or a
// Result.Failure naming what went wrong.
public sealed class PackagedStartup : IStartupSwitch
{
    const string TaskId = "Deskbar";
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    // An unreadable state reads as off: the box then offers to turn it on, which is the harmless guess.
    public bool IsEnabled => State().Map(IsOn).GetValueOrDefault(false);

    public StartupAvailability Availability => State().Map(AvailabilityOf).GetValueOrDefault(StartupAvailability.Unavailable);

    // RequestEnableAsync answers with the state it left the task in. Anything other than Enabled means it
    // did not turn on (the user or a policy turned it off, and only they can turn it back on), so it is a
    // failure that names the state.
    public Result Enable() =>
        StartupTaskOf()
            .Bind(task => Try(() => Wait(task.RequestEnableAsync())))
            .Ensure(state => state == StartupTaskState.Enabled, state => $"the startup task {TaskId} is {state}")
            .Bind(_ => Result.Success());

    // Disable is synchronous in the API itself, so there is nothing to wait for.
    public Result Disable() =>
        StartupTaskOf().Bind(task => Result.Try(task.Disable, Describe));

    static bool IsOn(StartupTaskState state) => state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;

    // Enabled and Disabled are states the app may leave as they are or change (the second is just the box
    // unticked). The two Disabled-by states are the ones only the user or an administrator can undo, and
    // EnabledByPolicy is an administrator's ON that the app may not turn off, so it counts as enabled (IsOn
    // above) and locked: Manage shows the box ticked and greyed.
    static StartupAvailability AvailabilityOf(StartupTaskState state) => state switch
    {
        StartupTaskState.DisabledByUser => StartupAvailability.DisabledByUser,
        StartupTaskState.DisabledByPolicy => StartupAvailability.DisabledByPolicy,
        StartupTaskState.EnabledByPolicy => StartupAvailability.LockedOn,
        _ => StartupAvailability.Available,
    };

    // Read fresh on every ask rather than cached: the user can flip the task in Settings while Manage is
    // open, and the box should follow the next time it is read.
    static Result<StartupTaskState> State() => StartupTaskOf().Map(task => task.State);

    static Result<StartupTask> StartupTaskOf() => Try(() => Wait(StartupTask.GetAsync(TaskId)));

    // A TimeoutException from WaitAsync surfaces through GetResult and is caught by Try like any other.
    static T Wait<T>(IAsyncOperation<T> operation) =>
        operation.AsTask().WaitAsync(Timeout).GetAwaiter().GetResult();

    static Result<T> Try<T>(Func<T> call) => Result.Try(call, Describe);

    static string Describe(Exception ex) => $"startup task {TaskId}: {ex.GetType().Name}: {ex.Message}";
}
