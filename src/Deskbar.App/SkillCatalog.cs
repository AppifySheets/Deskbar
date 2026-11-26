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
    readonly ConcurrentDictionary<string, (DateTime At, IReadOnlyList<string> Skills)> known = new(StringComparer.OrdinalIgnoreCase);

    // The probe running for a folder, so two "/" in quick succession share one. Read and written under its own lock.
    readonly Dictionary<string, Task<Result<IReadOnlyList<string>>>> running = new(StringComparer.OrdinalIgnoreCase);

    static string Key(string folder) => folder.TrimEnd('\\', '/');

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
    // read from skill folders. When the home folder cannot be asked, nothing is taken away rather than guessed at.
    public async Task<IReadOnlyList<SkillChoice>> For(IReadOnlyList<string> folders)
    {
        var home = Of(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var answers = await Task.WhenAll(folders.Select(async folder => (folder, skills: await Of(folder))));
        var everywhere = (await home).GetValueOrDefault([]);
        return ClaudeSkills.Merge(answers
            .Where(a => a.skills.HasValue)
            .Select(a => (a.folder, ClaudeSkills.Own(a.skills.Value, everywhere)))
            .ToList());
    }

    /// <summary>The skills kept for a folder, if it has been asked; nothing is started.</summary>
    public Maybe<IReadOnlyList<string>> Kept(string folder) =>
        known.TryGetValue(Key(folder), out var kept) ? Maybe.From(kept.Skills) : Maybe<IReadOnlyList<string>>.None;

    async Task<Maybe<IReadOnlyList<string>>> Of(string folder)
    {
        var key = Key(folder);
        if (known.TryGetValue(key, out var kept))
        {
            if (DateTime.UtcNow - kept.At > Fresh) _ = Probe(key);
            return Maybe.From(kept.Skills);
        }
        var answer = await Probe(key);
        return answer.IsSuccess ? Maybe.From(answer.Value) : Maybe<IReadOnlyList<string>>.None;
    }

    // A probe still running for the folder is shared; a finished one is never handed out again, so a refresh asks anew.
    Task<Result<IReadOnlyList<string>>> Probe(string key)
    {
        lock (running)
        {
            if (running.TryGetValue(key, out var busy) && !busy.IsCompleted) return busy;
            var probe = Task.Run(() => ProbeNow(key));
            running[key] = probe;
            return probe;
        }
    }

    async Task<Result<IReadOnlyList<string>>> ProbeNow(string folder)
    {
        var answer = await ClaudeSkillProbe.Skills(folder, ProbeTimeout);
        answer
            .Tap(skills => known[folder] = (DateTime.UtcNow, skills))
            .Tap(skills => ClickTrace.Write($"skills: {skills.Count} in {folder}"))
            .TapError(error => ClickTrace.Write($"skills: none read in {folder}: {error}"));
        return answer;
    }
}
