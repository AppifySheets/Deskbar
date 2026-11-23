using System.Text;
using System.Text.Json;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// THE NAME A CLAUDE CODE SESSION'S TAB SHOWS, read from the session's transcript. Petre: "deskbar workspace was
// blinking but the app in it was not, why not?"
//
// Why it is needed. A session asking for the user (`attention`, RemoteControl.Open.cs) finds its VS Code window by
// its folder (EditorFolder), and on Petre's machine five windows show the TaskSpaces folder at once, one per
// worktree session. Five matches named no one window, so the workspace's row blinked and no icon glowed. What tells
// those windows apart is their title: the Claude panel's tab carries the session's name, and VS Code puts the active
// tab's name first in the window title, cut at 24 characters with an ellipsis (measured on 2026-11-23):
//
//   window title   Deskbar: tasks board, wi… - TaskSpaces - Visual Studio Code
//   session name   Deskbar: tasks board, windows and Claude sessions
//
// Where the name comes from (measured in Claude Code 2.1.330's hook schemas and in the transcripts on this machine):
//
//   * The Stop and Notification hooks do NOT carry it. Only UserPromptSubmit has a session_title field.
//   * Every hook carries transcript_path, the session's JSONL transcript, and the transcript records the name as
//     {"type":"custom-title","customTitle":...} once the session was renamed (by /rename or a hook), and as
//     {"type":"ai-title","aiTitle":...} for the name Claude Code generated. A custom title is what the tab shows
//     when there is one, so it wins over a generated one.
//   * Claude Code appends these entries again as the session goes on. In the six largest TaskSpaces transcripts
//     (11 to 134 MB) the last one sat 1 to 27 KB from the end. So the file is read from its END, a window at a
//     time, and never in full: a hook runs on every turn, and a transcript can be hundreds of megabytes.
//
// Nothing here can fail loudly. A missing, locked or unreadable transcript, or one with no title in the part read,
// is simply no name, and the caller falls back to what it did before (no window glows).
public static class SessionTitle
{
    // How much of the transcript's end is read before giving up: 64 KB first, which covers every case measured, and
    // then wider windows up to 4 MB for a session whose last title entry sits further back.
    static readonly int[] Windows = [64 * 1024, 1024 * 1024, 4 * 1024 * 1024];

    // The session's name from the transcript at `path`, or nothing.
    public static Maybe<string> Read(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Maybe<string>.None;
        try
        {
            if (!File.Exists(path)) return Maybe<string>.None;
            // ReadWrite sharing, because Claude Code has the transcript open and is still appending to it.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Windows
                .Select(size => (int)Math.Min(size, stream.Length))
                .Distinct()
                .Select(size => Last(Tail(stream, size), cutFirstLine: size < stream.Length))
                .FirstOrDefault(found => found.HasValue);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return Maybe<string>.None;
        }
    }

    // The last `size` bytes of the stream as text.
    static string Tail(FileStream stream, int size)
    {
        var buffer = new byte[size];
        stream.Seek(-size, SeekOrigin.End);
        var read = 0;
        while (read < size && stream.Read(buffer, read, size - read) is var n && n > 0) read += n;
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    // The name the lines of a transcript end on: the last custom title if there is one, else the last generated one.
    // `cutFirstLine` drops the first line, which a window into the middle of the file cuts in half. Pure, so the rule
    // is tested without a file.
    public static Maybe<string> Last(string text, bool cutFirstLine = false)
    {
        var lines = text.Split('\n').Skip(cutFirstLine ? 1 : 0)
            // A cheap filter first: almost every line is a message, and parsing all of them would cost the most.
            .Where(line => line.Contains("-title\"", StringComparison.Ordinal))
            .Select(TitleOf)
            .Where(t => t.HasValue)
            .Select(t => t.Value)
            .ToList();
        var custom = lines.Where(t => t.Custom).Select(t => t.Name).LastOrDefault();
        var any = lines.Select(t => t.Name).LastOrDefault();
        return (custom ?? any) is { } name ? name : Maybe<string>.None;
    }

    // One transcript line's title entry, or nothing when the line is not one (or does not parse).
    static Maybe<(string Name, bool Custom)> TitleOf(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type)) return Maybe<(string, bool)>.None;
            return type.GetString() switch
            {
                "custom-title" => Named(root, "customTitle").Map(n => (n, true)),
                "ai-title" => Named(root, "aiTitle").Map(n => (n, false)),
                _ => Maybe<(string, bool)>.None,
            };
        }
        catch (JsonException)
        {
            return Maybe<(string, bool)>.None;
        }
    }

    static Maybe<string> Named(JsonElement root, string field) =>
        root.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { } text
            && text.Trim() is { Length: > 0 } name
            ? name
            : Maybe<string>.None;
}
