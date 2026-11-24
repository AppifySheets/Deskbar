using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// THE INBOX A COMMENT IS DELIVERED INTO, one per Claude session, in <state dir>\claude-inbox\.
// Plan: docs/superpowers/plans/2026-11-24-comment-wakes-session.md (Tasks 3 and 4); CommentWake says when it is used.
//
// Two files per session:
//
//   <session id>.md   the message waiting for it. Deliver appends, a blank line between two, so comments posted in quick
//                     succession arrive together in one wake. The waiter claims it by renaming it away (only one rename
//                     can win), prints it and exits 2, which is what wakes the session.
//   <session id>.gen  the newest waiter's generation: the moment it registered, in milliseconds since 1970. Each Stop
//                     starts a waiter, and every waiter but the newest exits 0 when it sees a number that is not its own,
//                     so one session never has two waiters fighting over one inbox.
//
// The gen file IS the registration, read back from disk, never kept in memory only: Deskbar restarts with every release,
// and a waiter keeps running through that (it never calls Deskbar again after registering), so a restarted Deskbar must
// still know the waiter is there. A waiter lapses when Claude Code's timeout for the hook ends it, a month after it
// registered (WaiterTimeout, the hook entry's "timeout" in ClaudeHooks); CommentWake then takes another road.
//
// A session id becomes a file name, so only a plain name (letters, digits, dash, underscore, dot, not starting with a dot)
// is taken; anything that could climb out of the folder is refused.
public sealed class CommentInbox(string folder, Func<DateTimeOffset> now)
{
    /// <summary>How long a waiter lives: the Stop hook entry's "timeout" (measured accepted, 2026-11-24).</summary>
    public static readonly TimeSpan WaiterTimeout = TimeSpan.FromSeconds(2592000);

    static readonly Regex PlainName = new(@"^[A-Za-z0-9_-][A-Za-z0-9._-]{0,99}$", RegexOptions.Compiled);

    /// <summary>The folder's name under the state folder.</summary>
    public const string FolderName = "claude-inbox";

    /// <summary>
    /// A new waiter for the session: its generation written to the gen file (newer than any before), and the paths the
    /// waiter script watches.
    /// </summary>
    public Result<(string InboxPath, string GenPath, long Generation)> Register(string sessionId) =>
        Named(sessionId).Bind(name => Result.Try(() =>
        {
            Directory.CreateDirectory(folder);
            var gen = GenPath(name);
            var generation = Math.Max(now().ToUnixTimeMilliseconds(), GenerationOf(gen).GetValueOrDefault(0) + 1);
            File.WriteAllText(gen, generation.ToString());
            return (InboxPath(name), gen, generation);
        }, e => $"The comment inbox could not be set up: {e.Message}"));

    /// <summary>Whether the session has no waiter that can still be alive: never registered, forgotten, or a month old.</summary>
    public bool Lapsed(string sessionId) =>
        Named(sessionId).Map(name => GenerationOf(GenPath(name))
                .Map(generation => now() > DateTimeOffset.FromUnixTimeMilliseconds(generation) + WaiterTimeout)
                .GetValueOrDefault(true))
            .GetValueOrDefault(true);

    /// <summary>The message added to the session's inbox (after any already waiting there, a blank line between).</summary>
    public Result Deliver(string sessionId, string message) =>
        Named(sessionId).Bind(name => Result.Try(() =>
        {
            Directory.CreateDirectory(folder);
            var path = InboxPath(name);
            // Each message ends in a newline, so one more makes the blank line between two.
            File.AppendAllText(path, (File.Exists(path) && new FileInfo(path).Length > 0 ? "\n" : "") + message.Trim() + "\n");
        }, e => $"The comment could not be put in the session's inbox: {e.Message}"));

    /// <summary>A session that ended: its inbox and gen file go. A file that will not go (held open) is left.</summary>
    public void Forget(string sessionId) =>
        Named(sessionId).Tap(name =>
        {
            Result.Try(() => File.Delete(InboxPath(name)));
            Result.Try(() => File.Delete(GenPath(name)));
        });

    string InboxPath(string name) => Path.Combine(folder, name + ".md");

    string GenPath(string name) => Path.Combine(folder, name + ".gen");

    static Maybe<long> GenerationOf(string genPath) =>
        Result.Try(() => File.Exists(genPath) && long.TryParse(File.ReadAllText(genPath).Trim(), out var g) ? g : (long?)null)
            .Map(g => g.HasValue ? Maybe.From(g.Value) : Maybe<long>.None)
            .GetValueOrDefault(Maybe<long>.None);

    static Result<string> Named(string sessionId) =>
        PlainName.IsMatch(sessionId ?? "") ? sessionId! : Result.Failure<string>($"\"{sessionId}\" is not a session id");
}
