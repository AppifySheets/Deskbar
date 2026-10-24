namespace Deskbar.App.Startup;

// The line under Manage's "Start Deskbar with Windows" box, or "" for none.
//
// Kept apart from the window so every case can be tested, including the one a window test cannot set up:
// whether the Store copy is installed is a real file on the machine (its execution alias), so the window
// passes File.Exists(PackageIdentity.AliasPath) in and the tests pass a value.
// Design: docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md
public static class StartupNoteText
{
    public static string For(StartupAvailability availability, bool packaged, bool storeCopyInstalled) => availability switch
    {
        StartupAvailability.DisabledByPolicy => "Startup is turned off by policy. Turn it on under Settings, Apps, Startup.",
        StartupAvailability.DisabledByUser => "Startup is turned off in Settings. Turn it on under Settings, Apps, Startup.",
        // An administrator's EnabledByPolicy: on, and not the app's to turn off, so there is no place to send them.
        StartupAvailability.LockedOn => "Startup is turned on by policy and cannot be changed here.",
        StartupAvailability.Unavailable => "Startup could not be read.",
        // Store packaging design: the direct build warns when the Store copy is installed beside it, because
        // that copy's startup task (on by default) and this build's Run value would each start a Deskbar at
        // login. The Store copy itself has the alias too, so it never warns about itself.
        _ when !packaged && storeCopyInstalled => "The Microsoft Store copy of Deskbar starts with Windows. Ticking this starts both.",
        _ => "",
    };
}
