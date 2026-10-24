using CSharpFunctionalExtensions;

namespace Deskbar.App.Startup;

// The direct build's start with Windows: the HKCU Run value, through StartupRegistration.
//
// A wrapper rather than a move: StartupRegistration also serves the rename migration, the start-up
// re-assert and OldVersionCleanup, and its guards (only the app itself writes the value, never a test
// host; only a published build re-points it) are the history in its comments. Everything here goes
// through those same guards, so nothing about who may touch the real login entry changes.
public sealed class RunValueStartup : IStartupSwitch
{
    public bool IsEnabled => StartupRegistration.IsEnabled;

    // A Run value has no policy or Settings state of its own that the app is barred from overriding:
    // Settings, Apps, Startup can turn it off, but writing the value again is allowed.
    public StartupAvailability Availability => StartupAvailability.Available;

    public Result Enable() => StartupRegistration.Enable();

    public Result Disable() => StartupRegistration.Disable();
}
