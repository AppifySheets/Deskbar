using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// THE MANAGER ASKS THE USER A QUESTION (task 61e4d143, option 2 of three weighed in session 0af1a941). Design:
// docs/superpowers/specs/2026-11-25-manager-question-line-design.md.
//
// Until this, the manager had no way to put a question in front of the user: `task ask` is a session's form, and every
// session form is refused from the manager's folder because the manager has no task. So a decision it needed waited
// until the user happened to open its window. Now:
//
//   * `task ask -` from the manager's folder (no --session) stores ONE question on the manager's record (ManagerRecord
//     .Question), replacing any earlier one. The panel and the bar show it as the manager line, above every task's
//     question in "Waiting on you", in a colour of its own. It stays one line and never becomes a task, so the rule
//     "the manager is not a task" from the manager session design holds: no tick, nothing to close.
//   * Clicking the line opens the manager (the Manager button's click). Opening alone does NOT clear the question: the
//     user may open it and look away.
//   * The user's reply clears it: a prompt typed into the manager's own session (UserPromptSubmit from its folder, for
//     the stored session id) that is not one of Deskbar's own wakes. The briefing tells the manager to ask again when
//     the reply left the question open, the same deal a task session gets when Deskbar sets its question aside.
//   * `task answered` from the manager's folder clears it too, for a question the manager settled itself.
//   * A new manager (a new day, a claim) is a new record, so yesterday's question goes with yesterday's session.
//
// THE MANAGER'S ANSWER asks for the user's attention too (task 2c5de076; the user: "does the manager answer my question by
// asking for my attention in inbox? it should"). Before this, a reply to the user was plain text in the manager's own
// terminal and nothing else, so a reply written while the user looked elsewhere waited until they happened to open it.
// Now the same manager line carries it:
//
//   * The user's message to the manager marks the record Replying (AfterPrompt). A Deskbar wake does not: its replies
//     are mostly one line saying nothing needs the user, and a line for each would be noise.
//   * When that turn ends (the Stop hook, AfterStop), the reply's first line is stored as the Answer and the manager line
//     shows it: "Manager answered", the line, its age. The question wins while one is open: it asks for the same window.
//   * Unless the manager's terminal was the window in front as the turn ended: the user watched the reply being written.
//   * It clears when the user opens the manager (the Manager button, the line, `task manager`), brings its window to the
//     front, or writes to it again (Read and AfterPrompt). Unlike the question, opening is enough: the reply is the
//     thing to read, and the window shows it.
//
// The manager vets nothing: every task's question still reaches the user at once (Petre asked whether the manager should
// filter them first; the questions on the board that day all needed him personally, so a gate would only add delay).
//
// Pure: the record and the inputs come in, the record to store goes out. RemoteControl does the storing.
public static class ManagerQuestion
{
    /// <summary>What `task ask` answers the manager when no manager is stored (it was not started by Deskbar).</summary>
    public const string NoManager = "Deskbar has no manager session on record; open it from the bar's Manager button first.";

    public const string Empty = "The question is empty; give it on stdin.";

    /// <summary>The longest question kept, so a pasted page cannot blow up the bar's line or state.json.</summary>
    public const int MaxLength = 2000;

    /// <summary>The manager's question, replacing any earlier one, asked now.</summary>
    public static Result<ManagerRecord> Ask(Maybe<ManagerRecord> stored, string text, DateTimeOffset now) =>
        stored.ToResult(NoManager)
            .Ensure(_ => !string.IsNullOrWhiteSpace(text), Empty)
            .Map(record => record with { Question = Trimmed(text), AskedAt = now });

    /// <summary>The record with no question open, or the same record when none was.</summary>
    public static Result<ManagerRecord> Answered(Maybe<ManagerRecord> stored) =>
        stored.ToResult(NoManager).Map(Cleared);

