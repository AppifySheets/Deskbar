using CSharpFunctionalExtensions;
using Deskbar.Core.Control;

namespace Deskbar.Core.Tasks;

// THE PURE RULES OF THE MANAGER SESSION: a Fable background Claude Code session, opened from a bar button, that
// coordinates the user's task board. Design: docs/superpowers/specs/2026-11-25-manager-session-design.md; the claude
// flags below were measured in docs/superpowers/notes/2026-11-25-manager-session-probes.md. Nothing here starts a
// process or touches a disk, so every rule is a test; the Windows side does the starting.

/// <summary>The manager Deskbar last started: its full session id and the local calendar day it was started on.</summary>
public sealed record ManagerRecord(string SessionId, DateOnly Day);

/// <summary>What a click on the manager button should do.</summary>
public abstract record ManagerStep
{
    /// <summary>The manager is alive and listed: open a window on it.</summary>
    public sealed record Attach(ClaudeAgent Agent) : ManagerStep;

    /// <summary>Today's manager exists but is not running: wake it, under its own id, with its saved options.</summary>
    public sealed record Resume(string SessionId) : ManagerStep;

    /// <summary>Start a fresh manager; stop the named stale one first, when there is one.</summary>
    public sealed record StartNew(Maybe<ClaudeAgent> StopFirst) : ManagerStep;
}

public static class ManagerSession
{
    public const string FolderName = "manager";
    public const string Name = "Deskbar manager";
    public const string BriefingFile = "manager-briefing.md";

    // A verbatim quote of the user in a manager comment starts with this marker, so a reader can tell the user's own
    // words from the manager's summary. The spec's "Petre says:" written for every user, since the text ships to them.
    public const string QuoteMarker = "The user says:";

    // What the task verbs answer when the caller is the manager and the verb would end a task.
    public const string Refusal = "The manager coordinates; it does not close, delete or finish tasks. Ask the user.";

    // What `task join` answers when the caller is the manager: it coordinates, it never owns a task.
    public const string JoinRefusal = "The manager session does not take a task of its own.";

    public const string FirstMessage = "What needs me now, and what is stuck? Run the overview once and answer in a few lines.";

    // The manager lives in its own folder under the state directory (so DESKBAR_APPDATA moves it too), which keeps its
    // cwd out of every repo and lets its sessions be told apart from the user's by folder alone.
    public static string FolderIn(string stateDir) => Path.Combine(stateDir, FolderName);

    // Same folder by the one comparison every folder uses (short 8.3 forms, slashes, case). A subfolder is not it.
    public static bool IsManager(string? folder, string managerFolder, Func<string, string> longPath) =>
        !string.IsNullOrWhiteSpace(folder) && FolderPath.Same(folder, managerFolder, longPath);

    /// <summary>The listed sessions that are not the manager, so the manager never counts as a task's session.</summary>
    public static IReadOnlyList<ClaudeAgent> Others(IEnumerable<ClaudeAgent> listed, string managerFolder, Func<string, string> longPath) =>
        listed.Where(agent => !IsManager(agent.Folder, managerFolder, longPath)).ToList();

    // The start command's arguments, in the order and forms the probe notes measured:
    //  * --permission-mode dontAsk: a session defaults to auto mode, which runs everything; under dontAsk a command
    //    with no allow rule is denied without a prompt.
    //  * --allowedTools: only the shim's `task` verbs, by its FULL path with forward slashes. The short `deskbar.cmd`
    //    form matches but is not on Git Bash's PATH, and a `$APPDATA` spelling hits a permission prompt.
    //  * --disallowedTools: no file, web, agent or timer tools; PowerShell too, because a Bash rule does not cover it
    //    and the manager would otherwise run anything through it.
    //  * `--` before the message, so the variadic --disallowedTools does not swallow it.
    // The briefing path and shim path are parameters: both come from the user's real expanded paths at runtime.
    public static IReadOnlyList<string> StartArguments(string briefingPath, string shimPath) =>
    [
        "--bg", "--model", "fable", "-n", Name, "--strict-mcp-config",
        "--permission-mode", "dontAsk",
        "--append-system-prompt-file", briefingPath,
        "--allowedTools", $"Bash({shimPath.Replace('\\', '/')} task:*)",
        "--disallowedTools", "Read", "Edit", "Write", "NotebookEdit", "Agent", "WebFetch", "WebSearch", "Monitor", "PowerShell",
        "--", FirstMessage
    ];

    // A BARE resume and nothing else: it keeps every saved option (measured), while any flag on a resume starts a
    // forked copy under a new id. The full session id is needed for the same reason; a short one forks too.
    public static IReadOnlyList<string> ResumeArguments(string sessionId) => ["--bg", "--resume", sessionId];

    /// <summary>
    /// The system prompt appended to the manager, short imperative lines. The shim is named by the exact full path with
    /// forward slashes, because it is what the allow rule matches.
    /// </summary>
    public static string Briefing(string shim) => string.Join("\n",
        "You are the Deskbar manager. You coordinate the user's task board; you do not do the tasks.",
        $"Call the Deskbar shim by exactly this full path, forward slashes, never a variable or $APPDATA: {shim}",
        "Allowed:",
        $"- {shim} task overview : the whole board, once per question.",
        $"- {shim} task show <id> : one task in full.",
        $"- {shim} task comment <id> - : post a comment; the text goes on stdin.",
        "Refused: close, delete, and setting a task to done or closed. The shim answers:",
        $"  {Refusal}",
        "When that happens, tell the user and leave the task alone.",
        $"When you quote the user in a comment, start it with the exact marker \"{QuoteMarker}\" and copy their words unchanged.",
        "Never paraphrase into a quote.",
        "Do not take a task of your own: never join one.",
        "Never use Monitor, loop, scheduled wakeups, subagents, file reading or transcript reading.",
        "Nothing runs on a timer; you act only when the user messages you.",
        "Answer in a few lines. Say what needs the user now, then what is stuck.",
        "Name a task by its title and short id, so the user can find it.");

    /// <summary>
    /// What a click does, in this order: nothing stored, start. Stored today and listed, attach. Stored today, not
    /// listed, with its transcript on disk, resume. Stored today, transcript gone, start. Stored earlier and listed busy,
    /// attach (never stop work in progress). Stored earlier and listed idle, start and stop the old one first. Stored
    /// earlier and not listed, start.
    /// </summary>
    public static ManagerStep Next(Maybe<ManagerRecord> stored, DateOnly today, IReadOnlyCollection<ClaudeAgent> listed,
        Func<string, bool> transcriptExists)
    {
        if (stored.HasNoValue) return new ManagerStep.StartNew(Maybe<ClaudeAgent>.None);

        var record = stored.Value;
        var agent = listed.TryFirst(a => a.SessionId == record.SessionId);

        return record.Day == today
            ? agent.HasValue ? new ManagerStep.Attach(agent.Value)
                : transcriptExists(record.SessionId) ? new ManagerStep.Resume(record.SessionId)
                : new ManagerStep.StartNew(Maybe<ClaudeAgent>.None)
            : agent.HasValue && agent.Value.Busy ? new ManagerStep.Attach(agent.Value)
            : new ManagerStep.StartNew(agent);
    }
}
