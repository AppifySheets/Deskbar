using System.Text.Json;
using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// SKILLS IN THE ADD BOX (task 3a872301; Petre: "let me do a /skill in the workspace selected", then "it should propose
// what skills i have in that workspace. can you get it through claude? so that we don't invent skills"). Typing "/" at
// the start of the bar's add box lists the Claude Code skills of the workspace on the chip, filtered as he types; Tab
// or Enter puts the chosen one in the box, and "Add and start Claude" then starts a session whose first message runs it.
//
// WHERE THE LIST COMES FROM: Claude Code itself, never Deskbar's own reading of skill folders. Every `claude -p` run
// with `--output-format stream-json --verbose` prints, before it sends anything to the model, one line
// {"type":"system","subtype":"init",...} whose "skills" array is every skill that session would have in its folder:
// the user's own (~/.claude/skills), the project's (<repo>/.claude/skills), the plugins' ("superpowers:brainstorming")
// and Claude Code's bundled ones. Measured 2026-11-26 on claude 2.1.332: 108 skills in E:\repos\TaskSpaces with
// refresh-pr among them, the same 108 less refresh-pr plus update-site in deskbar-site, the line about 3 s after the
// start. Pointed at a closed port (ANTHROPIC_BASE_URL=http://127.0.0.1:9) the same line still came with all 108, so it
// is printed before any model call: the probe reads it, ends the process, and costs nothing on the account.
//
// "skills" IS NOT EVERY SKILL, SO "slash_commands" IS READ TOO (task b3fb4e8c, "/skills don't show latest skills, like
// /iam-check-data"). Measured 2026-11-28 on claude 2.1.332 in E:\repos\personalized-health: its .claude/skills holds 11
// iam-* skills, and "slash_commands" names all 11, but "skills" names only 7. iam-check-data, iam-health-record,
// iam-parse-docs and iam-text-layer-to-lab-values are missing from it, and so is the user's own ~/.claude/skills/deskbar.
// Not the description budget (SLASH_COMMAND_TOOL_CHAR_BUDGET=200000 changed nothing) and not the file size (a 38 KB
// skill is in, a 24 KB one out); Claude Code does not say why. "slash_commands" is the list of what typing "/" can run
// there, so it is the complete one; the price is that it also carries the built-in commands (/compact, /clear, /model).
// Those are in every folder, the home folder too, so ClaudeSkills.Own takes them away with the user's own skills, and
// ClaudeInit.Known keeps them out of the "does a title run a skill" check. Every name in "skills" is also in
// "slash_commands" (same measurement), so the two together lose nothing either list had.
//
// WHAT A SKILL TITLE STARTS: a message that begins "/<skill> <words>" runs the skill even with more lines under it, and
// everything after the name becomes the skill's arguments (measured the same day with /humanizer, the transcript reads
// <command-name>/humanizer</command-name> with the task's join lines inside <command-args>). So a task titled
// "/refresh-pr 245" starts a session whose first line is that title, and the usual "Deskbar task ... join" lines ride
// along under it (ClaudeStart.FirstMessage).
//
// Everything here is pure: the probe that runs claude is Deskbar.Windows' ClaudeSkillProbe, and the popup the App's
// SkillCompletion.
public static class ClaudeSkills
{
    /// <summary>
    /// The arguments of the probe run: a headless run (so Deskbar's own hooks stay quiet, ClaudeHookEvent.IsHeadless)
    /// that prints its init line as JSON, saves no transcript, and could do nothing if it ever reached the model (no
    /// tools, one turn, the cheapest model). The probe ends it once the init line is read, and points it at a closed
    /// port (<see cref="NoModel"/>) as well, so it never does.
    /// </summary>
    public static IReadOnlyList<string> ProbeArguments =>
        ["-p", "List nothing.", "--output-format", "stream-json", "--verbose", "--max-turns", "1", "--model", "haiku",
            "--tools", "", "--no-session-persistence"];

    /// <summary>
    /// The API address the probe runs with: a port nothing listens on, so even a probe that was not ended in time
    /// fails at its first request instead of spending anything.
    /// </summary>
    public const string NoModel = "http://127.0.0.1:9";

    /// <summary>
    /// The skills and commands named by one line of the probe's output, when it is the init line; none for any other
    /// line, or a line that is not JSON. The init line needs its "skills" array; "slash_commands" is read when present
    /// and taken as empty when not, so a Claude Code that stops printing it lists what it did before this change.
    /// </summary>
    public static Maybe<ClaudeInit> FromInit(string line)
    {
        try
        {
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            return root.ValueKind == JsonValueKind.Object
                   && root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "system"
                   && root.TryGetProperty("subtype", out var subtype) && subtype.ValueKind == JsonValueKind.String && subtype.GetString() == "init"
                   && root.TryGetProperty("skills", out var skills) && skills.ValueKind == JsonValueKind.Array
                ? new ClaudeInit(Names(skills),
                    root.TryGetProperty("slash_commands", out var commands) && commands.ValueKind == JsonValueKind.Array
                        ? Names(commands)
                        : [])
                : Maybe<ClaudeInit>.None;
        }
        catch (JsonException)
        {
            return Maybe<ClaudeInit>.None;
        }
    }

    // The strings of one of the init line's arrays that can follow a "/", each once, in Claude Code's order.
    static IReadOnlyList<string> Names(JsonElement array) =>
        array.EnumerateArray()
            .Where(s => s.ValueKind == JsonValueKind.String)
            .Select(s => s.GetString()!)
            .Where(IsSkillName)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    // A skill's name as Claude Code accepts it after "/": letters, digits, '-', '_', '.', and ':' between a plugin's name
    // and its skill's. Anything else (a space, a second '/') ends the name, which is also what keeps a path such as
    // "/etc/hosts is wrong" from being read as a skill called etc.
    static readonly Regex Name = new(@"^[A-Za-z0-9][A-Za-z0-9_.:\-]*\z", RegexOptions.Compiled);

