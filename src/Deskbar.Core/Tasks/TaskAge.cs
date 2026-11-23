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

    /// <summary>
    /// The same age in words for a tooltip ("Added 2 hours 3 minutes ago"), so the compact "2h3m" is not misread as a
    /// timer or a due time (Ruling 46). Same bands and rounding as <see cref="Format"/>.
    /// </summary>
    public static string Words(TimeSpan age) =>
        age < TimeSpan.FromMinutes(1) ? "Added just now"
        : age < TimeSpan.FromHours(1) ? $"Added {Unit((int)age.TotalMinutes, "minute")} ago"
        : age < TimeSpan.FromDays(1) ? $"Added {Pair((int)age.TotalHours, "hour", age.Minutes, "minute")} ago"
        : age < TimeSpan.FromDays(28) ? $"Added {Pair((int)age.TotalDays, "day", age.Hours, "hour")} ago"
        : $"Added {Unit((int)age.TotalDays / 7, "week")} ago";

    static string Unit(int n, string name) => n == 1 ? $"1 {name}" : $"{n} {name}s";

    static string Pair(int big, string bigName, int small, string smallName) =>
        small == 0 ? Unit(big, bigName) : $"{Unit(big, bigName)} {Unit(small, smallName)}";

    static string Two(int big, string bigUnit, int small, string smallUnit) =>
        small == 0 ? $"{big}{bigUnit}" : $"{big}{bigUnit}{small}{smallUnit}";
}
