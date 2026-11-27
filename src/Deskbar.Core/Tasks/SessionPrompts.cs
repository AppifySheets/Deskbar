using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// WHAT DESKBAR KNOWS ABOUT THE DIALOGS SESSIONS WAIT ON (task b310a649; SessionPrompt words the question). Two things per
// session, both in memory only, since a dialog does not outlive the Claude Code process that drew it:
//
//   * the latest DETAIL a hook gave when a dialog opened (a PermissionRequest's tool and command, an Elicitation's server
//     and message). The Notification that makes the question comes six seconds later and names neither. A detail older
//     than DetailFor is not used: by then it belongs to some earlier dialog, and a wrong command on the bar is worse
//     than none;
//   * the questions PLACED on tasks from dialogs, each with where the session's transcript stood (TranscriptMark). The
//     transcript takes nothing while a dialog waits, and its next conversation line means the dialog was answered,
//     refused or escaped (TranscriptMark's probe), so Moved finds the answered ones on Deskbar's five-second tick, and
//     Take hands back a session's on its own events (Stop, the user's prompt, SessionEnd, an MCP answer).
//
// Not thread-safe: the pipe's handler and the tick both run on the UI thread in the app (RemoteControl.SettlePrompts).
public sealed class SessionPrompts(Func<DateTimeOffset> now)
{
    /// <summary>How long a noted detail may name a dialog: the Notification follows its dialog by six seconds.</summary>
    public static readonly TimeSpan DetailFor = TimeSpan.FromMinutes(2);

    /// <summary>A question placed on a task from a session's dialog, and the transcript it is answered in.</summary>
    public sealed record Placement(string Session, Guid Task, string Question, Maybe<TranscriptMark> Mark);

    readonly Dictionary<string, PromptDetail> details = new(StringComparer.Ordinal);
    readonly List<Placement> placed = [];

    /// <summary>The questions placed and not yet settled, oldest first.</summary>
    public IReadOnlyList<Placement> All => placed.ToList();

    /// <summary>Notes what a hook said about a dialog that just opened; a newer detail replaces the session's older one.</summary>
    public void Note(string session, PromptDetail detail) => details[session] = detail;

    /// <summary>The session's latest detail while it is fresh (DetailFor), or none.</summary>
    public Maybe<PromptDetail> Detail(string session) =>
        details.TryGetValue(session, out var detail) && now() - detail.At <= DetailFor ? detail : Maybe<PromptDetail>.None;

    /// <summary>
    /// Remembers a question placed on a task, with the transcript mark it is settled by (none: only the session's
    /// events settle it). A session has one dialog at a time, so its older placement goes.
    /// </summary>
    public void Placed(string session, Guid task, string question, Maybe<TranscriptMark> mark)
    {
        placed.RemoveAll(p => p.Session == session);
        placed.Add(new Placement(session, task, question, mark));
    }

    /// <summary>Removes and returns the placements whose transcript has moved on: their dialogs were answered.</summary>
    public IReadOnlyList<Placement> Moved() => Remove(p => p.Mark.Map(m => m.MovedOn()).GetValueOrDefault(false));

    /// <summary>Removes and returns the session's placements, and forgets its detail: the session went on or ended.</summary>
    public IReadOnlyList<Placement> Take(string session)
    {
        details.Remove(session);
        return Remove(p => p.Session == session);
    }

    IReadOnlyList<Placement> Remove(Func<Placement, bool> which)
    {
        var gone = placed.Where(which).ToList();
        placed.RemoveAll(p => gone.Contains(p));
        return gone;
    }
}
