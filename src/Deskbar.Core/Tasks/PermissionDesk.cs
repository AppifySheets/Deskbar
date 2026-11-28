using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// THE PERMISSION QUESTIONS WAITING ON THE USER, and the folder their answers go through (Allow / Deny from a task's
// page; plan notes/tasks-inbox-permission-plan.md in the PM's workspace, sections 1 to 3).
//
// WHY FILES AND NOT THE PIPE. A permission hook must wait until the user answers, and the control pipe cannot hold a
// caller: it serves one client at a time on one instance (ControlPipeServer), gives up on any request after 10 seconds,
// and runs the handler on the UI thread. So the hook script (claude-permission-hook.cmd, written by the app) makes a SLOT,
// a folder of its own under <state dir>\claude-permission named req-<n>-<n>, calls the running Deskbar ONCE to register
// (`claude-permission --slot <name>`, RemoteControl.Permission), and then polls the slot for an answer file. That is the
// comment waiter's pattern (CommentInbox, ClaudeSkill.WaitScript), and for the same reason: the script never holds the
// pipe, and a restarted Deskbar does not leave it hanging.
//
// WHAT IS HELD, AND WHERE. The request itself (the command, the file, the input) is held in MEMORY only, never written to
// disk by Deskbar and never traced. A Deskbar that restarts has forgotten its questions, so it sweeps every slot left
// over (Sweep): their scripts find their slot gone and print nothing, which leaves Claude Code's own terminal dialog.
//
// THE CODE. Register hands the script a fresh 128-bit code (32 hex digits, from the system's secure random generator)
// as the pipe's reply, and the script takes it straight from the call's output into a variable: it is never in a file
// before the click (the plan first had the script park the reply in the slot's reply.txt for a moment; the review's M1
// showed a file watcher could read it there, so a slot must now be EMPTY to register). An answer is "<code> <word>" in
// the slot's answer.txt, written once at the click, and the script acts on it only when the code is its own
// (PermissionAnswer.LineFor). Any process of the same user can write into the slot folder, another Claude session
// included, so the folder's ACL alone would let one session approve another's request; the code is what stops that. Its
// boundary, said plainly: the code lives in the hook script's environment, so a process of the same user that can read
// another process's memory or environment can learn it; Deskbar does not defend against that (ClaudeSkill.PermissionScript
// says where the code is and is not). A code answers once: Answer takes the request out of the desk, so a second click,
// or a click after the request expired or ended, finds nothing and writes nothing.
//
// ONE CARD PER SESSION (the review's L5). A session's newer question means its older one is settled: answered in the
// terminal, or given up. So a registration first takes every older question of the same session off and deletes its
// slot (Register; RemoteControl.Permission does it for a question it does not show, too), and a click can never reach a
// hook that no longer waits on it.
//
// EVERY WAY A QUESTION ENDS WITHOUT A CLICK deletes its slot, and a script whose slot is gone exits printing nothing:
//   * Expire: the question has been held for HeldFor (the script's own wait less a margin, so a card never offers a
//     click the script would no longer read);
//   * Forget: the session ended, or the next event shows the question was settled elsewhere (in the terminal);
//   * Register: a newer question of the same session arrived, so the older one was settled (one card per session);
//   * Keep: the session list says the session no longer waits on a permission prompt (a BACKGROUND session's question
//     only: `claude agents` lists nothing else, see TranscriptMark);
//   * Settle: the transcript of a session in a terminal or VS Code shows the conversation went on past the question
//     (TranscriptMark: the tool ran or was refused, measured 2026-11-27);
//   * Quiet: the hook script stopped touching its heartbeat file, killed on its timeout or gone with its session's
//     console (task ca73633d);
//   * ReleaseAll: Deskbar exits, or the setting is turned off;
//   * Sweep: Deskbar starts, with nothing in memory.
public sealed class PermissionDesk(string folder, Func<DateTimeOffset> now)
{
    /// <summary>The folder's name under the state folder.</summary>
    public const string FolderName = "claude-permission";

