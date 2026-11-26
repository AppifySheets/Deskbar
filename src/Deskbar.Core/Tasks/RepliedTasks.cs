using System.Collections.Immutable;

namespace Deskbar.Core.Tasks;

// A WAITING TASK STOPS WAITING ONCE CLAUDE WORKS ON IT AGAIN AFTER PETRE'S REPLY (task 8bd18246).
//
// WHY. A task waits on Petre from a session's `task ask` until the session runs `task answered` (Ruling 49: a prompt
// proves nothing, the session decides). Sessions often skip that: Petre replies (a comment on the task, or a message in
// the session's terminal), the session gets to work on the reply, and the task sits on top of "Waiting on you" with a
// question that no longer holds while Claude is busy on it. On 2026-11-25 a task asking "Review and merge #285" stayed
// waiting while its session reworked the pull request on Petre's comment "remove footnote about ai".
//
// THE RULE, per open task Waiting on you with a question:
//   * one of its LIVE sessions is working (busy and listed for StalledTasks.BusyGrace, the set StalledTasks takes a task
//     back on), AND
//   * Petre has replied since the task began waiting (WaitingSince, or Raised for a file from before it): a comment of his
//     own on the task (no author: not a session's, Deskbar's or the manager's), or a prompt in one of the task's live
//     sessions (TaskService.Prompted; Deskbar's own resume message is not counted, see RemoteControl.Prompted).
// Both are needed. Working alone is not enough: a session is busy for a while right after it asks, finishing its turn,
// and a session watching its pull request wakes to rebase it while the merge question still holds. A reply alone is not
// enough either: until the session works on it, the question is still what Petre sees.
//
// WHAT HAPPENS is a SET-ASIDE, not a settlement (TaskBoard.SetQuestionAside): the task goes In progress and to Claude,
// leaves the waiting block, and keeps the question in TaskItem.QuestionSetAside. The session decides at the end of its
// turn (Ruling 49 stands): the Stop hook asks it once (SessionBriefing.SetAsideNudge) to run `task ask` if the question
// still holds or `task answered` if the reply settled it. Asking the same question again restores it as it was (its place
// in the block, WaitingSince). If the session does neither, the first poll that finds none of its sessions busy asks it
// to write the question afresh from the facts (task 0cac3082, TaskService's `regenerating`, SessionBriefing.Regenerate),
// and only if that brings nothing either is the old question restored (Restore; TaskBoard.HandBack does the same as a
// fallback). So a reply that did not settle the question costs
// it nothing but a stretch in "Claude working".
//
// KNOWN LIMITATION (Ruling 33's): a /loop or scheduled check-in arrives as a prompt that cannot be told from Petre's. In a
// session whose task waits, it sets the question aside while the check-in runs, and the session then asks it again or it
// is restored. The cost is the question leaving the block for that turn and one line in the thread; a snoozed task is left
// alone. Prompts Deskbar itself delivers (a comment by resume, the keeper's resume message) are not counted, nor are those
// Claude Code delivers (a subagent's hand-back, another session's message, a background job's notice, an idle notice;
// task b846f5ad), see
// SessionBriefing.FromTheUser.
//
// THIS IS A PURE FUNCTION over the board, the working set and the prompt times; TaskService keeps the prompt times in
// memory (a prompt before a Deskbar restart is not seen, so a restart can only make this rule wait for the next reply).
public static class RepliedTasks
{
    /// <summary>
    /// The waiting tasks whose question to set aside now, each with the question as it was read (so a new ask that lands
    /// meanwhile is not the one set aside, see TaskBoard.SetQuestionAside).
    /// </summary>
    /// <param name="working">The sessions busy and listed for at least StalledTasks.BusyGrace.</param>
    /// <param name="prompted">When each session last had a prompt submitted, by session id.</param>
    public static IReadOnlyList<(Guid TaskId, string Question)> SetAside(TaskBoard board, IReadOnlySet<string> working,
        ImmutableDictionary<string, DateTimeOffset> prompted) =>
        board.Tasks
            // A snoozed waiting task is left alone: Petre has seen the question and put it off, and setting it aside would
            // end the snooze (TaskBoard.Woken) for every check-in its session runs.
            .Where(t => t is { IsOpen: true, Status: TaskStatus.WaitingOnYou, Question.HasValue: true, IsSnoozed: false })
            .Where(t => t.Sessions.Any(s => s.IsLive && working.Contains(s.Id)))
            .Where(t => RepliedSince(t, AskedAt(t), prompted))
            .Select(t => (t.Id, t.Question.Value))
            .ToArray();

    // When the question last began to stand: the ask (WaitingSince, or Raised in a file from before it), or a later restore
    // of a set-aside question, which keeps WaitingSince (for the pull request watch) and moves only Raised
    // (TaskBoard.Restored). So the reply that set a question aside never sets it aside a second time.
    static DateTimeOffset AskedAt(TaskItem task) =>
        task.WaitingSince.GetValueOrDefault(task.Raised) is var since && since > task.Raised ? since : task.Raised;

    /// <summary>
    /// The set-aside questions to restore now (TaskBoard.RestoreQuestion): an open, In progress task with a question set
    /// aside and no live session busy. The session's turn is over and it neither asked again nor settled the question, for
    /// instance because the set-aside landed just after its Stop hook had run, so nothing asked it. Waiting for
    /// StalledTasks.After instead would hide the question for a quarter of an hour.
    /// </summary>
    /// <param name="busy">Every session the list shows busy now, grace or not.</param>
    public static IReadOnlyList<Guid> Restore(TaskBoard board, IReadOnlySet<string> busy) =>
        board.Tasks
            .Where(t => t is { IsOpen: true, Status: TaskStatus.InProgress, QuestionSetAside.HasValue: true })
            .Where(t => !t.Sessions.Any(s => s.IsLive && busy.Contains(s.Id)))
            .Select(t => t.Id)
            .ToArray();

    // Petre's own comment after the task began waiting, or a prompt in one of its live sessions after it.
    static bool RepliedSince(TaskItem task, DateTimeOffset since, ImmutableDictionary<string, DateTimeOffset> prompted) =>
        task.Comments.Any(c => c.SessionId.HasNoValue && c.At > since)
        || task.Sessions.Any(s => s.IsLive && prompted.TryGetValue(s.Id, out var at) && at > since);

    /// <summary>
    /// How Deskbar's set-aside line opens. TaskComment.IsSetAsideNote finds the line by it, so TaskBoard can fold it once
    /// the question it reports on is settled or back on the line.
    /// </summary>
    public const string NoteOpening = "Set aside while Claude works on your reply:";

    /// <summary>
    /// The opening of the same line before task aa42794f shortened it ("too much bla bla"), so the lines already in
    /// tasks.json fold too.
    /// </summary>
    public const string OldNoteOpening = "Deskbar set the question aside at ";

    /// <summary>
    /// Deskbar's line in the thread when it sets a question aside: why, and the question itself as a quote, so the thread
    /// still says what was asked once the line under the title no longer does. Short on purpose (task aa42794f, Petre:
    /// "too much bla bla"): the thread's header already says Deskbar wrote it and at what time, so the line repeats
    /// neither. TaskBoard folds it once it no longer matters (FoldSetAsideNotes).
    /// </summary>
    public static string Note(string question) =>
        $"{NoteOpening}\n\n"
        +string.Join("\n", question.Split('\n').Select(line => $"> {line.TrimEnd('\r')}"));
}
