using System.Text.Json;
using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// A MERGE SETTLES A "MERGE THIS" QUESTION BY ITSELF (task b7f987b4, 2026-11-25). Petre: "whenever you're not waiting on
// me, like now, it should take away the task from my eyes." Seen on task 7f06274f: its session asked "Merge PR 504", he
// merged it, and the session went on rebasing and rerunning tests for minutes before it ran `task answered`. All that
// time the task sat at the top of the bar as waiting on him, though nothing was.
//
// Most questions a session asks are "merge this pull request", and a session writes the pull request as a link
// ("[Deskbar #504](https://github.com/AppifySheets/Deskbar/pull/504)", the briefing asks for it). So Deskbar reads the
// links out of a waiting task's question, asks GitHub where each pull request stands (PullRequestWatch, through `gh`),
// and settles the question itself once nothing it links is still open. The session's own `task answered` later finds
// the task in progress already and changes nothing, so the two never fight.
//
// WHEN A QUESTION IS SETTLED (Settles): no linked pull request is open any more, AND at least one of them was merged or
// closed at or after the moment the task began waiting (TaskItem.WaitingSince). The second half is what keeps a question
// that only MENTIONS a finished pull request ("now that [#500] is in, do you want A or B?") from vanishing the moment it
// is asked: nothing linked closed after the ask, so it waits for Petre as before. Closed without a merge counts too: a
// pull request someone closed is no longer something Petre can merge, so the question is moot either way, and the thread
// line says which it was. A pull request GitHub cannot tell about (gh missing, offline, no access) blocks the settling:
// unknown is never read as closed.
//
// Only GitHub pull request links count, the form `https://github.com/<owner>/<repo>/pull/<n>` (anything after the
// number, "/files" or "#issuecomment-1", is ignored). A bare "#504" names no repository and is left alone.
public static class AskedPullRequests
{
    // owner and repo as GitHub allows them (letters, digits, '-', '_' and '.'), then the number. A trailing path or
    // fragment is not part of the match, so ".../pull/504/files" and ".../pull/504" are one pull request.
    static readonly Regex Link = new(@"https?://github\.com/(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+)/pull/(?<number>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The GitHub pull requests a text links to, each once, in the order they first appear. Owner and repo are compared
    /// without case (GitHub's are case-insensitive), so two spellings of one link are one pull request.
    /// </summary>
    public static IReadOnlyList<PullRequestLink> In(string? text) =>
        Link.Matches(text ?? "")
            .Select(m => int.TryParse(m.Groups["number"].Value, out var number)
                ? new PullRequestLink(m.Groups["owner"].Value, m.Groups["repo"].Value, number)
                : null)
            .OfType<PullRequestLink>()
            .DistinctBy(link => link.Key)
            .ToArray();

    /// <summary>
    /// The open tasks waiting on Petre whose question links at least one pull request, with those links: what the watch
    /// asks GitHub about. A snoozed task counts too, since it still waits and settling it is still right.
    /// </summary>
    public static IReadOnlyList<(TaskItem Task, IReadOnlyList<PullRequestLink> Links)> Watched(TaskBoard board) =>
        board.Tasks
            .Where(t => t.IsOpen && t.Status == TaskStatus.WaitingOnYou && t.Question.HasValue)
            .Select(t => (Task: t, Links: In(t.Question.Value)))
            .Where(x => x.Links.Count > 0)
            .ToArray();

    /// <summary>
    /// Whether the question is settled by what GitHub says (see the header): every link known, none open, and at least
    /// one merged or closed at or after <paramref name="waitingSince"/>. <paramref name="facts"/> holds what was learned,
    /// keyed by <see cref="PullRequestLink.Key"/>; a link missing from it is unknown, and unknown settles nothing.
    /// </summary>
    public static bool Settles(IReadOnlyList<PullRequestLink> links, IReadOnlyDictionary<string, PullRequestFacts> facts, DateTimeOffset waitingSince) =>
        links.Count > 0
        && links.All(link => facts.TryGetValue(link.Key, out var f) && f.State != PullRequestState.Open)
        && links.Any(link => facts[link.Key].ClosedAt.Map(at => at >= waitingSince).GetValueOrDefault(false));

    /// <summary>
    /// The questions to settle now, from the board and what GitHub said: for each, the task, the question as it was read
    /// (so a new ask that lands meanwhile is not the one settled, see TaskBoard.SettleQuestion) and the thread line that
    /// says why.
    /// </summary>
    public static IReadOnlyList<Settlement> Settlements(TaskBoard board, IReadOnlyDictionary<string, PullRequestFacts> facts, DateTimeOffset now) =>
        Watched(board)
            .Where(x => Settles(x.Links, facts, x.Task.WaitingSince.GetValueOrDefault(x.Task.Raised)))
            .Select(x => new Settlement(x.Task.Id, x.Task.Question.Value, Note(x.Links, facts)))
            .ToArray();

    /// <summary>
    /// Deskbar's line in the thread when it settles a question: "Question settled: [Deskbar #504](...) was merged."
    /// Several links are joined with commas and "and", each with what happened to it. No author or time in the text: the
    /// thread's header shows both (task aa42794f, "too much bla bla").
    /// </summary>
    public static string Note(IReadOnlyList<PullRequestLink> links, IReadOnlyDictionary<string, PullRequestFacts> facts)
    {
        var parts = links.Select(link => $"[{link.Label}]({link.Url}) was {(facts[link.Key].State == PullRequestState.Merged ? "merged" : "closed without merging")}").ToArray();
        var joined = parts.Length == 1 ? parts[0] : string.Join(", ", parts[..^1]) + " and " + parts[^1];
        return $"Question settled: {joined}.";
    }

    /// <summary>
    /// What `gh pr view &lt;url&gt; --json state,closedAt` printed, read: the state and when it closed. Measured on
    /// 2026-11-25: <c>{"closedAt":"2026-11-25T07:06:18Z","state":"MERGED"}</c>, and an open one has <c>"closedAt":null</c>.
    /// Anything else is a failure, never a guess.
    /// </summary>
    public static Result<PullRequestFacts> Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String)
                return Result.Failure<PullRequestFacts>($"gh gave no state: {json}");
            var parsed = state.GetString()!.ToUpperInvariant() switch
            {
                "OPEN" => Maybe.From(PullRequestState.Open),
                "MERGED" => Maybe.From(PullRequestState.Merged),
                "CLOSED" => Maybe.From(PullRequestState.Closed),
                _ => Maybe<PullRequestState>.None,
            };
            var closedAt = root.TryGetProperty("closedAt", out var closed) && closed.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(closed.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
                ? Maybe.From(at)
                : Maybe<DateTimeOffset>.None;
            return parsed.HasValue
                ? new PullRequestFacts(parsed.Value, closedAt)
                : Result.Failure<PullRequestFacts>($"gh gave an unknown state: {state.GetString()}");
        }
        catch (JsonException e)
        {
            return Result.Failure<PullRequestFacts>($"gh's answer is not JSON: {e.Message}");
        }
    }
}

/// <summary>
/// A GitHub pull request a question links to. <see cref="Key"/> is the lower-cased "owner/repo#n" that two spellings of
/// one link share; <see cref="Label"/> is how the thread line names it, "Deskbar #504", as a session writes it.
/// </summary>
public sealed record PullRequestLink(string Owner, string Repo, int Number)
{
    public string Key => $"{Owner}/{Repo}#{Number}".ToLowerInvariant();

    public string Url => $"https://github.com/{Owner}/{Repo}/pull/{Number}";

    public string Label => $"{Repo} #{Number}";
}

/// <summary>Where a pull request stands on GitHub.</summary>
public enum PullRequestState { Open, Merged, Closed }

/// <summary>What GitHub says about one pull request: its state, and when it was merged or closed (none while open).</summary>
public sealed record PullRequestFacts(PullRequestState State, Maybe<DateTimeOffset> ClosedAt);

/// <summary>A question to settle: the task, the question as it was read, and Deskbar's line for the thread.</summary>
public sealed record Settlement(Guid TaskId, string Question, string Note);
