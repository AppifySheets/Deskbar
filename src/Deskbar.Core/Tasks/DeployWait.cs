using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// A TASK THAT ONLY WAITS FOR A DEPLOY IS NOT A QUESTION (task 1565789d, 2026-11-27). Petre, looking at an iam.ge task in
// the Inbox whose code pull request was merged, whose stacks pull request pinning the new build was merged, and whose
// session had handed over "Redeploy the portal stack in Portainer": "why doesn't it have the semi-done circle", then
// "it's not a question. it should qualify as merged, built, waiting for deploy". The Inbox drew every task waiting on
// him as "Asks you" with the question bubble, while the bar's lines already gave the same task the hollow ring that
// means "merged, not running yet" (RunningChanges.Of). He picked option A of the mockup
// (https://claude.ai/artifact/DJkCdr4CzCyr6mnn6TUWVK): the ring in amber, since the next step is his, a track of the
// steps (Merged, Built, Deploy, Running) on the task's page, the session's hand-over shown as an instruction under it
// rather than a question bubble, and the task staying in Needs you.
//
// WHEN (Waits): the task waits on him, its change is merged and not running yet (the caller's `merged`, which is the
// task's delivery stage, RunningChanges.Of: Merged for a task that is not Done), and the question offers no answer
// buttons. Answer buttons mean the session asks a real question ("A or B?"), so that stays a question. The question's
// text is never hidden: it is shown as the instruction under the track.
//
// BUILT: known only from the task's deploy pull requests (TaskItem.DeployPullRequests, `task pr <url> --deploy`): built
// once every one has ended (DeployPullRequestsEnded), not built while one is still open. A task with none says nothing
// about a build, and its track leaves the step out rather than claim one. "Ended" counts a deploy pull request closed
// without merging too: one replaced by a later one (stacks #308 by #309) is the usual reason, and the session is told
// of every close and asked to register the replacement.
public static class DeployWait
{
    /// <summary>Whether a task shows as waiting for its deploy rather than asking a question (see the header).</summary>
    // THE SESSION CAN SAY SO (task a87891ca, `task ask - --deploy`, TaskItem.QuestionIsDeploy): a hand-over the session
    // marked waits for the deploy whatever Deskbar has measured, since the session knows its change is merged and built
    // even while Deskbar is still asking GitHub or its pull request is not recorded. Unmarked asks are read as above.
    public static bool Waits(TaskStatus status, bool merged, int answerOptions, bool handOver = false) =>
        status == TaskStatus.WaitingOnYou && (handOver || (merged && answerOptions == 0));

    /// <summary>
    /// Whether the change is built, as far as the task's deploy pull requests tell: none when the task has none, true once
    /// every one has ended, false while one is still open.
    /// </summary>
    public static Maybe<bool> Built(TaskItem task) =>
        task.DeployPullRequests.Count == 0
            ? Maybe<bool>.None
            : task.DeployPullRequests.All(p => task.DeployPullRequestsEnded.Contains(p.Key));

    /// <summary>The state in words, for the Inbox line and the page: "Merged, built · waiting for your deploy".</summary>
    public static string Words(Maybe<bool> built) =>
        built.HasNoValue ? "Merged · waiting for your deploy"
        : built.Value ? "Merged, built · waiting for your deploy"
        : "Merged · waiting for the deploy pull request";
}
