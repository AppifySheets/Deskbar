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
    const string Shim = "deskbar.cmd";

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
        "  **Found it**: the guard ran twice",
        "  EOF",
        "Never put free text with quotes or & | < > on the command line.",
        "While you work on it:",
        $"  {Shim} task status-line - --session {sessionId}   (keep it current, one short line)",
        $"  {Shim} task ask - --session {sessionId}   (when you need a decision from the user; the task waits on them)",
        $"  {Shim} task answered --session {sessionId}   (when the user's reply settles your question, or you settle it yourself; run it straight away)",
        "A message from the user never resolves your question by itself: you do, with task answered. Never leave a question open once it is resolved.",
        $"  {Shim} task comment - --session {sessionId}   (detail worth keeping, in markdown: findings, links, what is left)",
        $"  {Shim} task done --session {sessionId}   (on a clear finish; only the user closes a task)",
        $"  {Shim} task later - --session {sessionId}   (the title of a follow-up note on your workspace)",
        "Never close or delete a task, and leave the user's own notes alone unless they ask.",
    ];

    // A task as one line: id prefix, title, status, and the status line or question when there is one.
    static string Describe(TaskItem t) =>
        $"{t.Id.ToString()[..8]} \"{Cut(t.Title)}\" ({StatusText(t.Status)}{Detail(t)}{(t.WorkspaceId.HasValue ? "" : ", on no workspace")})";

    static string Detail(TaskItem t) =>
        t.Status == TaskStatus.WaitingOnYou && t.Question.HasValue ? $", asked: \"{Cut(t.Question.Value)}\""
        : t.StatusLine.Length > 0 ? $", status line: \"{Cut(t.StatusLine)}\""
        : "";

    static string StatusText(TaskStatus status) => status switch
    {
        TaskStatus.Backlog => "backlog",
        TaskStatus.InProgress => "in progress",
        TaskStatus.WaitingOnYou => "waiting on the user",
        TaskStatus.Done => "done, waiting for the user to close it",
        _ => "closed",
    };

    static string Cut(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= TitleLength ? line : line[..(TitleLength - 3)] + "...";
    }
}
