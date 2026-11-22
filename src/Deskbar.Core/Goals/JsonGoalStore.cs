using System.Text.Json;
using System.Text.Json.Serialization;
using CSharpFunctionalExtensions;
using Deskbar.Core.Persistence;

namespace Deskbar.Core.Goals;

// goals.json, beside tasks.json and state.json. Design: docs/superpowers/specs/2026-11-22-goals-planner-design.md
// ("Calls made in writing this up", "Constraints") and the plan's Task 3.
//
// WHY A FILE OF ITS OWN. The earlier short design put goals inside tasks.json. A build from before goals reads a newer
// tasks.json as the version it knows and drops what it does not understand when it saves, and the updater has put
// older builds back before (branch builds). Goals inside tasks.json would be lost on that build's first save; in their
// own file the older build never touches them. Only the task's LINK to its goal lives in tasks.json (`goalId`), so the
// worst an older build can do is cost the links, never the goals.
//
// The file: {"version":1,"weekSeen":"2026-11-23","monthSeen":"2026-11-01","goals":[{"id","title","kind":"week"|"month",
// "period":"yyyy-MM-dd","status":"open"|"done"|"dropped","order","parentId","created","updatedAt"}]}. weekSeen and
// monthSeen are the review state (GoalReview), null until Petre first reviews or dismisses the prompt.
//
// Written and read through DurableFile, as tasks.json is: flushed before the swap, the old file kept as
// goals.json.previous, and an unreadable file falls back to that copy. When both are unreadable, Load fails and the
// service starts from an empty board, so before anything can be saved the unreadable files are moved aside as
// `<name>.<yyyyMMdd-HHmmss>.bak` (DurableFile.KeepAside) and the failure says where they went. That happens inside
// Load because IGoalStore is the seam a synced store replaces and must stay its two members (JsonTaskStore's header
// has the full reasoning). The tasks keep their goalId through all of this: nothing prunes links (plan, "Calls this
// plan makes"), so a goals.json that fails to load costs no task its link.
//
// TOLERANT READING, because a failed parse is what makes the whole file unreadable:
//   * The model uses Maybe, which serialises badly, so the file is written from private DTO records: a Maybe becomes a
//     nullable field (null means none). A null title reads as "".
//   * A kind or a status is written as its lower-case name and read by name in any case; a name a newer build added
//     (or null, or a number) reads as the first value, a week and Open. An open goal this build cannot place stays in
//     front of Petre rather than vanishing.
//   * A period is a local date written as yyyy-MM-dd (no time, no zone: GoalPeriod's header says why). A date that is
//     not the first day of its period (a hand edit) is snapped to the period holding it.
//   * Every time is converted to UTC before writing AND after reading, as in tasks.json, so a later sync can compare
//     UpdatedAt directly.
//   * A newer version is read as this one, tolerating what it does not know, rather than failing the whole file.
public sealed class JsonGoalStore(string baseDirectory, Action<string>? trace = null) : IGoalStore
{
    const int CurrentVersion = 1;

    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new TolerantNameConverter<GoalKind>(), new TolerantNameConverter<GoalStatus>() },
    };

    sealed record GoalDto(
        Guid Id,
        string? Title,
        GoalKind Kind,
        DateOnly Period,
        GoalStatus Status,
        int Order,
        Guid? ParentId,
        DateTimeOffset Created,
        DateTimeOffset UpdatedAt);

    sealed record FileDto(int Version, DateOnly? WeekSeen, DateOnly? MonthSeen, IReadOnlyList<GoalDto>? Goals);

    string GoalsPath => Path.Combine(baseDirectory, "goals.json");

    // A missing file is the normal first run (an empty board, nothing seen). See the header for the unreadable case.
    public Result<GoalBoard> Load() =>
        DurableFile.Read(GoalsPath, Parse, GoalBoard.Empty, line => trace?.Invoke($"goals: {line}"))
            .MapError(error => $"{error}; {DurableFile.KeepAside(GoalsPath)}");

    public Result Save(GoalBoard board) =>
        Result.Try(() =>
        {
            Directory.CreateDirectory(baseDirectory);
            DurableFile.Write(GoalsPath, JsonSerializer.Serialize(ToDto(board), Options));
        }, e => $"Could not write {GoalsPath}: {e.Message}");

    static GoalBoard Parse(string text) =>
        FromDto(JsonSerializer.Deserialize<FileDto>(text, Options) ?? throw new JsonException("goals.json deserialized to null"));

    static FileDto ToDto(GoalBoard board) =>
        new(CurrentVersion,
            board.WeekSeen.HasValue ? board.WeekSeen.Value : null,
            board.MonthSeen.HasValue ? board.MonthSeen.Value : null,
            board.Goals.Select(g => new GoalDto(g.Id, g.Title, g.Kind, g.Period.Start, g.Status, g.Order,
                g.ParentId.HasValue ? g.ParentId.Value : null,
                g.Created.ToUniversalTime(), g.UpdatedAt.ToUniversalTime())).ToArray());

    static GoalBoard FromDto(FileDto file) =>
        new GoalBoard((file.Goals ?? []).Select(d => new Goal(d.Id, d.Title ?? "",
            GoalPeriod.Of(d.Kind, d.Period), d.Status, d.Order,
            d.ParentId is { } parent ? parent : Maybe<Guid>.None,
            d.Created.ToUniversalTime(), d.UpdatedAt.ToUniversalTime())).ToArray())
        {
            WeekSeen = file.WeekSeen is { } week ? week : Maybe<DateOnly>.None,
            MonthSeen = file.MonthSeen is { } month ? month : Maybe<DateOnly>.None,
        };

    // Reads an enum from its name in any case and writes it as its lower-case name. Anything else, a name a newer build
    // added, a number, null or an object included, reads as the enum's first value (see the header).
    sealed class TolerantNameConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var name = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            // An object or array where the name should be: passed over whole, so the reader is left after it.
            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) reader.Skip();
            return Enum.GetValues<T>()
                .Where(value => string.Equals(value.ToString(), name, StringComparison.OrdinalIgnoreCase))
                .DefaultIfEmpty(default)
                .First();
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString().ToLowerInvariant());
    }
}
