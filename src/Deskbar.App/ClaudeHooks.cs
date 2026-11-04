using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Deskbar.App;

// Deskbar's two entries in Claude Code's ~/.claude/settings.json (see ClaudeSkill for why they exist).
//
// That file is the user's, and Claude Code's, and holds everything from permissions to the model, so this is careful
// in the ways the file deserves:
//
//   * Only Deskbar's own entries are added or removed, recognised by the hook script's name in their command. Every
//     other hook, Petre's bell on Notification and Stop included, stays exactly where it is.
//   * Written only when something changes, so a Deskbar that starts every morning does not rewrite the file every
//     morning.
//   * Before the FIRST change, the file as it was is copied to settings.json.deskbar-backup, once.
//   * A file that does not parse as strict JSON (comments, a half-written save, a repeated key) is left alone and
//     traced: rewriting it through a parser would drop what the parser skipped. Sync then returns false.
//   * A foreign entry of an odd shape (a string, null, a command that is not a string) is kept and skipped: it is not
//     Deskbar's, and it does not stop Deskbar's own from being added.
//   * Written to a temporary file and moved over the original, so a crash mid-write cannot leave half a file.
static class ClaudeHooks
{
    // Notification: a permission prompt, or a session left waiting for input. Stop: a reply finished.
    static readonly string[] Events = ["Notification", "Stop"];

    static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        // Keep non-ASCII and characters like + and < as they are rather than \u-escaping a file people read.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // True when the file now matches what was asked: hooks added or removed, nothing to do, or no file with the setting
    // off. False only when the file was left alone because it cannot be parsed (see above), so the caller can tell.
    internal static bool Sync(bool on, string settingsPath, string command)
    {
        JsonObject root;
        string? original = null;
        if (File.Exists(settingsPath))
        {
            original = File.ReadAllText(settingsPath);
            try
            {
                // A repeated key anywhere in the file is an error here, not "the last one wins": rewriting the file
                // would silently drop the other value.
                root = JsonNode.Parse(original, documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false })
                    as JsonObject ?? throw new JsonException("not an object");
            }
            catch (JsonException e)
            {
                ClickTrace.Write($"claude hooks: {settingsPath} left alone, it does not parse as JSON (or repeats a key): {e.Message}");
                return false;
            }
        }
        else if (on) root = [];
        else return true;

        if (!Apply(root, on, command)) return true;

        if (original is not null)
        {
            var backup = settingsPath + ".deskbar-backup";
            if (!File.Exists(backup)) File.WriteAllText(backup, original);
        }
        var temporary = settingsPath + ".deskbar-new";
        File.WriteAllText(temporary, root.ToJsonString(Indented));
        File.Move(temporary, settingsPath, overwrite: true);
        ClickTrace.Write($"claude hooks {(on ? "added to" : "removed from")} {settingsPath}");
        return true;
    }

    // The change itself, on the parsed file: true when anything changed. Public to the tests through Sync.
    static bool Apply(JsonObject root, bool on, string command)
    {
        var changed = false;
        var hooks = root["hooks"] as JsonObject;

        foreach (var name in Events)
        {
            var entries = hooks?[name] as JsonArray;
            var ours = entries?.Where(e => IsOurs(e)).ToList() ?? [];
            var current = ours.Count == 1 && CommandOf(ours[0]) == command;

            if (on && current) continue;

            // Anything of ours that is not exactly the current entry goes: a stale path from an older Deskbar, or a
            // duplicate.
            ours.ForEach(e => entries!.Remove(e));
            changed |= ours.Count > 0;

            if (on)
            {
                if (hooks is null) root["hooks"] = hooks = [];
                if (entries is null) hooks[name] = entries = [];
                entries.Add(new JsonObject
                {
                    ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = command }),
                });
                changed = true;
            }
            else if (entries is { Count: 0 })
                hooks!.Remove(name);
        }

        if (!on && hooks is { Count: 0 }) root.Remove("hooks");
        return changed;
    }

    static bool IsOurs(JsonNode? entry) =>
        CommandOf(entry) is { } command && command.Contains(ClaudeSkill.HookScriptName, StringComparison.OrdinalIgnoreCase)
        && command.Contains("Deskbar", StringComparison.OrdinalIgnoreCase);

    // The command of an entry that holds exactly one command hook, which is the shape Deskbar writes; anything else is
    // not Deskbar's.
    static string? CommandOf(JsonNode? entry) =>
        (entry as JsonObject)?["hooks"] is JsonArray { Count: 1 } list
        && list[0] is JsonObject hook
        && hook["command"] is JsonValue value && value.TryGetValue<string>(out var command) ? command : null;
}
