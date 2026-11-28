using System.Collections.Concurrent;
using CSharpFunctionalExtensions;
using Deskbar.Core.Tasks;
using Deskbar.Windows.Claude;

namespace Deskbar.App;

// THE SKILLS OF EACH FOLDER, AS CLAUDE CODE LAST LISTED THEM (task 3a872301; ClaudeSkills in Core has the design).
// Asking Claude Code takes about 3 s a folder (ClaudeSkillProbe), too slow to wait for at every "/" typed in the add
// box, and skills change rarely. So a folder is asked once, its answer is kept for the app's life, and an answer older
// than Fresh is handed out as it is while a new probe runs behind it (the next "/" sees the new one). Only the first
// "/" in a folder since Deskbar started waits for Claude, and the box says it is reading meanwhile.
//
// A failed probe (claude missing, the folder gone) is not kept: "never cache a lookup failure that can succeed later"
// (CLAUDE.md, Traps). Its folder is left out of the merged list and asked again next time.
//
// Also read by "Start Claude on this" (ClaudeTaskActions.SkillFolders): a task titled with a project's skill starts in
// the folder that has it, from what is kept here, without asking Claude again.
sealed class SkillCatalog
{
    // How long an answer counts as fresh; older ones are used, and refreshed behind the scenes.
    static readonly TimeSpan Fresh = TimeSpan.FromMinutes(10);

    // How long one probe may take before it is given up (the init line came after about 3 s, measured).
    static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    // Keyed by folder, case-insensitively, without a trailing separator (ClaudeStart spells folders both ways).
    readonly ConcurrentDictionary<string, (DateTime At, ClaudeInit Init)> known = new(StringComparer.OrdinalIgnoreCase);

    // The probe running for a folder, so two "/" in quick succession share one. Read and written under its own lock.
    readonly Dictionary<string, Task<Result<ClaudeInit>>> running = new(StringComparer.OrdinalIgnoreCase);

    static string Key(string folder) => folder.TrimEnd('\\', '/');

    // The user's home folder, where Claude Code lists what every folder has and nothing of any repo's (For says why).
    static string Home => Key(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>
    /// The skills of these folders' OWN, merged (ClaudeSkills.Own, ClaudeSkills.Merge): kept answers at once, the
    /// others once Claude has listed them. A folder that cannot be read is left out; none readable gives an empty list.
    /// </summary>
    //
    // ONLY THE REPO'S OWN SKILLS (Petre, trying the first build: "i only want custom skills, like those /iam not all
    // skills that are available"; /iam-check-data and /iam-deploy-staging are personalized-health's .claude/skills).
    // Claude Code lists, in any folder, the user's ~/.claude/skills, every plugin's and its own bundled ones besides the
    // repo's, over a hundred here. Asked in the user's home folder it lists all of those and nothing of any repo's, so
    // a repo's own skills are what it lists there and not at home. Both lists still come from Claude Code: nothing is
    // read from skill folders.
    //
    // BOTH OF THE INIT LINE'S LISTS (task b3fb4e8c): its "skills" array left /iam-check-data out, so a folder's list is
    // that array together with its "slash_commands" (ClaudeInit.All; ClaudeSkills has the measurement). The commands
    // carry the built-ins (/compact, /clear), and the home folder's do too, so subtracting home takes them away. When
    // the home folder cannot be asked, nothing can be subtracted: then a folder lists only its "skills" array, as before
    // the commands were read, rather than every built-in.
    public async Task<IReadOnlyList<SkillChoice>> For(IReadOnlyList<string> folders)
    {
        var home = Of(Home);
        var answers = await Task.WhenAll(folders.Select(async folder => (folder, init: await Of(folder))));
        var atHome = await home;
        return ClaudeSkills.Merge(answers
            .Where(a => a.init.HasValue)
            .Select(a => (a.folder, atHome.Match(
                everywhere => ClaudeSkills.Own(a.init.Value.All, everywhere.All),
                () => ClaudeSkills.Own(a.init.Value.Skills, []))))
            .ToList());
    }

    /// <summary>
    /// The skills kept for a folder, if it has been asked; nothing is started. Built-in commands are never among them
    /// (ClaudeInit.Known), so a title such as "/logout ..." is not taken for a skill.
    /// </summary>
    public Maybe<IReadOnlyList<string>> Kept(string folder) =>
        known.TryGetValue(Key(folder), out var kept)
            ? Maybe.From(kept.Init.Known(known.TryGetValue(Home, out var home) ? Maybe.From(home.Init) : Maybe<ClaudeInit>.None))
            : Maybe<IReadOnlyList<string>>.None;

    async Task<Maybe<ClaudeInit>> Of(string folder)
    {
        var key = Key(folder);
        if (known.TryGetValue(key, out var kept))
        {
            if (DateTime.UtcNow - kept.At > Fresh) _ = Probe(key);
            return Maybe.From(kept.Init);
        }
        var answer = await Probe(key);
        return answer.IsSuccess ? Maybe.From(answer.Value) : Maybe<ClaudeInit>.None;
    }

    // A probe still running for the folder is shared; a finished one is never handed out again, so a refresh asks anew.
    Task<Result<ClaudeInit>> Probe(string key)
    {
        lock (running)
        {
            if (running.TryGetValue(key, out var busy) && !busy.IsCompleted) return busy;
            var probe = Task.Run(() => ProbeNow(key));
            running[key] = probe;
            return probe;
        }
    }

    async Task<Result<ClaudeInit>> ProbeNow(string folder)
    {
        var answer = await ClaudeSkillProbe.Skills(folder, ProbeTimeout);
        answer
            .Tap(init => known[folder] = (DateTime.UtcNow, init))
            .Tap(init => ClickTrace.Write($"skills: {init.Skills.Count} skills, {init.Commands.Count} commands in {folder}"))
            .TapError(error => ClickTrace.Write($"skills: none read in {folder}: {error}"));
        return answer;
    }
}
