using System.Text.Json;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// DESKBAR WATCHES THE PULL REQUESTS, NOT EVERY SESSION (Deskbar task c8e6967d, 2026-11-27; design:
// docs/superpowers/specs/2026-11-27-repo-watcher-design.md, plan: docs/superpowers/plans/2026-11-27-repo-watch.md).
// Petre: "let one agent per repo take on the role of the watcher. it should monitor pr merges and deploys and report to
// the manager", then, offered a model for it, "do what you like". Deskbar itself is the watcher: no model, no tokens.
//
// Before this, every session with an open pull request ran its own Monitor loop against GitHub and, after the merge,
// waited on the release in another loop. Five features in one repository were five identical loops, and a session that
// forgot to re-arm its loop silently stopped watching. Now Deskbar asks GitHub once a minute per repository that has a
// task in flight, applies the rules in this file, and nudges the task's session through CommentWaker's delivery queue.
//
// This file holds the PURE parts only: the types, the gh answer parsers (this section), and the rules (Poll, Follow).
// State lives in TaskService, the polling schedule and the gh calls in RepoWatchPoll, so every rule here can be tested
// with plain values and no clock, scheduler or gh.
//
// THE GH ANSWERS. One GraphQL query per repository asks, for each in-flight pull request, its state, mergeable flag,
// base branch, that base branch's head commit and its merge commit. The merge commit's workflow runs come from
// `gh run list --commit <sha> --json name,status,conclusion,url`. Both parsers fail loudly (Result.Failure) on anything
// unexpected, as RunningChanges.ParseMerge does: a look that cannot be read is a failed look, never a guess.
public static class RepoWatch
{
    // One alias per pull request ("p12") so a single request answers them all. The alias is the pull request's number,
    // which is unique within a repository, and ParseRepository finds each link's answer by it.
    static string Alias(PullRequestLink link) => $"p{link.Number}";

    /// <summary>
    /// The GraphQL text asking about every pull request in <paramref name="links"/>, all of one repository (the first
    /// link's): <c>p12: pullRequest(number: 12) { state mergeable baseRefName baseRef { target { oid } } mergeCommit { oid } }</c>
    /// inside <c>repository(owner: "...", name: "...")</c>. Owner and repo are GitHub names (letters, digits, '-', '_',
    /// '.'), which AskedPullRequests' link pattern already guarantees, so nothing here needs escaping.
    /// </summary>
    public static string Query(IReadOnlyList<PullRequestLink> links) =>
        $"{{ repository(owner: \"{links[0].Owner}\", name: \"{links[0].Repo}\") {{ "
        + string.Join(" ", links.Select(l =>
            $"{Alias(l)}: pullRequest(number: {l.Number}) {{ state mergeable baseRefName baseRef {{ target {{ oid }} }} mergeCommit {{ oid }} }}"))
        + " } }";

    /// <summary>
    /// The facts of every link in <paramref name="links"/> from the GraphQL answer <paramref name="json"/>. A link whose
    /// alias is missing (or null: GitHub answers null for a pull request it cannot show) fails the whole look, so a
    /// pull request is never dropped silently and then treated as unchanged.
    /// </summary>
    public static Result<IReadOnlyList<PullFacts>> ParseRepository(IReadOnlyList<PullRequestLink> links, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("repository", out var repository)
                || repository.ValueKind != JsonValueKind.Object)
                return Result.Failure<IReadOnlyList<PullFacts>>($"gh gave no repository: {json}");
            return links
                .Select(link => repository.TryGetProperty(Alias(link), out var pull) && pull.ValueKind == JsonValueKind.Object
                    ? ParsePull(link, pull)
                    : Result.Failure<PullFacts>($"gh's answer has nothing for {link.Label}: {json}"))
                .Combine()
                .Map(facts => (IReadOnlyList<PullFacts>)facts.ToArray());
        }
        catch (JsonException e)
        {
            return Result.Failure<IReadOnlyList<PullFacts>>($"gh's answer is not JSON: {e.Message}");
        }
    }

    // A string property, or "" when absent or not a string (GraphQL nulls what it cannot show).
    static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    // baseRef.target.oid, the head commit of the base branch; "" when the branch is gone (a deleted stacked base).
    static string BaseHead(JsonElement pull) =>
        pull.TryGetProperty("baseRef", out var baseRef) && baseRef.ValueKind == JsonValueKind.Object
        && baseRef.TryGetProperty("target", out var target) && target.ValueKind == JsonValueKind.Object
            ? Text(target, "oid")
            : "";

    static Result<PullFacts> ParsePull(PullRequestLink link, JsonElement pull) =>
        Text(pull, "state").ToUpperInvariant() switch
        {
            "OPEN" => Maybe.From(PullRequestState.Open),
            "MERGED" => Maybe.From(PullRequestState.Merged),
            "CLOSED" => Maybe.From(PullRequestState.Closed),
            _ => Maybe<PullRequestState>.None,
        } is { HasValue: true } state
            ? new PullFacts(link, state.Value,
                // Anything but the two known answers is Unknown: GitHub computes the flag lazily and says UNKNOWN meanwhile.
                Text(pull, "mergeable").ToUpperInvariant() switch
                {
                    "MERGEABLE" => Mergeable.Mergeable,
                    "CONFLICTING" => Mergeable.Conflicting,
                    _ => Mergeable.Unknown,
                },
                Text(pull, "baseRefName"), BaseHead(pull),
                pull.TryGetProperty("mergeCommit", out var commit) && commit.ValueKind == JsonValueKind.Object
                && Text(commit, "oid") is { Length: > 0 } sha
                    ? Maybe.From(sha)
                    : Maybe<string>.None)
            : Result.Failure<PullFacts>($"gh gave an unknown state for {link.Label}: {Text(pull, "state")}");

    /// <summary>
    /// What `gh run list --json name,status,conclusion,url` printed, read: each run's name, link, whether it finished
    /// (status "completed") and its conclusion lower case ("" while it runs, since GitHub gives null).
    /// </summary>
    public static Result<IReadOnlyList<RunFacts>> ParseRuns(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? Result.Success<IReadOnlyList<RunFacts>>(document.RootElement.EnumerateArray()
                    .Select(run => new RunFacts(Text(run, "name"), Text(run, "url"),
                        Text(run, "status").Equals("completed", StringComparison.OrdinalIgnoreCase),
                        Text(run, "conclusion").ToLowerInvariant()))
                    .ToArray())
                : Result.Failure<IReadOnlyList<RunFacts>>($"gh's run list is not an array: {json}");
        }
        catch (JsonException e)
        {
            return Result.Failure<IReadOnlyList<RunFacts>>($"gh's answer is not JSON: {e.Message}");
        }
    }
}

/// <summary>GitHub's mergeable flag: Unknown while it is still computing (neither a conflict nor a clear).</summary>
public enum Mergeable { Unknown, Mergeable, Conflicting }

/// <summary>
/// What one poll learned about one pull request: its state, whether it merges cleanly, its base branch and that branch's
/// head commit, and (once merged) its merge commit.
/// </summary>
public sealed record PullFacts(PullRequestLink Link, PullRequestState State, Mergeable Mergeable, string BaseRef, string BaseHead,
    Maybe<string> MergeCommit);

/// <summary>One workflow run on a merge commit; <see cref="Conclusion"/> is lower case and "" while it runs.</summary>
public sealed record RunFacts(string Name, string Url, bool Finished, string Conclusion);
