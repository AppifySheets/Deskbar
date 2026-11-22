namespace Deskbar.Core.Tasks;

// HOW LONG AGO A TASK APPEARED, as its line on the bar and in the tasks window says it (Petre: "tasks should say how
// long ago they appeared - 2h3m"; controller ruling 26). Compact, two units at most, the larger first:
//
//   under a minute     "now"
//   under an hour      "45m"
//   under a day        "2h3m", or "2h" on the hour
//   under four weeks   "3d2h", or "3d" on the day
//   from four weeks    "5w", whole weeks
//
// Every unit rounds down, so a task never reads older than it is. A negative age (a clock behind the task's own stamp)
// reads "now".
public static class TaskAge
{
    public static string Format(TimeSpan age) =>
        age < TimeSpan.FromMinutes(1) ? "now"
        : age < TimeSpan.FromHours(1) ? $"{(int)age.TotalMinutes}m"
        : age < TimeSpan.FromDays(1) ? Two((int)age.TotalHours, "h", age.Minutes, "m")
        : age < TimeSpan.FromDays(28) ? Two((int)age.TotalDays, "d", age.Hours, "h")
        : $"{(int)age.TotalDays / 7}w";

    static string Two(int big, string bigUnit, int small, string smallUnit) =>
        small == 0 ? $"{big}{bigUnit}" : $"{big}{bigUnit}{small}{smallUnit}";
}
