using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// The four timer lengths the window offers and the control pipe accepts
// (docs/superpowers/specs/2026-11-19-tasks-in-the-bar-design.md, "The model"). Free-form lengths are
// deliberately not supported: four buttons keep parking a one-click act.
public static class TaskPresets
{
    /// <summary>The offered lengths, shortest first.</summary>
    public static readonly IReadOnlyList<TimeSpan> Lengths =
        new[] { 1.0, 5, 10, 30 }.Select(TimeSpan.FromMinutes).ToArray();

    /// <summary>Reads "1m", "5m", "10m" or "30m" (case-insensitive); anything else is a failure.</summary>
    public static Result<TimeSpan> Parse(string text) =>
        Lengths.Where(l => string.Equals(text?.Trim(), $"{(int)l.TotalMinutes}m", StringComparison.OrdinalIgnoreCase))
            .Select(l => Result.Success(l))
            .DefaultIfEmpty(Result.Failure<TimeSpan>(
                $"'{text}' is not a timer length; use one of {string.Join(", ", Lengths.Select(l => $"{(int)l.TotalMinutes}m"))}"))
            .First();

    /// <summary>The button text for a length, "10 min".</summary>
    public static string Label(TimeSpan length) => $"{(int)length.TotalMinutes} min";
}
