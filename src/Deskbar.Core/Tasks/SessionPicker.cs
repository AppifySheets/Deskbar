using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

/// <summary>Where a task's settings switch is (SessionPicker.RowOf), as the picker row says it.</summary>
public enum PickPhase
{
    /// <summary>No switch under way: the row shows what the session runs on.</summary>
    Settled,

    /// <summary>A pick waits for the session to pause (PendingSwitch, REQUESTED). Cancel drops it.</summary>
    Waiting,

    /// <summary>The copy is being made (ISSUED). Nothing can be cancelled for the few seconds it takes.</summary>
    Switching,

    /// <summary>The copy runs and is the task's session; the earlier one stops once the copy answers (JOINED). Cancel goes back.</summary>
    Confirming,
}

/// <summary>
/// What the picker row under a task's comment box shows (the Inbox page and a task window): the two buttons' words, the
/// settings the menus tick (<see cref="Shown"/>), the phase in words, whether Cancel is offered, and why the row is
/// read-only when it is. Built by <see cref="SessionPicker.RowOf"/>; <see cref="Signature"/> is its part of the page's
/// redraw signature.
/// </summary>
public sealed record SessionPickRow(
    SessionSettings Shown,
    string ModelWords,
    string ModeWords,
    PickPhase Phase,
    string PhaseWords,
    bool CanCancel,
    Maybe<string> ReadOnlyWhy,
    string Tip)
{
    public bool ReadOnly => ReadOnlyWhy.HasValue;

    /// <summary>Whether the buttons take a pick now: not read-only, and not while the copy is being made.</summary>
    public bool Pickable => !ReadOnly && Phase != PickPhase.Switching;

    public string Signature => $"{ModelWords}|{ModeWords}|{Phase}|{PhaseWords}|{CanCancel}|{ReadOnlyWhy.GetValueOrDefault("")}|{Tip}|{Shown}";
}

// THE PICKER ROW'S WORDS, worked out from the task and Claude Code's list. Pure, so every case is a test; the page only
// draws what this says (TasksWindow.SessionPicker.cs).
//
// WHAT THE BUTTONS SHOW, in this order of trust: a switch under way shows where it is going (its PendingSwitch.To); else
// what Deskbar started the task's current session with (TaskSession.LaunchedWith); else, for a session Deskbar did not
// start or that predates the picker, the model its last reply came from (ClaudeTranscript.LastModel, Claude Code's own
// undocumented field, so said as "last reply"), the rest unknown; else, for a task with no session, the task's pick, which
// the first start will pass. A value not set reads as Claude Code's own ("default effort", "Claude Code's setting").
//
// READ-ONLY: the task's session is listed and runs in a window of its own (VS Code, a terminal: no short id, or not a
// background row), or is a background session Deskbar did not start. A switch never copies or stops such a session
// (SettingsSwitch), so the buttons are disabled, and the tooltip says why, never hidden.
//
// "THE TASK'S SESSION" is SettingsSwitch.SessionOf, the one rule every switch question uses: an ended entry, or a
// session that has moved to another task, is not it, and the row then shows the pick for the next start.
//
// CANCEL is offered while a pick waits for a pause and after the copy joined, until the copy has answered: from then on
// the switch is final (review M2), and the row says the earlier session stops at its next pause.
public static class SessionPicker
{
    public const string FirstStartTip = "No session yet. Your pick is used by the first session Deskbar starts on this task.";
    public const string NoLiveSessionTip = "No session runs on this task now. Your pick is used by the next session Deskbar starts on it.";
    public const string LastReplyTip = "Deskbar did not start this session, so it shows the model of its last reply. Pick to change it.";
    public const string NextPauseTip = "Applies at Claude's next pause.";
    public const string ConfirmingWords = "Switched. The earlier session stops once this one has answered.";