    /// <summary>
    /// The record after a prompt reached a session in the manager's folder, or nothing to store when it is unchanged (another
    /// session in the folder, or nothing to change). The user's message to TODAY'S stored manager clears its question and
    /// any unread answer, and marks the record Replying, so the end of this turn is an answer to the user (AfterStop). A
    /// Deskbar wake arrives as a prompt too (a bare resume with the wake's text), and it is not the user's message, so it
    /// leaves the question and the answer as they are and marks the turn as no reply. The prefix is looked for ANYWHERE in
    /// the prompt, not only at its start: the other road a wake takes, the inbox read by the waiting Stop hook
    /// (asyncRewake), was not measured, and if Claude Code ever reports that as a prompt it may wrap the text.
    /// </summary>
    public static Maybe<ManagerRecord> AfterPrompt(Maybe<ManagerRecord> stored, string sessionId, Maybe<string> prompt) =>
        Changed(stored.Where(record => record.SessionId == sessionId),
            record => prompt.GetValueOrDefault("").Contains(ManagerWakes.Prefix, StringComparison.Ordinal)
                ? record with { Replying = false }
                : Read(Cleared(record)) with { Replying = true });

    /// <summary>
    /// The record after a turn of a session in the manager's folder ended (the Stop hook), or nothing to store. Only a turn
    /// that began with the user's message (Replying) and only today's stored manager: its reply's first line becomes the
    /// Answer the manager line shows, unless <paramref name="inFront"/> says the manager's window was in front, where the
    /// user saw the reply arrive. <paramref name="reply"/> is the Stop hook's last_assistant_message (Claude Code 2.1.47
    /// on); without one the line still asks the user to come and read it.
    /// </summary>
    public static Maybe<ManagerRecord> AfterStop(Maybe<ManagerRecord> stored, string sessionId, Maybe<string> reply,
        bool inFront, DateTimeOffset now) =>
        stored.Where(record => record.Replying && record.SessionId == sessionId)
            .Map(record => inFront
                ? record with { Replying = false }
                // The echo line repeats the user's own message (task fa99f981), so the answer is the line after it.
                : record with { Replying = false, Answer = FirstLine(reply.Map(ManagerSession.WithoutEcho)), AnsweredAt = now });

    /// <summary>
    /// The record once the user has seen the manager (opened it, or its window came to the front): the answer is read, so
    /// the line goes. Nothing to store when no answer was unread. The question stays: only a reply settles that.
    /// </summary>
    public static Maybe<ManagerRecord> Seen(Maybe<ManagerRecord> stored) =>
        Changed(stored, Read);

    /// <summary>What the line says when the reply came without its text (a Claude Code older than 2.1.47).</summary>
    public const string NoReplyText = "The manager replied. Open it to read the answer.";

    /// <summary>The longest first line kept: the bar shows two lines of it, the panel all of it, and the reply is a click away.</summary>
    public const int AnswerLength = 300;

    static ManagerRecord Cleared(ManagerRecord record) => record with { Question = null, AskedAt = null };

    static ManagerRecord Read(ManagerRecord record) => record with { Answer = null, AnsweredAt = null };

    // The record `change` makes, or nothing when it makes none (the caller then stores nothing and repaints nothing).
    static Maybe<ManagerRecord> Changed(Maybe<ManagerRecord> stored, Func<ManagerRecord, ManagerRecord> change) =>
        stored.Map(record => (Before: record, After: change(record)))
            .Where(pair => !pair.After.Equals(pair.Before))
            .Map(pair => pair.After);

    // The reply's first line that has words in it, as plain text: without the bold marks, which the line cannot draw,
    // and without the marks that open a heading, a quote or a list item. Cut to AnswerLength. A reply with no words in it
    // reads as NoReplyText, as a missing one does.
    static string FirstLine(Maybe<string> reply) =>
        reply.GetValueOrDefault("").Split('\n')
            .Select(line => line.Replace("**", "").Trim().TrimStart('#', '>', ' '))
            .Select(line => line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal) ? line[2..] : line)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0) is { } line
            ? line.Length > AnswerLength ? line[..(AnswerLength - 1)].TrimEnd() + "…" : line
            : NoReplyText;

    static string Trimmed(string text) =>
        text.Trim() is var trimmed && trimmed.Length > MaxLength ? trimmed[..(MaxLength - 1)].TrimEnd() + "…" : trimmed;
}
