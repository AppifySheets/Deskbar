using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// TRUSTING A FOLDER FOR CLAUDE CODE, THE PURE HALF (Deskbar task 50b61120). Petre, on the workspace's "Claude folder…":
// it "should also run claude and add permissions on that choice". Claude Code keeps the folders a person has trusted in
// its .claude.json, under "projects", one entry per folder keyed by its path with forward slashes
// ("C:/Users/me/repos/Deskbar", measured 2026-11-27 on Petre's machine), with "hasTrustDialogAccepted": true once its
// trust prompt was answered yes. A background session (`claude --bg`) cannot answer that prompt, so a start in an
// untrusted folder fails, and until now Deskbar only offered a terminal where Petre answered it himself
// (ClaudeTaskActions.OfferTrust: "Deskbar never accepts trust for him").
//
// Choosing a workspace's Claude folder in a folder picker IS Petre answering it: he named that folder, for that purpose,
// a moment ago. So this, and only this, writes the yes for him. Nothing else in Deskbar trusts a folder: a folder a
// task merely guessed (ClaudeStart.NewTaskFolders) still gets the terminal and the prompt.
//
// THE KEY IS CASE-SENSITIVE. Claude Code looks a folder up by the exact text of its working directory, and the same
// folder reaches it spelled two ways: "c:/..." from VS Code, "C:/..." from a terminal or Deskbar's own start. Petre's
// file had 14 such pairs on 2026-11-27, with different answers in them. So both drive-letter spellings are marked, and
// only an exact key counts as trusted; an entry that differs in any other letter's case is a different folder to it.
//
// A DRIVE'S ROOT IS NEVER WRITTEN. Claude Code counts a folder as trusted when any folder above it is, so a yes on "E:/"
// would trust everything on the drive for good, which is not what picking it for one workspace means. The terminal's
// own prompt still asks there. The user's home folder is refused the same way by ClaudeTrustFile, which knows where
// it is.
//
// Every other key in the file is left exactly as it was, the entries' other fields included: the file is Claude Code's,
// and it holds sign-in state, tips seen and per-project history that Deskbar has no business touching. The disk half
// (reading, writing next to it and swapping in) is ClaudeTrustFile in the app.
public static class ClaudeTrust
{
    /// <summary>
    /// The key Claude Code files a folder under in <c>projects</c>: its path with forward slashes and no trailing
    /// separator, the drive's root keeping its own ("C:/").
    /// </summary>
    public static string Key(string folder)
    {
        var trimmed = folder.Trim().Replace('\\', '/').TrimEnd('/');
        // "C:" alone would mean the drive's current directory, not its root; keep the root's slash.
        return IsDrive(trimmed) ? trimmed + "/" : trimmed;
    }

    /// <summary>
    /// The keys a folder is marked under: <see cref="Key"/>, and the same with the drive letter in the other case, the
    /// one VS Code (lower) or a terminal (upper) would use. One key for a path without a drive letter.
    /// </summary>
    public static IReadOnlyList<string> Keys(string folder)
    {
        var key = Key(folder);
        if (key.Length < 2 || key[1] != ':' || !char.IsLetter(key[0])) return [key];
        var flipped = (char.IsUpper(key[0]) ? char.ToLowerInvariant(key[0]) : char.ToUpperInvariant(key[0])) + key[1..];
        return [key, flipped];
    }

    /// <summary>Whether the folder is a drive's root ("E:\", "e:/"), which is never trusted from here.</summary>
    public static bool IsDriveRoot(string folder) => IsDrive(Key(folder).TrimEnd('/'));

    /// <summary>
    /// Whether <paramref name="json"/> (the text of .claude.json) already trusts <paramref name="folder"/> under every
    /// one of its <see cref="Keys"/>, by exact key.
    /// </summary>
    public static bool IsAccepted(string? json, string folder) =>
        Parse(json).Map(root => root["projects"] is JsonObject projects
                && Keys(folder).All(key => projects.TryGetPropertyValue(key, out var entry)
                    && entry is JsonObject found
                    && found["hasTrustDialogAccepted"] is JsonValue value
                    && value.TryGetValue<bool>(out var accepted) && accepted))
            .GetValueOrDefault(false);

    /// <summary>
    /// The text of .claude.json with <paramref name="folder"/> trusted: under each of its <see cref="Keys"/>, the entry
    /// made when there is none and <c>hasTrustDialogAccepted</c> set to true on it, everything else as it was. A missing
    /// or empty text is a new file holding only that. Text that is not a JSON object is a failure, never overwritten: it
    /// may be Claude Code halfway through writing it, and the caller tries again. A drive's root is refused.
    /// </summary>
    public static Result<string> Accepted(string? json, string folder) =>
        string.IsNullOrWhiteSpace(folder) ? Result.Failure<string>("No folder to trust.")
        : IsDriveRoot(folder) ? Result.Failure<string>($"{folder} is a drive's root; trusting it would trust everything on the drive.")
        : Parse(json).ToResult(".claude.json is not a JSON object Deskbar can read, so it was left as it is.")
            .Map(root =>
            {
                var projects = root["projects"] as JsonObject ?? Replace(root, "projects");
                Keys(folder).ToList().ForEach(key => (projects[key] as JsonObject ?? Replace(projects, key))["hasTrustDialogAccepted"] = true);
                return root.ToJsonString(Written);
            });

    // How Claude Code writes the file: indented by two spaces, "\n" line ends (not Windows' "\r\n", which would rewrite
    // every line of it), and its text as it is (no \uXXXX for a Georgian name or a "+"). Keeping all three keeps the file
    // readable and a diff of it down to the lines that changed.
    static readonly JsonSerializerOptions Written = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    static bool IsDrive(string text) => text.Length == 2 && text[1] == ':' && char.IsLetter(text[0]);

    // The file's root object; none for text that is not one. Empty or missing text is a new, empty object. Any parse
    // failure is none, not an exception: a duplicate key, say, throws ArgumentException rather than JsonException, and
    // this runs from a menu click.
    static Maybe<JsonObject> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JsonObject();
        try
        {
            var root = JsonNode.Parse(json) as JsonObject;
            // Touching every member makes a duplicate key fail here, inside the catch, rather than at the first lookup.
            _ = root?.Count;
            return root ?? Maybe<JsonObject>.None;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        {
            return Maybe<JsonObject>.None;
        }
    }

    // A fresh object under the key, in place of anything there that was not one.
    static JsonObject Replace(JsonObject parent, string key)
    {
        var made = new JsonObject();
        parent[key] = made;
        return made;
    }
}
