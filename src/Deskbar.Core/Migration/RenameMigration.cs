namespace Deskbar.Core.Migration;

// What the app found at its start, as much of it as the rename migration decides on. Gathered by the
// App layer (RenameMigrator.Gather) and nothing else: this type knows no path, no registry and no
// mutex, which is what lets every rule below be settled in a unit test.
//
// OldFilesPresent names the state files found in the 1.x folder, among RenameMigration.StateFiles.
// NewStateFileExists is the new folder's state.json, not the folder: the folder alone can exist before
// the first start (the trace marker lives in it), and that must not count as "already migrated".
// OldMutexHeld means `Local\TaskSpaces.SingleInstance` was already held when this copy took it, which
// by then can only be a TaskSpaces 1.x that is running right now. AwaitingPrevious is true for a copy
// whose predecessor is on its way out: the updater's handshake, and a relaunch after a crash. IsTheApp
// is the same test StartupRegistration applies (the entry assembly is Deskbar), false under a test host.
// Packaged is PackageIdentity.IsPackaged: the Store copy, whose autostart is its package startup task
// (docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md).
public sealed record RenameFacts(
    bool OldFolderExists, bool NewStateFileExists,
    IReadOnlyList<string> OldFilesPresent,
    bool OldShimExists,
    bool OldRunValueExists, bool NewRunValueExists,
    bool OldMutexHeld, bool AwaitingPrevious,
    bool RootOverridden, bool IsTheApp,
    bool Packaged);

// One thing the migration does. The App layer applies them in the order the plan lists them.
public abstract record RenameStep;

// Do nothing at all, show Message and exit. Only ever the plan's one step.
public sealed record RefuseToStart(string Message) : RenameStep;

// Copy %APPDATA%\TaskSpaces\<Name> to %APPDATA%\Deskbar\<Name> (both under the override root, when set).
public sealed record CopyFile(string Name) : RenameStep;

// Delete %APPDATA%\TaskSpaces\taskspaces.cmd.
public sealed record DeleteOldShim : RenameStep;

// Delete the HKCU Run value `TaskSpaces`.
public sealed record DeleteOldRunValue : RenameStep;

// Set the HKCU Run value `Deskbar` to the running exe.
public sealed record SetNewRunValueToThisExe : RenameStep;

// The first start of Deskbar 2.0 on a machine that ran TaskSpaces 1.x. It runs inside the app that
// owns the state, before the state store is opened, because nothing outside the app edits the state.
// The rules, and why each is the way it is:
//
// - REFUSE beside a running 1.x. The mutex names differ, so without this a 2.0 started while 1.x runs
//   would put a second bar on screen. The control pipe deliberately exposes no Exit, so there is no
//   clean way to stop the old instance from here; the person is asked to, and nothing else happens.
//   The updater's handshake (--await-previous) is the exception: 1.x started this copy and is leaving.
//   So is a relaunch after a crash: the copy that crashed was a 2.x holding the old name, so no 1.x can
//   be running, and the old name may still be held for a moment by the copy that is dying.
//
// - COPY, not move, the three state files. A rollback to 1.x, or a second machine syncing the old
//   folder, keeps working on files that are where they were. trace.on and the shim are not copied.
//   The old folder is never deleted.
//
// - ONCE only, which means until the new state.json exists: copies are planned only while it does not.
//   A second start, or a second upgrade after a rollback, copies nothing, so changes made in 2.0 are
//   never overwritten by older ones. The gate is the FILE, not the folder, because the folder can exist
//   before the first start with nothing in it but trace.on (the briefing tells a session to create the
//   marker before restarting), and gating on the folder then skipped the copy for good: the app started
//   with no workspaces and wrote a fresh state.json that no later start would ever look past. The copy
//   itself never overwrites (File.Copy with overwrite: false), so a time.json already there stays.
//
// - The old SHIM goes, so a stale `taskspaces.cmd` cannot launch the old exe.
//
// - The REGISTRY only from the app itself and never under DESKBAR_APPDATA. A test host must not touch
//   the real login entry (StartupRegistration carries that history), and an overridden root is a
//   rehearsal that must leave the real autostart alone. The old Run value goes, so login does not
//   start TaskSpaces beside Deskbar; the new one is set to this exe only if it is not already there,
//   which is what keeps "start with Windows" true across the rename, including for the copy a 1.x
//   updater has just started. Never for the Store copy, whose autostart is its startup task.
public static class RenameMigration
{
    // The one file that holds the workspaces. Named on its own because two rules key on it: its
    // presence in the new folder ends the copying, and a failure to copy it is the one copy failure
    // the person is told about.
    public const string StateFile = "state.json";

    // The files 1.x kept in its folder that are worth carrying across, in the order they are copied.
    public static IReadOnlyList<string> StateFiles { get; } = [StateFile, "time.json", "last-used.json"];

    public const string RefusalMessage = "Exit TaskSpaces from its tray icon first, then start Deskbar.";

    public static IReadOnlyList<RenameStep> Plan(RenameFacts facts) =>
        facts.OldMutexHeld && !facts.AwaitingPrevious
            ? [new RefuseToStart(RefusalMessage)]
            : [.. Copies(facts), .. Shim(facts), .. Registry(facts)];

    // Filtered against StateFiles as well as gathered from it, so a fact naming any other file can
    // never turn into a copy.
    static IEnumerable<RenameStep> Copies(RenameFacts facts) =>
        facts.OldFolderExists && !facts.NewStateFileExists
            ? facts.OldFilesPresent.Where(StateFiles.Contains).Select(name => new CopyFile(name))
            : [];

    static IEnumerable<RenameStep> Shim(RenameFacts facts) =>
        facts.OldShimExists ? [new DeleteOldShim()] : [];

    // Packaged (the Store copy, see the Store packaging design,
    // docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md): the old TaskSpaces value still
    // goes, but no Deskbar value is set, because the package's startup task is its autostart and a Run
    // value pointing at the package's exe would start a second bar at login.
    static IEnumerable<RenameStep> Registry(RenameFacts facts) =>
        facts.IsTheApp && !facts.RootOverridden && facts.OldRunValueExists
            ? facts.NewRunValueExists || facts.Packaged
                ? [new DeleteOldRunValue()]
                : [new DeleteOldRunValue(), new SetNewRunValueToThisExe()]
            : [];
}
