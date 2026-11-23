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
    public static string Start(ClaudeHookEvent e, Maybe<TaskItem> own, IReadOnlyList<TaskItem> offered) =>
        string.Join("\n", Intro(e.SessionId)
            .Concat(own.HasValue ? [$"Your task is {Describe(own.Value)}. Keep working on it."] : Choice(e.SessionId, offered))
            .Append("")
            .Concat(Commands(e.SessionId))) + "\n";

    /// <summary>
    /// The one line a session reads with each prompt: its task, or that the user closed it and new work needs a new
    /// task, or that it has none yet.
    /// </summary>
    public static string Prompt(string sessionId, Maybe<TaskItem> own, Maybe<TaskItem> closed) =>
        // Ruling 49: a prompt never settles a question (a /loop or scheduled check-in is not the user answering), so while
        // the task waits the reminder keeps it open and tells the session how to resolve it.
        own is { HasValue: true, Value: { Status: TaskStatus.WaitingOnYou } waiting }
            ? $"Deskbar: your question \"{Cut(waiting.Question.GetValueOrDefault(waiting.Title))}\" is still open, and your task waits on the user. If this message answers it, or the question no longer matters, run {Shim} task answered --session {sessionId}\n"
        // A prompt leaves a Done task Done (RemoteControl.Prompted): a question about finished work must not reopen it.
        // When the message is more work on it, the session reopens it itself; Join moves Done back to In progress.
        : own is { HasValue: true, Value: { Status: TaskStatus.Done } done }
            ? $"Deskbar: your task is {Describe(done)}. If this message gives you more work on it, run {Shim} task join {done.Id.ToString()[..8]} --session {sessionId} to put it back in progress; if it only asks about the finished work, leave it done.\n"
        : own.HasValue
            ? $"Deskbar: your task is {Describe(own.Value)}. Keep its status line current: {Shim} task status-line - --session {sessionId}, the line on stdin (a heredoc).\n"
        : closed.HasValue
            ? $"Deskbar: the user closed your task \"{Cut(closed.Value.Title)}\". Work on this prompt needs a new task: {Shim} task new - --session {sessionId}, the title on stdin (a heredoc).\n"
        : $"Deskbar: this session has no task yet. Once you know what you are working on, run {Shim} task join <id> --session {sessionId} with an id from {Shim} task list, or {Shim} task new - --session {sessionId} with the title on stdin (a heredoc).\n";

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
        "Keep the task lean: say what needs the user, and nothing else. The title is a few words naming the work. The status line is one short phrase, under about 60 characters, saying where it stands. Comments are rare: a comment is for the user and only for what needs their attention or a result they must know (a PR to merge, a decision made for them, a blocker), in one or two short lines, with no progress logs, measurements, spike notes or findings dumps (those belong in the PR, the repo's notes or the session itself). When the user must act, say exactly what the user must do in the first words, or use task ask if it is a decision.",
        "Never close or delete a task, and leave the user's own notes alone unless they ask.",
    ];

    // A task as one line: id prefix, title, status, and the status line or question when there is one.
    static string Describe(TaskItem t) =>
        $"{t.Id.ToString()[..8]} \"{Cut(t.Title)}\" ({StatusText(t.Status)}{Detail(t)}{(t.WorkspaceId.HasValue ? "" : ", on no workspace")})";

    // Petre's own description (Ruling 58) follows, longer than a title is cut: for a note he handed to Claude it is the
    // brief. Still bounded, since twenty tasks may be listed; `task list` has the whole text.
    static string Detail(TaskItem t) =>
        (t.Status == TaskStatus.WaitingOnYou && t.Question.HasValue ? $", asked: \"{Cut(t.Question.Value)}\""
        : t.StatusLine.Length > 0 ? $", status line: \"{Cut(t.StatusLine)}\""
        : "")
        + (t.Description.Length > 0 ? $", the user's description: \"{Cut(t.Description, DescriptionLength)}\"" : "");

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
