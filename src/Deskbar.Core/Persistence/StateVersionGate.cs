using CSharpFunctionalExtensions;

namespace Deskbar.Core.Persistence;

// Decides whether this build may read a state.json it did not write itself.
//
// Why it exists: the Microsoft Store build lags the GitHub build, and both share %APPDATA%\Deskbar\state.json
// (see docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md). An older build that loads a
// newer file could lose what the newer build wrote. So every save stamps AppState.WrittenBy with the running
// version, and a build refuses a stamp from a newer MAJOR version.
//
// Why only the major (changed after the whole-branch review, 2026-10-24): releases go out daily and the Store
// copy lags certification by days, so the Store copy is nearly always a patch or a minor behind the GitHub
// copy. Refusing on a newer patch or minor would block the Store upgrade path (the GitHub copy writes 2.2.3,
// the Store copy is 2.2.1 and could never start) and any machine that keeps two builds. Within a major an
// older build that loads a newer file keeps what it understands: AppState.Extra carries unknown top-level
// fields through a save (Workspace.Extra and Bookmark.Extra do the same inside those records), and the file is
// copied aside first (JsonPersistenceStore.KeepCopyIfNewer), because a field nested anywhere else is still dropped by
// the older build's save: that is how 2.6.48 lost every bookmark on 2026-11-27 (Deskbar task 0bd17709).
// A new major is where a file shape may change incompatibly, so that is where the refusal stays.
//
// The parse is still major.minor.patch from the start of each string. Anything after a '+' (build metadata
// such as the commit sha) or a '-' (pre-release) is ignored. A stamp that does not parse passes: an
// unreadable stamp is not a reason to lock the user out of their own settings.
public static class StateVersionGate
{
    // The App tells this refusal from a corrupt file by this prefix: a corrupt file is backed up and replaced,
    // a newer one must never be, so the App shows the message and exits instead.
    public const string RefusalPrefix = "Your settings were saved by";

    // The one place the prefix is tested, so the App's two load-failure branches cannot disagree.
    public static bool IsRefusal(string message) => message.StartsWith(RefusalPrefix, StringComparison.Ordinal);

    // Fails only when the stamp's major is above the running major: 3.0.0 against 2.9.99 refuses, while
    // 2.2.0 against 2.1.49 and 2.1.50 against 2.1.44 both load.
    public static Result Check(string? writtenBy, string runningVersion) =>
        Parse(writtenBy) is { } written && Parse(runningVersion) is { } running && written.Major > running.Major
            ? Result.Failure(RefusalMessage(writtenBy!, runningVersion))
            : Result.Success();

    // Whether the stamp is from a newer build than this one at ANY part of major.minor.patch: 2.6.61 against 2.6.48,
    // and a test build's 2.6.208 against the release 2.6.61. Not a refusal (Check above lets these load); it tells
    // JsonPersistenceStore to keep a copy of the file before this build saves over it and drops what it does not know.
    // False when either side does not parse, like Check.
    public static bool IsNewer(string? writtenBy, string runningVersion) =>
        Parse(writtenBy) is { } written && Parse(runningVersion) is { } running && written > running;

    public static string RefusalMessage(string writtenBy, string running) =>
        $"{RefusalPrefix} Deskbar {writtenBy}; this is {running}. Start the newer copy, or remove it and delete the WrittenBy line from state.json to go on with this one.";

    // "2.1.44+abc" and "2.1.44-beta" both read as 2.1.44; null when the start of the text is not a version.
    // System.Version compares numerically (2.10.0 is above 2.9.0), which string comparison would get wrong.
    static Version? Parse(string? text) =>
        text?.Split('+', '-')[0].Trim() is { Length: > 0 } core
        && core.Count(c => c == '.') >= 2
        && Version.TryParse(core, out var version)
            // A fourth part (an assembly version's ".0") is dropped: only major.minor.patch counts.
            ? new Version(version.Major, version.Minor, version.Build)
            : null;
}
