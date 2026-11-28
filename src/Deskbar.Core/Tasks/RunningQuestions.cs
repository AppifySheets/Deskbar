using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// THE RUNNING BUILD SETTLES AN "UPDATE DESKBAR" QUESTION BY ITSELF (task fa22a198, 2026-11-28). The user, seeing three
// tasks still ask "Update Deskbar to v2.6.77" while his Deskbar already ran 2.6.77: "why didn't tasks show as done?", and
// of their sessions, "wouldn't they know by themselves?".
//
// WHAT HAPPENED. Three Deskbar tasks merged, v2.6.77 was released at 01:41 his time, and at 01:45 each session asked him to
// update and went idle. He installed 2.6.77 at 09:23 (the PC slept in between). Within a minute the running-change watch
// (RunningChangeWatch) saw that the running commit contains the three pull requests and filled their ticks, but that
// answer only redrew the views (TaskService.Arrived): it settled no question and told no session. The deploy wake
// (DeployWaits, task 6a4104c0) skips Deskbar's own pull requests on purpose, since the running build decides those and a
// report never does (DeployNotices' selfChecked). So for the one repository where Deskbar knows the answer itself, nothing
// acted on it, and the questions stood until the manager commented by hand.
//
// THE RULE, the way a merge settles a merge question (AskedPullRequests, task b7f987b4) and wakes the session (task
// ede8dbe8). A waiting task's question is settled once the running build carries the task's change, when ALL of these hold:
//   * the task is open, Waiting on you, with a question and no answer buttons (buttons ask for a choice, not an update);
//   * the question asks for the update: it names a version ("v2.6.77", or a releases/tag/v2.6.77 link) and the highest
//     version it names is one this build has reached, or it is a deploy hand-over (`task ask --deploy`) that names none.
//     All three asks seen named v2.6.77, and one was asked without --deploy, so the flag alone would have missed it. A
//     plain instruction that names no version ("Look at the new chip on the bar") is never settled: any restart (a
//     reboot, a crash, a later update) would otherwise read as the install it asked for (review finding). A version
//     named that this build has not reached ("update to v2.6.78" while 2.6.77 runs) waits too. A deploy hand-over
//     must also ask nothing else (no "?" outside its links);
//   * the task records at least one pull request of this build's repository, and its change runs as a whole
//     (RunningChanges.Of says Running: every recorded pull request merged and in this build, or, for another repository,
//     reported deployed). A task that also waits on another app's redeploy is not settled by Deskbar's own version;
//   * every pull request the question links is one the task records (its own work, covered by the line above). A question
//     linking anything else ("merge [#712] next") is about that link and is left to its own watch;
//   * the task began waiting BEFORE this Deskbar started (runningSince). The update is what restarted it, so a question
//     asked before the start and a change running after it means the update landed after the ask. A question asked
//     while this build already ran the change is about something else (a session should not ask the user to install what
//     he runs), so it is left alone.
//
// WHAT FOLLOWS (TaskService.SettleRunning): the question is settled with a line in the thread naming the version and the
// pull requests, and the session is woken through the watch's nudges to report the deploy and finish (deployed, done). Its
// own `task answered` then finds the task in progress and changes nothing.
//
// WHEN IT IS ASKED: each time GitHub's answers change (TaskService.Arrived), which at a start is within a minute or two.
// A task that also records another app's pull request is settled only by an answer that lands after that app's deploy
// report; until then that pull request keeps it on DeployWaits' schedule (it is not this build's), so its session is
// woken to check either way.
//
// A PURE FUNCTION: the board, the running build, GitHub's answers and when this Deskbar started come in.
public static class RunningQuestions
{
    // A version as a session writes it: "v2.6.77", "2.6.77" or the tag in a releases/tag/v2.6.77 link. Not the middle of
    // a longer dotted number (an address, a four-part version), and a sentence's closing full stop is not part of it.
    static readonly Regex VersionToken = new(@"(?<![\w.])v?(?<v>\d+\.\d+\.\d+)(?!\.?\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The highest version a text names (see VersionToken), or none.</summary>
    public static Maybe<Version> HighestVersion(string text) =>
        VersionToken.Matches(text)
            .Select(m => Version.TryParse(m.Groups["v"].Value, out var version) ? version : null)
            .OfType<Version>()
            .OrderByDescending(v => v)
            .FirstOrDefault() is { } highest ? Maybe.From(highest) : Maybe<Version>.None;

    /// <summary>
    /// Whether the question asks for an update this build carries out (see the rule): the highest version it names is
    /// one this build has reached, or, naming none, it is a deploy hand-over that asks nothing else.
    /// </summary>
    public static bool AsksForThisUpdate(TaskItem task, RunningBuild build) =>
        HighestVersion(task.Question.Value) is { HasValue: true } named
            ? Version.TryParse(build.Version, out var running) && named.Value <= running
            : task.QuestionIsDeploy && !AskedPullRequests.AsksSomethingElse(task.Question.Value);

    /// <summary>
    /// The questions the running build settles now (see the rule above), each with the task, the question as read (so a
    /// new ask that lands meanwhile is not the one settled, TaskBoard.SettleQuestion), the thread line and the wake text.
    /// </summary>
    public static IReadOnlyList<RunningSettlement> Settlements(TaskBoard board, RunningBuild build,
        IReadOnlyDictionary<string, PullRequestArrival> facts, DateTimeOffset runningSince) =>
        board.Tasks
            .Where(task => Settles(task, build, facts, runningSince))
            .Select(task => (Task: task, Ours: Ours(task, build)))
            .Select(x => new RunningSettlement(x.Task.Id, x.Task.Question.Value, Note(build, x.Ours), WakeText(build, x.Ours)))
            .ToArray();

    /// <summary>Whether the running build settles the task's question (see the rule in the header).</summary>
    public static bool Settles(TaskItem task, RunningBuild build, IReadOnlyDictionary<string, PullRequestArrival> facts,
        DateTimeOffset runningSince) =>
        task is { IsOpen: true, Status: TaskStatus.WaitingOnYou, Question.HasValue: true, QuestionOptions.Count: 0 }
        && AsksForThisUpdate(task, build)
        && task.WaitingSince.GetValueOrDefault(task.Raised) < runningSince
        && Ours(task, build).Count > 0
        && AskedPullRequests.In(task.Question.Value).All(link => task.PullRequests.Any(p => p.Key == link.Key))
        && RunningChanges.Of(task, build, facts).Stage == DeliveryStage.Running;

    // The task's recorded pull requests of this build's repository that the running build carries: what the thread line
    // and the session are told. Running is false for one closed without a merge, so it is not named.
    static IReadOnlyList<PullRequestLink> Ours(TaskItem task, RunningBuild build) =>
        RunningChanges.Watched(task).Where(build.Builds).ToArray();

    /// <summary>
    /// Deskbar's line in the thread: "Question settled: this Deskbar runs 2.6.77, which carries [Deskbar #697](...)." No
    /// author or time: the thread's header shows both (task aa42794f).
    /// </summary>
    public static string Note(RunningBuild build, IReadOnlyList<PullRequestLink> links) =>
        $"Question settled: this Deskbar runs {build.Version}, which carries {Joined(links.Select(l => $"[{l.Label}]({l.Url})").ToArray())}.";

    /// <summary>
    /// What the session hears: its change runs in this Deskbar, which is the deploy's evidence, so it reports the deploy
    /// and finishes in the briefing's order. The version and commit are given so the report can quote them.
    /// </summary>
    public static string WakeText(RunningBuild build, IReadOnlyList<PullRequestLink> links) =>
        $"Your question is settled: the running Deskbar is now {build.Version}+{build.Commit}, which carries "
        + Joined(links.Select(l => $"{l.Label} ({l.Url})").ToArray())
        + ". That is the deploy: report it with task deployed (the running version is the evidence), then finish as your briefing says. The question is answered already, so do not run task answered or ask again.";

    // "a", "a and b", "a, b and c".
    static string Joined(IReadOnlyList<string> parts) =>
        parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
}

/// <summary>
/// A question the running build settles (RunningQuestions.Settlements): the task, the question as it was read, Deskbar's
/// line for the thread, and what the session is told.
/// </summary>
public sealed record RunningSettlement(Guid TaskId, string Question, string Note, string Wake);