    /// <summary>
    /// How long the hook script waits for an answer before it gives up and prints nothing. Long, because the terminal is
    /// never blocked by the wait: probe Q1 (a spike's probe, 2026-11-26) saw a background session's own dialog
    /// already drawn 4 s into the hook's wait, and closed with "Allowed by PermissionRequest hook" once the hook answered.
    /// An interactive terminal session behaves the same (measured 2026-11-27, TranscriptMark): its dialog is drawn the
    /// moment the hook starts, and answering it there runs the tool at once while the hook sleeps on.
    /// So the terminal is always a parallel way to answer, and the card can stay as long as a person might take to look.
    ///
    /// A DAY, NOT TEN MINUTES (task ca73633d, "question not asked in the chat"). Petre runs many sessions and comes back
    /// to one later, and ten minutes was shorter than that: on 2026-11-28 a session asked an AskUserQuestion at 13:06, its
    /// card left the page at 13:16 when the hold ran out, and at 13:19 the task's page showed nothing of the dialog that
    /// still waited in the terminal. The session had run its own `task ask` ten seconds before the dialog, and a task
    /// waiting on a `task ask` keeps that question (RemoteControl.SessionPrompts, TakesDialogQuestion), so no dialog
    /// question had been placed that could be reworded to the terminal's lead once the card went.
    ///
    /// The length costs nothing while the question waits: the script looks at its slot once a second, and every way the
    /// question ends without a click (the session's next event, its transcript moving on, Deskbar starting with the slot
    /// swept) deletes the slot, which ends the script within a second. Claude Code puts no ceiling on a command hook's
    /// timeout (2.1.332 computes it as `timeout*1000` with no cap; only remote tool hooks are clamped), and a day in
    /// milliseconds is far below the 2^31 limit of a JavaScript timer.
    /// </summary>
    public static readonly TimeSpan ScriptWait = TimeSpan.FromHours(24);

    /// <summary>How long Deskbar offers a question: the script's wait less a margin, so a click always lands while it reads.</summary>
    public static readonly TimeSpan HeldFor = ScriptWait - TimeSpan.FromSeconds(10);

    /// <summary>The script's wait in its own ticks: one look a second, each tick a second or a little more.</summary>
    public static int ScriptSeconds => (int)ScriptWait.TotalSeconds;

    /// <summary>
    /// The hook entry's "timeout", in seconds: the script's wait plus room for ticks that run slow, so Claude Code never
    /// kills the script while it prints. Were it killed, nothing would be lost: Claude Code drops a killed hook's output
    /// and keeps its own dialog (the spike's run B5), and the slot is deleted when the question expires.
    /// The room grows with the wait (a tenth of it, plus two minutes): the script counts ticks, not the clock, and over a
    /// day of ticks that each run a little over a second the drift is minutes, not seconds.
    /// </summary>
    public static int HookTimeoutSeconds => ScriptSeconds + ScriptSeconds / 10 + 120;

    /// <summary>Raised after a question is added or taken off, so the page and the bar can redraw.</summary>
    public event Action? Changed;

    /// <summary>The answer file's name inside a slot.</summary>
    public const string AnswerFile = "answer.txt";

    /// <summary>A slot's name as the script makes it: req-%RANDOM%-%RANDOM%, each number 0 to 32767.</summary>
    public static readonly Regex SlotName = new(@"^req-\d{1,5}-\d{1,5}$", RegexOptions.Compiled);

    /// <summary>
    /// One question on show: its slot, the request, when it arrived and until when it is offered. <c>Listed</c>: the
    /// session is a background one, which `claude agents` lists, so the list says when it stops waiting (Keep).
    /// <c>Mark</c>: where a terminal or VS Code session's transcript stood when it asked, which says when it moved on
    /// (Settle); null for a background session, and for one whose transcript could not be found, whose question then goes
    /// only with its session's next event, a newer question or its expiry.
    /// </summary>
    public sealed record Pending(string Slot, PermissionRequest Request, DateTimeOffset Arrived, DateTimeOffset Until,
        bool Listed = true, TranscriptMark? Mark = null);

    readonly List<(Pending Pending, string Code)> held = [];

    /// <summary>Every question on show, the oldest first.</summary>
    public IReadOnlyList<Pending> All => held.Select(h => h.Pending).ToList();

