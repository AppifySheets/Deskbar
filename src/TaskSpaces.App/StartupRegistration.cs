using System.Reflection;
using CSharpFunctionalExtensions;
using Microsoft.Win32;

namespace TaskSpaces.App;

// "Start with Windows" via HKCU Run -- per-user, no admin, trivially reversible.
public static class StartupRegistration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "Deskbar";

    // The value name 1.x wrote. Only the rename migration touches it (LegacyIsSet, MigrateFromTaskSpaces):
    // it is deleted so that login does not start TaskSpaces 1.x beside Deskbar.
    internal const string LegacyName = "TaskSpaces";

    public static bool IsEnabled =>
        Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue(Name) is not null;

    // Read-only, so the test host may ask it: RenameMigrator.Gather does, for the plan's facts.
    internal static bool LegacyIsSet =>
        Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue(LegacyName) is not null;

    // Petre: "taskspaces didn't start with windows". The entry pointed at the TEST HOST. ManageWindow
    // ticks its "Start with Windows" box from the registry when it is built, that raises Checked, and
    // Enable wrote Environment.ProcessPath, which under a test run is testhost.exe. Three test classes
    // build a ManageWindow, so every run of the suite repointed his real login entry; the next app
    // start put it back, so it only showed when the machine rebooted between a test run and an app
    // start. Measured: one run of ManageSettingsTabTests turned the value into testhost.exe.
    //
    // So only the app itself writes or removes the entry, decided by the entry assembly rather than by
    // the exe's file name: the release exe is called Deskbar-2.0.0-win-x64.exe and the debug build
    // Deskbar.exe, but both have Deskbar as their entry assembly, single-file included. The old
    // TaskSpaces.App name is refused on purpose, so a 1.x build never registers under the new value.
    // A test host refuses Disable as well as Enable, because a test that unticks the box would
    // otherwise delete the real registration outright.
    public static bool IsTheApp(string? entryAssembly) => entryAssembly == "Deskbar";

    static Result OwnedByThisProcess() =>
        Result.SuccessIf(IsTheApp(Assembly.GetEntryAssembly()?.GetName().Name),
            "only Deskbar itself registers or unregisters its start with Windows");

    public static Result Enable() =>
        OwnedByThisProcess().Tap(() =>
            Registry.CurrentUser.CreateSubKey(RunKey).SetValue(Name, $"\"{Environment.ProcessPath}\""));

    // Petre, on the update flow (#71): "after a portable swap to a new file/path, that value still
    // points at the OLD exe."
    //
    // The app is portable and updating it means running a DIFFERENT FILE -- a new exe downloaded
    // next to the old one, never a replacement of the running one. So the path stored here goes
    // stale the moment a newer version is started, and the machine keeps launching the old build
    // at every login while the user is looking at the new one, wondering why the update did not
    // take.
    //
    // Called on every start rather than only after an update, because there is no reliable moment
    // that means "I was just updated": the new exe is started by the old one, by Explorer, or by
    // the Run key itself, and only the first of those is knowable. Re-asserting unconditionally
    // needs no such signal.
    //
    // Only when autostart is already ON: this must never turn it on for someone who chose off.
    // The value name is fixed, so this overwrites rather than accumulating entries -- which also
    // means whichever version runs last owns startup, and that is the intended rule for a folder
    // holding several versions side by side. Latest one started wins.
    //
    // ...but only from a PUBLISHED build. "Latest one started wins" was written for release versions sitting
    // side by side in one folder, and it let every DEVELOPMENT copy win too: each build a session started to
    // show Petre a change (from bin\Debug, or copied to a scratch folder so the build could overwrite bin)
    // repointed his login entry at itself. Measured on 2026-10-20: the entry named a session's temporary
    // scratch folder, the one before it another session's, and no installed copy existed at all, so the next
    // cleanup of that folder would have left Deskbar not starting at login. His answer: "fix autostart only".
    public static void ReassertIfEnabled()
    {
        if (IsEnabled && RunsAsPublished) Enable();
    }

    // A release is published as ONE file (release.yml: PublishSingleFile), and a development build never is:
    // bin\Debug and every copy of it are an exe beside a folder of dlls. In a single-file app the assembly
    // has no file of its own, so its Location is empty, which is the documented way to tell the two apart.
    internal static bool IsPublished(string assemblyLocation) => assemblyLocation.Length == 0;

#pragma warning disable IL3000 // Location is empty in a single-file app, and that emptiness is the point here.
    internal static bool RunsAsPublished => IsPublished(typeof(StartupRegistration).Assembly.Location);
#pragma warning restore IL3000

    public static Result Disable() =>
        OwnedByThisProcess().Tap(() =>
            Registry.CurrentUser.CreateSubKey(RunKey).DeleteValue(Name, throwOnMissingValue: false));

    // The rename migration's half of autostart, on Deskbar's first start after TaskSpaces 1.x: the old
    // value goes, so login does not start 1.x beside Deskbar, and the new one points at this exe, which
    // is what keeps "start with Windows" true for the copy a 1.x updater has just started. Without it,
    // two bars would start at the next login. Which of the two to do is decided by RenameMigration.Plan
    // (setNew only when Deskbar is not already set, so a chosen path is never overwritten).
    //
    // Guarded exactly like Enable and Disable, for the same reason: the test host must never touch the
    // real login entry. A registry failure comes back as a failure rather than an exception, because
    // this runs during startup and an autostart that could not be swapped is no reason not to start.
    //
    // The new value is written only by a published build, for the reason ReassertIfEnabled gives: a
    // development copy pointing the login entry at itself is the bug that guard exists for.
    public static Result MigrateFromTaskSpaces(bool deleteOld, bool setNew) =>
        OwnedByThisProcess().Bind(() => Result.Try(() =>
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (deleteOld) key.DeleteValue(LegacyName, throwOnMissingValue: false);
            if (setNew && RunsAsPublished) key.SetValue(Name, $"\"{Environment.ProcessPath}\"");
        }, error => $"could not move start with Windows from TaskSpaces to Deskbar: {error.Message}"));
}
