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
//   * Keep: the session list says the session no longer waits on a permission prompt;
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
    /// So the terminal is always a parallel way to answer, and the card can stay as long as a person might take to look.
    /// </summary>
    public static readonly TimeSpan ScriptWait = TimeSpan.FromMinutes(10);

    /// <summary>How long Deskbar offers a question: the script's wait less a margin, so a click always lands while it reads.</summary>
    public static readonly TimeSpan HeldFor = ScriptWait - TimeSpan.FromSeconds(10);

    /// <summary>The script's wait in its own ticks: one look a second, each tick a second or a little more.</summary>
    public static int ScriptSeconds => (int)ScriptWait.TotalSeconds;

    /// <summary>
    /// The hook entry's "timeout", in seconds: the script's wait plus room for ticks that run slow, so Claude Code never
    /// kills the script while it prints. Were it killed, nothing would be lost: Claude Code drops a killed hook's output
    /// and keeps its own dialog (the spike's run B5), and the slot is deleted when the question expires.
    /// </summary>
    public static int HookTimeoutSeconds => ScriptSeconds + 120;

    /// <summary>Raised after a question is added or taken off, so the page and the bar can redraw.</summary>
    public event Action? Changed;

    /// <summary>The answer file's name inside a slot.</summary>
    public const string AnswerFile = "answer.txt";

    /// <summary>A slot's name as the script makes it: req-%RANDOM%-%RANDOM%, each number 0 to 32767.</summary>
    public static readonly Regex SlotName = new(@"^req-\d{1,5}-\d{1,5}$", RegexOptions.Compiled);

    /// <summary>One question on show: its slot, the request, when it arrived and until when it is offered.</summary>
    public sealed record Pending(string Slot, PermissionRequest Request, DateTimeOffset Arrived, DateTimeOffset Until);

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
    /// session is taken off first and its slot deleted (one card per session, the header's L5).
    /// </summary>
    public Result<string> Register(string slot, PermissionRequest request) =>
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
                held.Add((new Pending(slot!, request, at, at + HeldFor), code));
                Changed?.Invoke();
                return code;
            });

    /// <summary>
    /// The user's click: the answer written to the question's slot, atomically (a temporary file moved into place, so the
    /// script never reads half a line), and the question taken off the desk. Refused, with nothing written, for a
    /// question that is not on show: never asked, already answered, expired or ended.
    /// </summary>
    public Result Answer(string slot, PermissionDecision decision)
    {
        var index = held.FindIndex(h => h.Pending.Slot == slot);
        if (index < 0) return Result.Failure("This question has ended. Answer it in the terminal.");
        var (pending, code) = held[index];
        held.RemoveAt(index);
        Changed?.Invoke();
        if (now() >= pending.Until)
        {
            Delete(slot);
            return Result.Failure("This question has ended. Answer it in the terminal.");
        }
        return Result.Try(() =>
        {
            var path = SlotPath(slot);
            if (!Directory.Exists(path)) throw new IOException("the slot is gone");
            var temporary = Path.Combine(path, "answer.tmp");
            File.WriteAllText(temporary, $"{code} {PermissionAnswer.WordOf(decision)}\n");
            File.Move(temporary, Path.Combine(path, AnswerFile), overwrite: false);
        }, e => $"The answer could not be handed to Claude Code ({e.GetType().Name}). Answer it in the terminal.");
    }

    /// <summary>Questions held past <see cref="HeldFor"/> taken off, their slots deleted. True when any went.</summary>
    public bool Expire() => Drop(h => now() >= h.Until);

    /// <summary>A session's questions taken off, their slots deleted: it ended, or settled them elsewhere. True when any went.</summary>
    public bool Forget(string sessionId) => Drop(h => h.Request.SessionId == sessionId);

    /// <summary>
    /// After a read of the session list: the questions of sessions that no longer wait on a permission prompt taken off
    /// (answered in the terminal, or the session went). Only questions that arrived before <paramref name="seenBefore"/>
    /// count, because a question newer than the read may not show in it yet. True when any went.
    /// </summary>
    public bool Keep(Func<string, bool> stillAsking, DateTimeOffset seenBefore) =>
        Drop(h => h.Arrived < seenBefore && !stillAsking(h.Request.SessionId));

    /// <summary>Every question taken off and its slot deleted: Deskbar exits, or the setting went off.</summary>
    public bool ReleaseAll() => Drop(_ => true);

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
