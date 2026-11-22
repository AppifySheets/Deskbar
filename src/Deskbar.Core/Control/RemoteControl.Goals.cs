using System.Globalization;
using CSharpFunctionalExtensions;
using Deskbar.Core.Goals;

namespace Deskbar.Core.Control;

// THE `goal` COMMANDS: the pipe twins of the planner, for a script or a session acting for Petre. Design:
// docs/superpowers/specs/2026-11-22-goals-planner-design.md ("Pipe commands") and the plan's Task 5.
//
//   goal list [--week [<date>] | --month [<date>] | --all]
//   goal add <title> [--week [<date>] | --month [<date>]]
//   goal done <id> | drop <id> | reopen <id> | delete <id>
//   goal rename <id> <title>
//   goal carry <id> [--week [<date>] | --month [<date>]]
//   goal parent <id> <monthly-id>|none
//   goal prompt [dismiss]
//
// Within the `task` commands' conventions: an id is the goal's Guid or a unique prefix of at least four characters
// (GoalBoard.Find); a period is `--week` or `--month`, bare for the current one or with any yyyy-MM-dd date inside it
// (snapped to its Monday or its 1st). With no period option, `add` uses this week, `carry` the current period of the
// goal's own kind (the review's "Carry over"), and `list` shows this month's goals then this week's; `--all` lists
// every goal, the oldest period first. "Current" is Petre's local week and month (GoalService.ThisWeek).
//
// Every verb calls the same GoalService method the planner calls, so a goal changed from a script obeys the rules a
// click does. Every reply but `list`, `prompt` and `delete` is the goal read back after the change; `delete` answers
// with the goal as it was, and `prompt` with {due: "week"|"month"|null}. `prompt dismiss` is the twin of the bar
// line's close sign: it records this week and month as seen.
//
// `goal` is NOT in ControlArguments.AboutTheCaller: no form depends on the caller's folder (a goal has no
// workspace), so the client appends no --from. `task goal <id> <goal-id>|none` lives with the task verbs
// (RemoteControl.Tasks.cs) and links through GoalService.LinkTask.
//
// Every failure is one line and changes nothing: parsing and lookups finish before the first service call.
public sealed partial class RemoteControl
{
    const string GoalsUnavailable = "Goals are not available";

    ControlReply Goal(string[] rest) =>
        goals is not { } service
            ? ControlReply.Fail(GoalsUnavailable)
            : rest switch
            {
                ["list", .. var options] => GoalList(service, options),
                ["add", var title, .. var options] => ParsePeriod(service, options)
                    .Bind(period => service.Add(title, period.GetValueOrDefault(service.ThisWeek)))
                    .Match(goal => Success(DescribeGoal(service, goal.Id)), ControlReply.Fail),
                ["done", var id] => GoalChange(service, id, g => service.MarkDone(g.Id)),
                ["drop", var id] => GoalChange(service, id, g => service.Drop(g.Id)),
                ["reopen", var id] => GoalChange(service, id, g => service.Reopen(g.Id)),
                ["rename", var id, var title] => GoalChange(service, id, g => service.Rename(g.Id, title)),
                ["carry", var id, .. var options] => ParsePeriod(service, options)
                    .Match(to => GoalChange(service, id, g => to.HasValue ? service.Carry(g.Id, to.Value) : service.CarryToNow(g.Id)),
                        ControlReply.Fail),
                ["parent", var id, var parent] => ParseParent(service, parent)
                    .Match(p => GoalChange(service, id, g => service.SetParent(g.Id, p)), ControlReply.Fail),
                // The reply is the goal as it was, described before the delete, since it is gone afterwards.
                ["delete", var id] => service.Board.Find(id)
                    .Map(goal => (goal.Id, AsItWas: DescribeGoal(service, goal.Id)))
                    .Bind(found => service.Delete(found.Id).Map(() => found.AsItWas))
                    .Match(Success, ControlReply.Fail),
                ["prompt"] => GoalPrompt(service),
                ["prompt", "dismiss"] => GoalPromptDismissed(service),
                _ => ControlReply.Fail($"Wrong arguments for goal.\n{GoalUsage()}"),
            };

    // The bar line's close sign from outside: this week and month are recorded as seen, and the reply is what is due
    // after that (nothing, until the next week or month).
    static ControlReply GoalPromptDismissed(GoalService service)
    {
        service.Seen();
        return GoalPrompt(service);
    }

