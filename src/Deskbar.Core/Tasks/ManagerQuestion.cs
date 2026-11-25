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
    /// The record after a prompt reached a session in the manager's folder: the question cleared when the prompt is the
    /// user's reply to TODAY'S stored manager, otherwise nothing to store. A Deskbar wake arrives as a prompt too (a bare
    /// resume with the wake's text), and it is not the user's reply, so it leaves the question open. The prefix is looked
    /// for ANYWHERE in the prompt, not only at its start: the other road a wake takes, the inbox read by the waiting Stop
    /// hook (asyncRewake), was not measured, and if Claude Code ever reports that as a prompt it may wrap the text.
    /// </summary>
    public static Maybe<ManagerRecord> AfterPrompt(Maybe<ManagerRecord> stored, string sessionId, Maybe<string> prompt) =>
        stored.Where(record => record.Question is not null
                               && record.SessionId == sessionId
                               && !prompt.GetValueOrDefault("").Contains(ManagerWakes.Prefix, StringComparison.Ordinal))
            .Map(Cleared);

    static ManagerRecord Cleared(ManagerRecord record) => record with { Question = null, AskedAt = null };

    static string Trimmed(string text) =>
        text.Trim() is var trimmed && trimmed.Length > MaxLength ? trimmed[..(MaxLength - 1)].TrimEnd() + "…" : trimmed;
}
