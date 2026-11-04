using Deskbar.Core.Control;
using Deskbar.Core.Domain;
using Deskbar.Core.Persistence;
using Deskbar.Core.Rehydration;

namespace Deskbar.Core.QuickFind;

// One entry of the Start menu's full app list (shell:AppsFolder). ParsingName is what the shell launches it by:
// an AppUserModelID ("Microsoft.Windows.Photos_8wekyb3d8bbwe!App") for a packaged app or a PWA, a path-like
// name for a desktop shortcut. TargetPath is the exe a desktop shortcut points at, null otherwise. Measured on
// Petre's machine: 494 entries (docs/superpowers/notes/2026-11-04-quick-find-probes.md).
public sealed record StartMenuApp(string Name, string ParsingName, string? TargetPath);

// Everything the catalogue needs, handed in by the app so the rules are tested without Windows:
//   Roster          every remembered app (AppState.Inventory's entries)
//   StartMenu       the Start menu's app list
//   Running         every open window, with its process path and command line
//   RunningAppIds   the AppUserModelIDs of open windows: a PWA's window belongs to the browser and a framed
//                   app's to ApplicationFrameHost, so only this says they are open
//   Starting        exe names a start is still waiting for (WorkspaceManager.AwaitedProcesses)
//   PathExists      whether a local file or folder exists
//   DestinationOf   where a start of this program and command line would land, as the row shows it
//   IsSelf          Deskbar's own Start menu entry
public sealed record LaunchFacts(
    IReadOnlyList<InventoryEntry> Roster,
    IReadOnlyList<StartMenuApp> StartMenu,
    IReadOnlyList<WindowInfo> Running,
    IReadOnlySet<string> RunningAppIds,
    IReadOnlySet<string> Starting,
    Func<string, bool> PathExists,
    Func<string, string?, FindPlace> DestinationOf,
    Func<StartMenuApp, bool> IsSelf);

