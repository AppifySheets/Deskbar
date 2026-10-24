using CSharpFunctionalExtensions;

namespace Deskbar.Core.Persistence;

// Decides whether this build may read a state.json it did not write itself.
//
// Why it exists: the Microsoft Store build lags the GitHub build, and both share %APPDATA%\Deskbar\state.json
// (see docs/superpowers/specs/2026-10-24-deskbar-store-packaging-design.md). An older build that loads a
// newer file silently drops every field it does not know and then saves the file back without them. So
// every save stamps AppState.WrittenBy with the running version, and an older build refuses a newer stamp.
//
// Only major.minor.patch is compared, parsed from the start of each string. Anything after a '+' (build
// metadata such as the commit sha) or a '-' (pre-release) is ignored, because two builds of one release must
// not refuse each other. A stamp that does not parse passes: an unreadable stamp is not a reason to lock the
// user out of their own settings. Equal passes. Only a strictly newer stamp refuses.
public static class StateVersionGate
{
    // The App tells this refusal from a corrupt file by this prefix: a corrupt file is backed up and replaced,
    // a newer one must never be, so the App shows the message and exits instead.
    public const string RefusalPrefix = "Your settings were saved by";

    public static Result Check(string? writtenBy, string runningVersion) =>
        Parse(writtenBy) is { } written && Parse(runningVersion) is { } running && written > running
            ? Result.Failure(RefusalMessage(writtenBy!, runningVersion))
            : Result.Success();

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
