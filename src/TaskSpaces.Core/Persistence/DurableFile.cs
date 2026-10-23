using System.Text;
using CSharpFunctionalExtensions;

namespace TaskSpaces.Core.Persistence;

// How state.json and time.json are written and read so that a computer restarting cannot take them with it.
//
// A colleague's machine came back from a restart with every workspace gone (the bar showed one unnamed
// "Desktop 1"). Reconcile recreates a desktop that vanished, by the workspace's name, so the workspaces
// themselves had to be gone from state.json: the file came back unreadable. The write was already
// write-then-rename, which protects against the APP dying mid-write and not against the MACHINE doing so.
// Windows writes file data back lazily while the rename is journalled at once, so a hard restart soon after
// a save can leave a state.json of the right length with every byte zero. Two changes, one per half of the
// problem, and the second is Petre's: "let's also have a backup for the previous file", "so that if the last
// one is not available the one before that is".
//
//   * Write flushes the new file to the disk itself before it replaces the old one, so the rename can never
//     reach the disk ahead of the bytes it points at. Measured on Petre's machine before choosing it: 6.1 ms
//     per 31 KB save with the flush against 6.2 ms without, so the flush costs nothing that shows.
//   * The file it replaces is kept as <name>.previous, and Read falls back to it when the file itself is
//     missing or unreadable. The unreadable one is moved aside as <name>.<stamp>.bak, never deleted, and
//     moving it aside is also what stops the next save rotating it over the good copy just rescued.
public static class DurableFile
{
    public static string PreviousOf(string path) => path + ".previous";

    public static void Write(string path, string content)
    {
        var tmp = path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            // UTF-8 without a byte order mark, the same bytes File.WriteAllText wrote before this.
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        if (!File.Exists(path))
        {
            File.Move(tmp, path);
            return;
        }

        // One call swaps the new file in and keeps the old one as the previous copy. Some file systems (a folder
        // redirected to a network share) refuse Replace; there the same two steps happen one after the other.
        try { File.Replace(tmp, path, PreviousOf(path), ignoreMetadataErrors: true); }
        catch (Exception e) when (e is IOException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            File.Copy(path, PreviousOf(path), overwrite: true);
            File.Move(tmp, path, overwrite: true);
        }
    }

    // `parse` turns the file's text into the value and throws when it cannot. `empty` is the answer when there
    // is nothing to read at all, the normal first run.
    public static Result<T> Read<T>(string path, Func<string, T> parse, T empty, Action<string>? trace = null)
    {
        var previous = PreviousOf(path);
        Result<T> Parse(string file) =>
            Result.Try(() => parse(File.ReadAllText(file)), e => $"Could not read {file}: {e.Message}");

        var latest = File.Exists(path) ? Parse(path) : Result.Failure<T>($"{path} is missing");
        if (latest.IsSuccess) return latest;

        if (!File.Exists(previous))
            return File.Exists(path) ? latest : Result.Success(empty);

        var fallback = Parse(previous);
        if (fallback.IsFailure)
        {
            // Nothing on disk is readable. With the file itself missing there is nothing left to protect, so this
            // is a first run; with it present, the caller's own handling of a corrupt file takes over.
            trace?.Invoke($"{previous} is unreadable too: {fallback.Error}");
            return File.Exists(path) ? latest : Result.Success(empty);
        }

        if (File.Exists(path))
        {
            var aside = $"{path}.{DateTime.Now:yyyyMMddHHmmss}.bak";
            File.Move(path, aside);
            trace?.Invoke($"{latest.Error}; kept it as {aside} and loaded the previous copy");
        }
        else trace?.Invoke($"{path} is missing; loaded the previous copy");
        return fallback;
    }
}
