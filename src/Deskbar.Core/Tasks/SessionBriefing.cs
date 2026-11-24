using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// THE TEXT A CLAUDE CODE SESSION READS FROM DESKBAR'S HOOKS. Design: docs/superpowers/specs/2026-11-22-tasks-board-design.md
// ("Claude sessions", "What a session is offered, and joining a note", "After Done or Closed"); plan Task 8.
//
// Claude Code adds what a SessionStart or a UserPromptSubmit hook prints to the session's context, so this is written
// FOR CLAUDE: plain sentences, the session's own id, and every command spelt out exactly as it is to be run, because a
// session copies commands rather than composing them. Two rules keep it cheap, since every session pays for it:
//
//   * Start (SessionStart) is the long one, and it is bounded: a title is cut at 80 characters and the offered list at
//     20 tasks, with a pointer at `task list` for the rest, which keeps it far under Claude Code's 10,000 characters
//     for a hook's context (the test builds fifty tasks with 300-character titles).
//   * Prompt (UserPromptSubmit, on every prompt) is ONE line.
//
// Ids are shown by their first 8 characters, which `task join` and every other form accept as a prefix (TaskBoard.Find
// takes four or more), to keep the list short. No em dashes, and no names: the person is "the user".
//
// The skill (ClaudeSkill.Text, Task 9) carries the longer explanation of the rules (never close, leave the user's notes
// alone unless asked); this text names them in a sentence so a session that never loads the skill still keeps them.
public static class SessionBriefing
{
    const int TitleLength = 80;
    const int ListLength = 20;
    const int DescriptionLength = 200;
    const string Shim = "deskbar.cmd";

    // The family block's bounds (subtasks spec, "What a session reads"): at most this many siblings or subtasks, this
    // many sessions per task and this many of the parent's comments, and the whole block cut at FamilyLength with a
    // pointer at `task family`. Twenty subtasks of five sessions each with 300-character titles stay under it (test), so
    // a SessionStart briefing that carries it stays far under Claude Code's 10,000 characters for a hook's context.
    const int FamilyLength = 4000;
    const int FamilyList = 10;
    const int SessionsShown = 3;
    const int CommentsShown = 3;

    /// <summary>
    /// What the Stop hook tells a session whose turn ended with its task still In progress and Claude's (stalled tasks,
    /// spec docs/superpowers/specs/2026-11-23-stalled-claude-tasks-design.md, part 3; RemoteControl.ClaudeHook). Sessions
    /// often stop with a status line like "ready to merge" and never ask, so the task stays off the bar. This asks once
    /// per turn and leaves an out, so a turn that really is not waiting on the user ends with nothing more said.
    /// </summary>
    public const string ParkedNudge =
        $"If the next move is the user's (a pull request to merge, something to try, a decision), run {Shim} task ask with " +
        "what they must do, the text on stdin. If it is not, stop without saying anything.";

    /// <summary>
    /// What a session reads when it starts, resumes, clears or compacts: its own task when it has one, otherwise the
    /// tasks it is offered and how to join one or create its own.
    /// </summary>
    /// <remarks>
    /// <paramref name="expand"/> makes image paths absolute (TaskService.ExpandImages); the own task's description images
    /// are listed whole with it (Detail). Left null they are listed as stored.
    /// <paramref name="family"/> is the <see cref="Family"/> block of the session's own task, "" when it has no parent
    /// and no subtasks; it follows the line naming the task (subtasks spec).
    /// </remarks>
    public static string Start(ClaudeHookEvent e, Maybe<TaskItem> own, IReadOnlyList<TaskItem> offered, Func<string, string>? expand = null,
        string family = "") =>
        string.Join("\n", Intro(e.SessionId)
            .Concat(own.HasValue ? [$"Your task is {Describe(own.Value, expand ?? Same)}. Keep working on it."] : Choice(e.SessionId, offered))
            .Concat(family.Length > 0 ? [family] : Array.Empty<string>())
            .Append("")
            .Concat(Commands(e.SessionId))) + "\n";

