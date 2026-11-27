using System.IO;
using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;

namespace Deskbar.App;

// TRUSTING A FOLDER FOR CLAUDE CODE, THE DISK HALF (Deskbar task 50b61120). ClaudeTrust decides what the file should say;
// this finds Claude Code's .claude.json, reads it, hands it over, and puts the answer back.
//
// The file is Claude Code's and every running session rewrites it (tips seen, project history), so the write is kept as
// short and as whole as it can be: the new text goes to a file beside it and is MOVED over it, which is what Claude Code
// does itself (its own .claude.json.tmp.* files show it), so nothing ever reads half a file. A folder already trusted is
// not written at all. A read that fails, or text that does not parse (a session halfway through its own write), is
// tried again a few times a moment apart and then given up: the caller opens a terminal running `claude` in the folder
// next, whose own prompt asks for the trust when this did not write it.
//
// Refused outright, so the terminal's prompt decides instead: a missing file (Claude Code not set up yet, or kept
// somewhere this does not know: a stub here would be one it never reads), and the home folder itself, which would
// trust everything under it (ClaudeTrust refuses a drive's root for the same reason).
//
// What cannot be prevented from here: a session that read the file before this write and writes its own copy after
// it would drop the yes. That is why the terminal follows, and why nothing here claims the folder is trusted for good.
// Runs off the UI thread (ClaudeTaskActions.SetWorkspaceFolder): the file is a few hundred kilobytes, parsed twice.
public static class ClaudeTrustFile
{
    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// Where Claude Code keeps its settings and trusted folders: <c>%CLAUDE_CONFIG_DIR%\.claude.json</c> when that is
    /// set, as Claude Code itself reads it, and <c>%USERPROFILE%\.claude.json</c> otherwise.
    /// </summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir ? dir : Home, ".claude.json");

    /// <summary>
    /// Marks <paramref name="folder"/> trusted in the file at <paramref name="path"/>, leaving everything else in it as it
    /// was. Success when it already was. Tries <paramref name="attempts"/> times before giving up with the last reason.
    /// </summary>
    public static Result Accept(string path, string folder, int attempts = 3)
    {
        if (SameFolder(folder, Home)) return Result.Failure($"{folder} is the home folder; trusting it would trust everything under it.");
        if (!File.Exists(path)) return Result.Failure($"{path} does not exist, so Claude Code has not been set up there.");
        var last = Result.Failure("Not tried.");
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (attempt > 0) Thread.Sleep(150);
            last = TryAccept(path, folder);
            if (last.IsSuccess) return last;
        }
        return last;
    }

    static bool SameFolder(string a, string b) =>
        string.Equals(ClaudeTrust.Key(a), ClaudeTrust.Key(b), StringComparison.OrdinalIgnoreCase);

    static Result TryAccept(string path, string folder)
    {
        try
        {
            var before = File.ReadAllText(path);
            if (ClaudeTrust.IsAccepted(before, folder)) return Result.Success();
            return ClaudeTrust.Accepted(before, folder).Tap(after => Replace(path, after)).Bind(_ => Result.Success());
        }
        // ArgumentException and InvalidOperationException: a duplicate key deep in the file, which JsonNode only notices
        // when that part is first read. A failure like any other, never an exception out of a menu click.
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return Result.Failure(e.Message);
        }
    }

    // The new text beside the file, then moved over it in one step, so a reader sees the old file or the new one.
    static void Replace(string path, string text)
    {
        var beside = $"{path}.deskbar-{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(beside, text);
            File.Move(beside, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(beside)) File.Delete(beside);
        }
    }
}
