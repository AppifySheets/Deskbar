using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;
using Deskbar.Core.Control;

namespace Deskbar.Core.Tasks;

// THE PURE HALF OF STARTING A CLAUDE SESSION FROM A TASK: what the session is told first, which folder it starts in,
// and the command that puts a Windows Terminal window on it. Design:
// docs/superpowers/specs/2026-11-23-tasks-open-claude-sessions-design.md. Nothing here starts a process or touches a
// disk, so every rule is a test; the Windows side (ClaudeCli, ProgramStarter) does the starting.
public static class ClaudeStart
{
    // Claude Code takes a first message of about this size comfortably on a command line; a long title must not
    // push the ask to join the task off the end, so the title and status line are what get cut (see FirstMessage).
    const int MaxMessage = 1000;

    // `<repo>\.claude\worktrees\<topic>`: this repo's convention for a topic's worktree. Either slash, any case.
    static readonly Regex WorktreeMarker = new(@"[\\/]\.claude[\\/]worktrees[\\/]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The first message of a new session, exactly: <c>Deskbar task &lt;id&gt;: &lt;title&gt;</c>, the status line when
    /// there is one, then the ask to join. Cut to 1000 characters; the last line is built first and kept whole, and the
    /// title and status line are cut to fit in front of it, so the session can always join the task by its full id.
    /// </summary>
    public static string FirstMessage(TaskItem task)
    {
        var join = $"Join this task (deskbar task join {task.Id}) and start on it.";
        var head = string.Join("\n", new[] { $"Deskbar task {task.Id}: {task.Title}", task.StatusLine }
            .Where(line => !string.IsNullOrWhiteSpace(line)));
        // The room in front of the last line is the limit less that line and the newline that joins them.
        var room = Math.Max(0, MaxMessage - join.Length - 1);
        // A cut between the two halves of a surrogate pair (an emoji) would leave a lone high surrogate, which is not
        // valid text on a command line; step back one so the pair goes whole or not at all.
        var cut = room > 0 && room < head.Length && char.IsHighSurrogate(head[room - 1]) ? room - 1 : room;
        return (head.Length > room ? head[..cut] : head) + "\n" + join;
    }

    // THE MODEL A TASK SESSION RUNS ON (design docs/superpowers/specs/2026-11-25-session-model-pick-design.md). Deskbar
    // names it on every start, so a task session never runs on whatever `~/.claude/settings.json` holds that day:
    // Petre's "give each claude session best model for the job, never default". One change of that setting used to
    // move every task session to the dearest model without anyone choosing it. Opus is what a week of his task
    // sessions ran on (measured, 99% of their usage), so it is what an unset choice means.
    public const string DefaultModel = "opus";

    // What `--model` takes: an alias ("opus", "sonnet", "fable") or a model's full name ("claude-opus-5-5", with a
    // "[1m]" suffix at most). Anything else, a space or a leading dash above all, could be read by `claude` as another
    // argument, so it is refused before it reaches a command line.
    static readonly Regex ModelName = new(@"^[A-Za-z0-9][A-Za-z0-9._\-\[\]]{0,63}$", RegexOptions.Compiled);

    /// <summary>A model name as `--model` takes it, trimmed, or why it is not one.</summary>
    public static Result<string> Model(string given) =>
        ModelName.IsMatch(given.Trim())
            ? given.Trim()
            : Result.Failure<string>($"'{given}' is not a model name. Give an alias such as opus, sonnet or fable, or a model's full name.");

    /// <summary>
    /// The arguments of `claude` that start a background session on the task: always with <c>--model</c>, then the
    /// first message. A plain `claude --bg --resume` keeps the model the session started with (measured: it wakes
    /// "with its saved options"), so only the start names it.
    /// </summary>
    public static IReadOnlyList<string> Arguments(TaskItem task, string model) =>
        ["--bg", "--model", model, FirstMessage(task)];

    /// <summary>
    /// Where to start the session, the likeliest first. A task that has had sessions starts at the REPO of its latest
    /// one (a new session starts at the repo, not in an old worktree, because worktrees are deleted after the merge).
    /// A task with none offers, after its parent's latest repo when it is a subtask (ParentRepo), the repos of every
    /// session of every task on its workspace (a free task matches only free tasks), the most recently used first, then
    /// the folders the workspace's editors have open.
    /// </summary>
    public static IReadOnlyList<string> Folders(TaskItem task, TaskBoard board, IEnumerable<string> editorFolders) =>
        task.Sessions.Where(s => s.Folder != "").OrderByDescending(s => s.Joined).FirstOrDefault() is { } latest
            ? [RepoOf(latest.Folder)]
            : ParentRepo(task, board)
                .Concat(board.Tasks
                    .Where(other => other.WorkspaceId.Equals(task.WorkspaceId))
                    .SelectMany(other => other.Sessions)
                    .Where(s => s.Folder != "")
                    .OrderByDescending(s => s.Joined)
                    .Select(s => RepoOf(s.Folder)))
                .Concat(editorFolders.Where(f => f != ""))
                // The first spelling of a folder wins, so the one used most recently is the one shown.
                .DistinctBy(f => f.TrimEnd('\\', '/'), StringComparer.OrdinalIgnoreCase)
                .ToList();

    // A subtask with no session of its own most likely continues in its parent's repo (subtasks spec,
    // docs/superpowers/specs/2026-11-24-subtasks-design.md, "Making subtasks"), so that repo, from the parent's latest
    // session, leads the list. Nothing for a plain task, or a parent with no sessions or no longer on the board.
    static IEnumerable<string> ParentRepo(TaskItem task, TaskBoard board) =>
        task.ParentId.Bind(p => board.Tasks.TryFirst(t => t.Id == p))
            .Bind(parent => parent.Sessions.Where(s => s.Folder != "").OrderByDescending(s => s.Joined).TryFirst())
            .Map(s => new[] { RepoOf(s.Folder) })
            .GetValueOrDefault([]);

    /// <summary>The repo a worktree folder belongs to: the part before <c>\.claude\worktrees\</c>; any other folder as it is.</summary>
    public static string RepoOf(string folder) =>
        WorktreeMarker.Match(folder) is { Success: true } marker ? folder[..marker.Index] : folder;

    /// <summary>
    /// The Windows Terminal command that opens a NEW window on a background session: the "Deskbar Claude" profile
    /// (TerminalFragment), the folder as its directory, and `claude attach` with the session's short id.
    /// </summary>
    //
    // The folder goes WITHOUT a trailing separator (Task 9): wt reads its own command line, and a folder that ends in a
    // backslash arrives as `"C:\repos\x\"`, whose last backslash escapes the closing quote. A drive's root keeps its
    // own ("C:\"), since "C:" alone means the drive's current folder.
    //
    // <paramref name="claude"/> is the program the terminal runs: the full path of claude.exe when Deskbar found one
    // (ClaudeLocator), so the window works on a machine whose PATH does not lead to it (task dbe25323), and the bare
    // name otherwise, which leaves the finding to Windows Terminal as before.
    public static ProgramLaunch AttachWindow(string shortId, string folder, string title, string claude = "claude") =>
        new("wt.exe", ["-w", "new", "--title", title, "-p", "Deskbar Claude", "-d", Bare(folder), claude, "attach", shortId], Bare(folder));

    /// <summary>
    /// The window Deskbar opens when a folder was never trusted: a NEW Windows Terminal window running plain `claude`
    /// there, with Windows Terminal's default profile, so Petre answers Claude's trust prompt himself (the spec: Deskbar
    /// never accepts trust for him). Once he has, "Start Claude on this" works in that folder. <paramref name="claude"/>
    /// as in <see cref="AttachWindow"/>.
    /// </summary>
    public static ProgramLaunch TrustWindow(string folder, string claude = "claude") =>
        new("wt.exe", ["-w", "new", "-d", Bare(folder), claude], Bare(folder));

    // WITHOUT WINDOWS TERMINAL (task abfe1db8, "win2022"). Windows Server 2022 ships no Windows Terminal, and a colleague's
    // terminal server showed what that did: "Deskbar could not open a terminal in C:\Users\...\repos\stock: Could not
    // start wt.exe ... The system cannot find the file specified." Neither the trust window nor the window on a session
    // could open. So where wt.exe is not installed (HasWindowsTerminal), Deskbar starts claude.exe ITSELF in a console
    // window of its own, the classic one Windows gives any console program, and these two say how.
    //
    // The console's title comes from CreateProcess (STARTUPINFO.lpTitle), since there is no wt to pass --title to and no
    // profile to keep it: CLAUDE_CODE_DISABLE_TERMINAL_TITLE=1 stops Claude Code from retitling the window, which is the
    // job the "Deskbar Claude" profile's suppressApplicationTitle does in Windows Terminal. The title is how Deskbar finds
    // the window again (ClaudeTaskActions.Titled), so it must stay.
    //
    // Measured 2026-11-24 on Windows 11 with a conhost probe: a classic console window reports its CLIENT as its owner
    // (GetWindowThreadProcessId gave cmd's pid, not conhost's), so this window shows as process "claude", which is the
    // name Deskbar expects it by.

    /// <summary>
    /// The console window on a background session, for a machine without Windows Terminal: claude.exe started on
    /// `attach &lt;short id&gt;` in the folder, titled <paramref name="title"/> and kept so.
    /// </summary>
    public static ConsoleLaunch AttachConsole(string shortId, string folder, string title, string claude = "claude") =>
        new(claude, ["attach", shortId], Bare(folder), Maybe.From(title));

    /// <summary>
    /// The trust window for a machine without Windows Terminal: plain claude.exe in the folder, in a console window, with
    /// Claude Code free to title it as it likes (as the default profile leaves it in Windows Terminal).
    /// </summary>
    public static ConsoleLaunch TrustConsole(string folder, string claude = "claude") =>
        new(claude, [], Bare(folder), Maybe<string>.None);

    // INSTALLING CLAUDE CODE (task 3641561d, "install claude code when start claude on this doesn't have claude installed").
    // On a machine with no claude.exe, "Start Claude on this" used to end at ClaudeLocator.NotFound: a message box telling
    // Petre (or a colleague) to open PowerShell and paste the installer's one-liner. Deskbar now offers to do that for him
    // in a window he can watch: Windows PowerShell runs Claude Code's official installer, then the claude.exe it installed,
    // in the task's folder, so the sign-in and the folder's trust prompt are answered in the same window (Deskbar never
    // answers either for him, as with TrustWindow). Once he has signed in, "Start Claude on this" works.
    //
    // Why the script is shaped as it is (measured on Petre's machine, 2026-11-24):
    //   * The installer is saved to a file and run with `&`, not piped into `iex` as its own docs show. It ends its failures
    //     with `exit 1`, and an `exit` inside `iex` or a script block closes the whole PowerShell, so the window would
    //     vanish with the reason in it. An `exit` in a script FILE ends only that file.
    //   * It goes as -EncodedCommand (base64 of UTF-16LE): wt splits its command line into commands at every ';' and
    //     ProgramStarter's start goes through cmd, which reads & | < >. Base64 has none of those.
    //   * -ExecutionPolicy Bypass for this one process, because the file is a script and Windows' default policy for
    //     Windows PowerShell refuses scripts. -NoExit keeps the window, and what the installer said, after it finishes.
    //   * Windows PowerShell (powershell.exe), not pwsh: it is on every Windows, Server 2022 included.
    //   * The installer's `claude install` puts claude.exe in %USERPROFILE%\.local\bin, the folder ClaudeLocator always
    //     searches, so the next start finds it without a restart even though that folder may not be on the PATH yet.

    /// <summary>Claude Code's official installer for Windows (its docs' <c>irm https://claude.ai/install.ps1 | iex</c>).</summary>
    public const string InstallerUrl = "https://claude.ai/install.ps1";

    /// <summary>
    /// The PowerShell the install window runs: the official installer, saved to a file and run from it, then the installed
    /// claude.exe in the window's folder so Petre signs in and trusts the folder; a clear line when the install failed.
    /// </summary>
    public static readonly string InstallScript = string.Join("\n",
        "$ErrorActionPreference = 'Stop'",
        $"Write-Host 'Deskbar is installing Claude Code with its official installer, {InstallerUrl}' -ForegroundColor Cyan",
        "$installer = Join-Path $env:TEMP 'deskbar-claude-install.ps1'",
        "try {",
        $"  Invoke-RestMethod -Uri '{InstallerUrl}' -OutFile $installer",
        "  & $installer",
        "} catch {",
        "  Write-Host $_ -ForegroundColor Red",
        "} finally {",
        "  Remove-Item $installer -ErrorAction SilentlyContinue",
        "}",
        @"$claude = Join-Path $env:USERPROFILE '.local\bin\claude.exe'",
        "if (Test-Path $claude) {",
        "  Write-Host 'Claude Code is installed. Sign in and trust this folder below, then go back to the task in Deskbar and choose Start Claude on this again.' -ForegroundColor Green",
        "  & $claude",
        "} else {",
        "  Write-Host 'Claude Code was not installed. What the installer said is above.' -ForegroundColor Red",
        "}");

    /// <summary>A script as PowerShell's -EncodedCommand takes it: base64 of its UTF-16LE bytes.</summary>
    public static string Encoded(string script) => Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));

    // Windows PowerShell's arguments for the install, the same in a Windows Terminal window and in a console.
    static IReadOnlyList<string> InstallArguments =>
        ["-NoProfile", "-ExecutionPolicy", "Bypass", "-NoExit", "-EncodedCommand", Encoded(InstallScript)];

    /// <summary>The program the install runs in, by the name its window's process has (PowerShell's console is its own).</summary>
    public const string InstallShell = "powershell.exe";

    /// <summary>
    /// The window that installs Claude Code: a NEW Windows Terminal window in the folder, with the default profile's look,
    /// running <see cref="InstallScript"/> in Windows PowerShell. wt passes everything after the program to it, dashes
    /// included (its docs: <c>wt new-tab PowerShell -c Start-Service</c>).
    /// </summary>
    public static ProgramLaunch InstallWindow(string folder) =>
        new("wt.exe", ["-w", "new", "-d", Bare(folder), InstallShell, .. InstallArguments], Bare(folder));

    /// <summary>
    /// The install window for a machine without Windows Terminal: the same PowerShell in a console of its own, which
    /// PowerShell titles itself.
    /// </summary>
    public static ConsoleLaunch InstallConsole(string folder) =>
        new(InstallShell, InstallArguments, Bare(folder), Maybe<string>.None);

    /// <summary>
    /// Whether Windows Terminal's <c>wt.exe</c> can be started: on the PATH (Deskbar's own, then the user's and the
    /// machine's, as ClaudeLocator searches), or as the execution alias Windows Terminal's package puts in
    /// <c>%LOCALAPPDATA%\Microsoft\WindowsApps</c>, which the shell finds even when that folder is not on the PATH.
    /// </summary>
    //
    // The alias is a zero-byte reparse point, and File.Exists answers true for it (measured on this machine). An
    // unpackaged Windows Terminal (a zip or portable copy) is found through the PATH, as wt itself would be.
    // <paramref name="exists"/> answers whether a file exists, so the rule is a test without a disk.
    public static bool HasWindowsTerminal(string? pathVariable, string localAppData, Func<string, bool> exists) =>
        (pathVariable ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(folder => folder.Trim('"'))
            .Where(folder => folder.Length > 0)
            .Append(Path.Combine(localAppData, "Microsoft", "WindowsApps"))
            .Select(folder => Path.Combine(folder, "wt.exe"))
            .Any(exists);

    /// <summary>The variable that stops Claude Code from retitling its console window (see AttachConsole).</summary>
    public const string KeepTitleVariable = "CLAUDE_CODE_DISABLE_TERMINAL_TITLE";

    // The longest title the window is given; the rest of a long title is in the task.
    const int MaxTitle = 80;

    /// <summary>
    /// The title of the window on a task's session: the task's title made safe for wt's command line, then " · Claude".
    /// Deskbar finds the window again by this exact title (Windows Terminal draws every window from one process, so the
    /// title is the only thing that tells them apart; the "Deskbar Claude" profile's suppressApplicationTitle keeps it).
    /// </summary>
    //
    // wt splits its command line into commands at every ';', so each becomes ','. A leading '-' would be read as one of
    // wt's options, so leading dashes and spaces go. A title can hold line breaks (the editors take Shift+Enter), which a
    // window title cannot show: its first line is used, the one the bar shows. Then the whole is cut to 80 characters,
    // stepping back over a split emoji as FirstMessage does.
    public static string AttachTitle(string taskTitle)
    {
        var firstLine = taskTitle.Split('\n')[0].Replace('\r', ' ');
        var clean = firstLine.Replace(';', ',').TrimStart('-', ' ', '\t').Trim();
        var cut = clean.Length > MaxTitle && char.IsHighSurrogate(clean[MaxTitle - 1]) ? MaxTitle - 1 : MaxTitle;
        return (clean.Length > MaxTitle ? clean[..cut] : clean) + TitleSuffix;
    }

    /// <summary>What every <see cref="AttachTitle"/> ends with, and so what tells a session's window from other terminals.</summary>
    public const string TitleSuffix = " · Claude";

    // Windows Terminal draws every one of its windows from this one process (no extension, as WindowInfo.ProcessName has it).
    const string TerminalProcess = "WindowsTerminal";

    /// <summary>
    /// Whether a window is one Deskbar opened on a Claude session: a Windows Terminal window titled by
    /// <see cref="AttachTitle"/>. The bar draws Claude's icon for it instead of the terminal's (task 0bd79d0e).
    /// </summary>
    //
    // The TITLE, because it is all there is: Windows Terminal hosts every window in one process, so the process and its
    // command line are the same for a Claude window and a PowerShell one, and the window's own icon is the terminal's
    // logo whatever profile its tab runs (measured: WM_GETICON's ICON_BIG on a "Deskbar Claude" window is the class
    // icon, Windows Terminal's own). The "Deskbar Claude" profile's suppressApplicationTitle keeps the title fixed, and a
    // second tab opened in the window takes the title with it, which is right: the window then shows that tab, not Claude.
    //
    // The process is matched by its exe's file name, so a path from any install location (Store, unpackaged, Preview's
    // WindowsTerminal.exe too) qualifies, and a stray "· Claude" title on any other app does not.
    public static bool IsSessionWindow(string? processPath, string? title) =>
        title is not null
        && title.EndsWith(TitleSuffix, StringComparison.Ordinal)
        && processPath is not null
        && Path.GetFileNameWithoutExtension(processPath).Equals(TerminalProcess, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Where the Claude Code CLI's own exe is, for its icon: the first <c>claude.exe</c> on the PATH, else the native
    /// installer's <c>%USERPROFILE%\.local\bin\claude.exe</c>; None when neither exists.
    /// </summary>
    //
    // An exe only, because the icon is read from it: an npm install puts a claude.cmd on the PATH, which runs node and
    // carries no icon, so it is passed over (and with nothing else the bar keeps the terminal's icon). The installer's
    // folder is the fallback because Deskbar's own PATH is the one it started with, which can predate the install.
    // <paramref name="exists"/> answers whether a file exists, so the rule is a test without a disk.
    public static Maybe<string> ExecutableIn(string? pathVariable, string home, Func<string, bool> exists) =>
        (pathVariable ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(folder => folder.Trim('"'))
            .Where(folder => folder.Length > 0)
            .Append(Path.Combine(home, ".local", "bin"))
            .Select(folder => Path.Combine(folder, "claude.exe"))
            .TryFirst(exists);

    /// <summary>
    /// The folders a workspace's VS Code windows show, as full paths (spec, "Choosing the folder": the editor windows'
    /// folders, "resolved to full paths through those session folders"). A VS Code title names only the folder
    /// (EditorFolder.Of), so each name is matched, ignoring case, against the last segment of the repo of every session
    /// the board knows; a name no repo ends in is left out, since a session cannot be started in a bare name. Each repo
    /// comes once, the most recently joined session's spelling first.
    /// </summary>
    public static IReadOnlyList<string> EditorRepos(IEnumerable<string> folderNames, TaskBoard board)
    {
        var repos = board.Tasks.SelectMany(t => t.Sessions)
            .Where(s => s.Folder != "")
            .OrderByDescending(s => s.Joined)
            .Select(s => Bare(RepoOf(s.Folder)))
            .DistinctBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return folderNames
            .SelectMany(name => repos.Where(repo => LastSegment(repo).Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)).Take(1))
            .DistinctBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The command line CreateProcess is given for a console launch: the program quoted, then each argument, quoted only
    /// when it holds a space, a tab or a quote, by the C runtime's rules (a quote as \", and the backslashes before a quote
    /// or the closing quote doubled), which is how claude.exe splits it again.
    /// </summary>
    public static string CommandLine(ConsoleLaunch launch) =>
        string.Join(" ", new[] { $"\"{launch.Program}\"" }.Concat(launch.Arguments.Select(QuoteArgument)));

    static string QuoteArgument(string argument) =>
        argument.Length > 0 && !argument.Any(c => c is ' ' or '\t' or '"')
            ? argument
            : "\"" + Regex.Replace(Regex.Replace(argument, @"(\\*)""", @"$1$1\"""), @"(\\+)$", "$1$1") + "\"";

    static string LastSegment(string folder) => folder.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";

    // A folder without its trailing separators, except a drive's root (see AttachWindow).
    static string Bare(string folder) =>
        folder.TrimEnd('\\', '/') is var trimmed && trimmed.Length == 2 && trimmed[1] == ':' && folder.Length > 2 ? folder[..3] : trimmed;
}