    static bool IsSkillName(string name) => Name.IsMatch(name);

    // What the box holds while a skill is being picked: "/" and the start of a name, with the caret in it.
    static readonly Regex Typing = new(@"^/([A-Za-z0-9_.:\-]*)\z", RegexOptions.Compiled);

    /// <summary>
    /// What is being looked up: the part of a name typed after the "/" that opens the box, while the caret is still in
    /// it (nothing typed after it yet on that line). None for any other text, so the list shows only while the first
    /// word is being typed and goes once a space follows it.
    /// </summary>
    public static Maybe<string> Query(string text, int caret) =>
        caret >= 1 && caret <= text.Length
        && (caret == text.Length || char.IsWhiteSpace(text[caret]))
        && Typing.Match(text[..caret]) is { Success: true } typed
            ? typed.Groups[1].Value
            : Maybe<string>.None;

    /// <summary>
    /// The skills that match what is typed, best first: names that start with it, then names whose part after a
    /// plugin's "plugin:" starts with it, then names that merely contain it, each alphabetically. Case does not matter.
    /// An empty query matches every skill.
    /// </summary>
    public static IReadOnlyList<SkillChoice> Match(IEnumerable<SkillChoice> skills, string query) =>
        skills
            .Select(skill => (skill, rank: Rank(skill.Name, query)))
            .Where(x => x.rank < 3)
            .OrderBy(x => x.rank)
            .ThenBy(x => x.skill.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.skill)
            .ToList();

    static int Rank(string name, string query) =>
        name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0
        : name.IndexOf(':') is var colon and >= 0 && name[(colon + 1)..].StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 1
        : name.Contains(query, StringComparison.OrdinalIgnoreCase) ? 2
        : 3;

    /// <summary>
    /// A repo's own skills: those Claude Code lists in its folder and not in the user's home folder, where it lists the
    /// user's, the plugins' and its own bundled ones but none of a repo's (SkillCatalog.For says why only these show).
    /// </summary>
    public static IReadOnlyList<string> Own(IReadOnlyList<string> inFolder, IReadOnlyList<string> everywhere) =>
        inFolder.Except(everywhere, StringComparer.Ordinal).ToList();

    /// <summary>
    /// One list out of each folder's: a skill every folder has (the user's, a plugin's, Claude Code's) once and plainly,
    /// a skill only some folders have (a project's) with those folders, so the box can say which repo it comes from and a
    /// start can go there. Folders whose probe failed are left out by the caller, not passed here.
    /// </summary>
    public static IReadOnlyList<SkillChoice> Merge(IReadOnlyList<(string Folder, IReadOnlyList<string> Skills)> folders) =>
        folders
            .SelectMany(f => f.Skills)
            .Distinct(StringComparer.Ordinal)
            .Select(name => (name, inFolders: folders.Where(f => f.Skills.Contains(name)).Select(f => f.Folder).ToList()))
            .Select(x => new SkillChoice(x.name, x.inFolders.Count == folders.Count ? [] : x.inFolders))
            .ToList();

    /// <summary>
    /// The text after a pick: the typed "/part" becomes "/skill " and whatever followed the caret stays, with the caret
    /// after the space, ready for the skill's arguments.
    /// </summary>
    public static (string Text, int Caret) Complete(string text, int caret, string skill)
    {
        var rest = text[Math.Clamp(caret, 0, text.Length)..].TrimStart(' ');
        var head = $"/{skill} ";
        return (head + rest, head.Length);
    }

    // A title that starts with a skill: "/" then a name, then a space or the end of the title.
    static readonly Regex Leading = new(@"^/([A-Za-z0-9][A-Za-z0-9_.:\-]*)(?=\s|$)", RegexOptions.Compiled);

    /// <summary>The skill a task's title starts with ("/refresh-pr 245" names refresh-pr); none for any other title.</summary>
    public static Maybe<string> SkillOf(string title) =>
        Leading.Match(title) is { Success: true } m ? m.Groups[1].Value : Maybe<string>.None;
}

/// <summary>
/// What Claude Code's init line says one folder has: <paramref name="Skills"/> is its "skills" array, which leaves some
/// skills out, and <paramref name="Commands"/> its "slash_commands", which has every skill and the built-in commands
/// besides (the header of <see cref="ClaudeSkills"/> has the measurement).
/// </summary>
public sealed record ClaudeInit(IReadOnlyList<string> Skills, IReadOnlyList<string> Commands)
{
    /// <summary>Everything typing "/" can run in the folder: both lists, each name once. Built-ins included.</summary>
    public IReadOnlyList<string> All => Skills.Union(Commands, StringComparer.Ordinal).ToList();

    /// <summary>
    /// The names a task title may run as a skill here (ClaudeSessions.KnowsSkill, SkillFolders): the "skills" array,
    /// plus the commands the home folder does not have, which are the folder's own skills the array left out. A built-in
    /// such as /logout is in every folder's commands and never in "skills", so it is never one of these. Without the
    /// home folder's answer, only the "skills" array counts, as before the commands were read.
    /// </summary>
    public IReadOnlyList<string> Known(Maybe<ClaudeInit> home) =>
        home.Match(
            atHome => Skills.Union(Commands.Except(atHome.Commands, StringComparer.Ordinal), StringComparer.Ordinal).ToList(),
            () => Skills);
}

/// <summary>
/// A skill as the add box lists it: its name, and the folders that have it when only some of the workspace's do (a
/// project's own skill); empty when every folder has it.
/// </summary>
public sealed record SkillChoice(string Name, IReadOnlyList<string> Folders);