    /// <summary>A session's questions, the oldest first.</summary>
    public IReadOnlyList<Pending> Of(string sessionId) => held.Where(h => h.Pending.Request.SessionId == sessionId).Select(h => h.Pending).ToList();

    /// <summary>Whether a session has a question on show.</summary>
    public bool Asks(string sessionId) => held.Any(h => h.Pending.Request.SessionId == sessionId);

    /// <summary>
    /// A task's questions: those of every session the board has open on it (TaskBoard.TaskOf), the oldest first. What the
    /// task's page draws as cards and what its line's mark reads.
    /// </summary>
    public IReadOnlyList<Pending> OfTask(TaskBoard board, Guid taskId) =>
        held.Select(h => h.Pending).Where(p => board.TaskOf(p.Request.SessionId).Map(t => t.Id == taskId).GetValueOrDefault(false)).ToList();

    /// <summary>
    /// A question registered under the script's slot, and the code the script must see in its answer. Refused for a
    /// slot name the script would not make, a slot that is not a folder in the desk's folder, one already registered, or
    /// one holding anything at all (a file planted before the question was asked). Every older question of the same
    /// session is taken off first and its slot deleted (one card per session, the header's L5). <paramref name="listed"/>
    /// and <paramref name="mark"/> say how the question is watched (Pending).
    /// </summary>
    public Result<string> Register(string slot, PermissionRequest request, bool listed = true, TranscriptMark? mark = null) =>
        Result.SuccessIf(SlotName.IsMatch(slot ?? ""), $"\"{slot}\" is not a permission slot")
            .Ensure(() => held.All(h => h.Pending.Slot != slot), $"The slot {slot} is already registered")
            .Bind(() => Result.Try(() => Directory.Exists(SlotPath(slot!)) && !Directory.EnumerateFileSystemEntries(SlotPath(slot!)).Any(),
                e => $"The slot could not be read: {e.Message}"))
            .Ensure(usable => usable, $"The slot {slot} is not a fresh, empty folder of the permission box")
            .Map(_ =>
            {
                Drop(h => h.Request.SessionId == request.SessionId);
                var code = RandomNumberGenerator.GetHexString(32, lowercase: true);
                var at = now();
                held.Add((new Pending(slot!, request, at, at + HeldFor, listed, listed ? null : mark), code));
                Changed?.Invoke();
                return code;
            });

    /// <summary>
    /// The user's click: the answer written to the question's slot, atomically (a temporary file moved into place, so the
    /// script never reads half a line), and the question taken off the desk. Refused, with nothing written, for a
    /// question that is not on show: never asked, already answered, expired or ended.
    /// </summary>
    public Result Answer(string slot, PermissionDecision decision) =>
        // A dialog's slot takes its answers (AnswerDialog, AnswerPlan) or Pass; Allow and Deny are not answers to a dialog,
        // and its script would print nothing for them anyway (PermissionAnswer.LineFor).
        held.Any(h => h.Pending.Slot == slot && h.Pending.Request.IsDialog) && decision != PermissionDecision.Pass
            ? Result.Failure("This question takes its answers, not Allow or Deny.")
            : Handed(slot, (path, code) => Write(path, AnswerFile, $"{code} {PermissionAnswer.WordOf(decision)}\n"));

    /// <summary>
    /// The user's answers to a multiple-choice dialog (question text to answer, DialogAsk): the hook's line kept IN
    /// MEMORY for the script to fetch (<see cref="Fetch"/>), then "&lt;code&gt; dialog" written to the slot's answer file,
    /// and the question taken off the desk. Refused, with nothing written and the question left on show, for answers that
    /// do not cover every question; refused like <see cref="Answer"/> for a question no longer on show.
    ///
    /// WHY THE LINE IS NOT A FILE (the branch's review, finding 1): the code authenticates the answer file's word, not a
    /// second file beside it. A dialog.json in the slot could be swapped by any process of the user that watches the
    /// folder, in the tick between Deskbar writing it and the script reading it, without knowing the code, and the hook
    /// would then print that process's JSON: a decision with permission rules of its own, say. So the script asks Deskbar
    /// for the line over the control pipe, whose server it checks is Deskbar's own executable (PipeTrust), showing its
    /// code; what it prints can only be what Deskbar holds.
    /// </summary>
    public Result AnswerDialog(string slot, IReadOnlyDictionary<string, string> answers) =>
        HandedLine(slot, request => DialogAsk.Of(request)
            .ToResult("This question is not a dialog Deskbar can answer. Answer it in the terminal.")
            .Bind(dialog => dialog.AnswerLine(answers)));

