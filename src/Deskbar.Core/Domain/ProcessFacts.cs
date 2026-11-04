namespace Deskbar.Core.Domain;

// One process, as much of it as the launched-by rule needs (#94): its name, so the shell can be told
// apart from an app, and its parent, so the chain can be walked.
//
// Name WITHOUT the extension, matching WindowInfo.ProcessName ("chrome", not "chrome.exe"), so the two
// can be compared without either side remembering to trim.
public sealed record ProcessFacts(int ProcessId, string Name, int ParentProcessId)
{
    // The one process that owns the window of several Store apps at once (Calculator, Settings): ending it
    // closes every app it hosts, so Force close, Close all and Restart all must not treat it as "the app".
    //
    // The same name is spelled in other places, each for its own list, so a change here may belong there too:
    // LaunchedBy.NotLaunchers (lower case, "plumbing" ancestors skipped when looking for a launcher) and
    // RosterIdentity.StayWhereOpened (windows that stay in the workspace they opened in), and LaunchCatalog's own
    // check on a path (quick find offers no frame to launch).
    public static bool IsFrameHost(string processName) =>
        processName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase);
}
