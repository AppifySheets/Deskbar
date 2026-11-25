using CSharpFunctionalExtensions;
using Deskbar.Core.Control;

namespace Deskbar.Core.Tasks;

// THE PURE RULES OF THE MANAGER SESSION: a Fable background Claude Code session, opened from a bar button, that
// coordinates the user's task board. Design: docs/superpowers/specs/2026-11-25-manager-session-design.md; the claude
// flags below were measured in docs/superpowers/notes/2026-11-25-manager-session-probes.md. Nothing here starts a
// process or touches a disk, so every rule is a test; the Windows side does the starting.

/// <summary>
/// The manager Deskbar last started: its full session id and the local calendar day it was started on. The other three
/// are step 2's event wakes (ManagerWakes; docs/superpowers/specs/2026-11-25-manager-event-wakes-design.md), optional so
/// a record saved before them still loads:
/// <list type="bullet">
/// <item><paramref name="Since"/>: events are counted from here (the start, then each wake); null until the first tick.</item>
/// <item><paramref name="Unread"/>: a wake was delivered and the user has not opened the manager since (the button's dot).</item>
/// <item><paramref name="HandedBack"/>: the tasks already reported as handed back today, so a second one is not nudged.</item>
/// </list>
/// And the manager's own question to the user (task 61e4d143, ManagerQuestion), optional for the same reason:
/// <list type="bullet">
/// <item><paramref name="Question"/>: what the manager asked with `task ask`, shown as the manager line at the top of
/// "Waiting on you"; null while it has nothing open.</item>
/// <item><paramref name="AskedAt"/>: when it asked, for the line's age.</item>
/// </list>
/// A new manager (StartNew, Claim) is a new record, so a new day's manager starts without yesterday's question.
/// </summary>
public sealed record ManagerRecord(string SessionId, DateOnly Day, DateTimeOffset? Since = null, bool Unread = false,
    IReadOnlyList<Guid>? HandedBack = null, string? Question = null, DateTimeOffset? AskedAt = null)
{
    // A record compares by its list's CONTENTS, not the list's reference, so a record loaded from state.json equals the
    // one saved, and ManagerWaker can tell a changed record from the same one to skip a needless save.
    public bool Equals(ManagerRecord? other) =>
        other is not null && SessionId == other.SessionId && Day == other.Day && Since == other.Since
        && Unread == other.Unread && (HandedBack ?? []).SequenceEqual(other.HandedBack ?? [])
        && Question == other.Question && AskedAt == other.AskedAt;

    public override int GetHashCode() => HashCode.Combine(SessionId, Day, Since, Unread, (HandedBack ?? []).Count, Question, AskedAt);
}

/// <summary>What a click on the manager button should do.</summary>
public abstract record ManagerStep
{
    /// <summary>The manager is alive and listed: open a window on it.</summary>
    public sealed record Attach(ClaudeAgent Agent) : ManagerStep;

    /// <summary>Today's manager exists but is not running: wake it, under its own id, with its saved options.</summary>
    public sealed record Resume(string SessionId) : ManagerStep;

    /// <summary>Start a fresh manager; stop the named stale one first, when there is one.</summary>
    public sealed record StartNew(Maybe<ClaudeAgent> StopFirst) : ManagerStep;

    /// <summary>
    /// A manager runs in the manager folder that is not the stored one (a start whose session was not listed in time
    /// stored nothing): record it as today's and open a window on it, never start another beside it.
    /// </summary>
    public sealed record Claim(ClaudeAgent Agent) : ManagerStep;
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

    // What `task comment-edit` and `task comment-settle` answer when the caller is the manager (final review, item 1).
    // EditComment refuses only a session's comment, so without this the manager could rewrite the user's own words, and
    // the edit would count as the user's (it clears Adopted). Settling a comment folds it away, which is the user's call.
    public const string CommentRefusal = "The manager does not edit or settle the user's comments. Ask the user.";

    // What `task join` answers when the caller is the manager: it coordinates, it never owns a task.
    public const string JoinRefusal = "The manager session does not take a task of its own.";

    public const string FirstMessage = "What needs me now, and what is stuck? Run the overview once and answer in a few lines.";

    // The manager lives in its own folder under the state directory (so DESKBAR_APPDATA moves it too), which keeps its
    // cwd out of every repo and lets its sessions be told apart from the user's by folder alone.
    public static string FolderIn(string stateDir) => Path.Combine(stateDir, FolderName);