    /// <summary>
    /// The user's click on a plan card (task 8dbbaa0b, PlanAsk): approve, approve and accept edits, or keep planning with
    /// a note. The hook's line travels as a dialog's answers do (<see cref="AnswerDialog"/>): kept in memory for the
    /// script to fetch, "&lt;code&gt; dialog" in the slot. Refused, with nothing written and the plan left on show, for
    /// keep planning without a note; refused like <see cref="Answer"/> for a plan no longer on show.
    /// </summary>
    public Result AnswerPlan(string slot, PlanDecision decision, string note = "") =>
        HandedLine(slot, request => PlanAsk.Of(request)
            .ToResult("This question is not a plan Deskbar can answer. Answer it in the terminal.")
            .Bind(plan => plan.AnswerLine(decision, note)));

    /// <summary>
    /// The user's click on a "Yes, and ..." built from one of Claude Code's permission_suggestions (task bd55bd54,
    /// PermissionChoices): the line that allows the call and hands that entry back (PermissionAnswer.SuggestionLine) kept
    /// in memory for the script to fetch, as a dialog's is, and "&lt;code&gt; fetch" written to the slot. Refused, with
    /// nothing written and the question left on show, for a dialog's slot and for an index the request does not have;
    /// refused like <see cref="Answer"/> for a question no longer on show.
    /// </summary>
    public Result AnswerSuggestion(string slot, int index) =>
        HandedLine(slot, request =>
                request.IsDialog
                    ? Result.Failure<string>("This question takes its answers, not Allow or Deny.")
                    : index >= 0 && index < request.Suggestions.Count
                        // Re-serialising the entry can throw on text JSON allows but .NET will not write (a lone
                        // surrogate escape, say), which must not reach the click handler as an exception.
                        ? Result.Try(() => PermissionAnswer.SuggestionLine(request.Suggestions[index]),
                            _ => "This choice could not be handed to Claude Code. Answer it in the terminal.")
                        : Result.Failure<string>("This choice is not one Claude Code offered. Answer it in the terminal."),
            PermissionAnswer.FetchWord);

    // An answer with a line built from data (a dialog's, or a picked suggestion's): the line `lineOf` makes for the slot's
    // request is kept for the script's fetch, then `word` goes into the slot and the question off the desk. A refused line
    // leaves everything as it was.
    Result HandedLine(string slot, Func<PermissionRequest, Result<string>> lineOf, string word = PermissionAnswer.DialogWord)
    {
        var found = held.FirstOrDefault(h => h.Pending.Slot == slot).Pending;
        if (found is null) return Result.Failure(Ended);
        return lineOf(found.Request)
            .Bind(line => Handed(slot, (path, code) =>
            {
                handedLines[slot] = (code, line, now() + FetchWindow);
                Write(path, AnswerFile, $"{code} {word}\n");
            }))
            // Noted for RemoteControl.CardsGone (task 28fd8550): the card leaves the desk the moment Send is pressed, a few
            // seconds before the dialog's PostToolUse settles the question, and in that gap the question must not be
            // reworded to "Answer in the session's terminal" as if the card had been handed back. A dialog's only: a
            // tool permission answered with Allow notes nothing either.
            .TapIf(word == PermissionAnswer.DialogWord, () => answeredOnCard[found.Request.SessionId] = now());
    }

    // When each session's dialog was last answered on its card (AnswerDialog), for AnsweredOnCardSince.
    readonly Dictionary<string, DateTimeOffset> answeredOnCard = new(StringComparer.Ordinal);