    /// <summary>
    /// The one line a session reads with each prompt: its task, or that the user closed it and new work needs a new
    /// task, or that it has none yet.
    /// </summary>
    public static string Prompt(string sessionId, Maybe<TaskItem> own, Maybe<TaskItem> closed, Func<string, string>? expand = null) =>
        // Ruling 49: a prompt never settles a question (a /loop or scheduled check-in is not the user answering), so while
        // the task waits the reminder keeps it open and tells the session how to resolve it.
        own is { HasValue: true, Value: { Status: TaskStatus.WaitingOnYou } waiting }
            ? $"Deskbar: your question \"{Cut(waiting.Question.GetValueOrDefault(waiting.Title))}\" is still open, and your task waits on the user. If this message answers it, or the question no longer matters, run {Shim} task answered --session {sessionId}\n"
        // A prompt leaves a Done task Done (RemoteControl.Prompted): a question about finished work must not reopen it.
        // When the message is more work on it, the session reopens it itself; Join moves Done back to In progress.
        : own is { HasValue: true, Value: { Status: TaskStatus.Done } done }
            ? $"Deskbar: your task is {Describe(done, expand ?? Same)}. If this message gives you more work on it, run {Shim} task join {done.Id.ToString()[..8]} --session {sessionId} to put it back in progress; if it only asks about the finished work, leave it done.\n"
        : own.HasValue
            ? $"Deskbar: your task is {Describe(own.Value, expand ?? Same)}. Keep its status line current: {Shim} task status-line - --session {sessionId}, the line on stdin (a heredoc).{SubtaskHint(own.Value, sessionId)}\n"
        : closed.HasValue
            ? $"Deskbar: the user closed your task \"{Cut(closed.Value.Title)}\". Work on this prompt needs a new task: {Shim} task new - --session {sessionId}, the title on stdin (a heredoc).\n"
        : $"Deskbar: this session has no task yet. Once you know what you are working on, run {Shim} task join <id> --session {sessionId} with an id from {Shim} task list, or {Shim} task new - --session {sessionId} with the title on stdin (a heredoc).\n";

    static string Same(string markdown) => markdown;

    // A subtask's prompt line reminds a long-running session that the family exists and how to read it again (subtasks
    // spec: siblings move on after the session started). Still one line.
    static string SubtaskHint(TaskItem task, string sessionId) =>
        task.ParentId.HasValue
            ? $" It is a subtask of {task.ParentId.Value.ToString()[..8]}: {Shim} task family --session {sessionId} shows the tasks around it."
            : "";

    // --- the family block (subtasks spec, docs/superpowers/specs/2026-11-24-subtasks-design.md) ----------------------

    /// <summary>
    /// What a session on a subtask, or on a parent, reads about the tasks around its own: for a subtask, the parent (its
    /// line, the user's description, its last unsettled comments and its sessions with their transcripts) and the
    /// siblings; for a parent, its subtasks. Each session is listed with its folder and, when <paramref name="transcript"/>
    /// finds one, its transcript file, which the session is told to read: that conversation is where a task's context
    /// really is, and reading it is what spares Petre explaining it. Printed by the `task join` reply, the SessionStart
    /// briefing and `task family`. Bounded (see FamilyLength); no trailing newline.
    /// </summary>
    public static string Family(TaskFamily family, Func<TaskSession, Maybe<string>> transcript)
    {
        var more = $"{Shim} task family {family.Task.Id.ToString()[..8]}";
        var lines = family.Parent.HasValue
            ? SubtaskLines(family.Parent.Value, family.Siblings, AnyLive(family), transcript, more)
            : ParentLines(family.Subtasks, AnyLive(family), transcript, more);
        return Bounded(lines.ToArray(), more);
    }

    const string Messaging = "A live session on this machine can be reached with Claude Code's own session messaging (ListAgents, SendMessage).";

    static IEnumerable<string> SubtaskLines(TaskItem parent, IReadOnlyList<TaskItem> siblings, bool anyLive,
        Func<TaskSession, Maybe<string>> transcript, string more) =>
    [
        $"This task is a subtask of {Brief(parent)}.",
        .. parent.Description.Length > 0 ? [$"The user's description of the parent: \"{Cut(parent.Description, DescriptionLength)}\""] : Array.Empty<string>(),
        .. RecentComments(parent),
        .. parent.Sessions.Count > 0 ? ["Sessions on the parent:", .. SessionLines(parent, "  ", transcript)] : Array.Empty<string>(),
        "Before you start, read the end of the parent session's transcript to learn what was done and decided, and look at the siblings below so you do not redo or collide with their work.",
        .. anyLive ? [Messaging] : Array.Empty<string>(),
        .. siblings.Count > 0 ? ["Its other subtasks:", .. Listed(siblings, transcript, more)] : Array.Empty<string>(),
    ];

    static IEnumerable<string> ParentLines(IReadOnlyList<TaskItem> subtasks, bool anyLive, Func<TaskSession, Maybe<string>> transcript, string more) =>
    [
        "Before you start, look at its subtasks below so you do not redo or collide with their work.",
        .. anyLive ? [Messaging] : Array.Empty<string>(),
        "This task has subtasks:",
        .. Listed(subtasks, transcript, more),
    ];

