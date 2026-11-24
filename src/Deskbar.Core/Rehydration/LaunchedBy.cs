using CSharpFunctionalExtensions;
using Deskbar.Core.Domain;

namespace Deskbar.Core.Rehydration;

// Which app started this one (#94), decided by walking up the process chain.
//
// Pure, like PlacementMemory next door: every fact arrives through two delegates, so the whole rule
// is unit-testable without a process on the machine. What the OS is asked for lives behind
// IProcessTree.
//
// THE SHAPE OF THE PROBLEM, measured on Petre's machine rather than assumed, because a plain
// "read the parent pid" implementation answers nothing useful:
//
//   chrome ← node ← cmd ← node ← cmd ← claude ← Code ← Code[window] ← explorer
//   Code[window] ← explorer
//   Beeper ← explorer
//   msedgewebview2 ← WhatsApp.Root[window]
//
// Two facts fall out of that. A browser window opened from inside an editor sits SEVEN hops below
// the editor's window, every one of them a windowless helper, so the walk has to keep going. And
// almost everything a person launches by hand is a child of explorer, so the shell must never count
// as the app that started something: without that, launching anything from the taskbar would send it
// to wherever a File Explorer window happened to be sitting.
public static class LaunchedBy
{
    // Long enough for the chain measured above, which needed seven. Short enough that the walk cannot
    // become a cost: each hop is one process open and one query, so a miss is bounded rather than
    // proportional to how deep the machine's process tree happens to go.
    public const int MaxHops = 12;