    /// <summary>Whether the session's dialog was answered on its card at or after <paramref name="since"/> (AnswerDialog).</summary>
    public bool AnsweredOnCardSince(string sessionId, DateTimeOffset since) =>
        answeredOnCard.TryGetValue(sessionId, out var at) && at >= since;

    /// <summary>How long a fetched answer line waits to be fetched: the script reads the answer file within a tick or two.</summary>
    public static readonly TimeSpan FetchWindow = TimeSpan.FromMinutes(1);

    // The answer lines waiting for their scripts, by slot, with the code a fetch must show: a dialog's or a plan's
    // (AnswerDialog, AnswerPlan) and a picked suggestion's (AnswerSuggestion, task bd55bd54). Fetch hands them over.
    readonly Dictionary<string, (string Code, string Line, DateTimeOffset Until)> handedLines = [];

    /// <summary>
    /// An answer line (a dialog's, a plan's or a picked suggestion's), handed ONCE to the script of <paramref name="slot"/> that shows its code
    /// (`claude-permission --fetch`, RemoteControl.Permission). Refused for a wrong code, a slot with no line waiting, and
    /// a line past <see cref="FetchWindow"/>; a line handed or expired is gone.
    /// </summary>
    public Result<string> Fetch(string slot, string code)
    {
        handedLines.Where(h => now() >= h.Value.Until).Select(h => h.Key).ToList().ForEach(k => handedLines.Remove(k));
        if (!handedLines.TryGetValue(slot, out var handed) || !PermissionAnswer.Code.IsMatch(code) || handed.Code != code)
            return Result.Failure<string>("No answer waits for this slot.");
        handedLines.Remove(slot);
        return handed.Line;
    }

    const string Ended = "This question has ended. Answer it in the terminal.";

    // Takes the question off the desk and hands its answer to the script by `write` (the slot's folder and the code), or
    // says why it cannot: not on show, held past its time, or the slot gone.
    Result Handed(string slot, Action<string, string> write)
    {
        var index = held.FindIndex(h => h.Pending.Slot == slot);
        if (index < 0) return Result.Failure(Ended);
        var (pending, code) = held[index];
        held.RemoveAt(index);
        Changed?.Invoke();
        if (now() >= pending.Until)
        {
            Delete(slot);
            return Result.Failure(Ended);
        }
        return Result.Try(() =>
        {
            var path = SlotPath(slot);
            if (!Directory.Exists(path)) throw new IOException("the slot is gone");
            write(path, code);
        }, e => $"The answer could not be handed to Claude Code ({e.GetType().Name}). Answer it in the terminal.");
    }

    // A file of the slot written atomically: a temporary file moved into place, so the script never reads half of it.
    static void Write(string path, string name, string text)
    {
        var temporary = Path.Combine(path, name + ".tmp");
        File.WriteAllText(temporary, text);
        File.Move(temporary, Path.Combine(path, name), overwrite: false);
    }

    /// <summary>Questions held past <see cref="HeldFor"/> taken off, their slots deleted. True when any went.</summary>
    public bool Expire() => Drop(h => now() >= h.Until);

    // THE SCRIPT'S HEARTBEAT (task ca73633d, the review of the day-long hold). A card is only worth a click while its hook
    // script still reads the slot, and a day is long enough for the script to stop without a word reaching Deskbar:
    //   * a session started before the hold went from ten minutes to a day keeps the hook timeout it read at its start
    //     (Claude Code takes the hooks once per session), so Claude Code kills the script after twelve minutes, while the
    //     rewritten script and HeldFor would keep the card for a day and a click would answer nothing;
    //   * a session whose console closed or whose process was killed sends no SessionEnd, and its transcript never moves,
    //     so neither Forget nor Settle would take its card off.
    // So the script touches AliveFile in its slot every BeatTicks ticks (ClaudeSkill.PermissionScript), and Quiet takes off
    // a question whose script has not touched it for Silent. A script that dies with its session's console stops beating;
    // one that Claude Code kills on its timeout does too.
    //
    // SLEEP IS NOT SILENCE. While the machine sleeps the scripts stop ticking and Deskbar stops looking, and on waking the
    // last beat is as old as the sleep. A look that comes more than half of Silent after the one before it (the keeper
    // looks every five seconds) therefore starts the count again from that look, which gives every script time to beat.