    /// <param name="listed">The last good `claude agents --json` read (none read yet: empty).</param>
    /// <param name="lastModel">The model of a session's last reply, from its transcript; none when unknown.</param>
    /// <param name="copyReplied">Whether a switch's joined copy has answered since it joined.</param>
    public static SessionPickRow RowOf(TaskBoard board, TaskItem task, IReadOnlyCollection<ClaudeAgent> listed,
        Func<TaskSession, Maybe<string>> lastModel, Func<TaskSession, bool> copyReplied)
    {
        var current = SettingsSwitch.SessionOf(board, task);
        var readOnly = current.Bind(s => listed.TryFirst(a => a.SessionId == s.Id).Map(a => (Session: s, Agent: a)))
            .Bind(x => !x.Agent.Background || x.Agent.ShortId.HasNoValue ? Maybe.From(SettingsSwitch.NotDeskbars)
                : !SettingsSwitch.StartedByDeskbar(x.Session) ? Maybe.From(SettingsSwitch.NotStartedByDeskbar)
                : Maybe<string>.None);

        if (task.Switching is { HasValue: true } switching)
        {
            var pending = switching.Value;
            var replied = pending.CopyId.Bind(id => task.Sessions.TryFirst(s => s.Id == id)).Map(copyReplied).GetValueOrDefault(false);
            var (phase, words, cancel) = pending.CopyId.HasValue
                    ? replied ? (PickPhase.Confirming, SettingsSwitch.OriginalStopsAtPause, false) : (PickPhase.Confirming, ConfirmingWords, true)
                : pending.Issued.HasValue ? (PickPhase.Switching, SettingsSwitch.UnderWay, false)
                : (PickPhase.Waiting, WaitWords(pending, listed), true);
            return Made(pending.To, phase, words, cancel, Maybe<string>.None, NextPauseTip, Maybe<string>.None);
        }

        if (current.HasNoValue)
            return Made(task.Wanted.GetValueOrDefault(SessionSettings.None), PickPhase.Settled, "", false, Maybe<string>.None,
                task.Sessions.Count == 0 ? FirstStartTip : NoLiveSessionTip, Maybe<string>.None);

        var session = current.Value;
        if (!session.LaunchedWith.IsEmpty)
            return Made(session.LaunchedWith, PickPhase.Settled, "", false, readOnly, readOnly.GetValueOrDefault(NextPauseTip), Maybe<string>.None);
        var seen = lastModel(session);
        return Made(SessionSettings.None, PickPhase.Settled, "", false, readOnly,
            readOnly.GetValueOrDefault(seen.HasValue ? LastReplyTip : NextPauseTip), seen);
    }

    // A waiting pick says what it waits for: a permission question, or the step in progress.
    static string WaitWords(PendingSwitch pending, IReadOnlyCollection<ClaudeAgent> listed) =>
        listed.TryFirst(a => a.SessionId == pending.FromSession).Map(a => a.Status.Map(s => s == "waiting").GetValueOrDefault(false)).GetValueOrDefault(false)
            ? SettingsSwitch.WaitingOnPermission
            : SettingsSwitch.Busy;

    static SessionPickRow Made(SessionSettings shown, PickPhase phase, string words, bool cancel, Maybe<string> readOnly, string tip, Maybe<string> seenModel) =>
        new(shown, ModelWords(shown, seenModel), ModeWords(shown), phase, words, cancel, readOnly, tip);

    /// <summary>The model button's words: "Sonnet · medium", "claude-opus-5-5 (last reply) · default effort".</summary>
    public static string ModelWords(SessionSettings shown, Maybe<string> seenModel = default) =>
        $"{shown.Model.Map(Label).GetValueOrDefault(seenModel.Map(m => $"{m} (last reply)").GetValueOrDefault("Default model"))} · "
        + shown.Effort.Map(e => SessionChoices.Efforts.TryFirst(c => c.Value == e).Map(c => c.Label).GetValueOrDefault(e).ToLowerInvariant())
            .GetValueOrDefault("default effort");

    /// <summary>The permission button's words, the mode in plain words, or Claude Code's own setting when none is set.</summary>
    public static string ModeWords(SessionSettings shown) =>
        shown.Mode.Map(p => SessionChoices.Modes.TryFirst(c => c.Value == p).Map(c => c.Label).GetValueOrDefault(p)).GetValueOrDefault("Claude Code's setting");

    static string Label(string model) => SessionChoices.Models.TryFirst(c => c.Value == model).Map(c => c.Label).GetValueOrDefault(model);
}