    // Processes that are the shell or a system host, and therefore never the app that started
    // anything. Hitting one ends the walk.
    //
    // explorer is the one that matters and the reason this list exists: it is the parent of every app
    // started from the taskbar, the Start menu or the desktop, and it owns File Explorer windows, so
    // treating it as a launcher would place a newly started app wherever a folder window happened to
    // be. Petre picking an app off his taskbar is not "an app starting another app".
    //
    // The rest are the ancestors every user process eventually has (a session's chain runs up through
    // sihost, svchost, services, wininit). None of them owns a window that means anything here, and
    // stopping at them saves hops.
    static readonly IReadOnlySet<string> NotLaunchers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "sihost", "svchost", "services", "wininit", "winlogon", "csrss", "smss",
        "taskhostw", "dllhost", "runtimebroker", "startmenuexperiencehost", "searchhost",
        "shellexperiencehost", "applicationframehost", // also ProcessFacts.IsFrameHost, its own check
    };

    // How old a process may be when its window appears and still count as started for that window.
    //
    // The parent chain answers "who started this PROCESS", and that is only the same question as "who asked
    // for this WINDOW" while the process is new. A single-instance app breaks the link: every Edge window
    // belongs to one browser process, and a link clicked anywhere starts a short-lived msedge that hands the
    // URL over and exits, so the window that appears is owned by a process whose parent was fixed when the
    // browser first started. Measured 2026-11-16: Edge had been started the day before by a link clicked in
    // VS Code, and a link clicked in Teams on the GPH workspace was sent to iam.ge, where the VS Code window
    // last used happened to be:
    //
    //   launched-by msedge(58264) launcher=30480 workspace=iam.ge moved=True
    //
    // The same length as WorkspaceManager.ExpectationLifetime and for the same reason: long enough for an
    // editor or a browser to open cold on a busy machine, short enough that a process which was already
    // running is not mistaken for one that was just started.
    public static readonly TimeSpan StartedForItsWindowWithin = TimeSpan.FromSeconds(30);

    // Whether the process was started for the window that has just appeared, as opposed to having been
    // running already. A process whose start is unknown passes: the real ProcessTree always knows it, so
    // unknown only means a caller that does not model time, and refusing those would turn the whole tier off.
    public static bool StartedForItsWindow(ProcessFacts process, DateTimeOffset now) =>
        process.Started is not { } started || now - started <= StartedForItsWindowWithin;

    // The nearest ancestor process that owns a window this app is tracking, or None.
    //
    // "Owns a window we track" is what makes an ancestor a LAUNCHER rather than plumbing: a windowless
    // helper (node, cmd, an editor's own background process) is something the app you launched from
    // used to do the launching, and the question being answered is which app that was.
    //
    // `lookup` is IProcessTree.Of and is expected to drop a parent that cannot be this process's
    // parent -- see the implementation for why a bare parent pid is not trustworthy.
    public static Maybe<int> Launcher(int processId, Func<int, Maybe<ProcessFacts>> lookup, Func<int, bool> ownsTrackedWindow)
    {
        var seen = new HashSet<int> { processId };
        var at = lookup(processId);

        for (var hop = 0; hop < MaxHops; hop++)
        {
            if (at.GetValueOrDefault() is not { } facts) return Maybe<int>.None;

            var parent = facts.ParentProcessId;
            // A chain that loops cannot happen from real parentage, but it arrives here from pid
            // arithmetic this code does not own, and an infinite walk on the dispatcher thread would
            // freeze the bar rather than fail.
            if (parent <= 0 || !seen.Add(parent)) return Maybe<int>.None;

            at = lookup(parent);
            if (at.GetValueOrDefault() is not { } up) return Maybe<int>.None;
            if (NotLaunchers.Contains(up.Name)) return Maybe<int>.None;
            if (ownsTrackedWindow(parent)) return parent;
        }

        return Maybe<int>.None;
    }

    // A HIDDEN CLAUDE SESSION as the launcher (Deskbar task e671709b, Petre: "when a hidden claude task opens a
    // browser or anything, it should stay in the correct workspace").
    //
    // Launcher above finds nothing for these, and the measured chain shows why. A `claude --bg` session is run by
    // Claude Code's daemon, which was started through WMI so that it outlives whoever started it:
    //
    //   chrome ← node ← cmd ← claude --session-id <id> ← claude --bg-pty-host ...-pty-<short id> ← claude daemon
    //     ← WmiPrvSE ← svchost
    //
    // Not one of those owns a window, so the new window used to stay on the desktop in front, which is whichever
    // one Petre happened to be working on, not the one the session's task belongs to.
    //
    // The pty host is the one link every background session has in the same form: the session process below it
    // reads `--session-id <id>` for a new session, `--resume <id>` or `--resume <path to the .jsonl>` for a resumed
    // one, while the pty host always names the session's short id (the first eight characters, the same id
    // `claude --bg` prints and `claude attach` takes) at the end of its pipe name. Measured 2026-11-25, Claude Code
    // 2.1.330, on all ten background sessions then running. A foreground session (VS Code's, or `claude` typed in a
    // terminal) has no pty host, so this never answers for one, and Launcher finds its editor or terminal anyway.
    //
    // Only processes named claude have their command line read, so the walk costs one memory read per claude hop
    // and nothing for the plumbing around it. It stops where Launcher stops: the shell, a system host, a gone
    // parent, MaxHops.
    public static Maybe<string> BackgroundSession(int processId, Func<int, Maybe<ProcessFacts>> lookup, Func<int, Maybe<string>> commandLineOf)
    {
        var seen = new HashSet<int> { processId };
        var at = lookup(processId);

        for (var hop = 0; hop < MaxHops; hop++)
        {
            if (at.GetValueOrDefault() is not { } facts) return Maybe<string>.None;

            var parent = facts.ParentProcessId;
            // The same loop guard as Launcher's, for the same reason.
            if (parent <= 0 || !seen.Add(parent)) return Maybe<string>.None;

            at = lookup(parent);
            if (at.GetValueOrDefault() is not { } up) return Maybe<string>.None;
            if (NotLaunchers.Contains(up.Name)) return Maybe<string>.None;

            var session = up.Name.Equals("claude", StringComparison.OrdinalIgnoreCase)
                ? commandLineOf(parent).Bind(PtyHostSession)
                : Maybe<string>.None;
            if (session.HasValue) return session;
        }

        return Maybe<string>.None;
    }

    // `claude.exe --bg-pty-host \\.\pipe\cc-daemon-37b43e613ddb8ec4-pty-84bee58f 200 50 -- claude.exe ...` -> "84bee58f".
    // Lower case, as `claude --bg` prints it and as session ids are written everywhere else.
    public static Maybe<string> PtyHostSession(string commandLine) =>
        PtyHost.Match(commandLine) is { Success: true } match
            ? match.Groups["id"].Value.ToLowerInvariant()
            : Maybe<string>.None;

    static readonly System.Text.RegularExpressions.Regex PtyHost =
        new(@"--bg-pty-host\s+\S*-pty-(?<id>[0-9a-fA-F]{8})\b", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}
