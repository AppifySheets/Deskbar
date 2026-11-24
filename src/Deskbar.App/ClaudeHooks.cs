using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Deskbar.App;

// Deskbar's entries in Claude Code's ~/.claude/settings.json (see ClaudeSkill for why they exist): two that ask for the
// user (Notification, Stop) while "Teach Claude Code about Deskbar" is on, and three more (SessionStart,
// UserPromptSubmit, SessionEnd) while Claude Code sessions also keep their tasks (AppState.ClaudeTasksInEffect; design
// docs/superpowers/specs/2026-11-22-tasks-board-design.md, "Who gets it"). Each runs a script that hands the event's
// JSON to `claude-hook`; the event's name is in the JSON.
//
// TWO SCRIPTS, NOT ONE (Ruling 35, review of Task 7, I-3). The ask events run claude-hook.cmd, the name every Deskbar
// since the hooks were added knows; the task events run claude-task-hook.cmd, a name no older build knows. That matters
// when Petre swaps back to an older Deskbar: an older build recognises its entries by "claude-hook.cmd" in the command
// and rewrites claude-hook.cmd to run `attention`, so task entries pointing at claude-hook.cmd would blink the bar on
// every session start, prompt and end. Pointing at claude-task-hook.cmd, it neither counts them as its own nor rewrites
// the script, and the script keeps running `claude-hook`, which an older Deskbar refuses as an unknown command: the
// refusal goes to stderr, the script sends that to nul and exits 0, and nothing blinks and the session reads nothing.
//
// That file is the user's, and Claude Code's, and holds everything from permissions to the model, so this is careful
// in the ways the file deserves:
//
//   * Only Deskbar's own entries are added or removed, recognised by either script's name in their command. Every
//     other hook, Petre's bell on Notification and Stop included, stays exactly where it is.
//   * Written only when something changes, so a Deskbar that starts every morning does not rewrite the file every
//     morning.
//   * Before the FIRST change, the file as it was is copied to settings.json.deskbar-backup, once.
//   * A file that does not parse as strict JSON (comments, a half-written save, a repeated key) is left alone and
//     traced: rewriting it through a parser would drop what the parser skipped. Sync then returns false.
//   * The same for a "hooks" value that is not an object, and an event's value that is not an array (review of Task 7,
//     M-3): Claude Code would not read them as hooks either, but they are the user's, so they are left as they are and
//     traced, never replaced. The other events are still kept in step, and Sync returns false.
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
    // off. False when anything was left alone because it cannot be read as hooks (see above), so the caller can tell.
    // `on` is "Teach Claude Code about Deskbar"; `tasks` adds the task events, and only counts while `on` does, so the
    // teaching turned off takes every entry of Deskbar's. `askCommand` runs claude-hook.cmd, `taskCommand`
    // claude-task-hook.cmd (the header says why two).
    //
    // `imagesRule` (ImagesRule) is the one PERMISSION entry Deskbar keeps beside its hooks, in permissions.allow, on the
    // same terms as the task events (docs/superpowers/specs/2026-11-24-task-images-design.md, "Can a session read the
    // file?"). Pictures Petre pastes into a task live in %APPDATA%\Deskbar\task-images, outside every session's folder,
    // and a session in default permission mode stops at a prompt before reading one (measured 2026-11-24; auto mode reads
    // it). A woken session stuck at a prompt defeats the point, so Petre agreed (2026-11-24) to a Read rule for that one
    // folder and nothing else. It is recognised as Deskbar's by its shape (Read(// ... /Deskbar/task-images/**)), so a
    // rule for an older state folder is replaced and the user's own rules, task-images ones included, are never touched.
    internal static bool Sync(bool on, bool tasks, string settingsPath, string askCommand, string taskCommand, string? imagesRule = null)
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

        if (root["hooks"] is { } odd && odd is not JsonObject)
        {
            ClickTrace.Write($"claude hooks: {settingsPath} left alone, its \"hooks\" is not an object");
            return false;
        }

        var (hooksChanged, hooksComplete) = Apply(root, Wanted(on, tasks, askCommand, taskCommand));
        var (ruleChanged, ruleComplete) = imagesRule is null ? (false, true) : ApplyRule(root, on && tasks, imagesRule);
        var (changed, complete) = (hooksChanged || ruleChanged, hooksComplete && ruleComplete);
        if (!changed) return complete;

        if (original is not null)
        {
            var backup = settingsPath + ".deskbar-backup";
            if (!File.Exists(backup)) File.WriteAllText(backup, original);
        }
        var temporary = settingsPath + ".deskbar-new";
        File.WriteAllText(temporary, root.ToJsonString(Indented));
        File.Move(temporary, settingsPath, overwrite: true);
        ClickTrace.Write($"claude hooks {(on ? $"set ({(tasks ? "with" : "without")} the task events) in" : "removed from")} {settingsPath}");
        return complete;
    }

    // Each event of Deskbar's, whether its entry should be there, and the command it runs.
    static IReadOnlyList<(string Name, bool On, string Command)> Wanted(bool on, bool tasks, string askCommand, string taskCommand) =>
        [.. AskEvents.Select(name => (name, on, askCommand)), .. TaskEvents.Select(name => (name, on && tasks, taskCommand))];

    // The change itself, on the parsed file: whether anything changed, and whether every event could be kept in step
    // (false when an event's value is not an array and was left alone).
    static (bool Changed, bool Complete) Apply(JsonObject root, IReadOnlyList<(string Name, bool On, string Command)> wanted)
    {
        var changed = false;
        var complete = true;
        var hooks = root["hooks"] as JsonObject;

        foreach (var (name, on, command) in wanted)
        {
            if (hooks?[name] is { } value && value is not JsonArray)
            {
                ClickTrace.Write($"claude hooks: \"{name}\" left alone, it is not an array");
                complete = false;
                continue;
            }

            var entries = hooks?[name] as JsonArray;
            var ours = entries?.Where(e => IsOurs(e)).ToList() ?? [];
            var current = ours.Count == 1 && CommandOf(ours[0]) == command;

            if (on && current) continue;

            // Anything of ours that is not exactly the current entry goes: a stale path from an older Deskbar, a task
            // event still on claude-hook.cmd from before Ruling 35, or a duplicate.
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
        return (changed, complete);
    }

    /// <summary>
    /// The Read rule for a state folder's task-images, in Claude Code's absolute-path form: "//", the drive letter in lower
    /// case, forward slashes, then "/task-images/**". C:\Users\p\AppData\Roaming\Deskbar gives
    /// Read(//c/Users/p/AppData/Roaming/Deskbar/task-images/**), the form measured to work on 2026-11-24.
    /// </summary>
    internal static string ImagesRule(string stateDir)
    {
        var full = Path.GetFullPath(stateDir).TrimEnd('\\', '/');
        var posix = char.ToLowerInvariant(full[0]) + full[2..].Replace('\\', '/');
        return $"Read(//{posix}/{Core.Tasks.TaskImages.Folder}/**)";
    }

    // The images rule in permissions.allow: added once when wanted, any other rule of Deskbar's shape taken out, and an
    // allow list or permissions object left empty by taking ours out removed with it. A "permissions" that is not an
    // object, or an "allow" that is not an array, is the user's and is left alone (complete false), as for hooks.
    static (bool Changed, bool Complete) ApplyRule(JsonObject root, bool wanted, string rule)
    {
        if (root["permissions"] is { } odd && odd is not JsonObject)
        {
            ClickTrace.Write("claude hooks: \"permissions\" left alone, it is not an object");
            return (false, false);
        }
        var permissions = root["permissions"] as JsonObject;
        if (permissions?["allow"] is { } oddAllow && oddAllow is not JsonArray)
        {
            ClickTrace.Write("claude hooks: \"permissions.allow\" left alone, it is not an array");
            return (false, false);
        }

        var allow = permissions?["allow"] as JsonArray;
        var ours = allow?.Where(IsOurRule).ToList() ?? [];
        if (wanted && ours.Count == 1 && RuleOf(ours[0]) == rule) return (false, true);

        ours.ForEach(r => allow!.Remove(r));
        if (wanted)
        {
            if (permissions is null) root["permissions"] = permissions = [];
            if (allow is null) permissions["allow"] = allow = [];
            allow.Add(rule);
        }
        else if (ours.Count > 0 && allow is { Count: 0 })
        {
            permissions!.Remove("allow");
            if (permissions.Count == 0) root.Remove("permissions");
        }
        return (wanted || ours.Count > 0, true);
    }

    // Deskbar's images rule, by its shape: Read(// ... /Deskbar/task-images/**). The user's own rules never have that
    // exact ending unless they name Deskbar's own folder, which is the one Deskbar manages.
    static bool IsOurRule(JsonNode? node) =>
        RuleOf(node) is { } rule
        && rule.StartsWith("Read(//", StringComparison.Ordinal)
        && rule.EndsWith($"/Deskbar/{Core.Tasks.TaskImages.Folder}/**)", StringComparison.OrdinalIgnoreCase);

    static string? RuleOf(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var rule) ? rule : null;

    // Deskbar's entries: a command naming either of its scripts, in Deskbar's folder.
    static bool IsOurs(JsonNode? entry) =>
        CommandOf(entry) is { } command
        && (command.Contains(ClaudeSkill.HookScriptName, StringComparison.OrdinalIgnoreCase)
            || command.Contains(ClaudeSkill.TaskHookScriptName, StringComparison.OrdinalIgnoreCase))
        && command.Contains("Deskbar", StringComparison.OrdinalIgnoreCase);

    // The command of an entry that holds exactly one command hook, which is the shape Deskbar writes; anything else is
    // not Deskbar's.
    static string? CommandOf(JsonNode? entry) =>
        (entry as JsonObject)?["hooks"] is JsonArray { Count: 1 } list
        && list[0] is JsonObject hook
        && hook["command"] is JsonValue value && value.TryGetValue<string>(out var command) ? command : null;
}
