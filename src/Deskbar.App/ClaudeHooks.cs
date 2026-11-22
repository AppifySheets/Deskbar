using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Deskbar.App;

// Deskbar's entries in Claude Code's ~/.claude/settings.json (see ClaudeSkill for why they exist): two that ask for the
// user (Notification, Stop) while "Teach Claude Code about Deskbar" is on, and three more (SessionStart,
// UserPromptSubmit, SessionEnd) while Claude Code sessions also keep their tasks (AppState.ClaudeTasksInEffect; design
// docs/superpowers/specs/2026-11-22-tasks-board-design.md, "Who gets it"). All five run the same command, the hook
// script, which hands the event's JSON to `claude-hook`; the event's name is in the JSON, so one script serves them all.
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
    // Notification: a permission prompt, or a session left waiting for input. Stop: a reply finished. Both ask for the
    // user, so they stay while the skill is on whatever the task setting says.
    static readonly string[] AskEvents = ["Notification", "Stop"];

    // A session starting (or resuming, clearing, compacting), each prompt Petre sends, and a session ending: how a session
    // joins or creates its task and keeps it (Task 8). No matcher, so SessionStart fires for every source.
    static readonly string[] TaskEvents = ["SessionStart", "UserPromptSubmit", "SessionEnd"];

    static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        // Keep non-ASCII and characters like + and < as they are rather than \u-escaping a file people read.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // True when the file now matches what was asked: hooks added or removed, nothing to do, or no file with the setting
    // off. False only when the file was left alone because it cannot be parsed (see above), so the caller can tell.
    // `on` is "Teach Claude Code about Deskbar"; `tasks` adds the task events, and only counts while `on` does, so the
    // teaching turned off takes every entry of Deskbar's.
    internal static bool Sync(bool on, bool tasks, string settingsPath, string command)
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

        if (!Apply(root, Wanted(on, tasks), command)) return true;

        if (original is not null)
        {
            var backup = settingsPath + ".deskbar-backup";
            if (!File.Exists(backup)) File.WriteAllText(backup, original);
        }
        var temporary = settingsPath + ".deskbar-new";
        File.WriteAllText(temporary, root.ToJsonString(Indented));
        File.Move(temporary, settingsPath, overwrite: true);
        ClickTrace.Write($"claude hooks {(on ? $"set ({(tasks ? "with" : "without")} the task events) in" : "removed from")} {settingsPath}");
        return true;
    }

    // Each event of Deskbar's, and whether its entry should be there.
    static IReadOnlyList<(string Name, bool On)> Wanted(bool on, bool tasks) =>
        [.. AskEvents.Select(name => (name, on)), .. TaskEvents.Select(name => (name, on && tasks))];

    // The change itself, on the parsed file: true when anything changed. Public to the tests through Sync.
    static bool Apply(JsonObject root, IReadOnlyList<(string Name, bool On)> wanted, string command)
    {
        var changed = false;
        var hooks = root["hooks"] as JsonObject;

        foreach (var (name, on) in wanted)
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
            // An event left empty by taking ours out goes too; one the user left empty is theirs and stays.
            else if (ours.Count > 0 && entries is { Count: 0 })
                hooks!.Remove(name);
        }

        // The same for the whole "hooks" object, once nothing of anyone's is left in it.
        if (changed && hooks is { Count: 0 }) root.Remove("hooks");
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
