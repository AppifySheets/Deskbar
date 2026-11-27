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
/// <item><paramref name="Unread"/>: a wake reporting a task handed back again was delivered, and the user has not opened
/// the manager since (the button's dot; task 6a87df67).</item>
/// <item><paramref name="HandedBack"/>: the tasks already reported as handed back today, so a second one is not nudged.</item>
/// </list>
/// And the manager's own question to the user (task 61e4d143, ManagerQuestion), optional for the same reason:
/// <list type="bullet">
/// <item><paramref name="Question"/>: what the manager asked with `task ask`, shown as the manager line at the top of
/// "Waiting on you"; null while it has nothing open.</item>
/// <item><paramref name="AskedAt"/>: when it asked, for the line's age.</item>
/// </list>
/// And the manager's answer to the user's message (task 2c5de076, ManagerQuestion.AfterStop), optional for the same reason:
/// <list type="bullet">
/// <item><paramref name="Replying"/>: the user's message reached the manager and its turn has not ended yet, so the
/// turn's end is an answer to the user and not to one of Deskbar's wakes.</item>
/// <item><paramref name="Answer"/>: the answer's first line, shown as the manager line until the user opens the manager,
/// brings its window to the front or writes to it again; null while there is nothing unread.</item>
/// <item><paramref name="AnsweredAt"/>: when the turn ended, for the line's age.</item>
/// </list>
/// A new manager (StartNew, Claim) is a new record, so a new day's manager starts without yesterday's question.
/// </summary>
public sealed record ManagerRecord(string SessionId, DateOnly Day, DateTimeOffset? Since = null, bool Unread = false,
    IReadOnlyList<Guid>? HandedBack = null, string? Question = null, DateTimeOffset? AskedAt = null,
    bool Replying = false, string? Answer = null, DateTimeOffset? AnsweredAt = null)
{
    // A record compares by its list's CONTENTS, not the list's reference, so a record loaded from state.json equals the
    // one saved, and ManagerWaker can tell a changed record from the same one to skip a needless save.
    public bool Equals(ManagerRecord? other) =>
        other is not null && SessionId == other.SessionId && Day == other.Day && Since == other.Since
        && Unread == other.Unread && (HandedBack ?? []).SequenceEqual(other.HandedBack ?? [])
        && Question == other.Question && AskedAt == other.AskedAt
        && Replying == other.Replying && Answer == other.Answer && AnsweredAt == other.AnsweredAt;

    public override int GetHashCode() =>
        HashCode.Combine(HashCode.Combine(SessionId, Day, Since, Unread, (HandedBack ?? []).Count, Question, AskedAt),
            Replying, Answer, AnsweredAt);
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

    /// <summary>The manager's window title, `Deskbar manager · Claude`, by which its window is found and told apart.</summary>
    public static string WindowTitle => ClaudeStart.AttachTitle(Name);

    /// <summary>
    /// Whether a window is the manager's: a Claude session window (<see cref="ClaudeStart.IsSessionWindow"/>, a Windows
    /// Terminal window Deskbar opened) titled <see cref="WindowTitle"/>.
    /// </summary>
    //
    // THE MANAGER'S WINDOW SET APART (task f171bca6, Petre: "manager terminal window should have a special icon and ring
    // and everything and should be pinned to the deskbar, like tasks", then from the choices offered: pinned to every
    // desktop, a violet icon, a violet ring, and a glow while it asks). Everything that treats this window differently
    // asks here, so the icon (IconCache), the ring (FocusRings.WithManager), the glow (FloatingBar.Glows) and the pin
    // (App.PinManagerWindow) cannot disagree about which window it is.
    //
    // By the title, for the reason IsSessionWindow gives: Windows Terminal draws every window from one process, so the
    // title is all that tells this window from a task's. The "Deskbar Claude" profile keeps it fixed. A second tab opened
    // in the window takes the title with it, and the window then stops being the manager's, which is right: it shows
    // that tab. A task cannot take the title: AttachTitle cuts a task's title to its first line plus " · Claude", so
    // only a task titled exactly "Deskbar manager" would, and its window would then be drawn and pinned as the manager's.
    // Rare enough to leave: renaming the task gives the window back its task look the next time it opens.
    public static bool IsWindow(string? processPath, string? title) =>
        ClaudeStart.IsSessionWindow(processPath, title) && title == WindowTitle;

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

    // Task 6a87df67: the first turn asks only for what the bar cannot show (the briefing's reporting rule), not "what
    // needs me now", which every task's own question and pull request already answer on the bar.
    public const string FirstMessage =
        "Run the overview once. Tell me only what the bar does not show: sessions that collide or duplicate each other, "
        + "work stuck on something else, one decision blocking several tasks. One line if there is none.";

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
    //  * --allowedTools: only the shim's `task` verbs and its `report` (task c28ed674; two rules after one flag, which
    //    takes a list), by its FULL path with forward slashes. The short `deskbar.cmd`
    //    form matches but is not on Git Bash's PATH, and a `$APPDATA` spelling hits a permission prompt.
    //  * --disallowedTools: no file, web, agent or timer tools; PowerShell too, because a Bash rule does not cover it
    //    and the manager would otherwise run anything through it. The timers (ScheduleWakeup, CronCreate, CronDelete,
    //    RemoteTrigger) and Workflow because nothing of the manager's runs on a timer or in the background; EnterWorktree
    //    and Artifact because it touches no repo and publishes nothing; SendMessage because it talks to sessions only
    //    through task comments, which the user can read. Every name was measured to be accepted by `claude --bg` 2.1.332
    //    and to take the tool off the session's list, deferred ones included (probe notes, "Final review probes").
    //  * --remote-control "Deskbar manager" (task 1c9eefdf, and the voice ask in task 0f73cd0f): the manager is the one
    //    session that shows up in the Claude phone app, so Petre can talk to it away from the desk, the phone keyboard's
    //    mic included. Task sessions start with it held off (ClaudeStart.RemoteControlOff). It has to be this flag:
    //    claude ignores remoteControlAtStartup true in a project's settings and in --settings (claude 2.1.332's code,
    //    and a probe). A bare resume reattaches the bridge (probed in task 0f73cd0f), so a wake keeps it. Named after
    //    the session so the phone's list reads "Deskbar manager" rather than the machine name.
    //  * `--` before the message, so the variadic --disallowedTools does not swallow it.
    // The briefing path and shim path are parameters: both come from the user's real expanded paths at runtime. The shim
    // is the form ManagerActions.ShimCommand chose, so a path with a space arrives as its 8.3 short form, or quoted.
    public static IReadOnlyList<string> StartArguments(string briefingPath, string shimPath) =>
    [
        "--bg", "--model", "fable", "-n", Name, "--remote-control", Name, "--strict-mcp-config",
        "--permission-mode", "dontAsk",
        "--append-system-prompt-file", briefingPath,
        "--allowedTools", $"Bash({shimPath.Replace('\\', '/')} task:*)", $"Bash({shimPath.Replace('\\', '/')} report:*)",
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
        // Task 56b76f4a: "when asked to do the work, manager session should create a new task for it". "You do not do the
        // tasks" (the first line) told the manager what not to do, never what to do instead, so asked-for work stalled:
        // on 2026-11-25 the user's "manager doesn't have to tell me each merge" went into the manager's memory folder,
        // and only "don't you need to pr?" got it a task (6a87df67); the voice task (0f73cd0f) came only after the user
        // spelt out "create a task and deploy a claude agent". So asked-for work becomes a task with a session on it at
        // once, unless an open task already covers it. "Memory", not "note": the shim's help calls `task add` adding a
        // note, so "never keep it in a note" would forbid the very verb prescribed here. `task start` without --folder
        // starts where exactly one folder is known (ClaudeStart.Folders: the workspace's other sessions and open VS Code
        // folders), names them when there are several, and refuses when there is none; the manager has no file tools,
        // so the fallback is another task's folder from task show, else asking the user. The new task's title and short
        // id reach the user through the last line ("Name a task by its title and short id"). Kept to five lines because
        // the tests cap the briefing under 40.
        "When the user asks for work (a fix, a change, a feature, a look into something), it becomes a task: never do it, save",
        "  it to your memory or say it is not your job. Asks about how you behave count too: this briefing is Deskbar's code.",
        $"  If an open task covers it, comment there, quoting the user. Otherwise: {shim} task add - --workspace <name> (a few",
        "  words naming the work), task describe <id> - quoting the user, then task start <id>. If start names several folders,",
        "  pass the right one with --folder; if it knows none, use another task's folder on that workspace (task show), else ask.",
        "Free text always goes on stdin with - in its place, never quoted on the command line.",
        "Refused: close, delete, and setting a task to done or closed. The shim answers:",
        $"  {Refusal}",
        // Two pairs of lines joined into one each (task c28ed674), to make room for the report lines under the 40 line cap.
        $"Editing or settling a comment is refused too: {CommentRefusal} When that happens, tell the user and leave the task alone.",
        $"When you quote the user in a comment, start it with the exact marker \"{QuoteMarker}\" and copy their words unchanged. Never paraphrase into a quote.",
        // Task c28ed674, "let the manager session submit a bug report to deskbar": a problem with Deskbar itself goes to
        // Deskbar's developers through `report` (RemoteControl.Report.cs), the twin of Report a problem…, which the user
        // may not be at the screen to fill in. Only when asked, because the report carries a debug dump that lists every
        // window title on the user's screens. The window it opens shows the reference, so the manager does not wait on it.
        $"When the user asks you to report a problem with Deskbar itself to its developers: {shim} report - with what happened",
        "  on stdin, in the user's words, then what you saw. It sends at once with a debug dump; a window on their screen shows the result.",
        "Do not take a task of your own: never join one.",
        "Never use Monitor, loop, scheduled wakeups, subagents, file reading or transcript reading.",
        "You act when the user messages you, or when a message starts with \"Deskbar wake.\" (event wakes, ManagerWakes).",
        "On a Deskbar wake:",
        $"- Each handed-back task not marked AGAIN: one comment ({shim} task comment <id> -) to wake its session.",
        "  If its status line says the next move is the user's, tell it to run task ask with that move.",
        "  Otherwise tell it to finish (a merged change once it is deployed) with task done, or carry on with the work.",
        "- A task marked AGAIN gets no comment: its session stopped twice, so name it to the user in one line.",
        "- Run the overview only if the wake does not tell you enough.",
        // Task 6a87df67: the user, on the manager listing every merge and question after each wake: "manager doesn't have
        // to tell me each merge and needs you now", "tasks do it themselves already on the bar". Every task already shows
        // its own question, pull request and status line on the bar and in the tasks panel, so a manager that lists them
        // again only adds a second copy to read. It reports what no single task can show: how tasks bear on each other.
        "The bar already shows every task's question, pull request to merge and status line. Never repeat them to the user:",
        "  no list of what waits on the user, no merge reminders, no \"needs you now\".",
        "Report only what the bar cannot show: sessions that collide or do the same work, work stuck on another task or person,",
        "  one decision that blocks several tasks, a session that stopped twice. If there is none, say so in one line.",
        // Task 2c5de076: the user asked that the manager's answer ask for their attention in the Inbox. Deskbar does that
        // itself when the turn ends (ManagerQuestion.AfterStop), showing the reply's first line; the manager only needs
        // to know the line exists, so that its first line carries the answer. Kept to the one line it replaced: the tests
        // cap the briefing under 40 and it is at the cap.
        "Answer in full only when the user asks you directly. Your reply's first line shows in their Inbox: make it the answer.",
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
