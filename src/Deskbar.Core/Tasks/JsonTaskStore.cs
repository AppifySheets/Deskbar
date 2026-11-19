using System.Text.Json;
using System.Text.Json.Serialization;
using CSharpFunctionalExtensions;
using Deskbar.Core.Persistence;

namespace Deskbar.Core.Tasks;

// tasks.json, beside state.json and time.json. Written and read through DurableFile for the same reason as
// they are: neither a crash mid-write nor a restart may destroy what was already parked. Design:
// docs/superpowers/specs/2026-11-19-tasks-in-the-bar-design.md ("Persistence").
//
// The model uses Maybe and TimeSpan, which serialise badly, so the file is written from private DTO records:
// a Maybe becomes a nullable field (null means none) and a TimeSpan becomes seconds, so the file stays
// readable and a spreadsheet can add the lengths up. Every time is converted to UTC before writing AND after
// reading, so the file means the same on a machine in another time zone, a hand-edited time in local time is
// still the same instant, and a later sync can compare UpdatedAt directly.
//
// How a timer ended (TimerEnd) is written by NAME and read tolerantly (TolerantTimerEndConverter), the way
// state.json's strip items are (KnownNamesOnlyConverter): a name a newer build added reads as Cleared instead of
// failing the whole file, because a failed parse is what makes the file unreadable, and an unreadable file is
// what the next paragraph has to rescue.
//
// AN UNREADABLE FILE IS KEPT, NEVER SAVED OVER (final review I2). When tasks.json and its previous copy are both
// unreadable, Load fails and the service starts from an empty board; the next save would then replace tasks.json
// and the one after it would rotate that over tasks.json.previous, so two clicks would destroy both. So a failed
// Load moves the unreadable files aside as `<name>.<yyyyMMdd-HHmmss>.bak` before anything can be saved. That
// happens HERE, inside Load, rather than as a Quarantine() the service calls, because ITaskStore is the seam a
// cloud store replaces (see ITaskStore) and must stay its two members: only a store made of files has files to
// move aside, and a cloud store's failed load has nothing on disk to protect.
public sealed class JsonTaskStore(string baseDirectory) : ITaskStore
{
    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new TolerantTimerEndConverter() },
    };

    sealed record TimerDto(DateTimeOffset Started, double LengthSeconds);

    sealed record RecordDto(DateTimeOffset Started, double LengthSeconds, DateTimeOffset Ended, TimerEnd How);

    // Title is nullable here because a hand-edited file can say `"title": null`; FromDto makes that "" (M8).
    sealed record TaskDto(
        Guid Id,
        string? Title,
        Guid? WorkspaceId,
        DateTimeOffset Created,
        DateTimeOffset UpdatedAt,
        TimerDto? Timer,
        DateTimeOffset? DoneAt,
        IReadOnlyList<RecordDto>? History);

    sealed record FileDto(IReadOnlyList<TaskDto>? Tasks);

    string TasksPath => Path.Combine(baseDirectory, "tasks.json");

    // A missing file is the normal first run. An unreadable one falls back to the previous copy
    // (DurableFile.Read); only when both are unreadable is it a failure, and then the unreadable files are moved
    // aside (see the header) and the failure says where they went, which App traces.
    public Result<TaskBoard> Load() =>
        DurableFile.Read(TasksPath,
                text => Rebuild(JsonSerializer.Deserialize<FileDto>(text, Options) ?? throw new JsonException("tasks.json deserialized to null")),
                TaskBoard.Empty)
            .MapError(error => $"{error}; {KeepAside()}");

    public Result Save(TaskBoard board) =>
        Result.Try(() =>
        {
            Directory.CreateDirectory(baseDirectory);
            // Flushed and swapped in with the old file kept as tasks.json.previous, as state.json is.
            DurableFile.Write(TasksPath, JsonSerializer.Serialize(new FileDto(board.Tasks.Select(ToDto).ToArray()), Options));
        }, e => $"Could not write {TasksPath}: {e.Message}");

    // Moves tasks.json and tasks.json.previous, whichever exist, out of the way of the next two saves, and says
    // what happened to each. A move that fails (the file is locked) is reported, not thrown: Load must still answer.
    string KeepAside() =>
        string.Join("; ", new[] { TasksPath, DurableFile.PreviousOf(TasksPath) }
            .Where(File.Exists)
            .Select(path => Result.Try(() => MoveAside(path), e => $"could not keep {path} aside: {e.Message}")
                .Match(aside => $"kept {Path.GetFileName(path)} as {Path.GetFileName(aside)}", error => error)));

    // `<path>.<yyyyMMdd-HHmmss>.bak`, with a counter only if that name is taken (two failed starts in one second).
    static string MoveAside(string path)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var aside = Enumerable.Range(0, int.MaxValue)
            .Select(n => n == 0 ? $"{path}.{stamp}.bak" : $"{path}.{stamp}-{n}.bak")
            .First(candidate => !File.Exists(candidate));
        File.Move(path, aside);
        return aside;
    }

    static TaskBoard Rebuild(FileDto file) =>
        new((file.Tasks ?? []).Select(FromDto).ToArray());

    static TaskDto ToDto(TaskItem t) =>
        new(t.Id, t.Title,
            t.WorkspaceId.HasValue ? t.WorkspaceId.Value : null,
            t.Created.ToUniversalTime(), t.UpdatedAt.ToUniversalTime(),
            t.Timer.HasValue ? new TimerDto(t.Timer.Value.Started.ToUniversalTime(), t.Timer.Value.Length.TotalSeconds) : null,
            t.DoneAt.HasValue ? t.DoneAt.Value.ToUniversalTime() : null,
            t.History.Select(h => new RecordDto(h.Started.ToUniversalTime(), h.Length.TotalSeconds, h.Ended.ToUniversalTime(), h.How)).ToArray());

    static TaskItem FromDto(TaskDto d) =>
        new(d.Id, d.Title ?? "",
            d.WorkspaceId is { } w ? w : Maybe<Guid>.None,
            d.Created.ToUniversalTime(), d.UpdatedAt.ToUniversalTime(),
            d.Timer is { } t ? new TaskTimer(t.Started.ToUniversalTime(), TimeSpan.FromSeconds(t.LengthSeconds)) : Maybe<TaskTimer>.None,
            d.DoneAt is { } done ? done.ToUniversalTime() : Maybe<DateTimeOffset>.None,
            (d.History ?? []).Select(h => new TimerRecord(h.Started.ToUniversalTime(), TimeSpan.FromSeconds(h.LengthSeconds), h.Ended.ToUniversalTime(), h.How)).ToArray());

    // Reads a TimerEnd from its name in any case, or from the bare number files written before names were (the
    // first builds of this feature wrote numbers), and writes it as its name. Anything else, a name a newer build
    // added included, reads as Cleared: the timer ended and this build cannot say how, which is what Cleared means
    // to every total this build computes. Failing instead would make the whole file unreadable over one word.
    sealed class TolerantTimerEndConverter : JsonConverter<TimerEnd>
    {
        public override TimerEnd Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var read = reader.TokenType switch
            {
                JsonTokenType.String when Enum.TryParse<TimerEnd>(reader.GetString(), ignoreCase: true, out var named) && Enum.IsDefined(named) => named,
                JsonTokenType.Number when reader.TryGetInt32(out var number) && Enum.IsDefined(typeof(TimerEnd), number) => (TimerEnd)number,
                _ => (TimerEnd?)null,
            };
            // An object or array where the name should be: passed over whole, so the reader is left after it.
            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) reader.Skip();
            return read ?? TimerEnd.Cleared;
        }

        public override void Write(Utf8JsonWriter writer, TimerEnd value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
}