    // The manager folder, or anything UNDER it, by the one normal form every folder uses (short 8.3 forms, slashes,
    // trailing separators, case). A subfolder counts because the folder the pipe sees is the client's current directory,
    // and Claude Code lets a session `cd` inside its own working folder without a permission rule: a manager that ran
    // `cd .claude && <shim> task close <id>` would otherwise arrive as a non-manager and skip every refusal. A sibling
    // that merely shares the prefix (`manager2`) is NOT it: the prefix must be followed by a path separator. An empty
    // manager folder matches nothing.
    public static bool IsManager(string? folder, string managerFolder, Func<string, string> longPath)
    {
        var caller = FolderPath.Normalise(folder, longPath);
        var root = FolderPath.Normalise(managerFolder, longPath);
        if (caller.Length == 0 || root.Length == 0) return false;

        // Normalise keeps a drive root's separator, so trim before adding one: "C:\" must not become "C:\\".
        var prefix = root.TrimEnd('\\') + "\\";
        return string.Equals(caller, root, StringComparison.OrdinalIgnoreCase)
               || caller.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The listed sessions that are not the manager, so the manager never counts as a task's session.</summary>
    public static IReadOnlyList<ClaudeAgent> Others(IEnumerable<ClaudeAgent> listed, string managerFolder, Func<string, string> longPath) =>
        listed.Where(agent => !IsManager(agent.Folder, managerFolder, longPath)).ToList();

    // The start command's arguments, in the order and forms the probe notes measured:
    //  * --permission-mode dontAsk: a session defaults to auto mode, which runs everything; under dontAsk a command
    //    with no allow rule is denied without a prompt.
    //  * --allowedTools: only the shim's `task` verbs, by its FULL path with forward slashes. The short `deskbar.cmd`
    //    form matches but is not on Git Bash's PATH, and a `$APPDATA` spelling hits a permission prompt.
    //  * --disallowedTools: no file, web, agent or timer tools; PowerShell too, because a Bash rule does not cover it
    //    and the manager would otherwise run anything through it. The timers (ScheduleWakeup, CronCreate, CronDelete,
    //    RemoteTrigger) and Workflow because nothing of the manager's runs on a timer or in the background; EnterWorktree
    //    and Artifact because it touches no repo and publishes nothing; SendMessage because it talks to sessions only
    //    through task comments, which the user can read. Every name was measured to be accepted by `claude --bg` 2.1.332
    //    and to take the tool off the session's list, deferred ones included (probe notes, "Final review probes").
    //  * `--` before the message, so the variadic --disallowedTools does not swallow it.
    // The briefing path and shim path are parameters: both come from the user's real expanded paths at runtime. The shim
    // is the form ManagerActions.ShimCommand chose, so a path with a space arrives as its 8.3 short form, or quoted.
    public static IReadOnlyList<string> StartArguments(string briefingPath, string shimPath) =>
    [
        "--bg", "--model", "fable", "-n", Name, "--strict-mcp-config",
        "--permission-mode", "dontAsk",
        "--append-system-prompt-file", briefingPath,
        "--allowedTools", $"Bash({shimPath.Replace('\\', '/')} task:*)",
        "--disallowedTools", .. DisallowedTools,
        "--", FirstMessage
    ];

    /// <summary>The tools the manager is started without (StartArguments has the reasons).</summary>
    public static readonly IReadOnlyList<string> DisallowedTools =
    [
        "Read", "Edit", "Write", "NotebookEdit", "Agent", "WebFetch", "WebSearch", "Monitor", "PowerShell",
        "ScheduleWakeup", "CronCreate", "CronDelete", "Workflow", "EnterWorktree", "Artifact", "RemoteTrigger", "SendMessage",
    ];

    // A BARE resume and nothing else: it keeps every saved option (measured), while any flag on a resume starts a
    // forked copy under a new id. The full session id is needed for the same reason; a short one forks too.
    public static IReadOnlyList<string> ResumeArguments(string sessionId) => ["--bg", "--resume", sessionId];

    /// <summary>
    /// The system prompt appended to the manager, short imperative lines. The shim is named by the exact full path with
    /// forward slashes, because it is what the allow rule matches; a path with a space comes as its 8.3 short form, or
    /// in double quotes when the volume has no short names (ManagerActions.ShimCommand), and is written as it came.
    /// </summary>
    public static string Briefing(string shim) => string.Join("\n",
        "You are the Deskbar manager. You coordinate the user's task board; you do not do the tasks.",
        $"Call the Deskbar shim exactly as written here (full path, forward slashes, any quotes kept), never a variable or $APPDATA: {shim}",
        "Allowed:",
        $"- {shim} task overview : the whole board, once per question.",
        $"- {shim} task show <id> : one task in full.",
        $"- {shim} task comment <id> - : post a comment; the text goes on stdin.",
        // Task 61e4d143 (ManagerQuestion): the manager's one road to the user that does not wait for them to open it.
        $"- {shim} task ask - : your own question to the user, on stdin. It shows above every task's question; a new one replaces the last.",
        $"- {shim} task answered : clear your question once it is settled.",
        "Ask rarely: only a call no task's session can make (two tasks that look like one job, which waiting thing comes first,",
        "  a hand-back nudging did not fix). Never relay a task's own question: it already reaches the user.",
        "The user's message to you clears your question. If it leaves the question open, ask again.",
        $"The other task verbs are allowed too (start, flag, order, move, assign, add, rename, describe, and a subtask by add --parent); {shim} task prints them.",
        "Free text always goes on stdin with - in its place, never quoted on the command line.",
        "Refused: close, delete, and setting a task to done or closed. The shim answers:",
        $"  {Refusal}",
        $"Editing or settling a comment is refused too: {CommentRefusal}",
        "When that happens, tell the user and leave the task alone.",
        $"When you quote the user in a comment, start it with the exact marker \"{QuoteMarker}\" and copy their words unchanged.",
        "Never paraphrase into a quote.",
        "Do not take a task of your own: never join one.",
        "Never use Monitor, loop, scheduled wakeups, subagents, file reading or transcript reading.",
        "You act when the user messages you, or when a message starts with \"Deskbar wake.\" (event wakes, ManagerWakes).",
        "On a Deskbar wake:",
        $"- Each handed-back task not marked AGAIN: one comment ({shim} task comment <id> -) to wake its session.",
        "  If its status line says the next move is the user's, tell it to run task ask with that move.",
        "  Otherwise tell it to finish with task done, or carry on with the work.",
        "- A task marked AGAIN gets no comment: it goes on your list.",
        "- Then list what waits on the user, oldest and most blocking first, each with your suggested answer, a few lines.",
        "- Run the overview only if the wake does not tell you enough.",
        "Answer in a few lines. Say what needs the user now, then what is stuck.",
        "Name a task by its title and short id, so the user can find it.");

    /// <summary>
    /// What a click does, in this order. The stored manager not listed (or nothing stored) while a background session
    /// runs in the manager folder: claim that one (the busy one first, else the newest). Otherwise: nothing stored,
    /// start. Stored today and listed, attach. Stored today, not listed, with its transcript on disk, resume. Stored
    /// today, transcript gone, start. Stored earlier and listed busy, attach (never stop work in progress). Stored earlier
    /// and listed idle, start and stop the old one first. Stored earlier and not listed, start.
    /// </summary>
    /// <param name="inManagerFolder">
    /// Whether a listed session's folder is the manager folder (IsManager). The folder is the manager's identity (design,
    /// "Who counts as the manager"), so a manager whose id was never stored is still found by it.
    /// </param>
    public static ManagerStep Next(Maybe<ManagerRecord> stored, DateOnly today, IReadOnlyCollection<ClaudeAgent> listed,
        Func<string, bool> transcriptExists, Func<ClaudeAgent, bool> inManagerFolder)
    {
        var agent = stored.Bind(record => listed.TryFirst(a => a.SessionId == record.SessionId));
        // Task review, Important: a start whose session Claude Code did not list within the search stored nothing, yet
        // runs on. Starting again would put a second Fable manager beside it that nothing ever stops (the keeper leaves
        // the manager folder alone), so a manager found in the folder is today's. Only background sessions: the trust
        // terminal of the first start runs an interactive `claude` there, and no window can be attached to it.
        if (agent.HasNoValue
            && listed.Where(a => a.Background && inManagerFolder(a))
                .OrderByDescending(a => a.Busy)
                .ThenByDescending(a => a.StartedAt.GetValueOrDefault(DateTimeOffset.MinValue))
                .TryFirst() is { HasValue: true } found)
            return new ManagerStep.Claim(found.Value);

        if (stored.HasNoValue) return new ManagerStep.StartNew(Maybe<ClaudeAgent>.None);

        var record = stored.Value;

        return record.Day == today
            ? agent.HasValue ? new ManagerStep.Attach(agent.Value)
                : transcriptExists(record.SessionId) ? new ManagerStep.Resume(record.SessionId)
                : new ManagerStep.StartNew(Maybe<ClaudeAgent>.None)
            : agent.HasValue && agent.Value.Busy ? new ManagerStep.Attach(agent.Value)
            : new ManagerStep.StartNew(agent);
    }
}
