using System.Text.Json;
using CSharpFunctionalExtensions;
using Deskbar.Core.Persistence;

namespace Deskbar.Core.Tasks;

// tasks.json, beside state.json and time.json. Written and read through DurableFile for the same reason as
// they are: neither a crash mid-write nor a restart may destroy what was already parked. Design:
// docs/superpowers/specs/2026-11-19-tasks-in-the-bar-design.md ("Persistence").
//
// The model uses Maybe and TimeSpan, which serialise badly, so the file is written from private DTO records:
// a Maybe becomes a nullable field (null means none) and a TimeSpan becomes seconds, so the file stays
// readable and a spreadsheet can add the lengths up. Every time is converted to UTC before writing, so the
// file means the same on a machine in another time zone and a later sync can compare UpdatedAt directly.
public sealed class JsonTaskStore(string baseDirectory) : ITaskStore
{
    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    sealed record TimerDto(DateTimeOffset Started, double LengthSeconds);

    sealed record RecordDto(DateTimeOffset Started, double LengthSeconds, DateTimeOffset Ended, TimerEnd How);

    sealed record TaskDto(
        Guid Id,
        string Title,
        Guid? WorkspaceId,
        DateTimeOffset Created,
        DateTimeOffset UpdatedAt,
        TimerDto? Timer,
        DateTimeOffset? DoneAt,
        IReadOnlyList<RecordDto>? History);

    sealed record FileDto(IReadOnlyList<TaskDto>? Tasks);

    string TasksPath => Path.Combine(baseDirectory, "tasks.json");

    // A missing file is the normal first run. An unreadable one falls back to the previous copy
    // (DurableFile.Read); only when both are unreadable is it a failure.
    public Result<TaskBoard> Load() =>
        DurableFile.Read(TasksPath,
            text => Rebuild(JsonSerializer.Deserialize<FileDto>(text, Options) ?? throw new JsonException("tasks.json deserialized to null")),
            TaskBoard.Empty);

    public Result Save(TaskBoard board) =>
        Result.Try(() =>
        {
            Directory.CreateDirectory(baseDirectory);
            // Flushed and swapped in with the old file kept as tasks.json.previous, as state.json is.
            DurableFile.Write(TasksPath, JsonSerializer.Serialize(new FileDto(board.Tasks.Select(ToDto).ToArray()), Options));
        }, e => $"Could not write {TasksPath}: {e.Message}");

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
        new(d.Id, d.Title,
            d.WorkspaceId is { } w ? w : Maybe<Guid>.None,
            d.Created, d.UpdatedAt,
            d.Timer is { } t ? new TaskTimer(t.Started, TimeSpan.FromSeconds(t.LengthSeconds)) : Maybe<TaskTimer>.None,
            d.DoneAt is { } done ? done : Maybe<DateTimeOffset>.None,
            (d.History ?? []).Select(h => new TimerRecord(h.Started, TimeSpan.FromSeconds(h.LengthSeconds), h.Ended, h.How)).ToArray());
}
