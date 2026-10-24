using CSharpFunctionalExtensions;

namespace Deskbar.App.Startup;

// Whether Windows will let the app's start-with-Windows switch be turned on from the app.
//
// Only the Store package's startup task can be anything but Available: Windows lets the user turn a
// startup task off in Settings, Apps, Startup (DisabledByUser) and an administrator turn it off by policy
// (DisabledByPolicy), and in both cases the app is not allowed to turn it back on, so Manage greys the box
// and says where it can be turned on. LockedOn is the mirror case: an administrator turned the task ON by
// policy (EnabledByPolicy), so it starts with Windows and the app may not turn it off; Manage shows the box
// ticked and greyed. Unavailable means the state could not be read at all (the call threw or timed out),
// which is no reason to lock the box. The Run value of the direct build is always Available.
public enum StartupAvailability
{
    Available,
    DisabledByUser,
    DisabledByPolicy,
    LockedOn,
    Unavailable,
}

// "Start Deskbar with Windows", whichever mechanism this build uses for it: an HKCU Run value for the
// direct build (RunValueStartup), the package's startup task for the Store copy (PackagedStartup).
// StartupSwitch.Current picks one. Enable and Disable return a failure rather than throwing, because the
// caller is a checkbox handler and a switch that did not flip is something to show, not a crash.
// Design: docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md
public interface IStartupSwitch
{
    bool IsEnabled { get; }
    StartupAvailability Availability { get; }
    Result Enable();
    Result Disable();
}
