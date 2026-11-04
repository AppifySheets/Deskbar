using Deskbar.Core.Control;
using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;
using Deskbar.Core.Rehydration;

namespace Deskbar.Core.QuickFind;

// One entry of the Start menu's full app list (shell:AppsFolder). ParsingName is what the shell launches it by:
// an AppUserModelID ("Microsoft.Windows.Photos_8wekyb3d8bbwe!App") for a packaged app, a path-like name for a
// desktop one. TargetPath is the exe a desktop app's shortcut points at, null for a packaged app. Measured on
// Petre's machine: 494 entries (docs/superpowers/notes/2026-11-04-quick-find-probes.md).
public sealed record StartMenuApp(string Name, string ParsingName, string? TargetPath);

// WHAT QUICK FIND COULD START (docs/superpowers/specs/2026-11-04-quick-find-design.md, "Launching"). Petre:
// "this should be a quick launcher as well. if a window isn't opened, it should show me, including which
// deskbar it will run in, before it runs".
//
// Two sources, and the roster is not a clean list of command lines. Of the 29 entries in Petre's real
// state.json, eleven were Teams with throwaway session arguments, Photos and Snipping Tool were stored under
// versioned WindowsApps paths that cannot be started, and others were ApplicationFrameHost or protocol
// activations. So:
//
//   * One row per PROGRAM, started bare. Session arguments are never replayed.
//   * Plus one row per CONTENT argument: an argument naming a file or folder that exists, like
//     `rider64 X.sln`. That row starts the program with just that argument.
//   * A packaged program (under \WindowsApps\) starts through its Start menu entry, by AppUserModelID, never by
//     its stored path; with no matching entry it gets no row. ApplicationFrameHost says nothing about the app
//     it framed, so it never gets a row.
//   * A program counts as running if any of its windows is open, whatever its arguments, and then it has no
//     row: its windows are already listed, and Enter must never start a second copy by accident. A content
//     row disappears only while a window with that content is open.
//   * Then every Start menu program the roster does not cover and that is not running. Only programs: an
//     AppUserModelID or an .exe target, and nothing named "Uninstall".
//
// `destinationOf` says where a start would land for a program path (or an AppUserModelID) and command line,
// from the placement chain the app runs on a new window; the row shows it before anything runs.
public static class LaunchCatalog
{
    public static IReadOnlyList<FindResult> Of(
        IReadOnlyList<InventoryEntry> roster,
        IReadOnlyList<StartMenuApp> startMenu,
        IReadOnlyList<WindowInfo> running,
        Func<string, bool> pathExists,
        Func<string, string?, FindPlace> destinationOf)
    {
        var programs = startMenu.Where(IsProgram).ToList();
        var runningPrograms = running.Where(w => w.ProcessPath is not null).Select(w => ProgramOf(w.ProcessPath!)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var remembered = roster
            .Where(e => !IsFrameHost(e.ProcessPath))
            .GroupBy(e => ProgramOf(e.ProcessPath), StringComparer.OrdinalIgnoreCase)
            .SelectMany(program => Remembered(program.Key, program.ToList(), programs, running, runningPrograms, pathExists, destinationOf))
            .ToList();

        var covered = roster.Select(e => ProgramOf(e.ProcessPath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fromStartMenu = programs
            .Where(app => !covered.Contains(ProgramOf(app)) && !runningPrograms.Contains(ProgramOf(app)))
            .DistinctBy(ProgramOf, StringComparer.OrdinalIgnoreCase)
            .Select(app => Row($"s:{app.ParsingName}", FindKind.StartMenu, app.Name, LaunchOf(app), app.TargetPath,
                destinationOf(app.TargetPath ?? app.ParsingName, null)));

        return remembered.Concat(fromStartMenu).ToList();
    }

    static IEnumerable<FindResult> Remembered(
        string program,
        IReadOnlyList<InventoryEntry> entries,
        IReadOnlyList<StartMenuApp> startMenu,
        IReadOnlyList<WindowInfo> running,
        IReadOnlySet<string> runningPrograms,
        Func<string, bool> pathExists,
        Func<string, string?, FindPlace> destinationOf)
    {
        var path = entries[0].ProcessPath;
        var entry = startMenu.FirstOrDefault(app => string.Equals(ProgramOf(app), program, StringComparison.OrdinalIgnoreCase));
        var packaged = IsPackaged(path);
        if (packaged && entry is null) yield break;

        var exe = Path.GetFileNameWithoutExtension(path);
        var bare = packaged ? LaunchOf(entry!) : new LaunchTarget(new ProgramLaunch(path, [], null), exe);

        if (!runningPrograms.Contains(program))
            yield return Row($"r:{program}", FindKind.Remembered, entry?.Name ?? exe, bare, path, destinationOf(path, null));

        // Content rows only for programs started by path: a packaged app's arguments are activations, not files.
        if (packaged) yield break;
        var contents = entries
            .Select(e => Arguments(CommandLines.ArgumentsOf(e.CommandLine, path)).FirstOrDefault(pathExists))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var content in contents.Where(c => !running.Any(w => SameProgram(w, program) && (w.CommandLine ?? "").Contains(c, StringComparison.OrdinalIgnoreCase))))
            yield return Row($"r:{program}|{content}", FindKind.Remembered, Path.GetFileName(content.TrimEnd('\\', '/')),
                new LaunchTarget(new ProgramLaunch(path, [content], null), exe), path, destinationOf(path, $"\"{path}\" \"{content}\""),
                detailPrefix: exe);
    }

    static FindResult Row(string key, FindKind kind, string title, LaunchTarget launch, string? iconPath, FindPlace place, string? detailPrefix = null)
    {
        var detail = detailPrefix is null ? "not running" : $"{detailPrefix}, not running";
        return new FindResult(key, kind, title, place, $"{title} {detail} {place.Label}", Detail: detail, IconPath: iconPath, Launch: launch);
    }

    // A packaged app is started through the shell by its AppUserModelID; its window then belongs to
    // ApplicationFrameHost or the app's own exe, never to anything it was started as, so there is nothing to
    // expect it by (probes, decision c). A desktop app is started by its exe and expected under that exe's name.
    static LaunchTarget LaunchOf(StartMenuApp app) =>
        app.TargetPath is { } target
            ? new LaunchTarget(new ProgramLaunch(target, [], null), Path.GetFileNameWithoutExtension(target))
            : new LaunchTarget(new ProgramLaunch("explorer.exe", [$@"shell:AppsFolder\{app.ParsingName}"], null), null);

    static bool IsProgram(StartMenuApp app) =>
        !app.Name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)
        && (app.TargetPath is { } target
            ? target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(target).Contains("unins", StringComparison.OrdinalIgnoreCase)
            : app.ParsingName.Contains('!'));

    // A program's identity across versions: a packaged app's family name (its WindowsApps folder changes with
    // every update, Photos_2025.11010... today, something else next week), a desktop app's exe path.
    static string ProgramOf(string processPath) =>
        IsPackaged(processPath) && Family(processPath) is { } family ? family : processPath;

    static string ProgramOf(StartMenuApp app) =>
        app.TargetPath ?? (app.ParsingName.Split('!') is [var family, ..] ? family : app.ParsingName);

    static bool SameProgram(WindowInfo window, string program) =>
        window.ProcessPath is { } path && string.Equals(ProgramOf(path), program, StringComparison.OrdinalIgnoreCase);

    static bool IsPackaged(string path) => path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);

    static bool IsFrameHost(string path) => Path.GetFileName(path).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);

    // "...\WindowsApps\Microsoft.Windows.Photos_2025.11010.4001.0_x64__8wekyb3d8bbwe\Photos.exe" becomes
    // "Microsoft.Windows.Photos_8wekyb3d8bbwe": the package name (before the first '_') and the publisher id
    // (after "__"), which is what an AppUserModelID starts with.
    static string? Family(string path)
    {
        var folder = path.Split('\\').SkipWhile(p => !p.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault();
        if (folder is null) return null;
        var nameEnd = folder.IndexOf('_');
        var publisher = folder.LastIndexOf("__", StringComparison.Ordinal);
        return nameEnd < 0 || publisher < 0 ? null : $"{folder[..nameEnd]}_{folder[(publisher + 2)..]}";
    }

    // Arguments split the way Windows programs split them for the common cases: on spaces, with double quotes
    // grouping. Enough to find a path argument; nothing here is replayed except a path that exists.
    internal static IReadOnlyList<string> Arguments(string arguments)
    {
        var found = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var c in arguments)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0) found.Add(current.ToString());
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) found.Add(current.ToString());
        return found;
    }
}