    ControlReply GoalList(GoalService service, string[] options) =>
        (options is ["--all"]
            ? Result.Success<IReadOnlyList<Goal>>(service.Board.Goals
                .OrderBy(g => g.Period.Start).ThenBy(g => g.Kind == GoalKind.Month ? 0 : 1)
                .ThenBy(g => g.Order).ThenBy(g => g.Created).ThenBy(g => g.Id).ToArray())
            : ParsePeriod(service, options).Map(period => period.HasValue ? service.Board.In(period.Value) : CurrentGoals(service.Board, service.Today)))
        .Match(list => Success(list.Select(g => DescribeGoal(service, g.Id)).ToArray()), ControlReply.Fail);

    // This month's goals then this week's, both from ONE read of today (review M2), so they cannot straddle midnight.
    static IReadOnlyList<Goal> CurrentGoals(GoalBoard board, DateOnly day) =>
        board.In(GoalPeriod.MonthOf(day)).Concat(board.In(GoalPeriod.WeekOf(day))).ToArray();

    static ControlReply GoalPrompt(GoalService service) =>
        Success(new { due = service.PromptDue() switch { ReviewDue.Week => "week", ReviewDue.Month => "month", _ => (string?)null } });

    // Finds the goal first, so an unknown or ambiguous id fails before anything is touched, then applies the change
    // and answers with the goal read back from the board.
    ControlReply GoalChange(GoalService service, string id, Func<Goal, Result> change) =>
        service.Board.Find(id)
            .Bind(goal => change(goal).Map(() => DescribeGoal(service, goal.Id)))
            .Match(Success, ControlReply.Fail);

    // --- parsing --------------------------------------------------------------------------------

    // No option is none (the caller's default); a bare --week or --month is the current one; a date names the period
    // holding it. Anything else is refused by name.
    static Result<Maybe<GoalPeriod>> ParsePeriod(GoalService service, string[] options) => options switch
    {
        [] => Maybe<GoalPeriod>.None,
        ["--week"] => Maybe.From(service.ThisWeek),
        ["--month"] => Maybe.From(service.ThisMonth),
        ["--week", var date] => ParseDate(date).Map(d => Maybe.From(GoalPeriod.WeekOf(d))),
        ["--month", var date] => ParseDate(date).Map(d => Maybe.From(GoalPeriod.MonthOf(d))),
        [var option, ..] when option.StartsWith("--", StringComparison.Ordinal) => Result.Failure<Maybe<GoalPeriod>>($"Unexpected {option}; use --week or --month"),
        [var extra, ..] => Result.Failure<Maybe<GoalPeriod>>($"Unexpected '{extra}': a title with spaces needs quotes"),
    };

    static Result<DateOnly> ParseDate(string text) =>
        DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : Result.Failure<DateOnly>($"'{text}' is not a date; use yyyy-MM-dd");

    static Result<Maybe<Guid>> ParseParent(GoalService service, string parent) =>
        parent.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)
            ? Maybe<Guid>.None
            : service.Board.Find(parent).Map(g => Maybe.From(g.Id));

    // Every line of the Goals section, as `deskbar.cmd goal <form>`.
    static string GoalUsage() => string.Join("\n", ControlUsage.Sections.SelectMany(s => s.Commands)
        .Where(c => c.Verb == "goal").Select(c => $"usage: deskbar.cmd goal {c.Arguments}"));

    // --- the JSON shape of a goal ----------------------------------------------------------------

    // One goal as `list` prints it and every other verb answers with it, read off the live boards. `period` is its
    // first day; `parent` the monthly goal's id or null; `progress` the planner's line (GoalProgress.Text) with its
    // counts beside it; `weeks` and `weeksDone` are null on a weekly goal, which has no weekly goals under it;
    // `taskIds` every task linked to it, closed ones included, in the task board's order.
    static object DescribeGoal(GoalService service, Guid id)
    {
        var goal = service.Board.Get(id).Value;
        var progress = service.ProgressOf(goal);
        var weekly = goal.Kind == GoalKind.Week;
        return new
        {
            id = goal.Id,
            title = goal.Title,
            kind = weekly ? "week" : "month",
            period = goal.Period.Key,
            status = goal.Status.ToString().ToLowerInvariant(),
            order = goal.Order,
            parent = goal.ParentId.Map(p => (Guid?)p).GetValueOrDefault(),
            progress = progress.Text,
            tasksDone = progress.TasksDone,
            tasks = progress.Tasks,
            weeksDone = weekly ? (int?)null : progress.WeeksDone,
            weeks = weekly ? (int?)null : progress.Weeks,
            taskIds = service.TasksOf(goal.Id).Select(t => t.Id).ToArray(),
        };
    }
}
