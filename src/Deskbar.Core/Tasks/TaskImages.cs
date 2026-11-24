using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

/// <summary>A task's markdown taken apart: its text, and the images that were lines of their own in it.</summary>
public sealed record TaskMarkdown(string Text, IReadOnlyList<string> Images);

// IMAGES IN A TASK'S MARKDOWN (comments and the description). Design: docs/superpowers/specs/2026-11-24-task-images-design.md,
// "PR 1: pasting and showing images".
//
// An image Petre pastes is saved as a file under the state folder (%APPDATA%\Deskbar, or DESKBAR_APPDATA's), in
// task-images\<task id>\, and the markdown points at it with a line of its own:
//
//     ![image](task-images/<task id>/20261124-050607-089.png)
//
// The path is RELATIVE to the state folder on purpose. An absolute one would be wrong under DESKBAR_APPDATA, on another
// profile, and in the cloud task list the tasks-in-the-bar design plans for, and changing the stored form later would
// mean migrating every comment. Whatever a Claude session reads (`task list`, the briefing, a delivered comment) gets the
// absolute path instead (Expand), because a session hands it straight to its Read tool.
//
// What counts as one of OURS is deliberately narrow (IsOurs): under task-images/, forward slashes only, no "." or ".."
// segment, no spaces, and a picture extension. A comment may come from a session, so a crafted path must never make
// Deskbar show or open a file anywhere else; anything that is not ours stays the text it was written as. An image in the
// middle of a sentence is text too: only a whole line is an image.
public static class TaskImages
{
    /// <summary>The folder, under the state folder, that holds every task's images (one subfolder per task).</summary>
    public const string Folder = "task-images";

    // A whole line that is only an image: ![alt](path) or ![alt](<path>), spaces around allowed.
    static readonly Regex ImageLine = new(@"^\s*!\[(?<alt>[^\]\n]*)\]\(\s*<?(?<path>[^)<>\s]+)>?\s*\)\s*$", RegexOptions.Compiled);

    // A relative path of ours: task-images/ then segments of letters, digits, dot, dash and underscore, ending in a
    // picture extension. The "." and ".." segments the character class allows are refused separately (IsOurs).
    static readonly Regex OurPath = new(@"^task-images(/[A-Za-z0-9._-]+)+\.(png|jpe?g)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The image lines of ours taken out, in order; the rest is the text, without trailing blank lines.</summary>
    public static TaskMarkdown Split(string markdown)
    {
        var lines = Lines(markdown);
        var images = lines.Select(PathOf).Where(p => p.HasValue).Select(p => p.Value).ToArray();
        var text = string.Join("\n", lines.Where(line => PathOf(line).HasNoValue)).TrimEnd();
        return new TaskMarkdown(images.Length == 0 ? (markdown ?? "") : text, images);
    }

    /// <summary>
    /// The text, a blank line, then one image line per image in order. Images alone have no blank line in front;
    /// text alone is the text as it was.
    /// </summary>
    public static string Join(string text, IEnumerable<string> images)
    {
        var lines = images.Select(path => $"![image]({path})").ToArray();
        var body = (text ?? "").TrimEnd();
        return lines.Length == 0 ? text ?? ""
            : body.Length == 0 ? string.Join("\n", lines)
            : body + "\n\n" + string.Join("\n", lines);
    }

    /// <summary>Whether a relative path is one of Deskbar's own task images (see the header for why so narrow).</summary>
    public static bool IsOurs(string relative) =>
        OurPath.IsMatch(relative ?? "") && relative!.Split('/').All(segment => segment is not ("." or ".."));

    /// <summary>
    /// The relative path a new image gets: named by the moment it was saved, in universal time to the millisecond, with
    /// "-index" for the second and later images saved together, so one save never names two files alike.
    /// </summary>
    public static string RelativeFor(Guid taskId, DateTimeOffset at, int index, bool jpeg) =>
        $"{Folder}/{taskId}/{at.UtcDateTime:yyyyMMdd-HHmmss-fff}{(index > 0 ? $"-{index}" : "")}.{(jpeg ? "jpg" : "png")}";

    /// <summary>The absolute path of one of ours under the state folder, or none for anything else.</summary>
    public static Maybe<string> Resolve(string relative, string stateDir) =>
        IsOurs(relative) ? Path.Combine([stateDir, .. relative.Split('/')]) : Maybe<string>.None;

    /// <summary>The markdown with every image line of ours pointing at its absolute path, for a session to read.</summary>
    public static string Expand(string markdown, string stateDir) =>
        string.Join("\n", Lines(markdown).Select(line =>
            ImageLine.Match(line) is { Success: true } m && Resolve(m.Groups["path"].Value, stateDir) is { HasValue: true } path
                ? $"![{m.Groups["alt"].Value}](<{path.Value}>)"
                : line));

    static string[] Lines(string markdown) => (markdown ?? "").Replace("\r\n", "\n").Split('\n');

    // The relative path of an image line of ours, or none for any other line.
    static Maybe<string> PathOf(string line) =>
        ImageLine.Match(line) is { Success: true } m && IsOurs(m.Groups["path"].Value) ? m.Groups["path"].Value : Maybe<string>.None;
}
