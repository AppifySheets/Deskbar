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
    public static bool IsFrameHost(string processName) =>
        processName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase);
}
