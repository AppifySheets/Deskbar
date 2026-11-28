namespace Deskbar.Core.Control;

// A SESSION IN A TEMP FOLDER IS A THROWAWAY, AND GETS NO TASK (task 98a6378a, Petre picked option A: "ignore temp-folder
// sessions").
//
// What happened: a helper agent of another task started a Haiku session in %TEMP%\probe-hook with one job, `echo hi >
// marker.txt`, to time how long Claude Code holds a permission hook. Deskbar's hooks run in every session, so the
// SessionStart briefing told it to join or create a task, and it made "echo hi marker.txt". When the helper stopped it,
// the comment waker took that for a cut-off turn and resumed it twice, the idle check nudged it, and Petre was left asking
// "why am i being asked this questions? what's this for?".
//
// Probes like that one are started in a scratch folder on purpose (the setup sessions pass around says "a fresh empty
// scratch folder under ...\.claude\jobs\<id>\tmp\probe-hook"), so the folder is the one sign every probe carries without
// anyone having to remember a flag (option B, an opt-out variable, was turned down for exactly that). A session counts
// as a throwaway when its working folder is:
//   * the user's temp folder (Path.GetTempPath(), %TEMP%) or anything below it, or
//   * a Claude Code background job's scratch folder, %USERPROFILE%\.claude\jobs\<job id>\tmp, or anything below it.
//     Only the tmp folder: the job folder itself holds the job's own files, and no session works there.
//
// What a throwaway is spared is decided where the hook answers (RemoteControl.ClaudeHook, TaskEvent): no briefing
// offering a task, no reminder line per prompt, and it is never marked attended, so the keeper never adopts it. Every
// resume and nudge Deskbar sends works on a task, so with no task none of them reaches it. A throwaway that makes a task
// on its own (`task new`, a deliberate test of the task board) is treated like any other session from then on.
public static class ThrowawayFolder
{
    /// <summary>
    /// Whether <paramref name="folder"/> (a session's working folder, in any form FolderPath.Normalise reads) is the temp
    /// folder <paramref name="tempPath"/> or below it, or a Claude job's tmp folder under <paramref name="userProfile"/>
    /// or below it. Empty roots match nothing, so a copy that does not know them leaves every session alone.
    /// </summary>
    public static bool Is(string? folder, string tempPath, string userProfile, Func<string, string> longPath)
    {
        var caller = FolderPath.Normalise(folder, longPath);
        if (caller.Length == 0) return false;
        return Within(caller, FolderPath.Normalise(tempPath, longPath)) || InJobTmp(caller, FolderPath.Normalise(userProfile, longPath));
    }

    // %USERPROFILE%\.claude\jobs\<id>\tmp and below: the part after the jobs folder must start with "<id>\tmp".
    static bool InJobTmp(string caller, string userProfile)
    {
        if (userProfile.Length == 0) return false;
        var jobs = Path.Combine(userProfile, ".claude", "jobs");
        if (!Within(caller, jobs) || caller.Length <= jobs.Length + 1) return false;
        var segments = caller[(jobs.TrimEnd('\\').Length + 1)..].Split('\\');
        return segments.Length >= 2 && segments[0].Length > 0 && string.Equals(segments[1], "tmp", StringComparison.OrdinalIgnoreCase);
    }

    // The root itself or a folder below it, ignoring case as Windows does. The separator is added after trimming, so a
    // root of "C:\Temp" does not take in "C:\TempOther", and a drive root "C:\" does not become "C:\\".
    static bool Within(string caller, string root) =>
        root.Length > 0
        && (string.Equals(caller, root, StringComparison.OrdinalIgnoreCase)
            || caller.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
}
