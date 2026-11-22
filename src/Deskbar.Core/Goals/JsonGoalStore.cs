using System.Globalization;
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
//     UpdatedAt directly. A missing time reads as the goal's other time, or the Unix epoch when both are missing.
//   * A goal with no id or no period (null or missing; a hand edit), or one whose id is not a Guid or whose period is
//     not a yyyy-MM-dd date in the years 2000 to 2999, is SKIPPED and traced, and the rest of the file reads (review M1,
//     M2). Failing the whole file over one goal would move every other goal aside to a .bak, and reading
//     the missing period as 0001-01-01 made Previous() throw. The skipped goal is not written back by the next save, so
//     it must not be lost with it: a load that skipped any goal COPIES the file it read aside once, as
//     `<name>.<yyyyMMdd-HHmmss>.bak` beside it (DurableFile.KeepCopy), and traces where. Copied, not moved: the file
//     was readable, its other goals are the board, and a crash before the next save must still find them.
//   * A newer version is read as this one, tolerating what it does not know, rather than failing the whole file.
public sealed class JsonGoalStore(string baseDirectory, Action<string>? trace = null) : IGoalStore
{
    const int CurrentVersion = 1;

    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new TolerantNameConverter<GoalKind>(), new TolerantNameConverter<GoalStatus>() },
    };

    // The id, the period, the parent and the seen dates are TEXT here, parsed by hand (review M2): as a Guid or a DateOnly
    // field, one malformed value ("not-a-guid", "2026-11-3") made the serializer fail the whole file. FromDto skips a goal
    // whose id or period does not parse, and reads a parent or a seen date that does not parse as none. The times are
    // nullable so a missing one reads instead of failing the file.
    sealed record GoalDto(
        string? Id,
        string? Title,
        GoalKind Kind,
        string? Period,
        GoalStatus Status,
        int Order,
        string? ParentId,
        DateTimeOffset? Created,
        DateTimeOffset? UpdatedAt);

    sealed record FileDto(int Version, string? WeekSeen, string? MonthSeen, IReadOnlyList<GoalDto>? Goals);

    // What one parse made: the board, and whether it skipped a goal, carried from Parse (which DurableFile.Read calls) to
    // Load (which then keeps the file aside), as JsonTaskStore's Parsed carries a version 1 file.
    sealed record Parsed(GoalBoard Board, bool Skipped);

    string GoalsPath => Path.Combine(baseDirectory, "goals.json");

    // A missing file is the normal first run (an empty board, nothing seen). See the header for the unreadable case and
    // for a file with a goal this build cannot read.
    public Result<GoalBoard> Load() =>
        DurableFile.Read(GoalsPath, Parse, new Parsed(GoalBoard.Empty, false), line => trace?.Invoke($"goals: {line}"))
            .MapError(error => $"{error}; {DurableFile.KeepAside(GoalsPath)}")
            .Tap(parsed => { if (parsed.Skipped) KeepSkippedSource(); })
            .Map(parsed => parsed.Board);

    // The file the board was read from: goals.json when it is still in place, else its previous copy (DurableFile.Read
    // moves an unreadable goals.json aside before it reads the previous one).
    void KeepSkippedSource() =>
        trace?.Invoke($"goals: a goal could not be read, so {DurableFile.KeepCopy(File.Exists(GoalsPath) ? GoalsPath : DurableFile.PreviousOf(GoalsPath))}");

    public Result Save(GoalBoard board) =>
        Result.Try(() =>
        {
            Directory.CreateDirectory(baseDirectory);
            DurableFile.Write(GoalsPath, JsonSerializer.Serialize(ToDto(board), Options));
        }, e => $"Could not write {GoalsPath}: {e.Message}");

    Parsed Parse(string text) =>
        FromDto(JsonSerializer.Deserialize<FileDto>(text, Options) ?? throw new JsonException("goals.json deserialized to null"));

    // The id as the serializer wrote it when it was a Guid ("D", lower case), and the period as yyyy-MM-dd, so the
    // file's bytes are the same as before these became text.
    static FileDto ToDto(GoalBoard board) =>
        new(CurrentVersion,
            board.WeekSeen.Map(Day).GetValueOrDefault(),
            board.MonthSeen.Map(Day).GetValueOrDefault(),
            board.Goals.Select(g => new GoalDto(g.Id.ToString("D"), g.Title, g.Kind, g.Period.Key, g.Status, g.Order,
                g.ParentId.Map(p => p.ToString("D")).GetValueOrDefault(),
                g.Created.ToUniversalTime(), g.UpdatedAt.ToUniversalTime())).ToArray());

    static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // Every goal read, or why it could not be: the unreadable ones are traced, one line each with the goal's title, and
    // skipped, and the parse says it skipped (see the header).
    Parsed FromDto(FileDto file)
    {
        var read = (file.Goals ?? []).Select(d => (Dto: d, Goal: ToGoal(d))).ToArray();
        read.Where(r => r.Goal.IsFailure).ToList()
            .ForEach(r => trace?.Invoke($"goals: skipped a goal with {r.Goal.Error}: '{r.Dto.Title}'"));
        return new Parsed(
            new GoalBoard(read.Where(r => r.Goal.IsSuccess).Select(r => r.Goal.Value).ToArray())
            {
                WeekSeen = SeenDay(file.WeekSeen),
                MonthSeen = SeenDay(file.MonthSeen),
            },
            read.Any(r => r.Goal.IsFailure));
    }

    // A goal this build can show needs an id that is a Guid and a period that is a date in range (GoalPeriod.ParseDay);
    // the error names what is wrong, for the trace. A parent that is not a Guid reads as none (a link, not a goal).
    static Result<Goal> ToGoal(GoalDto d) =>
        string.IsNullOrWhiteSpace(d.Id) ? Result.Failure<Goal>("no id")
        : !Guid.TryParse(d.Id, out var id) ? Result.Failure<Goal>($"an id that is not one, '{d.Id}'")
        : string.IsNullOrWhiteSpace(d.Period) ? Result.Failure<Goal>("no period")
        : GoalPeriod.ParseDay(d.Period).MapError(error => $"a period that is not usable: {error}").Map(day => new Goal(id, d.Title ?? "",
            GoalPeriod.Of(d.Kind, day), d.Status, d.Order,
            Guid.TryParse(d.ParentId, out var parent) ? parent : Maybe<Guid>.None,
            (d.Created ?? d.UpdatedAt ?? DateTimeOffset.UnixEpoch).ToUniversalTime(),
            (d.UpdatedAt ?? d.Created ?? DateTimeOffset.UnixEpoch).ToUniversalTime()));

    // A seen date that does not parse reads as none: the review then counts as not done, which costs one prompt.
    static Maybe<DateOnly> SeenDay(string? text) =>
        string.IsNullOrWhiteSpace(text) ? Maybe<DateOnly>.None
        : GoalPeriod.ParseDay(text).Match(day => Maybe.From(day), _ => Maybe<DateOnly>.None);

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