    // Siblings or subtasks: each one's line, the user's description and its sessions, at most FamilyList of them.
    static IEnumerable<string> Listed(IReadOnlyList<TaskItem> tasks, Func<TaskSession, Maybe<string>> transcript, string more) =>
        tasks.Take(FamilyList)
            .SelectMany(t => new[] { $"  {Brief(t)}" }
                .Concat(t.Description.Length > 0 ? [$"    the user's description: \"{Cut(t.Description, DescriptionLength)}\""] : Array.Empty<string>())
                .Concat(SessionLines(t, "    ", transcript)))
            .Concat(tasks.Count > FamilyList ? [$"  and {tasks.Count - FamilyList} more: {more}"] : Array.Empty<string>());

    // The parent's last unsettled comments, oldest of them first, each cut: what the user or a session last said on it.
    static IEnumerable<string> RecentComments(TaskItem parent) =>
        parent.Comments.Where(c => c.Settled.HasNoValue).OrderBy(c => c.At).TakeLast(CommentsShown).ToArray() is { Length: > 0 } recent
            ? recent.Select(c => $"  {(c.SessionId.HasValue ? "a session" : "the user")}: \"{Cut(c.Markdown, DescriptionLength)}\"").Prepend("Recent comments on the parent:")
            : [];

    // At most SessionsShown sessions, live ones first, then the latest joined: the ones worth reading.
    static IEnumerable<string> SessionLines(TaskItem task, string indent, Func<TaskSession, Maybe<string>> transcript) =>
        task.Sessions.OrderByDescending(s => s.IsLive).ThenByDescending(s => s.Joined).Take(SessionsShown)
            .Select(s => $"{indent}{Prefix(s.Id)} ({(s.IsLive ? "live" : "ended")}, folder {s.Folder})"
                + transcript(s).Map(path => $", transcript {path}").GetValueOrDefault(""));

    // Whether any session around the task (not the task's own, which is the reader) is live, so messaging is worth naming.
    static bool AnyLive(TaskFamily family) =>
        family.Parent.ToList().Concat(family.Siblings).Concat(family.Subtasks).Any(t => t.HasLiveSession);

    // Whole lines up to FamilyLength, then the pointer at `task family` for the rest.
    static string Bounded(IReadOnlyList<string> lines, string more)
    {
        var text = string.Join("\n", lines);
        if (text.Length <= FamilyLength) return text;
        var tail = $"The rest: {more}";
        var kept = lines
            .Select((line, i) => (Line: line, End: lines.Take(i + 1).Sum(l => l.Length + 1)))
            .TakeWhile(x => x.End + tail.Length <= FamilyLength)
            .Select(x => x.Line);
        return string.Join("\n", kept.Append(tail));
    }

    // A task as one line without the user's description, which the family block gives a line of its own.
    static string Brief(TaskItem t) =>
        $"{t.Id.ToString()[..8]} \"{Cut(t.Title)}\" ({StatusText(t.Status)}"
        + (t.Status == TaskStatus.WaitingOnYou && t.Question.HasValue ? $", asked: \"{Cut(t.Question.Value)}\""
            : t.StatusLine.Length > 0 ? $", status line: \"{Cut(t.StatusLine)}\"" : "")
        + ")";

    static string Prefix(string id) => id.Length > 8 ? id[..8] : id;

    static IEnumerable<string> Intro(string sessionId) =>
    [
        $"Deskbar keeps the user's task list, and this Claude Code session is {sessionId}.",
        $"Run every command below from your working folder, through {Shim} (%APPDATA%\\Deskbar\\deskbar.cmd; in Git Bash \"$APPDATA/Deskbar/deskbar.cmd\").",
    ];

    static IEnumerable<string> Choice(string sessionId, IReadOnlyList<TaskItem> offered) =>
        offered.Count == 0
            ? [
                "There are no open tasks for you to join. Once you know what this session is working on, create its task:",
                $"  {Shim} task new - --session {sessionId}   (the title on stdin, as below)",
              ]
            : [
                "This session has no task yet. Once you know what it is working on, join the task it continues, or create one if none fits. Open tasks:",
                .. offered.Take(ListLength).Select(t => $"  {Describe(t)}"),
                .. offered.Count > ListLength ? [$"  and {offered.Count - ListLength} more: {Shim} task list"] : Array.Empty<string>(),
                $"Join one: {Shim} task join <id> --session {sessionId}",
                $"Or create one: {Shim} task new - --session {sessionId}   (the title on stdin, as below)",
              ];