    /// <summary>The empty file the hook script touches in its slot while it waits (its heartbeat, see Quiet).</summary>
    public const string AliveFile = "alive";

    /// <summary>How many of its ticks (about a second each) the script waits between beats.</summary>
    public const int BeatTicks = 15;

    /// <summary>How long a script may go without a beat before its question is taken off: five beats' room.</summary>
    public static readonly TimeSpan Silent = TimeSpan.FromSeconds(BeatTicks * 5);

    // The keeper's last look, and the moment silence is counted from after a gap between looks (the header above).
    DateTimeOffset? lastQuietLook;
    DateTimeOffset quietFrom = DateTimeOffset.MinValue;

    /// <summary>
    /// Questions whose hook script has stopped beating for <see cref="Silent"/> taken off, their slots deleted: the script
    /// is gone, so a click would answer nothing (the header above). Silence counts from the question's arrival when no
    /// beat has come yet. True when any went.
    /// </summary>
    public bool Quiet()
    {
        var at = now();
        if (lastQuietLook is { } last && at - last > Silent / 2) quietFrom = at;
        lastQuietLook = at;
        return Drop(h => at - new[] { h.Arrived, LastBeat(h.Slot), quietFrom }.Max() >= Silent);
    }

    // When the slot's heartbeat file was last written; long ago when it is missing (the API's answer for no file is the
    // year 1601) or cannot be read.
    DateTimeOffset LastBeat(string slot) =>
        Result.Try(() => new DateTimeOffset(File.GetLastWriteTimeUtc(Path.Combine(SlotPath(slot), AliveFile)), TimeSpan.Zero))
            .GetValueOrDefault(DateTimeOffset.MinValue);

    /// <summary>A session's questions taken off, their slots deleted: it ended, or settled them elsewhere. True when any went.</summary>
    public bool Forget(string sessionId) => Drop(h => h.Request.SessionId == sessionId);

    /// <summary>
    /// After a read of the session list: the questions of sessions that no longer wait on a permission prompt taken off
    /// (answered in the terminal, or the session went). Only questions that arrived before <paramref name="seenBefore"/>
    /// count, because a question newer than the read may not show in it yet. True when any went.
    /// </summary>
    public bool Keep(Func<string, bool> stillAsking, DateTimeOffset seenBefore) =>
        Drop(h => h.Listed && h.Arrived < seenBefore && !stillAsking(h.Request.SessionId));

    /// <summary>
    /// The questions of terminal and VS Code sessions whose transcript went on past the question taken off: the user
    /// answered in the session's own dialog (TranscriptMark). True when any went.
    /// </summary>
    public bool Settle() => Drop(h => h.Mark is { } mark && mark.MovedOn());

    /// <summary>Every question taken off and its slot deleted: Deskbar exits, or the setting went off.</summary>
    public bool ReleaseAll()
    {
        // Answer lines not yet fetched go too: their scripts find no slot, or no line, and print nothing.
        handedLines.Clear();
        return Drop(_ => true);
    }

    /// <summary>
    /// On start: every slot folder left in the box deleted (a Deskbar that just started holds no question, so no slot can
    /// be answered). Only folders named as slots are touched.
    /// </summary>
    public void Sweep()
    {
        if (!Directory.Exists(folder)) return;
        foreach (var slot in Result.Try(() => Directory.GetDirectories(folder)).GetValueOrDefault([])
                     .Select(Path.GetFileName).Where(n => n is not null && SlotName.IsMatch(n)))
            Delete(slot!);
    }

    bool Drop(Func<Pending, bool> which)
    {
        var going = held.Where(h => which(h.Pending)).ToList();
        going.ForEach(h =>
        {
            held.Remove(h);
            Delete(h.Pending.Slot);
        });
        if (going.Count > 0) Changed?.Invoke();
        return going.Count > 0;
    }

    // A slot that will not go (held open by the script for a moment) is left; the script's own deadline ends it.
    void Delete(string slot) => Result.Try(() => Directory.Delete(SlotPath(slot), recursive: true));

    string SlotPath(string slot) => Path.Combine(folder, slot);
}