// WHAT QUICK FIND COULD START (docs/superpowers/specs/2026-11-04-quick-find-design.md, "Launching"). Petre:
// "this should be a quick launcher as well. if a window isn't opened, it should show me, including which
// deskbar it will run in, before it runs".
//
// Rebuilt after a review measured the first version against Petre's real Start menu and roster:
//
//   * ONE ROW PER START MENU ENTRY, launched through the shell by that entry (explorer.exe
//     shell:AppsFolder\<entry>), so the shortcut's own arguments and working folder come along. The first
//     version started the target exe bare and kept one row per exe, so "Electrum Testnet" started the mainnet
//     wallet and "Command Prompt" vanished behind one of the eleven shortcuts on cmd.exe.
//   * A remembered program with no Start menu entry gets one bare row, started by its exe. Session arguments
//     (eleven Teams entries differed only in --restart-count) are never replayed.
//   * One row per CONTENT argument a remembered program was started with: an existing local file or folder,
//     like `rider64 X.sln`, never an option's value or an exe repeated on the line.
//   * RUNNING means any window of a process with the same EXE NAME (a newer Rider in another folder is still
//     Rider), or a window carrying the entry's AppUserModelID (PWAs, framed apps), or a start still waiting
//     for its window. Such programs get no row: Enter must never start a second copy.
//   * Packaged programs are held to their row's workspace by the exe their window really belongs to
//     (ms-teams, Photos), known from the roster; ApplicationFrameHost entries say nothing and are skipped.
//   * Deskbar never lists itself: starting its own Store package here would perform the autostart takeover.
//
// A launch row matches on its name, its exe and its content path, never on "not running" or its workspace,
// so one common letter does not match every program on the machine.
public static class LaunchCatalog
{
    public static IReadOnlyList<FindResult> Of(LaunchFacts facts)
    {
        var runningExes = facts.Running.Where(w => w.ProcessPath is not null).Select(w => Path.GetFileName(w.ProcessPath!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var programs = facts.Roster
            .Where(e => !IsFrameHost(e.ProcessPath))
            .GroupBy(e => ProgramOf(e.ProcessPath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<InventoryEntry>)g.ToList(), StringComparer.OrdinalIgnoreCase);
        var menu = facts.StartMenu.Where(IsProgram).Where(app => !facts.IsSelf(app)).ToList();

        var menuRows = menu.Select(app => MenuRow(app, programs.GetValueOrDefault(ProgramOf(app)), facts, runningExes));
        var bareRows = programs.Where(p => !menu.Any(app => string.Equals(ProgramOf(app), p.Key, StringComparison.OrdinalIgnoreCase)))
            .Select(p => BareRow(p.Value, facts, runningExes));
        var contentRows = programs.Values.Where(entries => !IsPackaged(entries[0].ProcessPath)).SelectMany(entries => ContentRows(entries, facts));

        return menuRows.Concat(bareRows).OfType<FindResult>().Concat(contentRows)
            .Where(row => row.Launch?.ExpectedProcess is not { } expected || !facts.Starting.Contains(expected))
            .ToList();
    }

    static FindResult? MenuRow(StartMenuApp app, IReadOnlyList<InventoryEntry>? remembered, LaunchFacts facts, IReadOnlySet<string> runningExes)
    {
        var exe = app.TargetPath ?? remembered?[0].ProcessPath;
        if (exe is not null && runningExes.Contains(Path.GetFileName(exe))) return null;
        if (facts.RunningAppIds.Contains(app.ParsingName)) return null;

        var home = remembered is null ? null : Representative(remembered, facts);
        var place = home is null ? facts.DestinationOf(app.TargetPath ?? app.ParsingName, null) : facts.DestinationOf(home.ProcessPath, home.CommandLine);
        var launch = new LaunchTarget(
            new ProgramLaunch("explorer.exe", [$@"shell:AppsFolder\{app.ParsingName}"], null),
            exe is null ? null : Path.GetFileNameWithoutExtension(exe));
        return Row($"s:{app.ParsingName}", remembered is null ? FindKind.StartMenu : FindKind.Remembered, app.Name, launch,
            app.TargetPath ?? home?.ProcessPath, place, searchExtra: exe is null ? "" : Path.GetFileNameWithoutExtension(exe));
    }

    // A remembered program the Start menu does not list: started by its exe, bare. Only while that exe still
    // exists (an uninstalled program keeps roster entries) and never for a packaged one, whose versioned path
    // cannot be started.
    static FindResult? BareRow(IReadOnlyList<InventoryEntry> entries, LaunchFacts facts, IReadOnlySet<string> runningExes)
    {
        if (IsPackaged(entries[0].ProcessPath)) return null;
        var path = entries.Select(e => e.ProcessPath).FirstOrDefault(facts.PathExists);
        if (path is null || runningExes.Contains(Path.GetFileName(path))) return null;
        var exe = Path.GetFileNameWithoutExtension(path);
        var home = Representative(entries, facts)!;
        return Row($"r:{path}", FindKind.Remembered, exe, new LaunchTarget(new ProgramLaunch(path, [], null), exe),
            path, facts.DestinationOf(home.ProcessPath, home.CommandLine));
    }

    static IEnumerable<FindResult> ContentRows(IReadOnlyList<InventoryEntry> entries, LaunchFacts facts)
    {
        var path = entries.Select(e => e.ProcessPath).FirstOrDefault(facts.PathExists);
        if (path is null) yield break;
        var exeFile = Path.GetFileName(path);
        var exe = Path.GetFileNameWithoutExtension(path);
        var withContent = entries
            .Select(e => (Entry: e, Content: Content(e, facts.PathExists)))
            .Where(x => x.Content is not null)
            .DistinctBy(x => x.Content, StringComparer.OrdinalIgnoreCase);
        foreach (var (entry, content) in withContent)
        {
            var open = facts.Running.Any(w => w.ProcessPath is { } p
                && Path.GetFileName(p).Equals(exeFile, StringComparison.OrdinalIgnoreCase)
                && (w.CommandLine ?? "").Contains(content!, StringComparison.OrdinalIgnoreCase));
            if (open) continue;
            yield return Row($"r:{path}|{content}", FindKind.Remembered, Path.GetFileName(content!.TrimEnd('\\', '/')),
                new LaunchTarget(new ProgramLaunch(path, [content], null), exe), path,
                facts.DestinationOf(entry.ProcessPath, entry.CommandLine), detailPrefix: exe, searchExtra: $"{exe} {content}");
        }
    }

    // The argument that names a file or folder, if any: the first one that exists, skipping exes (a program
    // that repeats its own path) and the value right after an option (`--datadir C:\data` is not content).
    static string? Content(InventoryEntry entry, Func<string, bool> exists)
    {
        var tokens = Arguments(CommandLines.ArgumentsOf(entry.CommandLine, entry.ProcessPath));
        return tokens
            .Where((token, i) => !token.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && !(i > 0 && (tokens[i - 1].StartsWith('-') || tokens[i - 1].StartsWith('/'))))
            .FirstOrDefault(exists);
    }

    // The entry whose command line best stands for starting the program: one with no arguments if there is one
    // (what a bare start produces), else the first. Its identity is what placement memory knows the app by.
    static InventoryEntry? Representative(IReadOnlyList<InventoryEntry> entries, LaunchFacts facts) =>
        entries.FirstOrDefault(e => CommandLines.ArgumentsOf(e.CommandLine, e.ProcessPath).Length == 0) ?? entries.FirstOrDefault();

    static FindResult Row(string key, FindKind kind, string title, LaunchTarget launch, string? iconPath, FindPlace place,
        string? detailPrefix = null, string searchExtra = "")
    {
        var detail = detailPrefix is null ? "not running" : $"{detailPrefix}, not running";
        return new FindResult(key, kind, title, place, $"{title} {searchExtra}".Trim(), Detail: detail, IconPath: iconPath, Launch: launch);
    }

    static bool IsProgram(StartMenuApp app) =>
        !app.Name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)
        && (app.TargetPath is { } target
            ? target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(target).Contains("unins", StringComparison.OrdinalIgnoreCase)
            : app.ParsingName.Contains('!'));

    // A program's identity: a packaged app's family name (its WindowsApps folder changes with every update), a
    // desktop app's exe path.
    static string ProgramOf(string processPath) =>
        IsPackaged(processPath) && Family(processPath) is { } family ? family : processPath;

    static string ProgramOf(StartMenuApp app) =>
        app.TargetPath ?? (app.ParsingName.Split('!') is [var family, ..] ? family : app.ParsingName);

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