    // FREE TEXT GOES ON STDIN (review of PR B, I-1): deskbar.cmd hands its command line to cmd, which runs whatever
    // follows a quote it misreads, so a title, status line, question or comment with quotes or & | < > in it is not safe
    // there. Each text form takes `-` and reads its text from stdin (ControlArguments.WithTextInput).
    static IEnumerable<string> Commands(string sessionId) =>
    [
        "Free text (a title, a status line, a question, a comment) always goes on stdin: put - in its place and give the text as a heredoc, for example",
        $"  {Shim} task comment - --session {sessionId} <<'EOF'",
        "  PR 512 is ready: merge it when CI is green",
        "  EOF",
        "Never put free text with quotes or & | < > on the command line.",
        "While you work on it:",
        $"  {Shim} task status-line - --session {sessionId}   (keep it current, one short line)",
        $"  {Shim} task ask - --session {sessionId}   (when you need a decision from the user; the task waits on them)",
        $"  {Shim} task answered --session {sessionId}   (when the user's reply settles your question, or you settle it yourself; run it straight away; it also settles, that is folds away, your comments written since you asked)",
        $"  {Shim} task comment-settle <comment-id> --session {sessionId}   (settle one of your own comments that no longer matters)",
        "A message from the user never resolves your question by itself: you do, with task answered. Never leave a question open once it is resolved.",
        $"  {Shim} task comment - --session {sessionId}   (rare: only what the user must act on or know, in one or two short lines)",
        $"  {Shim} task done --session {sessionId}   (on a clear finish; only the user closes a task)",
        $"  {Shim} task later - --session {sessionId}   (the title of a follow-up note on your workspace)",
        // Subtasks spec: a session splitting its work makes a subtask, which the next session picks up with this one's
        // context (its join reply names this session and its transcript). `task family` is not listed here: the join
        // reply, the start briefing and a subtask's prompt line carry it where it matters.
        $"  {Shim} task subtask - --session {sessionId}   (split off a piece of your work as a subtask; the session that takes it up is briefed on your task and this session)",
        "Keep the task lean: say what needs the user, and nothing else. The title is a few words naming the work. The status line is one short phrase, under about 60 characters, saying where it stands. Comments are rare: a comment is for the user and only for what needs their attention or a result they must know (a PR to merge, a decision made for them, a blocker), in one or two short lines, with no progress logs, measurements, spike notes or findings dumps (those belong in the PR, the repo's notes or the session itself). When the user must act, say exactly what the user must do in the first words, or use task ask if it is a decision.",
        "Never close or delete a task, and leave the user's own notes alone unless they ask.",
    ];

    // A task as one line: id prefix, title, status, and the status line or question when there is one. `expand` is given
    // only for the session's OWN task (see Images): an offered task's pictures are not the session's business yet, and
    // twenty offered tasks' paths would only lengthen what every new session pays for.
    static string Describe(TaskItem t, Func<string, string>? expand = null) =>
        $"{t.Id.ToString()[..8]} \"{Cut(t.Title)}\" ({StatusText(t.Status)}{Detail(t, expand)}{(t.WorkspaceId.HasValue ? "" : ", on no workspace")})";

    // Petre's own description (Ruling 58) follows, longer than a title is cut: for a note he handed to Claude it is the
    // brief. Still bounded, since twenty tasks may be listed; `task list` has the whole text. Its image lines never go into
    // the cut text (a path cut in half is useless); Images lists them whole after it.
    static string Detail(TaskItem t, Func<string, string>? expand) =>
        (t.Status == TaskStatus.WaitingOnYou && t.Question.HasValue ? $", asked: \"{Cut(t.Question.Value)}\""
        : t.StatusLine.Length > 0 ? $", status line: \"{Cut(t.StatusLine)}\""
        : "")
        + (TaskImages.Split(t.Description).Text is { Length: > 0 } text ? $", the user's description: \"{Cut(text, DescriptionLength)}\"" : "")
        + (expand is null ? "" : Images(t.Description, expand));

    // Images in tasks (docs/superpowers/specs/2026-11-24-task-images-design.md): the description's pictures, each path made
    // absolute by `expand` (TaskService.ExpandImages) and given whole, with what to do with them. "Any you have not looked
    // at yet", not "read them": this line comes with every prompt, and a picture read again each turn costs a thousand or
    // two tokens every time (final review of the images branch).
    static string Images(string description, Func<string, string> expand) =>
        TaskImages.Split(description).Images is { Count: > 0 } images
            ? $", images in the description: {string.Join(", ", images.Select(path => AbsolutePath(expand(TaskImages.Join("", [path])))))}; read any you have not looked at yet with your Read tool"
            : "";

    // The path out of one expanded image line, `![image](<C:\...>)` or (no state folder) `![image](task-images/...)`.
    static string AbsolutePath(string imageLine) =>
        imageLine[(imageLine.IndexOf("](", StringComparison.Ordinal) + 2)..^1].Trim('<', '>');

    static string StatusText(TaskStatus status) => status switch
    {
        TaskStatus.Backlog => "backlog",
        TaskStatus.InProgress => "in progress",
        TaskStatus.WaitingOnYou => "waiting on the user",
        TaskStatus.Done => "done, waiting for the user to close it",
        _ => "closed",
    };

    static string Cut(string text, int length = TitleLength)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= length ? line : line[..(length - 3)] + "...";
    }
}
